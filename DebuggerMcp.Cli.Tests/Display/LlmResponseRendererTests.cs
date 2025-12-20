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
    public void AnsiToSpectreMarkup_HandlesBoldAndColor()
    {
        var text = "\u001B[1;32mhi\u001B[0m";
        var markup = AnsiToSpectreMarkup.Convert(text);
        Assert.Equal("[bold green]hi[/]", markup);
    }

    [Fact]
    public void AnsiToSpectreMarkup_HandlesBackgroundColors()
    {
        var text = "\u001B[41mhi\u001B[0m";
        var markup = AnsiToSpectreMarkup.Convert(text);
        Assert.Equal("[on red]hi[/]", markup);
    }

    [Fact]
    public void AnsiToSpectreMarkup_HandlesEmptySgrReset()
    {
        var text = "\u001B[mhi";
        var markup = AnsiToSpectreMarkup.Convert(text);
        Assert.Equal("hi", markup);
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
        var lines = console.Lines.ToArray();
        Assert.Contains(lines, l => l.Contains("para", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Title", StringComparison.Ordinal));

        var paraIndex = Array.FindIndex(lines, l => l.Contains("para", StringComparison.Ordinal));
        var titleIndex = Array.FindIndex(lines, l => l.Contains("Title", StringComparison.Ordinal));
        Assert.True(paraIndex >= 0 && titleIndex > paraIndex);

        // Exactly one whitespace-only line between para and Title.
        var between = lines[(paraIndex + 1)..titleIndex];
        Assert.Single(between);
        Assert.True(string.IsNullOrWhiteSpace(between[0]));
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
    public void Render_Heading_UsesYellowAccent()
    {
        var renderer = new LlmResponseRenderer();
        var blocks = renderer.Render("# Title\n", consoleWidth: 80);

        var rule = Assert.IsType<Rule>(blocks[0]);
        Assert.Contains("yellow", rule.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Color.Yellow, rule.Style?.Foreground);
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

    [Fact]
    public void Render_OrderedList_UsesStartIndex()
    {
        var renderer = new LlmResponseRenderer();
        var blocks = renderer.Render("5. a\n6. b\n", consoleWidth: 80);

        using var console = new TestConsole().Width(80);
        console.Write(new Spectre.Console.Rows(blocks));
        var output = string.Join('\n', console.Lines);
        Assert.Contains("5. a", output, StringComparison.Ordinal);
        Assert.Contains("6. b", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NestedList_IndentsChildItems()
    {
        var renderer = new LlmResponseRenderer();
        var blocks = renderer.Render("- a\n  - b\n", consoleWidth: 80);

        using var console = new TestConsole().Width(80);
        console.Write(new Spectre.Console.Rows(blocks));
        var output = string.Join('\n', console.Lines);
        Assert.Contains("- a", output, StringComparison.Ordinal);
        Assert.Contains("  - b", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_List_RespectsMaxListItemsAndAddsTruncationMarker()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 10; i++)
        {
            sb.AppendLine($"- item{i}");
        }

        var renderer = new LlmResponseRenderer(new LlmResponseRenderer.Options { MaxListItems = 2 });
        var blocks = renderer.Render(sb.ToString(), consoleWidth: 80);

        using var console = new TestConsole().Width(80);
        console.Write(new Spectre.Console.Rows(blocks));
        var output = string.Join('\n', console.Lines);
        Assert.Contains("item0", output, StringComparison.Ordinal);
        Assert.Contains("item1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("item2", output, StringComparison.Ordinal);
        Assert.Contains("list truncated", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_Link_RendersLabelAndUrl()
    {
        var renderer = new LlmResponseRenderer();
        var blocks = renderer.Render("[x](https://example.com)\n", consoleWidth: 80);

        using var console = new TestConsole().Width(80);
        console.Write(new Spectre.Console.Rows(blocks));
        var output = string.Join('\n', console.Lines);
        Assert.Contains("x", output, StringComparison.Ordinal);
        Assert.Contains("https://example.com", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Table_WhenTooWide_FallsBackToPanelText()
    {
        var renderer = new LlmResponseRenderer(new LlmResponseRenderer.Options { MaxTableColumns = 1 });
        var blocks = renderer.Render("| A | B |\n|---|---|\n| a | b |\n", consoleWidth: 80);

        using var console = new TestConsole().Width(80);
        console.Write(new Spectre.Console.Rows(blocks));
        var output = string.Join('\n', console.Lines);
        Assert.Contains("a", output, StringComparison.Ordinal);
        Assert.Contains("b", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_InlineFormatting_RendersTextWithoutRawMarkdownMarkers()
    {
        var renderer = new LlmResponseRenderer();
        var blocks = renderer.Render("This is *it* and **bold** and `code`.\n", consoleWidth: 80);

        using var console = new TestConsole().Width(80);
        console.Write(new Spectre.Console.Rows(blocks));
        var output = string.Join('\n', console.Lines);
        Assert.Contains("it", output, StringComparison.Ordinal);
        Assert.Contains("bold", output, StringComparison.Ordinal);
        Assert.Contains("code", output, StringComparison.Ordinal);
        Assert.DoesNotContain("`", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Link_WithEmptyLabel_RendersUrl()
    {
        var renderer = new LlmResponseRenderer();
        var blocks = renderer.Render("[](https://example.com)\n", consoleWidth: 80);

        using var console = new TestConsole().Width(80);
        console.Write(new Spectre.Console.Rows(blocks));
        var output = string.Join('\n', console.Lines);
        Assert.Contains("https://example.com", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Heading_Level3_UsesMarkup()
    {
        var renderer = new LlmResponseRenderer();
        var blocks = renderer.Render("### Title\n", consoleWidth: 80);

        Assert.NotEmpty(blocks);
        Assert.IsNotType<Rule>(blocks[0]);
    }

    [Fact]
    public void Render_CodeBlock_WhenTooLong_Truncates()
    {
        var renderer = new LlmResponseRenderer(new LlmResponseRenderer.Options { MaxCodeBlockChars = 40 });
        var blocks = renderer.Render("```txt\n" + new string('x', 200) + "\n```\n", consoleWidth: 80);

        using var console = new TestConsole().Width(80);
        console.Write(new Spectre.Console.Rows(blocks));
        var output = string.Join('\n', console.Lines);
        Assert.Contains("truncated", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_WhenMarkdownExceedsMaxChars_FallsBackToAnsiText()
    {
        var renderer = new LlmResponseRenderer(new LlmResponseRenderer.Options { MaxMarkdownChars = 10 });
        var blocks = renderer.Render("\u001B[31mHELLO\u001B[0m world", consoleWidth: 80);

        using var console = new TestConsole().Width(80);
        console.Write(new Spectre.Console.Rows(blocks));
        var output = string.Join('\n', console.Lines);
        Assert.Contains("HELLO", output, StringComparison.Ordinal);
        Assert.Contains("world", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_HtmlBlock_FallsBackToToString()
    {
        var renderer = new LlmResponseRenderer();
        var blocks = renderer.Render("<div>hi</div>\n", consoleWidth: 80);

        using var console = new TestConsole().Width(80);
        console.Write(new Spectre.Console.Rows(blocks));
        var output = string.Join('\n', console.Lines);
        Assert.Contains("HtmlBlock", output, StringComparison.OrdinalIgnoreCase);
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
