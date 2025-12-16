using DebuggerMcp.Cli.Shell.Transcript;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Shell.Transcript;

public class TranscriptScopeTests
{
    [Fact]
    public void Matches_NormalizesServerUrlAndIds()
    {
        var entry = new CliTranscriptEntry
        {
            ServerUrl = "http://localhost:5000/",
            SessionId = " s1 ",
            DumpId = "d1"
        };

        var ok = TranscriptScope.Matches(entry, "http://localhost:5000", "s1", "d1");
        Assert.True(ok);
    }

    [Fact]
    public void Matches_ReturnsFalse_WhenAnyScopeComponentDiffers()
    {
        var entry = new CliTranscriptEntry
        {
            ServerUrl = "http://localhost:5000",
            SessionId = "s1",
            DumpId = "d1"
        };

        Assert.False(TranscriptScope.Matches(entry, "http://localhost:5001", "s1", "d1"));
        Assert.False(TranscriptScope.Matches(entry, "http://localhost:5000", "s2", "d1"));
        Assert.False(TranscriptScope.Matches(entry, "http://localhost:5000", "s1", "d2"));
    }
}

