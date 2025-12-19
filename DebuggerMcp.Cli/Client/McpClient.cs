using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DebuggerMcp.Cli.Serialization;

namespace DebuggerMcp.Cli.Client;

/// <summary>
/// MCP client for connecting to the Debugger MCP Server via HTTP/SSE transport.
/// </summary>
/// <remarks>
/// This client connects to the MCP server's SSE endpoint and uses bidirectional
/// communication: requests via HTTP POST, responses via SSE stream.
/// The server must be started with --mcp-http flag.
/// </remarks>
public class McpClient : IMcpClient
{
    private HttpClient? _httpClient;
    private HttpClient? _sseClient;
    private string? _apiKey;
    private string? _serverUrl;
    private string? _messageEndpoint;
    private readonly List<string> _availableTools = [];
    private bool _disposed;
    private bool _protocolInitialized;
    private int _requestId;

    // SSE response handling
    private Task? _sseListenerTask;
    private CancellationTokenSource? _sseListenerCts;
    private HttpResponseMessage? _sseResponse;
    private StreamReader? _sseReader;
    private readonly SemaphoreSlim _sseReconnectLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, Func<JsonElement?, CancellationToken, Task<object?>>> _serverRequestHandlers =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = CliJsonSerializationDefaults.CaseInsensitiveCamelCaseIgnoreNull;

    /// <summary>
    /// Gets or sets the maximum time to wait for a tool response to arrive over SSE.
    /// </summary>
    /// <remarks>
    /// Some operations (like opening large dumps and downloading symbols) can take a long time server-side.
    /// This timeout only governs how long the client waits for the asynchronous SSE response after the
    /// request is accepted.
    /// </remarks>
    public TimeSpan ToolResponseTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <inheritdoc/>
    public bool IsConnected => _httpClient != null && !string.IsNullOrEmpty(_messageEndpoint);

    /// <inheritdoc/>
    public IReadOnlyList<string> AvailableTools => _availableTools.AsReadOnly();

    /// <summary>
    /// Registers a JSON-RPC request handler for server-initiated requests delivered over SSE.
    /// </summary>
    public void RegisterServerRequestHandler(
        string method,
        Func<JsonElement?, CancellationToken, Task<object?>> handler)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("Method cannot be empty.", nameof(method));
        }
        _serverRequestHandlers[method] = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>
    /// Unregisters a previously registered server request handler.
    /// </summary>
    public bool UnregisterServerRequestHandler(string method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return false;
        }
        return _serverRequestHandlers.TryRemove(method, out _);
    }

    /// <inheritdoc/>
    public async Task ConnectAsync(string serverUrl, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        if (_httpClient != null)
        {
            await DisconnectAsync();
        }

        _protocolInitialized = false;

        // Normalize URL
        _serverUrl = serverUrl.TrimEnd('/');
        _apiKey = apiKey;

        // Create HTTP client with long timeout for debugging operations
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DebuggerMcp.Cli/1.0");

        if (!string.IsNullOrEmpty(apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        }

        // Try to connect to MCP SSE endpoint and get message endpoint
        try
        {
            await InitializeMcpConnectionAsync(apiKey, cancellationToken);
            try
            {
                await InitializeProtocolAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Non-fatal: tools can still work without initialize, but sampling capability advertisement won't.
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Warning: MCP initialize failed: {ex.Message}");
                Console.ResetColor();
            }
            await DiscoverToolsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // MCP connection failed, but we might still be able to use HTTP API
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Warning: MCP initialization failed: {ex.Message}");
            Console.WriteLine("Make sure the server is started with --mcp-http flag.");
            Console.WriteLine("Debugging commands will not be available via MCP.");
            Console.ResetColor();
            _messageEndpoint = null;
        }
    }

    /// <summary>
    /// Parses an SSE stream until an MCP message endpoint is discovered.
    /// </summary>
    /// <param name="reader">The SSE stream reader.</param>
    /// <param name="serverUrl">Normalized server base URL.</param>
    /// <returns>The absolute message endpoint to post JSON-RPC requests to, or null if none was found.</returns>
    internal static async Task<string?> TryReadMessageEndpointAsync(StreamReader reader, string serverUrl, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            // SSE event format: "event: endpoint\ndata: /mcp/message?sessionId=xxx\n\n"
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                // Some servers emit an explicit event type marker.
                continue;
            }

            var data = line[6..];

            // Skip "endpoint" event type markers
            if (string.Equals(data, "endpoint", StringComparison.Ordinal))
            {
                continue;
            }

            if (data.StartsWith("/") || data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return data.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? data
                    : $"{serverUrl}{data}";
            }

            // Try parsing as JSON in case the endpoint is sent as JSON
            if (data.StartsWith("{"))
            {
                try
                {
                    using var json = JsonDocument.Parse(data);
                    if (json.RootElement.TryGetProperty("endpoint", out var endpointProp))
                    {
                        var endpoint = endpointProp.GetString();
                        if (!string.IsNullOrEmpty(endpoint))
                        {
                            return endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                ? endpoint
                                : $"{serverUrl}{endpoint}";
                        }
                    }
                }
                catch
                {
                    // Not JSON; ignore.
                }
            }
        }
    }

    /// <summary>
    /// Processes a full SSE event payload (including possible multi-line data fields).
    /// </summary>
    internal static bool TryProcessSseEventPayload(string? payloadJson, Action<int, string> onResponse)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || !payloadJson.StartsWith("{"))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("id", out var idProp))
            {
                return false;
            }

            var id = idProp.ValueKind == JsonValueKind.Number
                ? idProp.GetInt32()
                : int.Parse(idProp.GetString() ?? "0");

            onResponse(id, payloadJson);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Initializes the MCP connection by connecting to SSE endpoint.
    /// </summary>
    private async Task InitializeMcpConnectionAsync(string? apiKey, CancellationToken cancellationToken)
    {
        // The MCP HTTP transport uses SSE for bidirectional communication
        // First, make a GET request to /mcp/sse to establish the event stream
        // The server will respond with an endpoint URL for sending messages

        var sseUrl = $"{_serverUrl}/mcp/sse";

        try
        {
            await StopSseListenerAsync().ConfigureAwait(false);

            // Dispose any existing SSE client
            _sseClient?.Dispose();
            
            // Create a separate client for SSE that stays open
            _sseClient = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan // SSE connection stays open
            };
            _sseClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            _sseClient.DefaultRequestHeaders.UserAgent.ParseAdd("DebuggerMcp.Cli/1.0");

            if (!string.IsNullOrEmpty(apiKey))
            {
                _sseClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);
            }

            // Request the SSE endpoint
            var request = new HttpRequestMessage(HttpMethod.Get, sseUrl);

            _sseResponse = await _sseClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (_sseResponse == null || !_sseResponse.IsSuccessStatusCode)
            {
                throw new McpClientException($"SSE endpoint returned {_sseResponse?.StatusCode}. Is the server started with --mcp-http?");
            }

            // Read the first event which should contain the endpoint info
            var stream = await _sseResponse.Content.ReadAsStreamAsync(cancellationToken);
            _sseReader = new StreamReader(stream);

            _messageEndpoint = await TryReadMessageEndpointAsync(_sseReader, _serverUrl!, cancellationToken);

            // If we couldn't get the endpoint from SSE, try the default
            if (string.IsNullOrEmpty(_messageEndpoint))
            {
                _messageEndpoint = $"{_serverUrl}/mcp/message";
            }

            // Start background SSE listener for responses
            _sseListenerCts = new CancellationTokenSource();
            _sseListenerTask = Task.Run(() => ListenForSseResponsesAsync(_sseReader, _sseListenerCts.Token));

            // Give the listener a moment to start
            await Task.Delay(100, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new McpClientException("Timeout connecting to MCP SSE endpoint");
        }
        catch (HttpRequestException ex)
        {
            throw new McpClientException($"Failed to connect to MCP endpoint: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Background task that listens for SSE responses.
    /// </summary>
    private async Task ListenForSseResponsesAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            StringBuilder? currentData = null;

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break; // Stream closed
                }

                if (string.IsNullOrEmpty(line))
                {
                    // End of event - process accumulated data
                    if (currentData != null)
                    {
                        ProcessSseData(currentData.ToString());
                        currentData = null;
                    }
                    continue;
                }

                if (line.StartsWith("data: "))
                {
                    var data = line[6..];
                    currentData ??= new StringBuilder();
                    if (currentData.Length > 0)
                    {
                        currentData.Append('\n');
                    }
                    currentData.Append(data);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when disconnecting
        }
        catch (Exception ex)
        {
            FailAllPendingRequests(new McpClientException($"SSE listener error: {ex.Message}", ex));
            return;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                FailAllPendingRequests(new McpClientException("SSE stream ended unexpectedly."));
            }
        }
    }

    /// <summary>
    /// Processes an SSE data event (JSON-RPC response).
    /// </summary>
    private void ProcessSseData(string data)
    {
        // Handle server-initiated JSON-RPC requests (e.g., sampling/createMessage).
        if (LooksLikeJsonRpcRequest(data))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await TryHandleServerRequestAsync(data, _sseListenerCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Avoid crashing the SSE listener for a best-effort request handler.
                }
            });
            return;
        }

        if (!TryProcessSseEventPayload(data, (id, payload) =>
            {
                if (_pendingRequests.TryRemove(id, out var tcs))
                {
                    tcs.SetResult(payload);
                }
            }))
        {
            return;
        }
    }

    private static bool LooksLikeJsonRpcRequest(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || !payloadJson.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            return root.TryGetProperty("method", out var methodProp) &&
                   methodProp.ValueKind == JsonValueKind.String &&
                   root.TryGetProperty("id", out _);
        }
        catch
        {
            return false;
        }
    }

    internal async Task<bool> TryHandleServerRequestAsync(string payloadJson, CancellationToken cancellationToken)
    {
        if (_httpClient == null || string.IsNullOrWhiteSpace(_messageEndpoint))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(payloadJson) || !payloadJson.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return false;
        }

        JsonElement idElement;
        string method;
        JsonElement? paramsElement;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("method", out var methodProp) || methodProp.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (!root.TryGetProperty("id", out var idProp))
            {
                return false;
            }

            method = methodProp.GetString() ?? string.Empty;
            idElement = idProp.Clone();

            paramsElement = root.TryGetProperty("params", out var p) ? p.Clone() : null;
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            return false;
        }

        if (!_serverRequestHandlers.TryGetValue(method, out var handler))
        {
            await SendJsonRpcErrorAsync(
                idElement,
                code: -32601,
                message: $"Method not found: {method}",
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        try
        {
            var result = await handler(paramsElement, cancellationToken).ConfigureAwait(false);
            await SendJsonRpcResultAsync(idElement, result, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            await SendJsonRpcErrorAsync(
                idElement,
                code: -32000,
                message: "Request canceled.",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            await SendJsonRpcErrorAsync(
                idElement,
                code: -32000,
                message: ex.Message,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    private async Task SendJsonRpcResultAsync(JsonElement idElement, object? result, CancellationToken cancellationToken)
    {
        var json = BuildJsonRpcResponseJson(idElement, isError: false, result, error: null);
        await PostJsonRpcAsync(json, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendJsonRpcErrorAsync(JsonElement idElement, int code, string message, CancellationToken cancellationToken)
    {
        var errorObj = new { code, message };
        var json = BuildJsonRpcResponseJson(idElement, isError: true, result: null, error: errorObj);
        await PostJsonRpcAsync(json, cancellationToken).ConfigureAwait(false);
    }

    private string BuildJsonRpcResponseJson(JsonElement idElement, bool isError, object? result, object? error)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            idElement.WriteTo(writer);

            if (isError)
            {
                writer.WritePropertyName("error");
                JsonSerializer.Serialize(writer, error, JsonOptions);
            }
            else
            {
                writer.WritePropertyName("result");
                if (result is JsonElement elem)
                {
                    elem.WriteTo(writer);
                }
                else
                {
                    JsonSerializer.Serialize(writer, result, JsonOptions);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task PostJsonRpcAsync(string json, CancellationToken cancellationToken)
    {
        EnsureConnected();
        if (_httpClient == null || string.IsNullOrWhiteSpace(_messageEndpoint))
        {
            throw new McpClientException("MCP message endpoint is not available.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _messageEndpoint);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new McpClientException($"Failed to send JSON-RPC response ({(int)response.StatusCode}): {body}");
        }
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync()
    {
        await StopSseListenerAsync().ConfigureAwait(false);

        _protocolInitialized = false;

        // Complete any pending requests with cancellation
        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.TrySetCanceled();
        }
        _pendingRequests.Clear();

        // Dispose SSE client (separate from main HTTP client)
        _sseClient?.Dispose();
        _sseClient = null;
        
        _httpClient?.Dispose();
        _httpClient = null;
        _serverUrl = null;
        _apiKey = null;
        _messageEndpoint = null;
        _availableTools.Clear();
    }

    /// <summary>
    /// Discovers available tools by calling the MCP tools/list method.
    /// </summary>
    private async Task DiscoverToolsAsync(CancellationToken cancellationToken)
    {
        if (_httpClient == null || string.IsNullOrEmpty(_messageEndpoint))
        {
            return;
        }

        try
        {
            var request = new McpRequest
            {
                JsonRpc = "2.0",
                Id = Interlocked.Increment(ref _requestId),
                Method = "tools/list",
                Params = new { }
            };

            var response = await SendMcpRequestAsync<ToolsListResult>(request, cancellationToken);

            _availableTools.Clear();
            if (response?.Tools != null)
            {
                _availableTools.AddRange(response.Tools.Select(t => t.Name));
            }
        }
        catch (Exception)
        {
            // Tools discovery failed (non-fatal).
            _availableTools.Clear();
        }
    }

    private async Task EnsureSseConnectedAsync(CancellationToken cancellationToken)
    {
        if (_httpClient == null || string.IsNullOrEmpty(_serverUrl) || string.IsNullOrEmpty(_messageEndpoint))
        {
            return;
        }

        if (_sseListenerTask != null && !_sseListenerTask.IsCompleted)
        {
            return;
        }

        await _sseReconnectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sseListenerTask != null && !_sseListenerTask.IsCompleted)
            {
                return;
            }

            await InitializeMcpConnectionAsync(_apiKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sseReconnectLock.Release();
        }
    }

    private void FailAllPendingRequests(McpClientException exception)
    {
        foreach (var kvp in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetException(exception);
            }
        }
    }

    private async Task StopSseListenerAsync()
    {
        // Cancel SSE listener
        if (_sseListenerCts != null)
        {
            await _sseListenerCts.CancelAsync().ConfigureAwait(false);
            _sseListenerCts.Dispose();
            _sseListenerCts = null;
        }

        // Wait for SSE listener task to complete (with timeout)
        if (_sseListenerTask != null)
        {
            try
            {
                await _sseListenerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
                // Ignore shutdown failures; we'll dispose the response/stream below.
            }
            _sseListenerTask = null;
        }

        _sseReader?.Dispose();
        _sseReader = null;

        _sseResponse?.Dispose();
        _sseResponse = null;
    }

    /// <summary>
    /// Sends an MCP request and waits for the response via SSE.
    /// </summary>
    private async Task<T?> SendMcpRequestAsync<T>(McpRequest request, CancellationToken cancellationToken)
        where T : class
    {
        EnsureConnected();
        await EnsureSseConnectedAsync(cancellationToken).ConfigureAwait(false);

        // Create a completion source for this request
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[request.Id] = tcs;

        try
        {
            // Send the request via POST
            var jsonContent = JsonSerializer.Serialize(request, JsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var httpResponse = await _httpClient!.PostAsync(_messageEndpoint, content, cancellationToken);

            // For SSE transport, we expect 202 Accepted
            if (httpResponse.StatusCode != System.Net.HttpStatusCode.Accepted &&
                httpResponse.StatusCode != System.Net.HttpStatusCode.OK)
            {
                var errorBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                throw new McpClientException($"MCP request failed ({httpResponse.StatusCode}): {errorBody}");
            }

            // Wait for response via SSE with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(NormalizeToolResponseTimeout(ToolResponseTimeout));

            var responseJson = await tcs.Task.WaitAsync(timeoutCts.Token);

            // Parse the response
            var mcpResponse = JsonSerializer.Deserialize<McpResponse<T>>(responseJson, JsonOptions);

            if (mcpResponse?.Error != null)
            {
                throw new McpClientException($"MCP error ({mcpResponse.Error.Code}): {mcpResponse.Error.Message}");
            }

            return mcpResponse?.Result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpClientException("Request timed out waiting for response");
        }
        finally
        {
            _pendingRequests.TryRemove(request.Id, out _);
        }
    }

    internal static TimeSpan NormalizeToolResponseTimeout(TimeSpan configured)
    {
        if (configured == Timeout.InfiniteTimeSpan)
        {
            return Timeout.InfiniteTimeSpan;
        }

        if (configured <= TimeSpan.Zero)
        {
            return TimeSpan.FromMinutes(10);
        }

        return configured;
    }

    /// <summary>
    /// Ensures the client is connected.
    /// </summary>
    private void EnsureConnected()
    {
        if (_httpClient == null || string.IsNullOrEmpty(_messageEndpoint))
        {
            throw new InvalidOperationException(
                "MCP client is not connected. Ensure the server is started with --mcp-http flag.");
        }
    }

    private async Task InitializeProtocolAsync(CancellationToken cancellationToken)
    {
        if (_protocolInitialized)
        {
            return;
        }

        EnsureConnected();

        var request = new McpRequest
        {
            JsonRpc = "2.0",
            Id = Interlocked.Increment(ref _requestId),
            Method = "initialize",
            Params = BuildInitializeParams()
        };

        _ = await SendMcpRequestAsync<Dictionary<string, object?>>(request, cancellationToken).ConfigureAwait(false);
        _protocolInitialized = true;

        // Best-effort initialized notification.
        try
        {
            await SendMcpNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Ignore: some servers/transports may not require it.
        }
    }

    internal static Dictionary<string, object?> BuildInitializeParams()
    {
        var version =
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";

        return new Dictionary<string, object?>
        {
            ["protocolVersion"] = "2024-11-05",
            ["clientInfo"] = new Dictionary<string, object?>
            {
                ["name"] = "dbg-mcp",
                ["version"] = version
            },
            ["capabilities"] = new Dictionary<string, object?>
            {
                // Advertise MCP sampling with tools so the server can drive an investigation loop.
                ["sampling"] = new Dictionary<string, object?>
                {
                    ["tools"] = new Dictionary<string, object?>()
                }
            }
        };
    }

    private async Task SendMcpNotificationAsync(string method, object? @params, CancellationToken cancellationToken)
    {
        EnsureConnected();

        var payload = new
        {
            jsonrpc = "2.0",
            method,
            @params
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await PostJsonRpcAsync(json, cancellationToken).ConfigureAwait(false);
    }


    /// <inheritdoc/>
    public async Task<string> CreateSessionAsync(string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("session", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ListSessionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("session", new Dictionary<string, object?>
        {
            ["action"] = "list",
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> CloseSessionAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("session", new Dictionary<string, object?>
        {
            ["action"] = "close",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> GetDebuggerInfoAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("session", new Dictionary<string, object?>
        {
            ["action"] = "debugger_info",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> RestoreSessionAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("session", new Dictionary<string, object?>
        {
            ["action"] = "restore",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> LoadVerifyCoreModulesAsync(string sessionId, string userId, string? moduleNames = null, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["action"] = "verify_core_modules",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        };
        if (!string.IsNullOrEmpty(moduleNames))
        {
            parameters["moduleNames"] = moduleNames;
        }
        return await CallToolAsync("symbols", parameters, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> OpenDumpAsync(string sessionId, string userId, string dumpId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("dump", new Dictionary<string, object?>
        {
            ["action"] = "open",
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["dumpId"] = dumpId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> CloseDumpAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("dump", new Dictionary<string, object?>
        {
            ["action"] = "close",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ExecuteCommandAsync(string sessionId, string userId, string command, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("exec", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["command"] = command
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> AnalyzeCrashAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["kind"] = "crash",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> AnalyzeAiAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["kind"] = "ai",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }



    /// <summary>
    /// Inspects a .NET object at the given address and returns its structure as JSON.
    /// </summary>
    public async Task<string> InspectObjectAsync(
        string sessionId,
        string userId,
        string address,
        string? methodTable = null,
        int maxDepth = 5,
        int maxArrayElements = 10,
        int maxStringLength = 1024,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["kind"] = "object",
            ["address"] = address,
            ["maxDepth"] = maxDepth,
            ["maxArrayElements"] = maxArrayElements,
            ["maxStringLength"] = maxStringLength
        };

        if (!string.IsNullOrEmpty(methodTable))
        {
            args["methodTable"] = methodTable;
        }

        return await CallToolAsync("inspect", args, cancellationToken);
    }

    // NOTE: DumpObjectAsync removed - merged into InspectObjectAsync

    /// <summary>
    /// Dumps a .NET module using ClrMD (safe, won't crash LLDB).
    /// </summary>
    public async Task<string> DumpModuleAsync(
        string sessionId,
        string userId,
        string address,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["kind"] = "module",
            ["address"] = address
        };

        return await CallToolAsync("inspect", args, cancellationToken);
    }

    /// <summary>
    /// Lists all .NET modules using ClrMD.
    /// </summary>
    public async Task<string> ListModulesAsync(
        string sessionId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["kind"] = "modules"
        };

        return await CallToolAsync("inspect", args, cancellationToken);
    }

    /// <summary>
    /// Searches for a type by name across modules using ClrMD (equivalent to SOS !name2ee).
    /// </summary>
    public async Task<string> Name2EEAsync(
        string sessionId,
        string userId,
        string typeName,
        string? moduleName = "*",
        bool includeAllModules = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["kind"] = "lookup_type",
            ["typeName"] = typeName,
            ["moduleName"] = moduleName,
            ["includeAllModules"] = includeAllModules
        };

        return await CallToolAsync("inspect", args, cancellationToken);
    }

    /// <summary>
    /// Searches for a method by name within a type using ClrMD.
    /// </summary>
    public async Task<string> Name2EEMethodAsync(
        string sessionId,
        string userId,
        string typeName,
        string methodName,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["kind"] = "lookup_method",
            ["typeName"] = typeName,
            ["methodName"] = methodName
        };

        return await CallToolAsync("inspect", args, cancellationToken);
    }

    /// <summary>
    /// Gets managed call stacks for all threads using ClrMD.
    /// Fast alternative to SOS clrstack (~500ms vs 12s).
    /// </summary>
    public async Task<string> ClrStackAsync(
        string sessionId,
        string userId,
        bool includeArguments = true,
        bool includeLocals = true,
        bool includeRegisters = true,
        uint threadId = 0,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["kind"] = "clr_stack",
            ["includeArguments"] = includeArguments,
            ["includeLocals"] = includeLocals,
            ["includeRegisters"] = includeRegisters,
            ["threadId"] = threadId
        };

        return await CallToolAsync("inspect", args, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> AnalyzePerformanceAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["kind"] = "performance",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> AnalyzeCpuUsageAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["kind"] = "cpu",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> AnalyzeAllocationsAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["kind"] = "allocations",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> AnalyzeGcAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["kind"] = "gc",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> AnalyzeContentionAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["kind"] = "contention",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }



    /// <inheritdoc/>
    public async Task<string> AnalyzeSecurityAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["kind"] = "security",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }



    /// <inheritdoc/>
    public async Task<string> CompareDumpsAsync(
        string baselineSessionId,
        string baselineUserId,
        string targetSessionId,
        string targetUserId,
        CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("compare", new Dictionary<string, object?>
        {
            ["kind"] = "dumps",
            ["baselineSessionId"] = baselineSessionId,
            ["baselineUserId"] = baselineUserId,
            ["targetSessionId"] = targetSessionId,
            ["targetUserId"] = targetUserId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> CompareHeapsAsync(
        string baselineSessionId,
        string baselineUserId,
        string targetSessionId,
        string targetUserId,
        CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("compare", new Dictionary<string, object?>
        {
            ["kind"] = "heaps",
            ["baselineSessionId"] = baselineSessionId,
            ["baselineUserId"] = baselineUserId,
            ["targetSessionId"] = targetSessionId,
            ["targetUserId"] = targetUserId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> CompareThreadsAsync(
        string baselineSessionId,
        string baselineUserId,
        string targetSessionId,
        string targetUserId,
        CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("compare", new Dictionary<string, object?>
        {
            ["kind"] = "threads",
            ["baselineSessionId"] = baselineSessionId,
            ["baselineUserId"] = baselineUserId,
            ["targetSessionId"] = targetSessionId,
            ["targetUserId"] = targetUserId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> CompareModulesAsync(
        string baselineSessionId,
        string baselineUserId,
        string targetSessionId,
        string targetUserId,
        CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("compare", new Dictionary<string, object?>
        {
            ["kind"] = "modules",
            ["baselineSessionId"] = baselineSessionId,
            ["baselineUserId"] = baselineUserId,
            ["targetSessionId"] = targetSessionId,
            ["targetUserId"] = targetUserId
        }, cancellationToken);
    }



    /// <inheritdoc/>
    public async Task<string> AddWatchAsync(string sessionId, string userId, string expression, string? name = null, CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["action"] = "add",
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["expression"] = expression
        };
        if (!string.IsNullOrEmpty(name))
        {
            args["description"] = name;
        }
        return await CallToolAsync("watch", args, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ListWatchesAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("watch", new Dictionary<string, object?>
        {
            ["action"] = "list",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> EvaluateWatchesAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("watch", new Dictionary<string, object?>
        {
            ["action"] = "evaluate_all",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> EvaluateWatchAsync(string sessionId, string userId, string watchId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("watch", new Dictionary<string, object?>
        {
            ["action"] = "evaluate",
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["watchId"] = watchId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> RemoveWatchAsync(string sessionId, string userId, string watchId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("watch", new Dictionary<string, object?>
        {
            ["action"] = "remove",
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["watchId"] = watchId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ClearWatchesAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("watch", new Dictionary<string, object?>
        {
            ["action"] = "clear",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }



    /// <inheritdoc/>
    public async Task<string> GenerateReportAsync(string sessionId, string userId, string format = "markdown", bool includeWatches = true, bool includeComparison = false, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("report", new Dictionary<string, object?>
        {
            ["action"] = "full",
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["format"] = format,
            ["includeWatches"] = includeWatches
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> GenerateSummaryReportAsync(string sessionId, string userId, string format = "markdown", CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("report", new Dictionary<string, object?>
        {
            ["action"] = "summary",
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["format"] = format
        }, cancellationToken);
    }



    /// <inheritdoc/>
    public async Task<string> ResolveSourceLinkAsync(string sessionId, string userId, string sourceFile, int? lineNumber = null, CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["action"] = "resolve",
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["sourceFile"] = sourceFile
        };
        if (lineNumber.HasValue)
        {
            args["lineNumber"] = lineNumber.Value;
        }
        return await CallToolAsync("source_link", args, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> GetSourceLinkInfoAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("source_link", new Dictionary<string, object?>
        {
            ["action"] = "info",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }



    /// <inheritdoc/>
    public async Task<string> ConfigureAdditionalSymbolsAsync(string sessionId, string userId, string symbolPath, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("symbols", new Dictionary<string, object?>
        {
            ["action"] = "configure_additional",
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["additionalPaths"] = symbolPath
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> GetSymbolServersAsync(CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("symbols", new Dictionary<string, object?>
        {
            ["action"] = "get_servers"
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ClearSymbolCacheAsync(string userId, string dumpId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("symbols", new Dictionary<string, object?>
        {
            ["action"] = "clear_cache",
            ["userId"] = userId,
            ["dumpId"] = dumpId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ReloadSymbolsAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("symbols", new Dictionary<string, object?>
        {
            ["action"] = "reload",
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }



    /// <inheritdoc/>
    public async Task<string> DownloadDatadogSymbolsAsync(
        string sessionId,
        string userId,
        string commitSha,
        string? targetFramework = null,
        bool loadIntoDebugger = true,
        bool forceVersion = false,
        string? version = null,
        int? buildId = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["commitSha"] = commitSha,
            ["loadIntoDebugger"] = loadIntoDebugger,
            ["forceVersion"] = forceVersion
        };

        if (!string.IsNullOrEmpty(targetFramework))
        {
            args["targetFramework"] = targetFramework;
        }

        if (!string.IsNullOrEmpty(version))
        {
            args["version"] = version;
        }

        if (buildId.HasValue)
        {
            args["buildId"] = buildId.Value;
        }

        args["action"] = "download";
        return await CallToolAsync("datadog_symbols", args, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ListDatadogArtifactsAsync(string commitSha, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("datadog_symbols", new Dictionary<string, object?>
        {
            ["action"] = "list_artifacts",
            ["commitSha"] = commitSha
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> GetDatadogSymbolsConfigAsync(CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("datadog_symbols", new Dictionary<string, object?>
        {
            ["action"] = "get_config"
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> PrepareDatadogSymbolsAsync(
        string sessionId,
        string userId,
        bool loadIntoDebugger = true,
        bool forceVersion = false,
        CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("datadog_symbols", new Dictionary<string, object?>
        {
            ["action"] = "prepare",
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["loadIntoDebugger"] = loadIntoDebugger,
            ["forceVersion"] = forceVersion
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ClearDatadogSymbolsAsync(
        string sessionId,
        string userId,
        bool clearApiCache = false,
        CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("datadog_symbols", new Dictionary<string, object?>
        {
            ["action"] = "clear",
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["clearApiCache"] = clearApiCache
        }, cancellationToken);
    }



    /// <inheritdoc/>
    public async Task<string> CallToolAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        try
        {
            var request = new McpRequest
            {
                JsonRpc = "2.0",
                Id = Interlocked.Increment(ref _requestId),
                Method = "tools/call",
                Params = new ToolCallParams
                {
                    Name = toolName,
                    Arguments = arguments
                }
            };

            var result = await SendMcpRequestAsync<ToolCallResult>(request, cancellationToken);

            // Extract text content from result
            if (result?.Content != null && result.Content.Count > 0)
            {
                var textItems = result.Content
                    .Where(c => string.Equals(c.Type, "text", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (textItems.Count > 0)
                {
                    var textContent = textItems
                        .Select(c => c.Text ?? string.Empty)
                        .FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? string.Empty;

                    // Check if this is an error result - throw exception with the actual message (even if empty).
                    if (result.IsError == true)
                    {
                        throw new McpClientException(string.IsNullOrEmpty(textContent)
                            ? "Tool returned an error with no details"
                            : textContent);
                    }

                    // If the tool returned an empty text payload but also provided non-text content,
                    // fall back to returning the first non-text content item (as JSON) instead of
                    // printing a confusing {"type":"text","text":""} placeholder.
                    if (string.IsNullOrEmpty(textContent))
                    {
                        var firstNonText = result.Content.FirstOrDefault(c =>
                            !string.Equals(c.Type, "text", StringComparison.OrdinalIgnoreCase));
                        if (firstNonText != null)
                        {
                            return JsonSerializer.Serialize(firstNonText, JsonOptions);
                        }
                    }

                    return textContent;
                }

                // If no text content but is an error, serialize the content for the error
                if (result.IsError == true)
                {
                    var errorJson = JsonSerializer.Serialize(result.Content[0], JsonOptions);
                    throw new McpClientException($"Tool error: {errorJson}");
                }

                // If no text content, serialize the first content item
                return JsonSerializer.Serialize(result.Content[0], JsonOptions);
            }

            if (result?.IsError == true)
            {
                throw new McpClientException("Tool returned an error with no details");
            }

            return "Tool executed successfully (no output)";
        }
        catch (HttpRequestException ex)
        {
            throw new McpClientException($"HTTP error calling tool '{toolName}': {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new McpClientException($"JSON error calling tool '{toolName}': {ex.Message}", ex);
        }
    }


    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DisconnectAsync();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}


/// <summary>
/// MCP JSON-RPC request.
/// </summary>
internal class McpRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public object? Params { get; set; }
}

/// <summary>
/// MCP JSON-RPC response.
/// </summary>
internal class McpResponse<T>
{
    [JsonPropertyName("jsonrpc")]
    public string? JsonRpc { get; set; }

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("result")]
    public T? Result { get; set; }

    [JsonPropertyName("error")]
    public McpError? Error { get; set; }
}

/// <summary>
/// MCP error object.
/// </summary>
internal class McpError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Parameters for tools/call method.
/// </summary>
internal class ToolCallParams
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public Dictionary<string, object?>? Arguments { get; set; }
}

/// <summary>
/// Result of tools/list method.
/// </summary>
internal class ToolsListResult
{
    [JsonPropertyName("tools")]
    public List<ToolInfo>? Tools { get; set; }
}

/// <summary>
/// Tool information.
/// </summary>
internal class ToolInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// Result of tools/call method.
/// </summary>
internal class ToolCallResult
{
    [JsonPropertyName("content")]
    public List<ContentItem>? Content { get; set; }

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }
}

/// <summary>
/// Content item in tool result.
/// </summary>
internal class ContentItem
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}


/// <summary>
/// Exception thrown when an MCP operation fails.
/// </summary>
public class McpClientException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="McpClientException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public McpClientException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
