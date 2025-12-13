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
        if (Manager != null)
        {
            await Manager.DisposeAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

}
