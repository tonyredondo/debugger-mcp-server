using DebuggerMcp.Cli.Client;

namespace DebuggerMcp.Cli.Tests.Client;

/// <summary>
/// Unit tests for the SSE parsing helpers in <see cref="McpClient"/>.
/// </summary>
public class McpClientSseParsingTests
{
    [Fact]
    public async Task TryReadMessageEndpointAsync_WhenDataIsRelativePath_ReturnsAbsoluteUrl()
    {
        var sse = "data: endpoint\n" +
                  "data: /mcp/message?sessionId=abc\n";

        using var reader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sse)));

        var endpoint = await McpClient.TryReadMessageEndpointAsync(reader, "http://localhost:5000", CancellationToken.None);

        Assert.Equal("http://localhost:5000/mcp/message?sessionId=abc", endpoint);
    }

    [Fact]
    public async Task TryReadMessageEndpointAsync_WhenDataIsAbsoluteUrl_ReturnsAbsoluteUrl()
    {
        var sse = "data: https://example.test/mcp/message?sessionId=abc\n";

        using var reader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sse)));

        var endpoint = await McpClient.TryReadMessageEndpointAsync(reader, "http://localhost:5000", CancellationToken.None);

        Assert.Equal("https://example.test/mcp/message?sessionId=abc", endpoint);
    }

    [Fact]
    public async Task TryReadMessageEndpointAsync_WhenDataIsJson_ReturnsAbsoluteUrl()
    {
        var sse = "data: {\"endpoint\":\"/mcp/message?sessionId=abc\"}\n";

        using var reader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sse)));

        var endpoint = await McpClient.TryReadMessageEndpointAsync(reader, "http://localhost:5000", CancellationToken.None);

        Assert.Equal("http://localhost:5000/mcp/message?sessionId=abc", endpoint);
    }

    [Fact]
    public void TryProcessSseEventPayload_WhenPayloadHasNumericId_InvokesCallback()
    {
        int? seenId = null;
        string? seenPayload = null;

        var ok = McpClient.TryProcessSseEventPayload("{\"jsonrpc\":\"2.0\",\"id\":123,\"result\":{}}", (id, payload) =>
        {
            seenId = id;
            seenPayload = payload;
        });

        Assert.True(ok);
        Assert.Equal(123, seenId);
        Assert.Contains("\"id\":123", seenPayload);
    }

    [Fact]
    public void TryProcessSseEventPayload_WhenPayloadHasStringId_InvokesCallback()
    {
        int? seenId = null;

        var ok = McpClient.TryProcessSseEventPayload("{\"id\":\"42\",\"result\":{}}", (id, _) => seenId = id);

        Assert.True(ok);
        Assert.Equal(42, seenId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("{not-json}")]
    public void TryProcessSseEventPayload_WhenInvalid_ReturnsFalse(string? payload)
    {
        var ok = McpClient.TryProcessSseEventPayload(payload, (_, _) => { });
        Assert.False(ok);
    }
}

