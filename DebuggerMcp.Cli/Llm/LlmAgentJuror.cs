using System.Text.Json;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Implements a “juror” pass for <c>llmagent</c> to validate conclusions against evidence.
/// </summary>
internal static class LlmAgentJuror
{
    /// <summary>
    /// Builds juror messages for a single, tool-disabled validation pass.
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildJurorMessages(
        LlmAgentSessionState sessionState,
        IReadOnlyList<ChatMessage> seedMessages,
        string proposedAnswer,
        int maxEvidenceIndexItems = 30)
    {
        maxEvidenceIndexItems = Math.Clamp(maxEvidenceIndexItems, 1, 200);

        var prompt = TryGetLastUserPrompt(seedMessages);
        var baseline = BuildBaselineSummary(sessionState);
        var evidenceIndex = sessionState.Evidence.Entries
            .TakeLast(maxEvidenceIndexItems)
            .Select(e => new
            {
                id = e.EvidenceId,
                tool = e.ToolName,
                tags = e.Tags,
                preview = e.ToolResultPreview,
                error = e.ToolWasError
            })
            .ToList();

        var instructions =
            """
            You are a strict juror validating a proposed crash-analysis conclusion.

            Rules:
            - You have NO tools. Do not request tools or propose tool calls inside your answer.
            - Output MUST be a single JSON object and nothing else (no markdown, no prose outside JSON).
            - Only accept conclusions that are supported by explicit evidence IDs.
            - If evidence is insufficient, keep confidence low and request at most 2 missing evidence steps.

            Output JSON schema:
            {
              "selectedHypothesisId": "H1|H2|...|unknown",
              "confidence": "high|medium|low",
              "rationale": "string",
              "supportsEvidenceIds": ["E1","E2"],
              "missingEvidenceNextSteps": [
                { "tool": "report_get|report_index|exec|analyze|get_report_section|find_report_sections", "argsJson": "{...json...}" }
              ]
            }
            """;

        var payload = new
        {
            userPrompt = prompt,
            proposedAnswer,
            reportSnapshot = new
            {
                dumpId = sessionState.LastReportDumpId ?? string.Empty,
                generatedAt = sessionState.LastReportGeneratedAt ?? string.Empty
            },
            baseline,
            evidenceIndex
        };

        return
        [
            new ChatMessage("system", instructions.Trim()),
            new ChatMessage("user", JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }))
        ];
    }

    /// <summary>
    /// Parses juror output into a structured verdict.
    /// </summary>
    public static bool TryParseVerdict(string? jurorText, out LlmAgentJurorVerdict verdict)
    {
        verdict = new LlmAgentJurorVerdict(
            SelectedHypothesisId: "unknown",
            Confidence: "low",
            Rationale: "No juror output.",
            SupportsEvidenceIds: [],
            MissingEvidenceNextSteps: []);

        if (string.IsNullOrWhiteSpace(jurorText))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(jurorText);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = doc.RootElement;
            var hypothesisId = GetString(root, "selectedHypothesisId") ?? "unknown";
            var confidence = GetString(root, "confidence") ?? "low";
            var rationale = GetString(root, "rationale") ?? string.Empty;

            var supports = new List<string>();
            if (root.TryGetProperty("supportsEvidenceIds", out var supportsProp) && supportsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in supportsProp.EnumerateArray())
                {
                    if (id.ValueKind == JsonValueKind.String)
                    {
                        var s = id.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            supports.Add(s);
                        }
                    }
                }
            }

            var missing = new List<LlmAgentSuggestedToolCall>();
            if (root.TryGetProperty("missingEvidenceNextSteps", out var missingProp) && missingProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var step in missingProp.EnumerateArray())
                {
                    if (step.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var tool = GetString(step, "tool");
                    var argsJson = GetString(step, "argsJson") ?? "{}";
                    if (!string.IsNullOrWhiteSpace(tool))
                    {
                        missing.Add(new LlmAgentSuggestedToolCall(tool, argsJson));
                    }
                }
            }

            verdict = new LlmAgentJurorVerdict(
                SelectedHypothesisId: hypothesisId,
                Confidence: confidence,
                Rationale: rationale,
                SupportsEvidenceIds: supports,
                MissingEvidenceNextSteps: missing);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static object BuildBaselineSummary(LlmAgentSessionState sessionState)
    {
        var missing = new List<string>();
        var evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in LlmAgentBaselinePolicy.RequiredBaseline)
        {
            var entry = sessionState.Evidence.TryGetLatestByTag(item.Tag);
            if (entry == null || entry.ToolWasError)
            {
                missing.Add(item.Tag);
                continue;
            }

            evidence[item.Tag] = entry.EvidenceId;
        }

        return new
        {
            baselineComplete = missing.Count == 0,
            missingTags = missing,
            baselineEvidence = evidence
        };
    }

    private static string? TryGetLastUserPrompt(IReadOnlyList<ChatMessage> seedMessages)
    {
        for (var i = seedMessages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(seedMessages[i].Role, "user", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(seedMessages[i].Content))
            {
                return seedMessages[i].Content;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!obj.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return prop.GetString();
    }
}

/// <summary>
/// Structured juror verdict used by <c>llmagent</c> to decide whether to gather more evidence.
/// </summary>
internal sealed record LlmAgentJurorVerdict(
    string SelectedHypothesisId,
    string Confidence,
    string Rationale,
    IReadOnlyList<string> SupportsEvidenceIds,
    IReadOnlyList<LlmAgentSuggestedToolCall> MissingEvidenceNextSteps);

