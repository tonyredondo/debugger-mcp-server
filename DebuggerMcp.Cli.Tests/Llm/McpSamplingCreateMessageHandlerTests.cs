using System.Text.Json;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

[Collection("NonParallelConsole")]
public class McpSamplingCreateMessageHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenModelRequestsTool_EmitsProgress()
    {
        var settings = new LlmSettings { OpenRouterModel = "openrouter/test" };
        var progress = new List<string>();

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _) =>
            {
                return Task.FromResult(new ChatCompletionResult
                {
                    Text = "ok",
                    ToolCalls =
                    [
                        new ChatToolCall("tc1", "exec", "{\"command\":\"bt\"}")
                    ]
                });
            },
            progress.Add);

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.Contains(progress, p => p.Contains("AI requests tool: exec", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandleAsync_WhenModelReturnsMcpToolUseJsonInText_ConvertsToToolUseBlock()
    {
        var settings = new LlmSettings { OpenRouterModel = "openrouter/test" };
        var progress = new List<string>();

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _) =>
            {
                return Task.FromResult(new ChatCompletionResult
                {
                    Text = """
                    { "type":"tool_use", "id":"tc1", "name":"exec", "input": { "command":"sos dumpobj 0x1234" } }
                    """
                });
            },
            progress.Add);

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(result.Content);
        Assert.Contains(result.Content, b => string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progress, p => p.Contains("AI requests tool: exec", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandleAsync_WhenModelReturnsEmptyTextAndNoToolCalls_Throws()
    {
        var settings = new LlmSettings { OpenRouterModel = "openrouter/test" };

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _) => Task.FromResult(new ChatCompletionResult()),
            progress: null);

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(doc.RootElement, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WhenReceivingToolResult_EmitsProgressOnce()
    {
        var settings = new LlmSettings { OpenRouterModel = "openrouter/test" };
        var progress = new List<string>();

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _) => Task.FromResult(new ChatCompletionResult { Text = "ok" }),
            progress.Add);

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            {
              "role": "assistant",
              "content": [
                { "type": "tool_use", "id": "tc1", "name": "exec", "input": { "command": "!clrstack" } }
              ]
            },
            {
              "role": "user",
              "content": [
                { "type": "tool_result", "tool_use_id": "tc1", "content": [ { "type": "text", "text": "OUTPUT" } ] }
              ]
            }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);
        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.Single(progress, p => p.Contains("AI tool result", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progress, p => p.Contains("exec", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progress, p => p.Contains("OUTPUT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandleAsync_WhenModelReusesToolCallIdAcrossRequests_EmitsProgressEachTime()
    {
        var settings = new LlmSettings { OpenRouterModel = "openrouter/test" };
        var progress = new List<string>();

        var callCount = 0;
        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _) =>
            {
                callCount++;
                var cmd = callCount == 1 ? "sos dumpdomain" : "sos clrstack -a";
                return Task.FromResult(new ChatCompletionResult
                {
                    Text = "ok",
                    ToolCalls =
                    [
                        // Some providers reuse tool-call IDs (e.g., always "call_0") across requests.
                        new ChatToolCall("call_0", "exec", $$"""{"command":"{{cmd}}"}""")
                    ]
                });
            },
            progress.Add);

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);
        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.Equal(2, progress.Count(p => p.Contains("AI requests tool: exec", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(progress, p => p.Contains("dumpdomain", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progress, p => p.Contains("clrstack", StringComparison.OrdinalIgnoreCase));
    }

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

    [Fact]
    public async Task HandleAsync_WhenOpenRouterAndMessagesContainMcpBlocks_PreservesContentJson()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        ChatCompletionRequest? seenRequest = null;
        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (request, _) =>
            {
                seenRequest = request;
                return Task.FromResult(new ChatCompletionResult
                {
                    Text = "ok"
                });
            });

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            {
              "role": "assistant",
              "content": [
                { "type": "tool_use", "id": "tc1", "name": "exec", "thought_signature": "sig1", "input": { "command": "sos dumpdomain" } }
              ]
            },
            {
              "role": "user",
              "content": [
                { "type": "tool_result", "tool_use_id": "tc1", "content": [ { "type": "text", "text": "OUTPUT" } ] }
              ]
            }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Equal(2, seenRequest!.Messages.Count);
        Assert.Equal("assistant", seenRequest.Messages[0].Role);
        Assert.True(seenRequest.Messages[0].ContentJson.HasValue);
        Assert.Equal(JsonValueKind.Array, seenRequest.Messages[0].ContentJson!.Value.ValueKind);
        Assert.Contains("thought_signature", seenRequest.Messages[0].ContentJson!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal("user", seenRequest.Messages[1].Role);
        Assert.True(seenRequest.Messages[1].ContentJson.HasValue);
        Assert.Equal(JsonValueKind.Array, seenRequest.Messages[1].ContentJson!.Value.ValueKind);
        Assert.Contains("tool_result", seenRequest.Messages[1].ContentJson!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WhenOpenRouterResponseHasToolUseBlock_PreservesExtensionFields()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        using var rawDoc = JsonDocument.Parse("""
        [
          { "type":"tool_use", "id":"tc1", "name":"exec", "thought_signature":"sig1", "input": { "command":"bt" } }
        ]
        """);

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _) =>
            {
                return Task.FromResult(new ChatCompletionResult
                {
                    Text = null,
                    RawMessageContent = rawDoc.RootElement.Clone(),
                    ToolCalls = [ new ChatToolCall("tc1", "exec", "{\"command\":\"bt\"}") ]
                });
            });

        using var reqDoc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        var result = await handler.HandleAsync(reqDoc.RootElement, CancellationToken.None);

        Assert.Contains(result.Content, b =>
            string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase) &&
            b.ExtensionData != null &&
            b.ExtensionData.ContainsKey("thought_signature"));
    }

    [Fact]
    public async Task HandleAsync_ParsesOpenAiStyleToolsAndToolChoice()
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
                    Text = "ok"
                });
            });

        using var doc = JsonDocument.Parse("""
        {
          "tools": [
            {
              "type": "function",
              "function": {
                "name": "exec",
                "description": "run debugger command",
                "parameters": { "type":"object","properties":{"command":{"type":"string"}},"required":["command"] }
              }
            }
          ],
          "tool_choice": { "type": "function", "function": { "name": "exec" } },
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.NotNull(seenRequest!.Tools);
        Assert.Single(seenRequest.Tools!);
        Assert.Equal("exec", seenRequest.Tools![0].Name);
        Assert.Equal("run debugger command", seenRequest.Tools![0].Description);
        Assert.Equal("string", seenRequest.Tools![0].Parameters.GetProperty("properties").GetProperty("command").GetProperty("type").GetString());

        Assert.NotNull(seenRequest.ToolChoice);
        Assert.Equal("exec", seenRequest.ToolChoice!.FunctionName);
    }

    [Fact]
    public async Task HandleAsync_IgnoresNonFunctionOpenAiTools()
    {
        var settings = new LlmSettings { OpenRouterModel = "openrouter/test" };

        ChatCompletionRequest? seenRequest = null;
        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (request, _) =>
            {
                seenRequest = request;
                return Task.FromResult(new ChatCompletionResult { Text = "ok" });
            });

        using var doc = JsonDocument.Parse("""
        {
          "tools": [
            {
              "type": "web_search",
              "function": {
                "name": "exec",
                "description": "run debugger command",
                "parameters": { "type":"object","properties":{"command":{"type":"string"}} }
              }
            }
          ],
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Null(seenRequest!.Tools);
    }

    [Fact]
    public async Task HandleAsync_IgnoresToolsWithNonFunctionTypeEvenIfNamePresent()
    {
        var settings = new LlmSettings { OpenRouterModel = "openrouter/test" };

        ChatCompletionRequest? seenRequest = null;
        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (request, _) =>
            {
                seenRequest = request;
                return Task.FromResult(new ChatCompletionResult { Text = "ok" });
            });

        using var doc = JsonDocument.Parse("""
        {
          "tools": [
            {
              "type": "web_search",
              "name": "exec",
              "description": "run debugger command",
              "inputSchema": { "type":"object","properties":{"command":{"type":"string"}} }
            }
          ],
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Null(seenRequest!.Tools);
    }

    [Fact]
    public async Task HandleAsync_ParsesOpenAiStyleToolCallsAndToolResults()
    {
        var settings = new LlmSettings { OpenRouterModel = "openrouter/test" };

        ChatCompletionRequest? seenRequest = null;
        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (request, _) =>
            {
                seenRequest = request;
                return Task.FromResult(new ChatCompletionResult { Text = "ok" });
            });

        using var doc = JsonDocument.Parse("""
        {
          "tools": [
            {
              "type": "function",
              "function": {
                "name": "exec",
                "description": "run debugger command",
                "parameters": { "type":"object","properties":{"command":{"type":"string"}},"required":["command"] }
              }
            }
          ],
          "messages": [
            { "role": "user", "content": "Hello" },
            {
              "role": "assistant",
              "content": null,
              "tool_calls": [
                {
                  "id": "tc1",
                  "type": "function",
                  "function": { "name": "exec", "arguments": "{\"command\":\"bt\"}" }
                }
              ]
            },
            { "role": "tool", "tool_call_id": "tc1", "content": "ok" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Equal(3, seenRequest!.Messages.Count);

        Assert.Equal("user", seenRequest.Messages[0].Role);
        Assert.Equal("Hello", seenRequest.Messages[0].Content);

        Assert.Equal("assistant", seenRequest.Messages[1].Role);
        Assert.NotNull(seenRequest.Messages[1].ToolCalls);
        Assert.Single(seenRequest.Messages[1].ToolCalls!);
        Assert.Equal("tc1", seenRequest.Messages[1].ToolCalls![0].Id);
        Assert.Equal("exec", seenRequest.Messages[1].ToolCalls![0].Name);
        Assert.Contains("\"command\":\"bt\"", seenRequest.Messages[1].ToolCalls![0].ArgumentsJson);

        Assert.Equal("tool", seenRequest.Messages[2].Role);
        Assert.Equal("tc1", seenRequest.Messages[2].ToolCallId);
        Assert.Equal("ok", seenRequest.Messages[2].Content);
    }

    [Fact]
    public async Task HandleAsync_ToolMessageWithoutToolCallId_PreservesAsUserMessage()
    {
        var settings = new LlmSettings { OpenRouterModel = "openrouter/test" };

        ChatCompletionRequest? seenRequest = null;
        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (request, _) =>
            {
                seenRequest = request;
                return Task.FromResult(new ChatCompletionResult { Text = "ok" });
            });

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" },
            { "role": "tool", "content": "tool output without id" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Equal(2, seenRequest!.Messages.Count);
        Assert.Equal("user", seenRequest.Messages[0].Role);
        Assert.Equal("Hello", seenRequest.Messages[0].Content);

        Assert.Equal("user", seenRequest.Messages[1].Role);
        Assert.Contains("missing tool_call_id", seenRequest.Messages[1].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tool output without id", seenRequest.Messages[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_WhenRequestIncludesReasoningEffort_PropagatesToCompletionRequest()
    {
        var settings = new LlmSettings { OpenRouterModel = "openrouter/test" };

        ChatCompletionRequest? seenRequest = null;
        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (request, _) =>
            {
                seenRequest = request;
                return Task.FromResult(new ChatCompletionResult { Text = "ok" });
            });

        using var doc = JsonDocument.Parse("""
        {
          "reasoningEffort": "high",
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Equal("high", seenRequest!.ReasoningEffort);
    }
}
