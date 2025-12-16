using System.Text.RegularExpressions;

namespace DebuggerMcp.Cli.Shell.Transcript;

/// <summary>
/// Redacts sensitive values from stored transcripts (best-effort).
/// </summary>
internal static class TranscriptRedactor
{
    internal static string RedactCommand(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        var text = commandLine;

        // Common CLI patterns that can include secrets.
        text = ReplaceArgumentValue(text, "llm set-key");
        text = ReplaceFlagValue(text, "--api-key");
        text = ReplaceFlagValue(text, "-k");

        return RedactKeyValuePairs(text);
    }

    internal static string RedactText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return RedactKeyValuePairs(text);
    }

    private static string ReplaceArgumentValue(string text, string prefix)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        // Replace everything after the prefix with a placeholder.
        return prefix + " ***";
    }

    private static string ReplaceFlagValue(string text, string flag)
    {
        // Replace: "--api-key abc" -> "--api-key ***"
        return Regex.Replace(
            text,
            $"({Regex.Escape(flag)})\\s+([^\\s]+)",
            "$1 ***",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string RedactKeyValuePairs(string text)
    {
        // JSON-style pairs: "apiKey": "..."
        text = Regex.Replace(
            text,
            @"(?i)""(openrouter_api_key|api[_-]?key|token|password|secret)""\s*:\s*""[^""]*""",
            "\"$1\":\"***\"",
            RegexOptions.CultureInvariant);

        // Replace things like "apiKey=...", "token: ...", "Authorization: Bearer ...".
        text = Regex.Replace(
            text,
            @"(?i)(authorization\s*:\s*bearer)\s+([^\s]+)",
            "$1 ***",
            RegexOptions.CultureInvariant);

        text = Regex.Replace(
            text,
            @"(?i)\b(openrouter_api_key|api[_-]?key|token|password|secret)\b\s*[:=]\s*([^\s]+)",
            "$1=***",
            RegexOptions.CultureInvariant);

        return text;
    }
}
