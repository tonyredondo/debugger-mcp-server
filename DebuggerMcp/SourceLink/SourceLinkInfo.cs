using System.Text.Json.Serialization;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Represents Source Link information extracted from a PDB file.
/// </summary>
public class SourceLinkInfo
{
    /// <summary>
    /// Gets or sets the document mappings from local paths to URLs.
    /// The key is a glob pattern (e.g., "/src/*") and the value is a URL template.
    /// </summary>
    [JsonPropertyName("documents")]
    public Dictionary<string, string> Documents { get; set; } = new();
}

/// <summary>
/// Represents a resolved source location with a URL.
/// </summary>
public class SourceLocation
{
    /// <summary>
    /// Gets or sets the original source file path (from the PDB).
    /// </summary>
    [JsonPropertyName("sourceFile")]
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the line number in the source file.
    /// </summary>
    [JsonPropertyName("lineNumber")]
    public int LineNumber { get; set; }

    /// <summary>
    /// Gets or sets the column number in the source file (if available).
    /// </summary>
    [JsonPropertyName("columnNumber")]
    public int? ColumnNumber { get; set; }

    /// <summary>
    /// Gets or sets the resolved URL to the source file.
    /// This is a browsable URL (e.g., GitHub blob URL with line number).
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// Gets or sets the raw URL from Source Link (before line number formatting).
    /// This is typically a raw content URL.
    /// </summary>
    [JsonPropertyName("rawUrl")]
    public string? RawUrl { get; set; }

    /// <summary>
    /// Gets or sets the source control provider (GitHub, GitLab, Azure DevOps, etc.).
    /// </summary>
    [JsonPropertyName("provider")]
    public SourceProvider Provider { get; set; } = SourceProvider.Unknown;

    /// <summary>
    /// Gets or sets whether the source link was successfully resolved.
    /// </summary>
    [JsonPropertyName("resolved")]
    public bool Resolved { get; set; }

    /// <summary>
    /// Gets or sets an error message if resolution failed.
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Supported source control providers for URL formatting.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceProvider
{
    /// <summary>
    /// Unknown or unsupported provider.
    /// </summary>
    Unknown,

    /// <summary>
    /// GitHub (github.com or GitHub Enterprise).
    /// </summary>
    GitHub,

    /// <summary>
    /// GitLab (gitlab.com or self-hosted).
    /// </summary>
    GitLab,

    /// <summary>
    /// Azure DevOps / Azure Repos.
    /// </summary>
    AzureDevOps,

    /// <summary>
    /// Bitbucket.
    /// </summary>
    Bitbucket,

    /// <summary>
    /// Generic Git repository.
    /// </summary>
    Generic
}

/// <summary>
/// Cache entry for Source Link information per module.
/// </summary>
public class ModuleSourceLinkCache
{
    /// <summary>
    /// Gets or sets the module name.
    /// </summary>
    public string ModuleName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the PDB path (if found).
    /// </summary>
    public string? PdbPath { get; set; }

    /// <summary>
    /// Gets or sets whether Source Link info was found.
    /// </summary>
    public bool HasSourceLink { get; set; }

    /// <summary>
    /// Gets or sets the Source Link info (if available).
    /// </summary>
    public SourceLinkInfo? SourceLink { get; set; }

    /// <summary>
    /// Gets or sets when this cache entry was created.
    /// </summary>
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;
}

