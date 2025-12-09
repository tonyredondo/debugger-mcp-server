using System.IO.Compression;
using System.Text.Json;
using DebuggerMcp.Analysis;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Information about a GitHub release.
/// </summary>
public class GitHubReleaseInfo
{
    /// <summary>
    /// Gets or sets the release ID.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the tag name (e.g., "v3.31.0").
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
    /// Gets or sets the publish date.
    /// </summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>
    /// Gets or sets the list of assets.
    /// </summary>
    public List<GitHubReleaseAsset> Assets { get; set; } = new();
}

/// <summary>
/// Information about a GitHub release asset.
/// </summary>
public class GitHubReleaseAsset
{
    /// <summary>
    /// Gets or sets the asset ID.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the asset name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the download URL.
    /// </summary>
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the content type.
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long Size { get; set; }
}

/// <summary>
/// Result of a GitHub release symbol download.
/// </summary>
public class GitHubSymbolDownloadResult
{
    /// <summary>
    /// Gets or sets whether the download was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the release info.
    /// </summary>
    public GitHubReleaseInfo? Release { get; set; }

    /// <summary>
    /// Gets or sets the downloaded assets.
    /// </summary>
    public List<string> DownloadedAssets { get; set; } = new();

    /// <summary>
    /// Gets or sets the merge result.
    /// </summary>
    public ArtifactMergeResult? MergeResult { get; set; }

    /// <summary>
    /// Gets or sets the error message if failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Resolves and downloads Datadog symbols from GitHub Releases.
/// </summary>
public class GitHubReleasesResolver : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;
    private readonly string _owner;
    private readonly string _repo;
    private readonly GitHubReleasesCache _cache;
    private bool _cacheModified;

    // GitHub API base URL
    private const string GitHubApiBaseUrl = "https://api.github.com";

    // Known asset names for Datadog symbols
    private const string LinuxNativeSymbols = "linux-native-symbols.tar.gz";
    private const string WindowsTracerHome = "windows-tracer-home.zip";
    private const string WindowsNativeSymbols = "windows-native-symbols.zip";

    /// <summary>
    /// Creates a new GitHub releases resolver.
    /// </summary>
    /// <param name="cacheDirectory">Directory to store cache files.</param>
    /// <param name="owner">Repository owner (e.g., "DataDog").</param>
    /// <param name="repo">Repository name (e.g., "dd-trace-dotnet").</param>
    /// <param name="logger">Optional logger.</param>
    public GitHubReleasesResolver(string? cacheDirectory = null, string owner = "DataDog", string repo = "dd-trace-dotnet", ILogger? logger = null)
    {
        _owner = owner;
        _repo = repo;
        _logger = logger;
        _cache = new GitHubReleasesCache(cacheDirectory ?? DatadogTraceSymbolsConfig.GetCacheDirectory());
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "DebuggerMcp");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        // Add GitHub token if available (for higher rate limits)
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ??
                   Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrEmpty(token))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        }
    }

    /// <summary>
    /// Finds a release by version tag.
    /// </summary>
    /// <param name="version">Version string (e.g., "3.31.0").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Release info if found.</returns>
    public async Task<GitHubReleaseInfo?> FindReleaseByVersionAsync(string version, CancellationToken ct = default)
    {
        // Check cache first
        if (_cache.TryGetReleaseByVersion(_owner, _repo, version, out var cachedRelease))
        {
            if (cachedRelease != null)
            {
                // Avoid repeated API calls for previously resolved tags
                _logger?.LogDebug("Cache hit for GitHub release v{Version}", version);
                return cachedRelease;
            }
            else
            {
                // Preserve negative cache result to avoid re-querying a missing tag
                _logger?.LogDebug("Cache hit (null) for GitHub release v{Version}", version);
                return null;
            }
        }

        // Try different tag formats
        var tagFormats = new[] { $"v{version}", version };

        foreach (var tag in tagFormats)
        {
            var url = $"{GitHubApiBaseUrl}/repos/{_owner}/{_repo}/releases/tags/{Uri.EscapeDataString(tag)}";

            try
            {
                _logger?.LogInformation("Trying GitHub release tag: {Tag}", tag);

                var response = await _httpClient.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                {
                    // Try next tag pattern when this one isn't present
                    _logger?.LogDebug("GitHub release tag {Tag} returned {Status}", tag, response.StatusCode);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                var release = ParseReleaseResponse(json);

                if (release != null)
                {
                    _logger?.LogInformation("Found GitHub release: {Name} (tag: {Tag})", release.Name, release.TagName);
                    _cache.SetReleaseByVersion(_owner, _repo, version, release);
                    _cacheModified = true;
                    return release;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error fetching GitHub release tag {Tag}", tag);
            }
        }

        // Cache the "not found" result
        _cache.SetReleaseByVersion(_owner, _repo, version, null);
        _cacheModified = true;
        return null;
    }

    /// <summary>
    /// Finds a release by commit SHA by searching recent releases.
    /// </summary>
    /// <param name="commitSha">The commit SHA to search for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Release info if found.</returns>
    public async Task<GitHubReleaseInfo?> FindReleaseByCommitAsync(string commitSha, CancellationToken ct = default)
    {
        // Check cache first
        if (_cache.TryGetReleaseByCommit(_owner, _repo, commitSha, out var cachedRelease))
        {
            if (cachedRelease != null)
            {
                // Use cached release when we already mapped the commit
                _logger?.LogDebug("Cache hit for GitHub release by commit {Commit}", DatadogTraceSymbolsConfig.GetShortSha(commitSha));
                return cachedRelease;
            }
            else
            {
                // Avoid re-scanning the full release list when we already know it is absent
                _logger?.LogDebug("Cache hit (null) for GitHub release by commit {Commit}", DatadogTraceSymbolsConfig.GetShortSha(commitSha));
                return null;
            }
        }

        // GitHub Releases API doesn't directly support querying by commit SHA
        // We need to list releases and check each one's target_commitish
        var url = $"{GitHubApiBaseUrl}/repos/{_owner}/{_repo}/releases?per_page=100";

        try
        {
            _logger?.LogInformation("Searching GitHub releases for commit {Commit}", DatadogTraceSymbolsConfig.GetShortSha(commitSha));

            var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("GitHub releases API returned {Status}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            foreach (var releaseElement in doc.RootElement.EnumerateArray())
            {
                var targetCommitish = releaseElement.TryGetProperty("target_commitish", out var tc) ? tc.GetString() : null;

                if (!string.IsNullOrEmpty(targetCommitish) && !string.IsNullOrEmpty(commitSha))
                {
                    // Check if commit matches (handle short/long SHA)
                    if (targetCommitish.StartsWith(commitSha, StringComparison.OrdinalIgnoreCase) ||
                        commitSha.StartsWith(targetCommitish, StringComparison.OrdinalIgnoreCase))
                    {
                        var release = ParseSingleRelease(releaseElement);
                        if (release != null)
                        {
                            // Cache as soon as we find a match so subsequent calls are fast
                            _logger?.LogInformation("Found GitHub release by commit: {Name} (tag: {Tag})", release.Name, release.TagName);
                            _cache.SetReleaseByCommit(_owner, _repo, commitSha, release);
                            _cacheModified = true;
                            return release;
                        }
                    }
                }
            }

            // Cache the "not found" result
            _cache.SetReleaseByCommit(_owner, _repo, commitSha, null);
            _cacheModified = true;
            _logger?.LogDebug("No GitHub release found for commit {Commit}", DatadogTraceSymbolsConfig.GetShortSha(commitSha));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error searching GitHub releases for commit {Commit}", DatadogTraceSymbolsConfig.GetShortSha(commitSha));
        }

        return null;
    }

    /// <summary>
    /// Downloads Datadog symbols from a GitHub release.
    /// </summary>
    /// <param name="release">The release to download from.</param>
    /// <param name="platform">Platform info from the dump.</param>
    /// <param name="outputDirectory">Base output directory for symbols.</param>
    /// <param name="targetTfm">Target framework folder (e.g., "net6.0").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Download result.</returns>
    public async Task<GitHubSymbolDownloadResult> DownloadSymbolsAsync(
        GitHubReleaseInfo release,
        PlatformInfo platform,
        string outputDirectory,
        string targetTfm,
        CancellationToken ct = default)
    {
        var result = new GitHubSymbolDownloadResult { Release = release };
        var platformSuffix = DatadogArtifactMapper.GetPlatformSuffix(platform);
        var symbolsDir = Path.Combine(outputDirectory, $"symbols-{platformSuffix}");

        Directory.CreateDirectory(symbolsDir);

        var tempDir = Path.Combine(outputDirectory, ".temp_github_download");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Determine which assets to download based on platform
            var assetsToDownload = GetAssetsForPlatform(release.Assets, platform);

            if (assetsToDownload.Count == 0)
            {
                // Stop early when the release doesn't contain symbols for this platform
                result.ErrorMessage = $"No suitable assets found in release {release.TagName} for platform {platformSuffix}";
                return result;
            }

            _logger?.LogInformation("Downloading {Count} assets from GitHub release {Tag}", assetsToDownload.Count, release.TagName);

            // Download each asset
            foreach (var asset in assetsToDownload)
            {
                var localPath = Path.Combine(tempDir, asset.Name);

                if (await DownloadAssetAsync(asset, localPath, ct))
                {
                    result.DownloadedAssets.Add(asset.Name);
                }
            }

            if (result.DownloadedAssets.Count == 0)
            {
                // Avoid attempting extraction when downloads all failed
                result.ErrorMessage = "Failed to download any assets";
                return result;
            }

            // Extract and merge assets
            result.MergeResult = await ExtractAndMergeAssetsAsync(tempDir, symbolsDir, platform, targetTfm, ct);
            result.Success = result.MergeResult != null;

            if (result.Success)
            {
                _logger?.LogInformation("Extracted GitHub release symbols to {Dir}", symbolsDir);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error downloading GitHub release symbols");
            result.ErrorMessage = ex.Message;
        }
        finally
        {
            // Clean up temp directory
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch { /* Ignore cleanup errors */ }
        }

        return result;
    }

    /// <summary>
    /// Gets the assets to download for a specific platform.
    /// </summary>
    private List<GitHubReleaseAsset> GetAssetsForPlatform(List<GitHubReleaseAsset> assets, PlatformInfo platform)
    {
        var result = new List<GitHubReleaseAsset>();

        if (platform.Os.Equals("Linux", StringComparison.OrdinalIgnoreCase))
        {
            // For Linux, we need linux-native-symbols.tar.gz
            var nativeSymbols = assets.FirstOrDefault(a => a.Name.Equals(LinuxNativeSymbols, StringComparison.OrdinalIgnoreCase));
            if (nativeSymbols != null)
            {
                result.Add(nativeSymbols);
            }

            // We also need managed symbols - they're in windows-tracer-home.zip (yes, it has all platforms)
            var tracerHome = assets.FirstOrDefault(a => a.Name.Equals(WindowsTracerHome, StringComparison.OrdinalIgnoreCase));
            if (tracerHome != null)
            {
                // The tracer bundle ships managed assemblies for Linux too
                result.Add(tracerHome);
            }
        }
        else if (platform.Os.Equals("Windows", StringComparison.OrdinalIgnoreCase))
        {
            // For Windows, we need windows-tracer-home.zip and windows-native-symbols.zip
            var tracerHome = assets.FirstOrDefault(a => a.Name.Equals(WindowsTracerHome, StringComparison.OrdinalIgnoreCase));
            if (tracerHome != null)
            {
                result.Add(tracerHome);
            }

            var nativeSymbols = assets.FirstOrDefault(a => a.Name.Equals(WindowsNativeSymbols, StringComparison.OrdinalIgnoreCase));
            if (nativeSymbols != null)
            {
                // Separate native symbols archive so add alongside tracer home
                result.Add(nativeSymbols);
            }
        }

        return result;
    }

    /// <summary>
    /// Downloads a single asset.
    /// </summary>
    private async Task<bool> DownloadAssetAsync(GitHubReleaseAsset asset, string localPath, CancellationToken ct)
    {
        try
        {
            _logger?.LogInformation("Downloading {Name} ({Size}KB)...", asset.Name, asset.Size / 1024);

            using var response = await _httpClient.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Skip to next asset when GitHub returns an error for this file
                _logger?.LogWarning("Failed to download {Name}: {Status}", asset.Name, response.StatusCode);
                return false;
            }

            await using var fileStream = File.Create(localPath);
            await response.Content.CopyToAsync(fileStream, ct);

            _logger?.LogInformation("Downloaded {Name}", asset.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error downloading {Name}", asset.Name);
            return false;
        }
    }

    /// <summary>
    /// Extracts and merges downloaded assets into the symbol directory.
    /// </summary>
    private async Task<ArtifactMergeResult?> ExtractAndMergeAssetsAsync(
        string tempDir,
        string symbolsDir,
        PlatformInfo platform,
        string targetTfm,
        CancellationToken ct)
    {
        var result = new ArtifactMergeResult
        {
            SymbolDirectory = symbolsDir
        };

        var platformSuffix = DatadogArtifactMapper.GetPlatformSuffix(platform);
        var nativeDir = Path.Combine(symbolsDir, platformSuffix);
        var managedDir = Path.Combine(symbolsDir, targetTfm);

        Directory.CreateDirectory(nativeDir);
        Directory.CreateDirectory(managedDir);

        result.NativeSymbolDirectory = nativeDir;
        result.ManagedSymbolDirectory = managedDir;

        foreach (var file in Directory.GetFiles(tempDir))
        {
            var fileName = Path.GetFileName(file);

            if (fileName.Equals(LinuxNativeSymbols, StringComparison.OrdinalIgnoreCase))
            {
                // Extract tar.gz - linux native symbols
                await ExtractTarGzAsync(file, symbolsDir, platform, result, ct);
            }
            else if (fileName.Equals(WindowsTracerHome, StringComparison.OrdinalIgnoreCase))
            {
                // Extract zip - managed symbols (and native for Windows)
                ExtractTracerHomeZip(file, symbolsDir, platform, targetTfm, result);
            }
            else if (fileName.Equals(WindowsNativeSymbols, StringComparison.OrdinalIgnoreCase))
            {
                // Extract zip - Windows native symbols
                ExtractWindowsNativeSymbolsZip(file, nativeDir, platform, result);
            }
        }

        result.Success = result.DebugSymbolFiles.Count > 0 || result.PdbFiles.Count > 0;

        return result;
    }

    /// <summary>
    /// Extracts a tar.gz file containing Linux native symbols.
    /// </summary>
    private async Task ExtractTarGzAsync(string tarGzPath, string symbolsDir, PlatformInfo platform, ArtifactMergeResult result, CancellationToken ct)
    {
        var platformSuffix = DatadogArtifactMapper.GetPlatformSuffix(platform);
        var targetDir = Path.Combine(symbolsDir, platformSuffix);
        Directory.CreateDirectory(targetDir);

        // Determine which subfolder to extract based on platform
        var subfolderPattern = GetLinuxSubfolderPattern(platform);

        _logger?.LogInformation("Extracting {File} (looking for {Pattern})", Path.GetFileName(tarGzPath), subfolderPattern);

        // Use tar command for extraction (more reliable than .NET for tar.gz)
        var extractDir = Path.Combine(Path.GetDirectoryName(tarGzPath)!, "tar_extract");
        Directory.CreateDirectory(extractDir);

        try
        {
            // Extract the tar.gz using tar command
            _logger?.LogInformation("Running: tar -xzf \"{TarGz}\" -C \"{ExtractDir}\"", Path.GetFileName(tarGzPath), extractDir);

            var tarProcess = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "tar",
                    Arguments = $"-xzf \"{tarGzPath}\" -C \"{extractDir}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            tarProcess.Start();
            var stdout = await tarProcess.StandardOutput.ReadToEndAsync(ct);
            var stderr = await tarProcess.StandardError.ReadToEndAsync(ct);
            await tarProcess.WaitForExitAsync(ct);

            _logger?.LogInformation("tar exit code: {ExitCode}", tarProcess.ExitCode);
            if (!string.IsNullOrEmpty(stdout))
            {
                _logger?.LogDebug("tar stdout: {Output}", stdout);
            }
            if (!string.IsNullOrEmpty(stderr))
            {
                _logger?.LogWarning("tar stderr: {Error}", stderr);
            }

            if (tarProcess.ExitCode != 0)
            {
                _logger?.LogWarning("tar extraction failed with exit code {ExitCode}", tarProcess.ExitCode);
                return;
            }

            // List what was extracted
            _logger?.LogInformation("Extraction complete. Checking extracted contents...");

            // Find and copy matching files - search recursively as the folder might be nested
            string? sourceDir = null;

            // First try direct path
            var directPath = Path.Combine(extractDir, subfolderPattern);
            if (Directory.Exists(directPath))
            {
                sourceDir = directPath;
            }
            else
            {
                // Search recursively for the platform folder
                _logger?.LogDebug("Direct path not found, searching recursively for {Pattern}", subfolderPattern);
                try
                {
                    var foundDirs = Directory.GetDirectories(extractDir, subfolderPattern, SearchOption.AllDirectories);
                    if (foundDirs.Length > 0)
                    {
                        sourceDir = foundDirs[0];
                        _logger?.LogDebug("Found platform folder at: {Path}", sourceDir);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Error searching for platform folder");
                }
            }

            if (sourceDir != null && Directory.Exists(sourceDir))
            {
                foreach (var debugFile in Directory.GetFiles(sourceDir, "*.debug"))
                {
                    var destPath = Path.Combine(targetDir, Path.GetFileName(debugFile));
                    File.Copy(debugFile, destPath, overwrite: true);
                    result.DebugSymbolFiles.Add(destPath);
                    _logger?.LogInformation("Extracted native symbol: {File}", Path.GetFileName(debugFile));
                }

                if (result.DebugSymbolFiles.Count == 0)
                {
                    _logger?.LogWarning("Platform folder {Pattern} found but contains no .debug files", subfolderPattern);
                }
            }
            else
            {
                _logger?.LogWarning("Expected subfolder {Pattern} not found in tar.gz", subfolderPattern);

                // List what we found for debugging
                _logger?.LogInformation("Listing extracted directories:");
                try
                {
                    foreach (var dir in Directory.GetDirectories(extractDir, "*", SearchOption.AllDirectories))
                    {
                        var relativePath = Path.GetRelativePath(extractDir, dir);
                        _logger?.LogInformation("  Found: {Dir}", relativePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Error listing directories");
                }
            }
        }
        finally
        {
            // Cleanup
            try
            {
                if (Directory.Exists(extractDir))
                {
                    Directory.Delete(extractDir, recursive: true);
                }
            }
            catch { /* Ignore */ }
        }
    }

    /// <summary>
    /// Gets the Linux subfolder pattern based on platform.
    /// </summary>
    private string GetLinuxSubfolderPattern(PlatformInfo platform)
    {
        var arch = platform.Architecture?.ToLowerInvariant() ?? "x64";
        if (arch == "aarch64") arch = "arm64";

        if (platform.IsAlpine == true || platform.LibcType?.Equals("musl", StringComparison.OrdinalIgnoreCase) == true)
        {
            return $"linux-musl-{arch}";
        }

        return $"linux-{arch}";
    }

    /// <summary>
    /// Extracts the windows-tracer-home.zip file.
    /// </summary>
    private void ExtractTracerHomeZip(string zipPath, string symbolsDir, PlatformInfo platform, string targetTfm, ArtifactMergeResult result)
    {
        var managedDir = Path.Combine(symbolsDir, targetTfm);
        Directory.CreateDirectory(managedDir);

        _logger?.LogInformation("Extracting {File} (TFM: {Tfm})", Path.GetFileName(zipPath), targetTfm);

        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            // Skip directories
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            // Check if this file is in our target TFM folder
            var pathParts = entry.FullName.Split('/');
            if (pathParts.Length >= 2 && pathParts[0].Equals(targetTfm, StringComparison.OrdinalIgnoreCase))
            {
                var destPath = Path.Combine(managedDir, entry.Name);

                // Extract PDB files (symbols)
                if (entry.Name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                {
                    entry.ExtractToFile(destPath, overwrite: true);
                    result.PdbFiles.Add(destPath);
                    _logger?.LogDebug("Extracted symbol: {File}", entry.Name);
                }
                // Extract DLL files (binaries - needed for symbol matching)
                else if (entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    entry.ExtractToFile(destPath, overwrite: true);
                    result.NativeLibraries.Add(destPath); // Reuse NativeLibraries for managed DLLs
                    _logger?.LogDebug("Extracted library: {File}", entry.Name);
                }
            }
        }
    }

    /// <summary>
    /// Extracts the windows-native-symbols.zip file.
    /// </summary>
    private void ExtractWindowsNativeSymbolsZip(string zipPath, string nativeDir, PlatformInfo platform, ArtifactMergeResult result)
    {
        var arch = platform.Architecture?.ToLowerInvariant() ?? "x64";
        var targetSubfolder = $"win-{arch}";

        _logger?.LogInformation("Extracting {File} (arch: {Arch})", Path.GetFileName(zipPath), arch);

        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            // Skip directories
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            // Check if this file is in our target architecture folder
            var pathParts = entry.FullName.Split('/');
            if (pathParts.Length >= 2 && pathParts[0].Equals(targetSubfolder, StringComparison.OrdinalIgnoreCase))
            {
                // Extract PDB files
                if (entry.Name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                {
                    var destPath = Path.Combine(nativeDir, entry.Name);
                    entry.ExtractToFile(destPath, overwrite: true);
                    result.PdbFiles.Add(destPath);
                    _logger?.LogDebug("Extracted: {File}", entry.Name);
                }
            }
        }
    }

    /// <summary>
    /// Parses a release response JSON.
    /// </summary>
    private GitHubReleaseInfo? ParseReleaseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseSingleRelease(doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Error parsing GitHub release response");
            return null;
        }
    }

    /// <summary>
    /// Parses a single release from a JSON element.
    /// </summary>
    private GitHubReleaseInfo? ParseSingleRelease(JsonElement element)
    {
        try
        {
            var release = new GitHubReleaseInfo
            {
                Id = element.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
                TagName = element.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "",
                Name = element.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                TargetCommitish = element.TryGetProperty("target_commitish", out var tc) ? tc.GetString() : null,
                HtmlUrl = element.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : ""
            };

            if (element.TryGetProperty("published_at", out var pa) && pa.ValueKind != JsonValueKind.Null)
            {
                release.PublishedAt = pa.GetDateTime();
            }

            // Parse assets
            if (element.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var assetElement in assets.EnumerateArray())
                {
                    var asset = new GitHubReleaseAsset
                    {
                        Id = assetElement.TryGetProperty("id", out var aid) ? aid.GetInt64() : 0,
                        Name = assetElement.TryGetProperty("name", out var aname) ? aname.GetString() ?? "" : "",
                        BrowserDownloadUrl = assetElement.TryGetProperty("browser_download_url", out var aurl) ? aurl.GetString() ?? "" : "",
                        ContentType = assetElement.TryGetProperty("content_type", out var ct) ? ct.GetString() ?? "" : "",
                        Size = assetElement.TryGetProperty("size", out var size) ? size.GetInt64() : 0
                    };

                    if (!string.IsNullOrEmpty(asset.Name))
                    {
                        release.Assets.Add(asset);
                    }
                }
            }

            return release;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Error parsing single release");
            return null;
        }
    }

    /// <summary>
    /// Saves the cache to disk.
    /// </summary>
    public void SaveCache()
    {
        if (_cacheModified)
        {
            _cache.Save();
            _cacheModified = false;
        }
    }

    /// <summary>
    /// Disposes the HTTP client and saves the cache.
    /// </summary>
    public void Dispose()
    {
        SaveCache();
        _httpClient.Dispose();
    }
}
