using DebuggerMcp.Cli.Client;
using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Models;
using DebuggerMcp.Cli.Shell;
using Moq;
using Spectre.Console.Testing;
using System.Text.Json;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Client;

/// <summary>
/// Tests for <see cref="ConnectionRecovery"/>.
/// </summary>
public class ConnectionRecoveryTests
{
    [Fact]
    public async Task CheckConnectionAsync_WhenNotConnected_ReturnsFalse()
    {
        var state = new ShellState { IsConnected = false };
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var httpClient = new Mock<IHttpApiClient>(MockBehavior.Strict);
        var mcpClient = new Mock<IMcpClient>(MockBehavior.Strict);

        var recovery = new ConnectionRecovery(httpClient.Object, mcpClient.Object, state, output);

        var isHealthy = await recovery.CheckConnectionAsync();

        Assert.False(isHealthy);
    }

    [Theory]
    [InlineData("Healthy", true)]
    [InlineData("healthy", true)]
    [InlineData("Unhealthy", false)]
    public async Task CheckConnectionAsync_WhenConnected_UsesHealthStatusIsHealthy(string status, bool expected)
    {
        var state = new ShellState { IsConnected = true };
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var httpClient = new Mock<IHttpApiClient>();
        httpClient
            .Setup(c => c.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthStatus { Status = status });

        var mcpClient = new Mock<IMcpClient>(MockBehavior.Strict);

        var recovery = new ConnectionRecovery(httpClient.Object, mcpClient.Object, state, output);

        var isHealthy = await recovery.CheckConnectionAsync();

        Assert.Equal(expected, isHealthy);
    }

    [Fact]
    public async Task CheckConnectionAsync_WhenHealthThrows_ReturnsFalse()
    {
        var state = new ShellState { IsConnected = true };
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var httpClient = new Mock<IHttpApiClient>();
        httpClient
            .Setup(c => c.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));

        var mcpClient = new Mock<IMcpClient>(MockBehavior.Strict);

        var recovery = new ConnectionRecovery(httpClient.Object, mcpClient.Object, state, output);

        var isHealthy = await recovery.CheckConnectionAsync();

        Assert.False(isHealthy);
    }

    [Fact]
    public async Task TryRecoverAsync_WhenServerUrlMissing_ReturnsFalseAndWarns()
    {
        var state = new ShellState();
        state.Settings.ServerUrl = null;
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var httpClient = new Mock<IHttpApiClient>(MockBehavior.Strict);
        var mcpClient = new Mock<IMcpClient>(MockBehavior.Strict);

        var recovery = new ConnectionRecovery(httpClient.Object, mcpClient.Object, state, output);

        var ok = await recovery.TryRecoverAsync();

        Assert.False(ok);
        Assert.Contains("No server URL", console.Output);
    }

    [Fact]
    public async Task TryRecoverAsync_WhenHealthy_ReconnectsAndReturnsTrue()
    {
        var state = new ShellState { IsConnected = true };
        state.Settings.ServerUrl = "http://localhost:5000";
        state.Settings.ApiKey = "k";

        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var httpClient = new Mock<IHttpApiClient>();
        httpClient
            .Setup(c => c.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthStatus { Status = "Healthy" });

        var mcpClient = new Mock<IMcpClient>();
        mcpClient
            .Setup(c => c.ConnectAsync("http://localhost:5000", "k", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var recovery = new ConnectionRecovery(httpClient.Object, mcpClient.Object, state, output)
        {
            MaxReconnectAttempts = 1,
            ReconnectDelay = TimeSpan.Zero
        };

        var ok = await recovery.TryRecoverAsync();

        Assert.True(ok);
        Assert.Contains("Reconnected successfully", console.Output);
    }

    [Fact]
    public async Task TryRecoverAsync_WhenSessionMissing_ClearsSession()
    {
        var state = new ShellState { IsConnected = true };
        state.Settings.ServerUrl = "http://localhost:5000";
        state.Settings.ApiKey = "k";
        state.Settings.UserId = "u";
        state.SetSession("1234567890abcdef", "LLDB");
        state.SetDumpLoaded("dump-123");

        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var httpClient = new Mock<IHttpApiClient>();
        httpClient
            .Setup(c => c.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthStatus { Status = "Healthy" });

        var mcpClient = new Mock<IMcpClient>();
        mcpClient
            .Setup(c => c.ConnectAsync("http://localhost:5000", "k", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mcpClient
            .Setup(c => c.ListSessionsAsync("u", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonSerializer.Serialize(new SessionListResponse
            {
                UserId = "u",
                Total = 0,
                Sessions = []
            }));

        var recovery = new ConnectionRecovery(httpClient.Object, mcpClient.Object, state, output)
        {
            MaxReconnectAttempts = 1,
            ReconnectDelay = TimeSpan.Zero
        };

        var ok = await recovery.TryRecoverAsync();

        Assert.True(ok);
        Assert.False(state.HasSession);
        Assert.False(state.HasDumpLoaded);
        Assert.Contains("no longer exists", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteWithRecoveryAsync_WhenRecoverableErrorAndRecoverySucceeds_RetriesOperation()
    {
        var state = new ShellState { IsConnected = true };
        state.Settings.ServerUrl = "http://localhost:5000";
        state.Settings.ApiKey = "k";

        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var httpClient = new Mock<IHttpApiClient>();
        httpClient
            .Setup(c => c.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthStatus { Status = "Healthy" });

        var mcpClient = new Mock<IMcpClient>();
        mcpClient
            .Setup(c => c.ConnectAsync("http://localhost:5000", "k", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var recovery = new ConnectionRecovery(httpClient.Object, mcpClient.Object, state, output)
        {
            MaxReconnectAttempts = 1,
            ReconnectDelay = TimeSpan.Zero
        };

        var callCount = 0;
        Task<string> Operation()
        {
            callCount++;
            if (callCount == 1)
            {
                throw new HttpRequestException("network down");
            }
            return Task.FromResult("ok");
        }

        var result = await recovery.ExecuteWithRecoveryAsync(Operation, "test-op");

        Assert.Equal("ok", result);
        Assert.Equal(2, callCount);
        Assert.Contains("Retrying", console.Output);
    }
}
