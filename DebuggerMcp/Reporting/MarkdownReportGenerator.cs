using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DebuggerMcp.Analysis;
using DebuggerMcp.Watches;

namespace DebuggerMcp.Reporting;

/// <summary>
/// Generates crash analysis reports in Markdown format with ASCII charts.
/// </summary>
public class MarkdownReportGenerator : IReportGenerator
{
    /// <inheritdoc />
    public string Generate(CrashAnalysisResult analysis, ReportOptions options, ReportMetadata metadata)
    {
        if (analysis == null)
        {
            throw new ArgumentNullException(nameof(analysis));
        }

        options ??= ReportOptions.FullReport; // Ensure we always have sensible defaults
        metadata ??= new ReportMetadata();    // Allow callers to omit metadata

        var sb = new StringBuilder();

        // Header
        AppendHeader(sb, analysis, options, metadata);

        // Executive Summary
        AppendSummary(sb, analysis);

        // Crash/Exception Info
        if (options.IncludeCrashInfo)
        {
            AppendCrashInfo(sb, analysis);
        }

        // Call Stacks (all threads with frames) - use new structure if available
        var threads = analysis.Threads?.All ?? new List<ThreadInfo>();
        if (options.IncludeCallStacks && threads.Any(t => t.CallStack.Any()))
        {
            // Avoid empty sections when no frames are present
            AppendAllThreadCallStacks(sb, analysis, options);
        }

        // Source context snippets (best-effort, bounded) derived from the JSON report model.
        if (analysis.SourceContext?.Any() == true)
        {
            AppendSourceContext(sb, analysis.SourceContext);
        }

        // Memory/Heap Stats with Charts
        if (options.IncludeHeapStats || options.IncludeMemoryLeakInfo)
        {
            AppendMemorySection(sb, analysis, options);
        }

        // Thread Information - use new structure if available
        var threadsForInfo = analysis.Threads?.All ?? new List<ThreadInfo>();
        if (options.IncludeThreadInfo && threadsForInfo.Any())
        {
            // Skip thread table if no thread objects exist
            AppendThreadInfo(sb, analysis, options);
        }

        // .NET Specific Info (from new hierarchical structure)
        var hasDotNetInfo = analysis.Environment?.Runtime != null ||
                            analysis.Exception?.Analysis != null ||
                            analysis.Assemblies?.Items?.Any() == true ||
                            analysis.Memory?.Gc != null;
        if (options.IncludeDotNetInfo && hasDotNetInfo)
        {
            AppendDotNetInfoFromHierarchy(sb, analysis, options);

            // Exception Deep Analysis
            if (analysis.Exception?.Analysis != null)
            {
                AppendExceptionAnalysis(sb, analysis.Exception.Analysis);
            }

            // Type Resolution Analysis (for MissingMethodException, TypeLoadException, etc.)
            if (analysis.Exception?.Analysis?.TypeResolution != null)
            {
                AppendTypeResolutionAnalysis(sb, analysis.Exception.Analysis.TypeResolution);
            }

            // NativeAOT / Trimming Analysis
            if (analysis.Environment?.NativeAot != null)
            {
                AppendNativeAotAnalysis(sb, analysis.Environment.NativeAot);
            }

            // Assembly Versions
            if (analysis.Assemblies?.Items?.Any() == true)
            {
                AppendAssemblyVersions(sb, analysis.Assemblies.Items);
            }

            // === Phase 2 ClrMD Enrichment ===

            // GC Summary
            if (analysis.Memory?.Gc != null)
            {
                AppendGcSummary(sb, analysis.Memory.Gc);
            }

            // Top Memory Consumers (deep analysis)
            if (analysis.Memory?.TopConsumers != null)
            {
                AppendTopMemoryConsumers(sb, analysis.Memory.TopConsumers);
            }

            // Async Analysis (deep analysis) - use AsyncAnalysis from Async info
            if (analysis.Async != null)
            {
                AppendAsyncInfoFromHierarchy(sb, analysis.Async);
            }

            // String Analysis (deep analysis)
            if (analysis.Memory?.Strings != null)
            {
                AppendStringAnalysis(sb, analysis.Memory.Strings);
            }
        }

        // Deadlock Info - use new structure if available
        var deadlockInfo = analysis.Threads?.Deadlock;
        if (options.IncludeDeadlockInfo && deadlockInfo != null)
        {
            AppendDeadlockInfo(sb, deadlockInfo);
        }

        // Watch Results
        var watchResults = analysis.Watches;
        if (options.IncludeWatchResults && watchResults != null)
        {
            AppendWatchResults(sb, watchResults);
        }

        // Security Analysis
        if (options.IncludeSecurityAnalysis && analysis.Security != null)
        {
            AppendSecurityInfo(sb, analysis.Security);
        }

        // Modules
        if (options.IncludeModules && analysis.Modules?.Any() == true)
        {
            AppendModules(sb, analysis, options);
        }

        // Process Information (arguments and environment variables) - use new structure if available
        var processInfo = analysis.Environment?.Process;
        if (options.IncludeProcessInfo && processInfo != null)
        {
            AppendProcessInfo(sb, processInfo, options);
        }

        // Recommendations - use new structure if available
        var recommendations = analysis.Summary?.Recommendations ?? new List<string>();
        if (options.IncludeRecommendations && recommendations.Any())
        {
            AppendRecommendations(sb, analysis);
        }

        // Raw Output - use new structure if available
        var rawOutput = analysis.RawCommands ?? new Dictionary<string, string>();
        if (options.IncludeRawOutput && rawOutput.Any())
        {
            AppendRawOutput(sb, analysis);
        }

        // Footer
        AppendFooter(sb, metadata);

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, CrashAnalysisResult analysis, ReportOptions options, ReportMetadata metadata)
    {
        var title = options.Title ?? "Crash Analysis Report";
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("| Property | Value |");
        sb.AppendLine("|----------|-------|");
        sb.AppendLine($"| **Generated** | {metadata.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC |");
        sb.AppendLine($"| **Dump ID** | {metadata.DumpId} |");
        sb.AppendLine($"| **Crash Type** | {analysis.Summary?.CrashType ?? "Unknown"} |");
        sb.AppendLine($"| **Debugger** | {metadata.DebuggerType} |");

        // Add platform info if available (use new structure first, fall back to old)
        var platform = analysis.Environment?.Platform;
        if (platform != null)
        {
            var osInfo = platform.Os;
            if (!string.IsNullOrEmpty(platform.Distribution))
            {
                osInfo += $" ({platform.Distribution})";
            }
            if (platform.IsAlpine == true)
            {
                osInfo += " - musl";
            }
            else if (platform.LibcType == "glibc")
            {
                osInfo += " - glibc";
            }

            sb.AppendLine($"| **Platform** | {osInfo} |");

            if (!string.IsNullOrEmpty(platform.Architecture))
            {
                sb.AppendLine($"| **Architecture** | {platform.Architecture} ({platform.PointerSize ?? 64}-bit) |");
            }

            // Runtime version from new Environment.Runtime or old Platform.RuntimeVersion
            var runtimeVersion = analysis.Environment?.Runtime?.Version ?? platform.RuntimeVersion;
            if (!string.IsNullOrEmpty(runtimeVersion))
            {
                sb.AppendLine($"| **.NET Runtime** | {runtimeVersion} |");
            }
        }

        sb.AppendLine();
    }

    private static void AppendSummary(StringBuilder sb, CrashAnalysisResult analysis)
    {
        sb.AppendLine("## 📋 Executive Summary");
        sb.AppendLine();

        // Use new structure first, fall back to old
        var summaryText = analysis.Summary?.Description;
        if (!string.IsNullOrEmpty(summaryText))
        {
            sb.AppendLine("> " + summaryText.Replace("\n", "\n> "));
        }
        else
        {
            sb.AppendLine("> No summary available.");
        }
        sb.AppendLine();
    }

    private static void AppendCrashInfo(StringBuilder sb, CrashAnalysisResult analysis)
    {
        sb.AppendLine("## 🔴 Crash Information");
        sb.AppendLine();

        // Use new structure
        var exception = analysis.Exception;

        if (exception != null)
        {
            sb.AppendLine("### Exception Details");
            sb.AppendLine();
            sb.AppendLine($"- **Type**: `{exception.Type}`");
            if (!string.IsNullOrEmpty(exception.Message))
            {
                sb.AppendLine($"- **Message**: {exception.Message}");
            }
            if (!string.IsNullOrEmpty(exception.Address))
            {
                sb.AppendLine($"- **Address**: `{exception.Address}`");
            }
            if (!string.IsNullOrEmpty(exception.HResult))
            {
                sb.AppendLine($"- **HResult**: `{exception.HResult}`");
            }
            sb.AppendLine();
        }
        else
        {
            var crashType = analysis.Summary?.CrashType ?? "Unknown";
            sb.AppendLine($"**Crash Type**: {crashType}");
            sb.AppendLine();
        }
    }

    private static void AppendAllThreadCallStacks(StringBuilder sb, CrashAnalysisResult analysis, ReportOptions options)
    {
        sb.AppendLine("## 📚 Thread Call Stacks");
        sb.AppendLine();

        // Get threads with call stacks, faulting thread first
        var allThreads = analysis.Threads?.All ?? new List<ThreadInfo>();
        var threadsWithStacks = allThreads
            .Where(t => t.CallStack.Any())
            .OrderByDescending(t => t.IsFaulting)
            .ThenBy(t => t.ThreadId)
            .ToList();

        foreach (var thread in threadsWithStacks)
        {
            var faultingMarker = thread.IsFaulting ? " ⚠️ **Faulting**" : "";
            var stateInfo = !string.IsNullOrEmpty(thread.State) ? $" - {thread.State}" : "";

            // Add CLR thread info if available
            var clrInfo = "";
            if (!string.IsNullOrEmpty(thread.ThreadType))
            {
                clrInfo = $" ({thread.ThreadType})";
            }
            if (!string.IsNullOrEmpty(thread.CurrentException))
            {
                clrInfo += $" 🔥 {thread.CurrentException}";
            }

            sb.AppendLine($"### Thread #{thread.ThreadId}{faultingMarker}{stateInfo}{clrInfo}");
            sb.AppendLine();

            // Show parameters only for faulting thread
            AppendThreadCallStack(sb, thread.CallStack, options, showParameters: thread.IsFaulting);
        }
    }

    private static void AppendSourceContext(StringBuilder sb, List<SourceContextEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        sb.AppendLine("## 🧩 Source Context (Selected Frames)");
        sb.AppendLine();
        sb.AppendLine("_Best-effort snippets around selected frames with resolved sources._");
        sb.AppendLine();

        foreach (var entry in entries)
        {
            sb.AppendLine($"### Thread #{entry.ThreadId} — Frame #{entry.FrameNumber}");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(entry.Module) || !string.IsNullOrWhiteSpace(entry.Function))
            {
                sb.AppendLine($"- **Frame**: `{entry.Module}!{entry.Function}`");
            }

            if (!string.IsNullOrWhiteSpace(entry.SourceFile) || entry.LineNumber is > 0)
            {
                var sourceFile = entry.SourceFile ?? string.Empty;
                var lineNumber = entry.LineNumber is > 0 ? entry.LineNumber.ToString() : "?";
                sb.AppendLine($"- **Location**: `{sourceFile}:{lineNumber}`");
            }

            if (!string.IsNullOrWhiteSpace(entry.SourceUrl))
            {
                sb.AppendLine($"- **Source URL**: {entry.SourceUrl}");
            }

            if (entry.StartLine is > 0 && entry.EndLine is > 0)
            {
                var focus = entry.LineNumber is > 0 ? entry.LineNumber.ToString() : "?";
                sb.AppendLine($"- **Lines**: {entry.StartLine}-{entry.EndLine} (focus: {focus})");
            }

            sb.AppendLine($"- **Status**: `{entry.Status}`");

            if (!string.IsNullOrWhiteSpace(entry.Error))
            {
                sb.AppendLine($"- **Error**: {entry.Error}");
                sb.AppendLine();
                continue;
            }

            if (entry.Lines == null || entry.Lines.Count == 0)
            {
                sb.AppendLine();
                continue;
            }

            sb.AppendLine();
            var language = GuessMarkdownCodeFenceLanguage(entry.SourceFile);
            sb.AppendLine(string.IsNullOrEmpty(language) ? "```" : $"```{language}");
            foreach (var line in entry.Lines)
            {
                sb.AppendLine(line);
            }
            sb.AppendLine("```");
            sb.AppendLine();
        }
    }

    private static string GuessMarkdownCodeFenceLanguage(string? sourceFile)
    {
        var ext = Path.GetExtension(sourceFile ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "csharp",
            ".fs" => "fsharp",
            ".vb" => "vbnet",
            ".cpp" or ".cc" or ".cxx" => "cpp",
            ".c" => "c",
            ".h" or ".hpp" => "cpp",
            ".rs" => "rust",
            ".go" => "go",
            ".java" => "java",
            ".kt" or ".kts" => "kotlin",
            ".js" => "javascript",
            ".ts" => "typescript",
            ".py" => "python",
            ".rb" => "ruby",
            ".php" => "php",
            ".swift" => "swift",
            ".m" or ".mm" => "objectivec",
            ".sh" => "bash",
            ".ps1" => "powershell",
            ".json" => "json",
            ".yml" or ".yaml" => "yaml",
            ".xml" => "xml",
            _ => string.Empty
        };
    }

    private static void AppendThreadCallStack(StringBuilder sb, List<StackFrame> callStack, ReportOptions options, bool showParameters = false)
    {
        var totalFrames = callStack.Count;
        var frames = callStack;
        if (options.MaxCallStackFrames > 0 && frames.Count > options.MaxCallStackFrames)
        {
            frames = frames.Take(options.MaxCallStackFrames).ToList();
            sb.AppendLine($"*Showing top {options.MaxCallStackFrames} frames of {totalFrames} total*");
            sb.AppendLine();
        }

        // Check if any frames have source links
        var hasSourceLinks = frames.Any(f => !string.IsNullOrEmpty(f.SourceUrl));

        // Check if any frames have parameters (for faulting thread)
        var hasParameters = showParameters && frames.Any(f =>
            (f.Parameters != null && f.Parameters.Count > 0) ||
            (f.Locals != null && f.Locals.Count > 0));

        if (hasSourceLinks || hasParameters)
        {
            // Use detailed format with source links
            sb.AppendLine("| # | Type | Module | Function | Source |");
            sb.AppendLine("|---|------|--------|----------|--------|");

            foreach (var frame in frames)
            {
                var moduleName = frame.Module ?? "???";
                var functionName = frame.Function ?? "???";
                var frameType = frame.IsManaged ? "🟢" : "🔵";  // Green for managed, blue for native

                string sourceColumn;
                if (!string.IsNullOrEmpty(frame.SourceUrl))
                {
                    var fileName = Path.GetFileName(frame.SourceFile ?? "");
                    var lineNum = frame.LineNumber ?? 0;
                    sourceColumn = $"[{fileName}:{lineNum}]({frame.SourceUrl})";
                }
                else if (!string.IsNullOrEmpty(frame.Source))
                {
                    sourceColumn = frame.Source ?? "";
                }
                else
                {
                    sourceColumn = "-";
                }

                sb.AppendLine($"| {frame.FrameNumber:D2} | {frameType} | {moduleName} | `{functionName}` | {sourceColumn} |");
            }
            sb.AppendLine();

            // Show parameters and locals for frames that have them
            if (hasParameters)
            {
                AppendFrameVariables(sb, frames);
            }
        }
        else
        {
            // Use code block format (original)
            sb.AppendLine("```");
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                var moduleName = frame.Module ?? "???";
                var functionName = frame.Function ?? "???";
                var source = !string.IsNullOrEmpty(frame.Source) ? $" [{frame.Source}]" : "";
                var frameType = frame.IsManaged ? "[M]" : "[N]";
                sb.AppendLine($"  {i:D2} {frameType} {moduleName}!{functionName}{source}");
            }
            sb.AppendLine("```");
            sb.AppendLine();
        }
    }

    private static void AppendFrameVariables(StringBuilder sb, List<StackFrame> frames)
    {
        sb.AppendLine("#### 📍 Frame Variables (Faulting Thread)");
        sb.AppendLine();

        foreach (var frame in frames.Where(f =>
            (f.Parameters != null && f.Parameters.Count > 0) ||
            (f.Locals != null && f.Locals.Count > 0)))
        {
            var funcName = frame.Function ?? "???";

            sb.AppendLine($"<details>");
            sb.AppendLine($"<summary><strong>Frame {frame.FrameNumber:D2}</strong>: {funcName}</summary>");
            sb.AppendLine();

            if (frame.Parameters != null && frame.Parameters.Count > 0)
            {
                sb.AppendLine("**Parameters:**");
                sb.AppendLine();
                sb.AppendLine("| Name | Type | Value |");
                sb.AppendLine("|------|------|-------|");

                foreach (var param in frame.Parameters)
                {
                    var name = param.Name ?? "[unnamed]";
                    var type = param.Type ?? "-";
                    var value = FormatVariableValue(param.Value, 60);

                    // Show ByRef resolution if available
                    if (!string.IsNullOrEmpty(param.ByRefAddress))
                    {
                        type += " (ByRef)";
                    }

                    sb.AppendLine($"| `{name}` | `{type}` | {value} |");
                }
                sb.AppendLine();
            }

            if (frame.Locals != null && frame.Locals.Count > 0)
            {
                sb.AppendLine("**Local Variables:**");
                sb.AppendLine();
                sb.AppendLine("| Name | Type | Value |");
                sb.AppendLine("|------|------|-------|");

                foreach (var local in frame.Locals)
                {
                    var name = local.Name ?? "[unnamed]";
                    var type = local.Type ?? "-";
                    var value = FormatVariableValue(local.Value, 60);

                    sb.AppendLine($"| `{name}` | `{type}` | {value} |");
                }
                sb.AppendLine();
            }

            sb.AppendLine("</details>");
            sb.AppendLine();
        }
    }

    private static string FormatVariableValue(object? value, int maxLength)
    {
        if (value == null)
        {
            return "*null*";
        }

        // If value is an expanded object (from showobj), format it as JSON
        if (value is not string)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = false,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
                // For markdown table, truncate if too long
                if (json.Length > maxLength)
                {
                    json = json[..(maxLength - 3)] + "...";
                }
                return $"`{json}`";
            }
            catch
            {
                return value.ToString() ?? "";
            }
        }

        var stringValue = value as string ?? value.ToString() ?? "";

        if (string.IsNullOrEmpty(stringValue) || stringValue == "[NO DATA]")
        {
            return "*no data*";
        }

        var truncated = stringValue;

        // Format based on value type
        if (truncated.StartsWith("\"") && truncated.EndsWith("\""))
        {
            // String value
            return $"`{truncated}`";
        }
        else if (truncated.StartsWith("0x"))
        {
            // Pointer/address
            return $"`{truncated}`";
        }
        else if (truncated == "true" || truncated == "false")
        {
            // Boolean
            return $"**{truncated}**";
        }
        else if (int.TryParse(truncated, out _) || long.TryParse(truncated, out _))
        {
            // Numeric
            return $"`{truncated}`";
        }

        return truncated;
    }

    private static void AppendMemorySection(StringBuilder sb, CrashAnalysisResult analysis, ReportOptions options)
    {
        sb.AppendLine("## 💾 Memory Analysis");
        sb.AppendLine();

        // Heap Stats from .NET info if available - use new structure if available
        var heapStats = analysis.Memory?.HeapStats;
        if (heapStats != null && heapStats.Any())
        {
            sb.AppendLine("### Heap Statistics");
            sb.AppendLine();

            if (options.IncludeCharts)
            {
                // Group by truncated key to avoid duplicates (different types can truncate to same string)
                var heapData = heapStats.Take(10)
                    .GroupBy(kv => kv.Key)
                    .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));

                if (heapData.Any())
                {
                    sb.AppendLine("```");
                    sb.AppendLine(AsciiCharts.HorizontalBarChart(heapData, "Top Types by Size", 25, true, true, AsciiCharts.FormatBytes));
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }
        }

        // Memory Consumption Analysis
        var leakAnalysis = analysis.Memory?.LeakAnalysis;
        if (options.IncludeMemoryLeakInfo && leakAnalysis != null)
        {
            var leak = new MemoryLeakInfo
            {
                Detected = leakAnalysis.Detected,
                Severity = leakAnalysis.Severity ?? "Unknown",
                TotalHeapBytes = leakAnalysis.TotalHeapBytes ?? 0,
                TopConsumers = leakAnalysis.TopConsumers ?? new List<MemoryConsumer>(),
                PotentialIssueIndicators = leakAnalysis.PotentialIssueIndicators ?? new List<string>()
            };

            if (leak.Detected)
            {
                var displaySeverity = leak.Severity ?? "Elevated";
                sb.AppendLine($"### ⚠️ Memory Consumption: {displaySeverity}");
                sb.AppendLine();
                var heapSize = leak.TotalHeapBytes > 0
                    ? leak.TotalHeapBytes
                    : leak.TopConsumers?.Sum(c => c.TotalSize) ?? 0;
                sb.AppendLine($"**Total Heap Size**: {AsciiCharts.FormatBytes(heapSize)}");
                sb.AppendLine();

                // Show potential issue indicators if any
                if (leak.PotentialIssueIndicators?.Any() == true)
                {
                    sb.AppendLine("**Potential Issues**:");
                    foreach (var indicator in leak.PotentialIssueIndicators.Take(5))
                    {
                        sb.AppendLine($"- {indicator}");
                    }
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine("*Note: High memory consumption detected. Use memory profiling with multiple snapshots to identify actual leaks.*");
                    sb.AppendLine();
                }
            }

            if (leak.TopConsumers?.Any() == true)
            {
                sb.AppendLine("### Top Memory Consumers");
                sb.AppendLine();

                if (options.IncludeCharts)
                {
                    // Group by truncated key to avoid duplicates (different types can truncate to same string)
                    var consumerData = leak.TopConsumers.Take(10)
                        .GroupBy(c => c.TypeName)
                        .ToDictionary(g => g.Key, g => g.Sum(c => c.TotalSize));

                    sb.AppendLine("```");
                    sb.AppendLine(AsciiCharts.HorizontalBarChart(consumerData, null, 25, true, true, AsciiCharts.FormatBytes));
                    sb.AppendLine("```");
                    sb.AppendLine();
                }

                // Table format
                var headers = new[] { "Type", "Count", "Total Size" };
                var rows = leak.TopConsumers.Take(10)
                    .Select(c => new[] { c.TypeName, c.Count.ToString("N0"), AsciiCharts.FormatBytes(c.TotalSize) })
                    .ToList();

                sb.AppendLine(AsciiCharts.Table(headers, rows));
                sb.AppendLine();
            }
        }
    }

    private static void AppendThreadInfo(StringBuilder sb, CrashAnalysisResult analysis, ReportOptions options)
    {
        sb.AppendLine("## 🧵 Thread Information");
        sb.AppendLine();

        // Use new structure if available
        var allThreads = analysis.Threads?.All ?? new List<ThreadInfo>();
        var threadSummary = analysis.Threads?.Summary;

        var threadCount = threadSummary?.Total ?? allThreads.Count;
        sb.AppendLine($"**Total Threads**: {threadCount}");
        sb.AppendLine();

        // Thread state distribution
        if (options.IncludeCharts)
        {
            var stateGroups = allThreads
                .GroupBy(t => t.State ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.Count());

            if (stateGroups.Any())
            {
                sb.AppendLine("### Thread State Distribution");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(AsciiCharts.ThreadStateChart(stateGroups));
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        // Thread table
        var threads = allThreads.AsEnumerable();
        if (options.MaxThreadsToShow > 0 && allThreads.Count > options.MaxThreadsToShow)
        {
            threads = threads.Take(options.MaxThreadsToShow);
            sb.AppendLine($"*Showing {options.MaxThreadsToShow} of {allThreads.Count} threads*");
            sb.AppendLine();
        }

        // Check if we have CLR thread info
        var hasCLRInfo = allThreads.Any(t => t.ManagedThreadId.HasValue);

        if (hasCLRInfo)
        {
            var headers = new[] { "Thread ID", "Type", "GC Mode", "Locks", "State", "Exception" };
            var rows = threads.Select(t => new[]
            {
                FormatThreadId(t),
                t.ThreadType ?? (t.IsDead ? "Dead" : "-"),
                t.GcMode ?? "-",
                t.LockCount?.ToString() ?? "-",
                t.State ?? "Unknown",
                t.CurrentException ?? "-"
            }).ToList();
            sb.AppendLine(AsciiCharts.Table(headers, rows));
        }
        else
        {
            var headers = new[] { "Thread ID", "State", "Top Function" };
            var rows = threads.Select(t => new[]
            {
            t.ThreadId,
            t.State ?? "Unknown",
            t.TopFunction ?? "-"
        }).ToList();
            sb.AppendLine(AsciiCharts.Table(headers, rows));
        }
        sb.AppendLine();
    }

    private static void AppendDotNetInfoFromHierarchy(StringBuilder sb, CrashAnalysisResult analysis, ReportOptions options)
    {
        sb.AppendLine("## 🟣 .NET Runtime Information");
        sb.AppendLine();

        // CLR Version from Runtime info
        var runtime = analysis.Environment?.Runtime;
        if (!string.IsNullOrEmpty(runtime?.ClrVersion))
        {
            sb.AppendLine($"**CLR Version**: {runtime.ClrVersion}");
        }

        // Thread statistics from Threads.Summary
        var threadSummary = analysis.Threads?.Summary;
        if (threadSummary?.Total > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Managed Thread Statistics");
            sb.AppendLine();
            sb.AppendLine($"| Metric | Count |");
            sb.AppendLine($"|--------|-------|");
            sb.AppendLine($"| Total Threads | {threadSummary.Total} |");
            if (threadSummary.Background > 0)
                sb.AppendLine($"| Background | {threadSummary.Background} |");
            if (threadSummary.Unstarted > 0)
                sb.AppendLine($"| Unstarted | {threadSummary.Unstarted} |");
            if (threadSummary.Pending > 0)
                sb.AppendLine($"| Pending | {threadSummary.Pending} |");
            if (threadSummary.Dead > 0)
                sb.AppendLine($"| 💀 Dead | {threadSummary.Dead} |");
            if (runtime?.IsHosted == true)
                sb.AppendLine($"| Hosted Runtime | Yes |");
        }

        // Exception info from Exception
        var exception = analysis.Exception;
        if (!string.IsNullOrEmpty(exception?.Type))
        {
            sb.AppendLine();
            sb.AppendLine("### 🔥 Managed Exception");
            sb.AppendLine();
            sb.AppendLine($"**Type**: `{exception.Type}`");

            if (!string.IsNullOrEmpty(exception.Message))
            {
                sb.AppendLine();
                sb.AppendLine($"**Message**: {exception.Message}");
            }

            if (!string.IsNullOrEmpty(exception.HResult))
            {
                sb.AppendLine();
                sb.AppendLine($"**HResult**: 0x{exception.HResult}");
            }

            if (exception.HasInnerException == true)
            {
                sb.AppendLine();
                sb.AppendLine($"**Inner Exceptions**: {(exception.NestedExceptionCount > 0 ? exception.NestedExceptionCount : 1)} nested exception(s)");
            }

            if (exception.StackTrace != null && exception.StackTrace.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("**Stack Trace**:");
                sb.AppendLine();
                sb.AppendLine("```");
                foreach (var frame in exception.StackTrace)
                {
                    sb.AppendLine($"  {frame.Function}");
                }
                sb.AppendLine("```");
            }
        }

        // Finalizer queue from ThreadSummary
        if (threadSummary?.FinalizerQueueLength > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Finalization Queue**: {threadSummary.FinalizerQueueLength:N0} objects");
        }

        // Async deadlock from Async
        if (analysis.Async?.HasDeadlock == true)
        {
            sb.AppendLine();
            sb.AppendLine("### ⚠️ Async Deadlock Detected");
            sb.AppendLine();
            sb.AppendLine("The analysis detected a potential async/await deadlock pattern.");
        }

        // Thread Pool Information from Threads.ThreadPool
        var tp = analysis.Threads?.ThreadPool;
        if (tp != null)
        {
            sb.AppendLine();
            sb.AppendLine("### 🔄 Thread Pool Status");
            sb.AppendLine();

            sb.AppendLine("| Metric | Value |");
            sb.AppendLine("|--------|-------|");

            if (tp.CpuUtilization.HasValue)
                sb.AppendLine($"| CPU Utilization | {tp.CpuUtilization}% |");
            if (tp.WorkersTotal.HasValue)
                sb.AppendLine($"| Workers Total | {tp.WorkersTotal} |");
            if (tp.WorkersRunning.HasValue)
                sb.AppendLine($"| Workers Running | {tp.WorkersRunning} |");
            if (tp.WorkersIdle.HasValue)
                sb.AppendLine($"| Workers Idle | {tp.WorkersIdle} |");
            if (tp.WorkerMinLimit.HasValue)
                sb.AppendLine($"| Min Threads | {tp.WorkerMinLimit} |");
            if (tp.WorkerMaxLimit.HasValue)
                sb.AppendLine($"| Max Threads | {tp.WorkerMaxLimit:N0} |");
            if (tp.IsPortableThreadPool == true)
                sb.AppendLine($"| Thread Pool Type | Portable |");

            // Add warnings
            if (tp.WorkersRunning == tp.WorkersTotal && tp.WorkersTotal > 0)
            {
                sb.AppendLine();
                sb.AppendLine("> ⚠️ **Thread Pool Saturation**: All worker threads are busy. Consider increasing thread pool limits or reducing blocking operations.");
            }

            if (tp.CpuUtilization > 90)
            {
                sb.AppendLine();
                sb.AppendLine($"> ⚠️ **High CPU Utilization** ({tp.CpuUtilization}%): Consider profiling for CPU-bound operations.");
            }
        }

        // Timer Information from Async.Timers
        var timers = analysis.Async?.Timers;
        if (timers != null && timers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### ⏱️ Active Timers");
            sb.AppendLine();
            sb.AppendLine($"**Total Active Timers**: {timers.Count}");
            sb.AppendLine();

            // Group by state type
            var timerGroups = timers
                .GroupBy(t => t.StateType ?? "Unknown")
                .OrderByDescending(g => g.Count())
                .Take(10)
                .ToList();

            sb.AppendLine("| State Type | Count | Due Time | Period |");
            sb.AppendLine("|------------|-------|----------|--------|");

            foreach (var group in timerGroups)
            {
                var sample = group.First();
                var count = group.Count();
                var dueTime = sample.DueTimeMs.HasValue ? $"{sample.DueTimeMs:N0}ms" : "-";
                var period = sample.PeriodMs.HasValue ? $"{sample.PeriodMs:N0}ms" : "one-shot";
                var stateType = group.Key;
                sb.AppendLine($"| `{stateType}` | {count} | {dueTime} | {period} |");
            }

            // Warnings
            if (timers.Count > 50)
            {
                sb.AppendLine();
                sb.AppendLine($"> ⚠️ **High Timer Count** ({timers.Count}): This may indicate timer leaks. Consider using a single timer with multiplexing.");
            }

            var shortTimers = timers.Where(t => t.PeriodMs.HasValue && t.PeriodMs < 100).ToList();
            if (shortTimers.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"> ⚠️ **Short Timer Intervals**: {shortTimers.Count} timer(s) have periods under 100ms. Consider consolidating or using longer intervals.");
            }
        }

        sb.AppendLine();
    }

    private static void AppendAsyncInfoFromHierarchy(StringBuilder sb, AsyncInfo asyncInfo)
    {
        sb.AppendLine("## ⚡ Async Analysis");
        sb.AppendLine();

        if (asyncInfo.Summary != null)
        {
            sb.AppendLine("### Task Summary");
            sb.AppendLine();
            sb.AppendLine("| Metric | Count |");
            sb.AppendLine("|--------|-------|");
            sb.AppendLine($"| Total Tasks | {asyncInfo.Summary.TotalTasks} |");
            if (asyncInfo.Summary.PendingTasks > 0)
                sb.AppendLine($"| Pending | {asyncInfo.Summary.PendingTasks} |");
            if (asyncInfo.Summary.CompletedTasks > 0)
                sb.AppendLine($"| Completed | {asyncInfo.Summary.CompletedTasks} |");
            if (asyncInfo.Summary.FaultedTasks > 0)
                sb.AppendLine($"| ⚠️ Faulted | {asyncInfo.Summary.FaultedTasks} |");
            if (asyncInfo.Summary.CanceledTasks > 0)
                sb.AppendLine($"| Canceled | {asyncInfo.Summary.CanceledTasks} |");
            sb.AppendLine();
        }

        if (asyncInfo.StateMachines?.Count > 0)
        {
            sb.AppendLine("### Pending State Machines");
            sb.AppendLine();
            sb.AppendLine("| State Machine | State |");
            sb.AppendLine("|---------------|-------|");
            foreach (var sm in asyncInfo.StateMachines.Take(10))
            {
                sb.AppendLine($"| `{sm.StateMachineType}` | {sm.CurrentState} |");
            }
            if (asyncInfo.StateMachines.Count > 10)
            {
                sb.AppendLine($"| ... | +{asyncInfo.StateMachines.Count - 10} more |");
            }
            sb.AppendLine();
        }

        if (asyncInfo.FaultedTasks?.Count > 0)
        {
            sb.AppendLine("### Faulted Tasks");
            sb.AppendLine();
            foreach (var task in asyncInfo.FaultedTasks.Take(5))
            {
                sb.AppendLine($"- **{task.TaskType}**: {task.ExceptionType} - {task.ExceptionMessage}");
            }
            sb.AppendLine();
        }

        if (asyncInfo.AnalysisTimeMs.HasValue)
        {
            sb.AppendLine($"> Analysis completed in {asyncInfo.AnalysisTimeMs}ms");
            if (asyncInfo.WasAborted == true)
            {
                sb.AppendLine("> ⚠️ Analysis was aborted due to timeout");
            }
        }

        sb.AppendLine();
    }

    private static void AppendExceptionAnalysis(StringBuilder sb, ExceptionAnalysis exceptionAnalysis)
    {
        sb.AppendLine("## 🔎 Exception Deep Analysis");
        sb.AppendLine();

        // Basic exception info
        sb.AppendLine("### Exception Details");
        sb.AppendLine();
        sb.AppendLine("| Property | Value |");
        sb.AppendLine("|----------|-------|");

        if (!string.IsNullOrEmpty(exceptionAnalysis.FullTypeName))
            sb.AppendLine($"| **Type** | `{exceptionAnalysis.FullTypeName}` |");
        if (!string.IsNullOrEmpty(exceptionAnalysis.Message))
            sb.AppendLine($"| **Message** | {exceptionAnalysis.Message} |");
        if (!string.IsNullOrEmpty(exceptionAnalysis.HResult))
            sb.AppendLine($"| **HResult** | `{exceptionAnalysis.HResult}` |");
        if (!string.IsNullOrEmpty(exceptionAnalysis.ExceptionAddress))
            sb.AppendLine($"| **Address** | `0x{exceptionAnalysis.ExceptionAddress}` |");
        if (!string.IsNullOrEmpty(exceptionAnalysis.Source))
            sb.AppendLine($"| **Source** | {exceptionAnalysis.Source} |");
        sb.AppendLine();

        // Target Site
        if (exceptionAnalysis.TargetSite != null)
        {
            sb.AppendLine("### Target Site (Where Exception Was Thrown)");
            sb.AppendLine();
            sb.AppendLine($"- **Method**: `{exceptionAnalysis.TargetSite.Name}`");
            if (!string.IsNullOrEmpty(exceptionAnalysis.TargetSite.DeclaringType))
                sb.AppendLine($"- **Class**: `{exceptionAnalysis.TargetSite.DeclaringType}`");
            if (!string.IsNullOrEmpty(exceptionAnalysis.TargetSite.Signature))
                sb.AppendLine($"- **Signature**: `{exceptionAnalysis.TargetSite.Signature}`");
            sb.AppendLine();
        }

        // Exception Chain
        if (exceptionAnalysis.ExceptionChain?.Any() == true)
        {
            sb.AppendLine("### Exception Chain");
            sb.AppendLine();

            foreach (var entry in exceptionAnalysis.ExceptionChain)
            {
                var depthMarker = entry.Depth == 0 ? "🔴" : new string('↳', entry.Depth);
                sb.AppendLine($"{depthMarker} **{entry.Type}**");
                if (!string.IsNullOrEmpty(entry.Message))
                    sb.AppendLine($"   - Message: {entry.Message}");
                if (!string.IsNullOrEmpty(entry.HResult))
                    sb.AppendLine($"   - HResult: `{entry.HResult}`");
                sb.AppendLine();
            }
        }

        // Custom Properties (type-specific)
        if (exceptionAnalysis.CustomProperties?.Any() == true)
        {
            sb.AppendLine("### Custom Properties");
            sb.AppendLine();
            sb.AppendLine("| Property | Value |");
            sb.AppendLine("|----------|-------|");

            foreach (var (key, value) in exceptionAnalysis.CustomProperties)
            {
                var displayValue = value?.ToString() ?? "*null*";
                if (displayValue.Length > 100)
                    displayValue = displayValue[..100] + "...";
                sb.AppendLine($"| `{key}` | {displayValue} |");
            }
            sb.AppendLine();
        }
    }

    private static void AppendTypeResolutionAnalysis(StringBuilder sb, TypeResolutionAnalysis typeResolution)
    {
        sb.AppendLine("## 🔍 Type/Method Resolution Analysis");
        sb.AppendLine();

        // Failed Type Info
        if (!string.IsNullOrEmpty(typeResolution.FailedType))
        {
            sb.AppendLine($"**Failed Type**: `{typeResolution.FailedType}`");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(typeResolution.MethodTable))
                sb.AppendLine($"- MethodTable: `0x{typeResolution.MethodTable}`");
            if (!string.IsNullOrEmpty(typeResolution.EEClass))
                sb.AppendLine($"- EEClass: `0x{typeResolution.EEClass}`");
            sb.AppendLine();
        }

        // Expected Member
        if (typeResolution.ExpectedMember != null)
        {
            sb.AppendLine("### Expected Member (Not Found)");
            sb.AppendLine();
            sb.AppendLine($"- **Name**: `{typeResolution.ExpectedMember.Name}`");
            if (!string.IsNullOrEmpty(typeResolution.ExpectedMember.Signature))
                sb.AppendLine($"- **Signature**: `{typeResolution.ExpectedMember.Signature}`");
            if (!string.IsNullOrEmpty(typeResolution.ExpectedMember.MemberType))
                sb.AppendLine($"- **Member Type**: {typeResolution.ExpectedMember.MemberType}");
            sb.AppendLine();
        }

        // Similar Methods (potential matches)
        if (typeResolution.SimilarMethods?.Any() == true)
        {
            sb.AppendLine("### Similar Methods (Potential Matches)");
            sb.AppendLine();
            sb.AppendLine("These methods have similar names - check for signature mismatches:");
            sb.AppendLine();

            foreach (var method in typeResolution.SimilarMethods)
            {
                sb.AppendLine($"- `{method.Signature}` ({method.JitStatus})");
            }
            sb.AppendLine();
        }

        // Diagnosis
        if (!string.IsNullOrEmpty(typeResolution.Diagnosis))
        {
            sb.AppendLine("### Diagnosis");
            sb.AppendLine();
            sb.AppendLine($"> {typeResolution.Diagnosis}");
            sb.AppendLine();
        }

        // Actual Methods (collapsible for large lists)
        if (typeResolution.ActualMethods?.Any() == true)
        {
            sb.AppendLine("<details>");
            sb.AppendLine($"<summary>📋 All Methods on Type ({typeResolution.ActualMethods.Count} total)</summary>");
            sb.AppendLine();
            sb.AppendLine("| Method | JIT Status |");
            sb.AppendLine("|--------|------------|");

            foreach (var method in typeResolution.ActualMethods)
            {
                var sig = method.Signature ?? method.Name ?? "???";
                sb.AppendLine($"| `{sig}` | {method.JitStatus} |");
            }

            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }
    }

    private static void AppendNativeAotAnalysis(StringBuilder sb, NativeAotAnalysis nativeAot)
    {
        sb.AppendLine("## 🚀 NativeAOT / Trimming Analysis");
        sb.AppendLine();

        // Status badges
        var aotBadge = nativeAot.IsNativeAot ? "🔴 **NativeAOT Detected**" : "🟢 **Not NativeAOT**";
        var jitBadge = nativeAot.HasJitCompiler ? "✅ JIT Present" : "❌ No JIT";

        sb.AppendLine($"| Property | Status |");
        sb.AppendLine($"|----------|--------|");
        sb.AppendLine($"| NativeAOT | {aotBadge} |");
        sb.AppendLine($"| JIT Compiler | {jitBadge} |");
        sb.AppendLine();

        // Indicators
        if (nativeAot.Indicators?.Any() == true)
        {
            sb.AppendLine("### Detection Indicators");
            sb.AppendLine();

            foreach (var indicator in nativeAot.Indicators)
            {
                sb.AppendLine($"- **Pattern**: `{indicator.Pattern}`");
                if (!string.IsNullOrEmpty(indicator.MatchedValue))
                    sb.AppendLine($"  - Matched: `{indicator.MatchedValue}`");
                if (indicator.Frame != null)
                    sb.AppendLine($"  - Frame: `{indicator.Frame.Function}`");
                sb.AppendLine();
            }
        }

        // Trimming Analysis
        if (nativeAot.TrimmingAnalysis != null)
        {
            var trimming = nativeAot.TrimmingAnalysis;

            var confidenceEmoji = trimming.Confidence switch
            {
                "high" => "🔴",
                "medium" => "🟠",
                _ => "🟡"
            };

            sb.AppendLine("### Trimming Analysis");
            sb.AppendLine();

            if (trimming.PotentialTrimmingIssue)
            {
                sb.AppendLine($"> {confidenceEmoji} **Potential Trimming Issue Detected** (Confidence: {trimming.Confidence})");
            }
            else
            {
                sb.AppendLine($"> {confidenceEmoji} Possible version mismatch or configuration issue (Confidence: {trimming.Confidence})");
            }
            sb.AppendLine();

            if (!string.IsNullOrEmpty(trimming.ExceptionType))
                sb.AppendLine($"- **Exception Type**: `{trimming.ExceptionType}`");
            if (!string.IsNullOrEmpty(trimming.MissingMember))
                sb.AppendLine($"- **Missing Member**: `{trimming.MissingMember}`");

            if (trimming.CallingFrame != null)
            {
                sb.AppendLine($"- **Called From**: `{trimming.CallingFrame.Function}`");
                if (!string.IsNullOrEmpty(trimming.CallingFrame.SourceUrl))
                    sb.AppendLine($"  - [View Source]({trimming.CallingFrame.SourceUrl})");
            }
            sb.AppendLine();

            if (!string.IsNullOrEmpty(trimming.Recommendation))
            {
                sb.AppendLine("### Recommendations");
                sb.AppendLine();
                sb.AppendLine(trimming.Recommendation);
                sb.AppendLine();
            }
        }

        // Reflection Usage
        if (nativeAot.ReflectionUsage?.Any() == true)
        {
            sb.AppendLine("### Reflection Usage Patterns");
            sb.AppendLine();
            sb.AppendLine("The following reflection patterns were detected:");
            sb.AppendLine();
            sb.AppendLine("| Location | Pattern | Risk |");
            sb.AppendLine("|----------|---------|------|");

            foreach (var usage in nativeAot.ReflectionUsage)
            {
                var location = usage.Location ?? "-";
                var pattern = usage.Pattern ?? "-";
                var risk = usage.Risk ?? "-";
                sb.AppendLine($"| `{location}` | {pattern} | {risk} |");
            }
            sb.AppendLine();
        }
    }

    private static void AppendAssemblyVersions(StringBuilder sb, List<AssemblyVersionInfo> assemblies)
    {
        sb.AppendLine("## 📦 Assembly Versions");
        sb.AppendLine();

        if (!assemblies.Any())
        {
            sb.AppendLine("*No assembly information available.*");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("<details>");
        sb.AppendLine($"<summary>View all {assemblies.Count} assemblies</summary>");
        sb.AppendLine();
        sb.AppendLine("| Assembly | Version | Info Version | Config | Company | Repository |");
        sb.AppendLine("|----------|---------|--------------|--------|---------|------------|");

        foreach (var asm in assemblies)
        {
            var name = asm.Name;
            if (name.Length > 50)
                name = name[..50] + "...";
            // Escape pipe characters to prevent breaking the markdown table
            name = name.Replace("|", "\\|");
            var asmVersion = (asm.AssemblyVersion ?? "-").Replace("|", "\\|");
            var dynamicBadge = asm.IsDynamic == true ? " 🔄" : "";

            // Build info version with optional commit link
            var infoVersion = asm.InformationalVersion ?? "-";
            if (infoVersion.Length > 30)
                infoVersion = infoVersion[..30] + "...";
            infoVersion = infoVersion.Replace("|", "\\|");
            if (!string.IsNullOrEmpty(asm.CommitHash) && !string.IsNullOrEmpty(asm.RepositoryUrl))
            {
                var shortHash = asm.CommitHash.Length > 7 ? asm.CommitHash[..7] : asm.CommitHash;
                // Escape parentheses in URLs for Markdown compatibility
                var escapedUrl = asm.RepositoryUrl.Replace("(", "%28").Replace(")", "%29");
                infoVersion += $" [`{shortHash}`]({escapedUrl}/commit/{asm.CommitHash})";
            }

            // Repository link
            var repoLink = !string.IsNullOrEmpty(asm.RepositoryUrl)
                ? $"[🔗]({asm.RepositoryUrl.Replace("(", "%28").Replace(")", "%29")})" : "-";

            // Configuration badge
            var config = (asm.Configuration ?? "-").Replace("|", "\\|");

            // Company (truncate if too long)
            var company = asm.Company ?? "-";
            if (company.Length > 25)
                company = company[..25] + "...";
            company = company.Replace("|", "\\|");

            sb.AppendLine($"| `{name}`{dynamicBadge} | {asmVersion} | {infoVersion} | {config} | {company} | {repoLink} |");
        }

        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();
    }

    private static void AppendDeadlockInfo(StringBuilder sb, DeadlockInfo deadlockInfo)
    {
        if (!deadlockInfo.Detected)
        {
            return;
        }

        sb.AppendLine("## 🔒 Deadlock Detection");
        sb.AppendLine();
        sb.AppendLine("### ⚠️ DEADLOCK DETECTED");
        sb.AppendLine();

        if (deadlockInfo.InvolvedThreads?.Any() == true)
        {
            sb.AppendLine("**Involved Threads**:");
            foreach (var thread in deadlockInfo.InvolvedThreads)
            {
                sb.AppendLine($"- Thread {thread}");
            }
            sb.AppendLine();
        }

        if (deadlockInfo.Locks?.Any() == true)
        {
            sb.AppendLine("**Locks**:");
            foreach (var lockInfo in deadlockInfo.Locks)
            {
                sb.AppendLine($"- `{lockInfo}`");
            }
            sb.AppendLine();
        }
    }

    private static void AppendWatchResults(StringBuilder sb, WatchEvaluationReport watchResults)
    {
        sb.AppendLine("## 📌 Watch Expression Results");
        sb.AppendLine();
        sb.AppendLine($"**Total Watches**: {watchResults.TotalWatches}");
        sb.AppendLine($"**Successful**: {watchResults.SuccessfulEvaluations} | **Failed**: {watchResults.FailedEvaluations}");
        sb.AppendLine();

        if (watchResults.Watches.Any())
        {
            foreach (var watch in watchResults.Watches)
            {
                var status = watch.Success ? "✓" : "✗";
                sb.AppendLine($"### {status} `{watch.Expression}`");

                if (!string.IsNullOrEmpty(watch.Description))
                {
                    sb.AppendLine($"*{watch.Description}*");
                }
                sb.AppendLine();

                if (watch.Success && !string.IsNullOrEmpty(watch.Value))
                {
                    sb.AppendLine("```");
                    sb.AppendLine(watch.Value.Length > 500 ? watch.Value.Substring(0, 500) + "..." : watch.Value);
                    sb.AppendLine("```");
                }
                else if (!string.IsNullOrEmpty(watch.Error))
                {
                    sb.AppendLine($"**Error**: {watch.Error}");
                }
                sb.AppendLine();
            }
        }

        if (watchResults.Insights.Any())
        {
            sb.AppendLine("### Insights");
            sb.AppendLine();
            foreach (var insight in watchResults.Insights)
            {
                sb.AppendLine($"- {insight}");
            }
            sb.AppendLine();
        }
    }

    private static void AppendModules(StringBuilder sb, CrashAnalysisResult analysis, ReportOptions options)
    {
        if (analysis.Modules == null) return;

        sb.AppendLine("## 📦 Loaded Modules");
        sb.AppendLine();

        var modules = analysis.Modules.AsEnumerable();
        if (options.MaxModulesToShow > 0 && analysis.Modules.Count > options.MaxModulesToShow)
        {
            modules = modules.Take(options.MaxModulesToShow);
            sb.AppendLine($"*Showing {options.MaxModulesToShow} of {analysis.Modules.Count} modules*");
            sb.AppendLine();
        }

        var headers = new[] { "Module", "Base Address", "Symbols" };
        var rows = modules.Select(m => new[]
        {
            m.Name,
            m.BaseAddress ?? "-",
            m.HasSymbols ? "✓" : "✗"
        }).ToList();

        sb.AppendLine(AsciiCharts.Table(headers, rows));
        sb.AppendLine();
    }

    private static void AppendSecurityInfo(StringBuilder sb, SecurityInfo security)
    {
        sb.AppendLine("## 🔒 Security Analysis");
        sb.AppendLine();

        // Risk Level
        sb.AppendLine($"**Overall Risk**: {security.OverallRisk ?? "Unknown"}");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(security.Summary))
        {
            sb.AppendLine(security.Summary);
            sb.AppendLine();
        }

        // Findings Table
        if (security.Findings?.Any() == true)
        {
            sb.AppendLine("### Detected Vulnerabilities");
            sb.AppendLine();
            sb.AppendLine("| Severity | Type | Description |");
            sb.AppendLine("|----------|------|-------------|");

            foreach (var finding in security.Findings.OrderByDescending(f => f.Severity))
            {
                var description = finding.Description?.Replace("|", "\\|") ?? "";
                sb.AppendLine($"| {finding.Severity} | {finding.Type} | {description} |");
            }
            sb.AppendLine();
        }

        // Recommendations
        if (security.Recommendations?.Any() == true)
        {
            sb.AppendLine("### Recommendations");
            sb.AppendLine();
            foreach (var rec in security.Recommendations)
            {
                sb.AppendLine($"- {rec}");
            }
            sb.AppendLine();
        }
    }

    private static void AppendSecurityAnalysis(StringBuilder sb, SecurityAnalysisResult security)
    {
        sb.AppendLine("## 🔒 Security Analysis");
        sb.AppendLine();

        // Risk Level Badge
        var riskEmoji = security.OverallRisk switch
        {
            SecurityRisk.Critical => "🔴 CRITICAL",
            SecurityRisk.High => "🟠 HIGH",
            SecurityRisk.Medium => "🟡 MEDIUM",
            SecurityRisk.Low => "🟢 LOW",
            _ => "⚪ NONE"
        };
        sb.AppendLine($"**Overall Risk**: {riskEmoji}");
        sb.AppendLine();
        sb.AppendLine(security.Summary);
        sb.AppendLine();

        // Vulnerabilities Table
        if (security.Vulnerabilities.Any())
        {
            sb.AppendLine("### Detected Vulnerabilities");
            sb.AppendLine();
            sb.AppendLine("| Severity | Type | Description | CWE |");
            sb.AppendLine("|----------|------|-------------|-----|");

            foreach (var vuln in security.Vulnerabilities.OrderByDescending(v => v.Severity))
            {
                var severityBadge = vuln.Severity switch
                {
                    VulnerabilitySeverity.Critical => "🔴 Critical",
                    VulnerabilitySeverity.High => "🟠 High",
                    VulnerabilitySeverity.Medium => "🟡 Medium",
                    VulnerabilitySeverity.Low => "🟢 Low",
                    _ => "⚪ Info"
                };
                var cweLinks = vuln.CweIds.Any()
                    ? string.Join(", ", vuln.CweIds.Select(c => $"[{c}](https://cwe.mitre.org/data/definitions/{c.Replace("CWE-", "")}.html)"))
                    : "-";
                sb.AppendLine($"| {severityBadge} | {vuln.Type} | {vuln.Description} | {cweLinks} |");
            }
            sb.AppendLine();

            // Critical/High vulnerability details
            var criticalVulns = security.Vulnerabilities.Where(v => v.Severity >= VulnerabilitySeverity.High).ToList();
            if (criticalVulns.Any())
            {
                sb.AppendLine("### Critical/High Severity Details");
                sb.AppendLine();

                foreach (var vuln in criticalVulns)
                {
                    sb.AppendLine($"#### {vuln.Type}");
                    sb.AppendLine();
                    sb.AppendLine(vuln.Description);

                    if (!string.IsNullOrEmpty(vuln.Address))
                    {
                        sb.AppendLine($"- **Address**: `{vuln.Address}`");
                    }
                    if (!string.IsNullOrEmpty(vuln.Module))
                    {
                        sb.AppendLine($"- **Module**: {vuln.Module}");
                    }
                    if (vuln.Indicators.Any())
                    {
                        sb.AppendLine($"- **Indicators**: {string.Join(", ", vuln.Indicators)}");
                    }
                    if (vuln.Remediation.Any())
                    {
                        sb.AppendLine();
                        sb.AppendLine("**Remediation:**");
                        foreach (var step in vuln.Remediation)
                        {
                            sb.AppendLine($"- {step}");
                        }
                    }
                    sb.AppendLine();
                }
            }
        }

        // Memory Protections
        if (security.MemoryProtections != null)
        {
            sb.AppendLine("### Memory Protection Status");
            sb.AppendLine();
            sb.AppendLine($"| Protection | Status |");
            sb.AppendLine("|------------|--------|");
            sb.AppendLine($"| ASLR | {(security.MemoryProtections.AslrEnabled ? "✅ Enabled" : "❌ Disabled")} |");
            sb.AppendLine($"| DEP/NX | {(security.MemoryProtections.DepEnabled ? "✅ Enabled" : "❌ Disabled")} |");
            sb.AppendLine($"| Stack Canaries | {(security.MemoryProtections.StackCanariesPresent ? "✅ Present" : "⚠️ Unknown")} |");
            if (security.MemoryProtections.ModulesWithoutAslr.Any())
            {
                sb.AppendLine();
                sb.AppendLine($"**Modules without ASLR**: {string.Join(", ", security.MemoryProtections.ModulesWithoutAslr.Take(5))}");
            }
            sb.AppendLine();
        }

        // Security Recommendations
        if (security.Recommendations.Any())
        {
            sb.AppendLine("### Security Recommendations");
            sb.AppendLine();
            foreach (var rec in security.Recommendations)
            {
                sb.AppendLine($"- {rec}");
            }
            sb.AppendLine();
        }
    }

    private static void AppendProcessInfo(StringBuilder sb, ProcessInfo process, ReportOptions options)
    {
        sb.AppendLine("## 🖥️ Process Information");
        sb.AppendLine();

        // Command-line arguments
        if (process.Arguments.Any())
        {
            sb.AppendLine("### Command Line Arguments");
            sb.AppendLine();
            sb.AppendLine("| # | Argument |");
            sb.AppendLine("|---|----------|");

            for (var i = 0; i < process.Arguments.Count; i++)
            {
                var arg = EscapeMarkdownTableCell(process.Arguments[i]);
                // Truncate very long arguments
                if (arg.Length > 200)
                {
                    arg = arg[..200] + "...";
                }
                sb.AppendLine($"| {i} | `{arg}` |");
            }
            sb.AppendLine();
        }

        // Environment variables
        if (process.EnvironmentVariables.Any())
        {
            var envVars = process.EnvironmentVariables;
            var maxEnvVars = options.MaxEnvironmentVariables > 0
                ? options.MaxEnvironmentVariables
                : envVars.Count;
            var showAll = maxEnvVars >= envVars.Count;

            sb.AppendLine($"### Environment Variables ({envVars.Count} total)");
            sb.AppendLine();

            // Use collapsible section for large env var lists
            if (!showAll)
            {
                sb.AppendLine("<details>");
                sb.AppendLine($"<summary>View {maxEnvVars} of {envVars.Count} environment variables</summary>");
                sb.AppendLine();
            }

            sb.AppendLine("| Variable | Value |");
            sb.AppendLine("|----------|-------|");

            foreach (var envVar in envVars.Take(maxEnvVars))
            {
                var equalsIndex = envVar.IndexOf('=');
                string name, value;

                if (equalsIndex > 0)
                {
                    name = envVar[..equalsIndex];
                    value = envVar[(equalsIndex + 1)..];
                }
                else
                {
                    name = envVar;
                    value = "";
                }

                // Escape and truncate
                name = EscapeMarkdownTableCell(name);
                value = EscapeMarkdownTableCell(value);
                if (value.Length > 100)
                {
                    value = value[..100] + "...";
                }

                sb.AppendLine($"| `{name}` | {value} |");
            }

            if (!showAll)
            {
                sb.AppendLine();
                sb.AppendLine("</details>");
            }
            sb.AppendLine();
        }

        // Metadata
        if (process.Argc.HasValue || !string.IsNullOrEmpty(process.ArgvAddress) || process.SensitiveDataFiltered == true)
        {
            sb.AppendLine("### Extraction Metadata");
            sb.AppendLine();
            if (process.Argc.HasValue)
            {
                sb.AppendLine($"- **argc**: {process.Argc}");
            }
            if (!string.IsNullOrEmpty(process.ArgvAddress))
            {
                sb.AppendLine($"- **argv address**: `{process.ArgvAddress}`");
            }
            if (process.SensitiveDataFiltered == true)
            {
                sb.AppendLine("- **Note**: ⚠️ Sensitive environment variables were filtered");
            }
            sb.AppendLine();
        }
    }

    private static void AppendRecommendations(StringBuilder sb, CrashAnalysisResult analysis)
    {
        sb.AppendLine("## 💡 Recommendations");
        sb.AppendLine();

        // Use new structure if available
        var recommendations = analysis.Summary?.Recommendations ?? new List<string>();
        foreach (var rec in recommendations)
        {
            sb.AppendLine($"- {rec}");
        }
        sb.AppendLine();
    }

    private static void AppendRawOutput(StringBuilder sb, CrashAnalysisResult analysis)
    {
        sb.AppendLine("## 📝 Raw Debugger Output");
        sb.AppendLine();
        sb.AppendLine("<details>");
        sb.AppendLine("<summary>Click to expand raw output</summary>");
        sb.AppendLine();

        // Use new structure if available
        var rawOutput = analysis.RawCommands ?? new Dictionary<string, string>();
        foreach (var (command, output) in rawOutput)
        {
            sb.AppendLine($"### `{command}`");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(output.Length > 5000 ? output.Substring(0, 5000) + "\n... (truncated)" : output);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("</details>");
        sb.AppendLine();
    }

    private static void AppendFooter(StringBuilder sb, ReportMetadata metadata)
    {
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"*Report generated by Debugger MCP Server v{metadata.ServerVersion}*");
        sb.AppendLine();
        sb.AppendLine($"*Timestamp: {metadata.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC*");
    }

    private static string TruncateString(string value, int maxLength = 0)
    {
        // Don't truncate by default - show full values
        if (string.IsNullOrEmpty(value))
        {
            return value ?? "";
        }
        // Only truncate if maxLength > 0 and value exceeds it
        if (maxLength > 0 && value.Length > maxLength)
        {
            return value.Substring(0, maxLength - 3) + "...";
        }
        return value;
    }

    private static string FormatThreadId(ThreadInfo thread)
    {
        if (thread.ManagedThreadId.HasValue)
        {
            // Format: "M#1 (0x8954)" or "M#1 DEAD"
            var prefix = thread.IsDead ? "💀" : thread.IsFaulting ? "⚠️" : "";
            return $"{prefix}M#{thread.ManagedThreadId} (0x{thread.OsThreadId})";
        }
        return thread.ThreadId;
    }

    // === Phase 2 ClrMD Enrichment Methods ===

    private static void AppendGcSummary(StringBuilder sb, GcSummary gc)
    {
        sb.AppendLine("### 🗑️ GC Heap Summary (ClrMD)");
        sb.AppendLine();
        sb.AppendLine("| Property | Value |");
        sb.AppendLine("|----------|-------|");
        sb.AppendLine($"| Mode | {gc.GcMode} |");
        sb.AppendLine($"| Heap Count | {gc.HeapCount} |");
        sb.AppendLine($"| Total Heap Size | {FormatBytesCompact(gc.TotalHeapSize)} |");
        if (gc.Fragmentation.HasValue)
        {
            sb.AppendLine($"| Fragmentation | {gc.Fragmentation.Value:P2} ({FormatBytesCompact(gc.FragmentationBytes ?? 0)}) |");
        }
        sb.AppendLine($"| Finalizable Objects | {gc.FinalizableObjectCount:N0} |");
        sb.AppendLine();

        if (gc.GenerationSizes != null)
        {
            sb.AppendLine("**Generation Sizes:**");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine($"Gen0: {FormatBytesCompact(gc.GenerationSizes.Gen0)}");
            sb.AppendLine($"Gen1: {FormatBytesCompact(gc.GenerationSizes.Gen1)}");
            sb.AppendLine($"Gen2: {FormatBytesCompact(gc.GenerationSizes.Gen2)}");
            sb.AppendLine($"LOH:  {FormatBytesCompact(gc.GenerationSizes.Loh)}");
            sb.AppendLine($"POH:  {FormatBytesCompact(gc.GenerationSizes.Poh)}");
            sb.AppendLine("```");
            sb.AppendLine();
        }
    }

    private static void AppendTopMemoryConsumers(StringBuilder sb, TopMemoryConsumers mem)
    {
        sb.AppendLine("### 📊 Top Memory Consumers (ClrMD)");
        sb.AppendLine();

        if (mem.Summary != null)
        {
            sb.AppendLine($"*{mem.Summary.TotalObjects:N0} objects, {FormatBytesCompact(mem.Summary.TotalSize)}, {mem.Summary.UniqueTypes:N0} unique types*");
            sb.AppendLine($"*Analysis time: {mem.Summary.AnalysisTimeMs:N0}ms*");
            if (mem.Summary.WasAborted)
                sb.AppendLine("*⚠️ Analysis was aborted due to timeout*");
            sb.AppendLine();
        }

        if (mem.BySize?.Count > 0)
        {
            sb.AppendLine("**By Total Size:**");
            sb.AppendLine();
            sb.AppendLine("| Type | Count | Total Size | % |");
            sb.AppendLine("|------|------:|----------:|--:|");
            foreach (var item in mem.BySize.Take(15))
            {
                var escapedType = EscapeMarkdownTableCell(item.Type);
                sb.AppendLine($"| {escapedType} | {item.Count:N0} | {FormatBytesCompact(item.TotalSize)} | {item.Percentage:F1}% |");
            }
            sb.AppendLine();
        }

        if (mem.LargeObjects?.Count > 0)
        {
            sb.AppendLine("**Large Objects (>85KB):**");
            sb.AppendLine();
            sb.AppendLine("| Address | Type | Size |");
            sb.AppendLine("|---------|------|-----:|");
            foreach (var obj in mem.LargeObjects.Take(10))
            {
                var escapedType = EscapeMarkdownTableCell(obj.Type);
                sb.AppendLine($"| `{obj.Address}` | {escapedType} | {FormatBytesCompact(obj.Size)} |");
            }
            sb.AppendLine();
        }
    }

    private static void AppendAsyncAnalysis(StringBuilder sb, AsyncAnalysis async)
    {
        sb.AppendLine("### ⏳ Async/Task Analysis (ClrMD)");
        sb.AppendLine();

        if (async.Summary != null)
        {
            sb.AppendLine("| Status | Count |");
            sb.AppendLine("|--------|------:|");
            sb.AppendLine($"| Total Tasks | {async.Summary.TotalTasks:N0} |");
            sb.AppendLine($"| Completed | {async.Summary.CompletedTasks:N0} |");
            sb.AppendLine($"| Pending | {async.Summary.PendingTasks:N0} |");
            sb.AppendLine($"| Faulted | {async.Summary.FaultedTasks:N0} |");
            sb.AppendLine($"| Canceled | {async.Summary.CanceledTasks:N0} |");
            sb.AppendLine();
        }

        sb.AppendLine($"*Analysis time: {async.AnalysisTimeMs:N0}ms*");
        if (async.WasAborted)
            sb.AppendLine("*⚠️ Analysis was aborted due to timeout*");
        sb.AppendLine();

        if (async.FaultedTasks?.Count > 0)
        {
            sb.AppendLine("**🔥 Faulted Tasks:**");
            sb.AppendLine();
            foreach (var task in async.FaultedTasks.Take(10))
            {
                sb.AppendLine($"- `{task.Address}`: {EscapeMarkdownTableCell(task.TaskType)}");
                if (!string.IsNullOrEmpty(task.ExceptionType))
                    sb.AppendLine($"  - Exception: `{task.ExceptionType}`");
                if (!string.IsNullOrEmpty(task.ExceptionMessage))
                    sb.AppendLine($"  - Message: {EscapeMarkdownTableCell(task.ExceptionMessage)}");
            }
            sb.AppendLine();
        }

        if (async.PendingStateMachines?.Count > 0)
        {
            sb.AppendLine("**🔄 Pending State Machines:**");
            sb.AppendLine();
            sb.AppendLine("| Address | Type | State |");
            sb.AppendLine("|---------|------|------:|");
            foreach (var sm in async.PendingStateMachines.Take(20))
            {
                var stateDesc = sm.CurrentState switch
                {
                    -1 => "-1 (not started)",
                    -2 => "-2 (completed)",
                    >= 0 => $"{sm.CurrentState} (await #{sm.CurrentState})",
                    _ => sm.CurrentState.ToString()
                };
                sb.AppendLine($"| `{sm.Address}` | {EscapeMarkdownTableCell(sm.StateMachineType)} | {stateDesc} |");
            }
            sb.AppendLine();
        }
    }

    private static void AppendStringAnalysis(StringBuilder sb, StringAnalysis str)
    {
        sb.AppendLine("### 📝 String Duplicate Analysis (ClrMD)");
        sb.AppendLine();

        if (str.Summary != null)
        {
            sb.AppendLine("| Metric | Value |");
            sb.AppendLine("|--------|------:|");
            sb.AppendLine($"| Total Strings | {str.Summary.TotalStrings:N0} |");
            sb.AppendLine($"| Unique Strings | {str.Summary.UniqueStrings:N0} |");
            sb.AppendLine($"| Duplicate Count | {str.Summary.DuplicateStrings:N0} |");
            sb.AppendLine($"| Total Size | {FormatBytesCompact(str.Summary.TotalSize)} |");
            sb.AppendLine($"| Wasted Size | {FormatBytesCompact(str.Summary.WastedSize)} ({str.Summary.WastedPercentage:F1}%) |");
            sb.AppendLine();
        }

        sb.AppendLine($"*Analysis time: {str.AnalysisTimeMs:N0}ms*");
        if (str.WasAborted)
            sb.AppendLine("*⚠️ Analysis was aborted due to timeout*");
        sb.AppendLine();

        if (str.ByLength != null)
        {
            sb.AppendLine("**Length Distribution:**");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine($"Empty (0):       {str.ByLength.Empty:N0}");
            sb.AppendLine($"Short (1-10):    {str.ByLength.Short:N0}");
            sb.AppendLine($"Medium (11-100): {str.ByLength.Medium:N0}");
            sb.AppendLine($"Long (101-1000): {str.ByLength.Long:N0}");
            sb.AppendLine($"Very Long (>1k): {str.ByLength.VeryLong:N0}");
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (str.TopDuplicates?.Count > 0)
        {
            sb.AppendLine("**Top Duplicates (by wasted bytes):**");
            sb.AppendLine();
            sb.AppendLine("| Value | Count | Wasted | Suggestion |");
            sb.AppendLine("|-------|------:|-------:|------------|");
            foreach (var dup in str.TopDuplicates.Take(15))
            {
                var displayValue = dup.Value.Length > 40
                    ? $"`{EscapeMarkdownTableCell(dup.Value[..40])}...`"
                    : $"`{EscapeMarkdownTableCell(dup.Value)}`";
                sb.AppendLine($"| {displayValue} | {dup.Count:N0} | {FormatBytesCompact(dup.WastedBytes)} | {EscapeMarkdownTableCell(dup.Suggestion ?? "")} |");
            }
            sb.AppendLine();
        }
    }

    private static string FormatBytesCompact(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    private static string EscapeMarkdownTableCell(string value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        return value.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
    }
}
