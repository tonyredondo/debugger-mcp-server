using System.Text.Json;
using System.Text.Json.Serialization;
using DebuggerMcp.Cli.Serialization;

namespace DebuggerMcp.Cli.Configuration;

/// <summary>
/// Settings for connecting to a Debugger MCP Server.
/// </summary>
/// <remarks>
/// These settings can be configured via:
/// <list type="bullet">
/// <item><description>Command-line arguments (highest priority)</description></item>
/// <item><description>Environment variables</description></item>
/// <item><description>Configuration file (~/.dbg-mcp/config.json)</description></item>
/// <item><description>Default values (lowest priority)</description></item>
/// </list>
/// </remarks>
public class ConnectionSettings
{
    private static readonly JsonSerializerOptions JsonOptions = CliJsonSerializationDefaults.CaseInsensitiveCamelCaseIndentedIgnoreNull;

    /// <summary>
    /// Default request timeout in seconds.
    /// Increased from 300 to 600 to handle dumps with many symbols that take longer to download.
    /// </summary>
    public const int DefaultTimeoutSeconds = 600;

    /// <summary>
    /// Default history file path.
    /// </summary>
    public static readonly string DefaultHistoryFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dbg-mcp-history");

    /// <summary>
    /// Default configuration directory.
    /// </summary>
    public static readonly string DefaultConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dbg-mcp");

    /// <summary>
    /// Default configuration file path.
    /// </summary>
    public static readonly string DefaultConfigFile = Path.Combine(
        DefaultConfigDirectory,
        "config.json");

    /// <summary>
    /// Gets or sets the server URL.
    /// </summary>
    /// <example>http://localhost:5000</example>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// Gets or sets the API key for authentication.
    /// </summary>
    /// <remarks>
    /// This is sent in the X-API-Key header for each request.
    /// </remarks>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the user ID for operations.
    /// </summary>
    /// <remarks>
    /// Defaults to the current system username if not specified.
    /// </remarks>
    public string UserId { get; set; } = Environment.UserName;

    /// <summary>
    /// Gets or sets the request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    /// <summary>
    /// Gets the timeout as a TimeSpan.
    /// </summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    /// <summary>
    /// Gets or sets the default output format.
    /// </summary>
    public OutputFormat OutputFormat { get; set; } = OutputFormat.Text;

    /// <summary>
    /// Gets or sets whether verbose output is enabled.
    /// </summary>
    public bool Verbose { get; set; }

    /// <summary>
    /// Gets or sets the path to the command history file.
    /// </summary>
    public string HistoryFile { get; set; } = DefaultHistoryFile;

    /// <summary>
    /// Gets or sets the maximum number of commands to keep in history.
    /// </summary>
    public int HistorySize { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the server profiles for quick switching.
    /// </summary>
    public Dictionary<string, ServerProfile> Profiles { get; set; } = new();

    /// <summary>
    /// Gets or sets the last-used session ID per server/user.
    /// </summary>
    /// <remarks>
    /// The key is a normalized "<c>{serverUrl}::{userId}</c>" identifier.
    /// </remarks>
    public Dictionary<string, string> LastSessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the last-used session ID for a given server/user pair.
    /// </summary>
    public string? GetLastSessionId(string? serverUrl, string? userId)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var key = BuildLastSessionKey(serverUrl, userId);
        return LastSessions.TryGetValue(key, out var sessionId) && !string.IsNullOrWhiteSpace(sessionId)
            ? sessionId
            : null;
    }

    /// <summary>
    /// Sets the last-used session ID for a given server/user pair.
    /// </summary>
    public void SetLastSessionId(string? serverUrl, string? userId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId cannot be null or empty.", nameof(sessionId));
        }

        LastSessions[BuildLastSessionKey(serverUrl, userId)] = sessionId;
    }

    /// <summary>
    /// Clears the last-used session ID for a given server/user pair when it matches the provided session ID.
    /// </summary>
    public bool ClearLastSessionId(string? serverUrl, string? userId, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var key = BuildLastSessionKey(serverUrl, userId);
        if (!LastSessions.TryGetValue(key, out var stored) || !string.Equals(stored, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return LastSessions.Remove(key);
    }

    private static string BuildLastSessionKey(string serverUrl, string userId)
        => $"{NormalizeServerUrl(serverUrl)}::{userId.Trim()}";

    private static string NormalizeServerUrl(string url)
    {
        url = url.Trim().TrimEnd('/');

        if (!url.Contains("://", StringComparison.Ordinal))
        {
            url = "http://" + url;
        }

        return url;
    }

    /// <summary>
    /// Creates settings from environment variables and defaults.
    /// </summary>
    /// <returns>A new ConnectionSettings instance.</returns>
    public static ConnectionSettings FromEnvironment()
    {
        return new ConnectionSettings
        {
            ServerUrl = CliEnvironment.Get(CliEnvironment.ServerUrl),
            ApiKey = CliEnvironment.Get(CliEnvironment.ApiKey),
            UserId = CliEnvironment.GetOrDefault(CliEnvironment.UserId, Environment.UserName),
            TimeoutSeconds = CliEnvironment.GetIntOrDefault(CliEnvironment.Timeout, DefaultTimeoutSeconds),
            OutputFormat = ParseOutputFormat(CliEnvironment.Get(CliEnvironment.OutputFormat)),
            Verbose = CliEnvironment.GetBoolOrDefault(CliEnvironment.Verbose, false),
            HistoryFile = CliEnvironment.GetOrDefault(CliEnvironment.HistoryFile, DefaultHistoryFile)
        };
    }

    /// <summary>
    /// Loads settings from the default config file, then applies environment variable overrides.
    /// </summary>
    /// <returns>The merged settings.</returns>
    public static ConnectionSettings Load()
    {
        // Start with defaults
        var settings = new ConnectionSettings();

        // Load from config file if it exists
        if (File.Exists(DefaultConfigFile))
        {
            try
            {
                var fileSettings = LoadFromFile(DefaultConfigFile);
                if (fileSettings != null)
                {
                    settings = fileSettings;
                }
            }
            catch
            {
                // Ignore config file errors, use defaults
            }
        }

        // Apply environment variable overrides
        var envUrl = CliEnvironment.Get(CliEnvironment.ServerUrl);
        if (!string.IsNullOrEmpty(envUrl))
        {
            settings.ServerUrl = envUrl;
        }

        var envApiKey = CliEnvironment.Get(CliEnvironment.ApiKey);
        if (!string.IsNullOrEmpty(envApiKey))
        {
            settings.ApiKey = envApiKey;
        }

        var envUserId = CliEnvironment.Get(CliEnvironment.UserId);
        if (!string.IsNullOrEmpty(envUserId))
        {
            settings.UserId = envUserId;
        }

        var envTimeout = CliEnvironment.Get(CliEnvironment.Timeout);
        if (!string.IsNullOrEmpty(envTimeout) && int.TryParse(envTimeout, out var timeout))
        {
            settings.TimeoutSeconds = timeout;
        }

        var envOutput = CliEnvironment.Get(CliEnvironment.OutputFormat);
        if (!string.IsNullOrEmpty(envOutput))
        {
            settings.OutputFormat = ParseOutputFormat(envOutput);
        }

        var envVerbose = CliEnvironment.Get(CliEnvironment.Verbose);
        if (!string.IsNullOrEmpty(envVerbose))
        {
            settings.Verbose = envVerbose.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                               envVerbose == "1";
        }

        var envHistoryFile = CliEnvironment.Get(CliEnvironment.HistoryFile);
        if (!string.IsNullOrEmpty(envHistoryFile))
        {
            settings.HistoryFile = envHistoryFile;
        }

        return settings;
    }

    /// <summary>
    /// Loads settings from a JSON configuration file.
    /// </summary>
    /// <param name="filePath">The path to the configuration file.</param>
    /// <returns>The loaded settings, or null if the file doesn't exist.</returns>
    public static ConnectionSettings? LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        var json = File.ReadAllText(filePath);
        var config = JsonSerializer.Deserialize<ConfigFile>(json, JsonOptions);

        if (config == null)
        {
            return null;
        }

        return new ConnectionSettings
        {
            ServerUrl = config.DefaultServer,
            ApiKey = config.ApiKey,
            UserId = config.UserId ?? Environment.UserName,
            TimeoutSeconds = config.Timeout ?? DefaultTimeoutSeconds,
            OutputFormat = ParseOutputFormat(config.OutputFormat),
            Verbose = false,
            HistoryFile = config.HistoryFile ?? DefaultHistoryFile,
            HistorySize = config.HistorySize ?? 1000,
            Profiles = config.Profiles ?? new Dictionary<string, ServerProfile>(),
            LastSessions = config.LastSessions != null
                ? new Dictionary<string, string>(config.LastSessions, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    /// Saves the current settings to the default config file.
    /// </summary>
    public void Save()
    {
        SaveToFile(DefaultConfigFile);
    }

    /// <summary>
    /// Saves the current settings to a JSON configuration file.
    /// </summary>
    /// <param name="filePath">The path to save the configuration file.</param>
    public void SaveToFile(string filePath)
    {
        // Ensure directory exists
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var config = new ConfigFile
        {
            DefaultServer = ServerUrl,
            ApiKey = ApiKey,
            UserId = UserId != Environment.UserName ? UserId : null,
            Timeout = TimeoutSeconds != DefaultTimeoutSeconds ? TimeoutSeconds : null,
            OutputFormat = OutputFormat != OutputFormat.Text ? OutputFormat.ToString().ToLowerInvariant() : null,
            HistoryFile = HistoryFile != DefaultHistoryFile ? HistoryFile : null,
            HistorySize = HistorySize != 1000 ? HistorySize : null,
            Profiles = Profiles.Count > 0 ? Profiles : null,
            LastSessions = LastSessions.Count > 0 ? LastSessions : null
        };

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Lists all available profiles.
    /// </summary>
    /// <returns>Profile names.</returns>
    public IEnumerable<string> GetProfileNames()
    {
        return Profiles.Keys;
    }

    /// <summary>
    /// Applies a server profile to these settings.
    /// </summary>
    /// <param name="profileName">The profile name.</param>
    /// <returns>True if the profile was found and applied.</returns>
    public bool ApplyProfile(string profileName)
    {
        if (!Profiles.TryGetValue(profileName, out var profile))
        {
            return false;
        }

        ServerUrl = profile.Url;
        ApiKey = profile.ApiKey;

        if (!string.IsNullOrEmpty(profile.UserId))
        {
            UserId = profile.UserId;
        }

        if (profile.TimeoutSeconds.HasValue)
        {
            TimeoutSeconds = profile.TimeoutSeconds.Value;
        }

        return true;
    }

    /// <summary>
    /// Validates the settings for connecting to a server.
    /// </summary>
    /// <returns>A list of validation errors, empty if valid.</returns>
    public List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            errors.Add("Server URL is required. Use 'connect <url>' or set DEBUGGER_MCP_URL.");
        }
        else if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            errors.Add($"Invalid server URL: {ServerUrl}. Must be a valid HTTP(S) URL.");
        }

        if (string.IsNullOrWhiteSpace(UserId))
        {
            errors.Add("User ID is required. Set DEBUGGER_MCP_USER_ID or use --user-id option.");
        }

        if (TimeoutSeconds <= 0)
        {
            errors.Add("Timeout must be a positive number of seconds.");
        }

        return errors;
    }

    /// <summary>
    /// Gets the base URI for API calls.
    /// </summary>
    /// <returns>The base URI or null if not configured.</returns>
    public Uri? GetBaseUri()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            return null;
        }

        return new Uri(ServerUrl.TrimEnd('/') + "/");
    }

    private static OutputFormat ParseOutputFormat(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return OutputFormat.Text;
        }

        return value.ToLowerInvariant() switch
        {
            "json" => OutputFormat.Json,
            "text" => OutputFormat.Text,
            _ => OutputFormat.Text
        };
    }
}

/// <summary>
/// Output format for CLI results.
/// </summary>
public enum OutputFormat
{
    /// <summary>
    /// Human-readable text output with colors and formatting.
    /// </summary>
    Text,

    /// <summary>
    /// Machine-readable JSON output.
    /// </summary>
    Json
}

/// <summary>
/// A saved server profile for quick connection switching.
/// </summary>
public class ServerProfile
{
    /// <summary>
    /// Gets or sets the server URL.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the API key.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the user ID override.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Gets or sets the timeout override in seconds.
    /// </summary>
    public int? TimeoutSeconds { get; set; }
}

/// <summary>
/// JSON configuration file structure for ~/.dbg-mcp/config.json
/// </summary>
internal class ConfigFile
{
    /// <summary>
    /// Default server URL to connect to.
    /// </summary>
    [JsonPropertyName("defaultServer")]
    public string? DefaultServer { get; set; }

    /// <summary>
    /// API key for authentication.
    /// </summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    /// <summary>
    /// User ID for operations.
    /// </summary>
    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    [JsonPropertyName("timeout")]
    public int? Timeout { get; set; }

    /// <summary>
    /// Output format (text or json).
    /// </summary>
    [JsonPropertyName("outputFormat")]
    public string? OutputFormat { get; set; }

    /// <summary>
    /// Theme name.
    /// </summary>
    [JsonPropertyName("theme")]
    public string? Theme { get; set; }

    /// <summary>
    /// Path to command history file.
    /// </summary>
    [JsonPropertyName("historyFile")]
    public string? HistoryFile { get; set; }

    /// <summary>
    /// Maximum number of commands to keep in history.
    /// </summary>
    [JsonPropertyName("historySize")]
    public int? HistorySize { get; set; }

    /// <summary>
    /// Server profiles for quick switching.
    /// </summary>
    [JsonPropertyName("profiles")]
    public Dictionary<string, ServerProfile>? Profiles { get; set; }

    /// <summary>
    /// Last-used session IDs per server/user.
    /// </summary>
    [JsonPropertyName("lastSessions")]
    public Dictionary<string, string>? LastSessions { get; set; }
}
