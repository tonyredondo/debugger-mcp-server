using DebuggerMcp.Cli.Shell.Transcript;

namespace DebuggerMcp.Cli.Tests.Shell.Transcript;

public class AnsiTextTests
{
    [Fact]
    public void StripAnsi_RemovesEscapeSequences()
    {
        var input = "\u001b[31mred\u001b[0m plain";
        var output = AnsiText.StripAnsi(input);
        Assert.Equal("red plain", output);
    }
}

