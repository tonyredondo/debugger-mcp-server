using DebuggerMcp.Cli.Shell.Transcript;
using System.Buffers;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Runs an iterative tool-calling loop for the CLI <c>llm</c> command agent mode.
/// </summary>
internal sealed class LlmAgentRunner(
    Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<ChatCompletionResult>> completeAsync,
    Func<ChatToolCall, CancellationToken, Task<string>> executeToolAsync,
    int maxIterations = 20,
    int maxToolResultChars = 20_000)
{
    private readonly Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<ChatCompletionResult>> _completeAsync =
        completeAsync ?? throw new ArgumentNullException(nameof(completeAsync));

    private readonly Func<ChatToolCall, CancellationToken, Task<string>> _executeToolAsync =
        executeToolAsync ?? throw new ArgumentNullException(nameof(executeToolAsync));

    private readonly int _maxIterations = maxIterations <= 0 ? 20 : maxIterations;
    private readonly int _maxToolResultChars = maxToolResultChars <= 512 ? 20_000 : maxToolResultChars;

    public async Task<LlmAgentRunResult> RunAsync(IReadOnlyList<ChatMessage> seedMessages, CancellationToken cancellationToken)
    {
        if (seedMessages == null)
        {
            throw new ArgumentNullException(nameof(seedMessages));
        }

        var messages = new List<ChatMessage>(seedMessages);
        var finalText = string.Empty;
        var toolCallsExecuted = 0;
        var toolResultCache = new Dictionary<string, string>(StringComparer.Ordinal);
        var consecutiveCacheOnlyIterations = 0;

        for (var iteration = 1; iteration <= _maxIterations; iteration++)
        {
            var completion = await _completeAsync(messages, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(completion.Text))
            {
                finalText = completion.Text.Trim();
            }

            if (completion.ToolCalls.Count == 0)
            {
                return new LlmAgentRunResult(
                    FinalText: string.IsNullOrWhiteSpace(finalText) ? "(LLM returned no content)" : finalText,
                    Iterations: iteration,
                    ToolCallsExecuted: toolCallsExecuted);
            }

            messages.Add(new ChatMessage(
                "assistant",
                completion.Text ?? string.Empty,
                toolCallId: null,
                toolCalls: completion.ToolCalls,
                contentJson: completion.RawMessageContent,
                providerMessageFields: completion.ProviderMessageFields));

            var executedAtIterationStart = toolCallsExecuted;
            foreach (var toolCall in completion.ToolCalls)
            {
                var toolKey = BuildToolCacheKey(toolCall);
                string toolResult;
                if (toolResultCache.TryGetValue(toolKey, out var cached))
                {
                    toolResult = $"[cached tool result] Duplicate tool call detected: {toolCall.Name} {toolCall.ArgumentsJson}\n" +
                                 "Do not repeat identical tool calls; use this cached output as evidence.\n\n" +
                                 cached;
                }
                else
                {
                    toolResult = await _executeToolAsync(toolCall, cancellationToken).ConfigureAwait(false);
                    toolCallsExecuted++;
                    // Defense-in-depth: redact sensitive values before sending tool output to the model.
                    toolResult = TranscriptRedactor.RedactText(toolResult);
                    toolResult = TruncateForModel(toolResult, _maxToolResultChars);
                    toolResultCache[toolKey] = toolResult;
                }

                // Even for cached results, apply truncation again in case cache entries were created before a limit change.
                toolResult = TruncateForModel(toolResult, _maxToolResultChars);
                messages.Add(new ChatMessage("tool", toolResult, toolCall.Id, toolCalls: null));
            }

            if (toolCallsExecuted == executedAtIterationStart)
            {
                consecutiveCacheOnlyIterations++;
                if (consecutiveCacheOnlyIterations >= 2)
                {
                    return new LlmAgentRunResult(
                        FinalText: "(LLM agent appears stuck repeating identical tool calls; stopping. Try `llm reset` or ask a more specific question.)",
                        Iterations: iteration,
                        ToolCallsExecuted: toolCallsExecuted);
                }
            }
            else
            {
                consecutiveCacheOnlyIterations = 0;
            }
        }

        return new LlmAgentRunResult(
            FinalText: string.IsNullOrWhiteSpace(finalText)
                ? $"(LLM agent stopped after {_maxIterations} steps without a final answer)"
                : $"(LLM agent stopped after {_maxIterations} steps)\n{finalText}",
            Iterations: _maxIterations,
            ToolCallsExecuted: toolCallsExecuted);
    }

    private static string BuildToolCacheKey(ChatToolCall toolCall)
    {
        if (toolCall == null)
        {
            return string.Empty;
        }

        // Prefer name-specific normalization for stability (addresses and command whitespace/case vary in practice).
        if (string.Equals(toolCall.Name, "exec", StringComparison.OrdinalIgnoreCase))
        {
            var command = TryReadJsonStringProperty(toolCall.ArgumentsJson, "command");
            if (!string.IsNullOrWhiteSpace(command))
            {
                return $"exec:{NormalizeDebuggerCommand(command)}";
            }
        }

        if (string.Equals(toolCall.Name, "analyze", StringComparison.OrdinalIgnoreCase))
        {
            var kind = TryReadJsonStringProperty(toolCall.ArgumentsJson, "kind");
            if (!string.IsNullOrWhiteSpace(kind))
            {
                return $"analyze:{kind.Trim().ToLowerInvariant()}";
            }
        }

        // Fallback: canonical JSON for stable equality.
        return $"{toolCall.Name.Trim().ToLowerInvariant()}:{CanonicalizeJson(toolCall.ArgumentsJson)}";
    }

    private static string NormalizeDebuggerCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        // Collapse whitespace and make casing stable for matching (debugger commands are case-insensitive).
        var trimmed = command.Trim();
        var sb = new StringBuilder(trimmed.Length);
        var lastWasWhitespace = false;
        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasWhitespace)
                {
                    sb.Append(' ');
                    lastWasWhitespace = true;
                }
                continue;
            }

            lastWasWhitespace = false;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static string? TryReadJsonStringProperty(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return value.GetString();
        }
        catch
        {
            return null;
        }
    }

    private static string CanonicalizeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var buffer = new ArrayBufferWriter<byte>(json.Length);
            using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
            WriteCanonicalJson(doc.RootElement, writer);
            writer.Flush();
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch
        {
            return json.Trim();
        }
    }

    private static void WriteCanonicalJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(item, writer);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string TruncateForModel(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0 || text.Length <= maxChars)
        {
            return text ?? string.Empty;
        }

        if (maxChars < 128)
        {
            return text.Substring(0, maxChars);
        }

        var marker = $"\n... [truncated, total {text.Length} chars]\n";
        var remaining = maxChars - marker.Length;
        if (remaining <= 0)
        {
            return text.Substring(0, maxChars);
        }

        var head = remaining / 2;
        var tail = remaining - head;
        return text.Substring(0, head) + marker + text.Substring(text.Length - tail);
    }
}

internal sealed record LlmAgentRunResult(string FinalText, int Iterations, int ToolCallsExecuted);
