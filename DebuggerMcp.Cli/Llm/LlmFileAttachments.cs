using System.Text;
using System.Text.RegularExpressions;
using DebuggerMcp.Cli.Shell.Transcript;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Parses and loads file attachments referenced in an <c>llm</c> prompt.
/// </summary>
internal static class LlmFileAttachments
{
    private const int MaxAttachmentReadBytes = 1_048_576;

    // Accept explicit file reference prefixes to avoid accidentally treating hashtags as attachments.
    // Examples:
    // - #./file.json
    // - #../logs/output.txt
    // - #/absolute/path
    // - #~/path
    // - #C:\path\file.txt
    // Also supports paths with spaces using parentheses: #(./path with spaces.json)
    private static readonly Regex AttachmentRegex = new(
        @"(?<!\w)#(?:(?<path>(?:\./|\.\./|/|~\/)[^\s,;:\)\]\}\""']+|[A-Za-z]:\\[^\s,;:\)\]\}\""']+)|\((?<path>(?:\./|\.\./|/|~\/)[^)]+|[A-Za-z]:\\[^)]+)\))(?<trail>[,.;:\)\]\}\""']*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal sealed record Attachment(string DisplayPath, string AbsolutePath, string Content, bool Truncated);

    internal sealed record ReportAttachmentContext(
        string DisplayPath,
        string AbsolutePath,
        LlmReportCache.CachedReport CachedReport,
        string MessageForModel,
        IReadOnlyDictionary<string, string> SectionIdToFile,
        IReadOnlyDictionary<string, string> PointerToFile);

    internal static (string CleanedPrompt, IReadOnlyList<Attachment> Attachments, IReadOnlyList<ReportAttachmentContext> Reports) ExtractAndLoad(
        string prompt,
        string baseDirectory,
        int maxBytesPerFile = 200_000,
        int maxTotalBytes = 400_000,
        string? cacheRootDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return (prompt, [], []);
        }

        baseDirectory = string.IsNullOrWhiteSpace(baseDirectory) ? Environment.CurrentDirectory : baseDirectory;
        cacheRootDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dbg-mcp",
            "llm-cache");

        var attachments = new List<Attachment>();
        var reports = new List<ReportAttachmentContext>();
        var sb = new StringBuilder();
        var lastIndex = 0;
        var remainingTotal = Math.Max(0, maxTotalBytes);

        foreach (Match match in AttachmentRegex.Matches(prompt))
        {
            if (!match.Success)
            {
                continue;
            }

            var rawPath = match.Groups["path"].Value;
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }
            var path = TrimTrailingPathPunctuation(rawPath);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var pathTrail = rawPath.Length > path.Length ? rawPath[path.Length..] : string.Empty;
            var trail = pathTrail + (match.Groups["trail"].Value ?? string.Empty);

            sb.Append(prompt.AsSpan(lastIndex, match.Index - lastIndex));
            sb.Append($"(<attached: {path}>)");
            sb.Append(trail);
            lastIndex = match.Index + match.Length;

            if (remainingTotal <= 0)
            {
                continue;
            }

            var (attachment, report, bytesUsed) = TryLoad(path, baseDirectory, maxBytesPerFile, remainingTotal, cacheRootDirectory);
            remainingTotal -= bytesUsed;
            if (attachment != null)
            {
                attachments.Add(attachment);
            }
            if (report != null)
            {
                reports.Add(report);
            }
        }

        sb.Append(prompt.AsSpan(lastIndex));
        var cleaned = sb.ToString().Trim();

        return (cleaned, attachments, reports);
    }

    private static (Attachment? Attachment, ReportAttachmentContext? Report, int BytesUsed) TryLoad(
        string displayPath,
        string baseDirectory,
        int maxBytesPerFile,
        int remainingTotalBytes,
        string cacheRootDirectory)
    {
        try
        {
            var expanded = ExpandHome(displayPath);
            var absolute = Path.GetFullPath(expanded, baseDirectory);
            if (!File.Exists(absolute))
            {
                return (null, null, 0);
            }

            var limit = Math.Min(Math.Max(0, maxBytesPerFile), Math.Max(0, remainingTotalBytes));
            if (limit <= 0)
            {
                return (null, null, 0);
            }

            // If this is a DebuggerMcp JSON report and it exceeds the attachment cap,
            // generate a cached per-section representation and attach only summary + manifest.
            var fileInfo = new FileInfo(absolute);
            if (fileInfo.Length > maxBytesPerFile &&
                absolute.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                LlmReportCache.LooksLikeDebuggerMcpReport(absolute))
            {
                var cached = LlmReportCache.BuildOrLoadCachedReport(absolute, cacheRootDirectory, maxSectionBytes: maxBytesPerFile);
                var idToFile = BuildFirstWinsMap(cached.Sections, s => s.SectionId, s => s.FilePath);
                var ptrToFile = BuildFirstWinsMap(cached.Sections, s => s.JsonPointer, s => s.FilePath);

                // Respect the remaining total budget, including the wrapper message overhead.
                var wrapperOverhead = LlmReportCache.BuildModelAttachmentMessage(displayPath, summaryJson: string.Empty, manifestJson: string.Empty);
                var wrapperBytes = Encoding.UTF8.GetByteCount(wrapperOverhead);
                var budgetForPayload = Math.Max(0, remainingTotalBytes - wrapperBytes);

                // Allocate remaining budget: manifest first, then summary.
                var manifestForModel = cached.ManifestJson;
                var summaryForModel = cached.SummaryJson;

                var manifestBytes = Encoding.UTF8.GetByteCount(manifestForModel);
                if (manifestBytes >= budgetForPayload)
                {
                    manifestForModel = TruncateUtf8ToBytes(manifestForModel, budgetForPayload, "... (truncated report index) ...");
                    summaryForModel = string.Empty;
                }
                else
                {
                    var remainingForSummary = Math.Max(0, budgetForPayload - manifestBytes);
                    summaryForModel = TruncateUtf8ToBytes(summaryForModel, remainingForSummary, "... (truncated report summary) ...");
                }

                var message = LlmReportCache.BuildModelAttachmentMessage(displayPath, summaryForModel, manifestForModel);
                message = TruncateUtf8ToBytes(message, remainingTotalBytes, "... (truncated attachment) ...");
                var used = Encoding.UTF8.GetByteCount(message);

                var report = new ReportAttachmentContext(displayPath, absolute, cached, message, idToFile, ptrToFile);
                return (null, report, used);
            }

            var (text, truncated, bytesRead) = ReadTextCapped(absolute, limit);
            if (string.IsNullOrWhiteSpace(text))
            {
                return (null, null, 0);
            }

            // Redact any secrets before sending to the model.
            text = TranscriptRedactor.RedactText(text);

            return (new Attachment(displayPath, absolute, text, truncated), null, bytesRead);
        }
        catch
        {
            return (null, null, 0);
        }
    }

    private static IReadOnlyDictionary<string, string> BuildFirstWinsMap(
        IEnumerable<LlmReportCache.ReportSection> sections,
        Func<LlmReportCache.ReportSection, string> keySelector,
        Func<LlmReportCache.ReportSection, string> valueSelector)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections)
        {
            var key = keySelector(section);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }
            if (!map.ContainsKey(key))
            {
                map[key] = valueSelector(section);
            }
        }
        return map;
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
        maxBytes = Math.Min(Math.Max(0, maxBytes), MaxAttachmentReadBytes);
        if (maxBytes <= 0)
        {
            return (string.Empty, false, 0);
        }

        // Read as bytes (for accurate caps), then decode as UTF-8 with replacement.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[Math.Min(maxBytes + 1, MaxAttachmentReadBytes + 1)];
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

    private static string TrimTrailingPathPunctuation(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var end = path.Length;
        while (end > 0)
        {
            var ch = path[end - 1];
            if (ch is ',' or '.' or ';' or ':' or ')' or ']' or '}' or '"' or '\'')
            {
                end--;
                continue;
            }

            break;
        }

        return end == path.Length ? path : path[..end];
    }

    private static string TruncateUtf8ToBytes(string text, int maxBytes, string suffix)
    {
        if (string.IsNullOrEmpty(text) || maxBytes <= 0)
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
        {
            return text;
        }

        var suffixBytes = Encoding.UTF8.GetByteCount(Environment.NewLine + suffix);
        var limit = Math.Max(0, maxBytes - suffixBytes);
        var prefix = Encoding.UTF8.GetString(bytes, 0, limit);
        return prefix + Environment.NewLine + suffix;
    }
}
