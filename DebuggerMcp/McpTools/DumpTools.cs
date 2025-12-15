using System.ComponentModel;
using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Controllers;
using DebuggerMcp.Security;
using DebuggerMcp.Serialization;
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
    ILogger<DumpTools> logger)
    : DebuggerToolsBase(sessionManager, symbolManager, watchStore, logger)
{
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

            // Initialize the debugger if not already initialized
            // This is done lazily on first use to avoid overhead for sessions that may not be used
            // Uses async initialization to avoid blocking ThreadPool threads in ASP.NET
            if (!manager.IsInitialized)
            {
                Logger.LogInformation("[OpenDump] Initializing debugger ({DebuggerType})...", manager.DebuggerType);
                await manager.InitializeAsync();
                Logger.LogInformation("[OpenDump] Debugger initialized - Elapsed: {Elapsed}ms", sw.ElapsedMilliseconds);
            }

            // Resolve the dumpId to a file path using the user's dump storage directory
            // This follows the pattern: {dumpStoragePath}/{userId}/{dumpId}.dmp
            var dumpPath = SessionManager.GetDumpPath(sanitizedDumpId, sanitizedUserId);
            Logger.LogInformation("[OpenDump] Dump path resolved: {DumpPath}", dumpPath);

            // Check if there's a custom executable for this dump (standalone apps)
            string? executablePath = null;
            var dumpDir = Path.GetDirectoryName(dumpPath);
            if (dumpDir != null)
            {
                // Check both naming conventions for metadata
                var metadataPath = Path.Combine(dumpDir, $"{sanitizedDumpId}.json");
                var altMetadataPath = Path.Combine(dumpDir, $".metadata_{sanitizedDumpId}.json");
                var actualMetadataPath = File.Exists(metadataPath) ? metadataPath :
                                         File.Exists(altMetadataPath) ? altMetadataPath : null;
                
                if (actualMetadataPath != null)
                {
                    try
                    {
                        var metadataJson = await File.ReadAllTextAsync(actualMetadataPath);
                        var metadata = System.Text.Json.JsonSerializer.Deserialize<DumpMetadata>(metadataJson);
                        if (metadata?.ExecutablePath != null && File.Exists(metadata.ExecutablePath))
                        {
                            executablePath = metadata.ExecutablePath;
                            Logger.LogInformation("[OpenDump] Found custom executable for standalone app: {ExecutablePath}", executablePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogDebug(ex, "[OpenDump] Failed to read dump metadata for executable path");
                    }
                }
            }

            // Configure symbols automatically for this dump
            // This includes Microsoft Symbol Server + dump-specific symbols if they exist
            Logger.LogDebug("[OpenDump] Configuring symbol paths...");
            SymbolManager.ConfigureSessionSymbolPaths(sessionId, sanitizedDumpId, includeMicrosoftSymbols: true);

            // Get the configured symbol path and apply it to the debugger
            // WinDbg uses semicolon-separated paths, LLDB uses a different format
            var symbolPath = manager.DebuggerType == "WinDbg"
                ? SymbolManager.BuildWinDbgSymbolPath(sessionId)
                : SymbolManager.BuildLldbSymbolPath(sessionId);

            // Apply symbol path if one was configured
            if (!string.IsNullOrWhiteSpace(symbolPath))
            {
                Logger.LogDebug("[OpenDump] Applying symbol path: {SymbolPath}", symbolPath);
                manager.ConfigureSymbolPath(symbolPath);
            }

            // Open the dump file in the debugger
            Logger.LogInformation("[OpenDump] Opening dump file (this may take a while for large dumps)...");
            manager.OpenDumpFile(dumpPath, executablePath);
            Logger.LogInformation("[OpenDump] Dump file opened - Elapsed: {Elapsed}ms", sw.ElapsedMilliseconds);

            // Open with ClrMD for metadata enrichment (after debugger opens the dump)
            try
            {
                var clrMdAnalyzer = new ClrMdAnalyzer(Logger);
                if (clrMdAnalyzer.OpenDump(dumpPath))
                {
                    session.ClrMdAnalyzer = clrMdAnalyzer;
                    Logger.LogInformation("[OpenDump] ClrMD analyzer attached for metadata enrichment");

                    // Set up SequencePointResolver for source location resolution in ClrStack
                    try
                    {
                        var seqResolver = new SourceLink.SequencePointResolver(Logger);
                        
                        // Add symbol directory as PDB search path
                        var symbolDir = SymbolManager.GetDumpSymbolDirectory(sanitizedDumpId);
                        if (!string.IsNullOrEmpty(symbolDir))
                        {
                            seqResolver.AddPdbSearchPath(symbolDir);
                        }
                        
                        // Also add the dump's directory (for side-by-side PDBs)
                        var dumpDirectory = Path.GetDirectoryName(dumpPath);
                        if (!string.IsNullOrEmpty(dumpDirectory))
                        {
                            seqResolver.AddPdbSearchPath(dumpDirectory);
                        }
                        
                        // Add common symbol cache locations
                        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        if (!string.IsNullOrEmpty(homeDir))
                        {
                            // dotnet-symbol cache
                            var dotnetSymbolCache = Path.Combine(homeDir, ".dotnet", "symbolcache");
                            if (Directory.Exists(dotnetSymbolCache))
                            {
                                seqResolver.AddPdbSearchPath(dotnetSymbolCache);
                            }
                            
                            // NuGet symbol cache
                            var nugetSymbols = Path.Combine(homeDir, ".nuget", "packages");
                            if (Directory.Exists(nugetSymbols))
                            {
                                seqResolver.AddPdbSearchPath(nugetSymbols);
                            }
                        }
                        
                        // Extract runtime directory from ClrMD modules and add as search path
                        if (clrMdAnalyzer.Runtime != null)
                        {
                            foreach (var module in clrMdAnalyzer.Runtime.EnumerateModules())
                            {
                                var moduleDir = Path.GetDirectoryName(module.Name);
                                if (!string.IsNullOrEmpty(moduleDir) && Directory.Exists(moduleDir))
                                {
                                    seqResolver.AddPdbSearchPath(moduleDir);
                                }
                            }
                        }
                        
                        clrMdAnalyzer.SetSequencePointResolver(seqResolver);
                        Logger.LogDebug("[OpenDump] SequencePointResolver configured for ClrStack");
                    }
                    catch (Exception seqEx)
                    {
                        Logger.LogDebug(seqEx, "[OpenDump] SequencePointResolver setup failed, ClrStack will work without source locations");
                    }
                }
                else
                {
                    Logger.LogDebug("[OpenDump] ClrMD could not open dump (non-.NET or architecture mismatch)");
                    clrMdAnalyzer.Dispose();
                }
            }
            catch (Exception clrMdEx)
            {
                // Don't fail the dump opening if ClrMD fails - it's optional enrichment
                Logger.LogDebug(clrMdEx, "[OpenDump] ClrMD initialization failed, continuing without metadata enrichment");
            }

            // Track which dump is open in this session and persist to disk
            session.CurrentDumpId = sanitizedDumpId;
            SessionManager.PersistSession(sessionId);

            // Check and update dump metadata if incomplete (Alpine/RuntimeVersion detection)
            await UpdateDumpMetadataIfIncompleteAsync(dumpPath, sanitizedUserId, sanitizedDumpId);

            // Build response with symbol information and timing
            var hasSymbols = SymbolManager.HasSymbols(sanitizedDumpId);
            var customSymbolCount = hasSymbols ? SymbolManager.ListDumpSymbols(sanitizedDumpId).Count : 0;
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

        // Clear the tracked dump ID and persist to disk
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

            // Get the session with user ownership validation and execute the command
            var manager = GetSessionManager(sessionId, sanitizedUserId);
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

            // Check if SOS is already loaded (e.g., auto-loaded during OpenDump)
            if (manager.IsSosLoaded)
            {
                return "SOS extension is already loaded. You can use SOS commands like clrthreads, dumpheap, clrstack, etc.";
            }

            // Warn if this doesn't appear to be a .NET dump, but still allow loading
            var warning = !manager.IsDotNetDump
                ? "Note: This does not appear to be a .NET dump. SOS commands may not work as expected. "
                : string.Empty;

            // Load SOS (user explicitly requested it)
            manager.LoadSosExtension();

            return warning + "SOS extension loaded successfully. You can now use SOS commands like clrthreads, dumpheap, clrstack, etc.";
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

    /// <summary>
    /// Checks if dump metadata is incomplete and updates it with analysis results.
    /// </summary>
    /// <param name="dumpPath">Path to the dump file.</param>
    /// <param name="userId">The sanitized user ID.</param>
    /// <param name="dumpId">The sanitized dump ID.</param>
    /// <remarks>
    /// This ensures dumps uploaded before Alpine/RuntimeVersion detection was added
    /// get their metadata updated when opened.
    /// </remarks>
    private async Task UpdateDumpMetadataIfIncompleteAsync(string dumpPath, string userId, string dumpId)
    {
        try
        {
            // Get metadata file path
            var userDir = Path.GetDirectoryName(dumpPath);
            if (userDir == null) return;

            // Check both naming conventions for metadata
            var metadataPath = Path.Combine(userDir, $"{dumpId}.json");
            var altMetadataPath = Path.Combine(userDir, $".metadata_{dumpId}.json");
            var actualMetadataPath = File.Exists(metadataPath) ? metadataPath :
                                     File.Exists(altMetadataPath) ? altMetadataPath : null;

            // Check if metadata file exists
            if (actualMetadataPath == null)
            {
                Logger.LogDebug("[OpenDump] No metadata file found for dump {DumpId}", dumpId);
                return;
            }

            // Read existing metadata
            var metadataJson = await File.ReadAllTextAsync(actualMetadataPath);
            var metadata = JsonSerializer.Deserialize<DumpMetadata>(metadataJson);

            if (metadata == null)
            {
                Logger.LogWarning("[OpenDump] Failed to deserialize metadata for dump {DumpId}", dumpId);
                return;
            }

            // Check if metadata is incomplete (missing Alpine, RuntimeVersion, or Architecture)
            if (metadata.IsAlpineDump.HasValue &&
                !string.IsNullOrEmpty(metadata.RuntimeVersion) &&
                !string.IsNullOrEmpty(metadata.Architecture))
            {
                Logger.LogDebug("[OpenDump] Metadata is complete for dump {DumpId}", dumpId);
                return;
            }

            Logger.LogInformation("[OpenDump] Dump metadata incomplete, running analysis for {DumpId}...", dumpId);

            // Run analysis to detect Alpine, RuntimeVersion, and Architecture
            var analysisResult = await DumpAnalyzer.AnalyzeDumpAsync(dumpPath, Logger);

            // Update metadata with analysis results
            var updated = false;

            if (!metadata.IsAlpineDump.HasValue && analysisResult.IsAlpine.HasValue)
            {
                metadata.IsAlpineDump = analysisResult.IsAlpine;
                updated = true;
                Logger.LogInformation("[OpenDump] Updated IsAlpineDump to {IsAlpine} for dump {DumpId}",
                    analysisResult.IsAlpine, dumpId);
            }

            if (string.IsNullOrEmpty(metadata.RuntimeVersion) && !string.IsNullOrEmpty(analysisResult.RuntimeVersion))
            {
                metadata.RuntimeVersion = analysisResult.RuntimeVersion;
                updated = true;
                Logger.LogInformation("[OpenDump] Updated RuntimeVersion to {Version} for dump {DumpId}",
                    analysisResult.RuntimeVersion, dumpId);
            }

            if (string.IsNullOrEmpty(metadata.Architecture) && !string.IsNullOrEmpty(analysisResult.Architecture))
            {
                metadata.Architecture = analysisResult.Architecture;
                updated = true;
                Logger.LogInformation("[OpenDump] Updated Architecture to {Architecture} for dump {DumpId}",
                    analysisResult.Architecture, dumpId);
            }

            // Save updated metadata if changes were made (back to the same file we read from)
            if (updated)
            {
                await File.WriteAllTextAsync(
                    actualMetadataPath,
                    JsonSerializer.Serialize(metadata, JsonSerializationDefaults.Indented));
                Logger.LogInformation("[OpenDump] Saved updated metadata for dump {DumpId}", dumpId);
            }
        }
        catch (Exception ex)
        {
            // Don't fail the open operation if metadata update fails
            Logger.LogWarning(ex, "[OpenDump] Failed to update dump metadata for {DumpId}", dumpId);
        }
    }
}
