using System.ComponentModel;
using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Reporting;
using DebuggerMcp.Security;
using DebuggerMcp.Serialization;
using DebuggerMcp.SourceLink;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DebuggerMcp.McpTools;

/// <summary>
/// MCP tools for generating reports from crash analysis.
/// </summary>
/// <remarks>
/// Provides tools for:
/// <list type="bullet">
/// <item><description>Generating comprehensive reports in Markdown, HTML, or JSON</description></item>
/// <item><description>Generating summary reports with key findings only</description></item>
/// </list>
/// 
/// Reports include crash analysis, security findings, watch evaluations, and recommendations.
/// </remarks>
[McpServerToolType]
public class ReportTools(
    DebuggerSessionManager sessionManager,
    SymbolManager symbolManager,
    WatchStore watchStore,
    ILogger<ReportTools> logger)
    : DebuggerToolsBase(sessionManager, symbolManager, watchStore, logger)
{
    /// <summary>
    /// Generates a comprehensive crash analysis report.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="format">Report format: "markdown", "html", or "json".</param>
    /// <param name="includeWatches">Include watch expression evaluations.</param>
    /// <param name="includeSecurity">Include security analysis results.</param>
    /// <param name="maxStackFrames">Maximum number of stack frames to include (0 = all frames).</param>
    /// <returns>The generated report content.</returns>
    /// <remarks>
    /// Generates a comprehensive report including:
    /// - Crash summary and type
    /// - Exception details
    /// - Call stack with Source Link URLs
    /// - Memory analysis
    /// - Thread information
    /// - Security findings
    /// - Watch expression values
    /// - Recommendations
    /// 
    /// The markdown format includes ASCII charts for visual representation.
    /// The HTML format is styled and printable.
    /// The JSON format is machine-readable.
    /// </remarks>
    [McpServerTool, Description("Generate a comprehensive crash analysis report. Formats: markdown (ASCII charts), html (styled, printable), json.")]
    public async Task<string> GenerateReport(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Report format: 'markdown', 'html', or 'json' (default: markdown)")] string format = "markdown",
        [Description("Include watch expression evaluations (default: true)")] bool includeWatches = true,
        [Description("Include security analysis results (default: true)")] bool includeSecurity = true,
        [Description("Maximum stack frames to include (0 = all frames)")] int maxStackFrames = 0)
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

        // Parse the format - default to markdown if invalid
        if (!Enum.TryParse<ReportFormat>(format, ignoreCase: true, out var reportFormat))
        {
            // Fall back to markdown so we always emit a report instead of failing
            reportFormat = ReportFormat.Markdown;
        }

        // Create report options using the correct properties
        var options = new ReportOptions
        {
            Format = reportFormat,
            IncludeCrashInfo = true,
            IncludeCallStacks = true,
            IncludeThreadInfo = true,
            IncludeModules = true,
            IncludeHeapStats = true,
            IncludeMemoryLeakInfo = true,
            IncludeDeadlockInfo = true,
            IncludeWatchResults = includeWatches,
            IncludeSecurityAnalysis = includeSecurity,
            IncludeDotNetInfo = true,
            IncludeRecommendations = true,
            IncludeCharts = true,
            MaxCallStackFrames = maxStackFrames
        };

        // Get a cached Source Link resolver configured for the current dump (PDBs may live under .symbols_{dumpId}).
        var sourceLinkResolver = GetOrCreateSourceLinkResolver(session, sanitizedUserId);

        // Use appropriate analyzer based on whether SOS is loaded
        // If SOS is loaded: Use DotNetCrashAnalyzer for CLR info, managed exceptions, heap stats, etc.
        // If SOS is not loaded: Use basic CrashAnalyzer for native analysis
        CrashAnalysisResult result;
        if (manager.IsSosLoaded)
        {
            Logger.LogInformation("[ReportTools] SOS is loaded, using .NET crash analyzer");
            var dotNetAnalyzer = new DotNetCrashAnalyzer(manager, sourceLinkResolver, session.ClrMdAnalyzer, Logger);
            result = await dotNetAnalyzer.AnalyzeDotNetCrashAsync();
        }
        else
        {
            Logger.LogInformation("[ReportTools] SOS is not loaded, using basic crash analyzer");
            var basicAnalyzer = new CrashAnalyzer(manager, sourceLinkResolver);
            result = await basicAnalyzer.AnalyzeCrashAsync();
        }

        // Run security analysis if enabled
        if (includeSecurity)
        {
            var securityAnalyzer = new SecurityAnalyzer(manager);
            var securityResult = await securityAnalyzer.AnalyzeSecurityAsync();
            ReportEnrichment.ApplySecurity(result, securityResult);
        }

        // Include watch evaluations if enabled and dump has watches
        if (includeWatches && !string.IsNullOrEmpty(session.CurrentDumpId))
        {
            var hasWatches = await WatchStore.HasWatchesAsync(sanitizedUserId, session.CurrentDumpId);
            if (hasWatches)
            {
                var evaluator = new WatchEvaluator(manager, WatchStore);
                result.Watches = await evaluator.EvaluateAllAsync(sanitizedUserId, session.CurrentDumpId);
            }
        }

        // Create metadata
        var metadata = new ReportMetadata
        {
            DumpId = session.CurrentDumpId ?? string.Empty,
            UserId = sanitizedUserId,
            GeneratedAt = DateTime.UtcNow,
            Format = reportFormat,
            DebuggerType = manager.DebuggerType
        };

        // Generate report based on format
        IReportGenerator generator = reportFormat switch
        {
            ReportFormat.Html => new HtmlReportGenerator(),
            ReportFormat.Json => new JsonReportGenerator(),
            _ => new MarkdownReportGenerator()
        };

        return generator.Generate(result, options, metadata);
    }

    /// <summary>
    /// Generates a brief summary report with key findings only.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="format">Report format: "markdown", "html", or "json".</param>
    /// <returns>The generated summary report.</returns>
    /// <remarks>
    /// Generates a condensed report including:
    /// - Crash type and summary
    /// - Primary exception
    /// - Top stack frame
    /// - Critical security findings (if any)
    /// - Key recommendations
    /// 
    /// Useful for quick triage of crash dumps.
    /// </remarks>
    [McpServerTool, Description("Generate a brief summary report with key findings only.")]
    public async Task<string> GenerateSummaryReport(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Report format: 'markdown', 'html', or 'json' (default: markdown)")] string format = "markdown")
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

        // Parse the format - default to markdown if invalid
        if (!Enum.TryParse<ReportFormat>(format, ignoreCase: true, out var reportFormat))
        {
            reportFormat = ReportFormat.Markdown;
        }

        // Use the built-in SummaryReport preset with some overrides
        var options = ReportOptions.SummaryReport;
        options.Format = reportFormat;

        // Get a cached Source Link resolver configured for the current dump (PDBs may live under .symbols_{dumpId}).
        var sourceLinkResolver = GetOrCreateSourceLinkResolver(session, sanitizedUserId);

        // Use appropriate analyzer based on whether SOS is loaded
        CrashAnalysisResult result;
        if (manager.IsSosLoaded)
        {
            Logger.LogInformation("[ReportTools] SOS is loaded, using .NET crash analyzer for summary");
            var dotNetAnalyzer = new DotNetCrashAnalyzer(manager, sourceLinkResolver, session.ClrMdAnalyzer, Logger);
            result = await dotNetAnalyzer.AnalyzeDotNetCrashAsync();
        }
        else
        {
            Logger.LogInformation("[ReportTools] SOS is not loaded, using basic crash analyzer for summary");
            var basicAnalyzer = new CrashAnalyzer(manager, sourceLinkResolver);
            result = await basicAnalyzer.AnalyzeCrashAsync();
        }

        // Run security analysis for critical findings
        var securityAnalyzer = new SecurityAnalyzer(manager);
        var securityResult = await securityAnalyzer.AnalyzeSecurityAsync();
        ReportEnrichment.ApplySecurity(result, securityResult);

        // Create metadata
        var metadata = new ReportMetadata
        {
            DumpId = session.CurrentDumpId ?? string.Empty,
            UserId = sanitizedUserId,
            GeneratedAt = DateTime.UtcNow,
            Format = reportFormat,
            DebuggerType = manager.DebuggerType
        };

        // Generate report based on format
        IReportGenerator generator = reportFormat switch
        {
            ReportFormat.Html => new HtmlReportGenerator(),
            ReportFormat.Json => new JsonReportGenerator(),
            _ => new MarkdownReportGenerator()
        };

        return generator.Generate(result, options, metadata);
    }
}

/// <summary>
/// Simple JSON report generator that serializes the analysis result directly.
/// </summary>
internal class JsonReportGenerator : IReportGenerator
{
    /// <inheritdoc />
    public string Generate(CrashAnalysisResult analysis, ReportOptions options, ReportMetadata metadata)
    {
        // Default-on: include bounded source context snippets and normalize timeline timestamps.
        SourceContextEnricher.Apply(analysis, metadata.GeneratedAt);

        var report = new
        {
            metadata,
            analysis
        };

        return JsonSerializer.Serialize(report, JsonSerializationDefaults.IndentedIgnoreNull);
    }
}
