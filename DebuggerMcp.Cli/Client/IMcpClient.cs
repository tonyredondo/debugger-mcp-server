namespace DebuggerMcp.Cli.Client;

/// <summary>
/// Interface for MCP (Model Context Protocol) client operations.
/// </summary>
/// <remarks>
/// This client connects to the Debugger MCP Server via HTTP/SSE transport
/// and provides methods to call MCP tools for debugging operations.
/// </remarks>
public interface IMcpClient : IAsyncDisposable
{
    /// <summary>
    /// Gets whether the client is connected to the MCP server.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets the list of available tools on the server.
    /// </summary>
    IReadOnlyList<string> AvailableTools { get; }

    /// <summary>
    /// Connects to the MCP server via SSE transport.
    /// </summary>
    /// <param name="serverUrl">The server URL.</param>
    /// <param name="apiKey">Optional API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ConnectAsync(string serverUrl, string? apiKey = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from the MCP server.
    /// </summary>
    Task DisconnectAsync();

    #region Session Tools

    /// <summary>
    /// Creates a new debugging session.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session ID.</returns>
    Task<string> CreateSessionAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all sessions for a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Session list information.</returns>
    Task<string> ListSessionsAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes a debugging session.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Confirmation message.</returns>
    Task<string> CloseSessionAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets information about the debugger in a session.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Debugger information.</returns>
    Task<string> GetDebuggerInfoAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores/attaches to an existing persisted session.
    /// </summary>
    /// <param name="sessionId">The session ID to restore.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Session information including dump status.</returns>
    Task<string> RestoreSessionAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    #endregion

    #region Dump Tools

    /// <summary>
    /// Opens a dump file in the debugger.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="dumpId">The dump ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Confirmation message with dump info.</returns>
    Task<string> OpenDumpAsync(string sessionId, string userId, string dumpId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the currently open dump.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Confirmation message.</returns>
    Task<string> CloseDumpAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a debugger command.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="command">The command to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Command output.</returns>
    Task<string> ExecuteCommandAsync(string sessionId, string userId, string command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the SOS extension for .NET debugging.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Confirmation message.</returns>
    Task<string> LoadSosAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    #endregion

    #region Analysis Tools

    /// <summary>
    /// Runs crash analysis on the current dump.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Crash analysis results.</returns>
    Task<string> AnalyzeCrashAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs .NET-specific analysis on the current dump.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>.NET analysis results.</returns>
    Task<string> AnalyzeDotNetAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    #endregion

    #region Performance Analysis Tools

    /// <summary>
    /// Runs comprehensive performance analysis.
    /// </summary>
    Task<string> AnalyzePerformanceAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes CPU usage patterns.
    /// </summary>
    Task<string> AnalyzeCpuUsageAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes memory allocations.
    /// </summary>
    Task<string> AnalyzeAllocationsAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes garbage collection behavior.
    /// </summary>
    Task<string> AnalyzeGcAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes thread contention.
    /// </summary>
    Task<string> AnalyzeContentionAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    #endregion

    #region Security Analysis Tools

    /// <summary>
    /// Runs security vulnerability analysis.
    /// </summary>
    Task<string> AnalyzeSecurityAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    #endregion

    #region Comparison Tools

    /// <summary>
    /// Compares two dumps comprehensively.
    /// </summary>
    Task<string> CompareDumpsAsync(
        string baselineSessionId,
        string baselineUserId,
        string targetSessionId,
        string targetUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares heap state between two dumps.
    /// </summary>
    Task<string> CompareHeapsAsync(
        string baselineSessionId,
        string baselineUserId,
        string targetSessionId,
        string targetUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares threads between two dumps.
    /// </summary>
    Task<string> CompareThreadsAsync(
        string baselineSessionId,
        string baselineUserId,
        string targetSessionId,
        string targetUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares loaded modules between two dumps.
    /// </summary>
    Task<string> CompareModulesAsync(
        string baselineSessionId,
        string baselineUserId,
        string targetSessionId,
        string targetUserId,
        CancellationToken cancellationToken = default);

    #endregion

    #region Watch Tools

    /// <summary>
    /// Adds a watch expression.
    /// </summary>
    Task<string> AddWatchAsync(string sessionId, string userId, string expression, string? name = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all watches for a session.
    /// </summary>
    Task<string> ListWatchesAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates all watches.
    /// </summary>
    Task<string> EvaluateWatchesAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates a specific watch.
    /// </summary>
    Task<string> EvaluateWatchAsync(string sessionId, string userId, string watchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a watch.
    /// </summary>
    Task<string> RemoveWatchAsync(string sessionId, string userId, string watchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all watches for a session.
    /// </summary>
    Task<string> ClearWatchesAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    #endregion

    #region Report Tools

    /// <summary>
    /// Generates a comprehensive report.
    /// </summary>
    Task<string> GenerateReportAsync(string sessionId, string userId, string format = "markdown", bool includeWatches = true, bool includeComparison = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a summary report.
    /// </summary>
    Task<string> GenerateSummaryReportAsync(string sessionId, string userId, string format = "markdown", CancellationToken cancellationToken = default);

    #endregion

    #region Source Link Tools

    /// <summary>
    /// Resolves a source file path to a Source Link URL.
    /// </summary>
    Task<string> ResolveSourceLinkAsync(string sessionId, string userId, string sourceFile, int? lineNumber = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets Source Link configuration information.
    /// </summary>
    Task<string> GetSourceLinkInfoAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    #endregion

    #region Symbol Tools

    /// <summary>
    /// Configures additional symbol paths for the session.
    /// </summary>
    Task<string> ConfigureAdditionalSymbolsAsync(string sessionId, string userId, string symbolPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the list of common symbol servers.
    /// </summary>
    Task<string> GetSymbolServersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the downloaded symbol cache for a dump.
    /// </summary>
    /// <param name="userId">The user ID that owns the dump.</param>
    /// <param name="dumpId">The dump ID whose symbol cache should be cleared.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result message indicating what was cleared.</returns>
    /// <remarks>
    /// Use this after a timed-out symbol download to force re-download on next open.
    /// </remarks>
    Task<string> ClearSymbolCacheAsync(string userId, string dumpId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reloads symbols into a running debugger session.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result message with loaded symbols count.</returns>
    /// <remarks>
    /// Use this after uploading new symbol files (especially ZIP archives) to a dump that is already open.
    /// </remarks>
    Task<string> ReloadSymbolsAsync(string sessionId, string userId, CancellationToken cancellationToken = default);

    #endregion

    #region Datadog Symbols

    /// <summary>
    /// Downloads Datadog.Trace symbols from Azure Pipelines or GitHub for a specific commit.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="commitSha">The commit SHA from the Datadog.Trace assembly.</param>
    /// <param name="targetFramework">Optional target framework (auto-detected if not specified).</param>
    /// <param name="loadIntoDebugger">Whether to load symbols into the debugger after download.</param>
    /// <param name="forceVersion">If true, falls back to version/tag lookup when SHA lookup fails.</param>
    /// <param name="version">Optional version for fallback lookup (e.g., "3.31.0").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON result with download status and loaded symbols.</returns>
    Task<string> DownloadDatadogSymbolsAsync(
        string sessionId,
        string userId,
        string commitSha,
        string? targetFramework = null,
        bool loadIntoDebugger = true,
        bool forceVersion = false,
        string? version = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists available Datadog.Trace artifacts from Azure Pipelines for a commit.
    /// </summary>
    /// <param name="commitSha">The commit SHA to find the build for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON result with build info and available artifacts.</returns>
    Task<string> ListDatadogArtifactsAsync(string commitSha, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the Datadog symbol download configuration and status.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON with configuration information.</returns>
    Task<string> GetDatadogSymbolsConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Auto-detect and download Datadog.Trace symbols from the opened dump.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="loadIntoDebugger">Whether to load symbols into the debugger after download.</param>
    /// <param name="forceVersion">If true, falls back to version/tag lookup when SHA lookup fails.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON result with download status and loaded symbols.</returns>
    /// <remarks>
    /// Scans the dump for Datadog assemblies, extracts commit SHA from InformationalVersion,
    /// and downloads appropriate symbols from Azure Pipelines or GitHub automatically.
    /// By default, only exact SHA matches are used. Use forceVersion=true to enable version-based fallback.
    /// </remarks>
    Task<string> PrepareDatadogSymbolsAsync(
        string sessionId,
        string userId,
        bool loadIntoDebugger = true,
        bool forceVersion = false,
        CancellationToken cancellationToken = default);

    #endregion

    #region Generic Tool Invocation

    /// <summary>
    /// Calls an MCP tool by name with the specified arguments.
    /// </summary>
    /// <param name="toolName">The name of the tool to call.</param>
    /// <param name="arguments">The arguments as key-value pairs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tool's response.</returns>
    Task<string> CallToolAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken cancellationToken = default);

    #endregion
}

