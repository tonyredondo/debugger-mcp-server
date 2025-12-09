using System.Text.Json;
using System.Text.Json.Serialization;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Cached GitHub release information.
/// </summary>
public class CachedGitHubRelease
{
    /// <summary>
    /// Gets or sets the release ID.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the tag name.
    /// </summary>
    public string TagName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the release name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target commit SHA.
    /// </summary>
    public string? TargetCommitish { get; set; }

    /// <summary>
    /// Gets or sets the release URL.
    /// </summary>
    public string HtmlUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the release was published.
    /// </summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>
    /// Gets or sets the asset names and download URLs.
    /// </summary>
    public Dictionary<string, string> Assets { get; set; } = new();
}

/// <summary>
/// Cache entry for GitHub releases.
/// </summary>
public class GitHubReleaseCacheEntry
{
    /// <summary>
    /// Gets or sets when this entry was cached.
    /// </summary>
    public DateTime CachedAt { get; set; }

    /// <summary>
    /// Gets or sets the release info (null if not found).
    /// </summary>
    public CachedGitHubRelease? Release { get; set; }
}

/// <summary>
/// Persistent cache for GitHub releases API responses.
/// Reduces API calls and respects rate limits.
/// </summary>
public class GitHubReleasesCache
{
    private const string CacheFileName = "github_releases_cache.json";
    private const int CacheExpirationHours = 1; // Cache entries expire after 1 hour

    private readonly string _cacheFilePath;
    private Dictionary<string, GitHubReleaseCacheEntry> _releasesByVersion = new();
    private Dictionary<string, GitHubReleaseCacheEntry> _releasesByCommit = new();
    private Dictionary<string, string> _downloadedSymbols = new(); // key: "{version}/{platform}", value: directory path

    /// <summary>
    /// Creates a new GitHub releases cache.
    /// </summary>
    /// <param name="cacheDirectory">Directory to store the cache file.</param>
    public GitHubReleasesCache(string cacheDirectory)
    {
        Directory.CreateDirectory(cacheDirectory);
        _cacheFilePath = Path.Combine(cacheDirectory, CacheFileName);
        Load();
    }

    /// <summary>
    /// Loads the cache from disk.
    /// </summary>
    private void Load()
    {
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                var json = File.ReadAllText(_cacheFilePath);
                var data = JsonSerializer.Deserialize<GitHubCacheData>(json);
                if (data != null)
                {
                    _releasesByVersion = data.ReleasesByVersion ?? new();
                    _releasesByCommit = data.ReleasesByCommit ?? new();
                    _downloadedSymbols = data.DownloadedSymbols ?? new();

                    // Clean expired entries
                    CleanExpiredEntries();
                }
            }
        }
        catch
        {
            // Ignore cache load errors
            _releasesByVersion = new();
            _releasesByCommit = new();
            _downloadedSymbols = new();
        }
    }

    /// <summary>
    /// Saves the cache to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            var data = new GitHubCacheData
            {
                ReleasesByVersion = _releasesByVersion,
                ReleasesByCommit = _releasesByCommit,
                DownloadedSymbols = _downloadedSymbols
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cacheFilePath, json);
        }
        catch
        {
            // Ignore cache save errors
        }
    }

    /// <summary>
    /// Cleans expired cache entries.
    /// </summary>
    private void CleanExpiredEntries()
    {
        var cutoff = DateTime.UtcNow.AddHours(-CacheExpirationHours);

        var expiredVersionKeys = _releasesByVersion
            .Where(kvp => kvp.Value.CachedAt < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredVersionKeys)
        {
            _releasesByVersion.Remove(key);
        }

        var expiredCommitKeys = _releasesByCommit
            .Where(kvp => kvp.Value.CachedAt < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredCommitKeys)
        {
            _releasesByCommit.Remove(key);
        }
    }

    /// <summary>
    /// Tries to get a cached release by version.
    /// </summary>
    /// <param name="owner">Repository owner.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="version">Version string (e.g., "3.31.0").</param>
    /// <param name="release">The cached release if found.</param>
    /// <returns>True if found in cache, false otherwise.</returns>
    public bool TryGetReleaseByVersion(string owner, string repo, string version, out GitHubReleaseInfo? release)
    {
        var key = $"{owner}/{repo}/v{version}";
        if (_releasesByVersion.TryGetValue(key, out var entry))
        {
            release = entry.Release != null ? ConvertToReleaseInfo(entry.Release) : null;
            return true;
        }

        release = null;
        return false;
    }

    /// <summary>
    /// Sets a cached release by version.
    /// </summary>
    public void SetReleaseByVersion(string owner, string repo, string version, GitHubReleaseInfo? release)
    {
        var key = $"{owner}/{repo}/v{version}";
        _releasesByVersion[key] = new GitHubReleaseCacheEntry
        {
            CachedAt = DateTime.UtcNow,
            Release = release != null ? ConvertToCachedRelease(release) : null
        };
    }

    /// <summary>
    /// Tries to get a cached release by commit SHA.
    /// </summary>
    /// <param name="owner">Repository owner.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="commitSha">Commit SHA.</param>
    /// <param name="release">The cached release if found.</param>
    /// <returns>True if found in cache, false otherwise.</returns>
    public bool TryGetReleaseByCommit(string owner, string repo, string commitSha, out GitHubReleaseInfo? release)
    {
        var key = $"{owner}/{repo}/{commitSha}";
        if (_releasesByCommit.TryGetValue(key, out var entry))
        {
            release = entry.Release != null ? ConvertToReleaseInfo(entry.Release) : null;
            return true;
        }

        release = null;
        return false;
    }

    /// <summary>
    /// Sets a cached release by commit SHA.
    /// </summary>
    public void SetReleaseByCommit(string owner, string repo, string commitSha, GitHubReleaseInfo? release)
    {
        var key = $"{owner}/{repo}/{commitSha}";
        _releasesByCommit[key] = new GitHubReleaseCacheEntry
        {
            CachedAt = DateTime.UtcNow,
            Release = release != null ? ConvertToCachedRelease(release) : null
        };
    }

    /// <summary>
    /// Checks if symbols have already been downloaded for a version/platform combo.
    /// </summary>
    public bool HasDownloadedSymbols(string version, string platformSuffix, out string? directory)
    {
        var key = $"{version}/{platformSuffix}";
        if (_downloadedSymbols.TryGetValue(key, out directory))
        {
            return Directory.Exists(directory);
        }

        directory = null;
        return false;
    }

    /// <summary>
    /// Records that symbols have been downloaded.
    /// </summary>
    public void SetDownloadedSymbols(string version, string platformSuffix, string directory)
    {
        var key = $"{version}/{platformSuffix}";
        _downloadedSymbols[key] = directory;
    }

    /// <summary>
    /// Converts a GitHubReleaseInfo to a CachedGitHubRelease.
    /// </summary>
    private static CachedGitHubRelease ConvertToCachedRelease(GitHubReleaseInfo release)
    {
        return new CachedGitHubRelease
        {
            Id = release.Id,
            TagName = release.TagName,
            Name = release.Name,
            TargetCommitish = release.TargetCommitish,
            HtmlUrl = release.HtmlUrl,
            PublishedAt = release.PublishedAt,
            Assets = release.Assets.ToDictionary(a => a.Name, a => a.BrowserDownloadUrl)
        };
    }

    /// <summary>
    /// Converts a CachedGitHubRelease to a GitHubReleaseInfo.
    /// </summary>
    private static GitHubReleaseInfo ConvertToReleaseInfo(CachedGitHubRelease cached)
    {
        var release = new GitHubReleaseInfo
        {
            Id = cached.Id,
            TagName = cached.TagName,
            Name = cached.Name,
            TargetCommitish = cached.TargetCommitish,
            HtmlUrl = cached.HtmlUrl,
            PublishedAt = cached.PublishedAt
        };

        foreach (var (name, url) in cached.Assets)
        {
            release.Assets.Add(new GitHubReleaseAsset
            {
                Name = name,
                BrowserDownloadUrl = url
            });
        }

        return release;
    }

    /// <summary>
    /// Cache data structure for serialization.
    /// </summary>
    private class GitHubCacheData
    {
        [JsonPropertyName("releasesByVersion")]
        public Dictionary<string, GitHubReleaseCacheEntry>? ReleasesByVersion { get; set; }

        [JsonPropertyName("releasesByCommit")]
        public Dictionary<string, GitHubReleaseCacheEntry>? ReleasesByCommit { get; set; }

        [JsonPropertyName("downloadedSymbols")]
        public Dictionary<string, string>? DownloadedSymbols { get; set; }
    }
}

