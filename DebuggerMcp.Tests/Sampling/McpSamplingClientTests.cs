using DebuggerMcp.Sampling;
using Moq;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace DebuggerMcp.Tests.Sampling;

public class McpSamplingClientTests
{
    [Fact]
    public void Ctor_WhenServerIsNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new McpSamplingClient(null!));
    }

    [Fact]
    public void IsSamplingSupported_WhenClientCapabilitiesMissing_ReturnsFalse()
    {
        var server = CreateServer(clientCapabilities: null);
        var client = new McpSamplingClient(server.Object);

        Assert.False(client.IsSamplingSupported);
        Assert.False(client.IsToolUseSupported);
    }

    [Fact]
    public void IsToolUseSupported_WhenSamplingToolsPresent_ReturnsTrue()
    {
        var clientCapabilities = new ClientCapabilities
        {
            Sampling = new SamplingCapability
            {
                Tools = new SamplingToolsCapability()
            }
        };

        var server = CreateServer(clientCapabilities);
        var client = new McpSamplingClient(server.Object);

        Assert.True(client.IsSamplingSupported);
        Assert.True(client.IsToolUseSupported);
    }

    [Fact]
    public async Task RequestCompletionAsync_WhenSamplingNotSupported_Throws()
    {
        var server = CreateServer(clientCapabilities: null);
        var client = new McpSamplingClient(server.Object);

        var request = new CreateMessageRequestParams
        {
            SystemPrompt = "test",
            Messages = [],
            MaxTokens = 1,
            Tools = [],
            ToolChoice = new ToolChoice { Mode = "auto" }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestCompletionAsync(request));
    }

    private static Mock<McpServer> CreateServer(ClientCapabilities? clientCapabilities)
    {
        var server = new Mock<McpServer>(MockBehavior.Loose);
        server.SetupGet(s => s.ClientCapabilities).Returns(clientCapabilities);
        return server;
    }
}
