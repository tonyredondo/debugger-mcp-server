using Xunit;

namespace DebuggerMcp.Cli.Tests;

/// <summary>
/// Tests for CLI command line parsing in <see cref="DebuggerMcp.Cli.Program"/>.
/// </summary>
public class ProgramCommandLineParsingTests
{
    [Fact]
    public void ParseCommandLine_SimpleTokens_SplitsOnSpaces()
    {
        var parts = DebuggerMcp.Cli.Program.ParseCommandLine("connect http://localhost:5000");

        Assert.Equal(["connect", "http://localhost:5000"], parts);
    }

    [Fact]
    public void ParseCommandLine_DoubleQuotedToken_KeepsSpacesInsideToken()
    {
        var parts = DebuggerMcp.Cli.Program.ParseCommandLine("set key \"value with spaces\"");

        Assert.Equal(["set", "key", "value with spaces"], parts);
    }

    [Fact]
    public void ParseCommandLine_SingleQuotedToken_KeepsSpacesInsideToken()
    {
        var parts = DebuggerMcp.Cli.Program.ParseCommandLine("set key 'value with spaces'");

        Assert.Equal(["set", "key", "value with spaces"], parts);
    }

    [Fact]
    public void ParseCommandLine_LeadingAndMultipleSpaces_IgnoresExtraSeparators()
    {
        var parts = DebuggerMcp.Cli.Program.ParseCommandLine("   dumps   list   ");

        Assert.Equal(["dumps", "list"], parts);
    }

    [Fact]
    public void ParseCommandLine_UnterminatedQuote_TreatsRestAsToken()
    {
        var parts = DebuggerMcp.Cli.Program.ParseCommandLine("connect \"http://localhost:5000");

        Assert.Equal(["connect", "http://localhost:5000"], parts);
    }
}
