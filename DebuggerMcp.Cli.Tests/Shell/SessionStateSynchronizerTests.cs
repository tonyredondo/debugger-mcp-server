using DebuggerMcp.Cli.Models;
using DebuggerMcp.Cli.Shell;

namespace DebuggerMcp.Cli.Tests.Shell;

public class SessionStateSynchronizerTests
{
    [Fact]
    public void TrySyncCurrentDumpFromSessionList_WhenSessionHasDump_SetsDumpId()
    {
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("s1", "LLDB");
        state.SetDumpLoaded("old-dump");

        var response = new SessionListResponse
        {
            UserId = "u",
            Total = 1,
            Sessions =
            [
                new SessionListItem
                {
                    SessionId = "s1",
                    CurrentDumpId = "dump-123"
                }
            ]
        };

        var found = SessionStateSynchronizer.TrySyncCurrentDumpFromSessionList(state, response);

        Assert.True(found);
        Assert.Equal("dump-123", state.DumpId);
        Assert.Equal("dump-123", state.SelectedDumpId);
    }

    [Fact]
    public void TrySyncCurrentDumpFromSessionList_WhenSessionHasNoDump_ClearsDumpId()
    {
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("s1", "LLDB");
        state.SetDumpLoaded("old-dump");

        var response = new SessionListResponse
        {
            UserId = "u",
            Total = 1,
            Sessions =
            [
                new SessionListItem
                {
                    SessionId = "s1",
                    CurrentDumpId = null
                }
            ]
        };

        var found = SessionStateSynchronizer.TrySyncCurrentDumpFromSessionList(state, response);

        Assert.True(found);
        Assert.Null(state.DumpId);
    }

    [Fact]
    public void TrySyncCurrentDumpFromSessionList_WhenSessionNotFound_ClearsDumpIdAndReturnsFalse()
    {
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("s1", "LLDB");
        state.SetDumpLoaded("old-dump");

        var response = new SessionListResponse
        {
            UserId = "u",
            Total = 1,
            Sessions =
            [
                new SessionListItem
                {
                    SessionId = "other",
                    CurrentDumpId = "dump-123"
                }
            ]
        };

        var found = SessionStateSynchronizer.TrySyncCurrentDumpFromSessionList(state, response);

        Assert.False(found);
        Assert.Null(state.DumpId);
    }
}
