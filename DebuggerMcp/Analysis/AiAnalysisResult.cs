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

    /// <summary>
    /// Optional AI-generated rewrite of the top-level analysis summary fields.
    /// When present, the report's <c>analysis.summary.description</c> and <c>analysis.summary.recommendations</c>
    /// may be overwritten with this content.
    /// </summary>
    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AiSummaryResult? Summary { get; set; }

    /// <summary>
    /// Backwards-compatible alias for older reports that emitted <c>summaryRewrite</c> under <c>analysis.aiAnalysis</c>.
    /// This property is only used for JSON deserialization and is never written.
    /// </summary>
    [JsonPropertyName("summaryRewrite")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Obsolete("Use Summary instead. This alias exists only for backwards-compatible JSON deserialization.", false)]
    public AiSummaryResult? SummaryRewrite
    {
        get => null;
        set
        {
            if (Summary == null && value != null)
            {
                Summary = value;
            }
        }
    }

    /// <summary>
    /// Optional AI-generated narrative describing what the process was doing at the time of the dump,
    /// derived from thread stacks/states.
    /// </summary>
    [JsonPropertyName("threadNarrative")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AiThreadNarrativeResult? ThreadNarrative { get; set; }
}

/// <summary>
/// AI-generated rewrite payload for the report summary.
/// </summary>
public sealed class AiSummaryResult
{
    /// <summary>
    /// Optional error message when the rewrite failed.
    /// </summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    /// <summary>
    /// Rewritten summary description.
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Rewritten recommendations.
    /// </summary>
    [JsonPropertyName("recommendations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Recommendations { get; set; }

    /// <summary>
    /// Gets or sets the number of sampling iterations performed.
    /// </summary>
    [JsonPropertyName("iterations")]
    public int Iterations { get; set; }

    /// <summary>
    /// Gets or sets the tools executed during this rewrite.
    /// </summary>
    [JsonPropertyName("commandsExecuted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExecutedCommand>? CommandsExecuted { get; set; }

    /// <summary>
    /// Model used by the client (when reported).
    /// </summary>
    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    /// <summary>
    /// When the rewrite was performed (UTC).
    /// </summary>
    [JsonPropertyName("analyzedAt")]
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// AI-generated narrative for thread activity at the time of the dump.
/// </summary>
public sealed class AiThreadNarrativeResult
{
    /// <summary>
    /// Optional error message when narrative generation failed.
    /// </summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    /// <summary>
    /// Narrative description.
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Confidence level ("high", "medium", "low", or "unknown").
    /// </summary>
    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "unknown";

    /// <summary>
    /// Gets or sets the number of sampling iterations performed.
    /// </summary>
    [JsonPropertyName("iterations")]
    public int Iterations { get; set; }

    /// <summary>
    /// Gets or sets the tools executed during narrative generation.
    /// </summary>
    [JsonPropertyName("commandsExecuted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExecutedCommand>? CommandsExecuted { get; set; }

    /// <summary>
    /// Model used by the client (when reported).
    /// </summary>
    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    /// <summary>
    /// When the narrative was generated (UTC).
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
