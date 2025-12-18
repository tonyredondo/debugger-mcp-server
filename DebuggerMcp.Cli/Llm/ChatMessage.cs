using System.Text.Json;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// A chat message for OpenAI-compatible chat completion APIs.
/// </summary>
public sealed class ChatMessage
{
    public ChatMessage(string role, string content)
    {
        Role = role;
        Content = content;
    }

    public ChatMessage(
        string role,
        string content,
        string? toolCallId,
        IReadOnlyList<ChatToolCall>? toolCalls,
        JsonElement? contentJson = null,
        IReadOnlyDictionary<string, JsonElement>? providerMessageFields = null)
    {
        Role = role;
        Content = content;
        ToolCallId = toolCallId;
        ToolCalls = toolCalls;
        ContentJson = contentJson;
        ProviderMessageFields = providerMessageFields;
    }

    public string Role { get; }

    public string Content { get; }

    /// <summary>
    /// Optional non-string content payload to preserve provider-specific structured content blocks.
    /// </summary>
    public JsonElement? ContentJson { get; }

    /// <summary>
    /// Optional provider-specific fields that must be sent back verbatim in subsequent requests.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? ProviderMessageFields { get; }

    /// <summary>
    /// For role <c>tool</c>, the corresponding tool call ID.
    /// </summary>
    public string? ToolCallId { get; }

    /// <summary>
    /// For role <c>assistant</c>, any tool calls emitted by the model.
    /// </summary>
    public IReadOnlyList<ChatToolCall>? ToolCalls { get; }
}
