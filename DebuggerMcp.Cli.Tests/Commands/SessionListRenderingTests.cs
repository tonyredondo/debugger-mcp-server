using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Models;
using DebuggerMcp.Cli.Shell;
using Spectre.Console.Testing;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Commands;

public class SessionListRenderingTests
{
    [Fact]
    public void RenderSessionListTable_WritesSessionsAsTable()
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        var output = new ConsoleOutput(console);
        var state = new ShellState { IsConnected = true, SessionId = "7532ff32-a39a-4d22-906c-c16b31e67a38" };
        var nowUtc = DateTime.UtcNow;

        var response = new SessionListResponse
        {
            UserId = "u",
            Total = 2,
            Sessions =
            [
                new SessionListItem
                {
                    SessionId = state.SessionId,
                    CreatedAtUtc = nowUtc.AddDays(-2).ToString("O"),
                    LastActivityUtc = nowUtc.AddDays(-1).ToString("O"),
                    CurrentDumpId = "6239b1aa-d3f9-441d-b43d-2460aeaa2204"
                },
                new SessionListItem
                {
                    SessionId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                    CreatedAtUtc = nowUtc.AddDays(-10).ToString("O"),
                    LastActivityUtc = nowUtc.AddDays(-9).ToString("O"),
                    CurrentDumpId = null
                }
            ]
        };

        Program.RenderSessionListTable(console, output, state, response);

        Assert.Contains("Sessions", console.Output);
        Assert.Contains("ID", console.Output);
        Assert.Contains("Created", console.Output);
        Assert.Contains("Last Activity", console.Output);
        Assert.Contains("Dump", console.Output);
        Assert.Contains("6239b1aa", console.Output);
        Assert.Contains("aaaaaaaa", console.Output);
        Assert.Contains("day ago", console.Output);
    }
}
