using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    private string? _serverUrl;
    private string? _messageEndpoint;
    private readonly List<string> _availableTools = [];
    private bool _disposed;
    private int _requestId;

    // SSE response handling
    private Task? _sseListenerTask;
    private CancellationTokenSource? _sseListenerCts;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pendingRequests = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <inheritdoc/>
    public bool IsConnected => _httpClient != null && !string.IsNullOrEmpty(_messageEndpoint);

    /// <inheritdoc/>
    public IReadOnlyList<string> AvailableTools => _availableTools.AsReadOnly();

    /// <inheritdoc/>
    public async Task ConnectAsync(string serverUrl, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        if (_httpClient != null)
        {
            await DisconnectAsync();
        }

        // Normalize URL
        _serverUrl = serverUrl.TrimEnd('/');

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

            var response = await _sseClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new McpClientException($"SSE endpoint returned {response.StatusCode}. Is the server started with --mcp-http?");
            }

            // Read the first event which should contain the endpoint info
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var reader = new StreamReader(stream);

            // Read SSE events until we get the endpoint
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break; // End of stream
                }

                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                // SSE event format: "event: endpoint\ndata: /mcp/message?sessionId=xxx\n\n"
                if (line.StartsWith("data: "))
                {
                    var data = line[6..];

                    // Skip "endpoint" event type markers
                    if (data == "endpoint")
                    {
                        continue;
                    }

                    // Check if this is a URL path
                    if (data.StartsWith("/") || data.StartsWith("http"))
                    {
                        _messageEndpoint = data.StartsWith("http")
                            ? data
                            : $"{_serverUrl}{data}";
                        break;
                    }

                    // Try parsing as JSON in case the endpoint is sent as JSON
                    if (data.StartsWith("{"))
                    {
                        try
                        {
                            var json = JsonDocument.Parse(data);
                            if (json.RootElement.TryGetProperty("endpoint", out var endpointProp))
                            {
                                var endpoint = endpointProp.GetString();
                                if (!string.IsNullOrEmpty(endpoint))
                                {
                                    _messageEndpoint = endpoint.StartsWith("http")
                                        ? endpoint
                                        : $"{_serverUrl}{endpoint}";
                                    break;
                                }
                            }
                        }
                        catch
                        {
                            // Not JSON, continue
                        }
                    }
                }

                // Check for event type
                if (line.StartsWith("event: endpoint"))
                {
                    // Next data line should be the endpoint
                    continue;
                }
            }

            // If we couldn't get the endpoint from SSE, try the default
            if (string.IsNullOrEmpty(_messageEndpoint))
            {
                _messageEndpoint = $"{_serverUrl}/mcp/message";
            }

            // Start background SSE listener for responses
            _sseListenerCts = new CancellationTokenSource();
            _sseListenerTask = Task.Run(() => ListenForSseResponsesAsync(reader, _sseListenerCts.Token));

            // Give the listener a moment to start
            await Task.Delay(100, cancellationToken);

            // Now discover tools
            await DiscoverToolsAsync(cancellationToken);
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
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"SSE listener error: {ex.Message}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Processes an SSE data event (JSON-RPC response).
    /// </summary>
    private void ProcessSseData(string data)
    {
        if (string.IsNullOrWhiteSpace(data) || !data.StartsWith("{"))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            // Get the request ID
            if (root.TryGetProperty("id", out var idProp))
            {
                var id = idProp.ValueKind == JsonValueKind.Number
                    ? idProp.GetInt32()
                    : int.Parse(idProp.GetString() ?? "0");

                if (_pendingRequests.TryRemove(id, out var tcs))
                {
                    tcs.SetResult(data);
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Error processing SSE response: {ex.Message}");
            Console.ResetColor();
        }
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync()
    {
        // Cancel SSE listener
        if (_sseListenerCts != null)
        {
            await _sseListenerCts.CancelAsync();
            _sseListenerCts.Dispose();
            _sseListenerCts = null;
        }

        // Wait for SSE listener task to complete (with timeout)
        if (_sseListenerTask != null)
        {
            try
            {
                await _sseListenerTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                // Listener task didn't complete in time, continue with cleanup
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelled
            }
            _sseListenerTask = null;
        }

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
        catch (Exception ex)
        {
            // Tools discovery failed
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Warning: Tool discovery failed: {ex.Message}");
            Console.ResetColor();
            _availableTools.Clear();
        }
    }

    /// <summary>
    /// Sends an MCP request and waits for the response via SSE.
    /// </summary>
    private async Task<T?> SendMcpRequestAsync<T>(McpRequest request, CancellationToken cancellationToken)
        where T : class
    {
        EnsureConnected();

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
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5)); // 5 minute timeout for long operations

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


    /// <inheritdoc/>
    public async Task<string> CreateSessionAsync(string userId, CancellationToken cancellationToken = default)
    {
        // MCP library converts PascalCase to snake_case
        return await CallToolAsync("create_session", new Dictionary<string, object?>
        {
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ListSessionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("list_sessions", new Dictionary<string, object?>
        {
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> CloseSessionAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("close_session", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> GetDebuggerInfoAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("get_debugger_info", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> RestoreSessionAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("restore_session", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ClearCommandCacheAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("clear_command_cache", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> LoadVerifyCoreModulesAsync(string sessionId, string userId, string? moduleNames = null, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        };
        if (!string.IsNullOrEmpty(moduleNames))
        {
            parameters["moduleNames"] = moduleNames;
        }
        return await CallToolAsync("load_verify_core_modules", parameters, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> OpenDumpAsync(string sessionId, string userId, string dumpId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("open_dump", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["dumpId"] = dumpId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> CloseDumpAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("close_dump", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ExecuteCommandAsync(string sessionId, string userId, string command, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("execute_command", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["command"] = command
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> LoadSosAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("load_sos", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }



    /// <inheritdoc/>
    public async Task<string> AnalyzeCrashAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("analyze_crash", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> AnalyzeDotNetAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("analyze_dot_net_crash", new Dictionary<string, object?>
        {
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
            ["address"] = address,
            ["maxDepth"] = maxDepth,
            ["maxArrayElements"] = maxArrayElements,
            ["maxStringLength"] = maxStringLength
        };

        if (!string.IsNullOrEmpty(methodTable))
        {
            args["methodTable"] = methodTable;
        }

        return await CallToolAsync("inspect_object", args, cancellationToken);
    }



    /// <inheritdoc/>
    public async Task<string> AnalyzePerformanceAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("analyze_performance", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> AnalyzeCpuUsageAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("analyze_cpu_usage", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> AnalyzeAllocationsAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("analyze_allocations", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> AnalyzeGcAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("analyze_gc", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> AnalyzeContentionAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("analyze_contention", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }



    /// <inheritdoc/>
    public async Task<string> AnalyzeSecurityAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("analyze_security", new Dictionary<string, object?>
        {
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
        return await CallToolAsync("compare_dumps", new Dictionary<string, object?>
        {
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
        return await CallToolAsync("compare_heaps", new Dictionary<string, object?>
        {
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
        return await CallToolAsync("compare_threads", new Dictionary<string, object?>
        {
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
        return await CallToolAsync("compare_modules", new Dictionary<string, object?>
        {
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
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["expression"] = expression
        };
        if (!string.IsNullOrEmpty(name))
        {
            args["name"] = name;
        }
        return await CallToolAsync("add_watch", args, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ListWatchesAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("list_watches", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> EvaluateWatchesAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("evaluate_watches", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> EvaluateWatchAsync(string sessionId, string userId, string watchId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("evaluate_watch", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["watchId"] = watchId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> RemoveWatchAsync(string sessionId, string userId, string watchId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("remove_watch", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["watchId"] = watchId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ClearWatchesAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("clear_watches", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }



    /// <inheritdoc/>
    public async Task<string> GenerateReportAsync(string sessionId, string userId, string format = "markdown", bool includeWatches = true, bool includeComparison = false, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("generate_report", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["format"] = format,
            ["includeWatches"] = includeWatches,
            ["includeComparison"] = includeComparison
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> GenerateSummaryReportAsync(string sessionId, string userId, string format = "markdown", CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("generate_summary_report", new Dictionary<string, object?>
        {
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
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["sourceFile"] = sourceFile
        };
        if (lineNumber.HasValue)
        {
            args["lineNumber"] = lineNumber.Value;
        }
        return await CallToolAsync("resolve_source_link", args, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> GetSourceLinkInfoAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("get_source_link_info", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId
        }, cancellationToken);
    }



    /// <inheritdoc/>
    public async Task<string> ConfigureAdditionalSymbolsAsync(string sessionId, string userId, string symbolPath, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("configure_additional_symbols", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["userId"] = userId,
            ["symbolPath"] = symbolPath
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> GetSymbolServersAsync(CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("get_symbol_servers", new Dictionary<string, object?>(), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ClearSymbolCacheAsync(string userId, string dumpId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("clear_symbol_cache", new Dictionary<string, object?>
        {
            ["userId"] = userId,
            ["dumpId"] = dumpId
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ReloadSymbolsAsync(string sessionId, string userId, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("reload_symbols", new Dictionary<string, object?>
        {
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

        return await CallToolAsync("download_datadog_symbols", args, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ListDatadogArtifactsAsync(string commitSha, CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("list_datadog_artifacts", new Dictionary<string, object?>
        {
            ["commitSha"] = commitSha
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> GetDatadogSymbolsConfigAsync(CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("get_datadog_symbols_config", new Dictionary<string, object?>(), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> PrepareDatadogSymbolsAsync(
        string sessionId,
        string userId,
        bool loadIntoDebugger = true,
        bool forceVersion = false,
        CancellationToken cancellationToken = default)
    {
        return await CallToolAsync("prepare_datadog_symbols", new Dictionary<string, object?>
        {
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
        return await CallToolAsync("clear_datadog_symbols", new Dictionary<string, object?>
        {
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
                var textContent = result.Content
                    .Where(c => c.Type == "text")
                    .Select(c => c.Text)
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(textContent))
                {
                    // Check if this is an error result - throw exception with the actual message
                    if (result.IsError == true)
                    {
                        throw new McpClientException(textContent);
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
