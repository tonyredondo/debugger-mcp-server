using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Help;
using DebuggerMcp.Cli.Shell;
using Spectre.Console.Testing;

namespace DebuggerMcp.Cli.Tests.Help;

/// <summary>
/// Rendering-oriented tests for <see cref="HelpSystem"/> helpers.
/// </summary>
public class HelpSystemRenderingTests
{
    [Fact]
    public void ShowCommandHelp_WithKnownCommand_WritesDetailedHelp()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var ok = HelpSystem.ShowCommandHelp(output, "connect");

        Assert.True(ok);
        Assert.Contains("CONNECT", console.Output);
        Assert.Contains("USAGE", console.Output);
        Assert.Contains("CATEGORY", console.Output);
    }

    [Fact]
    public void ShowContextualHelp_WhenInitialState_ShowsConnectHint()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();

        HelpSystem.ShowContextualHelp(output, state);

        Assert.Contains("SUGGESTED COMMANDS", console.Output);
        Assert.Contains("connect", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShowContextualHelp_WhenDumpLoaded_ShowsAnalysisHints()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("s", "lldb");
        state.SetDumpLoaded("d");

        HelpSystem.ShowContextualHelp(output, state);

        Assert.Contains("analyze", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("report", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exec", console.Output, StringComparison.OrdinalIgnoreCase);
    }
}

