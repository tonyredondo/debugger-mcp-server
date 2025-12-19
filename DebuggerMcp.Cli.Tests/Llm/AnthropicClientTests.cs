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
}

