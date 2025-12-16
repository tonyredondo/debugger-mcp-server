using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DebuggerMcp.Cli.Client;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Client;

[Collection("NonParallelConsole")]
public class McpClientToolResultExtractionTests
{
    private sealed class RespondingHandler(
        ConcurrentDictionary<int, TaskCompletionSource<string>> pendingRequests,
        Func<JsonElement, string> responseFactory)
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

            if (pendingRequests.TryGetValue(id, out var tcs))
            {
                var responseJson = responseFactory(root);
                tcs.TrySetResult(responseJson);
            }

            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
            };
        }
    }

    private static McpClient CreateClient(Func<JsonElement, string> responseFactory)
    {
        var client = new McpClient();

        var pendingField = typeof(McpClient).GetField("_pendingRequests", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(pendingField);
        var pending = (ConcurrentDictionary<int, TaskCompletionSource<string>>)pendingField!.GetValue(client)!;

        var httpClientField = typeof(McpClient).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(httpClientField);

        var messageEndpointField = typeof(McpClient).GetField("_messageEndpoint", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(messageEndpointField);

        httpClientField!.SetValue(client, new HttpClient(new RespondingHandler(pending, responseFactory)));
        messageEndpointField!.SetValue(client, "http://localhost/message");

        return client;
    }

    [Fact]
    public async Task ExecuteCommandAsync_WhenToolReturnsEmptyText_ReturnsEmptyString()
    {
        await using var client = CreateClient(root =>
        {
            var id = root.GetProperty("id").GetInt32();
            return $@"{{""jsonrpc"":""2.0"",""id"":{id},""result"":{{""content"":[{{""type"":""text"",""text"":""""}}],""isError"":false}}}}";
        });

        var result = await client.ExecuteCommandAsync("s1", "user1", "dumpheap -type Foo");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WhenToolReturnsErrorWithEmptyText_ThrowsWithNoDetailsMessage()
    {
        await using var client = CreateClient(root =>
        {
            var id = root.GetProperty("id").GetInt32();
            return $@"{{""jsonrpc"":""2.0"",""id"":{id},""result"":{{""content"":[{{""type"":""text"",""text"":""""}}],""isError"":true}}}}";
        });

        var ex = await Assert.ThrowsAsync<McpClientException>(() =>
            client.ExecuteCommandAsync("s1", "user1", "k"));

        Assert.Equal("Tool returned an error with no details", ex.Message);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WhenEmptyTextAndNonTextContent_ReturnsNonTextJson()
    {
        await using var client = CreateClient(root =>
        {
            var id = root.GetProperty("id").GetInt32();
            return $@"{{""jsonrpc"":""2.0"",""id"":{id},""result"":{{""content"":[{{""type"":""text"",""text"":""""}},{{""type"":""resource"",""text"":""abc""}}],""isError"":false}}}}";
        });

        var result = await client.ExecuteCommandAsync("s1", "user1", "k");

        Assert.Equal("{\"type\":\"resource\",\"text\":\"abc\"}", result);
    }
}

