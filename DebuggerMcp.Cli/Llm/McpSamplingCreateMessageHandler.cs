using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DebuggerMcp.Cli.Configuration;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Handles server-initiated MCP sampling requests by calling the configured LLM provider.
/// </summary>
internal sealed class McpSamplingCreateMessageHandler(
    LlmSettings settings,
    Func<ChatCompletionRequest, CancellationToken, Task<ChatCompletionResult>> complete,
    Action<string>? progress = null)
{
    private readonly LlmSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly Func<ChatCompletionRequest, CancellationToken, Task<ChatCompletionResult>> _complete =
        complete ?? throw new ArgumentNullException(nameof(complete));
    private readonly Action<string>? _progress = progress;

    private int _lastSeenMessageCount;

    public async Task<SamplingCreateMessageResult> HandleAsync(JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (parameters == null || parameters.Value.ValueKind == JsonValueKind.Undefined || parameters.Value.ValueKind == JsonValueKind.Null)
        {
            throw new ArgumentException("sampling/createMessage params are required.", nameof(parameters));
        }

        _settings.ApplyEnvironmentOverrides();

        EmitProgressForNewToolResults(parameters.Value);

        var systemPrompt = TryGetString(parameters.Value, "systemPrompt") ?? TryGetString(parameters.Value, "SystemPrompt");
        var request = BuildChatCompletionRequest(systemPrompt, parameters.Value);

        var response = await _complete(request, cancellationToken).ConfigureAwait(false);
        response = ApplyMcpToolUseFallback(response);

        if (string.IsNullOrWhiteSpace(response.Text) && (response.ToolCalls == null || response.ToolCalls.Count == 0))
        {
            throw new InvalidOperationException("LLM returned empty content and no tool calls.");
        }

        EmitProgressForRequestedTools(response);

        return new SamplingCreateMessageResult
        {
            Role = "assistant",
            Model = response.Model ?? _settings.GetEffectiveModel(),
            Content = BuildSamplingContentBlocks(response)
        };
    }

    private static ChatCompletionResult ApplyMcpToolUseFallback(ChatCompletionResult response)
    {
        if (response.ToolCalls.Count > 0)
        {
            return response;
        }

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            return response;
        }

        if (!TryParseMcpToolUsesFromText(response.Text, out var calls, out var cleanedText) || calls.Count == 0)
        {
            return response;
        }

        return new ChatCompletionResult
        {
            Model = response.Model,
            Text = string.IsNullOrWhiteSpace(cleanedText) ? null : cleanedText,
            ToolCalls = calls
        };
    }

    private static bool TryParseMcpToolUsesFromText(string text, out List<ChatToolCall> toolCalls, out string? cleanedText)
    {
        toolCalls = [];
        cleanedText = text;

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (!trimmed.Contains("tool_use", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // First try: the entire text is a JSON value.
        if (TryExtractToolCallsFromJson(trimmed, toolCalls, out cleanedText))
        {
            return toolCalls.Count > 0;
        }

        // Second try: the tool_use JSON object is embedded somewhere in the text.
        var matches = ExtractEmbeddedToolUseJsonObjects(text);
        if (matches.Count == 0)
        {
            return false;
        }

        foreach (var m in matches)
        {
            if (TryExtractToolCallsFromJson(m.Json, toolCalls, out _))
            {
                continue;
            }
        }

        if (toolCalls.Count == 0)
        {
            return false;
        }

        // Remove extracted JSON objects from the textual content to reduce model confusion in later turns.
        cleanedText = RemoveRanges(text, matches.Select(m => (m.Start, m.Length)).ToList());
        return true;
    }

    private static bool TryExtractToolCallsFromJson(string json, List<ChatToolCall> toolCalls, out string? cleanedText)
    {
        cleanedText = null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return TryExtractToolCallsFromJsonElement(doc.RootElement, toolCalls, ref cleanedText);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractToolCallsFromJsonElement(JsonElement element, List<ChatToolCall> toolCalls, ref string? cleanedText)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var type = TryGetString(element, "type") ?? string.Empty;
            if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                var id = TryGetString(element, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = $"text_tool_{Guid.NewGuid():N}";
                }

                var name = TryGetString(element, "name") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                {
                    return false;
                }

                var input = TryGetProperty(element, "input", out var inputProp)
                    ? inputProp.GetRawText()
                    : "{}";

                toolCalls.Add(new ChatToolCall(id, name, input));
                cleanedText ??= string.Empty;
                return true;
            }

            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                // Some models echo MCP blocks as { "type":"text", "text":"{...tool_use...}" }. Try to unwrap.
                var inner = TryGetString(element, "text");
                if (!string.IsNullOrWhiteSpace(inner) && inner!.Contains("tool_use", StringComparison.OrdinalIgnoreCase))
                {
                    return TryExtractToolCallsFromJson(inner.Trim(), toolCalls, out cleanedText);
                }
            }

            return false;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var any = false;
            foreach (var item in element.EnumerateArray())
            {
                any |= TryExtractToolCallsFromJsonElement(item, toolCalls, ref cleanedText);
            }
            if (any)
            {
                cleanedText ??= string.Empty;
            }
            return any;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var s = element.GetString();
            if (!string.IsNullOrWhiteSpace(s) && s!.Contains("tool_use", StringComparison.OrdinalIgnoreCase))
            {
                return TryExtractToolCallsFromJson(s.Trim(), toolCalls, out cleanedText);
            }
        }

        return false;
    }

    private sealed record ExtractedJsonObject(int Start, int Length, string Json);

    private static List<ExtractedJsonObject> ExtractEmbeddedToolUseJsonObjects(string text)
    {
        var matches = new List<ExtractedJsonObject>();

        var idx = 0;
        while (idx < text.Length)
        {
            var marker = text.IndexOf("tool_use", idx, StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                break;
            }

            var start = text.LastIndexOf('{', marker);
            if (start < 0)
            {
                idx = marker + 1;
                continue;
            }

            if (!TryExtractBalancedJsonObject(text, start, out var endExclusive))
            {
                idx = marker + 1;
                continue;
            }

            var json = text.Substring(start, endExclusive - start);
            matches.Add(new ExtractedJsonObject(start, endExclusive - start, json));
            idx = endExclusive;
        }

        // De-dupe identical ranges if the loop found nested markers inside the same object.
        return matches
            .GroupBy(m => (m.Start, m.Length))
            .Select(g => g.First())
            .OrderBy(m => m.Start)
            .ToList();
    }

    private static bool TryExtractBalancedJsonObject(string text, int startIndex, out int endExclusive)
    {
        endExclusive = -1;

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = startIndex; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '{')
            {
                depth++;
                continue;
            }

            if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    endExclusive = i + 1;
                    return true;
                }
            }
        }

        return false;
    }

    private static string RemoveRanges(string text, List<(int Start, int Length)> ranges)
    {
        if (ranges.Count == 0)
        {
            return text;
        }

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var sb = new StringBuilder(text.Length);
        var last = 0;

        foreach (var r in ranges)
        {
            if (r.Start < last)
            {
                continue;
            }

            sb.Append(text, last, r.Start - last);
            last = Math.Min(text.Length, r.Start + r.Length);
        }

        if (last < text.Length)
        {
            sb.Append(text, last, text.Length - last);
        }

        return sb.ToString().Trim();
    }

    private List<SamplingContentItem> BuildSamplingContentBlocks(ChatCompletionResult response)
    {
        // Prefer preserving raw provider content blocks when available, especially for providers that
        // require "reasoning/thought_signature" blocks to be echoed back verbatim across tool turns.
        if (_settings.GetProviderKind() is LlmProviderKind.OpenRouter or LlmProviderKind.Anthropic &&
            response.RawMessageContent.HasValue &&
            response.RawMessageContent.Value.ValueKind == JsonValueKind.Array)
        {
            var blocks = BuildSamplingContentBlocksFromRawArray(response.RawMessageContent.Value);

            // If we failed to recover any blocks, fall back to the normalized representation.
            if (blocks.Count > 0)
            {
                // Ensure any tool calls that weren't encoded as tool_use blocks are still emitted.
                var existingToolUseIds = new HashSet<string>(
                    blocks.Where(b => string.Equals(b.Type, "tool_use", StringComparison.OrdinalIgnoreCase))
                          .Select(b => b.Id ?? string.Empty),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var call in response.ToolCalls)
                {
                    if (existingToolUseIds.Add(call.Id))
                    {
                        blocks.Add(new SamplingContentItem
                        {
                            Type = "tool_use",
                            Id = call.Id,
                            Name = call.Name,
                            Input = ParseToolArguments(call.ArgumentsJson)
                        });
                    }
                }

                // Preserve plain text (if any) even when the provider omitted explicit text blocks.
                if (!string.IsNullOrWhiteSpace(response.Text) &&
                    blocks.All(b => !string.Equals(b.Type, "text", StringComparison.OrdinalIgnoreCase)))
                {
                    blocks.Insert(0, new SamplingContentItem { Type = "text", Text = response.Text!.TrimEnd() });
                }

                return blocks;
            }
        }

        return BuildSamplingContentBlocksNormalized(response);
    }

    private static List<SamplingContentItem> BuildSamplingContentBlocksNormalized(ChatCompletionResult response)
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

    private static List<SamplingContentItem> BuildSamplingContentBlocksFromRawArray(JsonElement rawArray)
    {
        var blocks = new List<SamplingContentItem>();

        foreach (var item in rawArray.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    blocks.Add(new SamplingContentItem { Type = "text", Text = s!.TrimEnd() });
                }
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = TryGetString(item, "type") ?? string.Empty;
            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                var text = TryGetString(item, "text") ?? string.Empty;
                var block = new SamplingContentItem { Type = "text", Text = text.TrimEnd() };
                CopyExtensionData(item, block, excludedKeys: ["type", "text"]);
                blocks.Add(block);
                continue;
            }

            if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                var block = new SamplingContentItem
                {
                    Type = "tool_use",
                    Id = TryGetString(item, "id"),
                    Name = TryGetString(item, "name"),
                    Input = TryGetProperty(item, "input", out var inputProp) ? inputProp.Clone() : null
                };
                CopyExtensionData(item, block, excludedKeys: ["type", "id", "name", "input", "text"]);
                blocks.Add(block);
                continue;
            }

            // Unknown provider-specific blocks: preserve as compact JSON in a text block.
            blocks.Add(new SamplingContentItem { Type = "text", Text = item.GetRawText() });
        }

        return blocks;
    }

    private static void CopyExtensionData(JsonElement obj, SamplingContentItem target, string[] excludedKeys)
    {
        try
        {
            Dictionary<string, JsonElement>? ext = null;
            foreach (var prop in obj.EnumerateObject())
            {
                if (excludedKeys.Any(k => string.Equals(k, prop.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                ext ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                ext[prop.Name] = prop.Value.Clone();
            }

            if (ext is { Count: > 0 })
            {
                target.ExtensionData = ext;
            }
        }
        catch
        {
            // best-effort
        }
    }

    private void EmitProgressForNewToolResults(JsonElement parameters)
    {
        if (_progress == null)
        {
            return;
        }

        if (!TryGetProperty(parameters, "messages", out var messagesProp) || messagesProp.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var toolUseLookup = BuildToolUseLookup(messagesProp);
        var messageCount = messagesProp.GetArrayLength();
        var startIndex = _lastSeenMessageCount;
        if (startIndex < 0 || startIndex > messageCount)
        {
            startIndex = 0;
        }

        // If the server resets the conversation, the messages array can shrink; treat as a new stream.
        if (messageCount < _lastSeenMessageCount)
        {
            startIndex = 0;
        }

        var index = 0;
        foreach (var msg in messagesProp.EnumerateArray())
        {
            if (index++ < startIndex)
            {
                continue;
            }

            if (msg.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var blocks = ExtractContentBlocks(msg);
            foreach (var toolResult in blocks.ToolResults)
            {
                var name = toolUseLookup.TryGetValue(toolResult.ToolCallId, out var n) ? n : "tool";
                var summary = CompactOneLine(toolResult.Content, 160);
                if (string.IsNullOrWhiteSpace(summary))
                {
                    summary = "(no output)";
                }

                _progress($"AI tool result: {name} -> {summary}");
            }
        }

        _lastSeenMessageCount = messageCount;
    }

    private void EmitProgressForRequestedTools(ChatCompletionResult response)
    {
        if (_progress == null)
        {
            return;
        }

        if (response.ToolCalls == null || response.ToolCalls.Count == 0)
        {
            return;
        }

        // Tool call IDs are not guaranteed to be globally unique across separate sampling requests.
        // Only de-dup within the current response to keep the output readable.
        var seenThisResponse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var call in response.ToolCalls)
        {
            if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name))
            {
                continue;
            }

            if (!seenThisResponse.Add(call.Id))
            {
                continue;
            }

            var summary = SummarizeToolCall(call);
            _progress(string.IsNullOrWhiteSpace(summary)
                ? $"AI requests tool: {call.Name}"
                : $"AI requests tool: {call.Name} ({summary})");
        }
    }

    private static Dictionary<string, string> BuildToolUseLookup(JsonElement messagesProp)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var msg in messagesProp.EnumerateArray())
        {
            if (msg.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var role = NormalizeRole(TryGetString(msg, "role") ?? "user");
            if (!string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var blocks = ExtractContentBlocks(msg);
            var openAiCalls = ParseOpenAiToolCalls(msg);

            foreach (var call in blocks.ToolCalls)
            {
                if (!string.IsNullOrWhiteSpace(call.Id) && !string.IsNullOrWhiteSpace(call.Name))
                {
                    map[call.Id] = call.Name;
                }
            }

            foreach (var call in openAiCalls)
            {
                if (!string.IsNullOrWhiteSpace(call.Id) && !string.IsNullOrWhiteSpace(call.Name))
                {
                    map[call.Id] = call.Name;
                }
            }
        }

        return map;
    }

    private static string SummarizeToolCall(ChatToolCall call)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(call.ArgumentsJson))
            {
                using var doc = JsonDocument.Parse(call.ArgumentsJson);
                var args = doc.RootElement;

                if (string.Equals(call.Name, "exec", StringComparison.OrdinalIgnoreCase) &&
                    args.ValueKind == JsonValueKind.Object &&
                    TryGetProperty(args, "command", out var cmd) &&
                    cmd.ValueKind == JsonValueKind.String)
                {
                    return CompactOneLine(cmd.GetString() ?? string.Empty, 160);
                }

                if (string.Equals(call.Name, "inspect", StringComparison.OrdinalIgnoreCase) &&
                    args.ValueKind == JsonValueKind.Object &&
                    TryGetProperty(args, "address", out var addr) &&
                    addr.ValueKind == JsonValueKind.String)
                {
                    return $"address={CompactOneLine(addr.GetString() ?? string.Empty, 80)}";
                }

                if (string.Equals(call.Name, "get_thread_stack", StringComparison.OrdinalIgnoreCase) &&
                    args.ValueKind == JsonValueKind.Object &&
                    TryGetProperty(args, "threadId", out var tid) &&
                    tid.ValueKind == JsonValueKind.String)
                {
                    return $"threadId={CompactOneLine(tid.GetString() ?? string.Empty, 40)}";
                }

                if (string.Equals(call.Name, "analysis_complete", StringComparison.OrdinalIgnoreCase) &&
                    args.ValueKind == JsonValueKind.Object &&
                    TryGetProperty(args, "rootCause", out var rc) &&
                    rc.ValueKind == JsonValueKind.String)
                {
                    return $"rootCause={CompactOneLine(rc.GetString() ?? string.Empty, 120)}";
                }

                return CompactOneLine(call.ArgumentsJson, 160);
            }
        }
        catch
        {
            // Ignore; fall through.
        }

        return string.Empty;
    }

    private static string CompactOneLine(string value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var s = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        while (s.Contains("  ", StringComparison.Ordinal))
        {
            s = s.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (maxLen > 0 && s.Length > maxLen)
        {
            return s.Substring(0, maxLen) + "...";
        }

        return s;
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

    private ChatCompletionRequest BuildChatCompletionRequest(string? systemPrompt, JsonElement parameters)
    {
        var preserveMcpContentBlocks = _settings.GetProviderKind() is LlmProviderKind.OpenRouter or LlmProviderKind.Anthropic;
        var messages = BuildChatMessages(systemPrompt, parameters, preserveMcpContentBlocks);
        var tools = ParseTools(parameters);
        var toolChoice = ParseToolChoice(parameters);
        var maxTokens = ParseMaxTokens(parameters);
        var reasoningEffort = GetEffectiveReasoningEffort(parameters);

        return new ChatCompletionRequest
        {
            Messages = messages,
            Tools = tools,
            ToolChoice = toolChoice,
            MaxTokens = maxTokens,
            ReasoningEffort = reasoningEffort
        };
    }

    private string? GetEffectiveReasoningEffort(JsonElement parameters)
    {
        var parsed = ParseReasoningEffort(parameters);
        return parsed.Status switch
        {
            ReasoningEffortParseStatus.NotSpecified => _settings.GetEffectiveReasoningEffort(),
            ReasoningEffortParseStatus.InvalidSpecified => _settings.GetEffectiveReasoningEffort(),
            ReasoningEffortParseStatus.ExplicitClear => null,
            ReasoningEffortParseStatus.ValidValue => parsed.Value,
            _ => _settings.GetEffectiveReasoningEffort()
        };
    }

    private sealed record ReasoningEffortParseResult(string? Value, ReasoningEffortParseStatus Status)
    {
        public static ReasoningEffortParseResult NotSpecified => new(null, ReasoningEffortParseStatus.NotSpecified);
        public static ReasoningEffortParseResult InvalidSpecified => new(null, ReasoningEffortParseStatus.InvalidSpecified);
        public static ReasoningEffortParseResult ExplicitClear => new(null, ReasoningEffortParseStatus.ExplicitClear);
        public static ReasoningEffortParseResult ValidValue(string value) => new(value, ReasoningEffortParseStatus.ValidValue);
    }

    private enum ReasoningEffortParseStatus
    {
        NotSpecified = 0,
        InvalidSpecified = 1,
        ExplicitClear = 2,
        ValidValue = 3
    }

    private static ReasoningEffortParseResult ParseReasoningEffort(JsonElement parameters)
    {
        if (TryGetProperty(parameters, "reasoningEffort", out var v) ||
            TryGetProperty(parameters, "reasoning_effort", out v))
        {
            return ParseReasoningEffortValue(v);
        }

        if (TryGetProperty(parameters, "reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object)
        {
            var effort = TryGetString(reasoning, "effort");
            if (string.IsNullOrWhiteSpace(effort))
            {
                return ReasoningEffortParseResult.InvalidSpecified;
            }

            return ParseReasoningEffortString(effort);
        }

        return ReasoningEffortParseResult.NotSpecified;
    }

    private static ReasoningEffortParseResult ParseReasoningEffortValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return ReasoningEffortParseResult.InvalidSpecified;
        }

        var s = value.GetString();
        if (string.IsNullOrWhiteSpace(s))
        {
            return ReasoningEffortParseResult.InvalidSpecified;
        }

        return ParseReasoningEffortString(s);
    }

    private static ReasoningEffortParseResult ParseReasoningEffortString(string value)
    {
        if (LlmSettings.IsReasoningEffortUnsetToken(value))
        {
            return ReasoningEffortParseResult.ExplicitClear;
        }

        var normalized = LlmSettings.NormalizeReasoningEffort(value);
        if (normalized != null)
        {
            return ReasoningEffortParseResult.ValidValue(normalized);
        }

        return ReasoningEffortParseResult.InvalidSpecified;
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

    private static List<ChatMessage> BuildChatMessages(string? systemPrompt, JsonElement parameters, bool preserveMcpContentBlocks)
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
            if (preserveMcpContentBlocks && role is "user" or "assistant")
            {
                if (TryGetProperty(msg, "content", out var contentProp) &&
                    contentProp.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                {
                    // Preserve raw MCP/Anthropic-style content blocks verbatim so providers that require
                    // thought signatures (e.g., Gemini tool calls) continue to work across iterations.
                    var messageBlocks = ExtractContentBlocks(msg);
                    result.Add(new ChatMessage(
                        role,
                        messageBlocks.Text,
                        toolCallId: null,
                        toolCalls: null,
                        contentJson: contentProp.Clone(),
                        providerMessageFields: null));
                    continue;
                }
            }

            var parsed = ExtractContentBlocks(msg);
            var openAiToolCalls = role == "assistant"
                ? ParseOpenAiToolCalls(msg)
                : [];

            if (openAiToolCalls.Count > 0)
            {
                // Merge, preserving order; avoid duplicate ids.
                var seen = new HashSet<string>(parsed.ToolCalls.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
                foreach (var call in openAiToolCalls)
                {
                    if (seen.Add(call.Id))
                    {
                        parsed.ToolCalls.Add(call);
                    }
                }
            }

            if (parsed.Text.Length == 0 && parsed.ToolCalls.Count == 0 && parsed.ToolResults.Count == 0)
            {
                continue;
            }

            if (parsed.ToolCalls.Count > 0 && role == "assistant")
            {
                result.Add(new ChatMessage("assistant", parsed.Text, toolCallId: null, toolCalls: parsed.ToolCalls));
            }
            else if (role == "tool")
            {
                var toolCallId =
                    TryGetString(msg, "tool_call_id") ??
                    TryGetString(msg, "toolCallId");

                if (!string.IsNullOrWhiteSpace(parsed.Text))
                {
                    if (!string.IsNullOrWhiteSpace(toolCallId))
                    {
                        result.Add(new ChatMessage("tool", parsed.Text, toolCallId: toolCallId, toolCalls: null));
                    }
                    else
                    {
                        // OpenAI-compatible APIs require tool_call_id for role=tool; preserve the content as a user message instead.
                        result.Add(new ChatMessage("user", $"[tool output missing tool_call_id]{Environment.NewLine}{parsed.Text}"));
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(parsed.Text))
            {
                result.Add(new ChatMessage(role, parsed.Text));
            }

            foreach (var toolResult in parsed.ToolResults)
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

        var sb = new StringBuilder();
        var toolCalls = new List<ChatToolCall>();
        var toolResults = new List<ToolResult>();

        if (contentProp.ValueKind == JsonValueKind.Object)
        {
            ExtractContentItem(contentProp, sb, toolCalls, toolResults);
            return new ParsedBlocks(sb.ToString().Trim(), toolCalls, toolResults);
        }

        if (contentProp.ValueKind != JsonValueKind.Array)
        {
            // Preserve unknown shapes as text so the model sees it.
            return new ParsedBlocks(contentProp.GetRawText(), [], []);
        }

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
            ExtractContentItem(item, sb, toolCalls, toolResults);
        }

        return new ParsedBlocks(sb.ToString().Trim(), toolCalls, toolResults);
    }

    private static void ExtractContentItem(
        JsonElement item,
        StringBuilder textBuffer,
        List<ChatToolCall> toolCalls,
        List<ToolResult> toolResults)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = TryGetString(item, "type") ?? string.Empty;
        if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
        {
            AppendBlock(textBuffer, TryGetString(item, "text"));
            return;
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
            return;
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
            return;
        }

        // Unknown content item: preserve as compact JSON so the model still sees it.
        AppendBlock(textBuffer, item.GetRawText());
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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
