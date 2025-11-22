using DebuggerMcp.Cli.Shell;

namespace DebuggerMcp.Cli.Tests.Shell;

/// <summary>
/// Tests for advanced commands (watch, report, sourcelink) auto-completion.
/// </summary>
public class AdvancedCommandsTests
{
    [Fact]
    public async Task AutoComplete_WatchCommand_ReturnsSubcommands()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "WinDbg");
        state.SetDumpLoaded("dump-123");
        var autoComplete = new AutoComplete(state);

        // Act
        var result = await autoComplete.GetCompletionsAsync("watch ", 6);

        // Assert
        Assert.True(result.HasCompletions);
        Assert.Contains("add", result.Completions);
        Assert.Contains("list", result.Completions);
        Assert.Contains("eval", result.Completions);
        Assert.Contains("remove", result.Completions);
        Assert.Contains("clear", result.Completions);
    }

    [Fact]
    public async Task AutoComplete_ReportCommand_ReturnsOptions()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "WinDbg");
        state.SetDumpLoaded("dump-123");
        var autoComplete = new AutoComplete(state);

        // Act
        var result = await autoComplete.GetCompletionsAsync("report ", 7);

        // Assert
        Assert.True(result.HasCompletions);
        Assert.Contains("--format", result.Completions);
        Assert.Contains("--output", result.Completions);
        Assert.Contains("--summary", result.Completions);
        Assert.Contains("markdown", result.Completions);
        Assert.Contains("html", result.Completions);
        Assert.Contains("json", result.Completions);
    }

    [Fact]
    public async Task AutoComplete_SourcelinkCommand_ReturnsSubcommands()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "WinDbg");
        state.SetDumpLoaded("dump-123");
        var autoComplete = new AutoComplete(state);

        // Act
        var result = await autoComplete.GetCompletionsAsync("sourcelink ", 11);

        // Assert
        Assert.True(result.HasCompletions);
        Assert.Contains("resolve", result.Completions);
        Assert.Contains("info", result.Completions);
    }

    [Fact]
    public async Task AutoComplete_WatchPartialSubcommand_ReturnsMatches()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "WinDbg");
        state.SetDumpLoaded("dump-123");
        var autoComplete = new AutoComplete(state);

        // Act
        var result = await autoComplete.GetCompletionsAsync("watch a", 7);

        // Assert
        Assert.True(result.HasCompletions);
        Assert.Contains("add", result.Completions);
        Assert.DoesNotContain("list", result.Completions);
    }

    [Fact]
    public async Task AutoComplete_ReportPartialFormat_ReturnsMatches()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "WinDbg");
        state.SetDumpLoaded("dump-123");
        var autoComplete = new AutoComplete(state);

        // Act
        var result = await autoComplete.GetCompletionsAsync("report m", 8);

        // Assert
        Assert.True(result.HasCompletions);
        Assert.Contains("markdown", result.Completions);
        Assert.DoesNotContain("html", result.Completions);
    }

    [Fact]
    public async Task AutoComplete_DumpLoaded_IncludesAdvancedCommands()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "WinDbg");
        state.SetDumpLoaded("dump-123");
        var autoComplete = new AutoComplete(state);

        // Act
        var result = await autoComplete.GetCompletionsAsync("", 0);

        // Assert
        Assert.True(result.HasCompletions);
        Assert.Contains("watch", result.Completions);
        Assert.Contains("report", result.Completions);
        Assert.Contains("sourcelink", result.Completions);
    }
}

