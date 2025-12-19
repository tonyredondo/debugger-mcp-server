using DebuggerMcp.Cli.Display;
using Spectre.Console;
using Spectre.Console.Testing;

namespace DebuggerMcp.Cli.Tests.Display;

public class LlmResponseRendererTests
{
    [Fact]
    public void AnsiToSpectreMarkup_ConvertsSgrAndCoalescesRuns()
    {
        var text = "\u001B[31mhi\u001B[0m";
        var markup = AnsiToSpectreMarkup.Convert(text);
        Assert.Equal("[red]hi[/]", markup);
    }

    [Fact]
    public void AnsiToSpectreMarkup_StripsNonSgrCsiSequences()
    {
        var text = "\u001B[2Jclear";
        var markup = AnsiToSpectreMarkup.Convert(text);
        Assert.Equal("clear", markup);
    }

    [Fact]
    public void AnsiToSpectreMarkup_StripsOscSequences()
    {
        var text = "\u001B]0;title\u0007hello";
        var markup = AnsiToSpectreMarkup.Convert(text);
        Assert.Equal("hello", markup);
    }

    [Fact]
    public void Render_UnorderedList_RendersBullets()
    {
        var renderer = new LlmResponseRenderer();
        var blocks = renderer.Render("- a\n- b\n", consoleWidth: 80);

        using var console = new TestConsole().Width(80);
        foreach (var block in blocks)
        {
            console.Write(block);
            console.WriteLine();
        }

        var output = string.Join('\n', console.Lines);
        Assert.Contains("- a", output, StringComparison.Ordinal);
        Assert.Contains("- b", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_DoesNotThrow_OnMalformedMarkdown()
    {
        var renderer = new LlmResponseRenderer();
        var blocks = renderer.Render("```csharp\nclass C {}\n", consoleWidth: 80);
        Assert.NotEmpty(blocks);
    }

    [Fact]
    public void Render_Image_DoesNotEmitRawMarkdownBrackets()
    {
        var renderer = new LlmResponseRenderer();
        var blocks = renderer.Render("![alt](https://example.com/x.png)\n", consoleWidth: 80);

        using var console = new TestConsole().Width(80);
        foreach (var block in blocks)
        {
            console.Write(block);
            console.WriteLine();
        }

        var output = string.Join('\n', console.Lines);
        Assert.Contains("image", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://example.com/x.png", output, StringComparison.Ordinal);
        Assert.DoesNotContain("![", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_HeaderOnlyTable_DoesNotDuplicateHeaderRow()
    {
        var renderer = new LlmResponseRenderer();
        var blocks = renderer.Render("| A | B |\n|---|---|\n", consoleWidth: 120);

        using var console = new TestConsole().Width(120);
        foreach (var block in blocks)
        {
            console.Write(block);
            console.WriteLine();
        }

        var output = string.Join('\n', console.Lines);
        Assert.Equal(1, CountOccurrences(output, "A"));
        Assert.Equal(1, CountOccurrences(output, "B"));
    }

    [Fact]
    public void Render_Heading_InsertsBlankLineBeforeTitle()
    {
        var renderer = new LlmResponseRenderer();
        var blocks = renderer.Render("para\n\n## Title\n\nafter\n", consoleWidth: 80);

        using var console = new TestConsole().Width(80);
        console.Write(new Spectre.Console.Rows(blocks));
        var output = string.Join('\n', console.Lines);
        Assert.Contains("para", output, StringComparison.Ordinal);
        Assert.Contains("Title", output, StringComparison.Ordinal);
        Assert.Contains("para\n\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_DoesNotEmit_AutoIdentifiersHeadingLinkReferenceDefinition()
    {
        var renderer = new LlmResponseRenderer();
        var blocks = renderer.Render("# One\n\n## Two\n", consoleWidth: 80);

        using var console = new TestConsole().Width(80);
        console.Write(new Spectre.Console.Rows(blocks));
        var output = string.Join('\n', console.Lines);
        Assert.DoesNotContain("HeadingLinkReferenceDefinition", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Table_TruncatesBodyRowsAndShowsMoreRowsMarker()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("| A |");
        sb.AppendLine("|---|");
        for (var i = 0; i < 60; i++)
        {
            sb.AppendLine($"| row{i} |");
        }

        var renderer = new LlmResponseRenderer(new LlmResponseRenderer.Options { MaxTableRows = 10 });
        var blocks = renderer.Render(sb.ToString(), consoleWidth: 120);

        using var console = new TestConsole().Width(120);
        foreach (var block in blocks)
        {
            console.Write(block);
            console.WriteLine();
        }

        var output = string.Join('\n', console.Lines);
        Assert.Contains("row0", output, StringComparison.Ordinal);
        Assert.Contains("row9", output, StringComparison.Ordinal);
        Assert.DoesNotContain("row10", output, StringComparison.Ordinal);
        Assert.Contains("more rows", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_Quote_DoesNotCountInnerBlocksTowardMaxBlocks()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("> para0");
        sb.AppendLine(">");
        sb.AppendLine("> para1");
        sb.AppendLine(">");
        sb.AppendLine("> para2");
        sb.AppendLine();

        var renderer = new LlmResponseRenderer(new LlmResponseRenderer.Options { MaxBlocks = 1 });
        var blocks = renderer.Render(sb.ToString(), consoleWidth: 80);

        Assert.Single(blocks);

        using var console = new TestConsole().Width(80);
        console.Write(new Spectre.Console.Rows(blocks));
        var output = string.Join('\n', console.Lines);
        Assert.Contains("para0", output, StringComparison.Ordinal);
        Assert.Contains("para2", output, StringComparison.Ordinal);
        Assert.DoesNotContain("output truncated", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_Quote_RespectsMaxQuoteBlocksAndAddsTruncationMarker()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            sb.AppendLine($"> para{i}");
            sb.AppendLine(">");
        }
        sb.AppendLine();

        var renderer = new LlmResponseRenderer(new LlmResponseRenderer.Options { MaxQuoteBlocks = 3 });
        var blocks = renderer.Render(sb.ToString(), consoleWidth: 80);

        Assert.Single(blocks);

        using var console = new TestConsole().Width(80);
        console.Write(new Spectre.Console.Rows(blocks));
        var output = string.Join('\n', console.Lines);
        Assert.Contains("para0", output, StringComparison.Ordinal);
        Assert.Contains("para2", output, StringComparison.Ordinal);
        Assert.DoesNotContain("para3", output, StringComparison.Ordinal);
        Assert.Contains("output truncated", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_MaxBlocks_AddsTruncationMarker()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 50; i++)
        {
            sb.AppendLine($"para{i}");
            sb.AppendLine();
        }

        var renderer = new LlmResponseRenderer(new LlmResponseRenderer.Options { MaxBlocks = 5 });
        var blocks = renderer.Render(sb.ToString(), consoleWidth: 120);

        Assert.True(blocks.Count <= 5);

        using var console = new TestConsole().Width(120);
        console.Write(new Spectre.Console.Rows(blocks));
        var output = string.Join('\n', console.Lines);
        Assert.Contains("output truncated", output, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while (true)
        {
            index = haystack.IndexOf(needle, index, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }
            count++;
            index += needle.Length;
        }

        return count;
    }
}
