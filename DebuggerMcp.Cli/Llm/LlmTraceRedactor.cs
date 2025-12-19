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

        // JSON-style pairs: "apiKey": "..."
        text = Regex.Replace(
            text,
            @"(?i)""(openrouter_api_key|api[_-]?key|password|secret)""\s*:\s*""[^""]*""",
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
            @"(?i)\b(openrouter_api_key|api[_-]?key|password|secret)\b\s*[:=]\s*([^\s]+)",
            "$1=***",
            RegexOptions.CultureInvariant);

        return text;
    }
}

