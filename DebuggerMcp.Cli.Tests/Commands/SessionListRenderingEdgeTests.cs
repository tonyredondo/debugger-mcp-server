using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Models;
using DebuggerMcp.Cli.Shell;
using Spectre.Console.Testing;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Commands;

public class SessionListRenderingEdgeTests
{
    [Fact]
    public void RenderSessionListTable_WhenNoSessions_PrintsInfo()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState { IsConnected = true };
        var response = new SessionListResponse { UserId = "u", Total = 0, Sessions = [] };

        Program.RenderSessionListTable(console, output, state, response);

        Assert.Contains("No active sessions found", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderSessionListTable_WhenSessionHasNullIdAndUnknownTimestamps_RendersPlaceholders()
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        var output = new ConsoleOutput(console);
        var state = new ShellState { IsConnected = true };

        var response = new SessionListResponse
        {
            UserId = "u",
            Total = 1,
            Sessions =
            [
                new SessionListItem
                {
                    SessionId = null,
                    CreatedAtUtc = null,
                    LastActivityUtc = "not-a-date",
                    CurrentDumpId = null
                }
            ]
        };

        Program.RenderSessionListTable(console, output, state, response);

        Assert.Contains("(unknown)", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not-a-date", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatUtcDateTimeWithAge_WhenTimestampInFuture_AddsMarker()
    {
        var nowUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var future = nowUtc.AddHours(1);

        var formatted = Program.FormatUtcDateTimeWithAge(future, nowUtc, "yyyy-MM-dd HH:mm:ss");

        Assert.Contains("in future", formatted, StringComparison.OrdinalIgnoreCase);
    }
}

