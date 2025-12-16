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

    public ChatMessage(string role, string content, string? toolCallId, IReadOnlyList<ChatToolCall>? toolCalls)
    {
        Role = role;
        Content = content;
        ToolCallId = toolCallId;
        ToolCalls = toolCalls;
    }

    public string Role { get; }

    public string Content { get; }

    /// <summary>
    /// For role <c>tool</c>, the corresponding tool call ID.
    /// </summary>
    public string? ToolCallId { get; }

    /// <summary>
    /// For role <c>assistant</c>, any tool calls emitted by the model.
    /// </summary>
    public IReadOnlyList<ChatToolCall>? ToolCalls { get; }
}
