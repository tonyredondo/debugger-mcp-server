using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

public class LlmAgentJurorTests
{
    [Fact]
    public void TryParseVerdict_WithValidJson_Parses()
    {
        var json = """
        {
          "selectedHypothesisId": "H1",
          "confidence": "medium",
          "rationale": "Looks plausible.",
          "supportsEvidenceIds": ["E1","E2"],
          "missingEvidenceNextSteps": [
            { "tool": "report_get", "argsJson": "{\"path\":\"analysis.exception.type\"}" }
          ]
        }
        """;

        Assert.True(LlmAgentJuror.TryParseVerdict(json, out var verdict));
        Assert.Equal("H1", verdict.SelectedHypothesisId);
        Assert.Equal("medium", verdict.Confidence);
        Assert.Contains("E1", verdict.SupportsEvidenceIds);
        Assert.Single(verdict.MissingEvidenceNextSteps);
        Assert.Equal("report_get", verdict.MissingEvidenceNextSteps[0].ToolName);
    }

    [Fact]
    public void BuildJurorMessages_IncludesEvidenceIndex()
    {
        var state = LlmAgentSessionStateStore.GetOrCreate("juror", "s", "d");
        _ = state.Evidence.AddOrUpdate(
            toolName: "report_get",
            argumentsJson: "{\"path\":\"analysis.summary\"}",
            toolKey: "report_get:{\"path\":\"analysis.summary\"}",
            toolResultForHashing: "{ \"path\":\"analysis.summary\",\"value\":{}}",
            toolResultPreview: "summary preview",
            tags: ["BASELINE_SUMMARY"],
            toolWasError: false,
            timestampUtc: DateTimeOffset.UtcNow);

        var messages = LlmAgentJuror.BuildJurorMessages(
            sessionState: state,
            seedMessages: [new ChatMessage("user", "analyze this crash")],
            proposedAnswer: "My conclusion");

        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[1].Role);
        Assert.Contains("evidenceIndex", messages[1].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("summary preview", messages[1].Content, StringComparison.OrdinalIgnoreCase);
    }
}

