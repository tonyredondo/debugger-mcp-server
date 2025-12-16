using DebuggerMcp.Cli.Client;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Shell;
using Moq;
using Spectre.Console.Testing;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Shell;

public class LastSessionAutoRestoreTests
{
    [Fact]
    public async Task TryRestoreAsync_WhenLastSessionExists_RestoresAndSyncsState()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var state = new ShellState
        {
            Settings = new ConnectionSettings
            {
                ServerUrl = "http://localhost:5000",
                UserId = "u"
            }
        };
        state.SetConnected("http://localhost:5000");
        state.Settings.SetLastSessionId(state.Settings.ServerUrl, state.Settings.UserId, "session-123");

        var mcpClient = new Mock<IMcpClient>(MockBehavior.Strict);
        mcpClient.SetupGet(c => c.IsConnected).Returns(true);
        mcpClient.SetupGet(c => c.AvailableTools).Returns([]);
        mcpClient
            .Setup(c => c.RestoreSessionAsync("session-123", "u", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Session restored successfully.");
        mcpClient
            .Setup(c => c.ListSessionsAsync("u", It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"sessions":[{"sessionId":"session-123","currentDumpId":"dump-aaa","createdAtUtc":"2025-12-01T00:00:00Z","lastActivityUtc":"2025-12-01T00:00:00Z"}]}""");
        mcpClient
            .Setup(c => c.GetDebuggerInfoAsync("session-123", "u", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Debugger Type: LLDB");

        var result = await LastSessionAutoRestore.TryRestoreAsync(output, state, mcpClient.Object);

        Assert.True(result.Restored);
        Assert.False(result.ClearedSavedSession);
        Assert.Equal("session-123", state.SessionId);
        Assert.Equal("dump-aaa", state.DumpId);
        Assert.Equal("LLDB", state.DebuggerType);
    }

    [Fact]
    public async Task TryRestoreAsync_WhenRestoreFails_ClearsSavedLastSession()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var state = new ShellState
        {
            Settings = new ConnectionSettings
            {
                ServerUrl = "http://localhost:5000",
                UserId = "u"
            }
        };
        state.SetConnected("http://localhost:5000");
        state.Settings.SetLastSessionId(state.Settings.ServerUrl, state.Settings.UserId, "session-123");

        var mcpClient = new Mock<IMcpClient>(MockBehavior.Strict);
        mcpClient.SetupGet(c => c.IsConnected).Returns(true);
        mcpClient.SetupGet(c => c.AvailableTools).Returns([]);
        mcpClient
            .Setup(c => c.RestoreSessionAsync("session-123", "u", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Error: Session not found");

        var result = await LastSessionAutoRestore.TryRestoreAsync(output, state, mcpClient.Object);

        Assert.False(result.Restored);
        Assert.True(result.ClearedSavedSession);
        Assert.Null(state.SessionId);
        Assert.Null(state.Settings.GetLastSessionId("http://localhost:5000", "u"));
    }
}

