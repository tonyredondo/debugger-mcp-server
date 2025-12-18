using System.Text.Json;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// A tool definition for OpenAI-compatible chat completion APIs.
/// </summary>
internal sealed class ChatTool
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// JSON Schema for the tool input.
    /// </summary>
    public JsonElement Parameters { get; set; }
}

/// <summary>
/// Tool choice configuration for OpenAI-compatible chat completion APIs.
/// </summary>
internal sealed class ChatToolChoice
{
    public string Mode { get; set; } = "auto"; // auto|none|required|function

    public string? FunctionName { get; set; }
}

/// <summary>
/// A tool call emitted by the model (OpenAI-compatible).
/// </summary>
public sealed class ChatToolCall
{
    public ChatToolCall(string id, string name, string argumentsJson)
    {
        Id = id;
        Name = name;
        ArgumentsJson = argumentsJson;
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>
    /// Raw JSON string (OpenAI "function.arguments").
    /// </summary>
    public string ArgumentsJson { get; }
}

/// <summary>
/// Chat completion request parameters for the LLM client.
/// </summary>
internal sealed class ChatCompletionRequest
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    public IReadOnlyList<ChatTool>? Tools { get; init; }

    public ChatToolChoice? ToolChoice { get; init; }

    public int? MaxTokens { get; init; }

    /// <summary>
    /// Reasoning effort hint for reasoning-capable models (provider-specific support).
    /// </summary>
    /// <remarks>
    /// Supported values: <c>low</c>, <c>medium</c>, <c>high</c>.
    /// When <c>null</c>, the field is omitted.
    /// </remarks>
    public string? ReasoningEffort { get; init; }
}

/// <summary>
/// Chat completion result (text + optional tool calls).
/// </summary>
internal sealed class ChatCompletionResult
{
    public string? Model { get; init; }

    public string? Text { get; init; }

    /// <summary>
    /// Raw OpenAI/OpenRouter "message.content" value (can be string, array, or object depending on provider).
    /// Used to preserve provider-specific reasoning/thought signatures across tool-call iterations.
    /// </summary>
    public JsonElement? RawMessageContent { get; init; }

    /// <summary>
    /// Provider-specific fields on the assistant message (e.g., reasoning details) that must be preserved in subsequent requests.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? ProviderMessageFields { get; init; }

    public IReadOnlyList<ChatToolCall> ToolCalls { get; init; } = [];
}
