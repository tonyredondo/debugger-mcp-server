using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DebuggerMcp.Cli.Client;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Client;

[Collection("NonParallelConsole")]
public class McpClientServerRequestHandlingTests
{
    private sealed class CapturingHandler(ConcurrentQueue<string> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            bodies.Enqueue(body);

            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
            };
        }
    }

    private static McpClient CreateClient(ConcurrentQueue<string> postedBodies)
    {
        var client = new McpClient();

        var httpClientField = typeof(McpClient).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(httpClientField);

        var messageEndpointField = typeof(McpClient).GetField("_messageEndpoint", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(messageEndpointField);

        httpClientField!.SetValue(client, new HttpClient(new CapturingHandler(postedBodies)));
        messageEndpointField!.SetValue(client, "http://localhost/mcp/message");

        return client;
    }

    [Fact]
    public async Task TryHandleServerRequestAsync_WhenHandlerRegistered_PostsJsonRpcResult()
    {
        var posted = new ConcurrentQueue<string>();
        var client = CreateClient(posted);
        await using var _ = client;

        client.RegisterServerRequestHandler("sampling/createMessage", (p, _) =>
        {
            Assert.True(p.HasValue);
            return Task.FromResult<object?>(new { role = "assistant", content = new[] { new { type = "text", text = "ok" } } });
        });

        var requestJson = """
        {"jsonrpc":"2.0","id":7,"method":"sampling/createMessage","params":{"systemPrompt":"SYS","messages":[{"role":"user","content":"hi"}]}}
        """;

        var handled = await client.TryHandleServerRequestAsync(requestJson, CancellationToken.None);

        Assert.True(handled);
        Assert.True(posted.TryDequeue(out var responseJson));

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        Assert.Equal(7, root.GetProperty("id").GetInt32());
        Assert.True(root.TryGetProperty("result", out var result));
        Assert.Equal("assistant", result.GetProperty("role").GetString());
    }

    [Fact]
    public async Task TryHandleServerRequestAsync_WhenNoHandlerRegistered_PostsMethodNotFoundError()
    {
        var posted = new ConcurrentQueue<string>();
        var client = CreateClient(posted);
        await using var _ = client;

        var requestJson = """
        {"jsonrpc":"2.0","id":"abc","method":"sampling/createMessage","params":{}}
        """;

        var handled = await client.TryHandleServerRequestAsync(requestJson, CancellationToken.None);

        Assert.True(handled);
        Assert.True(posted.TryDequeue(out var responseJson));

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        Assert.Equal("abc", root.GetProperty("id").GetString());
        Assert.True(root.TryGetProperty("error", out var error));
        Assert.Equal(-32601, error.GetProperty("code").GetInt32());
    }
}

