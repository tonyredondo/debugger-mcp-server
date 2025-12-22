using System.Text.Json;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

[Collection("NonParallelConsole")]
public class McpSamplingCreateMessageHandlerEdgeCaseTests
{
    [Fact]
    public async Task HandleAsync_WhenToolUseJsonEmbeddedInText_ExtractsToolCallAndCleansText()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) =>
            {
                return Task.FromResult(new ChatCompletionResult
                {
                    Text = """
                    Prefix text.
                    { "type":"tool_use", "id":"tc1", "name":"exec", "input": { "command":"bt" } }
                    Suffix text.
                    """
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

        var textBlock = Assert.Single(result.Content, b => string.Equals(b.Type, "text", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Prefix", textBlock.Text, StringComparison.Ordinal);
        Assert.Contains("Suffix", textBlock.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("tool_use", textBlock.Text, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(result.Content, b => string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase) && b.Id == "tc1");
    }

    [Fact]
    public async Task HandleAsync_WhenToolUseJsonMissingId_GeneratesId()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) => Task.FromResult(new ChatCompletionResult
            {
                Text = """{ "type":"tool_use", "name":"exec", "input": { "command":"bt" } }"""
            }));

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        var tool = Assert.Single(result.Content, b => string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("text_tool_", tool.Id, StringComparison.Ordinal);
        Assert.Equal(42, tool.Id?.Length);
        Assert.Equal("exec", tool.Name);
    }

    [Fact]
    public async Task HandleAsync_WhenToolUseJsonMissingName_DoesNotCreateToolCall()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) => Task.FromResult(new ChatCompletionResult
            {
                Text = """{ "type":"tool_use", "id":"tc1", "input": { "command":"bt" } }"""
            }));

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.DoesNotContain(result.Content, b => string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase));
        var text = Assert.Single(result.Content, b => string.Equals(b.Type, "text", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"type\":\"tool_use\"", text.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WhenToolUseWrappedInTextBlock_Unwraps()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) => Task.FromResult(new ChatCompletionResult
            {
                Text = """
                {
                  "type": "text",
                  "text": "{ \"type\":\"tool_use\", \"id\":\"tc1\", \"name\":\"exec\", \"input\": { \"command\":\"bt\" } }"
                }
                """
            }));

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.Contains(result.Content, b => string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase) && b.Id == "tc1");
        Assert.DoesNotContain(result.Content, b => string.Equals(b.Type, "text", StringComparison.OrdinalIgnoreCase) && b.Text?.Contains("\"type\":\"text\"", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task HandleAsync_WhenToolUseJsonArray_ExtractsAllToolCalls()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) => Task.FromResult(new ChatCompletionResult
            {
                Text = """
                [
                  { "type":"tool_use", "id":"tc1", "name":"exec", "input": { "command":"bt" } },
                  { "type":"tool_use", "id":"tc2", "name":"inspect", "input": { "address":"0x1234" } }
                ]
                """
            }));

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.Equal(2, result.Content.Count(b => string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(result.Content, b => b.Id == "tc1" && b.Name == "exec");
        Assert.Contains(result.Content, b => b.Id == "tc2" && b.Name == "inspect");
        Assert.DoesNotContain(result.Content, b => string.Equals(b.Type, "text", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandleAsync_WhenTextContainsToolUseMarkerButNoJson_LeavesTextUntouched()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) => Task.FromResult(new ChatCompletionResult
            {
                Text = "I see tool_use mentioned but there is no JSON."
            }));

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.DoesNotContain(result.Content, b => string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase));
        var text = Assert.Single(result.Content, b => string.Equals(b.Type, "text", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("tool_use mentioned", text.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WhenEmbeddedToolUseJsonIsUnbalanced_LeavesTextUntouched()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) => Task.FromResult(new ChatCompletionResult
            {
                Text = """
                prefix { "type":"tool_use", "id":"tc1", "name":"exec", "input": { "command":"bt" }
                """
            }));

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.DoesNotContain(result.Content, b => string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Content, b => string.Equals(b.Type, "text", StringComparison.OrdinalIgnoreCase) &&
                                            b.Text?.Contains("tool_use", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task HandleAsync_WhenEmbeddedToolUseJsonContainsEscapedQuotesAndBraces_ParsesCorrectly()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) => Task.FromResult(new ChatCompletionResult
            {
                Text = """
                prefix {"type":"tool_use","id":"tc1","name":"exec","input":{"command":"echo \"tool_use\" { }"}} suffix
                """
            }));

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        var tool = Assert.Single(result.Content, b => string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("tc1", tool.Id);
        Assert.Equal("exec", tool.Name);
        Assert.True(tool.Input.HasValue);
        Assert.Contains("echo", tool.Input!.Value.GetProperty("command").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_WhenToolResultReferencesOpenAiToolCalls_UsesToolNameInProgress()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };
        var progress = new List<string>();

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) => Task.FromResult(new ChatCompletionResult { Text = "ok" }),
            progress.Add);

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            {
              "role": "assistant",
              "content": null,
              "tool_calls": [
                { "id":"tc1", "type":"function", "function": { "name":"exec", "arguments":"{\"command\":\"bt\"}" } }
              ]
            },
            {
              "role": "user",
              "content": [
                { "type": "tool_result", "toolCallId": "tc1", "content": [ { "type": "text", "text": "OUTPUT" } ] }
              ]
            }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.Contains(progress, p => p.Contains("AI tool result", StringComparison.OrdinalIgnoreCase) && p.Contains("exec", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progress, p => p.Contains("OUTPUT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandleAsync_WhenToolResultIsSingleObjectContent_EmitsOpenAiToolRoleMessage()
    {
        var settings = new LlmSettings { Provider = "openai", OpenAiModel = "gpt-4o-mini" };
        ChatCompletionRequest? captured = null;

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (request, _, _) =>
            {
                captured = request;
                return Task.FromResult(new ChatCompletionResult { Text = "ok" });
            });

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            {
              "role": "assistant",
              "content": null,
              "tool_calls": [
                { "id":"tc1", "type":"function", "function": { "name":"exec", "arguments":"{\"command\":\"bt\"}" } }
              ]
            },
            {
              "role": "user",
              "content":
                { "type": "tool_result", "tool_use_id": "tc1", "content": [ { "type": "text", "text": "OUTPUT" } ] }
            }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains(captured!.Messages, m => m.Role == "assistant" && m.ToolCalls is { Count: > 0 });
        Assert.Contains(captured.Messages, m => m.Role == "tool" && m.ToolCallId == "tc1" && m.Content.Contains("OUTPUT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_WhenConversationShrinks_EmitsProgressAgain()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };
        var progress = new List<string>();

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) => Task.FromResult(new ChatCompletionResult { Text = "ok" }),
            progress.Add);

        using var doc1 = JsonDocument.Parse("""
        {
          "messages": [
            {
              "role": "assistant",
              "content": [
                { "type": "tool_use", "id": "tc1", "name": "exec", "input": { "command": "bt" } }
              ]
            },
            {
              "role": "user",
              "content": [
                { "type": "tool_result", "tool_use_id": "tc1", "content": [ { "type": "text", "text": "OUT1" } ] }
              ]
            }
          ]
        }
        """);

        using var doc2 = JsonDocument.Parse("""
        {
          "messages": [
            {
              "role": "user",
              "content": [
                { "type": "tool_result", "tool_use_id": "tc1", "content": [ { "type": "text", "text": "OUT1" } ] }
              ]
            }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc1.RootElement, CancellationToken.None);
        _ = await handler.HandleAsync(doc2.RootElement, CancellationToken.None);

        Assert.Equal(2, progress.Count(p => p.Contains("AI tool result", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task HandleAsync_WhenToolCallsContainDuplicateIds_DeduplicatesProgress()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };
        var progress = new List<string>();

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) => Task.FromResult(new ChatCompletionResult
            {
                Text = "ok",
                ToolCalls =
                [
                    new ChatToolCall("tc1", "exec", "{\"command\":\"bt\"}"),
                    new ChatToolCall("tc1", "exec", "{\"command\":\"k\"}")
                ]
            }),
            progress.Add);

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.Equal(1, progress.Count(p => p.Contains("AI requests tool: exec", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("inspect", "{\"address\":\"0x1234\"}", "address=0x1234")]
    [InlineData("get_thread_stack", "{\"threadId\":\"1\"}", "threadId=1")]
    [InlineData("analysis_complete", "{\"rootCause\":\"boom\"}", "rootCause=boom")]
    public async Task HandleAsync_WhenKnownToolsRequested_ProgressIncludesSummary(string toolName, string argsJson, string expectedSummary)
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };
        var progress = new List<string>();

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) => Task.FromResult(new ChatCompletionResult
            {
                Text = "ok",
                ToolCalls = [new ChatToolCall("tc1", toolName, argsJson)]
            }),
            progress.Add);

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.Contains(progress, p => p.Contains(expectedSummary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_WhenToolCallArgsInvalidJson_EmitsProgressWithoutSummaryAndPreservesRawInput()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };
        var progress = new List<string>();

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) => Task.FromResult(new ChatCompletionResult
            {
                Text = "ok",
                ToolCalls = [new ChatToolCall("tc1", "exec", "{not-json")]
            }),
            progress.Add);

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.Contains(progress, p => p.Contains("AI requests tool: exec", StringComparison.OrdinalIgnoreCase) && !p.Contains("(", StringComparison.Ordinal));

        var tool = Assert.Single(result.Content, b => string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase));
        Assert.True(tool.Input.HasValue);
        Assert.Equal("{not-json", tool.Input!.Value.GetProperty("__raw").GetString());
    }

    [Fact]
    public async Task HandleAsync_WhenReasoningEffortHasInvalidType_FallsBackToSettings()
    {
        var settings = new LlmSettings
        {
            Provider = "openrouter",
            OpenRouterModel = "openrouter/test",
            OpenRouterReasoningEffort = "low"
        };

        ChatCompletionRequest? seenRequest = null;
        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (request, _, _) =>
            {
                seenRequest = request;
                return Task.FromResult(new ChatCompletionResult { Text = "ok" });
            });

        using var doc = JsonDocument.Parse("""
        {
          "reasoningEffort": 123,
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Equal("low", seenRequest!.ReasoningEffort);
    }

    [Fact]
    public async Task HandleAsync_WhenToolUseIsJsonStringValue_UnwrapsToolUse()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        var inner = """{ "type":"tool_use", "id":"tc1", "name":"exec", "input": { "command":"bt" } }""";
        var jsonStringValue = JsonSerializer.Serialize(inner);

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) => Task.FromResult(new ChatCompletionResult { Text = jsonStringValue }));

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.Contains(result.Content, b => string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase) && b.Id == "tc1");
        Assert.DoesNotContain(result.Content, b => string.Equals(b.Type, "text", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandleAsync_WhenRawContentOmitsToolUseBlock_AddsMissingToolUseAndInsertsText()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        using var rawDoc = JsonDocument.Parse("""
        [
          { "type":"tool_use", "id":"tc1", "name":"exec", "input": { "command":"bt" } }
        ]
        """);

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) =>
            {
                return Task.FromResult(new ChatCompletionResult
                {
                    Text = "missing text block",
                    RawMessageContent = rawDoc.RootElement.Clone(),
                    ToolCalls =
                    [
                        new ChatToolCall("tc2", "inspect", "{\"address\":\"0x1234\"}")
                    ]
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

        Assert.Equal("text", result.Content[0].Type);
        Assert.Equal("missing text block", result.Content[0].Text);

        Assert.Contains(result.Content, b => string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase) && b.Id == "tc1");
        Assert.Contains(result.Content, b =>
            string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase) &&
            b.Id == "tc2" &&
            b.Name == "inspect" &&
            b.Input.HasValue &&
            b.Input.Value.GetProperty("address").GetString() == "0x1234");
    }

    [Fact]
    public async Task HandleAsync_WhenRawContentHasTextObjectAndString_PreservesExtensionData()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        using var rawDoc = JsonDocument.Parse("""
        [
          "STR",
          { "type":"text", "text":"OBJ", "extra":"x" },
          { "type":"unknown_block", "x": 1 },
          123
        ]
        """);

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) => Task.FromResult(new ChatCompletionResult
            {
                Text = "ignored",
                RawMessageContent = rawDoc.RootElement.Clone(),
                ToolCalls = []
            }));

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.Contains(result.Content, b => b.Type == "text" && b.Text == "STR");

        var objText = Assert.Single(result.Content, b => b.Type == "text" && b.Text == "OBJ");
        Assert.NotNull(objText.ExtensionData);
        Assert.True(objText.ExtensionData!.ContainsKey("extra"));

        Assert.Contains(result.Content, b => b.Type == "text" && b.Text?.Contains("unknown_block", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task HandleAsync_WhenRawContentArrayHasNoUsableBlocks_FallsBackToNormalized()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        using var rawDoc = JsonDocument.Parse("""[ null, 1 ]""");

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) => Task.FromResult(new ChatCompletionResult
            {
                Text = "ok",
                RawMessageContent = rawDoc.RootElement.Clone(),
                ToolCalls = []
            }));

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        var text = Assert.Single(result.Content, b => string.Equals(b.Type, "text", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ok", text.Text);
    }

    [Fact]
    public async Task HandleAsync_WhenToolResultHasNoOutput_ProgressUsesNoOutputMarker()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };
        var progress = new List<string>();

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (_, _, _) => Task.FromResult(new ChatCompletionResult { Text = "ok" }),
            progress.Add);

        using var doc = JsonDocument.Parse("""
        {
          "messages": [
            "not-an-object",
            {
              "role": "assistant",
              "content": [
                { "type": "tool_use", "id": "tc1", "name": "exec", "input": { "command": "bt" } }
              ]
            },
            {
              "role": "user",
              "content": [
                { "type": "tool_result", "tool_use_id": "tc1", "content": "" }
              ]
            }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.Contains(progress, p =>
            p.Contains("AI tool result", StringComparison.OrdinalIgnoreCase) &&
            p.Contains("exec", StringComparison.OrdinalIgnoreCase) &&
            p.Contains("(no output)", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandleAsync_WhenRequestContainsMalformedToolsAndChoices_IgnoresSafely()
    {
        var settings = new LlmSettings { Provider = "openrouter", OpenRouterModel = "openrouter/test" };

        ChatCompletionRequest? seenRequest = null;
        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (request, _, _) =>
            {
                seenRequest = request;
                return Task.FromResult(new ChatCompletionResult { Text = "ok" });
            });

        using var doc = JsonDocument.Parse("""
        {
          "maxTokens": 0,
          "toolChoice": 123,
          "tools": [
            "bad",
            { "type": "web_search", "name": "exec", "description": "ignored" },
            { "type": "function", "function": { "description": "missing name", "arguments": {} } }
          ],
          "messages": [
            { "role": "assistant", "content": null, "tool_calls": {} },
            { "role": "user", "content": [ 123, { "type": "unknown", "x": 1 } ] },
            { "role": "user" },
            123
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(seenRequest);
        Assert.Null(seenRequest!.MaxTokens);
        Assert.Null(seenRequest.ToolChoice);
        Assert.Null(seenRequest.Tools);
        Assert.NotEmpty(seenRequest.Messages);
        Assert.Contains(seenRequest.Messages, m => m.Role == "user" && m.Content.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }
}
