using System.Text.Json;
using System.Text.RegularExpressions;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Classifies tool results to help detect loops and produce deterministic next-step guidance.
/// </summary>
internal static partial class LlmAgentToolResultClassifier
{
    /// <summary>
    /// Returns <c>true</c> if the tool result represents a tool-level failure or contract error.
    /// </summary>
    public static bool IsError(string toolResult)
    {
        if (string.IsNullOrWhiteSpace(toolResult))
        {
            return false;
        }

        var trimmed = toolResult.TrimStart();
        if (trimmed.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith("invalid_path:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("invalid_cursor:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("too_large", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Common schema/contract failures (e.g. "report_get.path is required.").
        if (RequiredFieldRegex().IsMatch(trimmed))
        {
            return true;
        }

        // Some server tool errors are structured as a JSON object with { error: { code: ... } }.
        if (TryGetErrorCode(toolResult, out _))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts “Try:” hint tool calls from a <c>report_get</c> too-large response message.
    /// </summary>
    public static IReadOnlyList<LlmAgentSuggestedToolCall> ExtractTryHints(string toolResult)
    {
        if (string.IsNullOrWhiteSpace(toolResult))
        {
            return Array.Empty<LlmAgentSuggestedToolCall>();
        }

        var match = TryHintRegex().Match(toolResult);
        if (!match.Success)
        {
            return Array.Empty<LlmAgentSuggestedToolCall>();
        }

        var hintText = match.Groups["hint"].Value;
        if (string.IsNullOrWhiteSpace(hintText))
        {
            return Array.Empty<LlmAgentSuggestedToolCall>();
        }

        var results = new List<LlmAgentSuggestedToolCall>();
        foreach (Match call in SuggestedReportGetRegex().Matches(hintText))
        {
            var path = call.Groups["path"].Value;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var args = new { path = path.Trim() };
            var argsJson = JsonSerializer.Serialize(args);
            results.Add(new LlmAgentSuggestedToolCall("report_get", argsJson));
        }

        return results;
    }

    private static bool TryGetErrorCode(string toolResult, out string? code)
    {
        code = null;

        try
        {
            using var doc = JsonDocument.Parse(toolResult);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!doc.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!error.TryGetProperty("code", out var codeProp) || codeProp.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            code = codeProp.GetString();
            return !string.IsNullOrWhiteSpace(code);
        }
        catch
        {
            return false;
        }
    }

    [GeneratedRegex(@"Try:\s*(?<hint>.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex TryHintRegex();

    [GeneratedRegex(@"report_get\s*\(\s*path\s*=\s*""(?<path>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SuggestedReportGetRegex();

    [GeneratedRegex(@"^[a-z0-9_]+(\.[a-z0-9_]+)+\s+is\s+required\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex RequiredFieldRegex();
}

/// <summary>
/// A suggested tool call extracted from a tool error hint.
/// </summary>
internal sealed record LlmAgentSuggestedToolCall(string ToolName, string ArgumentsJson);
