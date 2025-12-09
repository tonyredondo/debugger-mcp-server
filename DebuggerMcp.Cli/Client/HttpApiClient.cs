using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DebuggerMcp.Cli.Models;

namespace DebuggerMcp.Cli.Client;

/// <summary>
/// HTTP API client for communicating with the Debugger MCP Server.
/// </summary>
/// <remarks>
/// Handles authentication, error handling, retry logic, and request/response serialization.
/// Implements automatic retry with exponential backoff for transient failures.
/// </remarks>
public class HttpApiClient : IHttpApiClient
{
    private HttpClient? _httpClient;
    private string? _serverUrl;
    private string? _apiKey;
    private bool _disposed;

    /// <summary>
    /// Maximum number of retry attempts for transient failures.
    /// </summary>
    public const int MaxRetries = 5;

    /// <summary>
    /// Initial delay between retries (doubles with each retry - exponential backoff).
    /// </summary>
    public static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum delay between retries.
    /// </summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <inheritdoc/>
    public bool IsConfigured => _httpClient != null && !string.IsNullOrEmpty(_serverUrl);

    /// <inheritdoc/>
    public string? ServerUrl => _serverUrl;

    /// <inheritdoc/>
    public void Configure(string serverUrl, string? apiKey = null, TimeSpan? timeout = null)
    {
        // Validate URL
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new ArgumentException("Server URL cannot be empty.", nameof(serverUrl));
        }

        // Add http:// scheme if no scheme is provided (default to http for localhost convenience)
        // Check if there's any scheme by looking for ://
        if (!serverUrl.Contains("://"))
        {
            serverUrl = "http://" + serverUrl;
        }

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new ArgumentException($"Invalid server URL: {serverUrl}. Must be a valid HTTP(S) URL.", nameof(serverUrl));
        }

        // Dispose old client if exists
        _httpClient?.Dispose();

        // Create new client
        _serverUrl = serverUrl.TrimEnd('/');
        _apiKey = apiKey;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_serverUrl + "/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(300)
        };

        // Set default headers
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DebuggerMcp.Cli/1.0");

        // Set API key if provided
        if (!string.IsNullOrEmpty(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
        }
    }

    /// <inheritdoc/>
    public async Task<HealthStatus> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        try
        {
            var response = await _httpClient!.GetAsync("health", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var health = await response.Content.ReadFromJsonAsync<HealthStatus>(JsonOptions, cancellationToken);
                return health ?? new HealthStatus { Status = "Unknown" };
            }

            // Server responded but with error
            return new HealthStatus
            {
                Status = $"Unhealthy ({(int)response.StatusCode} {response.ReasonPhrase})"
            };
        }
        catch (HttpRequestException ex)
        {
            // Network error
            return new HealthStatus
            {
                Status = $"Unreachable: {ex.Message}"
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout
            return new HealthStatus
            {
                Status = "Timeout: Server did not respond in time"
            };
        }
    }

    /// <inheritdoc/>
    public async Task<ServerInfo?> GetServerInfoAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        try
        {
            var response = await _httpClient!.GetAsync("info", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<ServerInfo>(JsonOptions, cancellationToken);
            }

            // Server responded but with error - endpoint may not be available (older server version)
            return null;
        }
        catch (HttpRequestException)
        {
            // Network error or endpoint not available
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        return await ExecuteWithRetryAsync(
            $"GET {path}",
            async ct =>
            {
                var response = await _httpClient!.GetAsync(path, ct);
                await EnsureSuccessAsync(response, ct);
                var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
                return result ?? throw new InvalidOperationException("Server returned null response.");
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        return await ExecuteWithRetryAsync(
            $"POST {path}",
            async ct =>
            {
                var response = await _httpClient!.PostAsJsonAsync(path, request, JsonOptions, ct);
                await EnsureSuccessAsync(response, ct);
                var result = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, ct);
                return result ?? throw new InvalidOperationException("Server returned null response.");
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        await ExecuteWithRetryAsync(
            $"DELETE {path}",
            async ct =>
            {
                var response = await _httpClient!.DeleteAsync(path, ct);
                await EnsureSuccessAsync(response, ct);
                return true; // Return value required by generic method
            },
            cancellationToken);
    }

    /// <summary>
    /// Ensures the client is configured before making requests.
    /// </summary>
    /// <exception cref="InvalidOperationException">If the client is not configured.</exception>
    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "HTTP client is not configured. Call Configure() or use 'connect' command first.");
        }
    }

    /// <summary>
    /// Executes an HTTP operation with retry logic for transient failures.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="operationName">Name of the operation for logging.</param>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    private static async Task<T> ExecuteWithRetryAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        var delay = InitialRetryDelay;

        while (true)
        {
            attempt++;

            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception ex) when (attempt <= MaxRetries && IsRetryableException(ex))
            {
                // Calculate delay with exponential backoff
                var actualDelay = delay > MaxRetryDelay ? MaxRetryDelay : delay;

                // Output retry information to console
                var retryMessage = GetRetryMessage(ex);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[Retry {attempt}/{MaxRetries}] {operationName} failed: {retryMessage}");
                Console.WriteLine($"[Retry {attempt}/{MaxRetries}] Retrying in {actualDelay.TotalSeconds:0.#} seconds...");
                Console.ResetColor();

                // Wait before retrying
                await Task.Delay(actualDelay, cancellationToken);

                // Double the delay for next retry (exponential backoff)
                delay = TimeSpan.FromTicks(delay.Ticks * 2);
            }
            catch (Exception ex) when (attempt > MaxRetries && IsRetryableException(ex))
            {
                // Max retries exceeded
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Error] {operationName} failed after {MaxRetries} retries: {ex.Message}");
                Console.ResetColor();
                throw;
            }
            // Non-retryable exceptions are thrown immediately
        }
    }

    /// <summary>
    /// Determines if an exception is retryable (transient failure).
    /// </summary>
    /// <param name="ex">The exception to check.</param>
    /// <returns>True if the request should be retried.</returns>
    private static bool IsRetryableException(Exception ex)
    {
        // Network errors are retryable
        if (ex is HttpRequestException)
        {
            return true;
        }

        // Timeouts are retryable
        if (ex is TaskCanceledException tce && tce.InnerException is TimeoutException)
        {
            return true;
        }

        // Certain HTTP status codes are retryable
        if (ex is HttpApiException httpEx)
        {
            return httpEx.StatusCode switch
            {
                HttpStatusCode.RequestTimeout => true,           // 408
                HttpStatusCode.TooManyRequests => true,          // 429 (rate limiting)
                HttpStatusCode.InternalServerError => true,      // 500
                HttpStatusCode.BadGateway => true,               // 502
                HttpStatusCode.ServiceUnavailable => true,       // 503
                HttpStatusCode.GatewayTimeout => true,           // 504
                _ => false
            };
        }

        return false;
    }

    /// <summary>
    /// Gets a user-friendly retry message for the exception.
    /// </summary>
    /// <param name="ex">The exception.</param>
    /// <returns>A brief message describing why the request will be retried.</returns>
    private static string GetRetryMessage(Exception ex)
    {
        if (ex is HttpRequestException)
        {
            return "Network error";
        }

        if (ex is TaskCanceledException)
        {
            return "Request timed out";
        }

        if (ex is HttpApiException httpEx)
        {
            return httpEx.StatusCode switch
            {
                HttpStatusCode.RequestTimeout => "Request timeout",
                HttpStatusCode.TooManyRequests => "Rate limit exceeded",
                HttpStatusCode.InternalServerError => "Server error (500)",
                HttpStatusCode.BadGateway => "Bad gateway (502)",
                HttpStatusCode.ServiceUnavailable => "Service unavailable (503)",
                HttpStatusCode.GatewayTimeout => "Gateway timeout (504)",
                _ => ex.Message
            };
        }

        return ex.Message;
    }

    /// <summary>
    /// Ensures the response indicates success, throwing an appropriate exception otherwise.
    /// </summary>
    /// <param name="response">The HTTP response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // Try to get error details from response body
        string errorMessage;
        try
        {
            var errorResponse = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions, cancellationToken);
            errorMessage = errorResponse?.Error ?? response.ReasonPhrase ?? "Unknown error";
        }
        catch
        {
            // If we can't parse the error response, use the status code
            errorMessage = response.ReasonPhrase ?? "Unknown error";
        }

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new HttpApiException(
                "Authentication failed. Please check your API key.",
                response.StatusCode,
                "AUTH_FAILED"),
            HttpStatusCode.Forbidden => new HttpApiException(
                "Access denied. You don't have permission for this operation.",
                response.StatusCode,
                "ACCESS_DENIED"),
            HttpStatusCode.NotFound => new HttpApiException(
                $"Resource not found: {errorMessage}",
                response.StatusCode,
                "NOT_FOUND"),
            HttpStatusCode.BadRequest => new HttpApiException(
                $"Invalid request: {errorMessage}",
                response.StatusCode,
                "BAD_REQUEST"),
            HttpStatusCode.TooManyRequests => new HttpApiException(
                "Rate limit exceeded. Please wait and try again.",
                response.StatusCode,
                "RATE_LIMIT"),
            HttpStatusCode.InternalServerError => new HttpApiException(
                $"Server error: {errorMessage}",
                response.StatusCode,
                "SERVER_ERROR"),
            HttpStatusCode.ServiceUnavailable => new HttpApiException(
                "Server is temporarily unavailable. Please try again later.",
                response.StatusCode,
                "SERVICE_UNAVAILABLE"),
            _ => new HttpApiException(
                $"Request failed ({(int)response.StatusCode}): {errorMessage}",
                response.StatusCode,
                "REQUEST_FAILED")
        };
    }


    /// <inheritdoc/>
    public async Task<DumpUploadResponse> UploadDumpAsync(
        string filePath,
        string userId,
        string? description = null,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Dump file not found: {filePath}");
        }

        var fileName = Path.GetFileName(filePath);

        return await ExecuteWithRetryAsync(
            $"Upload dump '{fileName}'",
            async ct =>
            {
                var fileInfo = new FileInfo(filePath);
                using var content = new MultipartFormDataContent();

                // Create progress-reporting stream (need to re-open for each retry)
                await using var fileStream = File.OpenRead(filePath);
                var progressStream = new ProgressStream(fileStream, fileInfo.Length, progress);

                var streamContent = new StreamContent(progressStream);
                streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                content.Add(streamContent, "file", fileName);
                content.Add(new StringContent(userId), "userId");

                if (!string.IsNullOrEmpty(description))
                {
                    content.Add(new StringContent(description), "description");
                }

                var response = await _httpClient!.PostAsync("api/dumps/upload", content, ct);
                await EnsureSuccessAsync(response, ct);

                var result = await response.Content.ReadFromJsonAsync<DumpUploadResponse>(JsonOptions, ct);
                return result ?? throw new InvalidOperationException("Server returned null response.");
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<DumpInfo>> ListDumpsAsync(string userId, CancellationToken cancellationToken = default)
    {
        return await GetAsync<List<DumpInfo>>($"api/dumps/user/{Uri.EscapeDataString(userId)}", cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<DumpInfo> GetDumpInfoAsync(string userId, string dumpId, CancellationToken cancellationToken = default)
    {
        return await GetAsync<DumpInfo>(
            $"api/dumps/{Uri.EscapeDataString(userId)}/{Uri.EscapeDataString(dumpId)}",
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task DeleteDumpAsync(string userId, string dumpId, CancellationToken cancellationToken = default)
    {
        await DeleteAsync(
            $"api/dumps/{Uri.EscapeDataString(userId)}/{Uri.EscapeDataString(dumpId)}",
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<BinaryUploadResponse?> UploadDumpBinaryAsync(
        string userId,
        string dumpId,
        string binaryPath,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        if (!File.Exists(binaryPath))
        {
            throw new FileNotFoundException($"Binary file not found: {binaryPath}");
        }

        var fileName = Path.GetFileName(binaryPath);

        return await ExecuteWithRetryAsync(
            $"Upload binary '{fileName}'",
            async ct =>
            {
                await using var fileStream = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                using var content = new MultipartFormDataContent
                {
                    { new StreamContent(fileStream), "file", fileName }
                };

                var response = await _httpClient!.PostAsync(
                    $"api/dumps/{Uri.EscapeDataString(userId)}/{Uri.EscapeDataString(dumpId)}/binary", 
                    content, 
                    ct);
                
                await EnsureSuccessAsync(response, ct);

                return await response.Content.ReadFromJsonAsync<BinaryUploadResponse>(JsonOptions, ct);
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<SymbolUploadResponse> UploadSymbolAsync(
        string filePath,
        string dumpId,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Symbol file not found: {filePath}");
        }

        var fileName = Path.GetFileName(filePath);

        return await ExecuteWithRetryAsync(
            $"Upload symbol '{fileName}'",
            async ct =>
            {
                var fileInfo = new FileInfo(filePath);
                using var content = new MultipartFormDataContent();

                // Create progress-reporting stream (need to re-open for each retry)
                await using var fileStream = File.OpenRead(filePath);
                var progressStream = new ProgressStream(fileStream, fileInfo.Length, progress);

                var streamContent = new StreamContent(progressStream);
                streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                content.Add(streamContent, "file", fileName);
                content.Add(new StringContent(dumpId), "dumpId");

                var response = await _httpClient!.PostAsync("api/symbols/upload", content, ct);
                await EnsureSuccessAsync(response, ct);

                var result = await response.Content.ReadFromJsonAsync<SymbolUploadResponse>(JsonOptions, ct);
                return result ?? throw new InvalidOperationException("Server returned null response.");
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<SymbolZipUploadResponse> UploadSymbolZipAsync(
        string zipFilePath,
        string dumpId,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        if (!File.Exists(zipFilePath))
        {
            throw new FileNotFoundException($"ZIP file not found: {zipFilePath}");
        }

        var fileName = Path.GetFileName(zipFilePath);

        return await ExecuteWithRetryAsync(
            $"Upload symbol ZIP '{fileName}'",
            async ct =>
            {
                var fileInfo = new FileInfo(zipFilePath);
                using var content = new MultipartFormDataContent();

                // Create progress-reporting stream (need to re-open for each retry)
                await using var fileStream = File.OpenRead(zipFilePath);
                var progressStream = new ProgressStream(fileStream, fileInfo.Length, progress);

                var streamContent = new StreamContent(progressStream);
                streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

                content.Add(streamContent, "file", fileName);
                content.Add(new StringContent(dumpId), "dumpId");

                var response = await _httpClient!.PostAsync("api/symbols/upload-zip", content, ct);
                await EnsureSuccessAsync(response, ct);

                var result = await response.Content.ReadFromJsonAsync<SymbolZipUploadResponse>(JsonOptions, ct);
                return result ?? throw new InvalidOperationException("Server returned null response.");
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<SymbolListResponse> ListSymbolsAsync(string dumpId, CancellationToken cancellationToken = default)
    {
        return await GetAsync<SymbolListResponse>(
            $"api/symbols/dump/{Uri.EscapeDataString(dumpId)}",
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<SymbolBatchUploadResponse> UploadSymbolsBatchAsync(
        IEnumerable<string> filePatterns,
        string dumpId,
        Action<int, int, string>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        // Expand wildcard patterns to actual files
        var files = ExpandFilePatterns(filePatterns).ToList();

        if (files.Count == 0)
        {
            return new SymbolBatchUploadResponse
            {
                TotalFiles = 0,
                SuccessfulUploads = 0,
                FailedUploads = 0,
                Results = []
            };
        }

        var results = new List<SymbolUploadResult>();
        var successCount = 0;
        var failCount = 0;

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var fileName = Path.GetFileName(file);

            progressCallback?.Invoke(i + 1, files.Count, fileName);

            try
            {
                var response = await UploadSymbolAsync(file, dumpId, null, cancellationToken);
                results.Add(new SymbolUploadResult
                {
                    FileName = response.FileName,
                    Success = true,
                    Message = "Upload successful"
                });
                successCount++;
            }
            catch (Exception ex)
            {
                results.Add(new SymbolUploadResult
                {
                    FileName = fileName,
                    Success = false,
                    Message = ex.Message
                });
                failCount++;
            }
        }

        return new SymbolBatchUploadResponse
        {
            TotalFiles = files.Count,
            SuccessfulUploads = successCount,
            FailedUploads = failCount,
            Results = results
        };
    }

    /// <summary>
    /// Expands file patterns (including wildcards) to actual file paths.
    /// </summary>
    /// <param name="patterns">File paths or glob patterns.</param>
    /// <returns>Actual file paths.</returns>
    private static IEnumerable<string> ExpandFilePatterns(IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            // Check if it's a wildcard pattern
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                // Split into directory and file pattern
                var directory = Path.GetDirectoryName(pattern);
                var filePattern = Path.GetFileName(pattern);

                // Use current directory if no directory specified
                if (string.IsNullOrEmpty(directory))
                {
                    directory = ".";
                }

                // Expand to full path
                directory = Path.GetFullPath(directory);

                if (!Directory.Exists(directory))
                {
                    continue;
                }

                // Handle recursive patterns (**/*)
                var searchOption = pattern.Contains("**")
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly;

                // For ** patterns, adjust the file pattern
                if (filePattern.StartsWith("**"))
                {
                    filePattern = filePattern.Substring(filePattern.LastIndexOf('/') + 1);
                    if (filePattern.StartsWith("**/"))
                    {
                        filePattern = filePattern[3..];
                    }
                }

                foreach (var file in Directory.EnumerateFiles(directory, filePattern, searchOption))
                {
                    // Only include symbol files
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is ".pdb" or ".dbg" or ".so" or ".dylib" or ".dll" or ".exe" or ".sym")
                    {
                        yield return file;
                    }
                }
            }
            else
            {
                // It's a specific file path
                var fullPath = Path.GetFullPath(pattern);
                if (File.Exists(fullPath))
                {
                    yield return fullPath;
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<SessionStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync<SessionStatistics>("api/dumps/stats", cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> DownloadReportAsync(
        string userId,
        string dumpId,
        string format,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        // Determine the content type based on format
        var acceptHeader = format.ToLowerInvariant() switch
        {
            "html" => "text/html",
            "json" => "application/json",
            _ => "text/markdown"
        };

        var extension = format.ToLowerInvariant() switch
        {
            "html" => ".html",
            "json" => ".json",
            _ => ".md"
        };

        // Ensure output path has the correct extension
        if (!outputPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            outputPath = Path.ChangeExtension(outputPath, extension);
        }

        var response = await _httpClient!.GetAsync($"api/dumps/{userId}/{dumpId}/report?format={format}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        // Ensure directory exists
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(outputPath, content, cancellationToken);
        return true;
    }


    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _httpClient?.Dispose();
        _httpClient = null;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Stream wrapper that reports read progress.
/// </summary>
internal class ProgressStream : Stream
{
    private readonly Stream _innerStream;
    private readonly long _totalLength;
    private readonly IProgress<long>? _progress;
    private long _bytesRead;

    public ProgressStream(Stream innerStream, long totalLength, IProgress<long>? progress)
    {
        _innerStream = innerStream;
        _totalLength = totalLength;
        _progress = progress;
        _bytesRead = 0;
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _totalLength;
    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    public override void Flush() => _innerStream.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _innerStream.Read(buffer, offset, count);
        _bytesRead += bytesRead;
        _progress?.Report(_bytesRead);
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var bytesRead = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
        _bytesRead += bytesRead;
        _progress?.Report(_bytesRead);
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var bytesRead = await _innerStream.ReadAsync(buffer, cancellationToken);
        _bytesRead += bytesRead;
        _progress?.Report(_bytesRead);
        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // Don't dispose inner stream - caller owns it
        base.Dispose(disposing);
    }
}

/// <summary>
/// Exception thrown when an HTTP API request fails.
/// </summary>
public class HttpApiException : Exception
{
    /// <summary>
    /// Gets the HTTP status code.
    /// </summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// Gets the error code.
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpApiException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="errorCode">The error code.</param>
    public HttpApiException(string message, HttpStatusCode statusCode, string errorCode)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}

