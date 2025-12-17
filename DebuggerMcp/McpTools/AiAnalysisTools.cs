#nullable enable

using System.ComponentModel;
using System.Text.Json;
using DebuggerMcp.Analysis;
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
    /// <returns>JSON crash report enriched with an <c>aiAnalysis</c> section.</returns>
    public async Task<string> AnalyzeCrashWithAiAsync(
        McpServer server,
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Maximum analysis iterations (default: 10)")] int maxIterations = 10,
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

        CrashAnalysisResult initialReport;
        if (manager.IsSosLoaded)
        {
            Logger.LogInformation("[AI] SOS loaded, using DotNetCrashAnalyzer for initial report");
            var dotNetAnalyzer = new DotNetCrashAnalyzer(manager, sourceLinkResolver, session.ClrMdAnalyzer, Logger);
            initialReport = await dotNetAnalyzer.AnalyzeDotNetCrashAsync().ConfigureAwait(false);
        }
        else
        {
            Logger.LogInformation("[AI] SOS not loaded, using CrashAnalyzer for initial report");
            var basicAnalyzer = new CrashAnalyzer(manager, sourceLinkResolver);
            initialReport = await basicAnalyzer.AnalyzeCrashAsync().ConfigureAwait(false);
        }

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

        var initialJson = JsonSerializer.Serialize(initialReport, JsonSerializationDefaults.IndentedIgnoreNull);

        var samplingClient = new McpSamplingClient(server);
        var orchestrator = new AiAnalysisOrchestrator(
            samplingClient,
            _loggerFactory.CreateLogger<AiAnalysisOrchestrator>())
        {
            MaxIterations = maxIterations,
            MaxTokensPerRequest = maxTokens
        };

        var aiResult = await orchestrator.AnalyzeCrashAsync(
                initialReport,
                initialJson,
                manager,
                session.ClrMdAnalyzer,
                cancellationToken)
            .ConfigureAwait(false);

        initialReport.AiAnalysis = aiResult;

        return JsonSerializer.Serialize(initialReport, JsonSerializationDefaults.IndentedIgnoreNull);
    }
}

