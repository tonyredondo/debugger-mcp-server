using System.Reflection;
using DebuggerMcp.Cli.Client;
using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Shell;
using Spectre.Console.Testing;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Commands;

public class AnalyzeCommandTests
{
    [Fact]
    public async Task HandleAnalyzeAsync_WithoutOutputFile_PrintsError()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "LLDB");
        state.SetDumpLoaded("dump-123");

        var mcpClient = CreateConnectedMcpClientForTests();

        var task = (Task)InvokePrivate(
            "HandleAnalyzeAsync",
            new object?[]
            {
                new[] { "ai" },
                output,
                state,
                mcpClient
            });

        await task;

        Assert.Contains("Output file is required", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("analyze ai -o", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static McpClient CreateConnectedMcpClientForTests()
    {
        var client = new McpClient();

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

