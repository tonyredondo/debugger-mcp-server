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
    internal const int DefaultPreviewChars = 2_048;
    private const int MaxPathChars = 512;
    private const int MaxCursorChars = 4096;
    private const int MaxSelectFields = 32;
    private const int MaxSelectFieldChars = 64;
    private const int MaxWhereValueChars = 256;
    private const int MaxTocEntries = 64;

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
                "Sampling/agent: report_get(path=\"analysis.exception\", select=[\"type\",\"message\",\"hresult\"])",
                "Sampling/agent: report_get(path=\"analysis.threads.faultingThread\"); MCP: report(action=\"get\", path=\"analysis.threads.faultingThread\")",
                "Sampling/agent: report_get(path=\"analysis.threads.all\", limit=25, cursor=null); MCP: report(action=\"get\", path=\"analysis.threads.all\", limit=25)",
                "Sampling/agent: report_get(path=\"analysis.assemblies.items\", limit=50, cursor=null, select=[\"name\",\"assemblyVersion\",\"path\"])",
                "Sampling/agent: report_get(path=\"analysis.assemblies.items\", where={field:\"name\", equals:\"Microsoft.Extensions.FileProviders.Physical\"}, limit=5)",
                "Sampling/agent: report_get(path=\"analysis.environment\", select=[\"platform\",\"runtime\"], pageKind=\"object\", limit=25)"
            }
        };

        return JsonSerializer.Serialize(index, JsonSerializationDefaults.IndentedIgnoreNull);
    }

    internal static string GetSection(
        string reportJson,
        string path,
        int? limit,
        string? cursor,
        int? maxChars,
        string? pageKind = null,
        IReadOnlyList<string>? select = null,
        ReportWhere? where = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var maxResponseChars = Math.Clamp(maxChars ?? DefaultMaxResponseChars, 1_000, 2_000_000);
        if (path.Length > MaxPathChars)
        {
            return SerializeError(path, "invalid_path", $"Path exceeds maximum length ({MaxPathChars}).", maxResponseChars);
        }

        if (!TryNormalizePageKind(pageKind, out var effectivePageKind, out var pageKindError))
        {
            return SerializeError(path, "invalid_argument", pageKindError ?? "Invalid pageKind.", maxResponseChars);
        }

        if (!TryNormalizeSelect(select, out var effectiveSelect, out var selectError))
        {
            return SerializeError(path, "invalid_argument", selectError ?? "Invalid select.", maxResponseChars);
        }

        if (!TryNormalizeWhere(where, out var effectiveWhere, out var whereError))
        {
            return SerializeError(path, "invalid_argument", whereError ?? "Invalid where.", maxResponseChars);
        }

        using var doc = JsonDocument.Parse(reportJson);
        var root = doc.RootElement;

        if (!TryResolvePath(root, path, out var value, out var resolveError))
        {
            return SerializeError(path, "invalid_path", resolveError ?? $"Path '{path}' does not exist.", maxResponseChars);
        }

        if (effectiveWhere != null && value.ValueKind != JsonValueKind.Array)
        {
            return SerializeError(path, "invalid_argument", "where is only supported for array paths.", maxResponseChars);
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var queryHash = ComputeQueryHash(pageKind: "array", select: effectiveSelect, where: effectiveWhere);
            var totalCount = value.GetArrayLength();
            var filtered = ApplyWhereFilter(value, effectiveWhere);
            var page = ResolvePage(path, kind: "array", length: filtered.Count, limit, cursor, queryHash, out var pageError);
            if (pageError != null)
            {
                return SerializeError(path, "invalid_cursor", pageError, maxResponseChars);
            }

            var items = new List<JsonElement>(capacity: page.Limit);
            var endExclusive = Math.Min(filtered.Count, page.Offset + page.Limit);
            for (var i = page.Offset; i < endExclusive; i++)
            {
                var element = filtered[i];
                items.Add(ProjectElement(element, effectiveSelect));
            }

            var nextCursor = endExclusive < filtered.Count
                ? EncodeCursor(new ReportCursor(path, endExclusive, page.Limit, "array", queryHash))
                : null;

            var response = new
            {
                path,
                value = items,
                page = new
                {
                    kind = "array",
                    offset = page.Offset,
                    total = totalCount,
                    filteredTotal = effectiveWhere == null ? (int?)null : filtered.Count,
                    limit = page.Limit,
                    nextCursor
                }
            };

            return SerializeBounded(response, maxResponseChars, path, value, pageKind: "array", select: effectiveSelect, where: effectiveWhere);
        }

        if (value.ValueKind == JsonValueKind.Object &&
            (effectivePageKind is "object" or "auto") &&
            value.EnumerateObject().Any())
        {
            var queryHash = ComputeQueryHash(pageKind: "object", select: effectiveSelect, where: null);
            List<string> properties;
            if (effectiveSelect is { Count: > 0 })
            {
                // For object results, select acts as a field filter on the object itself (no nesting).
                properties = effectiveSelect
                    .Where(p => value.TryGetProperty(p, out _))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
            else
            {
                properties = value.EnumerateObject()
                    .Select(p => p.Name)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();
            }

            var page = ResolvePage(path, kind: "object", length: properties.Count, limit, cursor, queryHash, out var pageError);
            if (pageError != null)
            {
                return SerializeError(path, "invalid_cursor", pageError, maxResponseChars);
            }

            var endExclusive = Math.Min(properties.Count, page.Offset + page.Limit);
            var selected = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
            for (var i = page.Offset; i < endExclusive; i++)
            {
                var propName = properties[i];
                if (value.TryGetProperty(propName, out var propValue))
                {
                    selected[propName] = propValue.Clone();
                }
            }

            var nextCursor = endExclusive < properties.Count
                ? EncodeCursor(new ReportCursor(path, endExclusive, page.Limit, "object", queryHash))
                : null;

            var response = new
            {
                path,
                value = JsonSerializer.SerializeToElement(selected, CompactCamelCaseIgnoreNull),
                page = new
                {
                    kind = "object",
                    offset = page.Offset,
                    total = properties.Count,
                    limit = page.Limit,
                    nextCursor
                }
            };

            return SerializeBounded(response, maxResponseChars, path, value, effectivePageKind, effectiveSelect, effectiveWhere);
        }

        var responseObj = new { path, value = ProjectElement(value, effectiveSelect) };

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            return SerializeError(path, "invalid_cursor", "Cursor cannot be used for this path because the value is not pageable (array, or object paging enabled via pageKind=\"object\").", maxResponseChars);
        }

        return SerializeBounded(responseObj, maxResponseChars, path, value, effectivePageKind, effectiveSelect, effectiveWhere);
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

    internal sealed record ReportWhere(string Field, string EqualsValue, bool CaseInsensitive = true);

    private sealed record PathSegment(string Name, int? Index);

    private static bool TryResolvePath(JsonElement root, string path, out JsonElement value, out string? error)
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

        if (!TryParseSegments(trimmed, out var segments, out var parseError))
        {
            error = parseError ?? "Invalid path.";
            return false;
        }

        var current = root;
        foreach (var segment in segments)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                error = $"Segment '{segment.Name}' cannot be resolved because the current node is not an object.";
                return false;
            }

            if (!current.TryGetProperty(segment.Name, out current))
            {
                error = $"Property '{segment.Name}' not found.";
                return false;
            }

            if (segment.Index.HasValue)
            {
                if (current.ValueKind != JsonValueKind.Array)
                {
                    error = $"Segment '{segment.Name}[{segment.Index.Value}]' cannot be resolved because '{segment.Name}' is not an array.";
                    return false;
                }

                var idx = segment.Index.Value;
                if (idx < 0 || idx >= current.GetArrayLength())
                {
                    error = $"Index {idx} is out of range for '{segment.Name}' (length {current.GetArrayLength()}).";
                    return false;
                }

                current = current[idx];
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

    private sealed record ReportCursor(string Path, int Offset, int Limit, string Kind, string? QueryHash);

    private static (int Offset, int Limit) ResolvePage(string path, string kind, int length, int? limit, string? cursor, string queryHash, out string? error)
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

            if (!string.Equals(decoded.Kind, kind, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Cursor kind '{decoded.Kind}' does not match requested kind '{kind}'.";
                return default;
            }

            var decodedHash = decoded.QueryHash;
            if (string.IsNullOrWhiteSpace(decodedHash))
            {
                // Back-compat: v0 cursors didn't include queryHash; treat them as the default query for this kind.
                decodedHash = ComputeQueryHash(kind, select: null, where: null);
            }

            if (!string.Equals(decodedHash, queryHash, StringComparison.Ordinal))
            {
                error = "Cursor does not match the current query (select/where/pageKind changed).";
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
        cursor = new ReportCursor(string.Empty, 0, DefaultLimit, "array", null);
        error = null;

        try
        {
            var bytes = Base64UrlDecode(token);
            var json = Encoding.UTF8.GetString(bytes);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Cursor payload is invalid.";
                return false;
            }

            var root = doc.RootElement;
            var path = root.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String ? pathEl.GetString() : null;
            var offset = root.TryGetProperty("offset", out var offsetEl) && offsetEl.ValueKind == JsonValueKind.Number && offsetEl.TryGetInt32(out var o) ? o : 0;
            var limit = root.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind == JsonValueKind.Number && limitEl.TryGetInt32(out var l) ? l : DefaultLimit;
            var kind = root.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String ? kindEl.GetString() : "array";
            var queryHash = root.TryGetProperty("queryHash", out var qh) && qh.ValueKind == JsonValueKind.String ? qh.GetString() : null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Cursor payload is invalid.";
                return false;
            }

            cursor = new ReportCursor(path, offset, limit, kind ?? "array", queryHash);
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

    private static string SerializeBounded(
        object response,
        int maxChars,
        string path,
        JsonElement resolvedValue,
        string pageKind,
        IReadOnlyList<string>? select,
        ReportWhere? where)
    {
        var json = JsonSerializer.Serialize(response, JsonSerializationDefaults.IndentedIgnoreNull);
        if (json.Length <= maxChars)
        {
            return json;
        }

        // Preview is embedded as a JSON string in the error response, so its serialized size can grow
        // significantly due to escaping. Keep it small relative to maxChars so we can still return
        // suggestedPaths even at low limits.
        var previewChars = Math.Clamp(maxChars / 8, 64, DefaultPreviewChars);
        return SerializeTooLarge(
            path,
            maxChars,
            estimatedChars: json.Length,
            resolvedValue,
            pageKind,
            select,
            where,
            preview: BuildPreview(json, previewChars));
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

    private static string SerializeTooLarge(
        string path,
        int maxChars,
        int estimatedChars,
        JsonElement resolvedValue,
        string pageKind,
        IReadOnlyList<string>? select,
        ReportWhere? where,
        string preview)
    {
        var toc = BuildTooLargeToc(path, resolvedValue, maxEntries: MaxTocEntries);
        var suggested = BuildSuggestedPaths(path, toc);

        var message = $"Response exceeds maxChars ({maxChars}). Fetch a narrower path, or page the data (arrays: limit/cursor; objects: pageKind=\"object\" + limit/cursor).";

        // Try progressively smaller payloads so we still return actionable guidance even for small maxChars.
        var payloads = new object[]
        {
            new
            {
                path,
                error = new { code = "too_large", message },
                extra = new
                {
                    estimatedChars,
                    maxChars,
                    valueKind = resolvedValue.ValueKind.ToString(),
                    toc,
                    suggestedPaths = suggested,
                    exampleCalls = BuildExampleCalls(path, resolvedValue, pageKind, select, where, suggested),
                    preview
                }
            },
            new
            {
                path,
                error = new { code = "too_large", message },
                extra = new
                {
                    estimatedChars,
                    maxChars,
                    valueKind = resolvedValue.ValueKind.ToString(),
                    suggestedPaths = suggested,
                    preview
                }
            },
            new
            {
                path,
                error = new { code = "too_large", message },
                extra = new
                {
                    estimatedChars,
                    maxChars,
                    suggestedPaths = suggested.Take(5).ToList()
                }
            }
        };

        foreach (var p in payloads)
        {
            var json = JsonSerializer.Serialize(p, JsonSerializationDefaults.IndentedIgnoreNull);
            if (json.Length <= maxChars)
            {
                return json;
            }
        }

        return JsonSerializer.Serialize(new
        {
            path,
            error = new { code = "too_large", message = "Error response exceeded maxChars." }
        }, JsonSerializationDefaults.IndentedIgnoreNull);
    }

    private static IReadOnlyList<Dictionary<string, object?>> BuildTooLargeToc(string basePath, JsonElement value, int maxEntries)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            return BuildLocalToc(basePath, value, maxEntries);
        }

        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0)
        {
            var first = value[0];
            if (first.ValueKind == JsonValueKind.Object)
            {
                return BuildLocalToc($"{basePath}[0]", first, maxEntries);
            }
        }

        return Array.Empty<Dictionary<string, object?>>();
    }

    private static string BuildPreview(string json, int maxChars)
    {
        if (string.IsNullOrEmpty(json) || maxChars <= 0 || json.Length <= maxChars)
        {
            return json ?? string.Empty;
        }

        if (maxChars < 64)
        {
            return json.Substring(0, maxChars);
        }

        var marker = $"\n... [truncated preview, total {json.Length} chars] ...\n";
        var remaining = maxChars - marker.Length;
        if (remaining <= 0)
        {
            return json.Substring(0, maxChars);
        }

        var head = remaining / 2;
        var tail = remaining - head;
        return json.Substring(0, head) + marker + json.Substring(json.Length - tail);
    }

    private static bool TryParseSegments(string path, out List<PathSegment> segments, out string? error)
    {
        segments = [];
        error = null;

        var trimmed = path.Trim();
        if (trimmed.Length == 0)
        {
            error = "Path is required.";
            return false;
        }

        if (trimmed.Contains('?', StringComparison.Ordinal))
        {
            error = "Unsupported path syntax. Only dot-separated properties and optional [index] are supported.";
            return false;
        }

        var i = 0;
        while (i < trimmed.Length)
        {
            if (trimmed[i] == '.')
            {
                i++;
                continue;
            }

            var start = i;
            while (i < trimmed.Length && trimmed[i] != '.' && trimmed[i] != '[')
            {
                i++;
            }

            var name = trimmed.Substring(start, i - start).Trim();
            if (name.Length == 0)
            {
                error = "Path contains an empty segment.";
                return false;
            }

            int? index = null;
            if (i < trimmed.Length && trimmed[i] == '[')
            {
                i++; // skip '['
                var idxStart = i;
                while (i < trimmed.Length && trimmed[i] != ']')
                {
                    i++;
                }

                if (i >= trimmed.Length || trimmed[i] != ']')
                {
                    error = "Unterminated [index] in path.";
                    return false;
                }

                var idxText = trimmed.Substring(idxStart, i - idxStart).Trim();
                if (!int.TryParse(idxText, out var parsed))
                {
                    error = $"Invalid array index '{idxText}'. Only numeric indices are supported.";
                    return false;
                }

                index = parsed;
                i++; // skip ']'
            }

            segments.Add(new PathSegment(name, index));
            if (segments.Count > 64)
            {
                error = "Path has too many segments.";
                return false;
            }

            if (i < trimmed.Length && trimmed[i] == '.')
            {
                i++;
            }
        }

        return true;
    }

    private static bool TryNormalizePageKind(string? pageKind, out string normalized, out string? error)
    {
        error = null;
        normalized = "array";

        if (string.IsNullOrWhiteSpace(pageKind))
        {
            return true;
        }

        var v = pageKind.Trim().ToLowerInvariant();
        normalized = v switch
        {
            "array" => "array",
            "object" => "object",
            "auto" => "auto",
            _ => string.Empty
        };

        if (normalized.Length == 0)
        {
            error = "pageKind must be one of: array, object, auto.";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeSelect(IReadOnlyList<string>? select, out IReadOnlyList<string>? normalized, out string? error)
    {
        error = null;
        normalized = null;

        if (select == null || select.Count == 0)
        {
            return true;
        }

        if (select.Count > MaxSelectFields)
        {
            error = $"select supports at most {MaxSelectFields} fields.";
            return false;
        }

        var list = new List<string>(select.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in select)
        {
            var trimmed = (s ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.Length > MaxSelectFieldChars)
            {
                error = $"select field '{trimmed}' exceeds max length ({MaxSelectFieldChars}).";
                return false;
            }

            if (trimmed.Contains('.') || trimmed.Contains('[') || trimmed.Contains(']'))
            {
                error = $"select field '{trimmed}' must be a direct property name (no nesting).";
                return false;
            }

            if (seen.Add(trimmed))
            {
                list.Add(trimmed);
            }
        }

        normalized = list.Count == 0 ? null : list;
        return true;
    }

    private static bool TryNormalizeWhere(ReportWhere? where, out ReportWhere? normalized, out string? error)
    {
        error = null;
        normalized = null;

        if (where == null)
        {
            return true;
        }

        var field = (where.Field ?? string.Empty).Trim();
        var equals = (where.EqualsValue ?? string.Empty).Trim();
        var ci = where.CaseInsensitive;

        if (field.Length == 0 || equals.Length == 0)
        {
            error = "where.field and where.equals are required when where is specified.";
            return false;
        }

        if (field.Length > MaxSelectFieldChars)
        {
            error = $"where.field exceeds max length ({MaxSelectFieldChars}).";
            return false;
        }

        if (equals.Length > MaxWhereValueChars)
        {
            error = $"where.equals exceeds max length ({MaxWhereValueChars}).";
            return false;
        }

        if (field.Contains('.') || field.Contains('[') || field.Contains(']'))
        {
            error = "where.field must be a direct property name (no nesting).";
            return false;
        }

        normalized = new ReportWhere(field, equals, ci);
        return true;
    }

    private static string ComputeQueryHash(string pageKind, IReadOnlyList<string>? select, ReportWhere? where)
    {
        var sb = new StringBuilder();
        sb.Append("v=1|");
        sb.Append(pageKind);
        sb.Append('|');
        if (where != null)
        {
            sb.Append("where:");
            sb.Append(where.Field);
            sb.Append('=');
            sb.Append(where.EqualsValue);
            sb.Append('|');
            sb.Append(where.CaseInsensitive ? "ci" : "cs");
        }
        sb.Append('|');
        if (select != null)
        {
            sb.Append("select:");
            for (var i = 0; i < select.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(select[i]);
            }
        }

        // Short, stable hash to keep cursors small.
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }

    private static List<JsonElement> ApplyWhereFilter(JsonElement array, ReportWhere? where)
    {
        var list = new List<JsonElement>();
        if (array.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        if (where == null)
        {
            foreach (var item in array.EnumerateArray())
            {
                list.Add(item);
            }
            return list;
        }

        var comparison = where.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!item.TryGetProperty(where.Field, out var prop))
            {
                continue;
            }

            var propValue = prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Number => prop.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };

            if (propValue != null && string.Equals(propValue, where.EqualsValue, comparison))
            {
                list.Add(item);
            }
        }

        return list;
    }

    private static JsonElement ProjectElement(JsonElement element, IReadOnlyList<string>? select)
    {
        if (select == null || select.Count == 0)
        {
            return element.Clone();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return element.Clone();
        }

        var dict = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in select)
        {
            if (element.TryGetProperty(key, out var prop))
            {
                dict[key] = prop.Clone();
            }
        }

        return JsonSerializer.SerializeToElement(dict, CompactCamelCaseIgnoreNull);
    }

    private static IReadOnlyList<Dictionary<string, object?>> BuildLocalToc(string basePath, JsonElement value, int maxEntries)
    {
        var results = new List<Dictionary<string, object?>>();

        if (value.ValueKind != JsonValueKind.Object)
        {
            return results;
        }

        foreach (var prop in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (results.Count >= maxEntries)
            {
                break;
            }

            var entry = new Dictionary<string, object?>
            {
                ["path"] = $"{basePath}.{prop.Name}",
                ["type"] = prop.Value.ValueKind.ToString().ToLowerInvariant(),
            };

            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                entry["count"] = prop.Value.GetArrayLength();
                entry["pageable"] = true;
            }
            else if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                entry["pageable"] = true;
                var capped = CountObjectPropertiesCapped(prop.Value, max: 256, out var truncated);
                entry["propertyCount"] = capped;
                if (truncated)
                {
                    entry["propertyCountIsTruncated"] = true;
                }
            }

            results.Add(entry);
        }

        return results;
    }

    private static int CountObjectPropertiesCapped(JsonElement obj, int max, out bool truncated)
    {
        truncated = false;

        if (obj.ValueKind != JsonValueKind.Object || max <= 0)
        {
            return 0;
        }

        var count = 0;
        foreach (var _ in obj.EnumerateObject())
        {
            count++;
            if (count > max)
            {
                truncated = true;
                return max;
            }
        }

        return count;
    }

    private static IReadOnlyList<string> BuildSuggestedPaths(string basePath, IReadOnlyList<Dictionary<string, object?>> toc)
    {
        var preferredNames = new[]
        {
            "type", "message", "hresult", "signalName", "inner", "stackTrace",
            "platform", "runtime", "process",
            "faultingThread", "threads", "modules", "assemblies", "items"
        };

        var candidates = toc
            .Select(t => t.TryGetValue("path", out var p) ? p as string : null)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .ToList();

        var preferred = new List<string>();
        foreach (var name in preferredNames)
        {
            var match = candidates.FirstOrDefault(p => p.EndsWith("." + name, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(match))
            {
                preferred.Add(match);
            }
        }

        // Fill remaining with first few paths.
        foreach (var p in candidates)
        {
            if (preferred.Count >= 8)
            {
                break;
            }

            if (!preferred.Contains(p, StringComparer.Ordinal))
            {
                preferred.Add(p);
            }
        }

        // If nothing else, suggest the base path itself.
        if (preferred.Count == 0)
        {
            preferred.Add(basePath);

            // For array paths, also suggest the first element (helps when the array is huge).
            if (!basePath.EndsWith("]", StringComparison.Ordinal))
            {
                preferred.Add($"{basePath}[0]");
            }
        }

        return preferred;
    }

    private static IReadOnlyList<string> BuildExampleCalls(
        string path,
        JsonElement value,
        string pageKind,
        IReadOnlyList<string>? select,
        ReportWhere? where,
        IReadOnlyList<string> suggestedPaths)
    {
        var calls = new List<string>();
        foreach (var p in suggestedPaths.Take(3))
        {
            calls.Add($"report_get(path=\"{p}\")");
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            calls.Add($"report_get(path=\"{path}\", limit=10)");
            calls.Add($"report_get(path=\"{path}\", limit=10, select=[\"name\",\"path\"])");
            calls.Add($"report_get(path=\"{path}\", where={{field:\"name\", equals:\"Microsoft.Extensions.FileProviders.Physical\"}}, limit=5, select=[\"name\",\"assemblyVersion\",\"path\"])");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            calls.Add($"report_get(path=\"{path}\", pageKind=\"object\", limit=25)");
        }

        return calls;
    }
}
