using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

[Collection("NonParallelConsole")]
public class OpenRouterClientTests
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
    public async Task ChatAsync_SendsOpenAiCompatibleRequestAndParsesResponse()
    {
        var settings = new LlmSettings
        {
            OpenRouterApiKey = "k",
            OpenRouterModel = "openrouter/auto",
            OpenRouterBaseUrl = "https://openrouter.ai/api/v1",
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
        var client = new OpenRouterClient(http, settings);

        var result = await client.ChatAsync([new ChatMessage("user", "hi")]);

        Assert.Equal("hello", result);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("k", handler.LastRequest.Headers.Authorization.Parameter);

        var requestBody = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(requestBody);
        Assert.Equal("openrouter/auto", doc.RootElement.GetProperty("model").GetString());
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("hi", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ChatAsync_Error_TruncatesAndRedactsBody()
    {
        var secret = "sk-123";
        var body = "apiKey=" + secret + "\n" + new string('x', 100_000);

        var settings = new LlmSettings
        {
            OpenRouterApiKey = "k",
            OpenRouterModel = "openrouter/auto",
            OpenRouterBaseUrl = "https://openrouter.ai/api/v1",
            TimeoutSeconds = 10
        };

        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        });

        using var http = new HttpClient(handler);
        var client = new OpenRouterClient(http, settings);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync([new ChatMessage("user", "hi")]));
        Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);
        Assert.Contains("apiKey=***", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(ex.Message.Length < 50_000);
    }

    [Fact]
    public async Task ChatCompletionAsync_SendsToolsAndParsesToolCalls()
    {
        var settings = new LlmSettings
        {
            OpenRouterApiKey = "k",
            OpenRouterModel = "openrouter/auto",
            OpenRouterBaseUrl = "https://openrouter.ai/api/v1",
            TimeoutSeconds = 10
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """
            {
              "model":"openrouter/auto",
              "choices":[
                {
                  "message":{
                    "content":null,
                    "tool_calls":[
                      {"id":"call_1","type":"function","function":{"name":"exec","arguments":"{\"command\":\"bt\"}"}}
                    ]
                  }
                }
              ]
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new OpenRouterClient(http, settings);

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
        Assert.Equal("call_1", result.ToolCalls[0].Id);
        Assert.Equal("exec", result.ToolCalls[0].Name);

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.True(reqDoc.RootElement.TryGetProperty("tools", out var tools));
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("function", tools[0].GetProperty("type").GetString());
        Assert.Equal("exec", tools[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("auto", reqDoc.RootElement.GetProperty("tool_choice").GetString());
        Assert.Equal(50, reqDoc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task ChatCompletionAsync_PreservesProviderFieldsInResult()
    {
        var settings = new LlmSettings
        {
            OpenRouterApiKey = "k",
            OpenRouterModel = "openrouter/auto",
            OpenRouterBaseUrl = "https://openrouter.ai/api/v1",
            TimeoutSeconds = 10
        };

        var handler = new CapturingHandler(_ =>
        {
            var body = """
            {
              "model":"openrouter/auto",
              "choices":[
                {
                  "message":{
                    "content":null,
                    "reasoning":{"thought_signature":"sig1","text":"x"},
                    "tool_calls":[
                      {"id":"call_1","type":"function","function":{"name":"exec","arguments":"{\"command\":\"bt\"}"}}
                    ]
                  }
                }
              ]
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new OpenRouterClient(http, settings);

        using var schemaDoc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"}}}");
        var result = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage("user", "hi")],
            Tools = [new ChatTool { Name = "exec", Description = "d", Parameters = schemaDoc.RootElement.Clone() }],
            ToolChoice = new ChatToolChoice { Mode = "auto" },
            MaxTokens = 50
        });

        Assert.NotNull(result.ProviderMessageFields);
        Assert.True(result.ProviderMessageFields!.ContainsKey("reasoning"));
        Assert.Contains("thought_signature", result.ProviderMessageFields["reasoning"].GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChatCompletionAsync_WhenAssistantMessageIncludesProviderFields_SendsThemBack()
    {
        var settings = new LlmSettings
        {
            OpenRouterApiKey = "k",
            OpenRouterModel = "openrouter/auto",
            OpenRouterBaseUrl = "https://openrouter.ai/api/v1",
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
        var client = new OpenRouterClient(http, settings);

        using var schemaDoc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"}}}");
        using var providerDoc = JsonDocument.Parse("{\"thought_signature\":\"sig1\",\"text\":\"x\"}");

        var toolCalls = new List<ChatToolCall> { new("call_1", "exec", "{\"command\":\"bt\"}") };
        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage("user", "hi"),
                new ChatMessage(
                    "assistant",
                    "",
                    toolCallId: null,
                    toolCalls: toolCalls,
                    contentJson: null,
                    providerMessageFields: new Dictionary<string, JsonElement>
                    {
                        ["reasoning"] = providerDoc.RootElement.Clone()
                    })
            ],
            Tools = [new ChatTool { Name = "exec", Description = "d", Parameters = schemaDoc.RootElement.Clone() }],
            ToolChoice = new ChatToolChoice { Mode = "auto" },
            MaxTokens = null
        });

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = reqDoc.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.True(messages[1].TryGetProperty("reasoning", out var reasoning));
        Assert.Equal("sig1", reasoning.GetProperty("thought_signature").GetString());
    }

    [Fact]
    public async Task ChatCompletionAsync_ToolChoiceRequiredWithName_SendsFunctionObject()
    {
        var settings = new LlmSettings
        {
            OpenRouterApiKey = "k",
            OpenRouterModel = "openrouter/auto",
            OpenRouterBaseUrl = "https://openrouter.ai/api/v1",
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
        var client = new OpenRouterClient(http, settings);

        using var schemaDoc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"}}}");
        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage("user", "hi")],
            Tools = [new ChatTool { Name = "exec", Description = "d", Parameters = schemaDoc.RootElement.Clone() }],
            ToolChoice = new ChatToolChoice { Mode = "required", FunctionName = "exec" },
            MaxTokens = null
        });

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        var toolChoice = reqDoc.RootElement.GetProperty("tool_choice");
        Assert.Equal(JsonValueKind.Object, toolChoice.ValueKind);
        Assert.Equal("function", toolChoice.GetProperty("type").GetString());
        Assert.Equal("exec", toolChoice.GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ChatCompletionAsync_AssistantToolCallsWithEmptyContent_OmitsContentField()
    {
        var settings = new LlmSettings
        {
            OpenRouterApiKey = "k",
            OpenRouterModel = "openrouter/auto",
            OpenRouterBaseUrl = "https://openrouter.ai/api/v1",
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
        var client = new OpenRouterClient(http, settings);

        using var schemaDoc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"}}}");

        var toolCalls = new List<ChatToolCall> { new("call_1", "exec", "{\"command\":\"bt\"}") };
        _ = await client.ChatCompletionAsync(new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage("user", "hi"),
                new ChatMessage("assistant", "", toolCallId: null, toolCalls: toolCalls)
            ],
            Tools = [new ChatTool { Name = "exec", Description = "d", Parameters = schemaDoc.RootElement.Clone() }],
            ToolChoice = new ChatToolChoice { Mode = "auto" },
            MaxTokens = null
        });

        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = reqDoc.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.True(messages[1].TryGetProperty("tool_calls", out _));
        Assert.False(messages[1].TryGetProperty("content", out _));
    }
}
