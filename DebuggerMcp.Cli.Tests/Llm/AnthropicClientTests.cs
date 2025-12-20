using System.Net;
using System.Text;
using System.Text.Json;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

[Collection("NonParallelConsole")]
public class AnthropicClientTests
{
    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }

    [Fact]
    public async Task ChatAsync_SendsAnthropicRequestAndParsesResponse()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicReasoningEffort = "medium",
            AnthropicBaseUrl = "https://api.anthropic.com/v1",
            TimeoutSeconds = 10
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """
            {
              "id":"msg_1",
              "type":"message",
              "role":"assistant",
              "model":"claude-test",
              "content":[{"type":"text","text":"hello"}]
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        var result = await client.ChatAsync(
            [
                new ChatMessage("system", "SYS"),
                new ChatMessage("user", "hi")
            ]);

        Assert.Equal("hello", result);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.LastRequest!.RequestUri!.ToString());

        Assert.True(handler.LastRequest.Headers.TryGetValues("x-api-key", out var keys));
        Assert.Contains("k", keys);
        Assert.True(handler.LastRequest.Headers.TryGetValues("anthropic-version", out var versions));
        Assert.Contains("2023-06-01", versions);

        var requestBody = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(requestBody);
        Assert.Equal("claude-test", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(4096, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("SYS", doc.RootElement.GetProperty("system").GetString());

        var messages = doc.RootElement.GetProperty("messages");
        Assert.Single(messages.EnumerateArray());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("hi", messages[0].GetProperty("content").GetString());

        Assert.True(doc.RootElement.TryGetProperty("thinking", out var thinking));
        Assert.Equal("enabled", thinking.GetProperty("type").GetString());
        Assert.Equal(1024, thinking.GetProperty("budget_tokens").GetInt32());
    }

    [Fact]
    public async Task ChatCompletionAsync_SendsToolsAndParsesToolCalls()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1",
            TimeoutSeconds = 10
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """
            {
              "model":"claude-test",
              "content":[
                {"type":"tool_use","id":"tc1","name":"exec","input":{"command":"bt"}}
              ]
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        using var schemaDoc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"}}}");
        var result = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage("user", "hi")],
            Tools = [new ChatTool { Name = "exec", Description = "d", Parameters = schemaDoc.RootElement.Clone() }],
            ToolChoice = new ChatToolChoice { Mode = "auto" },
            MaxTokens = 50
        });

        Assert.Null(result.Text);
        Assert.Single(result.ToolCalls);
        Assert.Equal("tc1", result.ToolCalls[0].Id);
        Assert.Equal("exec", result.ToolCalls[0].Name);
        Assert.Contains("bt", result.ToolCalls[0].ArgumentsJson, StringComparison.Ordinal);
        Assert.True(result.RawMessageContent.HasValue);

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(50, reqDoc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.True(reqDoc.RootElement.TryGetProperty("tools", out var tools));
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("exec", tools[0].GetProperty("name").GetString());
        Assert.True(tools[0].TryGetProperty("input_schema", out _));
        Assert.Equal("auto", reqDoc.RootElement.GetProperty("tool_choice").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenToolRoleMessagesPresent_SendsToolResultBlocks()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1",
            TimeoutSeconds = 10
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """
            {
              "model":"claude-test",
              "content":[{"type":"text","text":"ok"}]
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        var toolCalls = new List<ChatToolCall> { new("tc1", "exec", "{\"command\":\"bt\"}") };
        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage("user", "hi"),
                new ChatMessage("assistant", "", toolCallId: null, toolCalls: toolCalls),
                new ChatMessage("tool", "OUTPUT", toolCallId: "tc1", toolCalls: null)
            ],
            MaxTokens = 64
        });

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = reqDoc.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Array, messages[1].GetProperty("content").ValueKind);
        Assert.Contains("tool_use", messages[1].GetProperty("content")[0].GetRawText(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Array, messages[2].GetProperty("content").ValueKind);
        Assert.Equal("tool_result", messages[2].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("tc1", messages[2].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("OUTPUT", messages[2].GetProperty("content")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ChatAsync_Error_TruncatesAndRedactsBody()
    {
        var secret = "sk-123";
        var body = "apiKey=" + secret + "\n" + new string('x', 100_000);

        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1",
            TimeoutSeconds = 10
        };

        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync([new ChatMessage("user", "hi")]));
        Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);
        Assert.Contains("apiKey=***", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(ex.Message.Length < 50_000);
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenApiKeyMissing_Throws()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = null,
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        using var http = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new AnthropicClient(http, settings);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage("user", "hi")]
        }));
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenBaseUrlMissing_Throws()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = ""
        };

        using var http = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new AnthropicClient(http, settings);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage("user", "hi")]
        }));
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenToolChoiceNone_DisablesToolsAndToolChoiceFields()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """{ "model":"claude-test", "content":[{"type":"text","text":"ok"}] }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        using var schemaDoc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"}}}");
        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage("user", "hi")],
            Tools = [new ChatTool { Name = "exec", Description = "d", Parameters = schemaDoc.RootElement.Clone() }],
            ToolChoice = new ChatToolChoice { Mode = "none" },
            MaxTokens = 32
        });

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(reqDoc.RootElement.TryGetProperty("tools", out _));
        Assert.False(reqDoc.RootElement.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenToolChoiceFunctionName_SendsToolChoiceTool()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """{ "model":"claude-test", "content":[{"type":"text","text":"ok"}] }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        using var schemaDoc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"}}}");
        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage("user", "hi")],
            Tools = [new ChatTool { Name = "exec", Description = "d", Parameters = schemaDoc.RootElement.Clone() }],
            ToolChoice = new ChatToolChoice { Mode = "function", FunctionName = "exec" },
            MaxTokens = 32
        });

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("tool", reqDoc.RootElement.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal("exec", reqDoc.RootElement.GetProperty("tool_choice").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenThinkingBudgetExceedsMaxTokens_ClampsBudget()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """{ "model":"claude-test", "content":[{"type":"text","text":"ok"}] }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage("user", "hi")],
            MaxTokens = 10,
            ReasoningEffort = "high"
        });

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.True(reqDoc.RootElement.TryGetProperty("thinking", out var thinking));
        Assert.Equal(9, thinking.GetProperty("budget_tokens").GetInt32());
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenResponseHasNoContent_ReturnsEmptyResult()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """{ "model":"claude-test" }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        var result = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage("user", "hi")]
        });

        Assert.Null(result.Text);
        Assert.Empty(result.ToolCalls);
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenResponseJsonInvalid_Throws()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-json", Encoding.UTF8, "application/json")
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage("user", "hi")]
        }));
    }

    [Fact]
    public async Task ChatAsync_WhenResponseHasWhitespaceOnlyText_Throws()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """{ "model":"claude-test", "content":[{"type":"text","text":"   "}] }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync([new ChatMessage("user", "hi")]));
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenAssistantHasExistingToolUseBlocks_MergesAdditionalToolCalls()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """{ "model":"claude-test", "content":[{"type":"text","text":"ok"}] }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        using var contentJsonDoc = JsonDocument.Parse("""[{ "type":"tool_use", "id":"t1", "name":"exec", "input": { "command":"bt" } }]""");
        var toolCalls = new List<ChatToolCall> { new("t2", "exec", "{\"command\":\"clrstack\"}") };

        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage("user", "hi"),
                new ChatMessage("assistant", "x", toolCallId: null, toolCalls: toolCalls, contentJson: contentJsonDoc.RootElement.Clone())
            ],
            MaxTokens = 64
        });

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = reqDoc.RootElement.GetProperty("messages");
        var assistant = messages[1].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, assistant.ValueKind);
        Assert.Equal(2, assistant.GetArrayLength());
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenToolMessageMissingToolCallId_SendsTextBlock()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """{ "model":"claude-test", "content":[{"type":"text","text":"ok"}] }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage("user", "hi"),
                new ChatMessage("tool", "tool output", toolCallId: null, toolCalls: null)
            ],
            MaxTokens = 64
        });

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = reqDoc.RootElement.GetProperty("messages");
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("text", messages[1].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("tool output", messages[1].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenResponseContentNotArray_DoesNotReportToolCalls()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """{ "model":"claude-test", "content": { "x": 1 } }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        var result = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage("user", "hi")]
        });

        Assert.Empty(result.ToolCalls);
        Assert.NotNull(result.Text);
        using var contentDoc = JsonDocument.Parse(result.Text);
        Assert.Equal(1, contentDoc.RootElement.GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenResponseContentContainsNonObjects_IgnoresThem()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """
            {
              "model":"claude-test",
              "content":[
                "noise",
                5,
                {"type":"tool_use","id":"tc1","name":"exec","input":{"command":"bt"}}
              ]
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        var result = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage("user", "hi")]
        });

        var toolCall = Assert.Single(result.ToolCalls);
        Assert.Equal("tc1", toolCall.Id);
        Assert.Equal("exec", toolCall.Name);
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenRoleUnknown_DefaultsToUser_AndSkipsEmptyMessages()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """{ "model":"claude-test", "content":[{"type":"text","text":"ok"}] }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage("user", "hi"),
                new ChatMessage("CUSTOM_ROLE", "hello"),
                new ChatMessage("user", "   ")
            ],
            MaxTokens = 64
        });

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = reqDoc.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("hello", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenUserContentJsonArray_PreservesContentBlocks()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """{ "model":"claude-test", "content":[{"type":"text","text":"ok"}] }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        using var contentJsonDoc = JsonDocument.Parse("""[{ "type":"text", "text":"hello" }]""");
        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage("user", "", toolCallId: null, toolCalls: null, contentJson: contentJsonDoc.RootElement.Clone())
            ],
            MaxTokens = 64
        });

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = reqDoc.RootElement.GetProperty("messages");
        Assert.Equal(JsonValueKind.Array, messages[0].GetProperty("content").ValueKind);
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenUserContentJsonString_UsesThatString()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """{ "model":"claude-test", "content":[{"type":"text","text":"ok"}] }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        using var contentJsonDoc = JsonDocument.Parse("  \"hi\"  ");
        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage("user", "", toolCallId: null, toolCalls: null, contentJson: contentJsonDoc.RootElement.Clone())
            ],
            MaxTokens = 64
        });

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = reqDoc.RootElement.GetProperty("messages");
        Assert.Equal("hi", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenUserContentJsonObject_PreservesAsJsonText()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """{ "model":"claude-test", "content":[{"type":"text","text":"ok"}] }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        using var contentJsonDoc = JsonDocument.Parse("""{ "x": 1 }""");
        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage("user", "ignored", toolCallId: null, toolCalls: null, contentJson: contentJsonDoc.RootElement.Clone())
            ],
            MaxTokens = 64
        });

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = reqDoc.RootElement.GetProperty("messages");
        var content = messages[0].GetProperty("content").GetString();
        Assert.False(string.IsNullOrWhiteSpace(content));
        using var contentDoc = JsonDocument.Parse(content);
        Assert.Equal(1, contentDoc.RootElement.GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenAssistantHasToolCallsWithInvalidArguments_UsesFallbacksAndAddsTextBlock()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """{ "model":"claude-test", "content":[{"type":"text","text":"ok"}] }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        var toolCalls = new List<ChatToolCall>
        {
            new("", "exec", "{\"command\":\"bt\"}"),
            new("t1", "", "{\"command\":\"bt\"}"),
            new("t2", "exec", ""),
            new("t3", "exec", "not-json")
        };

        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage("user", "hi"),
                new ChatMessage("assistant", "assistant says", toolCallId: null, toolCalls: toolCalls)
            ],
            MaxTokens = 64
        });

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = reqDoc.RootElement.GetProperty("messages");
        var assistantContent = messages[1].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, assistantContent.ValueKind);
        Assert.Equal("text", assistantContent[0].GetProperty("type").GetString());
        Assert.Equal("assistant says", assistantContent[0].GetProperty("text").GetString());

        var toolUseBlocks = assistantContent.EnumerateArray()
            .Where(b => b.ValueKind == JsonValueKind.Object && b.TryGetProperty("type", out var t) && t.GetString() == "tool_use")
            .ToList();

        Assert.Equal(4, toolUseBlocks.Count);

        var t2 = Assert.Single(toolUseBlocks, b => b.GetProperty("id").GetString() == "t2");
        Assert.Equal(JsonValueKind.Object, t2.GetProperty("input").ValueKind);
        Assert.Empty(t2.GetProperty("input").EnumerateObject());

        var t3 = Assert.Single(toolUseBlocks, b => b.GetProperty("id").GetString() == "t3");
        Assert.Equal("not-json", t3.GetProperty("input").GetProperty("raw").GetString());
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenAssistantHasExistingContentBlocks_SkipsDuplicateToolUseIdsAndKeepsNonObjectItems()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """{ "model":"claude-test", "content":[{"type":"text","text":"ok"}] }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        using var contentJsonDoc = JsonDocument.Parse("""
        [
          5,
          { "type":"text", "text":"existing" },
          { "type":"tool_use", "id":"t1", "name":"exec", "input": { "command":"bt" } }
        ]
        """);

        var toolCalls = new List<ChatToolCall>
        {
            new("t1", "exec", "{\"command\":\"bt\"}"),
            new("t2", "exec", "not-json"),
            new("", "exec", "{\"command\":\"bt\"}")
        };

        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage("user", "hi"),
                new ChatMessage("assistant", "x", toolCallId: null, toolCalls: toolCalls, contentJson: contentJsonDoc.RootElement.Clone())
            ],
            MaxTokens = 64
        });

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = reqDoc.RootElement.GetProperty("messages");
        var assistantContent = messages[1].GetProperty("content");

        Assert.Equal(JsonValueKind.Array, assistantContent.ValueKind);
        Assert.Contains(assistantContent.EnumerateArray(), v => v.ValueKind == JsonValueKind.Number);
        Assert.Contains(assistantContent.EnumerateArray(), v =>
            v.ValueKind == JsonValueKind.Object &&
            v.TryGetProperty("type", out var t) &&
            string.Equals(t.GetString(), "tool_use", StringComparison.OrdinalIgnoreCase) &&
            v.TryGetProperty("id", out var id) &&
            id.GetString() == "t2");

        Assert.Equal(1, assistantContent.EnumerateArray().Count(v =>
            v.ValueKind == JsonValueKind.Object &&
            v.TryGetProperty("type", out var t) &&
            string.Equals(t.GetString(), "tool_use", StringComparison.OrdinalIgnoreCase) &&
            v.TryGetProperty("id", out var id) &&
            id.GetString() == "t1"));
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenToolChoiceRequired_SendsAnyToolChoice()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1"
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """{ "model":"claude-test", "content":[{"type":"text","text":"ok"}] }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        using var schemaDoc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"}}}");
        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage("user", "hi")],
            Tools = [new ChatTool { Name = "exec", Description = "d", Parameters = schemaDoc.RootElement.Clone() }],
            ToolChoice = new ChatToolChoice { Mode = "required" },
            MaxTokens = 64
        });

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("any", reqDoc.RootElement.GetProperty("tool_choice").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ChatAsync_ErrorBodyWithInvalidUtf8_DoesNotThrowDecoderExceptions()
    {
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = "k",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://api.anthropic.com/v1",
            TimeoutSeconds = 10
        };

        var invalidBytes = new byte[] { 0xFF, 0xFE, 0xFD, 0xFC, 0xFB, (byte)'a', (byte)'b', (byte)'c' };
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new ByteArrayContent(invalidBytes)
        });

        using var http = new HttpClient(handler);
        var client = new AnthropicClient(http, settings);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync([new ChatMessage("user", "hi")]));
        Assert.Contains("Anthropic request failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
