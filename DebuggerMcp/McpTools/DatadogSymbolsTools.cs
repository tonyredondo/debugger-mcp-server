using System.ComponentModel;
using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Serialization;
using DebuggerMcp.SourceLink;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.McpTools;

/// <summary>
/// MCP tools for downloading and loading Datadog.Trace symbols from Azure Pipelines.
/// </summary>
/// <remarks>
/// Provides tools for:
/// <list type="bullet">
/// <item><description>Downloading Datadog.Trace symbols for specific commits</description></item>
/// <item><description>Listing available artifacts from Azure Pipelines builds</description></item>
/// <item><description>Loading symbols into the debugger for better stack traces</description></item>
/// </list>
/// </remarks>
public class DatadogSymbolsTools(
    DebuggerSessionManager sessionManager,
    SymbolManager symbolManager,
    WatchStore watchStore,
    ILogger<DatadogSymbolsTools> logger)
    : DebuggerToolsBase(sessionManager, symbolManager, watchStore, logger)
{
    /// <summary>
    /// JSON serialization options for results.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializationDefaults.Indented;

    /// <summary>
    /// Downloads Datadog.Trace symbols from Azure Pipelines or GitHub for a specific commit.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="commitSha">The commit SHA from the Datadog.Trace assembly.</param>
    /// <param name="targetFramework">Optional target framework (auto-detected if not specified).</param>
    /// <param name="loadIntoDebugger">Whether to load symbols into the debugger (default: true).</param>
    /// <param name="forceVersion">If true, falls back to version/tag lookup when SHA lookup fails (default: false).</param>
    /// <param name="version">Optional version for fallback lookup (e.g., "3.31.0"). Extracted from SHA if not provided.</param>
    /// <returns>JSON result with download status and loaded symbols.</returns>
    /// <remarks>
    /// This tool downloads Datadog.Trace symbols from Azure Pipelines builds or GitHub releases.
    /// The commit SHA can be found in the assembly's InformationalVersion attribute.
    /// 
    /// By default, only exact SHA matches are used (Azure Pipelines + GitHub SHA lookup).
    /// If forceVersion is true, falls back to version-based lookup which may not exactly match your binary.
    /// 
    /// Symbols are downloaded for:
    /// - Native tracer symbols (.debug files)
    /// - Native profiler symbols (.debug files)
    /// - Managed symbols (.pdb files)
    /// 
    /// After download, symbols are automatically loaded into LLDB for improved stack traces.
    /// </remarks>
    public async Task<string> DownloadDatadogSymbols(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Commit SHA from the Datadog.Trace assembly (from InformationalVersion)")] string commitSha,
        [Description("Target framework (e.g., net6.0, netcoreapp3.1). Auto-detected if not specified.")] string? targetFramework = null,
        [Description("Whether to load symbols into the debugger after download (default: true)")] bool loadIntoDebugger = true,
        [Description("If true, falls back to version/tag lookup when SHA lookup fails (default: false)")] bool forceVersion = false,
        [Description("Optional version for fallback lookup (e.g., '3.31.0'). If not provided and forceVersion=true, will try to extract from commitSha.")] string? version = null,
        [Description("Optional Azure Pipelines build ID to download symbols directly (bypasses SHA/version lookup)")] int? buildId = null)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);
        var sanitizedUserId = SanitizeUserId(userId);

        // Must be at least 7 characters for a short SHA
        if (string.IsNullOrWhiteSpace(commitSha) || commitSha.Length < 7)
        {
            // Shorter SHAs are ambiguous and would cause noisy lookups
            throw new ArgumentException("commitSha must be at least 7 characters", nameof(commitSha));
        }

        // Check if feature is enabled
        if (!DatadogTraceSymbolsConfig.IsEnabled())
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Datadog symbol download is disabled. Set DATADOG_TRACE_SYMBOLS_ENABLED=true to enable."
            }, JsonOptions);
        }

        // Get the session and debugger manager
        var session = GetSessionInfo(sessionId, sanitizedUserId);
        var debuggerManager = GetSessionManager(sessionId, sanitizedUserId);

        // Detect platform from the dump using image list (includes native modules with arch info)
        PlatformInfo platform;
        try
        {
            // Use 'image list' to get native module paths which include architecture info
            // e.g., /lib/ld-musl-aarch64.so.1 tells us it's musl (Alpine) and aarch64 (ARM64)
            var imageListOutput = debuggerManager.ExecuteCommand("image list");
            platform = DetectPlatformFromSession(session, imageListOutput);
            Logger.LogInformation("[DatadogSymbols] Detected platform: {Os} {Arch} (Alpine: {IsAlpine})",
                platform.Os, platform.Architecture, platform.IsAlpine);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to detect platform, using defaults");
            platform = new PlatformInfo
            {
                Os = Environment.OSVersion.Platform == PlatformID.Win32NT ? "Windows" : "Linux",
                Architecture = Environment.Is64BitProcess ? "x64" : "x86"
            };
        }

        // Determine target TFM
        var tfm = !string.IsNullOrEmpty(targetFramework)
            ? targetFramework
            : DatadogArtifactMapper.GetTargetTfmFolder(platform.RuntimeVersion ?? ".NET 6.0");

        // Build output directory - Datadog symbols go in a .datadog subdirectory
        // to keep them separate from user-uploaded symbols
        if (string.IsNullOrEmpty(session.CurrentDumpId))
        {
            throw new InvalidOperationException("No dump is currently open. Open a dump first.");
        }

        var dumpStorage = SessionManager.GetDumpStoragePath();
        var symbolsDir = BuildDatadogSymbolsDirectory(dumpStorage, sanitizedUserId, session.CurrentDumpId);

        // Create symbol service and download
        var symbolService = new DatadogSymbolService(session.ClrMdAnalyzer, Logger);
        DatadogSymbolDownloadResult downloadResult;
        
        if (buildId.HasValue)
        {
            // Download directly from specific Azure Pipelines build ID (bypasses SHA/version lookup)
            Logger.LogInformation("[DatadogSymbols] Downloading symbols from Azure Pipelines build ID: {BuildId}", buildId.Value);
            downloadResult = await symbolService.DownloadFromBuildIdAsync(
                buildId.Value,
                platform,
                symbolsDir,
                tfm);
        }
        else
        {
            // Use the 4-step lookup (SHA -> GitHub SHA -> Version -> GitHub Version)
            downloadResult = await symbolService.DownloadSymbolsAsync(
                commitSha,
                version,  // Pass the optional version for fallback
                platform,
                symbolsDir,
                tfm,
                forceVersion);
        }

        // If there's a SHA mismatch (using forceVersion), patch the PDBs to match the DLLs
        // This allows SOS to load the symbols even when the exact build doesn't match
        List<PdbPatcher.PatchResult>? patchResults = null;
        Logger.LogInformation("[DatadogSymbols] Checking PDB patching conditions: Success={Success}, ShaMismatch={ShaMismatch}, ManagedDir={ManagedDir}",
            downloadResult.Success, downloadResult.ShaMismatch, downloadResult.MergeResult?.ManagedSymbolDirectory ?? "(null)");

        if (downloadResult.Success && downloadResult.ShaMismatch && downloadResult.MergeResult?.ManagedSymbolDirectory != null)
        {
            Logger.LogInformation("[DatadogSymbols] SHA mismatch detected, patching PDBs to match DLL GUIDs in {Dir}...",
                downloadResult.MergeResult.ManagedSymbolDirectory);
            var patcher = new PdbPatcher(Logger);
            patchResults = patcher.PatchAllPdbsInDirectory(downloadResult.MergeResult.ManagedSymbolDirectory);

            var patchedCount = patchResults.Count(r => r.WasPatched);
            var totalCount = patchResults.Count;
            Logger.LogInformation("[DatadogSymbols] PDB patching complete: {Patched}/{Total} files patched", patchedCount, totalCount);
        }
        else
        {
            // Skip patching when we don't have a mismatch or managed symbols to edit
            Logger.LogDebug("[DatadogSymbols] PDB patching skipped (conditions not met)");
        }

        // Load symbols if requested and download succeeded
        SymbolLoadResult? loadResult = null;
        if (loadIntoDebugger && downloadResult.Success && downloadResult.MergeResult != null)
        {
            var loader = new DatadogSymbolLoader(Logger);
            loadResult = await loader.LoadSymbolsAsync(
                downloadResult.MergeResult,
                cmd => debuggerManager.ExecuteCommand(cmd));

            // Clear command cache after loading new symbols so subsequent commands
            // Note: ClrMD handles most operations now, no cache to clear
        }

        // Build response
        var response = new
        {
            success = downloadResult.Success,
            buildId = downloadResult.BuildId,
            buildNumber = downloadResult.BuildNumber,
            buildUrl = downloadResult.BuildUrl,
            downloadedArtifacts = downloadResult.DownloadedArtifacts,
            symbolDirectory = downloadResult.MergeResult?.SymbolDirectory,
            nativeSymbolsDirectory = downloadResult.MergeResult?.NativeSymbolDirectory,
            managedSymbolsDirectory = downloadResult.MergeResult?.ManagedSymbolDirectory,
            filesExtracted = downloadResult.MergeResult?.TotalFilesExtracted ?? 0,
            shaMismatch = downloadResult.ShaMismatch,
            source = downloadResult.Source,
            pdbsPatched = patchResults != null ? new
            {
                total = patchResults.Count,
                patched = patchResults.Count(r => r.WasPatched),
                verified = patchResults.Count(r => r.WasPatched && r.Verified),
                files = patchResults.Where(r => r.WasPatched).Select(r => new
                {
                    file = Path.GetFileName(r.PdbPath),
                    originalGuid = r.OriginalGuid.ToString(),
                    newGuid = r.NewGuid.ToString(),
                    verified = r.Verified,
                    verifiedGuid = r.VerifiedGuid?.ToString()
                }).ToList()
            } : null,
            platform = new
            {
                os = platform.Os,
                architecture = platform.Architecture,
                isAlpine = platform.IsAlpine,
                suffix = DatadogArtifactMapper.GetPlatformSuffix(platform)
            },
            targetFramework = tfm,
            symbolsLoaded = loadResult != null ? new
            {
                success = loadResult.Success,
                nativeSymbolsLoaded = loadResult.NativeSymbolsLoaded.Count,
                managedSymbolPaths = loadResult.ManagedSymbolPaths.Count,
                commandsExecuted = loadResult.CommandsExecuted.Count
            } : null,
            error = downloadResult.ErrorMessage ?? loadResult?.ErrorMessage
        };

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    /// <summary>
    /// Automatically downloads Datadog.Trace symbols by scanning the dump for assemblies.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="loadIntoDebugger">Whether to load symbols into the debugger (default: true).</param>
    /// <param name="forceVersion">If true, falls back to version/tag lookup when SHA lookup fails (default: false).</param>
    /// <returns>JSON result with download status and loaded symbols.</returns>
    /// <remarks>
    /// This tool automatically scans the dump for Datadog assemblies, extracts the commit SHA
    /// from InformationalVersion, and downloads the appropriate symbols from Azure Pipelines or GitHub.
    /// 
    /// By default, only exact SHA matches are used. If forceVersion is true, falls back to
    /// version-based lookup which may not exactly match your binary.
    /// 
    /// This is the recommended way to download Datadog symbols when you have a dump open,
    /// as it handles all the detection automatically.
    /// </remarks>
    public async Task<string> PrepareDatadogSymbols(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Whether to load symbols into the debugger after download (default: true)")] bool loadIntoDebugger = true,
        [Description("If true, falls back to version/tag lookup when SHA lookup fails (default: false)")] bool forceVersion = false)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);
        var sanitizedUserId = SanitizeUserId(userId);

        // Check if feature is enabled
        if (!DatadogTraceSymbolsConfig.IsEnabled())
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Datadog symbol download is disabled. Set DATADOG_TRACE_SYMBOLS_ENABLED=true to enable."
            }, JsonOptions);
        }

        // Get the session and debugger manager
        var session = GetSessionInfo(sessionId, sanitizedUserId);
        var debuggerManager = GetSessionManager(sessionId, sanitizedUserId);

        // Detect platform from the dump using image list (includes native modules with arch info)
        PlatformInfo platform;
        try
        {
            // Use 'image list' to get native module paths which include architecture info
            // e.g., /lib/ld-musl-aarch64.so.1 tells us it's musl (Alpine) and aarch64 (ARM64)
            var imageListOutput = debuggerManager.ExecuteCommand("image list");
            platform = DetectPlatformFromSession(session, imageListOutput);
            Logger.LogInformation("[DatadogSymbols] Detected platform: {Os} {Arch} (Alpine: {IsAlpine})",
                platform.Os, platform.Architecture, platform.IsAlpine);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to detect platform, using defaults");
            platform = new PlatformInfo
            {
                Os = Environment.OSVersion.Platform == PlatformID.Win32NT ? "Windows" : "Linux",
                Architecture = Environment.Is64BitProcess ? "x64" : "x86"
            };
        }

        // Build output directory - Datadog symbols go in a .datadog subdirectory
        // to keep them separate from user-uploaded symbols
        if (string.IsNullOrEmpty(session.CurrentDumpId))
        {
            throw new InvalidOperationException("No dump is currently open. Open a dump first.");
        }

        var dumpStorage = SessionManager.GetDumpStoragePath();
        var symbolsDir = BuildDatadogSymbolsDirectory(dumpStorage, sanitizedUserId, session.CurrentDumpId);

        // Create the symbol service
        var symbolService = new DatadogSymbolService(session.ClrMdAnalyzer, Logger);

        // Prepare symbols (this handles scanning, downloading, and loading)
        var prepResult = await symbolService.PrepareSymbolsAsync(
            platform,
            symbolsDir,
            debuggerManager.ExecuteCommand,
            loadIntoDebugger,
            forceVersion);

        // Clear command cache after loading new symbols so subsequent commands
        // Note: ClrMD handles most operations now, no cache to clear

        // Build response
        var response = new
        {
            success = prepResult.Success,
            message = prepResult.Message,
            datadogAssemblies = prepResult.DatadogAssemblies.Select(a => new
            {
                name = a.Name,
                informationalVersion = a.InformationalVersion,
                commitSha = a.CommitSha != null ? DatadogTraceSymbolsConfig.GetShortSha(a.CommitSha) : null,
                targetFramework = a.TargetFramework
            }).ToList(),
            downloadResult = prepResult.DownloadResult != null ? new
            {
                buildId = prepResult.DownloadResult.BuildId,
                buildNumber = prepResult.DownloadResult.BuildNumber,
                buildUrl = prepResult.DownloadResult.BuildUrl,
                downloadedArtifacts = prepResult.DownloadResult.DownloadedArtifacts,
                filesExtracted = prepResult.DownloadResult.MergeResult?.TotalFilesExtracted ?? 0,
                shaMismatch = prepResult.DownloadResult.ShaMismatch,
                source = prepResult.DownloadResult.Source
            } : null,
            pdbsPatched = prepResult.PatchResults != null ? new
            {
                total = prepResult.PatchResults.Count,
                patched = prepResult.PatchResults.Count(r => r.WasPatched),
                verified = prepResult.PatchResults.Count(r => r.WasPatched && r.Verified),
                files = prepResult.PatchResults.Where(r => r.WasPatched).Select(r => new
                {
                    file = Path.GetFileName(r.PdbPath),
                    originalGuid = r.OriginalGuid.ToString(),
                    newGuid = r.NewGuid.ToString(),
                    verified = r.Verified,
                    verifiedGuid = r.VerifiedGuid?.ToString()
                }).ToList()
            } : null,
            symbolsLoaded = prepResult.LoadResult != null ? new
            {
                nativeSymbolsLoaded = prepResult.LoadResult.NativeSymbolsLoaded.Count,
                managedSymbolPaths = prepResult.LoadResult.ManagedSymbolPaths.Count,
                commandsExecuted = prepResult.LoadResult.CommandsExecuted.Count
            } : null,
            platform = new
            {
                os = platform.Os,
                architecture = platform.Architecture,
                isAlpine = platform.IsAlpine
            }
        };

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    /// <summary>
    /// Lists available artifacts from an Azure Pipelines build for Datadog.Trace.
    /// </summary>
    /// <param name="commitSha">The commit SHA to find the build for.</param>
    /// <returns>JSON result with build info and available artifacts.</returns>
    /// <remarks>
    /// This tool queries Azure Pipelines to find builds for a specific commit SHA
    /// and lists all available artifacts. This is useful for understanding what
    /// artifacts are available before downloading.
    /// </remarks>
    public async Task<string> ListDatadogArtifacts(
        [Description("Commit SHA from the Datadog.Trace assembly")] string commitSha)
    {
        // Validate commitSha
        if (string.IsNullOrWhiteSpace(commitSha))
        {
            throw new ArgumentException("commitSha cannot be null or empty", nameof(commitSha));
        }

        // Check if feature is enabled
        if (!DatadogTraceSymbolsConfig.IsEnabled())
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Datadog symbol download is disabled. Set DATADOG_TRACE_SYMBOLS_ENABLED=true to enable."
            }, JsonOptions);
        }

        using var resolver = new AzurePipelinesResolver(
            DatadogTraceSymbolsConfig.GetCacheDirectory(),
            Logger);

        // Find build
        var build = await resolver.FindBuildByCommitAsync(
            DatadogTraceSymbolsConfig.AzureDevOpsOrganization,
            DatadogTraceSymbolsConfig.AzureDevOpsProject,
            commitSha);

        if (build == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"No build found for commit {commitSha[..Math.Min(8, commitSha.Length)]}"
            }, JsonOptions);
        }

        // List artifacts
        var artifacts = await resolver.ListArtifactsAsync(
            DatadogTraceSymbolsConfig.AzureDevOpsOrganization,
            DatadogTraceSymbolsConfig.AzureDevOpsProject,
            build.Id);

        // Group artifacts by category
        var tracerSymbols = artifacts.Where(a => a.Name.Contains("tracer-symbols")).Select(a => a.Name).ToList();
        var profilerSymbols = artifacts.Where(a => a.Name.Contains("profiler-symbols")).Select(a => a.Name).ToList();
        var monitoringHome = artifacts.Where(a => a.Name.Contains("monitoring-home")).Select(a => a.Name).ToList();
        var universalSymbols = artifacts.Where(a => a.Name.Contains("universal-symbols")).Select(a => a.Name).ToList();
        var other = artifacts.Where(a =>
            !a.Name.Contains("tracer-symbols") &&
            !a.Name.Contains("profiler-symbols") &&
            !a.Name.Contains("monitoring-home") &&
            !a.Name.Contains("universal-symbols")).Select(a => a.Name).ToList();

        var response = new
        {
            success = true,
            build = new
            {
                id = build.Id,
                number = build.BuildNumber,
                status = build.Status,
                result = build.Result,
                branch = build.SourceBranch,
                commit = build.SourceVersion,
                finishTime = build.FinishTime,
                url = build.WebUrl
            },
            totalArtifacts = artifacts.Count,
            artifactsByCategory = new
            {
                tracerSymbols,
                profilerSymbols,
                monitoringHome,
                universalSymbols,
                other
            },
            platformSuffixes = new[]
            {
                "linux-x64",
                "linux-arm64",
                "linux-musl-x64",
                "linux-musl-arm64",
                "win-x64",
                "win-x86",
                "osx-x64",
                "osx-arm64"
            }
        };

        resolver.SaveCache();

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    /// <summary>
    /// Gets information about Datadog symbol download configuration and status.
    /// </summary>
    /// <returns>JSON with configuration information.</returns>
    public string GetDatadogSymbolsConfig()
    {
        var response = new
        {
            enabled = DatadogTraceSymbolsConfig.IsEnabled(),
            hasPatToken = !string.IsNullOrEmpty(DatadogTraceSymbolsConfig.GetPatToken()),
            cacheDirectory = DatadogTraceSymbolsConfig.GetCacheDirectory(),
            timeoutSeconds = DatadogTraceSymbolsConfig.GetTimeoutSeconds(),
            maxArtifactSizeMB = DatadogTraceSymbolsConfig.GetMaxArtifactSize() / 1024 / 1024,
            azureDevOps = new
            {
                organization = DatadogTraceSymbolsConfig.AzureDevOpsOrganization,
                project = DatadogTraceSymbolsConfig.AzureDevOpsProject,
                baseUrl = DatadogTraceSymbolsConfig.AzureDevOpsBaseUrl
            },
            environmentVariables = new Dictionary<string, string>
            {
                ["DATADOG_TRACE_SYMBOLS_ENABLED"] = "Enable/disable feature (default: true)",
                ["DATADOG_TRACE_SYMBOLS_PAT"] = "Optional Azure DevOps PAT for private repos",
                ["DATADOG_TRACE_SYMBOLS_CACHE_DIR"] = "Custom cache directory",
                ["DATADOG_TRACE_SYMBOLS_TIMEOUT_SECONDS"] = "API/download timeout (default: 120)"
            }
        };

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    /// <summary>
    /// Clears downloaded Datadog symbols for the current dump.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="clearApiCache">Whether to also clear the API cache (build lookups, release lookups).</param>
    /// <returns>JSON result with cleared files information.</returns>
    /// <remarks>
    /// This tool removes all previously downloaded Datadog symbols for the current dump.
    /// Use this to:
    /// <list type="bullet">
    /// <item><description>Force re-download of symbols</description></item>
    /// <item><description>Reclaim disk space</description></item>
    /// <item><description>Clear stale/corrupted symbol files</description></item>
    /// </list>
    /// </remarks>
    public string ClearDatadogSymbols(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Also clear API cache (build/release lookups). Default: false")] bool clearApiCache = false)
    {
        ValidateSessionId(sessionId);
        var sanitizedUserId = SanitizeUserId(userId);

        // Get session info to find dump ID
        var session = GetSessionInfo(sessionId, sanitizedUserId);

        if (string.IsNullOrEmpty(session.CurrentDumpId))
        {
            throw new InvalidOperationException("No dump is currently open. Open a dump first.");
        }

        var dumpStorage = SessionManager.GetDumpStoragePath();
        var dumpName = Path.GetFileNameWithoutExtension(session.CurrentDumpId);
        // Only delete the .datadog subdirectory, not the entire symbols directory
        var datadogSymbolsDir = Path.Combine(dumpStorage, sanitizedUserId, $".symbols_{dumpName}", ".datadog");

        var filesDeleted = 0;
        var totalSizeMb = 0.0;
        var apiCacheCleared = false;

        // Delete Datadog symbols directory if it exists
        if (Directory.Exists(datadogSymbolsDir))
        {
            try
            {
                var files = Directory.GetFiles(datadogSymbolsDir, "*", SearchOption.AllDirectories);
                filesDeleted = files.Length;
                totalSizeMb = files.Sum(f => new FileInfo(f).Length) / (1024.0 * 1024.0);

                Directory.Delete(datadogSymbolsDir, recursive: true);
                Logger.LogInformation("[DatadogSymbols] Deleted {FileCount} files ({SizeMb:F1} MB) from {Path}",
                    filesDeleted, totalSizeMb, datadogSymbolsDir);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[DatadogSymbols] Failed to delete symbols at {Path}", datadogSymbolsDir);
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"Failed to delete symbols: {ex.Message}"
                }, JsonOptions);
            }
        }
        else
        {
            Logger.LogInformation("[DatadogSymbols] No Datadog symbol directory found at {Path}", datadogSymbolsDir);
        }

        // Optionally clear API caches
        if (clearApiCache)
        {
            try
            {
                var cacheDir = DatadogTraceSymbolsConfig.GetCacheDirectory();
                var azureCachePath = Path.Combine(cacheDir, "azure_pipelines_cache.json");
                var githubCachePath = Path.Combine(cacheDir, "github_releases_cache.json");

                if (File.Exists(azureCachePath))
                {
                    File.Delete(azureCachePath);
                    Logger.LogInformation("[DatadogSymbols] Deleted Azure Pipelines cache");
                }

                if (File.Exists(githubCachePath))
                {
                    File.Delete(githubCachePath);
                    Logger.LogInformation("[DatadogSymbols] Deleted GitHub Releases cache");
                }

                apiCacheCleared = true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[DatadogSymbols] Failed to clear API caches");
            }
        }

        // Clear debugger command cache since symbols are now gone
        try
        {
            // Session found - symbols will be re-evaluated on next command
            _ = GetSessionManager(sessionId, sanitizedUserId);
        }
        catch (Exception)
        {
            // Session not found or expired - that's okay, we still cleared the files
        }

        var response = new
        {
            success = true,
            message = filesDeleted > 0
                ? $"Cleared Datadog symbols for dump {dumpName}"
                : $"No Datadog symbols found for dump {dumpName}",
            dumpId = dumpName,
            filesDeleted,
            sizeFreedMb = Math.Round(totalSizeMb, 2),
            apiCacheCleared
        };

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    /// <summary>
    /// Detects platform information from the session and debugger output.
    /// Uses 'image list' output and falls back to ClrMD for native module detection.
    /// </summary>
    private PlatformInfo DetectPlatformFromSession(DebuggerSession session, string debuggerOutput)
    {
        var (platform, detectedArchitectureFromDebugger, detectedAlpineFromDebugger) = DetectPlatformFromDebuggerOutput(debuggerOutput);
        
        // If LLDB didn't give us enough info (e.g., standalone app), try dotnet-symbol --verifycore
        // This is the most reliable method as it directly parses the dump's module list
        if ((!detectedAlpineFromDebugger || !detectedArchitectureFromDebugger) && session.Manager is LldbManager lldbManager)
        {
            Logger.LogDebug("[DatadogSymbols] Checking dotnet-symbol --verifycore result...");
            
            // Check if we already have verified core result (populated during dump open), or run it now
            var verifyCoreResult = lldbManager.VerifiedCorePlatform;
            if (verifyCoreResult == null && !string.IsNullOrEmpty(lldbManager.CurrentDumpPath))
            {
                Logger.LogDebug("[DatadogSymbols] Running dotnet-symbol --verifycore for platform detection...");
                verifyCoreResult = lldbManager.VerifyCore(lldbManager.CurrentDumpPath);
            }
            
            if (verifyCoreResult != null)
            {
                if (!detectedAlpineFromDebugger && verifyCoreResult.IsAlpine)
                {
                    platform.IsAlpine = true;
                    platform.LibcType = "musl";
                    Logger.LogInformation("[DatadogSymbols] Detected Alpine/musl via dotnet-symbol --verifycore");
                }
                
                if (!detectedArchitectureFromDebugger && !string.IsNullOrEmpty(verifyCoreResult.Architecture))
                {
                    platform.Architecture = verifyCoreResult.Architecture;
                    Logger.LogInformation("[DatadogSymbols] Detected architecture {Arch} via dotnet-symbol --verifycore", verifyCoreResult.Architecture);
                }
            }
        }
        
        // Fallback to ClrMD if dotnet-symbol didn't work
        if ((platform.IsAlpine != true || !detectedArchitectureFromDebugger) && session.ClrMdAnalyzer != null)
        {
            Logger.LogDebug("[DatadogSymbols] Trying ClrMD native modules as final fallback...");
            
            // Try to detect Alpine from ClrMD native modules
            if (platform.IsAlpine != true && session.ClrMdAnalyzer.DetectIsAlpine())
            {
                platform.IsAlpine = true;
                platform.LibcType = "musl";
                Logger.LogInformation("[DatadogSymbols] Detected Alpine/musl via ClrMD native modules");
            }
            
            // Also try to detect architecture from ClrMD if not detected
            if (!detectedArchitectureFromDebugger)
            {
                var arch = session.ClrMdAnalyzer.DetectArchitecture();
                if (!string.IsNullOrEmpty(arch))
                {
                    platform.Architecture = arch;
                    Logger.LogInformation("[DatadogSymbols] Detected architecture {Arch} via ClrMD", arch);
                }
            }
        }
        
        if (platform.Os == "Linux" && platform.IsAlpine != true)
        {
            // Default to glibc for Linux if musl not detected
            platform.LibcType = "glibc";
        }

        return platform;
    }

    /// <summary>
    /// Detects platform information purely from debugger output (e.g., LLDB <c>image list</c>).
    /// </summary>
    /// <param name="debuggerOutput">Debugger output containing module paths.</param>
    /// <returns>Detected platform info along with whether key properties were inferred from the output.</returns>
    /// <remarks>
    /// This method is internal to enable deterministic unit testing of parsing behavior
    /// without requiring a dump.
    /// </remarks>
    internal static (PlatformInfo Platform, bool DetectedArchitecture, bool DetectedAlpine) DetectPlatformFromDebuggerOutput(string debuggerOutput)
    {
        var platform = new PlatformInfo
        {
            Os = "Linux",  // Default to Linux since most dumps will be Linux
            Architecture = "x64"  // Default, will be overridden if detected
        };

        var outputLower = (debuggerOutput ?? string.Empty).ToLowerInvariant();
        var detectedArchitecture = false;

        // Detect architecture first - look for specific patterns in module paths
        // Common patterns: /lib/ld-musl-aarch64.so, /lib64/ld-linux-x86-64.so.2
        if (outputLower.Contains("aarch64") || outputLower.Contains("-arm64"))
        {
            platform.Architecture = "arm64";
            detectedArchitecture = true;
        }
        else if (outputLower.Contains("x86_64") || outputLower.Contains("x86-64") || outputLower.Contains("amd64"))
        {
            platform.Architecture = "x64";
            detectedArchitecture = true;
        }
        else if (outputLower.Contains("i386") || outputLower.Contains("i686"))
        {
            platform.Architecture = "x86";
            detectedArchitecture = true;
        }

        // Detect OS from module extensions and paths
        if (outputLower.Contains(".dylib") || outputLower.Contains("/usr/lib/dyld"))
        {
            platform.Os = "macOS";
        }
        else if (outputLower.Contains(".dll") || outputLower.Contains("\\windows\\"))
        {
            platform.Os = "Windows";
        }
        else if (outputLower.Contains(".so") || outputLower.Contains("/lib/") || outputLower.Contains("/usr/"))
        {
            platform.Os = "Linux";
        }

        // Detect Alpine/musl - look for ld-musl in the loader path
        // e.g., /lib/ld-musl-aarch64.so.1 or /lib/ld-musl-x86_64.so.1
        var detectedAlpine = false;
        if (outputLower.Contains("ld-musl") || outputLower.Contains("musl-"))
        {
            platform.IsAlpine = true;
            platform.LibcType = "musl";
            detectedAlpine = true;
        }

        if (platform.Os == "Linux" && platform.IsAlpine != true)
        {
            platform.LibcType = "glibc";
        }

        return (platform, detectedArchitecture, detectedAlpine);
    }

    /// <summary>
    /// Builds the per-dump Datadog symbols directory.
    /// </summary>
    internal static string BuildDatadogSymbolsDirectory(string dumpStoragePath, string sanitizedUserId, string currentDumpId)
    {
        if (string.IsNullOrEmpty(currentDumpId))
        {
            throw new InvalidOperationException("No dump is currently open. Open a dump first.");
        }

        var dumpName = Path.GetFileNameWithoutExtension(currentDumpId);
        return Path.Combine(dumpStoragePath, sanitizedUserId, $".symbols_{dumpName}", ".datadog");
    }
}
