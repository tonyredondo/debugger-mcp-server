#nullable enable

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DebuggerMcp.Serialization;

namespace DebuggerMcp.Reporting;

/// <summary>
/// Provides section-addressable access to the canonical JSON report document.
/// </summary>
/// <remarks>
/// The canonical report document is shaped as <c>{ "metadata": { ... }, "analysis": { ... } }</c>.
/// This helper allows callers (MCP tools, LLM sampling, CLI agent) to fetch small, specific subtrees
/// by a stable dot-path (e.g. <c>analysis.exception</c>, <c>analysis.threads.all</c>) with paging for arrays.
/// </remarks>
internal static class ReportSectionApi
{
    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 200;
    internal const int DefaultMaxResponseChars = 20_000;
    private const int MaxPathChars = 512;
    private const int MaxCursorChars = 4096;

    private static readonly JsonSerializerOptions CompactCamelCaseIgnoreNull = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions CompactCamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static string BuildIndex(string reportJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJson);

        using var doc = JsonDocument.Parse(reportJson);
        var root = doc.RootElement;

        var metadata = root.TryGetProperty("metadata", out var metaEl) && metaEl.ValueKind == JsonValueKind.Object
            ? metaEl
            : default;

        var analysis = root.TryGetProperty("analysis", out var analysisEl) && analysisEl.ValueKind == JsonValueKind.Object
            ? analysisEl
            : default;

        var summary = TryBuildSummary(analysis);
        var toc = BuildToc(analysis);

        JsonElement? metadataValue = metadata.ValueKind == JsonValueKind.Undefined ? null : metadata;

        var index = new
        {
            metadata = metadataValue,
            summary,
            toc,
            howToExpand = new[]
            {
                "Sampling/agent: report_get(path=\"analysis.exception\"); MCP: report(action=\"get\", path=\"analysis.exception\")",
                "Sampling/agent: report_get(path=\"analysis.threads.faultingThread\"); MCP: report(action=\"get\", path=\"analysis.threads.faultingThread\")",
                "Sampling/agent: report_get(path=\"analysis.threads.all\", limit=25, cursor=null); MCP: report(action=\"get\", path=\"analysis.threads.all\", limit=25)",
                "Sampling/agent: report_get(path=\"analysis.assemblies.items\", limit=50, cursor=null); MCP: report(action=\"get\", path=\"analysis.assemblies.items\", limit=50)"
            }
        };

        return JsonSerializer.Serialize(index, JsonSerializationDefaults.IndentedIgnoreNull);
    }

    internal static string GetSection(
        string reportJson,
        string path,
        int? limit,
        string? cursor,
        int? maxChars)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var maxResponseChars = Math.Clamp(maxChars ?? DefaultMaxResponseChars, 1_000, 2_000_000);
        if (path.Length > MaxPathChars)
        {
            return SerializeError(path, "invalid_path", $"Path exceeds maximum length ({MaxPathChars}).", maxResponseChars);
        }

        using var doc = JsonDocument.Parse(reportJson);
        var root = doc.RootElement;

        if (!TryResolveDotPath(root, path, out var value, out var resolveError))
        {
            return SerializeError(path, "invalid_path", resolveError ?? $"Path '{path}' does not exist.", maxResponseChars);
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var page = ResolvePage(path, value.GetArrayLength(), limit, cursor, out var pageError);
            if (pageError != null)
            {
                return SerializeError(path, "invalid_cursor", pageError, maxResponseChars);
            }

            var items = new List<JsonElement>(capacity: page.Limit);
            var endExclusive = Math.Min(value.GetArrayLength(), page.Offset + page.Limit);
            for (var i = page.Offset; i < endExclusive; i++)
            {
                items.Add(value[i].Clone());
            }

            var nextCursor = endExclusive < value.GetArrayLength()
                ? EncodeCursor(new ReportCursor(path, endExclusive, page.Limit))
                : null;

            var response = new
            {
                path,
                value = items,
                page = new
                {
                    limit = page.Limit,
                    nextCursor
                }
            };

            return SerializeBounded(response, maxResponseChars, path);
        }

        var responseObj = new
        {
            path,
            value
        };

        return SerializeBounded(responseObj, maxResponseChars, path);
    }

    private static object? TryBuildSummary(JsonElement analysis)
    {
        if (analysis.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Keep index factual and small: avoid biasing with server recommendations (those remain in the full report).
        if (!analysis.TryGetProperty("summary", out var summaryEl) || summaryEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? crashType = TryGetString(summaryEl, "crashType");
        string? severity = TryGetString(summaryEl, "severity");
        int? threadCount = TryGetInt(summaryEl, "threadCount");
        int? moduleCount = TryGetInt(summaryEl, "moduleCount");
        int? assemblyCount = TryGetInt(summaryEl, "assemblyCount");

        string? exceptionType = null;
        if (analysis.TryGetProperty("exception", out var exEl) && exEl.ValueKind == JsonValueKind.Object)
        {
            exceptionType = TryGetString(exEl, "type");
        }

        JsonElement? warningsValue = summaryEl.TryGetProperty("warnings", out var warnings) ? warnings : null;
        JsonElement? errorsValue = summaryEl.TryGetProperty("errors", out var errors) ? errors : null;

        return new
        {
            crashType,
            exceptionType,
            severity,
            threadCount,
            moduleCount,
            assemblyCount,
            warnings = warningsValue,
            errors = errorsValue
        };
    }

    private static IReadOnlyList<object> BuildToc(JsonElement analysis)
    {
        var toc = new List<object>();
        if (analysis.ValueKind != JsonValueKind.Object)
        {
            return toc;
        }

        // Always include top-level analysis children.
        foreach (var prop in analysis.EnumerateObject())
        {
            var path = $"analysis.{prop.Name}";
            var entry = BuildTocEntry(path, prop.Value);
            if (entry != null)
            {
                toc.Add(entry);
            }
        }

        // Add a few high-value nested paths (stable across common flows).
        AddIfExists(toc, analysis, "analysis.exception");
        AddIfExists(toc, analysis, "analysis.environment");
        AddIfExists(toc, analysis, "analysis.threads");
        AddIfExists(toc, analysis, "analysis.threads.faultingThread");
        AddIfExists(toc, analysis, "analysis.threads.all");
        AddIfExists(toc, analysis, "analysis.assemblies");
        AddIfExists(toc, analysis, "analysis.assemblies.items");
        AddIfExists(toc, analysis, "analysis.modules");
        AddIfExists(toc, analysis, "analysis.security");
        AddIfExists(toc, analysis, "analysis.synchronization");
        AddIfExists(toc, analysis, "analysis.memory");

        // De-dupe by path while keeping first occurrence.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<object>();
        foreach (var entry in toc)
        {
            if (entry is Dictionary<string, object?> dict &&
                dict.TryGetValue("path", out var pathValue) &&
                pathValue is string pathString &&
                !string.IsNullOrWhiteSpace(pathString))
            {
                if (!seen.Add(pathString))
                {
                    continue;
                }
            }

            deduped.Add(entry);
        }

        return deduped;
    }

    private static void AddIfExists(List<object> toc, JsonElement analysis, string path)
    {
        if (!path.StartsWith("analysis.", StringComparison.Ordinal))
        {
            return;
        }

        var local = path.Substring("analysis.".Length);
        if (!TryResolveRelativeDotPath(analysis, local, out var value))
        {
            return;
        }

        var entry = BuildTocEntry(path, value);
        if (entry != null)
        {
            toc.Add(entry);
        }
    }

    private static object? BuildTocEntry(string path, JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => new Dictionary<string, object?>
            {
                ["path"] = path,
                ["type"] = "object"
            },
            JsonValueKind.Array => new Dictionary<string, object?>
            {
                ["path"] = path,
                ["type"] = "array",
                ["count"] = value.GetArrayLength(),
                ["pageable"] = true
            },
            JsonValueKind.String => new Dictionary<string, object?>
            {
                ["path"] = path,
                ["type"] = "string"
            },
            JsonValueKind.Number => new Dictionary<string, object?>
            {
                ["path"] = path,
                ["type"] = "number"
            },
            JsonValueKind.True or JsonValueKind.False => new Dictionary<string, object?>
            {
                ["path"] = path,
                ["type"] = "boolean"
            },
            JsonValueKind.Null => new Dictionary<string, object?>
            {
                ["path"] = path,
                ["type"] = "null"
            },
            _ => null
        };
    }

    private static string? TryGetString(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int? TryGetInt(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i) ? i : null;

    private static bool TryResolveDotPath(JsonElement root, string path, out JsonElement value, out string? error)
    {
        value = default;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Path is required.";
            return false;
        }

        // Only allow rooted paths for safety.
        var trimmed = path.Trim();
        if (!trimmed.StartsWith("analysis", StringComparison.Ordinal) && !trimmed.StartsWith("metadata", StringComparison.Ordinal))
        {
            error = "Only 'analysis.*' and 'metadata.*' paths are supported.";
            return false;
        }

        var segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var current = root;
        foreach (var segment in segments)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                error = $"Segment '{segment}' cannot be resolved because the current node is not an object.";
                return false;
            }

            if (!current.TryGetProperty(segment, out current))
            {
                error = $"Property '{segment}' not found.";
                return false;
            }
        }

        value = current;
        return true;
    }

    private static bool TryResolveRelativeDotPath(JsonElement root, string path, out JsonElement value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var segments = path.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var current = root;
        foreach (var segment in segments)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        value = current;
        return true;
    }

    private sealed record ReportCursor(string Path, int Offset, int Limit);

    private static (int Offset, int Limit) ResolvePage(string path, int length, int? limit, string? cursor, out string? error)
    {
        error = null;

        var resolvedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var offset = 0;

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (cursor.Length > MaxCursorChars)
            {
                error = $"Cursor exceeds maximum length ({MaxCursorChars}).";
                return default;
            }

            if (!TryDecodeCursor(cursor, out var decoded, out var decodeError))
            {
                error = decodeError ?? "Invalid cursor.";
                return default;
            }

            if (!string.Equals(decoded.Path, path, StringComparison.Ordinal))
            {
                error = $"Cursor path '{decoded.Path}' does not match requested path '{path}'.";
                return default;
            }

            offset = decoded.Offset;
            resolvedLimit = Math.Clamp(decoded.Limit, 1, MaxLimit);
        }

        if (offset < 0 || offset > length)
        {
            error = "Cursor offset is out of range.";
            return default;
        }

        return (offset, resolvedLimit);
    }

    private static string EncodeCursor(ReportCursor cursor)
    {
        var json = JsonSerializer.Serialize(cursor, CompactCamelCaseIgnoreNull);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Base64UrlEncode(bytes);
    }

    private static bool TryDecodeCursor(string token, out ReportCursor cursor, out string? error)
    {
        cursor = new ReportCursor(string.Empty, 0, DefaultLimit);
        error = null;

        try
        {
            var bytes = Base64UrlDecode(token);
            var json = Encoding.UTF8.GetString(bytes);
            var decoded = JsonSerializer.Deserialize<ReportCursor>(json, CompactCamelCase);
            if (decoded == null || string.IsNullOrWhiteSpace(decoded.Path))
            {
                error = "Cursor payload is invalid.";
                return false;
            }

            cursor = decoded;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        var base64 = Convert.ToBase64String(bytes);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string token)
    {
        var s = token.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2:
                s += "==";
                break;
            case 3:
                s += "=";
                break;
        }
        return Convert.FromBase64String(s);
    }

    private static string SerializeBounded(object response, int maxChars, string path)
    {
        var json = JsonSerializer.Serialize(response, JsonSerializationDefaults.IndentedIgnoreNull);
        if (json.Length <= maxChars)
        {
            return json;
        }

        return SerializeError(
            path,
            "too_large",
            $"Response exceeds maxChars ({maxChars}). Narrow the path or reduce limit.",
            maxChars,
            extra: new { estimatedChars = json.Length });
    }

    private static string SerializeError(string path, string code, string message, int maxChars, object? extra = null)
    {
        var payload = new
        {
            path,
            error = new
            {
                code,
                message
            },
            extra
        };

        var json = JsonSerializer.Serialize(payload, JsonSerializationDefaults.IndentedIgnoreNull);
        if (json.Length <= maxChars)
        {
            return json;
        }

        // If even the error payload is too large, fall back to a minimal message.
        return JsonSerializer.Serialize(new
        {
            path,
            error = new { code = "too_large", message = "Error response exceeded maxChars." }
        }, JsonSerializationDefaults.IndentedIgnoreNull);
    }
}
