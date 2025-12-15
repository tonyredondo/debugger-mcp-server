namespace DebuggerMcp.Configuration;

/// <summary>
/// Centralized documentation and access for all environment variable configuration.
/// </summary>
/// <remarks>
/// This class provides a single source of truth for all environment variables used by the debugger MCP server.
/// All configuration values can be overridden via environment variables for deployment flexibility.
/// 
/// <para><b>Quick Reference Table:</b></para>
/// <list type="table">
///   <listheader>
///     <term>Variable</term>
///     <term>Default</term>
///     <term>Description</term>
///   </listheader>
///   <item>
///     <term>DUMP_STORAGE_PATH</term>
///     <term>{TempPath}/WinDbgDumps</term>
///     <term>Directory for uploaded dump files</term>
///   </item>
///   <item>
///     <term>SYMBOL_STORAGE_PATH</term>
///     <term>Platform-specific</term>
///     <term>Directory for symbol cache downloads</term>
///   </item>
///   <item>
///     <term>API_KEY</term>
///     <term>None (disabled)</term>
///     <term>API key for authentication</term>
///   </item>
///   <item>
///     <term>CORS_ALLOWED_ORIGINS</term>
///     <term>* (any)</term>
///     <term>Comma-separated allowed CORS origins</term>
///   </item>
///   <item>
///     <term>RATE_LIMIT_REQUESTS_PER_MINUTE</term>
///     <term>120</term>
///     <term>Maximum requests per minute per IP</term>
///   </item>
///   <item>
///     <term>ENABLE_SWAGGER</term>
///     <term>false (prod)</term>
///     <term>Enable Swagger UI in production</term>
///   </item>
///   <item>
///     <term>MAX_SESSIONS_PER_USER</term>
///     <term>10</term>
///     <term>Maximum concurrent sessions per user</term>
///   </item>
///   <item>
///     <term>MAX_TOTAL_SESSIONS</term>
///     <term>50</term>
///     <term>Maximum total concurrent sessions</term>
///   </item>
///   <item>
///     <term>SESSION_CLEANUP_INTERVAL_MINUTES</term>
///     <term>5</term>
///     <term>Interval between session cleanup runs</term>
///   </item>
///   <item>
///     <term>SESSION_INACTIVITY_THRESHOLD_MINUTES</term>
///     <term>1440 (24 hours)</term>
///     <term>Session inactivity timeout</term>
///   </item>
///   <item>
///     <term>SOS_PLUGIN_PATH</term>
///     <term>Auto-detect</term>
///     <term>Path to SOS plugin for .NET debugging</term>
///   </item>
///   <item>
///     <term>SYMBOL_DOWNLOAD_TIMEOUT_MINUTES</term>
///     <term>10</term>
///     <term>Timeout (minutes) for symbol downloads via dotnet-symbol</term>
///   </item>
///   <item>
///     <term>DOTNET_SYMBOL_TOOL_PATH</term>
///     <term>Auto-detect</term>
///     <term>Optional override path for the dotnet-symbol tool</term>
///   </item>
///   <item>
///     <term>SESSION_STORAGE_PATH</term>
///     <term>/app/sessions</term>
///     <term>Directory for persistent session storage (shared volume)</term>
///   </item>
///   <item>
///     <term>MAX_REQUEST_BODY_SIZE_GB</term>
///     <term>5</term>
///     <term>Maximum dump upload size (GB). Enforced by Kestrel and the upload controller.</term>
///   </item>
///   <item>
///     <term>SKIP_DUMP_ANALYSIS</term>
///     <term>false</term>
///     <term>
///       When true, skips post-upload dump analysis (dotnet-symbol --verifycore and architecture detection).
///       Intended for constrained environments and tests.
///     </term>
///   </item>
///   <item>
///     <term>PORT</term>
///     <term>5000</term>
///     <term>
///       Convenience port value used in startup messages. Actual HTTP binding is controlled by ASP.NET Core (e.g., ASPNETCORE_URLS).
///     </term>
///   </item>
/// </list>
/// </remarks>
public static class EnvironmentConfig
{
    // ========== Storage Configuration ==========

    /// <summary>
    /// Environment variable name for dump storage path.
    /// </summary>
    /// <remarks>
    /// Specifies the directory where uploaded dump files are stored.
    /// Dump files are organized by user ID: {DUMP_STORAGE_PATH}/{userId}/{dumpId}.dmp
    /// </remarks>
    public const string DumpStoragePath = "DUMP_STORAGE_PATH";

    /// <summary>
    /// Default dump storage path when environment variable is not set.
    /// </summary>
    public static string DefaultDumpStoragePath => Path.Combine(Path.GetTempPath(), "WinDbgDumps");

    /// <summary>
    /// Environment variable name for symbol storage path.
    /// </summary>
    /// <remarks>
    /// Specifies the directory where symbol cache files are stored.
    /// This is used as the cache root for remote symbol servers.
    /// </remarks>
    public const string SymbolStoragePath = "SYMBOL_STORAGE_PATH";

    /// <summary>
    /// Gets the default symbol cache path based on the current platform.
    /// </summary>
    /// <returns>
    /// Windows: %LOCALAPPDATA%\DebuggerMcp\symbols
    /// Linux/macOS: ~/.debuggermcp/symbols
    /// </returns>
    public static string DefaultSymbolStoragePath =>
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DebuggerMcp", "symbols")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".debuggermcp", "symbols");

    // ========== Security Configuration ==========

    /// <summary>
    /// Environment variable name for API key authentication.
    /// </summary>
    /// <remarks>
    /// When set, all HTTP API requests must include the X-API-Key header with this value.
    /// When not set, authentication is disabled (useful for development).
    /// </remarks>
    public const string ApiKey = "API_KEY";

    /// <summary>
    /// Environment variable name for CORS allowed origins.
    /// </summary>
    /// <remarks>
    /// Comma-separated list of allowed origins for CORS.
    /// Example: "https://app.example.com,https://admin.example.com"
    /// When not set, all origins are allowed (development mode).
    /// </remarks>
    public const string CorsAllowedOrigins = "CORS_ALLOWED_ORIGINS";

    // ========== Rate Limiting Configuration ==========

    /// <summary>
    /// Environment variable name for rate limit configuration.
    /// </summary>
    /// <remarks>
    /// Maximum number of requests allowed per minute per IP address.
    /// Requests exceeding this limit will receive HTTP 429 (Too Many Requests).
    /// </remarks>
    public const string RateLimitRequestsPerMinute = "RATE_LIMIT_REQUESTS_PER_MINUTE";

    /// <summary>
    /// Default rate limit when environment variable is not set.
    /// </summary>
    public const int DefaultRateLimitRequestsPerMinute = 120;

    // ========== UI Configuration ==========

    /// <summary>
    /// Environment variable name for enabling Swagger UI.
    /// </summary>
    /// <remarks>
    /// Set to "true" to enable Swagger UI in production.
    /// Swagger UI is always enabled in development mode regardless of this setting.
    /// </remarks>
    public const string EnableSwagger = "ENABLE_SWAGGER";

    // ========== Session Configuration ==========

    /// <summary>
    /// Environment variable name for maximum sessions per user.
    /// </summary>
    /// <remarks>
    /// Limits the number of concurrent debugging sessions a single user can have.
    /// This prevents resource exhaustion from a single user.
    /// </remarks>
    public const string MaxSessionsPerUser = "MAX_SESSIONS_PER_USER";

    /// <summary>
    /// Default maximum sessions per user when environment variable is not set.
    /// </summary>
    public const int DefaultMaxSessionsPerUser = 10;

    /// <summary>
    /// Environment variable name for maximum total sessions.
    /// </summary>
    /// <remarks>
    /// Limits the total number of concurrent debugging sessions across all users.
    /// This prevents system-wide resource exhaustion.
    /// </remarks>
    public const string MaxTotalSessions = "MAX_TOTAL_SESSIONS";

    /// <summary>
    /// Default maximum total sessions when environment variable is not set.
    /// </summary>
    public const int DefaultMaxTotalSessions = 50;

    /// <summary>
    /// Environment variable name for session cleanup interval.
    /// </summary>
    /// <remarks>
    /// Specifies how often (in minutes) the cleanup service runs to remove inactive sessions.
    /// </remarks>
    public const string SessionCleanupIntervalMinutes = "SESSION_CLEANUP_INTERVAL_MINUTES";

    /// <summary>
    /// Default cleanup interval when environment variable is not set.
    /// </summary>
    public const int DefaultSessionCleanupIntervalMinutes = 5;

    /// <summary>
    /// Environment variable name for session inactivity threshold.
    /// </summary>
    /// <remarks>
    /// Sessions that have been inactive for longer than this duration (in minutes) will be cleaned up.
    /// </remarks>
    public const string SessionInactivityThresholdMinutes = "SESSION_INACTIVITY_THRESHOLD_MINUTES";

    /// <summary>
    /// Default inactivity threshold when environment variable is not set.
    /// </summary>
    /// <remarks>
    /// Default is 24 hours (1440 minutes) to support long-running debugging sessions
    /// and allow sessions to persist across server restarts.
    /// </remarks>
    public const int DefaultSessionInactivityThresholdMinutes = 1440;

    /// <summary>
    /// Environment variable name for session storage path.
    /// </summary>
    /// <remarks>
    /// Specifies the directory where session metadata files are persisted.
    /// This should be a shared volume when running multiple server instances
    /// to enable session sharing across servers.
    /// </remarks>
    public const string SessionStoragePath = "SESSION_STORAGE_PATH";

    /// <summary>
    /// Environment variable name for skipping post-upload dump analysis.
    /// </summary>
    /// <remarks>
    /// When set to "true", the server will skip running external tools (dotnet-symbol and file)
    /// after a dump upload. This can speed up tests and avoid timeouts in environments where
    /// those tools are not available.
    /// </remarks>
    public const string SkipDumpAnalysis = "SKIP_DUMP_ANALYSIS";

    /// <summary>
    /// Gets the default session storage path based on the current platform.
    /// </summary>
    /// <returns>
    /// Container: /app/sessions
    /// Windows: %LOCALAPPDATA%\DebuggerMcp\sessions
    /// Linux/macOS: ~/.debuggermcp/sessions
    /// </returns>
    public static string DefaultSessionStoragePath =>
        Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
            ? "/app/sessions"
            : System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DebuggerMcp", "sessions")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".debuggermcp", "sessions");

    // ========== Debugger Configuration ==========

    /// <summary>
    /// Environment variable name for SOS plugin path.
    /// </summary>
    /// <remarks>
    /// Specifies the full path to the SOS plugin (libsosplugin.so or libsosplugin.dylib).
    /// When not set, the system will attempt to auto-detect the SOS plugin location.
    /// </remarks>
    public const string SosPluginPath = "SOS_PLUGIN_PATH";

    /// <summary>
    /// Environment variable name for overriding the dotnet-symbol tool path.
    /// </summary>
    /// <remarks>
    /// When set, this path is used instead of auto-discovery (useful for containers, custom installs, and tests).
    /// </remarks>
    public const string DotnetSymbolToolPath = "DOTNET_SYMBOL_TOOL_PATH";

    /// <summary>
    /// Environment variable name for symbol download timeout in minutes.
    /// </summary>
    /// <remarks>
    /// The dotnet-symbol tool can take a long time to download symbols for large dumps.
    /// This timeout controls how long to wait before killing the process.
    /// Default is 10 minutes which should be sufficient for most dumps.
    /// </remarks>
    public const string SymbolDownloadTimeoutMinutes = "SYMBOL_DOWNLOAD_TIMEOUT_MINUTES";

    /// <summary>
    /// Default symbol download timeout in minutes.
    /// </summary>
    public const int DefaultSymbolDownloadTimeoutMinutes = 10;

    // ========== Server Configuration ==========

    /// <summary>
    /// Environment variable name for HTTP server port.
    /// </summary>
    /// <remarks>
    /// The port on which the HTTP server will listen.
    /// Only used in HTTP mode (--http or --mcp-http).
    /// </remarks>
    public const string Port = "PORT";

    /// <summary>
    /// Default HTTP server port when environment variable is not set.
    /// </summary>
    public const int DefaultPort = 5000;

    /// <summary>
    /// Environment variable name for maximum request body size.
    /// </summary>
    /// <remarks>
    /// Specifies the maximum size (in GB) for uploaded dump files.
    /// This limit is enforced by Kestrel at the HTTP level.
    /// </remarks>
    public const string MaxRequestBodySizeGb = "MAX_REQUEST_BODY_SIZE_GB";

    /// <summary>
    /// Default maximum request body size in GB when environment variable is not set.
    /// </summary>
    public const int DefaultMaxRequestBodySizeGb = 5;

    // ========== Helper Methods ==========

    /// <summary>
    /// Gets a string environment variable value with a default fallback.
    /// </summary>
    /// <param name="name">The environment variable name.</param>
    /// <param name="defaultValue">The default value if the variable is not set.</param>
    /// <returns>The environment variable value or the default.</returns>
    public static string GetString(string name, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? defaultValue : value;
    }

    /// <summary>
    /// Gets an integer environment variable value with a default fallback.
    /// </summary>
    /// <param name="name">The environment variable name.</param>
    /// <param name="defaultValue">The default value if the variable is not set or invalid.</param>
    /// <returns>The environment variable value or the default.</returns>
    public static int GetInt(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
        {
            return defaultValue;
        }

        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;
    }

    /// <summary>
    /// Gets a boolean environment variable value with a default fallback.
    /// </summary>
    /// <param name="name">The environment variable name.</param>
    /// <param name="defaultValue">The default value if the variable is not set.</param>
    /// <returns>True if the value is "true" (case-insensitive), false otherwise.</returns>
    public static bool GetBool(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
        {
            return defaultValue;
        }

        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the configured dump storage path.
    /// </summary>
    /// <returns>The dump storage path from environment or default.</returns>
    public static string GetDumpStoragePath() => GetString(DumpStoragePath, DefaultDumpStoragePath);

    /// <summary>
    /// Gets the configured symbol storage path.
    /// </summary>
    /// <returns>The symbol storage path from environment or platform default.</returns>
    public static string GetSymbolStoragePath() => GetString(SymbolStoragePath, DefaultSymbolStoragePath);

    /// <summary>
    /// Gets the configured rate limit.
    /// </summary>
    /// <returns>The rate limit from environment or default.</returns>
    public static int GetRateLimit() => GetInt(RateLimitRequestsPerMinute, DefaultRateLimitRequestsPerMinute);

    /// <summary>
    /// Gets the configured maximum sessions per user.
    /// </summary>
    /// <returns>The max sessions per user from environment or default.</returns>
    public static int GetMaxSessionsPerUser() => GetInt(MaxSessionsPerUser, DefaultMaxSessionsPerUser);

    /// <summary>
    /// Gets the configured maximum total sessions.
    /// </summary>
    /// <returns>The max total sessions from environment or default.</returns>
    public static int GetMaxTotalSessions() => GetInt(MaxTotalSessions, DefaultMaxTotalSessions);

    /// <summary>
    /// Gets the configured session cleanup interval.
    /// </summary>
    /// <returns>The cleanup interval from environment or default.</returns>
    public static TimeSpan GetSessionCleanupInterval() =>
        TimeSpan.FromMinutes(GetInt(SessionCleanupIntervalMinutes, DefaultSessionCleanupIntervalMinutes));

    /// <summary>
    /// Gets the configured session inactivity threshold.
    /// </summary>
    /// <returns>The inactivity threshold from environment or default (24 hours).</returns>
    public static TimeSpan GetSessionInactivityThreshold() =>
        TimeSpan.FromMinutes(GetInt(SessionInactivityThresholdMinutes, DefaultSessionInactivityThresholdMinutes));

    /// <summary>
    /// Gets the configured session storage path.
    /// </summary>
    /// <returns>The session storage path from environment or default.</returns>
    public static string GetSessionStoragePath() => GetString(SessionStoragePath, DefaultSessionStoragePath);

    /// <summary>
    /// Checks if Swagger UI should be enabled.
    /// </summary>
    /// <returns>True if Swagger should be enabled.</returns>
    public static bool IsSwaggerEnabled() => GetBool(EnableSwagger, false);

    /// <summary>
    /// Checks whether post-upload dump analysis should be skipped.
    /// </summary>
    /// <returns>True when dump analysis should be skipped.</returns>
    public static bool IsDumpAnalysisSkipped() => GetBool(SkipDumpAnalysis, false);

    /// <summary>
    /// Gets the configured API key, if any.
    /// </summary>
    /// <returns>The API key or null if not configured.</returns>
    public static string? GetApiKey() => Environment.GetEnvironmentVariable(ApiKey);

    /// <summary>
    /// Gets the configured CORS allowed origins.
    /// </summary>
    /// <returns>Array of allowed origins, or empty array if not configured (allow all).</returns>
    public static string[] GetCorsAllowedOrigins()
    {
        var origins = Environment.GetEnvironmentVariable(CorsAllowedOrigins);
        if (string.IsNullOrEmpty(origins))
        {
            return Array.Empty<string>();
        }

        return origins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Gets the configured SOS plugin path, if any.
    /// </summary>
    /// <returns>The SOS plugin path or null for auto-detection.</returns>
    public static string? GetSosPluginPath() => Environment.GetEnvironmentVariable(SosPluginPath);

    /// <summary>
    /// Gets the configured dotnet-symbol tool path override, if any.
    /// </summary>
    /// <returns>The dotnet-symbol tool path override, or null to auto-detect.</returns>
    public static string? GetDotnetSymbolToolPath() => Environment.GetEnvironmentVariable(DotnetSymbolToolPath);

    /// <summary>
    /// Gets the configured symbol download timeout in minutes.
    /// </summary>
    /// <returns>The symbol download timeout from environment or default (10 minutes).</returns>
    public static int GetSymbolDownloadTimeoutMinutes() => GetInt(SymbolDownloadTimeoutMinutes, DefaultSymbolDownloadTimeoutMinutes);

    /// <summary>
    /// Gets the configured HTTP server port.
    /// </summary>
    /// <returns>The port from environment or default (5000).</returns>
    public static int GetPort() => GetInt(Port, DefaultPort);

    /// <summary>
    /// Gets the configured maximum request body size in bytes.
    /// </summary>
    /// <returns>The max request body size from environment or default (5GB).</returns>
    public static long GetMaxRequestBodySize()
    {
        var sizeGb = GetInt(MaxRequestBodySizeGb, DefaultMaxRequestBodySizeGb);
        return (long)sizeGb * 1024 * 1024 * 1024;
    }
}
