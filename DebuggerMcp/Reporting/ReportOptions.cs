using System;
using System.Text.Json.Serialization;

namespace DebuggerMcp.Reporting;

/// <summary>
/// Supported report output formats.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReportFormat
{
    /// <summary>
    /// Markdown format with ASCII charts. Works in any text viewer.
    /// </summary>
    Markdown,

    /// <summary>
    /// HTML format with styled charts and CSS. Opens in any browser.
    /// </summary>
    Html,

    /// <summary>
    /// JSON format for programmatic consumption.
    /// </summary>
    Json
}

/// <summary>
/// Options for customizing report generation.
/// </summary>
public class ReportOptions
{
    /// <summary>
    /// Gets or sets the output format for the report.
    /// </summary>
    public ReportFormat Format { get; set; } = ReportFormat.Markdown;

    /// <summary>
    /// Gets or sets a custom title for the report. If null, uses default.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets whether to include the exception/crash information section.
    /// </summary>
    public bool IncludeCrashInfo { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to include call stack information.
    /// </summary>
    public bool IncludeCallStacks { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to include thread information.
    /// </summary>
    public bool IncludeThreadInfo { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to include module information.
    /// </summary>
    public bool IncludeModules { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to include heap/memory statistics.
    /// </summary>
    public bool IncludeHeapStats { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to include memory leak analysis.
    /// </summary>
    public bool IncludeMemoryLeakInfo { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to include deadlock analysis.
    /// </summary>
    public bool IncludeDeadlockInfo { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to include watch expression results.
    /// </summary>
    public bool IncludeWatchResults { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to include security vulnerability analysis.
    /// </summary>
    public bool IncludeSecurityAnalysis { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to include .NET specific information.
    /// </summary>
    public bool IncludeDotNetInfo { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to include process information (arguments and environment variables).
    /// Only available for Linux/macOS dumps analyzed with LLDB.
    /// </summary>
    public bool IncludeProcessInfo { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of environment variables to include in the report.
    /// Set to 0 for unlimited. Default is 100 to avoid very large reports.
    /// </summary>
    public int MaxEnvironmentVariables { get; set; } = 100;

    /// <summary>
    /// Gets or sets whether to include recommendations.
    /// </summary>
    public bool IncludeRecommendations { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to include visual charts (ASCII for Markdown, CSS for HTML).
    /// </summary>
    public bool IncludeCharts { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of threads to include in detail.
    /// Set to 0 for unlimited.
    /// </summary>
    public int MaxThreadsToShow { get; set; } = 0; // Show all threads by default

    /// <summary>
    /// Gets or sets the maximum number of modules to include.
    /// Set to 0 for unlimited.
    /// </summary>
    public int MaxModulesToShow { get; set; } = 50;

    /// <summary>
    /// Gets or sets the maximum call stack depth to show.
    /// Set to 0 for unlimited.
    /// </summary>
    public int MaxCallStackFrames { get; set; } = 0; // Show all frames by default

    /// <summary>
    /// Gets or sets whether to include verbose raw JSON detail blocks within derived reports.
    /// </summary>
    /// <remarks>
    /// JSON is always available via <c>report(action="full", format="json")</c> and <c>report(action="get")</c>.
    /// Disabling raw JSON details keeps Markdown/HTML reports concise (especially for summary reports).
    /// </remarks>
    public bool IncludeRawJsonDetails { get; set; } = true;

    /// <summary>
    /// Creates default options for a full report.
    /// </summary>
    public static ReportOptions FullReport => new()
    {
        IncludeCrashInfo = true,
        IncludeCallStacks = true,
        IncludeThreadInfo = true,
        IncludeModules = true,
        IncludeHeapStats = true,
        IncludeMemoryLeakInfo = true,
        IncludeDeadlockInfo = true,
        IncludeWatchResults = true,
        IncludeSecurityAnalysis = true,
        IncludeDotNetInfo = true,
        IncludeProcessInfo = true,
        IncludeRecommendations = true,
        IncludeCharts = true
    };

    /// <summary>
    /// Creates options for a summary report (minimal details).
    /// </summary>
    public static ReportOptions SummaryReport => new()
    {
        IncludeCrashInfo = true,
        IncludeCallStacks = true,
        IncludeThreadInfo = false,
        IncludeModules = false,
        IncludeHeapStats = true,
        IncludeMemoryLeakInfo = true,
        IncludeDeadlockInfo = true,
        IncludeWatchResults = true,
        IncludeSecurityAnalysis = true,
        IncludeDotNetInfo = false,
        IncludeProcessInfo = true,
        IncludeRecommendations = true,
        IncludeCharts = true,
        MaxCallStackFrames = 10,
        MaxThreadsToShow = 5,
        MaxEnvironmentVariables = 20,  // Show fewer env vars in summary
        IncludeRawJsonDetails = false
    };
}

/// <summary>
/// Metadata about a generated report.
/// </summary>
public class ReportMetadata
{
    /// <summary>
    /// Gets or sets the dump ID this report is for.
    /// </summary>
    [JsonPropertyName("dumpId")]
    public string DumpId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user ID who generated the report.
    /// </summary>
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the report was generated.
    /// </summary>
    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the format of the report.
    /// </summary>
    [JsonPropertyName("format")]
    public ReportFormat Format { get; set; }

    /// <summary>
    /// Gets or sets the debugger type used (WinDbg/LLDB).
    /// </summary>
    [JsonPropertyName("debuggerType")]
    public string DebuggerType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the server version.
    /// </summary>
    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; set; } = "1.0.0";
}
