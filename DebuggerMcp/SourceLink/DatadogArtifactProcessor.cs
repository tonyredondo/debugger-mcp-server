using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Result of merging multiple Datadog artifacts into a unified symbol directory.
/// </summary>
public class ArtifactMergeResult
{
    /// <summary>
    /// Gets or sets whether the merge was successful.
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// Gets or sets the root symbol directory (e.g., symbols-linux-musl-arm64).
    /// </summary>
    public string? SymbolDirectory { get; set; }
    
    /// <summary>
    /// Gets or sets the native symbol directory containing .debug files.
    /// </summary>
    public string? NativeSymbolDirectory { get; set; }
    
    /// <summary>
    /// Gets or sets the managed symbol directory containing .pdb files for TFM.
    /// </summary>
    public string? ManagedSymbolDirectory { get; set; }
    
    /// <summary>
    /// Gets the list of native .debug files extracted.
    /// </summary>
    public List<string> DebugSymbolFiles { get; } = new();
    
    /// <summary>
    /// Gets the list of managed .pdb files extracted.
    /// </summary>
    public List<string> PdbFiles { get; } = new();
    
    /// <summary>
    /// Gets the list of native library files (*.so, *.dll) extracted.
    /// </summary>
    public List<string> NativeLibraries { get; } = new();
    
    /// <summary>
    /// Gets or sets error message if merge failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// Gets or sets the total number of files extracted.
    /// </summary>
    public int TotalFilesExtracted { get; set; }
}

/// <summary>
/// Processes and merges Datadog symbol artifacts into a unified directory structure.
/// </summary>
/// <remarks>
/// Expected merged structure:
/// symbols-linux-musl-arm64/
/// ├── linux-musl-arm64/           # Native symbols
/// │   ├── Datadog.Trace.ClrProfiler.Native.debug
/// │   ├── Datadog.Profiler.Native.debug
/// │   ├── Datadog.Tracer.Native.so
/// │   └── ...
/// └── net6.0/                     # Managed symbols (TFM-specific)
///     ├── Datadog.Trace.pdb
///     ├── Datadog.Trace.dll
///     └── ...
/// </remarks>
public class DatadogArtifactProcessor
{
    private readonly ILogger? _logger;
    
    // File extensions we want to extract
    private static readonly HashSet<string> SymbolExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".debug", ".pdb", ".so", ".dll", ".dylib"
    };
    
    /// <summary>
    /// Initializes a new instance of the <see cref="DatadogArtifactProcessor"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public DatadogArtifactProcessor(ILogger? logger = null)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Merges multiple artifact ZIPs into a unified symbol directory.
    /// </summary>
    /// <param name="artifactZips">Dictionary mapping artifact type to ZIP file path.</param>
    /// <param name="baseOutputDirectory">Base directory for symbols.</param>
    /// <param name="platformSuffix">Platform suffix (e.g., "linux-musl-arm64").</param>
    /// <param name="targetTfm">Target TFM folder name (e.g., "net6.0").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Merge result with paths to extracted symbols.</returns>
    public async Task<ArtifactMergeResult> MergeArtifactsAsync(
        Dictionary<DatadogArtifactType, string> artifactZips,
        string baseOutputDirectory,
        string platformSuffix,
        string targetTfm,
        CancellationToken ct = default)
    {
        var result = new ArtifactMergeResult();
        
        try
        {
            // Create symbol directory structure
            var symbolDir = Path.Combine(baseOutputDirectory, $"symbols-{platformSuffix}");
            var nativeDir = Path.Combine(symbolDir, platformSuffix);
            var managedDir = Path.Combine(symbolDir, targetTfm);
            
            Directory.CreateDirectory(nativeDir);
            Directory.CreateDirectory(managedDir);
            
            result.SymbolDirectory = symbolDir;
            result.NativeSymbolDirectory = nativeDir;
            result.ManagedSymbolDirectory = managedDir;
            
            // Process each artifact type
            foreach (var (artifactType, zipPath) in artifactZips)
            {
                if (!File.Exists(zipPath))
                    continue;
                
                _logger?.LogDebug("Processing artifact {Type} from {Path}", artifactType, zipPath);
                
                var extracted = await ExtractArtifactAsync(
                    zipPath, artifactType, nativeDir, managedDir, targetTfm, platformSuffix, ct);
                
                result.DebugSymbolFiles.AddRange(extracted.debugFiles);
                result.PdbFiles.AddRange(extracted.pdbFiles);
                result.NativeLibraries.AddRange(extracted.nativeLibs);
                result.TotalFilesExtracted += extracted.debugFiles.Count + 
                                              extracted.pdbFiles.Count + 
                                              extracted.nativeLibs.Count;
            }
            
            result.Success = result.TotalFilesExtracted > 0;
            
            if (result.Success)
            {
                _logger?.LogInformation("Merged {Count} files into {Dir}", 
                    result.TotalFilesExtracted, symbolDir);
            }
            else
            {
                result.ErrorMessage = "No symbol files were extracted";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to merge artifacts");
            result.ErrorMessage = ex.Message;
            result.Success = false;
        }
        
        return result;
    }
    
    /// <summary>
    /// Extracts relevant files from an artifact ZIP.
    /// </summary>
    private async Task<(List<string> debugFiles, List<string> pdbFiles, List<string> nativeLibs)> ExtractArtifactAsync(
        string zipPath,
        DatadogArtifactType artifactType,
        string nativeDir,
        string managedDir,
        string targetTfm,
        string platformSuffix,
        CancellationToken ct)
    {
        var debugFiles = new List<string>();
        var pdbFiles = new List<string>();
        var nativeLibs = new List<string>();
        
        await using var zipStream = File.OpenRead(zipPath);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            
            // Skip directories
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            
            var ext = Path.GetExtension(entry.Name);
            if (!SymbolExtensions.Contains(ext))
                continue;
            
            // Determine target directory based on artifact type and file
            var (targetDir, shouldExtract) = GetTargetDirectory(
                entry.FullName, entry.Name, ext, artifactType, nativeDir, managedDir, targetTfm, platformSuffix);
            
            if (!shouldExtract || targetDir == null)
                continue;
            
            // Extract file
            var targetPath = Path.Combine(targetDir, entry.Name);
            
            // Skip if file already exists and is same size
            if (File.Exists(targetPath) && new FileInfo(targetPath).Length == entry.Length)
            {
                _logger?.LogDebug("Skipping existing file: {Path}", entry.Name);
                
                // Still track the file
                TrackFile(targetPath, ext, debugFiles, pdbFiles, nativeLibs);
                continue;
            }
            
            try
            {
                await using var entryStream = entry.Open();
                await using var fileStream = File.Create(targetPath);
                await entryStream.CopyToAsync(fileStream, ct);
                
                _logger?.LogDebug("Extracted: {Path}", targetPath);
                
                // Track extracted file
                TrackFile(targetPath, ext, debugFiles, pdbFiles, nativeLibs);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to extract {Entry}", entry.FullName);
            }
        }
        
        return (debugFiles, pdbFiles, nativeLibs);
    }
    
    /// <summary>
    /// Determines the target directory for a file based on artifact type and file extension.
    /// </summary>
    /// <param name="entryFullName">Full path within the ZIP.</param>
    /// <param name="entryName">Just the filename.</param>
    /// <param name="extension">File extension.</param>
    /// <param name="artifactType">Type of artifact being processed.</param>
    /// <param name="nativeDir">Directory for native symbols.</param>
    /// <param name="managedDir">Directory for managed symbols.</param>
    /// <param name="targetTfm">Target framework moniker folder.</param>
    /// <param name="platformSuffix">Platform folder name (e.g., "linux-musl-arm64").</param>
    /// <returns>Tuple of target directory and whether to extract.</returns>
    private (string? targetDir, bool shouldExtract) GetTargetDirectory(
        string entryFullName,
        string entryName,
        string extension,
        DatadogArtifactType artifactType,
        string nativeDir,
        string managedDir,
        string targetTfm,
        string platformSuffix)
    {
        var extLower = extension.ToLowerInvariant();
        var fullNameLower = entryFullName.ToLowerInvariant().Replace('\\', '/');
        var platformFolderLower = platformSuffix.ToLowerInvariant();
        
        switch (artifactType)
        {
            case DatadogArtifactType.TracerSymbols:
            case DatadogArtifactType.ProfilerSymbols:
                // Native symbols - these are in platform subfolder
                // Structure: linux-musl-arm64/*.debug
                if (extLower == ".debug" || extLower == ".pdb")
                {
                    // Check if in platform subfolder (for Linux) or at root (for Windows)
                    if (IsInPlatformFolder(fullNameLower, platformFolderLower) || 
                        !fullNameLower.Contains('/'))
                    {
                        return (nativeDir, true);
                    }
                }
                break;
            
            case DatadogArtifactType.MonitoringHome:
                // Monitoring home has both:
                // - Native binaries in platform subfolder: linux-musl-arm64/*.so
                // - Managed assemblies in TFM folders: net6.0/*.dll, net6.0/*.pdb
                
                // Check for managed code in TFM folder
                if (IsInTfmFolder(fullNameLower, targetTfm))
                {
                    if (extLower == ".pdb" || extLower == ".dll")
                        return (managedDir, true);
                }
                
                // Check for native libraries in platform subfolder
                if (extLower == ".so" || extLower == ".dylib" || extLower == ".dll")
                {
                    if (IsInPlatformFolder(fullNameLower, platformFolderLower))
                    {
                        return (nativeDir, true);
                    }
                }
                break;
            
            case DatadogArtifactType.UniversalSymbols:
                // Universal symbols have FLAT structure - .debug files at root level
                // These are native symbols (Datadog.Linux.ApiWrapper.x64.debug, etc.)
                if (extLower == ".debug")
                {
                    // Flat structure: files at root level (no subfolder)
                    // Only extract if NOT in a subfolder
                    if (!fullNameLower.Contains('/') || 
                        fullNameLower.StartsWith($"{Path.GetFileName(fullNameLower)}"))
                    {
                        return (nativeDir, true);
                    }
                    // Also accept if directly under artifact name folder
                    // (ZIP may have: artifact-name/file.debug)
                    var parts = fullNameLower.Split('/');
                    if (parts.Length <= 2)
                    {
                        return (nativeDir, true);
                    }
                }
                break;
        }
        
        return (null, false);
    }
    
    /// <summary>
    /// Checks if the entry path is within the platform folder.
    /// </summary>
    private static bool IsInPlatformFolder(string fullName, string platformFolder)
    {
        return fullName.Contains($"/{platformFolder}/") ||
               fullName.StartsWith($"{platformFolder}/");
    }
    
    /// <summary>
    /// Checks if the entry path is within the target TFM folder.
    /// </summary>
    private static bool IsInTfmFolder(string fullName, string targetTfm)
    {
        var tfmLower = targetTfm.ToLowerInvariant();
        
        // Match paths like:
        // - monitoring-home/net6.0/Datadog.Trace.dll
        // - universal-symbols/net6.0/Datadog.Trace.pdb
        // - tracer-home/net6.0/publish/...
        
        return fullName.Contains($"/{tfmLower}/") || 
               fullName.Contains($"\\{tfmLower}\\") ||
               fullName.Contains($"/{tfmLower}\\") ||
               fullName.Contains($"\\{tfmLower}/");
    }
    
    /// <summary>
    /// Tracks a file in the appropriate list based on extension.
    /// </summary>
    private static void TrackFile(
        string path, 
        string extension,
        List<string> debugFiles,
        List<string> pdbFiles,
        List<string> nativeLibs)
    {
        var ext = extension.ToLowerInvariant();
        
        switch (ext)
        {
            case ".debug":
                debugFiles.Add(path);
                break;
            case ".pdb":
                pdbFiles.Add(path);
                break;
            case ".so":
            case ".dll":
            case ".dylib":
                nativeLibs.Add(path);
                break;
        }
    }
}

