using DebuggerMcp.Cli.Shell;

namespace DebuggerMcp.Cli.Tests.Shell;

public class SessionExpiryHandlerTests
{
    [Theory]
    [InlineData("Session ID not found", true)]
    [InlineData("session not found", true)]
    [InlineData("Session expired due to inactivity", true)]
    [InlineData("Session does not exist", true)]
    [InlineData("Session no longer exists", true)]
    [InlineData("Dump not loaded", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSessionExpiredMessage_RecognizesCommonMessages(string? message, bool expected)
    {
        Assert.Equal(expected, SessionExpiryHandler.IsSessionExpiredMessage(message));
    }

    [Fact]
    public void ClearExpiredSession_ClearsSessionAndDump()
    {
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "LLDB");
        state.SetDumpLoaded("dump-123");

        SessionExpiryHandler.ClearExpiredSession(state);

        Assert.False(state.HasSession);
        Assert.False(state.HasDumpLoaded);
    }
}

