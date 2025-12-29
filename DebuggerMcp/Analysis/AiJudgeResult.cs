#nullable enable

using System.Text.Json.Serialization;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Result of an internal "judge" step that selects the best-supported hypothesis and rejects top alternatives,
/// citing evidence IDs from the evidence ledger.
/// </summary>
public sealed class AiJudgeResult
{
    /// <summary>
    /// Gets or sets the selected hypothesis ID (e.g., H2).
    /// </summary>
    [JsonPropertyName("selectedHypothesisId")]
    public string SelectedHypothesisId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the judge confidence ("high", "medium", "low", or "unknown").
    /// </summary>
    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "unknown";

    /// <summary>
    /// Gets or sets a concise rationale that cites evidence IDs (E#).
    /// </summary>
    [JsonPropertyName("rationale")]
    public string Rationale { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets evidence IDs that directly support the selected hypothesis.
    /// </summary>
    [JsonPropertyName("supportsEvidenceIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SupportsEvidenceIds { get; set; }

    /// <summary>
    /// Gets or sets rejected competing hypotheses and the evidence IDs that contradict each.
    /// </summary>
    [JsonPropertyName("rejectedHypotheses")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AiRejectedHypothesis>? RejectedHypotheses { get; set; }

    /// <summary>
    /// Gets or sets the model used by the client (when reported).
    /// </summary>
    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    /// <summary>
    /// Gets or sets when the judge step was performed (UTC).
    /// </summary>
    [JsonPropertyName("analyzedAt")]
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A rejected competing hypothesis and the evidence IDs that contradict it.
/// </summary>
public sealed class AiRejectedHypothesis
{
    /// <summary>
    /// Gets or sets the hypothesis ID being rejected (e.g., H3).
    /// </summary>
    [JsonPropertyName("hypothesisId")]
    public string HypothesisId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets evidence IDs that contradict this hypothesis.
    /// </summary>
    [JsonPropertyName("contradictsEvidenceIds")]
    public List<string> ContradictsEvidenceIds { get; set; } = [];

    /// <summary>
    /// Gets or sets a concise explanation for why the hypothesis is rejected, citing the evidence IDs.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

