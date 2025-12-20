using DebuggerMcp.Cli.Display;
using Spectre.Console.Testing;
using Xunit;

namespace DebuggerMcp.Cli.Tests;

/// <summary>
/// Tests for interactive help rendering in <see cref="DebuggerMcp.Cli.Program"/>.
/// </summary>
public class ProgramHelpTests
{
    [Fact]
    public void ShowHelp_NoArgs_ShowsOverview()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        DebuggerMcp.Cli.Program.ShowHelp(output, []);

        Assert.Contains("Command Reference", console.Output);
        Assert.Contains("KEYBOARD SHORTCUTS", console.Output);
    }

    [Fact]
    public void ShowHelp_Category_ShowsCategoryHelp()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        DebuggerMcp.Cli.Program.ShowHelp(output, ["connection"]);

        Assert.Contains("Connection Commands", console.Output);
    }

    [Theory]
    [InlineData("all", "All Commands")]
    [InlineData("commands", "All Commands")]
    public void ShowHelp_AllCommands_ShowsCommandList(string arg, string expected)
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        DebuggerMcp.Cli.Program.ShowHelp(output, [arg]);

        Assert.Contains(expected, console.Output);
    }

    [Theory]
    [InlineData("connect", "CONNECT Command")]
    [InlineData("health", "HEALTH Command")]
    [InlineData("dumps", "DUMPS Command")]
    [InlineData("symbols", "SYMBOLS Command")]
    [InlineData("stats", "STATS Command")]
    [InlineData("open", "OPEN Command")]
    [InlineData("exec", "EXEC Command")]
    [InlineData("x", "EXEC Command")]
    [InlineData("cmd", "CMD Command")]
    [InlineData("showobj", "SHOWOBJ Command")]
    [InlineData("so", "SHOWOBJ Command")]
    [InlineData("analyze", "ANALYZE Command")]
    [InlineData("compare", "COMPARE Command")]
    [InlineData("watch", "WATCH Command")]
    [InlineData("w", "WATCH Command")]
    [InlineData("report", "REPORT Command")]
    [InlineData("sourcelink", "SOURCELINK Command")]
    [InlineData("sl", "SOURCELINK Command")]
    [InlineData("server", "SERVER Command")]
    [InlineData("close", "CLOSE Command")]
    [InlineData("history", "HISTORY Command")]
    [InlineData("copy", "COPY Command")]
    [InlineData("cp", "COPY Command")]
    public void ShowHelp_KnownCommand_ShowsExpectedHeader(string command, string expectedHeader)
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        DebuggerMcp.Cli.Program.ShowHelp(output, [command]);

        Assert.Contains(expectedHeader, console.Output);
    }

    [Fact]
    public void ShowHelp_SessionCategory_ShowsSessionCategoryHelp()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        DebuggerMcp.Cli.Program.ShowHelp(output, ["session"]);

        Assert.Contains("Session Commands", console.Output);
    }

    [Fact]
    public void ShowHelp_SessionCommand_WhenCategoryRemoved_ShowsLegacySessionCommandHelp()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        // Program.ShowHelp checks HelpSystem.Categories before falling back to its legacy switch/case.
        // Temporarily remove the "session" category to cover the command-specific help text.
        var hadSession = DebuggerMcp.Cli.Help.HelpSystem.Categories.TryGetValue("session", out var originalDescription) &&
                         DebuggerMcp.Cli.Help.HelpSystem.Categories.Remove("session");
        try
        {
            DebuggerMcp.Cli.Program.ShowHelp(output, ["session"]);
            Assert.Contains("SESSION Command", console.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (hadSession)
            {
                DebuggerMcp.Cli.Help.HelpSystem.Categories["session"] = originalDescription ?? "Debugging session management";
            }
        }
    }

    [Fact]
    public void ShowHelp_UnknownCommand_ShowsWarning()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        DebuggerMcp.Cli.Program.ShowHelp(output, ["definitely-not-a-command"]);

        Assert.Contains("No help available for command", console.Output);
    }
}
