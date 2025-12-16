using DebuggerMcp.Cli.Client;
using DebuggerMcp.Cli.Shell;
using Moq;

namespace DebuggerMcp.Cli.Tests.Shell;

public class DumpStateRecoveryTests
{
    [Fact]
    public async Task TrySyncOpenedDumpFromServerAsync_WhenNoSession_ClearsDumpIdAndDoesNotCallServer()
    {
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetDumpLoaded("dump-123");
        state.SessionId = null;

        var mcpClient = new Mock<IMcpClient>(MockBehavior.Strict);

        var result = await DumpStateRecovery.TrySyncOpenedDumpFromServerAsync(state, mcpClient.Object);

        Assert.False(result);
        Assert.Null(state.DumpId);
        mcpClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TrySyncOpenedDumpFromServerAsync_WhenSessionFoundAndHasDump_SetsDumpId()
    {
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("s1", "LLDB");
        state.DumpId = null;

        var mcpClient = new Mock<IMcpClient>();
        mcpClient
            .Setup(c => c.ListSessionsAsync(state.Settings.UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"userId\":\"u\",\"total\":1,\"sessions\":[{\"sessionId\":\"s1\",\"currentDumpId\":\"dump-999\"}]}");

        var result = await DumpStateRecovery.TrySyncOpenedDumpFromServerAsync(state, mcpClient.Object);

        Assert.True(result);
        Assert.Equal("dump-999", state.DumpId);
        Assert.Equal("dump-999", state.SelectedDumpId);
    }

    [Fact]
    public async Task TrySyncOpenedDumpFromServerAsync_WhenSessionFoundAndNoDump_ClearsDumpId()
    {
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("s1", "LLDB");
        state.SetDumpLoaded("dump-123");

        var mcpClient = new Mock<IMcpClient>();
        mcpClient
            .Setup(c => c.ListSessionsAsync(state.Settings.UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"userId\":\"u\",\"total\":1,\"sessions\":[{\"sessionId\":\"s1\",\"currentDumpId\":null}]}");

        var result = await DumpStateRecovery.TrySyncOpenedDumpFromServerAsync(state, mcpClient.Object);

        Assert.True(result);
        Assert.Null(state.DumpId);
    }
}

