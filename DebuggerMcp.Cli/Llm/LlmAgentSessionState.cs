using System.Text.Json;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Per-scope orchestration state for <c>llmagent</c> runs.
/// </summary>
internal sealed class LlmAgentSessionState
{
    /// <summary>
    /// Creates a new session state for a given transcript scope key.
    /// </summary>
    public LlmAgentSessionState(LlmAgentSessionStateStore.TranscriptScopeKey scopeKey)
    {
        ScopeKey = scopeKey;
    }

    /// <summary>
    /// Transcript scope key that this state is associated with.
    /// </summary>
    public LlmAgentSessionStateStore.TranscriptScopeKey ScopeKey { get; }

    /// <summary>
    /// Evidence ledger used as durable “memory” across multiple prompts.
    /// </summary>
    public LlmAgentEvidenceLedger Evidence { get; } = new();

    /// <summary>
    /// Most recent checkpoint JSON that should be carried forward across runs.
    /// </summary>
    public string? LastCheckpointJson { get; set; }

    /// <summary>
    /// Last known report snapshot id from metadata (metadata.dumpId).
    /// </summary>
    public string? LastReportDumpId { get; private set; }

    /// <summary>
    /// Last known report generation timestamp from metadata (metadata.generatedAt).
    /// </summary>
    public string? LastReportGeneratedAt { get; private set; }

    /// <summary>
    /// Updates report snapshot identity from a <c>report_get(path="metadata")</c> tool result and resets state if needed.
    /// </summary>
    /// <remarks>
    /// When the report snapshot changes, prior evidence/checkpoints are unsafe to reuse because they refer to a different report.
    /// </remarks>
    public bool TryUpdateSnapshotFromMetadataToolResult(string toolResult, out string? resetReason)
    {
        resetReason = null;

        if (string.IsNullOrWhiteSpace(toolResult))
        {
            return false;
        }

        if (!TryParseMetadataEnvelope(toolResult, out var dumpId, out var generatedAt))
        {
            return false;
        }

        dumpId = string.IsNullOrWhiteSpace(dumpId) ? null : dumpId.Trim();
        generatedAt = string.IsNullOrWhiteSpace(generatedAt) ? null : generatedAt.Trim();

        var changed = false;
        if (!string.IsNullOrWhiteSpace(LastReportDumpId) &&
            !string.Equals(LastReportDumpId, dumpId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(dumpId))
        {
            changed = true;
            resetReason = $"Report dumpId changed ({LastReportDumpId ?? "(unknown)"} -> {dumpId}).";
        }

        if (!changed &&
            !string.Equals(LastReportGeneratedAt, generatedAt, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(generatedAt) &&
            !string.IsNullOrWhiteSpace(LastReportGeneratedAt))
        {
            changed = true;
            resetReason = $"Report generatedAt changed ({LastReportGeneratedAt} -> {generatedAt}).";
        }

        if (changed)
        {
            Evidence.Reset();
            LastCheckpointJson = null;
        }

        if (!string.IsNullOrWhiteSpace(dumpId))
        {
            LastReportDumpId = dumpId;
        }

        if (!string.IsNullOrWhiteSpace(generatedAt))
        {
            LastReportGeneratedAt = generatedAt;
        }

        return changed;
    }

    private static bool TryParseMetadataEnvelope(string toolResult, out string? dumpId, out string? generatedAt)
    {
        dumpId = null;
        generatedAt = null;

        try
        {
            using var doc = JsonDocument.Parse(toolResult);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("path", out var pathProp) ||
                pathProp.ValueKind != JsonValueKind.String ||
                !string.Equals(pathProp.GetString(), "metadata", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!root.TryGetProperty("value", out var valueProp) || valueProp.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (valueProp.TryGetProperty("dumpId", out var dumpProp) && dumpProp.ValueKind == JsonValueKind.String)
            {
                dumpId = dumpProp.GetString();
            }

            if (valueProp.TryGetProperty("generatedAt", out var genProp) && genProp.ValueKind == JsonValueKind.String)
            {
                generatedAt = genProp.GetString();
            }

            return !string.IsNullOrWhiteSpace(dumpId) || !string.IsNullOrWhiteSpace(generatedAt);
        }
        catch
        {
            return false;
        }
    }
}
