using System.Text.Json;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

[Collection("NonParallelConsole")]
public class McpSamplingCreateMessageHandlerTests
{
    [Fact]
    public async Task HandleAsync_BuildsChatCompletionRequestAndMapsToolCalls()
    {
        var settings = new LlmSettings { OpenRouterModel = "openrouter/test" };

        ChatCompletionRequest? seenRequest = null;
        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (request, _) =>
            {
                seenRequest = request;
                return Task.FromResult(new ChatCompletionResult
                {
                    Model = "openrouter/test",
                    Text = "ok",
                    ToolCalls =
                    [
                        new ChatToolCall("tc1", "exec", "{\"command\":\"bt\"}")
                    ]
                });
            });

        using var doc = JsonDocument.Parse("""
        {
          "systemPrompt": "SYS",
          "maxTokens": 123,
          "tools": [
            {
              "name": "exec",
              "description": "run debugger command",
              "inputSchema": { "type":"object","properties":{"command":{"type":"string"}},"required":["command"] }
            }
          ],
          "toolChoice": { "mode":"auto" },
          "messages": [
            { "role": "user", "content": "Hello" },
            { "role": "assistant", "content": [ { "type": "text", "text": "Hi" } ] }
          ]
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Equal(123, seenRequest!.MaxTokens);
        Assert.NotNull(seenRequest.Tools);
        Assert.Single(seenRequest.Tools!);
        Assert.Equal("exec", seenRequest.Tools![0].Name);
        Assert.Equal("run debugger command", seenRequest.Tools![0].Description);
        Assert.NotNull(seenRequest.ToolChoice);
        Assert.Equal("auto", seenRequest.ToolChoice!.Mode);

        Assert.Equal(3, seenRequest.Messages.Count);
        Assert.Equal("system", seenRequest.Messages[0].Role);
        Assert.Equal("SYS", seenRequest.Messages[0].Content);
        Assert.Equal("user", seenRequest.Messages[1].Role);
        Assert.Equal("Hello", seenRequest.Messages[1].Content);
        Assert.Equal("assistant", seenRequest.Messages[2].Role);
        Assert.Equal("Hi", seenRequest.Messages[2].Content);

        Assert.Equal("assistant", result.Role);
        Assert.Equal("openrouter/test", result.Model);
        Assert.Equal(2, result.Content.Count);
        Assert.Equal("text", result.Content[0].Type);
        Assert.Equal("ok", result.Content[0].Text);
        Assert.Equal("tool_use", result.Content[1].Type);
        Assert.Equal("tc1", result.Content[1].Id);
        Assert.Equal("exec", result.Content[1].Name);
        Assert.True(result.Content[1].Input.HasValue);
        Assert.Equal("bt", result.Content[1].Input!.Value.GetProperty("command").GetString());
    }
}
