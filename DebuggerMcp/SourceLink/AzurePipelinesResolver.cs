using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DebuggerMcp.Analysis;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Resolves Azure Pipelines builds and downloads artifacts for Datadog symbol retrieval.
/// </summary>
public class AzurePipelinesResolver : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;
    private readonly string? _cacheDirectory;
    private readonly bool _ownsHttpClient;
    private AzurePipelinesCache _cache;
    private bool _cacheModified;
    
    private const string UserAgent = "DebuggerMcp/1.0";
    
    /// <summary>
    /// Initializes a new instance of the <see cref="AzurePipelinesResolver"/> class.
    /// </summary>
    /// <param name="cacheDirectory">Directory for caching build metadata, or null for no persistence.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="httpClient">Optional HttpClient for testing.</param>
    public AzurePipelinesResolver(
        string? cacheDirectory = null,
        ILogger? logger = null,
        HttpClient? httpClient = null)
    {
        _cacheDirectory = cacheDirectory ?? DatadogTraceSymbolsConfig.GetCacheDirectory();
        _logger = logger;
        _cache = AzurePipelinesCache.Load(_cacheDirectory);
        
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(DatadogTraceSymbolsConfig.GetTimeoutSeconds())
            };
            _ownsHttpClient = true;
        }
    }
    
    /// <summary>
    /// Finds a build by commit SHA, with fallback to version tag lookup.
    /// </summary>
    /// <param name="organization">Azure DevOps organization name.</param>
    /// <param name="project">Azure DevOps project name.</param>
    /// <param name="commitSha">The commit SHA to search for.</param>
    /// <param name="version">Optional version string (e.g., "3.31.0") for tag-based fallback lookup.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Build info if found, null otherwise.</returns>
    public async Task<AzurePipelinesBuildInfo?> FindBuildByCommitAsync(
        string organization,
        string project,
        string commitSha,
        string? version = null,
        CancellationToken ct = default)
    {
        // Check cache first, but validate the cached build's sourceVersion matches
        var skipCommitLookup = false;
        if (_cache.TryGetBuild(organization, project, commitSha, out var cachedBuild))
        {
            if (cachedBuild == null)
            {
                // Cached "not found" result - but if we have a version, skip commit lookup and go straight to tag lookup
                if (!string.IsNullOrEmpty(version))
                {
                    _logger?.LogInformation("Cache has null for commit {Commit}, but we have version {Version} - skipping to tag lookup", 
                        DatadogTraceSymbolsConfig.GetShortSha(commitSha), version);
                    skipCommitLookup = true;
                    // Fall through to tag lookup below
                }
                else
                {
                    _logger?.LogDebug("Cache hit (null) for build: {Org}/{Project}/{Commit}", organization, project, DatadogTraceSymbolsConfig.GetShortSha(commitSha));
                    return null;
                }
            }
            else
            {
                // Validate the cached build's sourceVersion matches our expected commit
                var cachedSv = cachedBuild.SourceVersion ?? "";
                var isMatch = cachedSv.StartsWith(commitSha, StringComparison.OrdinalIgnoreCase) ||
                             commitSha.StartsWith(cachedSv, StringComparison.OrdinalIgnoreCase);
                
                if (isMatch)
                {
                    _logger?.LogDebug("Cache hit for build {Id}: {Org}/{Project}/{Commit}", cachedBuild.Id, organization, project, DatadogTraceSymbolsConfig.GetShortSha(commitSha));
                    return cachedBuild;
                }
                else
                {
                    // Cached build has wrong sourceVersion - invalidate and re-fetch
                    _logger?.LogWarning("Cached build {Id} has wrong sourceVersion ({CachedSv}), expected {Expected}. Re-fetching.",
                        cachedBuild.Id, DatadogTraceSymbolsConfig.GetShortSha(cachedSv), DatadogTraceSymbolsConfig.GetShortSha(commitSha));
                    _cache.SetBuild(organization, project, commitSha, null);
                    _cacheModified = true;
                }
            }
        }
        
        // Skip directly to tag lookup if we know commit lookup won't find anything
        if (skipCommitLookup)
        {
            if (!string.IsNullOrEmpty(version))
            {
                _logger?.LogInformation("Trying version tag lookup for v{Version}", version);
                var tagBuild = await FindBuildByVersionTagAsync(organization, project, version, commitSha, ct);
                if (tagBuild != null)
                {
                    // Cache under the commit SHA for future lookups
                    _cache.SetBuild(organization, project, commitSha, tagBuild);
                    _cacheModified = true;
                    return tagBuild;
                }
            }
            return null;
        }
        
        var url = $"{DatadogTraceSymbolsConfig.AzureDevOpsBaseUrl}/{organization}/{project}/_apis/build/builds" +
                  $"?sourceVersion={commitSha}&api-version={DatadogTraceSymbolsConfig.ApiVersion}";
        
        try
        {
            using var request = CreateRequest(HttpMethod.Get, url);
            _logger?.LogDebug("Fetching build for commit: {Url}", url);
            
            var response = await _httpClient.SendAsync(request, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Azure DevOps API returned {Status} for {Url}", response.StatusCode, url);
                
                // Cache null result to avoid repeated failed lookups
                _cache.SetBuild(organization, project, commitSha, null);
                _cacheModified = true;
                return null;
            }
            
            var json = await response.Content.ReadAsStringAsync(ct);
            var build = ParseBuildResponse(json, organization, project, commitSha);
            
            // Cache the result
            _cache.SetBuild(organization, project, commitSha, build);
            _cacheModified = true;
            
            if (build != null)
            {
                _logger?.LogInformation("Found build {BuildNumber} (ID: {BuildId}) for commit {Commit}",
                    build.BuildNumber, build.Id, DatadogTraceSymbolsConfig.GetShortSha(commitSha));
                return build;
            }
            
            // Fallback: Try to find build by version tag (e.g., v3.31.0)
            if (!string.IsNullOrEmpty(version))
            {
                _logger?.LogInformation("No direct commit match, trying version tag lookup for v{Version}", version);
                var tagBuild = await FindBuildByVersionTagAsync(organization, project, version, commitSha, ct);
                if (tagBuild != null)
                {
                    // Cache under the commit SHA for future lookups
                    _cache.SetBuild(organization, project, commitSha, tagBuild);
                    _cacheModified = true;
                    return tagBuild;
                }
            }
            
            _logger?.LogDebug("No build found for commit {Commit}", DatadogTraceSymbolsConfig.GetShortSha(commitSha));
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "Network error fetching build for commit: {Commit}", DatadogTraceSymbolsConfig.GetShortSha(commitSha));
            return null;
        }
        catch (TaskCanceledException ex)
        {
            _logger?.LogWarning(ex, "Timeout fetching build for commit: {Commit}", DatadogTraceSymbolsConfig.GetShortSha(commitSha));
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch build for commit: {Commit}", DatadogTraceSymbolsConfig.GetShortSha(commitSha));
            return null;
        }
    }
    
    /// <summary>
    /// Finds a build by version tag (e.g., v3.31.0).
    /// This is a fallback when the commit SHA lookup doesn't return a direct match.
    /// </summary>
    private async Task<AzurePipelinesBuildInfo?> FindBuildByVersionTagAsync(
        string organization,
        string project,
        string version,
        string expectedCommitSha,
        CancellationToken ct)
    {
        // Try different tag/branch formats that projects commonly use for releases
        var tagFormats = new[]
        {
            // Tag formats
            $"refs/tags/v{version}",
            $"refs/tags/{version}",
            // Branch formats
            $"refs/heads/release/{version}",
            $"refs/heads/release/v{version}",
            $"refs/heads/releases/{version}",
            $"refs/heads/releases/v{version}",
            $"refs/heads/release-{version}",
            $"refs/heads/v{version}",
            // NOTE: Do NOT add refs/heads/master - it returns latest builds, not version-specific
        };
        
        foreach (var tag in tagFormats)
        {
            var url = $"{DatadogTraceSymbolsConfig.AzureDevOpsBaseUrl}/{organization}/{project}/_apis/build/builds" +
                      $"?branchName={Uri.EscapeDataString(tag)}&$top=5&api-version={DatadogTraceSymbolsConfig.ApiVersion}";
            
            try
            {
                using var request = CreateRequest(HttpMethod.Get, url);
                _logger?.LogInformation("Trying version tag lookup: {Tag}", tag);
                
                var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogInformation("Tag {Tag} returned {Status}", tag, response.StatusCode);
                    continue;
                }
                
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("value", out var builds) || builds.GetArrayLength() == 0)
                {
                    _logger?.LogInformation("Tag {Tag} returned no builds", tag);
                    continue;
                }
                
                _logger?.LogInformation("Tag {Tag} returned {Count} builds", tag, builds.GetArrayLength());
                
                // Find a build that matches our expected commit (or any successful build for this tag)
                foreach (var candidate in builds.EnumerateArray())
                {
                    var candidateSv = candidate.TryGetProperty("sourceVersion", out var svProp) ? svProp.GetString() : null;
                    var candidateResult = candidate.TryGetProperty("result", out var resProp) ? resProp.GetString() : null;
                    var candidateId = candidate.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                    
                    _logger?.LogDebug("Tag build candidate: ID={Id}, Result={Result}, SourceVersion={Sv}", 
                        candidateId, candidateResult, DatadogTraceSymbolsConfig.GetShortSha(candidateSv));
                    
                    // Check if commit matches
                    var commitMatches = !string.IsNullOrEmpty(candidateSv) && !string.IsNullOrEmpty(expectedCommitSha) &&
                                       (candidateSv.StartsWith(expectedCommitSha, StringComparison.OrdinalIgnoreCase) ||
                                        expectedCommitSha.StartsWith(candidateSv, StringComparison.OrdinalIgnoreCase));
                    
                    // Prefer builds that match commit, but accept succeeded builds for this tag
                    if (commitMatches || candidateResult == "succeeded")
                    {
                        _logger?.LogInformation("Found build via tag {Tag}: ID={Id}, SourceVersion={Sv}", 
                            tag, candidateId, DatadogTraceSymbolsConfig.GetShortSha(candidateSv));
                        
                        // Parse and return this build
                        return ParseSingleBuild(candidate, organization, project);
                    }
                }
                
                _logger?.LogInformation("Tag {Tag} builds didn't match expected commit or weren't succeeded", tag);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error trying tag {Tag}", tag);
            }
        }
        
        _logger?.LogWarning("Version tag lookup exhausted all formats without finding a matching build");
        
        return null;
    }
    
    /// <summary>
    /// Parses a single build from a JSON element.
    /// </summary>
    private AzurePipelinesBuildInfo ParseSingleBuild(JsonElement buildElement, string organization, string project)
    {
        var buildInfo = new AzurePipelinesBuildInfo
        {
            Id = buildElement.GetProperty("id").GetInt32(),
            BuildNumber = buildElement.TryGetProperty("buildNumber", out var bn) ? bn.GetString() ?? "" : "",
            Status = buildElement.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
            Result = buildElement.TryGetProperty("result", out var rs) ? rs.GetString() ?? "" : "",
            SourceVersion = buildElement.TryGetProperty("sourceVersion", out var sv) ? sv.GetString() ?? "" : "",
            SourceBranch = buildElement.TryGetProperty("sourceBranch", out var sb) ? sb.GetString() ?? "" : "",
        };
        
        if (buildElement.TryGetProperty("finishTime", out var ft) && ft.ValueKind != JsonValueKind.Null)
            buildInfo.FinishTime = ft.GetDateTime();
        
        if (buildElement.TryGetProperty("queueTime", out var qt) && qt.ValueKind != JsonValueKind.Null)
            buildInfo.QueueTime = qt.GetDateTime();
        
        if (buildElement.TryGetProperty("startTime", out var stt) && stt.ValueKind != JsonValueKind.Null)
            buildInfo.StartTime = stt.GetDateTime();
        
        // Extract web URL from _links
        if (buildElement.TryGetProperty("_links", out var links) &&
            links.TryGetProperty("web", out var web) &&
            web.TryGetProperty("href", out var href))
        {
            buildInfo.WebUrl = href.GetString() ?? "";
        }
        else
        {
            buildInfo.WebUrl = $"https://dev.azure.com/{organization}/{project}/_build/results?buildId={buildInfo.Id}";
        }
        
        return buildInfo;
    }
    
    /// <summary>
    /// Lists artifacts for a build.
    /// </summary>
    /// <param name="organization">Azure DevOps organization name.</param>
    /// <param name="project">Azure DevOps project name.</param>
    /// <param name="buildId">The build ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of artifacts for the build.</returns>
    public async Task<List<AzurePipelinesArtifact>> ListArtifactsAsync(
        string organization,
        string project,
        int buildId,
        CancellationToken ct = default)
    {
        // Check cache first
        if (_cache.TryGetArtifacts(organization, project, buildId, out var cachedArtifacts) && cachedArtifacts != null)
        {
            _logger?.LogDebug("Cache hit for artifacts: {Org}/{Project}/{BuildId}", organization, project, buildId);
            return cachedArtifacts;
        }
        
        var url = $"{DatadogTraceSymbolsConfig.AzureDevOpsBaseUrl}/{organization}/{project}/_apis/build/builds/{buildId}/artifacts" +
                  $"?api-version={DatadogTraceSymbolsConfig.ApiVersion}";
        
        try
        {
            using var request = CreateRequest(HttpMethod.Get, url);
            _logger?.LogDebug("Fetching artifacts for build {BuildId}: {Url}", buildId, url);
            
            var response = await _httpClient.SendAsync(request, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Azure DevOps API returned {Status} for artifacts: {Url}", response.StatusCode, url);
                return new List<AzurePipelinesArtifact>();
            }
            
            var json = await response.Content.ReadAsStringAsync(ct);
            var artifacts = ParseArtifactsResponse(json);
            
            // Cache the result
            _cache.SetArtifacts(organization, project, buildId, artifacts);
            _cacheModified = true;
            
            _logger?.LogInformation("Found {Count} artifacts for build {BuildId}", artifacts.Count, buildId);
            
            return artifacts;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch artifacts for build {BuildId}", buildId);
            return new List<AzurePipelinesArtifact>();
        }
    }
    
    /// <summary>
    /// Downloads an artifact to the specified directory.
    /// </summary>
    /// <param name="organization">Azure DevOps organization name.</param>
    /// <param name="project">Azure DevOps project name.</param>
    /// <param name="buildId">The build ID.</param>
    /// <param name="artifactName">The artifact name to download.</param>
    /// <param name="outputDirectory">Directory to save the downloaded ZIP.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Path to the downloaded ZIP file, or null if download failed.</returns>
    public async Task<string?> DownloadArtifactAsync(
        string organization,
        string project,
        int buildId,
        string artifactName,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var url = $"{DatadogTraceSymbolsConfig.AzureDevOpsBaseUrl}/{organization}/{project}/_apis/build/builds/{buildId}/artifacts" +
                  $"?artifactName={Uri.EscapeDataString(artifactName)}&$format=zip&api-version={DatadogTraceSymbolsConfig.ApiVersion}";
        
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var zipPath = Path.Combine(outputDirectory, $"{artifactName}.zip");
            
            using var request = CreateRequest(HttpMethod.Get, url);
            _logger?.LogDebug("Downloading artifact {Name}: {Url}", artifactName, url);
            
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Failed to download artifact {Name}: {Status}", artifactName, response.StatusCode);
                return null;
            }
            
            // Check content length
            var contentLength = response.Content.Headers.ContentLength;
            var maxSize = DatadogTraceSymbolsConfig.GetMaxArtifactSize();
            
            if (contentLength > maxSize)
            {
                _logger?.LogWarning("Artifact {Name} too large: {Size}MB > {Max}MB", 
                    artifactName, contentLength / 1024 / 1024, maxSize / 1024 / 1024);
                return null;
            }
            
            await using var fileStream = File.Create(zipPath);
            await response.Content.CopyToAsync(fileStream, ct);
            
            _logger?.LogInformation("Downloaded artifact {Name} ({Size}KB) to {Path}", 
                artifactName, new FileInfo(zipPath).Length / 1024, zipPath);
            
            return zipPath;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to download artifact {Name}", artifactName);
            return null;
        }
    }
    
    /// <summary>
    /// Downloads Datadog symbols for a specific platform/architecture.
    /// Downloads multiple artifacts and merges them into a unified symbol directory.
    /// </summary>
    /// <param name="commitSha">The commit SHA from the Datadog assembly.</param>
    /// <param name="platform">Platform info from the dump.</param>
    /// <param name="outputDirectory">Base output directory for symbols.</param>
    /// <param name="targetTfm">Target framework folder to extract (e.g., "net6.0").</param>
    /// <param name="version">Optional version string (e.g., "3.31.0") for tag-based fallback lookup.</param>
    /// <param name="overrideBuildId">Optional build ID to use directly (bypasses commit SHA lookup).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Download result with merge info.</returns>
    public async Task<DatadogSymbolDownloadResult> DownloadDatadogSymbolsAsync(
        string commitSha,
        PlatformInfo platform,
        string outputDirectory,
        string targetTfm,
        string? version = null,
        int? overrideBuildId = null,
        CancellationToken ct = default)
    {
        var result = new DatadogSymbolDownloadResult();
        var platformSuffix = DatadogArtifactMapper.GetPlatformSuffix(platform);
        
        // Check if already downloaded (only if not using override build ID)
        if (overrideBuildId == null && 
            _cache.HasDownloadedSymbols(commitSha, platformSuffix, out var existingDir) && 
            Directory.Exists(existingDir))
        {
            _logger?.LogDebug("Symbols already downloaded for {Commit}/{Platform}", DatadogTraceSymbolsConfig.GetShortSha(commitSha), platformSuffix);
            result.Success = true;
            result.MergeResult = new ArtifactMergeResult
            {
                SymbolDirectory = existingDir,
                NativeSymbolDirectory = Path.Combine(existingDir, platformSuffix),
                ManagedSymbolDirectory = Path.Combine(existingDir, targetTfm)
            };
            // Populate debug symbol files from existing directory
            if (result.MergeResult.NativeSymbolDirectory != null && Directory.Exists(result.MergeResult.NativeSymbolDirectory))
            {
                result.MergeResult.DebugSymbolFiles.AddRange(
                    Directory.GetFiles(result.MergeResult.NativeSymbolDirectory, "*.debug"));
            }
            return result;
        }
        
        AzurePipelinesBuildInfo? build;
        
        if (overrideBuildId.HasValue)
        {
            // Use the provided build ID directly
            _logger?.LogInformation("Using override build ID: {BuildId}", overrideBuildId.Value);
            build = new AzurePipelinesBuildInfo
            {
                Id = overrideBuildId.Value,
                BuildNumber = $"Override-{overrideBuildId.Value}",
                SourceVersion = commitSha,
                WebUrl = $"https://dev.azure.com/{DatadogTraceSymbolsConfig.AzureDevOpsOrganization}/{DatadogTraceSymbolsConfig.AzureDevOpsProject}/_build/results?buildId={overrideBuildId.Value}"
            };
        }
        else
        {
            // Find build by commit (with version tag fallback)
            build = await FindBuildByCommitAsync(
                DatadogTraceSymbolsConfig.AzureDevOpsOrganization,
                DatadogTraceSymbolsConfig.AzureDevOpsProject,
                commitSha,
                version,
                ct);
        }
        
        if (build == null)
        {
            var versionHint = !string.IsNullOrEmpty(version) ? $" (version {version})" : "";
            var searchUrl = $"https://dev.azure.com/{DatadogTraceSymbolsConfig.AzureDevOpsOrganization}/{DatadogTraceSymbolsConfig.AzureDevOpsProject}/_build?definitionId=54";
            result.ErrorMessage = $"No build found for commit {DatadogTraceSymbolsConfig.GetShortSha(commitSha)}{versionHint}. " +
                                  $"The build may have been triggered by a merge commit. " +
                                  $"Find the build manually at: {searchUrl} " +
                                  $"(look for builds around the release date, then use --build-id <id>)";
            return result;
        }
        
        result.BuildId = build.Id;
        result.BuildNumber = build.BuildNumber;
        result.BuildUrl = build.WebUrl;
        
        _logger?.LogInformation("Found Azure Pipelines build {BuildNumber} (ID: {BuildId}) for Datadog symbols",
            build.BuildNumber, build.Id);
        
        // Get artifact names for platform
        var artifactNames = DatadogArtifactMapper.GetArtifactNames(platform);
        
        _logger?.LogDebug("Downloading {Count} artifacts for platform {Platform}: {Names}",
            artifactNames.Count, platformSuffix, string.Join(", ", artifactNames.Values));
        
        // Download each artifact
        var tempDir = Path.Combine(outputDirectory, ".temp_download");
        var artifactZips = new Dictionary<DatadogArtifactType, string>();
        
        try
        {
            Directory.CreateDirectory(tempDir);
            
            foreach (var (artifactType, artifactName) in artifactNames)
            {
                var zipPath = await DownloadArtifactAsync(
                    DatadogTraceSymbolsConfig.AzureDevOpsOrganization,
                    DatadogTraceSymbolsConfig.AzureDevOpsProject,
                    build.Id,
                    artifactName,
                    tempDir,
                    ct);
                
                if (zipPath != null && File.Exists(zipPath))
                {
                    var fileSize = new FileInfo(zipPath).Length;
                    _logger?.LogDebug("Downloaded artifact {Name} ({Size:N0} bytes)", artifactName, fileSize);
                    artifactZips[artifactType] = zipPath;
                    result.DownloadedArtifacts.Add(artifactName);
                }
                else
                {
                    _logger?.LogDebug("Artifact {Name} not available for this platform", artifactName);
                }
            }
            
            if (artifactZips.Count == 0)
            {
                result.ErrorMessage = "No artifacts could be downloaded";
                return result;
            }
            
            // Merge artifacts
            var processor = new DatadogArtifactProcessor(_logger);
            result.MergeResult = await processor.MergeArtifactsAsync(
                artifactZips,
                outputDirectory,
                platformSuffix,
                targetTfm,
                ct);
            
            result.Success = result.MergeResult.Success;
            
            // Update cache
            if (result.Success && result.MergeResult.SymbolDirectory != null)
            {
                _cache.SetDownloadedSymbols(commitSha, platformSuffix, result.MergeResult.SymbolDirectory);
                _cacheModified = true;
            }
        }
        finally
        {
            // Clean up temp directory
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Creates an HTTP request with appropriate headers.
    /// </summary>
    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("User-Agent", UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        
        // Add PAT authentication if configured
        var pat = DatadogTraceSymbolsConfig.GetPatToken();
        if (!string.IsNullOrEmpty(pat))
        {
            var credentials = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{pat}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
        
        return request;
    }
    
    /// <summary>
    /// Parses the build list response from Azure DevOps API.
    /// </summary>
    private AzurePipelinesBuildInfo? ParseBuildResponse(string json, string organization, string project, string? expectedCommitSha = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (!root.TryGetProperty("value", out var builds) || builds.GetArrayLength() == 0)
                return null;
            
            // The Azure DevOps API with sourceVersion filter returns builds that REFERENCE the commit
            // (including merge commits). We need to find the build where sourceVersion EQUALS our commit.
            JsonElement? matchingBuild = null;
            JsonElement? fallbackBuild = null;
            
            var buildCount = builds.GetArrayLength();
            _logger?.LogInformation("Azure Pipelines returned {Count} builds for commit {Commit}", 
                buildCount, DatadogTraceSymbolsConfig.GetShortSha(expectedCommitSha));
            
            foreach (var candidate in builds.EnumerateArray())
            {
                var candidateSv = candidate.TryGetProperty("sourceVersion", out var svProp) ? svProp.GetString() : null;
                var candidateId = candidate.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                var candidateBn = candidate.TryGetProperty("buildNumber", out var bnProp) ? bnProp.GetString() : null;
                
                _logger?.LogInformation("Build candidate: ID={Id}, BuildNumber={Bn}, SourceVersion={SV}", 
                    candidateId, candidateBn ?? "(null)", candidateSv ?? "(null)");
                
                if (fallbackBuild == null)
                {
                    fallbackBuild = candidate;
                }
                
                // If we expect a specific commit, only use the build that EXACTLY matches
                if (!string.IsNullOrEmpty(expectedCommitSha) && !string.IsNullOrEmpty(candidateSv))
                {
                    // Check for exact match (full SHA) or prefix match (short SHA to full or full to short)
                    var isMatch = string.Equals(candidateSv, expectedCommitSha, StringComparison.OrdinalIgnoreCase) ||
                                  candidateSv.StartsWith(expectedCommitSha, StringComparison.OrdinalIgnoreCase) ||
                                  expectedCommitSha.StartsWith(candidateSv, StringComparison.OrdinalIgnoreCase);
                    
                    if (isMatch)
                    {
                        matchingBuild = candidate;
                        _logger?.LogInformation("Found EXACT matching build {Id} for commit {Commit}", 
                            candidateId, DatadogTraceSymbolsConfig.GetShortSha(expectedCommitSha));
                        break;
                    }
                }
            }
            
            // If no exact match found but we have builds, log a warning with advice
            if (matchingBuild == null && fallbackBuild != null)
            {
                var fallbackSv = fallbackBuild.Value.TryGetProperty("sourceVersion", out var fbSv) ? fbSv.GetString() : null;
                _logger?.LogWarning(
                    "No build found with exact sourceVersion match for {Commit}. " +
                    "API returned builds with different sourceVersions (e.g., merge commits). " +
                    "First returned build has sourceVersion={FallbackSv}. " +
                    "The requested commit may be from an older release - try finding the release build ID manually.",
                    DatadogTraceSymbolsConfig.GetShortSha(expectedCommitSha), 
                    DatadogTraceSymbolsConfig.GetShortSha(fallbackSv));
                return null;
            }
            
            if (matchingBuild == null)
            {
                _logger?.LogWarning("No builds returned for commit {Commit}", DatadogTraceSymbolsConfig.GetShortSha(expectedCommitSha));
                return null;
            }
            
            var buildElement = matchingBuild.Value;
            
            var buildInfo = new AzurePipelinesBuildInfo
            {
                Id = buildElement.GetProperty("id").GetInt32(),
                BuildNumber = buildElement.TryGetProperty("buildNumber", out var bn) ? bn.GetString() ?? "" : "",
                Status = buildElement.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                Result = buildElement.TryGetProperty("result", out var rs) ? rs.GetString() ?? "" : "",
                SourceVersion = buildElement.TryGetProperty("sourceVersion", out var sv) ? sv.GetString() ?? "" : "",
                SourceBranch = buildElement.TryGetProperty("sourceBranch", out var sb) ? sb.GetString() ?? "" : "",
            };
            
            if (buildElement.TryGetProperty("finishTime", out var ft) && ft.ValueKind != JsonValueKind.Null)
                buildInfo.FinishTime = ft.GetDateTime();
            
            if (buildElement.TryGetProperty("queueTime", out var qt) && qt.ValueKind != JsonValueKind.Null)
                buildInfo.QueueTime = qt.GetDateTime();
            
            if (buildElement.TryGetProperty("startTime", out var stt) && stt.ValueKind != JsonValueKind.Null)
                buildInfo.StartTime = stt.GetDateTime();
            
            // Extract web URL from _links
            if (buildElement.TryGetProperty("_links", out var links) &&
                links.TryGetProperty("web", out var web) &&
                web.TryGetProperty("href", out var href))
            {
                buildInfo.WebUrl = href.GetString() ?? "";
            }
            else
            {
                // Construct URL manually
                buildInfo.WebUrl = $"https://dev.azure.com/{organization}/{project}/_build/results?buildId={buildInfo.Id}";
            }
            
            return buildInfo;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Failed to parse build response");
            return null;
        }
    }
    
    /// <summary>
    /// Parses the artifacts list response from Azure DevOps API.
    /// </summary>
    private List<AzurePipelinesArtifact> ParseArtifactsResponse(string json)
    {
        var artifacts = new List<AzurePipelinesArtifact>();
        
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (!root.TryGetProperty("value", out var artifactList))
                return artifacts;
            
            foreach (var artifactElement in artifactList.EnumerateArray())
            {
                var artifact = new AzurePipelinesArtifact
                {
                    Id = artifactElement.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                    Name = artifactElement.TryGetProperty("name", out var name) ? name.GetString() ?? "" : ""
                };
                
                // Extract download URL from resource
                if (artifactElement.TryGetProperty("resource", out var resource))
                {
                    if (resource.TryGetProperty("downloadUrl", out var url))
                        artifact.DownloadUrl = url.GetString() ?? "";
                    
                    if (resource.TryGetProperty("type", out var type))
                        artifact.ResourceType = type.GetString() ?? "";
                }
                
                artifacts.Add(artifact);
            }
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Failed to parse artifacts response");
        }
        
        return artifacts;
    }
    
    /// <summary>
    /// Saves the cache if modified.
    /// </summary>
    public void SaveCache()
    {
        if (_cacheModified)
        {
            _cache.Save(_cacheDirectory);
            _cacheModified = false;
        }
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

