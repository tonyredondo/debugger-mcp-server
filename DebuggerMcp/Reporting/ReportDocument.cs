using System.Text.Json.Serialization;
using DebuggerMcp.Analysis;

namespace DebuggerMcp.Reporting;

/// <summary>
/// Canonical report document shape used as the source of truth for all report formats.
/// </summary>
/// <remarks>
/// This matches the JSON report schema: <c>{ "metadata": { ... }, "analysis": { ... } }</c>.
/// Other formats (Markdown/HTML) should be rendered from this document to avoid divergence.
/// </remarks>
public sealed class ReportDocument
{
    /// <summary>
    /// Gets or sets metadata about the report generation.
    /// </summary>
    [JsonPropertyName("metadata")]
    public ReportMetadata Metadata { get; set; } = new();

    /// <summary>
    /// Gets or sets the crash analysis result payload.
    /// </summary>
    [JsonPropertyName("analysis")]
    public CrashAnalysisResult Analysis { get; set; } = new();
}

