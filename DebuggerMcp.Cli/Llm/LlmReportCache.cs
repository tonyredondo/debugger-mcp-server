using System.Buffers;
using System.Text;
using System.Text.Json;
using DebuggerMcp.Cli.Shell.Transcript;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Builds a cached, per-section representation of a large DebuggerMcp JSON report for LLM consumption.
/// </summary>
internal static class LlmReportCache
{
    internal const int DefaultMaxSectionBytes = 200_000;
    internal const int DefaultMaxModelIndexBytes = 120_000;
    internal const int DefaultMaxModelSummaryBytes = 120_000;

    internal sealed record CachedReport(
        string CacheDirectory,
        IReadOnlyList<ReportSection> Sections,
        string SummaryJson,
        string ManifestJson);

    internal sealed record ReportSection(
        string SectionId,
        string JsonPointer,
        string FilePath,
        int SizeBytes);

    internal static bool LooksLikeDebuggerMcpReport(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return LooksLikeDebuggerMcpReport(stream);
        }
        catch
        {
            return false;
        }
    }

    internal static bool LooksLikeDebuggerMcpReport(Stream utf8JsonStream)
    {
        return TryFindDebuggerMcpReportSignature(
            utf8JsonStream,
            out var hasMetadataDumpId,
            out var hasKnownAnalysisSection) &&
            hasMetadataDumpId &&
            hasKnownAnalysisSection;
    }

    internal static CachedReport BuildOrLoadCachedReport(
        string reportPath,
        string cacheRootDirectory,
        int maxSectionBytes = DefaultMaxSectionBytes)
    {
        var fileInfo = new FileInfo(reportPath);
        var cacheKey = $"{reportPath}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}";
        var cacheDir = Path.Combine(cacheRootDirectory, "reports", ComputeStableId(cacheKey));
        Directory.CreateDirectory(cacheDir);

        var manifestPath = Path.Combine(cacheDir, "manifest.json");
        var summaryPath = Path.Combine(cacheDir, "summary.json");

        if (File.Exists(manifestPath) && File.Exists(summaryPath))
        {
            try
            {
                var manifestJson = File.ReadAllText(manifestPath);
                var summaryJson = File.ReadAllText(summaryPath);
                var sections = LoadSectionsFromManifest(manifestJson, cacheDir);
                if (sections.Count > 0)
                {
                    return new CachedReport(cacheDir, sections, summaryJson, BuildModelManifestJson(reportPath, fileInfo, sections, maxSectionBytes));
                }
            }
            catch
            {
                // Rebuild below.
            }
        }

        using var stream = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var doc = JsonDocument.Parse(stream);

        var sectionsList = new List<ReportSection>();

        // Always cache metadata and analysis root (as subsections).
        if (doc.RootElement.TryGetProperty("metadata", out var metadata))
        {
            WriteSectionRecursive(
                cacheDir,
                "metadata",
                "/metadata",
                metadata,
                sectionsList,
                maxSectionBytes);
        }

        if (doc.RootElement.TryGetProperty("analysis", out var analysis))
        {
            WriteSectionRecursive(
                cacheDir,
                "analysis",
                "/analysis",
                analysis,
                sectionsList,
                maxSectionBytes);
        }

        var summaryJsonOut = BuildSummaryJson(doc.RootElement);
        File.WriteAllText(summaryPath, summaryJsonOut);

        var internalManifest = BuildInternalManifestJson(reportPath, fileInfo, sectionsList, maxSectionBytes);
        File.WriteAllText(manifestPath, internalManifest);

        var modelManifest = BuildModelManifestJson(reportPath, fileInfo, sectionsList, maxSectionBytes);
        return new CachedReport(cacheDir, sectionsList, summaryJsonOut, modelManifest);
    }

    private static void WriteSectionRecursive(
        string cacheDir,
        string sectionId,
        string pointer,
        JsonElement element,
        List<ReportSection> sections,
        int maxSectionBytes)
    {
        if (TrySerializeElementCapped(element, maxSectionBytes, out var bytes, out var sizeBytes))
        {
            var fileName = GetSectionFileName(sectionId, pointer);
            var filePath = Path.Combine(cacheDir, fileName);
            var text = Encoding.UTF8.GetString(bytes);
            text = TranscriptRedactor.RedactText(text);
            File.WriteAllText(filePath, text);
            sections.Add(new ReportSection(sectionId, pointer, filePath, Encoding.UTF8.GetByteCount(text)));
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            WriteSplitContainerSection(cacheDir, sectionId, pointer, element, sections, maxProperties: 200);

            // Split object into properties.
            foreach (var prop in element.EnumerateObject())
            {
                var childId = $"{sectionId}.{prop.Name}";
                var childPointer = $"{pointer}/{EscapeJsonPointer(prop.Name)}";
                WriteSectionRecursive(cacheDir, childId, childPointer, prop.Value, sections, maxSectionBytes);
            }
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            WriteSplitContainerSection(cacheDir, sectionId, pointer, element, sections, maxItems: 50);

            var idx = 0;
            foreach (var item in element.EnumerateArray())
            {
                var key = TryGetStableArrayItemKey(item) ?? idx.ToString();
                var childId = $"{sectionId}[{key}]";
                var childPointer = $"{pointer}/{idx}";
                WriteSectionRecursive(cacheDir, childId, childPointer, item, sections, maxSectionBytes);
                idx++;
            }
            return;
        }

        // Primitive too large to serialize within cap: store a safe, valid JSON placeholder.
        var placeholder = JsonSerializer.Serialize(new
        {
            truncated = true,
            jsonPointer = pointer,
            note = $"Value exceeded {maxSectionBytes} bytes; split not possible for primitive JSON value."
        }, new JsonSerializerOptions { WriteIndented = true });
        placeholder = TranscriptRedactor.RedactText(placeholder);
        var cappedFile = GetSectionFileName(sectionId, pointer);
        var cappedPath = Path.Combine(cacheDir, cappedFile);
        File.WriteAllText(cappedPath, placeholder);
        sections.Add(new ReportSection(sectionId, pointer, cappedPath, Encoding.UTF8.GetByteCount(placeholder)));
    }

    private static void WriteSplitContainerSection(
        string cacheDir,
        string sectionId,
        string pointer,
        JsonElement element,
        List<ReportSection> sections,
        int? maxProperties = null,
        int? maxItems = null)
    {
        // When a section is too large and we split it into children, still emit a small "container" section
        // so higher-level jsonPointers can be fetched deterministically.
        object container;

        if (element.ValueKind == JsonValueKind.Object)
        {
            var props = element.EnumerateObject().Select(p => p.Name).ToList();
            var sample = maxProperties.HasValue ? props.Take(maxProperties.Value).ToList() : props;
            container = new
            {
                split = true,
                kind = "object",
                sectionId,
                jsonPointer = pointer,
                propertyCount = props.Count,
                properties = sample,
                note = "This section was split into smaller per-property sections; fetch children by jsonPointer prefix."
            };
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var count = element.GetArrayLength();
            var sampleCount = Math.Clamp(maxItems ?? 50, 0, 200);
            var sample = Enumerable.Range(0, Math.Min(count, sampleCount))
                .Select(i => new
                {
                    index = i,
                    jsonPointer = $"{pointer}/{i}"
                })
                .ToList();

            container = new
            {
                split = true,
                kind = "array",
                sectionId,
                jsonPointer = pointer,
                count,
                sample,
                note = "This array section was split into smaller per-item sections; fetch children by jsonPointer."
            };
        }
        else
        {
            return;
        }

        var json = JsonSerializer.Serialize(container, new JsonSerializerOptions { WriteIndented = true });
        json = TranscriptRedactor.RedactText(json);

        var fileName = GetSectionFileName(sectionId, pointer);
        var filePath = Path.Combine(cacheDir, fileName);
        File.WriteAllText(filePath, json);
        sections.Add(new ReportSection(sectionId, pointer, filePath, Encoding.UTF8.GetByteCount(json)));
    }

    private static string? TryGetStableArrayItemKey(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (item.TryGetProperty("dumpId", out var dumpId) && dumpId.ValueKind == JsonValueKind.String)
        {
            return dumpId.GetString();
        }

        if (item.TryGetProperty("threadId", out var threadId))
        {
            if (threadId.ValueKind == JsonValueKind.String)
            {
                return threadId.GetString();
            }
            if (threadId.ValueKind == JsonValueKind.Number && threadId.TryGetInt32(out var threadInt))
            {
                return threadInt.ToString();
            }
        }

        if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            return name.GetString();
        }

        return null;
    }

    private static string BuildSummaryJson(JsonElement root)
    {
        // Avoid bias: exclude recommendations/findings/rootCause/summary-like fields.
        var obj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("metadata", out var metadata))
        {
            obj["metadata"] = metadata.Clone();
        }

        if (root.TryGetProperty("analysis", out var analysis))
        {
            var analysisObj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            // Whitelist only factual, low-bias sections.
            foreach (var key in new[] { "environment", "symbols", "stackSelection", "threads", "modules", "assemblies", "timeline", "memory", "async", "synchronization" })
            {
                if (!analysis.TryGetProperty(key, out var section))
                {
                    continue;
                }

                if (string.Equals(key, "threads", StringComparison.OrdinalIgnoreCase) &&
                    section.ValueKind == JsonValueKind.Object)
                {
                    var threadsObj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    if (section.TryGetProperty("summary", out var summary))
                    {
                        threadsObj["summary"] = summary.Clone();
                    }
                    if (section.TryGetProperty("faultingThread", out var ft))
                    {
                        threadsObj["faultingThread"] = ft.Clone();
                    }
                    if (section.TryGetProperty("osThreadCount", out var osThreadCount))
                    {
                        threadsObj["osThreadCount"] = osThreadCount.Clone();
                    }
                    analysisObj[key] = threadsObj;
                    continue;
                }

                analysisObj[key] = section.Clone();
            }

            obj["analysis"] = analysisObj;
        }

        var json = TranscriptRedactor.RedactText(JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        return TruncateUtf8(json, DefaultMaxModelSummaryBytes, "... (truncated summary) ...");
    }

    private static string BuildInternalManifestJson(
        string reportPath,
        FileInfo fileInfo,
        IReadOnlyList<ReportSection> sections,
        int maxSectionBytes)
    {
        var manifest = new
        {
            version = 1,
            source = new
            {
                path = reportPath,
                sizeBytes = fileInfo.Length,
                lastWriteUtc = fileInfo.LastWriteTimeUtc
            },
            maxSectionBytes,
            sections = sections
                .OrderBy(s => s.JsonPointer, StringComparer.Ordinal)
                .Select(s => new
                {
                    id = s.SectionId,
                    pointer = s.JsonPointer,
                    file = Path.GetFileName(s.FilePath),
                    sizeBytes = s.SizeBytes
                })
                .ToList()
        };

        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildModelManifestJson(
        string reportPath,
        FileInfo fileInfo,
        IReadOnlyList<ReportSection> sections,
        int maxSectionBytes)
    {
        var fileName = Path.GetFileName(reportPath);

        var analysisKeys = sections
            .Select(s => s.JsonPointer)
            .Where(p => p.StartsWith("/analysis/", StringComparison.Ordinal))
            .Select(p => p.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2)
            .Select(parts => parts[1])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var index = new
        {
            version = 1,
            source = new
            {
                fileName,
                sizeBytes = fileInfo.Length,
                lastWriteUtc = fileInfo.LastWriteTimeUtc
            },
            cache = new
            {
                maxSectionBytes,
                sectionCount = sections.Count
            },
            topLevel = new
            {
                analysisKeys
            },
            tools = new
            {
                find = new
                {
                    name = "find_report_sections",
                    usage = "Use to locate section ids/pointers by keyword; then fetch with get_report_section."
                },
                get = new
                {
                    name = "get_report_section",
                    usage = "Fetch a cached JSON section by sectionId or jsonPointer."
                }
            }
        };

        var json = JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true });
        json = TranscriptRedactor.RedactText(json);
        return TruncateUtf8(json, DefaultMaxModelIndexBytes, "... (truncated index) ...");
    }

    private static List<ReportSection> LoadSectionsFromManifest(string manifestJson, string cacheDir)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        if (!doc.RootElement.TryGetProperty("sections", out var sectionsElem) || sectionsElem.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<ReportSection>();
        foreach (var item in sectionsElem.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = item.TryGetProperty("id", out var idElem) ? idElem.GetString() : null;
            var pointer = item.TryGetProperty("pointer", out var pElem) ? pElem.GetString() : null;
            var file = item.TryGetProperty("file", out var fElem) ? fElem.GetString() : null;
            var size = item.TryGetProperty("sizeBytes", out var sElem) && sElem.TryGetInt32(out var n) ? n : 0;

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(pointer) || string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            if (!TryResolveCacheFile(cacheDir, file!, out var path))
            {
                continue;
            }
            if (!File.Exists(path))
            {
                continue;
            }

            result.Add(new ReportSection(id!, pointer!, path, size));
        }

        return result;
    }

    private static bool TryResolveCacheFile(string cacheDir, string fileName, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        // Only allow plain file names (no directory separators, no rooted paths).
        var name = fileName.Trim();
        if (name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return false;
        }

        if (Path.IsPathRooted(name))
        {
            return false;
        }

        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var fullDir = Path.GetFullPath(cacheDir);
            var combined = Path.GetFullPath(Path.Combine(fullDir, name));
            var rel = Path.GetRelativePath(fullDir, combined);

            if (Path.IsPathRooted(rel))
            {
                return false;
            }

            if (rel.Equals("..", StringComparison.Ordinal) ||
                rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                rel.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                return false;
            }

            fullPath = combined;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeStableId(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars);
        if (s.Length > 120)
        {
            s = s[..120];
        }
        return s;
    }

    private static string GetSectionFileName(string sectionId, string jsonPointer)
    {
        // Ensure uniqueness even when sanitized names collide (e.g., long ids truncated to 120 chars).
        // Keep names stable across runs for the same report content.
        var baseName = SanitizeFileName(sectionId);
        var hash = ComputeStableId($"{sectionId}|{jsonPointer}")[..12];
        return $"{baseName}-{hash}.json";
    }

    private static string EscapeJsonPointer(string segment)
        => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    internal static string BuildModelAttachmentMessage(string displayPath, string summaryJson, string manifestJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Attached DebuggerMcp report: {displayPath}");
        sb.AppendLine("This file was detected as a DebuggerMcp JSON report and is too large to attach inline.");
        sb.AppendLine("The CLI cached it locally as per-section JSON for on-demand retrieval.");
        sb.AppendLine();
        sb.AppendLine("To retrieve more context:");
        sb.AppendLine("1) Enable agent mode: `llm set-agent true`");
        sb.AppendLine("2) Ask the agent to fetch sections using these local tools:");
        sb.AppendLine("   - `find_report_sections` (search section ids/pointers)");
        sb.AppendLine("   - `get_report_section` (fetch a section by id/pointer)");
        sb.AppendLine();
        sb.AppendLine("Example (agent mode):");
        sb.AppendLine("  - find_report_sections {\"query\":\"faultingThread\"}");
        sb.AppendLine("  - get_report_section {\"jsonPointer\":\"/analysis/threads/faultingThread\"}");
        sb.AppendLine("If multiple reports are attached, add `{ \"report\": \"<fileName>\" }` to disambiguate.");
        sb.AppendLine();
        sb.AppendLine("Report summary (factual; excludes recommendations/root cause):");
        sb.AppendLine("```json");
        sb.AppendLine(summaryJson);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Report index:");
        sb.AppendLine("```json");
        sb.AppendLine(manifestJson);
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static bool TrySerializeElementCapped(JsonElement element, int maxBytes, out byte[] utf8, out int sizeBytes)
    {
        utf8 = [];
        sizeBytes = 0;

        if (maxBytes <= 0)
        {
            return false;
        }

        try
        {
            var bufferWriter = new CappedBufferWriter(maxBytes + 1);
            using var writer = new Utf8JsonWriter(bufferWriter, new JsonWriterOptions { Indented = true });
            element.WriteTo(writer);
            writer.Flush();

            if (bufferWriter.WrittenCount > maxBytes)
            {
                return false;
            }

            utf8 = bufferWriter.WrittenSpan.ToArray();
            sizeBytes = bufferWriter.WrittenCount;
            return true;
        }
        catch (BufferLimitExceededException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFindDebuggerMcpReportSignature(
        Stream utf8JsonStream,
        out bool hasMetadataDumpId,
        out bool hasKnownAnalysisSection)
    {
        hasMetadataDumpId = false;
        hasKnownAnalysisSection = false;

        try
        {
            // Parse a single buffer to avoid token-splitting issues with streaming Utf8JsonReader usage.
            // DebuggerMcp reports always include metadata+analysis near the start of the file.
            var data = ReadFirstBytes(utf8JsonStream, 512 * 1024);
            var reader = new Utf8JsonReader(data, isFinalBlock: true, state: default);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            // We already consumed the root StartObject token.
            var depth = 1;
            var pendingSection = Section.None;
            var activeSection = Section.None;
            var sectionDepth = -1;

            // Analysis sections we consider "signature" evidence (low false positive risk).
            // These are stable across our reports and unlikely to occur together with top-level metadata/dumpId in arbitrary JSON.
            var analysisKeys =
                new HashSet<string>(
                    ["environment", "threads", "modules", "assemblies", "signature", "symbols", "stackSelection", "timeline", "memory", "async", "synchronization"],
                    StringComparer.OrdinalIgnoreCase);

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        depth++;
                        if (pendingSection != Section.None && depth == 2)
                        {
                            activeSection = pendingSection;
                            sectionDepth = depth;
                            pendingSection = Section.None;
                        }
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (activeSection != Section.None && depth == sectionDepth)
                        {
                            activeSection = Section.None;
                            sectionDepth = -1;
                        }
                        depth--;
                        break;
                    case JsonTokenType.PropertyName:
                        if (depth == 1)
                        {
                            var name = reader.GetString() ?? string.Empty;
                            if (string.Equals(name, "metadata", StringComparison.OrdinalIgnoreCase))
                            {
                                pendingSection = Section.Metadata;
                            }
                            else if (string.Equals(name, "analysis", StringComparison.OrdinalIgnoreCase))
                            {
                                pendingSection = Section.Analysis;
                            }
                        }
                        else if (activeSection == Section.Metadata && depth == 2)
                        {
                            var name = reader.GetString() ?? string.Empty;
                            if (string.Equals(name, "dumpId", StringComparison.OrdinalIgnoreCase))
                            {
                                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                                {
                                    hasMetadataDumpId = !string.IsNullOrWhiteSpace(reader.GetString());
                                }
                            }
                        }
                        else if (activeSection == Section.Analysis && depth == 2)
                        {
                            var name = reader.GetString() ?? string.Empty;
                            if (analysisKeys.Contains(name))
                            {
                                hasKnownAnalysisSection = true;
                            }
                        }
                        break;
                    case JsonTokenType.String:
                    case JsonTokenType.Number:
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                    case JsonTokenType.Null:
                        // If metadata/analysis isn't an object/array, clear the pending marker to avoid mis-attribution.
                        if (pendingSection != Section.None && depth == 1)
                        {
                            pendingSection = Section.None;
                        }
                        break;
                }
                if (hasMetadataDumpId && hasKnownAnalysisSection)
                {
                    return true;
                }
            }

            return hasMetadataDumpId && hasKnownAnalysisSection;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ReadFirstBytes(Stream stream, int maxBytes)
    {
        if (stream.CanSeek)
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        var buffer = new byte[maxBytes];
        var read = stream.Read(buffer, 0, buffer.Length);
        return buffer.AsSpan(0, read).ToArray();
    }

    private sealed class BufferLimitExceededException : Exception;

    private sealed class CappedBufferWriter(int maxBytes) : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[Math.Min(16 * 1024, Math.Max(1, maxBytes))];
        private int _written;

        public int WrittenCount => _written;
        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Advance(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (_written + count > maxBytes)
            {
                _written = maxBytes + 1;
                throw new BufferLimitExceededException();
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void Ensure(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            sizeHint = Math.Max(sizeHint, 1);

            if (_written + sizeHint > maxBytes)
            {
                throw new BufferLimitExceededException();
            }

            if (_buffer.Length - _written >= sizeHint)
            {
                return;
            }

            var newSize = Math.Min(maxBytes, Math.Max(_buffer.Length * 2, _written + sizeHint));
            if (newSize <= _buffer.Length)
            {
                throw new BufferLimitExceededException();
            }

            Array.Resize(ref _buffer, newSize);
        }
    }

    private static string TruncateUtf8(string text, int maxBytes, string suffix)
    {
        if (string.IsNullOrEmpty(text) || maxBytes <= 0)
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
        {
            return text;
        }

        var suffixBytes = Encoding.UTF8.GetByteCount(Environment.NewLine + suffix);
        var limit = Math.Max(0, maxBytes - suffixBytes);
        var prefix = Encoding.UTF8.GetString(bytes, 0, limit);
        return prefix + Environment.NewLine + suffix;
    }

    private enum Section
    {
        None = 0,
        Metadata = 1,
        Analysis = 2
    }
}
