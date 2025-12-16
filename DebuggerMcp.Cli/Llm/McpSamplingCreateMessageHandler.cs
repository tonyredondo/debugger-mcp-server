using System.Text;
using System.Text.Json;
using DebuggerMcp.Cli.Configuration;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Handles server-initiated MCP sampling requests by calling the configured OpenRouter model.
/// </summary>
internal sealed class McpSamplingCreateMessageHandler(
    LlmSettings settings,
    Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>> chat)
{
    private readonly LlmSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>> _chat = chat ?? throw new ArgumentNullException(nameof(chat));

    public async Task<SamplingCreateMessageResult> HandleAsync(JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (parameters == null || parameters.Value.ValueKind == JsonValueKind.Undefined || parameters.Value.ValueKind == JsonValueKind.Null)
        {
            throw new ArgumentException("sampling/createMessage params are required.", nameof(parameters));
        }

        _settings.ApplyEnvironmentOverrides();

        var systemPrompt = TryGetString(parameters.Value, "systemPrompt") ?? TryGetString(parameters.Value, "SystemPrompt");
        var messages = BuildChatMessages(systemPrompt, parameters.Value);

        var responseText = await _chat(messages, cancellationToken).ConfigureAwait(false);

        return new SamplingCreateMessageResult
        {
            Role = "assistant",
            Model = _settings.OpenRouterModel,
            Content =
            [
                new SamplingContentItem
                {
                    Type = "text",
                    Text = responseText
                }
            ]
        };
    }

    private static List<ChatMessage> BuildChatMessages(string? systemPrompt, JsonElement parameters)
    {
        var result = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            result.Add(new ChatMessage("system", systemPrompt.Trim()));
        }

        if (!TryGetProperty(parameters, "messages", out var messagesProp) || messagesProp.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var msg in messagesProp.EnumerateArray())
        {
            if (msg.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var role = TryGetString(msg, "role") ?? "user";
            var contentText = ExtractContentText(msg);
            if (string.IsNullOrWhiteSpace(contentText))
            {
                continue;
            }

            result.Add(new ChatMessage(NormalizeRole(role), contentText));
        }

        return result;
    }

    private static string NormalizeRole(string role)
    {
        var r = role.Trim().ToLowerInvariant();
        return r switch
        {
            "system" => "system",
            "assistant" => "assistant",
            _ => "user"
        };
    }

    private static string ExtractContentText(JsonElement messageObject)
    {
        if (!TryGetProperty(messageObject, "content", out var contentProp))
        {
            return string.Empty;
        }

        return ExtractContentTextFromElement(contentProp);
    }

    private static string ExtractContentTextFromElement(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString() ?? string.Empty;

            case JsonValueKind.Array:
            {
                var sb = new StringBuilder();
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        AppendBlock(sb, item.GetString());
                        continue;
                    }

                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var type = TryGetString(item, "type") ?? string.Empty;
                    var text = TryGetString(item, "text");

                    if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                    {
                        AppendBlock(sb, text);
                        continue;
                    }

                    // Unknown content item: preserve as compact JSON so the model still sees it.
                    AppendBlock(sb, item.GetRawText());
                }
                return sb.ToString().Trim();
            }

            case JsonValueKind.Object:
                // Some callers may send a single content object instead of an array.
                if (TryGetString(content, "text") is { Length: > 0 } t)
                {
                    return t;
                }
                return content.GetRawText();

            default:
                return string.Empty;
        }
    }

    private static void AppendBlock(StringBuilder sb, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (sb.Length > 0)
        {
            sb.AppendLine();
        }

        sb.Append(text.TrimEnd());
    }

    private static string? TryGetString(JsonElement obj, string name)
        => TryGetProperty(obj, name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

/// <summary>
/// Result shape for MCP sampling/createMessage.
/// </summary>
internal sealed class SamplingCreateMessageResult
{
    public string Role { get; set; } = "assistant";

    public string? Model { get; set; }

    public List<SamplingContentItem> Content { get; set; } = [];
}

/// <summary>
/// Content item in a sampling message result.
/// </summary>
internal sealed class SamplingContentItem
{
    public string Type { get; set; } = "text";

    public string? Text { get; set; }
}

