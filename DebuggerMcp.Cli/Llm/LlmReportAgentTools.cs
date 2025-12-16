using System.IO;
using System.Linq;
using System.Text.Json;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Local tools for working with large attached DebuggerMcp JSON reports (cached per-section).
/// </summary>
internal static class LlmReportAgentTools
{
    internal static IReadOnlyList<ChatTool> GetTools()
        =>
        [
            new ChatTool
            {
                Name = "find_report_sections",
                Description = "Search the attached cached report sections by keyword (matches sectionId and jsonPointer).",
                Parameters = JsonDocument.Parse("""
                {
                  "type":"object",
                  "properties":{
                    "report":{"type":"string","description":"Optional report identifier (file name or attachment path) if multiple reports are attached"},
                    "query":{"type":"string","description":"Keyword to search for (e.g., 'faultingThread', 'threads', 'modules', 'System.Threading')"},
                    "maxResults":{"type":"integer","description":"Max matches to return (default 25)"}
                  },
                  "required":["query"]
                }
                """).RootElement.Clone()
            },
            new ChatTool
            {
                Name = "get_report_section",
                Description = "Fetch a cached JSON section from an attached report by sectionId or jsonPointer.",
                Parameters = JsonDocument.Parse("""
                {
                  "type":"object",
                  "properties":{
                    "report":{"type":"string","description":"Optional report identifier (file name or attachment path) if multiple reports are attached"},
                    "sectionId":{"type":"string","description":"Section id from the report index (preferred)"},
                    "jsonPointer":{"type":"string","description":"JSON pointer path (e.g., '/analysis/threads/faultingThread')"},
                    "maxChars":{"type":"integer","description":"Max characters to return (default 30000)"}
                  }
                }
                """).RootElement.Clone()
            }
        ];

    internal static bool IsReportTool(string toolName)
        => toolName is "find_report_sections" or "get_report_section";

    internal static Task<string> ExecuteAsync(
        ChatToolCall call,
        IReadOnlyList<LlmFileAttachments.ReportAttachmentContext> reports,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return call.Name switch
        {
            "find_report_sections" => Task.FromResult(ExecuteFind(call, reports)),
            "get_report_section" => Task.FromResult(ExecuteGet(call, reports)),
            _ => Task.FromResult($"ERROR: Unknown report tool '{call.Name}'.")
        };
    }

    private static string ExecuteFind(ChatToolCall call, IReadOnlyList<LlmFileAttachments.ReportAttachmentContext> reports)
    {
        if (!TryParseArgs(call.ArgumentsJson, out var args, out var error))
        {
            return error;
        }

        var query = GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return "ERROR: query is required.";
        }

        var maxResults = GetInt(args, "maxResults") ?? 25;
        maxResults = Math.Clamp(maxResults, 1, 200);

        var reportSelector = GetString(args, "report");
        if (!TrySelectReport(reports, reportSelector, out var report, out var selectError))
        {
            return selectError;
        }

        var matches = report.CachedReport.Sections
            .Where(s =>
                s.SectionId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.JsonPointer.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.JsonPointer, StringComparer.Ordinal)
            .Take(maxResults)
            .Select(s => new { id = s.SectionId, pointer = s.JsonPointer, sizeBytes = s.SizeBytes })
            .ToList();

        var payload = new
        {
            report = Path.GetFileName(report.AbsolutePath),
            query,
            matchCount = matches.Count,
            matches
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ExecuteGet(ChatToolCall call, IReadOnlyList<LlmFileAttachments.ReportAttachmentContext> reports)
    {
        if (!TryParseArgs(call.ArgumentsJson, out var args, out var error))
        {
            return error;
        }

        var reportSelector = GetString(args, "report");
        if (!TrySelectReport(reports, reportSelector, out var report, out var selectError))
        {
            return selectError;
        }

        var sectionId = GetString(args, "sectionId");
        var pointer = GetString(args, "jsonPointer");

        if (string.IsNullOrWhiteSpace(sectionId) && string.IsNullOrWhiteSpace(pointer))
        {
            return "ERROR: Provide sectionId or jsonPointer.";
        }

        var maxChars = GetInt(args, "maxChars") ?? 30_000;
        maxChars = Math.Clamp(maxChars, 1_000, 200_000);

        string? filePath = null;
        if (!string.IsNullOrWhiteSpace(sectionId))
        {
            report.SectionIdToFile.TryGetValue(sectionId, out filePath);
        }
        else if (!string.IsNullOrWhiteSpace(pointer))
        {
            report.PointerToFile.TryGetValue(pointer, out filePath);
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return "ERROR: Section not found. Use find_report_sections to locate valid sectionId/jsonPointer.";
        }

        // Safety: ensure resolved file is inside the report cache directory.
        var cacheDir = Path.GetFullPath(report.CachedReport.CacheDirectory);
        var full = Path.GetFullPath(filePath);
        if (!full.StartsWith(cacheDir, StringComparison.OrdinalIgnoreCase))
        {
            return "ERROR: Refusing to read section outside report cache directory.";
        }

        var text = File.ReadAllText(full);
        var truncated = false;
        if (text.Length > maxChars)
        {
            text = text[..maxChars] + "\n... (truncated) ...";
            truncated = true;
        }

        JsonElement contentElement;
        try
        {
            using var doc = JsonDocument.Parse(text);
            contentElement = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // If we truncated or the file contains non-JSON placeholder text, fall back to string content.
            var fallback = new { text };
            var fallbackJson = JsonSerializer.Serialize(fallback, new JsonSerializerOptions { WriteIndented = true });
            using var doc = JsonDocument.Parse(fallbackJson);
            contentElement = doc.RootElement.Clone();
        }

        var payload = new
        {
            report = Path.GetFileName(report.AbsolutePath),
            sectionId = sectionId ?? string.Empty,
            jsonPointer = pointer ?? string.Empty,
            truncated,
            content = contentElement
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static bool TrySelectReport(
        IReadOnlyList<LlmFileAttachments.ReportAttachmentContext> reports,
        string? selector,
        out LlmFileAttachments.ReportAttachmentContext report,
        out string error)
    {
        report = reports.Count == 1 ? reports[0] : null!;
        error = string.Empty;

        if (reports.Count == 0)
        {
            error = "ERROR: No reports are attached.";
            return false;
        }

        if (reports.Count == 1 && string.IsNullOrWhiteSpace(selector))
        {
            report = reports[0];
            return true;
        }

        if (string.IsNullOrWhiteSpace(selector))
        {
            error = "ERROR: Multiple reports are attached. Provide the 'report' parameter (file name or attachment path).";
            return false;
        }

        var normalized = selector.Trim();
        var match = reports.FirstOrDefault(r =>
            string.Equals(r.DisplayPath, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(r.AbsolutePath), normalized, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            var available = string.Join(", ", reports.Select(r => Path.GetFileName(r.AbsolutePath)));
            error = $"ERROR: Unknown report '{selector}'. Available: {available}";
            return false;
        }

        report = match;
        return true;
    }

    private static bool TryParseArgs(string json, out JsonElement args, out string error)
    {
        args = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "ERROR: Missing tool arguments.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "ERROR: Tool arguments must be a JSON object.";
                return false;
            }
            args = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            error = "ERROR: Invalid tool arguments JSON.";
            return false;
        }
    }

    private static string? GetString(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}
