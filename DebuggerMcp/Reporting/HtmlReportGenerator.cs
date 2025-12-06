using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;
using DebuggerMcp.Analysis;
using DebuggerMcp.Watches;

namespace DebuggerMcp.Reporting;

/// <summary>
/// Generates crash analysis reports in HTML format with CSS-styled charts.
/// </summary>
public class HtmlReportGenerator : IReportGenerator
{
    /// <inheritdoc />
    public string Generate(CrashAnalysisResult analysis, ReportOptions options, ReportMetadata metadata)
    {
        if (analysis == null)
        {
            throw new ArgumentNullException(nameof(analysis));
        }

        options ??= ReportOptions.FullReport;
        metadata ??= new ReportMetadata();

        var sb = new StringBuilder();

        // HTML header with embedded CSS
        AppendHtmlHeader(sb, options, metadata);

        // Body content
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"container\">");

        // Header section
        AppendHeader(sb, analysis, metadata);

        // Executive Summary
        AppendSummary(sb, analysis);

        // Crash Info
        if (options.IncludeCrashInfo)
        {
            AppendCrashInfo(sb, analysis);
        }

        // Call Stacks (all threads with frames) - use new structure if available
        var threads = analysis.Threads?.All ?? new List<ThreadInfo>();
        if (options.IncludeCallStacks && threads.Any(t => t.CallStack.Any()))
        {
            AppendAllThreadCallStacks(sb, analysis, options);
        }

        // Memory Section
        if (options.IncludeHeapStats || options.IncludeMemoryLeakInfo)
        {
            AppendMemorySection(sb, analysis, options);
        }

        // Thread Info - use new structure if available
        var threadsForInfo = analysis.Threads?.All ?? new List<ThreadInfo>();
        if (options.IncludeThreadInfo && threadsForInfo.Any())
        {
            AppendThreadInfo(sb, analysis, options);
        }

        // .NET Info (from new hierarchical structure)
        var hasDotNetInfo = analysis.Environment?.Runtime != null || 
                            analysis.Exception?.Analysis != null ||
                            analysis.Assemblies?.Items?.Any() == true ||
                            analysis.Memory?.Gc != null;
        if (options.IncludeDotNetInfo && hasDotNetInfo)
        {
            AppendDotNetInfoFromHierarchy(sb, analysis);
            
            // Exception Deep Analysis
            if (analysis.Exception?.Analysis != null)
            {
                AppendExceptionAnalysis(sb, analysis.Exception.Analysis);
            }
            
            // Type Resolution Analysis
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
                AppendGcSummaryHtml(sb, analysis.Memory.Gc);
            }
            
            // Top Memory Consumers (deep analysis)
            if (analysis.Memory?.TopConsumers != null)
            {
                AppendTopMemoryConsumersHtml(sb, analysis.Memory.TopConsumers);
            }
            
            // Async Analysis (deep analysis) - use AsyncInfo
            if (analysis.Async != null)
            {
                AppendAsyncInfoFromHierarchyHtml(sb, analysis.Async);
            }
            
            // String Analysis (deep analysis)
            if (analysis.Memory?.Strings != null)
            {
                AppendStringAnalysisHtml(sb, analysis.Memory.Strings);
            }
        }

        // Deadlock Info - use new structure if available
        var deadlockInfo = analysis.Threads?.Deadlock;
        if (options.IncludeDeadlockInfo && deadlockInfo?.Detected == true)
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

        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private static void AppendHtmlHeader(StringBuilder sb, ReportOptions options, ReportMetadata metadata)
    {
        var title = HttpUtility.HtmlEncode(options.Title ?? "Crash Analysis Report");
        
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine($"<title>{title}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(GetEmbeddedCss());
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
    }

    private static string GetEmbeddedCss()
    {
        return @"
:root {
    --bg-primary: #1a1a2e;
    --bg-secondary: #16213e;
    --bg-card: #0f3460;
    --text-primary: #eaeaea;
    --text-secondary: #a0a0a0;
    --accent: #e94560;
    --accent-secondary: #00d9ff;
    --success: #00c851;
    --warning: #ffbb33;
    --error: #ff4444;
    --border: #2a3f5f;
}

* { box-sizing: border-box; margin: 0; padding: 0; }

body {
    font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
    background: linear-gradient(135deg, var(--bg-primary) 0%, var(--bg-secondary) 100%);
    color: var(--text-primary);
    line-height: 1.6;
    min-height: 100vh;
}

.container {
    max-width: 1200px;
    margin: 0 auto;
    padding: 2rem;
}

h1, h2, h3 { margin-bottom: 1rem; }
h1 { color: var(--accent); font-size: 2.5rem; border-bottom: 3px solid var(--accent); padding-bottom: 0.5rem; }
h2 { color: var(--accent-secondary); font-size: 1.8rem; margin-top: 2rem; }
h3 { color: var(--text-primary); font-size: 1.3rem; }

.card {
    background: var(--bg-card);
    border-radius: 12px;
    padding: 1.5rem;
    margin: 1rem 0;
    border: 1px solid var(--border);
    box-shadow: 0 4px 20px rgba(0,0,0,0.3);
}

.summary-box {
    background: linear-gradient(135deg, var(--bg-card) 0%, #1a3a5c 100%);
    border-left: 4px solid var(--accent);
    padding: 1.5rem;
    margin: 1rem 0;
    border-radius: 0 12px 12px 0;
    font-size: 1.1rem;
}

.alert { padding: 1rem; border-radius: 8px; margin: 1rem 0; }
.alert-error { background: rgba(255,68,68,0.2); border: 1px solid var(--error); }
.alert-danger { background: rgba(255,68,68,0.3); border: 1px solid var(--error); color: #ff6b6b; font-weight: 600; }
.alert-warning { background: rgba(255,187,51,0.2); border: 1px solid var(--warning); }
.alert-info { background: rgba(0,217,255,0.15); border: 1px solid var(--accent); }
.alert-success { background: rgba(0,200,81,0.2); border: 1px solid var(--success); }

/* Security Analysis */
.vuln-detail { padding: 1rem; margin: 1rem 0; background: rgba(0,0,0,0.2); border-radius: 6px; border-left: 3px solid var(--warning); }
.vuln-detail h4 { color: var(--warning); margin: 0 0 0.5rem 0; }
.compact-table { width: auto; }
.compact-table td { padding: 0.5rem 1rem; }
.recommendation-list { list-style-type: disc; padding-left: 1.5rem; }
.recommendation-list li { padding: 0.25rem 0; }

.meta-table { width: 100%; border-collapse: collapse; margin: 1rem 0; }
.meta-table td { padding: 0.5rem 1rem; border-bottom: 1px solid var(--border); }
.meta-table td:first-child { color: var(--text-secondary); width: 150px; }

table { width: 100%; border-collapse: collapse; margin: 1rem 0; }
th, td { padding: 0.75rem 1rem; text-align: left; border-bottom: 1px solid var(--border); }
th { background: var(--bg-secondary); color: var(--accent-secondary); font-weight: 600; }
tr:hover { background: rgba(0,217,255,0.05); }

.code-block {
    background: #0d1117;
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 1rem;
    font-family: 'Consolas', 'Monaco', monospace;
    font-size: 0.9rem;
    overflow-x: auto;
    white-space: pre-wrap;
    word-break: break-all;
}

/* Thread Call Stacks */
.thread-callstack { margin-bottom: 1.5rem; }
.thread-callstack h3 { margin-bottom: 1rem; display: flex; align-items: center; gap: 0.5rem; flex-wrap: wrap; }
.thread-callstack.faulting-thread { border-left: 3px solid var(--error); }
.thread-state { color: var(--text-secondary); font-weight: normal; font-size: 0.9rem; }
.badge-danger { background: var(--error); color: #fff; }

/* Tables - General */
.table-container { overflow-x: auto; margin: 1rem 0; }
.data-table { width: 100%; border-collapse: collapse; }
.data-table th, .data-table td { padding: 0.5rem; text-align: left; border-bottom: 1px solid var(--border); }
.data-table thead { background: var(--bg-secondary); }
.data-table th { color: var(--accent-secondary); font-weight: 600; }

/* Call Stack Table */
.callstack-table { width: 100%; border-collapse: collapse; font-size: 0.85rem; table-layout: auto; }
.callstack-table thead { background: var(--bg-secondary); }
.callstack-table th { padding: 0.5rem 0.75rem; text-align: left; color: var(--text-secondary); font-weight: 600; border-bottom: 2px solid var(--border); white-space: nowrap; }
.callstack-table td { padding: 0.4rem 0.75rem; border-bottom: 1px solid rgba(255,255,255,0.05); vertical-align: middle; }
.callstack-table tr:hover { background: rgba(0,217,255,0.1); }
.callstack-table tr.managed-frame { background: rgba(0,200,83,0.05); }
.callstack-table tr.native-frame { background: rgba(0,120,215,0.05); }
.callstack-table .frame-type { font-size: 0.8rem; text-align: center; white-space: nowrap; }
.callstack-table .frame-num { color: var(--text-secondary); text-align: center; font-family: monospace; white-space: nowrap; }
.callstack-table .frame-module { color: var(--accent-secondary); font-family: monospace; white-space: nowrap; max-width: 250px; overflow: hidden; text-overflow: ellipsis; }
.callstack-table .frame-func { color: var(--text-primary); font-family: monospace; white-space: nowrap; }
.callstack-table .frame-source { font-size: 0.8rem; white-space: nowrap; color: var(--text-secondary); }
.source-link { color: var(--success); text-decoration: none; transition: color 0.2s; }
.source-link:hover { color: var(--accent-secondary); text-decoration: underline; }
.source-info { color: var(--text-secondary); }

/* Bar Charts */
.bar-chart { margin: 1rem 0; }
.bar-item { display: flex; align-items: center; gap: 0.75rem; margin: 0.5rem 0; }
.bar-label { flex: 0 0 200px; color: var(--text-secondary); font-size: 0.9rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.bar-container { display: block; flex: 1; height: 24px; background: var(--bg-secondary); border-radius: 4px; overflow: hidden; min-width: 100px; position: relative; }
.bar-fill { display: block; height: 100%; background: #3b82f6; border-radius: 4px; }
.bar-value { flex: 0 0 140px; text-align: right; font-size: 0.9rem; color: var(--text-secondary); }

/* Pie Chart (CSS only) */
.pie-chart { display: flex; align-items: center; gap: 2rem; margin: 1rem 0; }
.pie-visual { width: 150px; height: 150px; border-radius: 50%; background: conic-gradient(var(--accent) 0deg, var(--accent-secondary) 90deg, var(--success) 180deg, var(--warning) 270deg, var(--accent) 360deg); }
.pie-legend { list-style: none; }
.pie-legend li { padding: 0.25rem 0; display: flex; align-items: center; gap: 0.5rem; }
.legend-color { width: 16px; height: 16px; border-radius: 3px; }

.badge { display: inline-block; padding: 0.25rem 0.75rem; border-radius: 20px; font-size: 0.8rem; font-weight: 600; }
.badge-success { background: var(--success); color: #000; }
.badge-error { background: var(--error); color: #fff; }
.badge-warning { background: var(--warning); color: #000; }
.badge-info { background: var(--accent-secondary); color: #fff; }

/* Variables Section */
details { margin: 0.5rem 0; }
details summary { cursor: pointer; padding: 0.5rem; background: var(--bg-secondary); border-radius: 4px; }
details summary:hover { background: rgba(0,217,255,0.1); }
details[open] summary { margin-bottom: 0.5rem; }
.data-table.compact { font-size: 0.85rem; }
.data-table.compact td, .data-table.compact th { padding: 0.3rem 0.5rem; }

/* Variable Value Styles */
.string-value { color: var(--success); }
.address-value { color: var(--accent); }
.numeric-value { color: var(--warning); }

.watch-item { margin: 1rem 0; padding: 1rem; background: var(--bg-secondary); border-radius: 8px; }
.watch-expr { font-family: monospace; color: var(--accent-secondary); font-size: 1.1rem; }
.watch-desc { color: var(--text-secondary); font-style: italic; margin: 0.5rem 0; }
.watch-value { background: #0d1117; padding: 0.75rem; border-radius: 4px; font-family: monospace; margin-top: 0.5rem; max-height: 200px; overflow: auto; }

.recommendation { padding: 0.75rem 1rem; margin: 0.5rem 0; background: var(--bg-secondary); border-radius: 8px; border-left: 3px solid var(--accent-secondary); }
.dim { color: var(--text-secondary); font-style: italic; }

footer { margin-top: 3rem; padding-top: 1rem; border-top: 1px solid var(--border); color: var(--text-secondary); font-size: 0.9rem; text-align: center; }

@media print {
    body { background: white; color: black; }
    .card { box-shadow: none; border: 1px solid #ccc; }
    .bar-fill { background: #333; }
}
";
    }

    private static void AppendHeader(StringBuilder sb, CrashAnalysisResult analysis, ReportMetadata metadata)
    {
        var crashType = analysis.Summary?.CrashType ?? "Unknown";
        sb.AppendLine($"<h1>🔍 {HttpUtility.HtmlEncode(crashType)} - Crash Analysis</h1>");
        sb.AppendLine("<table class=\"meta-table\">");
        sb.AppendLine($"<tr><td>Generated</td><td>{metadata.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC</td></tr>");
        sb.AppendLine($"<tr><td>Dump ID</td><td><code>{HttpUtility.HtmlEncode(metadata.DumpId)}</code></td></tr>");
        sb.AppendLine($"<tr><td>Debugger</td><td>{HttpUtility.HtmlEncode(metadata.DebuggerType)}</td></tr>");
        
        // Add platform info if available - use new structure first, fall back to old
        var platform = analysis.Environment?.Platform;
        if (platform != null)
        {
            var osInfo = platform.Os ?? "Unknown";
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
            
            sb.AppendLine($"<tr><td>Platform</td><td>{HttpUtility.HtmlEncode(osInfo)}</td></tr>");
            
            if (!string.IsNullOrEmpty(platform.Architecture))
            {
                sb.AppendLine($"<tr><td>Architecture</td><td>{HttpUtility.HtmlEncode(platform.Architecture)} ({platform.PointerSize ?? 64}-bit)</td></tr>");
            }
            
            // Runtime version from new Environment.Runtime or old Platform.RuntimeVersion
            var runtimeVersion = analysis.Environment?.Runtime?.Version ?? platform.RuntimeVersion;
            if (!string.IsNullOrEmpty(runtimeVersion))
            {
                sb.AppendLine($"<tr><td>.NET Runtime</td><td>{HttpUtility.HtmlEncode(runtimeVersion)}</td></tr>");
            }
        }
        
        sb.AppendLine("</table>");
    }

    private static void AppendSummary(StringBuilder sb, CrashAnalysisResult analysis)
    {
        sb.AppendLine("<h2>📋 Executive Summary</h2>");
        sb.AppendLine("<div class=\"summary-box\">");
        // Use new structure first, fall back to old
        var summaryText = analysis.Summary?.Description;
        sb.AppendLine(HttpUtility.HtmlEncode(string.IsNullOrEmpty(summaryText) ? "No summary available." : summaryText));
        sb.AppendLine("</div>");
    }

    private static void AppendCrashInfo(StringBuilder sb, CrashAnalysisResult analysis)
    {
        sb.AppendLine("<h2>🔴 Crash Information</h2>");
        sb.AppendLine("<div class=\"card\">");

        // Use new structure first, fall back to old
        var crashType = analysis.Summary?.CrashType ?? "Unknown";
        var summaryText = analysis.Summary?.Description;

        // Show crash type and summary
        if (!string.IsNullOrEmpty(crashType) || !string.IsNullOrEmpty(summaryText))
        {
            sb.AppendLine("<table class=\"meta-table\">");
            if (!string.IsNullOrEmpty(crashType))
            {
                sb.AppendLine($"<tr><td>Crash Type</td><td><strong>{HttpUtility.HtmlEncode(crashType)}</strong></td></tr>");
            }
            if (!string.IsNullOrEmpty(summaryText))
            {
                sb.AppendLine($"<tr><td>Summary</td><td>{HttpUtility.HtmlEncode(summaryText)}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        // Show exception details if available
        var exception = analysis.Exception;
        
        if (exception != null)
        {
            sb.AppendLine("<h3>Exception Details</h3>");
            sb.AppendLine("<table class=\"meta-table\">");
            sb.AppendLine($"<tr><td>Type</td><td><code>{HttpUtility.HtmlEncode(exception.Type)}</code></td></tr>");
            if (!string.IsNullOrEmpty(exception.Message))
            {
                sb.AppendLine($"<tr><td>Message</td><td>{HttpUtility.HtmlEncode(exception.Message)}</td></tr>");
            }
            if (!string.IsNullOrEmpty(exception.Address))
            {
                sb.AppendLine($"<tr><td>Address</td><td><code>{HttpUtility.HtmlEncode(exception.Address)}</code></td></tr>");
            }
            if (!string.IsNullOrEmpty(exception.HResult))
            {
                sb.AppendLine($"<tr><td>HResult</td><td><code>{HttpUtility.HtmlEncode(exception.HResult)}</code></td></tr>");
            }
            sb.AppendLine("</table>");
        }
        
        // If no crash info at all, show a message
        if (string.IsNullOrEmpty(crashType) && string.IsNullOrEmpty(summaryText) && exception == null)
        {
            sb.AppendLine("<p class=\"dim\">No specific crash information available. Check thread stacks for details.</p>");
        }

        sb.AppendLine("</div>");
    }

    private static void AppendAllThreadCallStacks(StringBuilder sb, CrashAnalysisResult analysis, ReportOptions options)
    {
        sb.AppendLine("<h2>📚 Thread Call Stacks</h2>");

        // Get threads with call stacks, faulting thread first - use new structure if available
        var allThreads = analysis.Threads?.All ?? new List<ThreadInfo>();
        var threadsWithStacks = allThreads
            .Where(t => t.CallStack.Any())
            .OrderByDescending(t => t.IsFaulting)
            .ThenBy(t => t.ThreadId)
            .ToList();

        foreach (var thread in threadsWithStacks)
        {
            var faultingClass = thread.IsFaulting ? " faulting-thread" : "";
            var faultingBadge = thread.IsFaulting ? "<span class=\"badge badge-danger\">⚠️ Faulting</span> " : "";
            var stateInfo = !string.IsNullOrEmpty(thread.State) ? $" <span class=\"thread-state\">({HttpUtility.HtmlEncode(thread.State)})</span>" : "";
            
            // CLR thread info
            var clrInfo = "";
            if (!string.IsNullOrEmpty(thread.ThreadType))
            {
                clrInfo += $" <span class=\"badge badge-info\">{HttpUtility.HtmlEncode(thread.ThreadType)}</span>";
            }
            if (!string.IsNullOrEmpty(thread.CurrentException))
            {
                clrInfo += $" <span class=\"badge badge-danger\">🔥 {HttpUtility.HtmlEncode(thread.CurrentException)}</span>";
            }
            
            sb.AppendLine($"<div class=\"card thread-callstack{faultingClass}\">");
            sb.AppendLine($"<h3>{faultingBadge}Thread #{HttpUtility.HtmlEncode(thread.ThreadId)}{stateInfo}{clrInfo}</h3>");
            
            // Show parameters for faulting thread
            AppendThreadCallStack(sb, thread.CallStack, options, showParameters: thread.IsFaulting);
            
            sb.AppendLine("</div>");
        }
    }

    private static void AppendThreadCallStack(StringBuilder sb, List<StackFrame> callStack, ReportOptions options, bool showParameters = false)
    {
        var totalFrames = callStack.Count;
        var frames = callStack;
        if (options.MaxCallStackFrames > 0 && frames.Count > options.MaxCallStackFrames)
        {
            frames = frames.Take(options.MaxCallStackFrames).ToList();
            sb.AppendLine($"<p><em>Showing top {options.MaxCallStackFrames} of {totalFrames} frames</em></p>");
        }

        // Use a proper table for better alignment, wrapped for scroll
        sb.AppendLine("<div class=\"table-container\">");
        sb.AppendLine("<table class=\"callstack-table\">");
        sb.AppendLine("<thead><tr><th></th><th>#</th><th>Module</th><th>Function</th><th>Source</th></tr></thead>");
        sb.AppendLine("<tbody>");
        
        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            var module = HttpUtility.HtmlEncode(frame.Module ?? "???");
            var func = HttpUtility.HtmlEncode(frame.Function ?? "???");
            var frameTypeClass = frame.IsManaged ? "managed-frame" : "native-frame";
            var frameTypeIcon = frame.IsManaged ? "🟢" : "🔵";
            
            // Build the source info - clean up [opt] markers
            string sourceCell = "";
            if (!string.IsNullOrEmpty(frame.SourceUrl))
            {
                var fileName = Path.GetFileName(frame.SourceFile ?? "source");
                var lineNum = frame.LineNumber ?? 0;
                var displayText = HttpUtility.HtmlEncode($"{fileName}:{lineNum}");
                var url = HttpUtility.HtmlEncode(frame.SourceUrl);
                sourceCell = $"<a href=\"{url}\" target=\"_blank\" class=\"source-link\">{displayText}</a>";
            }
            else if (!string.IsNullOrEmpty(frame.Source))
            {
                // Clean up debugger markers like [opt], [inlined], etc.
                var cleanSource = CleanSourceInfo(frame.Source);
                sourceCell = $"<span class=\"source-info\">{HttpUtility.HtmlEncode(cleanSource)}</span>";
            }
            
            sb.AppendLine($"<tr class=\"{frameTypeClass}\">");
            sb.AppendLine($"<td class=\"frame-type\">{frameTypeIcon}</td>");
            sb.AppendLine($"<td class=\"frame-num\">{i:D2}</td>");
            sb.AppendLine($"<td class=\"frame-module\">{module}</td>");
            sb.AppendLine($"<td class=\"frame-func\">{func}</td>");
            sb.AppendLine($"<td class=\"frame-source\">{sourceCell}</td>");
            sb.AppendLine("</tr>");
        }
        
        sb.AppendLine("</tbody></table>");
        sb.AppendLine("</div>"); // Close table-container
        
        // Show parameters and locals for faulting thread
        if (showParameters)
        {
            var framesWithVars = frames.Where(f => 
                (f.Parameters != null && f.Parameters.Count > 0) || 
                (f.Locals != null && f.Locals.Count > 0)).ToList();
            
            if (framesWithVars.Any())
            {
                sb.AppendLine("<h4>📍 Frame Variables</h4>");
                
                foreach (var frame in framesWithVars)
                {
                    var funcName = HttpUtility.HtmlEncode(frame.Function ?? "???");
                    
                    sb.AppendLine("<details>");
                    sb.AppendLine($"<summary><strong>Frame {frame.FrameNumber:D2}:</strong> {funcName}</summary>");
                    
                    if (frame.Parameters != null && frame.Parameters.Count > 0)
                    {
                        sb.AppendLine("<p><strong>Parameters:</strong></p>");
                        sb.AppendLine("<table class=\"data-table compact\">");
                        sb.AppendLine("<thead><tr><th>Name</th><th>Type</th><th>Value</th></tr></thead>");
                        sb.AppendLine("<tbody>");
                        
                        foreach (var param in frame.Parameters)
                        {
                            var name = HttpUtility.HtmlEncode(param.Name ?? "[unnamed]");
                            var type = HttpUtility.HtmlEncode(param.Type ?? "-");
                            if (!string.IsNullOrEmpty(param.ByRefAddress))
                            {
                                type += " (ByRef)";
                            }
                            var value = FormatHtmlVariableValue(param.Value);
                            sb.AppendLine($"<tr><td><code>{name}</code></td><td><code>{type}</code></td><td>{value}</td></tr>");
                        }
                        
                        sb.AppendLine("</tbody></table>");
                    }
                    
                    if (frame.Locals != null && frame.Locals.Count > 0)
                    {
                        sb.AppendLine("<p><strong>Local Variables:</strong></p>");
                        sb.AppendLine("<table class=\"data-table compact\">");
                        sb.AppendLine("<thead><tr><th>Name</th><th>Type</th><th>Value</th></tr></thead>");
                        sb.AppendLine("<tbody>");
                        
                        foreach (var local in frame.Locals)
                        {
                            var name = HttpUtility.HtmlEncode(local.Name ?? "[unnamed]");
                            var type = HttpUtility.HtmlEncode(local.Type ?? "-");
                            var value = FormatHtmlVariableValue(local.Value);
                            sb.AppendLine($"<tr><td><code>{name}</code></td><td><code>{type}</code></td><td>{value}</td></tr>");
                        }
                        
                        sb.AppendLine("</tbody></table>");
                    }
                    
                    sb.AppendLine("</details>");
                }
            }
        }
    }
    
    private static string FormatHtmlVariableValue(object? value)
    {
        if (value == null)
        {
            return "<em>null</em>";
        }
        
        // If value is an expanded object (from showobj), format it as JSON
        if (value is not string)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
                var encoded = HttpUtility.HtmlEncode(json);
                return $"<details><summary>Object</summary><pre class=\"object-value\">{encoded}</pre></details>";
            }
            catch
            {
                return HttpUtility.HtmlEncode(value.ToString() ?? "");
            }
        }
        
        var stringValue = value as string ?? value.ToString() ?? "";
        
        if (string.IsNullOrEmpty(stringValue) || stringValue == "[NO DATA]")
        {
            return "<em>no data</em>";
        }
        
        var truncated = stringValue;
        var encoded2 = HttpUtility.HtmlEncode(truncated);
        
        // Format based on value type
        if (truncated.StartsWith("\"") && truncated.EndsWith("\""))
        {
            // String value
            return $"<code class=\"string-value\">{encoded2}</code>";
        }
        else if (truncated.StartsWith("0x"))
        {
            // Pointer/address
            return $"<code class=\"address-value\">{encoded2}</code>";
        }
        else if (truncated == "true" || truncated == "false")
        {
            // Boolean
            return $"<strong>{encoded2}</strong>";
        }
        else if (int.TryParse(truncated, out _) || long.TryParse(truncated, out _))
        {
            // Numeric
            return $"<code class=\"numeric-value\">{encoded2}</code>";
        }
        
        return encoded2;
    }

    private static void AppendMemorySection(StringBuilder sb, CrashAnalysisResult analysis, ReportOptions options)
    {
        sb.AppendLine("<h2>💾 Memory Analysis</h2>");

        // Memory consumption alert
        var leakAnalysis = analysis.Memory?.LeakAnalysis;
        var detected = leakAnalysis?.Detected ?? false;
        
        if (detected)
        {
            var severity = leakAnalysis?.Severity ?? "Elevated";
            var totalHeapBytes = leakAnalysis?.TotalHeapBytes ?? 0;
            var estimatedLeakedBytes = leakAnalysis?.EstimatedLeakedBytes ?? 0;
            var heapSize = totalHeapBytes > 0 ? totalHeapBytes : estimatedLeakedBytes;
            var potentialIssueIndicators = leakAnalysis?.PotentialIssueIndicators;
            
            sb.AppendLine("<div class=\"alert alert-warning\">");
            sb.AppendLine($"<strong>⚠️ Memory Consumption: {severity}</strong> - Total Heap: {AsciiCharts.FormatBytes(heapSize)}");
            
            if (potentialIssueIndicators?.Any() == true)
            {
                sb.AppendLine("<ul style=\"margin-top:8px; margin-bottom:0\">");
                foreach (var indicator in potentialIssueIndicators.Take(3))
                {
                    sb.AppendLine($"<li>{HttpUtility.HtmlEncode(indicator)}</li>");
                }
                sb.AppendLine("</ul>");
            }
            else
            {
                sb.AppendLine("<p style=\"margin-top:8px; margin-bottom:0; font-size:0.9em;\"><em>Note: Use memory profiling with multiple snapshots to identify actual leaks.</em></p>");
            }
            sb.AppendLine("</div>");
        }

        // Top consumers chart
        var topConsumers = analysis.Memory?.LeakAnalysis?.TopConsumers;
        if (topConsumers?.Any() == true)
        {
            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine("<h3>Top Memory Consumers</h3>");
            
            var consumers = topConsumers.Take(10).ToList();
            var maxSize = consumers.Max(c => c.TotalSize);

            sb.AppendLine("<div class=\"bar-chart\">");
            foreach (var consumer in consumers)
            {
                var percentage = maxSize > 0 ? (double)consumer.TotalSize / maxSize * 100 : 0;
                var label = HttpUtility.HtmlEncode(consumer.TypeName);
                sb.AppendLine("<div class=\"bar-item\">");
                sb.AppendLine($"<span class=\"bar-label\" title=\"{HttpUtility.HtmlEncode(consumer.TypeName)}\">{label}</span>");
                sb.AppendLine($"<span class=\"bar-container\"><span class=\"bar-fill\" style=\"width:{percentage:F1}%\"></span></span>");
                sb.AppendLine($"<span class=\"bar-value\">{AsciiCharts.FormatBytes(consumer.TotalSize)} ({consumer.Count:N0})</span>");
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
        }
    }

    private static void AppendThreadInfo(StringBuilder sb, CrashAnalysisResult analysis, ReportOptions options)
    {
        sb.AppendLine("<h2>🧵 Thread Information</h2>");
        sb.AppendLine("<div class=\"card\">");
        
        // Use new structure if available
        var allThreads = analysis.Threads?.All ?? new List<ThreadInfo>();
        var threadSummary = analysis.Threads?.Summary;
        var threadCount = threadSummary?.Total ?? allThreads.Count;
        
        sb.AppendLine($"<p><strong>Total Threads:</strong> {threadCount}</p>");

        // Thread state chart
        var stateGroups = allThreads.GroupBy(t => t.State ?? "Unknown").ToDictionary(g => g.Key, g => g.Count());
        if (stateGroups.Any())
        {
            var total = stateGroups.Values.Sum();
            sb.AppendLine("<h3>Thread State Distribution</h3>");
            sb.AppendLine("<div class=\"bar-chart\">");
            foreach (var (state, count) in stateGroups.OrderByDescending(kv => kv.Value))
            {
                var percentage = total > 0 ? (double)count / total * 100 : 0;
                sb.AppendLine("<div class=\"bar-item\">");
                sb.AppendLine($"<span class=\"bar-label\">{HttpUtility.HtmlEncode(state)}</span>");
                sb.AppendLine($"<span class=\"bar-container\"><span class=\"bar-fill\" style=\"width:{percentage:F1}%\"></span></span>");
                sb.AppendLine($"<span class=\"bar-value\">{count} ({percentage:F1}%)</span>");
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");
        }

        // Thread table - use new structure if available
        var threadsList = analysis.Threads?.All ?? new List<ThreadInfo>();
        var threadsToShow = threadsList.AsEnumerable();
        if (options.MaxThreadsToShow > 0 && threadsList.Count > options.MaxThreadsToShow)
        {
            threadsToShow = threadsToShow.Take(options.MaxThreadsToShow);
        }

        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Thread ID</th><th>State</th><th>Top Function</th></tr>");
        foreach (var thread in threadsToShow)
        {
            sb.AppendLine($"<tr><td>{HttpUtility.HtmlEncode(thread.ThreadId)}</td><td>{HttpUtility.HtmlEncode(thread.State ?? "Unknown")}</td><td><code>{HttpUtility.HtmlEncode(thread.TopFunction ?? "-")}</code></td></tr>");
        }
        sb.AppendLine("</table>");
        sb.AppendLine("</div>");
    }

    private static void AppendDotNetInfoFromHierarchy(StringBuilder sb, CrashAnalysisResult analysis)
    {
        sb.AppendLine("<h2>🟣 .NET Runtime Information</h2>");
        sb.AppendLine("<div class=\"card\">");

        // CLR Version from Runtime info
        var runtime = analysis.Environment?.Runtime;
        if (!string.IsNullOrEmpty(runtime?.ClrVersion))
        {
            sb.AppendLine($"<p><strong>CLR Version:</strong> {HttpUtility.HtmlEncode(runtime.ClrVersion)}</p>");
        }

        // Exception info from Exception
        var exception = analysis.Exception;
        if (!string.IsNullOrEmpty(exception?.Type))
        {
            sb.AppendLine("<h3>🔥 Managed Exception</h3>");
            sb.AppendLine("<div class=\"exception-details\">");
            sb.AppendLine($"<p><strong>Type:</strong> <code>{HttpUtility.HtmlEncode(exception.Type)}</code></p>");
            
            if (!string.IsNullOrEmpty(exception.Message))
            {
                sb.AppendLine($"<p><strong>Message:</strong> {HttpUtility.HtmlEncode(exception.Message)}</p>");
            }
            
            if (!string.IsNullOrEmpty(exception.HResult))
            {
                sb.AppendLine($"<p><strong>HResult:</strong> 0x{HttpUtility.HtmlEncode(exception.HResult)}</p>");
            }
            
            if (exception.HasInnerException == true)
            {
                var count = exception.NestedExceptionCount > 0 ? exception.NestedExceptionCount : 1;
                sb.AppendLine($"<p><strong>Inner Exceptions:</strong> {count} nested exception(s)</p>");
            }
            
            if (exception.StackTrace != null && exception.StackTrace.Count > 0)
            {
                sb.AppendLine("<p><strong>Stack Trace:</strong></p>");
                sb.AppendLine("<div class=\"code-block\">");
                foreach (var frame in exception.StackTrace)
                {
                    sb.AppendLine(HttpUtility.HtmlEncode($"  {frame.Function}"));
                }
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");
        }

        // Finalizer queue from ThreadSummary
        var threadSummary = analysis.Threads?.Summary;
        if (threadSummary?.FinalizerQueueLength > 0)
        {
            sb.AppendLine($"<p><strong>Finalization Queue:</strong> {threadSummary.FinalizerQueueLength:N0} objects</p>");
        }

        // Async deadlock from Async
        if (analysis.Async?.HasDeadlock == true)
        {
            sb.AppendLine("<div class=\"alert alert-error\">");
            sb.AppendLine("<strong>⚠️ Async Deadlock Detected</strong> - Potential async/await deadlock pattern found.");
            sb.AppendLine("</div>");
        }
        
        // Thread Pool Information from Threads.ThreadPool
        var tp = analysis.Threads?.ThreadPool;
        if (tp != null)
        {
            sb.AppendLine("<h3>🔄 Thread Pool Status</h3>");
            sb.AppendLine("<table class=\"data-table\">");
            sb.AppendLine("<thead><tr><th>Metric</th><th>Value</th></tr></thead>");
            sb.AppendLine("<tbody>");
            
            if (tp.CpuUtilization.HasValue)
                sb.AppendLine($"<tr><td>CPU Utilization</td><td>{tp.CpuUtilization}%</td></tr>");
            if (tp.WorkersTotal.HasValue)
                sb.AppendLine($"<tr><td>Workers Total</td><td>{tp.WorkersTotal}</td></tr>");
            if (tp.WorkersRunning.HasValue)
                sb.AppendLine($"<tr><td>Workers Running</td><td>{tp.WorkersRunning}</td></tr>");
            if (tp.WorkersIdle.HasValue)
                sb.AppendLine($"<tr><td>Workers Idle</td><td>{tp.WorkersIdle}</td></tr>");
            if (tp.WorkerMinLimit.HasValue)
                sb.AppendLine($"<tr><td>Min Threads</td><td>{tp.WorkerMinLimit}</td></tr>");
            if (tp.WorkerMaxLimit.HasValue)
                sb.AppendLine($"<tr><td>Max Threads</td><td>{tp.WorkerMaxLimit:N0}</td></tr>");
            if (tp.IsPortableThreadPool == true)
                sb.AppendLine($"<tr><td>Thread Pool Type</td><td>Portable</td></tr>");
            
            sb.AppendLine("</tbody></table>");
            
            // Warnings
            if (tp.WorkersRunning == tp.WorkersTotal && tp.WorkersTotal > 0)
            {
                sb.AppendLine("<div class=\"alert alert-warning\">");
                sb.AppendLine("<strong>⚠️ Thread Pool Saturation:</strong> All worker threads are busy.");
                sb.AppendLine("</div>");
            }
            
            if (tp.CpuUtilization > 90)
            {
                sb.AppendLine("<div class=\"alert alert-warning\">");
                sb.AppendLine($"<strong>⚠️ High CPU Utilization ({tp.CpuUtilization}%):</strong> Consider profiling for CPU-bound operations.");
                sb.AppendLine("</div>");
            }
        }
        
        // Timer Information from Async.Timers
        var timers = analysis.Async?.Timers;
        if (timers != null && timers.Count > 0)
        {
            sb.AppendLine("<h3>⏱️ Active Timers</h3>");
            sb.AppendLine($"<p><strong>Total Active Timers:</strong> {timers.Count}</p>");
            
            // Group by state type
            var timerGroups = timers
                .GroupBy(t => t.StateType ?? "Unknown")
                .OrderByDescending(g => g.Count())
                .Take(10)
                .ToList();
            
            sb.AppendLine("<table class=\"data-table\">");
            sb.AppendLine("<thead><tr><th>State Type</th><th>Count</th><th>Due Time</th><th>Period</th></tr></thead>");
            sb.AppendLine("<tbody>");
            
            foreach (var group in timerGroups)
            {
                var sample = group.First();
                var count = group.Count();
                var dueTime = sample.DueTimeMs.HasValue ? $"{sample.DueTimeMs:N0}ms" : "-";
                var period = sample.PeriodMs.HasValue ? $"{sample.PeriodMs:N0}ms" : "one-shot";
                var stateType = group.Key;
                sb.AppendLine($"<tr><td><code>{HttpUtility.HtmlEncode(stateType)}</code></td><td>{count}</td><td>{dueTime}</td><td>{period}</td></tr>");
            }
            
            sb.AppendLine("</tbody></table>");
            
            // Warnings
            if (timers.Count > 50)
            {
                sb.AppendLine("<div class=\"alert alert-warning\">");
                sb.AppendLine($"<strong>⚠️ High Timer Count ({timers.Count}):</strong> May indicate timer leaks.");
                sb.AppendLine("</div>");
            }
            
            var shortTimers = timers.Where(t => t.PeriodMs.HasValue && t.PeriodMs < 100).ToList();
            if (shortTimers.Count > 0)
            {
                sb.AppendLine("<div class=\"alert alert-info\">");
                sb.AppendLine($"<strong>ℹ️ Short Timer Intervals:</strong> {shortTimers.Count} timer(s) have periods under 100ms.");
                sb.AppendLine("</div>");
            }
        }

        sb.AppendLine("</div>");
    }

    private static void AppendAsyncInfoFromHierarchyHtml(StringBuilder sb, AsyncInfo asyncInfo)
    {
        sb.AppendLine("<h2>⚡ Async Analysis</h2>");
        sb.AppendLine("<div class=\"card\">");
        
        if (asyncInfo.Summary != null)
        {
            sb.AppendLine("<h3>Task Summary</h3>");
            sb.AppendLine("<table class=\"data-table\">");
            sb.AppendLine("<thead><tr><th>Metric</th><th>Count</th></tr></thead>");
            sb.AppendLine("<tbody>");
            sb.AppendLine($"<tr><td>Total Tasks</td><td>{asyncInfo.Summary.TotalTasks}</td></tr>");
            if (asyncInfo.Summary.PendingTasks > 0)
                sb.AppendLine($"<tr><td>Pending</td><td>{asyncInfo.Summary.PendingTasks}</td></tr>");
            if (asyncInfo.Summary.CompletedTasks > 0)
                sb.AppendLine($"<tr><td>Completed</td><td>{asyncInfo.Summary.CompletedTasks}</td></tr>");
            if (asyncInfo.Summary.FaultedTasks > 0)
                sb.AppendLine($"<tr class=\"highlight-row\"><td>⚠️ Faulted</td><td>{asyncInfo.Summary.FaultedTasks}</td></tr>");
            if (asyncInfo.Summary.CanceledTasks > 0)
                sb.AppendLine($"<tr><td>Canceled</td><td>{asyncInfo.Summary.CanceledTasks}</td></tr>");
            sb.AppendLine("</tbody></table>");
        }
        
        if (asyncInfo.StateMachines?.Count > 0)
        {
            sb.AppendLine("<h3>Pending State Machines</h3>");
            sb.AppendLine("<table class=\"data-table\">");
            sb.AppendLine("<thead><tr><th>State Machine</th><th>State</th></tr></thead>");
            sb.AppendLine("<tbody>");
            foreach (var sm in asyncInfo.StateMachines.Take(10))
            {
                sb.AppendLine($"<tr><td><code>{HttpUtility.HtmlEncode(sm.StateMachineType ?? "?")}</code></td><td>{sm.CurrentState}</td></tr>");
            }
            if (asyncInfo.StateMachines.Count > 10)
            {
                sb.AppendLine($"<tr><td>...</td><td>+{asyncInfo.StateMachines.Count - 10} more</td></tr>");
            }
            sb.AppendLine("</tbody></table>");
        }
        
        if (asyncInfo.FaultedTasks?.Count > 0)
        {
            sb.AppendLine("<h3>Faulted Tasks</h3>");
            sb.AppendLine("<ul>");
            foreach (var task in asyncInfo.FaultedTasks.Take(5))
            {
                sb.AppendLine($"<li><strong>{HttpUtility.HtmlEncode(task.TaskType ?? "?")}</strong>: {HttpUtility.HtmlEncode(task.ExceptionType ?? "?")} - {HttpUtility.HtmlEncode(task.ExceptionMessage ?? "")}</li>");
            }
            sb.AppendLine("</ul>");
        }
        
        if (asyncInfo.AnalysisTimeMs.HasValue)
        {
            sb.AppendLine($"<p class=\"text-muted\">Analysis completed in {asyncInfo.AnalysisTimeMs}ms</p>");
            if (asyncInfo.WasAborted == true)
            {
                sb.AppendLine("<div class=\"alert alert-warning\">⚠️ Analysis was aborted due to timeout</div>");
            }
        }

        sb.AppendLine("</div>");
    }

    private static void AppendExceptionAnalysis(StringBuilder sb, ExceptionAnalysis exceptionAnalysis)
    {
        sb.AppendLine("<h2>🔎 Exception Deep Analysis</h2>");
        sb.AppendLine("<div class=\"card\">");
        
        // Basic exception info
        sb.AppendLine("<h3>Exception Details</h3>");
        sb.AppendLine("<table class=\"meta-table\">");
        
        if (!string.IsNullOrEmpty(exceptionAnalysis.FullTypeName))
            sb.AppendLine($"<tr><td>Type</td><td><code>{HttpUtility.HtmlEncode(exceptionAnalysis.FullTypeName)}</code></td></tr>");
        if (!string.IsNullOrEmpty(exceptionAnalysis.Message))
            sb.AppendLine($"<tr><td>Message</td><td>{HttpUtility.HtmlEncode(exceptionAnalysis.Message)}</td></tr>");
        if (!string.IsNullOrEmpty(exceptionAnalysis.HResult))
            sb.AppendLine($"<tr><td>HResult</td><td><code>{HttpUtility.HtmlEncode(exceptionAnalysis.HResult)}</code></td></tr>");
        if (!string.IsNullOrEmpty(exceptionAnalysis.ExceptionAddress))
            sb.AppendLine($"<tr><td>Address</td><td><code>0x{HttpUtility.HtmlEncode(exceptionAnalysis.ExceptionAddress)}</code></td></tr>");
        if (!string.IsNullOrEmpty(exceptionAnalysis.Source))
            sb.AppendLine($"<tr><td>Source</td><td>{HttpUtility.HtmlEncode(exceptionAnalysis.Source)}</td></tr>");
        
        sb.AppendLine("</table>");
        
        // Target Site
        if (exceptionAnalysis.TargetSite != null)
        {
            sb.AppendLine("<h3>Target Site (Where Exception Was Thrown)</h3>");
            sb.AppendLine("<ul>");
            sb.AppendLine($"<li><strong>Method:</strong> <code>{HttpUtility.HtmlEncode(exceptionAnalysis.TargetSite.Name ?? "?")}</code></li>");
            if (!string.IsNullOrEmpty(exceptionAnalysis.TargetSite.DeclaringType))
                sb.AppendLine($"<li><strong>Class:</strong> <code>{HttpUtility.HtmlEncode(exceptionAnalysis.TargetSite.DeclaringType)}</code></li>");
            if (!string.IsNullOrEmpty(exceptionAnalysis.TargetSite.Signature))
                sb.AppendLine($"<li><strong>Signature:</strong> <code>{HttpUtility.HtmlEncode(exceptionAnalysis.TargetSite.Signature)}</code></li>");
            sb.AppendLine("</ul>");
        }
        
        // Exception Chain
        if (exceptionAnalysis.ExceptionChain?.Any() == true)
        {
            sb.AppendLine("<h3>Exception Chain</h3>");
            sb.AppendLine("<div class=\"exception-chain\">");
            
            foreach (var entry in exceptionAnalysis.ExceptionChain)
            {
                var depthClass = entry.Depth == 0 ? "root-exception" : "inner-exception";
                var indent = entry.Depth * 20;
                
                sb.AppendLine($"<div class=\"{depthClass}\" style=\"margin-left: {indent}px; padding: 10px; border-left: 3px solid var(--accent); margin-bottom: 8px;\">");
                sb.AppendLine($"<strong>{HttpUtility.HtmlEncode(entry.Type ?? "?")}</strong>");
                if (!string.IsNullOrEmpty(entry.Message))
                    sb.AppendLine($"<p style=\"margin: 5px 0;\">{HttpUtility.HtmlEncode(entry.Message)}</p>");
                if (!string.IsNullOrEmpty(entry.HResult))
                    sb.AppendLine($"<span class=\"badge badge-info\">HResult: {HttpUtility.HtmlEncode(entry.HResult)}</span>");
                sb.AppendLine("</div>");
            }
            
            sb.AppendLine("</div>");
        }
        
        // Custom Properties
        if (exceptionAnalysis.CustomProperties?.Any() == true)
        {
            sb.AppendLine("<h3>Custom Properties</h3>");
            sb.AppendLine("<table class=\"data-table\">");
            sb.AppendLine("<thead><tr><th>Property</th><th>Value</th></tr></thead>");
            sb.AppendLine("<tbody>");
            
            foreach (var (key, value) in exceptionAnalysis.CustomProperties)
            {
                var displayValue = value?.ToString() ?? "<em>null</em>";
                if (displayValue.Length > 100)
                    displayValue = displayValue[..100] + "...";
                sb.AppendLine($"<tr><td><code>{HttpUtility.HtmlEncode(key)}</code></td><td>{HttpUtility.HtmlEncode(displayValue)}</td></tr>");
            }
            
            sb.AppendLine("</tbody></table>");
        }
        
        sb.AppendLine("</div>");
    }

    private static void AppendTypeResolutionAnalysis(StringBuilder sb, TypeResolutionAnalysis typeResolution)
    {
        sb.AppendLine("<h2>🔍 Type/Method Resolution Analysis</h2>");
        sb.AppendLine("<div class=\"card\">");
        
        // Failed Type Info
        if (!string.IsNullOrEmpty(typeResolution.FailedType))
        {
            sb.AppendLine($"<p><strong>Failed Type:</strong> <code>{HttpUtility.HtmlEncode(typeResolution.FailedType)}</code></p>");
            
            if (!string.IsNullOrEmpty(typeResolution.MethodTable) || !string.IsNullOrEmpty(typeResolution.EEClass))
            {
                sb.AppendLine("<ul>");
                if (!string.IsNullOrEmpty(typeResolution.MethodTable))
                    sb.AppendLine($"<li>MethodTable: <code>0x{HttpUtility.HtmlEncode(typeResolution.MethodTable)}</code></li>");
                if (!string.IsNullOrEmpty(typeResolution.EEClass))
                    sb.AppendLine($"<li>EEClass: <code>0x{HttpUtility.HtmlEncode(typeResolution.EEClass)}</code></li>");
                sb.AppendLine("</ul>");
            }
        }
        
        // Expected Member
        if (typeResolution.ExpectedMember != null)
        {
            sb.AppendLine("<h3>Expected Member (Not Found)</h3>");
            sb.AppendLine("<ul>");
            sb.AppendLine($"<li><strong>Name:</strong> <code>{HttpUtility.HtmlEncode(typeResolution.ExpectedMember.Name ?? "?")}</code></li>");
            if (!string.IsNullOrEmpty(typeResolution.ExpectedMember.Signature))
                sb.AppendLine($"<li><strong>Signature:</strong> <code>{HttpUtility.HtmlEncode(typeResolution.ExpectedMember.Signature)}</code></li>");
            if (!string.IsNullOrEmpty(typeResolution.ExpectedMember.MemberType))
                sb.AppendLine($"<li><strong>Member Type:</strong> {HttpUtility.HtmlEncode(typeResolution.ExpectedMember.MemberType)}</li>");
            sb.AppendLine("</ul>");
        }
        
        // Similar Methods
        if (typeResolution.SimilarMethods?.Any() == true)
        {
            sb.AppendLine("<h3>Similar Methods (Potential Matches)</h3>");
            sb.AppendLine("<p>These methods have similar names - check for signature mismatches:</p>");
            sb.AppendLine("<ul>");
            
            foreach (var method in typeResolution.SimilarMethods)
            {
                sb.AppendLine($"<li><code>{HttpUtility.HtmlEncode(method.Signature ?? method.Name ?? "?")}</code> <span class=\"badge badge-info\">{HttpUtility.HtmlEncode(method.JitStatus ?? "?")}</span></li>");
            }
            
            sb.AppendLine("</ul>");
        }
        
        // Diagnosis
        if (!string.IsNullOrEmpty(typeResolution.Diagnosis))
        {
            sb.AppendLine("<h3>Diagnosis</h3>");
            sb.AppendLine($"<div class=\"alert alert-info\">{HttpUtility.HtmlEncode(typeResolution.Diagnosis)}</div>");
        }
        
        // Actual Methods (collapsible)
        if (typeResolution.ActualMethods?.Any() == true)
        {
            sb.AppendLine("<details>");
            sb.AppendLine($"<summary>📋 All Methods on Type ({typeResolution.ActualMethods.Count} total)</summary>");
            sb.AppendLine("<table class=\"data-table compact\">");
            sb.AppendLine("<thead><tr><th>Method</th><th>JIT Status</th></tr></thead>");
            sb.AppendLine("<tbody>");
            
            foreach (var method in typeResolution.ActualMethods)
            {
                var sig = method.Signature ?? method.Name ?? "???";
                sb.AppendLine($"<tr><td><code>{HttpUtility.HtmlEncode(sig)}</code></td><td>{HttpUtility.HtmlEncode(method.JitStatus ?? "-")}</td></tr>");
            }
            
            sb.AppendLine("</tbody></table>");
            sb.AppendLine("</details>");
        }
        
        sb.AppendLine("</div>");
    }

    private static void AppendNativeAotAnalysis(StringBuilder sb, NativeAotAnalysis nativeAot)
    {
        sb.AppendLine("<h2>🚀 NativeAOT / Trimming Analysis</h2>");
        sb.AppendLine("<div class=\"card\">");
        
        // Status table
        var aotBadge = nativeAot.IsNativeAot 
            ? "<span class=\"badge badge-danger\">🔴 NativeAOT Detected</span>" 
            : "<span class=\"badge badge-success\">🟢 Not NativeAOT</span>";
        var jitBadge = nativeAot.HasJitCompiler 
            ? "<span class=\"badge badge-success\">✅ JIT Present</span>" 
            : "<span class=\"badge badge-warning\">❌ No JIT</span>";
        
        sb.AppendLine("<table class=\"meta-table\">");
        sb.AppendLine($"<tr><td>NativeAOT</td><td>{aotBadge}</td></tr>");
        sb.AppendLine($"<tr><td>JIT Compiler</td><td>{jitBadge}</td></tr>");
        sb.AppendLine("</table>");
        
        // Indicators
        if (nativeAot.Indicators?.Any() == true)
        {
            sb.AppendLine("<h3>Detection Indicators</h3>");
            
            foreach (var indicator in nativeAot.Indicators)
            {
                sb.AppendLine("<div style=\"padding: 8px; margin: 8px 0; background: var(--bg-secondary); border-radius: 4px;\">");
                sb.AppendLine($"<strong>Pattern:</strong> <code>{HttpUtility.HtmlEncode(indicator.Pattern)}</code><br>");
                if (!string.IsNullOrEmpty(indicator.MatchedValue))
                    sb.AppendLine($"<strong>Matched:</strong> <code>{HttpUtility.HtmlEncode(indicator.MatchedValue)}</code><br>");
                if (indicator.Frame != null)
                    sb.AppendLine($"<strong>Frame:</strong> <code>{HttpUtility.HtmlEncode(indicator.Frame.Function ?? "?")}</code>");
                sb.AppendLine("</div>");
            }
        }
        
        // Trimming Analysis
        if (nativeAot.TrimmingAnalysis != null)
        {
            var trimming = nativeAot.TrimmingAnalysis;
            
            var alertClass = trimming.Confidence switch
            {
                "high" => "alert-danger",
                "medium" => "alert-warning",
                _ => "alert-info"
            };
            
            sb.AppendLine("<h3>Trimming Analysis</h3>");
            
            var issueText = trimming.PotentialTrimmingIssue 
                ? $"<strong>Potential Trimming Issue Detected</strong> (Confidence: {trimming.Confidence})"
                : $"Possible version mismatch or configuration issue (Confidence: {trimming.Confidence})";
            
            sb.AppendLine($"<div class=\"alert {alertClass}\">{issueText}</div>");
            
            sb.AppendLine("<ul>");
            if (!string.IsNullOrEmpty(trimming.ExceptionType))
                sb.AppendLine($"<li><strong>Exception Type:</strong> <code>{HttpUtility.HtmlEncode(trimming.ExceptionType)}</code></li>");
            if (!string.IsNullOrEmpty(trimming.MissingMember))
                sb.AppendLine($"<li><strong>Missing Member:</strong> <code>{HttpUtility.HtmlEncode(trimming.MissingMember)}</code></li>");
            if (trimming.CallingFrame != null)
            {
                var funcName = HttpUtility.HtmlEncode(trimming.CallingFrame.Function ?? "?");
                if (!string.IsNullOrEmpty(trimming.CallingFrame.SourceUrl))
                {
                    sb.AppendLine($"<li><strong>Called From:</strong> <a href=\"{HttpUtility.HtmlEncode(trimming.CallingFrame.SourceUrl)}\" target=\"_blank\" class=\"source-link\">{funcName}</a></li>");
                }
                else
                {
                    sb.AppendLine($"<li><strong>Called From:</strong> <code>{funcName}</code></li>");
                }
            }
            sb.AppendLine("</ul>");
            
            if (!string.IsNullOrEmpty(trimming.Recommendation))
            {
                sb.AppendLine("<h4>Recommendations</h4>");
                sb.AppendLine($"<div class=\"code-block\">{HttpUtility.HtmlEncode(trimming.Recommendation)}</div>");
            }
        }
        
        // Reflection Usage
        if (nativeAot.ReflectionUsage?.Any() == true)
        {
            sb.AppendLine("<h3>Reflection Usage Patterns</h3>");
            sb.AppendLine("<p>The following reflection patterns were detected:</p>");
            sb.AppendLine("<table class=\"data-table\">");
            sb.AppendLine("<thead><tr><th>Location</th><th>Pattern</th><th>Risk</th></tr></thead>");
            sb.AppendLine("<tbody>");
            
            foreach (var usage in nativeAot.ReflectionUsage)
            {
                var riskClass = (usage.Risk ?? "").ToLowerInvariant() switch
                {
                    "high" => "badge-danger",
                    "medium" => "badge-warning",
                    _ => "badge-info"
                };
                sb.AppendLine($"<tr><td><code>{HttpUtility.HtmlEncode(usage.Location ?? "-")}</code></td><td>{HttpUtility.HtmlEncode(usage.Pattern ?? "-")}</td><td><span class=\"badge {riskClass}\">{HttpUtility.HtmlEncode(usage.Risk ?? "-")}</span></td></tr>");
            }
            
            sb.AppendLine("</tbody></table>");
        }
        
        sb.AppendLine("</div>");
    }

    private static void AppendAssemblyVersions(StringBuilder sb, List<AssemblyVersionInfo> assemblies)
    {
        sb.AppendLine("<h2>📦 Assembly Versions</h2>");
        sb.AppendLine("<div class=\"card\">");
        
        if (!assemblies.Any())
        {
            sb.AppendLine("<p class=\"dim\">No assembly information available.</p>");
            sb.AppendLine("</div>");
            return;
        }
        
        sb.AppendLine("<details>");
        sb.AppendLine($"<summary>View all {assemblies.Count} assemblies</summary>");
        sb.AppendLine("<table class=\"data-table\">");
        sb.AppendLine("<thead><tr><th>Assembly</th><th>Version</th><th>Info Version</th><th>Config</th><th>Company</th><th>Repository</th></tr></thead>");
        sb.AppendLine("<tbody>");
        
        foreach (var asm in assemblies)
        {
            var name = asm.Name;
            if (name.Length > 50)
                name = name[..50] + "...";
            var asmVersion = HttpUtility.HtmlEncode(asm.AssemblyVersion ?? "-");
            var dynamicBadge = asm.IsDynamic == true ? " <span class=\"badge badge-info\">🔄 Dynamic</span>" : "";
            
            // Build info version with optional commit link
            var infoVersion = HttpUtility.HtmlEncode(asm.InformationalVersion ?? "-");
            if (!string.IsNullOrEmpty(asm.CommitHash) && !string.IsNullOrEmpty(asm.RepositoryUrl))
            {
                var shortHash = asm.CommitHash.Length > 7 ? asm.CommitHash[..7] : asm.CommitHash;
                // URL-encode commit hash for href, HTML-encode for display text
                infoVersion += $" <a href=\"{HttpUtility.HtmlEncode(asm.RepositoryUrl)}/commit/{HttpUtility.UrlEncode(asm.CommitHash)}\" target=\"_blank\" class=\"commit-link\">{HttpUtility.HtmlEncode(shortHash)}</a>";
            }
            
            // Repository link
            var repoLink = !string.IsNullOrEmpty(asm.RepositoryUrl)
                ? $"<a href=\"{HttpUtility.HtmlEncode(asm.RepositoryUrl)}\" target=\"_blank\">🔗</a>" : "-";
            
            // Configuration badge
            var config = HttpUtility.HtmlEncode(asm.Configuration ?? "-");
            
            // Company
            var company = asm.Company ?? "-";
            if (company.Length > 25)
                company = company[..25] + "...";
            company = HttpUtility.HtmlEncode(company);
            
            sb.AppendLine($"<tr><td><code>{HttpUtility.HtmlEncode(name)}</code>{dynamicBadge}</td><td>{asmVersion}</td><td>{infoVersion}</td><td><span class=\"badge\">{config}</span></td><td>{company}</td><td>{repoLink}</td></tr>");
        }
        
        sb.AppendLine("</tbody></table>");
        sb.AppendLine("</details>");
        sb.AppendLine("</div>");
    }

    private static void AppendDeadlockInfo(StringBuilder sb, DeadlockInfo deadlockInfo)
    {
        sb.AppendLine("<h2>🔒 Deadlock Detection</h2>");
        sb.AppendLine("<div class=\"alert alert-error\">");
        sb.AppendLine("<strong>🔴 DEADLOCK DETECTED</strong>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"card\">");

        if (deadlockInfo.InvolvedThreads?.Any() == true)
        {
            sb.AppendLine("<h3>Involved Threads</h3>");
            sb.AppendLine("<ul>");
            foreach (var thread in deadlockInfo.InvolvedThreads)
            {
                sb.AppendLine($"<li>Thread {HttpUtility.HtmlEncode(thread)}</li>");
            }
            sb.AppendLine("</ul>");
        }

        sb.AppendLine("</div>");
    }

    private static void AppendWatchResults(StringBuilder sb, WatchEvaluationReport watchResults)
    {
        sb.AppendLine("<h2>📌 Watch Expression Results</h2>");
        sb.AppendLine("<div class=\"card\">");
        sb.AppendLine($"<p><strong>Total:</strong> {watchResults.TotalWatches} | ");
        sb.AppendLine($"<span class=\"badge badge-success\">✓ {watchResults.SuccessfulEvaluations}</span> ");
        sb.AppendLine($"<span class=\"badge badge-error\">✗ {watchResults.FailedEvaluations}</span></p>");

        foreach (var watch in watchResults.Watches)
        {
            var statusClass = watch.Success ? "badge-success" : "badge-error";
            var status = watch.Success ? "✓" : "✗";
            
            sb.AppendLine("<div class=\"watch-item\">");
            sb.AppendLine($"<span class=\"badge {statusClass}\">{status}</span> ");
            sb.AppendLine($"<span class=\"watch-expr\">{HttpUtility.HtmlEncode(watch.Expression)}</span>");
            
            if (!string.IsNullOrEmpty(watch.Description))
            {
                sb.AppendLine($"<div class=\"watch-desc\">{HttpUtility.HtmlEncode(watch.Description)}</div>");
            }

            if (watch.Success && !string.IsNullOrEmpty(watch.Value))
            {
                var value = watch.Value.Length > 500 ? watch.Value.Substring(0, 500) + "..." : watch.Value;
                sb.AppendLine($"<div class=\"watch-value\">{HttpUtility.HtmlEncode(value)}</div>");
            }
            else if (!string.IsNullOrEmpty(watch.Error))
            {
                sb.AppendLine($"<div class=\"alert alert-error\">{HttpUtility.HtmlEncode(watch.Error)}</div>");
            }
            sb.AppendLine("</div>");
        }

        if (watchResults.Insights.Any())
        {
            sb.AppendLine("<h3>Insights</h3>");
            foreach (var insight in watchResults.Insights)
            {
                sb.AppendLine($"<div class=\"recommendation\">{HttpUtility.HtmlEncode(insight)}</div>");
            }
        }

        sb.AppendLine("</div>");
    }

    private static void AppendSecurityInfo(StringBuilder sb, SecurityInfo security)
    {
        sb.AppendLine("<h2>🔒 Security Analysis</h2>");
        
        // Risk Level Alert
        var riskClass = security.OverallRisk?.ToLowerInvariant() switch
        {
            "critical" => "alert-danger",
            "high" => "alert-warning",
            "medium" or "low" => "alert-success",
            _ => "alert-info"
        };
        
        sb.AppendLine($"<div class=\"alert {riskClass}\">");
        sb.AppendLine($"<strong>Overall Risk: {HttpUtility.HtmlEncode(security.OverallRisk ?? "Unknown")}</strong>");
        if (!string.IsNullOrEmpty(security.Summary))
        {
            sb.AppendLine($"<p>{HttpUtility.HtmlEncode(security.Summary)}</p>");
        }
        sb.AppendLine("</div>");

        // Findings Table
        if (security.Findings?.Any() == true)
        {
            sb.AppendLine("<h3>Detected Vulnerabilities</h3>");
            sb.AppendLine("<table><thead><tr><th>Severity</th><th>Type</th><th>Description</th></tr></thead><tbody>");
            foreach (var finding in security.Findings.OrderByDescending(f => f.Severity))
            {
                sb.AppendLine($"<tr><td>{HttpUtility.HtmlEncode(finding.Severity ?? "")}</td>");
                sb.AppendLine($"<td>{HttpUtility.HtmlEncode(finding.Type ?? "")}</td>");
                sb.AppendLine($"<td>{HttpUtility.HtmlEncode(finding.Description ?? "")}</td></tr>");
            }
            sb.AppendLine("</tbody></table>");
        }

        // Recommendations
        if (security.Recommendations?.Any() == true)
        {
            sb.AppendLine("<h3>Recommendations</h3>");
            sb.AppendLine("<ul>");
            foreach (var rec in security.Recommendations)
            {
                sb.AppendLine($"<li>{HttpUtility.HtmlEncode(rec)}</li>");
            }
            sb.AppendLine("</ul>");
        }
    }

    private static void AppendSecurityAnalysis(StringBuilder sb, SecurityAnalysisResult security)
    {
        sb.AppendLine("<h2>🔒 Security Analysis</h2>");
        
        // Risk Level Alert
        var (riskClass, riskEmoji) = security.OverallRisk switch
        {
            SecurityRisk.Critical => ("alert-danger", "🔴 CRITICAL RISK"),
            SecurityRisk.High => ("alert-warning", "🟠 HIGH RISK"),
            SecurityRisk.Medium => ("alert-info", "🟡 MEDIUM RISK"),
            SecurityRisk.Low => ("alert-success", "🟢 LOW RISK"),
            _ => ("alert-success", "⚪ NO RISK DETECTED")
        };

        sb.AppendLine($"<div class=\"alert {riskClass}\">");
        sb.AppendLine($"<strong>{riskEmoji}</strong> - {HttpUtility.HtmlEncode(security.Summary)}");
        sb.AppendLine("</div>");

        // Vulnerabilities
        if (security.Vulnerabilities.Any())
        {
            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine("<h3>Detected Vulnerabilities</h3>");
            sb.AppendLine("<table>");
            sb.AppendLine("<thead><tr><th>Severity</th><th>Type</th><th>Description</th><th>CWE</th></tr></thead>");
            sb.AppendLine("<tbody>");

            foreach (var vuln in security.Vulnerabilities.OrderByDescending(v => v.Severity))
            {
                var badgeClass = vuln.Severity switch
                {
                    VulnerabilitySeverity.Critical => "badge-danger",
                    VulnerabilitySeverity.High => "badge-warning",
                    VulnerabilitySeverity.Medium => "badge-info",
                    _ => "badge-success"
                };
                var severityText = vuln.Severity.ToString().ToUpper();
                var cweLinks = vuln.CweIds.Any()
                    ? string.Join(", ", vuln.CweIds.Select(c => 
                        $"<a href=\"https://cwe.mitre.org/data/definitions/{c.Replace("CWE-", "")}.html\" target=\"_blank\">{HttpUtility.HtmlEncode(c)}</a>"))
                    : "-";
                
                sb.AppendLine($"<tr>");
                sb.AppendLine($"<td><span class=\"badge {badgeClass}\">{severityText}</span></td>");
                sb.AppendLine($"<td>{HttpUtility.HtmlEncode(vuln.Type.ToString())}</td>");
                sb.AppendLine($"<td>{HttpUtility.HtmlEncode(vuln.Description)}</td>");
                sb.AppendLine($"<td>{cweLinks}</td>");
                sb.AppendLine($"</tr>");
            }

            sb.AppendLine("</tbody>");
            sb.AppendLine("</table>");
            sb.AppendLine("</div>");

            // Critical vulnerability details
            var criticalVulns = security.Vulnerabilities.Where(v => v.Severity >= VulnerabilitySeverity.High).ToList();
            if (criticalVulns.Any())
            {
                sb.AppendLine("<div class=\"card\">");
                sb.AppendLine("<h3>Critical/High Severity Details</h3>");

                foreach (var vuln in criticalVulns)
                {
                    sb.AppendLine("<div class=\"vuln-detail\">");
                    sb.AppendLine($"<h4>{HttpUtility.HtmlEncode(vuln.Type.ToString())}</h4>");
                    sb.AppendLine($"<p>{HttpUtility.HtmlEncode(vuln.Description)}</p>");
                    
                    if (!string.IsNullOrEmpty(vuln.Address))
                    {
                        sb.AppendLine($"<p><strong>Address:</strong> <code>{HttpUtility.HtmlEncode(vuln.Address)}</code></p>");
                    }
                    if (vuln.Indicators.Any())
                    {
                        sb.AppendLine($"<p><strong>Indicators:</strong> {HttpUtility.HtmlEncode(string.Join(", ", vuln.Indicators))}</p>");
                    }
                    if (vuln.Remediation.Any())
                    {
                        sb.AppendLine("<p><strong>Remediation:</strong></p><ul>");
                        foreach (var step in vuln.Remediation)
                        {
                            sb.AppendLine($"<li>{HttpUtility.HtmlEncode(step)}</li>");
                        }
                        sb.AppendLine("</ul>");
                    }
                    sb.AppendLine("</div>");
                }
                sb.AppendLine("</div>");
            }
        }

        // Memory Protections
        if (security.MemoryProtections != null)
        {
            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine("<h3>Memory Protection Status</h3>");
            sb.AppendLine("<table class=\"compact-table\">");
            sb.AppendLine("<tbody>");
            sb.AppendLine($"<tr><td>ASLR</td><td>{(security.MemoryProtections.AslrEnabled ? "<span class=\"badge badge-success\">✅ Enabled</span>" : "<span class=\"badge badge-danger\">❌ Disabled</span>")}</td></tr>");
            sb.AppendLine($"<tr><td>DEP/NX</td><td>{(security.MemoryProtections.DepEnabled ? "<span class=\"badge badge-success\">✅ Enabled</span>" : "<span class=\"badge badge-danger\">❌ Disabled</span>")}</td></tr>");
            sb.AppendLine($"<tr><td>Stack Canaries</td><td>{(security.MemoryProtections.StackCanariesPresent ? "<span class=\"badge badge-success\">✅ Present</span>" : "<span class=\"badge badge-info\">⚠️ Unknown</span>")}</td></tr>");
            sb.AppendLine("</tbody>");
            sb.AppendLine("</table>");
            
            if (security.MemoryProtections.ModulesWithoutAslr.Any())
            {
                sb.AppendLine($"<p class=\"warning\"><strong>⚠️ Modules without ASLR:</strong> {HttpUtility.HtmlEncode(string.Join(", ", security.MemoryProtections.ModulesWithoutAslr.Take(5)))}</p>");
            }
            sb.AppendLine("</div>");
        }

        // Security Recommendations
        if (security.Recommendations.Any())
        {
            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine("<h3>Security Recommendations</h3>");
            sb.AppendLine("<ul class=\"recommendation-list\">");
            foreach (var rec in security.Recommendations)
            {
                sb.AppendLine($"<li>{HttpUtility.HtmlEncode(rec)}</li>");
            }
            sb.AppendLine("</ul>");
            sb.AppendLine("</div>");
        }
    }

    private static void AppendModules(StringBuilder sb, CrashAnalysisResult analysis, ReportOptions options)
    {
        if (analysis.Modules == null) return;
        
        sb.AppendLine("<h2>📦 Loaded Modules</h2>");
        sb.AppendLine("<div class=\"card\">");

        var modules = analysis.Modules.AsEnumerable();
        if (options.MaxModulesToShow > 0 && analysis.Modules.Count > options.MaxModulesToShow)
        {
            modules = modules.Take(options.MaxModulesToShow);
            sb.AppendLine($"<p><em>Showing {options.MaxModulesToShow} of {analysis.Modules.Count} modules</em></p>");
        }

        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Module</th><th>Base Address</th><th>Symbols</th></tr>");
        foreach (var module in modules)
        {
            var symbolBadge = module.HasSymbols 
                ? "<span class=\"badge badge-success\">✓</span>" 
                : "<span class=\"badge badge-error\">✗</span>";
            sb.AppendLine($"<tr><td>{HttpUtility.HtmlEncode(module.Name)}</td><td><code>{HttpUtility.HtmlEncode(module.BaseAddress ?? "-")}</code></td><td>{symbolBadge}</td></tr>");
        }
        sb.AppendLine("</table>");
        sb.AppendLine("</div>");
    }

    private static void AppendProcessInfo(StringBuilder sb, ProcessInfo process, ReportOptions options)
    {
        sb.AppendLine("<h2>🖥️ Process Information</h2>");
        sb.AppendLine("<div class=\"card\">");

        // Command-line arguments
        if (process.Arguments.Any())
        {
            sb.AppendLine("<h3>Command Line Arguments</h3>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>#</th><th>Argument</th></tr>");
            
            for (var i = 0; i < process.Arguments.Count; i++)
            {
                var arg = HttpUtility.HtmlEncode(process.Arguments[i]);
                // Truncate very long arguments
                if (arg.Length > 200)
                {
                    arg = arg[..200] + "...";
                }
                sb.AppendLine($"<tr><td>{i}</td><td><code>{arg}</code></td></tr>");
            }
            sb.AppendLine("</table>");
        }

        // Environment variables
        if (process.EnvironmentVariables.Any())
        {
            var envVars = process.EnvironmentVariables;
            var maxEnvVars = options.MaxEnvironmentVariables > 0 
                ? options.MaxEnvironmentVariables 
                : envVars.Count;
            
            sb.AppendLine($"<h3>Environment Variables ({envVars.Count} total)</h3>");
            
            if (maxEnvVars < envVars.Count)
            {
                sb.AppendLine($"<p class=\"dim\">Showing {maxEnvVars} of {envVars.Count} environment variables</p>");
            }
            
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Variable</th><th>Value</th></tr>");
            
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
                
                // HTML encode
                name = HttpUtility.HtmlEncode(name);
                value = HttpUtility.HtmlEncode(value);
                if (value.Length > 100)
                {
                    value = value[..100] + "...";
                }
                
                sb.AppendLine($"<tr><td><code>{name}</code></td><td>{value}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        // Metadata
        if (process.Argc.HasValue || !string.IsNullOrEmpty(process.ArgvAddress) || process.SensitiveDataFiltered == true)
        {
            sb.AppendLine("<h3>Extraction Metadata</h3>");
            sb.AppendLine("<ul>");
            if (process.Argc.HasValue)
            {
                sb.AppendLine($"<li><strong>argc</strong>: {process.Argc}</li>");
            }
            if (!string.IsNullOrEmpty(process.ArgvAddress))
            {
                sb.AppendLine($"<li><strong>argv address</strong>: <code>{HttpUtility.HtmlEncode(process.ArgvAddress)}</code></li>");
            }
            if (process.SensitiveDataFiltered == true)
            {
                sb.AppendLine("<li><strong>Note</strong>: ⚠️ Sensitive environment variables were filtered</li>");
            }
            sb.AppendLine("</ul>");
        }

        sb.AppendLine("</div>");
    }

    private static void AppendRecommendations(StringBuilder sb, CrashAnalysisResult analysis)
    {
        sb.AppendLine("<h2>💡 Recommendations</h2>");
        sb.AppendLine("<div class=\"card\">");
        // Use new structure if available
        var recommendations = analysis.Summary?.Recommendations ?? new List<string>();
        foreach (var rec in recommendations)
        {
            sb.AppendLine($"<div class=\"recommendation\">{HttpUtility.HtmlEncode(rec)}</div>");
        }
        sb.AppendLine("</div>");
    }

    private static void AppendRawOutput(StringBuilder sb, CrashAnalysisResult analysis)
    {
        sb.AppendLine("<h2>📝 Raw Debugger Output</h2>");
        sb.AppendLine("<div class=\"card\">");
        sb.AppendLine("<p class=\"dim\">Raw output from debugger commands (for advanced troubleshooting)</p>");
        
        // Use new structure if available
        var rawOutput = analysis.RawCommands ?? new Dictionary<string, string>();
        foreach (var (command, output) in rawOutput)
        {
            sb.AppendLine($"<h3><code>{HttpUtility.HtmlEncode(command)}</code></h3>");
            sb.AppendLine($"<pre class=\"code-block\">{HttpUtility.HtmlEncode(output)}</pre>");
        }
        
        sb.AppendLine("</div>");
    }

    private static void AppendFooter(StringBuilder sb, ReportMetadata metadata)
    {
        sb.AppendLine("<footer>");
        sb.AppendLine($"<p>Report generated by <strong>Debugger MCP Server</strong> v{metadata.ServerVersion}</p>");
        sb.AppendLine($"<p>{metadata.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC</p>");
        sb.AppendLine("</footer>");
    }

    private static string TruncateString(string value, int maxLength = 0)
    {
        // Don't truncate by default - show full values
        if (string.IsNullOrEmpty(value))
            return value ?? "";
        // Only truncate if maxLength > 0 and value exceeds it
        if (maxLength > 0 && value.Length > maxLength)
            return value.Substring(0, maxLength - 3) + "...";
        return value;
    }

    /// <summary>
    /// Cleans up debugger-specific markers from source info.
    /// Removes markers like [opt], [inlined], etc. that come from LLDB/debugger output.
    /// </summary>
    private static string CleanSourceInfo(string source)
    {
        if (string.IsNullOrEmpty(source))
            return source;

        // Remove common debugger markers
        var result = source;
        
        // Remove [opt] - optimization marker
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s*\[opt\]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Remove [inlined] marker
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s*\[inlined\]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Remove trailing whitespace
        result = result.Trim();
        
        // Remove surrounding brackets if present
        if (result.StartsWith("[") && result.EndsWith("]"))
        {
            result = result.Substring(1, result.Length - 2);
        }
        
        return result;
    }
    
    // === Phase 2 ClrMD Enrichment Methods ===
    
    private static void AppendGcSummaryHtml(StringBuilder sb, GcSummary gc)
    {
        sb.AppendLine("<h2>🗑️ GC Heap Summary (ClrMD)</h2>");
        sb.AppendLine("<div class=\"card\">");
        
        // Summary cards
        sb.AppendLine("<div class=\"summary-cards\" style=\"display:flex;gap:1rem;flex-wrap:wrap;margin-bottom:1rem;\">");
        sb.AppendLine($"<div style=\"background:#f0f0f0;padding:0.5rem 1rem;border-radius:4px;\"><strong>Mode:</strong> {HttpUtility.HtmlEncode(gc.GcMode)}</div>");
        sb.AppendLine($"<div style=\"background:#f0f0f0;padding:0.5rem 1rem;border-radius:4px;\"><strong>Heaps:</strong> {gc.HeapCount}</div>");
        sb.AppendLine($"<div style=\"background:#f0f0f0;padding:0.5rem 1rem;border-radius:4px;\"><strong>Total:</strong> {FormatBytesCompact(gc.TotalHeapSize)}</div>");
        if (gc.Fragmentation.HasValue)
        {
            var fragColor = gc.Fragmentation.Value > 0.3 ? "#f44336" : gc.Fragmentation.Value > 0.15 ? "#ff9800" : "#4CAF50";
            sb.AppendLine($"<div style=\"background:#f0f0f0;padding:0.5rem 1rem;border-radius:4px;\"><strong>Fragmentation:</strong> <span style=\"color:{fragColor}\">{gc.Fragmentation.Value:P1}</span></div>");
        }
        sb.AppendLine($"<div style=\"background:#f0f0f0;padding:0.5rem 1rem;border-radius:4px;\"><strong>Finalizable:</strong> {gc.FinalizableObjectCount:N0}</div>");
        sb.AppendLine("</div>");
        
        // Generation sizes as bar chart
        if (gc.GenerationSizes != null)
        {
            var total = gc.GenerationSizes.Gen0 + gc.GenerationSizes.Gen1 + 
                        gc.GenerationSizes.Gen2 + gc.GenerationSizes.Loh + gc.GenerationSizes.Poh;
            if (total > 0)
            {
                sb.AppendLine("<h3>Generation Sizes</h3>");
                sb.AppendLine("<div style=\"display:flex;flex-direction:column;gap:0.5rem;\">");
                AppendGenBarHtml(sb, "Gen0", gc.GenerationSizes.Gen0, total, "#4CAF50");
                AppendGenBarHtml(sb, "Gen1", gc.GenerationSizes.Gen1, total, "#2196F3");
                AppendGenBarHtml(sb, "Gen2", gc.GenerationSizes.Gen2, total, "#FF9800");
                AppendGenBarHtml(sb, "LOH", gc.GenerationSizes.Loh, total, "#9C27B0");
                AppendGenBarHtml(sb, "POH", gc.GenerationSizes.Poh, total, "#607D8B");
                sb.AppendLine("</div>");
            }
        }
        
        sb.AppendLine("</div>");
    }
    
    private static void AppendGenBarHtml(StringBuilder sb, string name, long size, long total, string color)
    {
        var pct = total > 0 ? (size * 100.0 / total) : 0;
        sb.AppendLine($"<div style=\"display:flex;align-items:center;gap:0.5rem;\">");
        sb.AppendLine($"  <span style=\"width:40px;\">{name}</span>");
        sb.AppendLine($"  <div style=\"flex:1;background:#eee;border-radius:4px;height:20px;\">");
        sb.AppendLine($"    <div style=\"width:{pct:F1}%;background:{color};height:100%;border-radius:4px;\"></div>");
        sb.AppendLine($"  </div>");
        sb.AppendLine($"  <span style=\"width:80px;text-align:right;\">{FormatBytesCompact(size)}</span>");
        sb.AppendLine($"</div>");
    }
    
    private static void AppendTopMemoryConsumersHtml(StringBuilder sb, TopMemoryConsumers mem)
    {
        sb.AppendLine("<h2>📊 Top Memory Consumers (ClrMD)</h2>");
        sb.AppendLine("<div class=\"card\">");
        
        if (mem.Summary != null)
        {
            sb.AppendLine($"<p><em>{mem.Summary.TotalObjects:N0} objects, {FormatBytesCompact(mem.Summary.TotalSize)}, {mem.Summary.UniqueTypes:N0} unique types</em></p>");
            sb.AppendLine($"<p class=\"dim\">Analysis time: {mem.Summary.AnalysisTimeMs:N0}ms</p>");
            if (mem.Summary.WasAborted)
                sb.AppendLine("<p class=\"warning\">⚠️ Analysis was aborted due to timeout</p>");
        }
        
        if (mem.BySize?.Count > 0)
        {
            sb.AppendLine("<h3>By Total Size</h3>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Type</th><th>Count</th><th>Total Size</th><th>%</th></tr>");
            foreach (var item in mem.BySize.Take(15))
            {
                sb.AppendLine($"<tr><td><code>{HttpUtility.HtmlEncode(item.Type)}</code></td><td>{item.Count:N0}</td><td>{FormatBytesCompact(item.TotalSize)}</td><td>{item.Percentage:F1}%</td></tr>");
            }
            sb.AppendLine("</table>");
        }
        
        if (mem.LargeObjects?.Count > 0)
        {
            sb.AppendLine("<h3>Large Objects (&gt;85KB)</h3>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Address</th><th>Type</th><th>Size</th></tr>");
            foreach (var obj in mem.LargeObjects.Take(10))
            {
                sb.AppendLine($"<tr><td><code>{HttpUtility.HtmlEncode(obj.Address)}</code></td><td>{HttpUtility.HtmlEncode(obj.Type)}</td><td>{FormatBytesCompact(obj.Size)}</td></tr>");
            }
            sb.AppendLine("</table>");
        }
        
        sb.AppendLine("</div>");
    }
    
    private static void AppendAsyncAnalysisHtml(StringBuilder sb, AsyncAnalysis async)
    {
        sb.AppendLine("<h2>⏳ Async/Task Analysis (ClrMD)</h2>");
        sb.AppendLine("<div class=\"card\">");
        
        if (async.Summary != null)
        {
            sb.AppendLine("<table class=\"compact-table\">");
            sb.AppendLine("<tbody>");
            sb.AppendLine($"<tr><td>Total Tasks</td><td>{async.Summary.TotalTasks:N0}</td></tr>");
            sb.AppendLine($"<tr><td>Completed</td><td>{async.Summary.CompletedTasks:N0}</td></tr>");
            sb.AppendLine($"<tr><td>Pending</td><td>{async.Summary.PendingTasks:N0}</td></tr>");
            sb.AppendLine($"<tr><td>Faulted</td><td><span class=\"{(async.Summary.FaultedTasks > 0 ? "badge badge-error" : "")}\">{async.Summary.FaultedTasks:N0}</span></td></tr>");
            sb.AppendLine($"<tr><td>Canceled</td><td>{async.Summary.CanceledTasks:N0}</td></tr>");
            sb.AppendLine("</tbody>");
            sb.AppendLine("</table>");
        }
        
        sb.AppendLine($"<p class=\"dim\">Analysis time: {async.AnalysisTimeMs:N0}ms</p>");
        if (async.WasAborted)
            sb.AppendLine("<p class=\"warning\">⚠️ Analysis was aborted due to timeout</p>");
        
        if (async.FaultedTasks?.Count > 0)
        {
            sb.AppendLine("<h3>🔥 Faulted Tasks</h3>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Address</th><th>Type</th><th>Exception</th></tr>");
            foreach (var task in async.FaultedTasks.Take(10))
            {
                var exInfo = !string.IsNullOrEmpty(task.ExceptionType) 
                    ? $"{HttpUtility.HtmlEncode(task.ExceptionType)}" 
                    : "-";
                sb.AppendLine($"<tr><td><code>{HttpUtility.HtmlEncode(task.Address)}</code></td><td>{HttpUtility.HtmlEncode(task.TaskType)}</td><td>{exInfo}</td></tr>");
            }
            sb.AppendLine("</table>");
        }
        
        if (async.PendingStateMachines?.Count > 0)
        {
            sb.AppendLine("<h3>🔄 Pending State Machines</h3>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Address</th><th>Type</th><th>State</th></tr>");
            foreach (var sm in async.PendingStateMachines.Take(20))
            {
                var stateDesc = sm.CurrentState switch
                {
                    -1 => "-1 (not started)",
                    -2 => "-2 (completed)",
                    >= 0 => $"{sm.CurrentState} (await #{sm.CurrentState})",
                    _ => sm.CurrentState.ToString()
                };
                sb.AppendLine($"<tr><td><code>{HttpUtility.HtmlEncode(sm.Address)}</code></td><td>{HttpUtility.HtmlEncode(sm.StateMachineType)}</td><td>{HttpUtility.HtmlEncode(stateDesc)}</td></tr>");
            }
            sb.AppendLine("</table>");
        }
        
        sb.AppendLine("</div>");
    }
    
    private static void AppendStringAnalysisHtml(StringBuilder sb, StringAnalysis str)
    {
        sb.AppendLine("<h2>📝 String Duplicate Analysis (ClrMD)</h2>");
        sb.AppendLine("<div class=\"card\">");
        
        if (str.Summary != null)
        {
            sb.AppendLine("<table class=\"compact-table\">");
            sb.AppendLine("<tbody>");
            sb.AppendLine($"<tr><td>Total Strings</td><td>{str.Summary.TotalStrings:N0}</td></tr>");
            sb.AppendLine($"<tr><td>Unique Strings</td><td>{str.Summary.UniqueStrings:N0}</td></tr>");
            sb.AppendLine($"<tr><td>Duplicates</td><td>{str.Summary.DuplicateStrings:N0}</td></tr>");
            sb.AppendLine($"<tr><td>Total Size</td><td>{FormatBytesCompact(str.Summary.TotalSize)}</td></tr>");
            sb.AppendLine($"<tr><td>Wasted Size</td><td><strong>{FormatBytesCompact(str.Summary.WastedSize)}</strong> ({str.Summary.WastedPercentage:F1}%)</td></tr>");
            sb.AppendLine("</tbody>");
            sb.AppendLine("</table>");
        }
        
        sb.AppendLine($"<p class=\"dim\">Analysis time: {str.AnalysisTimeMs:N0}ms</p>");
        if (str.WasAborted)
            sb.AppendLine("<p class=\"warning\">⚠️ Analysis was aborted due to timeout</p>");
        
        if (str.ByLength != null)
        {
            sb.AppendLine("<h3>Length Distribution</h3>");
            sb.AppendLine("<table class=\"compact-table\">");
            sb.AppendLine("<tbody>");
            sb.AppendLine($"<tr><td>Empty (0)</td><td>{str.ByLength.Empty:N0}</td></tr>");
            sb.AppendLine($"<tr><td>Short (1-10)</td><td>{str.ByLength.Short:N0}</td></tr>");
            sb.AppendLine($"<tr><td>Medium (11-100)</td><td>{str.ByLength.Medium:N0}</td></tr>");
            sb.AppendLine($"<tr><td>Long (101-1000)</td><td>{str.ByLength.Long:N0}</td></tr>");
            sb.AppendLine($"<tr><td>Very Long (&gt;1000)</td><td>{str.ByLength.VeryLong:N0}</td></tr>");
            sb.AppendLine("</tbody>");
            sb.AppendLine("</table>");
        }
        
        if (str.TopDuplicates?.Count > 0)
        {
            sb.AppendLine("<h3>Top Duplicates (by wasted bytes)</h3>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Value</th><th>Count</th><th>Wasted</th><th>Suggestion</th></tr>");
            foreach (var dup in str.TopDuplicates.Take(15))
            {
                var displayValue = dup.Value.Length > 40 
                    ? HttpUtility.HtmlEncode(dup.Value[..40]) + "..." 
                    : HttpUtility.HtmlEncode(dup.Value);
                sb.AppendLine($"<tr><td><code>{displayValue}</code></td><td>{dup.Count:N0}</td><td>{FormatBytesCompact(dup.WastedBytes)}</td><td class=\"dim\">{HttpUtility.HtmlEncode(dup.Suggestion ?? "")}</td></tr>");
            }
            sb.AppendLine("</table>");
        }
        
        sb.AppendLine("</div>");
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
}

