using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DebuggerMcp.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp;

/// <summary>
/// Manages interactions with LLDB (Low Level Debugger) for analyzing core dumps on Linux/macOS.
/// </summary>
/// <remarks>
/// This class provides a wrapper around the LLDB command-line debugger, using process-based
/// communication to send commands and receive output. It implements the same interface as
/// WinDbgManager to provide a consistent API across platforms.
/// </remarks>
public class LldbManager : IDebuggerManager
{
    /// <summary>
    /// Logger for diagnostic output.
    /// </summary>
    private readonly ILogger _logger;
    /// <summary>
    /// Default path to the .NET host binary on Linux.
    /// This binary can load any .NET runtime version - SOS will find the correct DAC.
    /// </summary>
    private const string DefaultDotnetHostPath = "/usr/share/dotnet/dotnet";

    /// <summary>
    /// The LLDB process instance.
    /// </summary>
    private Process? _lldbProcess;

    /// <summary>
    /// Buffer for capturing output from LLDB.
    /// Using string instead of StringBuilder since we need to check for sentinel anyway.
    /// </summary>
    private string _outputBuffer = string.Empty;

    /// <summary>
    /// Lock object for thread-safe output buffer access.
    /// </summary>
    private readonly object _outputLock = new();

    /// <summary>
    /// Indicates whether this instance has been disposed.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Event to signal when command output is complete (sentinel received).
    /// ManualResetEventSlim is more efficient for short wait times.
    /// </summary>
    private readonly ManualResetEventSlim _outputCompleteEvent = new(false);

    /// <summary>
    /// Sentinel command sent after each real command to detect completion.
    /// </summary>
    private const string SentinelCommand = "---MCP-END---";

    /// <summary>
    /// The exact error message LLDB produces for the sentinel command.
    /// </summary>
    private const string SentinelError = "error: '---MCP-END---' is not a valid command.";

    /// <summary>
    /// Maximum time to wait for command completion (seconds).
    /// </summary>
    private const int CommandTimeoutSeconds = 30;

    /// <summary>
    /// Time to wait for LLDB process to initialize after startup (milliseconds).
    /// </summary>
    private const int ProcessInitializationDelayMs = 500;

    /// <summary>
    /// The path to the currently open dump file.
    /// </summary>
    private string? _currentDumpPath;

    /// <summary>
    /// The symbol cache directory for downloaded symbols.
    /// </summary>
    private string? _symbolCacheDirectory;

    /// <summary>
    /// The detected .NET runtime version from the dump (e.g., "9.0.10").
    /// Parsed from dotnet-symbol output to select the correct libcoreclr.so version.
    /// </summary>
    private string? _detectedRuntimeVersion;

    /// <summary>
    /// The executable path used when opening the dump (for recovery after crash).
    /// </summary>
    private string? _currentExecutablePath;

    /// <summary>
    /// Lock object for crash recovery to prevent concurrent recovery attempts.
    /// </summary>
    private readonly object _recoveryLock = new();

    /// <summary>
    /// Flag indicating whether a crash recovery is in progress.
    /// </summary>
    private bool _isRecovering;

    /// <summary>
    /// Gets a value indicating whether the debugger engine has been initialized.
    /// </summary>
    /// <value>
    /// <c>true</c> if the LLDB process is running; otherwise, <c>false</c>.
    /// </value>
    public bool IsInitialized => _lldbProcess != null && !_lldbProcess.HasExited;

    /// <summary>
    /// Gets a value indicating whether a dump file is currently open.
    /// </summary>
    /// <value>
    /// <c>true</c> if a core dump is loaded in LLDB; otherwise, <c>false</c>.
    /// </value>
    public bool IsDumpOpen { get; private set; }

    /// <summary>
    /// Gets the path to the currently open dump file.
    /// </summary>
    /// <value>
    /// The full path to the dump file if one is open; otherwise, <c>null</c>.
    /// </value>
    public string? CurrentDumpPath => _currentDumpPath;

    /// <summary>
    /// Gets a value indicating whether the SOS extension is loaded.
    /// </summary>
    /// <value>
    /// <c>true</c> if SOS is loaded and .NET commands are available; otherwise, <c>false</c>.
    /// </value>
    public bool IsSosLoaded { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the currently open dump is a .NET dump.
    /// </summary>
    /// <value>
    /// <c>true</c> if the dump contains .NET runtime modules; otherwise, <c>false</c>.
    /// </value>
    public bool IsDotNetDump { get; private set; }

    /// <summary>
    /// Gets the type of debugger this manager controls.
    /// </summary>
    /// <value>
    /// Always returns "LLDB" for this implementation.
    /// </value>
    public string DebuggerType => "LLDB";

    /// <summary>
    /// Command cache for improving performance during analysis operations.
    /// </summary>
    private readonly CommandCache _commandCache;

    /// <summary>
    /// Gets whether command caching is currently enabled.
    /// </summary>
    public bool IsCommandCacheEnabled => _commandCache.IsEnabled;



    /// <summary>
    /// Initializes a new instance of the <see cref="LldbManager"/> class.
    /// </summary>
    /// <remarks>
    /// The constructor does not start the LLDB process. Call <see cref="InitializeAsync"/>
    /// to start LLDB before using other methods.
    /// </remarks>
    public LldbManager() : this(NullLogger<LldbManager>.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LldbManager"/> class with a logger.
    /// </summary>
    /// <param name="logger">The logger for diagnostic output.</param>
    /// <remarks>
    /// The constructor does not start the LLDB process. Call <see cref="InitializeAsync"/>
    /// to start LLDB before using other methods.
    /// </remarks>
    public LldbManager(ILogger<LldbManager> logger)
    {
        _logger = logger;
        _commandCache = new CommandCache(logger);
        // Initialization is deferred to the InitializeAsync() method
    }



    /// <summary>
    /// Initializes the debugger engine by starting the LLDB process.
    /// </summary>
    /// <remarks>
    /// <para>This method starts LLDB in interactive mode with stdin/stdout redirection.</para>
    /// <para>LLDB must be installed and available in the system PATH.</para>
    /// <para>The method includes an async delay to allow LLDB to fully initialize.</para>
    /// </remarks>
    /// <returns>A task representing the asynchronous initialization operation.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if LLDB is already initialized or if the LLDB process fails to start.
    /// </exception>
    public virtual async Task InitializeAsync()
    {
        _logger.LogInformation("[LLDB] Starting initialization...");

        // Check if already initialized
        if (IsInitialized)
        {
            throw new InvalidOperationException("LLDB is already initialized");
        }

        try
        {
            // Create process start info for LLDB
            var startInfo = new ProcessStartInfo
            {
                FileName = "lldb",
                // No arguments - we use interactive mode via stdin/stdout
                // Commands are sent via StandardInput and output read via StandardOutput
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _logger.LogDebug("[LLDB] Process start info: FileName={FileName}", startInfo.FileName);

            // Start the LLDB process
            _lldbProcess = new Process { StartInfo = startInfo };

            // Set up output handlers
            _lldbProcess.OutputDataReceived += OnOutputDataReceived;
            _lldbProcess.ErrorDataReceived += OnErrorDataReceived;

            // Start the process
            _logger.LogInformation("[LLDB] Starting LLDB process...");
            if (!_lldbProcess.Start())
            {
                throw new InvalidOperationException("Failed to start LLDB process");
            }
            _logger.LogInformation("[LLDB] LLDB process started - PID: {ProcessId}", _lldbProcess.Id);

            // Begin asynchronous reading of output
            _lldbProcess.BeginOutputReadLine();
            _lldbProcess.BeginErrorReadLine();

            // Wait for LLDB to initialize and become ready for commands.
            // This delay is necessary because LLDB needs time to:
            // 1. Load its internal state and configurations
            // 2. Initialize the debugging subsystem
            // 3. Begin accepting commands on stdin
            // Using async delay to avoid blocking ThreadPool threads in ASP.NET scenarios
            _logger.LogDebug("[LLDB] Waiting {Delay}ms for process initialization...", ProcessInitializationDelayMs);
            await Task.Delay(ProcessInitializationDelayMs);

            // Verify the process is still running
            if (_lldbProcess.HasExited)
            {
                // Bail out early if LLDB died before we can issue commands
                throw new InvalidOperationException(
                    $"LLDB process exited immediately with code {_lldbProcess.ExitCode}");
            }

            _logger.LogInformation("[LLDB] Initialization complete - LLDB is ready");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogError(ex, "[LLDB] Failed to initialize LLDB");
            // Wrap other exceptions for consistent error handling
            throw new InvalidOperationException(
                $"Failed to initialize LLDB. Ensure LLDB is installed and in PATH. Error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Opens a core dump file for analysis.
    /// </summary>
    /// <param name="dumpFilePath">The absolute path to the core dump file.</param>
    /// <remarks>
    /// <para>The debugger must be initialized before calling this method.</para>
    /// <para>For Linux core dumps, you may need to specify the executable path separately.</para>
    /// <para>This implementation uses the "target create --core" command.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the debugger is not initialized or if a dump is already open.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// Thrown if the specified dump file does not exist.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown if the dump file path is null, empty, or whitespace.
    /// </exception>
    public virtual void OpenDumpFile(string dumpFilePath, string? executablePath = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("[LLDB] Opening dump file: {DumpPath}", dumpFilePath);
        
        if (!string.IsNullOrEmpty(executablePath))
        {
            _logger.LogInformation("[LLDB] Using custom executable for standalone app: {ExecutablePath}", executablePath);
        }

        // Validate the dump file path first (before checking initialization)
        if (string.IsNullOrWhiteSpace(dumpFilePath))
        {
            throw new ArgumentException("Dump file path cannot be null or empty", nameof(dumpFilePath));
        }

        // Validate that the manager is initialized
        if (!IsInitialized)
        {
            throw new InvalidOperationException("LLDB is not initialized");
        }

        // Validate that the dump file exists
        if (!File.Exists(dumpFilePath))
        {
            throw new FileNotFoundException($"Core dump file not found: {dumpFilePath}");
        }

        // Log file size for diagnostics
        var fileInfo = new FileInfo(dumpFilePath);
        _logger.LogInformation("[LLDB] Dump file size: {Size:N0} bytes ({SizeMB:N1} MB)",
            fileInfo.Length, fileInfo.Length / (1024.0 * 1024.0));

        // Check if a dump is already open
        if (IsDumpOpen)
        {
            throw new InvalidOperationException("A dump file is already open. Close it first.");
        }

        try
        {
            // Store the dump path and executable path (for recovery after crash)
            _currentDumpPath = dumpFilePath;
            _currentExecutablePath = executablePath;

            // Create symbol cache directory for this dump
            var dumpDir = Path.GetDirectoryName(dumpFilePath) ?? "/tmp";
            var dumpName = Path.GetFileNameWithoutExtension(dumpFilePath);
            _symbolCacheDirectory = Path.Combine(dumpDir, $".symbols_{dumpName}");
            _logger.LogDebug("[LLDB] Symbol cache directory: {SymbolCache}", _symbolCacheDirectory);

            // Try to load cached runtime version from metadata JSON
            LoadRuntimeVersionFromMetadata();

            // Pre-download symbols using dotnet-symbol (optional, for managed PDBs)
            try
            {
                _logger.LogInformation("[LLDB] Downloading symbols using dotnet-symbol...");
                var symbolsDownloaded = DownloadSymbols(dumpFilePath, _symbolCacheDirectory);
                _logger.LogInformation("[LLDB] Symbol download {Status} - Elapsed: {Elapsed}ms",
                    symbolsDownloaded ? "completed" : "skipped", sw.ElapsedMilliseconds);

                // Save detected runtime version to metadata JSON
                SaveRuntimeVersionToMetadata();
            }
            catch (Exception ex)
            {
                // Symbol download is optional, continue without it
                _logger.LogWarning(ex, "[LLDB] Symbol download failed (continuing without symbols)");
            }

            // Run verifycore to detect platform (Alpine/musl, architecture)
            // This is especially useful for standalone apps where LLDB's image list may be incomplete
            try
            {
                _logger.LogInformation("[LLDB] Running platform detection via dotnet-symbol --verifycore...");
                var verifyCoreResult = VerifyCore(dumpFilePath);
                if (verifyCoreResult != null)
                {
                    _logger.LogInformation("[LLDB] Platform detection complete: IsAlpine={IsAlpine}, Arch={Arch}",
                        verifyCoreResult.IsAlpine, verifyCoreResult.Architecture ?? "unknown");
                }
            }
            catch (Exception ex)
            {
                // Platform detection is optional
                _logger.LogDebug(ex, "[LLDB] Platform detection failed (continuing without it)");
            }

            // Configure LLDB to search for symbols in the symbol cache directory and all subdirectories
            // This must be done BEFORE opening the dump so LLDB can find debug symbols
            if (!string.IsNullOrEmpty(_symbolCacheDirectory) && Directory.Exists(_symbolCacheDirectory))
            {
                _logger.LogInformation("[LLDB] Configuring symbol search paths: {SymbolCache}", _symbolCacheDirectory);

                // Add root directory
                ExecuteCommandInternal($"settings append target.debug-file-search-paths \"{_symbolCacheDirectory}\"");

                // Add all subdirectories (dotnet-symbol creates nested structure like guid/filename)
                try
                {
                    var subDirs = Directory.GetDirectories(_symbolCacheDirectory, "*", SearchOption.AllDirectories);
                    foreach (var subDir in subDirs)
                    {
                        ExecuteCommandInternal($"settings append target.debug-file-search-paths \"{subDir}\"");
                    }
                    if (subDirs.Length > 0)
                    {
                        _logger.LogInformation("[LLDB] Added {Count} subdirectories to symbol search path", subDirs.Length);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[LLDB] Failed to enumerate symbol subdirectories");
                }
            }

            // Detect if this is a standalone app (first module is NOT the dotnet host)
            // Standalone apps bundle the runtime, so their main module is the app binary itself
            var isStandaloneApp = VerifiedCorePlatform?.MainExecutableName != null &&
                !VerifiedCorePlatform.MainExecutableName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                !VerifiedCorePlatform.MainExecutablePath?.Contains("/dotnet/") == true;

            // Open the core dump using LLDB command.
            // Priority order:
            // 1. Custom executable (uploaded binary for standalone apps)
            // 2. Core-only mode (for standalone apps without the binary - dotnet host won't work!)
            // 3. dotnet host binary (for framework-dependent apps)
            // 4. Core-only load (fallback when dotnet host not found)
            string targetCreateCmd;
            
            if (!string.IsNullOrEmpty(executablePath) && File.Exists(executablePath))
            {
                // Use custom executable for standalone apps (binary was uploaded)
                _logger.LogInformation("[LLDB] Using custom executable: {ExecutablePath}", executablePath);
                targetCreateCmd = $"target create \"{executablePath}\" --core \"{dumpFilePath}\"";
            }
            else if (isStandaloneApp)
            {
                // Standalone app detected but no binary provided
                // Try to find a usable ELF file (.dbg might work if it has program headers)
                var usableElf = FindUsableElfForStandaloneApp(VerifiedCorePlatform!.MainExecutableName!);
                
                if (usableElf != null)
                {
                    // Found a usable ELF (possibly a .dbg file with full ELF structure)
                    _logger.LogInformation("[LLDB] Using found ELF file as executable: {Path}", usableElf);
                    targetCreateCmd = $"target create \"{usableElf}\" --core \"{dumpFilePath}\"";
                }
                else
                {
                    // No usable ELF found - fall back to core-only mode (limited functionality)
                    _logger.LogWarning("[LLDB] Standalone app detected ({Name}) but no usable binary found - using core-only mode",
                        VerifiedCorePlatform.MainExecutableName);
                    _logger.LogWarning("[LLDB] SOS functionality will be severely limited without the executable!");
                    _logger.LogInformation("[LLDB] Tip: Upload the binary using 'dumps binary <dumpId> <path>' for full debugging support");
                    targetCreateCmd = $"target create --core \"{dumpFilePath}\"";
                }
            }
            else
            {
                // Framework-dependent app - try to use dotnet host
                var dotnetHost = GetDotnetHostPath();
                _logger.LogDebug("[LLDB] dotnet host path: {DotnetHost} (exists: {Exists})", dotnetHost, File.Exists(dotnetHost));

                if (File.Exists(dotnetHost))
                {
                    _logger.LogInformation("[LLDB] Using dotnet host for framework-dependent app");
                    targetCreateCmd = $"target create \"{dotnetHost}\" --core \"{dumpFilePath}\"";
                }
                else
                {
                    // Fall back to core-only load when a suitable host binary is missing
                    _logger.LogWarning("[LLDB] dotnet host not found, using core-only mode");
                    targetCreateCmd = $"target create --core \"{dumpFilePath}\"";
                }
            }

            _logger.LogInformation("[LLDB] Executing: {Command}", targetCreateCmd);
            ExecuteCommandInternal(targetCreateCmd);

            // Mark the dump as open
            IsDumpOpen = true;

            // Explicitly add debug symbol files (.dbg) from the symbol cache
            // LLDB's search path doesn't auto-load .dbg files, we need 'target symbols add'
            LoadDebugSymbolFiles();

            // If we're in core-only mode (no executable), try to find and load .dbg for the main executable
            // This helps with standalone apps where we don't have the binary
            if (string.IsNullOrEmpty(executablePath) && VerifiedCorePlatform?.MainExecutableName != null)
            {
                TryLoadMainExecutableDebugSymbols(VerifiedCorePlatform.MainExecutableName);
            }

            // Load all native modules from verifycore at their correct memory addresses
            // This is critical for SOS to work properly - LLDB doesn't auto-load modules from core dumps
            // Without this, libcoreclr.so and other modules won't be mapped correctly
            if (VerifiedCorePlatform?.ModuleAddresses != null && VerifiedCorePlatform.ModuleAddresses.Count > 0)
            {
                _logger.LogInformation("[LLDB] Loading {Count} native modules from verifycore at their correct memory addresses...", 
                    VerifiedCorePlatform.ModuleAddresses.Count);
                var loadResults = LoadModulesFromVerifyCore();
                var successCount = loadResults.Count(r => r.Value);
                _logger.LogInformation("[LLDB] Successfully loaded {Success}/{Total} modules from verifycore", 
                    successCount, loadResults.Count);
            }

            // Log loaded modules for diagnostics
            var moduleList = ExecuteCommandInternal("image list");
            _logger.LogInformation("[LLDB] Loaded modules:\n{Modules}", moduleList);

            // Enable command caching for the entire dump session
            // Dump files are static, so all commands can be safely cached
            EnableCommandCache();

            // Detect if this is a .NET dump and auto-load SOS if so
            IsDotNetDump = DetectDotNetDump(moduleList);
            if (IsDotNetDump)
            {
                _logger.LogInformation("[LLDB] .NET runtime detected, auto-loading SOS extension...");
                try
                {
                    LoadSosExtension();
                    _logger.LogInformation("[LLDB] SOS extension auto-loaded successfully");
                }
                catch (Exception sosEx)
                {
                    _logger.LogWarning(sosEx, "[LLDB] Failed to auto-load SOS extension (dump is still usable for native debugging)");
                }
            }
            else
            {
                _logger.LogInformation("[LLDB] No .NET runtime detected - this appears to be a native dump");
            }

            sw.Stop();
            _logger.LogInformation("[LLDB] Dump file opened successfully - Total time: {Elapsed}ms", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LLDB] Failed to open dump file");
            // Reset all dump-specific state on failure
            IsDumpOpen = false;
            IsDotNetDump = false;
            _currentDumpPath = null;
            _detectedRuntimeVersion = null;
            throw new InvalidOperationException($"Failed to open core dump: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Closes the currently open dump file.
    /// </summary>
    /// <remarks>
    /// This method removes the current target in LLDB, allowing another dump to be opened.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the debugger is not initialized.
    /// </exception>
    public virtual void CloseDump()
    {
        // Validate that the manager is initialized
        if (!IsInitialized)
        {
            throw new InvalidOperationException("LLDB is not initialized");
        }

        // If no dump is open, nothing to do
        if (!IsDumpOpen)
        {
            return;
        }

        try
        {
            // Clear command cache when closing dump (cache is dump-specific)
            _commandCache.Clear();

            // Delete the current target to close the dump
            // Note: Must disable cache temporarily since this modifies state
            var cacheWasEnabled = _commandCache.IsEnabled;
            _commandCache.IsEnabled = false;
            try
            {
                ExecuteCommandInternal("target delete");
            }
            finally
            {
                _commandCache.IsEnabled = cacheWasEnabled;
            }

            // Mark the dump as closed and reset all dump-specific state
            IsDumpOpen = false;
            IsSosLoaded = false;
            IsDotNetDump = false;
            _currentDumpPath = null;
            _detectedRuntimeVersion = null;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to close dump: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Enables command caching to improve performance for repeated commands.
    /// </summary>
    public void EnableCommandCache()
    {
        _commandCache.IsEnabled = true;
        _logger.LogInformation("[LLDB] Command cache enabled");
    }

    /// <summary>
    /// Disables command caching.
    /// </summary>
    public void DisableCommandCache()
    {
        _commandCache.IsEnabled = false;
        _logger.LogInformation("[LLDB] Command cache disabled");
    }

    /// <summary>
    /// Clears the command cache and ObjectInspector cache.
    /// </summary>
    public void ClearCommandCache()
    {
        _commandCache.Clear();
        ObjectInspection.ObjectInspector.ClearCache();
        _logger.LogInformation("[LLDB] Command cache and ObjectInspector cache cleared");
    }

    /// <summary>
    /// Executes an LLDB command and returns the output.
    /// </summary>
    /// <param name="command">The LLDB command to execute (e.g., "bt" for backtrace, "register read").</param>
    /// <returns>The output from the LLDB command execution.</returns>
    /// <remarks>
    /// <para>The debugger must be initialized and a dump must be open before calling this method.</para>
    /// <para>Common LLDB commands:</para>
    /// <list type="bullet">
    /// <item><description>bt - Backtrace (call stack)</description></item>
    /// <item><description>register read - Display registers</description></item>
    /// <item><description>image list - List loaded modules</description></item>
    /// <item><description>memory read - Read memory</description></item>
    /// </list>
    /// <para>After loading SOS, .NET commands like "clrthreads", "dumpheap" are available.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the debugger is not initialized or no dump is open.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown if the command is null, empty, or whitespace.
    /// </exception>
    public virtual string ExecuteCommand(string command)
    {
        // Validate the command first (fail-fast on bad input)
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command cannot be null or empty", nameof(command));
        }

        // Validate that the manager is initialized
        if (!IsInitialized)
        {
            throw new InvalidOperationException("LLDB is not initialized");
        }

        // Validate that a dump is open (consistent with WinDbgManager behavior)
        if (!IsDumpOpen)
        {
            throw new InvalidOperationException("No dump file is currently open");
        }

        // Transform SOS commands: In LLDB, '!' is a history expansion prefix, not a command prefix
        // SOS commands in LLDB should be called without the '!' prefix
        var transformedCommand = TransformSosCommand(command);

        // Check cache first (significant performance improvement for analysis operations)
        if (_commandCache.TryGetCachedResult(transformedCommand, out var cachedResult))
        {
            return cachedResult!;
        }

        // Execute and cache the result
        var result = ExecuteCommandInternal(transformedCommand);
        _commandCache.CacheResult(transformedCommand, result);

        return result;
    }

    /// <summary>
    /// Transforms WinDbg-style SOS commands (with ! prefix) to LLDB-compatible commands.
    /// </summary>
    /// <param name="command">The command to transform.</param>
    /// <returns>The transformed command suitable for LLDB.</returns>
    /// <remarks>
    /// <para>In WinDbg, SOS commands use the ! prefix (e.g., !pe, !dumpheap -stat).</para>
    /// <para>In LLDB with the SOS plugin, the ! character is a history expansion prefix.</para>
    /// <para>SOS commands in LLDB should be called without the ! prefix (e.g., pe, dumpheap -stat).</para>
    /// <para>This transformation allows the same commands to work across both debuggers.</para>
    /// </remarks>
    private static string TransformSosCommand(string command)
    {
        // If command starts with '!', it's likely a SOS command - remove the prefix
        if (command.StartsWith('!'))
        {
            return command.Substring(1);
        }

        return command;
    }

    /// <summary>
    /// Internal method that executes an LLDB command without requiring a dump to be open.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <returns>The output from executing the command.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the manager is not initialized or command execution fails.
    /// </exception>
    /// <remarks>
    /// This method uses sentinel-based completion detection for instant response times.
    /// A sentinel command is sent after the real command, and when LLDB returns an error
    /// for the invalid sentinel, we know the real command has completed.
    /// </remarks>
    private string ExecuteCommandInternal(string command)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogDebug("[LLDB] Sending command: {Command}", command);

        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command cannot be null or empty", nameof(command));
        }

        if (!IsInitialized)
        {
            throw new InvalidOperationException("LLDB is not initialized");
        }

        try
        {
            // Reset state
            lock (_outputLock)
            {
                _outputBuffer = string.Empty;
            }
            _outputCompleteEvent.Reset();

            // Send command + sentinel
            _lldbProcess!.StandardInput.WriteLine(command);
            _lldbProcess.StandardInput.WriteLine(SentinelCommand);
            _lldbProcess.StandardInput.Flush();

            // Wait for sentinel - event is ONLY signaled when sentinel error is received
            var completed = _outputCompleteEvent.Wait(TimeSpan.FromSeconds(CommandTimeoutSeconds));

            sw.Stop();

            if (!completed)
            {
                _logger.LogWarning("[LLDB] Command timed out after {Timeout}s: {Command}",
                    CommandTimeoutSeconds, command);
            }

            var output = ExtractOutput();
            LogCommandOutput(command, output, sw.ElapsedMilliseconds);
            
            // Check for LLDB crash indicators and attempt recovery
            if (DetectLldbCrash(output))
            {
                _logger.LogError("[LLDB] Detected LLDB crash during command: {Command}", command);
                
                // Attempt auto-recovery (restore LLDB but don't retry the command)
                if (TryRecoverFromCrash())
                {
                    _logger.LogInformation("[LLDB] Successfully recovered from crash - LLDB restored and dump reopened");
                    // Return error message instead of retrying (the crashing command might crash again)
                    throw new InvalidOperationException(
                        $"LLDB crashed while executing command. LLDB has been automatically recovered and the dump reopened. " +
                        $"The command that caused the crash was: {(command.Length > 100 ? command[..97] + "..." : command)}. " +
                        $"Please retry your operation.");
                }
                else
                {
                    _logger.LogError("[LLDB] Failed to recover from crash");
                    throw new InvalidOperationException(
                        $"LLDB crashed while executing command and recovery failed. " +
                        $"Please close and reopen the dump manually.");
                }
            }
            
            return output;
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not ArgumentException)
        {
            _logger.LogError(ex, "[LLDB] Command failed: {Command}", command);
            
            // Check if LLDB process has exited (crashed)
            if (_lldbProcess?.HasExited == true)
            {
                _logger.LogWarning("[LLDB] Process has exited unexpectedly, attempting recovery...");
                if (TryRecoverFromCrash())
                {
                    _logger.LogInformation("[LLDB] Recovered from crash - LLDB restored and dump reopened");
                    throw new InvalidOperationException(
                        $"LLDB crashed unexpectedly. LLDB has been automatically recovered and the dump reopened. " +
                        $"Please retry your operation. Original error: {ex.Message}");
                }
            }
            
            throw new InvalidOperationException($"Failed to execute command: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Extracts command output from the buffer, removing echo, sentinel, and prompts.
    /// </summary>
    /// <returns>The cleaned command output.</returns>
    private string ExtractOutput()
    {
        string output;
        lock (_outputLock)
        {
            output = _outputBuffer;
        }

        // Output format (sentinel error is discarded in OnErrorDataReceived):
        // (lldb) <command>         <- prompt + echo (line 1)
        // <actual output...>       <- what we want
        // (lldb) ---MCP-END---     <- prompt + sentinel (stdout) - triggers completion

        // 1. Remove command echo (first line)
        var firstNewline = output.IndexOf('\n');
        if (firstNewline >= 0)
        {
            output = output[(firstNewline + 1)..];
        }

        // 2. Find and remove sentinel + everything after
        var sentinelIndex = output.IndexOf(SentinelCommand, StringComparison.Ordinal);
        if (sentinelIndex >= 0)
        {
            output = output[..sentinelIndex];
        }

        // 3. Remove trailing (lldb) prompt if present
        output = output.TrimEnd();
        if (output.EndsWith("(lldb)"))
        {
            output = output[..^6];
        }

        return output.Trim();
    }

    /// <summary>
    /// Logs the output of an LLDB command.
    /// </summary>
    private void LogCommandOutput(string command, string output, long elapsedMs)
    {
        // Truncate command for display if too long
        var displayCommand = command.Length > 100 ? command[..97] + "..." : command;

        if (string.IsNullOrWhiteSpace(output))
        {
            _logger.LogInformation("[LLDB] Command '{Command}' completed with no output ({Elapsed}ms)", displayCommand, elapsedMs);
        }
        else
        {
            // Log full output at Info level for diagnostic visibility
            _logger.LogInformation("[LLDB] Command '{Command}' output ({Elapsed}ms):\n{Output}",
                displayCommand, elapsedMs, output);
        }
    }

    /// <summary>
    /// Detects if LLDB has crashed based on command output.
    /// </summary>
    /// <param name="output">The command output to check.</param>
    /// <returns>True if crash indicators are found, false otherwise.</returns>
    private bool DetectLldbCrash(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        // Check for LLDB crash indicators in the output
        // These messages appear when LLDB crashes during command execution
        var crashIndicators = new[]
        {
            "PLEASE submit a bug report",
            "Stack dump:",
            "LLDB diagnostics will be written to",
            "Segmentation fault",
            "Aborted (core dumped)"
        };

        foreach (var indicator in crashIndicators)
        {
            if (output.Contains(indicator, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Also check if the process has exited unexpectedly
        if (_lldbProcess?.HasExited == true)
        {
            _logger.LogWarning("[LLDB] Process has exited with code: {ExitCode}", _lldbProcess.ExitCode);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to recover from an LLDB crash by reinitializing and reopening the dump.
    /// </summary>
    /// <returns>True if recovery was successful, false otherwise.</returns>
    private bool TryRecoverFromCrash()
    {
        lock (_recoveryLock)
        {
            if (_isRecovering)
            {
                _logger.LogDebug("[LLDB] Recovery already in progress, skipping");
                return false;
            }

            _isRecovering = true;
        }

        try
        {
            _logger.LogWarning("[LLDB] Attempting crash recovery...");

            // Save state before cleanup
            var dumpPath = _currentDumpPath;
            var executablePath = _currentExecutablePath;
            var wasOpen = IsDumpOpen;

            // Cleanup the crashed process
            CleanupProcess();

            // Reinitialize
            _logger.LogInformation("[LLDB] Reinitializing LLDB after crash...");
            Task.Run(() => InitializeAsync()).GetAwaiter().GetResult();

            // Reopen dump if one was open
            if (wasOpen && !string.IsNullOrEmpty(dumpPath) && File.Exists(dumpPath))
            {
                _logger.LogInformation("[LLDB] Reopening dump after crash recovery: {DumpPath}", dumpPath);
                OpenDumpFile(dumpPath, executablePath);
                _logger.LogInformation("[LLDB] Crash recovery complete - dump reopened successfully");
            }
            else
            {
                _logger.LogInformation("[LLDB] Crash recovery complete - LLDB reinitialized (no dump to reopen)");
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LLDB] Failed to recover from crash");
            return false;
        }
        finally
        {
            lock (_recoveryLock)
            {
                _isRecovering = false;
            }
        }
    }

    /// <summary>
    /// Cleans up the LLDB process without full disposal.
    /// </summary>
    private void CleanupProcess()
    {
        try
        {
            if (_lldbProcess != null)
            {
                try
                {
                    if (!_lldbProcess.HasExited)
                    {
                        _lldbProcess.Kill(entireProcessTree: true);
                        _lldbProcess.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process already exited
                }
                finally
                {
                    _lldbProcess.Dispose();
                    _lldbProcess = null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LLDB] Error during process cleanup");
        }

        // Reset state but preserve dump/executable path for recovery
        IsDumpOpen = false;
        IsSosLoaded = false;
        IsDotNetDump = false;
        _commandCache.IsEnabled = false;
        _commandCache.Clear();
    }

    /// <summary>
    /// Configures the symbol path for the debugger.
    /// </summary>
    /// <param name="symbolPath">The symbol path string (space-separated directories for LLDB).</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the manager is not initialized.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the symbol path is null, empty, or whitespace.
    /// </exception>
    /// <remarks>
    /// LLDB uses space-separated directory paths for symbol search.
    /// Remote symbol servers are not directly supported in LLDB.
    /// Example: "/path/to/symbols /another/path"
    /// This method can be called before opening a dump file, which is the recommended practice
    /// to ensure symbols are available when the dump is loaded.
    /// </remarks>
    public virtual void ConfigureSymbolPath(string symbolPath)
    {
        // Validate parameters
        if (string.IsNullOrWhiteSpace(symbolPath))
        {
            throw new ArgumentException("Symbol path cannot be null or empty.", nameof(symbolPath));
        }

        // Validate that the manager is initialized
        if (!IsInitialized)
        {
            throw new InvalidOperationException("LLDB is not initialized");
        }

        try
        {
            // Split space-separated paths and add each one
            var paths = symbolPath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in paths)
            {
                // Use settings set target.debug-file-search-paths to add symbol search paths
                // Note: Using internal method because this can be executed before a dump is open
                ExecuteCommandInternal($"settings append target.debug-file-search-paths {path}");
            }
        }
        catch (Exception ex)
        {
            // Wrap any exceptions for consistent error handling
            throw new InvalidOperationException($"Failed to configure symbol path: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Loads the SOS (Son of Strike) extension for .NET debugging.
    /// </summary>
    /// <remarks>
    /// <para>The debugger must be initialized and a dump must be open before calling this method.</para>
    /// <para>On Linux/macOS, this loads the libsosplugin.so library.</para>
    /// <para>The SOS plugin must be installed (typically comes with .NET SDK).</para>
    /// <para>After loading, commands like "clrthreads", "dumpheap", "pe", etc. become available.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the debugger is not initialized or if no dump is open.
    /// </exception>
    public virtual void LoadSosExtension()
    {
        // Validate that the manager is initialized
        if (!IsInitialized)
        {
            throw new InvalidOperationException("LLDB is not initialized");
        }

        // Validate that a dump is open
        if (!IsDumpOpen)
        {
            throw new InvalidOperationException("No core dump is currently open");
        }

        // Idempotent: if SOS is already loaded, nothing to do
        if (IsSosLoaded)
        {
            _logger.LogDebug("[LLDB] SOS extension already loaded, skipping");
            return;
        }

        try
        {
            // Step 1: Load the SOS plugin
            var sosPath = FindSosPlugin();
            string loadOutput;

            if (!string.IsNullOrEmpty(sosPath))
            {
                _logger.LogInformation("[LLDB] Loading SOS plugin from: {SosPath}", sosPath);
                loadOutput = ExecuteCommand($"plugin load \"{sosPath}\"");
            }
            else
            {
                _logger.LogWarning("[LLDB] SOS plugin not found in common locations, trying default name");
                loadOutput = ExecuteCommand("plugin load libsosplugin.so");
            }

            _logger.LogDebug("[LLDB] Plugin load output: {Output}", loadOutput);

            // Check if load failed
            if (loadOutput.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
                loadOutput.Contains("failed to load", StringComparison.OrdinalIgnoreCase) ||
                loadOutput.Contains("no such file", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Failed to load SOS plugin. Output: {loadOutput.Trim()}");
            }

            // Verify SOS is loaded
            _logger.LogInformation("[LLDB] Verifying SOS loaded correctly...");
            var verifyOutput = ExecuteCommand("sos help");

            if (verifyOutput.Contains("not a valid command", StringComparison.OrdinalIgnoreCase) ||
                verifyOutput.Contains("error:", StringComparison.OrdinalIgnoreCase))
            {
                var sosStatusOutput = ExecuteCommand("soshelp");
                if (sosStatusOutput.Contains("not a valid command", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "SOS plugin was loaded but commands are not available. " +
                        "This may happen when debugging Windows dumps on Linux.");
                }
            }

            _logger.LogInformation("[LLDB] SOS extension loaded and verified successfully");
            IsSosLoaded = true;

            // Step 2: Set host runtime - this must be done right after loading SOS
            var clrPath = FindMatchingRuntimePath();
            if (!string.IsNullOrEmpty(clrPath))
            {
                _logger.LogInformation("[LLDB] Setting host runtime to: {Path}", clrPath);
                var setHostRuntimeOutput = ExecuteCommandInternal($"sethostruntime \"{clrPath}\"");
                _logger.LogInformation("[LLDB] sethostruntime output: {Output}", setHostRuntimeOutput.Trim());

                // Step 3: Set CLR path for DAC/DBI files
                _logger.LogInformation("[LLDB] Setting CLR path to: {Path}", clrPath);
                var setClrPathOutput = ExecuteCommandInternal($"setclrpath \"{clrPath}\"");
                _logger.LogInformation("[LLDB] setclrpath output: {Output}", setClrPathOutput.Trim());
            }
            else
            {
                _logger.LogWarning("[LLDB] Could not find matching .NET runtime - SOS commands may fail");
            }

            // Step 4: Configure symbol server for managed symbols (PDBs)
            var cacheDir = !string.IsNullOrEmpty(_symbolCacheDirectory)
                ? _symbolCacheDirectory
                : "/tmp/sos-symbols";

            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }

            // Get configured timeout (same as dotnet-symbol)
            var timeoutMinutes = EnvironmentConfig.GetSymbolDownloadTimeoutMinutes();

            // Configure Microsoft Symbol Server with timeout
            // Note: -retries is not supported in current SOS version
            var msSymbolServerCmd = $"setsymbolserver -ms -cache \"{cacheDir}\" -timeout {timeoutMinutes}";
            _logger.LogInformation("[LLDB] Configuring Microsoft symbol server: {Command}", msSymbolServerCmd);
            ExecuteCommandInternal(msSymbolServerCmd);

            // Also add NuGet Symbol Server for library symbols with same timeout
            var nugetSymbolServerCmd = $"setsymbolserver -directory \"{cacheDir}\" -timeout {timeoutMinutes} https://nuget.smbsrc.net";
            _logger.LogInformation("[LLDB] Configuring NuGet symbol server: {Command}", nugetSymbolServerCmd);
            ExecuteCommandInternal(nugetSymbolServerCmd);

            // Step 5: Flush SOS internal cache to pick up any modules we loaded
            _logger.LogInformation("[LLDB] Flushing SOS internal cache...");
            ExecuteCommandInternal("sosflush");

            // Step 6: Check SOS status
            var statusOutput = ExecuteCommandInternal("sosstatus");
            _logger.LogInformation("[LLDB] SOS status:\n{Output}", statusOutput);

            // Log warning if there are runtime issues
            if (statusOutput.Contains("Invalid module base address") ||
                statusOutput.Contains("Failed to find runtime"))
            {
                _logger.LogWarning("[LLDB] SOS reports runtime issues - some commands may not work correctly");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LLDB] Failed to load SOS extension");
            throw new InvalidOperationException(
                $"Failed to load SOS extension. Ensure libsosplugin.so is installed. Error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Finds the .NET runtime path that matches the detected version from the dump.
    /// </summary>
    /// <returns>The full path to the runtime directory, or null if not found.</returns>
    private string? FindMatchingRuntimePath()
    {
        var runtimeBasePaths = new[]
        {
            "/usr/share/dotnet/shared/Microsoft.NETCore.App",
            "/usr/local/share/dotnet/shared/Microsoft.NETCore.App",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "shared", "Microsoft.NETCore.App"),
            "/opt/homebrew/share/dotnet/shared/Microsoft.NETCore.App"
        };

        // First priority: exact version match
        if (!string.IsNullOrEmpty(_detectedRuntimeVersion))
        {
            foreach (var basePath in runtimeBasePaths)
            {
                var exactPath = Path.Combine(basePath, _detectedRuntimeVersion);
                var dacPath = Path.Combine(exactPath, "libmscordaccore.so");
                var dacDylibPath = Path.Combine(exactPath, "libmscordaccore.dylib");

                if (File.Exists(dacPath) || File.Exists(dacDylibPath))
                {
                    _logger.LogInformation("[LLDB] Found exact runtime match: {Path}", exactPath);
                    return exactPath;
                }
            }
            _logger.LogWarning("[LLDB] Exact runtime version {Version} not found", _detectedRuntimeVersion);
        }

        // Fallback: newest available runtime
        foreach (var basePath in runtimeBasePaths)
        {
            if (!Directory.Exists(basePath))
                continue;

            try
            {
                var versionDirs = Directory.GetDirectories(basePath)
                    .OrderByDescending(d => d)
                    .ToList();

                foreach (var versionDir in versionDirs)
                {
                    var dacPath = Path.Combine(versionDir, "libmscordaccore.so");
                    var dacDylibPath = Path.Combine(versionDir, "libmscordaccore.dylib");

                    if (File.Exists(dacPath) || File.Exists(dacDylibPath))
                    {
                        _logger.LogWarning("[LLDB] Using fallback runtime (may not match dump): {Path}", versionDir);
                        return versionDir;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[LLDB] Error searching {Path}", basePath);
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the path to the .NET host binary for loading core dumps.
    /// </summary>
    /// <returns>
    /// The path to the dotnet host binary. Returns the default path even if the file
    /// doesn't exist (caller should verify existence).
    /// </returns>
    /// <remarks>
    /// <para>The dotnet host is a version-agnostic binary that can load any .NET runtime.</para>
    /// <para>When opening a core dump, LLDB uses this binary to provide context for native
    /// symbols. SOS will then automatically find the correct DAC (Data Access Component)
    /// from the installed runtimes in /usr/share/dotnet/shared/Microsoft.NETCore.App/.</para>
    /// <para>Search order:</para>
    /// <list type="number">
    /// <item>System-wide: /usr/share/dotnet/dotnet</item>
    /// <item>Local: /usr/local/share/dotnet/dotnet</item>
    /// <item>macOS Homebrew: /opt/homebrew/share/dotnet/dotnet or /usr/local/opt/dotnet/libexec/dotnet</item>
    /// <item>User-local: ~/.dotnet/dotnet</item>
    /// </list>
    /// </remarks>
    private static string GetDotnetHostPath()
    {
        // Search for the dotnet host in common locations
        var searchPaths = new[]
        {
            // System-wide installations (most common in Docker/Linux)
            "/usr/share/dotnet/dotnet",
            "/usr/local/share/dotnet/dotnet",
            // macOS Homebrew
            "/opt/homebrew/share/dotnet/dotnet",
            "/usr/local/opt/dotnet/libexec/dotnet",
            // User-local installation
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet")
        };

        // Return the first path that exists
        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        // Fallback to default (may not exist, but caller will handle)
        return DefaultDotnetHostPath;
    }

    /// <summary>
    /// Saves the detected runtime version to the existing dump metadata JSON file.
    /// </summary>
    private void SaveRuntimeVersionToMetadata()
    {
        if (string.IsNullOrEmpty(_currentDumpPath) || string.IsNullOrEmpty(_detectedRuntimeVersion))
            return;

        try
        {
            // Metadata file is at {dumpDir}/{dumpId}.json (same name as dump but .json extension)
            var dumpDir = Path.GetDirectoryName(_currentDumpPath);
            var dumpName = Path.GetFileNameWithoutExtension(_currentDumpPath);
            if (string.IsNullOrEmpty(dumpDir) || string.IsNullOrEmpty(dumpName))
                return;

            var metadataPath = Path.Combine(dumpDir, $"{dumpName}.json");

            if (File.Exists(metadataPath))
            {
                // Load existing metadata and update runtime version
                var json = File.ReadAllText(metadataPath);
                var metadata = JsonSerializer.Deserialize<Controllers.DumpMetadata>(json);

                if (metadata != null)
                {
                    metadata.RuntimeVersion = _detectedRuntimeVersion;
                    var updatedJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(metadataPath, updatedJson);
                    _logger.LogInformation("[LLDB] Saved runtime version {Version} to metadata: {Path}", _detectedRuntimeVersion, metadataPath);
                }
            }
            else
            {
                _logger.LogDebug("[LLDB] Metadata file not found, skipping runtime version save: {Path}", metadataPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LLDB] Failed to save runtime version to metadata");
        }
    }

    /// <summary>
    /// Loads the runtime version from the existing dump metadata JSON file.
    /// </summary>
    private void LoadRuntimeVersionFromMetadata()
    {
        if (string.IsNullOrEmpty(_currentDumpPath))
            return;

        try
        {
            // Metadata file is at {dumpDir}/{dumpId}.json (same name as dump but .json extension)
            var dumpDir = Path.GetDirectoryName(_currentDumpPath);
            var dumpName = Path.GetFileNameWithoutExtension(_currentDumpPath);
            if (string.IsNullOrEmpty(dumpDir) || string.IsNullOrEmpty(dumpName))
                return;

            var metadataPath = Path.Combine(dumpDir, $"{dumpName}.json");

            if (File.Exists(metadataPath))
            {
                var json = File.ReadAllText(metadataPath);
                var metadata = JsonSerializer.Deserialize<Controllers.DumpMetadata>(json);

                if (metadata != null && !string.IsNullOrEmpty(metadata.RuntimeVersion))
                {
                    _detectedRuntimeVersion = metadata.RuntimeVersion;
                    _logger.LogInformation("[LLDB] Loaded cached runtime version from metadata: {Version}", _detectedRuntimeVersion);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LLDB] Failed to load runtime version from metadata");
        }
    }

    /// <summary>
    /// Downloads symbols, DAC, and SOS for a dump file using dotnet-symbol.
    /// Uses smart caching: checks if all cached symbol files exist before running.
    /// </summary>
    /// <param name="dumpFilePath">The path to the dump file.</param>
    /// <param name="outputDirectory">The directory to store downloaded symbols.</param>
    /// <returns>True if symbols are available (cached or downloaded), false otherwise.</returns>
    /// <remarks>
    /// <para>Smart caching behavior:</para>
    /// <list type="number">
    /// <item>Load cached symbol file list from dump metadata</item>
    /// <item>If all cached files exist, skip dotnet-symbol execution</item>
    /// <item>If any file is missing (or no cache), run dotnet-symbol</item>
    /// <item>After successful execution, save the new file list to metadata</item>
    /// </list>
    /// </remarks>
    private bool DownloadSymbols(string dumpFilePath, string outputDirectory)
    {
        // Check if dotnet-symbol is available
        var dotnetSymbolPath = FindDotnetSymbolTool();
        if (dotnetSymbolPath == null)
        {
            _logger.LogWarning("[dotnet-symbol] Tool not found, skipping symbol download");
            return false;
        }

        try
        {
            // Ensure output directory exists
            Directory.CreateDirectory(outputDirectory);

            // Check if we can use cached symbols
            var cachedFiles = LoadSymbolFilesFromMetadata();
            _logger.LogDebug("[dotnet-symbol] Cached files from metadata: {Count}", cachedFiles?.Count ?? 0);

            if (cachedFiles != null && cachedFiles.Count > 0)
            {
                _logger.LogInformation("[dotnet-symbol] Found {Count} cached files in metadata, checking if they exist in {Dir}",
                    cachedFiles.Count, outputDirectory);

                if (AllCachedFilesExist(cachedFiles, outputDirectory))
                {
                    _logger.LogInformation("[dotnet-symbol] All {Count} cached symbol files exist, skipping download", cachedFiles.Count);
                    LogDownloadedFiles(outputDirectory);
                    return true;
                }

                // Find which files are missing
                var missingFiles = cachedFiles.Where(f => !File.Exists(Path.Combine(outputDirectory, f))).ToList();
                _logger.LogInformation("[dotnet-symbol] {Missing} of {Total} cached symbol files missing, re-downloading",
                    missingFiles.Count, cachedFiles.Count);
            }
            else
            {
                _logger.LogInformation("[dotnet-symbol] No cached symbol list found (list is null or empty), downloading symbols");
            }

            // Run dotnet-symbol with both Microsoft and NuGet symbol servers
            // --server-path REPLACES the default, so we need to specify Microsoft explicitly first
            var timeoutMinutes = EnvironmentConfig.GetSymbolDownloadTimeoutMinutes();
            var arguments = $"\"{dumpFilePath}\" -o \"{outputDirectory}\" --timeout {timeoutMinutes} " +
                           $"--server-path {SymbolManager.MicrosoftSymbolServer} " +
                           $"--server-path {SymbolManager.NuGetSymbolServer}";
            _logger.LogInformation("[dotnet-symbol] Running: {Tool} {Arguments}", dotnetSymbolPath, arguments);

            var startInfo = new ProcessStartInfo
            {
                FileName = dotnetSymbolPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogInformation("[dotnet-symbol] {Output}", e.Data);
                    TryParseRuntimeVersion(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogWarning("[dotnet-symbol] {Error}", e.Data);
                    TryParseRuntimeVersion(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Use configured timeout (already retrieved above for arguments)
            var timeoutMs = timeoutMinutes * 60 * 1000;

            var completed = process.WaitForExit(timeoutMs);

            if (!completed)
            {
                _logger.LogWarning("[dotnet-symbol] Command timed out after {TimeoutMinutes} minutes", timeoutMinutes);
                try
                {
                    process.Kill();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[dotnet-symbol] Exception while killing timed-out process (may have already exited)");
                }
                finally
                {
                    process.Dispose();
                }
                return false;
            }

            var success = process.ExitCode == 0;
            _logger.LogInformation("[dotnet-symbol] Completed with exit code {ExitCode}", process.ExitCode);

            // Run a second pass to download PDBs (symbol files) for source link resolution
            // This is separate from the module download to ensure we get both DLLs and PDBs
            if (success)
            {
                DownloadPdbSymbols(dotnetSymbolPath, dumpFilePath, outputDirectory, timeoutMinutes);
            }

            // List what was downloaded for debugging
            LogDownloadedFiles(outputDirectory);

            // Save the new file list to metadata (only if dotnet-symbol was executed)
            if (success)
            {
                SaveSymbolFilesToMetadata(outputDirectory);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[dotnet-symbol] Exception during symbol download");
            return false;
        }
    }

    /// <summary>
    /// Downloads PDB symbol files using dotnet-symbol with the --symbols flag.
    /// This is a separate pass from the module download to ensure we get both DLLs and PDBs.
    /// PDBs are required for source link resolution in stack traces.
    /// </summary>
    /// <param name="dotnetSymbolPath">Path to the dotnet-symbol tool.</param>
    /// <param name="dumpFilePath">Path to the dump file.</param>
    /// <param name="outputDirectory">Directory to download symbols to.</param>
    /// <param name="timeoutMinutes">Timeout in minutes.</param>
    private void DownloadPdbSymbols(string dotnetSymbolPath, string dumpFilePath, string outputDirectory, int timeoutMinutes)
    {
        try
        {
            // Find all DLL files in the output directory
            var dllFiles = Directory.GetFiles(outputDirectory, "*.dll", SearchOption.AllDirectories);
            if (dllFiles.Length == 0)
            {
                _logger.LogInformation("[dotnet-symbol] No DLL files found in {Dir}, skipping PDB download", outputDirectory);
                return;
            }

            _logger.LogInformation("[dotnet-symbol] Starting PDB symbol download for {Count} DLLs...", dllFiles.Length);

            // Run dotnet-symbol with --symbols flag on the downloaded DLLs
            // Pass each DLL file explicitly to ensure dotnet-symbol finds them
            var dllList = string.Join("\" \"", dllFiles);
            var arguments = $"--symbols -o \"{outputDirectory}\" --timeout {timeoutMinutes} " +
                           $"--server-path {SymbolManager.MicrosoftSymbolServer} " +
                           $"--server-path {SymbolManager.NuGetSymbolServer} " +
                           $"\"{dllList}\"";
            _logger.LogInformation("[dotnet-symbol] Running: {Tool} {Arguments}", dotnetSymbolPath, arguments);

            var startInfo = new ProcessStartInfo
            {
                FileName = dotnetSymbolPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogInformation("[dotnet-symbol-pdb] {Output}", e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogWarning("[dotnet-symbol-pdb] {Error}", e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeoutMs = timeoutMinutes * 60 * 1000;
            var completed = process.WaitForExit(timeoutMs);

            if (!completed)
            {
                _logger.LogWarning("[dotnet-symbol-pdb] PDB download timed out after {TimeoutMinutes} minutes", timeoutMinutes);
                try
                {
                    process.Kill();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[dotnet-symbol-pdb] Exception while killing timed-out process");
                }
                return;
            }

            _logger.LogInformation("[dotnet-symbol-pdb] PDB download completed with exit code {ExitCode}", process.ExitCode);
        }
        catch (Exception ex)
        {
            // PDB download failure is not critical - SOS can still download on-demand via setsymbolserver
            _logger.LogWarning(ex, "[dotnet-symbol-pdb] Exception during PDB symbol download (continuing without pre-downloaded PDBs)");
        }
    }

    /// <summary>
    /// Gets the platform information detected from the dump via dotnet-symbol --verifycore.
    /// </summary>
    public VerifyCoreResult? VerifiedCorePlatform { get; private set; }

    /// <summary>
    /// Runs dotnet-symbol --verifycore to detect platform information from the dump.
    /// This is more reliable than LLDB's image list for standalone apps.
    /// </summary>
    /// <param name="dumpFilePath">The path to the dump file.</param>
    /// <returns>Platform detection result, or null if detection failed.</returns>
    public VerifyCoreResult? VerifyCore(string dumpFilePath)
    {
        var dotnetSymbolPath = FindDotnetSymbolTool();
        if (dotnetSymbolPath == null)
        {
            _logger.LogWarning("[dotnet-symbol-verifycore] Tool not found, skipping platform detection");
            return null;
        }

        try
        {
            var arguments = $"--verifycore \"{dumpFilePath}\"";
            _logger.LogInformation("[dotnet-symbol-verifycore] Running: {Tool} {Arguments}", dotnetSymbolPath, arguments);

            var startInfo = new ProcessStartInfo
            {
                FileName = dotnetSymbolPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var outputLines = new List<string>();

            using var process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    outputLines.Add(e.Data);
                    _logger.LogDebug("[dotnet-symbol-verifycore] {Output}", e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogDebug("[dotnet-symbol-verifycore] stderr: {Error}", e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Use a shorter timeout for verifycore (30 seconds should be enough)
            var completed = process.WaitForExit(30000);

            if (!completed)
            {
                _logger.LogWarning("[dotnet-symbol-verifycore] Command timed out after 30 seconds");
                try { process.Kill(); } catch { /* ignore */ }
                return null;
            }

            _logger.LogInformation("[dotnet-symbol-verifycore] Completed with exit code {ExitCode}, parsed {Count} modules",
                process.ExitCode, outputLines.Count);

            // Parse the output for platform detection
            var result = ParseVerifyCoreOutput(outputLines);
            VerifiedCorePlatform = result;
            
            if (result != null)
            {
                _logger.LogInformation("[dotnet-symbol-verifycore] Detected: IsAlpine={IsAlpine}, Architecture={Arch}",
                    result.IsAlpine, result.Architecture ?? "unknown");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[dotnet-symbol-verifycore] Exception during platform detection");
            return null;
        }
    }

    /// <summary>
    /// Parses the output of dotnet-symbol --verifycore to detect platform information.
    /// </summary>
    private VerifyCoreResult ParseVerifyCoreOutput(List<string> outputLines)
    {
        var result = new VerifyCoreResult();
        var modulePaths = new List<string>();
        var moduleAddresses = new Dictionary<string, ulong>();

        foreach (var line in outputLines)
        {
            // Skip empty lines, invalid images, and lines without addresses
            if (string.IsNullOrWhiteSpace(line) || line.Contains("invalid image"))
                continue;

            // verifycore output format:
            // First line is the dump file path (no address) - skip it
            // Module lines: "00007F3156DC7000 /lib/ld-musl-x86_64.so.1"
            
            // Only process lines that start with a hex address (module lines)
            // Addresses are 16 hex chars for 64-bit, 8 for 32-bit
            var trimmedLine = line.TrimStart();
            if (trimmedLine.Length < 8) continue;
            
            // Check if line starts with hex address (0-9, A-F, a-f)
            var firstWord = trimmedLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
            if (firstWord.Length < 8 || !firstWord.All(c => char.IsAsciiHexDigit(c)))
            {
                _logger.LogDebug("[dotnet-symbol-verifycore] Skipping non-module line: {Line}", 
                    line.Length > 80 ? line[..80] + "..." : line);
                continue;
            }

            // Extract the address and path from lines like "00007F3156DC7000 /lib/ld-musl-x86_64.so.1"
            var parts = trimmedLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1].StartsWith("/"))
            {
                var path = parts[1];
                modulePaths.Add(path);
                
                // Parse the address and store it
                if (ulong.TryParse(firstWord, System.Globalization.NumberStyles.HexNumber, null, out var address))
                {
                    moduleAddresses[path] = address;
                    _logger.LogDebug("[dotnet-symbol-verifycore] Module: {Path} @ 0x{Address:X16}", path, address);
                }
            }
        }

        result.ModulePaths = modulePaths;
        result.ModuleAddresses = moduleAddresses;

        // The first module is typically the main executable
        if (modulePaths.Count > 0)
        {
            result.MainExecutablePath = modulePaths[0];
            // Use GetFileName, not GetFileNameWithoutExtension!
            // Linux executables don't have extensions - "Samples.BuggyBits" is the full name
            result.MainExecutableName = Path.GetFileName(modulePaths[0]);
            _logger.LogInformation("[dotnet-symbol-verifycore] Main executable: {Name} ({Path})",
                result.MainExecutableName, result.MainExecutablePath);
        }

        // Detect Alpine/musl
        foreach (var path in modulePaths)
        {
            var pathLower = path.ToLowerInvariant();

            // Check for musl indicators
            if (pathLower.Contains("ld-musl") || pathLower.Contains("/musl-") || pathLower.Contains("linux-musl-"))
            {
                result.IsAlpine = true;
                _logger.LogDebug("[dotnet-symbol-verifycore] Detected musl from: {Path}", path);
            }

            // Check for architecture indicators
            if (result.Architecture == null)
            {
                if (pathLower.Contains("x86_64") || pathLower.Contains("-x64/") || pathLower.Contains("/x64/") || 
                    pathLower.Contains("amd64") || pathLower.Contains("musl-x64"))
                {
                    result.Architecture = "x64";
                    _logger.LogDebug("[dotnet-symbol-verifycore] Detected x64 from: {Path}", path);
                }
                else if (pathLower.Contains("aarch64") || pathLower.Contains("-arm64/") || pathLower.Contains("/arm64/") ||
                         pathLower.Contains("musl-arm64"))
                {
                    result.Architecture = "arm64";
                    _logger.LogDebug("[dotnet-symbol-verifycore] Detected arm64 from: {Path}", path);
                }
                else if (pathLower.Contains("i386") || pathLower.Contains("i686") || pathLower.Contains("-x86/") || 
                         pathLower.Contains("/x86/"))
                {
                    result.Architecture = "x86";
                    _logger.LogDebug("[dotnet-symbol-verifycore] Detected x86 from: {Path}", path);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Loads all .dbg debug symbol files from the symbol cache directory.
    /// </summary>
    private void LoadDebugSymbolFiles()
    {
        if (string.IsNullOrEmpty(_symbolCacheDirectory) || !Directory.Exists(_symbolCacheDirectory))
            return;

        try
        {
            var debugFiles = Directory.GetFiles(_symbolCacheDirectory, "*.dbg", SearchOption.AllDirectories);
            if (debugFiles.Length > 0)
            {
                _logger.LogInformation("[LLDB] Found {Count} .dbg symbol files, loading explicitly...", debugFiles.Length);
                foreach (var dbgFile in debugFiles)
                {
                    try
                    {
                        var result = ExecuteCommandInternal($"target symbols add \"{dbgFile}\"");
                        if (!result.Contains("error", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogDebug("[LLDB] Loaded symbols: {File}", Path.GetFileName(dbgFile));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[LLDB] Failed to load symbol file: {File}", dbgFile);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LLDB] Failed to enumerate debug symbol files");
        }
    }

    /// <summary>
    /// Checks if a file is a valid ELF that could potentially be used as an executable.
    /// Some .dbg files created with objcopy --only-keep-debug retain enough ELF structure.
    /// </summary>
    /// <param name="filePath">Path to the file to check.</param>
    /// <returns>True if file appears to be a usable ELF, false otherwise.</returns>
    private bool IsUsableElf(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 64) return false; // Too small for ELF header

            var header = new byte[64];
            if (fs.Read(header, 0, 64) < 64) return false;

            // Check ELF magic: 0x7F 'E' 'L' 'F'
            if (header[0] != 0x7F || header[1] != 'E' || header[2] != 'L' || header[3] != 'F')
            {
                _logger.LogDebug("[LLDB] {File} is not an ELF file", Path.GetFileName(filePath));
                return false;
            }

            // Check ELF class (32-bit or 64-bit)
            var is64Bit = header[4] == 2;
            
            // Get e_phnum (program header count) - indicates if it can be loaded
            // For 64-bit: offset 56 (2 bytes), for 32-bit: offset 44 (2 bytes)
            int phNumOffset = is64Bit ? 56 : 44;
            var phNum = BitConverter.ToUInt16(header, phNumOffset);

            // Get e_type (object file type)
            // 2 = ET_EXEC (executable), 3 = ET_DYN (shared object/PIE)
            var eType = BitConverter.ToUInt16(header, 16);

            _logger.LogDebug("[LLDB] ELF check for {File}: type={Type}, phnum={PhNum}, is64bit={Is64}",
                Path.GetFileName(filePath), eType, phNum, is64Bit);

            // A usable ELF should have program headers and be executable or shared object
            var isUsable = phNum > 0 && (eType == 2 || eType == 3);
            
            if (isUsable)
            {
                _logger.LogInformation("[LLDB] {File} appears to be a usable ELF (type={Type}, {PhNum} program headers)",
                    Path.GetFileName(filePath), eType == 2 ? "executable" : "shared object", phNum);
            }

            return isUsable;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LLDB] Error checking ELF structure of {File}", filePath);
            return false;
        }
    }

    /// <summary>
    /// Tries to find a usable ELF file (.dbg or .debug) for a standalone app.
    /// If found and valid, returns the path so it can be used as the executable.
    /// </summary>
    /// <param name="executableName">The name of the executable (without extension).</param>
    /// <returns>Path to usable ELF file, or null if not found.</returns>
    public string? FindUsableElfForStandaloneApp(string executableName)
    {
        _logger.LogInformation("[LLDB] Searching for usable ELF for standalone app: {Name}", executableName);

        // Search patterns - .dbg files might be usable ELFs
        var searchPatterns = new[]
        {
            $"{executableName}.dbg",
            $"{executableName}.debug",
            $"{executableName}",  // The actual executable if it exists
            $"lib{executableName}.dbg",
            $"lib{executableName}.debug"
        };

        var searchPaths = new List<string>();

        // Add symbol cache directory
        if (!string.IsNullOrEmpty(_symbolCacheDirectory) && Directory.Exists(_symbolCacheDirectory))
        {
            searchPaths.Add(_symbolCacheDirectory);
        }

        // Add dump directory
        if (!string.IsNullOrEmpty(_currentDumpPath))
        {
            var dumpDir = Path.GetDirectoryName(_currentDumpPath);
            if (!string.IsNullOrEmpty(dumpDir) && Directory.Exists(dumpDir))
            {
                searchPaths.Add(dumpDir);
            }
        }

        foreach (var searchPath in searchPaths)
        {
            foreach (var pattern in searchPatterns)
            {
                try
                {
                    var matches = Directory.GetFiles(searchPath, pattern, SearchOption.AllDirectories);
                    foreach (var file in matches)
                    {
                        if (IsUsableElf(file))
                        {
                            _logger.LogInformation("[LLDB] Found usable ELF for standalone app: {File}", file);
                            return file;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[LLDB] Error searching for {Pattern} in {Path}", pattern, searchPath);
                }
            }
        }

        _logger.LogWarning("[LLDB] No usable ELF found for standalone app: {Name}", executableName);
        return null;
    }

    /// <summary>
    /// Tries to find and load debug symbols (.dbg) for the main executable.
    /// This is useful for standalone apps where we don't have the binary but may have debug symbols.
    /// </summary>
    /// <param name="executableName">The name of the executable (without extension).</param>
    private void TryLoadMainExecutableDebugSymbols(string executableName)
    {
        _logger.LogInformation("[LLDB] Standalone app detected: {Name} - searching for debug symbols...", executableName);

        // Common patterns for debug symbol files
        var searchPatterns = new[]
        {
            $"{executableName}.dbg",
            $"{executableName}.debug",
            $"lib{executableName}.dbg",
            $"lib{executableName}.debug"
        };

        var searchPaths = new List<string>();

        // Add symbol cache directory
        if (!string.IsNullOrEmpty(_symbolCacheDirectory) && Directory.Exists(_symbolCacheDirectory))
        {
            searchPaths.Add(_symbolCacheDirectory);
        }

        // Add dump directory
        if (!string.IsNullOrEmpty(_currentDumpPath))
        {
            var dumpDir = Path.GetDirectoryName(_currentDumpPath);
            if (!string.IsNullOrEmpty(dumpDir) && Directory.Exists(dumpDir))
            {
                searchPaths.Add(dumpDir);
            }
        }

        foreach (var searchPath in searchPaths)
        {
            foreach (var pattern in searchPatterns)
            {
                try
                {
                    var matches = Directory.GetFiles(searchPath, pattern, SearchOption.AllDirectories);
                    foreach (var dbgFile in matches)
                    {
                        try
                        {
                            _logger.LogInformation("[LLDB] Found debug symbols for main executable: {File}", dbgFile);
                            var result = ExecuteCommandInternal($"target symbols add \"{dbgFile}\"");
                            if (!result.Contains("error", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogInformation("[LLDB] Successfully loaded main executable debug symbols: {File}", Path.GetFileName(dbgFile));
                                return; // Found and loaded, we're done
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "[LLDB] Failed to load debug file: {File}", dbgFile);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[LLDB] Error searching for {Pattern} in {Path}", pattern, searchPath);
                }
            }
        }

        _logger.LogWarning("[LLDB] No debug symbols found for standalone app: {Name}. Native debugging may be limited.", executableName);
        _logger.LogInformation("[LLDB] Tip: Upload the debug file ({Name}.dbg) using 'symbols upload' or 'dumps binary' for full debugging support.", executableName);
    }

    /// <summary>
    /// Loads modules from verifycore output at their correct memory addresses.
    /// This is useful for standalone apps where LLDB doesn't automatically load modules.
    /// </summary>
    /// <param name="moduleNames">Optional list of module names to load. If null, loads all modules with .so files available.</param>
    /// <returns>Dictionary of module name to success status.</returns>
    public Dictionary<string, bool> LoadModulesFromVerifyCore(IEnumerable<string>? moduleNames = null)
    {
        var results = new Dictionary<string, bool>();

        if (VerifiedCorePlatform?.ModuleAddresses == null || VerifiedCorePlatform.ModuleAddresses.Count == 0)
        {
            _logger.LogWarning("[LLDB] No module addresses available from verifycore");
            return results;
        }

        if (string.IsNullOrEmpty(_symbolCacheDirectory) || !Directory.Exists(_symbolCacheDirectory))
        {
            _logger.LogWarning("[LLDB] Symbol cache directory not available");
            return results;
        }

        // Get available .so files in the symbol cache
        var availableModules = Directory.GetFiles(_symbolCacheDirectory, "*.so", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetFileName(f), f => f, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("[LLDB] Found {Count} .so files in symbol cache", availableModules.Count);

        // Determine which modules to load
        var modulesToLoad = new List<(string Name, string Path, ulong Address)>();

        foreach (var (modulePath, address) in VerifiedCorePlatform.ModuleAddresses)
        {
            var moduleName = Path.GetFileName(modulePath);
            
            // Filter by requested names if provided
            if (moduleNames != null && !moduleNames.Any(m => 
                moduleName.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Check if we have this module in our symbol cache
            if (availableModules.TryGetValue(moduleName, out var localPath))
            {
                modulesToLoad.Add((moduleName, localPath, address));
            }
        }

        _logger.LogInformation("[LLDB] Loading {Count} modules from verifycore", modulesToLoad.Count);

        foreach (var (name, localPath, address) in modulesToLoad)
        {
            try
            {
                _logger.LogInformation("[LLDB] Loading module {Name} at 0x{Address:X16}", name, address);

                // Step 1: Add the module to the target
                var addResult = ExecuteCommandInternal($"target modules add \"{localPath}\"");
                _logger.LogDebug("[LLDB] target modules add result: {Result}", addResult);

                // Step 2: Load the module at the correct address using --slide
                // The slide value is the offset from the original link address (usually 0)
                var loadResult = ExecuteCommandInternal($"target modules load --file \"{name}\" --slide 0x{address:X}");
                _logger.LogDebug("[LLDB] target modules load result: {Result}", loadResult);

                if (loadResult.Contains("error", StringComparison.OrdinalIgnoreCase))
                {
                    // Fallback: try loading individual sections
                    _logger.LogDebug("[LLDB] Slide failed, trying section load for {Name}", name);
                    
                    // Get section info
                    var sectionsOutput = ExecuteCommandInternal($"image dump sections \"{name}\"");
                    
                    // Try loading .text section at the address
                    var textResult = ExecuteCommandInternal($"target modules load --file \"{name}\" .text 0x{address:X}");
                    _logger.LogDebug("[LLDB] Section load result: {Result}", textResult);

                    results[name] = !textResult.Contains("error", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    results[name] = true;
                }

                // Also try to load associated .dbg file if it exists
                var dbgPath = localPath + ".dbg";
                if (File.Exists(dbgPath))
                {
                    var symbolResult = ExecuteCommandInternal($"target symbols add \"{dbgPath}\"");
                    _logger.LogDebug("[LLDB] Symbol add result: {Result}", symbolResult);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LLDB] Failed to load module {Name}", name);
                results[name] = false;
            }
        }

        return results;
    }

    /// <summary>
    /// Loads the cached symbol file list from the dump metadata.
    /// </summary>
    /// <returns>List of relative file paths, or null if not found.</returns>
    private List<string>? LoadSymbolFilesFromMetadata()
    {
        _logger.LogDebug("[dotnet-symbol] LoadSymbolFilesFromMetadata: _currentDumpPath = {Path}", _currentDumpPath ?? "(null)");

        if (string.IsNullOrEmpty(_currentDumpPath))
        {
            _logger.LogDebug("[dotnet-symbol] LoadSymbolFilesFromMetadata: _currentDumpPath is null/empty, returning null");
            return null;
        }

        try
        {
            var dumpDir = Path.GetDirectoryName(_currentDumpPath);
            var dumpName = Path.GetFileNameWithoutExtension(_currentDumpPath);
            _logger.LogDebug("[dotnet-symbol] LoadSymbolFilesFromMetadata: dumpDir={Dir}, dumpName={Name}", dumpDir, dumpName);

            if (string.IsNullOrEmpty(dumpDir) || string.IsNullOrEmpty(dumpName))
            {
                _logger.LogDebug("[dotnet-symbol] LoadSymbolFilesFromMetadata: dumpDir or dumpName is null/empty, returning null");
                return null;
            }

            var metadataPath = Path.Combine(dumpDir, $"{dumpName}.json");
            _logger.LogDebug("[dotnet-symbol] LoadSymbolFilesFromMetadata: looking for metadata at {Path}", metadataPath);

            if (File.Exists(metadataPath))
            {
                var json = File.ReadAllText(metadataPath);
                var metadata = JsonSerializer.Deserialize<Controllers.DumpMetadata>(json);
                var symbolFiles = metadata?.SymbolFiles;
                _logger.LogDebug("[dotnet-symbol] LoadSymbolFilesFromMetadata: found metadata, SymbolFiles count = {Count}", symbolFiles?.Count ?? -1);
                return symbolFiles;
            }
            else
            {
                _logger.LogDebug("[dotnet-symbol] LoadSymbolFilesFromMetadata: metadata file not found at {Path}", metadataPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[dotnet-symbol] Failed to load symbol file list from metadata");
        }

        return null;
    }

    /// <summary>
    /// Checks if all cached symbol files exist in the output directory.
    /// </summary>
    /// <param name="cachedFiles">List of relative file paths.</param>
    /// <param name="outputDirectory">The symbol output directory.</param>
    /// <returns>True if all files exist, false if any are missing.</returns>
    private bool AllCachedFilesExist(List<string> cachedFiles, string outputDirectory)
    {
        _logger.LogDebug("[dotnet-symbol] AllCachedFilesExist: checking {Count} files in {Dir}", cachedFiles.Count, outputDirectory);
        _logger.LogDebug("[dotnet-symbol] AllCachedFilesExist: directory exists = {Exists}", Directory.Exists(outputDirectory));

        var checkedCount = 0;
        foreach (var relativePath in cachedFiles)
        {
            var fullPath = Path.Combine(outputDirectory, relativePath);
            var exists = File.Exists(fullPath);
            checkedCount++;

            if (!exists)
            {
                _logger.LogInformation("[dotnet-symbol] Missing cached file (#{Num}): {Path} -> {FullPath}",
                    checkedCount, relativePath, fullPath);
                return false;
            }
        }

        _logger.LogDebug("[dotnet-symbol] AllCachedFilesExist: all {Count} files exist", cachedFiles.Count);
        return true;
    }

    /// <summary>
    /// Saves the current symbol file list to the dump metadata.
    /// </summary>
    /// <param name="outputDirectory">The symbol output directory.</param>
    private void SaveSymbolFilesToMetadata(string outputDirectory)
    {
        if (string.IsNullOrEmpty(_currentDumpPath) || !Directory.Exists(outputDirectory))
            return;

        try
        {
            var dumpDir = Path.GetDirectoryName(_currentDumpPath);
            var dumpName = Path.GetFileNameWithoutExtension(_currentDumpPath);
            if (string.IsNullOrEmpty(dumpDir) || string.IsNullOrEmpty(dumpName))
                return;

            var metadataPath = Path.Combine(dumpDir, $"{dumpName}.json");

            if (!File.Exists(metadataPath))
            {
                _logger.LogDebug("[dotnet-symbol] Metadata file not found, skipping symbol list save: {Path}", metadataPath);
                return;
            }

            // Get all files in the output directory (relative paths)
            var allFiles = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories);
            var relativePaths = allFiles
                .Select(f => Path.GetRelativePath(outputDirectory, f))
                .OrderBy(f => f)
                .ToList();

            // Load existing metadata and update symbol files list
            var json = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize<Controllers.DumpMetadata>(json);

            if (metadata != null)
            {
                metadata.SymbolFiles = relativePaths;
                var updatedJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(metadataPath, updatedJson);
                _logger.LogInformation("[dotnet-symbol] Saved {Count} symbol files to metadata: {Path}", relativePaths.Count, metadataPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[dotnet-symbol] Failed to save symbol file list to metadata");
        }
    }

    /// <summary>
    /// Logs information about downloaded files for debugging.
    /// </summary>
    private void LogDownloadedFiles(string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
            return;

        try
        {
            var downloadedFiles = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories);
            _logger.LogInformation("[dotnet-symbol] Total files downloaded: {Count} to {Dir}", downloadedFiles.Length, outputDirectory);

            var coreclrFiles = downloadedFiles.Where(f => f.Contains("libcoreclr", StringComparison.OrdinalIgnoreCase)).ToList();
            var sosFiles = downloadedFiles.Where(f => f.Contains("sos", StringComparison.OrdinalIgnoreCase)).ToList();
            var dacFiles = downloadedFiles.Where(f => f.Contains("dac", StringComparison.OrdinalIgnoreCase) || f.Contains("mscordac", StringComparison.OrdinalIgnoreCase)).ToList();
            var soFiles = downloadedFiles.Where(f => f.EndsWith(".so", StringComparison.OrdinalIgnoreCase)).ToList();

            _logger.LogInformation("[dotnet-symbol] Key files found:");
            _logger.LogInformation("[dotnet-symbol]   libcoreclr files: {Count}", coreclrFiles.Count);
            foreach (var f in coreclrFiles) _logger.LogInformation("[dotnet-symbol]     - {File}", Path.GetFileName(f));

            _logger.LogInformation("[dotnet-symbol]   SOS files: {Count}", sosFiles.Count);
            foreach (var f in sosFiles) _logger.LogInformation("[dotnet-symbol]     - {File}", Path.GetFileName(f));

            _logger.LogInformation("[dotnet-symbol]   DAC files: {Count}", dacFiles.Count);
            foreach (var f in dacFiles) _logger.LogInformation("[dotnet-symbol]     - {File}", Path.GetFileName(f));

            _logger.LogInformation("[dotnet-symbol]   All .so files: {Count}", soFiles.Count);
            foreach (var f in soFiles) _logger.LogInformation("[dotnet-symbol]     - {File}", Path.GetFileName(f));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[dotnet-symbol] Error listing downloaded files");
        }
    }

    /// <summary>
    /// Finds the dotnet-symbol tool in common locations.
    /// </summary>
    /// <returns>The path to dotnet-symbol, or null if not found.</returns>
    private string? FindDotnetSymbolTool()
    {
        var searchPaths = new[]
        {
            // Dockerfile copies tools to /tools
            "/tools/dotnet-symbol",
            // Global tool installation
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "dotnet-symbol"),
            // Root user in Docker
            "/root/.dotnet/tools/dotnet-symbol",
            // System PATH (just the command name)
            "dotnet-symbol"
        };

        _logger.LogDebug("[dotnet-symbol] Searching for tool in: {Paths}", string.Join(", ", searchPaths));

        foreach (var path in searchPaths)
        {
            // If it's just the command name, check if it's in PATH
            if (path == "dotnet-symbol")
            {
                try
                {
                    var whichProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "which",
                            Arguments = "dotnet-symbol",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        }
                    };
                    whichProcess.Start();
                    var result = whichProcess.StandardOutput.ReadToEnd().Trim();
                    whichProcess.WaitForExit();
                    if (whichProcess.ExitCode == 0 && !string.IsNullOrEmpty(result))
                    {
                        _logger.LogDebug("[dotnet-symbol] Found via 'which' at: {Path}", result);
                        return result;
                    }
                }
                catch
                {
                    // which command failed, continue searching
                }
            }
            else if (File.Exists(path))
            {
                _logger.LogDebug("[dotnet-symbol] Found at: {Path}", path);
                return path;
            }
        }

        _logger.LogDebug("[dotnet-symbol] Tool not found in any location");
        return null;
    }

    /// <summary>
    /// Tries to parse the .NET runtime version from dotnet-symbol output.
    /// Looks for patterns like "Microsoft.NETCore.App/9.0.10" or version directories.
    /// </summary>
    /// <param name="outputLine">A line of output from dotnet-symbol.</param>
    private void TryParseRuntimeVersion(string outputLine)
    {
        // Skip if we already have a detected version
        if (!string.IsNullOrEmpty(_detectedRuntimeVersion))
            return;

        // Look for patterns like:
        // - "Microsoft.NETCore.App/9.0.10"
        // - "/shared/Microsoft.NETCore.App/9.0.10/"
        // - "libcoreclr.so" paths with version directories

        // Pattern: Microsoft.NETCore.App/X.Y.Z
        var netCoreAppIndex = outputLine.IndexOf("Microsoft.NETCore.App/", StringComparison.OrdinalIgnoreCase);
        if (netCoreAppIndex >= 0)
        {
            var versionStart = netCoreAppIndex + "Microsoft.NETCore.App/".Length;
            var versionEnd = outputLine.IndexOfAny(['/', ' ', '"', '\\'], versionStart);
            if (versionEnd < 0) versionEnd = outputLine.Length;

            var version = outputLine[versionStart..versionEnd].Trim();

            // Validate it looks like a version (e.g., "9.0.10", "8.0.0")
            if (IsValidVersionString(version))
            {
                _detectedRuntimeVersion = version;
                _logger.LogInformation("[dotnet-symbol] Detected .NET runtime version: {Version}", version);
            }
        }
    }

    /// <summary>
    /// Validates if a string looks like a .NET version (e.g., "9.0.10", "8.0.0").
    /// </summary>
    private static bool IsValidVersionString(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        var parts = version.Split('.');
        if (parts.Length < 2 || parts.Length > 4)
            return false;

        return parts.All(p => int.TryParse(p, out _));
    }

    /// <summary>
    /// Detects if the currently open dump is a .NET dump by checking:
    /// 1. Detected runtime version from dotnet-symbol output
    /// 2. Presence of .NET runtime modules in the module list (libcoreclr, libhostpolicy, etc.)
    /// </summary>
    /// <param name="moduleList">The output from 'image list' command.</param>
    /// <returns>True if .NET runtime is detected; otherwise, false.</returns>
    private bool DetectDotNetDump(string moduleList)
    {
        // Method 1: Check if we detected a runtime version from dotnet-symbol
        if (!string.IsNullOrEmpty(_detectedRuntimeVersion))
        {
            _logger.LogInformation("[LLDB] .NET dump detected via runtime version: {Version}", _detectedRuntimeVersion);
            return true;
        }

        // Method 2: Check loaded modules for .NET runtime libraries
        // These modules indicate a .NET Core/.NET 5+ process
        var dotNetModules = new[]
        {
            "libcoreclr.so",         // Linux CoreCLR runtime
            "libcoreclr.dylib",      // macOS CoreCLR runtime
            "libhostpolicy.so",      // Linux host policy
            "libhostpolicy.dylib",   // macOS host policy
            "libhostfxr.so",         // Linux host framework resolver
            "libhostfxr.dylib",      // macOS host framework resolver
            "libclrjit.so",          // Linux JIT compiler
            "libclrjit.dylib",       // macOS JIT compiler
            "Microsoft.NETCore.App", // Runtime directory indicator
        };

        foreach (var module in dotNetModules)
        {
            if (moduleList.Contains(module, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[LLDB] .NET dump detected via module: {Module}", module);
                return true;
            }
        }

        _logger.LogDebug("[LLDB] No .NET runtime indicators found in module list");
        return false;
    }

    /// <summary>
    /// Searches for the SOS plugin in common installation locations.
    /// </summary>
    /// <returns>The full path to the SOS plugin, or null if not found.</returns>
    private string? FindSosPlugin()
    {
        _logger.LogInformation("[SOS] Searching for SOS plugin...");

        // Check for environment variable override first (via centralized config)
        var envPath = EnvironmentConfig.GetSosPluginPath();
        if (!string.IsNullOrEmpty(envPath))
        {
            _logger.LogInformation("[SOS] Environment variable SOS_PLUGIN_PATH set to: {Path}", envPath);
            if (File.Exists(envPath))
            {
                _logger.LogInformation("[SOS] Found via environment variable: {Path}", envPath);
                return envPath;
            }
            _logger.LogWarning("[SOS] Environment path does not exist: {Path}", envPath);
        }

        // FIRST: Check the symbol cache directory (where dotnet-symbol downloads SOS)
        // This is the most likely location when running in Docker with downloaded symbols
        _logger.LogInformation("[SOS] Symbol cache directory: {SymbolCache}", _symbolCacheDirectory ?? "(null)");
        if (!string.IsNullOrEmpty(_symbolCacheDirectory) && Directory.Exists(_symbolCacheDirectory))
        {
            _logger.LogInformation("[SOS] Searching recursively in symbol cache...");

            try
            {
                // Search recursively in the symbol cache - dotnet-symbol may put it in subdirectories
                var sosFiles = Directory.GetFiles(_symbolCacheDirectory, "libsosplugin.so", SearchOption.AllDirectories);
                _logger.LogInformation("[SOS] Found {Count} libsosplugin.so files in symbol cache", sosFiles.Length);
                if (sosFiles.Length > 0)
                {
                    _logger.LogInformation("[SOS] Using: {Path}", sosFiles[0]);
                    return sosFiles[0];
                }

                // Also check for .dylib (macOS)
                sosFiles = Directory.GetFiles(_symbolCacheDirectory, "libsosplugin.dylib", SearchOption.AllDirectories);
                if (sosFiles.Length > 0)
                {
                    _logger.LogInformation("[SOS] Using (dylib): {Path}", sosFiles[0]);
                    return sosFiles[0];
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SOS] Error searching symbol cache");
            }
        }

        // Check Docker-installed SOS location (set up by dotnet-sos in Dockerfile)
        var dockerSosPath = "/sos/libsosplugin.so";
        if (File.Exists(dockerSosPath))
        {
            _logger.LogInformation("[SOS] Found in Docker location: {Path}", dockerSosPath);
            return dockerSosPath;
        }

        // Also check for dotnet-sos installed location (~/.dotnet/sos)
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dotnetSosPath = Path.Combine(homeDir, ".dotnet", "sos", "libsosplugin.so");
        if (File.Exists(dotnetSosPath))
        {
            _logger.LogInformation("[SOS] Found in dotnet-sos location: {Path}", dotnetSosPath);
            return dotnetSosPath;
        }

        // Also check macOS variant
        var dotnetSosDylibPath = Path.Combine(homeDir, ".dotnet", "sos", "libsosplugin.dylib");
        if (File.Exists(dotnetSosDylibPath))
        {
            _logger.LogInformation("[SOS] Found in dotnet-sos location (dylib): {Path}", dotnetSosDylibPath);
            return dotnetSosDylibPath;
        }

        // Legacy: Search in .NET runtime directories (SOS used to be included there)
        var searchPatterns = new[]
        {
            // User-local .NET SDK installation
            Path.Combine(homeDir, ".dotnet", "shared", "Microsoft.NETCore.App"),
            // System-wide installations (SDK)
            "/usr/share/dotnet/shared/Microsoft.NETCore.App",
            "/usr/local/share/dotnet/shared/Microsoft.NETCore.App",
            // macOS Homebrew
            "/opt/homebrew/share/dotnet/shared/Microsoft.NETCore.App",
            "/usr/local/opt/dotnet/libexec/shared/Microsoft.NETCore.App"
        };

        _logger.LogInformation("[SOS] Searching in {Count} base paths...", searchPatterns.Length);

        foreach (var basePath in searchPatterns)
        {
            var exists = Directory.Exists(basePath);
            _logger.LogInformation("[SOS] Checking: {Path} (exists: {Exists})", basePath, exists);

            if (!exists)
                continue;

            try
            {
                // Get all version directories and sort them in descending order
                var versionDirs = Directory.GetDirectories(basePath)
                    .OrderByDescending(d => d)
                    .ToList();

                _logger.LogInformation("[SOS] Found {Count} version directories in {Path}", versionDirs.Count, basePath);

                foreach (var versionDir in versionDirs)
                {
                    var sosPath = Path.Combine(versionDir, "libsosplugin.so");
                    var sosExists = File.Exists(sosPath);
                    _logger.LogDebug("[SOS] Checking: {Path} (exists: {Exists})", sosPath, sosExists);

                    if (sosExists)
                    {
                        _logger.LogInformation("[SOS] Found in SDK: {Path}", sosPath);
                        return sosPath;
                    }

                    // macOS uses .dylib extension
                    var sosDylibPath = Path.Combine(versionDir, "libsosplugin.dylib");
                    if (File.Exists(sosDylibPath))
                    {
                        _logger.LogInformation("[SOS] Found in SDK (dylib): {Path}", sosDylibPath);
                        return sosDylibPath;
                    }

                    // Log first version dir contents to help debug
                    if (versionDir == versionDirs.First())
                    {
                        try
                        {
                            var files = Directory.GetFiles(versionDir, "*.so").Take(10).ToList();
                            var dlls = Directory.GetFiles(versionDir, "*.dll").Take(5).ToList();
                            _logger.LogInformation("[SOS] Sample files in {Dir}: .so files: [{SoFiles}], .dll files: [{DllFiles}]",
                                Path.GetFileName(versionDir),
                                string.Join(", ", files.Select(Path.GetFileName)),
                                string.Join(", ", dlls.Select(Path.GetFileName)));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "[SOS] Could not enumerate directory contents for debugging");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SOS] Error searching {Path}", basePath);
            }
        }

        _logger.LogWarning("[SOS] SOS plugin not found in any location!");
        return null;
    }



    /// <summary>
    /// Handles output data received from LLDB stdout.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">Event data containing the output line.</param>
    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;

        // Check for prompt + sentinel command on stdout
        // This is the definitive signal that the command is done
        var sentinelFound = e.Data.Contains(SentinelCommand);
        if (!sentinelFound)
        {
            lock (_outputLock)
            {
                _outputBuffer = $"{_outputBuffer}{e.Data}{Environment.NewLine}";
                if (_outputBuffer.Contains(SentinelCommand))
                {
                    _outputBuffer = _outputBuffer.Replace(SentinelCommand, string.Empty);
                    sentinelFound = true;
                }
            }
        }

        // Signal completion ONLY when we see the sentinel on stdout
        if (sentinelFound)
        {
            _outputCompleteEvent.Set();
        }
    }

    /// <summary>
    /// Handles error data received from LLDB stderr.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">Event data containing the error line.</param>
    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;

        // Discard the sentinel error - it's just noise from our sentinel command
        // The real completion signal comes from stdout (prompt + sentinel)
        if (e.Data.Contains(SentinelError))
        {
            return;
        }

        // Append real errors to the buffer (they may be part of command output)
        lock (_outputLock)
        {
            _outputBuffer = $"{_outputBuffer}{e.Data}{Environment.NewLine}";
            if (_outputBuffer.Contains(SentinelError))
            {
                _outputBuffer = _outputBuffer.Replace(SentinelError, string.Empty);
            }
        }
    }



    /// <summary>
    /// Releases all resources used by the <see cref="LldbManager"/>.
    /// </summary>
    /// <remarks>
    /// This method closes any open dumps and terminates the LLDB process.
    /// It is safe to call this method multiple times.
    /// </remarks>
    public void Dispose()
    {
        // Check if already disposed
        if (_disposed)
        {
            return;
        }

        // Close any open dump
        if (IsDumpOpen)
        {
            try
            {
                CloseDump();
            }
            catch
            {
                // Ignore errors during disposal
            }
        }

        // Terminate the LLDB process
        if (_lldbProcess != null)
        {
            try
            {
                // Unsubscribe from events
                _lldbProcess.OutputDataReceived -= OnOutputDataReceived;
                _lldbProcess.ErrorDataReceived -= OnErrorDataReceived;

                // Kill the process if it's still running
                if (!_lldbProcess.HasExited)
                {
                    _lldbProcess.Kill();
                    _lldbProcess.WaitForExit(1000); // Wait up to 1 second
                }

                _lldbProcess.Dispose();
                _lldbProcess = null;
            }
            catch
            {
                // Ignore errors during disposal
            }
        }

        // Dispose the event
        _outputCompleteEvent.Dispose();

        // Mark as disposed
        _disposed = true;
    }

    /// <summary>
    /// Asynchronously releases all resources used by the <see cref="LldbManager"/>.
    /// </summary>
    /// <remarks>
    /// This method provides async disposal for ASP.NET Core scenarios.
    /// It properly awaits process termination without blocking a ThreadPool thread.
    /// </remarks>
    /// <returns>A ValueTask representing the asynchronous dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        // Check if already disposed
        if (_disposed)
        {
            return;
        }

        // Close any open dump
        if (IsDumpOpen)
        {
            try
            {
                CloseDump();
            }
            catch
            {
                // Ignore errors during disposal
            }
        }

        // Terminate the LLDB process
        if (_lldbProcess != null)
        {
            try
            {
                // Unsubscribe from events
                _lldbProcess.OutputDataReceived -= OnOutputDataReceived;
                _lldbProcess.ErrorDataReceived -= OnErrorDataReceived;

                // Kill the process if it's still running
                if (!_lldbProcess.HasExited)
                {
                    _lldbProcess.Kill();
                    // Use async wait to avoid blocking ThreadPool threads
                    await _lldbProcess.WaitForExitAsync().ConfigureAwait(false);
                }

                _lldbProcess.Dispose();
                _lldbProcess = null;
            }
            catch
            {
                // Ignore errors during disposal
            }
        }

        // Dispose the event
        _outputCompleteEvent.Dispose();

        // Mark as disposed
        _disposed = true;
    }
}

/// <summary>
/// Result of dotnet-symbol --verifycore platform detection.
/// </summary>
public class VerifyCoreResult
{
    /// <summary>
    /// Gets or sets whether the dump is from an Alpine/musl system.
    /// </summary>
    public bool IsAlpine { get; set; }

    /// <summary>
    /// Gets or sets the detected architecture (x64, arm64, x86).
    /// </summary>
    public string? Architecture { get; set; }

    /// <summary>
    /// Gets or sets the list of module paths found in the dump.
    /// </summary>
    public List<string> ModulePaths { get; set; } = [];

    /// <summary>
    /// Gets or sets the modules with their load addresses.
    /// Key: module path, Value: load address in memory.
    /// </summary>
    public Dictionary<string, ulong> ModuleAddresses { get; set; } = [];

    /// <summary>
    /// Gets or sets the main executable path (first module in the dump).
    /// For standalone .NET apps, this is the app binary.
    /// </summary>
    public string? MainExecutablePath { get; set; }

    /// <summary>
    /// Gets or sets the main executable name (without path).
    /// </summary>
    public string? MainExecutableName { get; set; }
}
