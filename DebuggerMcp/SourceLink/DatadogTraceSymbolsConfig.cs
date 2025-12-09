using DebuggerMcp.Configuration;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Configuration for Datadog.Trace symbol download from Azure Pipelines.
/// </summary>
public static class DatadogTraceSymbolsConfig
{
    /// <summary>
    /// Azure DevOps organization for Datadog builds.
    /// </summary>
    public const string AzureDevOpsOrganization = "datadoghq";

    /// <summary>
    /// Azure DevOps project for dd-trace-dotnet.
    /// </summary>
    public const string AzureDevOpsProject = "dd-trace-dotnet";

    /// <summary>
    /// Azure DevOps API version.
    /// </summary>
    public const string ApiVersion = "7.1";

    /// <summary>
    /// Base URL for Azure DevOps API.
    /// </summary>
    public const string AzureDevOpsBaseUrl = "https://dev.azure.com";

    /// <summary>
    /// Gets the Personal Access Token for Azure DevOps API (optional).
    /// Public artifacts don't require authentication.
    /// </summary>
    public static string? GetPatToken()
        => Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_PAT");

    /// <summary>
    /// Whether Datadog symbol download is enabled.
    /// Default is true - set DATADOG_TRACE_SYMBOLS_ENABLED=false to disable.
    /// </summary>
    public static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED");
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the cache directory for build metadata and downloaded symbols.
    /// </summary>
    public static string GetCacheDirectory()
    {
        var custom = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_CACHE_DIR");
        if (!string.IsNullOrEmpty(custom))
            return custom;

        var dumpStorage = EnvironmentConfig.GetDumpStoragePath();
        return Path.Combine(dumpStorage, ".datadog_symbols_cache");
    }

    /// <summary>
    /// Gets the timeout in seconds for API calls and downloads.
    /// Default is 120 seconds.
    /// </summary>
    public static int GetTimeoutSeconds()
    {
        var value = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_TIMEOUT_SECONDS");
        return int.TryParse(value, out var timeout) ? timeout : 120;
    }

    /// <summary>
    /// Gets the maximum artifact size in bytes (500 MB default).
    /// </summary>
    public static long GetMaxArtifactSize()
    {
        var value = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_MAX_ARTIFACT_SIZE");
        return long.TryParse(value, out var size) ? size : 500 * 1024 * 1024;
    }

    /// <summary>
    /// Gets a short version of a commit SHA for logging (first 8 chars).
    /// Safely handles short strings.
    /// </summary>
    /// <param name="commitSha">The full commit SHA.</param>
    /// <returns>Short SHA (up to 8 characters).</returns>
    public static string GetShortSha(string? commitSha)
    {
        if (string.IsNullOrEmpty(commitSha))
            return "(unknown)";

        return commitSha.Length >= 8 ? commitSha[..8] : commitSha;
    }
}

