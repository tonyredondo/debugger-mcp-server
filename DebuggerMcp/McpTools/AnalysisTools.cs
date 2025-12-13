using System.ComponentModel;
using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Security;
using DebuggerMcp.SourceLink;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DebuggerMcp.McpTools;

/// <summary>
/// MCP tools for automated crash analysis.
/// </summary>
/// <remarks>
/// Provides tools for:
/// <list type="bullet">
/// <item><description>General crash analysis</description></item>
/// <item><description>.NET-specific crash analysis</description></item>
/// </list>
/// 
/// These tools provide AI-friendly structured output with crash type, exception details,
/// call stacks, memory analysis, and recommendations.
/// </remarks>
[McpServerToolType]
public class AnalysisTools(
    DebuggerSessionManager sessionManager,
    SymbolManager symbolManager,
    WatchStore watchStore,
    ILogger<AnalysisTools> logger)
    : DebuggerToolsBase(sessionManager, symbolManager, watchStore, logger)
{
    /// <summary>
    /// JSON serialization options for analysis results.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Performs automated crash analysis on the currently open dump.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session (for security validation).</param>
    /// <param name="includeWatches">Include watch expression evaluations in the report.</param>
    /// <returns>JSON formatted crash analysis results.</returns>
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
    /// IMPORTANT: A dump file must be open before calling this tool (use OpenDump first).
    /// </remarks>
    [McpServerTool, Description("Perform automated crash analysis on the open dump. Returns structured JSON with crash type, exception info, watch results, and recommendations.")]
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

        // Use DotNetCrashAnalyzer for more complete analysis (CLR info, managed exceptions,
        // heap stats, interleaved call stacks from clrstack -f -r -all, async deadlock detection)
        var analyzer = new DotNetCrashAnalyzer(manager, sourceLinkResolver, session.ClrMdAnalyzer, Logger);
        var result = await analyzer.AnalyzeDotNetCrashAsync();

        // Run security analysis and include in results
        var securityAnalyzer = new SecurityAnalyzer(manager);
        var securityResult = await securityAnalyzer.AnalyzeSecurityAsync();
        if (securityResult != null)
        {
            result.Security = new SecurityInfo
            {
                HasVulnerabilities = securityResult.Vulnerabilities?.Count > 0,
                OverallRisk = securityResult.OverallRisk.ToString(),
                Summary = securityResult.Summary,
                AnalyzedAt = securityResult.AnalyzedAt.ToString("O"),
                Findings = securityResult.Vulnerabilities?.Select(v => new SecurityFinding
                {
                    Type = v.Type.ToString(),
                    Severity = v.Severity.ToString(),
                    Description = v.Description,
                    Location = v.Address,
                    Recommendation = v.Details
                }).ToList(),
                Recommendations = securityResult.Recommendations
            };
        }

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

        // Return JSON formatted result
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <summary>
    /// Performs .NET specific crash analysis on the currently open dump.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session (for security validation).</param>
    /// <param name="includeWatches">Include watch expression evaluations in the report.</param>
    /// <returns>JSON formatted .NET crash analysis results.</returns>
    /// <remarks>
    /// This tool provides .NET specific analysis including:
    /// - CLR version information
    /// - Managed exception details
    /// - Heap statistics and memory usage
    /// - Async/await deadlock detection
    /// - Finalizer queue analysis
    /// 
    /// IMPORTANT: 
    /// - A dump file must be open (use OpenDump first - SOS is auto-loaded for .NET dumps)
    /// - The dump must be from a .NET application
    /// </remarks>
    [McpServerTool, Description("Perform .NET specific crash analysis. Returns structured JSON with CLR info, managed exceptions, heap stats, watch results, and async deadlock detection. SOS is auto-loaded by OpenDump.")]
    public async Task<string> AnalyzeDotNetCrash(
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

        // Create .NET analyzer and perform analysis
        var analyzer = new DotNetCrashAnalyzer(manager, sourceLinkResolver, session.ClrMdAnalyzer, Logger);
        var result = await analyzer.AnalyzeDotNetCrashAsync();

        // Run security analysis and include in results
        var securityAnalyzer = new SecurityAnalyzer(manager);
        var securityResult = await securityAnalyzer.AnalyzeSecurityAsync();
        if (securityResult != null)
        {
            result.Security = new SecurityInfo
            {
                HasVulnerabilities = securityResult.Vulnerabilities?.Count > 0,
                OverallRisk = securityResult.OverallRisk.ToString(),
                Summary = securityResult.Summary,
                AnalyzedAt = securityResult.AnalyzedAt.ToString("O"),
                Findings = securityResult.Vulnerabilities?.Select(v => new SecurityFinding
                {
                    Type = v.Type.ToString(),
                    Severity = v.Severity.ToString(),
                    Description = v.Description,
                    Location = v.Address,
                    Recommendation = v.Details
                }).ToList(),
                Recommendations = securityResult.Recommendations
            };
        }

        // Include watch evaluations if enabled and dump has watches
        if (includeWatches && !string.IsNullOrEmpty(session.CurrentDumpId))
        {
            var hasWatches = await WatchStore.HasWatchesAsync(sanitizedUserId, session.CurrentDumpId);
            if (hasWatches)
            {
                var evaluator = new WatchEvaluator(manager, WatchStore);
                result.Watches = await evaluator.EvaluateAllAsync(sanitizedUserId, session.CurrentDumpId);

                // Add watch insights to recommendations for actionable guidance (if available)
                if (result.Watches?.Insights?.Count > 0)
                {
                    result.Summary?.Recommendations?.AddRange(result.Watches.Insights);
                }
            }
        }

        // Return JSON formatted result
        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
