using System.Text;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Helper for formatting arbitrary text as a Markdown code block without being broken by fence sequences.
/// </summary>
internal static class MarkdownCodeBlock
{
    internal static string Format(string content, string? language = null)
    {
        content ??= string.Empty;
        language = string.IsNullOrWhiteSpace(language) ? null : language.Trim();

        var fence = ChooseFence(content);
        if (fence != null)
        {
            var header = language == null ? fence : fence + language;
            return $"{header}{Environment.NewLine}{content.TrimEnd()}{Environment.NewLine}{fence}";
        }

        // Fallback: indented code block. This can't be prematurely "closed" by content.
        // It does not support language hints, but is robust.
        var sb = new StringBuilder();
        foreach (var line in NormalizeNewlines(content).Split('\n'))
        {
            sb.Append("    ");
            sb.AppendLine(line);
        }
        sb.AppendLine(); // terminate block by ending indentation
        return sb.ToString().TrimEnd();
    }

    private static string NormalizeNewlines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string? ChooseFence(string text)
    {
        // Try backticks first, then tildes. Cap fence length to prevent pathological allocations.
        const int maxFenceLen = 20;
        return ChooseFenceWithChar(text, '`', maxFenceLen) ?? ChooseFenceWithChar(text, '~', maxFenceLen);
    }

    private static string? ChooseFenceWithChar(string text, char fenceChar, int maxLen)
    {
        // Always allow at least a triple fence.
        if (string.IsNullOrEmpty(text))
        {
            return new string(fenceChar, 3);
        }

        var maxRun = 0;
        var current = 0;
        foreach (var ch in text)
        {
            if (ch == fenceChar)
            {
                current++;
                if (current > maxRun)
                {
                    maxRun = current;
                }
            }
            else
            {
                current = 0;
            }
        }

        var len = Math.Max(3, maxRun + 1);
        if (len > maxLen)
        {
            return null;
        }

        return new string(fenceChar, len);
    }
}

