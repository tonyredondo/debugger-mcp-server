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
        text = ReplaceLlmPrompt(text);
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

    private static string ReplaceLlmPrompt(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("llm", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        if (string.Equals(trimmed, "llm", StringComparison.OrdinalIgnoreCase))
        {
            return "llm";
        }

        if (!trimmed.StartsWith("llm ", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var after = trimmed[4..].TrimStart();
        var firstToken = after.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var sub = firstToken.Trim().ToLowerInvariant();

        // Keep subcommands as-is (these are already covered by other redaction rules if needed).
        if (sub is "set-key" or "model" or "reset" or "set-agent" or "agent" or "set-agent-confirm" or "agent-confirm")
        {
            return text;
        }

        // For free-form prompts, avoid persisting potentially sensitive content (the llm_user entry is stored separately).
        return "llm ***";
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
