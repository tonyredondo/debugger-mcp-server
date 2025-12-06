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
        
        if (_clrMdAnalyzer == null || !_clrMdAnalyzer.IsOpen)
        {
            _logger?.LogDebug("ClrMD analyzer not available, skipping Datadog assembly scan");
            return result;
        }
        
        try
        {
            // Get all modules with attributes in one pass
            var modules = _clrMdAnalyzer.GetAllModulesWithAttributes();
            
            foreach (var module in modules)
            {
                // Check if this is a Datadog assembly
                if (!IsDatadogAssembly(module.Name))
                    continue;
                
                var info = new DatadogAssemblyInfo
                {
                    Name = module.Name
                };
                
                // Extract metadata from attributes
                foreach (var attr in module.Attributes)
                {
                    switch (attr.AttributeType)
                    {
                        case "AssemblyInformationalVersionAttribute":
                            info.InformationalVersion = attr.Value;
                            info.CommitSha = ExtractCommitSha(attr.Value);
                            break;
                        case "AssemblyMetadataAttribute" when attr.Key == "RepositoryUrl":
                            info.RepositoryUrl = attr.Value;
                            break;
                        case "TargetFrameworkAttribute":
                            info.TargetFramework = attr.Value;
                            break;
                    }
                }
                
                // Only add if we have a commit SHA (needed for download)
                if (!string.IsNullOrEmpty(info.CommitSha))
                {
                    result.Add(info);
                    _logger?.LogDebug("Found Datadog assembly: {Name} commit={Commit}", 
                        info.Name, DatadogTraceSymbolsConfig.GetShortSha(info.CommitSha));
                }
            }
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
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Preparation result.</returns>
    public async Task<DatadogSymbolPreparationResult> PrepareSymbolsAsync(
        PlatformInfo platform,
        string symbolsOutputDirectory,
        Func<string, string> executeCommand,
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
        
        // Phase B: Download symbols
        try
        {
            using var resolver = new AzurePipelinesResolver(
                DatadogTraceSymbolsConfig.GetCacheDirectory(),
                _logger);
            
            result.DownloadResult = await resolver.DownloadDatadogSymbolsAsync(
                primaryAssembly.CommitSha,
                platform,
                symbolsOutputDirectory,
                targetTfm,
                ct);
            
            resolver.SaveCache();
            
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
        if (result.DownloadResult?.MergeResult != null)
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
}

