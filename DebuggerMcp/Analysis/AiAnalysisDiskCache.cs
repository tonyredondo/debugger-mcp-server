#nullable enable

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using DebuggerMcp.Serialization;
using DebuggerMcp.Security;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Persists AI-enriched canonical JSON reports to disk so they can be reused by server-side report generation
/// without rerunning MCP sampling.
/// </summary>
internal sealed class AiAnalysisDiskCache
{
    private const int CurrentSchemaVersion = 1;
    private const string CacheDirectoryName = "ai-analysis";
    private const string KeyedDirectoryName = "by-llm";
    private const string ReportFileName = "report.json";
    private const string MetadataFileName = "report.meta.json";

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dumpStoragePath;
    private readonly ILogger<AiAnalysisDiskCache> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public AiAnalysisDiskCache(string dumpStoragePath, ILogger<AiAnalysisDiskCache> logger)
    {
        if (string.IsNullOrWhiteSpace(dumpStoragePath))
        {
            throw new ArgumentException("dumpStoragePath cannot be null or empty", nameof(dumpStoragePath));
        }

        _dumpStoragePath = dumpStoragePath;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AiAnalysisDiskCacheEntry?> TryReadAsync(
        string userId,
        string dumpId,
        AiAnalysisDiskCacheLlmKey? llmKey,
        bool requireWatches,
        bool requireSecurity,
        bool requireAllFrames,
        CancellationToken cancellationToken = default)
    {
        var sanitizedUserId = PathSanitizer.SanitizeIdentifier(userId, nameof(userId));
        var sanitizedDumpId = PathSanitizer.SanitizeIdentifier(dumpId, nameof(dumpId));

        var cacheKey = GetCacheKey(sanitizedUserId, sanitizedDumpId);
        var gate = _locks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reportPath = GetReportFilePath(sanitizedUserId, sanitizedDumpId, llmKey);
            var metadataPath = GetMetadataFilePath(sanitizedUserId, sanitizedDumpId, llmKey);

            if (!File.Exists(reportPath) || !File.Exists(metadataPath))
            {
                return null;
            }

            AiAnalysisDiskCacheMetadata? metadata;
            try
            {
                var metadataJson = await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false);
                metadata = JsonSerializer.Deserialize<AiAnalysisDiskCacheMetadata>(metadataJson, MetadataJsonOptions);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogWarning(ex, "[AI Cache] Failed to read cache metadata: {MetadataPath}", metadataPath);
                return null;
            }

            if (metadata == null ||
                metadata.SchemaVersion != CurrentSchemaVersion ||
                !string.Equals(metadata.DumpId, sanitizedDumpId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(metadata.UserId, sanitizedUserId, StringComparison.OrdinalIgnoreCase) ||
                (llmKey != null && !metadata.MatchesLlmKey(llmKey)) ||
                (requireWatches && !metadata.IncludesWatches) ||
                (requireSecurity && !metadata.IncludesSecurity) ||
                (requireAllFrames && metadata.MaxStackFrames != 0) ||
                !metadata.IncludesAiAnalysis)
            {
                return null;
            }

            try
            {
                var reportJson = await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(reportJson))
                {
                    return null;
                }

                return new AiAnalysisDiskCacheEntry(metadata, reportJson);
            }
            catch (Exception ex) when (ex is IOException)
            {
                _logger.LogWarning(ex, "[AI Cache] Failed to read cached report: {ReportPath}", reportPath);
                return null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task WriteAsync(
        string userId,
        string dumpId,
        AiAnalysisDiskCacheLlmKey? llmKey,
        AiAnalysisDiskCacheMetadata metadata,
        string reportJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var sanitizedUserId = PathSanitizer.SanitizeIdentifier(userId, nameof(userId));
        var sanitizedDumpId = PathSanitizer.SanitizeIdentifier(dumpId, nameof(dumpId));

        if (string.IsNullOrWhiteSpace(reportJson))
        {
            throw new ArgumentException("reportJson cannot be null or empty", nameof(reportJson));
        }

        var cacheKey = GetCacheKey(sanitizedUserId, sanitizedDumpId);
        var gate = _locks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteSingleAsync(sanitizedUserId, sanitizedDumpId, llmKey, metadata, reportJson, cancellationToken).ConfigureAwait(false);

            // Also write a "latest" pointer (unkeyed) so server-side report generation can reuse the most
            // recently produced AI-enriched report JSON without needing LLM settings.
            if (llmKey != null)
            {
                await WriteSingleAsync(sanitizedUserId, sanitizedDumpId, llmKey: null, metadata, reportJson, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WriteSingleAsync(
        string sanitizedUserId,
        string sanitizedDumpId,
        AiAnalysisDiskCacheLlmKey? llmKey,
        AiAnalysisDiskCacheMetadata metadata,
        string reportJson,
        CancellationToken cancellationToken)
    {
        var directory = GetCacheDirectoryPath(sanitizedUserId, sanitizedDumpId, llmKey);
        Directory.CreateDirectory(directory);

        var reportPath = GetReportFilePath(sanitizedUserId, sanitizedDumpId, llmKey);
        var metadataPath = GetMetadataFilePath(sanitizedUserId, sanitizedDumpId, llmKey);

        var normalizedMetadata = metadata with
        {
            SchemaVersion = CurrentSchemaVersion,
            DumpId = sanitizedDumpId,
            UserId = sanitizedUserId,
            LlmProvider = llmKey?.Provider,
            LlmModel = llmKey?.Model,
            LlmReasoningEffort = llmKey?.ReasoningEffort
        };

        await File.WriteAllTextAsync(reportPath, reportJson, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(normalizedMetadata, JsonSerializationDefaults.IndentedCamelCaseIgnoreNull),
            cancellationToken).ConfigureAwait(false);
    }

    private static string GetCacheKey(string userId, string dumpId) => $"{userId}/{dumpId}";

    private string GetCacheDirectoryPath(string userId, string dumpId, AiAnalysisDiskCacheLlmKey? llmKey)
    {
        var baseDir = Path.Combine(_dumpStoragePath, userId, dumpId, CacheDirectoryName);
        if (llmKey == null)
        {
            return baseDir;
        }

        var hash = ComputeStableId($"{llmKey.Provider}|{llmKey.Model}|{llmKey.ReasoningEffort}")[..12];
        var providerSegment = SanitizePathSegment(llmKey.Provider, maxLength: 30);
        var modelSegment = SanitizePathSegment(llmKey.Model, maxLength: 100);
        if (modelSegment.Length >= 96)
        {
            modelSegment = modelSegment[..96] + "-" + hash;
        }
        var effortSegment = SanitizePathSegment(llmKey.ReasoningEffort, maxLength: 30);

        return Path.Combine(baseDir, KeyedDirectoryName, providerSegment, modelSegment, $"{effortSegment}-{hash}");
    }

    private string GetReportFilePath(string userId, string dumpId, AiAnalysisDiskCacheLlmKey? llmKey)
        => Path.Combine(GetCacheDirectoryPath(userId, dumpId, llmKey), ReportFileName);

    private string GetMetadataFilePath(string userId, string dumpId, AiAnalysisDiskCacheLlmKey? llmKey)
        => Path.Combine(GetCacheDirectoryPath(userId, dumpId, llmKey), MetadataFileName);

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

internal sealed record AiAnalysisDiskCacheEntry(AiAnalysisDiskCacheMetadata Metadata, string ReportJson);

internal sealed record AiAnalysisDiskCacheLlmKey(string Provider, string Model, string ReasoningEffort)
{
    public static AiAnalysisDiskCacheLlmKey? TryCreate(string? provider, string? model, string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var normalizedProvider = provider.Trim().ToLowerInvariant();
        var normalizedModel = model.Trim();
        var normalizedEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? "default" : reasoningEffort.Trim().ToLowerInvariant();

        return new AiAnalysisDiskCacheLlmKey(normalizedProvider, normalizedModel, normalizedEffort);
    }
}

internal sealed record AiAnalysisDiskCacheMetadata
{
    public int SchemaVersion { get; init; } = 0;
    public string DumpId { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public DateTime GeneratedAtUtc { get; init; }
    public bool IncludesWatches { get; init; }
    public bool IncludesSecurity { get; init; }
    public int MaxStackFrames { get; init; }
    public bool IncludesAiAnalysis { get; init; }
    public string? Model { get; init; }
    public string? LlmProvider { get; init; }
    public string? LlmModel { get; init; }
    public string? LlmReasoningEffort { get; init; }

    public bool MatchesLlmKey(AiAnalysisDiskCacheLlmKey llmKey)
    {
        ArgumentNullException.ThrowIfNull(llmKey);

        if (string.IsNullOrWhiteSpace(LlmProvider) ||
            string.IsNullOrWhiteSpace(LlmModel) ||
            string.IsNullOrWhiteSpace(LlmReasoningEffort))
        {
            return false;
        }

        return string.Equals(LlmProvider.Trim(), llmKey.Provider, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(LlmModel.Trim(), llmKey.Model, StringComparison.Ordinal) &&
               string.Equals(LlmReasoningEffort.Trim(), llmKey.ReasoningEffort, StringComparison.OrdinalIgnoreCase);
    }
}
