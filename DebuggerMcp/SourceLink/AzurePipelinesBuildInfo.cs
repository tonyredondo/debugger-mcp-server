using System.Text.Json.Serialization;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Build information from Azure Pipelines.
/// </summary>
public class AzurePipelinesBuildInfo
{
    /// <summary>
    /// Gets or sets the build ID.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    /// <summary>
    /// Gets or sets the build number (version string).
    /// </summary>
    [JsonPropertyName("buildNumber")]
    public string BuildNumber { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the build status (completed, inProgress, cancelling, postponed, notStarted, none).
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the build result (succeeded, partiallySucceeded, failed, canceled, none).
    /// </summary>
    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the source version (commit SHA).
    /// </summary>
    [JsonPropertyName("sourceVersion")]
    public string SourceVersion { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the source branch (e.g., refs/heads/master).
    /// </summary>
    [JsonPropertyName("sourceBranch")]
    public string SourceBranch { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the finish time of the build.
    /// </summary>
    [JsonPropertyName("finishTime")]
    public DateTime? FinishTime { get; set; }
    
    /// <summary>
    /// Gets or sets the queue time of the build.
    /// </summary>
    [JsonPropertyName("queueTime")]
    public DateTime? QueueTime { get; set; }
    
    /// <summary>
    /// Gets or sets the start time of the build.
    /// </summary>
    [JsonPropertyName("startTime")]
    public DateTime? StartTime { get; set; }
    
    /// <summary>
    /// Gets or sets the web URL to view the build in Azure DevOps.
    /// </summary>
    public string WebUrl { get; set; } = string.Empty;
}

/// <summary>
/// Artifact information from Azure Pipelines.
/// </summary>
public class AzurePipelinesArtifact
{
    /// <summary>
    /// Gets or sets the artifact ID.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    /// <summary>
    /// Gets or sets the artifact name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the download URL for the artifact.
    /// </summary>
    public string DownloadUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the artifact size in bytes (if available).
    /// </summary>
    public long? Size { get; set; }
    
    /// <summary>
    /// Gets or sets the resource type (Container, FilePath, etc.).
    /// </summary>
    public string ResourceType { get; set; } = string.Empty;
}

/// <summary>
/// Result of downloading Datadog symbols from Azure Pipelines.
/// </summary>
public class DatadogSymbolDownloadResult
{
    /// <summary>
    /// Gets or sets whether the download was successful.
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// Gets or sets the Azure Pipelines build ID.
    /// </summary>
    public int? BuildId { get; set; }
    
    /// <summary>
    /// Gets or sets the build number (version).
    /// </summary>
    public string? BuildNumber { get; set; }
    
    /// <summary>
    /// Gets or sets the URL to the build in Azure DevOps.
    /// </summary>
    public string? BuildUrl { get; set; }
    
    /// <summary>
    /// Gets or sets the list of downloaded artifact names.
    /// </summary>
    public List<string> DownloadedArtifacts { get; set; } = new();
    
    /// <summary>
    /// Gets or sets the merge result containing extracted files info.
    /// </summary>
    public ArtifactMergeResult? MergeResult { get; set; }
    
    /// <summary>
    /// Gets or sets the error message if download failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// Gets or sets whether symbols were downloaded using version tag instead of exact commit SHA.
    /// True when the exact commit wasn't found and we fell back to version-based lookup.
    /// </summary>
    public bool ShaMismatch { get; set; }
    
    /// <summary>
    /// Gets or sets the source type (e.g., "AzurePipelines", "GitHubReleases").
    /// </summary>
    public string? Source { get; set; }
}
