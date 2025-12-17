using System.Text;
using System.Text.Json;
using DebuggerMcp.Cli.Configuration;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Handles server-initiated MCP sampling requests by calling the configured OpenRouter model.
/// </summary>
internal sealed class McpSamplingCreateMessageHandler(
    LlmSettings settings,
    Func<ChatCompletionRequest, CancellationToken, Task<ChatCompletionResult>> complete)
{
    private readonly LlmSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly Func<ChatCompletionRequest, CancellationToken, Task<ChatCompletionResult>> _complete =
        complete ?? throw new ArgumentNullException(nameof(complete));

    public async Task<SamplingCreateMessageResult> HandleAsync(JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (parameters == null || parameters.Value.ValueKind == JsonValueKind.Undefined || parameters.Value.ValueKind == JsonValueKind.Null)
        {
            throw new ArgumentException("sampling/createMessage params are required.", nameof(parameters));
        }

        _settings.ApplyEnvironmentOverrides();

        var systemPrompt = TryGetString(parameters.Value, "systemPrompt") ?? TryGetString(parameters.Value, "SystemPrompt");
        var request = BuildChatCompletionRequest(systemPrompt, parameters.Value);

        var response = await _complete(request, cancellationToken).ConfigureAwait(false);

        return new SamplingCreateMessageResult
        {
            Role = "assistant",
            Model = response.Model ?? _settings.OpenRouterModel,
            Content = BuildSamplingContentBlocks(response)
        };
    }

    private static List<SamplingContentItem> BuildSamplingContentBlocks(ChatCompletionResult response)
    {
        var blocks = new List<SamplingContentItem>();
        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            blocks.Add(new SamplingContentItem
            {
                Type = "text",
                Text = response.Text!.TrimEnd()
            });
        }

        foreach (var call in response.ToolCalls)
        {
            blocks.Add(new SamplingContentItem
            {
                Type = "tool_use",
                Id = call.Id,
                Name = call.Name,
                Input = ParseToolArguments(call.ArgumentsJson)
            });
        }

        return blocks;
    }

    private static JsonElement ParseToolArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            using var emptyDoc = JsonDocument.Parse("{}");
            return emptyDoc.RootElement.Clone();
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.Clone();
        }
        catch
        {
            // Preserve the raw string in a stable shape.
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["__raw"] = argumentsJson }));
            return doc.RootElement.Clone();
        }
    }

    private static ChatCompletionRequest BuildChatCompletionRequest(string? systemPrompt, JsonElement parameters)
    {
        var messages = BuildChatMessages(systemPrompt, parameters);
        var tools = ParseTools(parameters);
        var toolChoice = ParseToolChoice(parameters);
        var maxTokens = ParseMaxTokens(parameters);

        return new ChatCompletionRequest
        {
            Messages = messages,
            Tools = tools,
            ToolChoice = toolChoice,
            MaxTokens = maxTokens
        };
    }

    private static int? ParseMaxTokens(JsonElement parameters)
    {
        if (TryGetProperty(parameters, "maxTokens", out var v) || TryGetProperty(parameters, "max_tokens", out v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0)
            {
                return n;
            }
        }
        return null;
    }

    private static ChatToolChoice? ParseToolChoice(JsonElement parameters)
    {
        if (!TryGetProperty(parameters, "toolChoice", out var choiceProp) &&
            !TryGetProperty(parameters, "tool_choice", out choiceProp))
        {
            return null;
        }

        if (choiceProp.ValueKind == JsonValueKind.String)
        {
            return new ChatToolChoice { Mode = choiceProp.GetString() ?? "auto" };
        }

        if (choiceProp.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // MCP plan-style: { "mode": "auto" } or { "mode":"tool", "name":"exec" }
        var mode = TryGetString(choiceProp, "mode") ?? TryGetString(choiceProp, "type") ?? "auto";
        var name = TryGetString(choiceProp, "name");

        // OpenAI-style: { "type":"function", "function": { "name":"exec" } }
        if (string.IsNullOrWhiteSpace(name) &&
            TryGetProperty(choiceProp, "function", out var fn) &&
            fn.ValueKind == JsonValueKind.Object)
        {
            name = TryGetString(fn, "name");
        }

        return new ChatToolChoice
        {
            Mode = mode,
            FunctionName = name
        };
    }

    private static List<ChatTool>? ParseTools(JsonElement parameters)
    {
        if (!TryGetProperty(parameters, "tools", out var toolsProp) || toolsProp.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<ChatTool>();
        foreach (var tool in toolsProp.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = TryGetString(tool, "type");
            var openAiFunction = TryGetProperty(tool, "function", out var fn) && fn.ValueKind == JsonValueKind.Object
                ? fn
                : (JsonElement?)null;

            if (!string.IsNullOrWhiteSpace(type) &&
                !string.Equals(type, "function", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name =
                TryGetString(openAiFunction ?? tool, "name") ??
                string.Empty;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var description =
                TryGetString(openAiFunction ?? tool, "description") ??
                TryGetString(tool, "description");

            var schema = GetToolSchema(tool);

            result.Add(new ChatTool
            {
                Name = name,
                Description = description,
                Parameters = schema
            });
        }

        return result.Count == 0 ? null : result;
    }

    private static JsonElement GetToolSchema(JsonElement tool)
    {
        // OpenAI-style: { "type":"function", "function": { "parameters": { ... } } }
        if (TryGetProperty(tool, "function", out var fn) && fn.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(fn, "parameters", out var p) && (p.ValueKind == JsonValueKind.Object || p.ValueKind == JsonValueKind.Array))
            {
                return p.Clone();
            }
        }

        if (TryGetProperty(tool, "inputSchema", out var schema) ||
            TryGetProperty(tool, "input_schema", out schema) ||
            TryGetProperty(tool, "parameters", out schema))
        {
            if (schema.ValueKind == JsonValueKind.Object || schema.ValueKind == JsonValueKind.Array)
            {
                return schema.Clone();
            }
        }

        using var emptyDoc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
        return emptyDoc.RootElement.Clone();
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

            var role = NormalizeRole(TryGetString(msg, "role") ?? "user");
            var messageBlocks = ExtractContentBlocks(msg);
            var openAiToolCalls = role == "assistant"
                ? ParseOpenAiToolCalls(msg)
                : [];

            if (openAiToolCalls.Count > 0)
            {
                // Merge, preserving order; avoid duplicate ids.
                var seen = new HashSet<string>(messageBlocks.ToolCalls.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
                foreach (var call in openAiToolCalls)
                {
                    if (seen.Add(call.Id))
                    {
                        messageBlocks.ToolCalls.Add(call);
                    }
                }
            }

            if (messageBlocks.Text.Length == 0 && messageBlocks.ToolCalls.Count == 0 && messageBlocks.ToolResults.Count == 0)
            {
                continue;
            }

            if (messageBlocks.ToolCalls.Count > 0 && role == "assistant")
            {
                result.Add(new ChatMessage("assistant", messageBlocks.Text, toolCallId: null, toolCalls: messageBlocks.ToolCalls));
            }
            else if (role == "tool")
            {
                var toolCallId =
                    TryGetString(msg, "tool_call_id") ??
                    TryGetString(msg, "toolCallId");

                if (!string.IsNullOrWhiteSpace(messageBlocks.Text))
                {
                    if (!string.IsNullOrWhiteSpace(toolCallId))
                    {
                        result.Add(new ChatMessage("tool", messageBlocks.Text, toolCallId: toolCallId, toolCalls: null));
                    }
                    else
                    {
                        // OpenAI-compatible APIs require tool_call_id for role=tool; preserve the content as a user message instead.
                        result.Add(new ChatMessage("user", $"[tool output missing tool_call_id]{Environment.NewLine}{messageBlocks.Text}"));
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(messageBlocks.Text))
            {
                result.Add(new ChatMessage(role, messageBlocks.Text));
            }

            foreach (var toolResult in messageBlocks.ToolResults)
            {
                result.Add(new ChatMessage("tool", toolResult.Content, toolResult.ToolCallId, toolCalls: null));
            }
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
            "tool" => "tool",
            _ => "user"
        };
    }

    private sealed record ParsedBlocks(string Text, List<ChatToolCall> ToolCalls, List<ToolResult> ToolResults);
    private sealed record ToolResult(string ToolCallId, string Content);

    private static ParsedBlocks ExtractContentBlocks(JsonElement messageObject)
    {
        if (!TryGetProperty(messageObject, "content", out var contentProp))
        {
            return new ParsedBlocks(string.Empty, [], []);
        }

        if (contentProp.ValueKind == JsonValueKind.String)
        {
            var s = contentProp.GetString();
            return new ParsedBlocks(string.IsNullOrWhiteSpace(s) ? string.Empty : s!, [], []);
        }

        if (contentProp.ValueKind == JsonValueKind.Null)
        {
            return new ParsedBlocks(string.Empty, [], []);
        }

        if (contentProp.ValueKind != JsonValueKind.Array)
        {
            // Preserve unknown shapes as text so the model sees it.
            return new ParsedBlocks(contentProp.GetRawText(), [], []);
        }

        var sb = new StringBuilder();
        var toolCalls = new List<ChatToolCall>();
        var toolResults = new List<ToolResult>();

        foreach (var item in contentProp.EnumerateArray())
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
            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                AppendBlock(sb, TryGetString(item, "text"));
                continue;
            }

            if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                var id = TryGetString(item, "id") ?? string.Empty;
                var name = TryGetString(item, "name") ?? string.Empty;
                var input = TryGetProperty(item, "input", out var inputProp) ? inputProp.GetRawText() : "{}";
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                {
                    toolCalls.Add(new ChatToolCall(id, name, input));
                }
                continue;
            }

            if (string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
            {
                var toolUseId =
                    TryGetString(item, "tool_use_id") ??
                    TryGetString(item, "toolUseId") ??
                    TryGetString(item, "toolCallId") ??
                    string.Empty;

                var content = TryGetProperty(item, "content", out var trContent)
                    ? ExtractTextFromContentElement(trContent)
                    : (TryGetString(item, "text") ?? string.Empty);

                if (!string.IsNullOrWhiteSpace(toolUseId))
                {
                    toolResults.Add(new ToolResult(toolUseId, content));
                }
                continue;
            }

            // Unknown content item: preserve as compact JSON so the model still sees it.
            AppendBlock(sb, item.GetRawText());
        }

        return new ParsedBlocks(sb.ToString().Trim(), toolCalls, toolResults);
    }

    private static List<ChatToolCall> ParseOpenAiToolCalls(JsonElement messageObject)
    {
        if (!TryGetProperty(messageObject, "tool_calls", out var toolCallsProp) &&
            !TryGetProperty(messageObject, "toolCalls", out toolCallsProp))
        {
            return [];
        }

        if (toolCallsProp.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<ChatToolCall>();

        foreach (var item in toolCallsProp.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = TryGetString(item, "type");
            if (!string.IsNullOrWhiteSpace(type) &&
                !string.Equals(type, "function", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = TryGetString(item, "id") ?? string.Empty;
            if (TryGetProperty(item, "function", out var fn) && fn.ValueKind == JsonValueKind.Object)
            {
                var name = TryGetString(fn, "name") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var args = "{}";
                if (TryGetProperty(fn, "arguments", out var arguments))
                {
                    args = arguments.ValueKind switch
                    {
                        JsonValueKind.String => arguments.GetString() ?? "{}",
                        JsonValueKind.Object => arguments.GetRawText(),
                        JsonValueKind.Array => arguments.GetRawText(),
                        _ => "{}"
                    };
                }

                result.Add(new ChatToolCall(id, name, args));
            }
        }

        return result;
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

    private static string ExtractTextFromContentElement(JsonElement content)
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

                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var type = TryGetString(item, "type") ?? string.Empty;
                        if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                        {
                            AppendBlock(sb, TryGetString(item, "text"));
                            continue;
                        }
                        AppendBlock(sb, item.GetRawText());
                    }
                }
                return sb.ToString().Trim();
            }
            default:
                return content.GetRawText();
        }
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

    public string? Id { get; set; }

    public string? Name { get; set; }

    public JsonElement? Input { get; set; }
}
