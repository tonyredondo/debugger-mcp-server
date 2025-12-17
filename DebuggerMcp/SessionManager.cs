using System.Collections.Concurrent;
using DebuggerMcp.Analysis;
using DebuggerMcp.Configuration;
using DebuggerMcp.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp;

/// <summary>
/// Manages multiple debugging sessions for different users (multitenant).
/// </summary>
/// <remarks>
/// This class provides thread-safe session management, including creation,
/// retrieval, and cleanup of inactive sessions. It enforces per-user limits
/// to prevent resource exhaustion.
/// 
/// Sessions are persisted to disk via <see cref="PersistentSessionStore"/>, allowing:
/// - Sessions to survive server restarts
/// - Sessions to be accessed from different server instances (shared volume)
/// - Default session timeout of 24 hours
/// 
/// Configuration via environment variables (see <see cref="EnvironmentConfig"/>):
/// - MAX_SESSIONS_PER_USER: Maximum concurrent sessions per user (default: 10)
/// - MAX_TOTAL_SESSIONS: Maximum total concurrent sessions (default: 50)
/// - SESSION_STORAGE_PATH: Directory for session persistence (default: /app/sessions)
/// - SESSION_INACTIVITY_THRESHOLD_MINUTES: Session timeout (default: 1440 = 24 hours)
/// </remarks>
public class DebuggerSessionManager
{
    /// <summary>
    /// Maximum number of concurrent sessions allowed per user.
    /// </summary>
    private readonly int _maxSessionsPerUser;

    /// <summary>
    /// Maximum total number of concurrent sessions across all users.
    /// </summary>
    private readonly int _maxTotalSessions;

    /// <summary>
    /// Thread-safe dictionary storing all active sessions (in-memory).
    /// </summary>
    private readonly ConcurrentDictionary<string, DebuggerSession> _sessions = new();

    /// <summary>
    /// Lock object for thread-safe session creation.
    /// </summary>
    private readonly object _creationLock = new();

    /// <summary>
    /// Base directory for dump file storage.
    /// </summary>
    private readonly string _dumpStoragePath;

    /// <summary>
    /// Logger factory for creating debugger loggers.
    /// </summary>
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Logger for this class.
    /// </summary>
    private readonly ILogger<DebuggerSessionManager> _logger;

    /// <summary>
    /// Persistent session store for disk-based session metadata.
    /// </summary>
    private readonly PersistentSessionStore _sessionStore;

    private readonly Func<ILoggerFactory, IDebuggerManager> _debuggerFactory;

    /// <summary>
    /// Optional callback invoked when a session is closed.
    /// Used to clean up related resources like symbol paths.
    /// </summary>
    public Action<string>? OnSessionClosed { get; set; }

    /// <summary>
    /// Optional callback invoked when a session is restored from disk.
    /// Parameters: (sessionId, dumpId, manager) - used to configure symbol paths.
    /// </summary>
    public Action<string, string?, IDebuggerManager>? OnSessionRestored { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DebuggerSessionManager"/> class.
    /// </summary>
    /// <param name="dumpStoragePath">
    /// Optional path for dump storage. If not provided, uses the centralized configuration
    /// from <see cref="EnvironmentConfig"/>.
    /// </param>
    /// <param name="loggerFactory">
    /// Optional logger factory for creating debugger loggers. If not provided, uses NullLoggerFactory.
    /// </param>
    /// <param name="sessionStoragePath">
    /// Optional path for session storage. If not provided, uses the centralized configuration
    /// from <see cref="EnvironmentConfig"/>. Useful for tests to use isolated storage.
    /// If a custom dumpStoragePath is provided but sessionStoragePath is not, sessions are stored
    /// in a "sessions" subdirectory of the dump storage path.
    /// </param>
    /// <param name="debuggerFactory">
    /// Optional factory for creating debugger managers.
    /// This is primarily intended for unit tests to avoid spawning real debugger processes.
    /// </param>
    public DebuggerSessionManager(
        string? dumpStoragePath = null,
        ILoggerFactory? loggerFactory = null,
        string? sessionStoragePath = null,
        Func<ILoggerFactory, IDebuggerManager>? debuggerFactory = null)
    {
        _dumpStoragePath = dumpStoragePath ?? EnvironmentConfig.GetDumpStoragePath();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<DebuggerSessionManager>();
        _debuggerFactory = debuggerFactory ?? DebuggerFactory.CreateDebugger;

        // Determine session storage path:
        // 1. Use explicit sessionStoragePath if provided
        // 2. If custom dumpStoragePath was provided, use sessions subdirectory (for test isolation)
        // 3. Otherwise use centralized configuration
        var effectiveSessionPath = sessionStoragePath
            ?? (dumpStoragePath != null ? Path.Combine(dumpStoragePath, "sessions") : null);

        // Initialize persistent session store with resolved path
        _sessionStore = new PersistentSessionStore(
            _loggerFactory.CreateLogger<PersistentSessionStore>(),
            effectiveSessionPath);

        // Read session limits from centralized configuration
        _maxSessionsPerUser = EnvironmentConfig.GetMaxSessionsPerUser();
        _maxTotalSessions = EnvironmentConfig.GetMaxTotalSessions();

        _logger.LogInformation(
            "Session manager initialized. Max per user: {MaxPerUser}, Max total: {MaxTotal}, Storage: {StoragePath}",
            _maxSessionsPerUser, _maxTotalSessions, _sessionStore.StoragePath);
    }



    /// <summary>
    /// Creates a new debugging session for the specified user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <returns>The newly created session ID.</returns>
    /// <exception cref="ArgumentException">Thrown when userId is null or empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the system has reached the maximum total sessions.
    /// </exception>
    public string CreateSession(string userId)
    {
        // Validate userId parameter
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
        }

        // Use lock to ensure thread-safe session creation
        lock (_creationLock)
        {
            // Load all persisted sessions once for efficient counting
            var inactivityThreshold = EnvironmentConfig.GetSessionInactivityThreshold();
            var now = DateTime.UtcNow;
            var persistedSessions = _sessionStore.LoadAll()
                .Where(m => now - m.LastAccessedAt <= inactivityThreshold) // Only count non-expired
                .ToList();

            // Get non-expired in-memory session IDs
            var activeInMemorySessionIds = new HashSet<string>(
                _sessions.Values
                    .Where(s => now - s.LastAccessedAt <= inactivityThreshold)
                    .Select(s => s.SessionId));

            // Merge persisted and in-memory session IDs to avoid double counting
            var allSessionIds = new HashSet<string>(activeInMemorySessionIds);
            foreach (var persisted in persistedSessions)
            {
                allSessionIds.Add(persisted.SessionId);
            }

            // Count unique sessions per user (combining in-memory and persisted)
            var userSessionCount = _sessions.Values
                .Count(s => s.UserId == userId && now - s.LastAccessedAt <= inactivityThreshold);
            foreach (var persisted in persistedSessions.Where(m => m.UserId == userId))
            {
                if (!activeInMemorySessionIds.Contains(persisted.SessionId))
                {
                    // Only count persisted sessions not already tracked in-memory to prevent double counting.
                    userSessionCount++;
                }
            }

            // If the user is at/over the limit, evict the oldest sessions (rollover).
            if (userSessionCount >= _maxSessionsPerUser)
            {
                // Build a unique set of the user's non-expired sessions (avoid double counting persisted + in-memory).
                var userSessions = new Dictionary<string, (DateTime CreatedAt, string SessionId)>(StringComparer.Ordinal);
                foreach (var s in _sessions.Values.Where(s => s.UserId == userId && now - s.LastAccessedAt <= inactivityThreshold))
                {
                    userSessions[s.SessionId] = (s.CreatedAt, s.SessionId);
                }

                foreach (var persisted in persistedSessions.Where(m => m.UserId == userId))
                {
                    if (!userSessions.ContainsKey(persisted.SessionId))
                    {
                        userSessions[persisted.SessionId] = (persisted.CreatedAt, persisted.SessionId);
                    }
                }

                while (userSessionCount >= _maxSessionsPerUser)
                {
                    if (userSessions.Count == 0)
                    {
                        break;
                    }

                    // Oldest is defined by CreatedAt (tie-break by SessionId for determinism).
                    var oldest = userSessions.Values
                        .OrderBy(s => s.CreatedAt)
                        .ThenBy(s => s.SessionId, StringComparer.Ordinal)
                        .FirstOrDefault();

                    if (string.IsNullOrWhiteSpace(oldest.SessionId))
                    {
                        break;
                    }

                    try
                    {
                        CloseSession(oldest.SessionId, userId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to evict oldest session {SessionId} for user {UserId}", oldest.SessionId, userId);
                        break;
                    }

                    userSessions.Remove(oldest.SessionId);
                    allSessionIds.Remove(oldest.SessionId);
                    userSessionCount = Math.Max(0, userSessionCount - 1);
                }
            }

            // Total unique sessions
            if (allSessionIds.Count >= _maxTotalSessions)
            {
                throw new InvalidOperationException(
                    $"System has reached the maximum number of total sessions ({_maxTotalSessions})");
            }

            // Generate a unique session ID
            var sessionId = Guid.NewGuid().ToString();

            // Create the session with a new debugger manager instance
            // The factory automatically selects WinDbg (Windows) or LLDB (Linux/macOS)
            var session = new DebuggerSession
            {
                SessionId = sessionId,
                UserId = userId,
                Manager = _debuggerFactory(_loggerFactory),
                CreatedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow
            };

            // Add the session to the in-memory dictionary
            if (!_sessions.TryAdd(sessionId, session))
            {
                // This should never happen due to GUID uniqueness, but handle it anyway
                session.Dispose();
                throw new InvalidOperationException("Failed to create session due to ID collision");
            }

            // Persist the session to disk
            try
            {
                _sessionStore.Save(session);
            }
            catch (Exception ex)
            {
                // If persistence fails, remove from memory to keep state consistent
                _sessions.TryRemove(sessionId, out _);
                session.Dispose();
                _logger.LogError(ex, "Failed to persist new session {SessionId}, rolling back", sessionId);
                throw;
            }

            _logger.LogInformation("Created session {SessionId} for user {UserId}", sessionId, userId);

            return sessionId;
        }
    }

    /// <summary>
    /// Retrieves a session by its ID and validates user ownership.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="userId">The user identifier for ownership validation.</param>
    /// <returns>The debugger manager instance for the session.</returns>
    /// <exception cref="ArgumentException">Thrown when sessionId or userId is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the session is not found.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when the user does not own the session.
    /// </exception>
    /// <remarks>
    /// If the session is not in memory but exists on disk (e.g., from another server
    /// or after a restart), it will be restored automatically. The previously opened
    /// dump file will also be reopened if available.
    /// </remarks>
    public IDebuggerManager GetSession(string sessionId, string userId)
    {
        // Validate parameters
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
        }

        // Try to retrieve the session from memory
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            // Session not in memory - check if it exists on disk first
            var metadata = _sessionStore.Load(sessionId);

            if (metadata == null)
            {
                throw new InvalidOperationException($"Session '{sessionId}' not found");
            }

            // Validate user ownership before attempting restore
            if (metadata.UserId != userId)
            {
                throw new UnauthorizedAccessException(
                    $"User '{userId}' does not have access to session '{sessionId}'");
            }

            // Check if session has expired
            var inactivityThreshold = EnvironmentConfig.GetSessionInactivityThreshold();
            if (DateTime.UtcNow - metadata.LastAccessedAt > inactivityThreshold)
            {
                // Remove stale disk entry so future lookups do not keep retrying it.
                _sessionStore.Delete(sessionId);
                throw new InvalidOperationException($"Session '{sessionId}' has expired");
            }

            // Now restore the session (pass pre-validated metadata to avoid redundant disk read)
            session = RestoreSessionFromDisk(sessionId, userId, metadata);

            if (session == null)
            {
                throw new InvalidOperationException($"Failed to restore session '{sessionId}'");
            }
        }

        // Validate user ownership (for in-memory sessions)
        if (session.UserId != userId)
        {
            throw new UnauthorizedAccessException(
                $"User '{userId}' does not have access to session '{sessionId}'");
        }

        // Update last accessed time
        session.LastAccessedAt = DateTime.UtcNow;

        // Persist the updated access time
        // Note: If this fails, the session still works (just with stale LastAccessedAt on disk)
        try
        {
            _sessionStore.Save(session);
        }
        catch (Exception ex)
        {
            // Log but don't fail - the session is valid, just the access time won't be persisted
            _logger.LogWarning(ex, "Failed to persist updated access time for session {SessionId}", sessionId);
        }

        return session.Manager;
    }

    /// <summary>
    /// Restores a session from disk storage.
    /// </summary>
    /// <param name="sessionId">The session ID to restore.</param>
    /// <param name="userId">The user ID for validation.</param>
    /// <param name="preloadedMetadata">Optional pre-loaded metadata to avoid redundant disk reads.</param>
    /// <returns>The restored session, or null if not found or validation fails.</returns>
    private DebuggerSession? RestoreSessionFromDisk(string sessionId, string userId, SessionMetadata? preloadedMetadata = null)
    {
        var metadata = preloadedMetadata ?? _sessionStore.Load(sessionId);

        if (metadata == null)
        {
            return null;
        }

        // Validate user ownership before restoring (skip if already validated by caller)
        if (preloadedMetadata == null && metadata.UserId != userId)
        {
            _logger.LogWarning(
                "Session {SessionId} found on disk but owned by different user (expected: {Expected}, actual: {Actual})",
                sessionId, userId, metadata.UserId);
            return null;
        }

        // Check if session has expired (skip if already validated by caller)
        if (preloadedMetadata == null)
        {
            var inactivityThreshold = EnvironmentConfig.GetSessionInactivityThreshold();
            if (DateTime.UtcNow - metadata.LastAccessedAt > inactivityThreshold)
            {
                _logger.LogInformation(
                    "Session {SessionId} found on disk but has expired (last accessed: {LastAccessed})",
                    sessionId, metadata.LastAccessedAt);
                _sessionStore.Delete(sessionId); // Delete expired disk artifacts to keep storage tidy.
                return null;
            }
        }

        _logger.LogInformation(
            "Restoring session {SessionId} from disk (last server: {LastServer})",
            sessionId, metadata.LastServerId);

        // Create a new debugger manager for this session
        var session = new DebuggerSession
        {
            SessionId = metadata.SessionId,
            UserId = metadata.UserId,
            Manager = _debuggerFactory(_loggerFactory),
            CreatedAt = metadata.CreatedAt,
            LastAccessedAt = DateTime.UtcNow,
            CurrentDumpId = metadata.CurrentDumpId
        };

        // Add to in-memory dictionary
        if (!_sessions.TryAdd(sessionId, session))
        {
            // Another thread may have restored it concurrently
            session.Dispose();
            return _sessions.TryGetValue(sessionId, out var existing) ? existing : null;
        }

        // Try to reopen the dump file if one was open
        if (!string.IsNullOrEmpty(metadata.CurrentDumpPath) && File.Exists(metadata.CurrentDumpPath))
        {
            try
            {
                // Initialize the debugger first (required before opening dump)
                // Use Task.Run to avoid potential deadlocks with sync-over-async in ASP.NET contexts
                if (!session.Manager.IsInitialized)
                {
                    _logger.LogInformation("Initializing debugger for restored session {SessionId}", sessionId);
                    Task.Run(() => session.Manager.InitializeAsync()).GetAwaiter().GetResult();
                }

                // Check if there's a custom executable for this dump (standalone apps)
                string? executablePath = null;
                var dumpDir = Path.GetDirectoryName(metadata.CurrentDumpPath);
                if (dumpDir != null && !string.IsNullOrEmpty(metadata.CurrentDumpId))
                {
                    // Check both naming conventions for metadata
                    var dumpMetadataPath = Path.Combine(dumpDir, $"{metadata.CurrentDumpId}.json");
                    var altMetadataPath = Path.Combine(dumpDir, $".metadata_{metadata.CurrentDumpId}.json");
                    var actualDumpMetadataPath = File.Exists(dumpMetadataPath) ? dumpMetadataPath :
                                                 File.Exists(altMetadataPath) ? altMetadataPath : null;
                    
                    if (actualDumpMetadataPath != null)
                    {
                        try
                        {
                            var dumpMetadataJson = File.ReadAllText(actualDumpMetadataPath);
                            var dumpMeta = System.Text.Json.JsonSerializer.Deserialize<Controllers.DumpMetadata>(dumpMetadataJson);
                            if (dumpMeta?.ExecutablePath != null && File.Exists(dumpMeta.ExecutablePath))
                            {
                                executablePath = dumpMeta.ExecutablePath;
                                _logger.LogInformation("[SessionManager] Found custom executable for standalone app: {ExecutablePath}", executablePath);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "[SessionManager] Failed to read dump metadata for executable path");
                        }
                    }
                }

                _logger.LogInformation("Reopening dump file: {DumpPath}", metadata.CurrentDumpPath);
                session.Manager.OpenDumpFile(metadata.CurrentDumpPath, executablePath);
                // Note: SOS is now auto-loaded by OpenDumpFile if .NET runtime is detected

                // Open ClrMD for metadata enrichment (after debugger opens the dump)
                try
                {
                    var clrMdAnalyzer = new ClrMdAnalyzer(_logger);
                    if (clrMdAnalyzer.OpenDump(metadata.CurrentDumpPath))
                    {
                        session.ClrMdAnalyzer = clrMdAnalyzer;
                        _logger.LogInformation("[SessionManager] ClrMD analyzer attached for metadata enrichment");

	                        // Set up SequencePointResolver for source location resolution in ClrStack
	                        try
	                        {
	                            var seqResolver = new SourceLink.SequencePointResolver(_logger);

	                            var pdbPaths = SourceLink.PdbSearchPathBuilder.BuildExistingPaths(
	                                metadata.CurrentDumpPath,
	                                dumpId: metadata.CurrentDumpId,
	                                runtime: clrMdAnalyzer.Runtime);

	                            if (pdbPaths.Count > 0)
	                            {
	                                _logger.LogInformation(
	                                    "[SessionManager] PDB search paths for ClrStack ({Count}): {Paths}",
	                                    pdbPaths.Count,
	                                    string.Join(" | ", pdbPaths));
	                            }

	                            foreach (var path in pdbPaths)
	                            {
	                                seqResolver.AddPdbSearchPath(path);
	                            }
	                            
	                            clrMdAnalyzer.SetSequencePointResolver(seqResolver);
	                            _logger.LogDebug("[SessionManager] SequencePointResolver configured for ClrStack");
	                        }
                        catch (Exception seqEx)
                        {
                            _logger.LogDebug(seqEx, "[SessionManager] SequencePointResolver setup failed, ClrStack will work without source locations");
                        }
                    }
                    else
                    {
                        _logger.LogDebug("[SessionManager] ClrMD could not open dump (non-.NET or architecture mismatch)");
                        clrMdAnalyzer.Dispose();
                    }
                }
                catch (Exception clrMdEx)
                {
                    // Don't fail session restore if ClrMD fails - it's optional enrichment
                    _logger.LogDebug(clrMdEx, "[SessionManager] ClrMD initialization failed, continuing without metadata enrichment");
                }

                // Note: Symbol paths are NOT automatically reconfigured during session restore.
                // The SymbolManager is not available in SessionManager. Users may need to manually
                // reconfigure symbols if using custom symbol paths. Default symbol servers will still work.
                _logger.LogInformation(
                    "Session {SessionId} restored with dump {DumpPath}",
                    sessionId, metadata.CurrentDumpPath);
                
                // Invoke callback to configure symbol paths (if set)
                try
                {
                    OnSessionRestored?.Invoke(sessionId, metadata.CurrentDumpId, session.Manager);
                }
                catch (Exception callbackEx)
                {
                    _logger.LogWarning(callbackEx, "[SessionManager] OnSessionRestored callback failed for session {SessionId}", sessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reopen dump file {DumpPath} for session {SessionId}",
                    metadata.CurrentDumpPath, sessionId);
                // Session is still valid, just without the dump
                session.CurrentDumpId = null;
            }
        }

        // Always persist the restored session to update LastAccessedAt and LastServerId
        _sessionStore.Save(session);

        return session;
    }

    /// <summary>
    /// Retrieves session information without returning the manager.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="userId">The user identifier for ownership validation.</param>
    /// <returns>The session information.</returns>
    /// <exception cref="ArgumentException">Thrown when sessionId or userId is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the session is not found.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when the user does not own the session.
    /// </exception>
    /// <remarks>
    /// This method will restore the session from disk if not in memory,
    /// similar to <see cref="GetSession"/>.
    /// </remarks>
    public DebuggerSession GetSessionInfo(string sessionId, string userId)
    {
        // Validate parameters
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
        }

        // Try to retrieve the session from memory
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            // Session not in memory - check if it exists on disk first
            var metadata = _sessionStore.Load(sessionId);

            if (metadata == null)
            {
                throw new InvalidOperationException($"Session '{sessionId}' not found");
            }

            // Validate user ownership before attempting restore
            if (metadata.UserId != userId)
            {
                throw new UnauthorizedAccessException(
                    $"User '{userId}' does not have access to session '{sessionId}'");
            }

            // Check if session has expired
            var inactivityThreshold = EnvironmentConfig.GetSessionInactivityThreshold();
            if (DateTime.UtcNow - metadata.LastAccessedAt > inactivityThreshold)
            {
                _sessionStore.Delete(sessionId);
                throw new InvalidOperationException($"Session '{sessionId}' has expired");
            }

            // Now restore the session (pass pre-validated metadata to avoid redundant disk read)
            session = RestoreSessionFromDisk(sessionId, userId, metadata);

            if (session == null)
            {
                throw new InvalidOperationException($"Failed to restore session '{sessionId}'");
            }
        }

        // Validate user ownership (for in-memory sessions)
        if (session.UserId != userId)
        {
            throw new UnauthorizedAccessException(
                $"User '{userId}' does not have access to session '{sessionId}'");
        }

        // Note: GetSessionInfo does NOT update LastAccessedAt for existing in-memory sessions
        // to avoid unnecessary disk writes for read-only operations.
        // However, if the session was restored from disk, RestoreSessionFromDisk does persist
        // the updated LastAccessedAt and LastServerId as part of the restoration process.

        return session;
    }

    /// <summary>
    /// Closes and removes a session from both memory and disk.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="userId">The user identifier for ownership validation.</param>
    /// <exception cref="ArgumentException">Thrown when sessionId or userId is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the session is not found.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when the user does not own the session.
    /// </exception>
    public void CloseSession(string sessionId, string userId)
    {
        // Validate parameters
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
        }

        // Try to get session from memory first
        DebuggerSession? session = null;
        if (_sessions.TryGetValue(sessionId, out var memorySession))
        {
            session = memorySession;
        }
        else
        {
            // Check if it exists on disk
            var metadata = _sessionStore.Load(sessionId);
            if (metadata == null)
            {
                throw new InvalidOperationException($"Session '{sessionId}' not found");
            }

            // Validate ownership using disk metadata
            if (metadata.UserId != userId)
            {
                throw new UnauthorizedAccessException(
                    $"User '{userId}' does not have access to session '{sessionId}'");
            }
        }

        // Validate user ownership if we have an in-memory session
        if (session != null && session.UserId != userId)
        {
            throw new UnauthorizedAccessException(
                $"User '{userId}' does not have access to session '{sessionId}'");
        }

        // Remove from memory if present
        if (_sessions.TryRemove(sessionId, out var removedSession))
        {
            // Dispose the session and its resources
            removedSession.Dispose();
        }

        // Always remove from disk
        _sessionStore.Delete(sessionId);

        _logger.LogInformation("Closed session {SessionId} for user {UserId}", sessionId, userId);

        // Notify listeners to clean up related resources (e.g., symbol paths)
        OnSessionClosed?.Invoke(sessionId);
    }

    /// <summary>
    /// Lists all sessions for a specific user, including persisted sessions.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <returns>A list of session information for the user.</returns>
    /// <exception cref="ArgumentException">Thrown when userId is null or empty.</exception>
    /// <remarks>
    /// <para>This method returns both in-memory and persisted sessions.</para>
    /// <para><b>Warning:</b> For persisted sessions that are not in memory, the 
    /// <see cref="DebuggerSession.Manager"/> property will be null. These sessions
    /// will be fully restored (with a valid Manager) when accessed via <see cref="GetSession"/>.</para>
    /// <para>Callers should only use the returned sessions for displaying metadata
    /// (SessionId, UserId, CreatedAt, LastAccessedAt, CurrentDumpId).</para>
    /// </remarks>
    public List<DebuggerSession> ListUserSessions(string userId)
    {
        // Validate userId parameter
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
        }

        var inactivityThreshold = EnvironmentConfig.GetSessionInactivityThreshold();
        var now = DateTime.UtcNow;

        // Get in-memory sessions, filtering out expired ones
        var inMemorySessions = _sessions.Values
            .Where(s => s.UserId == userId && now - s.LastAccessedAt <= inactivityThreshold)
            .ToDictionary(s => s.SessionId);

        // Get persisted sessions and merge

        foreach (var metadata in _sessionStore.LoadAll().Where(m => m.UserId == userId))
        {
            // Skip expired sessions
            if (now - metadata.LastAccessedAt > inactivityThreshold)
            {
                continue;
            }

            // Skip if already in memory
            if (inMemorySessions.ContainsKey(metadata.SessionId))
            {
                continue;
            }

            // Create a lightweight session object for listing
            // (Manager will be created when the session is actually accessed)
            var session = new DebuggerSession
            {
                SessionId = metadata.SessionId,
                UserId = metadata.UserId,
                Manager = null!, // Will be created on access
                CreatedAt = metadata.CreatedAt,
                LastAccessedAt = metadata.LastAccessedAt,
                CurrentDumpId = metadata.CurrentDumpId
            };

            inMemorySessions[metadata.SessionId] = session;
        }

        // Return all sessions ordered by last access time
        return inMemorySessions.Values
            .OrderByDescending(s => s.LastAccessedAt)
            .ToList();
    }

    /// <summary>
    /// Cleans up inactive sessions that haven't been accessed for the specified duration.
    /// </summary>
    /// <param name="inactivityThreshold">
    /// The time span after which an inactive session should be cleaned up.
    /// </param>
    /// <returns>The number of sessions that were cleaned up.</returns>
    /// <remarks>
    /// This method cleans up both in-memory and persisted sessions.
    /// </remarks>
    public int CleanupInactiveSessions(TimeSpan inactivityThreshold)
    {
        var now = DateTime.UtcNow;
        var cleanedCount = 0;

        // Track which sessions we clean up in-memory to avoid redundant disk operations
        var cleanedSessionIds = new HashSet<string>();

        // Clean up in-memory sessions
        var inactiveSessions = _sessions.Values
            .Where(s => now - s.LastAccessedAt > inactivityThreshold)
            .ToList();

        foreach (var session in inactiveSessions)
        {
            if (_sessions.TryRemove(session.SessionId, out var removedSession))
            {
                removedSession.Dispose();

                // Also remove from disk
                _sessionStore.Delete(session.SessionId);
                cleanedSessionIds.Add(session.SessionId);

                // Notify listeners to clean up related resources (e.g., symbol paths)
                OnSessionClosed?.Invoke(session.SessionId);

                cleanedCount++;

                _logger.LogDebug("Cleaned up inactive in-memory session {SessionId}", session.SessionId);
            }
        }

        // Also clean up persisted sessions that may not be in memory
        // (e.g., sessions from other servers that have expired)
        // Skip sessions we already cleaned up above to avoid redundant file operations
        foreach (var metadata in _sessionStore.LoadAll())
        {
            // Skip if already cleaned up
            if (cleanedSessionIds.Contains(metadata.SessionId))
            {
                continue;
            }

            // Check if expired
            if (now - metadata.LastAccessedAt > inactivityThreshold)
            {
                _sessionStore.Delete(metadata.SessionId);
                cleanedCount++;
                _logger.LogInformation("Cleaned up expired persisted session {SessionId} (inactive since {LastAccessed})",
                    metadata.SessionId, metadata.LastAccessedAt);
            }
        }

        return cleanedCount;
    }

    /// <summary>
    /// Persists the current state of a session to disk.
    /// </summary>
    /// <param name="sessionId">The session ID to persist.</param>
    /// <remarks>
    /// Call this method after making changes to a session that should be persisted,
    /// such as opening or closing a dump file.
    /// </remarks>
    public void PersistSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            _sessionStore.Save(session);
        }
        else
        {
            _logger.LogDebug("PersistSession called for session {SessionId} not in memory (may be disk-only)", sessionId);
        }
    }

    /// <summary>
    /// Gets statistics about current sessions.
    /// </summary>
    /// <returns>A dictionary containing session statistics.</returns>
    /// <remarks>
    /// Statistics include both in-memory and persisted sessions.
    /// </remarks>
    public Dictionary<string, object> GetStatistics()
    {
        // Get all sessions (in-memory and persisted)
        var allMetadata = _sessionStore.LoadAll();
        var inactivityThreshold = EnvironmentConfig.GetSessionInactivityThreshold();
        var now = DateTime.UtcNow;

        // Filter out expired sessions
        var activeSessions = allMetadata
            .Where(m => now - m.LastAccessedAt <= inactivityThreshold)
            .ToList();

        var stats = new Dictionary<string, object>
        {
            ["TotalSessions"] = activeSessions.Count,
            ["InMemorySessions"] = _sessions.Count,
            ["PersistedSessions"] = allMetadata.Count,
            ["MaxSessionsPerUser"] = _maxSessionsPerUser,
            ["MaxTotalSessions"] = _maxTotalSessions,
            ["UniqueUsers"] = activeSessions.Select(s => s.UserId).Distinct().Count(),
            ["SessionStoragePath"] = _sessionStore.StoragePath,
            ["ServerId"] = _sessionStore.ServerId
        };

        // Calculate sessions per user
        var sessionsPerUser = activeSessions
            .GroupBy(s => s.UserId)
            .ToDictionary(g => g.Key, g => g.Count());

        stats["SessionsPerUser"] = sessionsPerUser;

        return stats;
    }

    /// <summary>
    /// Gets the user ID associated with a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>The user ID that owns the session.</returns>
    /// <exception cref="ArgumentException">Thrown when sessionId is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the session is not found or has expired.</exception>
    /// <remarks>
    /// This method checks both in-memory and persisted sessions.
    /// Expired sessions are cleaned up and will throw as "not found".
    /// </remarks>
    public string GetSessionUserId(string sessionId)
    {
        // Validate parameter
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));
        }

        // Try to retrieve the session from memory first
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            // Check if expired (in-memory sessions should be kept in sync, but check anyway)
            var inactivityThreshold = EnvironmentConfig.GetSessionInactivityThreshold();
            if (DateTime.UtcNow - session.LastAccessedAt > inactivityThreshold)
            {
                // Clean up the expired session
                if (_sessions.TryRemove(sessionId, out var expiredSession))
                {
                    expiredSession.Dispose();
                    _sessionStore.Delete(sessionId);
                    OnSessionClosed?.Invoke(sessionId);
                }
                throw new InvalidOperationException($"Session '{sessionId}' has expired");
            }
            return session.UserId;
        }

        // Try to load from disk
        var metadata = _sessionStore.Load(sessionId);
        if (metadata != null)
        {
            // Check if expired
            var inactivityThreshold = EnvironmentConfig.GetSessionInactivityThreshold();
            if (DateTime.UtcNow - metadata.LastAccessedAt > inactivityThreshold)
            {
                _sessionStore.Delete(sessionId);
                throw new InvalidOperationException($"Session '{sessionId}' has expired");
            }
            return metadata.UserId;
        }

        throw new InvalidOperationException($"Session '{sessionId}' not found");
    }

    /// <summary>
    /// Gets the dump file path for a given dump ID and user.
    /// </summary>
    /// <param name="dumpId">The dump identifier.</param>
    /// <param name="userId">The user identifier who owns the dump.</param>
    /// <returns>The full path to the dump file.</returns>
    /// <exception cref="ArgumentException">Thrown when dumpId or userId is null, empty, or contains invalid characters.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the dump file does not exist.</exception>
    /// <remarks>
    /// This method resolves a dump ID (returned from the upload API) to its file path.
    /// The path follows the pattern: {dumpStoragePath}/{userId}/{dumpId}.dmp
    /// 
    /// Security: Both userId and dumpId are sanitized to prevent path traversal attacks.
    /// </remarks>
    public string GetDumpPath(string dumpId, string userId)
    {
        // Sanitize parameters to prevent path traversal attacks
        // This also validates that they are not null or empty
        var sanitizedUserId = PathSanitizer.SanitizeIdentifier(userId, nameof(userId));
        var sanitizedDumpId = PathSanitizer.SanitizeIdentifier(dumpId, nameof(dumpId));

        // Construct the file path following the same pattern as DumpController
        var filePath = Path.Combine(_dumpStoragePath, sanitizedUserId, $"{sanitizedDumpId}.dmp");

        // Verify the file exists
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                $"Dump file not found for dumpId '{sanitizedDumpId}' and userId '{sanitizedUserId}'",
                filePath);
        }

        return filePath;
    }

    /// <summary>
    /// Checks if a dump file exists for the given dump ID and user.
    /// </summary>
    /// <param name="dumpId">The dump identifier.</param>
    /// <param name="userId">The user identifier who owns the dump.</param>
    /// <returns>True if the dump file exists, false otherwise.</returns>
    /// <exception cref="ArgumentException">Thrown when dumpId or userId is null, empty, or contains invalid characters.</exception>
    /// <remarks>
    /// Security: Both userId and dumpId are sanitized to prevent path traversal attacks.
    /// </remarks>
    public bool DumpExists(string dumpId, string userId)
    {
        // Sanitize parameters to prevent path traversal attacks
        // This also validates that they are not null or empty
        var sanitizedUserId = PathSanitizer.SanitizeIdentifier(userId, nameof(userId));
        var sanitizedDumpId = PathSanitizer.SanitizeIdentifier(dumpId, nameof(dumpId));

        // Construct the file path and check existence
        var filePath = Path.Combine(_dumpStoragePath, sanitizedUserId, $"{sanitizedDumpId}.dmp");
        return File.Exists(filePath);
    }

    /// <summary>
    /// Gets the configured dump storage path.
    /// </summary>
    /// <returns>The base directory where dump files are stored.</returns>
    public string GetDumpStoragePath() => _dumpStoragePath;

}
