using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Serialization;
using DebuggerMcp.Cli.Shell.Transcript;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Minimal OpenAI client for chat completions (OpenAI-compatible API surface).
/// </summary>
public sealed class OpenAiClient(HttpClient httpClient, LlmSettings settings)
{
    private static readonly JsonSerializerOptions JsonOptions = CliJsonSerializationDefaults.CaseInsensitiveCamelCaseIgnoreNull;
    private const int MaxErrorBodyBytes = 32_000;

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly LlmSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public async Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var result = await ChatCompletionAsync(
            new ChatCompletionRequest
            {
                Messages = messages,
                ReasoningEffort = _settings.OpenAiReasoningEffort
            },
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Text))
        {
            throw new InvalidOperationException("OpenAI response did not contain any message content.");
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

        var apiKey = _settings.GetEffectiveOpenAiApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenAI API key is not configured.");
        }

        var baseUrl = _settings.OpenAiBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("OpenAI base URL is not configured.");
        }

        var url = $"{baseUrl}/chat/completions";

        var payload = new OpenAiChatRequest
        {
            Model = _settings.OpenAiModel,
            Messages = completionRequest.Messages.Select(ToOpenAiMessage).ToList(),
            Tools = completionRequest.Tools?.Select(ToOpenAiTool).ToList(),
            ToolChoice = ToOpenAiToolChoice(completionRequest.ToolChoice),
            ReasoningEffort = completionRequest.ReasoningEffort
        };

        var maxTokens = completionRequest.MaxTokens;
        if (maxTokens.HasValue)
        {
            if (PreferMaxCompletionTokens(_settings.OpenAiModel))
            {
                payload.MaxCompletionTokens = maxTokens;
            }
            else
            {
                payload.MaxTokens = maxTokens;
            }
        }

        var (status, responseBody, errorBody) = await SendOnceAsync(url, apiKey, payload, cancellationToken).ConfigureAwait(false);

        // Some OpenAI models do not accept max_tokens and require max_completion_tokens.
        if (status == 400 &&
            maxTokens.HasValue &&
            payload.MaxTokens.HasValue &&
            MentionsUnsupportedParameter(errorBody, "max_tokens") &&
            MentionsParameter(errorBody, "max_completion_tokens"))
        {
            payload.MaxTokens = null;
            payload.MaxCompletionTokens = maxTokens;
            (status, responseBody, errorBody) = await SendOnceAsync(url, apiKey, payload, cancellationToken).ConfigureAwait(false);
        }

        // Some models may accept max_tokens but not max_completion_tokens; retry the other direction too.
        if (status == 400 &&
            maxTokens.HasValue &&
            payload.MaxCompletionTokens.HasValue &&
            MentionsUnsupportedParameter(errorBody, "max_completion_tokens") &&
            MentionsParameter(errorBody, "max_tokens"))
        {
            payload.MaxCompletionTokens = null;
            payload.MaxTokens = maxTokens;
            (status, responseBody, errorBody) = await SendOnceAsync(url, apiKey, payload, cancellationToken).ConfigureAwait(false);
        }

        if (status < 200 || status >= 300)
        {
            var redacted = TranscriptRedactor.RedactText(errorBody);
            throw new HttpRequestException($"OpenAI request failed ({status}): {redacted}");
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            var model = root.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String
                ? modelProp.GetString()
                : null;

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                return new ChatCompletionResult
                {
                    Model = model ?? _settings.OpenAiModel,
                    Text = null,
                    ToolCalls = []
                };
            }

            var choice = choices[0];
            if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            {
                return new ChatCompletionResult
                {
                    Model = model ?? _settings.OpenAiModel,
                    Text = null,
                    ToolCalls = []
                };
            }

            var toolCalls = new List<ChatToolCall>();
            if (message.TryGetProperty("tool_calls", out var toolCallsProp) && toolCallsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in toolCallsProp.EnumerateArray())
                {
                    if (call.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var id = call.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                        ? idProp.GetString() ?? string.Empty
                        : string.Empty;

                    if (!call.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var name = fn.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                        ? nameProp.GetString() ?? string.Empty
                        : string.Empty;

                    var args = fn.TryGetProperty("arguments", out var argsProp)
                        ? argsProp.ValueKind == JsonValueKind.String
                            ? argsProp.GetString() ?? string.Empty
                            : argsProp.GetRawText()
                        : string.Empty;

                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    {
                        toolCalls.Add(new ChatToolCall(id, name, args));
                    }
                }
            }

            JsonElement? rawContent = null;
            string? text = null;
            if (message.TryGetProperty("content", out var contentProp))
            {
                rawContent = contentProp.Clone();
                text = OpenRouterClient.ExtractText(contentProp);
            }

            var providerFields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in message.EnumerateObject())
            {
                if (string.Equals(prop.Name, "content", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prop.Name, "tool_calls", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prop.Name, "role", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                providerFields[prop.Name] = prop.Value.Clone();
            }

            return new ChatCompletionResult
            {
                Model = model ?? _settings.OpenAiModel,
                Text = text,
                RawMessageContent = rawContent,
                ProviderMessageFields = providerFields.Count == 0 ? null : providerFields,
                ToolCalls = toolCalls
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Failed to parse OpenAI response JSON.", ex);
        }
    }

    private static bool PreferMaxCompletionTokens(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var m = model.Trim().ToLowerInvariant();
        var slash = m.LastIndexOf('/');
        var normalized = slash >= 0 ? m[(slash + 1)..] : m;

        return normalized.StartsWith("gpt-5", StringComparison.Ordinal) ||
               normalized.StartsWith("o1", StringComparison.Ordinal) ||
               normalized.StartsWith("o3", StringComparison.Ordinal);
    }

    private static bool MentionsUnsupportedParameter(string? errorBody, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(errorBody) || string.IsNullOrWhiteSpace(parameterName))
        {
            return false;
        }

        // Example: Unsupported parameter: 'max_tokens' ...
        return errorBody.Contains("unsupported parameter", StringComparison.OrdinalIgnoreCase) &&
               errorBody.Contains(parameterName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MentionsParameter(string? errorBody, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(errorBody) || string.IsNullOrWhiteSpace(parameterName))
        {
            return false;
        }

        return errorBody.Contains(parameterName, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(int StatusCode, string ResponseBody, string ErrorBody)> SendOnceAsync(
        string url,
        string apiKey,
        OpenAiChatRequest payload,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.UserAgent.ParseAdd("DebuggerMcp.Cli/1.0");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ((int)response.StatusCode, body, string.Empty);
        }

        var (errorBody, truncated) = await ReadContentCappedAsync(response.Content, MaxErrorBodyBytes, cancellationToken).ConfigureAwait(false);
        if (truncated)
        {
            errorBody += $"{Environment.NewLine}... (truncated) ...";
        }
        return ((int)response.StatusCode, string.Empty, errorBody);
    }

    private static OpenAiChatMessage ToOpenAiMessage(ChatMessage msg)
    {
        var message = new OpenAiChatMessage
        {
            Role = msg.Role,
            Content = msg.ContentJson.HasValue ? msg.ContentJson.Value : msg.Content
        };

        if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(msg.ToolCallId))
        {
            message.ToolCallId = msg.ToolCallId;
        }

        if (string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
            msg.ToolCalls is { Count: > 0 })
        {
            if (!msg.ContentJson.HasValue && string.IsNullOrWhiteSpace(msg.Content))
            {
                message.Content = null;
            }

            message.ToolCalls = msg.ToolCalls
                .Select(tc => new OpenAiToolCall
                {
                    Id = tc.Id,
                    Type = "function",
                    Function = new OpenAiToolCallFunction { Name = tc.Name, Arguments = tc.ArgumentsJson }
                })
                .ToList();
        }

        if (msg.ProviderMessageFields is { Count: > 0 })
        {
            message.ExtensionData = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in msg.ProviderMessageFields)
            {
                if (string.Equals(kvp.Key, "role", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "content", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "tool_calls", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "tool_call_id", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                message.ExtensionData[kvp.Key] = kvp.Value;
            }

            if (message.ExtensionData.Count == 0)
            {
                message.ExtensionData = null;
            }
        }

        return message;
    }

    private static OpenAiTool ToOpenAiTool(ChatTool tool)
        => new()
        {
            Type = "function",
            Function = new OpenAiToolFunction
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = tool.Parameters
            }
        };

    private static object? ToOpenAiToolChoice(ChatToolChoice? choice)
        => choice == null
            ? null
            : string.Equals(choice.Mode, "auto", StringComparison.OrdinalIgnoreCase)
                ? "auto"
                : string.Equals(choice.Mode, "none", StringComparison.OrdinalIgnoreCase)
                    ? "none"
                    : string.Equals(choice.Mode, "required", StringComparison.OrdinalIgnoreCase)
                        ? "required"
                        : string.Equals(choice.Mode, "function", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(choice.FunctionName)
                            ? new { type = "function", function = new { name = choice.FunctionName } }
                            : "auto";

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
                if (n == 0)
                {
                    break;
                }
                read += n;
            }

            var truncated = read > maxBytes;
            if (truncated)
            {
                read = maxBytes;
            }

            return (DecodeUtf8Prefix(buffer, read), truncated);
        }
        catch
        {
            return (string.Empty, false);
        }
    }

    private static string DecodeUtf8Prefix(byte[] buffer, int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        // Avoid splitting multi-byte UTF-8 sequences by backing off a few bytes at most.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var safeCount = Math.Min(count, buffer.Length);
        for (var i = 0; i <= 4 && safeCount - i >= 0; i++)
        {
            try
            {
                return encoding.GetString(buffer, 0, safeCount - i);
            }
            catch (DecoderFallbackException)
            {
                // try again with fewer bytes
            }
        }

        return Encoding.UTF8.GetString(buffer, 0, safeCount);
    }

    private sealed class OpenAiChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<OpenAiChatMessage> Messages { get; set; } = [];

        [JsonPropertyName("tools")]
        public List<OpenAiTool>? Tools { get; set; }

        [JsonPropertyName("tool_choice")]
        public object? ToolChoice { get; set; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; set; }

        [JsonPropertyName("max_completion_tokens")]
        public int? MaxCompletionTokens { get; set; }

        [JsonPropertyName("reasoning_effort")]
        public string? ReasoningEffort { get; set; }
    }

    private sealed class OpenAiChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public object? Content { get; set; }

        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<OpenAiToolCall>? ToolCalls { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    private sealed class OpenAiTool
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public OpenAiToolFunction Function { get; set; } = new();
    }

    private sealed class OpenAiToolFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("parameters")]
        public JsonElement Parameters { get; set; }
    }

    private sealed class OpenAiToolCall
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public OpenAiToolCallFunction Function { get; set; } = new();
    }

    private sealed class OpenAiToolCallFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        public string Arguments { get; set; } = "{}";
    }
}
