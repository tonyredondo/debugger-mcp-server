using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

public class LlmAgentPromptClassifierTests
{
    [Fact]
    public void IsConclusionSeekingOrContinuation_WhenPromptIsConclusionSeeking_ReturnsTrue()
    {
        var state = LlmAgentSessionStateStore.GetOrCreate("pc1", "s", "d");
        Assert.True(LlmAgentPromptClassifier.IsConclusionSeekingOrContinuation(state, "what happened with this dump?"));
    }

    [Fact]
    public void IsConclusionSeekingOrContinuation_WhenPriorCheckpointWasConclusionAndBaselineIncomplete_ReturnsTrueForAnyFollowUp()
    {
        var state = LlmAgentSessionStateStore.GetOrCreate("pc2", "s", "d");
        state.LastCheckpointJson = """{ "promptKind": "conclusion" }""";

        Assert.True(LlmAgentPromptClassifier.IsConclusionSeekingOrContinuation(state, "please do it"));
        Assert.True(LlmAgentPromptClassifier.IsConclusionSeekingOrContinuation(state, "hazlo por favor"));
    }

    [Fact]
    public void IsConclusionSeekingOrContinuation_WhenContinuationAndPriorCheckpointWasInteractive_ReturnsFalse()
    {
        var state = LlmAgentSessionStateStore.GetOrCreate("pc3", "s", "d");
        state.LastCheckpointJson = """{ "promptKind": "interactive" }""";

        Assert.False(LlmAgentPromptClassifier.IsConclusionSeekingOrContinuation(state, "do it"));
    }

    [Fact]
    public void IsConclusionSeekingOrContinuation_WhenBaselineComplete_ReturnsFalseForContinuation()
    {
        var state = LlmAgentSessionStateStore.GetOrCreate("pc4", "s", "d");
        state.LastCheckpointJson = """{ "promptKind": "conclusion" }""";

        foreach (var item in LlmAgentBaselinePolicy.RequiredBaseline)
        {
            _ = state.Evidence.AddOrUpdate(
                toolName: "report_get",
                argumentsJson: "{}",
                toolKey: "k:" + item.Tag,
                toolResultForHashing: item.Tag,
                toolResultPreview: item.Tag,
                tags: [item.Tag],
                toolWasError: false,
                timestampUtc: DateTimeOffset.UtcNow);
        }

        Assert.False(LlmAgentPromptClassifier.IsConclusionSeekingOrContinuation(state, "do it"));
    }
}
