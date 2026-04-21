using System.ComponentModel;
using DebuggerMcp.Dumps;
using DebuggerMcp.Security;
using DebuggerMcp.SourceLink;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.McpTools;

/// <summary>
/// MCP tools for managing dump files and executing debugger commands.
/// </summary>
/// <remarks>
/// Provides tools for:
/// <list type="bullet">
/// <item><description>Opening dump files for analysis</description></item>
/// <item><description>Closing dump files</description></item>
/// <item><description>Executing debugger commands</description></item>
/// <item><description>Loading SOS extension for .NET debugging</description></item>
/// </list>
/// </remarks>
public class DumpTools(
    DebuggerSessionManager sessionManager,
    SymbolManager symbolManager,
    WatchStore watchStore,
    ILogger<DumpTools> logger,
    DumpOpenCoordinator? dumpOpenCoordinator = null)
    : DebuggerToolsBase(sessionManager, symbolManager, watchStore, logger)
{
    /// <summary>
    /// Canonical dump-open workflow shared with session restore.
    /// </summary>
    private DumpOpenCoordinator DumpOpenCoordinator { get; } = dumpOpenCoordinator ??
        new DumpOpenCoordinator(
            symbolManager,
            new SourceResolutionStateRefresher(symbolManager, logger),
            logger);

    /// <summary>
    /// Opens a memory dump file for analysis.
    /// </summary>
    /// <param name="sessionId">The session ID returned from CreateSession.</param>
    /// <param name="userId">The user ID that owns the session (for security validation).</param>
    /// <param name="dumpId">The dump ID returned from the HTTP upload API.</param>
    /// <returns>Confirmation message with dump information.</returns>
    /// <remarks>
    /// IMPORTANT: The dump file must first be uploaded via the HTTP API (POST /api/dumps/upload).
    /// The upload API will return a dumpId that should be used here.
    /// 
    /// The debugger type (WinDbg or LLDB) is automatically selected based on the operating system:
    /// - Windows: Uses WinDbg with DbgEng COM API
    /// - Linux/macOS: Uses LLDB with process communication
    /// </remarks>
    public async Task<string> OpenDump(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Dump ID from the upload API")] string dumpId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Logger.LogInformation("[OpenDump] Starting - SessionId: {SessionId}, UserId: {UserId}, DumpId: {DumpId}",
            sessionId, userId, dumpId);

        try
        {
            // Validate input parameters
            ValidateSessionId(sessionId);

            // Sanitize userId and dumpId to prevent path traversal attacks
            var sanitizedUserId = SanitizeUserId(userId);
            var sanitizedDumpId = SanitizeDumpId(dumpId);

            // Get the session from the manager with user ownership validation
            // This will throw if the session doesn't exist or belongs to another user
            Logger.LogDebug("[OpenDump] Getting session manager...");
            var manager = GetSessionManager(sessionId, sanitizedUserId);
            var session = GetSessionInfo(sessionId, sanitizedUserId);
            Logger.LogDebug("[OpenDump] Session retrieved - DebuggerType: {DebuggerType}", manager.DebuggerType);

            Logger.LogInformation("[OpenDump] Opening dump file (this may take a while for large dumps)...");
            var openResult = await DumpOpenCoordinator.OpenDumpAsync(new DumpOpenCoordinatorRequest
            {
                SessionId = sessionId,
                UserId = sanitizedUserId,
                DumpId = sanitizedDumpId,
                Session = session,
                Manager = manager,
                DumpPathResolver = () => SessionManager.GetDumpPath(sanitizedDumpId, sanitizedUserId),
                AllowAlreadyOpenSameDump = true,
                UpdateMetadataIfIncomplete = true
            });
            Logger.LogInformation("[OpenDump] Dump open workflow completed - Elapsed: {Elapsed}ms", sw.ElapsedMilliseconds);

            if (openResult.RequiresPersistence)
            {
                SessionManager.PersistSession(sessionId);
            }

            // Build response with symbol information and timing
            var hasSymbols = SymbolManager.HasSymbols(sanitizedDumpId, sanitizedUserId, manager.CurrentDumpPath);
            var customSymbolCount = hasSymbols
                ? SymbolManager.ListDumpSymbols(sanitizedDumpId, sanitizedUserId, manager.CurrentDumpPath).Count
                : 0;
            var symbolInfo = hasSymbols
                ? $"Symbols: Microsoft Symbol Server + {customSymbolCount} custom"
                : "Symbols: Microsoft Symbol Server";

            // Include .NET detection and SOS status
            var dotNetInfo = manager.IsDotNetDump
                ? manager.IsSosLoaded
                    ? " .NET dump detected, SOS auto-loaded."
                    : " .NET dump detected, but SOS failed to load - use LoadSos to retry."
                : " Native dump (non-.NET).";

            sw.Stop();
            var elapsedSeconds = sw.Elapsed.TotalSeconds;
            Logger.LogInformation("[OpenDump] Completed - Total time: {Elapsed}ms", sw.ElapsedMilliseconds);

            // Include timing info so the client knows what happened
            var timingInfo = elapsedSeconds > 30
                ? $" (took {elapsedSeconds:F0}s - symbols were downloaded from server)"
                : $" (took {elapsedSeconds:F0}s - symbols were cached)";

            if (openResult.AlreadyOpen)
            {
                return $"Dump already open: {sanitizedDumpId}. {symbolInfo}.{dotNetInfo}{timingInfo}";
            }

            return $"Dump opened: {sanitizedDumpId}. {symbolInfo}.{dotNetInfo}{timingInfo}";
        }
        catch (ArgumentException)
        {
            // Re-throw validation errors - these indicate client mistakes
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            // Re-throw authorization errors - these indicate permission issues
            throw;
        }
        catch (FileNotFoundException)
        {
            // Re-throw file not found - these indicate the dump doesn't exist
            throw;
        }
        catch (Exception ex)
        {
            // Return the actual error message for server-side errors instead of 
            // letting MCP framework generate a generic "An error occurred" message
            Logger.LogError(ex, "[OpenDump] Failed to open dump");
            return $"Error: Failed to open dump. {ex.Message}";
        }
    }

    /// <summary>
    /// Closes the currently open dump file in a session.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session (for security validation).</param>
    /// <returns>Confirmation message.</returns>
    /// <remarks>
    /// This releases the dump file and associated resources but keeps the session active.
    /// You can open another dump file in the same session after closing.
    /// </remarks>
    public string CloseDump(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);

        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        // Get the session with user ownership validation and close the dump
        var manager = GetSessionManager(sessionId, sanitizedUserId);
        var session = GetSessionInfo(sessionId, sanitizedUserId);
        manager.CloseDump(); // Safe if no dump is open; CloseDump is idempotent.

        // Close ClrMD analyzer if open
        session.ClrMdAnalyzer?.Dispose();
        session.ClrMdAnalyzer = null;
        session.ClearSourceLinkResolver();
        session.ClearCachedReport();

        // Clear the tracked dump ID and persist to disk
        SymbolManager.ConfigureSessionSymbolPaths(sessionId, dumpId: null, includeMicrosoftSymbols: true);
        session.SymbolConfiguration = SymbolManager.GetPersistedSessionSymbolConfiguration(sessionId);
        session.CurrentDumpId = null;
        SessionManager.PersistSession(sessionId);

        return "Dump file closed successfully.";
    }

    /// <summary>
    /// Executes a debugger command and returns the output.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session (for security validation).</param>
    /// <param name="command">The debugger command to execute (WinDbg or LLDB syntax depending on platform).</param>
    /// <returns>The output from the debugger command.</returns>
    /// <remarks>
    /// A dump must already be open in the target session before this tool can execute
    /// debugger commands. Creating a session alone does not initialize the debugger
    /// engine or load a dump.
    ///
    /// Supported commands depend on the debugger:
    /// 
    /// WinDbg (Windows):
    /// - k: Display call stack
    /// - !analyze -v: Analyze crash dump
    /// - !threads: List threads (.NET)
    /// - !dumpheap: Dump managed heap (.NET)
    /// - lm: List loaded modules
    /// - And all other WinDbg commands
    /// 
    /// LLDB (Linux/macOS):
    /// - bt: Display backtrace
    /// - thread list: List threads
    /// - frame info: Frame information
    /// - plugin load libsosplugin.so: Load SOS plugin for .NET
    /// - And all other LLDB commands
    /// </remarks>
    public string ExecuteCommand(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Debugger command to execute (e.g., 'k' for call stack, '!analyze -v' for crash analysis)")] string command)
    {
        try
        {
            // Validate input parameters
            ValidateSessionId(sessionId);

            // Sanitize userId to prevent path traversal attacks
            var sanitizedUserId = SanitizeUserId(userId);

            // Validate command is not empty
            ValidateCommand(command);

            // Get the session with user ownership validation and execute the command.
            // Return an actionable message here instead of leaking low-level debugger
            // initialization errors when the session exists but no dump is open yet.
            var manager = GetSessionManager(sessionId, sanitizedUserId);

            if (!manager.IsDumpOpen)
            {
                return "Error: No dump file is currently open in this session. Open a dump first before executing debugger commands.";
            }

            if (!manager.IsInitialized)
            {
                return $"Error: The {manager.DebuggerType} debugger for this session is not initialized. Close and reopen the dump, then try the command again.";
            }

            var output = manager.ExecuteCommand(command);

            return output;
        }
        catch (ArgumentException)
        {
            // Re-throw validation errors - these indicate client mistakes
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            // Re-throw authorization errors - these indicate permission issues
            throw;
        }
        catch (Exception ex)
        {
            // Return the actual error message for server-side errors instead of 
            // letting MCP framework generate a generic "An error occurred" message
            Logger.LogError(ex, "[ExecuteCommand] Failed to execute command: {Command}", command);
            return $"Error: Command execution failed. {ex.Message}";
        }
    }

    /// <summary>
    /// Loads the SOS extension for .NET debugging.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session (for security validation).</param>
    /// <returns>Confirmation message.</returns>
    /// <remarks>
    /// SOS (Son of Strike) is a debugging extension for analyzing .NET applications.
    /// 
    /// NOTE: SOS is now automatically loaded when a .NET dump is detected during OpenDump.
    /// This command is provided for backwards compatibility and manual loading if needed.
    /// 
    /// On Windows (WinDbg):
    /// - Automatically loads sos.dll from the .NET runtime
    /// 
    /// On Linux/macOS (LLDB):
    /// - Loads libsosplugin.so
    /// 
    /// After loading SOS, you can use commands like:
    /// - !threads: List managed threads
    /// - !dumpheap: Dump the managed heap
    /// - !clrstack: Display managed call stack
    /// - !eeheap: Display GC heap information
    /// </remarks>
    public string LoadSos(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);

        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        try
        {
            // Get the session with user ownership validation
            var manager = GetSessionManager(sessionId, sanitizedUserId);
            var session = GetSessionInfo(sessionId, sanitizedUserId);

            // Check if SOS is already loaded (e.g., auto-loaded during OpenDump)
            if (manager.IsSosLoaded)
            {
                return "SOS extension is already loaded. You can use SOS commands like clrthreads, clrstack, dumpheap -stat, pe, etc. (WinDbg-style '!<command>' is also accepted; the server normalizes this for LLDB).";
            }

            // Warn if this doesn't appear to be a .NET dump, but still allow loading
            var warning = !manager.IsDotNetDump
                ? "Note: This does not appear to be a .NET dump. SOS commands may not work as expected. "
                : string.Empty;

            // Load SOS (user explicitly requested it)
            manager.LoadSosExtension();

            // SOS availability can materially change .NET crash analysis output; invalidate cached report so
            // subsequent report_index/report_get regenerates with the richer data set.
            session.ClearCachedReport();

            return warning + "SOS extension loaded successfully. You can now use SOS commands like clrthreads, clrstack, dumpheap -stat, pe, etc. (WinDbg-style '!<command>' is also accepted; the server normalizes this for LLDB).";
        }
        catch (ArgumentException)
        {
            // Re-throw validation errors - these indicate client mistakes
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            // Re-throw authorization errors - these indicate permission issues
            throw;
        }
        catch (Exception ex)
        {
            // Return the actual error message for server-side errors instead of 
            // letting MCP framework generate a generic "An error occurred" message
            Logger.LogError(ex, "[LoadSos] Failed to load SOS extension");
            return $"Error: Failed to load SOS extension. {ex.Message}";
        }
    }

}
