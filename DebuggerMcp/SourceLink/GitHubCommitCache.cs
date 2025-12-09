using System.Text.Json.Serialization;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Persistent cache for GitHub commit metadata.
/// Stored in dump metadata folder to avoid repeated API calls.
/// </summary>
public class GitHubCommitCache
{
    /// <summary>
    /// Cache format version for future compatibility.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// When the cache was last updated.
    /// </summary>
    [JsonPropertyName("lastUpdated")]
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// Cached commit information keyed by "owner/repo/commitHash" (lowercase).
    /// </summary>
    [JsonPropertyName("commits")]
    public Dictionary<string, GitHubCommitInfo?> Commits { get; set; } = new();
}

