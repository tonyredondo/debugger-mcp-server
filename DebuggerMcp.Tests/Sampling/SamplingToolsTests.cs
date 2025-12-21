using System.Linq;
using DebuggerMcp.Sampling;
using Xunit;

namespace DebuggerMcp.Tests.Sampling;

public class SamplingToolsTests
{
    [Fact]
    public void GetCrashAnalysisTools_ReturnsExpectedToolNames()
    {
        var tools = SamplingTools.GetCrashAnalysisTools();

        Assert.NotNull(tools);
        Assert.NotEmpty(tools);

        var names = tools.Select(t => t.Name).ToList();
        Assert.Contains("exec", names);
        Assert.Contains("inspect", names);
        Assert.Contains("get_thread_stack", names);
        Assert.Contains("analysis_complete", names);
    }

    [Fact]
    public void GetSummaryRewriteTools_ContainsCompletionTool()
    {
        var tools = SamplingTools.GetSummaryRewriteTools();

        var names = tools.Select(t => t.Name).ToList();
        Assert.Contains("analysis_summary_rewrite_complete", names);
        Assert.DoesNotContain("analysis_complete", names);
    }

    [Fact]
    public void GetThreadNarrativeTools_ContainsCompletionTool()
    {
        var tools = SamplingTools.GetThreadNarrativeTools();

        var names = tools.Select(t => t.Name).ToList();
        Assert.Contains("analysis_thread_narrative_complete", names);
        Assert.DoesNotContain("analysis_complete", names);
    }

    [Fact]
    public void AllSamplingTools_HaveSchemas()
    {
        var all = SamplingTools.GetCrashAnalysisTools()
            .Concat(SamplingTools.GetSummaryRewriteTools())
            .Concat(SamplingTools.GetThreadNarrativeTools())
            .GroupBy(t => t.Name)
            .Select(g => g.First())
            .ToList();

        foreach (var tool in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Name));
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.True(tool.InputSchema.ValueKind is System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Array);
        }
    }
}
