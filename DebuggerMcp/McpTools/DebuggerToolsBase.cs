using DebuggerMcp.Security;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.McpTools;

/// <summary>
/// Base class providing shared dependencies and helper methods for all MCP tool classes.
/// </summary>
/// <remarks>
/// This base class provides:
/// <list type="bullet">
/// <item><description>Access to core services (SessionManager, SymbolManager, WatchStore)</description></item>
/// <item><description>Common validation and sanitization methods</description></item>
/// <item><description>Shared error handling patterns</description></item>
/// </list>
/// 
/// All tool classes inherit from this base to ensure consistent behavior and reduce code duplication.
/// </remarks>
public abstract class DebuggerToolsBase(
    DebuggerSessionManager sessionManager,
    SymbolManager symbolManager,
    WatchStore watchStore,
    ILogger logger)
{
    /// <summary>
    /// Manages debugging sessions, including creation, retrieval, and cleanup.
    /// </summary>
    protected DebuggerSessionManager SessionManager { get; } = sessionManager;

    /// <summary>
    /// Manages symbol files and symbol server configurations.
    /// </summary>
    protected SymbolManager SymbolManager { get; } = symbolManager;

    /// <summary>
    /// Manages watch expression persistence and retrieval.
    /// </summary>
    protected WatchStore WatchStore { get; } = watchStore;

    /// <summary>
    /// Logger for diagnostic output.
    /// </summary>
    protected ILogger Logger { get; } = logger;

    /// <summary>
    /// Validates that a session ID is not null or empty.
    /// </summary>
    /// <param name="sessionId">The session ID to validate.</param>
    /// <exception cref="ArgumentException">Thrown when sessionId is null or whitespace.</exception>
    protected static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId cannot be null or empty", nameof(sessionId));
        }
    }

    /// <summary>
    /// Validates that a command string is not null or empty.
    /// </summary>
    /// <param name="command">The command to validate.</param>
    /// <exception cref="ArgumentException">Thrown when command is null or whitespace.</exception>
    protected static void ValidateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("command cannot be null or empty", nameof(command));
        }
    }

    /// <summary>
    /// Sanitizes a user ID to prevent path traversal attacks.
    /// </summary>
    /// <param name="userId">The user ID to sanitize.</param>
    /// <returns>The sanitized user ID.</returns>
    protected static string SanitizeUserId(string userId)
    {
        return PathSanitizer.SanitizeIdentifier(userId, nameof(userId));
    }

    /// <summary>
    /// Sanitizes a dump ID to prevent path traversal attacks.
    /// </summary>
    /// <param name="dumpId">The dump ID to sanitize.</param>
    /// <returns>The sanitized dump ID.</returns>
    protected static string SanitizeDumpId(string dumpId)
    {
        return PathSanitizer.SanitizeIdentifier(dumpId, nameof(dumpId));
    }

    /// <summary>
    /// Gets a session's debugger manager with ownership validation.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="sanitizedUserId">The already-sanitized user ID.</param>
    /// <returns>The debugger manager for the session.</returns>
    protected IDebuggerManager GetSessionManager(string sessionId, string sanitizedUserId)
    {
        return SessionManager.GetSession(sessionId, sanitizedUserId);
    }

    /// <summary>
    /// Gets session information with ownership validation.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="sanitizedUserId">The already-sanitized user ID.</param>
    /// <returns>The session information.</returns>
    protected DebuggerSession GetSessionInfo(string sessionId, string sanitizedUserId)
    {
        return SessionManager.GetSessionInfo(sessionId, sanitizedUserId);
    }

    /// <summary>
    /// Validates that a dump is open in the specified session.
    /// </summary>
    /// <param name="manager">The debugger manager to check.</param>
    /// <exception cref="InvalidOperationException">Thrown when no dump is open.</exception>
    protected static void ValidateDumpIsOpen(IDebuggerManager manager)
    {
        if (!manager.IsDumpOpen)
        {
            throw new InvalidOperationException("No dump file is open. Use OpenDump first.");
        }
    }

    /// <summary>
    /// Validates that a dump is open in the specified session, with a custom error message.
    /// </summary>
    /// <param name="manager">The debugger manager to check.</param>
    /// <param name="sessionId">The session ID for the error message.</param>
    /// <exception cref="InvalidOperationException">Thrown when no dump is open.</exception>
    protected static void ValidateDumpIsOpen(IDebuggerManager manager, string sessionId)
    {
        if (!manager.IsDumpOpen)
        {
            throw new InvalidOperationException($"Session {sessionId} does not have a dump file open. Use OpenDump first.");
        }
    }
}

