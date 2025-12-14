using System;
using System.Text;
using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Serialization;

namespace DebuggerMcp.Reporting;

/// <summary>
/// Service for generating reports from crash analysis results.
/// Coordinates between different report generators based on format.
/// </summary>
public class ReportService
{
    private readonly MarkdownReportGenerator _markdownGenerator;
    private readonly HtmlReportGenerator _htmlGenerator;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportService"/> class.
    /// </summary>
    public ReportService()
    {
        _markdownGenerator = new MarkdownReportGenerator();
        _htmlGenerator = new HtmlReportGenerator();
    }

    /// <summary>
    /// Generates a report from a crash analysis result.
    /// </summary>
    /// <param name="analysis">The crash analysis result.</param>
    /// <param name="options">Report generation options.</param>
    /// <param name="metadata">Report metadata.</param>
    /// <returns>The generated report content.</returns>
    public string GenerateReport(CrashAnalysisResult analysis, ReportOptions options, ReportMetadata metadata)
    {
        if (analysis == null)
        {
            throw new ArgumentNullException(nameof(analysis));
        }

        options ??= ReportOptions.FullReport;
        metadata ??= new ReportMetadata();

        return options.Format switch
        {
            ReportFormat.Markdown => _markdownGenerator.Generate(analysis, options, metadata),
            ReportFormat.Html => _htmlGenerator.Generate(analysis, options, metadata),
            ReportFormat.Json => GenerateJsonReport(analysis, metadata),
            _ => throw new ArgumentOutOfRangeException(nameof(options.Format), $"Unsupported format: {options.Format}")
        };
    }

    /// <summary>
    /// Generates a report with default options.
    /// </summary>
    /// <param name="analysis">The crash analysis result.</param>
    /// <param name="format">The output format.</param>
    /// <param name="dumpId">The dump ID for metadata.</param>
    /// <param name="userId">The user ID for metadata.</param>
    /// <param name="debuggerType">The debugger type.</param>
    /// <returns>The generated report content.</returns>
    public string GenerateReport(
        CrashAnalysisResult analysis,
        ReportFormat format,
        string dumpId,
        string userId,
        string debuggerType)
    {
        var options = new ReportOptions { Format = format };
        var metadata = new ReportMetadata
        {
            DumpId = dumpId,
            UserId = userId,
            DebuggerType = debuggerType,
            GeneratedAt = DateTime.UtcNow
        };

        return GenerateReport(analysis, options, metadata);
    }

    /// <summary>
    /// Gets the appropriate content type for a report format.
    /// </summary>
    /// <param name="format">The report format.</param>
    /// <returns>The MIME content type.</returns>
    public static string GetContentType(ReportFormat format)
    {
        return format switch
        {
            ReportFormat.Markdown => "text/markdown",
            ReportFormat.Html => "text/html",
            ReportFormat.Json => "application/json",
            _ => "text/plain"
        };
    }

    /// <summary>
    /// Gets the appropriate file extension for a report format.
    /// </summary>
    /// <param name="format">The report format.</param>
    /// <returns>The file extension (without dot).</returns>
    public static string GetFileExtension(ReportFormat format)
    {
        return format switch
        {
            ReportFormat.Markdown => "md",
            ReportFormat.Html => "html",
            ReportFormat.Json => "json",
            _ => "txt"
        };
    }

    /// <summary>
    /// Parses a format string to ReportFormat enum.
    /// </summary>
    /// <param name="format">The format string (e.g., "markdown", "html", "json").</param>
    /// <returns>The parsed ReportFormat.</returns>
    public static ReportFormat ParseFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return ReportFormat.Markdown;
        }

        return format.ToLowerInvariant() switch
        {
            "markdown" or "md" => ReportFormat.Markdown,
            "html" or "htm" => ReportFormat.Html,
            "json" => ReportFormat.Json,
            _ => throw new ArgumentException($"Unknown format: {format}. Supported formats: markdown, html, json", nameof(format))
        };
    }

    /// <summary>
    /// Gets report content as bytes with appropriate encoding.
    /// </summary>
    /// <param name="analysis">The crash analysis result.</param>
    /// <param name="options">Report generation options.</param>
    /// <param name="metadata">Report metadata.</param>
    /// <returns>The report content as bytes.</returns>
    public byte[] GenerateReportBytes(CrashAnalysisResult analysis, ReportOptions options, ReportMetadata metadata)
    {
        var content = GenerateReport(analysis, options, metadata);
        return Encoding.UTF8.GetBytes(content);
    }

    private static string GenerateJsonReport(CrashAnalysisResult analysis, ReportMetadata metadata)
    {
        // Default-on: include bounded source context snippets and normalize timeline timestamps.
        SourceContextEnricher.Apply(analysis, metadata.GeneratedAt);

        var report = new
        {
            metadata = new
            {
                metadata.DumpId,
                metadata.UserId,
                metadata.GeneratedAt,
                metadata.DebuggerType,
                metadata.ServerVersion,
                format = "json"
            },
            analysis
        };

        return JsonSerializer.Serialize(report, JsonSerializationDefaults.IndentedCamelCaseIgnoreNull);
    }
}
