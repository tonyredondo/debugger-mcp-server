using DebuggerMcp.Analysis;
using DebuggerMcp.SourceLink;

namespace DebuggerMcp;

/// <summary>
/// Represents a debugging session for a specific user.
/// </summary>
/// <remarks>
/// Each session maintains its own debugger manager instance (WinDbg or LLDB) and tracks
/// session metadata such as creation time and last access time.
/// 
/// Implements both <see cref="IDisposable"/> and <see cref="IAsyncDisposable"/> for
/// flexible cleanup in both synchronous and async contexts.
/// 
/// Thread-safety: The <see cref="LastAccessedAt"/> property uses atomic operations
/// to ensure thread-safe reads and writes from multiple concurrent requests.
/// </remarks>
public class DebuggerSession : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Backing field for LastAccessedAt, stored as ticks for atomic operations.
    /// </summary>
    private long _lastAccessedAtTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// Gets the unique identifier for this session.
    /// </summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the user identifier who owns this session.
    /// </summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the debugger manager instance for this session.
    /// </summary>
    /// <remarks>
    /// The actual type depends on the platform:
    /// - Windows: <see cref="WinDbgManager"/>
    /// - Linux/macOS: <see cref="LldbManager"/>
    /// </remarks>
    public IDebuggerManager Manager { get; init; } = null!;

    /// <summary>
    /// Gets the timestamp when this session was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the timestamp when this session was last accessed.
    /// </summary>
    /// <remarks>
    /// This property uses <see cref="Interlocked"/> operations for thread-safe access
    /// without requiring locks, making it safe for concurrent read/write from multiple threads.
    /// </remarks>
    public DateTime LastAccessedAt
    {
        get => new DateTime(Interlocked.Read(ref _lastAccessedAtTicks), DateTimeKind.Utc);
        set => Interlocked.Exchange(ref _lastAccessedAtTicks, value.Ticks);
    }

    /// <summary>
    /// Gets or sets the currently open dump ID, if any.
    /// </summary>
    public string? CurrentDumpId { get; set; }

    /// <summary>
    /// Gets or sets the ClrMD analyzer for assembly metadata enrichment.
    /// </summary>
    /// <remarks>
    /// This is optional - it's created when a dump is opened and may be null if:
    /// - ClrMD failed to open the dump (architecture mismatch, etc.)
    /// - The dump is not a .NET dump
    /// </remarks>
    public ClrMdAnalyzer? ClrMdAnalyzer { get; set; }

    private readonly object _sourceLinkResolverLock = new();

    /// <summary>
    /// Gets the cached Source Link resolver for the current dump, if one was created.
    /// </summary>
    /// <remarks>
    /// This resolver caches PDB/Source Link metadata and is safe to reuse across tool calls
    /// for the same dump. When the current dump changes, callers should create a new resolver.
    /// </remarks>
    public SourceLinkResolver? SourceLinkResolver { get; private set; }

    /// <summary>
    /// Gets the dump ID associated with the cached <see cref="SourceLinkResolver"/>.
    /// </summary>
    public string? SourceLinkResolverDumpId { get; private set; }

    private readonly object _reportCacheLock = new();

    /// <summary>
    /// Gets the dump ID associated with the cached canonical JSON report document, if any.
    /// </summary>
    public string? CachedReportDumpId { get; private set; }

    /// <summary>
    /// Gets the UTC timestamp when the cached canonical JSON report document was generated.
    /// </summary>
    public DateTime? CachedReportGeneratedAtUtc { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the cached canonical JSON report document includes watch evaluations.
    /// </summary>
    public bool CachedReportIncludesWatches { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the cached canonical JSON report document includes security analysis results.
    /// </summary>
    public bool CachedReportIncludesSecurity { get; private set; }

    private string? _cachedReportJson;

    /// <summary>
    /// Stores the canonical JSON report document in the session cache for the specified dump.
    /// </summary>
    /// <param name="dumpId">Dump ID that the report corresponds to.</param>
    /// <param name="generatedAtUtc">UTC timestamp when the report was generated.</param>
    /// <param name="reportJson">Canonical report JSON document string.</param>
    /// <param name="includesWatches"><c>true</c> when watch evaluations were included/enabled during report generation.</param>
    /// <param name="includesSecurity"><c>true</c> when security analysis was included during report generation.</param>
    public void SetCachedReport(string dumpId, DateTime generatedAtUtc, string reportJson, bool includesWatches, bool includesSecurity)
    {
        if (string.IsNullOrWhiteSpace(dumpId))
        {
            throw new ArgumentException("dumpId cannot be null or empty", nameof(dumpId));
        }

        if (string.IsNullOrWhiteSpace(reportJson))
        {
            throw new ArgumentException("reportJson cannot be null or empty", nameof(reportJson));
        }

        lock (_reportCacheLock)
        {
            if (_cachedReportJson != null &&
                CachedReportDumpId != null &&
                string.Equals(CachedReportDumpId, dumpId, StringComparison.OrdinalIgnoreCase))
            {
                var existingScore = GetReportCompletenessScore(CachedReportIncludesWatches, CachedReportIncludesSecurity);
                var incomingScore = GetReportCompletenessScore(includesWatches, includesSecurity);

                // Prefer the most complete cached report. Only replace when the incoming report is more complete,
                // or when completeness matches and the incoming report is newer.
                if (incomingScore < existingScore ||
                    (incomingScore == existingScore && CachedReportGeneratedAtUtc != null && generatedAtUtc < CachedReportGeneratedAtUtc.Value))
                {
                    return;
                }
            }

            CachedReportDumpId = dumpId;
            CachedReportGeneratedAtUtc = generatedAtUtc;
            CachedReportIncludesWatches = includesWatches;
            CachedReportIncludesSecurity = includesSecurity;
            _cachedReportJson = reportJson;
        }
    }

    /// <summary>
    /// Attempts to get the cached canonical JSON report document for the specified dump.
    /// </summary>
    /// <param name="dumpId">Dump ID that the caller expects.</param>
    /// <param name="requireWatches">When <c>true</c>, only returns a cached report that includes watch evaluations.</param>
    /// <param name="requireSecurity">When <c>true</c>, only returns a cached report that includes security analysis.</param>
    /// <param name="reportJson">When <c>true</c>, contains the cached report JSON.</param>
    /// <returns><c>true</c> when a matching cached report exists; otherwise <c>false</c>.</returns>
    public bool TryGetCachedReport(string dumpId, bool requireWatches, bool requireSecurity, out string reportJson)
    {
        if (string.IsNullOrWhiteSpace(dumpId))
        {
            reportJson = string.Empty;
            return false;
        }

        lock (_reportCacheLock)
        {
            if (_cachedReportJson == null ||
                CachedReportDumpId == null ||
                !string.Equals(CachedReportDumpId, dumpId, StringComparison.OrdinalIgnoreCase) ||
                (requireWatches && !CachedReportIncludesWatches) ||
                (requireSecurity && !CachedReportIncludesSecurity))
            {
                reportJson = string.Empty;
                return false;
            }

            reportJson = _cachedReportJson;
            return true;
        }
    }

    /// <summary>
    /// Clears the cached canonical JSON report document (if any).
    /// </summary>
    public void ClearCachedReport()
    {
        lock (_reportCacheLock)
        {
            _cachedReportJson = null;
            CachedReportDumpId = null;
            CachedReportGeneratedAtUtc = null;
            CachedReportIncludesWatches = false;
            CachedReportIncludesSecurity = false;
        }
    }

    private static int GetReportCompletenessScore(bool includesWatches, bool includesSecurity)
    {
        var score = 0;
        if (includesWatches)
        {
            score += 1;
        }

        if (includesSecurity)
        {
            score += 2;
        }

        return score;
    }

    /// <summary>
    /// Returns the cached <see cref="SourceLinkResolver"/> for a dump, or creates and caches a new one.
    /// </summary>
    /// <param name="dumpId">The dump ID the resolver should be associated with.</param>
    /// <param name="factory">Factory used to create a new resolver when needed.</param>
    /// <returns>A cached or newly created resolver.</returns>
    /// <exception cref="ArgumentException">Thrown when dumpId is null or empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when factory is null.</exception>
    public SourceLinkResolver GetOrCreateSourceLinkResolver(string dumpId, Func<SourceLinkResolver> factory)
    {
        if (string.IsNullOrWhiteSpace(dumpId))
        {
            throw new ArgumentException("dumpId cannot be null or empty", nameof(dumpId));
        }

        ArgumentNullException.ThrowIfNull(factory);

        var normalizedDumpId = Path.GetFileNameWithoutExtension(dumpId);
        if (string.IsNullOrWhiteSpace(normalizedDumpId))
        {
            normalizedDumpId = dumpId;
        }

        lock (_sourceLinkResolverLock)
        {
            if (SourceLinkResolver != null &&
                string.Equals(SourceLinkResolverDumpId, normalizedDumpId, StringComparison.OrdinalIgnoreCase))
            {
                return SourceLinkResolver;
            }

            SourceLinkResolver = factory();
            SourceLinkResolverDumpId = normalizedDumpId;
            return SourceLinkResolver;
        }
    }

    /// <summary>
    /// Clears the cached <see cref="SourceLinkResolver"/> (if any).
    /// </summary>
    public void ClearSourceLinkResolver()
    {
        lock (_sourceLinkResolverLock)
        {
            SourceLinkResolver = null;
            SourceLinkResolverDumpId = null;
        }
    }

    /// <summary>
    /// Releases all resources used by this session.
    /// </summary>
    public void Dispose()
    {
        ClrMdAnalyzer?.Dispose();
        ClrMdAnalyzer = null;
        ClearSourceLinkResolver();
        ClearCachedReport();
        Manager?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Asynchronously releases all resources used by this session.
    /// </summary>
    /// <returns>A ValueTask representing the asynchronous dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        ClrMdAnalyzer?.Dispose();
        ClrMdAnalyzer = null;
        ClearSourceLinkResolver();
        ClearCachedReport();
        if (Manager != null)
        {
            await Manager.DisposeAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

}
