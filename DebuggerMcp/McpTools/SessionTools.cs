using System.ComponentModel;
using DebuggerMcp.Security;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DebuggerMcp.McpTools;

/// <summary>
/// MCP tools for managing debugging sessions.
/// </summary>
/// <remarks>
/// Provides tools for:
/// <list type="bullet">
/// <item><description>Creating new debugging sessions</description></item>
/// <item><description>Listing active sessions</description></item>
/// <item><description>Getting session/debugger information</description></item>
/// <item><description>Closing sessions</description></item>
/// </list>
/// </remarks>
[McpServerToolType]
public class SessionTools(
    DebuggerSessionManager sessionManager,
    SymbolManager symbolManager,
    WatchStore watchStore,
    ILogger<SessionTools> logger)
    : DebuggerToolsBase(sessionManager, symbolManager, watchStore, logger)
{
    /// <summary>
    /// Creates a new debugging session for a user.
    /// </summary>
    /// <param name="userId">Unique identifier for the user.</param>
    /// <returns>The session ID that should be used in subsequent operations.</returns>
    /// <remarks>
    /// Each user can have multiple concurrent sessions (up to 5).
    /// The session ID must be provided to all other tools to identify which session to operate on.
    /// Sessions are automatically cleaned up after 30 minutes of inactivity.
    /// </remarks>
    [McpServerTool, Description("Create a new debugging session. Returns a sessionId that must be used in all subsequent operations.")]
    public string CreateSession(
        [Description("Unique identifier for the user (e.g., email, username)")] string userId)
    {
        try
        {
            // Validate and sanitize input parameter to prevent path traversal
            var sanitizedUserId = SanitizeUserId(userId);

            // Create a new session through the SessionManager
            // This will allocate a new debugger instance for this session
            var sessionId = SessionManager.CreateSession(sanitizedUserId);
            
            return $"Session created successfully. SessionId: {sessionId}. Use this sessionId in all subsequent operations.";
        }
        catch (InvalidOperationException ex)
        {
            // Return the actual error message (e.g., "max sessions reached")
            // so the client can take appropriate action
            Logger.LogWarning(ex, "[CreateSession] Session creation failed");
            return $"Error: {ex.Message}. Use 'list_sessions' to see active sessions and 'close_session' to close unused ones.";
        }
        catch (ArgumentException ex)
        {
            Logger.LogWarning(ex, "[CreateSession] Invalid user ID");
            return $"Error: Invalid user ID - {ex.Message}";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[CreateSession] Unexpected error");
            return $"Error: Failed to create session - {ex.Message}";
        }
    }

    /// <summary>
    /// Closes a debugging session and releases all associated resources.
    /// </summary>
    /// <param name="sessionId">The session ID to close.</param>
    /// <param name="userId">The user ID that owns the session (for security validation).</param>
    /// <returns>Confirmation message.</returns>
    /// <remarks>
    /// This will:
    /// - Close any open dump files
    /// - Terminate the debugger process
    /// - Release all associated resources
    /// - Remove the session from the session manager
    /// 
    /// After closing a session, the sessionId cannot be reused.
    /// </remarks>
    [McpServerTool, Description("Close a debugging session and release all resources. The sessionId cannot be reused after closing.")]
    public string CloseSession(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId)
    {
        try
        {
            // Validate input parameters
            ValidateSessionId(sessionId);
            
            // Sanitize userId to prevent path traversal attacks
            var sanitizedUserId = SanitizeUserId(userId);

            // Close the session with user ownership validation
            SessionManager.CloseSession(sessionId, sanitizedUserId);
            
            return $"Session {sessionId} closed successfully. All resources have been released.";
        }
        catch (ArgumentException ex)
        {
            Logger.LogWarning(ex, "[CloseSession] Invalid parameters");
            return $"Error: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.LogWarning(ex, "[CloseSession] Unauthorized access");
            return $"Error: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogWarning(ex, "[CloseSession] Session not found");
            return $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[CloseSession] Unexpected error");
            return $"Error: Failed to close session - {ex.Message}";
        }
    }

    /// <summary>
    /// Lists all active sessions for a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>List of active session IDs and their status.</returns>
    /// <remarks>
    /// This is useful for:
    /// - Checking how many sessions a user has active
    /// - Finding session IDs if they were lost
    /// - Monitoring session usage
    /// </remarks>
    [McpServerTool, Description("List all active debugging sessions for a user.")]
    public string ListSessions(
        [Description("User ID to list sessions for")] string userId)
    {
        try
        {
            // Sanitize userId to prevent path traversal attacks
            var sanitizedUserId = SanitizeUserId(userId);

            // Get sessions for the user
            var sessions = SessionManager.ListUserSessions(sanitizedUserId);
            
            // Format the response - no sessions case
            if (sessions.Count == 0)
            {
                return $"No active sessions found for user: {sanitizedUserId}";
            }
            
            // Format the response - sessions found
            var result = $"Active sessions for user {sanitizedUserId} ({sessions.Count} total):\n";
            foreach (var session in sessions)
            {
                var dumpInfo = !string.IsNullOrEmpty(session.CurrentDumpId) ? $", Dump: {session.CurrentDumpId}" : "";
                result += $"  - SessionId: {session.SessionId}, Created: {session.CreatedAt:yyyy-MM-dd HH:mm:ss}, LastActivity: {session.LastAccessedAt:yyyy-MM-dd HH:mm:ss}{dumpInfo}\n";
            }
            
            return result;
        }
        catch (ArgumentException ex)
        {
            Logger.LogWarning(ex, "[ListSessions] Invalid user ID");
            return $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ListSessions] Unexpected error");
            return $"Error: Failed to list sessions - {ex.Message}";
        }
    }

    /// <summary>
    /// Gets information about the debugger being used in a session.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session (for security validation).</param>
    /// <returns>Information about the debugger type and version.</returns>
    /// <remarks>
    /// Returns information such as:
    /// - Debugger type (WinDbg or LLDB)
    /// - Operating system
    /// - Whether the session has an open dump file
    /// </remarks>
    [McpServerTool, Description("Get information about the debugger being used in a session (WinDbg or LLDB).")]
    public string GetDebuggerInfo(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId)
    {
        try
        {
            // Validate input parameters
            ValidateSessionId(sessionId);
            
            // Sanitize userId to prevent path traversal attacks
            var sanitizedUserId = SanitizeUserId(userId);

            // Get the session with user ownership validation
            var manager = GetSessionManager(sessionId, sanitizedUserId);
            
            // Get debugger information
            var debuggerType = manager.DebuggerType;
            var isInitialized = manager.IsInitialized;
            var hasDumpOpen = manager.IsDumpOpen;
            
            return $"Debugger Type: {debuggerType}\n" +
                   $"Operating System: {Environment.OSVersion}\n" +
                   $"Initialized: {isInitialized}\n" +
                   $"Dump Open: {hasDumpOpen}";
        }
        catch (ArgumentException ex)
        {
            Logger.LogWarning(ex, "[GetDebuggerInfo] Invalid parameters");
            return $"Error: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.LogWarning(ex, "[GetDebuggerInfo] Unauthorized access");
            return $"Error: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogWarning(ex, "[GetDebuggerInfo] Session not found or expired");
            return $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[GetDebuggerInfo] Unexpected error");
            return $"Error: Failed to get debugger info - {ex.Message}";
        }
    }

    /// <summary>
    /// Restores/attaches to an existing persisted session.
    /// </summary>
    /// <param name="sessionId">The session ID to restore.</param>
    /// <param name="userId">The user ID that owns the session (for security validation).</param>
    /// <returns>Session information including dump status.</returns>
    /// <remarks>
    /// Use this to reconnect to a session that was persisted:
    /// - After server restart
    /// - When switching between servers
    /// - To continue a previous debugging session
    /// 
    /// The session will be restored with its previously open dump (if any).
    /// </remarks>
    [McpServerTool, Description("Restore/attach to an existing persisted session. Use this to reconnect to a session after server restart or when switching servers.")]
    public string RestoreSession(
        [Description("Session ID to restore")] string sessionId,
        [Description("User ID that owns the session")] string userId)
    {
        try
        {
            // Validate input parameters
            ValidateSessionId(sessionId);
            
            // Sanitize userId to prevent path traversal attacks
            var sanitizedUserId = SanitizeUserId(userId);

            // Try to get the session - this will restore from disk if needed
            var session = SessionManager.GetSessionInfo(sessionId, sanitizedUserId);
            
            // Build status message
            var result = $"Session restored successfully.\n" +
                         $"  SessionId: {session.SessionId}\n" +
                         $"  Created: {session.CreatedAt:yyyy-MM-dd HH:mm:ss}\n" +
                         $"  LastActivity: {session.LastAccessedAt:yyyy-MM-dd HH:mm:ss}\n";
            
            if (!string.IsNullOrEmpty(session.CurrentDumpId))
            {
                result += $"  CurrentDump: {session.CurrentDumpId}\n";
                result += session.Manager?.IsDumpOpen == true 
                    ? "  DumpStatus: Open and ready\n"
                    : "  DumpStatus: Reopened automatically\n";
            }
            else
            {
                result += "  CurrentDump: None (use open_dump to load a dump)\n";
            }
            
            return result;
        }
        catch (ArgumentException ex)
        {
            Logger.LogWarning(ex, "[RestoreSession] Invalid parameters");
            return $"Error: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.LogWarning(ex, "[RestoreSession] Unauthorized access");
            return $"Error: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogWarning(ex, "[RestoreSession] Session not found or expired");
            return $"Error: {ex.Message}. Use 'list_sessions' to see available sessions or 'create_session' to create a new one.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[RestoreSession] Unexpected error");
            return $"Error: Failed to restore session - {ex.Message}";
        }
    }
}
