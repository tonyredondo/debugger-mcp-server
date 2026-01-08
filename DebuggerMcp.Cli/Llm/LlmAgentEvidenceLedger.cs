using System.Security.Cryptography;
using System.Text;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Durable “memory” for <c>llmagent</c>: records tool outputs with stable evidence IDs so the model can cite them
/// even after conversational context is pruned.
/// </summary>
/// <remarks>
/// This is intentionally <b>not</b> a tool-result cache. Tools must still execute when requested; the ledger only
/// records what happened and de-duplicates evidence IDs for identical outputs.
/// </remarks>
internal sealed class LlmAgentEvidenceLedger
{
    private readonly object _gate = new();
    private int _nextId = 1;

    // Keyed by (toolKeyHash, toolOutputHash) so identical outputs do not mint new evidence IDs.
    private readonly Dictionary<(string ToolKeyHash, string ToolOutputHash), LlmAgentEvidenceEntry> _entriesByHash =
        new(StringTupleComparer.Ordinal);

    private readonly List<LlmAgentEvidenceEntry> _entriesInOrder = [];

    /// <summary>
    /// Current evidence entries in chronological (first-seen) order.
    /// </summary>
    public IReadOnlyList<LlmAgentEvidenceEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entriesInOrder.ToList();
            }
        }
    }

    /// <summary>
    /// Clears all recorded evidence (used when the report snapshot changes or on explicit reset).
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _entriesByHash.Clear();
            _entriesInOrder.Clear();
            _nextId = 1;
        }
    }

    /// <summary>
    /// Adds a new evidence entry for a tool execution or updates an existing one if the output is identical.
    /// </summary>
    public LlmAgentEvidenceUpdate AddOrUpdate(
        string toolName,
        string argumentsJson,
        string toolKey,
        string toolResultForHashing,
        string toolResultPreview,
        IReadOnlyList<string> tags,
        bool toolWasError,
        DateTimeOffset timestampUtc)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            toolName = "(unknown)";
        }

        argumentsJson ??= string.Empty;
        toolKey ??= string.Empty;
        toolResultForHashing ??= string.Empty;
        toolResultPreview ??= string.Empty;
        tags ??= Array.Empty<string>();

        var toolKeyHash = ComputeSha256Hex(toolKey);
        var toolOutputHash = ComputeSha256Hex(toolResultForHashing);

        lock (_gate)
        {
            var key = (toolKeyHash, toolOutputHash);
            if (_entriesByHash.TryGetValue(key, out var existing))
            {
                var updated = existing with
                {
                    SeenCount = existing.SeenCount + 1,
                    LastSeenAtUtc = timestampUtc,
                    ToolWasError = existing.ToolWasError || toolWasError,
                    Tags = MergeTags(existing.Tags, tags),
                    ToolResultPreview = string.IsNullOrWhiteSpace(existing.ToolResultPreview) ? toolResultPreview : existing.ToolResultPreview
                };

                _entriesByHash[key] = updated;
                ReplaceInOrder(updated);
                return new LlmAgentEvidenceUpdate(
                    Entry: updated,
                    IsNewEvidence: false);
            }

            var id = $"E{_nextId++}";
            var entry = new LlmAgentEvidenceEntry(
                EvidenceId: id,
                ToolName: toolName.Trim(),
                ArgumentsJson: argumentsJson,
                ToolKey: toolKey,
                ToolKeyHash: toolKeyHash,
                ToolOutputHash: toolOutputHash,
                ToolResultPreview: toolResultPreview,
                Tags: tags.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList(),
                ToolWasError: toolWasError,
                SeenCount: 1,
                FirstSeenAtUtc: timestampUtc,
                LastSeenAtUtc: timestampUtc);

            _entriesByHash[key] = entry;
            _entriesInOrder.Add(entry);
            return new LlmAgentEvidenceUpdate(
                Entry: entry,
                IsNewEvidence: true);
        }
    }

    /// <summary>
    /// Returns the newest evidence entry that contains the given tag, or <c>null</c> if none exist.
    /// </summary>
    public LlmAgentEvidenceEntry? TryGetLatestByTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        lock (_gate)
        {
            for (var i = _entriesInOrder.Count - 1; i >= 0; i--)
            {
                var entry = _entriesInOrder[i];
                if (entry.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                {
                    return entry;
                }
            }

            return null;
        }
    }

    private static IReadOnlyList<string> MergeTags(IReadOnlyList<string> existing, IReadOnlyList<string> added)
    {
        if (added.Count == 0)
        {
            return existing;
        }

        if (existing.Count == 0)
        {
            return added.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        }

        return existing
            .Concat(added)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ReplaceInOrder(LlmAgentEvidenceEntry entry)
    {
        for (var i = 0; i < _entriesInOrder.Count; i++)
        {
            if (string.Equals(_entriesInOrder[i].EvidenceId, entry.EvidenceId, StringComparison.Ordinal))
            {
                _entriesInOrder[i] = entry;
                break;
            }
        }
    }

    private static string ComputeSha256Hex(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "sha256:0";
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string ToolKeyHash, string ToolOutputHash)>
    {
        public static readonly StringTupleComparer Ordinal = new();

        public bool Equals((string ToolKeyHash, string ToolOutputHash) x, (string ToolKeyHash, string ToolOutputHash) y)
            => string.Equals(x.ToolKeyHash, y.ToolKeyHash, StringComparison.Ordinal) &&
               string.Equals(x.ToolOutputHash, y.ToolOutputHash, StringComparison.Ordinal);

        public int GetHashCode((string ToolKeyHash, string ToolOutputHash) obj)
            => HashCode.Combine(obj.ToolKeyHash, obj.ToolOutputHash);
    }
}

/// <summary>
/// Single evidence entry with a stable ID.
/// </summary>
internal sealed record LlmAgentEvidenceEntry(
    string EvidenceId,
    string ToolName,
    string ArgumentsJson,
    string ToolKey,
    string ToolKeyHash,
    string ToolOutputHash,
    string ToolResultPreview,
    IReadOnlyList<string> Tags,
    bool ToolWasError,
    int SeenCount,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc);

/// <summary>
/// Result of adding/updating an evidence entry.
/// </summary>
internal sealed record LlmAgentEvidenceUpdate(LlmAgentEvidenceEntry Entry, bool IsNewEvidence);

