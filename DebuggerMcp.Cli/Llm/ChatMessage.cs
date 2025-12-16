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

    public string Role { get; }

    public string Content { get; }
}

