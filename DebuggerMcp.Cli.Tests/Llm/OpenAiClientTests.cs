using System.Net;
using System.Text;
using System.Text.Json;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

[Collection("NonParallelConsole")]
public class OpenAiClientTests
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

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("HTTP handler should not be called.");
    }

    private sealed class SequenceHandler(IReadOnlyList<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private int _index;

        public List<string?> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken));

            var i = _index++;
            if (i >= responses.Count)
            {
                throw new InvalidOperationException($"No configured response at index {i}.");
            }

            return responses[i];
        }
    }

    [Fact]
    public async Task ChatAsync_SendsOpenAiRequestAndParsesResponse()
    {
        var settings = new LlmSettings
        {
            Provider = "openai",
            OpenAiApiKey = "k",
            OpenAiModel = "gpt-4o-mini",
            OpenAiReasoningEffort = "medium",
            OpenAiBaseUrl = "https://api.openai.com/v1",
            TimeoutSeconds = 10
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = "{\"choices\":[{\"message\":{\"content\":\"hello\"}}]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new OpenAiClient(http, settings);

        var result = await client.ChatAsync([new ChatMessage("user", "hi")]);

        Assert.Equal("hello", result);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("k", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.False(handler.LastRequest.Headers.Contains("HTTP-Referer"));
        Assert.False(handler.LastRequest.Headers.Contains("X-Title"));

        var requestBody = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(requestBody);
        Assert.Equal("gpt-4o-mini", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("medium", doc.RootElement.GetProperty("reasoning_effort").GetString());
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("hi", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ChatCompletionAsync_DoesNotSendEmptyToolsOrToolChoice()
    {
        var settings = new LlmSettings
        {
            Provider = "openai",
            OpenAiApiKey = "k",
            OpenAiModel = "gpt-4o-mini",
            OpenAiBaseUrl = "https://api.openai.com/v1",
            TimeoutSeconds = 10
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new OpenAiClient(http, settings);

        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage("user", "hi")],
            Tools = [],
            ToolChoice = new ChatToolChoice { Mode = "auto" }
        });

        var requestBody = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(requestBody);
        Assert.False(doc.RootElement.TryGetProperty("tools", out _), requestBody);
        Assert.False(doc.RootElement.TryGetProperty("tool_choice", out _), requestBody);
    }

    [Fact]
    public async Task ChatCompletionAsync_InvalidOpenAiModel_ThrowsBeforeSendingRequest()
    {
        var settings = new LlmSettings
        {
            Provider = "openai",
            OpenAiApiKey = "k",
            OpenAiModel = "openai/",
            OpenAiBaseUrl = "https://api.openai.com/v1",
            TimeoutSeconds = 10
        };

        using var http = new HttpClient(new ThrowingHandler());
        var client = new OpenAiClient(http, settings);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ChatCompletionAsync(new ChatCompletionRequest { Messages = [new ChatMessage("user", "hi")] }));

        Assert.Contains("model", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChatCompletionAsync_MaxTokens_WithGpt5Model_UsesMaxCompletionTokens()
    {
        var settings = new LlmSettings
        {
            Provider = "openai",
            OpenAiApiKey = "k",
            OpenAiModel = "gpt-5.2",
            OpenAiBaseUrl = "https://api.openai.com/v1",
            TimeoutSeconds = 10
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = "{\"choices\":[{\"message\":{\"content\":\"hello\"}}]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new OpenAiClient(http, settings);

        _ = await client.ChatCompletionAsync(
            new ChatCompletionRequest
            {
                Messages = [new ChatMessage("user", "hi")],
                MaxTokens = 123
            });

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(doc.RootElement.TryGetProperty("max_tokens", out _));
        Assert.Equal(123, doc.RootElement.GetProperty("max_completion_tokens").GetInt32());
    }

    [Fact]
    public async Task ChatCompletionAsync_MaxTokens_WithOpenAiPrefixedGpt5Model_UsesMaxCompletionTokens()
    {
        var settings = new LlmSettings
        {
            Provider = "openai",
            OpenAiApiKey = "k",
            OpenAiModel = "openai/gpt-5.2",
            OpenAiBaseUrl = "https://api.openai.com/v1",
            TimeoutSeconds = 10
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = "{\"choices\":[{\"message\":{\"content\":\"hello\"}}]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new OpenAiClient(http, settings);

        _ = await client.ChatCompletionAsync(
            new ChatCompletionRequest
            {
                Messages = [new ChatMessage("user", "hi")],
                MaxTokens = 123
            });

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("gpt-5.2", doc.RootElement.GetProperty("model").GetString());
        Assert.False(doc.RootElement.TryGetProperty("max_tokens", out _));
        Assert.Equal(123, doc.RootElement.GetProperty("max_completion_tokens").GetInt32());
    }

    [Fact]
    public async Task ChatCompletionAsync_MaxTokens_WhenServerRejectsMaxTokens_RetriesWithMaxCompletionTokens()
    {
        var settings = new LlmSettings
        {
            Provider = "openai",
            OpenAiApiKey = "k",
            OpenAiModel = "gpt-4o-mini",
            OpenAiBaseUrl = "https://api.openai.com/v1",
            TimeoutSeconds = 10
        };

        var errorBody = """
                        {
                          "error": {
                            "message": "Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.",
                            "type": "invalid_request_error",
                            "param": "max_tokens",
                            "code": "unsupported_parameter"
                          }
                        }
                        """;

        var okBody = "{\"choices\":[{\"message\":{\"content\":\"hello\"}}]}";

        var handler = new SequenceHandler(
        [
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(errorBody, Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(okBody, Encoding.UTF8, "application/json")
            }
        ]);

        using var http = new HttpClient(handler);
        var client = new OpenAiClient(http, settings);

        var result = await client.ChatCompletionAsync(
            new ChatCompletionRequest
            {
                Messages = [new ChatMessage("user", "hi")],
                MaxTokens = 321
            });

        Assert.Equal("hello", result.Text);
        Assert.Equal(2, handler.RequestBodies.Count);

        using var request1 = JsonDocument.Parse(handler.RequestBodies[0]!);
        Assert.Equal(321, request1.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(request1.RootElement.TryGetProperty("max_completion_tokens", out _));

        using var request2 = JsonDocument.Parse(handler.RequestBodies[1]!);
        Assert.False(request2.RootElement.TryGetProperty("max_tokens", out _));
        Assert.Equal(321, request2.RootElement.GetProperty("max_completion_tokens").GetInt32());
    }

    [Fact]
    public async Task ChatAsync_Error_TruncatesAndRedactsBody()
    {
        var secret = "sk-123";
        var body = "apiKey=" + secret + "\n" + new string('x', 100_000);

        var settings = new LlmSettings
        {
            Provider = "openai",
            OpenAiApiKey = "k",
            OpenAiModel = "gpt-4o-mini",
            OpenAiBaseUrl = "https://api.openai.com/v1",
            TimeoutSeconds = 10
        };

        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        });

        using var http = new HttpClient(handler);
        var client = new OpenAiClient(http, settings);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync([new ChatMessage("user", "hi")]));
        Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);
        Assert.Contains("apiKey=***", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(ex.Message.Length < 50_000);
    }

    [Fact]
    public async Task ChatCompletionAsync_EncodesToolAndAssistantToolCallsAndProviderFields()
    {
        var settings = new LlmSettings
        {
            Provider = "openai",
            OpenAiApiKey = "k",
            OpenAiModel = "gpt-4o-mini",
            OpenAiBaseUrl = "https://api.openai.com/v1",
            TimeoutSeconds = 10
        };

        using var providerDoc = JsonDocument.Parse("""{ "effort": "high" }""");
        using var reservedDoc = JsonDocument.Parse("\"tc1\"");
        using var toolSchemaDoc = JsonDocument.Parse("""{ "type":"object","properties":{"command":{"type":"string"}} }""");

        IReadOnlyList<ChatToolCall> assistantToolCalls =
        [
            new ChatToolCall("tc1", "exec", "{\"command\":\"bt\"}")
        ];

        var handler = new CapturingHandler(_ =>
        {
            var body = "{\"choices\":[{\"message\":{\"content\":\"hello\"}}]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new OpenAiClient(http, settings);

        _ = await client.ChatCompletionAsync(
            new ChatCompletionRequest
            {
                Messages =
                [
                    new ChatMessage("user", "hi", toolCallId: null, toolCalls: null, providerMessageFields: new Dictionary<string, JsonElement>
                    {
                        // Reserved key: should not appear as extension data.
                        ["tool_call_id"] = reservedDoc.RootElement.Clone()
                    }),
                    new ChatMessage(
                        "assistant",
                        string.Empty,
                        toolCallId: null,
                        toolCalls: assistantToolCalls,
                        providerMessageFields: new Dictionary<string, JsonElement>
                        {
                            ["reasoning"] = providerDoc.RootElement.Clone(),
                            ["role"] = reservedDoc.RootElement.Clone(),
                            ["content"] = reservedDoc.RootElement.Clone()
                        }),
                    new ChatMessage("tool", "bt-output", toolCallId: "tc1", toolCalls: null)
                ],
                Tools =
                [
                    new ChatTool
                    {
                        Name = "exec",
                        Description = "run",
                        Parameters = toolSchemaDoc.RootElement.Clone()
                    }
                ]
            });

        Assert.NotNull(handler.LastRequestBody);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);

        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());

        // User message does not gain tool_call_id from provider fields.
        Assert.False(messages[0].TryGetProperty("tool_call_id", out _));

        // Assistant tool calls are encoded and empty content is omitted/null.
        Assert.True(messages[1].TryGetProperty("tool_calls", out var toolCalls));
        Assert.Equal(1, toolCalls.GetArrayLength());
        Assert.Equal("tc1", toolCalls[0].GetProperty("id").GetString());
        Assert.Equal("exec", toolCalls[0].GetProperty("function").GetProperty("name").GetString());

        // Provider-specific fields included as extension data.
        Assert.True(messages[1].TryGetProperty("reasoning", out var reasoning));
        Assert.Equal("high", reasoning.GetProperty("effort").GetString());

        // Tool message includes tool_call_id.
        Assert.Equal("tc1", messages[2].GetProperty("tool_call_id").GetString());

        // Tool schema passed through.
        var tools = doc.RootElement.GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("exec", tools[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ChatAsync_WhenErrorBodyEmpty_Throws()
    {
        var settings = new LlmSettings
        {
            Provider = "openai",
            OpenAiApiKey = "k",
            OpenAiModel = "gpt-4o-mini",
            OpenAiBaseUrl = "https://api.openai.com/v1",
            TimeoutSeconds = 10
        };

        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain")
        });

        using var http = new HttpClient(handler);
        var client = new OpenAiClient(http, settings);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync([new ChatMessage("user", "hi")]));
        Assert.Contains("OpenAI request failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChatAsync_WhenErrorBodyHasInvalidUtf8_DoesNotThrowDecoderExceptions()
    {
        var settings = new LlmSettings
        {
            Provider = "openai",
            OpenAiApiKey = "k",
            OpenAiModel = "gpt-4o-mini",
            OpenAiBaseUrl = "https://api.openai.com/v1",
            TimeoutSeconds = 10
        };

        var bytes = new byte[] { 0xFF, 0xFF, 0x61 };
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new ByteArrayContent(bytes)
        });

        using var http = new HttpClient(handler);
        var client = new OpenAiClient(http, settings);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync([new ChatMessage("user", "hi")]));
        Assert.Contains("OpenAI request failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
