using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp;

/// <summary>
/// Provides caching for debugger command results to improve performance during analysis.
/// </summary>
/// <remarks>
/// <para>
/// Since memory dumps are immutable snapshots, command results are deterministic and safe to cache.
/// This significantly speeds up analysis operations that execute many repeated commands (e.g., dumpobj).
/// </para>
/// <para>
/// The cache is thread-safe and can be enabled/disabled at runtime.
/// </para>
/// </remarks>
public class CommandCache
{
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private readonly ILogger _logger;
    private volatile bool _isEnabled;
    private long _hits;
    private long _misses;

    /// <summary>
    /// Gets or sets whether caching is enabled.
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    /// <summary>
    /// Gets the number of cache hits.
    /// </summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>
    /// Gets the number of cache misses.
    /// </summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>
    /// Gets the current number of cached entries.
    /// </summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandCache"/> class.
    /// </summary>
    /// <param name="logger">The logger for diagnostic messages.</param>
    public CommandCache(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Tries to get a cached result for a command.
    /// </summary>
    /// <param name="command">The command to look up.</param>
    /// <param name="result">The cached result if found.</param>
    /// <returns>True if the result was found in cache; otherwise false.</returns>
    public bool TryGetCachedResult(string command, out string? result)
    {
        result = null;

        if (!_isEnabled)
        {
            // Cache bypassed when disabled to avoid stale results
            return false;
        }

        var key = NormalizeCommand(command);
        if (_cache.TryGetValue(key, out result))
        {
            Interlocked.Increment(ref _hits);
            _logger.LogDebug("[Cache] HIT for command: {Command} (hits: {Hits}, misses: {Misses})",
                TruncateForLog(command), _hits, _misses);
            return true;
        }

        Interlocked.Increment(ref _misses);
        return false;
    }

    /// <summary>
    /// Caches the result of a command.
    /// </summary>
    /// <param name="command">The command that was executed.</param>
    /// <param name="result">The result to cache.</param>
    public void CacheResult(string command, string result)
    {
        if (!_isEnabled)
        {
            // Ignore populate requests when caching is off
            return;
        }

        // Don't cache commands that modify state
        if (IsStateModifyingCommand(command))
        {
            _logger.LogDebug("[Cache] Skipping state-modifying command: {Command}", TruncateForLog(command));
            return;
        }

        var key = NormalizeCommand(command);
        _cache.TryAdd(key, result);
        _logger.LogDebug("[Cache] Cached result for command: {Command} (total cached: {Count})",
            TruncateForLog(command), _cache.Count);
    }

    /// <summary>
    /// Clears all cached results and resets statistics.
    /// </summary>
    public void Clear()
    {
        var count = _cache.Count;
        _cache.Clear();

        var hits = Interlocked.Exchange(ref _hits, 0);
        var misses = Interlocked.Exchange(ref _misses, 0);

        _logger.LogDebug("[Cache] Cleared {Count} entries (final stats: {Hits} hits, {Misses} misses)",
            count, hits, misses);
    }

    /// <summary>
    /// Normalizes a command for use as a cache key.
    /// </summary>
    private static string NormalizeCommand(string command)
    {
        // Normalize whitespace and case for consistent caching
        return command.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Checks if a command modifies debugger state and should not be cached.
    /// </summary>
    private static bool IsStateModifyingCommand(string command)
    {
        var normalizedCommand = command.Trim().ToLowerInvariant();

        // Commands that change debugger state
        return normalizedCommand.StartsWith("settings ") ||
               normalizedCommand.StartsWith("plugin ") ||
               normalizedCommand.StartsWith(".load") ||
               normalizedCommand.StartsWith(".unload") ||
               normalizedCommand.StartsWith(".sympath") ||
               normalizedCommand.StartsWith(".srcpath") ||
               normalizedCommand.StartsWith("target ") ||
               normalizedCommand.StartsWith("process ") ||
               normalizedCommand.StartsWith("thread select") ||
               normalizedCommand.StartsWith("frame select") ||
               normalizedCommand.StartsWith("breakpoint") ||
               normalizedCommand.StartsWith("watchpoint") ||
               normalizedCommand.StartsWith("register write") ||
               normalizedCommand.StartsWith("memory write") ||
               normalizedCommand.StartsWith("expression") ||
               normalizedCommand.StartsWith("p ") || // expression evaluation
               normalizedCommand.StartsWith("po "); // print object
    }

    /// <summary>
    /// Truncates a command for logging purposes.
    /// </summary>
    private static string TruncateForLog(string command)
    {
        const int maxLength = 80;
        if (command.Length <= maxLength)
        {
            return command;
        }
        return command[..maxLength] + "...";
    }
}
