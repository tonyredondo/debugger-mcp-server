using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DebuggerMcp.Cli.Configuration;

namespace DebuggerMcp.Cli.Client;

/// <summary>
/// Represents the capabilities returned by a server.
/// </summary>
public class ServerCapabilities
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "unknown";

    [JsonPropertyName("architecture")]
    public string Architecture { get; set; } = "unknown";

    [JsonPropertyName("isAlpine")]
    public bool IsAlpine { get; set; }

    [JsonPropertyName("runtimeVersion")]
    public string? RuntimeVersion { get; set; }

    [JsonPropertyName("debuggerType")]
    public string? DebuggerType { get; set; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("distribution")]
    public string? Distribution { get; set; }
}

/// <summary>
/// Represents a discovered server with its capabilities and status.
/// </summary>
public class DiscoveredServer
{
    /// <summary>
    /// Gets or sets the server URL.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional API key.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets whether the server is online.
    /// </summary>
    public bool IsOnline { get; set; }

    /// <summary>
    /// Gets or sets the server capabilities (null if offline).
    /// </summary>
    public ServerCapabilities? Capabilities { get; set; }

    /// <summary>
    /// Gets or sets any error message if the server is offline.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets the auto-generated server name based on capabilities.
    /// </summary>
    /// <remarks>
    /// Format: {distro}-{arch}, e.g., "alpine-x64", "debian-arm64"
    /// </remarks>
    public string Name
    {
        get
        {
            if (!IsOnline || Capabilities == null)
                return "-";

            var distro = Capabilities.IsAlpine ? "alpine" : 
                (Capabilities.Distribution ?? "debian");
            return $"{distro}-{Capabilities.Architecture}";
        }
    }

    /// <summary>
    /// Gets a short display name for the URL (hostname:port).
    /// </summary>
    public string ShortUrl
    {
        get
        {
            try
            {
                var uri = new Uri(Url);
                return $"{uri.Host}:{uri.Port}";
            }
            catch
            {
                return Url;
            }
        }
    }
}

/// <summary>
/// Discovers server capabilities by querying configured servers.
/// </summary>
public class ServerDiscovery
{
    private readonly ServerConfigManager _configManager;
    private readonly TimeSpan _timeout;
    private List<DiscoveredServer> _servers = [];
    private DiscoveredServer? _currentServer;

    /// <summary>
    /// Gets the list of discovered servers.
    /// </summary>
    public IReadOnlyList<DiscoveredServer> Servers => _servers.AsReadOnly();

    /// <summary>
    /// Gets or sets the current active server.
    /// </summary>
    public DiscoveredServer? CurrentServer
    {
        get => _currentServer;
        set => _currentServer = value;
    }

    /// <summary>
    /// Gets the number of online servers.
    /// </summary>
    public int OnlineCount => _servers.Count(s => s.IsOnline);

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerDiscovery"/> class.
    /// </summary>
    /// <param name="configManager">The configuration manager.</param>
    /// <param name="timeout">Timeout for capability requests (default: 5 seconds).</param>
    public ServerDiscovery(ServerConfigManager configManager, TimeSpan? timeout = null)
    {
        _configManager = configManager;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Discovers capabilities of all configured servers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of discovered servers.</returns>
    public async Task<List<DiscoveredServer>> DiscoverAllAsync(CancellationToken cancellationToken = default)
    {
        var serverEntries = _configManager.GetServers();
        
        if (serverEntries.Count == 0)
        {
            _servers = [];
            return _servers;
        }

        // Query all servers in parallel
        var tasks = serverEntries.Select(entry => DiscoverServerAsync(entry, cancellationToken));
        var results = await Task.WhenAll(tasks);
        
        _servers = results.ToList();
        
        // Set current server to first online server if not already set
        if (_currentServer == null || !_currentServer.IsOnline)
        {
            _currentServer = _servers.FirstOrDefault(s => s.IsOnline);
        }
        
        return _servers;
    }

    /// <summary>
    /// Discovers capabilities of a single server.
    /// </summary>
    private async Task<DiscoveredServer> DiscoverServerAsync(ServerEntry entry, CancellationToken cancellationToken)
    {
        var server = new DiscoveredServer
        {
            Url = entry.Url,
            ApiKey = entry.ApiKey
        };

        try
        {
            using var client = new HttpClient { Timeout = _timeout };
            
            if (!string.IsNullOrEmpty(entry.ApiKey))
            {
                client.DefaultRequestHeaders.Add("X-API-Key", entry.ApiKey);
            }

            var url = entry.Url.TrimEnd('/') + "/api/server/capabilities";
            var response = await client.GetAsync(url, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                server.Capabilities = await response.Content.ReadFromJsonAsync<ServerCapabilities>(cancellationToken: cancellationToken);
                server.IsOnline = true;
            }
            else
            {
                server.IsOnline = false;
                server.ErrorMessage = $"HTTP {(int)response.StatusCode}";
            }
        }
        catch (TaskCanceledException)
        {
            server.IsOnline = false;
            server.ErrorMessage = "Timeout";
        }
        catch (HttpRequestException ex)
        {
            server.IsOnline = false;
            server.ErrorMessage = ex.Message.Contains("Connection refused") ? "Connection refused" : ex.Message;
        }
        catch (Exception ex)
        {
            server.IsOnline = false;
            server.ErrorMessage = ex.Message;
        }

        return server;
    }

    /// <summary>
    /// Finds a server by URL or auto-generated name.
    /// </summary>
    /// <param name="urlOrName">The URL or name to search for.</param>
    /// <returns>The matching server, or null if not found.</returns>
    public DiscoveredServer? FindServer(string urlOrName)
    {
        if (string.IsNullOrWhiteSpace(urlOrName))
            return null;

        // Try exact URL match first
        var byUrl = _servers.FirstOrDefault(s => 
            s.Url.Equals(urlOrName, StringComparison.OrdinalIgnoreCase) ||
            s.ShortUrl.Equals(urlOrName, StringComparison.OrdinalIgnoreCase));
        
        if (byUrl != null)
            return byUrl;

        // Try name match
        return _servers.FirstOrDefault(s => 
            s.Name.Equals(urlOrName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds servers matching the given dump characteristics.
    /// </summary>
    /// <param name="architecture">The dump architecture (e.g., "x64", "arm64").</param>
    /// <param name="isAlpine">Whether the dump is from an Alpine container.</param>
    /// <returns>List of matching online servers.</returns>
    public List<DiscoveredServer> FindMatchingServers(string architecture, bool isAlpine)
    {
        return _servers
            .Where(s => s.IsOnline && s.Capabilities != null)
            .Where(s => s.Capabilities!.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase))
            .Where(s => s.Capabilities!.IsAlpine == isAlpine)
            .ToList();
    }

    /// <summary>
    /// Checks if the current server matches the dump characteristics.
    /// </summary>
    /// <param name="architecture">The dump architecture.</param>
    /// <param name="isAlpine">Whether the dump is from Alpine.</param>
    /// <returns>True if current server matches, false otherwise.</returns>
    public bool CurrentServerMatches(string architecture, bool isAlpine)
    {
        if (_currentServer?.Capabilities == null)
            return false;

        return _currentServer.Capabilities.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase) &&
               _currentServer.Capabilities.IsAlpine == isAlpine;
    }

    /// <summary>
    /// Refreshes the capabilities of a specific server.
    /// </summary>
    /// <param name="server">The server to refresh.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RefreshServerAsync(DiscoveredServer server, CancellationToken cancellationToken = default)
    {
        var entry = new ServerEntry { Url = server.Url, ApiKey = server.ApiKey };
        var refreshed = await DiscoverServerAsync(entry, cancellationToken);
        
        server.IsOnline = refreshed.IsOnline;
        server.Capabilities = refreshed.Capabilities;
        server.ErrorMessage = refreshed.ErrorMessage;
    }
}

