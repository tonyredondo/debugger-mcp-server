#nullable enable

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DebuggerMcp.Cli.Configuration;

namespace DebuggerMcp.Cli.Analysis;

/// <summary>
/// Stable cache key for an <c>analyze ai</c> result, based on the dump id and effective LLM settings.
/// </summary>
internal sealed record AiAnalysisCacheKey(
    string DumpId,
    string Provider,
    string Model,
    string ReasoningEffort)
{
    /// <summary>
    /// Creates a cache key for a dump and effective LLM settings.
    /// </summary>
    public static AiAnalysisCacheKey Create(string dumpId, LlmSettings llmSettings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpId);
        ArgumentNullException.ThrowIfNull(llmSettings);

        var provider = LlmSettings.NormalizeProvider(llmSettings.Provider);
        var model = llmSettings.GetEffectiveModel();
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "(unknown)";
        }

        var reasoningEffort = LlmSettings.NormalizeReasoningEffort(llmSettings.GetEffectiveReasoningEffort()) ?? "default";

        return new AiAnalysisCacheKey(dumpId, provider, model, reasoningEffort);
    }

    /// <summary>
    /// Gets a stable, reproducible string representation of this key for hashing.
    /// </summary>
    public string ToStableKey() => $"{DumpId}|{Provider}|{Model}|{ReasoningEffort}";
}

/// <summary>
/// Stores and retrieves <c>analyze ai</c> results on disk, keyed by dump id and effective LLM configuration.
/// </summary>
internal sealed class AiAnalysisCache
{
    /// <summary>
    /// Environment variable that overrides the cache root directory.
    /// </summary>
    internal const string CacheDirEnvVar = "DEBUGGER_MCP_AI_ANALYSIS_CACHE_DIR";

    /// <summary>
    /// Short/legacy environment variable that overrides the cache root directory.
    /// </summary>
    internal const string CacheDirEnvVarShort = "DBG_MCP_AI_ANALYSIS_CACHE_DIR";

    private readonly string _rootDirectory;

    /// <summary>
    /// Creates a cache rooted at the given directory.
    /// </summary>
    public AiAnalysisCache(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    /// <summary>
    /// Creates a cache using the default root (optionally overridden via environment variables).
    /// </summary>
    public static AiAnalysisCache CreateDefault()
    {
        var root =
            Environment.GetEnvironmentVariable(CacheDirEnvVar) ??
            Environment.GetEnvironmentVariable(CacheDirEnvVarShort) ??
            Path.Combine(ConnectionSettings.DefaultConfigDirectory, "cache");

        return new AiAnalysisCache(root);
    }

    /// <summary>
    /// Gets the on-disk cache file path for the given key.
    /// </summary>
    public string GetCacheFilePath(AiAnalysisCacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var stableId = ComputeStableId(key.ToStableKey());
        var hash = stableId[..12];

        var dumpSegment = SanitizePathSegment(key.DumpId, maxLength: 80);
        var providerSegment = SanitizePathSegment(key.Provider, maxLength: 30);
        var modelSegment = SanitizePathSegment(key.Model, maxLength: 100);
        if (modelSegment.Length >= 96)
        {
            modelSegment = modelSegment[..96] + "-" + hash;
        }

        var effortSegment = SanitizePathSegment(key.ReasoningEffort, maxLength: 30);
        var dir = Path.Combine(_rootDirectory, "ai-analysis", dumpSegment, providerSegment, modelSegment);
        return Path.Combine(dir, $"{effortSegment}-{hash}.json");
    }

    /// <summary>
    /// Attempts to read a cached analysis result. Returns <c>null</c> if not found or invalid.
    /// </summary>
    public async Task<string?> TryReadAsync(AiAnalysisCacheKey key, CancellationToken cancellationToken = default)
    {
        var path = GetCacheFilePath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using var _ = JsonDocument.Parse(json);
            return json;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes an analysis result to the cache (atomic write).
    /// </summary>
    public async Task WriteAsync(AiAnalysisCacheKey key, string json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);

        var path = GetCacheFilePath(key);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmpPath = path + ".tmp";
        await File.WriteAllTextAsync(tmpPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        try
        {
            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }
            }
            catch
            {
                // Best effort.
            }
            throw;
        }
    }

    private static string ComputeStableId(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SanitizePathSegment(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var trimmed = value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var chars = trimmed.Select(c =>
            invalid.Contains(c) ||
            c == Path.DirectorySeparatorChar ||
            c == Path.AltDirectorySeparatorChar ||
            c is ':' or '|' or '"' or '<' or '>' or '?' or '*'
                ? '_'
                : c).ToArray();

        var sanitized = new string(chars);
        if (sanitized is "." or "..")
        {
            sanitized = sanitized.Replace('.', '_');
        }
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "unknown";
        }
        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized[..maxLength];
        }

        return sanitized;
    }
}
