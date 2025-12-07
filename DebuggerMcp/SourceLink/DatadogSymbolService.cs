using DebuggerMcp.Analysis;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Information about a Datadog assembly detected in a dump.
/// </summary>
public class DatadogAssemblyInfo
{
    /// <summary>
    /// Gets or sets the assembly name (e.g., "Datadog.Trace").
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the full version from InformationalVersion.
    /// </summary>
    public string? InformationalVersion { get; set; }
    
    /// <summary>
    /// Gets or sets the extracted commit SHA (first 40 hex chars from version).
    /// </summary>
    public string? CommitSha { get; set; }
    
    /// <summary>
    /// Gets or sets the target framework (e.g., ".NET 6.0").
    /// </summary>
    public string? TargetFramework { get; set; }
    
    /// <summary>
    /// Gets or sets the repository URL from metadata.
    /// </summary>
    public string? RepositoryUrl { get; set; }
    
    /// <summary>
    /// Extracts the version part from InformationalVersion (e.g., "3.31.0" from "3.31.0+14fd3a2f...").
    /// </summary>
    public string? Version
    {
        get
        {
            if (string.IsNullOrEmpty(InformationalVersion))
                return null;
            
            // Handle formats like "3.31.0+14fd3a2f..." or "3.31.0.14fd3a2f..."
            var plusIndex = InformationalVersion.IndexOf('+');
            if (plusIndex > 0)
                return InformationalVersion[..plusIndex];
            
            // If no +, check for pattern like "3.31.0.abc123..." where last part is SHA
            var parts = InformationalVersion.Split('.');
            if (parts.Length >= 3)
            {
                // Check if last part looks like a SHA (all hex chars, length >= 7)
                var lastPart = parts[^1];
                if (lastPart.Length >= 7 && lastPart.All(c => "0123456789abcdefABCDEF".Contains(c)))
                {
                    // Return version without the SHA part
                    return string.Join(".", parts[..^1]);
                }
            }
            
            return InformationalVersion;
        }
    }
}

/// <summary>
/// Result of Datadog symbol preparation.
/// </summary>
public class DatadogSymbolPreparationResult
{
    /// <summary>
    /// Gets or sets whether symbols were successfully prepared.
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// Gets or sets the list of Datadog assemblies found.
    /// </summary>
    public List<DatadogAssemblyInfo> DatadogAssemblies { get; set; } = new();
    
    /// <summary>
    /// Gets or sets the download result if symbols were downloaded.
    /// </summary>
    public DatadogSymbolDownloadResult? DownloadResult { get; set; }
    
    /// <summary>
    /// Gets or sets the symbol load result if symbols were loaded.
    /// </summary>
    public SymbolLoadResult? LoadResult { get; set; }
    
    /// <summary>
    /// Gets or sets any error or warning message.
    /// </summary>
    public string? Message { get; set; }
    
    /// <summary>
    /// Gets or sets whether symbols were loaded into the debugger.
    /// </summary>
    public bool SymbolsLoaded => LoadResult?.Success == true;
}

/// <summary>
/// Service that orchestrates Datadog symbol download and loading.
/// Designed to be called early in the analysis pipeline for best stack traces.
/// </summary>
public class DatadogSymbolService
{
    private readonly ClrMdAnalyzer? _clrMdAnalyzer;
    private readonly ILogger? _logger;
    
    // Known Datadog assembly prefixes
    private static readonly string[] DatadogPrefixes = new[]
    {
        "Datadog.Trace",
        "Datadog.Profiler",
        "Datadog.AutoInstrumentation"
    };
    
    /// <summary>
    /// Initializes a new instance of the <see cref="DatadogSymbolService"/> class.
    /// </summary>
    /// <param name="clrMdAnalyzer">ClrMD analyzer for reading assembly metadata.</param>
    /// <param name="logger">Optional logger.</param>
    public DatadogSymbolService(ClrMdAnalyzer? clrMdAnalyzer, ILogger? logger = null)
    {
        _clrMdAnalyzer = clrMdAnalyzer;
        _logger = logger;
    }
    
    /// <summary>
    /// Quickly scans for Datadog assemblies using ClrMD.
    /// </summary>
    /// <returns>List of Datadog assembly information.</returns>
    public List<DatadogAssemblyInfo> ScanForDatadogAssemblies()
    {
        var result = new List<DatadogAssemblyInfo>();
        
        _logger?.LogInformation("[DatadogSymbols] Scanning for Datadog assemblies...");
        
        if (_clrMdAnalyzer == null)
        {
            _logger?.LogWarning("[DatadogSymbols] ClrMD analyzer is null, cannot scan for Datadog assemblies");
            return result;
        }
        
        if (!_clrMdAnalyzer.IsOpen)
        {
            _logger?.LogWarning("[DatadogSymbols] ClrMD analyzer is not open, cannot scan for Datadog assemblies");
            return result;
        }
        
        try
        {
            // Get all modules with attributes in one pass
            var modules = _clrMdAnalyzer.GetAllModulesWithAttributes();
            _logger?.LogInformation("[DatadogSymbols] ClrMD found {Count} modules in dump", modules.Count);
            
            var datadogModulesFound = 0;
            var datadogModulesWithCommit = 0;
            
            foreach (var module in modules)
            {
                // Check if this is a Datadog assembly
                if (!IsDatadogAssembly(module.Name))
                    continue;
                
                datadogModulesFound++;
                _logger?.LogDebug("Found Datadog module: {Name}, Attributes: {AttrCount}", 
                    module.Name, module.Attributes?.Count ?? 0);
                
                var info = new DatadogAssemblyInfo
                {
                    Name = module.Name
                };
                
                // Extract metadata from attributes
                // Attribute types are stored with full namespace (e.g., "System.Reflection.AssemblyInformationalVersionAttribute")
                if (module.Attributes != null)
                {
                    foreach (var attr in module.Attributes)
                    {
                        _logger?.LogDebug("  Attribute: {Type} = {Value}", attr.AttributeType, attr.Value);
                        
                        // Match by suffix since full type name includes namespace
                        var attrType = attr.AttributeType ?? "";
                        
                        if (attrType.EndsWith("AssemblyInformationalVersionAttribute"))
                        {
                            info.InformationalVersion = attr.Value;
                            info.CommitSha = ExtractCommitSha(attr.Value);
                            _logger?.LogDebug("  Extracted commit SHA: {Sha}", info.CommitSha ?? "(null)");
                        }
                        else if (attrType.EndsWith("AssemblyMetadataAttribute") && attr.Key == "RepositoryUrl")
                        {
                            info.RepositoryUrl = attr.Value;
                        }
                        else if (attrType.EndsWith("TargetFrameworkAttribute"))
                        {
                            info.TargetFramework = attr.Value;
                        }
                    }
                }
                
                // Only add if we have a commit SHA (needed for download)
                if (!string.IsNullOrEmpty(info.CommitSha))
                {
                    datadogModulesWithCommit++;
                    result.Add(info);
                    _logger?.LogInformation("[DatadogSymbols] Found: {Name} commit={Commit} (from: {Version})", 
                        info.Name, DatadogTraceSymbolsConfig.GetShortSha(info.CommitSha), info.InformationalVersion);
                }
                else
                {
                    _logger?.LogWarning("[DatadogSymbols] Module {Name} has no commit SHA - InformationalVersion: '{Version}' (attrs: {AttrCount})", 
                        module.Name, info.InformationalVersion ?? "(null)", module.Attributes?.Count ?? 0);
                }
            }
            
            _logger?.LogInformation("[DatadogSymbols] Scan complete: {Found} Datadog modules found, {WithCommit} with commit SHA", 
                datadogModulesFound, datadogModulesWithCommit);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error scanning for Datadog assemblies");
        }
        
        return result;
    }
    
    /// <summary>
    /// Prepares Datadog symbols: scans, downloads, and loads into debugger.
    /// </summary>
    /// <param name="platform">Platform information from the dump.</param>
    /// <param name="symbolsOutputDirectory">Directory to store downloaded symbols.</param>
    /// <param name="executeCommand">Function to execute debugger commands.</param>
    /// <param name="loadIntoDebugger">Whether to load symbols into the debugger after download.</param>
    /// <param name="forceVersion">If true, falls back to version/tag lookup when SHA lookup fails.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Preparation result.</returns>
    public async Task<DatadogSymbolPreparationResult> PrepareSymbolsAsync(
        PlatformInfo platform,
        string symbolsOutputDirectory,
        Func<string, string> executeCommand,
        bool loadIntoDebugger = true,
        bool forceVersion = false,
        CancellationToken ct = default)
    {
        var result = new DatadogSymbolPreparationResult();
        
        // Check if feature is enabled
        if (!DatadogTraceSymbolsConfig.IsEnabled())
        {
            result.Message = "Datadog symbol download is disabled";
            return result;
        }
        
        // Phase A: Quick scan for Datadog assemblies
        result.DatadogAssemblies = ScanForDatadogAssemblies();
        
        if (result.DatadogAssemblies.Count == 0)
        {
            result.Message = "No Datadog assemblies found in dump";
            result.Success = true; // Not an error, just nothing to do
            return result;
        }
        
        // Get the primary assembly (Datadog.Trace if available)
        var primaryAssembly = result.DatadogAssemblies
            .FirstOrDefault(a => a.Name.Equals("Datadog.Trace", StringComparison.OrdinalIgnoreCase))
            ?? result.DatadogAssemblies.First();
        
        if (string.IsNullOrEmpty(primaryAssembly.CommitSha))
        {
            result.Message = "Could not extract commit SHA from Datadog assembly";
            return result;
        }
        
        _logger?.LogInformation("Found {Count} Datadog assemblies, using commit {Commit} for symbol download",
            result.DatadogAssemblies.Count, DatadogTraceSymbolsConfig.GetShortSha(primaryAssembly.CommitSha));
        
        // Determine target TFM
        var targetTfm = DatadogArtifactMapper.GetTargetTfmFolder(
            primaryAssembly.TargetFramework ?? platform.RuntimeVersion ?? ".NET 6.0");
        
        // Phase B: Download symbols using lookup strategy:
        // Default (SHA only): 1. Azure Pipelines with SHA, 2. GitHub Releases with SHA
        // With forceVersion: + 3. Azure Pipelines with version/tag, 4. GitHub Releases with version/tag
        try
        {
            result.DownloadResult = await TryDownloadSymbolsAsync(
                primaryAssembly.CommitSha!,
                primaryAssembly.Version,
                platform,
                symbolsOutputDirectory,
                targetTfm,
                forceVersion,
                ct);
            
            if (!result.DownloadResult.Success)
            {
                result.Message = result.DownloadResult.ErrorMessage ?? "Symbol download failed";
                return result;
            }
            
            _logger?.LogInformation("Downloaded Datadog symbols: {Count} artifacts",
                result.DownloadResult.DownloadedArtifacts.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to download Datadog symbols");
            result.Message = $"Symbol download error: {ex.Message}";
            return result;
        }
        
        // Phase C: Load symbols into debugger
        if (loadIntoDebugger && result.DownloadResult?.MergeResult != null)
        {
            try
            {
                var loader = new DatadogSymbolLoader(_logger);
                result.LoadResult = await loader.LoadSymbolsAsync(
                    result.DownloadResult.MergeResult,
                    executeCommand);
                
                if (result.LoadResult.Success)
                {
                    _logger?.LogInformation("Loaded Datadog symbols: {Native} native, {Managed} managed paths",
                        result.LoadResult.NativeSymbolsLoaded.Count,
                        result.LoadResult.ManagedSymbolPaths.Count);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load Datadog symbols into debugger");
                result.Message = $"Symbol loading error: {ex.Message}";
                // Continue - download succeeded even if loading failed
            }
        }
        else if (!loadIntoDebugger)
        {
            _logger?.LogDebug("Skipping symbol loading as requested");
        }
        
        result.Success = result.DownloadResult?.Success == true;
        result.Message = result.Success 
            ? $"Downloaded and loaded symbols for {primaryAssembly.Name} ({DatadogTraceSymbolsConfig.GetShortSha(primaryAssembly.CommitSha)})"
            : result.Message;
        
        return result;
    }
    
    /// <summary>
    /// Checks if an assembly name is a Datadog assembly.
    /// </summary>
    private static bool IsDatadogAssembly(string assemblyName)
    {
        return DatadogPrefixes.Any(prefix => 
            assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
    
    /// <summary>
    /// Extracts commit SHA from InformationalVersion.
    /// Expected format: "3.10.0+abc123def456..." or "3.10.0.abc123def456..."
    /// </summary>
    private static string? ExtractCommitSha(string? informationalVersion)
    {
        if (string.IsNullOrEmpty(informationalVersion))
            return null;
        
        // Look for + or . followed by hex characters
        var plusIndex = informationalVersion.IndexOf('+');
        var dotIndex = informationalVersion.LastIndexOf('.');
        
        string? candidate = null;
        
        if (plusIndex >= 0 && plusIndex < informationalVersion.Length - 1)
        {
            candidate = informationalVersion[(plusIndex + 1)..];
        }
        else if (dotIndex >= 0 && dotIndex < informationalVersion.Length - 1)
        {
            // Check if the part after the last dot looks like a commit SHA
            var afterDot = informationalVersion[(dotIndex + 1)..];
            if (afterDot.Length >= 7 && afterDot.All(c => char.IsLetterOrDigit(c)))
            {
                // Make sure it's hex
                if (afterDot.All(c => "0123456789abcdefABCDEF".Contains(c)))
                {
                    candidate = afterDot;
                }
            }
        }
        
        if (candidate == null)
            return null;
        
        // Clean up: remove any non-hex suffix
        var hexChars = new List<char>();
        foreach (var c in candidate)
        {
            if ("0123456789abcdefABCDEF".Contains(c))
                hexChars.Add(c);
            else
                break;
        }
        
        // Need at least 7 characters for a valid short SHA
        if (hexChars.Count >= 7)
        {
            return new string(hexChars.ToArray()).ToLowerInvariant();
        }
        
        return null;
    }
    
    /// <summary>
    /// Tries to download symbols using the lookup strategy:
    /// Default (SHA only): 1. Azure Pipelines with SHA, 2. GitHub Releases with SHA
    /// With forceVersion: + 3. Azure Pipelines with version/tag, 4. GitHub Releases with version/tag
    /// </summary>
    /// <param name="commitSha">The commit SHA to look up.</param>
    /// <param name="version">The version for fallback lookup (e.g., "3.31.0").</param>
    /// <param name="platform">Platform information.</param>
    /// <param name="outputDirectory">Directory to store symbols.</param>
    /// <param name="targetTfm">Target framework moniker.</param>
    /// <param name="forceVersion">If true, falls back to version/tag lookup when SHA lookup fails.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<DatadogSymbolDownloadResult> TryDownloadSymbolsAsync(
        string commitSha,
        string? version,
        PlatformInfo platform,
        string outputDirectory,
        string targetTfm,
        bool forceVersion,
        CancellationToken ct)
    {
        var errors = new List<string>();
        
        // Step 1: Azure Pipelines with SHA
        _logger?.LogInformation("[DatadogSymbols] Step 1: Trying Azure Pipelines with commit SHA {Sha}", DatadogTraceSymbolsConfig.GetShortSha(commitSha));
        using (var azureResolver = new AzurePipelinesResolver(DatadogTraceSymbolsConfig.GetCacheDirectory(), _logger))
        {
            var azureResult = await azureResolver.DownloadDatadogSymbolsAsync(
                commitSha,
                platform,
                outputDirectory,
                targetTfm,
                version: null,  // Don't use version fallback in Azure resolver - we'll do it ourselves
                overrideBuildId: null,
                ct);
            
            azureResolver.SaveCache();
            
            if (azureResult.Success)
            {
                _logger?.LogInformation("[DatadogSymbols] Success: Downloaded from Azure Pipelines (build {Build})", azureResult.BuildNumber);
                azureResult.Source = "AzurePipelines";
                azureResult.ShaMismatch = false;  // Exact SHA match
                return azureResult;
            }
            
            errors.Add($"Azure Pipelines (SHA): {azureResult.ErrorMessage}");
        }
        
        // Step 2: GitHub Releases with SHA
        _logger?.LogInformation("[DatadogSymbols] Step 2: Trying GitHub Releases with commit SHA {Sha}", DatadogTraceSymbolsConfig.GetShortSha(commitSha));
        using (var githubResolver = new GitHubReleasesResolver(DatadogTraceSymbolsConfig.GetCacheDirectory(), logger: _logger))
        {
            var release = await githubResolver.FindReleaseByCommitAsync(commitSha, ct);
            if (release != null)
            {
                var githubResult = await githubResolver.DownloadSymbolsAsync(release, platform, outputDirectory, targetTfm, ct);
                if (githubResult.Success)
                {
                    _logger?.LogInformation("[DatadogSymbols] Success: Downloaded from GitHub Releases ({Tag})", release.TagName);
                    githubResolver.SaveCache();
                    var result = ConvertToDatadogResult(githubResult);
                    result.Source = "GitHubReleases";
                    result.ShaMismatch = false;  // Exact SHA match
                    return result;
                }
                errors.Add($"GitHub Releases (SHA): {githubResult.ErrorMessage}");
            }
            else
            {
                errors.Add("GitHub Releases (SHA): No release found for commit");
            }
            githubResolver.SaveCache();
        }
        
        // If forceVersion is not enabled, stop here and suggest using --force-version
        if (!forceVersion)
        {
            var shaOnlyError = $"No symbols found for commit SHA {DatadogTraceSymbolsConfig.GetShortSha(commitSha)}. Tried:\n" + 
                string.Join("\n", errors.Select(e => $"  - {e}"));
            
            if (!string.IsNullOrEmpty(version))
            {
                shaOnlyError += $"\n\nTip: Use --force-version to try downloading symbols by version tag (v{version}) instead.\n" +
                    "Note: Version-based symbols may not exactly match your binary.";
            }
            
            _logger?.LogWarning("[DatadogSymbols] {Error}", shaOnlyError);
            
            return new DatadogSymbolDownloadResult
            {
                Success = false,
                ErrorMessage = shaOnlyError
            };
        }
        
        // Step 3: Azure Pipelines with version/tag (only if we have a version and forceVersion is enabled)
        if (!string.IsNullOrEmpty(version))
        {
            _logger?.LogInformation("[DatadogSymbols] Step 3: Trying Azure Pipelines with version tag v{Version}", version);
            using (var azureResolver = new AzurePipelinesResolver(DatadogTraceSymbolsConfig.GetCacheDirectory(), _logger))
            {
                var azureResult = await azureResolver.DownloadDatadogSymbolsAsync(
                    commitSha,
                    platform,
                    outputDirectory,
                    targetTfm,
                    version: version,  // Use version for tag lookup
                    overrideBuildId: null,
                    ct);
                
                azureResolver.SaveCache();
                
                if (azureResult.Success)
                {
                    _logger?.LogInformation("[DatadogSymbols] Success: Downloaded from Azure Pipelines via version tag (build {Build})", azureResult.BuildNumber);
                    azureResult.Source = "AzurePipelines";
                    azureResult.ShaMismatch = true;  // Used version tag instead of exact SHA
                    return azureResult;
                }
                
                errors.Add($"Azure Pipelines (version): {azureResult.ErrorMessage}");
            }
            
            // Step 4: GitHub Releases with version/tag
            _logger?.LogInformation("[DatadogSymbols] Step 4: Trying GitHub Releases with version tag v{Version}", version);
            using (var githubResolver = new GitHubReleasesResolver(DatadogTraceSymbolsConfig.GetCacheDirectory(), logger: _logger))
            {
                var release = await githubResolver.FindReleaseByVersionAsync(version, ct);
                if (release != null)
                {
                    var githubResult = await githubResolver.DownloadSymbolsAsync(release, platform, outputDirectory, targetTfm, ct);
                    if (githubResult.Success)
                    {
                        _logger?.LogInformation("[DatadogSymbols] Success: Downloaded from GitHub Releases ({Tag})", release.TagName);
                        githubResolver.SaveCache();
                        var result = ConvertToDatadogResult(githubResult);
                        result.Source = "GitHubReleases";
                        result.ShaMismatch = true;  // Used version tag instead of exact SHA
                        return result;
                    }
                    errors.Add($"GitHub Releases (version): {githubResult.ErrorMessage}");
                }
                else
                {
                    errors.Add($"GitHub Releases (version): No release found for v{version}");
                }
                githubResolver.SaveCache();
            }
        }
        
        // All steps failed
        var combinedError = $"All symbol sources exhausted. Tried:\n" + string.Join("\n", errors.Select(e => $"  - {e}"));
        _logger?.LogWarning("[DatadogSymbols] {Error}", combinedError);
        
        return new DatadogSymbolDownloadResult
        {
            Success = false,
            ErrorMessage = combinedError
        };
    }
    
    /// <summary>
    /// Downloads symbols for a known commit SHA, with optional version fallback.
    /// </summary>
    /// <param name="commitSha">The commit SHA to look up.</param>
    /// <param name="version">Optional version for fallback lookup (e.g., "3.31.0").</param>
    /// <param name="platform">Platform information.</param>
    /// <param name="outputDirectory">Directory to store symbols.</param>
    /// <param name="targetTfm">Target framework moniker.</param>
    /// <param name="forceVersion">If true, falls back to version/tag lookup when SHA lookup fails.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Download result.</returns>
    public async Task<DatadogSymbolDownloadResult> DownloadSymbolsAsync(
        string commitSha,
        string? version,
        PlatformInfo platform,
        string outputDirectory,
        string targetTfm,
        bool forceVersion = false,
        CancellationToken ct = default)
    {
        return await TryDownloadSymbolsAsync(commitSha, version, platform, outputDirectory, targetTfm, forceVersion, ct);
    }
    
    /// <summary>
    /// Converts a GitHub result to a Datadog result for consistent return type.
    /// </summary>
    private static DatadogSymbolDownloadResult ConvertToDatadogResult(GitHubSymbolDownloadResult githubResult)
    {
        return new DatadogSymbolDownloadResult
        {
            Success = githubResult.Success,
            BuildNumber = githubResult.Release?.TagName ?? "",
            BuildUrl = githubResult.Release?.HtmlUrl ?? "",
            DownloadedArtifacts = githubResult.DownloadedAssets,
            MergeResult = githubResult.MergeResult,
            ErrorMessage = githubResult.ErrorMessage
        };
    }
}

