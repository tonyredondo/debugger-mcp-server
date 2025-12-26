using System;
using System.Collections.Generic;
using System.Globalization;
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

        reportJson = StripUtf8Bom(reportJson);
        using var doc = JsonDocument.Parse(reportJson);
        var root = doc.RootElement;
        var includeJsonDetails = options.IncludeRawJsonDetails;

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("<meta name=\"color-scheme\" content=\"dark light\">");
        sb.AppendLine("<title>Debugger MCP Report</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(GetCss());
        sb.AppendLine("</style>");
        sb.AppendLine("<script defer>");
        sb.AppendLine(GetJs());
        sb.AppendLine("</script>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine("<div class=\"layout\">");
        sb.AppendLine("<aside class=\"sidebar\">");
        RenderSidebar(sb, root, options);
        sb.AppendLine("</aside>");
        sb.AppendLine("<main class=\"main\">");

        RenderHeader(sb, root, options);
        if (TryGetAnalysis(root, out var analysisForAi) &&
            analysisForAi.TryGetProperty("aiAnalysis", out var aiAnalysis) &&
            aiAnalysis.ValueKind == JsonValueKind.Object)
        {
            RenderAiAnalysis(sb, root, includeJsonDetails);
        }
        RenderAtAGlance(sb, root);

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
            RenderSignature(sb, root, includeJsonDetails);
        }

        if (includeJsonDetails)
        {
            RenderRawJsonExplorer(sb, reportJson);
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
        sb.AppendLine("<button class=\"btn\" type=\"button\" data-action=\"toggle-theme\">Toggle theme</button>");

        sb.AppendLine("<nav class=\"nav\">");
        if (TryGetAnalysis(root, out var analysis) &&
            analysis.TryGetProperty("aiAnalysis", out var ai) &&
            ai.ValueKind == JsonValueKind.Object)
        {
            AppendNavLink(sb, "AI analysis", "#ai-analysis");
        }
        AppendNavLink(sb, "At a glance", "#at-a-glance");
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
            AppendNavLink(sb, "Signature", "#signature");
        }
        if (options.IncludeRawJsonDetails)
        {
            AppendNavLink(sb, "Raw JSON", "#raw-json");
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

        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"header-meta\">");
            sb.AppendLine("<div class=\"meta-chip\"><span class=\"meta-k\">Dump</span><span class=\"meta-v\"><code>" + HttpUtility.HtmlEncode(GetString(metadata, "dumpId")) + "</code></span></div>");
            sb.AppendLine("<div class=\"meta-chip\"><span class=\"meta-k\">Generated</span><span class=\"meta-v\"><code>" + HttpUtility.HtmlEncode(GetString(metadata, "generatedAt")) + "</code></span></div>");
            sb.AppendLine("<div class=\"meta-chip\"><span class=\"meta-k\">Debugger</span><span class=\"meta-v\"><code>" + HttpUtility.HtmlEncode(GetString(metadata, "debuggerType")) + "</code></span></div>");
            sb.AppendLine("<div class=\"meta-chip\"><span class=\"meta-k\">Server</span><span class=\"meta-v\"><code>" + HttpUtility.HtmlEncode(GetString(metadata, "serverVersion")) + "</code></span></div>");
            sb.AppendLine("</div>");
        }
        sb.AppendLine("</header>");
    }

    private static void RenderAiAnalysis(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("<section class=\"card\" id=\"ai-analysis\">");
        sb.AppendLine("<div class=\"section-title-row\">");
        sb.AppendLine("<h2>AI analysis</h2>");

        if (TryGetAnalysis(root, out var analysis) &&
            analysis.TryGetProperty("aiAnalysis", out var aiObj) &&
            aiObj.ValueKind == JsonValueKind.Object)
        {
            var confidence = GetString(aiObj, "confidence");
            var confidenceClass = GetConfidenceClass(confidence);
            if (!string.IsNullOrWhiteSpace(confidence))
            {
                sb.AppendLine("<span class=\"pill " + HttpUtility.HtmlEncode(confidenceClass) + "\">Confidence: <code>" + HttpUtility.HtmlEncode(confidence) + "</code></span>");
            }
        }
        sb.AppendLine("</div>");

        if (!TryGetAnalysis(root, out var analysisRoot) ||
            !analysisRoot.TryGetProperty("aiAnalysis", out var ai) ||
            ai.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"muted\">No AI analysis available.</div>");
            sb.AppendLine("</section>");
            return;
        }

        var rootCause = GetString(ai, "rootCause");
        if (!string.IsNullOrWhiteSpace(rootCause))
        {
            sb.AppendLine("<div class=\"callout\">");
            sb.AppendLine("<div class=\"callout-title\">Root cause</div>");
            sb.AppendLine("<div class=\"prose\"><pre class=\"prose-pre\">" + HttpUtility.HtmlEncode(rootCause) + "</pre></div>");
            sb.AppendLine("</div>");
        }
        else
        {
            sb.AppendLine("<div class=\"muted\">AI root cause not available.</div>");
        }

        sb.AppendLine("<div class=\"grid2\">");
        sb.AppendLine("<div class=\"panel\">");
        sb.AppendLine("<div class=\"panel-title\">Model &amp; run</div>");
        sb.AppendLine("<table class=\"kv\">");
        WriteKvRow(sb, "Model", ai, "model");
        WriteKvRow(sb, "Analyzed at (UTC)", ai, "analyzedAt");
        WriteKvRow(sb, "Iterations", ai, "iterations");
        sb.AppendLine("</table>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class=\"panel\">");
        sb.AppendLine("<div class=\"panel-title\">Notes</div>");
        var additionalFindings = GetArrayOrNull(ai, "additionalFindings");
        if (additionalFindings.HasValue && additionalFindings.Value.ValueKind == JsonValueKind.Array && additionalFindings.Value.GetArrayLength() > 0)
        {
            sb.AppendLine("<ul class=\"list\">");
            foreach (var finding in additionalFindings.Value.EnumerateArray())
            {
                var txt = finding.GetString();
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    sb.AppendLine("<li>" + HttpUtility.HtmlEncode(txt) + "</li>");
                }
            }
            sb.AppendLine("</ul>");
        }
        else
        {
            sb.AppendLine("<div class=\"muted\">(none)</div>");
        }
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        var reasoning = GetString(ai, "reasoning");
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            sb.AppendLine("<details class=\"details\" open>");
            sb.AppendLine("<summary>Reasoning</summary>");
            sb.AppendLine("<pre class=\"code wrap\"><code class=\"language-text\">" + HttpUtility.HtmlEncode(reasoning) + "</code></pre>");
            sb.AppendLine("</details>");
        }

        if (ai.TryGetProperty("recommendations", out var recs) && recs.ValueKind == JsonValueKind.Array && recs.GetArrayLength() > 0)
        {
            sb.AppendLine("<details class=\"details\" open>");
            sb.AppendLine("<summary>Recommendations</summary>");
            sb.AppendLine("<ul class=\"list\">");
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

        if (ai.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Array && evidence.GetArrayLength() > 0)
        {
            sb.AppendLine("<details class=\"details\">");
            sb.AppendLine("<summary>Evidence</summary>");
            sb.AppendLine("<ul class=\"list\">");
            foreach (var item in evidence.EnumerateArray())
            {
                var txt = item.GetString();
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    sb.AppendLine("<li>" + HttpUtility.HtmlEncode(txt) + "</li>");
                }
            }
            sb.AppendLine("</ul>");
            sb.AppendLine("</details>");
        }

        if (ai.TryGetProperty("hypotheses", out var hypotheses) && hypotheses.ValueKind == JsonValueKind.Array && hypotheses.GetArrayLength() > 0)
        {
            sb.AppendLine("<details class=\"details\">");
            sb.AppendLine("<summary>Hypotheses</summary>");
            sb.AppendLine("<div class=\"table-wrap\">");
            sb.AppendLine("<table class=\"table\">");
            sb.AppendLine("<thead><tr><th>ID</th><th>Confidence</th><th>Hypothesis</th><th>Notes</th></tr></thead>");
            sb.AppendLine("<tbody>");
            foreach (var h in hypotheses.EnumerateArray())
            {
                if (h.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                sb.AppendLine("<tr>");
                sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(GetString(h, "id")) + "</code></td>");
                sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(GetString(h, "confidence")) + "</code></td>");
                sb.AppendLine("<td>" + HttpUtility.HtmlEncode(GetString(h, "hypothesis")) + "</td>");
                sb.AppendLine("<td class=\"mono\">" + HttpUtility.HtmlEncode(GetString(h, "notes")) + "</td>");
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</tbody></table></div>");
            sb.AppendLine("</details>");
        }

        MaybeRenderJsonDetails(sb, "AI analysis JSON", ai, includeJsonDetails);
        sb.AppendLine("</section>");
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

    private static void RenderRawJsonExplorer(StringBuilder sb, string reportJson)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(reportJson));

        sb.AppendLine("<section class=\"card\" id=\"raw-json\">");
        sb.AppendLine("<div class=\"section-title-row\">");
        sb.AppendLine("<h2>Raw JSON</h2>");
        sb.AppendLine("<span class=\"pill\">Tip: use <code>Ctrl</code>+<code>F</code> to search in the raw view.</span>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class=\"toolbar\">");
        sb.AppendLine("<button class=\"btn-inline\" type=\"button\" data-action=\"json-copy\">Copy JSON</button>");
        sb.AppendLine("<button class=\"btn-inline\" type=\"button\" data-action=\"json-download\">Download JSON</button>");
        sb.AppendLine("<button class=\"btn-inline\" type=\"button\" data-action=\"json-expand\">Expand tree</button>");
        sb.AppendLine("<button class=\"btn-inline\" type=\"button\" data-action=\"json-collapse\">Collapse tree</button>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class=\"tabs\" role=\"tablist\" aria-label=\"Raw JSON views\">");
        sb.AppendLine("<button class=\"tab\" type=\"button\" role=\"tab\" aria-selected=\"true\" data-tab=\"tree\">Tree</button>");
        sb.AppendLine("<button class=\"tab\" type=\"button\" role=\"tab\" aria-selected=\"false\" data-tab=\"raw\">Raw</button>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class=\"tab-panels\">");
        sb.AppendLine("<div class=\"tab-panel\" data-panel=\"tree\">");
        sb.AppendLine("<div id=\"json-tree\" class=\"json-tree\" aria-label=\"JSON tree\"></div>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"tab-panel\" data-panel=\"raw\" hidden>");
        sb.AppendLine("<pre id=\"json-raw\" class=\"code\"><code class=\"language-json\"></code></pre>");
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<script id=\"dbg-mcp-report-json-b64\" type=\"text/plain\">" + base64 + "</script>");
        sb.AppendLine("</section>");
    }

    private static string StripUtf8Bom(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length > 0 && value[0] == '\uFEFF' ? value[1..] : value;
    }

    private static string GetConfidenceClass(string confidence)
    {
        if (string.IsNullOrWhiteSpace(confidence))
        {
            return "conf-unknown";
        }

        return confidence.Trim().ToLowerInvariant() switch
        {
            "high" => "conf-high",
            "medium" => "conf-medium",
            "low" => "conf-low",
            _ => "conf-unknown"
        };
    }

    private static string GetJs() =>
        """
        (() => {
          const THEME_KEY = "dbgMcpReportTheme";
          const html = document.documentElement;
          let reportJsonText = null;
          let reportJsonObject = null;

          function setTheme(theme) {
            html.dataset.theme = theme;
            try { localStorage.setItem(THEME_KEY, theme); } catch {}
          }

          function initTheme() {
            try {
              const saved = localStorage.getItem(THEME_KEY);
              if (saved === "light" || saved === "dark") { setTheme(saved); return; }
            } catch {}
            // default: prefer OS setting (CSS handles)
          }

          function wireThemeToggle() {
            const btn = document.querySelector("[data-action='toggle-theme']");
            if (!btn) return;
            btn.addEventListener("click", () => {
              const current = html.dataset.theme;
              setTheme(current === "light" ? "dark" : "light");
            });
          }

          function wireCopyButtons() {
            document.addEventListener("click", async (e) => {
              const target = e.target;
              if (!(target instanceof HTMLElement)) return;
              const btn = target.closest("[data-copy]");
              if (!(btn instanceof HTMLElement)) return;
              const text = btn.getAttribute("data-copy") || "";
              if (!text) return;
              try {
                await navigator.clipboard.writeText(text);
                btn.setAttribute("data-copied", "true");
                setTimeout(() => btn.removeAttribute("data-copied"), 1200);
              } catch {}
            });
          }

          function decodeBase64Utf8(base64) {
            const binary = atob(base64);
            const bytes = new Uint8Array(binary.length);
            for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
            return new TextDecoder("utf-8").decode(bytes);
          }

          function loadEmbeddedJson() {
            const script = document.getElementById("dbg-mcp-report-json-b64");
            if (!script) return;
            const base64 = (script.textContent || "").trim();
            if (!base64) return;
            try {
              reportJsonText = decodeBase64Utf8(base64);
              reportJsonObject = JSON.parse(reportJsonText);
            } catch {
              reportJsonText = null;
              reportJsonObject = null;
            }
          }

          function setTab(active) {
            document.querySelectorAll(".tab").forEach((t) => {
              const isActive = t.getAttribute("data-tab") === active;
              t.setAttribute("aria-selected", isActive ? "true" : "false");
              t.classList.toggle("active", isActive);
            });
            document.querySelectorAll(".tab-panel").forEach((p) => {
              const isActive = p.getAttribute("data-panel") === active;
              if (isActive) p.removeAttribute("hidden");
              else p.setAttribute("hidden", "");
            });
          }

          function wireTabs() {
            document.addEventListener("click", (e) => {
              const el = e.target;
              if (!(el instanceof HTMLElement)) return;
              const tab = el.closest(".tab");
              if (!(tab instanceof HTMLElement)) return;
              const name = tab.getAttribute("data-tab");
              if (name) setTab(name);
            });
          }

          function summarizeValue(value) {
            if (value === null) return "null";
            if (Array.isArray(value)) return `Array(${value.length})`;
            const t = typeof value;
            if (t === "string") return value.length > 80 ? JSON.stringify(value.slice(0, 77) + "...") : JSON.stringify(value);
            if (t === "number" || t === "boolean") return String(value);
            if (t === "object") return "Object";
            return t;
          }

          function buildCopyPathButton(path, stopToggle) {
            const btn = document.createElement("button");
            btn.className = "json-copy";
            btn.type = "button";
            btn.textContent = "copy path";
            btn.addEventListener("click", (e) => {
              if (stopToggle) {
                e.preventDefault();
                e.stopPropagation();
              }
              const toCopy = path || "$";
              navigator.clipboard?.writeText(toCopy).catch(() => {});
            });
            return btn;
          }

          function buildLeafRow(key, value, path) {
            const row = document.createElement("div");
            row.className = "json-row";

            const keyEl = document.createElement("span");
            keyEl.className = "json-key";
            keyEl.textContent = key;

            const previewEl = document.createElement("span");
            previewEl.className = "json-preview";
            previewEl.textContent = summarizeValue(value);

            const copyBtn = buildCopyPathButton(path, false);
            row.append(keyEl, previewEl, copyBtn);
            return row;
          }

          function buildTreeNode(key, value, path) {
            if (value === null || typeof value !== "object") {
              return buildLeafRow(key, value, path);
            }

            const details = document.createElement("details");
            details.className = "json-node";

            const summary = document.createElement("summary");
            summary.className = "json-summary";

            const keyEl = document.createElement("span");
            keyEl.className = "json-key";
            keyEl.textContent = key;

            const previewEl = document.createElement("span");
            previewEl.className = "json-preview";
            previewEl.textContent = summarizeValue(value);

            const copyBtn = buildCopyPathButton(path, true);
            summary.append(keyEl, previewEl, copyBtn);
            details.append(summary);

            const children = document.createElement("div");
            children.className = "json-children";
            details.append(children);

            function renderChildrenOnce() {
              if (details.dataset.rendered === "true") return;
              details.dataset.rendered = "true";

              if (Array.isArray(value)) {
                for (let i = 0; i < value.length; i++) {
                  const childKey = `[${i}]`;
                  const childPath = (path || "$") + childKey;
                  children.append(buildTreeNode(childKey, value[i], childPath));
                }
                return;
              }

              const keys = Object.keys(value);
              keys.sort();
              for (const k of keys) {
                const childPath = (path || "$") + "." + k;
                children.append(buildTreeNode(k, value[k], childPath));
              }
            }

            details.addEventListener("toggle", () => {
              if (details.open) renderChildrenOnce();
            });

            return details;
          }

          function renderRawJson() {
            const code = document.querySelector("#json-raw code");
            if (!code || !reportJsonText) return;
            try {
              const pretty = JSON.stringify(reportJsonObject, null, 2);
              code.textContent = pretty;
            } catch {
              code.textContent = reportJsonText;
            }
          }

          function renderJsonTree() {
            const container = document.getElementById("json-tree");
            if (!container || !reportJsonObject) return;
            container.textContent = "";
            const rootNode = buildTreeNode("root", reportJsonObject, "$");
            container.append(rootNode);
            if (rootNode instanceof HTMLDetailsElement) {
              rootNode.open = true;
              // Ensure first render happens even if the browser doesn't fire toggle for programmatic open.
              rootNode.dispatchEvent(new Event("toggle"));
            }
          }

          function tokenizeCSharp(text) {
            const keywords = new Set([
              "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue","decimal","default","delegate","do","double","else","enum","event","explicit","extern",
              "false","finally","fixed","float","for","foreach","goto","if","implicit","in","int","interface","internal","is","lock","long","namespace","new","null","object","operator","out","override",
              "params","private","protected","public","readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct","switch","this","throw","true","try","typeof",
              "uint","ulong","unchecked","unsafe","ushort","using","virtual","void","volatile","while",
              "async","await","record","init","with","when","yield","var"
            ]);

            function push(tokens, cls, value) {
              if (!value) return;
              const last = tokens.length ? tokens[tokens.length - 1] : null;
              if (last && last[0] === cls) { last[1] += value; return; }
              tokens.push([cls, value]);
            }

            function readUntilNewline(start) {
              let end = text.indexOf("\n", start);
              if (end === -1) end = text.length;
              return end;
            }

            function readBlockComment(start) {
              const end = text.indexOf("*/", start + 2);
              return end === -1 ? text.length : end + 2;
            }

            function readNormalString(start, quote) {
              let i = start + 1;
              while (i < text.length) {
                const ch = text[i];
                if (ch === "\\\\") { i += 2; continue; }
                if (ch === quote) return i + 1;
                i++;
              }
              return text.length;
            }

            function readVerbatimString(start) {
              // start at @"
              let i = start + 2;
              while (i < text.length) {
                if (text[i] === "\"" && text[i + 1] === "\"") { i += 2; continue; }
                if (text[i] === "\"") return i + 1;
                i++;
              }
              return text.length;
            }

            function readIdentifier(start) {
              let i = start;
              if (text[i] === "@") i++;
              while (i < text.length) {
                const ch = text[i];
                if ((ch >= "a" && ch <= "z") || (ch >= "A" && ch <= "Z") || (ch >= "0" && ch <= "9") || ch === "_") { i++; continue; }
                break;
              }
              return i;
            }

            function readNumber(start) {
              let i = start;
              if (text[i] === "0" && (text[i + 1] === "x" || text[i + 1] === "X")) {
                i += 2;
                while (i < text.length) {
                  const ch = text[i];
                  if ((ch >= "0" && ch <= "9") || (ch >= "a" && ch <= "f") || (ch >= "A" && ch <= "F") || ch === "_") { i++; continue; }
                  break;
                }
                return i;
              }
              while (i < text.length) {
                const ch = text[i];
                if ((ch >= "0" && ch <= "9") || ch === "_" || ch === ".") { i++; continue; }
                if (ch === "e" || ch === "E") {
                  i++;
                  if (text[i] === "+" || text[i] === "-") i++;
                  continue;
                }
                break;
              }
              return i;
            }

            const tokens = [];
            let i = 0;
            while (i < text.length) {
              const ch = text[i];

              // Comments
              if (ch === "/" && text[i + 1] === "/") {
                const end = readUntilNewline(i);
                push(tokens, "tok-com", text.slice(i, end));
                i = end;
                continue;
              }
              if (ch === "/" && text[i + 1] === "*") {
                const end = readBlockComment(i);
                push(tokens, "tok-com", text.slice(i, end));
                i = end;
                continue;
              }

              // Strings
              if (ch === "@" && text[i + 1] === "\"") {
                const end = readVerbatimString(i);
                push(tokens, "tok-str", text.slice(i, end));
                i = end;
                continue;
              }
              if (ch === "$" && text[i + 1] === "\"") {
                const end = readNormalString(i + 1, "\"");
                push(tokens, "tok-str", text.slice(i, end));
                i = end;
                continue;
              }
              if (ch === "$" && text[i + 1] === "@" && text[i + 2] === "\"") {
                const end = readVerbatimString(i + 1);
                push(tokens, "tok-str", text.slice(i, end));
                i = end;
                continue;
              }
              if (ch === "\"" || ch === "'") {
                const end = readNormalString(i, ch);
                push(tokens, "tok-str", text.slice(i, end));
                i = end;
                continue;
              }

              // Numbers
              if (ch >= "0" && ch <= "9") {
                const end = readNumber(i);
                push(tokens, "tok-num", text.slice(i, end));
                i = end;
                continue;
              }

              // Identifiers/keywords
              const isIdentStart = (ch >= "a" && ch <= "z") || (ch >= "A" && ch <= "Z") || ch === "_" || (ch === "@" && (((text[i + 1] >= "a" && text[i + 1] <= "z") || (text[i + 1] >= "A" && text[i + 1] <= "Z") || text[i + 1] === "_")));
              if (isIdentStart) {
                const end = readIdentifier(i);
                const raw = text.slice(i, end);
                const word = raw.startsWith("@") ? raw.slice(1) : raw;
                if (word === "true" || word === "false" || word === "null") {
                  push(tokens, "tok-lit", raw);
                } else if (keywords.has(word)) {
                  push(tokens, "tok-kw", raw);
                } else if (word.length && word[0] >= "A" && word[0] <= "Z") {
                  push(tokens, "tok-type", raw);
                } else {
                  push(tokens, null, raw);
                }
                i = end;
                continue;
              }

              push(tokens, null, ch);
              i++;
            }

            return tokens;
          }

          function tokenizeJson(text) {
            function push(tokens, cls, value) {
              if (!value) return;
              const last = tokens.length ? tokens[tokens.length - 1] : null;
              if (last && last[0] === cls) { last[1] += value; return; }
              tokens.push([cls, value]);
            }

            function readString(start) {
              let i = start + 1;
              while (i < text.length) {
                const ch = text[i];
                if (ch === "\\\\") { i += 2; continue; }
                if (ch === "\"") return i + 1;
                i++;
              }
              return text.length;
            }

            function readNumber(start) {
              let i = start;
              if (text[i] === "-") i++;
              while (i < text.length && text[i] >= "0" && text[i] <= "9") i++;
              if (text[i] === ".") { i++; while (i < text.length && text[i] >= "0" && text[i] <= "9") i++; }
              if (text[i] === "e" || text[i] === "E") {
                i++;
                if (text[i] === "+" || text[i] === "-") i++;
                while (i < text.length && text[i] >= "0" && text[i] <= "9") i++;
              }
              return i;
            }

            const tokens = [];
            let i = 0;
            while (i < text.length) {
              const ch = text[i];
              if (ch === "\"") {
                const end = readString(i);
                push(tokens, "tok-str", text.slice(i, end));
                i = end;
                continue;
              }
              if (ch === "-" || (ch >= "0" && ch <= "9")) {
                const end = readNumber(i);
                push(tokens, "tok-num", text.slice(i, end));
                i = end;
                continue;
              }
              if (text.startsWith("true", i) || text.startsWith("false", i) || text.startsWith("null", i)) {
                const lit = text.startsWith("true", i) ? "true" : (text.startsWith("false", i) ? "false" : "null");
                push(tokens, "tok-lit", lit);
                i += lit.length;
                continue;
              }
              if ("{}[]:,".includes(ch)) {
                push(tokens, "tok-punc", ch);
                i++;
                continue;
              }
              push(tokens, null, ch);
              i++;
            }
            return tokens;
          }

          function renderTokens(code, tokens) {
            code.textContent = "";
            for (const t of tokens) {
              const cls = t[0];
              const val = t[1];
              if (!cls) {
                code.appendChild(document.createTextNode(val));
                continue;
              }
              const span = document.createElement("span");
              span.className = cls;
              span.textContent = val;
              code.appendChild(span);
            }
          }

          function parseIntOrNull(value) {
            if (value === null || value === undefined) return null;
            const n = Number.parseInt(String(value), 10);
            return Number.isFinite(n) ? n : null;
          }

          function splitTokensToLines(tokens) {
            const lines = [[]];
            for (const t of tokens) {
              const cls = t[0];
              const val = t[1] ?? "";
              const parts = String(val).split("\n");
              for (let i = 0; i < parts.length; i++) {
                const part = parts[i];
                if (part) lines[lines.length - 1].push([cls, part]);
                if (i < parts.length - 1) lines.push([]);
              }
            }

            // Drop trailing empty line caused by ending newline.
            if (lines.length > 1 && lines[lines.length - 1].length === 0) lines.pop();
            return lines;
          }

          function renderTokensWithLineNumbers(code, tokens, startLine, focusLine) {
            code.textContent = "";
            code.classList.add("with-linenos");

            const lines = splitTokensToLines(tokens);
            const base = Number.isFinite(startLine) ? startLine : 1;
            const focus = Number.isFinite(focusLine) ? focusLine : null;

            for (let i = 0; i < lines.length; i++) {
              const lineNo = base + i;
              const row = document.createElement("div");
              row.className = "code-line";
              row.dataset.line = String(lineNo);
              if (focus !== null && lineNo === focus) row.classList.add("focus");

              const ln = document.createElement("span");
              ln.className = "code-lineno";
              ln.textContent = String(lineNo);

              const content = document.createElement("span");
              content.className = "code-content";

              for (const t of lines[i]) {
                const cls = t[0];
                const val = t[1];
                if (!cls) {
                  content.appendChild(document.createTextNode(val));
                  continue;
                }
                const span = document.createElement("span");
                span.className = cls;
                span.textContent = val;
                content.appendChild(span);
              }

              row.appendChild(ln);
              row.appendChild(content);
              code.appendChild(row);
            }
          }

          function highlightSourceCode() {
            document.querySelectorAll("code.source-code").forEach((code) => {
              if (!(code instanceof HTMLElement)) return;
              if (code.dataset.highlighted === "true") return;
              const lang = (code.dataset.lang || "").toLowerCase();
              const showLineNos = (code.dataset.showLinenos || "").toLowerCase() === "true";
              const text = code.textContent || "";
              let tokens = null;
              if (lang === "csharp") tokens = tokenizeCSharp(text);
              else if (lang === "json") tokens = tokenizeJson(text);
              else if (showLineNos) tokens = [[null, text]];
              if (!tokens) return;

              const startLine = parseIntOrNull(code.dataset.startLine);
              const focusLine = parseIntOrNull(code.dataset.focusLine);
              if (showLineNos) renderTokensWithLineNumbers(code, tokens, startLine, focusLine);
              else renderTokens(code, tokens);

              code.dataset.highlighted = "true";
            });
          }

          function wireJsonToolbar() {
            document.addEventListener("click", async (e) => {
              const el = e.target;
              if (!(el instanceof HTMLElement)) return;
              const action = el.getAttribute("data-action");
              if (!action) return;

              if (action === "json-copy") {
                if (!reportJsonText) return;
                try { await navigator.clipboard.writeText(reportJsonText); } catch {}
              } else if (action === "json-download") {
                if (!reportJsonText) return;
                const blob = new Blob([reportJsonText], { type: "application/json" });
                const url = URL.createObjectURL(blob);
                const a = document.createElement("a");
                a.href = url;
                a.download = "debugger-mcp-report.json";
                document.body.appendChild(a);
                a.click();
                a.remove();
                URL.revokeObjectURL(url);
              } else if (action === "json-expand") {
                document.querySelectorAll("#json-tree details").forEach((d) => {
                  if (!d.open) {
                    d.open = true;
                    d.dispatchEvent(new Event("toggle"));
                  }
                });
              } else if (action === "json-collapse") {
                document.querySelectorAll("#json-tree details").forEach((d) => { d.open = false; });
                const root = document.querySelector("#json-tree details");
                if (root) root.open = true;
              }
            });
          }

          document.addEventListener("DOMContentLoaded", () => {
            initTheme();
            wireThemeToggle();
            wireCopyButtons();
            wireTabs();
            wireJsonToolbar();
            loadEmbeddedJson();
            renderRawJson();
            renderJsonTree();
            highlightSourceCode();
          });
        })();
        """;

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

            var total = GetLongFlexible(summary, "total") ?? 0;
            var foreground = GetLongFlexible(summary, "foreground") ?? 0;
            var background = GetLongFlexible(summary, "background") ?? 0;
            var dead = GetLongFlexible(summary, "dead") ?? 0;
            var unstarted = GetLongFlexible(summary, "unstarted") ?? 0;
            var pending = GetLongFlexible(summary, "pending") ?? 0;

            if (total > 0)
            {
                sb.AppendLine("<div class=\"chart\" aria-label=\"Thread distribution\">");
                if (foreground > 0) RenderBarRow(sb, "Foreground", foreground, total, "ok");
                if (background > 0) RenderBarRow(sb, "Background", background, total, "");
                if (dead > 0) RenderBarRow(sb, "Dead", dead, total, "danger");
                if (unstarted > 0) RenderBarRow(sb, "Unstarted", unstarted, total, "warn");
                if (pending > 0) RenderBarRow(sb, "Pending", pending, total, "warn");
                sb.AppendLine("</div>");
            }

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
            if (IsSafeExternalUrl(sourceUrl))
            {
                sb.AppendLine("<div class=\"muted\">Source URL: <a href=\"" + HttpUtility.HtmlEncode(sourceUrl) + "\" target=\"_blank\" rel=\"noopener noreferrer\">" + HttpUtility.HtmlEncode(sourceUrl) + "</a></div>");
            }
            else
            {
                sb.AppendLine("<div class=\"muted\">Source URL: <code>" + HttpUtility.HtmlEncode(sourceUrl) + "</code></div>");
            }
        }
        if (!string.IsNullOrWhiteSpace(sourceRawUrl))
        {
            if (IsSafeExternalUrl(sourceRawUrl))
            {
                sb.AppendLine("<div class=\"muted\">Raw URL: <a href=\"" + HttpUtility.HtmlEncode(sourceRawUrl) + "\" target=\"_blank\" rel=\"noopener noreferrer\">" + HttpUtility.HtmlEncode(sourceRawUrl) + "</a></div>");
            }
            else
            {
                sb.AppendLine("<div class=\"muted\">Raw URL: <code>" + HttpUtility.HtmlEncode(sourceRawUrl) + "</code></div>");
            }
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
                    var langClass = string.IsNullOrEmpty(lang) ? "language-text" : "language-" + lang;
                    var startLine = GetLongFlexible(sc, "startLine");
                    var focusLine = GetLongFlexible(frame, "lineNumber");
                    sb.AppendLine("<pre class=\"code source\"><code class=\"" + HttpUtility.HtmlEncode(langClass + " source-code") + "\" data-lang=\"" + HttpUtility.HtmlEncode(string.IsNullOrEmpty(lang) ? "text" : lang) + "\" data-show-linenos=\"true\"" +
                                  (startLine.HasValue ? " data-start-line=\"" + HttpUtility.HtmlEncode(startLine.Value.ToString(CultureInfo.InvariantCulture)) + "\"" : string.Empty) +
                                  (focusLine.HasValue ? " data-focus-line=\"" + HttpUtility.HtmlEncode(focusLine.Value.ToString(CultureInfo.InvariantCulture)) + "\"" : string.Empty) +
                                  ">");
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

            var generationSizes = GetObjectOrNull(gc.Value, "generationSizes");
            if (generationSizes.HasValue)
            {
                var gen0 = GetDoubleFlexible(generationSizes.Value, "gen0") ?? 0;
                var gen1 = GetDoubleFlexible(generationSizes.Value, "gen1") ?? 0;
                var gen2 = GetDoubleFlexible(generationSizes.Value, "gen2") ?? 0;
                var loh = GetDoubleFlexible(generationSizes.Value, "loh") ?? 0;
                var poh = GetDoubleFlexible(generationSizes.Value, "poh") ?? 0;
                var total = gen0 + gen1 + gen2 + loh + poh;

                if (total > 0)
                {
                    sb.AppendLine("<div class=\"panel-sub\">Generation sizes (bytes)</div>");
                    sb.AppendLine("<div class=\"chart\" aria-label=\"GC generation sizes\">");
                    if (gen0 > 0) RenderBarRow(sb, "Gen0", gen0, total, "");
                    if (gen1 > 0) RenderBarRow(sb, "Gen1", gen1, total, "");
                    if (gen2 > 0) RenderBarRow(sb, "Gen2", gen2, total, "");
                    if (loh > 0) RenderBarRow(sb, "LOH", loh, total, "warn");
                    if (poh > 0) RenderBarRow(sb, "POH", poh, total, "warn");
                    sb.AppendLine("</div>");
                }
            }

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
                if (IsSafeExternalUrl(url))
                {
                    sb.AppendLine("<td><a href=\"" + HttpUtility.HtmlEncode(url) + "\" target=\"_blank\" rel=\"noopener noreferrer\">" + HttpUtility.HtmlEncode(url) + "</a></td>");
                }
                else
                {
                    sb.AppendLine("<td><code>" + HttpUtility.HtmlEncode(url) + "</code></td>");
                }
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

    private static void RenderSignature(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("<section class=\"card\" id=\"signature\">");
        sb.AppendLine("<h2>Signature</h2>");

        if (!TryGetAnalysis(root, out var analysis))
        {
            sb.AppendLine("<div class=\"muted\">No analysis available.</div>");
            sb.AppendLine("</section>");
            return;
        }

        if (analysis.TryGetProperty("signature", out var signature) && signature.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("<div class=\"panel\">");
            sb.AppendLine("<table class=\"kv\">");
            WriteKvRow(sb, "Kind", signature, "kind");
            WriteKvRow(sb, "Hash", signature, "hash");
            sb.AppendLine("</table>");
            MaybeRenderJsonDetails(sb, "Signature JSON", signature, includeJsonDetails);
            sb.AppendLine("</div>");
            sb.AppendLine("</section>");
            return;
        }

        sb.AppendLine("<div class=\"muted\">No signature available.</div>");
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

    private static JsonElement? GetArrayOrNull(JsonElement obj, string property)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(property, out var v) &&
            v.ValueKind == JsonValueKind.Array)
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

    private static long? GetLongFlexible(JsonElement obj, string property)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(property, out var v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
        {
            return n;
        }

        if (v.ValueKind == JsonValueKind.String &&
            long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? GetDoubleFlexible(JsonElement obj, string property)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(property, out var v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n))
        {
            return n;
        }

        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static void RenderBarRow(StringBuilder sb, string label, double value, double total, string styleClass)
    {
        if (total <= 0)
        {
            return;
        }

        var pct = Math.Clamp((value / total) * 100.0, 0, 100);
        sb.AppendLine("<div class=\"bar-row\">");
        sb.AppendLine("<div class=\"bar-label\">" + HttpUtility.HtmlEncode(label) + "</div>");
        sb.AppendLine("<div class=\"bar-track\"><div class=\"bar-fill " + HttpUtility.HtmlEncode(styleClass) + "\" style=\"width:" + pct.ToString("0.##", CultureInfo.InvariantCulture) + "%\"></div></div>");
        sb.AppendLine("<div class=\"bar-value\">" + HttpUtility.HtmlEncode(value.ToString("0", CultureInfo.InvariantCulture)) + "</div>");
        sb.AppendLine("</div>");
    }

    private static string SerializeIndented(JsonElement element) =>
        JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });

    private static bool IsSafeExternalUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

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
        html { scroll-behavior: smooth; }
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
          --danger: #fb7185;
          --sep: rgba(37,48,90,0.35);
          --tok-kw: #7aa2ff;
          --tok-type: #c084fc;
          --tok-str: #fbbf24;
          --tok-com: rgba(167,176,214,0.85);
          --tok-num: #2dd4bf;
          --tok-lit: #fb7185;
        }
        @media (prefers-color-scheme: light) {
          :root:not([data-theme="dark"]) {
            --bg: #f6f7fb;
            --card: #ffffff;
            --panel: rgba(17,24,39,0.04);
            --text: #0b1020;
            --muted: #4b5563;
            --border: #e5e7eb;
            --accent: #2563eb;
            --ok: #0f766e;
            --warn: #b45309;
            --danger: #be123c;
            --sep: rgba(17,24,39,0.10);
            --tok-kw: #1d4ed8;
            --tok-type: #7c3aed;
            --tok-str: #b45309;
            --tok-com: rgba(75,85,99,0.85);
            --tok-num: #0f766e;
            --tok-lit: #be123c;
          }
        }
        :root[data-theme="light"] {
          --bg: #f6f7fb;
          --card: #ffffff;
          --panel: rgba(17,24,39,0.04);
          --text: #0b1020;
          --muted: #4b5563;
          --border: #e5e7eb;
          --accent: #2563eb;
          --ok: #0f766e;
          --warn: #b45309;
          --danger: #be123c;
          --sep: rgba(17,24,39,0.10);
          --tok-kw: #1d4ed8;
          --tok-type: #7c3aed;
          --tok-str: #b45309;
          --tok-com: rgba(75,85,99,0.85);
          --tok-num: #0f766e;
          --tok-lit: #be123c;
        }
        body { margin: 0; font-family: system-ui, -apple-system, Segoe UI, sans-serif; background: var(--bg); color: var(--text); font-size: 14px; line-height: 1.5; }
        .layout { display: grid; grid-template-columns: 260px 1fr; min-height: 100vh; }
        .sidebar { position: sticky; top: 0; height: 100vh; overflow: auto; border-right: 1px solid var(--border); padding: 18px 14px; background: rgba(18,26,51,0.35); }
        .main { padding: 24px; max-width: 1100px; }
        .brand { font-size: 14px; font-weight: 800; letter-spacing: 0.06em; text-transform: uppercase; color: var(--muted); }
        .brand-sub { margin-top: 6px; font-size: 16px; font-weight: 800; }
        .brand-note { margin-top: 8px; color: var(--muted); font-size: 12px; }
        .btn { margin-top: 12px; display: inline-flex; align-items: center; justify-content: center; width: 100%; padding: 10px 12px; border-radius: 12px; border: 1px solid rgba(37,48,90,0.35); background: rgba(11,16,32,0.25); color: var(--text); cursor: pointer; }
        :root[data-theme="light"] .btn { border-color: rgba(17,24,39,0.12); background: rgba(17,24,39,0.03); }
        .btn:hover { border-color: rgba(122,162,255,0.35); background: rgba(122,162,255,0.08); }
        :root[data-theme="light"] .btn:hover { border-color: rgba(37,99,235,0.30); background: rgba(37,99,235,0.06); }
        .nav { margin-top: 16px; display: flex; flex-direction: column; gap: 6px; }
        .nav-link { display: block; padding: 8px 10px; border-radius: 10px; border: 1px solid rgba(37,48,90,0.35); background: rgba(11,16,32,0.25); color: var(--text); }
        .nav-link:hover { border-color: rgba(122,162,255,0.35); background: rgba(122,162,255,0.08); }
        .sidebar-meta { margin-top: 16px; padding-top: 14px; border-top: 1px solid rgba(37,48,90,0.55); }
        .kv-mini { display: flex; flex-direction: column; gap: 8px; }
        .kv-mini-row { display: grid; grid-template-columns: 88px 1fr; gap: 8px; }
        .kv-mini-k { color: var(--muted); font-size: 12px; }
        .kv-mini-v { word-break: break-word; }
        .header { padding: 18px 20px; border: 1px solid var(--border); border-radius: 14px; background: linear-gradient(180deg, rgba(122,162,255,0.12), rgba(18,26,51,0.85)); }
        .header-title { font-size: 20px; font-weight: 750; }
        .header-subtitle { margin-top: 6px; color: var(--muted); }
        .header-meta { margin-top: 12px; display: flex; flex-wrap: wrap; gap: 8px; }
        .meta-chip { display: inline-flex; gap: 8px; align-items: center; padding: 8px 10px; border-radius: 12px; border: 1px solid rgba(37,48,90,0.35); background: rgba(11,16,32,0.25); }
        :root[data-theme="light"] .meta-chip { border-color: rgba(17,24,39,0.12); background: rgba(17,24,39,0.03); }
        .meta-k { color: var(--muted); font-size: 12px; font-weight: 600; }
        .meta-v { font-size: 12px; }
        h2 { margin: 0 0 12px 0; font-size: 17px; }
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
        .muted { color: var(--muted); margin-top: 6px; font-size: 12px; line-height: 1.4; overflow-wrap: anywhere; word-break: break-word; }
        .pill { display: inline-block; padding: 6px 10px; border-radius: 999px; background: rgba(122,162,255,0.18); border: 1px solid rgba(122,162,255,0.28); }
        .pill.conf-high { background: rgba(45,212,191,0.15); border-color: rgba(45,212,191,0.35); }
        .pill.conf-medium { background: rgba(251,191,36,0.12); border-color: rgba(251,191,36,0.35); }
        .pill.conf-low { background: rgba(251,113,133,0.10); border-color: rgba(251,113,133,0.35); }
        .pill.conf-unknown { background: rgba(167,176,214,0.10); border-color: rgba(167,176,214,0.25); }
        .section-title-row { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; }
        .callout { margin-top: 10px; padding: 14px 14px; border-radius: 14px; border: 1px solid rgba(122,162,255,0.25); background: linear-gradient(180deg, rgba(122,162,255,0.10), rgba(18,26,51,0.10)); }
        :root[data-theme="light"] .callout { background: linear-gradient(180deg, rgba(37,99,235,0.08), rgba(17,24,39,0.02)); border-color: rgba(37,99,235,0.18); }
        .callout-title { font-weight: 800; letter-spacing: 0.02em; margin-bottom: 8px; }
        .prose-pre { margin: 0; white-space: pre-wrap; word-break: break-word; font-family: system-ui, -apple-system, Segoe UI, sans-serif; }
        .details { margin-top: 10px; }
        .details > summary { cursor: pointer; color: var(--accent); }
        .list { margin: 10px 0 0 18px; padding: 0; }
        .list li { margin: 6px 0; }
        .chart { margin-top: 12px; display: flex; flex-direction: column; gap: 10px; }
        .bar-row { display: grid; grid-template-columns: 140px 1fr 74px; gap: 10px; align-items: center; }
        @media (max-width: 980px) { .bar-row { grid-template-columns: 120px 1fr 64px; } }
        .bar-label { color: var(--muted); font-size: 12px; }
        .bar-track { height: 10px; border-radius: 999px; border: 1px solid rgba(37,48,90,0.55); background: rgba(11,16,32,0.25); overflow: hidden; }
        :root[data-theme="light"] .bar-track { border-color: rgba(17,24,39,0.10); background: rgba(17,24,39,0.04); }
        .bar-fill { height: 100%; border-radius: 999px; background: var(--accent); width: 0%; }
        .bar-fill.ok { background: rgba(45,212,191,0.9); }
        .bar-fill.warn { background: rgba(251,191,36,0.9); }
        .bar-fill.danger { background: rgba(251,113,133,0.9); }
        .bar-value { text-align: right; font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace; font-size: 12px; color: var(--muted); }
        .toolbar { margin-top: 12px; display: flex; flex-wrap: wrap; gap: 8px; }
        .btn-inline { display: inline-flex; align-items: center; justify-content: center; padding: 8px 10px; border-radius: 10px; border: 1px solid rgba(37,48,90,0.35); background: rgba(11,16,32,0.25); color: var(--text); cursor: pointer; }
        :root[data-theme="light"] .btn-inline { border-color: rgba(17,24,39,0.12); background: rgba(17,24,39,0.03); }
        .btn-inline:hover { border-color: rgba(122,162,255,0.35); background: rgba(122,162,255,0.08); }
        :root[data-theme="light"] .btn-inline:hover { border-color: rgba(37,99,235,0.30); background: rgba(37,99,235,0.06); }
        .tabs { margin-top: 12px; display: inline-flex; gap: 8px; }
        .tab { padding: 8px 10px; border-radius: 10px; border: 1px solid rgba(37,48,90,0.35); background: rgba(11,16,32,0.12); color: var(--text); cursor: pointer; }
        .tab.active, .tab[aria-selected="true"] { background: rgba(122,162,255,0.16); border-color: rgba(122,162,255,0.28); }
        .tab-panels { margin-top: 12px; }
        .json-tree { display: flex; flex-direction: column; gap: 8px; margin-top: 6px; }
        details.json-node { border: 1px solid rgba(37,48,90,0.55); border-radius: 12px; background: rgba(11,16,32,0.18); padding: 8px 10px; }
        :root[data-theme="light"] details.json-node { border-color: rgba(17,24,39,0.10); background: rgba(17,24,39,0.02); }
        details.json-node > summary { list-style: none; cursor: pointer; }
        details.json-node > summary::-webkit-details-marker { display: none; }
        .json-summary { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
        .json-row { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; padding: 8px 10px; border: 1px solid rgba(37,48,90,0.55); border-radius: 12px; background: rgba(11,16,32,0.18); }
        :root[data-theme="light"] .json-row { border-color: rgba(17,24,39,0.10); background: rgba(17,24,39,0.02); }
        .json-key { font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace; font-weight: 700; }
        .json-preview { font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace; color: var(--muted); font-size: 12px; }
        .json-copy { margin-left: auto; padding: 6px 8px; border-radius: 10px; border: 1px solid rgba(37,48,90,0.35); background: rgba(11,16,32,0.25); color: var(--muted); cursor: pointer; }
        :root[data-theme="light"] .json-copy { border-color: rgba(17,24,39,0.12); background: rgba(17,24,39,0.03); }
        .json-copy:hover { color: var(--text); border-color: rgba(122,162,255,0.35); }
        .json-children { margin-top: 10px; padding-left: 12px; border-left: 1px dashed rgba(37,48,90,0.55); display: flex; flex-direction: column; gap: 8px; }
        .stack { margin: 12px 0 0 20px; padding: 0; }
        .frame { margin: 0; padding: 12px 0; border-top: 1px solid var(--sep); }
        .frame:first-child { border-top: none; }
        .frame-title { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; overflow-wrap: anywhere; word-break: break-word; }
        .frame-title code { overflow-wrap: anywhere; word-break: break-word; }
        .badge { font-size: 12px; padding: 2px 8px; border-radius: 999px; border: 1px solid var(--border); color: var(--muted); }
        .badge.managed { border-color: rgba(45,212,191,0.5); color: var(--ok); }
        .badge.native { border-color: rgba(251,191,36,0.5); color: var(--warn); }
        .code { overflow: auto; padding: 12px; border-radius: 10px; border: 1px solid var(--border); background: rgba(11,16,32,0.6); font-size: 12px; line-height: 1.45; }
        :root[data-theme="light"] .code { background: rgba(17,24,39,0.03); }
        pre.code code { font-size: inherit; }
        .code.wrap { white-space: pre-wrap; overflow-wrap: anywhere; word-break: break-word; }
        .code.wrap code { white-space: pre-wrap; }
        pre.code.source code.with-linenos { display: block; }
        pre.code.source code.with-linenos .code-line { display: grid; grid-template-columns: 56px 1fr; gap: 12px; align-items: start; padding: 2px 8px; border-radius: 8px; }
        pre.code.source code.with-linenos .code-line.focus { background: rgba(122,162,255,0.10); }
        :root[data-theme="light"] pre.code.source code.with-linenos .code-line.focus { background: rgba(37,99,235,0.08); }
        pre.code.source code.with-linenos .code-lineno { color: var(--muted); text-align: right; user-select: none; font-variant-numeric: tabular-nums; padding-right: 10px; border-right: 1px solid rgba(37,48,90,0.55); }
        :root[data-theme="light"] pre.code.source code.with-linenos .code-lineno { border-right-color: rgba(17,24,39,0.12); }
        pre.code.source code.with-linenos .code-content { white-space: pre; overflow-wrap: normal; word-break: normal; }
        code { font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace; font-size: 12px; }
        a { color: var(--accent); text-decoration: none; overflow-wrap: anywhere; word-break: break-word; }
        a:hover { text-decoration: underline; }
        .tok-kw { color: var(--tok-kw); font-weight: 650; }
        .tok-type { color: var(--tok-type); }
        .tok-str { color: var(--tok-str); }
        .tok-com { color: var(--tok-com); font-style: italic; }
        .tok-num { color: var(--tok-num); }
        .tok-lit { color: var(--tok-lit); font-weight: 650; }
        .tok-punc { color: var(--muted); }
        .alert { margin-top: 10px; padding: 10px 12px; border-radius: 10px; border: 1px solid rgba(251,191,36,0.5); background: rgba(251,191,36,0.1); }
        .table-wrap { overflow: auto; border: 1px solid rgba(37,48,90,0.55); border-radius: 12px; margin-top: 10px; }
        .table { width: 100%; border-collapse: collapse; min-width: 860px; }
        .table th, .table td { padding: 10px 10px; border-top: 1px solid rgba(37,48,90,0.55); vertical-align: top; }
        .table th { text-align: left; color: var(--muted); font-weight: 700; background: rgba(11,16,32,0.35); position: sticky; top: 0; }
        .mono { font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace; font-size: 12.5px; overflow-wrap: anywhere; word-break: break-word; }
        """;
}
