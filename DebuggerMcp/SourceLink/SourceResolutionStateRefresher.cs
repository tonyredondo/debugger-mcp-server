using DebuggerMcp.Dumps;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Rebuilds the session's source-resolution state from the currently effective dump context.
/// </summary>
/// <remarks>
/// This helper keeps <see cref="SourceLinkResolver"/> and <see cref="SequencePointResolver"/>
/// aligned with the dump, executable, and symbol configuration that the debugger is actually using.
/// </remarks>
public sealed class SourceResolutionStateRefresher
{
    private readonly SymbolManager _symbolManager;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceResolutionStateRefresher"/> class.
    /// </summary>
    /// <param name="symbolManager">The symbol manager that owns the effective session symbol model.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public SourceResolutionStateRefresher(SymbolManager symbolManager, ILogger? logger = null)
    {
        _symbolManager = symbolManager ?? throw new ArgumentNullException(nameof(symbolManager));
        _logger = logger;
    }

    /// <summary>
    /// Rebuilds source-resolution state for the current dump context and attaches it to the session.
    /// </summary>
    /// <param name="session">The session whose state should be refreshed.</param>
    /// <param name="sessionId">The session identifier used to read effective symbol inputs.</param>
    /// <param name="dumpId">The dump identifier associated with the current dump.</param>
    /// <param name="dumpPath">The resolved path to the current dump.</param>
    /// <param name="executablePath">Optional resolved executable path for standalone apps.</param>
    /// <returns>The ordered list of local directories used to rebuild the source-resolution state.</returns>
    public IReadOnlyList<string> Refresh(
        DebuggerSession session,
        string sessionId,
        string dumpId,
        string dumpPath,
        string? executablePath = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session ID cannot be null or empty.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(dumpId))
        {
            throw new ArgumentException("Dump ID cannot be null or empty.", nameof(dumpId));
        }

        if (string.IsNullOrWhiteSpace(dumpPath))
        {
            throw new ArgumentException("Dump path cannot be null or empty.", nameof(dumpPath));
        }

        var resolvedExecutablePath = executablePath ?? ResolveExecutablePathFromMetadata(dumpPath, dumpId, _logger);
        var localSymbolDirectories = _symbolManager.GetEffectiveLocalSymbolDirectories(sessionId);
        var searchPaths = SourceResolutionPathBuilder.BuildExistingPaths(
            dumpPath,
            dumpId,
            resolvedExecutablePath,
            localSymbolDirectories,
            session.ClrMdAnalyzer?.Runtime);

        var sourceLinkResolver = new SourceLinkResolver(_logger);
        foreach (var path in searchPaths)
        {
            sourceLinkResolver.AddSymbolSearchPath(path);
        }

        session.ReplaceSourceLinkResolver(dumpId, sourceLinkResolver);

        if (session.ClrMdAnalyzer != null)
        {
            var sequencePointResolver = new SequencePointResolver(_logger);
            foreach (var path in searchPaths)
            {
                sequencePointResolver.AddPdbSearchPath(path);
            }

            session.ClrMdAnalyzer.SetSequencePointResolver(sequencePointResolver);
        }

        _logger?.LogInformation(
            "[SourceResolution] Rebuilt state for session {SessionId} and dump {DumpId} using {Count} search paths",
            sessionId,
            dumpId,
            searchPaths.Count);

        return searchPaths;
    }

    /// <summary>
    /// Resolves the executable path that should participate in source resolution for a dump.
    /// </summary>
    /// <param name="dumpPath">The resolved path to the dump.</param>
    /// <param name="dumpId">The logical dump identifier.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>The resolved executable path when metadata points to an existing file; otherwise <c>null</c>.</returns>
    internal static string? ResolveExecutablePathFromMetadata(string dumpPath, string dumpId, ILogger? logger = null)
    {
        return DumpMetadataStore.TryResolveExecutablePath(dumpPath, dumpId, logger);
    }
}
