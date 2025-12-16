using System.Text;
using System.Text.RegularExpressions;
using DebuggerMcp.Cli.Shell.Transcript;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Parses and loads file attachments referenced in an <c>llm</c> prompt.
/// </summary>
internal static class LlmFileAttachments
{
    // Accept explicit file reference prefixes to avoid accidentally treating hashtags as attachments.
    // Examples:
    // - #./file.json
    // - #../logs/output.txt
    // - #/absolute/path
    // - #~/path
    // - #C:\path\file.txt
    private static readonly Regex AttachmentRegex = new(
        @"(?<!\S)#(?<path>(?:\./|\.\./|/|~\/)[^\s]+|[A-Za-z]:\\[^\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal sealed record Attachment(string DisplayPath, string AbsolutePath, string Content, bool Truncated);

    internal static (string CleanedPrompt, IReadOnlyList<Attachment> Attachments) ExtractAndLoad(
        string prompt,
        string baseDirectory,
        int maxBytesPerFile = 200_000,
        int maxTotalBytes = 400_000)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return (prompt, []);
        }

        baseDirectory = string.IsNullOrWhiteSpace(baseDirectory) ? Environment.CurrentDirectory : baseDirectory;

        var attachments = new List<Attachment>();
        var sb = new StringBuilder();
        var lastIndex = 0;
        var remainingTotal = Math.Max(0, maxTotalBytes);

        foreach (Match match in AttachmentRegex.Matches(prompt))
        {
            if (!match.Success)
            {
                continue;
            }

            var path = match.Groups["path"].Value;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            sb.Append(prompt.AsSpan(lastIndex, match.Index - lastIndex));
            sb.Append($"(<attached: {path}>)");
            lastIndex = match.Index + match.Length;

            if (remainingTotal <= 0)
            {
                continue;
            }

            var (attachment, bytesUsed) = TryLoad(path, baseDirectory, maxBytesPerFile, remainingTotal);
            remainingTotal -= bytesUsed;
            if (attachment != null)
            {
                attachments.Add(attachment);
            }
        }

        sb.Append(prompt.AsSpan(lastIndex));
        var cleaned = sb.ToString().Trim();

        return (cleaned, attachments);
    }

    private static (Attachment? Attachment, int BytesUsed) TryLoad(
        string displayPath,
        string baseDirectory,
        int maxBytesPerFile,
        int remainingTotalBytes)
    {
        try
        {
            var expanded = ExpandHome(displayPath);
            var absolute = Path.GetFullPath(expanded, baseDirectory);
            if (!File.Exists(absolute))
            {
                return (null, 0);
            }

            var limit = Math.Min(Math.Max(0, maxBytesPerFile), Math.Max(0, remainingTotalBytes));
            if (limit <= 0)
            {
                return (null, 0);
            }

            var (text, truncated, bytesRead) = ReadTextCapped(absolute, limit);
            if (string.IsNullOrWhiteSpace(text))
            {
                return (null, 0);
            }

            // Redact any secrets before sending to the model.
            text = TranscriptRedactor.RedactText(text);

            return (new Attachment(displayPath, absolute, text, truncated), bytesRead);
        }
        catch
        {
            return (null, 0);
        }
    }

    private static string ExpandHome(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path[2..]);
        }

        return path;
    }

    private static (string Text, bool Truncated, int BytesRead) ReadTextCapped(string path, int maxBytes)
    {
        // Read as bytes (for accurate caps), then decode as UTF-8 with replacement.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[Math.Min(maxBytes + 1, 1_048_576)]; // avoid huge allocations
        var read = stream.Read(buffer, 0, Math.Min(buffer.Length, maxBytes + 1));
        var truncated = read > maxBytes;
        var effective = Math.Min(read, maxBytes);

        var text = Encoding.UTF8.GetString(buffer, 0, effective);
        if (truncated)
        {
            text += $"{Environment.NewLine}[...file truncated to {maxBytes} bytes...]";
        }

        return (text, truncated, effective);
    }
}

