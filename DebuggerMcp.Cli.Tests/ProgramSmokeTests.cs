using Xunit;

namespace DebuggerMcp.Cli.Tests;

/// <summary>
/// Smoke tests for the CLI entry point.
/// </summary>
public class ProgramSmokeTests
{
    [Fact]
    public async Task Main_VersionCommand_ReturnsZero()
    {
        var exitCode = await DebuggerMcp.Cli.Program.Main(["version"]);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Main_Help_ReturnsZero()
    {
        var exitCode = await DebuggerMcp.Cli.Program.Main(["--help"]);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Main_UnknownCommand_ReturnsNonZero()
    {
        var exitCode = await DebuggerMcp.Cli.Program.Main(["definitely-not-a-command"]);
        Assert.NotEqual(0, exitCode);
    }
}

