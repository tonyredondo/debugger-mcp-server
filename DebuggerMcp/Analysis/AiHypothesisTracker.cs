#nullable enable

using System.Text.RegularExpressions;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Stores a bounded set of competing hypotheses for an AI analysis run.
/// </summary>
internal sealed class AiHypothesisTracker
{
    private const int DefaultMaxHypotheses = 10;
    private const int MaxHypothesisChars = 2048;
    private const int MaxNotesChars = 2048;
    private static readonly Regex HypothesisIdRegex = new(@"^H(?<n>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly AiEvidenceLedger _evidenceLedger;
    private readonly int _maxHypotheses;
    private readonly Dictionary<string, AiHypothesis> _hypothesesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _idByDedupeKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AiHypothesis> _hypotheses = [];
    private int _nextId = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiHypothesisTracker"/> class.
    /// </summary>
    /// <param name="evidenceLedger">Evidence ledger used to validate evidence ID references.</param>
    /// <param name="maxHypotheses">Maximum hypotheses retained in memory.</param>
    public AiHypothesisTracker(AiEvidenceLedger evidenceLedger, int? maxHypotheses = null)
    {
        _evidenceLedger = evidenceLedger ?? throw new ArgumentNullException(nameof(evidenceLedger));
        _maxHypotheses = Math.Clamp(maxHypotheses ?? DefaultMaxHypotheses, 1, 100);
    }

    /// <summary>
    /// Gets the current hypotheses.
    /// </summary>
    public IReadOnlyList<AiHypothesis> Hypotheses => _hypotheses;

    /// <summary>
    /// Registers hypotheses, enforcing bounds and deduplication.
    /// </summary>
    public AiHypothesisRegisterResult Register(IEnumerable<AiHypothesis> hypotheses)
    {
        ArgumentNullException.ThrowIfNull(hypotheses);

        var result = new AiHypothesisRegisterResult();
        foreach (var raw in hypotheses)
        {
            if (raw == null)
            {
                result.InvalidItems++;
                continue;
            }

            var hypothesisText = NormalizeField(raw.Hypothesis, MaxHypothesisChars);
            if (string.IsNullOrWhiteSpace(hypothesisText))
            {
                result.InvalidItems++;
                continue;
            }

            var confidence = NormalizeConfidence(raw.Confidence);
            var unknowns = NormalizeList(raw.Unknowns, maxItemChars: 512, maxItems: 50);
            var testsToRun = NormalizeList(raw.TestsToRun, maxItemChars: 512, maxItems: 50);
            var notesValue = NormalizeField(raw.Notes, MaxNotesChars);
            var notes = string.IsNullOrWhiteSpace(notesValue) ? null : notesValue;

            List<string>? supportsEvidenceIds = null;
            if (raw.SupportsEvidenceIds != null)
            {
                ApplyEvidenceIds(raw.SupportsEvidenceIds, out supportsEvidenceIds, out _);
            }

            List<string>? contradictsEvidenceIds = null;
            if (raw.ContradictsEvidenceIds != null)
            {
                ApplyEvidenceIds(raw.ContradictsEvidenceIds, out contradictsEvidenceIds, out _);
            }

            var dedupeKey = NormalizeKeyPart(hypothesisText);

            if (TryNormalizeHypothesisId(raw.Id, out var providedId, out var numericId))
            {
                if (_hypothesesById.TryGetValue(providedId, out var existing))
                {
                    existing.Hypothesis = hypothesisText;
                    existing.Confidence = confidence;
                    existing.Unknowns = unknowns;
                    existing.TestsToRun = testsToRun;
                    existing.Notes = notes;
                    existing.SupportsEvidenceIds = supportsEvidenceIds;
                    existing.ContradictsEvidenceIds = contradictsEvidenceIds;
                    result.UpdatedIds.Add(providedId);
                    continue;
                }

                if (_hypotheses.Count >= _maxHypotheses)
                {
                    result.IgnoredAtCapacity++;
                    continue;
                }

                var h = new AiHypothesis
                {
                    Id = providedId,
                    Hypothesis = hypothesisText,
                    Confidence = confidence,
                    Unknowns = unknowns,
                    TestsToRun = testsToRun,
                    Notes = notes,
                    SupportsEvidenceIds = supportsEvidenceIds,
                    ContradictsEvidenceIds = contradictsEvidenceIds
                };
                _hypotheses.Add(h);
                _hypothesesById[providedId] = h;
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
                result.IgnoredDuplicates++;
                result.IgnoredDuplicateIds.Add(existingId);
                continue;
            }

            if (_hypotheses.Count >= _maxHypotheses)
            {
                result.IgnoredAtCapacity++;
                continue;
            }

            var id = $"H{_nextId++}";
            var added = new AiHypothesis
            {
                Id = id,
                Hypothesis = hypothesisText,
                Confidence = confidence,
                Unknowns = unknowns,
                TestsToRun = testsToRun,
                Notes = notes,
                SupportsEvidenceIds = supportsEvidenceIds,
                ContradictsEvidenceIds = contradictsEvidenceIds
            };

            _hypotheses.Add(added);
            _hypothesesById[id] = added;
            _idByDedupeKey[dedupeKey] = id;
            result.AddedIds.Add(id);
        }

        return result;
    }

    /// <summary>
    /// Updates hypothesis confidence and evidence links.
    /// </summary>
    public AiHypothesisUpdateResult Update(IEnumerable<AiHypothesisUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);

        var result = new AiHypothesisUpdateResult();
        foreach (var update in updates)
        {
            if (update == null || string.IsNullOrWhiteSpace(update.Id))
            {
                result.InvalidItems++;
                continue;
            }

            if (!TryNormalizeHypothesisId(update.Id, out var normalizedId, out _))
            {
                result.UnknownHypothesisIds.Add(update.Id.Trim());
                continue;
            }

            if (!_hypothesesById.TryGetValue(normalizedId, out var hypothesis))
            {
                result.UnknownHypothesisIds.Add(normalizedId);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(update.Confidence))
            {
                hypothesis.Confidence = NormalizeConfidence(update.Confidence);
            }

            if (!string.IsNullOrWhiteSpace(update.Notes))
            {
                hypothesis.Notes = NormalizeField(update.Notes, MaxNotesChars);
            }

            if (update.SupportsEvidenceIds != null)
            {
                ApplyEvidenceIds(
                    update.SupportsEvidenceIds,
                    out var valid,
                    out var invalid);
                hypothesis.SupportsEvidenceIds = valid;
                result.UnknownEvidenceIds.AddRange(invalid);
            }

            if (update.ContradictsEvidenceIds != null)
            {
                ApplyEvidenceIds(
                    update.ContradictsEvidenceIds,
                    out var valid,
                    out var invalid);
                hypothesis.ContradictsEvidenceIds = valid;
                result.UnknownEvidenceIds.AddRange(invalid);
            }

            result.UpdatedIds.Add(normalizedId);
        }

        result.UnknownEvidenceIds = result.UnknownEvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.UnknownHypothesisIds = result.UnknownHypothesisIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return result;
    }

    private void ApplyEvidenceIds(IEnumerable<string> ids, out List<string>? validIds, out List<string> invalidIds)
    {
        validIds = null;
        invalidIds = [];

        var valid = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in ids)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var trimmed = raw.Trim();
            if (!seen.Add(trimmed))
            {
                continue;
            }

            if (_evidenceLedger.ContainsEvidenceId(trimmed))
            {
                valid.Add(NormalizeEvidenceId(trimmed));
            }
            else
            {
                invalidIds.Add(trimmed);
            }
        }

        validIds = valid.Count == 0 ? null : valid;
    }

    private static string NormalizeEvidenceId(string id)
    {
        var trimmed = id.Trim();
        var match = Regex.Match(trimmed, @"^E(?<n>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return trimmed;
        }

        if (!int.TryParse(match.Groups["n"].Value, out var parsed) || parsed <= 0)
        {
            return trimmed;
        }

        return $"E{parsed}";
    }

    private static string NormalizeConfidence(string? value)
    {
        var s = (value ?? string.Empty).Trim().ToLowerInvariant();
        return s switch
        {
            "high" => "high",
            "medium" => "medium",
            "low" => "low",
            _ => "unknown"
        };
    }

    private static string NormalizeField(string? value, int maxChars)
    {
        var s = CollapseWhitespace(value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        return Truncate(s, maxChars);
    }

    private static List<string>? NormalizeList(List<string>? values, int maxItemChars, int maxItems)
    {
        if (values == null || values.Count == 0)
        {
            return null;
        }

        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in values)
        {
            if (list.Count >= maxItems)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(v))
            {
                continue;
            }

            var clean = Truncate(CollapseWhitespace(v).Trim(), maxItemChars);
            if (string.IsNullOrWhiteSpace(clean))
            {
                continue;
            }

            if (seen.Add(clean))
            {
                list.Add(clean);
            }
        }

        return list.Count == 0 ? null : list;
    }

    private static bool TryNormalizeHypothesisId(string? id, out string normalized, out int? numericId)
    {
        normalized = string.Empty;
        numericId = null;

        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var match = HypothesisIdRegex.Match(id.Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["n"].Value, out var parsed) || parsed <= 0)
        {
            return false;
        }

        numericId = parsed;
        normalized = $"H{parsed}";
        return true;
    }

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
/// Input model for hypothesis updates.
/// </summary>
internal sealed class AiHypothesisUpdate
{
    public string Id { get; set; } = string.Empty;
    public string? Confidence { get; set; }
    public List<string>? SupportsEvidenceIds { get; set; }
    public List<string>? ContradictsEvidenceIds { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Result of a hypothesis register operation.
/// </summary>
internal sealed class AiHypothesisRegisterResult
{
    public List<string> AddedIds { get; } = [];
    public List<string> UpdatedIds { get; } = [];
    public int InvalidItems { get; set; }
    public int IgnoredDuplicates { get; set; }
    public List<string> IgnoredDuplicateIds { get; } = [];
    public int IgnoredAtCapacity { get; set; }
}

/// <summary>
/// Result of a hypothesis update operation.
/// </summary>
internal sealed class AiHypothesisUpdateResult
{
    public List<string> UpdatedIds { get; } = [];
    public int InvalidItems { get; set; }
    public List<string> UnknownHypothesisIds { get; set; } = [];
    public List<string> UnknownEvidenceIds { get; set; } = [];
}
