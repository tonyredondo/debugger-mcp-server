#nullable enable

using System.Text.RegularExpressions;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Stores a bounded, deduplicated evidence ledger for an AI analysis run.
/// </summary>
internal sealed class AiEvidenceLedger
{
    private const int DefaultMaxItems = 50;
    private const int MaxSourceChars = 512;
    private const int MaxFindingChars = 2048;
    private const int MaxWhyItMattersChars = 2048;
    private const int MaxTagChars = 64;
    private static readonly Regex EvidenceIdRegex = new(@"^E(?<n>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly int _maxItems;
    private readonly Dictionary<string, AiEvidenceLedgerItem> _itemsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AiEvidenceLedgerItem> _items = [];
    private readonly Dictionary<string, string> _idByDedupeKey = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiEvidenceLedger"/> class.
    /// </summary>
    /// <param name="maxItems">Maximum number of evidence items retained in memory.</param>
    public AiEvidenceLedger(int? maxItems = null)
    {
        _maxItems = Math.Clamp(maxItems ?? DefaultMaxItems, 1, 500);
    }

    /// <summary>
    /// Gets the current evidence items.
    /// </summary>
    public IReadOnlyList<AiEvidenceLedgerItem> Items => _items;

    /// <summary>
    /// Returns <c>true</c> when an evidence ID exists in the ledger.
    /// </summary>
    public bool ContainsEvidenceId(string? id)
    {
        if (!TryNormalizeEvidenceId(id, out var normalized, out _))
        {
            return false;
        }

        return _itemsById.ContainsKey(normalized);
    }

    /// <summary>
    /// Adds or updates evidence items in the ledger, enforcing bounds and deduplication.
    /// </summary>
    public AiEvidenceLedgerAddResult AddOrUpdate(IEnumerable<AiEvidenceLedgerItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var result = new AiEvidenceLedgerAddResult();
        foreach (var raw in items)
        {
            if (raw == null)
            {
                result.InvalidItems++;
                continue;
            }

            var source = NormalizeField(raw.Source, MaxSourceChars);
            var finding = NormalizeField(raw.Finding, MaxFindingChars);
            var whyItMatters = NormalizeOptionalField(raw.WhyItMatters, MaxWhyItMattersChars);
            var tags = NormalizeTags(raw.Tags);

            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(finding))
            {
                result.InvalidItems++;
                continue;
            }

            var dedupeKey = BuildDedupeKey(source, finding);

            if (TryNormalizeEvidenceId(raw.Id, out var providedId, out var numericId))
            {
                if (_itemsById.TryGetValue(providedId, out var existing))
                {
                    if (_idByDedupeKey.TryGetValue(dedupeKey, out var idForKey)
                        && !idForKey.Equals(providedId, StringComparison.OrdinalIgnoreCase))
                    {
                        result.IgnoredDuplicates++;
                        result.IgnoredDuplicateIds.Add(idForKey);
                        continue;
                    }

                    RemoveAllDedupeKeysForId(providedId);
                    UpdateExisting(existing, source, finding, whyItMatters, tags);
                    _idByDedupeKey[dedupeKey] = providedId;
                    result.UpdatedIds.Add(providedId);
                    continue;
                }

                if (_idByDedupeKey.TryGetValue(dedupeKey, out var existingIdForKey))
                {
                    result.IgnoredDuplicates++;
                    result.IgnoredDuplicateIds.Add(existingIdForKey);
                    continue;
                }

                if (_items.Count >= _maxItems)
                {
                    result.IgnoredAtCapacity++;
                    continue;
                }

                var item = new AiEvidenceLedgerItem
                {
                    Id = providedId,
                    Source = source,
                    Finding = finding,
                    WhyItMatters = whyItMatters,
                    Tags = tags
                };

                _items.Add(item);
                _itemsById[providedId] = item;
                _idByDedupeKey[dedupeKey] = providedId;
                result.AddedIds.Add(providedId);

                if (numericId.HasValue && numericId.Value >= _nextId)
                {
                    _nextId = numericId.Value + 1;
                }

                continue;
            }

            if (_idByDedupeKey.TryGetValue(dedupeKey, out var existingId))
            {
                if (_itemsById.TryGetValue(existingId, out var existing))
                {
                    UpdateExisting(existing, source, finding, whyItMatters, tags);
                }

                result.IgnoredDuplicates++;
                result.IgnoredDuplicateIds.Add(existingId);
                continue;
            }

            if (_items.Count >= _maxItems)
            {
                result.IgnoredAtCapacity++;
                continue;
            }

            var id = $"E{_nextId++}";
            var added = new AiEvidenceLedgerItem
            {
                Id = id,
                Source = source,
                Finding = finding,
                WhyItMatters = whyItMatters,
                Tags = tags
            };

            _items.Add(added);
            _itemsById[id] = added;
            _idByDedupeKey[dedupeKey] = id;
            result.AddedIds.Add(id);
        }

        return result;
    }

    private void RemoveAllDedupeKeysForId(string evidenceId)
    {
        if (_idByDedupeKey.Count == 0)
        {
            return;
        }

        List<string>? keysToRemove = null;
        foreach (var kvp in _idByDedupeKey)
        {
            if (kvp.Value.Equals(evidenceId, StringComparison.OrdinalIgnoreCase))
            {
                keysToRemove ??= [];
                keysToRemove.Add(kvp.Key);
            }
        }

        if (keysToRemove == null)
        {
            return;
        }

        foreach (var key in keysToRemove)
        {
            _idByDedupeKey.Remove(key);
        }
    }

    private static void UpdateExisting(AiEvidenceLedgerItem target, string source, string finding, string? whyItMatters, List<string>? tags)
    {
        target.Source = source;
        target.Finding = finding;

        if (whyItMatters != null)
        {
            target.WhyItMatters = whyItMatters;
        }

        if (tags != null)
        {
            target.Tags = MergeTags(target.Tags, tags);
        }
    }

    private static List<string>? MergeTags(List<string>? existing, List<string> incoming)
    {
        if (incoming.Count == 0)
        {
            return existing;
        }

        if (existing == null || existing.Count == 0)
        {
            return incoming;
        }

        var merged = new List<string>(existing.Count + incoming.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in existing)
        {
            if (string.IsNullOrWhiteSpace(t))
            {
                continue;
            }

            if (seen.Add(t))
            {
                merged.Add(t);
            }
        }

        foreach (var t in incoming)
        {
            if (string.IsNullOrWhiteSpace(t))
            {
                continue;
            }

            if (seen.Add(t))
            {
                merged.Add(t);
            }
        }

        return merged.Count == 0 ? null : merged;
    }

    private static string NormalizeField(string? value, int maxChars)
        => Truncate(CollapseWhitespace(value ?? string.Empty).Trim(), maxChars);

    private static string? NormalizeOptionalField(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = CollapseWhitespace(value).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return Truncate(trimmed, maxChars);
    }

    private static List<string>? NormalizeTags(List<string>? tags)
    {
        if (tags == null || tags.Count == 0)
        {
            return null;
        }

        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tags)
        {
            if (string.IsNullOrWhiteSpace(t))
            {
                continue;
            }

            var clean = Truncate(CollapseWhitespace(t).Trim(), MaxTagChars);
            if (string.IsNullOrWhiteSpace(clean))
            {
                continue;
            }

            if (seen.Add(clean))
            {
                normalized.Add(clean);
            }
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static bool TryNormalizeEvidenceId(string? id, out string normalized, out int? numericId)
    {
        normalized = string.Empty;
        numericId = null;

        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var match = EvidenceIdRegex.Match(id.Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["n"].Value, out var parsed) || parsed <= 0)
        {
            return false;
        }

        numericId = parsed;
        normalized = $"E{parsed}";
        return true;
    }

    private static string BuildDedupeKey(string source, string finding)
        => $"{NormalizeKeyPart(source)}\u001f{NormalizeKeyPart(finding)}";

    private static string NormalizeKeyPart(string value)
        => CollapseWhitespace(value).Trim().ToLowerInvariant();

    private static string CollapseWhitespace(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }

            lastWasSpace = false;
            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string Truncate(string value, int maxChars)
    {
        if (maxChars <= 0 || value.Length <= maxChars)
        {
            return value;
        }

        if (maxChars <= 1)
        {
            return value.Substring(0, maxChars);
        }

        return value.Substring(0, maxChars - 1) + "…";
    }
}

/// <summary>
/// Result of an evidence ledger add/update operation.
/// </summary>
internal sealed class AiEvidenceLedgerAddResult
{
    /// <summary>
    /// Gets the IDs added to the ledger.
    /// </summary>
    public List<string> AddedIds { get; } = [];

    /// <summary>
    /// Gets the IDs updated in the ledger.
    /// </summary>
    public List<string> UpdatedIds { get; } = [];

    /// <summary>
    /// Gets the number of invalid items ignored.
    /// </summary>
    public int InvalidItems { get; set; }

    /// <summary>
    /// Gets the number of duplicates ignored.
    /// </summary>
    public int IgnoredDuplicates { get; set; }

    /// <summary>
    /// Gets the IDs of duplicates that were ignored (best-effort).
    /// </summary>
    public List<string> IgnoredDuplicateIds { get; } = [];

    /// <summary>
    /// Gets the number of items ignored due to ledger capacity.
    /// </summary>
    public int IgnoredAtCapacity { get; set; }
}
