#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Results from AI-powered crash analysis performed via MCP sampling.
/// </summary>
public sealed class AiAnalysisResult
{
    /// <summary>
    /// Gets or sets the identified root cause of the crash.
    /// </summary>
    [JsonPropertyName("rootCause")]
    public string RootCause { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the confidence level ("high", "medium", "low", or "unknown").
    /// </summary>
    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "unknown";

    /// <summary>
    /// Gets or sets the step-by-step reasoning and evidence.
    /// </summary>
    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reasoning { get; set; }

    /// <summary>
    /// Gets or sets recommended fixes or next steps.
    /// </summary>
    [JsonPropertyName("recommendations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Recommendations { get; set; }

    /// <summary>
    /// Gets or sets additional findings discovered during analysis.
    /// </summary>
    [JsonPropertyName("additionalFindings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AdditionalFindings { get; set; }

    /// <summary>
    /// Gets or sets the number of analysis iterations performed.
    /// </summary>
    [JsonPropertyName("iterations")]
    public int Iterations { get; set; }

    /// <summary>
    /// Gets or sets the debugger commands/tools executed during analysis.
    /// </summary>
    [JsonPropertyName("commandsExecuted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExecutedCommand>? CommandsExecuted { get; set; }

    /// <summary>
    /// Gets or sets the model used by the client (when reported).
    /// </summary>
    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    /// <summary>
    /// Gets or sets when the AI analysis was performed (UTC).
    /// </summary>
    [JsonPropertyName("analyzedAt")]
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Record of a tool execution performed during AI analysis.
/// </summary>
public sealed class ExecutedCommand
{
    /// <summary>
    /// Gets or sets the tool name (e.g., "exec", "inspect").
    /// </summary>
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the input passed to the tool.
    /// </summary>
    [JsonPropertyName("input")]
    public JsonElement Input { get; set; }

    /// <summary>
    /// Gets or sets the tool output (possibly truncated).
    /// </summary>
    [JsonPropertyName("output")]
    public string Output { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the iteration number this tool was executed in (1-based).
    /// </summary>
    [JsonPropertyName("iteration")]
    public int Iteration { get; set; }

    /// <summary>
    /// Gets or sets the duration of the tool execution as an invariant string (e.g., "00:00:00.234").
    /// </summary>
    [JsonPropertyName("duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Duration { get; set; }
}

