using System.Text.Json.Serialization;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Represents commit metadata fetched from GitHub API.
/// </summary>
public class GitHubCommitInfo
{
    /// <summary>
    /// Gets or sets the commit SHA hash.
    /// </summary>
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    /// <summary>
    /// Gets or sets the commit author's name.
    /// </summary>
    [JsonPropertyName("authorName")]
    public string? AuthorName { get; set; }

    /// <summary>
    /// Gets or sets the commit author's date (ISO 8601).
    /// </summary>
    [JsonPropertyName("authorDate")]
    public string? AuthorDate { get; set; }

    /// <summary>
    /// Gets or sets the committer's name.
    /// </summary>
    [JsonPropertyName("committerName")]
    public string? CommitterName { get; set; }

    /// <summary>
    /// Gets or sets the committer's date (ISO 8601).
    /// </summary>
    [JsonPropertyName("committerDate")]
    public string? CommitterDate { get; set; }

    /// <summary>
    /// Gets or sets the commit message.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the URL to the commit tree on GitHub.
    /// </summary>
    [JsonPropertyName("treeUrl")]
    public string? TreeUrl { get; set; }
}

