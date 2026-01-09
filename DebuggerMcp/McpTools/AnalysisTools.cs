using System.ComponentModel;
using DebuggerMcp.Analysis;
using DebuggerMcp.Reporting;
using DebuggerMcp.Security;
using DebuggerMcp.SourceLink;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.McpTools;

/// <summary>
/// MCP tools for automated crash analysis.
/// </summary>
/// <remarks>
/// Provides tools for:
/// <list type="bullet">
/// <item><description>.NET crash analysis (SOS/ClrMD)</description></item>
/// </list>
/// 
/// These tools provide AI-friendly structured output with crash type, exception details,
/// call stacks, memory analysis, and recommendations.
/// </remarks>
public class AnalysisTools(
    DebuggerSessionManager sessionManager,
    SymbolManager symbolManager,
    WatchStore watchStore,
    ILogger<AnalysisTools> logger)
    : DebuggerToolsBase(sessionManager, symbolManager, watchStore, logger)
{
    /// <summary>
    /// Performs automated crash analysis on the currently open dump.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session (for security validation).</param>
    /// <param name="includeWatches">Include watch expression evaluations in the report.</param>
    /// <returns>Canonical JSON report document (same schema as <c>report -o ./report.json --format json</c>).</returns>
    /// <remarks>
    /// This tool automatically:
    /// - Analyzes the crash dump to determine crash type
    /// - Extracts exception information
    /// - Analyzes call stacks
    /// - Provides recommendations
    /// 
    /// The output is structured JSON that includes:
    /// - Crash type (e.g., "Access Violation", "Stack Overflow")
    /// - Exception details
    /// - Thread information
    /// - Summary and recommendations
    /// 
    /// IMPORTANT: A dump file must be open before calling this tool (use <c>dump(action="open")</c> first; CLI: <c>open &lt;dumpId&gt;</c>).
    /// </remarks>
    public async Task<string> AnalyzeCrash(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Include watch expression evaluations in the report (default: true)")] bool includeWatches = true)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);

        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        // Get the session with user ownership validation
        var manager = GetSessionManager(sessionId, sanitizedUserId);
        var session = GetSessionInfo(sessionId, sanitizedUserId);

        // Check if a dump is open
        ValidateDumpIsOpen(manager);

        // Get a cached Source Link resolver configured for the current dump (PDBs may live under .symbols_{dumpId}).
        var sourceLinkResolver = GetOrCreateSourceLinkResolver(session, sanitizedUserId);

        if (!manager.IsSosLoaded && session.ClrMdAnalyzer?.IsOpen != true)
        {
            throw new InvalidOperationException(
                "This server is configured for .NET crash analysis only. SOS and/or ClrMD must be available. " +
                "Ensure the dump is a .NET dump and that it was opened via dump(action=\"open\") (CLI: open <dumpId>).");
        }

        // .NET-specific analysis using SOS and ClrMD enrichment (when available).
        var analyzer = new DotNetCrashAnalyzer(manager, sourceLinkResolver, session.ClrMdAnalyzer, Logger);
        var result = await analyzer.AnalyzeDotNetCrashAsync();

        // Run security analysis and include in results
        var securityAnalyzer = new SecurityAnalyzer(manager);
        var securityResult = await securityAnalyzer.AnalyzeSecurityAsync();
        ReportEnrichment.ApplySecurity(result, securityResult);

        // Include watch evaluations if enabled and dump has watches
        if (includeWatches && !string.IsNullOrEmpty(session.CurrentDumpId))
        {
            var hasWatches = await WatchStore.HasWatchesAsync(sanitizedUserId, session.CurrentDumpId);
            if (hasWatches)
            {
                var evaluator = new WatchEvaluator(manager, WatchStore);
                result.Watches = await evaluator.EvaluateAllAsync(sanitizedUserId, session.CurrentDumpId);

                // Add watch insights to recommendations for actionable guidance
                if (result.Watches?.Insights?.Count > 0)
                {
                    result.Summary?.Recommendations?.AddRange(result.Watches.Insights);
                }
            }
        }

        return GenerateCanonicalJsonReport(result, session, sanitizedUserId, manager.DebuggerType, includeWatches);
    }

    private static string GenerateCanonicalJsonReport(
        CrashAnalysisResult analysis,
        DebuggerSession session,
        string userId,
        string debuggerType,
        bool includeWatches)
    {
        if (analysis == null)
        {
            throw new ArgumentNullException(nameof(analysis));
        }

        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        // Treat the JSON report document shape as the canonical representation of crash analysis.
        // This ensures `analyze` and `report -f json` stay consistent and other formats can be derived from JSON.
        var reportService = new ReportService();
        var metadata = new ReportMetadata
        {
            DumpId = session.CurrentDumpId ?? string.Empty,
            UserId = userId,
            GeneratedAt = DateTime.UtcNow,
            DebuggerType = debuggerType,
            SosLoaded = session.Manager?.IsDotNetDump == true ? session.Manager.IsSosLoaded : null,
            Format = ReportFormat.Json
        };

        var json = reportService.GenerateReport(analysis, new ReportOptions { Format = ReportFormat.Json }, metadata);

        if (!string.IsNullOrWhiteSpace(metadata.DumpId))
        {
            session.SetCachedReport(
                metadata.DumpId,
                metadata.GeneratedAt,
                json,
                includesWatches: includeWatches,
                includesSecurity: true,
                maxStackFrames: 0,
                includesAiAnalysis: false);
        }

        return json;
    }
}
