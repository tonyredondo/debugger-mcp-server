using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DebuggerMcp.Analysis;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Resolves GitHub commit metadata for assemblies.
/// Implements caching to avoid GitHub API rate limits.
/// </summary>
public class GitHubCommitResolver : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;
    private readonly string? _cacheDirectory;
    private readonly string? _githubToken;
    private readonly bool _ownsHttpClient;
    private GitHubCommitCache _cache;
    private bool _cacheModified;

    // Rate limit tracking
    private int _remainingRequests = 60;
    private DateTime _rateLimitReset = DateTime.MinValue;

    // Constants
    private const int MaxCommitMessageLength = 1000;
    private const string CacheFileName = "github_commit_cache.json";
    private const string GitHubApiBase = "https://api.github.com";
    private const string UserAgent = "DebuggerMcp/1.0";

    /// <summary>
    /// Regex for parsing GitHub URLs.
    /// Matches: github.com/owner/repo, github.com:owner/repo, etc.
    /// </summary>
    private static readonly Regex GitHubUrlRegex = new(
        @"github\.com[/:]([^/]+)/([^/\.\s\?#]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubCommitResolver"/> class.
    /// </summary>
    /// <param name="cacheDirectory">Directory to store cache file, or null for no persistence.</param>
    /// <param name="githubToken">Optional GitHub token for higher rate limits.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="httpClient">Optional HttpClient for testing.</param>
    public GitHubCommitResolver(
        string? cacheDirectory = null,
        string? githubToken = null,
        ILogger? logger = null,
        HttpClient? httpClient = null)
    {
        _cacheDirectory = cacheDirectory;
        _githubToken = githubToken;
        _logger = logger;
        _cache = new GitHubCommitCache();

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient();
            _ownsHttpClient = true;
        }

        LoadCache();
    }

    /// <summary>
    /// Resolves the source URL for an assembly, preferring SourceCommitUrl if available.
    /// </summary>
    /// <param name="assembly">The assembly to resolve.</param>
    /// <returns>The source URL or null if not resolvable.</returns>
    public string? ResolveSourceUrl(AssemblyVersionInfo assembly)
    {
        // Priority 1: Use SourceCommitUrl from customAttributes if available
        if (assembly.CustomAttributes?.TryGetValue("SourceCommitUrl", out var sourceCommitUrl) == true
            && !string.IsNullOrEmpty(sourceCommitUrl))
        {
            return sourceCommitUrl;
        }

        // Priority 2: Construct from repositoryUrl + commitHash
        if (!string.IsNullOrEmpty(assembly.RepositoryUrl) && !string.IsNullOrEmpty(assembly.CommitHash))
        {
            var ownerRepo = ExtractGitHubOwnerRepo(assembly.RepositoryUrl);
            if (ownerRepo != null)
            {
                // Build a tree URL when we know both repo and commit hash
                return $"https://github.com/{ownerRepo}/tree/{assembly.CommitHash}";
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts owner/repo from a GitHub URL.
    /// </summary>
    /// <param name="url">The URL to parse.</param>
    /// <returns>Owner/repo string (e.g., "DataDog/dd-trace-dotnet") or null if not a GitHub URL.</returns>
    public static string? ExtractGitHubOwnerRepo(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        var match = GitHubUrlRegex.Match(url);
        if (!match.Success)
            return null;

        var owner = match.Groups[1].Value;
        var repo = match.Groups[2].Value;

        // Remove .git suffix if present
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            // Normalize to repository name without clone suffix
            repo = repo[..^4];
        }

        return $"{owner}/{repo}";
    }

    /// <summary>
    /// Fetches commit metadata from GitHub API with caching.
    /// </summary>
    /// <param name="ownerRepo">Owner/repo (e.g., "DataDog/dd-trace-dotnet").</param>
    /// <param name="commitHash">The commit SHA hash.</param>
    /// <returns>Commit info or null if not found/error.</returns>
    public async Task<GitHubCommitInfo?> FetchCommitInfoAsync(string ownerRepo, string commitHash)
    {
        var cacheKey = $"{ownerRepo}/{commitHash}".ToLowerInvariant();

        // Check cache first
        if (_cache.Commits.TryGetValue(cacheKey, out var cached))
        {
            // Avoid hitting GitHub when we already have a result (including null)
            _logger?.LogDebug("GitHub cache hit for {Key}", cacheKey);
            return cached;
        }

        // Check rate limit
        if (_remainingRequests <= 1 && DateTime.UtcNow < _rateLimitReset)
        {
            // Stop early when GitHub told us to back off
            _logger?.LogWarning("GitHub API rate limit reached ({Remaining} remaining), reset at {Reset}",
                _remainingRequests, _rateLimitReset);
            return null;
        }

        var url = $"{GitHubApiBase}/repos/{ownerRepo}/commits/{commitHash}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", UserAgent);
            request.Headers.Add("Accept", "application/vnd.github.v3+json");

            if (!string.IsNullOrEmpty(_githubToken))
            {
                request.Headers.Add("Authorization", $"Bearer {_githubToken}");
            }

            _logger?.LogDebug("Fetching GitHub commit: {Url}", url);
            var response = await _httpClient.SendAsync(request);

            // Update rate limit info
            UpdateRateLimitFromHeaders(response.Headers);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger?.LogDebug("GitHub commit not found: {OwnerRepo}/{Hash}", ownerRepo, commitHash);
                // Cache null to avoid repeated calls
                _cache.Commits[cacheKey] = null;
                _cacheModified = true;
                return null;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden && _remainingRequests <= 0)
            {
                // Stop hammering API when GitHub has already blocked us
                _logger?.LogWarning("GitHub API rate limit exceeded");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Avoid caching here so transient errors can succeed later
                _logger?.LogWarning("GitHub API returned {Status} for {Url}", response.StatusCode, url);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var info = ParseCommitResponse(json, ownerRepo, commitHash);

            if (info != null)
            {
                _cache.Commits[cacheKey] = info;
                _cacheModified = true;
                _logger?.LogDebug("Cached GitHub commit: {Key}", cacheKey);
            }

            return info;
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "Network error fetching GitHub commit: {Url}", url);
            return null;
        }
        catch (TaskCanceledException ex)
        {
            _logger?.LogWarning(ex, "Timeout fetching GitHub commit: {Url}", url);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch commit info from GitHub: {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Parses the GitHub API commit response.
    /// </summary>
    private GitHubCommitInfo? ParseCommitResponse(string json, string ownerRepo, string commitHash)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("commit", out var commit))
                // Unexpected payload shape
                return null;

            if (!commit.TryGetProperty("author", out var author))
                // Missing author block means the commit payload is incomplete
                return null;

            if (!commit.TryGetProperty("committer", out var committer))
                // Same for committer data
                return null;

            var message = commit.TryGetProperty("message", out var msgProp)
                ? msgProp.GetString()
                : null;

            // Truncate long messages
            if (message?.Length > MaxCommitMessageLength)
            {
                // Avoid large payloads bloating our cache or UI
                message = message[..MaxCommitMessageLength] + "...";
            }

            return new GitHubCommitInfo
            {
                Sha = commitHash,
                AuthorName = author.TryGetProperty("name", out var authorName)
                    ? authorName.GetString()
                    : null,
                AuthorDate = author.TryGetProperty("date", out var authorDate)
                    ? authorDate.GetString()
                    : null,
                CommitterName = committer.TryGetProperty("name", out var committerName)
                    ? committerName.GetString()
                    : null,
                CommitterDate = committer.TryGetProperty("date", out var committerDate)
                    ? committerDate.GetString()
                    : null,
                Message = message,
                TreeUrl = $"https://github.com/{ownerRepo}/tree/{commitHash}"
            };
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Failed to parse GitHub commit response");
            return null;
        }
    }

    /// <summary>
    /// Updates rate limit tracking from GitHub response headers.
    /// </summary>
    private void UpdateRateLimitFromHeaders(HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("X-RateLimit-Remaining", out var remaining))
        {
            if (int.TryParse(remaining.FirstOrDefault(), out var value))
            {
                // Track how many requests we can still make
                _remainingRequests = value;
            }
        }

        if (headers.TryGetValues("X-RateLimit-Reset", out var reset))
        {
            if (long.TryParse(reset.FirstOrDefault(), out var unixTime))
            {
                // GitHub returns epoch seconds; convert to UTC timestamp
                _rateLimitReset = DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime;
            }
        }

        _logger?.LogDebug("GitHub rate limit: {Remaining} remaining, resets at {Reset}",
            _remainingRequests, _rateLimitReset);
    }

    /// <summary>
    /// Loads the cache from disk.
    /// </summary>
    private void LoadCache()
    {
        _cache = new GitHubCommitCache();

        if (string.IsNullOrEmpty(_cacheDirectory))
            return;

        var cachePath = Path.Combine(_cacheDirectory, CacheFileName);

        if (!File.Exists(cachePath))
            return;

        try
        {
            var json = File.ReadAllText(cachePath);
            _cache = JsonSerializer.Deserialize<GitHubCommitCache>(json) ?? new GitHubCommitCache();
            _logger?.LogDebug("Loaded GitHub commit cache with {Count} entries from {Path}",
                _cache.Commits.Count, cachePath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load GitHub commit cache from {Path}, starting fresh", cachePath);
            _cache = new GitHubCommitCache();
        }
    }

    /// <summary>
    /// Saves the cache to disk if modified.
    /// </summary>
    public void SaveCache()
    {
        if (!_cacheModified || string.IsNullOrEmpty(_cacheDirectory))
            return;

        var cachePath = Path.Combine(_cacheDirectory, CacheFileName);

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            _cache.LastUpdated = DateTime.UtcNow;

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_cache, options);
            File.WriteAllText(cachePath, json);

            _cacheModified = false;
            _logger?.LogDebug("Saved GitHub commit cache with {Count} entries to {Path}",
                _cache.Commits.Count, cachePath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save GitHub commit cache to {Path}", cachePath);
        }
    }

    /// <summary>
    /// Gets the current rate limit status.
    /// </summary>
    /// <returns>Tuple of (remaining requests, reset time).</returns>
    public (int remaining, DateTime resetTime) GetRateLimitStatus()
    {
        return (_remainingRequests, _rateLimitReset);
    }

    /// <summary>
    /// Disposes resources and saves the cache.
    /// </summary>
    public void Dispose()
    {
        SaveCache();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
