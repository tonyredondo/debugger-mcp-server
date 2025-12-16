using System.Text.Json;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

[Collection("NonParallelConsole")]
public class McpSamplingCreateMessageHandlerTests
{
    [Fact]
    public async Task HandleAsync_BuildsMessagesFromSystemPromptAndMessagesArray()
    {
        var settings = new LlmSettings { OpenRouterModel = "openrouter/test" };

        IReadOnlyList<ChatMessage>? seenMessages = null;
        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (messages, _) =>
            {
                seenMessages = messages;
                return Task.FromResult("ok");
            });

        using var doc = JsonDocument.Parse("""
        {
          "systemPrompt": "SYS",
          "messages": [
            { "role": "user", "content": "Hello" },
            { "role": "assistant", "content": [ { "type": "text", "text": "Hi" } ] }
          ]
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenMessages);
        Assert.Equal(3, seenMessages!.Count);
        Assert.Equal("system", seenMessages[0].Role);
        Assert.Equal("SYS", seenMessages[0].Content);
        Assert.Equal("user", seenMessages[1].Role);
        Assert.Equal("Hello", seenMessages[1].Content);
        Assert.Equal("assistant", seenMessages[2].Role);
        Assert.Equal("Hi", seenMessages[2].Content);

        Assert.Equal("assistant", result.Role);
        Assert.Equal("openrouter/test", result.Model);
        Assert.Single(result.Content);
        Assert.Equal("text", result.Content[0].Type);
        Assert.Equal("ok", result.Content[0].Text);
    }
}

