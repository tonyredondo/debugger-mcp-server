using DebuggerMcp.Cli.Llm;
using System.Text.Json;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

public class LlmAgentCheckpointBuilderTests
{
    [Fact]
    public void BuildLoopBreakCheckpoint_WhenInvalidArraySliceInPath_SuggestsBasePathWithLimit()
    {
        var state = LlmAgentSessionStateStore.GetOrCreate("chk", "s", "d");

        _ = state.Evidence.AddOrUpdate(
            toolName: "report_get",
            argumentsJson: "{\"path\":\"analysis.exception.stackTrace[0:5]\"}",
            toolKey: "report_get:{\"path\":\"analysis.exception.stackTrace[0:5]\"}",
            toolResultForHashing: "invalid_path: Invalid array index '0:5'. Only numeric indices are supported.",
            toolResultPreview: "invalid_path: Invalid array index '0:5'. Only numeric indices are supported.",
            tags: ["REPORT_GET"],
            toolWasError: true,
            timestampUtc: DateTimeOffset.UtcNow);

        var checkpointJson = LlmAgentCheckpointBuilder.BuildLoopBreakCheckpoint(
            sessionState: state,
            seedMessages: [new ChatMessage("user", "analysis")],
            iteration: 3,
            toolCallsExecuted: 2);

        using var checkpointDoc = JsonDocument.Parse(checkpointJson);
        var nextSteps = checkpointDoc.RootElement.GetProperty("nextSteps");
        Assert.True(nextSteps.GetArrayLength() > 0);

        var argsJson = nextSteps[0].GetProperty("argsJson").GetString();
        Assert.False(string.IsNullOrWhiteSpace(argsJson));

        using var argsDoc = JsonDocument.Parse(argsJson!);
        Assert.Equal("analysis.exception.stackTrace", argsDoc.RootElement.GetProperty("path").GetString());
        Assert.Equal(10, argsDoc.RootElement.GetProperty("limit").GetInt32());
    }
}

