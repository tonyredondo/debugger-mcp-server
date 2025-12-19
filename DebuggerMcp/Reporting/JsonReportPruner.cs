#nullable enable

using System.Text.Json;
using System.Text.Json.Nodes;
using DebuggerMcp.Serialization;

namespace DebuggerMcp.Reporting;

/// <summary>
/// Helpers for producing compact JSON variants derived from the canonical report document.
/// </summary>
internal static class JsonReportPruner
{
    internal static string BuildSummaryJson(string canonicalReportJson, ReportOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalReportJson);
        ArgumentNullException.ThrowIfNull(options);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(canonicalReportJson);
        }
        catch
        {
            return canonicalReportJson;
        }

        if (node is not JsonObject root)
        {
            return canonicalReportJson;
        }

        if (root["analysis"] is not JsonObject analysis)
        {
            return canonicalReportJson;
        }

        PruneByOptions(analysis, options);
        ApplyOutputLimits(analysis, options);

        try
        {
            return JsonSerializer.Serialize(root, JsonSerializationDefaults.IndentedIgnoreNull);
        }
        catch
        {
            return canonicalReportJson;
        }
    }

    private static void PruneByOptions(JsonObject analysis, ReportOptions options)
    {
        if (!options.IncludeModules)
        {
            analysis.Remove("modules");
        }

        if (!options.IncludeSecurityAnalysis)
        {
            analysis.Remove("security");
        }

        if (!options.IncludeDeadlockInfo)
        {
            analysis.Remove("synchronization");
        }

        if (!options.IncludeHeapStats && !options.IncludeMemoryLeakInfo)
        {
            analysis.Remove("memory");
        }

        if (!options.IncludeProcessInfo && analysis["environment"] is JsonObject env)
        {
            env.Remove("process");
        }

        if (!options.IncludeDotNetInfo)
        {
            analysis.Remove("assemblies");
            analysis.Remove("symbols");
            analysis.Remove("signature");
            analysis.Remove("stackSelection");
            analysis.Remove("sourceContext");
        }

        if (!options.IncludeThreadInfo && analysis["threads"] is JsonObject threads)
        {
            // Keep only the faulting thread (if any) so consumers can see the primary stack.
            var faulting = threads["faultingThread"];
            var replacement = new JsonObject();
            if (faulting != null)
            {
                replacement["faultingThread"] = faulting.DeepClone();
            }

            analysis["threads"] = replacement;
        }

        if (!options.IncludeThreadInfo)
        {
            analysis.Remove("timeline");
        }

        if (!options.IncludeCallStacks && analysis["threads"] is JsonObject t)
        {
            if (t["faultingThread"] is JsonObject ft)
            {
                ft.Remove("callStack");
            }
        }
    }

    private static void ApplyOutputLimits(JsonObject analysis, ReportOptions options)
    {
        ApplyThreadLimits(analysis, options);
        ApplyModuleLimits(analysis, options);
        ApplyEnvironmentLimits(analysis, options);
    }

    private static void ApplyThreadLimits(JsonObject analysis, ReportOptions options)
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

        if (options.MaxThreadsToShow > 0 && threads["all"] is JsonArray all && all.Count > options.MaxThreadsToShow)
        {
            var limited = new JsonArray();

            foreach (var t in all)
            {
                if (limited.Count >= options.MaxThreadsToShow)
                {
                    break;
                }

                if (t is JsonObject obj &&
                    obj["isFaulting"] is JsonValue v &&
                    v.TryGetValue<bool>(out var b) &&
                    b)
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

                if (t is JsonObject obj &&
                    obj["isFaulting"] is JsonValue v &&
                    v.TryGetValue<bool>(out var b) &&
                    b)
                {
                    continue;
                }

                limited.Add(t?.DeepClone());
            }

            threads["all"] = limited;
        }
    }

    private static void TruncateCallStack(JsonNode? threadNode, int maxFrames)
    {
        if (maxFrames <= 0 || threadNode is not JsonObject thread)
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
}

