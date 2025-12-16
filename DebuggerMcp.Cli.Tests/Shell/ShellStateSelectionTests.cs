using DebuggerMcp.Cli.Shell;

namespace DebuggerMcp.Cli.Tests.Shell;

public class ShellStateSelectionTests
{
    [Fact]
    public void Level_WhenDumpSelectedButNotLoaded_RemainsSession()
    {
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "LLDB");
        state.SetSelectedDump("dump-selected");

        Assert.True(state.HasDumpSelected);
        Assert.False(state.HasDumpLoaded);
        Assert.Equal(ShellStateLevel.Session, state.Level);
    }

    [Fact]
    public void Prompt_WhenDumpSelectedButNotLoaded_DoesNotShowDump()
    {
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "LLDB");
        state.SetSelectedDump("dump-selected");

        var prompt = PromptBuilder.BuildPlain(state);

        Assert.DoesNotContain("dump:", prompt);
    }
}

