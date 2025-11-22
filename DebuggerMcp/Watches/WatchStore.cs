using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DebuggerMcp.Configuration;

namespace DebuggerMcp.Watches;

/// <summary>
/// Manages persistent storage of watch expressions per dump.
/// Watches are stored in JSON files alongside each dump file.
/// </summary>
/// <remarks>
/// Storage layout:
/// {dumpStoragePath}/{userId}/{dumpId}/watches.json
/// 
/// The store uses lazy loading and in-memory caching for performance.
/// Changes are persisted immediately to ensure durability.
/// </remarks>
public class WatchStore
{
    private readonly string _dumpStoragePath;
    private readonly ConcurrentDictionary<string, List<WatchExpression>> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
    
    private const string WatchesFileName = "watches.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchStore"/> class.
    /// </summary>
    /// <param name="dumpStoragePath">
    /// Optional path for dump storage. If not provided, uses the centralized configuration.
    /// </param>
    public WatchStore(string? dumpStoragePath = null)
    {
        _dumpStoragePath = dumpStoragePath ?? EnvironmentConfig.GetDumpStoragePath();
    }

    /// <summary>
    /// Gets the cache key for a dump.
    /// </summary>
    private static string GetCacheKey(string userId, string dumpId) => $"{userId}/{dumpId}";

    /// <summary>
    /// Gets the file path for watches for a specific dump.
    /// </summary>
    private string GetWatchesFilePath(string userId, string dumpId)
    {
        return Path.Combine(_dumpStoragePath, userId, dumpId, WatchesFileName);
    }

    /// <summary>
    /// Gets a semaphore for thread-safe file access.
    /// </summary>
    private SemaphoreSlim GetFileLock(string cacheKey)
    {
        return _fileLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// Adds a new watch expression for a dump.
    /// </summary>
    /// <param name="userId">The user ID who owns the dump.</param>
    /// <param name="dumpId">The dump ID to add the watch to.</param>
    /// <param name="watch">The watch expression to add.</param>
    /// <returns>The added watch with its generated ID.</returns>
    /// <exception cref="ArgumentException">Thrown when parameters are invalid.</exception>
    public async Task<WatchExpression> AddWatchAsync(string userId, string dumpId, WatchExpression watch)
    {
        ValidateParameters(userId, dumpId);
        
        if (watch == null)
        {
            throw new ArgumentNullException(nameof(watch));
        }

        if (string.IsNullOrWhiteSpace(watch.Expression))
        {
            throw new ArgumentException("Watch expression cannot be empty", nameof(watch));
        }

        // Ensure the watch has proper metadata
        watch.Id = string.IsNullOrWhiteSpace(watch.Id) ? Guid.NewGuid().ToString() : watch.Id;
        watch.DumpId = dumpId;
        watch.CreatedAt = DateTime.UtcNow;

        var cacheKey = GetCacheKey(userId, dumpId);
        var fileLock = GetFileLock(cacheKey);

        await fileLock.WaitAsync();
        try
        {
            var watches = await LoadWatchesInternalAsync(userId, dumpId);
            
            // Check for duplicate expression
            if (watches.Any(w => w.Expression.Equals(watch.Expression, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"A watch for expression '{watch.Expression}' already exists");
            }

            watches.Add(watch);
            await SaveWatchesInternalAsync(userId, dumpId, watches);
            
            // Update cache
            _cache[cacheKey] = watches;

            return watch;
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// Removes a watch expression by ID.
    /// </summary>
    /// <param name="userId">The user ID who owns the dump.</param>
    /// <param name="dumpId">The dump ID.</param>
    /// <param name="watchId">The watch ID to remove.</param>
    /// <returns>True if the watch was removed, false if not found.</returns>
    public async Task<bool> RemoveWatchAsync(string userId, string dumpId, string watchId)
    {
        ValidateParameters(userId, dumpId);
        
        if (string.IsNullOrWhiteSpace(watchId))
        {
            throw new ArgumentException("Watch ID cannot be empty", nameof(watchId));
        }

        var cacheKey = GetCacheKey(userId, dumpId);
        var fileLock = GetFileLock(cacheKey);

        await fileLock.WaitAsync();
        try
        {
            var watches = await LoadWatchesInternalAsync(userId, dumpId);
            var removed = watches.RemoveAll(w => w.Id == watchId) > 0;
            
            if (removed)
            {
                await SaveWatchesInternalAsync(userId, dumpId, watches);
                _cache[cacheKey] = watches;
            }

            return removed;
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// Gets all watches for a dump.
    /// </summary>
    /// <param name="userId">The user ID who owns the dump.</param>
    /// <param name="dumpId">The dump ID.</param>
    /// <returns>List of watch expressions.</returns>
    public async Task<List<WatchExpression>> GetWatchesAsync(string userId, string dumpId)
    {
        ValidateParameters(userId, dumpId);

        var cacheKey = GetCacheKey(userId, dumpId);
        
        // Check cache first
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached.ToList(); // Return a copy
        }

        var fileLock = GetFileLock(cacheKey);
        await fileLock.WaitAsync();
        try
        {
            var watches = await LoadWatchesInternalAsync(userId, dumpId);
            _cache[cacheKey] = watches;
            return watches.ToList();
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// Gets a specific watch by ID.
    /// </summary>
    /// <param name="userId">The user ID who owns the dump.</param>
    /// <param name="dumpId">The dump ID.</param>
    /// <param name="watchId">The watch ID.</param>
    /// <returns>The watch expression, or null if not found.</returns>
    public async Task<WatchExpression?> GetWatchAsync(string userId, string dumpId, string watchId)
    {
        var watches = await GetWatchesAsync(userId, dumpId);
        return watches.FirstOrDefault(w => w.Id == watchId);
    }

    /// <summary>
    /// Updates a watch expression.
    /// </summary>
    /// <param name="userId">The user ID who owns the dump.</param>
    /// <param name="dumpId">The dump ID.</param>
    /// <param name="watch">The updated watch expression.</param>
    /// <returns>True if the watch was updated, false if not found.</returns>
    public async Task<bool> UpdateWatchAsync(string userId, string dumpId, WatchExpression watch)
    {
        ValidateParameters(userId, dumpId);
        
        if (watch == null)
        {
            throw new ArgumentNullException(nameof(watch));
        }

        var cacheKey = GetCacheKey(userId, dumpId);
        var fileLock = GetFileLock(cacheKey);

        await fileLock.WaitAsync();
        try
        {
            var watches = await LoadWatchesInternalAsync(userId, dumpId);
            var index = watches.FindIndex(w => w.Id == watch.Id);
            
            if (index < 0)
            {
                return false;
            }

            watches[index] = watch;
            await SaveWatchesInternalAsync(userId, dumpId, watches);
            _cache[cacheKey] = watches;

            return true;
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// Clears all watches for a dump.
    /// </summary>
    /// <param name="userId">The user ID who owns the dump.</param>
    /// <param name="dumpId">The dump ID.</param>
    /// <returns>The number of watches that were cleared.</returns>
    public async Task<int> ClearWatchesAsync(string userId, string dumpId)
    {
        ValidateParameters(userId, dumpId);

        var cacheKey = GetCacheKey(userId, dumpId);
        var fileLock = GetFileLock(cacheKey);

        await fileLock.WaitAsync();
        try
        {
            var watches = await LoadWatchesInternalAsync(userId, dumpId);
            var count = watches.Count;
            
            if (count > 0)
            {
                watches.Clear();
                await SaveWatchesInternalAsync(userId, dumpId, watches);
                _cache[cacheKey] = watches;
            }

            return count;
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// Checks if any watches exist for a dump.
    /// </summary>
    /// <param name="userId">The user ID who owns the dump.</param>
    /// <param name="dumpId">The dump ID.</param>
    /// <returns>True if watches exist.</returns>
    public async Task<bool> HasWatchesAsync(string userId, string dumpId)
    {
        var watches = await GetWatchesAsync(userId, dumpId);
        return watches.Count > 0;
    }

    /// <summary>
    /// Gets the count of watches for a dump.
    /// </summary>
    /// <param name="userId">The user ID who owns the dump.</param>
    /// <param name="dumpId">The dump ID.</param>
    /// <returns>The number of watches.</returns>
    public async Task<int> GetWatchCountAsync(string userId, string dumpId)
    {
        var watches = await GetWatchesAsync(userId, dumpId);
        return watches.Count;
    }

    /// <summary>
    /// Invalidates the cache for a specific dump.
    /// </summary>
    /// <param name="userId">The user ID who owns the dump.</param>
    /// <param name="dumpId">The dump ID.</param>
    public void InvalidateCache(string userId, string dumpId)
    {
        var cacheKey = GetCacheKey(userId, dumpId);
        _cache.TryRemove(cacheKey, out _);
    }

    /// <summary>
    /// Invalidates all cached watches.
    /// </summary>
    public void InvalidateAllCaches()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Cleans up all resources (cache and locks) for a specific dump.
    /// Call this when a dump is deleted to prevent memory leaks.
    /// </summary>
    /// <param name="userId">The user ID who owns the dump.</param>
    /// <param name="dumpId">The dump ID.</param>
    /// <remarks>
    /// This method removes the cache entry and disposes the associated semaphore lock.
    /// It should be called when a dump is deleted to prevent the internal dictionaries
    /// from growing indefinitely.
    /// </remarks>
    public void CleanupDumpResources(string userId, string dumpId)
    {
        var cacheKey = GetCacheKey(userId, dumpId);
        
        // Remove from cache
        _cache.TryRemove(cacheKey, out _);
        
        // Remove and dispose the file lock
        if (_fileLocks.TryRemove(cacheKey, out var semaphore))
        {
            semaphore.Dispose();
        }
    }

    /// <summary>
    /// Cleans up all resources for a specific user.
    /// Call this when a user's data is being removed.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <remarks>
    /// This method removes all cache entries and disposes all semaphore locks
    /// associated with the specified user.
    /// </remarks>
    public void CleanupUserResources(string userId)
    {
        var prefix = $"{userId}/";
        
        // Find all keys for this user
        var keysToRemove = _cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        
        foreach (var key in keysToRemove)
        {
            _cache.TryRemove(key, out _);
            
            if (_fileLocks.TryRemove(key, out var semaphore))
            {
                semaphore.Dispose();
            }
        }
    }

    /// <summary>
    /// Gets the current number of cached entries (for diagnostics).
    /// </summary>
    /// <returns>The number of dumps with cached watch data.</returns>
    public int GetCacheCount() => _cache.Count;

    /// <summary>
    /// Gets the current number of file locks (for diagnostics).
    /// </summary>
    /// <returns>The number of active file locks.</returns>
    public int GetLockCount() => _fileLocks.Count;

    // === Private Methods ===

    private static void ValidateParameters(string userId, string dumpId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID cannot be empty", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(dumpId))
        {
            throw new ArgumentException("Dump ID cannot be empty", nameof(dumpId));
        }
    }

    private async Task<List<WatchExpression>> LoadWatchesInternalAsync(string userId, string dumpId)
    {
        var filePath = GetWatchesFilePath(userId, dumpId);
        
        if (!File.Exists(filePath))
        {
            return new List<WatchExpression>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<List<WatchExpression>>(json, JsonOptions) 
                   ?? new List<WatchExpression>();
        }
        catch (JsonException)
        {
            // If the file is corrupted, return empty list
            return new List<WatchExpression>();
        }
    }

    private async Task SaveWatchesInternalAsync(string userId, string dumpId, List<WatchExpression> watches)
    {
        var filePath = GetWatchesFilePath(userId, dumpId);
        var directory = Path.GetDirectoryName(filePath);
        
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(watches, JsonOptions);
        await File.WriteAllTextAsync(filePath, json);
    }
}

