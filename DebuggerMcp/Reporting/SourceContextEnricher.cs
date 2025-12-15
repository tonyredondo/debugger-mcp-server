using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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

    // Only allow fetching raw content URLs. Avoid fetching HTML blob pages by default.
    private static readonly HashSet<string> AllowedRawHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "raw.githubusercontent.com",
        "gitlab.com",
        "dev.azure.com"
    };

    // Hosts that are allowed as inputs for inference (not necessarily fetched directly).
    private static readonly HashSet<string> AllowedBrowsableHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "gitlab.com",
        "raw.githubusercontent.com",
        "dev.azure.com"
    };

    private static readonly Regex GitHubBlobRegex = new(
        @"^/([^/]+)/([^/]+)/blob/([^/]+)/(.+)$",
        RegexOptions.Compiled);

    private static readonly Regex GitLabBlobRegex = new(
        @"^/(.+?)/-/blob/([^/]+)/(.+)$",
        RegexOptions.Compiled);

    private static bool IsAzureHost(string host) =>
        host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedBrowsableHost(string host) =>
        AllowedBrowsableHosts.Contains(host) || IsAzureHost(host);

    private static bool IsAllowedRawHost(string host) =>
        AllowedRawHosts.Contains(host) || IsAzureHost(host);

    private const int MaxEntries = 10;
    private const int MaxEntriesPerThread = 2;
    private const int MaxEntriesFaultingThread = 10;
    private const int MinFaultingManagedEntries = 2;
    private const int MaxFaultingThreadEmbeddedEntries = 1000;
    private const int ContextWindow = 3; // ±3 lines
    private const int MaxLinesPerEntry = (ContextWindow * 2) + 1;
    private const int MaxLineLength = 400;
    private const int MaxRemoteBytes = 5 * 1024 * 1024;
    private const int MaxConcurrentRemoteFetches = 6;

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
            ownedClient = HttpClientFactory?.Invoke() ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            client = ownedClient;
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var cache = new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal);
        var inflight = new ConcurrentDictionary<string, Lazy<Task<string[]>>>(StringComparer.Ordinal);
        var summaryEntries = new List<SourceContextEntry>();

        try
        {
            var urlsToPrefetch = CollectRemoteSourceUrls(candidates, analysis.Threads?.FaultingThread);
            if (urlsToPrefetch.Count > 0)
            {
                await PrefetchRemoteSourcesAsync(client, cache, inflight, urlsToPrefetch, timeoutCts.Token).ConfigureAwait(false);
            }

            foreach (var candidate in candidates)
            {
                if (summaryEntries.Count >= MaxEntries)
                {
                    break;
                }

                var (thread, frame) = candidate;
                if (frame.LineNumber is not int line || line <= 0)
                {
                    continue;
                }

                try
                {
                    var entry = await BuildEntryAsync(thread, frame, client, cache, inflight, timeoutCts.Token).ConfigureAwait(false);
                    // The top-level summary should only include actionable context.
                    // Unavailable entries are omitted to avoid producing mostly-empty summaries for dumps without resolvable sources.
                    if (!string.Equals(entry.Status, "unavailable", StringComparison.OrdinalIgnoreCase))
                    {
                        summaryEntries.Add(entry);
                    }
                }
                catch (Exception ex)
                {
                    summaryEntries.Add(new SourceContextEntry
                    {
                        ThreadId = thread.ThreadId ?? string.Empty,
                        FrameNumber = frame.FrameNumber,
                        Function = frame.Function ?? string.Empty,
                        Module = frame.Module ?? string.Empty,
                        SourceFile = frame.SourceFile,
                        LineNumber = frame.LineNumber,
                        SourceUrl = frame.SourceUrl,
                        SourceRawUrl = frame.SourceRawUrl,
                        Status = "error",
                        Error = ex.Message
                    });
                }
            }

            // Embed an expanded list under the faulting thread (not constrained by the summary cap).
            // This is intended for UIs that want rich context for the most relevant thread while keeping the overall report bounded.
            if (analysis.Threads?.FaultingThread != null)
            {
                var faultingThread = analysis.Threads.FaultingThread;
                var faultingCandidates = SelectFaultingThreadFramesUnbounded(faultingThread);
                var embeddedCount = 0;

                foreach (var frame in faultingCandidates)
                {
                    if (embeddedCount >= MaxFaultingThreadEmbeddedEntries)
                    {
                        break;
                    }

                    try
                    {
                        var entry = await BuildEntryAsync(faultingThread, frame, client, cache, inflight, timeoutCts.Token).ConfigureAwait(false);
                        frame.SourceContext = ToFrameSourceContext(entry);
                        embeddedCount++;
                    }
                    catch (Exception ex)
                    {
                        frame.SourceContext = new StackFrameSourceContext
                        {
                            Status = "error",
                            Error = ex.Message
                        };
                        embeddedCount++;
                    }
                }
            }
        }
        finally
        {
            ownedClient?.Dispose();
        }

        analysis.SourceContext = summaryEntries;
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
            var perThreadLimit = thread.IsFaulting ? MaxEntriesFaultingThread : MaxEntriesPerThread;
            if (used >= perThreadLimit)
            {
                continue;
            }

            if (thread.IsFaulting)
            {
                var remainingBudget = Math.Min(MaxEntries - selected.Count, perThreadLimit - used);
                foreach (var frame in SelectFaultingThreadFrames(thread, remainingBudget))
                {
                    selected.Add((thread, frame));
                    used++;
                    perThreadCount[thread] = used;

                    if (selected.Count >= MaxEntries || used >= perThreadLimit)
                    {
                        break;
                    }
                }

                continue;
            }

            // Pick bounded meaningful frames per thread.
            foreach (var frame in thread.CallStack)
            {
                if (selected.Count >= MaxEntries || used >= perThreadLimit)
                {
                    break;
                }

                if (!IsContextCandidate(frame))
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

    private static List<StackFrame> SelectFaultingThreadFrames(ThreadInfo thread, int remainingBudget)
    {
        if (remainingBudget <= 0)
        {
            return [];
        }

        var candidates = thread.CallStack.Where(IsContextCandidate).ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var managedCandidates = candidates.Where(f => f.IsManaged).ToList();
        var targetManaged = Math.Min(MinFaultingManagedEntries, Math.Min(managedCandidates.Count, remainingBudget));
        if (targetManaged == 0)
        {
            return candidates.Take(remainingBudget).ToList();
        }

        // Reserve a small number of managed frames (by call stack order) so we don't end up with
        // only native frames when native frames dominate the top of the stack.
        var reservedManaged = new HashSet<StackFrame>(ReferenceEqualityComparer<StackFrame>.Instance);
        for (var i = 0; i < targetManaged; i++)
        {
            reservedManaged.Add(managedCandidates[i]);
        }

        var selected = new List<StackFrame>();
        var selectedManaged = 0;

        foreach (var frame in candidates)
        {
            if (selected.Count >= remainingBudget)
            {
                break;
            }

            if (reservedManaged.Contains(frame))
            {
                selected.Add(frame);
                selectedManaged++;
                continue;
            }

            var remainingSlots = remainingBudget - selected.Count;
            var remainingReserved = targetManaged - selectedManaged;

            // Only take non-reserved frames if we can still fit all remaining reserved managed frames.
            if (remainingSlots > remainingReserved)
            {
                selected.Add(frame);
            }
        }

        return selected;
    }

    private static List<StackFrame> SelectFaultingThreadFramesUnbounded(ThreadInfo thread)
    {
        return thread.CallStack
            .Where(IsContextCandidate)
            .ToList();
    }

    private static bool IsContextCandidate(StackFrame frame)
    {
        if (!StackFrameUtilities.IsMeaningfulTopFrameCandidate(frame))
        {
            return false;
        }

        if (frame.LineNumber is not int line || line <= 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(frame.SourceFile) &&
            string.IsNullOrWhiteSpace(frame.SourceUrl) &&
            string.IsNullOrWhiteSpace(frame.SourceRawUrl))
        {
            return false;
        }

        return true;
    }

    private static async Task<SourceContextEntry> BuildEntryAsync(
        ThreadInfo thread,
        StackFrame frame,
        HttpClient client,
        ConcurrentDictionary<string, string[]> cache,
        ConcurrentDictionary<string, Lazy<Task<string[]>>> inflight,
        CancellationToken cancellationToken)
    {
        if (frame.LineNumber is not int line || line <= 0)
        {
            return new SourceContextEntry
            {
                ThreadId = thread.ThreadId ?? string.Empty,
                FrameNumber = frame.FrameNumber,
                Function = frame.Function ?? string.Empty,
                Module = frame.Module ?? string.Empty,
                SourceFile = frame.SourceFile,
                LineNumber = frame.LineNumber,
                SourceUrl = frame.SourceUrl,
                SourceRawUrl = frame.SourceRawUrl,
                Status = "unavailable",
                Error = "Frame does not have a valid line number."
            };
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

        if (!string.IsNullOrWhiteSpace(frame.SourceFile) &&
            TryReadLocalContext(frame.SourceFile!, line, out var localLines, out var start, out var end))
        {
            entry.Status = "local";
            entry.StartLine = start;
            entry.EndLine = end;
            entry.Lines = localLines;
            return entry;
        }

        var rawUrl = GetRawUrl(frame);
        if (rawUrl == null)
        {
            entry.Status = "unavailable";
            return entry;
        }

        var fileLines = await GetOrFetchRemoteLinesAsync(client, rawUrl, cache, inflight, cancellationToken).ConfigureAwait(false);
        if (!TryExtractContext(fileLines, line, out var remoteLines, out var remoteStart, out var remoteEnd))
        {
            entry.Status = "error";
            entry.Error = "Line number outside fetched file content.";
            return entry;
        }

        entry.Status = "remote";
        entry.StartLine = remoteStart;
        entry.EndLine = remoteEnd;
        entry.Lines = remoteLines;
        return entry;
    }

    private static StackFrameSourceContext? ToFrameSourceContext(SourceContextEntry entry)
    {
        // Avoid emitting redundant payloads in callStack when we couldn't resolve any meaningful context.
        if (string.Equals(entry.Status, "unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new StackFrameSourceContext
        {
            Status = entry.Status,
            StartLine = entry.StartLine,
            EndLine = entry.EndLine,
            Lines = entry.Lines,
            Error = entry.Error
        };
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
            if (!TryGetMatchingRoot(fullPath, LocalSourceRoots, out var matchedRoot))
            {
                return false;
            }

            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists)
            {
                return false;
            }

            // Avoid symlink/junction escapes by refusing to read paths that traverse symlinked directories.
            if (HasSymlinkSegmentsBetweenRootAndPath(matchedRoot, fullPath))
            {
                return false;
            }

            // Avoid reading symlinked files as well.
            if (!string.IsNullOrWhiteSpace(fileInfo.LinkTarget) ||
                fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
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

        // Reject query strings for non-Azure hosts to avoid inadvertently embedding tokens.
        if (!string.IsNullOrEmpty(uri.Query) && !IsAzureHost(uri.Host))
        {
            return false;
        }

        if (!IsAllowedRawHost(uri.Host))
        {
            return false;
        }

        // Extra hardening: validate raw URL path shapes for known providers.
        if (string.Equals(uri.Host, "gitlab.com", StringComparison.OrdinalIgnoreCase))
        {
            if (!uri.AbsolutePath.Contains("/-/raw/", StringComparison.Ordinal))
            {
                return false;
            }
        }
        else if (string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            // raw.githubusercontent.com/{owner}/{repo}/{commit}/{path...}
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 4)
            {
                return false;
            }
        }
        else if (IsAzureHost(uri.Host))
        {
            // Azure DevOps raw fetch uses the Git Items API endpoint (query-based).
            // We only allow the items endpoint and disallow token-like query keys.
            if (!uri.AbsolutePath.Contains("/_apis/git/repositories/", StringComparison.OrdinalIgnoreCase) ||
                !uri.AbsolutePath.EndsWith("/items", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(uri.Query))
            {
                return false;
            }

            if (!TryValidateAzureQuery(uri.Query))
            {
                return false;
            }
        }

        validated = uri.ToString();
        return true;
    }

    private static bool TryValidateAzureQuery(string query)
    {
        // Very small parser: query comes in as "?a=b&c=d".
        // Disallow suspicious keys and allow only a known-safe subset.
        var q = query.StartsWith("?", StringComparison.Ordinal) ? query.Substring(1) : query;
        var parts = q.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var hasPath = false;

        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]).Trim();
            var value = kv.Length == 2 ? Uri.UnescapeDataString(kv[1]).Trim() : string.Empty;

            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            // Block obvious secret-bearing keys.
            if (key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("sig", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("access", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (key.Equals("path", StringComparison.OrdinalIgnoreCase))
            {
                hasPath = !string.IsNullOrWhiteSpace(value);
                continue;
            }

            // Allow a small subset needed to request file content.
            if (key.Equals("download", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("includeContent", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("resolveLfs", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("api-version", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("versionDescriptor.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Unknown query keys are not allowed.
            return false;
        }

        return hasPath;
    }

    private static bool TryInferRawUrlFromBrowsable(string sourceUrl, out string rawUrl)
    {
        rawUrl = string.Empty;
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!IsAllowedBrowsableHost(uri.Host))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
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

        if (IsAzureHost(uri.Host))
        {
            if (!TryInferAzureItemsApiUrl(uri, out var azureRaw))
            {
                return false;
            }

            rawUrl = azureRaw;
            return true;
        }

        return false;
    }

    private static bool TryInferAzureItemsApiUrl(Uri browsable, out string rawUrl)
    {
        rawUrl = string.Empty;

        // Expect: https://dev.azure.com/{org}/{project}/_git/{repo}?path=...&version=...
        // Emit:   https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repo}/items?path=...&includeContent=true&api-version=7.0
        var path = browsable.AbsolutePath.Trim('/');
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4)
        {
            return false;
        }

        var org = segments[0];
        var project = segments[1];
        if (!string.Equals(segments[2], "_git", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var repo = segments[3];
        if (string.IsNullOrWhiteSpace(repo))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(browsable.Query))
        {
            return false;
        }

        var query = ParseQuery(browsable.Query);
        if (!query.TryGetValue("path", out var filePath) || string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        // Best-effort: if a commit is present, pass it through as versionDescriptor.version.
        // Azure UI commonly uses version=GC<sha> or version=<sha>.
        string? commit = null;
        if (query.TryGetValue("version", out var version) && !string.IsNullOrWhiteSpace(version))
        {
            commit = version.StartsWith("GC", StringComparison.OrdinalIgnoreCase) ? version.Substring(2) : version;
        }

        var baseUri = $"{browsable.Scheme}://{browsable.Host}/{org}/{project}/_apis/git/repositories/{repo}/items";
        var sb = new StringBuilder();
        sb.Append(baseUri);
        sb.Append("?path=");
        sb.Append(Uri.EscapeDataString(filePath));
        sb.Append("&includeContent=true");
        if (!string.IsNullOrWhiteSpace(commit))
        {
            sb.Append("&versionDescriptor.version=");
            sb.Append(Uri.EscapeDataString(commit));
        }
        sb.Append("&api-version=7.0");

        rawUrl = sb.ToString();
        return TryValidateRemoteUrl(rawUrl, out _);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = query.StartsWith("?", StringComparison.Ordinal) ? query.Substring(1) : query;
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]);
            var value = kv.Length == 2 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
            if (!result.ContainsKey(key))
            {
                result[key] = value;
            }
        }
        return result;
    }

    private static async Task<string> FetchTextAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        if (!TryValidateRemoteUrl(url, out var validated))
        {
            throw new InvalidOperationException("Source URL is not allowed.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, validated);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        // Defend against allowlist bypass via redirects when the caller provides a custom HttpClient.
        var finalUri = response.RequestMessage?.RequestUri?.ToString() ?? validated;
        if (!TryValidateRemoteUrl(finalUri, out _))
        {
            throw new InvalidOperationException("Final source URL is not allowed.");
        }

        response.EnsureSuccessStatusCode();

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrWhiteSpace(mediaType) &&
            !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unexpected content type '{mediaType}'.");
        }

        // Explicitly reject HTML even though it is text/*.
        if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unexpected content type 'text/html'.");
        }

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

            var text = Encoding.UTF8.GetString(ms.ToArray());

            if (Uri.TryCreate(finalUri, UriKind.Absolute, out var finalParsed) &&
                IsAzureHost(finalParsed.Host) &&
                (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                 (mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        return content.GetString() ?? string.Empty;
                    }
                }
                catch
                {
                    // Fall through: return the raw body as text if JSON parsing fails.
                }
            }

            return text;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static HashSet<string> CollectRemoteSourceUrls(
        IReadOnlyList<(ThreadInfo thread, StackFrame frame)> candidates,
        ThreadInfo? faultingThread)
    {
        var urls = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (_, frame) in candidates)
        {
            if (TryGetRemoteUrlForPrefetch(frame, out var url))
            {
                urls.Add(url);
            }
        }

        if (faultingThread?.CallStack != null)
        {
            var seen = 0;
            foreach (var frame in SelectFaultingThreadFramesUnbounded(faultingThread))
            {
                if (seen >= MaxFaultingThreadEmbeddedEntries)
                {
                    break;
                }

                if (TryGetRemoteUrlForPrefetch(frame, out var url))
                {
                    urls.Add(url);
                }

                seen++;
            }
        }

        return urls;
    }

    private static bool TryGetRemoteUrlForPrefetch(StackFrame frame, out string url)
    {
        url = string.Empty;

        if (frame.LineNumber is not int line || line <= 0)
        {
            return false;
        }

        // When local roots are configured and the source file is under them, prefer local reads and avoid prefetching.
        if (!string.IsNullOrWhiteSpace(frame.SourceFile) &&
            LocalSourceRoots.Count > 0 &&
            Path.IsPathRooted(frame.SourceFile!))
        {
            try
            {
                var fullPath = Path.GetFullPath(frame.SourceFile!);
                if (TryGetMatchingRoot(fullPath, LocalSourceRoots, out _) && File.Exists(fullPath))
                {
                    return false;
                }
            }
            catch
            {
                // Fall back to remote prefetch when path normalization fails.
            }
        }

        var rawUrl = GetRawUrl(frame);
        if (rawUrl == null)
        {
            return false;
        }

        url = rawUrl;
        return true;
    }

    private static async Task PrefetchRemoteSourcesAsync(
        HttpClient client,
        ConcurrentDictionary<string, string[]> cache,
        ConcurrentDictionary<string, Lazy<Task<string[]>>> inflight,
        IEnumerable<string> urls,
        CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(MaxConcurrentRemoteFetches);

        var tasks = urls.Select(async url =>
        {
            if (cache.ContainsKey(url))
            {
                return;
            }

            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await GetOrFetchRemoteLinesAsync(client, url, cache, inflight, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static Task<string[]> GetOrFetchRemoteLinesAsync(
        HttpClient client,
        string url,
        ConcurrentDictionary<string, string[]> cache,
        ConcurrentDictionary<string, Lazy<Task<string[]>>> inflight,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(url, out var cached))
        {
            return Task.FromResult(cached);
        }

        var lazyTask = inflight.GetOrAdd(
            url,
            _ => new Lazy<Task<string[]>>(() => FetchRemoteLinesAsync(client, url, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));

        return AwaitAndCacheAsync(url, lazyTask, cache, inflight);
    }

    private static async Task<string[]> AwaitAndCacheAsync(
        string url,
        Lazy<Task<string[]>> lazyTask,
        ConcurrentDictionary<string, string[]> cache,
        ConcurrentDictionary<string, Lazy<Task<string[]>>> inflight)
    {
        try
        {
            var lines = await lazyTask.Value.ConfigureAwait(false);
            cache.TryAdd(url, lines);
            return lines;
        }
        finally
        {
            inflight.TryRemove(url, out _);
        }
    }

    private static async Task<string[]> FetchRemoteLinesAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        var text = await FetchTextAsync(client, url, cancellationToken).ConfigureAwait(false);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
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

        var separators = OperatingSystem.IsWindows()
            ? new[] { ';' }
            : new[] { ';', ':' };

        return raw
            .Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .ToList();
    }

    private static bool IsPathUnderAnyRoot(string fullPath, IReadOnlyList<string> roots)
    {
        return TryGetMatchingRoot(fullPath, roots, out _);
    }

    private static bool TryGetMatchingRoot(string fullPath, IReadOnlyList<string> roots, out string matchedRoot)
    {
        matchedRoot = string.Empty;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var bestLength = -1;
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var fullRoot = Path.GetFullPath(root);

            if (fullPath.Equals(fullRoot, comparison))
            {
                if (fullRoot.Length > bestLength)
                {
                    matchedRoot = fullRoot;
                    bestLength = fullRoot.Length;
                }

                continue;
            }

            var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;

            if (fullPath.StartsWith(prefix, comparison))
            {
                if (prefix.Length > bestLength)
                {
                    matchedRoot = fullRoot;
                    bestLength = prefix.Length;
                }
            }
        }

        return bestLength >= 0;
    }

    private static bool HasSymlinkSegmentsBetweenRootAndPath(string root, string fullPath)
    {
        // Both inputs are expected to be full paths.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var rootFull = Path.GetFullPath(root);
        var targetFull = Path.GetFullPath(fullPath);

        if (!targetFull.StartsWith(
                rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar,
                comparison) &&
            !targetFull.Equals(rootFull, comparison))
        {
            return true;
        }

        // Walk directories from the target up to (but excluding) the root.
        // If any directory in the chain is a symlink/junction, refuse the local read.
        var directory = new FileInfo(targetFull).Directory;
        while (directory != null)
        {
            var dirPath = directory.FullName;
            if (dirPath.Equals(rootFull, comparison))
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(directory.LinkTarget) ||
                directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }

            directory = directory.Parent;
        }

        // Also refuse reading when the root itself is a symlink/junction.
        try
        {
            var rootDir = new DirectoryInfo(rootFull);
            if (!string.IsNullOrWhiteSpace(rootDir.LinkTarget) ||
                rootDir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }
        catch
        {
            return true;
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
