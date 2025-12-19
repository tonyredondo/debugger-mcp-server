using DebuggerMcp.Cli.Shell;

namespace DebuggerMcp.Cli.Tests.Shell;

/// <summary>
/// Tests for analysis-related auto-completion.
/// </summary>
public class AnalysisCommandsTests
{
    [Fact]
    public async Task AutoComplete_AnalyzeCommand_ReturnsAllAnalysisTypes()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "WinDbg");
        state.SetDumpLoaded("dump-123");
        var autoComplete = new AutoComplete(state);

        // Act
        var result = await autoComplete.GetCompletionsAsync("analyze ", 8);

        // Assert
        Assert.True(result.HasCompletions);
        Assert.Contains("crash", result.Completions);
        Assert.Contains("ai", result.Completions);
        Assert.Contains("perf", result.Completions);
        Assert.Contains("cpu", result.Completions);
        Assert.Contains("memory", result.Completions);
        Assert.Contains("gc", result.Completions);
        Assert.Contains("contention", result.Completions);
        Assert.Contains("security", result.Completions);
    }

    [Fact]
    public async Task AutoComplete_AnalyzePartial_ReturnsMatchingTypes()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "WinDbg");
        state.SetDumpLoaded("dump-123");
        var autoComplete = new AutoComplete(state);

        // Act
        var result = await autoComplete.GetCompletionsAsync("analyze c", 9);

        // Assert
        Assert.True(result.HasCompletions);
        Assert.Contains("crash", result.Completions);
        Assert.Contains("cpu", result.Completions);
        Assert.Contains("contention", result.Completions);
        Assert.DoesNotContain("memory", result.Completions);
    }

    [Fact]
    public async Task AutoComplete_CompareCommand_ReturnsComparisonTypes()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "WinDbg");
        state.SetDumpLoaded("dump-123");
        var autoComplete = new AutoComplete(state);

        // Act
        var result = await autoComplete.GetCompletionsAsync("compare ", 8);

        // Assert
        Assert.True(result.HasCompletions);
        Assert.Contains("all", result.Completions);
        Assert.Contains("heap", result.Completions);
        Assert.Contains("threads", result.Completions);
        Assert.Contains("modules", result.Completions);
    }

    [Fact]
    public async Task AutoComplete_HistoryCommand_ReturnsSubcommands()
    {
        // Arrange
        var state = new ShellState();
        var autoComplete = new AutoComplete(state);

        // Act
        var result = await autoComplete.GetCompletionsAsync("history ", 8);

        // Assert
        Assert.True(result.HasCompletions);
        Assert.Contains("clear", result.Completions);
        Assert.Contains("search", result.Completions);
    }
}
