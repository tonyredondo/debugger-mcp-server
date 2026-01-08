using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

public class LlmAgentSessionStateTests
{
    [Fact]
    public void TryUpdateSnapshotFromMetadataToolResult_WhenGeneratedAtChanges_ResetsEvidence()
    {
        var state = LlmAgentSessionStateStore.GetOrCreate("snap", "s", "d");

        _ = state.Evidence.AddOrUpdate(
            toolName: "exec",
            argumentsJson: "{\"command\":\"bt\"}",
            toolKey: "exec:bt",
            toolResultForHashing: "bt-output",
            toolResultPreview: "bt-output",
            tags: ["EXEC"],
            toolWasError: false,
            timestampUtc: DateTimeOffset.UtcNow);

        Assert.Single(state.Evidence.Entries);

        var first = """{ "path":"metadata","value":{"dumpId":"X","generatedAt":"T1"} }""";
        Assert.False(state.TryUpdateSnapshotFromMetadataToolResult(first, out _));

        var second = """{ "path":"metadata","value":{"dumpId":"X","generatedAt":"T2"} }""";
        Assert.True(state.TryUpdateSnapshotFromMetadataToolResult(second, out var reason));
        Assert.Contains("generatedAt changed", reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(state.Evidence.Entries);
    }
}

