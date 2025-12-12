using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Shell;
using Spectre.Console.Testing;
using Xunit;

namespace DebuggerMcp.Cli.Tests;

/// <summary>
/// Tests for history command rendering in <see cref="DebuggerMcp.Cli.Program"/>.
/// </summary>
public class ProgramHistoryCommandTests
{
    [Fact]
    public void HandleHistoryCommand_NoArgsAndNoEntries_ShowsNoHistory()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var history = new CommandHistory();

        DebuggerMcp.Cli.Program.HandleHistoryCommand([], output, history);

        Assert.Contains("No command history", console.Output);
    }

    [Fact]
    public void HandleHistoryCommand_NoArgsWithEntries_ShowsRecentHistory()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var history = new CommandHistory();
        history.Add("connect http://localhost:5000");
        history.Add("health");

        DebuggerMcp.Cli.Program.HandleHistoryCommand([], output, history);

        Assert.Contains("Command History", console.Output);
        Assert.Contains("connect http://localhost:5000", console.Output);
        Assert.Contains("health", console.Output);
    }

    [Fact]
    public void HandleHistoryCommand_Clear_ClearsHistory()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var history = new CommandHistory();
        history.Add("health");

        DebuggerMcp.Cli.Program.HandleHistoryCommand(["clear"], output, history);

        Assert.Empty(history.Entries);
        Assert.Contains("cleared", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HandleHistoryCommand_SearchWithMatches_ShowsMatchingEntries()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var history = new CommandHistory();
        history.Add("connect http://localhost:5000");
        history.Add("status");
        history.Add("connect http://localhost:5001");

        DebuggerMcp.Cli.Program.HandleHistoryCommand(["search", "connect"], output, history);

        Assert.Contains("History matching", console.Output);
        Assert.Contains("connect http://localhost:5000", console.Output);
        Assert.Contains("connect http://localhost:5001", console.Output);
    }

    [Fact]
    public void HandleHistoryCommand_SearchWithoutMatches_ShowsWarning()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var history = new CommandHistory();
        history.Add("status");

        DebuggerMcp.Cli.Program.HandleHistoryCommand(["search", "connect"], output, history);

        Assert.Contains("No history entries match", console.Output);
    }

    [Fact]
    public void HandleHistoryCommand_Count_ShowsLastNCommands()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var history = new CommandHistory();
        history.Add("cmd1");
        history.Add("cmd2");
        history.Add("cmd3");

        DebuggerMcp.Cli.Program.HandleHistoryCommand(["2"], output, history);

        Assert.Contains("Last 2 commands", console.Output);
        Assert.DoesNotContain("cmd1", console.Output);
        Assert.Contains("cmd2", console.Output);
        Assert.Contains("cmd3", console.Output);
    }

    [Fact]
    public void HandleHistoryCommand_UnknownSubcommand_ShowsUsageError()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var history = new CommandHistory();

        DebuggerMcp.Cli.Program.HandleHistoryCommand(["nope"], output, history);

        Assert.Contains("Unknown subcommand", console.Output);
        Assert.Contains("Usage: history", console.Output);
    }
}

