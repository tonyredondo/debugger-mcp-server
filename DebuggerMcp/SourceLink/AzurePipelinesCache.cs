using System.Text.Json;
using System.Text.Json.Serialization;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Cache entry for a build lookup.
/// </summary>
public class CachedBuildEntry
{
    /// <summary>
    /// Gets or sets when this entry was cached.
    /// </summary>
    [JsonPropertyName("cachedAt")]
    public DateTime CachedAt { get; set; }

    /// <summary>
    /// Gets or sets the build info (null if not found).
    /// </summary>
    [JsonPropertyName("build")]
    public AzurePipelinesBuildInfo? Build { get; set; }
}

/// <summary>
/// Cache entry for artifacts lookup.
/// </summary>
public class CachedArtifactsEntry
{
    /// <summary>
    /// Gets or sets when this entry was cached.
    /// </summary>
    [JsonPropertyName("cachedAt")]
    public DateTime CachedAt { get; set; }

    /// <summary>
    /// Gets or sets the artifacts list.
    /// </summary>
    [JsonPropertyName("artifacts")]
    public List<AzurePipelinesArtifact> Artifacts { get; set; } = new();
}

/// <summary>
/// Cache for Azure Pipelines build and artifact information.
/// Persists to disk to avoid redundant API calls across sessions.
/// </summary>
public class AzurePipelinesCache
{
    private const string CacheFileName = "azure_pipelines_cache.json";
    private const int CacheExpirationHours = 1; // Cache entries expire after 1 hour

    /// <summary>
    /// Gets or sets when the cache was last updated.
    /// </summary>
    [JsonPropertyName("lastUpdated")]
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// Maps commit SHA to build info with timestamp.
    /// Key format: "{organization}/{project}/{commitSha}"
    /// </summary>
    [JsonPropertyName("builds")]
    public Dictionary<string, CachedBuildEntry> Builds { get; set; } = new();

    /// <summary>
    /// Maps build ID to artifact list with timestamp.
    /// Key format: "{organization}/{project}/{buildId}"
    /// </summary>
    [JsonPropertyName("artifacts")]
    public Dictionary<string, CachedArtifactsEntry> Artifacts { get; set; } = new();

    /// <summary>
    /// Tracks which symbols have been downloaded.
    /// Key format: "{commitSha}/{platformSuffix}"
    /// Value: local directory path
    /// </summary>
    [JsonPropertyName("downloadedSymbols")]
    public Dictionary<string, string> DownloadedSymbols { get; set; } = new();

    /// <summary>
    /// Gets the cache key for a build lookup.
    /// </summary>
    public static string GetBuildCacheKey(string organization, string project, string commitSha)
        => $"{organization}/{project}/{commitSha}".ToLowerInvariant();

    /// <summary>
    /// Gets the cache key for an artifact lookup.
    /// </summary>
    public static string GetArtifactCacheKey(string organization, string project, int buildId)
        => $"{organization}/{project}/{buildId}".ToLowerInvariant();

    /// <summary>
    /// Gets the cache key for downloaded symbols.
    /// </summary>
    public static string GetSymbolsCacheKey(string commitSha, string platformSuffix)
        => $"{commitSha}/{platformSuffix}".ToLowerInvariant();

    /// <summary>
    /// Loads the cache from disk.
    /// </summary>
    /// <param name="cacheDirectory">Directory containing the cache file.</param>
    /// <returns>Loaded cache or new empty cache if file doesn't exist or is invalid.</returns>
    public static AzurePipelinesCache Load(string? cacheDirectory)
    {
        if (string.IsNullOrEmpty(cacheDirectory))
            // No cache location configured, return in-memory cache only
            return new AzurePipelinesCache();

        var cachePath = Path.Combine(cacheDirectory, CacheFileName);

        if (!File.Exists(cachePath))
            // First run or cache cleared; start clean
            return new AzurePipelinesCache();

        try
        {
            var json = File.ReadAllText(cachePath);
            return JsonSerializer.Deserialize<AzurePipelinesCache>(json) ?? new AzurePipelinesCache();
        }
        catch
        {
            // If cache is corrupted, start fresh to avoid poisoning lookups
            return new AzurePipelinesCache();
        }
    }

    /// <summary>
    /// Saves the cache to disk.
    /// </summary>
    /// <param name="cacheDirectory">Directory to save the cache file.</param>
    public void Save(string? cacheDirectory)
    {
        if (string.IsNullOrEmpty(cacheDirectory))
            // Persistence disabled - keep cache in memory only
            return;

        try
        {
            Directory.CreateDirectory(cacheDirectory);
            LastUpdated = DateTime.UtcNow;

            var cachePath = Path.Combine(cacheDirectory, CacheFileName);
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(cachePath, json);
        }
        catch
        {
            // Cache writes are opportunistic; do not fail symbol download flow
        }
    }

    /// <summary>
    /// Tries to get a cached build by commit SHA.
    /// </summary>
    public bool TryGetBuild(string organization, string project, string commitSha, out AzurePipelinesBuildInfo? build)
    {
        build = null;

        var key = GetBuildCacheKey(organization, project, commitSha);
        if (Builds.TryGetValue(key, out var entry) && entry != null)
        {
            if (entry.CachedAt.AddHours(CacheExpirationHours) < DateTime.UtcNow)
            {
                // Drop stale entry to force a fresh lookup
                Builds.Remove(key);
                return false;
            }

            build = entry.Build;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Caches a build result (can be null if not found).
    /// </summary>
    public void SetBuild(string organization, string project, string commitSha, AzurePipelinesBuildInfo? build)
    {
        var key = GetBuildCacheKey(organization, project, commitSha);
        Builds[key] = new CachedBuildEntry
        {
            CachedAt = DateTime.UtcNow,
            Build = build
        };
    }

    /// <summary>
    /// Tries to get cached artifacts for a build.
    /// </summary>
    public bool TryGetArtifacts(string organization, string project, int buildId, out List<AzurePipelinesArtifact>? artifacts)
    {
        artifacts = null;

        var key = GetArtifactCacheKey(organization, project, buildId);
        if (Artifacts.TryGetValue(key, out var entry) && entry != null)
        {
            if (entry.CachedAt.AddHours(CacheExpirationHours) < DateTime.UtcNow)
            {
                // Force refetch when the cache window has expired
                Artifacts.Remove(key);
                return false;
            }

            artifacts = entry.Artifacts;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Caches artifacts for a build.
    /// </summary>
    public void SetArtifacts(string organization, string project, int buildId, List<AzurePipelinesArtifact> artifacts)
    {
        var key = GetArtifactCacheKey(organization, project, buildId);
        Artifacts[key] = new CachedArtifactsEntry
        {
            CachedAt = DateTime.UtcNow,
            Artifacts = artifacts
        };
    }

    /// <summary>
    /// Checks if symbols have already been downloaded for a commit/platform.
    /// </summary>
    public bool HasDownloadedSymbols(string commitSha, string platformSuffix, out string? symbolDirectory)
    {
        symbolDirectory = null;

        var key = GetSymbolsCacheKey(commitSha, platformSuffix);
        if (DownloadedSymbols.TryGetValue(key, out symbolDirectory))
        {
            // Verify the directory still exists
            if (Directory.Exists(symbolDirectory))
                return true;

            // Directory was deleted, remove from cache
            DownloadedSymbols.Remove(key);
        }

        symbolDirectory = null;
        return false;
    }

    /// <summary>
    /// Records that symbols have been downloaded.
    /// </summary>
    public void SetDownloadedSymbols(string commitSha, string platformSuffix, string symbolDirectory)
    {
        var key = GetSymbolsCacheKey(commitSha, platformSuffix);
        DownloadedSymbols[key] = symbolDirectory;
    }
}
