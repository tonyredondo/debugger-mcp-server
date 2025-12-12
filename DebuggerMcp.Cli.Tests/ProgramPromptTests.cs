using DebuggerMcp.Cli.Shell;
using Xunit;

namespace DebuggerMcp.Cli.Tests;

/// <summary>
/// Tests for prompt rendering in <see cref="DebuggerMcp.Cli.Program"/>.
/// </summary>
public class ProgramPromptTests
{
    [Fact]
    public void BuildPrompt_Disconnected_ReturnsBasePrompt()
    {
        var state = new ShellState();

        var prompt = DebuggerMcp.Cli.Program.BuildPrompt(state);

        Assert.Contains("dbg-mcp", prompt);
        Assert.Contains("> ", prompt);
    }

    [Fact]
    public void BuildPrompt_ConnectedWithSessionAndDump_TruncatesIdsAndIncludesDebuggerType()
    {
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("1234567890abcdef", "LLDB");
        state.SetDumpLoaded("abcdef1234567890");

        var prompt = DebuggerMcp.Cli.Program.BuildPrompt(state);

        Assert.Contains("[[localhost:5000]]", prompt);
        Assert.Contains("session:12345678", prompt);
        Assert.Contains("dump:abcdef12", prompt);
        Assert.Contains("(LLDB)", prompt);
    }
}

