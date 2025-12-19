#nullable enable

using System.Text.RegularExpressions;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Redacts sensitive values from llmagent trace payloads (best-effort).
/// </summary>
/// <remarks>
/// This redactor is intentionally narrower than <c>TranscriptRedactor</c> so that
/// common debugging terms (e.g., "token=0x0600...") are not unintentionally wiped.
/// </remarks>
internal static class LlmTraceRedactor
{
    internal static string RedactText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

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
            @"(?i)""(openrouter_api_key|openai_api_key|anthropic_api_key|debugger_mcp_openrouter_api_key|debugger_mcp_openai_api_key|debugger_mcp_anthropic_api_key|api[_-]?key|password|secret)""\s*:\s*""[^""]*""",
            "\"$1\":\"***\"",
            RegexOptions.CultureInvariant);

        // Header-ish text: "Authorization: Bearer ..."
        text = Regex.Replace(
            text,
            @"(?i)(authorization\s*:\s*bearer)\s+([^\s]+)",
            "$1 ***",
            RegexOptions.CultureInvariant);

        // Header-ish text: "x-api-key: ..."
        text = Regex.Replace(
            text,
            @"(?i)(x-api-key\s*:)\s*([^\s]+)",
            "$1 ***",
            RegexOptions.CultureInvariant);

        // Key=value or key: value pairs (exclude bare "token" to avoid wiping method tokens, etc.)
        text = Regex.Replace(
            text,
            @"(?i)\b(openrouter_api_key|openai_api_key|anthropic_api_key|debugger_mcp_openrouter_api_key|debugger_mcp_openai_api_key|debugger_mcp_anthropic_api_key|api[_-]?key|password|secret)\b\s*[:=]\s*([^\s]+)",
            "$1=***",
            RegexOptions.CultureInvariant);

        // Env-var style names (underscore-separated) don't match the generic apiKey/token patterns due to word-boundary behavior.
        text = Regex.Replace(
            text,
            @"(?i)\b(openai_api_key|openrouter_api_key|anthropic_api_key|openai[_-]?api[_-]?key|openrouter[_-]?api[_-]?key|anthropic[_-]?api[_-]?key)\b\s*[:=]\s*([^\s]+)",
            "$1=***",
            RegexOptions.CultureInvariant);

        // Raw key patterns (e.g., OpenAI/OpenRouter keys) sometimes appear outside key/value contexts.
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
