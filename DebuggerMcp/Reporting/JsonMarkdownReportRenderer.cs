using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DebuggerMcp.Reporting;

/// <summary>
/// Renders the canonical JSON report document into a human-readable Markdown report.
/// </summary>
/// <remarks>
/// The JSON report is treated as the source of truth; this renderer renders from JSON to avoid drift.
/// The output may include raw JSON detail blocks when <see cref="ReportOptions.IncludeRawJsonDetails"/> is enabled.
/// </remarks>
internal static class JsonMarkdownReportRenderer
{
    internal static string Render(string reportJson, ReportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var doc = JsonDocument.Parse(reportJson);
        var root = doc.RootElement;

        var sb = new StringBuilder();
        var includeJsonDetails = options.IncludeRawJsonDetails;

        var hasAiAnalysis = TryGetAnalysis(root, out var analysisForAi) &&
            analysisForAi.TryGetProperty("aiAnalysis", out var aiAnalysis) &&
            aiAnalysis.ValueKind == JsonValueKind.Object;

        AppendTitle(sb, root, options);
        AppendTableOfContents(sb, options, hasAiAnalysis);

        if (hasAiAnalysis)
        {
            AppendAiAnalysis(sb, root, includeJsonDetails);
        }

        AppendAtAGlance(sb, root);

        if (options.IncludeCallStacks)
        {
            AppendFaultingThread(sb, root, includeJsonDetails);
        }

        if (options.IncludeThreadInfo)
        {
            AppendThreads(sb, root, includeJsonDetails);
        }

        if (options.IncludeProcessInfo || options.IncludeDotNetInfo)
        {
            AppendEnvironment(sb, root, includeJsonDetails);
        }

        if (options.IncludeHeapStats || options.IncludeMemoryLeakInfo)
        {
            AppendMemory(sb, root, includeJsonDetails);
        }

        if (options.IncludeDeadlockInfo)
        {
            AppendSynchronization(sb, root, includeJsonDetails);
        }

        if (options.IncludeSecurityAnalysis)
        {
            AppendSecurity(sb, root, includeJsonDetails);
        }

        if (options.IncludeDotNetInfo)
        {
            AppendAssemblies(sb, root, includeJsonDetails);
        }

        if (options.IncludeModules)
        {
            AppendModules(sb, root, includeJsonDetails);
        }

        if (options.IncludeModules || options.IncludeDotNetInfo)
        {
            AppendSymbols(sb, root, includeJsonDetails);
        }

        if (options.IncludeThreadInfo)
        {
            AppendTimeline(sb, root, includeJsonDetails);
        }

        if (options.IncludeCallStacks && options.IncludeDotNetInfo)
        {
            AppendSourceContextIndex(sb, root, includeJsonDetails);
            AppendSignature(sb, root, includeJsonDetails);
        }

        return sb.ToString();
    }

    private static void AppendTitle(StringBuilder sb, JsonElement root, ReportOptions options)
    {
        sb.AppendLine("# Debugger MCP Report");
        sb.AppendLine();

        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            var dumpId = GetString(metadata, "dumpId");
            var generatedAt = GetString(metadata, "generatedAt");
            var debuggerType = GetString(metadata, "debuggerType");
            var serverVersion = GetString(metadata, "serverVersion");

            var line = new List<string>();
            if (!string.IsNullOrWhiteSpace(dumpId))
            {
                line.Add($"Dump: `{EscapeInline(dumpId)}`");
            }
            if (!string.IsNullOrWhiteSpace(debuggerType))
            {
                line.Add($"Debugger: `{EscapeInline(debuggerType)}`");
            }
            if (!string.IsNullOrWhiteSpace(serverVersion))
            {
                line.Add($"Server: `{EscapeInline(serverVersion)}`");
            }
            if (!string.IsNullOrWhiteSpace(generatedAt))
            {
                line.Add($"Generated: `{EscapeInline(generatedAt)}`");
            }

            if (line.Count > 0)
            {
                sb.AppendLine(string.Join(" • ", line));
                sb.AppendLine();
            }
        }

        sb.AppendLine($"> Rendered from canonical JSON (source of truth). Requested format: `{EscapeInline(options.Format.ToString())}`");
        sb.AppendLine();
    }

    private static void AppendTableOfContents(StringBuilder sb, ReportOptions options, bool includeAiAnalysis)
    {
        sb.AppendLine("## Table of Contents");
        sb.AppendLine();

        if (includeAiAnalysis)
        {
            sb.AppendLine("- [AI analysis](#ai-analysis)");
        }
        sb.AppendLine("- [At a glance](#at-a-glance)");
        if (options.IncludeCallStacks)
        {
            sb.AppendLine("- [Faulting thread](#faulting-thread)");
        }
        if (options.IncludeThreadInfo)
        {
            sb.AppendLine("- [Threads](#threads)");
        }
        if (options.IncludeProcessInfo || options.IncludeDotNetInfo)
        {
            sb.AppendLine("- [Environment](#environment)");
        }
        if (options.IncludeHeapStats || options.IncludeMemoryLeakInfo)
        {
            sb.AppendLine("- [Memory & GC](#memory--gc)");
        }
        if (options.IncludeDeadlockInfo)
        {
            sb.AppendLine("- [Synchronization](#synchronization)");
        }
        if (options.IncludeSecurityAnalysis)
        {
            sb.AppendLine("- [Security](#security)");
        }
        if (options.IncludeDotNetInfo)
        {
            sb.AppendLine("- [Assemblies](#assemblies)");
        }
        if (options.IncludeModules)
        {
            sb.AppendLine("- [Modules](#modules)");
        }
        if (options.IncludeModules || options.IncludeDotNetInfo)
        {
            sb.AppendLine("- [Symbols](#symbols)");
        }
        if (options.IncludeThreadInfo)
        {
            sb.AppendLine("- [Timeline](#timeline)");
        }
        if (options.IncludeCallStacks && options.IncludeDotNetInfo)
        {
            sb.AppendLine("- [Source context index](#source-context-index)");
            sb.AppendLine("- [Signature](#signature)");
        }
        sb.AppendLine();
    }

    private static void AppendAiAnalysis(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("## AI analysis");
        sb.AppendLine();

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("aiAnalysis", out var ai) ||
            ai.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("_No AI analysis available._");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("### Root cause");
        sb.AppendLine();
        var rootCause = GetString(ai, "rootCause");
        if (!string.IsNullOrWhiteSpace(rootCause))
        {
            sb.AppendLine(EscapeText(rootCause));
        }
        else
        {
            sb.AppendLine("_No root cause provided._");
        }
        sb.AppendLine();

        sb.AppendLine("| Key | Value |");
        sb.AppendLine("|---|---|");
        AppendTableRow(sb, "Confidence", GetString(ai, "confidence"));
        AppendTableRow(sb, "Model", GetString(ai, "model"));
        AppendTableRow(sb, "Analyzed at (UTC)", GetString(ai, "analyzedAt"));
        AppendTableRow(sb, "Iterations", GetString(ai, "iterations"));
        sb.AppendLine();

        if (ai.TryGetProperty("reasoning", out var reasoningElem) && reasoningElem.ValueKind == JsonValueKind.String)
        {
            var reasoning = reasoningElem.GetString();
            if (!string.IsNullOrWhiteSpace(reasoning))
            {
                sb.AppendLine("<details><summary>Reasoning</summary>");
                sb.AppendLine();
                sb.AppendLine("```text");
                sb.AppendLine(reasoning);
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }

        if (ai.TryGetProperty("recommendations", out var recs) && recs.ValueKind == JsonValueKind.Array && recs.GetArrayLength() > 0)
        {
            sb.AppendLine("<details><summary>Recommendations</summary>");
            sb.AppendLine();
            foreach (var r in recs.EnumerateArray())
            {
                var txt = r.GetString();
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    sb.AppendLine("- " + EscapeText(txt));
                }
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        if (ai.TryGetProperty("additionalFindings", out var findings) && findings.ValueKind == JsonValueKind.Array && findings.GetArrayLength() > 0)
        {
            sb.AppendLine("<details><summary>Additional findings</summary>");
            sb.AppendLine();
            foreach (var f in findings.EnumerateArray())
            {
                var txt = f.GetString();
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    sb.AppendLine("- " + EscapeText(txt));
                }
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        if (ai.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Array && evidence.GetArrayLength() > 0)
        {
            sb.AppendLine("<details><summary>Evidence</summary>");
            sb.AppendLine();
            foreach (var e in evidence.EnumerateArray())
            {
                var txt = e.GetString();
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    sb.AppendLine("- " + EscapeText(txt));
                }
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        if (ai.TryGetProperty("hypotheses", out var hypotheses) && hypotheses.ValueKind == JsonValueKind.Array && hypotheses.GetArrayLength() > 0)
        {
            sb.AppendLine("<details><summary>Hypotheses</summary>");
            sb.AppendLine();
            sb.AppendLine("| ID | Confidence | Hypothesis | Notes |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var h in hypotheses.EnumerateArray())
            {
                if (h.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = GetString(h, "id");
                var confidence = GetString(h, "confidence");
                var hypothesis = GetString(h, "hypothesis");
                var notes = GetString(h, "notes");
                var hypothesisCell = string.IsNullOrWhiteSpace(hypothesis) ? string.Empty : EscapeCell(hypothesis);
                var notesCell = string.IsNullOrWhiteSpace(notes) ? string.Empty : EscapeCell(notes);
                sb.AppendLine($"| `{EscapeInline(id)}` | `{EscapeInline(confidence)}` | {hypothesisCell} | {notesCell} |");
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        if (ai.TryGetProperty("evidenceLedger", out var ledger) && ledger.ValueKind == JsonValueKind.Array && ledger.GetArrayLength() > 0)
        {
            sb.AppendLine("<details><summary>Evidence ledger</summary>");
            sb.AppendLine();
            sb.AppendLine("| ID | Source | Finding | Tags |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var item in ledger.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = GetString(item, "id");
                var source = GetString(item, "source");
                var finding = GetString(item, "finding");
                var tags = item.TryGetProperty("tags", out var tagsElem) && tagsElem.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", tagsElem.EnumerateArray().Select(t => t.GetString()).Where(t => !string.IsNullOrWhiteSpace(t)))
                    : string.Empty;

                var sourceCell = string.IsNullOrWhiteSpace(source) ? string.Empty : EscapeCell(source);
                var findingCell = string.IsNullOrWhiteSpace(finding) ? string.Empty : EscapeCell(finding);
                var tagsCell = string.IsNullOrWhiteSpace(tags) ? string.Empty : EscapeCell(tags);
                sb.AppendLine($"| `{EscapeInline(id)}` | {sourceCell} | {findingCell} | {tagsCell} |");
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        if (ai.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
        {
            var desc = GetString(summary, "description");
            var err = GetString(summary, "error");
            var hasDesc = !string.IsNullOrWhiteSpace(desc);
            var hasErr = !string.IsNullOrWhiteSpace(err);
            if (hasDesc || hasErr)
            {
                sb.AppendLine("<details><summary>AI summary rewrite</summary>");
                sb.AppendLine();
                if (hasErr)
                {
                    sb.AppendLine("- Error: " + EscapeText(err));
                    sb.AppendLine();
                }
                if (hasDesc)
                {
                    sb.AppendLine(EscapeText(desc));
                    sb.AppendLine();
                }
                if (summary.TryGetProperty("recommendations", out var summaryRecs) &&
                    summaryRecs.ValueKind == JsonValueKind.Array &&
                    summaryRecs.GetArrayLength() > 0)
                {
                    sb.AppendLine("**Recommendations**");
                    sb.AppendLine();
                    foreach (var r in summaryRecs.EnumerateArray())
                    {
                        var txt = r.GetString();
                        if (!string.IsNullOrWhiteSpace(txt))
                        {
                            sb.AppendLine("- " + EscapeText(txt));
                        }
                    }
                    sb.AppendLine();
                }
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }

        if (ai.TryGetProperty("threadNarrative", out var narrative) && narrative.ValueKind == JsonValueKind.Object)
        {
            var desc = GetString(narrative, "description");
            var err = GetString(narrative, "error");
            var hasDesc = !string.IsNullOrWhiteSpace(desc);
            var hasErr = !string.IsNullOrWhiteSpace(err);
            if (hasDesc || hasErr)
            {
                sb.AppendLine("<details><summary>Thread narrative</summary>");
                sb.AppendLine();
                if (hasErr)
                {
                    sb.AppendLine("- Error: " + EscapeText(err));
                }
                if (!string.IsNullOrWhiteSpace(GetString(narrative, "confidence")))
                {
                    sb.AppendLine("- Confidence: `" + EscapeInline(GetString(narrative, "confidence")) + "`");
                }
                sb.AppendLine();
                if (hasDesc)
                {
                    sb.AppendLine(EscapeText(desc));
                }
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }

        MaybeAppendJsonDetails(sb, "AI analysis JSON", ai, includeJsonDetails);
        if (includeJsonDetails)
        {
            sb.AppendLine();
        }
    }

    private static void AppendAtAGlance(StringBuilder sb, JsonElement root)
    {
        sb.AppendLine("## At a glance");
        sb.AppendLine();

        if (!TryGetAnalysis(root, out var analysis))
        {
            sb.AppendLine("_No analysis available._");
            sb.AppendLine();
            return;
        }

        var summary = GetObjectOrNull(analysis, "summary");
        var env = GetObjectOrNull(analysis, "environment");
        var platform = env is { ValueKind: JsonValueKind.Object } ? GetObjectOrNull(env.Value, "platform") : null;
        var runtime = env is { ValueKind: JsonValueKind.Object } ? GetObjectOrNull(env.Value, "runtime") : null;
        var threads = GetObjectOrNull(analysis, "threads");
        var faultingThread = threads is { ValueKind: JsonValueKind.Object } ? GetObjectOrNull(threads.Value, "faultingThread") : null;

        sb.AppendLine("| Key | Value |");
        sb.AppendLine("|---|---|");
        AppendTableRow(sb, "Crash type", GetString(summary, "crashType"));
        AppendTableRow(sb, "Severity", GetString(summary, "severity"));
        AppendTableRow(sb, "Description", GetString(summary, "description"));
        AppendTableRow(sb, "OS", GetString(platform, "os"));
        AppendTableRow(sb, "Architecture", GetString(platform, "architecture"));
        AppendTableRow(sb, ".NET runtime", GetString(platform, "runtimeVersion"));
        AppendTableRow(sb, "Runtime type", GetString(runtime, "type"));
        AppendTableRow(sb, "CLR version", GetString(runtime, "clrVersion"));
        AppendTableRow(sb, "Thread count", GetString(summary, "threadCount"));
        AppendTableRow(sb, "Module count", GetString(summary, "moduleCount"));
        AppendTableRow(sb, "Assembly count", GetString(summary, "assemblyCount"));
        AppendTableRow(sb, "Faulting thread ID", GetString(faultingThread, "threadId"));
        AppendTableRow(sb, "Faulting top function", GetString(faultingThread, "topFunction"));
        sb.AppendLine();

        if (summary.HasValue && summary.Value.ValueKind == JsonValueKind.Object &&
            summary.Value.TryGetProperty("recommendations", out var recs) && recs.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("<details><summary>Recommendations</summary>");
            sb.AppendLine();
            foreach (var r in recs.EnumerateArray())
            {
                var text = r.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine("- " + EscapeText(text));
                }
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }
    }

    private static void AppendFaultingThread(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("## Faulting thread");
        sb.AppendLine();

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("threads", out var threads) ||
            threads.ValueKind != JsonValueKind.Object ||
            !threads.TryGetProperty("faultingThread", out var faultingThread) ||
            faultingThread.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("_No faulting thread detected._");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Key | Value |");
        sb.AppendLine("|---|---|");
        AppendTableRow(sb, "Thread ID", GetString(faultingThread, "threadId"));
        AppendTableRow(sb, "State", GetString(faultingThread, "state"));
        AppendTableRow(sb, "Top function", GetString(faultingThread, "topFunction"));
        AppendTableRow(sb, "Managed thread ID", GetString(faultingThread, "managedThreadId"));
        AppendTableRow(sb, "OS thread ID (hex)", GetString(faultingThread, "osThreadId"));
        AppendTableRow(sb, "OS thread ID (decimal)", GetString(faultingThread, "osThreadIdDecimal"));
        AppendTableRow(sb, "GC mode", GetString(faultingThread, "gcMode"));
        AppendTableRow(sb, "Lock count", GetString(faultingThread, "lockCount"));
        AppendTableRow(sb, "Apartment", GetString(faultingThread, "apartmentState"));
        AppendTableRow(sb, "Is background", GetString(faultingThread, "isBackground"));
        AppendTableRow(sb, "Is threadpool", GetString(faultingThread, "isThreadpool"));
        sb.AppendLine();

        if (!faultingThread.TryGetProperty("callStack", out var callStack) || callStack.ValueKind != JsonValueKind.Array)
        {
            sb.AppendLine("_No call stack available._");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"### Call stack ({callStack.GetArrayLength()} frames)");
        sb.AppendLine();

        foreach (var frame in callStack.EnumerateArray())
        {
            if (frame.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            AppendFrameDetails(sb, frame, includeSourceContext: true, includeJsonDetails: includeJsonDetails);
        }

        MaybeAppendJsonDetails(sb, "Faulting thread JSON", faultingThread, includeJsonDetails);
        if (includeJsonDetails)
        {
            sb.AppendLine();
        }
    }

    private static void AppendThreads(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("## Threads");
        sb.AppendLine();

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("threads", out var threads) ||
            threads.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("_No thread data available._");
            sb.AppendLine();
            return;
        }

        if (threads.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("### Summary");
            sb.AppendLine();
            sb.AppendLine("| Key | Value |");
            sb.AppendLine("|---|---|");
            AppendTableRow(sb, "Total", GetString(summary, "total"));
            AppendTableRow(sb, "Foreground", GetString(summary, "foreground"));
            AppendTableRow(sb, "Background", GetString(summary, "background"));
            AppendTableRow(sb, "Unstarted", GetString(summary, "unstarted"));
            AppendTableRow(sb, "Dead", GetString(summary, "dead"));
            AppendTableRow(sb, "Pending", GetString(summary, "pending"));
            sb.AppendLine();
        }

        if (threads.TryGetProperty("deadlock", out var deadlock) && deadlock.ValueKind == JsonValueKind.Object)
        {
            var detected = GetString(deadlock, "detected");
            if (!string.IsNullOrWhiteSpace(detected))
            {
                sb.AppendLine("### Deadlock");
                sb.AppendLine();
                sb.AppendLine($"- Detected: `{EscapeInline(detected)}`");
                sb.AppendLine();
            }
            MaybeAppendJsonDetails(sb, "Deadlock JSON", deadlock, includeJsonDetails);
            if (includeJsonDetails)
            {
                sb.AppendLine();
            }
        }

        if (threads.TryGetProperty("threadPool", out var threadPool) && threadPool.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("### ThreadPool");
            sb.AppendLine();
            MaybeAppendJsonDetails(sb, "ThreadPool JSON", threadPool, includeJsonDetails);
            if (includeJsonDetails)
            {
                sb.AppendLine();
            }
        }

        if (threads.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine($"### All threads ({all.GetArrayLength()})");
            sb.AppendLine();
            sb.AppendLine("<details><summary>Thread list</summary>");
            sb.AppendLine();
            sb.AppendLine("| Thread ID | State | Faulting | Top function | Frames |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var t in all.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var id = GetString(t, "threadId");
                var state = GetString(t, "state");
                var isFaulting = GetString(t, "isFaulting");
                var top = GetString(t, "topFunction");
                var frames = t.TryGetProperty("callStack", out var cs) && cs.ValueKind == JsonValueKind.Array ? cs.GetArrayLength().ToString() : string.Empty;
                sb.AppendLine($"| `{EscapeInline(id)}` | {EscapeText(state)} | `{EscapeInline(isFaulting)}` | {EscapeText(top)} | `{EscapeInline(frames)}` |");
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();

            foreach (var t in all.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = GetString(t, "threadId");
                var state = GetString(t, "state");
                var top = GetString(t, "topFunction");
                sb.AppendLine($"<details><summary>Thread `{EscapeInline(id)}` • {EscapeText(state)} • {EscapeText(top)}</summary>");
                sb.AppendLine();

                MaybeAppendJsonDetails(sb, "Thread JSON", t, includeJsonDetails);
                if (includeJsonDetails)
                {
                    sb.AppendLine();
                }

                if (t.TryGetProperty("callStack", out var cs) && cs.ValueKind == JsonValueKind.Array)
                {
                    sb.AppendLine($"**Call stack** ({cs.GetArrayLength()} frames)");
                    sb.AppendLine();
                    foreach (var frame in cs.EnumerateArray())
                    {
                        if (frame.ValueKind == JsonValueKind.Object)
                        {
                            AppendFrameSummary(sb, frame);
                        }
                    }
                }
                else
                {
                    sb.AppendLine("_No call stack._");
                    sb.AppendLine();
                }

                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }

        MaybeAppendJsonDetails(sb, "Threads JSON", threads, includeJsonDetails);
        if (includeJsonDetails)
        {
            sb.AppendLine();
        }
    }

    private static void AppendEnvironment(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("## Environment");
        sb.AppendLine();

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("environment", out var env) ||
            env.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("_No environment data available._");
            sb.AppendLine();
            return;
        }

        var platform = GetObjectOrNull(env, "platform");
        var runtime = GetObjectOrNull(env, "runtime");
        var process = GetObjectOrNull(env, "process");
        var crashInfo = GetObjectOrNull(env, "crashInfo");

        sb.AppendLine("### Platform");
        sb.AppendLine();
        sb.AppendLine("| Key | Value |");
        sb.AppendLine("|---|---|");
        AppendTableRow(sb, "OS", GetString(platform, "os"));
        AppendTableRow(sb, "Architecture", GetString(platform, "architecture"));
        AppendTableRow(sb, "Runtime version", GetString(platform, "runtimeVersion"));
        AppendTableRow(sb, "Pointer size", GetString(platform, "pointerSize"));
        sb.AppendLine();

        sb.AppendLine("### Runtime");
        sb.AppendLine();
        sb.AppendLine("| Key | Value |");
        sb.AppendLine("|---|---|");
        AppendTableRow(sb, "Type", GetString(runtime, "type"));
        AppendTableRow(sb, "CLR version", GetString(runtime, "clrVersion"));
        AppendTableRow(sb, "Hosted", GetString(runtime, "isHosted"));
        sb.AppendLine();

        sb.AppendLine("### Crash info");
        sb.AppendLine();
        sb.AppendLine("| Key | Value |");
        sb.AppendLine("|---|---|");
        AppendTableRow(sb, "Has info", GetString(crashInfo, "hasInfo"));
        AppendTableRow(sb, "Message", GetString(crashInfo, "message"));
        sb.AppendLine();

        sb.AppendLine("### Process");
        sb.AppendLine();
        if (process.HasValue && process.Value.ValueKind == JsonValueKind.Object)
        {
            var sensitiveFiltered = GetBool(process.Value, "sensitiveDataFiltered");
            if (sensitiveFiltered.HasValue)
            {
                sb.AppendLine($"- Sensitive data filtered: `{(sensitiveFiltered.Value ? "true" : "false")}`");
            }

            if (process.Value.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine();
                sb.AppendLine("<details><summary>Arguments</summary>");
                sb.AppendLine();
                var i = 0;
                foreach (var arg in args.EnumerateArray())
                {
                    sb.AppendLine($"- [{i++}] `{EscapeInline(arg.GetString() ?? string.Empty)}`");
                }
                sb.AppendLine();
                sb.AppendLine("</details>");
            }

            if (process.Value.TryGetProperty("environmentVariables", out var envVars) && envVars.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine();
                sb.AppendLine("<details><summary>Environment variables</summary>");
                sb.AppendLine();
                var i = 0;
                foreach (var ev in envVars.EnumerateArray())
                {
                    var text = ev.ValueKind == JsonValueKind.String ? ev.GetString() : ev.ToString();
                    sb.AppendLine($"- [{i++}] `{EscapeInline(text ?? string.Empty)}`");
                }
                sb.AppendLine();
                sb.AppendLine("</details>");
            }
        }
        else
        {
            sb.AppendLine("_No process data available._");
        }

        sb.AppendLine();
        MaybeAppendJsonDetails(sb, "Environment JSON", env, includeJsonDetails);
        if (includeJsonDetails)
        {
            sb.AppendLine();
        }
    }

    private static void AppendMemory(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("## Memory & GC");
        sb.AppendLine();

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("memory", out var memory) ||
            memory.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("_No memory data available._");
            sb.AppendLine();
            return;
        }

        var gc = GetObjectOrNull(memory, "gc");
        if (gc.HasValue && gc.Value.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("### GC");
            sb.AppendLine();
            sb.AppendLine("| Key | Value |");
            sb.AppendLine("|---|---|");
            AppendTableRow(sb, "Heap count", GetString(gc, "heapCount"));
            AppendTableRow(sb, "GC mode", GetString(gc, "gcMode"));
            AppendTableRow(sb, "Server GC", GetString(gc, "isServerGC"));
            AppendTableRow(sb, "Total heap size", GetString(gc, "totalHeapSize"));
            AppendTableRow(sb, "Fragmentation", GetString(gc, "fragmentation"));
            AppendTableRow(sb, "Fragmentation bytes", GetString(gc, "fragmentationBytes"));
            AppendTableRow(sb, "Finalizable objects", GetString(gc, "finalizableObjectCount"));
            sb.AppendLine();
        }

        if (memory.TryGetProperty("topConsumers", out var topConsumers) && topConsumers.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("### Top consumers");
            sb.AppendLine();
            MaybeAppendJsonDetails(sb, "Top consumers JSON", topConsumers, includeJsonDetails);
            if (includeJsonDetails)
            {
                sb.AppendLine();
            }
        }

        if (memory.TryGetProperty("strings", out var strings) && strings.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("### Strings");
            sb.AppendLine();
            MaybeAppendJsonDetails(sb, "Strings JSON", strings, includeJsonDetails);
            if (includeJsonDetails)
            {
                sb.AppendLine();
            }
        }

        if (memory.TryGetProperty("leakAnalysis", out var leak) && leak.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("### Leak analysis");
            sb.AppendLine();
            MaybeAppendJsonDetails(sb, "Leak analysis JSON", leak, includeJsonDetails);
            if (includeJsonDetails)
            {
                sb.AppendLine();
            }
        }

        if (memory.TryGetProperty("oom", out var oom) && oom.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("### Out-of-memory");
            sb.AppendLine();
            MaybeAppendJsonDetails(sb, "OOM JSON", oom, includeJsonDetails);
            if (includeJsonDetails)
            {
                sb.AppendLine();
            }
        }

        if (memory.TryGetProperty("heapStats", out var heapStats) && heapStats.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("### Heap stats");
            sb.AppendLine();
            sb.AppendLine("> Heap stats is large; the full dataset is available in the JSON source of truth.");
            sb.AppendLine();
            sb.AppendLine($"- Entries: `{heapStats.EnumerateObject().Count()}`");
            sb.AppendLine();
        }

        MaybeAppendJsonDetails(sb, "Memory JSON", memory, includeJsonDetails);
        if (includeJsonDetails)
        {
            sb.AppendLine();
        }
    }

    private static void AppendSynchronization(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("## Synchronization");
        sb.AppendLine();

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("synchronization", out var sync) ||
            sync.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("_No synchronization data available._");
            sb.AppendLine();
            return;
        }

        if (sync.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(summary.GetString()))
        {
            sb.AppendLine(EscapeText(summary.GetString() ?? string.Empty));
            sb.AppendLine();
        }

        if (sync.TryGetProperty("potentialDeadlocks", out var deadlocks) && deadlocks.ValueKind == JsonValueKind.Array && deadlocks.GetArrayLength() > 0)
        {
            sb.AppendLine("<details><summary>Potential deadlocks</summary>");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(SerializeIndented(deadlocks));
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        MaybeAppendJsonDetails(sb, "Synchronization JSON", sync, includeJsonDetails);
        if (includeJsonDetails)
        {
            sb.AppendLine();
        }
    }

    private static void AppendSecurity(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("## Security");
        sb.AppendLine();

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("security", out var security) ||
            security.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("_No security analysis available._");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Key | Value |");
        sb.AppendLine("|---|---|");
        AppendTableRow(sb, "Has vulnerabilities", GetString(security, "hasVulnerabilities"));
        AppendTableRow(sb, "Overall risk", GetString(security, "overallRisk"));
        AppendTableRow(sb, "Summary", GetString(security, "summary"));
        AppendTableRow(sb, "Analyzed at (UTC)", GetString(security, "analyzedAt"));
        sb.AppendLine();

        if (security.TryGetProperty("recommendations", out var recs) && recs.ValueKind == JsonValueKind.Array && recs.GetArrayLength() > 0)
        {
            sb.AppendLine("<details><summary>Security recommendations</summary>");
            sb.AppendLine();
            foreach (var rec in recs.EnumerateArray())
            {
                var text = rec.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine("- " + EscapeText(text));
                }
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        MaybeAppendJsonDetails(sb, "Security JSON", security, includeJsonDetails);
        if (includeJsonDetails)
        {
            sb.AppendLine();
        }
    }

    private static void AppendAssemblies(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("## Assemblies");
        sb.AppendLine();

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("assemblies", out var assemblies) ||
            assemblies.ValueKind != JsonValueKind.Object ||
            !assemblies.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            sb.AppendLine("_No assemblies available._");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"Total: `{items.GetArrayLength()}`");
        sb.AppendLine();
        sb.AppendLine("<details><summary>Assembly list</summary>");
        sb.AppendLine();
        sb.AppendLine("| Name | Version | Path | Source URL |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var asm in items.EnumerateArray())
        {
            if (asm.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var name = GetString(asm, "name");
            var version = GetString(asm, "assemblyVersion");
            var path = GetString(asm, "path");
            var sourceUrl = GetString(asm, "sourceUrl");
            sb.AppendLine($"| `{EscapeInline(name)}` | `{EscapeInline(version)}` | `{EscapeInline(path)}` | {EscapeText(sourceUrl)} |");
        }
        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();

        MaybeAppendJsonDetails(sb, "Assemblies JSON", assemblies, includeJsonDetails);
        if (includeJsonDetails)
        {
            sb.AppendLine();
        }
    }

    private static void AppendModules(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("## Modules");
        sb.AppendLine();

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("modules", out var modules) ||
            modules.ValueKind != JsonValueKind.Array)
        {
            sb.AppendLine("_No modules available._");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"Total: `{modules.GetArrayLength()}`");
        sb.AppendLine();
        sb.AppendLine("| Name | Base address | Has symbols |");
        sb.AppendLine("|---|---|---|");
        foreach (var mod in modules.EnumerateArray())
        {
            if (mod.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            sb.AppendLine($"| `{EscapeInline(GetString(mod, "name"))}` | `{EscapeInline(GetString(mod, "baseAddress"))}` | `{EscapeInline(GetString(mod, "hasSymbols"))}` |");
        }
        sb.AppendLine();

        MaybeAppendJsonDetails(sb, "Modules JSON", modules, includeJsonDetails);
        if (includeJsonDetails)
        {
            sb.AppendLine();
        }
    }

    private static void AppendSymbols(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("## Symbols");
        sb.AppendLine();

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("symbols", out var symbols) ||
            symbols.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("_No symbols data available._");
            sb.AppendLine();
            return;
        }

        var native = GetObjectOrNull(symbols, "native");
        var managed = GetObjectOrNull(symbols, "managed");
        var sourcelink = GetObjectOrNull(symbols, "sourcelink");

        sb.AppendLine("| Key | Value |");
        sb.AppendLine("|---|---|");
        AppendTableRow(sb, "Native missing count", GetString(native, "missingCount"));
        AppendTableRow(sb, "Managed PDB missing count", GetString(managed, "pdbMissingCount"));
        AppendTableRow(sb, "SourceLink resolved", GetString(sourcelink, "resolvedCount"));
        AppendTableRow(sb, "SourceLink unresolved", GetString(sourcelink, "unresolvedCount"));
        sb.AppendLine();

        if (native.HasValue && native.Value.TryGetProperty("examples", out var examples) && examples.ValueKind == JsonValueKind.Array && examples.GetArrayLength() > 0)
        {
            sb.AppendLine("<details><summary>Native missing examples</summary>");
            sb.AppendLine();
            foreach (var ex in examples.EnumerateArray())
            {
                var text = ex.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine("- " + EscapeText(text));
                }
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        MaybeAppendJsonDetails(sb, "Symbols JSON", symbols, includeJsonDetails);
        if (includeJsonDetails)
        {
            sb.AppendLine();
        }
    }

    private static void AppendTimeline(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("## Timeline");
        sb.AppendLine();

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("timeline", out var timeline) ||
            timeline.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("_No timeline available._");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Key | Value |");
        sb.AppendLine("|---|---|");
        AppendTableRow(sb, "Version", GetString(timeline, "version"));
        AppendTableRow(sb, "Kind", GetString(timeline, "kind"));
        AppendTableRow(sb, "Captured at (UTC)", GetString(timeline, "capturedAtUtc"));
        AppendTableRow(sb, "Capture reason", GetString(timeline, "captureReason"));
        sb.AppendLine();

        if (timeline.TryGetProperty("threads", out var threads) && threads.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("<details><summary>Thread timeline</summary>");
            sb.AppendLine();
            sb.AppendLine("| Thread | OS thread | State | Activity | Top frame | Wait |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var t in threads.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                sb.AppendLine($"| `{EscapeInline(GetString(t, "threadId"))}` | `{EscapeInline(GetString(t, "osThreadId"))}` | {EscapeText(GetString(t, "state"))} | {EscapeText(GetString(t, "activity"))} | {EscapeText(GetString(t, "topFrame"))} | {EscapeText(GetString(t, "wait"))} |");
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        MaybeAppendJsonDetails(sb, "Timeline JSON", timeline, includeJsonDetails);
        if (includeJsonDetails)
        {
            sb.AppendLine();
        }
    }

    private static void AppendSourceContextIndex(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("## Source context index");
        sb.AppendLine();

        if (!TryGetAnalysis(root, out var analysis) ||
            !analysis.TryGetProperty("sourceContext", out var contexts) ||
            contexts.ValueKind != JsonValueKind.Array ||
            contexts.GetArrayLength() == 0)
        {
            sb.AppendLine("_No source context index available._");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"Entries: `{contexts.GetArrayLength()}`");
        sb.AppendLine();
        sb.AppendLine("| Thread | Frame | Module | Function | File | Line | Status |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var c in contexts.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            sb.AppendLine($"| `{EscapeInline(GetString(c, "threadId"))}` | `{EscapeInline(GetString(c, "frameNumber"))}` | `{EscapeInline(GetString(c, "module"))}` | {EscapeText(GetString(c, "function"))} | `{EscapeInline(GetString(c, "sourceFile"))}` | `{EscapeInline(GetString(c, "lineNumber"))}` | `{EscapeInline(GetString(c, "status"))}` |");
        }
        sb.AppendLine();

        MaybeAppendJsonDetails(sb, "Source context index JSON", contexts, includeJsonDetails);
        if (includeJsonDetails)
        {
            sb.AppendLine();
        }
    }

    private static void AppendSignature(StringBuilder sb, JsonElement root, bool includeJsonDetails)
    {
        sb.AppendLine("## Signature");
        sb.AppendLine();

        if (!TryGetAnalysis(root, out var analysis))
        {
            sb.AppendLine("_No analysis available._");
            sb.AppendLine();
            return;
        }

        if (analysis.TryGetProperty("signature", out var signature) && signature.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("| Key | Value |");
            sb.AppendLine("|---|---|");
            AppendTableRow(sb, "Kind", GetString(signature, "kind"));
            AppendTableRow(sb, "Hash", GetString(signature, "hash"));
            sb.AppendLine();
            MaybeAppendJsonDetails(sb, "Signature JSON", signature, includeJsonDetails);
            if (includeJsonDetails)
            {
                sb.AppendLine();
            }
            return;
        }

        sb.AppendLine("_No signature available._");
        sb.AppendLine();
    }

    private static void AppendFrameDetails(StringBuilder sb, JsonElement frame, bool includeSourceContext, bool includeJsonDetails)
    {
        var frameNumber = GetString(frame, "frameNumber");
        var module = GetString(frame, "module");
        var function = GetString(frame, "function");
        var ip = GetString(frame, "instructionPointer");
        var sp = GetString(frame, "stackPointer");
        var isManaged = frame.TryGetProperty("isManaged", out var m) && m.ValueKind == JsonValueKind.True;

        sb.AppendLine($"<details><summary>Frame {EscapeInline(frameNumber)} • `{(isManaged ? "managed" : "native")}` • `{EscapeInline(module)}!{EscapeInline(function)}`</summary>");
        sb.AppendLine();
        sb.AppendLine("| Key | Value |");
        sb.AppendLine("|---|---|");
        AppendTableRow(sb, "Kind", isManaged ? "managed" : "native");
        AppendTableRow(sb, "Module", module);
        AppendTableRow(sb, "Function", function);
        AppendTableRow(sb, "Instruction pointer", ip);
        AppendTableRow(sb, "Stack pointer", sp);

        var sourceFile = GetString(frame, "sourceFile");
        var lineNumber = GetString(frame, "lineNumber");
        var sourceUrl = GetString(frame, "sourceUrl");
        var sourceRawUrl = GetString(frame, "sourceRawUrl");
        if (!string.IsNullOrWhiteSpace(sourceFile) || !string.IsNullOrWhiteSpace(lineNumber))
        {
            AppendTableRow(sb, "Source", $"{sourceFile}:{lineNumber}");
        }
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            AppendTableRow(sb, "Source URL", sourceUrl);
        }
        if (!string.IsNullOrWhiteSpace(sourceRawUrl))
        {
            AppendTableRow(sb, "Source raw URL", sourceRawUrl);
        }
        sb.AppendLine();

        if (includeSourceContext &&
            frame.TryGetProperty("sourceContext", out var sc) &&
            sc.ValueKind == JsonValueKind.Object &&
            sc.TryGetProperty("status", out var statusElem))
        {
            var status = statusElem.GetString() ?? string.Empty;
            if (!string.Equals(status, "unavailable", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("**Source context**");
                sb.AppendLine();
                sb.AppendLine($"- Status: `{EscapeInline(status)}`");
                var startLine = GetString(sc, "startLine");
                var endLine = GetString(sc, "endLine");
                if (!string.IsNullOrWhiteSpace(startLine) || !string.IsNullOrWhiteSpace(endLine))
                {
                    sb.AppendLine($"- Range: `{EscapeInline(startLine)}`-`{EscapeInline(endLine)}`");
                }
                if (!string.IsNullOrWhiteSpace(lineNumber))
                {
                    sb.AppendLine($"- Frame line: `{EscapeInline(lineNumber)}`");
                }
                sb.AppendLine();

                if (sc.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                {
                    var language = GuessFenceLanguage(frame);
                    sb.AppendLine(string.IsNullOrEmpty(language) ? "```" : $"```{language}");
                    foreach (var line in lines.EnumerateArray())
                    {
                        sb.AppendLine(line.GetString() ?? string.Empty);
                    }
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
                else if (sc.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                {
                    sb.AppendLine(EscapeText(err.GetString() ?? string.Empty));
                    sb.AppendLine();
                }

                MaybeAppendJsonDetails(sb, "Source context JSON", sc, includeJsonDetails);
                if (includeJsonDetails)
                {
                    sb.AppendLine();
                }
            }
        }

        MaybeAppendJsonDetails(sb, "Frame JSON", frame, includeJsonDetails);
        if (includeJsonDetails)
        {
            sb.AppendLine();
        }
        sb.AppendLine("</details>");
        sb.AppendLine();
    }

    private static void AppendFrameSummary(StringBuilder sb, JsonElement frame)
    {
        var frameNumber = GetString(frame, "frameNumber");
        var module = GetString(frame, "module");
        var function = GetString(frame, "function");
        var ip = GetString(frame, "instructionPointer");
        var isManaged = frame.TryGetProperty("isManaged", out var m) && m.ValueKind == JsonValueKind.True;
        var sourceFile = GetString(frame, "sourceFile");
        var lineNumber = GetString(frame, "lineNumber");

        var parts = new List<string>
        {
            $"`{EscapeInline(frameNumber)}`",
            $"`{(isManaged ? "managed" : "native")}`",
            $"`{EscapeInline(module)}!{EscapeInline(function)}`"
        };

        if (!string.IsNullOrWhiteSpace(ip))
        {
            parts.Add($"ip `{EscapeInline(ip)}`");
        }

        if (!string.IsNullOrWhiteSpace(sourceFile) || !string.IsNullOrWhiteSpace(lineNumber))
        {
            parts.Add($"src `{EscapeInline(sourceFile)}:{EscapeInline(lineNumber)}`");
        }

        sb.AppendLine("- " + string.Join(" • ", parts));
    }

    private static bool TryGetAnalysis(JsonElement root, out JsonElement analysis)
    {
        if (root.TryGetProperty("analysis", out analysis) && analysis.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        analysis = default;
        return false;
    }

    private static void AppendKv(StringBuilder sb, JsonElement obj, string label, string property)
    {
        if (!obj.TryGetProperty(property, out var value))
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

        sb.AppendLine($"| {EscapeInline(label)} | `{EscapeInline(display)}` |");
    }

    private static void AppendTableRow(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        sb.AppendLine($"| {EscapeText(label)} | {EscapeCell(value)} |");
    }

    private static string EscapeCell(string value)
    {
        var v = EscapeText(value).Replace("|", "\\|", StringComparison.Ordinal);
        if (v.Contains('\n', StringComparison.Ordinal))
        {
            return $"<pre>{v}</pre>";
        }
        return $"`{EscapeInline(v)}`";
    }

    private static string ElementToText(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(Environment.NewLine, element.EnumerateArray().Select(ElementToText)),
            _ => element.ToString()
        };

    private static void MaybeAppendJsonDetails(StringBuilder sb, string title, JsonElement element, bool include)
    {
        if (!include)
        {
            return;
        }

        AppendJsonDetails(sb, title, element);
    }

    private static void AppendJsonDetails(StringBuilder sb, string title, JsonElement element)
    {
        sb.AppendLine("<details><summary>" + EscapeText(title) + "</summary>");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(SerializeIndented(element));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("</details>");
    }

    private static string SerializeIndented(JsonElement element) =>
        JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });

    private static JsonElement? GetObjectOrNull(JsonElement obj, string property)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }
        return null;
    }

    private static string GetString(JsonElement? obj, string property)
    {
        if (obj.HasValue && obj.Value.ValueKind == JsonValueKind.Object && obj.Value.TryGetProperty(property, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => value.ToString()
            };
        }
        return string.Empty;
    }

    private static string GetString(JsonElement obj, string property)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => value.ToString()
            };
        }
        return string.Empty;
    }

    private static double? GetDouble(JsonElement obj, string property)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var d))
        {
            return d;
        }
        return null;
    }

    private static bool? GetBool(JsonElement obj, string property)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }
        return null;
    }

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

    private static string EscapeInline(string value) =>
        value.Replace("`", "\\`", StringComparison.Ordinal);

    private static string EscapeText(string value) =>
        value.Replace("\r", "", StringComparison.Ordinal);
}
