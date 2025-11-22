using System.Text.Json;
using DebuggerMcp.Configuration;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp;

/// <summary>
/// Provides disk-based persistence for session metadata.
/// </summary>
/// <remarks>
/// <para>Sessions are stored as individual JSON files in a shared volume,
/// allowing multiple server instances to share session state.</para>
/// 
/// <para>File format: {sessionId}.json</para>
/// <para>Location: {SESSION_STORAGE_PATH}/{sessionId}.json</para>
/// 
/// <para><b>Thread Safety:</b> Operations are thread-safe within a single process using locks.
/// For cross-process safety in multi-server deployments, the atomic file rename pattern
/// is used (write to .tmp, then rename). However, simultaneous writes from multiple
/// servers to the same session file may result in last-write-wins behavior.</para>
/// 
/// <para><b>Best Practice:</b> Design your deployment so that each session is primarily
/// handled by one server at a time. Session migration between servers should be
/// infrequent (e.g., during failover or load balancing).</para>
/// </remarks>
public class PersistentSessionStore
{
    private readonly string _storagePath;
    private readonly ILogger<PersistentSessionStore> _logger;
    private readonly object _fileLock = new();
    private readonly string _serverId;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistentSessionStore"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="storagePath">Optional storage path. Defaults to SESSION_STORAGE_PATH env var.</param>
    public PersistentSessionStore(ILogger<PersistentSessionStore> logger, string? storagePath = null)
    {
        _logger = logger;
        _storagePath = storagePath ?? EnvironmentConfig.GetSessionStoragePath();
        _serverId = $"{Environment.MachineName}-{Environment.ProcessId}";
        
        // Ensure storage directory exists
        Directory.CreateDirectory(_storagePath);
        
        _logger.LogInformation("Session store initialized at: {Path} (Server: {ServerId})", _storagePath, _serverId);
    }

    /// <summary>
    /// Saves a session's metadata to disk.
    /// </summary>
    /// <param name="session">The session to save.</param>
    public void Save(DebuggerSession session)
    {
        var metadata = SessionMetadata.FromSession(session, _serverId);
        SaveMetadata(metadata);
    }

    /// <summary>
    /// Saves session metadata to disk.
    /// </summary>
    /// <param name="metadata">The metadata to save.</param>
    public void SaveMetadata(SessionMetadata metadata)
    {
        var filePath = GetFilePath(metadata.SessionId);
        if (filePath == null)
        {
            _logger.LogError("Cannot save session with invalid ID: {SessionId}", metadata.SessionId);
            throw new ArgumentException($"Invalid session ID format: {metadata.SessionId}");
        }
        
        var tempPath = filePath + ".tmp";
        
        try
        {
            var json = JsonSerializer.Serialize(metadata, JsonOptions);
            
            lock (_fileLock)
            {
                // Write to temp file first, then rename for atomic operation
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, filePath, overwrite: true);
            }
            
            _logger.LogDebug("Saved session {SessionId} to disk", metadata.SessionId);
        }
        catch (Exception ex)
        {
            // Clean up temp file if it exists
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
            
            _logger.LogError(ex, "Failed to save session {SessionId} to disk", metadata.SessionId);
            throw;
        }
    }

    /// <summary>
    /// Loads session metadata from disk.
    /// </summary>
    /// <param name="sessionId">The session ID to load.</param>
    /// <returns>The session metadata, or null if not found.</returns>
    public SessionMetadata? Load(string sessionId)
    {
        var filePath = GetFilePath(sessionId);
        
        // Invalid session ID format - cannot exist
        if (filePath == null)
        {
            return null;
        }
        
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            string json;
            lock (_fileLock)
            {
                json = File.ReadAllText(filePath);
            }
            
            var metadata = JsonSerializer.Deserialize<SessionMetadata>(json, JsonOptions);
            
            if (metadata == null)
            {
                _logger.LogWarning("Session file {SessionId} contained invalid or empty JSON", sessionId);
                return null;
            }
            
            _logger.LogDebug("Loaded session {SessionId} from disk (last server: {LastServerId})", 
                sessionId, metadata.LastServerId);
            
            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load session {SessionId} from disk", sessionId);
            return null;
        }
    }

    /// <summary>
    /// Loads all session metadata from disk.
    /// </summary>
    /// <returns>A list of all persisted session metadata.</returns>
    public List<SessionMetadata> LoadAll()
    {
        var sessions = new List<SessionMetadata>();
        
        try
        {
            var files = Directory.GetFiles(_storagePath, "*.json");
            
            foreach (var file in files)
            {
                try
                {
                    var sessionId = Path.GetFileNameWithoutExtension(file);
                    var metadata = Load(sessionId);
                    
                    if (metadata != null)
                    {
                        sessions.Add(metadata);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load session file: {File}", file);
                }
            }
            
            if (sessions.Count > 0)
            {
                _logger.LogDebug("Loaded {Count} sessions from disk", sessions.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate session files");
        }
        
        return sessions;
    }

    /// <summary>
    /// Deletes a session's metadata from disk.
    /// </summary>
    /// <param name="sessionId">The session ID to delete.</param>
    public void Delete(string sessionId)
    {
        var filePath = GetFilePath(sessionId);
        
        // Invalid session ID format - nothing to delete
        if (filePath == null)
        {
            return;
        }
        
        try
        {
            lock (_fileLock)
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _logger.LogDebug("Deleted session {SessionId} from disk", sessionId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete session {SessionId} from disk", sessionId);
        }
    }

    /// <summary>
    /// Checks if a session exists on disk.
    /// </summary>
    /// <param name="sessionId">The session ID to check.</param>
    /// <returns>True if the session exists on disk.</returns>
    public bool Exists(string sessionId)
    {
        var filePath = GetFilePath(sessionId);
        return filePath != null && File.Exists(filePath);
    }

    /// <summary>
    /// Cleans up sessions that have exceeded the inactivity threshold.
    /// </summary>
    /// <param name="inactivityThreshold">Maximum allowed inactivity duration.</param>
    /// <returns>Number of sessions cleaned up.</returns>
    public int CleanupExpiredSessions(TimeSpan inactivityThreshold)
    {
        var now = DateTime.UtcNow;
        var cleaned = 0;
        
        foreach (var metadata in LoadAll())
        {
            if (now - metadata.LastAccessedAt > inactivityThreshold)
            {
                Delete(metadata.SessionId);
                cleaned++;
                _logger.LogInformation("Cleaned up expired session {SessionId} (inactive since {LastAccessed})",
                    metadata.SessionId, metadata.LastAccessedAt);
            }
        }
        
        return cleaned;
    }

    /// <summary>
    /// Gets the file path for a session's metadata.
    /// </summary>
    /// <remarks>
    /// Validates that sessionId is a valid GUID to prevent path traversal attacks.
    /// Returns null for invalid session IDs.
    /// </remarks>
    private string? GetFilePath(string sessionId)
    {
        // Validate sessionId is a valid GUID to prevent path traversal
        if (!Guid.TryParse(sessionId, out _))
        {
            _logger.LogDebug("Invalid session ID format (not a GUID): {SessionId}", sessionId);
            return null;
        }
        
        return Path.Combine(_storagePath, $"{sessionId}.json");
    }

    /// <summary>
    /// Gets the current server ID.
    /// </summary>
    public string ServerId => _serverId;

    /// <summary>
    /// Gets the storage path.
    /// </summary>
    public string StoragePath => _storagePath;
}

