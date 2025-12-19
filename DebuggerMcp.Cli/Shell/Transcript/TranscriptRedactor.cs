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
        text = ReplaceLlmSetKey(text);
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

    private static string ReplaceLlmSetKey(string text)
    {
        // Replace everything after "llm set-key" (allowing arbitrary whitespace) with a placeholder.
        // Examples:
        // - "llm set-key sk-123" -> "llm set-key ***"
        // - "llm   set-key\t sk-123" -> "llm set-key ***"
        if (!Regex.IsMatch(text, @"(?i)^\s*llm\s+set-key\b", RegexOptions.CultureInvariant))
        {
            return text;
        }

        return "llm set-key ***";
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

        if (!Regex.IsMatch(trimmed, @"(?i)^llm\s+", RegexOptions.CultureInvariant))
        {
            return text;
        }

        var after = Regex.Replace(trimmed, @"(?i)^llm\s+", string.Empty, RegexOptions.CultureInvariant);
        var firstToken = after.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
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
        // Preserve debugger-style hex tokens while still redacting typical secret tokens.
        // Examples to preserve: token=0x06000001, "token":"0x06000001"
        text = Regex.Replace(
            text,
            @"(?i)""(token)""\s*:\s*""([^""]*)""",
            match =>
            {
                var value = match.Groups[2].Value;
                return LooksLikeDebuggerHexToken(value) ? match.Value : $"\"{match.Groups[1].Value}\":\"***\"";
            },
            RegexOptions.CultureInvariant);

        text = Regex.Replace(
            text,
            @"(?i)\b(token)\b\s*[:=]\s*([^\s]+)",
            match =>
            {
                var key = match.Groups[1].Value;
                var rawValue = match.Groups[2].Value;
                var check = rawValue.TrimEnd('.', ',', ';', ')', ']', '}', '"', '\'');
                return LooksLikeDebuggerHexToken(check) ? match.Value : $"{key}=***";
            },
            RegexOptions.CultureInvariant);

        // JSON-style pairs: "apiKey": "..."
        text = Regex.Replace(
            text,
            @"(?i)""(openrouter_api_key|openai_api_key|anthropic_api_key|api[_-]?key|password|secret)""\s*:\s*""[^""]*""",
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
            @"(?i)(x-api-key\s*:)\s*([^\s]+)",
            "$1 ***",
            RegexOptions.CultureInvariant);

        text = Regex.Replace(
            text,
            @"(?i)\b(openrouter_api_key|openai_api_key|anthropic_api_key|api[_-]?key|password|secret)\b\s*[:=]\s*([^\s]+)",
            "$1=***",
            RegexOptions.CultureInvariant);

        // Common env-var style names (underscore-separated) don't match the patterns above because "_" is a word character.
        text = Regex.Replace(
            text,
            @"(?i)\b(openai_api_key|openrouter_api_key|anthropic_api_key|openai[_-]?api[_-]?key|openrouter[_-]?api[_-]?key|anthropic[_-]?api[_-]?key)\b\s*[:=]\s*([^\s]+)",
            "$1=***",
            RegexOptions.CultureInvariant);

        // Raw key patterns (e.g., OpenAI keys) sometimes appear outside key/value contexts.
        text = Regex.Replace(
            text,
            @"\b(sk|rk)-[A-Za-z0-9_-]+\b",
            "$1-***",
            RegexOptions.CultureInvariant);

        return text;
    }

    private static bool LooksLikeDebuggerHexToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var s = value.Trim();
        // SOS/metadata tokens are 32-bit values rendered as "0x" + 8 hex digits (e.g., 0x06000001).
        // Restricting to this format avoids accidentally preserving arbitrary hex-ish secrets.
        if (!s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.Length != 10)
        {
            return false;
        }

        for (var i = 2; i < s.Length; i++)
        {
            var c = s[i];
            var isHex =
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f') ||
                (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
