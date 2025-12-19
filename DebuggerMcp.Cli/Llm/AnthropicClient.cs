using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Serialization;
using DebuggerMcp.Cli.Shell.Transcript;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Minimal Anthropic client for the Messages API (<c>/v1/messages</c>).
/// </summary>
public sealed class AnthropicClient(HttpClient httpClient, LlmSettings settings)
{
    private static readonly JsonSerializerOptions JsonOptions = CliJsonSerializationDefaults.CaseInsensitiveCamelCaseIgnoreNull;
    private const int DefaultMaxTokens = 4096;
    private const string AnthropicVersionHeaderValue = "2023-06-01";
    private const int MaxErrorBodyBytes = 32_000;

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly LlmSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public async Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var result = await ChatCompletionAsync(
            new ChatCompletionRequest
            {
                Messages = messages,
                ReasoningEffort = _settings.AnthropicReasoningEffort
            },
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result.Text))
        {
            throw new InvalidOperationException("Anthropic response did not contain any message content.");
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

        var apiKey = _settings.GetEffectiveAnthropicApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Anthropic API key is not configured.");
        }

        var baseUrl = _settings.AnthropicBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Anthropic base URL is not configured.");
        }

        var url = $"{baseUrl}/messages";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.UserAgent.ParseAdd("DebuggerMcp.Cli/1.0");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpRequest.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        httpRequest.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersionHeaderValue);

        var (systemPrompt, messages) = ToAnthropicMessages(completionRequest.Messages);
        var disableTools = string.Equals((completionRequest.ToolChoice?.Mode ?? string.Empty).Trim(), "none", StringComparison.OrdinalIgnoreCase);

        var tools = disableTools
            ? null
            : completionRequest.Tools?
                .Select(ToAnthropicTool)
                .Where(t => t != null)
                .Select(t => t!)
                .ToList();

        if (tools is { Count: 0 })
        {
            tools = null;
        }

        var toolChoice = disableTools ? null : ToAnthropicToolChoice(completionRequest.ToolChoice);
        if (tools == null)
        {
            toolChoice = null;
        }
        var maxTokens = completionRequest.MaxTokens.GetValueOrDefault(DefaultMaxTokens);

        var payload = new AnthropicMessagesRequest
        {
            Model = _settings.AnthropicModel,
            MaxTokens = maxTokens <= 0 ? DefaultMaxTokens : maxTokens,
            System = systemPrompt,
            Messages = messages,
            Tools = tools,
            ToolChoice = toolChoice,
            Thinking = ToAnthropicThinking(completionRequest.ReasoningEffort, maxTokens)
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var (errorBody, truncated) = await ReadContentCappedAsync(response.Content, MaxErrorBodyBytes, cancellationToken).ConfigureAwait(false);
            errorBody = TranscriptRedactor.RedactText(errorBody);
            if (truncated)
            {
                errorBody += $"{Environment.NewLine}... (truncated) ...";
            }

            throw new HttpRequestException($"Anthropic request failed ({(int)response.StatusCode}): {errorBody}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var model = root.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String
                ? modelProp.GetString()
                : null;

            if (!root.TryGetProperty("content", out var contentProp))
            {
                return new ChatCompletionResult
                {
                    Model = model ?? _settings.AnthropicModel,
                    Text = null,
                    ToolCalls = []
                };
            }

            var toolCalls = ParseToolUses(contentProp);

            JsonElement? rawContent = null;
            string? text = null;
            if (contentProp.ValueKind != JsonValueKind.Undefined)
            {
                rawContent = contentProp.Clone();
                text = OpenRouterClient.ExtractText(contentProp);
            }

            return new ChatCompletionResult
            {
                Model = model ?? _settings.AnthropicModel,
                Text = text,
                RawMessageContent = rawContent,
                ProviderMessageFields = null,
                ToolCalls = toolCalls
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Failed to parse Anthropic response JSON.", ex);
        }
    }

    private static IReadOnlyList<ChatToolCall> ParseToolUses(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var calls = new List<ChatToolCall>();
        foreach (var item in content.EnumerateArray())
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

            var input = item.TryGetProperty("input", out var inputProp) ? inputProp.GetRawText() : "{}";
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
            {
                calls.Add(new ChatToolCall(id, name, input));
            }
        }

        return calls;
    }

    private static (string? SystemPrompt, List<AnthropicMessage> Messages) ToAnthropicMessages(IReadOnlyList<ChatMessage> messages)
    {
        var systemParts = new List<string>();
        var result = new List<AnthropicMessage>();

        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            var role = NormalizeRole(msg.Role);

            if (role == "system")
            {
                if (!string.IsNullOrWhiteSpace(msg.Content))
                {
                    systemParts.Add(msg.Content.Trim());
                }
                continue;
            }

            if (role == "tool")
            {
                var blocks = new List<object>();
                while (i < messages.Count && NormalizeRole(messages[i].Role) == "tool")
                {
                    var toolMsg = messages[i];
                    var toolCallId = toolMsg.ToolCallId ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(toolCallId))
                    {
                        blocks.Add(new
                        {
                            type = "tool_result",
                            tool_use_id = toolCallId,
                            content = toolMsg.Content
                        });
                    }
                    else if (!string.IsNullOrWhiteSpace(toolMsg.Content))
                    {
                        blocks.Add(new { type = "text", text = toolMsg.Content });
                    }

                    i++;
                }

                i--; // compensate for outer loop increment

                if (blocks.Count > 0)
                {
                    result.Add(new AnthropicMessage { Role = "user", Content = blocks });
                }

                continue;
            }

            if (role is not ("user" or "assistant"))
            {
                role = "user";
            }

            var content = ToAnthropicContent(msg, role);
            if (content == null)
            {
                continue;
            }

            result.Add(new AnthropicMessage { Role = role, Content = content });
        }

        var system = systemParts.Count == 0 ? null : string.Join($"{Environment.NewLine}{Environment.NewLine}", systemParts).Trim();
        return (string.IsNullOrWhiteSpace(system) ? null : system, result);
    }

    private static object? ToAnthropicContent(ChatMessage message, string role)
    {
        if (message.ContentJson.HasValue)
        {
            var contentJson = message.ContentJson.Value;
            if (contentJson.ValueKind == JsonValueKind.Array)
            {
                if (role == "assistant" && message.ToolCalls is { Count: > 0 })
                {
                    return MergeToolUseBlocks(contentJson, message.ToolCalls);
                }

                return contentJson.Clone();
            }

            if (contentJson.ValueKind == JsonValueKind.String)
            {
                var s = contentJson.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }

            if (contentJson.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                // fall back to string/tool blocks below
            }
            else
            {
                // Anthropic expects string or array; preserve unknown shapes as JSON text.
                var raw = contentJson.GetRawText();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return raw;
                }
            }
        }

        if (role == "assistant" && message.ToolCalls is { Count: > 0 })
        {
            var blocks = new List<object>();
            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                blocks.Add(new { type = "text", text = message.Content });
            }

            foreach (var call in message.ToolCalls)
            {
                blocks.Add(new
                {
                    type = "tool_use",
                    id = call.Id,
                    name = call.Name,
                    input = ParseJsonOrFallback(call.ArgumentsJson)
                });
            }

            return blocks;
        }

        return string.IsNullOrWhiteSpace(message.Content) ? null : message.Content;
    }

    private static object MergeToolUseBlocks(JsonElement existingArray, IReadOnlyList<ChatToolCall> toolCalls)
    {
        var blocks = new List<object>();
        var existingToolUseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in existingArray.EnumerateArray())
        {
            blocks.Add(item.Clone());

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = item.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                ? typeProp.GetString()
                : null;

            if (!string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            {
                var id = idProp.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    existingToolUseIds.Add(id);
                }
            }
        }

        foreach (var call in toolCalls)
        {
            if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name))
            {
                continue;
            }

            if (existingToolUseIds.Contains(call.Id))
            {
                continue;
            }

            blocks.Add(new
            {
                type = "tool_use",
                id = call.Id,
                name = call.Name,
                input = ParseJsonOrFallback(call.ArgumentsJson)
            });
        }

        return blocks;
    }

    private static object ParseJsonOrFallback(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new { };
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return new { raw = json };
        }
    }

    private static string NormalizeRole(string? role)
        => string.IsNullOrWhiteSpace(role) ? "user" : role.Trim().ToLowerInvariant();

    private static AnthropicTool? ToAnthropicTool(ChatTool tool)
        => tool == null || string.IsNullOrWhiteSpace(tool.Name)
            ? null
            : new AnthropicTool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = tool.Parameters
            };

    private static AnthropicToolChoice? ToAnthropicToolChoice(ChatToolChoice? choice)
    {
        if (choice == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(choice.FunctionName))
        {
            return new AnthropicToolChoice { Type = "tool", Name = choice.FunctionName };
        }

        var mode = string.IsNullOrWhiteSpace(choice.Mode) ? "auto" : choice.Mode.Trim().ToLowerInvariant();
        return mode switch
        {
            "auto" => new AnthropicToolChoice { Type = "auto" },
            "required" => new AnthropicToolChoice { Type = "any" },
            _ => new AnthropicToolChoice { Type = "auto" }
        };
    }

    private static AnthropicThinking? ToAnthropicThinking(string? reasoningEffort, int maxTokens)
    {
        var effort = LlmSettings.NormalizeReasoningEffort(reasoningEffort);
        if (effort == null)
        {
            return null;
        }

        var budget = effort switch
        {
            "low" => 512,
            "medium" => 1024,
            "high" => 2048,
            _ => 0
        };

        if (budget <= 0)
        {
            return null;
        }

        // Keep budget within the max token limit to avoid provider rejections.
        maxTokens = maxTokens <= 0 ? DefaultMaxTokens : maxTokens;
        var budgetCeiling = maxTokens <= 1 ? 1 : maxTokens - 1;
        budget = Math.Min(budget, budgetCeiling);

        return new AnthropicThinking { Type = "enabled", BudgetTokens = budget };
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
            return (string.Empty, false);
        }
    }

    private static string DecodeUtf8Prefix(byte[] buffer, int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

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

    private sealed class AnthropicMessagesRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; }

        [JsonPropertyName("system")]
        public string? System { get; set; }

        [JsonPropertyName("messages")]
        public List<AnthropicMessage> Messages { get; set; } = [];

        [JsonPropertyName("tools")]
        public List<AnthropicTool>? Tools { get; set; }

        [JsonPropertyName("tool_choice")]
        public AnthropicToolChoice? ToolChoice { get; set; }

        [JsonPropertyName("thinking")]
        public AnthropicThinking? Thinking { get; set; }
    }

    private sealed class AnthropicMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public object? Content { get; set; }
    }

    private sealed class AnthropicTool
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("input_schema")]
        public JsonElement InputSchema { get; set; }
    }

    private sealed class AnthropicToolChoice
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "auto";

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class AnthropicThinking
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "enabled";

        [JsonPropertyName("budget_tokens")]
        public int BudgetTokens { get; set; }
    }
}
