using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Serialization;
using DebuggerMcp.Cli.Shell.Transcript;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Minimal OpenRouter client for chat completions (OpenAI-compatible API surface).
/// </summary>
public sealed class OpenRouterClient(HttpClient httpClient, LlmSettings settings)
{
    private static readonly JsonSerializerOptions JsonOptions = CliJsonSerializationDefaults.CaseInsensitiveCamelCaseIgnoreNull;
    private const int MaxErrorBodyBytes = 32_000;

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly LlmSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public async Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var result = await ChatCompletionAsync(new ChatCompletionRequest { Messages = messages }, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Text))
        {
            throw new InvalidOperationException("OpenRouter response did not contain any message content.");
        }
        return result.Text.Trim();
    }

    internal async Task<ChatCompletionResult> ChatCompletionAsync(ChatCompletionRequest completionRequest, CancellationToken cancellationToken = default)
    {
        if (completionRequest == null)
        {
            throw new ArgumentNullException(nameof(completionRequest));
        }

        if (completionRequest.Messages == null)
        {
            throw new ArgumentNullException(nameof(completionRequest.Messages));
        }

        var apiKey = _settings.GetEffectiveOpenRouterApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenRouter API key is not configured.");
        }

        var baseUrl = _settings.OpenRouterBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("OpenRouter base URL is not configured.");
        }

        var url = $"{baseUrl}/chat/completions";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.UserAgent.ParseAdd("DebuggerMcp.Cli/1.0");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // OpenRouter recommends these for attribution; keep defaults stable.
        httpRequest.Headers.TryAddWithoutValidation("X-Title", "DebuggerMcp.Cli");
        httpRequest.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/tonyredondo/debugger-mcp-server");

        var payload = new OpenRouterChatRequest
        {
            Model = _settings.OpenRouterModel,
            Messages = completionRequest.Messages.Select(ToOpenRouterMessage).ToList(),
            Tools = completionRequest.Tools?.Select(ToOpenRouterTool).ToList(),
            ToolChoice = ToOpenRouterToolChoice(completionRequest.ToolChoice),
            MaxTokens = completionRequest.MaxTokens
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var (errorBody, truncated) = await ReadContentCappedAsync(response.Content, MaxErrorBodyBytes, cancellationToken).ConfigureAwait(false);
            errorBody = TranscriptRedactor.RedactText(errorBody);
            if (truncated)
            {
                errorBody += $"{Environment.NewLine}... (truncated) ...";
            }

            throw new HttpRequestException($"OpenRouter request failed ({(int)response.StatusCode}): {errorBody}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        OpenRouterChatResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<OpenRouterChatResponse>(responseBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Failed to parse OpenRouter response JSON.", ex);
        }

        var message = parsed?.Choices?.FirstOrDefault()?.Message;
        var content = message?.Content;

        var toolCalls = new List<ChatToolCall>();
        if (message?.ToolCalls != null)
        {
            foreach (var call in message.ToolCalls)
            {
                var id = call.Id ?? string.Empty;
                var name = call.Function?.Name ?? string.Empty;
                var args = call.Function?.Arguments ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                {
                    toolCalls.Add(new ChatToolCall(id, name, args));
                }
            }
        }

        return new ChatCompletionResult
        {
            Model = parsed?.Model ?? _settings.OpenRouterModel,
            Text = content,
            ToolCalls = toolCalls
        };
    }

    private static async Task<(string Text, bool Truncated)> ReadContentCappedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        maxBytes = Math.Max(0, maxBytes);
        if (maxBytes == 0)
        {
            return (string.Empty, false);
        }

        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[maxBytes + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken).ConfigureAwait(false);
                if (n <= 0)
                {
                    break;
                }
                read += n;
            }

            var truncated = read > maxBytes;
            var effective = Math.Min(read, maxBytes);
            var text = Encoding.UTF8.GetString(buffer, 0, effective);
            return (text, truncated);
        }
        catch
        {
            // Best-effort: do not block on error-body parsing.
            return (string.Empty, false);
        }
    }

    private static OpenRouterChatMessage ToOpenRouterMessage(ChatMessage message)
    {
        var msg = new OpenRouterChatMessage
        {
            Role = message.Role,
            Content = message.Content
        };

        if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            msg.ToolCallId = message.ToolCallId;
        }

        if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
            message.ToolCalls is { Count: > 0 })
        {
            msg.ToolCalls = message.ToolCalls
                .Select(tc => new OpenRouterToolCall
                {
                    Id = tc.Id,
                    Type = "function",
                    Function = new OpenRouterToolCallFunction
                    {
                        Name = tc.Name,
                        Arguments = tc.ArgumentsJson
                    }
                })
                .ToList();
        }

        return msg;
    }

    private static OpenRouterTool ToOpenRouterTool(ChatTool tool)
        => new()
        {
            Type = "function",
            Function = new OpenRouterToolFunction
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = tool.Parameters
            }
        };

    private static object? ToOpenRouterToolChoice(ChatToolChoice? choice)
    {
        if (choice == null)
        {
            return null;
        }

        // If a specific function name is provided, prefer the explicit OpenAI-style object.
        // This should override generic modes like "required".
        if (!string.IsNullOrWhiteSpace(choice.FunctionName))
        {
            return new
            {
                type = "function",
                function = new { name = choice.FunctionName }
            };
        }

        var mode = (choice.Mode ?? "auto").Trim().ToLowerInvariant();
        if (mode is "auto" or "none" or "required")
        {
            return mode;
        }

        return "auto";
    }

    private sealed class OpenRouterChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "openrouter/auto";

        [JsonPropertyName("messages")]
        public List<OpenRouterChatMessage> Messages { get; set; } = [];

        [JsonPropertyName("tools")]
        public List<OpenRouterTool>? Tools { get; set; }

        [JsonPropertyName("tool_choice")]
        public object? ToolChoice { get; set; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; set; }
    }

    private sealed class OpenRouterChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<OpenRouterToolCall>? ToolCalls { get; set; }
    }

    private sealed class OpenRouterTool
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public OpenRouterToolFunction Function { get; set; } = new();
    }

    private sealed class OpenRouterToolFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("parameters")]
        public JsonElement Parameters { get; set; }
    }

    private sealed class OpenRouterToolCall
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("function")]
        public OpenRouterToolCallFunction? Function { get; set; }
    }

    private sealed class OpenRouterToolCallFunction
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("arguments")]
        public string? Arguments { get; set; }
    }

    private sealed class OpenRouterChatResponse
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("choices")]
        public List<OpenRouterChoice>? Choices { get; set; }
    }

    private sealed class OpenRouterChoice
    {
        [JsonPropertyName("message")]
        public OpenRouterChoiceMessage? Message { get; set; }
    }

    private sealed class OpenRouterChoiceMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<OpenRouterToolCall>? ToolCalls { get; set; }
    }
}
