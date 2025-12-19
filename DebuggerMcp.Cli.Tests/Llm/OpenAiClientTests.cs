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
}
