using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

[Collection("NonParallelConsole")]
public class MarkdownCodeBlockTests
{
    [Fact]
    public void Format_WhenContentContainsTripleBackticks_UsesLongerFence()
    {
        var content = "before\n```\ninside\n```\nafter";

        var md = MarkdownCodeBlock.Format(content, "text");

        Assert.Contains("````text", md, StringComparison.Ordinal);
        Assert.Contains(content, md, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_WhenContentHasVeryLongBacktickRuns_FallsBackToIndentedBlock()
    {
        var content = new string('`', 50) + "\nhello\n" + new string('~', 50);

        var md = MarkdownCodeBlock.Format(content, "text");

        // Indented blocks start with 4 spaces; ensure we did not emit a giant fence.
        Assert.StartsWith("    ", md, StringComparison.Ordinal);
        Assert.Contains("hello", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_WhenLanguageIsNull_StillFormatsBlock()
    {
        var md = MarkdownCodeBlock.Format("hello", language: null);
        Assert.Contains("hello", md, StringComparison.Ordinal);
    }
}

