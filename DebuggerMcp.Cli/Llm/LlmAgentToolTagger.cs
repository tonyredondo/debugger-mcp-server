using System.Text.Json;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Tags tool calls with stable labels to support baseline tracking and evidence provenance.
/// </summary>
internal static class LlmAgentToolTagger
{
    /// <summary>
    /// Gets provenance tags for a tool call.
    /// </summary>
    public static IReadOnlyList<string> GetTags(string toolName, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return Array.Empty<string>();
        }

        if (string.Equals(toolName, "report_index", StringComparison.OrdinalIgnoreCase))
        {
            return ["ORIENT_REPORT_INDEX"];
        }

        if (string.Equals(toolName, "report_get", StringComparison.OrdinalIgnoreCase))
        {
            var path = TryReadJsonStringProperty(argumentsJson, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                return ["REPORT_GET"];
            }

            return path.Trim() switch
            {
                "metadata" => ["BASELINE_META"],
                "analysis.summary" => ["BASELINE_SUMMARY"],
                "analysis.environment" => ["BASELINE_ENV"],
                "analysis.exception.type" => ["BASELINE_EXC_TYPE"],
                "analysis.exception.message" => ["BASELINE_EXC_MESSAGE"],
                "analysis.exception.hResult" => ["BASELINE_EXC_HRESULT"],
                "analysis.exception.stackTrace" => ["BASELINE_EXC_STACK"],
                "analysis.exception.analysis" => ["BASELINE_EXC_ANALYSIS"],
                _ => ["REPORT_GET"]
            };
        }

        if (string.Equals(toolName, "exec", StringComparison.OrdinalIgnoreCase))
        {
            return ["EXEC"];
        }

        if (string.Equals(toolName, "analyze", StringComparison.OrdinalIgnoreCase))
        {
            var kind = TryReadJsonStringProperty(argumentsJson, "kind");
            return string.IsNullOrWhiteSpace(kind)
                ? ["ANALYZE"]
                : [$"ANALYZE:{kind.Trim().ToLowerInvariant()}"];
        }

        if (string.Equals(toolName, "find_report_sections", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolName, "get_report_section", StringComparison.OrdinalIgnoreCase))
        {
            return ["ATTACHED_REPORT"];
        }

        return Array.Empty<string>();
    }

    private static string? TryReadJsonStringProperty(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return value.GetString();
        }
        catch
        {
            return null;
        }
    }
}

