using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace DebuggerMcp.Reporting;

/// <summary>
/// Renders the canonical JSON report document into a human-readable HTML report.
/// </summary>
/// <remarks>
/// The JSON report is treated as the source of truth; this renderer renders from JSON to avoid drift.
/// The output may include raw JSON detail blocks when <see cref="ReportOptions.IncludeRawJsonDetails"/> is enabled.
/// </remarks>
internal static class JsonHtmlReportRenderer
{
    internal static string Render(string reportJson, ReportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var doc = JsonDocument.Parse(reportJson);
        var root = doc.RootElement;
        var includeJsonDetails = options.IncludeRawJsonDetails;

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("<title>Debugger MCP Report</title>");
        sb.AppendLine("<link rel=\"preconnect\" href=\"https://cdnjs.cloudflare.com\">");
        sb.AppendLine("<link rel=\"stylesheet\" href=\"https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.11.1/styles/github-dark.min.css\">");
        sb.AppendLine("<style>");
        sb.AppendLine(GetCss());
        sb.AppendLine("</style>");
        sb.AppendLine("<script src=\"https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.11.1/highlight.min.js\" defer></script>");
        sb.AppendLine("<script defer>");
        sb.AppendLine("document.addEventListener('DOMContentLoaded', () => {");
        sb.AppendLine("  try { if (window.hljs) { document.querySelectorAll('pre code').forEach((el) => window.hljs.highlightElement(el)); } } catch (e) {}");
        sb.AppendLine("});");
        sb.AppendLine("</script>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine("<div class=\"layout\">");
        sb.AppendLine("<aside class=\"sidebar\">");
        RenderSidebar(sb, root, options);
        sb.AppendLine("</aside>");
        sb.AppendLine("<main class=\"main\">");

        RenderHeader(sb, root, options);
        RenderAtAGlance(sb, root);
        RenderRootCause(sb, root, includeJsonDetails);
        RenderFindings(sb, root, includeJsonDetails);

        if (options.IncludeCallStacks)
        {
            RenderFaultingThread(sb, root, includeJsonDetails);
        }

        if (options.IncludeThreadInfo)
        {
            RenderThreads(sb, root, includeJsonDetails);
        }

        if (options.IncludeProcessInfo || options.IncludeDotNetInfo)
        {
            RenderEnvironment(sb, root, includeJsonDetails);
        }

        if (options.IncludeHeapStats || options.IncludeMemoryLeakInfo)
        {
            RenderMemory(sb, root, includeJsonDetails);
        }

        if (options.IncludeDeadlockInfo)
        {
            RenderSynchronization(sb, root, includeJsonDetails);
        }

        if (options.IncludeSecurityAnalysis)
        {
            RenderSecurity(sb, root, includeJsonDetails);
        }

        if (options.IncludeDotNetInfo)
        {
            RenderAssemblies(sb, root, includeJsonDetails);
        }

        if (options.IncludeModules)
        {
            RenderModules(sb, root, includeJsonDetails);
        }

        if (options.IncludeModules || options.IncludeDotNetInfo)
        {
            RenderSymbols(sb, root, includeJsonDetails);
        }

        if (options.IncludeThreadInfo)
        {
            RenderTimeline(sb, root, includeJsonDetails);
        }

        if (options.IncludeCallStacks && options.IncludeDotNetInfo)
        {
            RenderSourceContextIndex(sb, root, includeJsonDetails);
            RenderSignatureAndSelection(sb, root, includeJsonDetails);
        }

        sb.AppendLine("</main>");
        sb.AppendLine("</div>");

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static void RenderSidebar(StringBuilder sb, JsonElement root, ReportOptions options)
    {
        sb.AppendLine("<div class=\"brand\">Debugger MCP</div>");
        sb.AppendLine("<div class=\"brand-sub\">Report</div>");
        sb.AppendLine("<div class=\"brand-note\">Rendered from canonical JSON</div>");

        sb.AppendLine("<nav class=\"nav\">");
        AppendNavLink(sb, "At a glance", "#at-a-glance");
        AppendNavLink(sb, "Root cause", "#root-cause");
        AppendNavLink(sb, "Findings", "#findings");
        if (options.IncludeCallStacks)
        {
            AppendNavLink(sb, "Faulting thread", "#faulting-thread");
        }
        if (options.IncludeThreadInfo)
        {
            AppendNavLink(sb, "Threads", "#threads");
        }
        if (options.IncludeProcessInfo || options.IncludeDotNetInfo)
        {
            AppendNavLink(sb, "Environment", "#environment");
        }
        if (options.IncludeHeapStats || options.IncludeMemoryLeakInfo)
        {
            AppendNavLink(sb, "Memory & GC", "#memory-gc");
        }
        if (options.IncludeDeadlockInfo)
        {
            AppendNavLink(sb, "Synchronization", "#synchronization");
        }
        if (options.IncludeSecurityAnalysis)
        {
            AppendNavLink(sb, "Security", "#security");
        }
        if (options.IncludeDotNetInfo)
        {
            AppendNavLink(sb, "Assemblies", "#assemblies");
        }
        if (options.IncludeModules)
        {
            AppendNavLink(sb, "Modules", "#modules");
        }
        if (options.IncludeModules || options.IncludeDotNetInfo)
        {
            AppendNavLink(sb, "Symbols", "#symbols");
        }
        if (options.IncludeThreadInfo)
        {
            AppendNavLink(sb, "Timeline", "#timeline");
        }
        if (options.IncludeCallStacks && options.IncludeDotNetInfo)
        {
            AppendNavLink(sb, "Source context index", "#source-context-index");
            AppendNavLink(sb, "Signature & selection", "#signature-selection");
        }
        sb.AppendLine("</nav>");

        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"sidebar-meta\">");
            sb.AppendLine("<div class=\"kv-mini\">");
            WriteMini(sb, "Dump", GetString(metadata, "dumpId"));
            WriteMini(sb, "Generated", GetString(metadata, "generatedAt"));
            WriteMini(sb, "Debugger", GetString(metadata, "debuggerType"));
            WriteMini(sb, "Server", GetString(metadata, "serverVersion"));
            WriteMini(sb, "Requested", options.Format.ToString());
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
        }
    }

    private static void RenderHeader(StringBuilder sb, JsonElement root, ReportOptions options)
    {
        sb.AppendLine("<header class=\"header\">");
        sb.AppendLine("<div class=\"header-title\">Debugger MCP Report</div>");
        sb.AppendLine("<div class=\"header-subtitle\">Requested format: <code>" + HttpUtility.HtmlEncode(options.Format.ToString()) + "</code></div>");
        sb.AppendLine("</header>");
    }

    private static void RenderAtAGlance(StringBuilder sb, JsonElement root)
    {
        sb.AppendLine("<section class=\"card\" id=\"at-a-glance\">");
        sb.AppendLine("<h2>At a glance</h2>");

        if (!TryGetAnalysis(root, out var analysis))
        {
            sb.AppendLine("<div class=\"muted\">No analysis available.</div>");
            sb.AppendLine("</section>");
            return;
        }

        var summary = GetObjectOrNull(analysis, "summary");
        var env = GetObjectOrNull(analysis, "environment");
        var platform = env.HasValue ? GetObjectOrNull(env.Value, "platform") : null;
        var runtime = env.HasValue ? GetObjectOrNull(env.Value, "runtime") : null;
        var threads = GetObjectOrNull(analysis, "threads");
        var ft = threads.HasValue ? GetObjectOrNull(threads.Value, "faultingThread") : null;

        sb.AppendLine("<div class=\"grid2\">");
        sb.AppendLine("<div class=\"panel\">");
        sb.AppendLine("<div class=\"panel-title\">Crash</div>");
        sb.AppendLine("<table class=\"kv\">");
        WriteKvRow(sb, "Crash type", summary, "crashType");
        WriteKvRow(sb, "Severity", summary, "severity");
        WriteKvRow(sb, "Description", summary, "description");
        sb.AppendLine("</table>");
        if (summary.HasValue && summary.Value.TryGetProperty("recommendations", out var recs) && recs.ValueKind == JsonValueKind.Array && recs.GetArrayLength() > 0)
        {
            sb.AppendLine("<details class=\"details\">");
            sb.AppendLine("<summary>Recommendations</summary>");
            sb.AppendLine("<ul>");
            foreach (var rec in recs.EnumerateArray())
            {
                var txt = rec.GetString();
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    sb.AppendLine("<li>" + HttpUtility.HtmlEncode(txt) + "</li>");
                }
            }
            sb.AppendLine("</ul>");
            sb.AppendLine("</details>");
        }
        sb.AppendLine("</div>");

        sb.AppendLine("<div class=\"panel\">");
        sb.AppendLine("<div class=\"panel-title\">Runtime</div>");
        sb.AppendLine("<table class=\"kv\">");
        WriteKvRow(sb, "OS", platform, "os");
        WriteKvRow(sb, "Architecture", platform, "architecture");
        WriteKvRow(sb, ".NET runtime", platform, "runtimeVersion");
        WriteKvRow(sb, "Runtime type", runtime, "type");
        WriteKvRow(sb, "CLR version", runtime, "clrVersion");
        WriteKvRow(sb, "Faulting thread", ft, "threadId");
        WriteKvRow(sb, "Top function", ft, "topFunction");
        sb.AppendLine("</table>");
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        sb.AppendLine("</section>");
    }

    private static void RenderRootCause(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("<section class=\"card\" id=\"root-cause\">");
        sb.AppendLine("<h2>Root cause</h2>");

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("rootCause", out var rootCause) ||
            rootCause.ValueKind != JsonValueKind.Object ||
            !rootCause.TryGetProperty("hypotheses", out var hypotheses) ||
            hypotheses.ValueKind != JsonValueKind.Array ||
            hypotheses.GetArrayLength() == 0)
        {
            sb.AppendLine("<div class=\"muted\">No root-cause hypotheses available.</div>");
            sb.AppendLine("</section>");
            return;
        }

        var i = 0;
        foreach (var h in hypotheses.EnumerateArray())
        {
            if (h.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            i++;
            var label = GetString(h, "label");
            var confidence = GetDouble(h, "confidence");
            sb.AppendLine("<div class=\"panel\">");
            sb.AppendLine("<div class=\"panel-title\">Hypothesis " + i + "</div>");
            if (!string.IsNullOrWhiteSpace(label))
            {
                sb.AppendLine("<div class=\"panel-sub\">" + HttpUtility.HtmlEncode(label) + "</div>");
            }
            if (confidence.HasValue)
            {
                sb.AppendLine("<div class=\"pill\">Confidence <code>" + HttpUtility.HtmlEncode(confidence.Value.ToString("0.00")) + "</code></div>");
            }

            if (h.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Array && evidence.GetArrayLength() > 0)
            {
                sb.AppendLine("<details class=\"details\">");
                sb.AppendLine("<summary>Evidence</summary>");
                sb.AppendLine("<table class=\"kv\">");
                foreach (var ev in evidence.EnumerateArray())
                {
                    if (ev.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    sb.AppendLine("<tr><td class=\"k\"><code>" + HttpUtility.HtmlEncode(GetString(ev, "jsonPointer")) + "</code></td><td class=\"v\">" + HttpUtility.HtmlEncode(GetString(ev, "note")) + "</td></tr>");
                }
                sb.AppendLine("</table>");
                sb.AppendLine("</details>");
            }

            MaybeRenderJsonDetails(sb, "Hypothesis JSON", h, includeJsonDetails);
            sb.AppendLine("</div>");
        }

        MaybeRenderJsonDetails(sb, "Root cause JSON", rootCause, includeJsonDetails);
        sb.AppendLine("</section>");
    }

    private static void RenderFindings(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("<section class=\"card\" id=\"findings\">");
        sb.AppendLine("<h2>Findings</h2>");

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("findings", out var findings) ||
            findings.ValueKind != JsonValueKind.Array ||
            findings.GetArrayLength() == 0)
        {
            sb.AppendLine("<div class=\"muted\">No findings available.</div>");
            sb.AppendLine("</section>");
            return;
        }

        foreach (var finding in findings.EnumerateArray())
        {
            if (finding.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            sb.AppendLine("<div class=\"panel\">");
            sb.AppendLine("<div class=\"panel-title\">" + HttpUtility.HtmlEncode(GetString(finding, "title")) + "</div>");
            sb.AppendLine("<div class=\"panel-sub\">" + HttpUtility.HtmlEncode(GetString(finding, "summary")) + "</div>");
            sb.AppendLine("<table class=\"kv\">");
            WriteKvRow(sb, "ID", finding, "id");
            WriteKvRow(sb, "Category", finding, "category");
            WriteKvRow(sb, "Severity", finding, "severity");
            var c = GetDouble(finding, "confidence");
            if (c.HasValue)
            {
                sb.AppendLine("<tr><td class=\"k\">Confidence</td><td class=\"v\"><code>" + HttpUtility.HtmlEncode(c.Value.ToString("0.00")) + "</code></td></tr>");
            }
            sb.AppendLine("</table>");

            if (finding.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Array && evidence.GetArrayLength() > 0)
            {
                sb.AppendLine("<details class=\"details\">");
                sb.AppendLine("<summary>Evidence</summary>");
                sb.AppendLine("<table class=\"kv\">");
                foreach (var ev in evidence.EnumerateArray())
                {
                    if (ev.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    sb.AppendLine("<tr><td class=\"k\"><code>" + HttpUtility.HtmlEncode(GetString(ev, "jsonPointer")) + "</code></td><td class=\"v\">" + HttpUtility.HtmlEncode(GetString(ev, "note")) + "</td></tr>");
                }
                sb.AppendLine("</table>");
                sb.AppendLine("</details>");
            }

            if (finding.TryGetProperty("nextActions", out var actions) && actions.ValueKind == JsonValueKind.Array && actions.GetArrayLength() > 0)
            {
                sb.AppendLine("<details class=\"details\">");
                sb.AppendLine("<summary>Next actions</summary>");
                sb.AppendLine("<ul>");
                foreach (var action in actions.EnumerateArray())
                {
                    var txt = action.GetString();
                    if (!string.IsNullOrWhiteSpace(txt))
                    {
                        sb.AppendLine("<li>" + HttpUtility.HtmlEncode(txt) + "</li>");
                    }
                }
                sb.AppendLine("</ul>");
                sb.AppendLine("</details>");
            }

            MaybeRenderJsonDetails(sb, "Finding JSON", finding, includeJsonDetails);
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</section>");
    }

    private static void RenderFaultingThread(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("<section class=\"card\" id=\"faulting-thread\">");
        sb.AppendLine("<h2>Faulting thread</h2>");

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("threads", out var threads) ||
            threads.ValueKind != JsonValueKind.Object ||
            !threads.TryGetProperty("faultingThread", out var faultingThread) ||
            faultingThread.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"muted\">No faulting thread detected.</div>");
            sb.AppendLine("</section>");
            return;
        }

        sb.AppendLine("<div class=\"grid2\">");
        sb.AppendLine("<div class=\"panel\">");
        sb.AppendLine("<div class=\"panel-title\">Thread</div>");
        sb.AppendLine("<table class=\"kv\">");
        WriteKvRow(sb, "Thread ID", faultingThread, "threadId");
        WriteKvRow(sb, "State", faultingThread, "state");
        WriteKvRow(sb, "Top function", faultingThread, "topFunction");
        WriteKvRow(sb, "Managed thread ID", faultingThread, "managedThreadId");
        WriteKvRow(sb, "OS thread ID (hex)", faultingThread, "osThreadId");
        WriteKvRow(sb, "OS thread ID (decimal)", faultingThread, "osThreadIdDecimal");
        WriteKvRow(sb, "GC mode", faultingThread, "gcMode");
        WriteKvRow(sb, "Lock count", faultingThread, "lockCount");
        sb.AppendLine("</table>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class=\"panel\">");
        sb.AppendLine("<div class=\"panel-title\">Call stack</div>");
        if (faultingThread.TryGetProperty("callStack", out var cs) && cs.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("<div class=\"muted\">Frames: <code>" + cs.GetArrayLength() + "</code></div>");
        }
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        if (faultingThread.TryGetProperty("callStack", out var callStack) && callStack.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("<ol class=\"stack\">");
            foreach (var frame in callStack.EnumerateArray())
            {
                RenderFrame(sb, frame, includeSourceContext: true, includeFrameJson: includeJsonDetails);
            }
            sb.AppendLine("</ol>");
        }
        else
        {
            sb.AppendLine("<div class=\"muted\">No call stack available.</div>");
        }

        MaybeRenderJsonDetails(sb, "Faulting thread JSON", faultingThread, includeJsonDetails);
        sb.AppendLine("</section>");
    }

    private static void RenderThreads(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("<section class=\"card\" id=\"threads\">");
        sb.AppendLine("<h2>Threads</h2>");

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("threads", out var threads) ||
            threads.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"muted\">No thread data available.</div>");
            sb.AppendLine("</section>");
            return;
        }

        if (threads.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"panel\">");
            sb.AppendLine("<div class=\"panel-title\">Summary</div>");
            sb.AppendLine("<table class=\"kv\">");
            WriteKvRow(sb, "Total", summary, "total");
            WriteKvRow(sb, "Foreground", summary, "foreground");
            WriteKvRow(sb, "Background", summary, "background");
            WriteKvRow(sb, "Unstarted", summary, "unstarted");
            WriteKvRow(sb, "Dead", summary, "dead");
            WriteKvRow(sb, "Pending", summary, "pending");
            sb.AppendLine("</table>");
            sb.AppendLine("</div>");
        }

        if (threads.TryGetProperty("deadlock", out var deadlock) && deadlock.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"panel\">");
            sb.AppendLine("<div class=\"panel-title\">Deadlock</div>");
            sb.AppendLine("<table class=\"kv\">");
            WriteKvRow(sb, "Detected", deadlock, "detected");
            sb.AppendLine("</table>");
            MaybeRenderJsonDetails(sb, "Deadlock JSON", deadlock, includeJsonDetails);
            sb.AppendLine("</div>");
        }

        if (threads.TryGetProperty("threadPool", out var threadPool) && threadPool.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"panel\">");
            sb.AppendLine("<div class=\"panel-title\">ThreadPool</div>");
            MaybeRenderJsonDetails(sb, "ThreadPool JSON", threadPool, includeJsonDetails);
            sb.AppendLine("</div>");
        }

        if (threads.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("<details class=\"details\">");
            sb.AppendLine("<summary>Thread list (" + all.GetArrayLength() + ")</summary>");
            sb.AppendLine("<div class=\"table-wrap\">");
            sb.AppendLine("<table class=\"table\">");
            sb.AppendLine("<thead><tr><th>Thread ID</th><th>State</th><th>Faulting</th><th>Top function</th><th>Frames</th></tr></thead>");
            sb.AppendLine("<tbody>");
            foreach (var t in all.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var frames = t.TryGetProperty("callStack", out var cs) && cs.ValueKind == JsonValueKind.Array ? cs.GetArrayLength().ToString() : string.Empty;
                sb.AppendLine("<tr>");
                sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(GetString(t, "threadId")) + "</code></td>");
                sb.AppendLine("<td>" + HttpUtility.HtmlEncode(GetString(t, "state")) + "</td>");
                sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(GetString(t, "isFaulting")) + "</code></td>");
                sb.AppendLine("<td class=\"mono\">" + HttpUtility.HtmlEncode(GetString(t, "topFunction")) + "</td>");
                sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(frames) + "</code></td>");
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</tbody></table></div>");
            sb.AppendLine("</details>");

            foreach (var t in all.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                sb.AppendLine("<details class=\"details\">");
                sb.AppendLine("<summary><code>" + HttpUtility.HtmlEncode(GetString(t, "threadId")) + "</code> • " + HttpUtility.HtmlEncode(GetString(t, "state")) + "</summary>");
                MaybeRenderJsonDetails(sb, "Thread JSON", t, includeJsonDetails);
                if (t.TryGetProperty("callStack", out var cs) && cs.ValueKind == JsonValueKind.Array)
                {
                    sb.AppendLine("<ol class=\"stack\">");
                    foreach (var frame in cs.EnumerateArray())
                    {
                        RenderFrame(sb, frame, includeSourceContext: false, includeFrameJson: false);
                    }
                    sb.AppendLine("</ol>");
                }
                sb.AppendLine("</details>");
            }
        }

        MaybeRenderJsonDetails(sb, "Threads JSON", threads, includeJsonDetails);
        sb.AppendLine("</section>");
    }

    private static void RenderFrame(StringBuilder sb, JsonElement frame, bool includeSourceContext, bool includeFrameJson)
    {
        if (frame.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var frameNumber = GetString(frame, "frameNumber");
        var module = GetString(frame, "module");
        var function = GetString(frame, "function");
        var ip = GetString(frame, "instructionPointer");
        var managed = frame.TryGetProperty("isManaged", out var isManaged) && isManaged.ValueKind == JsonValueKind.True;
        var sourceFile = GetString(frame, "sourceFile");
        var lineNumber = GetString(frame, "lineNumber");
        var sourceUrl = GetString(frame, "sourceUrl");
        var sourceRawUrl = GetString(frame, "sourceRawUrl");

        sb.AppendLine("<li class=\"frame\">");
        sb.AppendLine("<div class=\"frame-title\">");
        sb.AppendLine("<span class=\"badge " + (managed ? "managed" : "native") + "\">" + (managed ? "managed" : "native") + "</span>");
        sb.AppendLine("<code>" + HttpUtility.HtmlEncode(module) + "!" + HttpUtility.HtmlEncode(function) + "</code>");
        if (!string.IsNullOrWhiteSpace(frameNumber))
        {
            sb.AppendLine("<span class=\"muted\">#</span><code>" + HttpUtility.HtmlEncode(frameNumber) + "</code>");
        }
        sb.AppendLine("</div>");

        var hasIp = !string.IsNullOrWhiteSpace(ip);
        var hasSource = !string.IsNullOrWhiteSpace(sourceFile) || !string.IsNullOrWhiteSpace(lineNumber);
        if (hasIp || hasSource)
        {
            sb.AppendLine("<div class=\"muted\">");
            if (hasIp)
            {
                sb.AppendLine("IP: <code>" + HttpUtility.HtmlEncode(ip) + "</code>");
            }
            if (hasSource)
            {
                sb.AppendLine("&nbsp;&nbsp;•&nbsp;&nbsp;Source: <code>" + HttpUtility.HtmlEncode(sourceFile) + ":" + HttpUtility.HtmlEncode(lineNumber) + "</code>");
            }
            sb.AppendLine("</div>");
        }

        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            sb.AppendLine("<div class=\"muted\">Source URL: <a href=\"" + HttpUtility.HtmlEncode(sourceUrl) + "\" target=\"_blank\" rel=\"noreferrer\">" + HttpUtility.HtmlEncode(sourceUrl) + "</a></div>");
        }
        if (!string.IsNullOrWhiteSpace(sourceRawUrl))
        {
            sb.AppendLine("<div class=\"muted\">Raw URL: <a href=\"" + HttpUtility.HtmlEncode(sourceRawUrl) + "\" target=\"_blank\" rel=\"noreferrer\">" + HttpUtility.HtmlEncode(sourceRawUrl) + "</a></div>");
        }

        if (includeSourceContext &&
            frame.TryGetProperty("sourceContext", out var sc) &&
            sc.ValueKind == JsonValueKind.Object &&
            sc.TryGetProperty("status", out var statusElem))
        {
            var status = statusElem.GetString() ?? string.Empty;
            if (!string.Equals(status, "unavailable", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("<details class=\"details\">");
                sb.AppendLine("<summary>Source context <span class=\"pill\">" + HttpUtility.HtmlEncode(status) + "</span></summary>");

                var lang = GuessFenceLanguage(frame);
                if (sc.TryGetProperty("lines", out var linesElem) && linesElem.ValueKind == JsonValueKind.Array)
                {
                    sb.AppendLine("<pre class=\"code\"><code class=\"" + HttpUtility.HtmlEncode(string.IsNullOrEmpty(lang) ? "language-text" : "language-" + lang) + "\">");
                    foreach (var line in linesElem.EnumerateArray())
                    {
                        sb.AppendLine(HttpUtility.HtmlEncode(line.GetString() ?? string.Empty));
                    }
                    sb.AppendLine("</code></pre>");
                }
                else if (sc.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                {
                    sb.AppendLine("<div class=\"alert\">" + HttpUtility.HtmlEncode(err.GetString() ?? string.Empty) + "</div>");
                }

                MaybeRenderJsonDetails(sb, "Source context JSON", sc, includeFrameJson);
                sb.AppendLine("</details>");
            }
        }

        if (includeFrameJson)
        {
            RenderJsonDetails(sb, "Frame JSON", frame);
        }
        sb.AppendLine("</li>");
    }

    private static void RenderEnvironment(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("<section class=\"card\" id=\"environment\">");
        sb.AppendLine("<h2>Environment</h2>");

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("environment", out var env) ||
            env.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"muted\">No environment data available.</div>");
            sb.AppendLine("</section>");
            return;
        }

        var platform = GetObjectOrNull(env, "platform");
        var runtime = GetObjectOrNull(env, "runtime");
        var crashInfo = GetObjectOrNull(env, "crashInfo");
        var process = GetObjectOrNull(env, "process");

        sb.AppendLine("<div class=\"grid2\">");
        sb.AppendLine("<div class=\"panel\">");
        sb.AppendLine("<div class=\"panel-title\">Platform</div>");
        sb.AppendLine("<table class=\"kv\">");
        WriteKvRow(sb, "OS", platform, "os");
        WriteKvRow(sb, "Architecture", platform, "architecture");
        WriteKvRow(sb, "Runtime version", platform, "runtimeVersion");
        WriteKvRow(sb, "Pointer size", platform, "pointerSize");
        sb.AppendLine("</table>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class=\"panel\">");
        sb.AppendLine("<div class=\"panel-title\">Runtime</div>");
        sb.AppendLine("<table class=\"kv\">");
        WriteKvRow(sb, "Type", runtime, "type");
        WriteKvRow(sb, "CLR version", runtime, "clrVersion");
        WriteKvRow(sb, "Hosted", runtime, "isHosted");
        sb.AppendLine("</table>");
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class=\"panel\">");
        sb.AppendLine("<div class=\"panel-title\">Crash info</div>");
        sb.AppendLine("<table class=\"kv\">");
        WriteKvRow(sb, "Has info", crashInfo, "hasInfo");
        WriteKvRow(sb, "Message", crashInfo, "message");
        sb.AppendLine("</table>");
        sb.AppendLine("</div>");

        if (process.HasValue && process.Value.ValueKind == JsonValueKind.Object)
        {
            if (process.Value.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("<details class=\"details\">");
                sb.AppendLine("<summary>Arguments (" + args.GetArrayLength() + ")</summary>");
                sb.AppendLine("<pre class=\"code\"><code class=\"language-text\">");
                var idx = 0;
                foreach (var arg in args.EnumerateArray())
                {
                    sb.AppendLine(HttpUtility.HtmlEncode("[" + idx++ + "] " + (arg.GetString() ?? string.Empty)));
                }
                sb.AppendLine("</code></pre>");
                sb.AppendLine("</details>");
            }

            if (process.Value.TryGetProperty("environmentVariables", out var vars) && vars.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("<details class=\"details\">");
                sb.AppendLine("<summary>Environment variables</summary>");
                sb.AppendLine("<pre class=\"code\"><code class=\"language-text\">");
                var idx = 0;
                foreach (var ev in vars.EnumerateArray())
                {
                    var text = ev.ValueKind == JsonValueKind.String ? ev.GetString() : ev.ToString();
                    sb.AppendLine(HttpUtility.HtmlEncode("[" + idx++ + "] " + (text ?? string.Empty)));
                }
                sb.AppendLine("</code></pre>");
                sb.AppendLine("</details>");
            }
        }

        MaybeRenderJsonDetails(sb, "Environment JSON", env, includeJsonDetails);
        sb.AppendLine("</section>");
    }

    private static void RenderMemory(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("<section class=\"card\" id=\"memory-gc\">");
        sb.AppendLine("<h2>Memory &amp; GC</h2>");

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("memory", out var memory) ||
            memory.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"muted\">No memory data available.</div>");
            sb.AppendLine("</section>");
            return;
        }

        var gc = GetObjectOrNull(memory, "gc");
        if (gc.HasValue && gc.Value.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"panel\">");
            sb.AppendLine("<div class=\"panel-title\">GC</div>");
            sb.AppendLine("<table class=\"kv\">");
            WriteKvRow(sb, "Heap count", gc, "heapCount");
            WriteKvRow(sb, "GC mode", gc, "gcMode");
            WriteKvRow(sb, "Server GC", gc, "isServerGC");
            WriteKvRow(sb, "Total heap size", gc, "totalHeapSize");
            WriteKvRow(sb, "Fragmentation", gc, "fragmentation");
            WriteKvRow(sb, "Fragmentation bytes", gc, "fragmentationBytes");
            WriteKvRow(sb, "Finalizable objects", gc, "finalizableObjectCount");
            sb.AppendLine("</table>");
            sb.AppendLine("</div>");
        }

        if (memory.TryGetProperty("heapStats", out var heapStats) && heapStats.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"panel\">");
            sb.AppendLine("<div class=\"panel-title\">Heap stats</div>");
            sb.AppendLine("<div class=\"muted\">Heap stats is large; see JSON for full dataset.</div>");
            sb.AppendLine("<div class=\"pill\">Entries <code>" + heapStats.EnumerateObject().Count() + "</code></div>");
            sb.AppendLine("</div>");
        }

        MaybeRenderJsonDetails(sb, "Memory JSON", memory, includeJsonDetails);
        sb.AppendLine("</section>");
    }

    private static void RenderSynchronization(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("<section class=\"card\" id=\"synchronization\">");
        sb.AppendLine("<h2>Synchronization</h2>");

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("synchronization", out var sync) ||
            sync.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"muted\">No synchronization data available.</div>");
            sb.AppendLine("</section>");
            return;
        }

        if (sync.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
        {
            sb.AppendLine("<div class=\"muted\">" + HttpUtility.HtmlEncode(s.GetString() ?? string.Empty) + "</div>");
        }

        if (sync.TryGetProperty("potentialDeadlocks", out var deadlocks) && deadlocks.ValueKind == JsonValueKind.Array && deadlocks.GetArrayLength() > 0)
        {
            sb.AppendLine("<details class=\"details\">");
            sb.AppendLine("<summary>Potential deadlocks</summary>");
            sb.AppendLine("<pre class=\"code\"><code class=\"language-json\">" + HttpUtility.HtmlEncode(SerializeIndented(deadlocks)) + "</code></pre>");
            sb.AppendLine("</details>");
        }

        MaybeRenderJsonDetails(sb, "Synchronization JSON", sync, includeJsonDetails);
        sb.AppendLine("</section>");
    }

    private static void RenderSecurity(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("<section class=\"card\" id=\"security\">");
        sb.AppendLine("<h2>Security</h2>");

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("security", out var security) ||
            security.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"muted\">No security analysis available.</div>");
            sb.AppendLine("</section>");
            return;
        }

        sb.AppendLine("<table class=\"kv\">");
        WriteKvRow(sb, "Has vulnerabilities", security, "hasVulnerabilities");
        WriteKvRow(sb, "Overall risk", security, "overallRisk");
        WriteKvRow(sb, "Summary", security, "summary");
        WriteKvRow(sb, "Analyzed at (UTC)", security, "analyzedAt");
        sb.AppendLine("</table>");

        if (security.TryGetProperty("recommendations", out var recs) && recs.ValueKind == JsonValueKind.Array && recs.GetArrayLength() > 0)
        {
            sb.AppendLine("<details class=\"details\">");
            sb.AppendLine("<summary>Recommendations</summary>");
            sb.AppendLine("<ul>");
            foreach (var rec in recs.EnumerateArray())
            {
                var txt = rec.GetString();
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    sb.AppendLine("<li>" + HttpUtility.HtmlEncode(txt) + "</li>");
                }
            }
            sb.AppendLine("</ul>");
            sb.AppendLine("</details>");
        }

        MaybeRenderJsonDetails(sb, "Security JSON", security, includeJsonDetails);
        sb.AppendLine("</section>");
    }

    private static void RenderAssemblies(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("<section class=\"card\" id=\"assemblies\">");
        sb.AppendLine("<h2>Assemblies</h2>");

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("assemblies", out var assemblies) ||
            assemblies.ValueKind != JsonValueKind.Object ||
            !assemblies.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            sb.AppendLine("<div class=\"muted\">No assemblies available.</div>");
            sb.AppendLine("</section>");
            return;
        }

        sb.AppendLine("<div class=\"muted\">Total: <code>" + items.GetArrayLength() + "</code></div>");
        sb.AppendLine("<details class=\"details\">");
        sb.AppendLine("<summary>Assembly list</summary>");
        sb.AppendLine("<div class=\"table-wrap\">");
        sb.AppendLine("<table class=\"table\">");
        sb.AppendLine("<thead><tr><th>Name</th><th>Version</th><th>Path</th><th>Source</th></tr></thead>");
        sb.AppendLine("<tbody>");
        foreach (var asm in items.EnumerateArray())
        {
            if (asm.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            sb.AppendLine("<tr>");
            sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(GetString(asm, "name")) + "</code></td>");
            sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(GetString(asm, "assemblyVersion")) + "</code></td>");
            sb.AppendLine("<td class=\"mono\">" + HttpUtility.HtmlEncode(GetString(asm, "path")) + "</td>");
            var url = GetString(asm, "sourceUrl");
            if (!string.IsNullOrWhiteSpace(url))
            {
                sb.AppendLine("<td><a href=\"" + HttpUtility.HtmlEncode(url) + "\" target=\"_blank\" rel=\"noreferrer\">" + HttpUtility.HtmlEncode(url) + "</a></td>");
            }
            else
            {
                sb.AppendLine("<td class=\"muted\">(none)</td>");
            }
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table></div>");
        sb.AppendLine("</details>");

        MaybeRenderJsonDetails(sb, "Assemblies JSON", assemblies, includeJsonDetails);
        sb.AppendLine("</section>");
    }

    private static void RenderModules(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("<section class=\"card\" id=\"modules\">");
        sb.AppendLine("<h2>Modules</h2>");

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("modules", out var modules) ||
            modules.ValueKind != JsonValueKind.Array)
        {
            sb.AppendLine("<div class=\"muted\">No modules available.</div>");
            sb.AppendLine("</section>");
            return;
        }

        sb.AppendLine("<div class=\"muted\">Total: <code>" + modules.GetArrayLength() + "</code></div>");
        sb.AppendLine("<div class=\"table-wrap\">");
        sb.AppendLine("<table class=\"table\">");
        sb.AppendLine("<thead><tr><th>Name</th><th>Base</th><th>Has symbols</th></tr></thead>");
        sb.AppendLine("<tbody>");
        foreach (var mod in modules.EnumerateArray())
        {
            if (mod.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            sb.AppendLine("<tr>");
            sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(GetString(mod, "name")) + "</code></td>");
            sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(GetString(mod, "baseAddress")) + "</code></td>");
            sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(GetString(mod, "hasSymbols")) + "</code></td>");
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table></div>");

        MaybeRenderJsonDetails(sb, "Modules JSON", modules, includeJsonDetails);
        sb.AppendLine("</section>");
    }

    private static void RenderSymbols(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("<section class=\"card\" id=\"symbols\">");
        sb.AppendLine("<h2>Symbols</h2>");

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("symbols", out var symbols) ||
            symbols.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"muted\">No symbols data available.</div>");
            sb.AppendLine("</section>");
            return;
        }

        var native = GetObjectOrNull(symbols, "native");
        var managed = GetObjectOrNull(symbols, "managed");
        var sourcelink = GetObjectOrNull(symbols, "sourcelink");

        sb.AppendLine("<table class=\"kv\">");
        WriteKvRow(sb, "Native missing count", native, "missingCount");
        WriteKvRow(sb, "Managed PDB missing count", managed, "pdbMissingCount");
        WriteKvRow(sb, "SourceLink resolved", sourcelink, "resolvedCount");
        WriteKvRow(sb, "SourceLink unresolved", sourcelink, "unresolvedCount");
        sb.AppendLine("</table>");

        if (native.HasValue && native.Value.TryGetProperty("examples", out var examples) && examples.ValueKind == JsonValueKind.Array && examples.GetArrayLength() > 0)
        {
            sb.AppendLine("<details class=\"details\">");
            sb.AppendLine("<summary>Native missing examples</summary>");
            sb.AppendLine("<ul>");
            foreach (var ex in examples.EnumerateArray())
            {
                var txt = ex.GetString();
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    sb.AppendLine("<li>" + HttpUtility.HtmlEncode(txt) + "</li>");
                }
            }
            sb.AppendLine("</ul>");
            sb.AppendLine("</details>");
        }

        MaybeRenderJsonDetails(sb, "Symbols JSON", symbols, includeJsonDetails);
        sb.AppendLine("</section>");
    }

    private static void RenderTimeline(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("<section class=\"card\" id=\"timeline\">");
        sb.AppendLine("<h2>Timeline</h2>");

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("timeline", out var timeline) ||
            timeline.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"muted\">No timeline available.</div>");
            sb.AppendLine("</section>");
            return;
        }

        sb.AppendLine("<table class=\"kv\">");
        WriteKvRow(sb, "Version", timeline, "version");
        WriteKvRow(sb, "Kind", timeline, "kind");
        WriteKvRow(sb, "Captured at (UTC)", timeline, "capturedAtUtc");
        WriteKvRow(sb, "Capture reason", timeline, "captureReason");
        sb.AppendLine("</table>");

        if (timeline.TryGetProperty("threads", out var threads) && threads.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("<details class=\"details\">");
            sb.AppendLine("<summary>Thread timeline</summary>");
            sb.AppendLine("<div class=\"table-wrap\">");
            sb.AppendLine("<table class=\"table\">");
            sb.AppendLine("<thead><tr><th>Thread</th><th>OS thread</th><th>State</th><th>Activity</th><th>Top frame</th><th>Wait</th></tr></thead>");
            sb.AppendLine("<tbody>");
            foreach (var t in threads.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                sb.AppendLine("<tr>");
                sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(GetString(t, "threadId")) + "</code></td>");
                sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(GetString(t, "osThreadId")) + "</code></td>");
                sb.AppendLine("<td>" + HttpUtility.HtmlEncode(GetString(t, "state")) + "</td>");
                sb.AppendLine("<td>" + HttpUtility.HtmlEncode(GetString(t, "activity")) + "</td>");
                sb.AppendLine("<td class=\"mono\">" + HttpUtility.HtmlEncode(GetString(t, "topFrame")) + "</td>");
                sb.AppendLine("<td>" + HttpUtility.HtmlEncode(GetString(t, "wait")) + "</td>");
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</tbody></table></div>");
            sb.AppendLine("</details>");
        }

        MaybeRenderJsonDetails(sb, "Timeline JSON", timeline, includeJsonDetails);
        sb.AppendLine("</section>");
    }

    private static void RenderSourceContextIndex(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("<section class=\"card\" id=\"source-context-index\">");
        sb.AppendLine("<h2>Source context index</h2>");

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("sourceContext", out var contexts) ||
            contexts.ValueKind != JsonValueKind.Array ||
            contexts.GetArrayLength() == 0)
        {
            sb.AppendLine("<div class=\"muted\">No source context index available.</div>");
            sb.AppendLine("</section>");
            return;
        }

        sb.AppendLine("<div class=\"muted\">Entries: <code>" + contexts.GetArrayLength() + "</code></div>");
        sb.AppendLine("<div class=\"table-wrap\">");
        sb.AppendLine("<table class=\"table\">");
        sb.AppendLine("<thead><tr><th>Thread</th><th>Frame</th><th>Module</th><th>Function</th><th>File</th><th>Line</th><th>Status</th></tr></thead>");
        sb.AppendLine("<tbody>");
        foreach (var c in contexts.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            sb.AppendLine("<tr>");
            sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(GetString(c, "threadId")) + "</code></td>");
            sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(GetString(c, "frameNumber")) + "</code></td>");
            sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(GetString(c, "module")) + "</code></td>");
            sb.AppendLine("<td class=\"mono\">" + HttpUtility.HtmlEncode(GetString(c, "function")) + "</td>");
            sb.AppendLine("<td class=\"mono\">" + HttpUtility.HtmlEncode(GetString(c, "sourceFile")) + "</td>");
            sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(GetString(c, "lineNumber")) + "</code></td>");
            sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(GetString(c, "status")) + "</code></td>");
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table></div>");

        MaybeRenderJsonDetails(sb, "Source context index JSON", contexts, includeJsonDetails);
        sb.AppendLine("</section>");
    }

    private static void RenderSignatureAndSelection(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("<section class=\"card\" id=\"signature-selection\">");
        sb.AppendLine("<h2>Signature &amp; stack selection</h2>");

        if (!TryGetAnalysis(root, out var analysis))
        {
            sb.AppendLine("<div class=\"muted\">No analysis available.</div>");
            sb.AppendLine("</section>");
            return;
        }

        if (analysis.TryGetProperty("signature", out var signature) && signature.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"panel\">");
            sb.AppendLine("<div class=\"panel-title\">Signature</div>");
            sb.AppendLine("<table class=\"kv\">");
            WriteKvRow(sb, "Kind", signature, "kind");
            WriteKvRow(sb, "Hash", signature, "hash");
            sb.AppendLine("</table>");
            MaybeRenderJsonDetails(sb, "Signature JSON", signature, includeJsonDetails);
            sb.AppendLine("</div>");
        }

        if (analysis.TryGetProperty("stackSelection", out var selection) && selection.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"panel\">");
            sb.AppendLine("<div class=\"panel-title\">Stack selection</div>");
            MaybeRenderJsonDetails(sb, "Stack selection JSON", selection, includeJsonDetails);
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</section>");
    }

    private static void AppendNavLink(StringBuilder sb, string label, string href) =>
        sb.AppendLine("<a class=\"nav-link\" href=\"" + HttpUtility.HtmlEncode(href) + "\">" + HttpUtility.HtmlEncode(label) + "</a>");

    private static bool TryGetAnalysis(JsonElement root, out JsonElement analysis)
    {
        if (root.TryGetProperty("analysis", out analysis) && analysis.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        analysis = default;
        return false;
    }

    private static void WriteMini(StringBuilder sb, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        sb.AppendLine("<div class=\"kv-mini-row\"><div class=\"kv-mini-k\">" + HttpUtility.HtmlEncode(label) + "</div><div class=\"kv-mini-v\"><code>" + HttpUtility.HtmlEncode(value) + "</code></div></div>");
    }

    private static void WriteKvRow(StringBuilder sb, string label, JsonElement? obj, string property)
    {
        if (!obj.HasValue || obj.Value.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        WriteKvRow(sb, label, obj.Value, property);
    }

    private static void WriteKvRow(StringBuilder sb, string label, JsonElement obj, string property)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(property, out var value))
        {
            return;
        }

        var display = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString()
        };

        if (string.IsNullOrWhiteSpace(display))
        {
            return;
        }

        sb.AppendLine("<tr><td class=\"k\">" + HttpUtility.HtmlEncode(label) + "</td><td class=\"v\">" + HttpUtility.HtmlEncode(display) + "</td></tr>");
    }

    private static JsonElement? GetObjectOrNull(JsonElement obj, string property)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(property, out var v) &&
            v.ValueKind == JsonValueKind.Object)
        {
            return v;
        }
        return null;
    }

    private static string GetString(JsonElement obj, string property)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var v))
        {
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? string.Empty,
                JsonValueKind.Number => v.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => v.ToString()
            };
        }
        return string.Empty;
    }

    private static double? GetDouble(JsonElement obj, string property)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(property, out var v) &&
            v.ValueKind == JsonValueKind.Number &&
            v.TryGetDouble(out var d))
        {
            return d;
        }
        return null;
    }

    private static string SerializeIndented(JsonElement element) =>
        JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });

    private static void MaybeRenderJsonDetails(StringBuilder sb, string title, JsonElement element, bool include)
    {
        if (!include)
        {
            return;
        }

        RenderJsonDetails(sb, title, element);
    }

    private static void RenderJsonDetails(StringBuilder sb, string title, JsonElement element)
    {
        sb.AppendLine("<details class=\"details\">");
        sb.AppendLine("<summary>" + HttpUtility.HtmlEncode(title) + "</summary>");
        sb.AppendLine("<pre class=\"code\"><code class=\"language-json\">" + HttpUtility.HtmlEncode(SerializeIndented(element)) + "</code></pre>");
        sb.AppendLine("</details>");
    }

    private static string ElementToText(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(Environment.NewLine, element.EnumerateArray().Select(ElementToText)),
            _ => element.ToString()
        };

    private static string GuessFenceLanguage(JsonElement frame)
    {
        if (!frame.TryGetProperty("sourceFile", out var sourceFile) || sourceFile.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var ext = Path.GetExtension(sourceFile.GetString() ?? string.Empty).ToLowerInvariant();
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

    private static string GetCss() =>
        """
        :root {
          --bg: #0b1020;
          --card: #121a33;
          --panel: rgba(255,255,255,0.03);
          --text: #e8ecff;
          --muted: #a7b0d6;
          --border: #25305a;
          --accent: #7aa2ff;
          --ok: #2dd4bf;
          --warn: #fbbf24;
        }
        body { margin: 0; font-family: system-ui, -apple-system, Segoe UI, sans-serif; background: var(--bg); color: var(--text); }
        .layout { display: grid; grid-template-columns: 260px 1fr; min-height: 100vh; }
        .sidebar { position: sticky; top: 0; height: 100vh; overflow: auto; border-right: 1px solid var(--border); padding: 18px 14px; background: rgba(18,26,51,0.35); }
        .main { padding: 24px; max-width: 1100px; }
        .brand { font-size: 14px; font-weight: 800; letter-spacing: 0.06em; text-transform: uppercase; color: var(--muted); }
        .brand-sub { margin-top: 6px; font-size: 18px; font-weight: 800; }
        .brand-note { margin-top: 8px; color: var(--muted); font-size: 12px; }
        .nav { margin-top: 16px; display: flex; flex-direction: column; gap: 6px; }
        .nav-link { display: block; padding: 8px 10px; border-radius: 10px; border: 1px solid rgba(37,48,90,0.35); background: rgba(11,16,32,0.25); color: var(--text); }
        .nav-link:hover { border-color: rgba(122,162,255,0.35); background: rgba(122,162,255,0.08); }
        .sidebar-meta { margin-top: 16px; padding-top: 14px; border-top: 1px solid rgba(37,48,90,0.55); }
        .kv-mini { display: flex; flex-direction: column; gap: 8px; }
        .kv-mini-row { display: grid; grid-template-columns: 88px 1fr; gap: 8px; }
        .kv-mini-k { color: var(--muted); font-size: 12px; }
        .kv-mini-v { word-break: break-word; }
        .header { padding: 18px 20px; border: 1px solid var(--border); border-radius: 14px; background: linear-gradient(180deg, rgba(122,162,255,0.12), rgba(18,26,51,0.85)); }
        .header-title { font-size: 22px; font-weight: 700; }
        .header-subtitle { margin-top: 6px; color: var(--muted); }
        h2 { margin: 0 0 12px 0; font-size: 18px; }
        .card { margin-top: 18px; padding: 16px 18px; border: 1px solid var(--border); border-radius: 14px; background: var(--card); }
        .grid2 { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
        @media (max-width: 980px) { .layout { grid-template-columns: 1fr; } .sidebar { position: relative; height: auto; } .grid2 { grid-template-columns: 1fr; } }
        .panel { margin-top: 12px; padding: 12px 12px; border: 1px solid rgba(37,48,90,0.55); border-radius: 12px; background: var(--panel); }
        .panel-title { font-weight: 700; }
        .panel-sub { margin-top: 6px; color: var(--muted); }
        .kv { width: 100%; border-collapse: collapse; }
        .kv td { padding: 8px 10px; vertical-align: top; border-top: 1px solid rgba(37,48,90,0.55); }
        .kv tr:first-child td { border-top: none; }
        .k { width: 220px; color: var(--muted); }
        .v { word-break: break-word; }
        .muted { color: var(--muted); margin-top: 6px; }
        .pill { display: inline-block; padding: 6px 10px; border-radius: 999px; background: rgba(122,162,255,0.18); border: 1px solid rgba(122,162,255,0.28); }
        .details { margin-top: 10px; }
        .details > summary { cursor: pointer; color: var(--accent); }
        .stack { margin: 12px 0 0 20px; padding: 0; }
        .frame { margin: 10px 0; }
        .frame-title { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
        .badge { font-size: 12px; padding: 2px 8px; border-radius: 999px; border: 1px solid var(--border); color: var(--muted); }
        .badge.managed { border-color: rgba(45,212,191,0.5); color: var(--ok); }
        .badge.native { border-color: rgba(251,191,36,0.5); color: var(--warn); }
        .code { overflow: auto; padding: 12px; border-radius: 10px; border: 1px solid var(--border); background: rgba(11,16,32,0.6); }
        code { font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace; font-size: 12.5px; }
        a { color: var(--accent); text-decoration: none; }
        a:hover { text-decoration: underline; }
        .alert { margin-top: 10px; padding: 10px 12px; border-radius: 10px; border: 1px solid rgba(251,191,36,0.5); background: rgba(251,191,36,0.1); }
        .table-wrap { overflow: auto; border: 1px solid rgba(37,48,90,0.55); border-radius: 12px; margin-top: 10px; }
        .table { width: 100%; border-collapse: collapse; min-width: 860px; }
        .table th, .table td { padding: 10px 10px; border-top: 1px solid rgba(37,48,90,0.55); vertical-align: top; }
        .table th { text-align: left; color: var(--muted); font-weight: 700; background: rgba(11,16,32,0.35); position: sticky; top: 0; }
        .mono { font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace; font-size: 12.5px; }
        """;
}
