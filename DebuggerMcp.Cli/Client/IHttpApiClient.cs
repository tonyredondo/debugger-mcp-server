using DebuggerMcp.Cli.Models;

namespace DebuggerMcp.Cli.Client;

/// <summary>
/// Interface for HTTP API client operations.
/// </summary>
/// <remarks>
/// This client handles all HTTP REST API operations with the Debugger MCP Server,
/// including file uploads, health checks, and dump/symbol management.
/// </remarks>
public interface IHttpApiClient : IDisposable
{
    /// <summary>
    /// Gets whether the client is configured with a server URL.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Gets the current server URL.
    /// </summary>
    string? ServerUrl { get; }

    /// <summary>
    /// Configures the client with connection settings.
    /// </summary>
    /// <param name="serverUrl">The server URL.</param>
    /// <param name="apiKey">Optional API key for authentication.</param>
    /// <param name="timeout">Optional request timeout.</param>
    void Configure(string serverUrl, string? apiKey = null, TimeSpan? timeout = null);

    /// <summary>
    /// Checks if the server is healthy and reachable.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The health status of the server.</returns>
    Task<HealthStatus> CheckHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets server host information.
    /// </summary>
    /// <remarks>
    /// This information is crucial for determining which dumps can be analyzed:
    /// - Alpine Linux dumps can only be debugged on Alpine hosts
    /// - Architecture (x64/arm64) affects dump compatibility
    /// - Installed runtimes determine supported dump versions
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The server host information.</returns>
    Task<ServerInfo?> GetServerInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a GET request to the server.
    /// </summary>
    /// <typeparam name="T">The response type.</typeparam>
    /// <param name="path">The API path (relative to base URL).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deserialized response.</returns>
    Task<T> GetAsync<T>(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a POST request to the server.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="path">The API path (relative to base URL).</param>
    /// <param name="request">The request body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deserialized response.</returns>
    Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a DELETE request to the server.
    /// </summary>
    /// <param name="path">The API path (relative to base URL).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);


    /// <summary>
    /// Uploads a dump file with progress reporting.
    /// </summary>
    /// <param name="filePath">Path to the dump file.</param>
    /// <param name="userId">User ID for the upload.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="progress">Progress reporter (reports bytes sent).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The upload response.</returns>
    Task<DumpUploadResponse> UploadDumpAsync(
        string filePath,
        string userId,
        string? description = null,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all dumps for a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of dump information.</returns>
    Task<List<DumpInfo>> ListDumpsAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets information about a specific dump.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="dumpId">The dump ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dump information.</returns>
    Task<DumpInfo> GetDumpInfoAsync(string userId, string dumpId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a dump file.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="dumpId">The dump ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteDumpAsync(string userId, string dumpId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads an executable binary for a standalone .NET app dump.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="dumpId">The dump ID.</param>
    /// <param name="binaryPath">Path to the executable binary.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The upload response, or null if failed.</returns>
    Task<BinaryUploadResponse?> UploadDumpBinaryAsync(
        string userId, 
        string dumpId, 
        string binaryPath, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a symbol file with progress reporting.
    /// </summary>
    /// <param name="filePath">Path to the symbol file.</param>
    /// <param name="dumpId">The dump ID to associate with.</param>
    /// <param name="progress">Progress reporter (reports bytes sent).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The upload response.</returns>
    Task<SymbolUploadResponse> UploadSymbolAsync(
        string filePath,
        string dumpId,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a ZIP file containing symbol files for a dump.
    /// The server extracts the ZIP preserving directory structure.
    /// </summary>
    /// <param name="zipFilePath">Path to the ZIP file.</param>
    /// <param name="dumpId">The dump ID to associate with.</param>
    /// <param name="progress">Progress reporter (reports bytes sent).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ZIP upload response.</returns>
    Task<SymbolZipUploadResponse> UploadSymbolZipAsync(
        string zipFilePath,
        string dumpId,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists symbols for a dump.
    /// </summary>
    /// <param name="dumpId">The dump ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Symbol list response.</returns>
    Task<SymbolListResponse> ListSymbolsAsync(string dumpId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads multiple symbol files with progress reporting.
    /// Supports wildcard patterns (e.g., *.pdb, **/*.pdb).
    /// </summary>
    /// <param name="filePatterns">File paths or glob patterns (e.g., "./bin/*.pdb").</param>
    /// <param name="dumpId">The dump ID to associate with.</param>
    /// <param name="progressCallback">Callback for overall progress (file index, total files, current file name).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of upload responses.</returns>
    Task<SymbolBatchUploadResponse> UploadSymbolsBatchAsync(
        IEnumerable<string> filePatterns,
        string dumpId,
        Action<int, int, string>? progressCallback = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets session statistics from the server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Session statistics.</returns>
    Task<SessionStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a report for a dump.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="dumpId">Dump ID.</param>
    /// <param name="format">Report format (markdown, html, json).</param>
    /// <param name="outputPath">Path to save the report.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if download successful.</returns>
    Task<bool> DownloadReportAsync(
        string userId,
        string dumpId,
        string format,
        string outputPath,
        CancellationToken cancellationToken = default);

}

