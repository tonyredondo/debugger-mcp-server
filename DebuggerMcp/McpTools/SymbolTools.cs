using System.ComponentModel;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DebuggerMcp.McpTools;

/// <summary>
/// MCP tools for managing symbol paths and symbol servers.
/// </summary>
/// <remarks>
/// Provides tools for:
/// <list type="bullet">
/// <item><description>Configuring additional symbol paths</description></item>
/// <item><description>Getting information about available symbol servers</description></item>
/// </list>
/// 
/// Note: Symbol configuration is done automatically when you call OpenDump.
/// These tools are for advanced scenarios where additional symbol servers are needed.
/// </remarks>
[McpServerToolType]
public class SymbolTools(
    DebuggerSessionManager sessionManager,
    SymbolManager symbolManager,
    WatchStore watchStore,
    ILogger<SymbolTools> logger)
    : DebuggerToolsBase(sessionManager, symbolManager, watchStore, logger)
{
    /// <summary>
    /// Configures additional symbol paths for a debugging session.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session (for security validation).</param>
    /// <param name="additionalPaths">Comma-separated list of additional symbol paths (directories or symbol servers).</param>
    /// <returns>Success message.</returns>
    /// <remarks>
    /// <para>NOTE: Symbol configuration is done automatically when you call OpenDump. This tool is only needed if you want to add extra symbol servers.</para>
    /// <para>By default, OpenDump configures:</para>
    /// <list type="bullet">
    /// <item><description>Microsoft Symbol Server (for Windows/Microsoft symbols)</description></item>
    /// <item><description>Dump-specific symbols (if you uploaded any via the HTTP API)</description></item>
    /// </list>
    /// <para>Use this tool to add additional symbol paths such as:</para>
    /// <list type="bullet">
    /// <item><description>NuGet Symbol Server: "https://symbols.nuget.org/download/symbols"</description></item>
    /// <item><description>Custom symbol server: "https://your-server.com/symbols"</description></item>
    /// <item><description>Local directory: "C:\\MySymbols" or "/path/to/symbols"</description></item>
    /// </list>
    /// <para>Multiple paths should be separated by commas.</para>
    /// <para>Call this BEFORE OpenDump if you want the additional paths configured from the start.</para>
    /// </remarks>
    [McpServerTool, Description("Add additional symbol paths to a session. NOTE: OpenDump automatically configures Microsoft symbols + dump-specific symbols, so this is optional.")]
    public string ConfigureAdditionalSymbols(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Comma-separated list of additional symbol paths (URLs or directories)")] string additionalPaths)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);
        
        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        // Validate additionalPaths is not empty
        if (string.IsNullOrWhiteSpace(additionalPaths))
        {
            throw new ArgumentException("additionalPaths cannot be null or empty", nameof(additionalPaths));
        }

        // Get the debugger manager for this session with user ownership validation
        var manager = GetSessionManager(sessionId, sanitizedUserId);

        // Configure additional symbol paths (this will merge with existing paths)
        SymbolManager.ConfigureSessionSymbolPaths(sessionId, dumpId: null, additionalPaths: additionalPaths, includeMicrosoftSymbols: true);

        // Build the appropriate symbol path string for the debugger type
        // WinDbg uses semicolon-separated paths with SRV* syntax
        // LLDB uses a different format
        var symbolPathString = manager.DebuggerType == "WinDbg"
            ? SymbolManager.BuildWinDbgSymbolPath(sessionId, includeLocalCache: true)
            : SymbolManager.BuildLldbSymbolPath(sessionId);

        // Configure the symbol path in the debugger if we have one
        if (!string.IsNullOrEmpty(symbolPathString))
        {
            manager.ConfigureSymbolPath(symbolPathString);
            
            // Clear command cache since symbol paths have changed
            manager.ClearCommandCache();
            Logger.LogInformation("[ConfigureAdditionalSymbols] Cleared command cache after configuring new symbol paths");
        }

        var pathCount = additionalPaths.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Length;
        return $"Additional symbol paths configured successfully for session {sessionId}.\n" +
               $"Added {pathCount} additional path(s).\n" +
               $"Debugger: {manager.DebuggerType}";
    }

    /// <summary>
    /// Gets a list of common symbol servers.
    /// </summary>
    /// <returns>List of symbol server URLs and descriptions.</returns>
    /// <remarks>
    /// Returns information about commonly used symbol servers:
    /// - Microsoft Symbol Server (automatically configured by OpenDump)
    /// - NuGet Symbol Server
    /// </remarks>
    [McpServerTool, Description("Get a list of common symbol servers (Microsoft, NuGet).")]
    public string GetSymbolServers()
    {
        return $"Common Symbol Servers:\n\n" +
               $"1. Microsoft Symbol Server (AUTO-CONFIGURED)\n" +
               $"   URL: {SymbolManager.MicrosoftSymbolServer}\n" +
               $"   Description: Official Microsoft public symbol server for Windows and .NET symbols\n" +
               $"   NOTE: This is automatically configured when you call OpenDump\n\n" +
               $"2. NuGet Symbol Server\n" +
               $"   URL: {SymbolManager.NuGetSymbolServer}\n" +
               $"   Description: Symbol server for NuGet packages\n" +
               $"   Usage: Use ConfigureAdditionalSymbols to add this\n\n" +
               $"Custom Symbols:\n" +
               $"   Upload via HTTP API: POST /api/symbols/upload (with dumpId)\n" +
               $"   Batch upload: POST /api/symbols/upload-batch (with dumpId)\n" +
               $"   These are automatically configured when you call OpenDump";
    }

    /// <summary>
    /// Clears the downloaded symbol cache for a specific dump.
    /// </summary>
    /// <param name="userId">The user ID that owns the dump.</param>
    /// <param name="dumpId">The dump ID whose symbol cache should be cleared.</param>
    /// <returns>Result message indicating what was cleared.</returns>
    /// <remarks>
    /// <para>Use this when:</para>
    /// <list type="bullet">
    /// <item><description>Symbol download timed out and you have incomplete symbols</description></item>
    /// <item><description>You want to force a fresh symbol download on next open</description></item>
    /// <item><description>You want to reclaim disk space</description></item>
    /// </list>
    /// <para>After clearing, the next OpenDump call will re-download symbols from the server.</para>
    /// </remarks>
    [McpServerTool, Description("Clear the downloaded symbol cache for a dump. Use this after a timed-out download to force re-download on next open.")]
    public string ClearSymbolCache(
        [Description("User ID that owns the dump")] string userId,
        [Description("Dump ID whose symbol cache should be cleared")] string dumpId)
    {
        // Sanitize inputs to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);
        var sanitizedDumpId = SanitizeDumpId(dumpId);

        // Get the dump storage path and construct symbol cache path
        var dumpStoragePath = SessionManager.GetDumpStoragePath();
        var dumpDir = Path.Combine(dumpStoragePath, sanitizedUserId);
        var dumpName = sanitizedDumpId; // Without extension
        var symbolCachePath = Path.Combine(dumpDir, $".symbols_{dumpName}");

        Logger.LogInformation("[ClearSymbolCache] Clearing cache for dump {DumpId} at {Path}", sanitizedDumpId, symbolCachePath);

        if (!Directory.Exists(symbolCachePath))
        {
            return $"No symbol cache found for dump {sanitizedDumpId}. The cache may have already been cleared or symbols were never downloaded.";
        }

        try
        {
            // Count files before deletion for the report
            var files = Directory.GetFiles(symbolCachePath, "*", SearchOption.AllDirectories);
            var fileCount = files.Length;
            var totalSize = files.Sum(f => new FileInfo(f).Length);
            var totalSizeMb = totalSize / (1024.0 * 1024.0);

            // Delete the directory
            Directory.Delete(symbolCachePath, recursive: true);

            Logger.LogInformation("[ClearSymbolCache] Deleted {FileCount} files ({SizeMb:F1} MB) from {Path}", 
                fileCount, totalSizeMb, symbolCachePath);

            // Also clear the SymbolFiles list from the dump metadata so the smart cache doesn't think files exist
            ClearSymbolFilesFromMetadata(dumpStoragePath, sanitizedUserId, sanitizedDumpId);

            return $"Symbol cache cleared for dump {sanitizedDumpId}.\n" +
                   $"Removed: {fileCount} files ({totalSizeMb:F1} MB)\n" +
                   $"Next 'open' command will re-download symbols from the server.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ClearSymbolCache] Failed to clear cache at {Path}", symbolCachePath);
            return $"Error: Failed to clear symbol cache. {ex.Message}";
        }
    }
    
    /// <summary>
    /// Reloads symbols into the running debugger session after uploading new symbol files.
    /// This adds the symbol directories to the debugger's search paths and explicitly loads .dbg/.debug files.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <returns>Result message with loaded symbols count.</returns>
    /// <remarks>
    /// <para>Use this tool after uploading new symbol files (especially ZIP archives) to a dump that is already open.</para>
    /// <para>This will:</para>
    /// <list type="bullet">
    /// <item><description>Add all symbol directories to the debugger's search paths</description></item>
    /// <item><description>For LLDB: Explicitly load .dbg and .debug files using 'target symbols add'</description></item>
    /// <item><description>For WinDbg: Reload symbols using '.reload /f'</description></item>
    /// </list>
    /// </remarks>
    [McpServerTool, Description("Reload symbols into a running debugger session after uploading new symbol files. Use after 'symbols upload' when a dump is already open.")]
    public string ReloadSymbols(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId)
    {
        ValidateSessionId(sessionId);
        var sanitizedUserId = SanitizeUserId(userId);

        // Get the session manager and session info
        var manager = GetSessionManager(sessionId, sanitizedUserId);
        var session = GetSessionInfo(sessionId, sanitizedUserId);

        if (!manager.IsInitialized)
        {
            throw new InvalidOperationException("Debugger is not initialized. Open a dump first.");
        }

        ValidateDumpIsOpen(manager);

        if (string.IsNullOrEmpty(session.CurrentDumpId))
        {
            throw new InvalidOperationException("No dump is currently open in this session");
        }

        // Get the symbol directory for this dump
        var symbolDir = SymbolManager.GetDumpSymbolDirectory(session.CurrentDumpId);
        if (symbolDir == null || !Directory.Exists(symbolDir))
        {
            return "No symbol directory found for the current dump. Upload symbols first using 'symbols upload'.";
        }

        Logger.LogInformation("[ReloadSymbols] Reloading symbols from {SymbolDir} for session {SessionId}", symbolDir, sessionId);

        var loadedCount = 0;
        var addedPaths = 0;
        var messages = new List<string>();

        if (manager.DebuggerType == "LLDB")
        {
            // Get all subdirectories
            var allDirs = SymbolManager.GetAllSubdirectories(symbolDir);
            
            // Add each directory to LLDB's search paths
            foreach (var dir in allDirs)
            {
                var result = manager.ExecuteCommand($"settings append target.debug-file-search-paths \"{dir}\"");
                if (!result.Contains("error", StringComparison.OrdinalIgnoreCase))
                {
                    addedPaths++;
                }
            }
            Logger.LogInformation("[ReloadSymbols] Added {Count} directories to LLDB search paths", addedPaths);

            // Get symbol files that LLDB can load (.dbg, .debug only - not .pdb)
            var symbolFiles = SymbolManager.GetSymbolFilesInDirectory(symbolDir, lldbOnly: true);
            
            foreach (var symbolFile in symbolFiles)
            {
                var result = manager.ExecuteCommand($"target symbols add \"{symbolFile}\"");
                if (!result.Contains("error", StringComparison.OrdinalIgnoreCase))
                {
                    loadedCount++;
                    Logger.LogInformation("[ReloadSymbols] Loaded symbol file: {File}", Path.GetFileName(symbolFile));
                }
                else
                {
                    Logger.LogWarning("[ReloadSymbols] Failed to load symbol file: {File} - {Error}", 
                        Path.GetFileName(symbolFile), result.Trim());
                }
            }
            
            messages.Add($"Added {addedPaths} directories to symbol search paths");
            messages.Add($"Loaded {loadedCount} of {symbolFiles.Count} symbol files");
        }
        else if (manager.DebuggerType == "WinDbg")
        {
            // For WinDbg, update the symbol path and reload
            var allDirs = SymbolManager.GetAllSubdirectories(symbolDir);
            var symbolPath = string.Join(";", allDirs);
            
            // Append to existing symbol path
            manager.ExecuteCommand($".sympath+ {symbolPath}");
            addedPaths = allDirs.Count;
            
            // Force reload symbols
            var result = manager.ExecuteCommand(".reload /f");
            
            messages.Add($"Added {addedPaths} directories to symbol path");
            messages.Add("Executed .reload /f to reload symbols");
            
            // Check if symbols loaded
            var symResult = manager.ExecuteCommand("lm");
            var loadedModules = symResult.Split('\n').Count(l => l.Contains(".pdb", StringComparison.OrdinalIgnoreCase));
            messages.Add($"Modules with symbols: {loadedModules}");
        }
        else
        {
            return $"Unknown debugger type: {manager.DebuggerType}";
        }

        // Clear command cache since symbols have changed - this ensures subsequent
        // commands like clrstack will re-run and show improved stack traces
        if (addedPaths > 0 || loadedCount > 0)
        {
            manager.ClearCommandCache();
            Logger.LogInformation("[ReloadSymbols] Cleared command cache after loading new symbols");
        }

        return $"Symbol reload completed.\n{string.Join("\n", messages)}";
    }
    
    /// <summary>
    /// Clears the SymbolFiles list from the dump metadata to invalidate the smart cache.
    /// </summary>
    private void ClearSymbolFilesFromMetadata(string dumpStoragePath, string userId, string dumpId)
    {
        try
        {
            // Find the dump file to get its directory
            var userDumpDir = Path.Combine(dumpStoragePath, userId);
            if (!Directory.Exists(userDumpDir))
                return;
            
            // Look for matching dump files
            var dumpFiles = Directory.GetFiles(userDumpDir, $"{dumpId}*.dmp", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(userDumpDir, $"{dumpId}*.dump", SearchOption.TopDirectoryOnly))
                .ToList();
            
            if (dumpFiles.Count == 0)
                return;
            
            // Get the metadata file path (same name as dump but .json)
            var dumpFile = dumpFiles.First();
            var metadataPath = Path.ChangeExtension(dumpFile, ".json");
            
            if (!File.Exists(metadataPath))
                return;
            
            // Load, clear SymbolFiles, and save
            var json = File.ReadAllText(metadataPath);
            var metadata = System.Text.Json.JsonSerializer.Deserialize<Controllers.DumpMetadata>(json);
            
            if (metadata != null && metadata.SymbolFiles != null && metadata.SymbolFiles.Count > 0)
            {
                Logger.LogInformation("[ClearSymbolCache] Clearing {Count} entries from SymbolFiles in metadata", metadata.SymbolFiles.Count);
                metadata.SymbolFiles = null;
                var updatedJson = System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(metadataPath, updatedJson);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[ClearSymbolCache] Failed to clear SymbolFiles from metadata");
        }
    }
}
