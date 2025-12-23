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
        var result = await ChatCompletionAsync(
            new ChatCompletionRequest
            {
                Messages = messages,
                ReasoningEffort = _settings.OpenRouterReasoningEffort
            },
            cancellationToken).ConfigureAwait(false);
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
            MaxTokens = completionRequest.MaxTokens,
            ReasoningEffort = completionRequest.ReasoningEffort
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        // Use ResponseHeadersRead so we can cap error bodies without buffering the entire response.
        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

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
                    Model = model ?? _settings.OpenRouterModel,
                    Text = null,
                    ToolCalls = []
                };
            }

            var choice0 = choices[0];
            if (!choice0.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            {
                return new ChatCompletionResult
                {
                    Model = model ?? _settings.OpenRouterModel,
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
                text = ExtractText(contentProp);

                // Some providers (notably Gemini via OpenRouter) emit tool calls as MCP/Anthropic-style blocks
                // in message.content rather than OpenAI-style message.tool_calls.
                if (contentProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in contentProp.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var type = item.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                            ? typeProp.GetString() ?? string.Empty
                            : string.Empty;

                        if (!string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var id = item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                            ? idProp.GetString() ?? string.Empty
                            : string.Empty;

                        var name = item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                            ? nameProp.GetString() ?? string.Empty
                            : string.Empty;

                        var input = item.TryGetProperty("input", out var inputProp)
                            ? inputProp.GetRawText()
                            : "{}";

                        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                        {
                            if (!toolCalls.Any(tc => string.Equals(tc.Id, id, StringComparison.OrdinalIgnoreCase)))
                            {
                                toolCalls.Add(new ChatToolCall(id, name, input));
                            }
                        }
                    }
                }
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
                Model = model ?? _settings.OpenRouterModel,
                Text = text,
                RawMessageContent = rawContent,
                ProviderMessageFields = providerFields.Count == 0 ? null : providerFields,
                ToolCalls = toolCalls
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Failed to parse OpenRouter response JSON.", ex);
        }
    }

    internal static string? ExtractText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind == JsonValueKind.Null || content.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(s.TrimEnd());
                    }
                    continue;
                }

                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (item.TryGetProperty("type", out var typeProp) &&
                    typeProp.ValueKind == JsonValueKind.String &&
                    (string.Equals(typeProp.GetString(), "text", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(typeProp.GetString(), "output_text", StringComparison.OrdinalIgnoreCase)) &&
                    item.TryGetProperty("text", out var textProp) &&
                    textProp.ValueKind == JsonValueKind.String)
                {
                    var t = textProp.GetString();
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(t.TrimEnd());
                    }
                }
            }

            var combined = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(combined) ? null : combined;
        }

        // Unknown shape: preserve as JSON text.
        var raw = content.GetRawText();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
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
            var text = DecodeUtf8Prefix(buffer, effective);
            return (text, truncated);
        }
        catch
        {
            // Best-effort: do not block on error-body parsing.
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

    private static OpenRouterChatMessage ToOpenRouterMessage(ChatMessage message)
    {
        var msg = new OpenRouterChatMessage
        {
            Role = message.Role,
            Content = ToOpenRouterContent(message)
        };

        if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            msg.ToolCallId = message.ToolCallId;
        }

        if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
            message.ToolCalls is { Count: > 0 })
        {
            // OpenAI-style: assistant messages with tool_calls may omit content or set it to null.
            if (!message.ContentJson.HasValue && string.IsNullOrWhiteSpace(message.Content))
            {
                msg.Content = null;
            }

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

        if (message.ProviderMessageFields is { Count: > 0 })
        {
            msg.ExtensionData = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in message.ProviderMessageFields)
            {
                // Avoid colliding with known OpenAI fields we already set.
                if (string.Equals(kvp.Key, "role", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "content", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "tool_calls", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "tool_call_id", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                msg.ExtensionData[kvp.Key] = kvp.Value;
            }

            if (msg.ExtensionData.Count == 0)
            {
                msg.ExtensionData = null;
            }
        }

        return msg;
    }

    private static object? ToOpenRouterContent(ChatMessage message)
    {
        if (message.ContentJson.HasValue)
        {
            var contentJson = message.ContentJson.Value;
            if (contentJson.ValueKind == JsonValueKind.Array)
            {
                return contentJson.Clone();
            }

            if (contentJson.ValueKind == JsonValueKind.Object)
            {
                // OpenAI-compatible APIs expect message.content to be a string or an array of parts.
                // MCP/Anthropic-style messages sometimes provide a single content block object; wrap it.
                return new[] { contentJson.Clone() };
            }

            if (contentJson.ValueKind == JsonValueKind.String)
            {
                var s = contentJson.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }
            else if (contentJson.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                // Preserve unknown shapes as JSON text so the request remains valid.
                var raw = contentJson.GetRawText();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return raw;
                }
            }
        }

        return string.IsNullOrWhiteSpace(message.Content) ? null : message.Content;
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

        [JsonPropertyName("reasoning_effort")]
        public string? ReasoningEffort { get; set; }
    }

    private sealed class OpenRouterChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public object? Content { get; set; }

        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<OpenRouterToolCall>? ToolCalls { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
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

    // Response parsing uses JsonDocument to preserve provider-specific fields.
}
