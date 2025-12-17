using DebuggerMcp.Sampling;
using Xunit;

namespace DebuggerMcp.Tests.Sampling;

public class SamplingToolsTests
{
    [Fact]
    public void GetDebuggerTools_ReturnsExpectedToolNames()
    {
        var tools = SamplingTools.GetDebuggerTools();

        Assert.NotNull(tools);
        Assert.NotEmpty(tools);

        var names = tools.Select(t => t.Name).ToList();
        Assert.Contains("exec", names);
        Assert.Contains("inspect", names);
        Assert.Contains("get_thread_stack", names);
        Assert.Contains("analysis_complete", names);
    }

    [Fact]
    public void GetDebuggerTools_AllToolsHaveSchemas()
    {
        var tools = SamplingTools.GetDebuggerTools();

        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Name));
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.True(tool.InputSchema.ValueKind is System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Array);
        }
    }
}

