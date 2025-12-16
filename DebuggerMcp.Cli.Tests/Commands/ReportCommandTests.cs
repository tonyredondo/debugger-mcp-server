using System.Reflection;
using DebuggerMcp.Cli.Client;
using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Shell;
using Spectre.Console.Testing;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Commands;

/// <summary>
/// Tests for the CLI report command behavior.
/// </summary>
public class ReportCommandTests
{
    [Fact]
    public async Task HandleReportAsync_WithoutOutputFile_PrintsErrorAndDoesNotAttemptReport()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "WinDbg");
        state.SetDumpLoaded("dump-123");

        var mcpClient = CreateConnectedMcpClientForTests();

        var task = (Task)InvokePrivate(
            "HandleReportAsync",
            new object?[]
            {
                Array.Empty<string>(),
                console,
                output,
                state,
                mcpClient,
                null
            });

        await task;

        Assert.Contains("Output file is required", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("report -o", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static McpClient CreateConnectedMcpClientForTests()
    {
        var client = new McpClient();

        // Mark as connected without making network calls.
        var httpClientField = typeof(McpClient).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance);
        var messageEndpointField = typeof(McpClient).GetField("_messageEndpoint", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(httpClientField);
        Assert.NotNull(messageEndpointField);

        httpClientField!.SetValue(client, new HttpClient());
        messageEndpointField!.SetValue(client, "/mcp/messages");

        Assert.True(client.IsConnected);
        return client;
    }

    private static object InvokePrivate(string name, object?[] args)
    {
        var method = typeof(DebuggerMcp.Cli.Program).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, args)!;
    }
}

