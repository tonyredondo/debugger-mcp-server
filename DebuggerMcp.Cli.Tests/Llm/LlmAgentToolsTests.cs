using System.Text.Json;
using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

public class LlmAgentToolsTests
{
    [Fact]
    public void GetDefaultTools_ReturnsStableSetOfTools()
    {
        var tools = LlmAgentTools.GetDefaultTools();

        Assert.NotNull(tools);
        Assert.Equal(6, tools.Count);

        var names = tools.Select(t => t.Name).ToList();
        Assert.Equal(6, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.Contains("report_index", names);
        Assert.Contains("report_get", names);
        Assert.Contains("exec", names);
        Assert.Contains("analyze", names);
        Assert.Contains("inspect_object", names);
        Assert.Contains("clr_stack", names);

        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Name));
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.Equal(JsonValueKind.Object, tool.Parameters.ValueKind);
        }

        var reportGet = tools.Single(t => string.Equals(t.Name, "report_get", StringComparison.OrdinalIgnoreCase));
        Assert.True(reportGet.Parameters.TryGetProperty("required", out var required));
        Assert.Equal(JsonValueKind.Array, required.ValueKind);
        Assert.Contains(required.EnumerateArray().Select(v => v.GetString()), v => v == "path");
    }
}

