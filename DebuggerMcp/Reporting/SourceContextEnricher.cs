using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DebuggerMcp.Analysis;

namespace DebuggerMcp.Reporting;

/// <summary>
/// Enriches a <see cref="CrashAnalysisResult"/> with bounded source code snippets.
/// </summary>
internal static class SourceContextEnricher
{
    /// <summary>
    /// Optional factory used to create <see cref="HttpClient"/> instances (primarily for tests).
    /// When null, a default <see cref="HttpClient"/> is used.
    /// </summary>
    internal static Func<HttpClient>? HttpClientFactory { get; set; }

    /// <summary>
    /// Local source roots eligible for reading local source files.
    /// When empty, local source reads are disabled (remote-only).
    /// </summary>
    internal static IReadOnlyList<string> LocalSourceRoots { get; set; } = ParseLocalSourceRoots();

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "raw.githubusercontent.com",
        "gitlab.com"
    };

    private static readonly Regex GitHubBlobRegex = new(
        @"^/([^/]+)/([^/]+)/blob/([^/]+)/(.+)$",
        RegexOptions.Compiled);

    private static readonly Regex GitLabBlobRegex = new(
        @"^/(.+?)/-/blob/([^/]+)/(.+)$",
        RegexOptions.Compiled);

    private const int MaxEntries = 10;
    private const int MaxEntriesPerThread = 2;
    private const int ContextWindow = 3; // ±3 lines
    private const int MaxLinesPerEntry = (ContextWindow * 2) + 1;
    private const int MaxLineLength = 400;
    private const int MaxRemoteBytes = 256 * 1024;

    internal static async Task ApplyAsync(CrashAnalysisResult analysis, DateTime generatedAtUtc, HttpClient? httpClient = null)
    {
        if (analysis == null)
        {
            throw new ArgumentNullException(nameof(analysis));
        }

        // Populate capturedAtUtc on the timeline when present.
        if (analysis.Timeline != null && string.IsNullOrWhiteSpace(analysis.Timeline.CapturedAtUtc))
        {
            analysis.Timeline.CapturedAtUtc = generatedAtUtc.ToString("O");
        }

        if (analysis.Threads?.All == null || analysis.Threads.All.Count == 0)
        {
            analysis.SourceContext = [];
            return;
        }

        var candidates = SelectFramesForContext(analysis);
        if (candidates.Count == 0)
        {
            analysis.SourceContext = [];
            return;
        }

        HttpClient? ownedClient = null;
        var client = httpClient;
        if (client == null)
        {
            ownedClient = HttpClientFactory?.Invoke() ?? new HttpClient();
            client = ownedClient;
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = new List<SourceContextEntry>();

        try
        {
            foreach (var candidate in candidates)
            {
                if (entries.Count >= MaxEntries)
                {
                    break;
                }

                var (thread, frame) = candidate;
                if (frame.LineNumber is not int line || line <= 0)
                {
                    continue;
                }

                var entry = new SourceContextEntry
                {
                    ThreadId = thread.ThreadId ?? string.Empty,
                    FrameNumber = frame.FrameNumber,
                    Function = frame.Function ?? string.Empty,
                    Module = frame.Module ?? string.Empty,
                    SourceFile = frame.SourceFile,
                    LineNumber = frame.LineNumber,
                    SourceUrl = frame.SourceUrl,
                    SourceRawUrl = frame.SourceRawUrl
                };

                try
                {
                    if (!string.IsNullOrWhiteSpace(frame.SourceFile) &&
                        TryReadLocalContext(frame.SourceFile!, line, out var localLines, out var start, out var end))
                    {
                        entry.Status = "local";
                        entry.StartLine = start;
                        entry.EndLine = end;
                        entry.Lines = localLines;
                        entries.Add(entry);
                        continue;
                    }

                    var rawUrl = GetRawUrl(frame);
                    if (rawUrl == null)
                    {
                        entry.Status = "unavailable";
                        entries.Add(entry);
                        continue;
                    }

                    if (!cache.TryGetValue(rawUrl, out var text))
                    {
                        text = await FetchTextAsync(client, rawUrl, timeoutCts.Token).ConfigureAwait(false);
                        cache[rawUrl] = text;
                    }

                    if (!TryExtractContext(text, line, out var remoteLines, out var remoteStart, out var remoteEnd))
                    {
                        entry.Status = "error";
                        entry.Error = "Line number outside fetched file content.";
                        entries.Add(entry);
                        continue;
                    }

                    entry.Status = "remote";
                    entry.StartLine = remoteStart;
                    entry.EndLine = remoteEnd;
                    entry.Lines = remoteLines;
                    entries.Add(entry);
                }
                catch (Exception ex)
                {
                    entry.Status = "error";
                    entry.Error = ex.Message;
                    entries.Add(entry);
                }
            }
        }
        finally
        {
            ownedClient?.Dispose();
        }

        analysis.SourceContext = entries;
    }

    internal static void Apply(CrashAnalysisResult analysis, DateTime generatedAtUtc, HttpClient? httpClient = null)
    {
        ApplyAsync(analysis, generatedAtUtc, httpClient).GetAwaiter().GetResult();
    }

    private static List<(ThreadInfo thread, StackFrame frame)> SelectFramesForContext(CrashAnalysisResult analysis)
    {
        var selected = new List<(ThreadInfo thread, StackFrame frame)>();
        var perThreadCount = new Dictionary<ThreadInfo, int>(ReferenceEqualityComparer<ThreadInfo>.Instance);

        var threads = analysis.Threads!.All!;
        IEnumerable<ThreadInfo> orderedThreads = threads;
        var faulting = threads.FirstOrDefault(t => t.IsFaulting);
        if (faulting != null)
        {
            orderedThreads = new[] { faulting }.Concat(threads.Where(t => !ReferenceEquals(t, faulting)));
        }

        foreach (var thread in orderedThreads)
        {
            if (selected.Count >= MaxEntries)
            {
                break;
            }

            if (thread.CallStack == null || thread.CallStack.Count == 0)
            {
                continue;
            }

            perThreadCount.TryGetValue(thread, out var used);
            if (used >= MaxEntriesPerThread)
            {
                continue;
            }

            // Pick up to two meaningful frames per thread.
            foreach (var frame in thread.CallStack)
            {
                if (selected.Count >= MaxEntries || used >= MaxEntriesPerThread)
                {
                    break;
                }

                if (!StackFrameSelection.IsMeaningfulTopFrameCandidate(frame))
                {
                    continue;
                }

                if (frame.LineNumber is not int line || line <= 0)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(frame.SourceFile) && string.IsNullOrWhiteSpace(frame.SourceUrl) && string.IsNullOrWhiteSpace(frame.SourceRawUrl))
                {
                    continue;
                }

                selected.Add((thread, frame));
                used++;
                perThreadCount[thread] = used;
            }
        }

        return selected;
    }

    private static bool TryReadLocalContext(string sourceFile, int lineNumber, out List<string> lines, out int startLine, out int endLine)
    {
        lines = [];
        startLine = 0;
        endLine = 0;

        try
        {
            if (LocalSourceRoots.Count == 0)
            {
                return false;
            }

            if (!Path.IsPathRooted(sourceFile))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(sourceFile);
            if (!IsPathUnderAnyRoot(fullPath, LocalSourceRoots))
            {
                return false;
            }

            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists)
            {
                return false;
            }

            // Avoid symlink escapes by refusing to read symlinks.
            if (!string.IsNullOrWhiteSpace(fileInfo.LinkTarget))
            {
                return false;
            }

            var allLines = File.ReadAllLines(fullPath);
            return TryExtractContext(allLines, lineNumber, out lines, out startLine, out endLine);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractContext(string fileText, int lineNumber, out List<string> lines, out int startLine, out int endLine)
    {
        var all = fileText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return TryExtractContext(all, lineNumber, out lines, out startLine, out endLine);
    }

    private static bool TryExtractContext(string[] allLines, int lineNumber, out List<string> lines, out int startLine, out int endLine)
    {
        lines = [];
        startLine = 0;
        endLine = 0;

        if (lineNumber <= 0 || lineNumber > allLines.Length)
        {
            return false;
        }

        startLine = Math.Max(1, lineNumber - ContextWindow);
        endLine = Math.Min(allLines.Length, lineNumber + ContextWindow);

        for (var i = startLine; i <= endLine; i++)
        {
            var value = allLines[i - 1];
            value = SanitizeLine(value);
            if (value.Length > MaxLineLength)
            {
                value = value.Substring(0, MaxLineLength) + "…";
            }

            lines.Add(value);
            if (lines.Count >= MaxLinesPerEntry)
            {
                break;
            }
        }

        return true;
    }

    private static string? GetRawUrl(StackFrame frame)
    {
        if (!string.IsNullOrWhiteSpace(frame.SourceRawUrl) && TryValidateRemoteUrl(frame.SourceRawUrl!, out var raw))
        {
            return raw;
        }

        if (!string.IsNullOrWhiteSpace(frame.SourceUrl) && TryInferRawUrlFromBrowsable(frame.SourceUrl!, out var inferred))
        {
            return inferred;
        }

        return null;
    }

    private static bool TryValidateRemoteUrl(string url, out string validated)
    {
        validated = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return false;
        }

        // Reject query strings to avoid inadvertently embedding tokens.
        if (!string.IsNullOrEmpty(uri.Query))
        {
            return false;
        }

        if (!AllowedHosts.Contains(uri.Host))
        {
            return false;
        }

        validated = uri.ToString();
        return true;
    }

    private static bool TryInferRawUrlFromBrowsable(string sourceUrl, out string rawUrl)
    {
        rawUrl = string.Empty;
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!AllowedHosts.Contains(uri.Host))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return false;
        }

        if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            var match = GitHubBlobRegex.Match(uri.AbsolutePath);
            if (!match.Success)
            {
                return false;
            }

            var owner = match.Groups[1].Value;
            var repo = match.Groups[2].Value;
            var commit = match.Groups[3].Value;
            var path = match.Groups[4].Value;
            rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{commit}/{path}";
            return true;
        }

        if (string.Equals(uri.Host, "gitlab.com", StringComparison.OrdinalIgnoreCase))
        {
            var match = GitLabBlobRegex.Match(uri.AbsolutePath);
            if (!match.Success)
            {
                return false;
            }

            var project = match.Groups[1].Value;
            var commit = match.Groups[2].Value;
            var path = match.Groups[3].Value;
            rawUrl = $"https://gitlab.com/{project}/-/raw/{commit}/{path}";
            return true;
        }

        if (string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            rawUrl = uri.ToString();
            return true;
        }

        return false;
    }

    private static async Task<string> FetchTextAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        if (!TryValidateRemoteUrl(url, out var validated))
        {
            throw new InvalidOperationException("Source URL is not allowed.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, validated);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var total = 0;
            using var ms = new MemoryStream();
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                total += read;
                if (total > MaxRemoteBytes)
                {
                    throw new InvalidOperationException("Remote source file exceeds max size cap.");
                }

                ms.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string SanitizeLine(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return line;
        }

        // Conservative redactions for common secret patterns in code/config.
        var value = line;
        value = Regex.Replace(value, "(?i)(api[_-]?key\\s*[:=]\\s*)\"[^\"]+\"", "$1\"<redacted>\"");
        value = Regex.Replace(value, "(?i)(token\\s*[:=]\\s*)\"[^\"]+\"", "$1\"<redacted>\"");
        value = Regex.Replace(value, "(?i)(password\\s*[:=]\\s*)\"[^\"]+\"", "$1\"<redacted>\"");
        value = Regex.Replace(value, "(?i)(secret\\s*[:=]\\s*)\"[^\"]+\"", "$1\"<redacted>\"");
        return value;
    }

    private static IReadOnlyList<string> ParseLocalSourceRoots()
    {
        var raw = Environment.GetEnvironmentVariable("DEBUGGERMCP_SOURCE_CONTEXT_ROOTS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .ToList();
    }

    private static bool IsPathUnderAnyRoot(string fullPath, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var fullRoot = Path.GetFullPath(root);

            if (fullPath.Equals(fullRoot, StringComparison.Ordinal))
            {
                return true;
            }

            var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;

            if (fullPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
        where T : class
    {
        public static readonly ReferenceEqualityComparer<T> Instance = new();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
