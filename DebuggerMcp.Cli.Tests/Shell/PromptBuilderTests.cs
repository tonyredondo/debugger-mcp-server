using DebuggerMcp.Cli.Shell;

namespace DebuggerMcp.Cli.Tests.Shell;

/// <summary>
/// Tests for <see cref="PromptBuilder"/>.
/// </summary>
public class PromptBuilderTests
{
    [Fact]
    public void BuildPlain_Initial_ReturnsDefaultPrompt()
    {
        // Arrange
        var state = new ShellState();

        // Act
        var prompt = PromptBuilder.BuildPlain(state);

        // Assert
        Assert.Equal("dbg-mcp> ", prompt);
    }

    [Fact]
    public void BuildPlain_Connected_IncludesServerDisplay()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");

        // Act
        var prompt = PromptBuilder.BuildPlain(state);

        // Assert
        Assert.Equal("dbg-mcp [localhost:5000]> ", prompt);
    }

    [Fact]
    public void BuildPlain_WithSession_IncludesSessionId()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("d0307dc3-5256-4eae-bbd2-5f12e67c6120", "WinDbg");

        // Act
        var prompt = PromptBuilder.BuildPlain(state);

        // Assert
        Assert.Contains("session:d0307dc3", prompt);
    }

    [Fact]
    public void BuildPlain_WithDump_IncludesDumpId()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("d0307dc3-5256-4eae-bbd2-5f12e67c6120", "WinDbg");
        state.SetDumpLoaded("abc123");

        // Act
        var prompt = PromptBuilder.BuildPlain(state);

        // Assert
        Assert.Contains("dump:abc123", prompt);
    }

    [Fact]
    public void BuildMarkup_Initial_ReturnsFormattedPrompt()
    {
        // Arrange
        var state = new ShellState();

        // Act
        var prompt = PromptBuilder.BuildMarkup(state);

        // Assert
        Assert.Contains("[grey]dbg-mcp[/]", prompt);
        Assert.Contains("[grey]>[/]", prompt);
    }

    [Fact]
    public void BuildMarkup_Connected_IncludesColoredServer()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");

        // Act
        var prompt = PromptBuilder.BuildMarkup(state);

        // Assert
        Assert.Contains("[cyan]", prompt);
        Assert.Contains("localhost:5000", prompt);
    }

    [Fact]
    public void GetPromptLength_ReturnsCorrectLength()
    {
        // Arrange
        var state = new ShellState();

        // Act
        var length = PromptBuilder.GetPromptLength(state);

        // Assert
        Assert.Equal("dbg-mcp> ".Length, length);
    }

    [Fact]
    public void BuildStatusLine_Initial_SuggestsConnect()
    {
        // Arrange
        var state = new ShellState();

        // Act
        var status = PromptBuilder.BuildStatusLine(state);

        // Assert
        Assert.Contains("connect", status.ToLower());
    }

    [Fact]
    public void BuildStatusLine_Connected_SuggestsSession()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");

        // Act
        var status = PromptBuilder.BuildStatusLine(state);

        // Assert
        Assert.Contains("session", status.ToLower());
    }

    [Fact]
    public void BuildStatusLine_DumpLoaded_SuggestsExec()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "WinDbg");
        state.SetDumpLoaded("dump-123");

        // Act
        var status = PromptBuilder.BuildStatusLine(state);

        // Assert
        Assert.Contains("exec", status.ToLower());
    }

    [Fact]
    public void GetContextualHints_Initial_SuggestsConnect()
    {
        // Arrange
        var state = new ShellState();

        // Act
        var hints = PromptBuilder.GetContextualHints(state).ToList();

        // Assert
        Assert.Contains("connect <url>", hints);
        Assert.Contains("help", hints);
        Assert.Contains("exit", hints);
    }

    [Fact]
    public void GetContextualHints_DumpLoaded_SuggestsDebugCommands()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "WinDbg");
        state.SetDumpLoaded("dump-123");

        // Act
        var hints = PromptBuilder.GetContextualHints(state).ToList();

        // Assert
        Assert.Contains("exec <cmd>", hints);
        Assert.Contains("analyze crash -o <file>", hints);
        Assert.Contains("close", hints);
    }
}
