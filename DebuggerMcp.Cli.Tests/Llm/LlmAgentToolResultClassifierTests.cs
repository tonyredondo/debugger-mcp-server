using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

public class LlmAgentToolResultClassifierTests
{
    [Theory]
    [InlineData("report_get.path is required.")]
    [InlineData("analysis_hypothesis_register.hypotheses is required.")]
    [InlineData("checkpoint_complete.facts is required")]
    public void IsError_WhenToolResultIsMissingRequiredField_ReturnsTrue(string text)
    {
        Assert.True(LlmAgentToolResultClassifier.IsError(text));
    }

    [Theory]
    [InlineData("invalid_path: Property 'missingMethod' not found.")]
    [InlineData("invalid_cursor: Cursor does not match the current query.")]
    [InlineData("too_large (estimatedChars=182492). Try: report_get(path=\"analysis.memory.gc\")")]
    public void IsError_WhenToolResultIsKnownErrorPrefix_ReturnsTrue(string text)
    {
        Assert.True(LlmAgentToolResultClassifier.IsError(text));
    }

    [Fact]
    public void ExtractTryHints_WhenTooLargeContainsReportGetHints_ReturnsSuggestedCalls()
    {
        var text = "too_large (estimatedChars=182492). Try: report_get(path=\"analysis.memory.gc\") | report_get(path=\"analysis.memory.heapStats\")";
        var hints = LlmAgentToolResultClassifier.ExtractTryHints(text);

        Assert.Equal(2, hints.Count);
        Assert.Equal("report_get", hints[0].ToolName);
        Assert.Contains("analysis.memory.gc", hints[0].ArgumentsJson, StringComparison.OrdinalIgnoreCase);
    }
}
