namespace DebuggerMcp.Cli.Configuration;

/// <summary>
/// Defines environment variable names used by the CLI.
/// </summary>
/// <remarks>
/// All environment variables are prefixed with DEBUGGER_MCP_ for namespacing.
/// These can be used to configure default values without command-line arguments.
/// </remarks>
public static class CliEnvironment
{
    /// <summary>
    /// Environment variable prefix for all CLI settings.
    /// </summary>
    public const string Prefix = "DEBUGGER_MCP_";

    /// <summary>
    /// Default server URL to connect to.
    /// Example: http://localhost:5000
    /// </summary>
    public const string ServerUrl = "DEBUGGER_MCP_URL";

    /// <summary>
    /// API key for server authentication.
    /// </summary>
    public const string ApiKey = "DEBUGGER_MCP_API_KEY";

    /// <summary>
    /// Default user ID for operations.
    /// Defaults to current system username if not set.
    /// </summary>
    public const string UserId = "DEBUGGER_MCP_USER_ID";

    /// <summary>
    /// Request timeout in seconds.
    /// Default: 300 (5 minutes)
    /// </summary>
    public const string Timeout = "DEBUGGER_MCP_TIMEOUT";

    /// <summary>
    /// Default output format: text or json.
    /// Default: text
    /// </summary>
    public const string OutputFormat = "DEBUGGER_MCP_OUTPUT";

    /// <summary>
    /// Path to command history file.
    /// Default: ~/.dbg-mcp-history
    /// </summary>
    public const string HistoryFile = "DEBUGGER_MCP_HISTORY_FILE";

    /// <summary>
    /// Enable verbose output for debugging.
    /// Default: false
    /// </summary>
    public const string Verbose = "DEBUGGER_MCP_VERBOSE";

    /// <summary>
    /// Path to configuration file.
    /// Default: ~/.dbg-mcp/config.json
    /// </summary>
    public const string ConfigFile = "DEBUGGER_MCP_CONFIG";

    /// <summary>
    /// Gets an environment variable value.
    /// </summary>
    /// <param name="name">The environment variable name (without prefix).</param>
    /// <returns>The value or null if not set.</returns>
    public static string? Get(string name) =>
        Environment.GetEnvironmentVariable(name);

    /// <summary>
    /// Gets an environment variable value with a default.
    /// </summary>
    /// <param name="name">The environment variable name.</param>
    /// <param name="defaultValue">The default value if not set.</param>
    /// <returns>The value or the default.</returns>
    public static string GetOrDefault(string name, string defaultValue) =>
        Environment.GetEnvironmentVariable(name) ?? defaultValue;

    /// <summary>
    /// Gets an integer environment variable value with a default.
    /// </summary>
    /// <param name="name">The environment variable name.</param>
    /// <param name="defaultValue">The default value if not set or invalid.</param>
    /// <returns>The parsed value or the default.</returns>
    public static int GetIntOrDefault(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var result) ? result : defaultValue;
    }

    /// <summary>
    /// Gets a boolean environment variable value with a default.
    /// </summary>
    /// <param name="name">The environment variable name.</param>
    /// <param name="defaultValue">The default value if not set.</param>
    /// <returns>True if set to "true", "1", or "yes" (case-insensitive).</returns>
    public static bool GetBoolOrDefault(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
        {
            return defaultValue;
        }

        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.Ordinal) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}

