#nullable enable

using System.ComponentModel;
using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Configuration;
using DebuggerMcp.Reporting;
using DebuggerMcp.Sampling;
using DebuggerMcp.Security;
using DebuggerMcp.Serialization;
using DebuggerMcp.SourceLink;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DebuggerMcp.McpTools;

/// <summary>
/// Internal helper that implements AI-powered crash analysis (via MCP sampling).
/// </summary>
/// <remarks>
/// This type is intentionally not exported as an MCP tool type. It is invoked via <see cref="CompactTools"/>.
/// </remarks>
public sealed class AiAnalysisTools(
    DebuggerSessionManager sessionManager,
    SymbolManager symbolManager,
    WatchStore watchStore,
    ILoggerFactory loggerFactory,
    ILogger<AiAnalysisTools> logger)
    : DebuggerToolsBase(sessionManager, symbolManager, watchStore, logger)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    private readonly AiAnalysisDiskCache _diskCache = new(
        sessionManager.GetDumpStoragePath(),
        (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<AiAnalysisDiskCache>());

    /// <summary>
    /// Performs AI-powered deep crash analysis using a server-driven sampling loop.
    /// </summary>
    /// <param name="server">The current MCP server connection (used to send sampling requests to the client).</param>
    /// <param name="sessionId">Session ID.</param>
    /// <param name="userId">User ID (session owner).</param>
    /// <param name="maxIterations">Maximum sampling iterations.</param>
    /// <param name="maxTokens">Maximum output tokens per sampling request.</param>
    /// <param name="includeWatches">Include watch expression evaluations in the initial report.</param>
    /// <param name="includeSecurity">Include security analysis in the initial report.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Canonical JSON report document enriched with an <c>analysis.aiAnalysis</c> section.</returns>
    public async Task<string> AnalyzeCrashWithAiAsync(
        McpServer server,
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Maximum analysis iterations (default: 100)")] int maxIterations = 100,
        [Description("Maximum output tokens per sampling request (default: 8192)")] int maxTokens = 8192,
        [Description("Include watch expression evaluations in the initial report (default: true)")] bool includeWatches = true,
        [Description("Include security analysis in the initial report (default: true)")] bool includeSecurity = true,
        [Description("Refresh cached AI analysis for this dump (default: false)")] bool refreshCache = false,
        [Description("LLM provider for caching (optional; e.g. openai/openrouter)")] string? llmProvider = null,
        [Description("LLM model identifier for caching (optional)")] string? llmModel = null,
        [Description("LLM reasoning effort for caching (optional; e.g. low/medium/high/default)")] string? llmReasoningEffort = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        ValidateSessionId(sessionId);
        var sanitizedUserId = SanitizeUserId(userId);

        var manager = GetSessionManager(sessionId, sanitizedUserId);
        var session = GetSessionInfo(sessionId, sanitizedUserId);
        ValidateDumpIsOpen(manager);

        var llmKey = AiAnalysisDiskCacheLlmKey.TryCreate(llmProvider, llmModel, llmReasoningEffort);

        if (!refreshCache && !string.IsNullOrWhiteSpace(session.CurrentDumpId))
        {
            var cached = await _diskCache.TryReadAsync(
                    sanitizedUserId,
                    session.CurrentDumpId,
                    llmKey,
                    requireWatches: includeWatches,
                    requireSecurity: includeSecurity,
                    requireAllFrames: true,
                    cancellationToken)
                .ConfigureAwait(false);

            if (cached != null)
            {
                Logger.LogInformation(
                    "[AI] Using cached AI analysis report (disk) for dump {DumpId} (generatedAt={GeneratedAt})",
                    cached.Metadata.DumpId,
                    cached.Metadata.GeneratedAtUtc);

                session.SetCachedReport(
                    cached.Metadata.DumpId,
                    cached.Metadata.GeneratedAtUtc,
                    cached.ReportJson,
                    includesWatches: cached.Metadata.IncludesWatches,
                    includesSecurity: cached.Metadata.IncludesSecurity,
                    maxStackFrames: cached.Metadata.MaxStackFrames,
                    includesAiAnalysis: cached.Metadata.IncludesAiAnalysis);

                return cached.ReportJson;
            }
        }

        var sourceLinkResolver = GetOrCreateSourceLinkResolver(session, sanitizedUserId);

        if (!manager.IsSosLoaded && session.ClrMdAnalyzer?.IsOpen != true)
        {
            throw new InvalidOperationException(
                "This server is configured for .NET crash analysis only. SOS and/or ClrMD must be available. " +
                "Ensure the dump is a .NET dump and that it was opened via dump(action=\"open\") (CLI: open <dumpId>).");
        }

        Logger.LogInformation("[AI] Using DotNetCrashAnalyzer for initial report (SOS loaded: {IsSosLoaded}, ClrMD open: {IsClrMdOpen})",
            manager.IsSosLoaded,
            session.ClrMdAnalyzer?.IsOpen == true);
        var dotNetAnalyzer = new DotNetCrashAnalyzer(manager, sourceLinkResolver, session.ClrMdAnalyzer, Logger);
        var initialReport = await dotNetAnalyzer.AnalyzeDotNetCrashAsync().ConfigureAwait(false);

        if (includeSecurity)
        {
            var securityAnalyzer = new SecurityAnalyzer(manager);
            var securityResult = await securityAnalyzer.AnalyzeSecurityAsync().ConfigureAwait(false);
            ReportEnrichment.ApplySecurity(initialReport, securityResult);
        }

        if (includeWatches && !string.IsNullOrEmpty(session.CurrentDumpId))
        {
            var hasWatches = await WatchStore.HasWatchesAsync(sanitizedUserId, session.CurrentDumpId).ConfigureAwait(false);
            if (hasWatches)
            {
                var evaluator = new WatchEvaluator(manager, WatchStore);
                initialReport.Watches = await evaluator.EvaluateAllAsync(sanitizedUserId, session.CurrentDumpId).ConfigureAwait(false);
            }
        }

        // Clamp user-controlled parameters to avoid accidental runaway sampling loops / oversized completions.
        maxIterations = Math.Clamp(maxIterations, 1, 100);
        maxTokens = Math.Clamp(maxTokens, 256, 8192);

        // Build the canonical JSON report document (source of truth for report_get during sampling).
        // The sampling prompt itself will remain bounded (index + evidence snapshot) inside the orchestrator.
        var reportServiceForSampling = new ReportService();
        var initialMetadata = new ReportMetadata
        {
            DumpId = session.CurrentDumpId ?? string.Empty,
            UserId = sanitizedUserId,
            GeneratedAt = DateTime.UtcNow,
            DebuggerType = manager.DebuggerType,
            SosLoaded = manager.IsDotNetDump ? manager.IsSosLoaded : null,
            Format = ReportFormat.Json
        };
        var fullReportJson = reportServiceForSampling.GenerateReport(initialReport, new ReportOptions { Format = ReportFormat.Json }, initialMetadata);

        var samplingClient = new McpSamplingClient(server);
        var orchestrator = new AiAnalysisOrchestrator(
            samplingClient,
            _loggerFactory.CreateLogger<AiAnalysisOrchestrator>())
        {
            MaxIterations = maxIterations,
            MaxTokensPerRequest = maxTokens,
            EnableVerboseSamplingTrace = EnvironmentConfig.IsAiSamplingTraceEnabled(),
            EnableSamplingTraceFiles = EnvironmentConfig.IsAiSamplingTraceFilesEnabled(),
            SamplingTraceFilesRootDirectory = EnvironmentConfig.GetAiSamplingTraceFilesDirectory(),
            SamplingTraceMaxFileBytes = EnvironmentConfig.GetAiSamplingTraceMaxFileBytes(),
            SamplingTraceLabel = $"{sessionId}-{session.CurrentDumpId ?? "no-dump"}",
            EnableEvidenceProvenance = EnvironmentConfig.IsAiEvidenceProvenanceEnabled(),
            EvidenceExcerptMaxChars = EnvironmentConfig.GetAiEvidenceExcerptMaxChars()
        };

        var checkpointEveryIterations = EnvironmentConfig.GetAiSamplingCheckpointEveryIterationsOverride();
        if (checkpointEveryIterations.HasValue)
        {
            orchestrator.CheckpointEveryIterations = checkpointEveryIterations.Value;
        }

        Logger.LogInformation(
            "[AI] Sampling checkpoint interval: {CheckpointEveryIterations} (env override: {HasOverride})",
            orchestrator.CheckpointEveryIterations,
            checkpointEveryIterations.HasValue);

        var aiResult = await orchestrator.AnalyzeCrashAsync(
                initialReport,
                fullReportJson,
                manager,
                session.ClrMdAnalyzer,
                cancellationToken)
            .ConfigureAwait(false);

        // Keep the final report compact: the full tool trace is already available via sampling trace files/logs.
        aiResult.RemoveCommandTraces();
        initialReport.AiAnalysis = aiResult;

        // Build a fresh report snapshot that includes analysis.aiAnalysis so subsequent sampling passes can reference it.
        // This also keeps report_get consistent when the model requests analysis.aiAnalysis.* paths.
        var reportJsonWithAi = reportServiceForSampling.GenerateReport(
            initialReport,
            new ReportOptions { Format = ReportFormat.Json },
            new ReportMetadata
            {
                DumpId = session.CurrentDumpId ?? string.Empty,
                UserId = sanitizedUserId,
                GeneratedAt = DateTime.UtcNow,
                DebuggerType = manager.DebuggerType,
                SosLoaded = manager.IsDotNetDump ? manager.IsSosLoaded : null,
                Format = ReportFormat.Json
            });

        // AI rewrite pass: overwrite analysis.summary.description and analysis.summary.recommendations.
        var summaryRewrite = await orchestrator.RewriteSummaryAsync(
                initialReport,
                reportJsonWithAi,
                manager,
                session.ClrMdAnalyzer,
                cancellationToken)
            .ConfigureAwait(false);

        if (summaryRewrite != null)
        {
            summaryRewrite.CommandsExecuted = null;
        }

        if (summaryRewrite != null && string.IsNullOrWhiteSpace(summaryRewrite.Error))
        {
            initialReport.Summary ??= new AnalysisSummary();
            initialReport.Summary.Description = summaryRewrite.Description;
            initialReport.Summary.Recommendations = summaryRewrite.Recommendations;
            initialReport.AiAnalysis.Summary = summaryRewrite;
        }
        else if (summaryRewrite != null)
        {
            initialReport.AiAnalysis.Summary = summaryRewrite;
        }

        // Build a fresh report snapshot to include the rewritten summary (and any other AI outputs) for later passes.
        var reportJsonWithAiAndSummary = reportServiceForSampling.GenerateReport(
            initialReport,
            new ReportOptions { Format = ReportFormat.Json },
            new ReportMetadata
            {
                DumpId = session.CurrentDumpId ?? string.Empty,
                UserId = sanitizedUserId,
                GeneratedAt = DateTime.UtcNow,
                DebuggerType = manager.DebuggerType,
                SosLoaded = manager.IsDotNetDump ? manager.IsSosLoaded : null,
                Format = ReportFormat.Json
            });

        // AI thread narrative pass: populate analysis.threads.summary.description and analysis.aiAnalysis.threadNarrative.
        var threadNarrative = await orchestrator.GenerateThreadNarrativeAsync(
                initialReport,
                reportJsonWithAiAndSummary,
                manager,
                session.ClrMdAnalyzer,
                cancellationToken)
            .ConfigureAwait(false);

        if (threadNarrative != null)
        {
            threadNarrative.CommandsExecuted = null;
        }

        if (threadNarrative != null && string.IsNullOrWhiteSpace(threadNarrative.Error))
        {
            initialReport.Threads ??= new ThreadsInfo();
            initialReport.Threads.Summary ??= new ThreadSummary();
            initialReport.Threads.Summary.Description = threadNarrative.Description;
            initialReport.AiAnalysis.ThreadNarrative = threadNarrative;
        }
        else if (threadNarrative != null)
        {
            initialReport.AiAnalysis.ThreadNarrative = threadNarrative;
        }

        initialReport.AiAnalysis?.RemoveCommandTraces();

        // Return the canonical report document shape so `analyze(kind=ai)` matches `report -f json`.
        // Other formats should be derived from this JSON document.
        var reportService = new ReportService();
        var metadata = new ReportMetadata
        {
            DumpId = session.CurrentDumpId ?? string.Empty,
            UserId = sanitizedUserId,
            GeneratedAt = DateTime.UtcNow,
            DebuggerType = manager.DebuggerType,
            SosLoaded = manager.IsDotNetDump ? manager.IsSosLoaded : null,
            Format = ReportFormat.Json
        };

        var finalJson = reportService.GenerateReport(initialReport, new ReportOptions { Format = ReportFormat.Json }, metadata);

        if (!string.IsNullOrWhiteSpace(metadata.DumpId))
        {
            session.SetCachedReport(
                metadata.DumpId,
                metadata.GeneratedAt,
                finalJson,
                includesWatches: includeWatches,
                includesSecurity: includeSecurity,
                maxStackFrames: 0,
                includesAiAnalysis: true);

            try
            {
                await _diskCache.WriteAsync(
                        sanitizedUserId,
                        metadata.DumpId,
                        llmKey,
                        new AiAnalysisDiskCacheMetadata
                        {
                            GeneratedAtUtc = metadata.GeneratedAt,
                            IncludesWatches = includeWatches,
                            IncludesSecurity = includeSecurity,
                            MaxStackFrames = 0,
                            IncludesAiAnalysis = true,
                            Model = initialReport.AiAnalysis?.Model
                        },
                        finalJson,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[AI] Failed to persist AI analysis cache for dump {DumpId}", metadata.DumpId);
            }
        }

        return finalJson;
    }
}
