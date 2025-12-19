using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DebuggerMcp.Analysis;
using DebuggerMcp.Serialization;

namespace DebuggerMcp.Reporting;

/// <summary>
/// Service for generating reports from crash analysis results.
/// Coordinates between different report generators based on format.
/// </summary>
public class ReportService
{
    private readonly JsonReportGenerator _jsonGenerator;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportService"/> class.
    /// </summary>
    public ReportService()
    {
        _jsonGenerator = new JsonReportGenerator();
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

        // Treat JSON as the source of truth for all formats.
        // Always generate the canonical JSON report document first, then render other formats from it.
        var canonicalOptions = new ReportOptions { Format = ReportFormat.Json };

        var canonicalMetadata = new ReportMetadata
        {
            DumpId = metadata.DumpId,
            UserId = metadata.UserId,
            GeneratedAt = metadata.GeneratedAt,
            DebuggerType = metadata.DebuggerType,
            ServerVersion = metadata.ServerVersion,
            Format = ReportFormat.Json
        };

        var json = _jsonGenerator.Generate(analysis, canonicalOptions, canonicalMetadata);

        if (options.Format == ReportFormat.Json)
        {
            return options.MaxCallStackFrames > 0 ? ApplyJsonOutputLimits(json, options, applyListLimits: false) : json;
        }

        var jsonForRendering = ApplyJsonOutputLimits(json, options, applyListLimits: true);
        return options.Format switch
        {
            ReportFormat.Markdown => JsonMarkdownReportRenderer.Render(jsonForRendering, options),
            ReportFormat.Html => JsonHtmlReportRenderer.Render(jsonForRendering, options),
            _ => throw new ArgumentOutOfRangeException(nameof(options.Format), $"Unsupported format: {options.Format}")
        };
    }

    private static string ApplyJsonOutputLimits(string reportJson, ReportOptions options, bool applyListLimits)
    {
        if (!applyListLimits && options.MaxCallStackFrames <= 0)
        {
            return reportJson;
        }

        if (applyListLimits &&
            options.MaxCallStackFrames <= 0 &&
            options.MaxThreadsToShow <= 0 &&
            options.MaxModulesToShow <= 0 &&
            options.MaxEnvironmentVariables <= 0)
        {
            return reportJson;
        }

        if (string.IsNullOrWhiteSpace(reportJson))
        {
            return reportJson;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(reportJson);
        }
        catch
        {
            return reportJson;
        }

        if (node is not JsonObject root)
        {
            return reportJson;
        }

        if (root["analysis"] is not JsonObject analysis)
        {
            return reportJson;
        }

        ApplyThreadLimits(analysis, options, applyThreadListLimit: applyListLimits);
        if (applyListLimits)
        {
            ApplyModuleLimits(analysis, options);
            ApplyEnvironmentLimits(analysis, options);
        }

        try
        {
            return JsonSerializer.Serialize(root, JsonSerializationDefaults.IndentedIgnoreNull);
        }
        catch
        {
            return reportJson;
        }
    }

    private static void ApplyThreadLimits(JsonObject analysis, ReportOptions options, bool applyThreadListLimit)
    {
        if (analysis["threads"] is not JsonObject threads)
        {
            return;
        }

        if (options.MaxCallStackFrames > 0)
        {
            TruncateCallStack(threads["faultingThread"], options.MaxCallStackFrames);

            if (threads["all"] is JsonArray allThreads)
            {
                foreach (var t in allThreads)
                {
                    TruncateCallStack(t, options.MaxCallStackFrames);
                }
            }
        }

        if (applyThreadListLimit && options.MaxThreadsToShow > 0 && threads["all"] is JsonArray all && all.Count > options.MaxThreadsToShow)
        {
            var limited = new JsonArray();

            // Prefer including faulting threads first, then fill remaining slots in original order.
            foreach (var t in all)
            {
                if (t is not JsonObject obj)
                {
                    continue;
                }

                if (IsFaultingThread(obj))
                {
                    limited.Add(obj.DeepClone());
                }
            }

            foreach (var t in all)
            {
                if (limited.Count >= options.MaxThreadsToShow)
                {
                    break;
                }

                if (t is not JsonObject obj)
                {
                    continue;
                }

                if (IsFaultingThread(obj))
                {
                    continue;
                }

                limited.Add(obj.DeepClone());
            }

            // As a final fallback, ensure we always return something.
            if (limited.Count == 0)
            {
                for (var i = 0; i < Math.Min(all.Count, options.MaxThreadsToShow); i++)
                {
                    limited.Add(all[i]?.DeepClone());
                }
            }

            threads["all"] = limited;
        }
    }

    private static bool IsFaultingThread(JsonObject thread)
    {
        var value = thread["isFaulting"];
        return value is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    private static void TruncateCallStack(JsonNode? threadNode, int maxFrames)
    {
        if (maxFrames <= 0)
        {
            return;
        }

        if (threadNode is not JsonObject thread)
        {
            return;
        }

        if (thread["callStack"] is not JsonArray callStack || callStack.Count <= maxFrames)
        {
            return;
        }

        var truncated = new JsonArray();
        for (var i = 0; i < maxFrames && i < callStack.Count; i++)
        {
            truncated.Add(callStack[i]?.DeepClone());
        }

        thread["callStack"] = truncated;
    }

    private static void ApplyModuleLimits(JsonObject analysis, ReportOptions options)
    {
        if (options.MaxModulesToShow <= 0)
        {
            return;
        }

        if (analysis["modules"] is not JsonArray modules || modules.Count <= options.MaxModulesToShow)
        {
            return;
        }

        var limited = new JsonArray();
        for (var i = 0; i < options.MaxModulesToShow && i < modules.Count; i++)
        {
            limited.Add(modules[i]?.DeepClone());
        }

        analysis["modules"] = limited;
    }

    private static void ApplyEnvironmentLimits(JsonObject analysis, ReportOptions options)
    {
        if (options.MaxEnvironmentVariables <= 0)
        {
            return;
        }

        if (analysis["environment"] is not JsonObject env ||
            env["process"] is not JsonObject process ||
            process["environmentVariables"] is not JsonArray vars ||
            vars.Count <= options.MaxEnvironmentVariables)
        {
            return;
        }

        var limited = new JsonArray();
        for (var i = 0; i < options.MaxEnvironmentVariables && i < vars.Count; i++)
        {
            limited.Add(vars[i]?.DeepClone());
        }

        process["environmentVariables"] = limited;
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
}
