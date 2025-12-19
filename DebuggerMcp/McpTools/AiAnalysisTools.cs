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
        [Description("Maximum output tokens per sampling request (default: 4096)")] int maxTokens = 4096,
        [Description("Include watch expression evaluations in the initial report (default: true)")] bool includeWatches = true,
        [Description("Include security analysis in the initial report (default: true)")] bool includeSecurity = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        ValidateSessionId(sessionId);
        var sanitizedUserId = SanitizeUserId(userId);

        var manager = GetSessionManager(sessionId, sanitizedUserId);
        var session = GetSessionInfo(sessionId, sanitizedUserId);
        ValidateDumpIsOpen(manager);

        var sourceLinkResolver = GetOrCreateSourceLinkResolver(session, sanitizedUserId);

        if (!manager.IsSosLoaded && session.ClrMdAnalyzer?.IsOpen != true)
        {
            throw new InvalidOperationException(
                "This server is configured for .NET crash analysis only. SOS and/or ClrMD must be available. " +
                "Ensure the dump is a .NET dump and that it was opened via OpenDump.");
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

        // Build a bounded JSON prompt for the LLM to keep sampling payload sizes reasonable.
        var promptJson = AiSamplingPromptBuilder.Build(initialReport);

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
            SamplingTraceLabel = $"{sessionId}-{session.CurrentDumpId ?? "no-dump"}"
        };

        var aiResult = await orchestrator.AnalyzeCrashAsync(
                initialReport,
                promptJson,
                manager,
                session.ClrMdAnalyzer,
                cancellationToken)
            .ConfigureAwait(false);

        initialReport.AiAnalysis = aiResult;

        // Return the canonical report document shape so `analyze(kind=ai)` matches `report -f json`.
        // Other formats should be derived from this JSON document.
        var reportService = new ReportService();
        var metadata = new ReportMetadata
        {
            DumpId = session.CurrentDumpId ?? string.Empty,
            UserId = sanitizedUserId,
            GeneratedAt = DateTime.UtcNow,
            DebuggerType = manager.DebuggerType,
            Format = ReportFormat.Json
        };

        return reportService.GenerateReport(initialReport, new ReportOptions { Format = ReportFormat.Json }, metadata);
    }
}
