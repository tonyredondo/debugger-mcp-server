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
