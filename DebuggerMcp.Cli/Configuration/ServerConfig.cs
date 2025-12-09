using System.Text.Json;
using System.Text.Json.Serialization;

namespace DebuggerMcp.Cli.Configuration;

/// <summary>
/// Represents a server entry in the configuration.
/// </summary>
public class ServerEntry
{
    /// <summary>
    /// Gets or sets the server URL.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional API key for authentication.
    /// </summary>
    [JsonPropertyName("apiKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey { get; set; }
}

/// <summary>
/// Represents the CLI server configuration file.
/// </summary>
public class ServerConfig
{
    /// <summary>
    /// Gets or sets the list of configured servers.
    /// </summary>
    [JsonPropertyName("servers")]
    public List<ServerEntry> Servers { get; set; } = [];
}

/// <summary>
/// Manages loading and saving of the server configuration file.
/// </summary>
public class ServerConfigManager
{
    private const string ConfigFileName = "servers.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _configPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerConfigManager"/> class.
    /// </summary>
    /// <remarks>
    /// The configuration file is stored in the same directory as the CLI binary.
    /// </remarks>
    public ServerConfigManager()
    {
        _configPath = GetConfigPath();
    }

    /// <summary>
    /// Gets the path to the configuration file.
    /// </summary>
    public string ConfigPath => _configPath;

    /// <summary>
    /// Loads the server configuration from disk.
    /// </summary>
    /// <returns>The server configuration, or a default empty config if the file doesn't exist.</returns>
    public ServerConfig Load()
    {
        if (!File.Exists(_configPath))
        {
            return new ServerConfig();
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<ServerConfig>(json, JsonOptions) ?? new ServerConfig();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Warning: Failed to load config from {_configPath}: {ex.Message}");
            Console.ResetColor();
            return new ServerConfig();
        }
    }

    /// <summary>
    /// Saves the server configuration to disk.
    /// </summary>
    /// <param name="config">The configuration to save.</param>
    public void Save(ServerConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Failed to save config to {_configPath}: {ex.Message}");
            Console.ResetColor();
            throw;
        }
    }

    /// <summary>
    /// Adds a server to the configuration.
    /// </summary>
    /// <param name="url">The server URL.</param>
    /// <param name="apiKey">Optional API key.</param>
    /// <returns>True if added, false if already exists.</returns>
    public bool AddServer(string url, string? apiKey = null)
    {
        var config = Load();

        // Normalize URL
        url = NormalizeUrl(url);

        // Check if already exists
        if (config.Servers.Any(s => NormalizeUrl(s.Url).Equals(url, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        config.Servers.Add(new ServerEntry { Url = url, ApiKey = apiKey });
        Save(config);
        return true;
    }

    /// <summary>
    /// Removes a server from the configuration by URL.
    /// </summary>
    /// <param name="url">The server URL to remove.</param>
    /// <returns>True if removed, false if not found.</returns>
    public bool RemoveServer(string url)
    {
        var config = Load();
        url = NormalizeUrl(url);

        var removed = config.Servers.RemoveAll(s =>
            NormalizeUrl(s.Url).Equals(url, StringComparison.OrdinalIgnoreCase)) > 0;

        if (removed)
        {
            Save(config);
        }

        return removed;
    }

    /// <summary>
    /// Gets all configured server URLs.
    /// </summary>
    /// <returns>List of server entries.</returns>
    public List<ServerEntry> GetServers()
    {
        return Load().Servers;
    }

    /// <summary>
    /// Creates a default configuration file with localhost servers.
    /// </summary>
    public void CreateDefaultConfig()
    {
        var config = new ServerConfig
        {
            Servers =
            [
                new ServerEntry { Url = "http://localhost:5000" },
                new ServerEntry { Url = "http://localhost:5001" },
                new ServerEntry { Url = "http://localhost:5002" },
                new ServerEntry { Url = "http://localhost:5003" }
            ]
        };
        Save(config);
    }

    /// <summary>
    /// Normalizes a URL for comparison.
    /// </summary>
    private static string NormalizeUrl(string url)
    {
        url = url.Trim().TrimEnd('/');

        // Add http:// if no scheme
        if (!url.Contains("://"))
        {
            url = "http://" + url;
        }

        return url;
    }

    /// <summary>
    /// Gets the configuration file path (same directory as the binary).
    /// </summary>
    private static string GetConfigPath()
    {
        // Get the directory where the CLI binary is located
        var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;

        // For single-file deployments, Location might be empty
        if (string.IsNullOrEmpty(assemblyLocation))
        {
            assemblyLocation = Environment.ProcessPath ?? AppContext.BaseDirectory;
        }

        var directory = Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;
        return Path.Combine(directory, ConfigFileName);
    }
}

