using System.ComponentModel;
using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.SourceLink;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

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
[McpServerToolType]
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
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Downloads Datadog.Trace symbols from Azure Pipelines for a specific commit.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="commitSha">The commit SHA from the Datadog.Trace assembly.</param>
    /// <param name="targetFramework">Optional target framework (auto-detected if not specified).</param>
    /// <param name="loadIntoDebugger">Whether to load symbols into the debugger (default: true).</param>
    /// <returns>JSON result with download status and loaded symbols.</returns>
    /// <remarks>
    /// This tool downloads Datadog.Trace symbols from Azure Pipelines builds.
    /// The commit SHA can be found in the assembly's InformationalVersion attribute.
    /// 
    /// Symbols are downloaded for:
    /// - Native tracer symbols (.debug files)
    /// - Native profiler symbols (.debug files)
    /// - Managed symbols (.pdb files)
    /// 
    /// After download, symbols are automatically loaded into LLDB for improved stack traces.
    /// </remarks>
    [McpServerTool, Description("Download Datadog.Trace symbols from Azure Pipelines for a specific commit SHA")]
    public async Task<string> DownloadDatadogSymbols(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Commit SHA from the Datadog.Trace assembly (from InformationalVersion)")] string commitSha,
        [Description("Target framework (e.g., net6.0, netcoreapp3.1). Auto-detected if not specified.")] string? targetFramework = null,
        [Description("Whether to load symbols into the debugger after download (default: true)")] bool loadIntoDebugger = true)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);
        var sanitizedUserId = SanitizeUserId(userId);

        // Validate commitSha
        if (string.IsNullOrWhiteSpace(commitSha))
        {
            throw new ArgumentException("commitSha cannot be null or empty", nameof(commitSha));
        }

        // Must be at least 7 characters for a short SHA
        if (commitSha.Length < 7)
        {
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

        // Detect platform from the dump
        PlatformInfo platform;
        try
        {
            var platformOutput = debuggerManager.ExecuteCommand("clrmodules");
            platform = DetectPlatformFromSession(session, platformOutput);
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

        // Build output directory
        var dumpStorage = SessionManager.GetDumpStoragePath();
        var symbolsDir = Path.Combine(dumpStorage, sanitizedUserId, $".symbols_{Path.GetFileNameWithoutExtension(session.CurrentDumpId)}");

        // Create resolver and download
        using var resolver = new AzurePipelinesResolver(
            DatadogTraceSymbolsConfig.GetCacheDirectory(),
            Logger);

        var downloadResult = await resolver.DownloadDatadogSymbolsAsync(
            commitSha,
            platform,
            symbolsDir,
            tfm);

        // Load symbols if requested and download succeeded
        SymbolLoadResult? loadResult = null;
        if (loadIntoDebugger && downloadResult.Success && downloadResult.MergeResult != null)
        {
            var loader = new DatadogSymbolLoader(Logger);
            loadResult = await loader.LoadSymbolsAsync(
                downloadResult.MergeResult,
                cmd => debuggerManager.ExecuteCommand(cmd));
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

        resolver.SaveCache();

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
    [McpServerTool, Description("List available Datadog.Trace artifacts from Azure Pipelines for a commit SHA")]
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
    [McpServerTool, Description("Get Datadog symbol download configuration and status")]
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
    /// Detects platform information from the session and debugger output.
    /// </summary>
    private PlatformInfo DetectPlatformFromSession(DebuggerSession session, string debuggerOutput)
    {
        var platform = new PlatformInfo
        {
            Os = Environment.OSVersion.Platform == PlatformID.Win32NT ? "Windows" : "Linux",
            Architecture = Environment.Is64BitProcess ? "x64" : "x86"
        };

        // Try to detect from debugger output
        var outputLower = debuggerOutput.ToLowerInvariant();

        // Detect OS
        if (outputLower.Contains("linux") || outputLower.Contains(".so"))
        {
            platform.Os = "Linux";
        }
        else if (outputLower.Contains("windows") || outputLower.Contains(".dll"))
        {
            platform.Os = "Windows";
        }
        else if (outputLower.Contains("darwin") || outputLower.Contains("macos") || outputLower.Contains(".dylib"))
        {
            platform.Os = "macOS";
        }

        // Detect architecture
        if (outputLower.Contains("arm64") || outputLower.Contains("aarch64"))
        {
            platform.Architecture = "arm64";
        }
        else if (outputLower.Contains("x64") || outputLower.Contains("amd64") || outputLower.Contains("x86_64"))
        {
            platform.Architecture = "x64";
        }
        else if (outputLower.Contains("x86") || outputLower.Contains("i386") || outputLower.Contains("i686"))
        {
            platform.Architecture = "x86";
        }

        // Detect Alpine/musl
        if (outputLower.Contains("musl") || outputLower.Contains("alpine"))
        {
            platform.IsAlpine = true;
            platform.LibcType = "musl";
        }
        else if (platform.Os == "Linux")
        {
            platform.IsAlpine = false;
            platform.LibcType = "glibc";
        }

        return platform;
    }
}

