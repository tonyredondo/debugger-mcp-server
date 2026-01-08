using System.Text.Json;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Builds compact, machine-readable checkpoint payloads to stabilize <c>llmagent</c> across pruning and loops.
/// </summary>
internal static class LlmAgentCheckpointBuilder
{
    /// <summary>
    /// Builds a carry-forward checkpoint suitable for persisting across multiple prompts within the same scope.
    /// </summary>
    public static string BuildCarryForwardCheckpoint(
        LlmAgentSessionState sessionState,
        IReadOnlyList<ChatMessage> seedMessages,
        int iteration,
        int toolCallsExecuted,
        int totalNewEvidence)
    {
        var baseline = ComputeBaselineState(sessionState.Evidence);
        var prompt = TryGetLastUserPrompt(seedMessages);

        var payload = new
        {
            version = 1,
            kind = "carry_forward",
            iteration,
            toolCallsExecuted,
            totalNewEvidence,
            promptKind = LlmAgentPromptClassifier.IsConclusionSeeking(prompt) ? "conclusion" : "interactive",
            reportSnapshot = new
            {
                dumpId = sessionState.LastReportDumpId ?? string.Empty,
                generatedAt = sessionState.LastReportGeneratedAt ?? string.Empty
            },
            phase = new
            {
                baselineComplete = baseline.IsComplete,
                missingBaseline = baseline.MissingTags
            },
            baselineEvidence = baseline.BaselineEvidenceByTag,
            evidenceIndex = BuildEvidenceIndex(sessionState.Evidence, maxItems: 25),
            doNotRepeat = Array.Empty<string>(),
            nextSteps = Array.Empty<object>(),
            facts =
            new[]
            {
                "This is an internal checkpoint to preserve stable evidence IDs across context pruning.",
                "Tool-result caching is disabled in llmagent; repeated tool calls execute, but avoid loops."
            }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Builds a loop-break checkpoint after repeated no-progress iterations.
    /// </summary>
    public static string BuildLoopBreakCheckpoint(
        LlmAgentSessionState sessionState,
        IReadOnlyList<ChatMessage> seedMessages,
        int iteration,
        int toolCallsExecuted)
    {
        var baseline = ComputeBaselineState(sessionState.Evidence);
        var prompt = TryGetLastUserPrompt(seedMessages);
        var wantsConclusion = LlmAgentPromptClassifier.IsConclusionSeeking(prompt);

        var latest = sessionState.Evidence.Entries.LastOrDefault();
        var tryHints = latest == null ? Array.Empty<LlmAgentSuggestedToolCall>() : LlmAgentToolResultClassifier.ExtractTryHints(latest.ToolResultPreview);

        var next = SelectNextStep(latest, baseline, wantsConclusion, tryHints);

        var payload = new
        {
            version = 1,
            kind = "loop_break",
            iteration,
            toolCallsExecuted,
            promptKind = wantsConclusion ? "conclusion" : "interactive",
            reportSnapshot = new
            {
                dumpId = sessionState.LastReportDumpId ?? string.Empty,
                generatedAt = sessionState.LastReportGeneratedAt ?? string.Empty
            },
            phase = new
            {
                baselineComplete = baseline.IsComplete,
                missingBaseline = baseline.MissingTags
            },
            baselineEvidence = baseline.BaselineEvidenceByTag,
            evidenceIndex = BuildEvidenceIndex(sessionState.Evidence, maxItems: 25),
            facts = new[]
            {
                "Loop guard: no new evidence was produced across repeated iterations.",
                "Follow the single nextStep below; do not repeat the immediately prior failing call without changing the query."
            },
            doNotRepeat = latest == null ? Array.Empty<string>() : new[] { latest.ToolKey },
            nextSteps = next == null ? Array.Empty<object>() : new[] { next }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Builds a checkpoint that enforces baseline collection for conclusion-seeking prompts.
    /// </summary>
    public static string BuildBaselineRequiredCheckpoint(
        LlmAgentSessionState sessionState,
        IReadOnlyList<ChatMessage> seedMessages,
        int iteration,
        int toolCallsExecuted)
    {
        var baseline = ComputeBaselineState(sessionState.Evidence);
        var prompt = TryGetLastUserPrompt(seedMessages);
        var wantsConclusion = LlmAgentPromptClassifier.IsConclusionSeeking(prompt);

        var next = wantsConclusion && baseline.MissingPlannedCalls.Count > 0
            ? new { tool = baseline.MissingPlannedCalls[0].ToolName, argsJson = baseline.MissingPlannedCalls[0].ArgumentsJson }
            : null;

        var payload = new
        {
            version = 1,
            kind = "baseline_required",
            iteration,
            toolCallsExecuted,
            promptKind = wantsConclusion ? "conclusion" : "interactive",
            reportSnapshot = new
            {
                dumpId = sessionState.LastReportDumpId ?? string.Empty,
                generatedAt = sessionState.LastReportGeneratedAt ?? string.Empty
            },
            phase = new
            {
                baselineComplete = baseline.IsComplete,
                missingBaseline = baseline.MissingTags
            },
            baselineEvidence = baseline.BaselineEvidenceByTag,
            evidenceIndex = BuildEvidenceIndex(sessionState.Evidence, maxItems: 25),
            facts = new[]
            {
                "Baseline required: do not conclude yet.",
                "Fetch the missing baseline item(s) first (starting with nextSteps[0]) so conclusions are evidence-backed."
            },
            doNotRepeat = Array.Empty<string>(),
            nextSteps = next == null ? Array.Empty<object>() : new[] { next }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string TryGetLastUserPrompt(IReadOnlyList<ChatMessage> seedMessages)
    {
        for (var i = seedMessages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(seedMessages[i].Role, "user", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(seedMessages[i].Content))
            {
                return seedMessages[i].Content;
            }
        }

        return string.Empty;
    }

    private static object? SelectNextStep(
        LlmAgentEvidenceEntry? latest,
        BaselineState baseline,
        bool wantsConclusion,
        IReadOnlyList<LlmAgentSuggestedToolCall> tryHints)
    {
        if (tryHints.Count > 0)
        {
            return new { tool = tryHints[0].ToolName, argsJson = tryHints[0].ArgumentsJson };
        }

        if (latest != null &&
            string.Equals(latest.ToolName, "report_get", StringComparison.OrdinalIgnoreCase) &&
            latest.ToolWasError)
        {
            var preview = latest.ToolResultPreview ?? string.Empty;

            if (preview.Contains("invalid_cursor", StringComparison.OrdinalIgnoreCase))
            {
                if (TryRemoveJsonProperty(latest.ArgumentsJson, "cursor", out var updatedArgs))
                {
                    return new { tool = "report_get", argsJson = updatedArgs };
                }
            }

            if (preview.Contains("Invalid array index", StringComparison.OrdinalIgnoreCase))
            {
                // Prefer suggesting limit-based paging over slice indices.
                if (TryReadJsonStringProperty(latest.ArgumentsJson, "path", out var path) &&
                    !string.IsNullOrWhiteSpace(path))
                {
                    var argsJson = JsonSerializer.Serialize(new { path = path.Trim(), limit = 10 });
                    return new { tool = "report_get", argsJson };
                }
            }

            if (preview.Contains("Segment 'items' cannot be resolved", StringComparison.OrdinalIgnoreCase))
            {
                if (TryReadJsonStringProperty(latest.ArgumentsJson, "path", out var path) &&
                    path != null &&
                    path.Contains(".items", StringComparison.OrdinalIgnoreCase))
                {
                    var fixedPath = path.Replace(".items", string.Empty, StringComparison.OrdinalIgnoreCase);
                    var argsJson = JsonSerializer.Serialize(new { path = fixedPath.Trim(), limit = 20 });
                    return new { tool = "report_get", argsJson };
                }
            }
        }

        if (wantsConclusion && baseline.MissingPlannedCalls.Count > 0)
        {
            var planned = baseline.MissingPlannedCalls[0];
            return new { tool = planned.ToolName, argsJson = planned.ArgumentsJson };
        }

        return new { tool = "report_index", argsJson = "{}" };
    }

    private static bool TryRemoveJsonProperty(string json, string propertyName, out string updatedJson)
    {
        updatedJson = json;
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    dict[p.Name] = p.Value.Clone();
                }
            }

            updatedJson = JsonSerializer.Serialize(dict);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadJsonStringProperty(string json, string propertyName, out string? value)
    {
        value = null;

        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!doc.RootElement.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = prop.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<object> BuildEvidenceIndex(LlmAgentEvidenceLedger ledger, int maxItems)
    {
        maxItems = Math.Clamp(maxItems, 1, 200);
        return ledger.Entries
            .TakeLast(maxItems)
            .Select(e => new
            {
                id = e.EvidenceId,
                tool = e.ToolName,
                tags = e.Tags,
                preview = e.ToolResultPreview,
                error = e.ToolWasError,
                seen = e.SeenCount
            })
            .ToList();
    }

    private static BaselineState ComputeBaselineState(LlmAgentEvidenceLedger ledger)
    {
        var required = LlmAgentBaselinePolicy.RequiredBaseline;
        var missingTags = new List<string>();
        var missingCalls = new List<LlmAgentPlannedToolCall>();
        var baselineEvidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in required)
        {
            var entry = ledger.TryGetLatestByTag(item.Tag);
            if (entry == null || entry.ToolWasError)
            {
                missingTags.Add(item.Tag);
                missingCalls.Add(item.PlannedCall);
                continue;
            }

            baselineEvidence[item.Tag] = entry.EvidenceId;
        }

        return new BaselineState(
            IsComplete: missingTags.Count == 0,
            MissingTags: missingTags,
            MissingPlannedCalls: missingCalls,
            BaselineEvidenceByTag: baselineEvidence);
    }

    private sealed record BaselineState(
        bool IsComplete,
        IReadOnlyList<string> MissingTags,
        IReadOnlyList<LlmAgentPlannedToolCall> MissingPlannedCalls,
        IReadOnlyDictionary<string, string> BaselineEvidenceByTag);
}

/// <summary>
/// Baseline “must fetch” set used for conclusion-seeking prompts.
/// </summary>
internal static class LlmAgentBaselinePolicy
{
    public static readonly IReadOnlyList<LlmAgentBaselineItem> RequiredBaseline =
    [
        new("BASELINE_META", new LlmAgentPlannedToolCall("report_get", JsonSerializer.Serialize(new { path = "metadata", pageKind = "object", limit = 50 }))),
        new("BASELINE_SUMMARY", new LlmAgentPlannedToolCall("report_get", JsonSerializer.Serialize(new { path = "analysis.summary", pageKind = "object", limit = 50 }))),
        new("BASELINE_ENV", new LlmAgentPlannedToolCall("report_get", JsonSerializer.Serialize(new { path = "analysis.environment", pageKind = "object", limit = 50 }))),
        new("BASELINE_EXC_TYPE", new LlmAgentPlannedToolCall("report_get", JsonSerializer.Serialize(new { path = "analysis.exception.type" }))),
        new("BASELINE_EXC_MESSAGE", new LlmAgentPlannedToolCall("report_get", JsonSerializer.Serialize(new { path = "analysis.exception.message" }))),
        new("BASELINE_EXC_HRESULT", new LlmAgentPlannedToolCall("report_get", JsonSerializer.Serialize(new { path = "analysis.exception.hResult" }))),
        new("BASELINE_EXC_STACK", new LlmAgentPlannedToolCall("report_get", JsonSerializer.Serialize(new { path = "analysis.exception.stackTrace", limit = 8, select = new[] { "frameNumber", "instructionPointer", "module", "function", "sourceFile", "lineNumber", "isManaged" } })))
    ];
}

/// <summary>
/// Single baseline item definition.
/// </summary>
internal sealed record LlmAgentBaselineItem(string Tag, LlmAgentPlannedToolCall PlannedCall);

/// <summary>
/// A tool call the agent should consider making (represented as JSON so it can be embedded in checkpoints).
/// </summary>
internal sealed record LlmAgentPlannedToolCall(string ToolName, string ArgumentsJson);

/// <summary>
/// Classifies user prompts to decide when “root cause rigor” should apply.
/// </summary>
internal static class LlmAgentPromptClassifier
{
    public static bool IsConclusionSeeking(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        var p = prompt.Trim().ToLowerInvariant();
        return p.Contains("root cause") ||
               p.Contains("why did") ||
               p.Contains("why does") ||
               p.Contains("what happened") ||
               p.Contains("analyze") ||
               p.Contains("analysis") ||
               p.Contains("recommend") ||
               p.Contains("recommendation") ||
               p.Contains("conclusion") ||
               p.Contains("explain the crash") ||
               p.Contains("explain this crash");
    }
}
