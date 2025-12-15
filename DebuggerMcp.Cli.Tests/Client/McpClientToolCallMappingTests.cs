using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DebuggerMcp.Cli.Client;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Client;

[Collection("NonParallelConsole")]
public class McpClientToolCallMappingTests
{
    private sealed record ObservedToolCall(string ToolName, Dictionary<string, JsonElement> Arguments);

    private sealed class CapturingHandler(
        ConcurrentQueue<ObservedToolCall> calls,
        ConcurrentDictionary<int, TaskCompletionSource<string>> pendingRequests)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetInt32();
            var method = root.GetProperty("method").GetString();

            if (method == "tools/call")
            {
                var @params = root.GetProperty("params");
                var toolName = @params.GetProperty("name").GetString() ?? string.Empty;
                var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                if (@params.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in argumentsElement.EnumerateObject())
                    {
                        args[prop.Name] = prop.Value.Clone();
                    }
                }

                calls.Enqueue(new ObservedToolCall(toolName, args));
            }

            // Complete the pending request directly (test shortcut instead of SSE).
            if (pendingRequests.TryGetValue(id, out var tcs))
            {
                var responseJson = $@"{{""jsonrpc"":""2.0"",""id"":{id},""result"":{{""content"":[{{""type"":""text"",""text"":""ok""}}],""isError"":false}}}}";
                tcs.TrySetResult(responseJson);
            }

            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
            };
        }
    }

    private static McpClient CreateClient(ConcurrentQueue<ObservedToolCall> calls)
    {
        var client = new McpClient();

        var pendingField = typeof(McpClient).GetField("_pendingRequests", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(pendingField);
        var pending = (ConcurrentDictionary<int, TaskCompletionSource<string>>)pendingField!.GetValue(client)!;

        var httpClientField = typeof(McpClient).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(httpClientField);

        var messageEndpointField = typeof(McpClient).GetField("_messageEndpoint", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(messageEndpointField);

        var handler = new CapturingHandler(calls, pending);
        httpClientField!.SetValue(client, new HttpClient(handler));
        messageEndpointField!.SetValue(client, "http://localhost/message");

        return client;
    }

    [Fact]
    public async Task CreateSessionAsync_UsesSessionToolWithCreateAction()
    {
        var calls = new ConcurrentQueue<ObservedToolCall>();
        await using var client = CreateClient(calls);

        await client.CreateSessionAsync("user1");

        Assert.True(calls.TryDequeue(out var call));
        Assert.Equal("session", call.ToolName);
        Assert.Equal("create", call.Arguments["action"].GetString());
        Assert.Equal("user1", call.Arguments["userId"].GetString());
    }

    [Fact]
    public async Task OpenDumpAsync_UsesDumpToolWithOpenAction()
    {
        var calls = new ConcurrentQueue<ObservedToolCall>();
        await using var client = CreateClient(calls);

        await client.OpenDumpAsync("s1", "user1", "d1");

        Assert.True(calls.TryDequeue(out var call));
        Assert.Equal("dump", call.ToolName);
        Assert.Equal("open", call.Arguments["action"].GetString());
        Assert.Equal("s1", call.Arguments["sessionId"].GetString());
        Assert.Equal("user1", call.Arguments["userId"].GetString());
        Assert.Equal("d1", call.Arguments["dumpId"].GetString());
    }

    [Fact]
    public async Task ExecuteCommandAsync_UsesExecTool()
    {
        var calls = new ConcurrentQueue<ObservedToolCall>();
        await using var client = CreateClient(calls);

        await client.ExecuteCommandAsync("s1", "user1", "k");

        Assert.True(calls.TryDequeue(out var call));
        Assert.Equal("exec", call.ToolName);
        Assert.Equal("s1", call.Arguments["sessionId"].GetString());
        Assert.Equal("user1", call.Arguments["userId"].GetString());
        Assert.Equal("k", call.Arguments["command"].GetString());
    }

    [Fact]
    public async Task AnalyzeCrashAsync_UsesAnalyzeToolWithCrashKind()
    {
        var calls = new ConcurrentQueue<ObservedToolCall>();
        await using var client = CreateClient(calls);

        await client.AnalyzeCrashAsync("s1", "user1");

        Assert.True(calls.TryDequeue(out var call));
        Assert.Equal("analyze", call.ToolName);
        Assert.Equal("crash", call.Arguments["kind"].GetString());
    }
}

