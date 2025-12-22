using System.Text.Json;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

[Collection("NonParallelConsole")]
    public class McpSamplingCreateMessageHandlerTests
    {
        [Fact]
        public async Task HandleAsync_OpenAi_WhenToolResultIsOrphan_DowngradesToUserMessage()
        {
            ChatCompletionRequest? observedRequest = null;
            var settings = new LlmSettings { Provider = "openai", OpenAiModel = "gpt-5.2" };

            var handler = new McpSamplingCreateMessageHandler(
                settings,
                (req, _) =>
                {
                    observedRequest = req;
                    return Task.FromResult(new ChatCompletionResult { Text = "ok" });
                },
                progress: null);

            using var doc = JsonDocument.Parse("""
            {
              "messages": [
                {
                  "role": "user",
                  "content": [
                    {
                      "type": "tool_result",
                      "tool_use_id": "tc1",
                      "content": "{ \"path\": \"analysis.summary\", \"value\": { \"description\": \"hi\" } }"
                    }
                  ]
                }
              ]
            }
            """);

            _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

            Assert.NotNull(observedRequest);
            Assert.NotNull(observedRequest!.Messages);
            Assert.DoesNotContain(observedRequest.Messages, m => string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(observedRequest.Messages, m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                                                         m.Content.Contains("orphan tool output", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task HandleAsync_OpenAi_WhenToolResultMatchesPriorToolCalls_LeavesToolMessageIntact()
        {
            ChatCompletionRequest? observedRequest = null;
            var settings = new LlmSettings { Provider = "openai", OpenAiModel = "gpt-5.2" };

            var handler = new McpSamplingCreateMessageHandler(
                settings,
                (req, _) =>
                {
                    observedRequest = req;
                    return Task.FromResult(new ChatCompletionResult { Text = "ok" });
                },
                progress: null);

            using var doc = JsonDocument.Parse("""
            {
              "messages": [
                {
                  "role": "assistant",
                  "content": [
                    { "type": "tool_use", "id": "tc1", "name": "report_get", "input": { "path": "analysis.summary" } }
                  ]
                },
                {
                  "role": "user",
                  "content": [
                    {
                      "type": "tool_result",
                      "tool_use_id": "tc1",
                      "content": "{ \"path\": \"analysis.summary\", \"value\": { \"description\": \"hi\" } }"
                    }
                  ]
                }
              ]
            }
            """);

            _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

            Assert.NotNull(observedRequest);
            Assert.Contains(observedRequest!.Messages, m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                                                          m.ToolCalls != null &&
                                                          m.ToolCalls.Any(c => string.Equals(c.Id, "tc1", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(observedRequest.Messages, m => string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
                                                         string.Equals(m.ToolCallId, "tc1", StringComparison.OrdinalIgnoreCase));
        }

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
    public async Task HandleAsync_WhenModelReturnsEmptyTextAndNoToolCallsForNoToolsRequest_ReturnsEmptyAssistantMessage()
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

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);
        var text = Assert.Single(result.Content, b => string.Equals(b.Type, "text", StringComparison.OrdinalIgnoreCase));
        Assert.True(string.IsNullOrWhiteSpace(text.Text));
    }

    [Fact]
    public async Task HandleAsync_WhenModelReturnsEmptyTextAndNoToolCallsForToolsRequest_Throws()
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
          ],
          "tools": [
            {
              "name": "exec",
              "description": "d",
              "inputSchema": { "type": "object", "properties": { "command": { "type": "string" } } }
            }
          ],
          "tool_choice": { "type": "auto" }
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
    public async Task HandleAsync_WhenAnthropicAndMessagesContainMcpBlocks_PreservesContentJson()
    {
        var settings = new LlmSettings { Provider = "anthropic", AnthropicModel = "claude-test" };

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
    public async Task HandleAsync_WhenAnthropicResponseHasToolUseBlock_PreservesExtensionFields()
    {
        var settings = new LlmSettings { Provider = "anthropic", AnthropicModel = "claude-test" };

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
    public async Task HandleAsync_WhenMcpToolUseUsesStringInput_ParsesArgumentsAndEmitsToolMessages()
    {
        var settings = new LlmSettings { Provider = "openai", OpenAiModel = "gpt-5.2" };

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
            {
              "role": "assistant",
              "content": [
                { "type": "tool_use", "id": "call_1", "name": "report_get", "input": "{\"path\":\"analysis.exception\",\"maxChars\":12000}" },
                { "type": "tool_use", "id": "call_2", "name": "report_get", "input": "{\"path\":\"analysis.environment\",\"maxChars\":12000}" }
              ]
            },
            {
              "role": "user",
              "content": [
                { "type": "tool_result", "tool_use_id": "call_1", "content": [ "{\\n  \\\\\\\"path\\\\\\\": \\\\\\\"analysis.exception\\\\\\\"\\n}" ] }
              ]
            },
            {
              "role": "user",
              "content": [
                { "type": "tool_result", "tool_use_id": "call_2", "content": [ "{\\n  \\\\\\\"path\\\\\\\": \\\\\\\"analysis.environment\\\\\\\"\\n}" ] }
              ]
            }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Equal(4, seenRequest!.Messages.Count);

        Assert.Equal("user", seenRequest.Messages[0].Role);
        Assert.Equal("Hello", seenRequest.Messages[0].Content);

        Assert.Equal("assistant", seenRequest.Messages[1].Role);
        var toolCalls = seenRequest.Messages[1].ToolCalls;
        Assert.NotNull(toolCalls);
        Assert.Equal(2, toolCalls!.Count);
        Assert.Equal("call_1", toolCalls[0].Id);
        Assert.Equal("report_get", toolCalls[0].Name);
        Assert.Contains("\"path\":\"analysis.exception\"", toolCalls[0].ArgumentsJson);

        var toolMessages = seenRequest.Messages.Where(m => string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(2, toolMessages.Count);
        Assert.Contains(toolMessages, m => m.ToolCallId == "call_1" && m.Content.Contains("analysis.exception", StringComparison.Ordinal));
        Assert.Contains(toolMessages, m => m.ToolCallId == "call_2" && m.Content.Contains("analysis.environment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_WhenToolResultMessageAlsoContainsText_EmitsToolMessagesBeforeUserText()
    {
        var settings = new LlmSettings { Provider = "openai", OpenAiModel = "gpt-5.2" };

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
            {
              "role": "assistant",
              "content": [
                { "type": "tool_use", "id": "call_1", "name": "exec", "input": "{\"command\":\"bt\"}" }
              ]
            },
            {
              "role": "user",
              "content": [
                { "type": "text", "text": "NOTE: tool output follows" },
                { "type": "tool_result", "tool_use_id": "call_1", "content": [ { "type": "text", "text": "OUTPUT" } ] }
              ]
            }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Equal(4, seenRequest!.Messages.Count);

        Assert.Equal("user", seenRequest.Messages[0].Role);
        Assert.Equal("Hello", seenRequest.Messages[0].Content);

        Assert.Equal("assistant", seenRequest.Messages[1].Role);
        Assert.NotNull(seenRequest.Messages[1].ToolCalls);
        Assert.Single(seenRequest.Messages[1].ToolCalls!);
        Assert.Equal("call_1", seenRequest.Messages[1].ToolCalls![0].Id);

        Assert.Equal("tool", seenRequest.Messages[2].Role);
        Assert.Equal("call_1", seenRequest.Messages[2].ToolCallId);
        Assert.Contains("OUTPUT", seenRequest.Messages[2].Content, StringComparison.Ordinal);

        Assert.Equal("user", seenRequest.Messages[3].Role);
        Assert.Contains("NOTE: tool output follows", seenRequest.Messages[3].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_WhenReportGetReturnsTooLarge_ProgressIncludesExampleCalls()
    {
        var settings = new LlmSettings { Provider = "openai", OpenAiModel = "gpt-5.2" };

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
                { "type": "tool_use", "id": "call_1", "name": "report_get", "input": "{\"path\":\"analysis.memory\",\"pageKind\":\"object\",\"limit\":200}" }
              ]
            },
            {
              "role": "user",
              "content": [
                {
                  "type": "tool_result",
                  "tool_use_id": "call_1",
                  "content": [
                    "{\n  \"path\": \"analysis.memory\",\n  \"error\": { \"code\": \"too_large\", \"message\": \"Response exceeds maxChars (20000).\" },\n  \"extra\": {\n    \"estimatedChars\": 123456,\n    \"exampleCalls\": [\n      \"report_get(path=\\\"analysis.memory\\\", pageKind=\\\"object\\\", limit=25, select=[\\\"gc\\\",\\\"topConsumers\\\"])\",\n      \"report_get(path=\\\"analysis.memory.gc\\\")\"\n    ]\n  }\n}"
                  ]
                }
              ]
            }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        var line = Assert.Single(progress, p => p.StartsWith("AI tool result: report_get ->", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("too_large", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Try:", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("report_get(path=\"analysis.memory\"", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WhenToolBlocksOmitType_StillMapsToolCallsAndToolResults()
    {
        var settings = new LlmSettings { Provider = "openai", OpenAiModel = "gpt-5.2" };

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
            {
              "role": "assistant",
              "content": [
                { "id": "call_1", "name": "report_get", "input": "{\"path\":\"analysis.exception\",\"maxChars\":12000}" }
              ]
            },
            {
              "role": "user",
              "content": [
                { "tool_use_id": "call_1", "content": [ "ok" ] }
              ]
            }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);

        var assistant = Assert.Single(seenRequest!.Messages, m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(assistant.ToolCalls);
        var call = Assert.Single(assistant.ToolCalls!);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("report_get", call.Name);

        var tool = Assert.Single(seenRequest.Messages, m => string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("call_1", tool.ToolCallId);
        Assert.Equal("ok", tool.Content);
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

    [Fact]
    public async Task HandleAsync_WhenReasoningEffortOmitted_UsesConfiguredDefault()
    {
        var settings = new LlmSettings
        {
            Provider = "openrouter",
            OpenRouterModel = "openrouter/test",
            OpenRouterReasoningEffort = "medium"
        };

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
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Equal("medium", seenRequest!.ReasoningEffort);
    }

    [Fact]
    public async Task HandleAsync_WhenReasoningEffortInvalid_FallsBackToConfiguredDefault()
    {
        var settings = new LlmSettings
        {
            Provider = "openrouter",
            OpenRouterModel = "openrouter/test",
            OpenRouterReasoningEffort = "medium"
        };

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
          "reasoningEffort": "invalid",
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Equal("medium", seenRequest!.ReasoningEffort);
    }

    [Fact]
    public async Task HandleAsync_WhenReasoningEffortUnset_ClearsEffort()
    {
        var settings = new LlmSettings
        {
            Provider = "openrouter",
            OpenRouterModel = "openrouter/test",
            OpenRouterReasoningEffort = "medium"
        };

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
          "reasoningEffort": "unset",
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Null(seenRequest!.ReasoningEffort);
    }

    [Fact]
    public async Task HandleAsync_WhenReasoningEffortSpecifiedInsideReasoningObject_UsesValue()
    {
        var settings = new LlmSettings { OpenRouterModel = "openrouter/test", OpenRouterReasoningEffort = "medium" };

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
          "reasoning": { "effort": "high" },
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Equal("high", seenRequest!.ReasoningEffort);
    }

    [Fact]
    public async Task HandleAsync_WhenMaxTokensProvidedAsMax_tokens_UsesValue()
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
          "max_tokens": 77,
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Equal(77, seenRequest!.MaxTokens);
    }

    [Fact]
    public async Task HandleAsync_WhenToolChoiceIsString_UsesMode()
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
          "tool_choice": "required",
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.NotNull(seenRequest!.ToolChoice);
        Assert.Equal("required", seenRequest.ToolChoice!.Mode);
    }

    [Fact]
    public async Task HandleAsync_WhenMessageContentIsObject_PreservesAsText()
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
            { "role": "user", "content": { "a": 1, "b": true } }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Contains("\"a\": 1", seenRequest!.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_WhenRawContentHasUnknownBlock_EmitsJsonTextBlock()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        using var rawDoc = JsonDocument.Parse("""
        [
          { "type": "unknown_block", "x": 1 }
        ]
        """);

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _) =>
            {
                return Task.FromResult(new ChatCompletionResult
                {
                    Text = "ok",
                    RawMessageContent = rawDoc.RootElement.Clone(),
                    ToolCalls = []
                });
            });

        using var req = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        var result = await handler.HandleAsync(req.RootElement, CancellationToken.None);
        Assert.Contains(result.Content, b => string.Equals(b.Type, "text", StringComparison.OrdinalIgnoreCase) && b.Text != null && b.Text.Contains("unknown_block", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandleAsync_WhenRawContentEmpty_FallsBackToNormalizedBlocks()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        using var rawDoc = JsonDocument.Parse("[]");

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _) =>
            {
                return Task.FromResult(new ChatCompletionResult
                {
                    Text = "ok",
                    RawMessageContent = rawDoc.RootElement.Clone(),
                    ToolCalls = [new ChatToolCall("tc1", "exec", "{\"command\":\"bt\"}")]
                });
            });

        using var req = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        var result = await handler.HandleAsync(req.RootElement, CancellationToken.None);
        Assert.Contains(result.Content, b => string.Equals(b.Type, "text", StringComparison.OrdinalIgnoreCase) && b.Text == "ok");
        Assert.Contains(result.Content, b => string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase) && b.Name == "exec");
    }

    [Fact]
    public async Task HandleAsync_WhenParametersNull_ThrowsArgumentException()
    {
        var settings = new LlmSettings { OpenRouterModel = "openrouter/test" };
        var handler = new McpSamplingCreateMessageHandler(settings, (_, _) => Task.FromResult(new ChatCompletionResult { Text = "ok" }));

        _ = await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(parameters: null, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WhenModelReturnsEmbeddedToolUseJson_RemovesJsonFromTextAndAddsToolCall()
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
                    before
                    { "type":"tool_use", "id":"tc1", "name":"exec", "input": { "command":"sos clrstack -a" } }
                    after
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

        Assert.Contains(result.Content, b => string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase));
        var text = string.Join("\n", result.Content.Where(b => string.Equals(b.Type, "text", StringComparison.OrdinalIgnoreCase)).Select(b => b.Text));
        Assert.Contains("before", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("after", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"type\":\"tool_use\"", text, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(progress, p => p.Contains("AI requests tool: exec", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandleAsync_WhenMessagesShrink_EmitsToolResultAgain()
    {
        var settings = new LlmSettings { OpenRouterModel = "openrouter/test" };
        var progress = new List<string>();

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _) => Task.FromResult(new ChatCompletionResult { Text = "ok" }),
            progress.Add);

        using var first = JsonDocument.Parse("""
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

        _ = await handler.HandleAsync(first.RootElement, CancellationToken.None);

        using var second = JsonDocument.Parse("""
        {
          "messages": [
            {
              "role": "user",
              "content": [
                { "type": "tool_result", "tool_use_id": "tc1", "content": [ { "type": "text", "text": "OUTPUT" } ] }
              ]
            }
          ]
        }
        """);

        _ = await handler.HandleAsync(second.RootElement, CancellationToken.None);

        Assert.Equal(2, progress.Count(p => p.Contains("AI tool result", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task HandleAsync_WhenToolCallArgsInvalidJson_PreservesRawArguments()
    {
        var settings = new LlmSettings { OpenRouterModel = "openrouter/test" };
        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _) =>
            {
                return Task.FromResult(new ChatCompletionResult
                {
                    Text = "ok",
                    ToolCalls = [new ChatToolCall("tc1", "exec", "{not-json")]
                });
            });

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);
        var toolUse = result.Content.Single(b => string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase));
        Assert.True(toolUse.Input.HasValue);
        Assert.True(toolUse.Input!.Value.TryGetProperty("__raw", out var raw));
        Assert.Contains("not-json", raw.GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_WhenToolCallsHaveDifferentShapes_EmitsUsefulProgressSummaries()
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
                        new ChatToolCall("c1", "exec", "{\"command\":\"bt\"}"),
                        new ChatToolCall("c2", "inspect", "{\"address\":\"0x1234\"}"),
                        new ChatToolCall("c3", "get_thread_stack", "{\"threadId\":\"7\"}"),
                        new ChatToolCall("c4", "analysis_complete", "{\"rootCause\":\"something\"}"),
                        // Duplicate id should be suppressed within one response.
                        new ChatToolCall("c4", "analysis_complete", "{\"rootCause\":\"something\"}")
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

        Assert.Contains(progress, p => p.Contains("exec", StringComparison.OrdinalIgnoreCase) && p.Contains("bt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progress, p => p.Contains("inspect", StringComparison.OrdinalIgnoreCase) && p.Contains("address=0x1234", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progress, p => p.Contains("get_thread_stack", StringComparison.OrdinalIgnoreCase) && p.Contains("threadId=7", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progress, p => p.Contains("analysis_complete", StringComparison.OrdinalIgnoreCase) && p.Contains("rootCause=", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, progress.Count(p => p.Contains("AI requests tool:", StringComparison.OrdinalIgnoreCase)));
    }
}
