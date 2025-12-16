using System.Text.RegularExpressions;

namespace DebuggerMcp.Cli.Shell.Transcript;

/// <summary>
/// Helpers for normalizing console output for transcript storage.
/// </summary>
internal static partial class AnsiText
{
    internal static string StripAnsi(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Basic ANSI escape sequence stripping (SGR, cursor controls, etc).
        // This intentionally keeps it lightweight; transcript consumers (LLM) prefer plain text.
        return AnsiEscapeRegex().Replace(text, string.Empty);
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscapeRegex();
}

