using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DebuggerMcp.ObjectInspection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp;

/// <summary>
/// Manages interactions with the Windows Debugger Engine (DbgEng) for analyzing memory dumps.
/// </summary>
/// <remarks>
/// This class provides a high-level wrapper around the DbgEng COM API, simplifying
/// operations such as opening dumps, executing commands, and capturing output.
/// It implements IDisposable to ensure proper cleanup of COM resources.
/// 
/// This class is Windows-only as it uses COM interop with the DbgEng API.
/// </remarks>
[SupportedOSPlatform("windows")]
public class WinDbgManager : IDebuggerManager, IDebuggerDiagnostics
{
    /// <summary>
    /// Active interrupt request flag for DbgEng.
    /// </summary>
    private const uint DebugInterruptActive = 0;

    /// <summary>
    /// Logger for debugger lifecycle, symbol, and SOS diagnostics.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Synchronizes dispatcher swaps and recovery transitions.
    /// </summary>
    private readonly object _executionLock = new();

    /// <summary>
    /// Timeout applied to WinDbg operations executed through DbgEng.
    /// </summary>
    private readonly TimeSpan _commandTimeout;

    /// <summary>
    /// Grace period after an interrupt request before the current engine is abandoned.
    /// </summary>
    private static readonly TimeSpan InterruptGracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Current dispatcher that owns the dedicated STA thread for DbgEng operations.
    /// </summary>
    private WinDbgStaDispatcher? _dispatcher;

    /// <summary>
    /// Current debugger context. A new context is created whenever recovery replaces the engine.
    /// </summary>
    private WinDbgEngineContext _context = new();

    /// <summary>
    /// Indicates whether this instance has been disposed.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Timeout in milliseconds for WaitForEvent operations.
    /// </summary>
    private const uint WaitForEventTimeoutMs = 5000;

    /// <summary>
    /// DEBUG_END_PASSIVE flag for EndSession - passive end without terminating processes.
    /// </summary>
    private const uint DebugEndPassive = 0x00000001;

    /// <summary>
    /// Gets a value indicating whether the debugger engine has been initialized.
    /// </summary>
    /// <value>
    /// <c>true</c> if both the client and control interfaces are available; otherwise, <c>false</c>.
    /// </value>
    public bool IsInitialized => GetCurrentContext().IsInitialized;

    /// <summary>
    /// Gets a value indicating whether a dump file is currently open.
    /// </summary>
    /// <value>
    /// <c>true</c> if a dump file is open and ready for analysis; otherwise, <c>false</c>.
    /// </value>
    public bool IsDumpOpen => GetCurrentContext().IsDumpOpen;

    /// <summary>
    /// Gets the path to the currently open dump file.
    /// </summary>
    /// <value>
    /// The full path to the dump file if one is open; otherwise, <c>null</c>.
    /// </value>
    public string? CurrentDumpPath => GetCurrentContext().CurrentDumpPath;

    /// <summary>
    /// Gets a value indicating whether the SOS extension is loaded.
    /// </summary>
    /// <value>
    /// <c>true</c> if SOS is loaded and .NET commands are available; otherwise, <c>false</c>.
    /// </value>
    public bool IsSosLoaded => GetCurrentContext().IsSosLoaded;

    /// <summary>
    /// Gets a value indicating whether the currently open dump is a .NET dump.
    /// </summary>
    /// <value>
    /// <c>true</c> if the dump contains .NET runtime modules (CoreCLR or CLR); otherwise, <c>false</c>.
    /// </value>
    public bool IsDotNetDump => GetCurrentContext().IsDotNetDump;

    /// <summary>
    /// Gets the type of debugger this manager controls.
    /// </summary>
    /// <value>
    /// Always returns "WinDbg" for this implementation.
    /// </value>
    public string DebuggerType => "WinDbg";

    /// <summary>
    /// Initializes a new instance of the <see cref="WinDbgManager"/> class.
    /// </summary>
    /// <remarks>
    /// The constructor does not initialize the debugger engine. Call <see cref="InitializeAsync"/>
    /// to set up the COM interfaces before using other methods.
    /// </remarks>
    public WinDbgManager() : this(NullLogger<WinDbgManager>.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WinDbgManager"/> class with logging support.
    /// </summary>
    /// <param name="logger">Logger used for debugger diagnostics.</param>
    public WinDbgManager(ILogger<WinDbgManager> logger)
    {
        _logger = logger ?? NullLogger<WinDbgManager>.Instance;
        _commandTimeout = TimeSpan.FromSeconds(Configuration.EnvironmentConfig.GetWinDbgCommandTimeoutSeconds());
    }

    /// <summary>
    /// Returns the current debugger context reference.
    /// </summary>
    /// <returns>The active debugger context.</returns>
    private WinDbgEngineContext GetCurrentContext() => _context;



    /// <summary>
    /// Initializes the debugger engine by creating COM interfaces.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the debugger engine fails to initialize.
    /// </exception>
    /// <remarks>
    /// This method creates the IDebugClient and IDebugControl interfaces and sets up
    /// output callbacks to capture debugger output. It is safe to call this method
    /// multiple times; subsequent calls will be ignored if already initialized.
    /// </remarks>
    /// <returns>A task representing the asynchronous initialization operation.</returns>
    public virtual Task InitializeAsync()
    {
        var context = GetCurrentContext();
        return ExecuteOperationAsync(
            "initialize WinDbg",
            context,
            () =>
            {
                InitializeCore(context);
                return true;
            },
            allowRecovery: false);
    }

    /// <summary>
    /// Opens a crash dump file for analysis.
    /// </summary>
    /// <param name="dumpFilePath">The full path to the dump file to open.</param>
    /// <returns>A message indicating successful opening along with basic dump information.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the manager is not initialized or the dump fails to open.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the specified dump file does not exist.
    /// </exception>
    /// <remarks>
    /// This method opens the dump file and waits for the debugger to process it.
    /// After opening, the dump is ready for command execution.
    /// </remarks>
    /// <param name="executablePath">
    /// Optional path to the executable for standalone apps (currently ignored on WinDbg).
    /// </param>
    public virtual void OpenDumpFile(string dumpFilePath, string? executablePath = null)
    {
        if (!IsInitialized)
        {
            throw new InvalidOperationException("WinDbg Manager is not initialized");
        }

        // Validate that the dump file exists
        if (!File.Exists(dumpFilePath))
        {
            throw new FileNotFoundException($"Dump file not found: {dumpFilePath}");
        }

        var context = GetCurrentContext();
        ExecuteOperation(
            "open dump",
            context,
            () =>
            {
                OpenDumpFileCore(context, dumpFilePath, executablePath, isRecovery: false);
                return true;
            });
    }

    /// <summary>
    /// Closes the currently open dump file and ends the debugging session.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the manager is not initialized or closing fails.
    /// </exception>
    /// <remarks>
    /// This method ends the debugging session and releases resources associated with the dump.
    /// After calling this method, you can open another dump file if needed.
    /// </remarks>
    public virtual void CloseDump()
    {
        var context = GetCurrentContext();
        ExecuteOperation(
            "close dump",
            context,
            () =>
            {
                CloseDumpCore(context);
                return true;
            });
    }

    /// <summary>
    /// Executes a WinDbg command on the currently open dump.
    /// </summary>
    /// <param name="command">The command to execute (e.g., "k", "!threads", "!dumpheap -stat").</param>
    /// <returns>The output from executing the command.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the manager is not initialized, no dump is open, or command execution fails.
    /// </exception>
    /// <remarks>
    /// This method supports all WinDbg commands, including extension commands.
    /// The output is captured via the registered output callbacks.
    /// </remarks>
    public virtual string ExecuteCommand(string command)
    {
        // Validate the command first (fail-fast on bad input)
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command cannot be null or empty", nameof(command));
        }

        var context = GetCurrentContext();
        return ExecuteOperation(
            $"execute WinDbg command '{SummarizeCommand(command)}'",
            context,
            () => ExecuteCommandCore(context, command));
    }

    /// <summary>
    /// Internal method that executes a WinDbg command without requiring a dump to be open.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <returns>The output from executing the command.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the manager is not initialized or command execution fails.
    /// </exception>
    /// <remarks>
    /// This method is used internally for commands that can be executed before opening a dump,
    /// such as .sympath for configuring symbol paths.
    /// </remarks>
    private string ExecuteCommandInternal(string command)
    {
        return ExecuteCommandCore(GetCurrentContext(), command);
    }

    /// <summary>
    /// Configures the symbol path for the debugger.
    /// </summary>
    /// <param name="symbolPath">The symbol path string in WinDbg format (e.g., "srv*c:\\symbols*https://msdl.microsoft.com/download/symbols").</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the manager is not initialized.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the symbol path is null, empty, or whitespace.
    /// </exception>
    /// <remarks>
    /// This method sets the symbol path using the .sympath command.
    /// It can be called before opening a dump file, which is the recommended practice
    /// to ensure symbols are available when the dump is loaded.
    /// </remarks>
    public virtual void ConfigureSymbolPath(string symbolPath)
    {
        // Validate parameters
        if (string.IsNullOrWhiteSpace(symbolPath))
        {
            throw new ArgumentException("Symbol path cannot be null or empty.", nameof(symbolPath));
        }

        var context = GetCurrentContext();
        ExecuteOperation(
            "configure symbol path",
            context,
            () =>
            {
                ConfigureSymbolPathCore(context, symbolPath);
                return true;
            });
    }

    /// <summary>
    /// Loads the SOS (Son of Strike) debugging extension for .NET analysis.
    /// </summary>
    /// <returns>The output from loading the SOS extension.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the manager is not initialized, no dump is open, or loading fails.
    /// </exception>
    /// <remarks>
    /// This method attempts to load SOS for CoreCLR first (.NET Core/.NET 5+),
    /// and falls back to CLR (.NET Framework) if that fails.
    /// </remarks>
    public virtual void LoadSosExtension()
    {
        var context = GetCurrentContext();
        ExecuteOperation(
            "load SOS",
            context,
            () =>
            {
                LoadSosExtensionCore(context);
                return true;
            });
    }

    /// <summary>
    /// Executes a WinDbg operation on the dedicated STA thread and returns its result.
    /// </summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="operationName">Friendly name used in logs and recovery messages.</param>
    /// <param name="context">Debugger context that the operation is allowed to mutate.</param>
    /// <param name="operation">Operation to execute.</param>
    /// <param name="allowRecovery">Whether timeout and engine-failure recovery should be attempted.</param>
    /// <returns>The operation result.</returns>
    private T ExecuteOperation<T>(
        string operationName,
        WinDbgEngineContext context,
        Func<T> operation,
        bool allowRecovery = true)
    {
        try
        {
            return ExecuteOperationAsync(operationName, context, operation, allowRecovery).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not ArgumentException && ex is not FileNotFoundException)
        {
            throw new InvalidOperationException($"Failed to {operationName}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Executes a WinDbg operation on the dedicated STA thread and returns its asynchronous task.
    /// </summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="operationName">Friendly name used in logs and recovery messages.</param>
    /// <param name="context">Debugger context that the operation is allowed to mutate.</param>
    /// <param name="operation">Operation to execute.</param>
    /// <param name="allowRecovery">Whether timeout and engine-failure recovery should be attempted.</param>
    /// <returns>The asynchronous operation task.</returns>
    private async Task<T> ExecuteOperationAsync<T>(
        string operationName,
        WinDbgEngineContext context,
        Func<T> operation,
        bool allowRecovery = true)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WinDbgManager));
        }

        var dispatcher = GetOrCreateDispatcher();
        var task = dispatcher.InvokeAsync(operation);

        if (await Task.WhenAny(task, Task.Delay(_commandTimeout)).ConfigureAwait(false) == task)
        {
            return await AwaitOperationResultAsync(task, context, operationName, allowRecovery, timeoutTriggered: false).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "[WinDbg] Operation timed out after {Timeout}s: {Operation}",
            _commandTimeout.TotalSeconds,
            operationName);

        TryInterruptContext(context, operationName);

        if (await Task.WhenAny(task, Task.Delay(InterruptGracePeriod)).ConfigureAwait(false) == task)
        {
            return await AwaitOperationResultAsync(task, context, operationName, allowRecovery, timeoutTriggered: true).ConfigureAwait(false);
        }

        if (allowRecovery && await TryRecoverEngineAsync(context, operationName, timeoutTriggered: true).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"WinDbg timed out while trying to {operationName}. The engine was recovered and the dump was reopened when possible. Please retry the operation.");
        }

        throw new InvalidOperationException(
            $"WinDbg timed out while trying to {operationName}. Recovery did not complete, so the debugger session may need to be reopened.");
    }

    /// <summary>
    /// Awaits a dispatched DbgEng operation and applies the standard recoverable-engine handling.
    /// </summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="task">The in-flight debugger task to await.</param>
    /// <param name="context">Debugger context that produced the task.</param>
    /// <param name="operationName">Friendly operation name for diagnostics.</param>
    /// <param name="allowRecovery">Whether recoverable failures should trigger engine recovery.</param>
    /// <param name="timeoutTriggered">Whether the operation had already timed out before completion.</param>
    /// <returns>The completed task result.</returns>
    private async Task<T> AwaitOperationResultAsync<T>(
        Task<T> task,
        WinDbgEngineContext context,
        string operationName,
        bool allowRecovery,
        bool timeoutTriggered)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (allowRecovery && ShouldAttemptRecovery(ex))
        {
            if (await TryRecoverEngineAsync(context, operationName, timeoutTriggered).ConfigureAwait(false))
            {
                var timeoutPrefix = timeoutTriggered ? "after timing out " : string.Empty;
                throw new InvalidOperationException(
                    $"WinDbg failed {timeoutPrefix}while trying to {operationName}. The engine was recovered and the dump was reopened when possible. Please retry the operation.",
                    ex);
            }

            var timeoutSuffix = timeoutTriggered ? " after timing out" : string.Empty;
            throw new InvalidOperationException(
                $"WinDbg failed while trying to {operationName}{timeoutSuffix}, and recovery did not complete.",
                ex);
        }
    }

    /// <summary>
    /// Gets the active STA dispatcher, creating it when the manager first needs DbgEng.
    /// </summary>
    /// <returns>The active dispatcher.</returns>
    private WinDbgStaDispatcher GetOrCreateDispatcher()
    {
        lock (_executionLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _dispatcher ??= new WinDbgStaDispatcher("WinDbgManager");
            return _dispatcher;
        }
    }

    /// <summary>
    /// Interrupts the current debugger execution path after a timeout.
    /// </summary>
    /// <param name="context">Debugger context that timed out.</param>
    /// <param name="operationName">The operation being interrupted.</param>
    private void TryInterruptContext(WinDbgEngineContext context, string operationName)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (context.Control == null)
                {
                    return;
                }

                context.Control.SetInterruptTimeout((uint)Math.Ceiling(InterruptGracePeriod.TotalSeconds));
                context.Control.SetInterrupt(DebugInterruptActive);
                _logger.LogWarning("[WinDbg] Interrupt requested for timed-out operation: {Operation}", operationName);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[WinDbg] Failed to interrupt timed-out operation: {Operation}", operationName);
            }
        });
    }

    /// <summary>
    /// Determines whether a failed operation looks like a debugger-engine failure worth recovering.
    /// </summary>
    /// <param name="exception">The exception that escaped the STA operation.</param>
    /// <returns><c>true</c> when recovery should be attempted; otherwise <c>false</c>.</returns>
    private static bool ShouldAttemptRecovery(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is COMException or InvalidComObjectException or ObjectDisposedException)
            {
                return true;
            }

            if (current.Message.Contains("HRESULT", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("RPC_E", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts to replace the current DbgEng context and reopen the active dump after a failure.
    /// </summary>
    /// <param name="failedContext">The context that experienced the failure.</param>
    /// <param name="operationName">The operation that failed.</param>
    /// <param name="timeoutTriggered">Whether the failure was a timeout.</param>
    /// <returns><c>true</c> if recovery completed; otherwise <c>false</c>.</returns>
    private async Task<bool> TryRecoverEngineAsync(WinDbgEngineContext failedContext, string operationName, bool timeoutTriggered)
    {
        lock (_executionLock)
        {
            if (!ReferenceEquals(_context, failedContext))
            {
                return true;
            }
        }

        var snapshot = failedContext.CreateRecoverySnapshot();
        _logger.LogWarning(
            "[WinDbg] Attempting recovery after {Operation}. Timeout={TimeoutTriggered}, DumpOpen={WasDumpOpen}",
            operationName,
            timeoutTriggered,
            snapshot.WasDumpOpen);

        var replacementContext = new WinDbgEngineContext();
        WinDbgStaDispatcher? replacementDispatcher = null;
        WinDbgStaDispatcher? abandonedDispatcher = null;

        lock (_executionLock)
        {
            if (!ReferenceEquals(_context, failedContext))
            {
                return true;
            }

            replacementDispatcher = new WinDbgStaDispatcher("WinDbgManager-Recovery");
            abandonedDispatcher = _dispatcher;
            _dispatcher = replacementDispatcher;
            _context = replacementContext;
        }

        try
        {
            await replacementDispatcher.InvokeAsync(() =>
            {
                InitializeCore(replacementContext);

                if (!string.IsNullOrWhiteSpace(snapshot.LastSymbolPath))
                {
                    ConfigureSymbolPathCore(replacementContext, snapshot.LastSymbolPath);
                }
                else
                {
                    replacementContext.SymbolCacheDirectory = snapshot.SymbolCacheDirectory;
                }

                if (snapshot.WasDumpOpen &&
                    !string.IsNullOrWhiteSpace(snapshot.DumpPath) &&
                    File.Exists(snapshot.DumpPath))
                {
                    OpenDumpFileCore(replacementContext, snapshot.DumpPath, snapshot.ExecutablePath, isRecovery: true);
                }
                else
                {
                    replacementContext.DetectedRuntimeVersion = snapshot.DetectedRuntimeVersion;
                }

                return true;
            }).ConfigureAwait(false);

            _logger.LogInformation(
                "[WinDbg] Recovery completed after {Operation}. DumpReopened={DumpReopened}",
                operationName,
                replacementContext.IsDumpOpen);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WinDbg] Recovery failed after {Operation}", operationName);
            return false;
        }
        finally
        {
            abandonedDispatcher?.Dispose();
        }
    }

    /// <summary>
    /// Initializes DbgEng for the supplied debugger context.
    /// </summary>
    /// <param name="context">The context to initialize.</param>
    private void InitializeCore(WinDbgEngineContext context)
    {
        if (context.IsInitialized)
        {
            return;
        }

        try
        {
            _logger.LogInformation("[WinDbg] Initializing DbgEng");

            var iid = DbgEng.IID_IDebugClient;
            var hr = DbgEng.DebugCreate(ref iid, out object clientObject);
            if (hr != 0)
            {
                throw new COMException($"Failed to create IDebugClient. HRESULT: 0x{hr:X8}", hr);
            }

            context.Client = (IDebugClient)clientObject;
            context.Control = (IDebugControl)context.Client;
            context.OutputCallbacks = new OutputCallbacks();
            context.OutputCallbacksPtr = Marshal.GetComInterfaceForObject(context.OutputCallbacks, typeof(IDebugOutputCallbacks));
            context.Client.SetOutputCallbacks(context.OutputCallbacksPtr);

            _logger.LogInformation("[WinDbg] DbgEng initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WinDbg] Failed to initialize DbgEng");
            ReleaseContextResources(context);
            throw new InvalidOperationException($"Failed to initialize WinDbg Manager: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Opens a dump file inside the supplied debugger context.
    /// </summary>
    /// <param name="context">The context that will own the dump.</param>
    /// <param name="dumpFilePath">The dump file path to open.</param>
    /// <param name="executablePath">Optional standalone executable path.</param>
    /// <param name="isRecovery">Whether the open is happening during recovery.</param>
    private void OpenDumpFileCore(WinDbgEngineContext context, string dumpFilePath, string? executablePath, bool isRecovery)
    {
        if (!context.IsInitialized)
        {
            throw new InvalidOperationException("WinDbg Manager is not initialized");
        }

        if (context.IsDumpOpen)
        {
            throw new InvalidOperationException("A dump file is already open. Close it first.");
        }

        try
        {
            ClearObjectInspectionCacheForDumpTransition("opening a dump");
            context.CurrentExecutablePath = executablePath;
            context.OutputCallbacks?.ClearOutput();

            TryApplyExecutableSearchPath(context, executablePath);

            var hr = context.Client!.OpenDumpFile(dumpFilePath);
            if (hr != 0)
            {
                throw new COMException($"Failed to open dump file. HRESULT: 0x{hr:X8}", hr);
            }

            context.IsDumpOpen = true;
            context.CurrentDumpPath = dumpFilePath;

            hr = context.Control!.WaitForEvent(0, WaitForEventTimeoutMs);
            if (hr != 0)
            {
                throw new COMException($"Failed to wait for event. HRESULT: 0x{hr:X8}", hr);
            }

            ExecuteCommandCore(context, ".echo Dump file opened successfully", requiresOpenDump: false);

            context.IsDotNetDump = DetectDotNetDumpCore(context);
            var detectedArchitecture = DetectArchitecture(context);
            context.DetectedRuntimeVersion = context.IsDotNetDump ? DetectRuntimeVersion(context) : null;

            _logger.LogInformation(
                "[WinDbg] Dump opened: {DumpPath}, DotNet={IsDotNetDump}, Architecture={Architecture}, Runtime={RuntimeVersion}, Recovery={IsRecovery}",
                dumpFilePath,
                context.IsDotNetDump,
                detectedArchitecture ?? "(unknown)",
                context.DetectedRuntimeVersion ?? "(unknown)",
                isRecovery);

            if (context.IsDotNetDump)
            {
                try
                {
                    LoadSosExtensionCore(context);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[WinDbg] Automatic SOS loading failed");
                }
            }
        }
        catch (Exception ex)
        {
            context.IsDumpOpen = false;
            context.IsDotNetDump = false;
            context.IsSosLoaded = false;
            context.CurrentDumpPath = null;
            context.CurrentExecutablePath = null;
            context.DetectedRuntimeVersion = null;
            throw new InvalidOperationException($"Failed to open dump file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Closes the open dump for the supplied context.
    /// </summary>
    /// <param name="context">The context whose dump should be closed.</param>
    private void CloseDumpCore(WinDbgEngineContext context)
    {
        if (!context.IsInitialized)
        {
            throw new InvalidOperationException("WinDbg Manager is not initialized");
        }

        try
        {
            if (!context.IsDumpOpen)
            {
                return;
            }

            ClearObjectInspectionCacheForDumpTransition("closing a dump");
            context.Client!.EndSession(DebugEndPassive);
            context.IsDumpOpen = false;
            context.IsSosLoaded = false;
            context.IsDotNetDump = false;
            context.CurrentDumpPath = null;
            context.CurrentExecutablePath = null;
            context.DetectedRuntimeVersion = null;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to close dump: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Clears cached object-inspection results when WinDbg moves between dump contexts.
    /// </summary>
    /// <param name="reason">Human-readable reason for the dump-state transition.</param>
    private void ClearObjectInspectionCacheForDumpTransition(string reason)
    {
        ClearObjectInspectionCache();
        _logger.LogDebug("[WinDbg] Cleared ObjectInspector cache while {Reason}", reason);
    }

    /// <summary>
    /// Clears the shared object-inspection cache after a WinDbg dump transition.
    /// </summary>
    private static void ClearObjectInspectionCache()
    {
        ObjectInspector.ClearCache();
    }

    /// <summary>
    /// Executes a debugger command inside the supplied context.
    /// </summary>
    /// <param name="context">The context that owns the command execution.</param>
    /// <param name="command">The command to execute.</param>
    /// <param name="requiresOpenDump">Whether the command requires an open dump.</param>
    /// <returns>The captured command output.</returns>
    private string ExecuteCommandCore(WinDbgEngineContext context, string command, bool requiresOpenDump = true)
    {
        if (!context.IsInitialized)
        {
            throw new InvalidOperationException("WinDbg Manager is not initialized");
        }

        if (requiresOpenDump && !context.IsDumpOpen)
        {
            throw new InvalidOperationException("No dump file is currently open");
        }

        try
        {
            context.OutputCallbacks?.ClearOutput();

            var hr = context.Control!.Execute(
                DbgEngConstants.DEBUG_OUTCTL_ALL_CLIENTS,
                command,
                DbgEngConstants.DEBUG_EXECUTE_DEFAULT);
            if (hr != 0)
            {
                throw new COMException($"Failed to execute command. HRESULT: 0x{hr:X8}", hr);
            }

            return context.OutputCallbacks?.GetOutput() ?? string.Empty;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to execute command '{command}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Applies the symbol path to the supplied debugger context.
    /// </summary>
    /// <param name="context">The context that should receive the symbol path.</param>
    /// <param name="symbolPath">The symbol path to apply.</param>
    private void ConfigureSymbolPathCore(WinDbgEngineContext context, string symbolPath)
    {
        if (!context.IsInitialized)
        {
            throw new InvalidOperationException("WinDbg Manager is not initialized");
        }

        try
        {
            ExecuteCommandCore(context, $".sympath {symbolPath}", requiresOpenDump: false);
            context.LastSymbolPath = symbolPath;
            context.SymbolCacheDirectory = TryExtractSymbolCacheDirectory(symbolPath);
            _logger.LogInformation("[WinDbg] Applied symbol path: {SymbolPath}", symbolPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to configure symbol path: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Loads and validates SOS for the supplied debugger context.
    /// </summary>
    /// <param name="context">The context that should load SOS.</param>
    private void LoadSosExtensionCore(WinDbgEngineContext context)
    {
        if (!context.IsInitialized)
        {
            throw new InvalidOperationException("WinDbg Manager is not initialized");
        }

        if (!context.IsDumpOpen)
        {
            throw new InvalidOperationException("No dump file is currently open");
        }

        if (context.IsSosLoaded)
        {
            return;
        }

        try
        {
            var result = ExecuteCommandCore(context, ".loadby sos coreclr");
            var coreclrFailed = result.Contains("Unable to find module", StringComparison.OrdinalIgnoreCase) ||
                                result.Contains("error", StringComparison.OrdinalIgnoreCase);

            if (coreclrFailed)
            {
                var clrResult = ExecuteCommandCore(context, ".loadby sos clr");
                var clrFailed = clrResult.Contains("Unable to find module", StringComparison.OrdinalIgnoreCase) ||
                                clrResult.Contains("error", StringComparison.OrdinalIgnoreCase);

                if (clrFailed && !TryLoadSosFromKnownLocations(context))
                {
                    throw new InvalidOperationException(
                        $"Failed to load SOS extension. CoreCLR result: {result.Trim()}. CLR result: {clrResult.Trim()}");
                }
            }

            var verifyResult = ExecuteCommandCore(context, "!eeversion");
            if (verifyResult.Contains("No export", StringComparison.OrdinalIgnoreCase) ||
                verifyResult.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                verifyResult.Contains("Unrecognized command", StringComparison.OrdinalIgnoreCase) ||
                verifyResult.Contains("Unable to load", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SOS extension loaded but commands are not available. Verify result: {verifyResult.Trim()}");
            }

            context.IsSosLoaded = true;
            _logger.LogInformation("[WinDbg] SOS loaded successfully");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load SOS extension: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Summarizes a debugger command for logging and timeout messages.
    /// </summary>
    /// <param name="command">The command to summarize.</param>
    /// <returns>A shortened single-line summary.</returns>
    private static string SummarizeCommand(string command)
    {
        var normalized = command.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 120 ? normalized : normalized[..117] + "...";
    }

    /// <summary>
    /// Returns normalized platform information for the current dump.
    /// </summary>
    /// <returns>The normalized platform information, or <c>null</c> when no dump is open.</returns>
    public Analysis.PlatformInfo? GetPlatformInfo()
    {
        var context = GetCurrentContext();
        if (!context.IsDumpOpen)
        {
            return null;
        }

        return ExecuteOperation(
            "read WinDbg platform information",
            context,
            () =>
            {
                var architecture = DetectArchitecture(context) ?? string.Empty;
                return new Analysis.PlatformInfo
                {
                    Os = "Windows",
                    Architecture = architecture,
                    RuntimeVersion = context.DetectedRuntimeVersion,
                    PointerSize = architecture is "x64" or "arm64" ? 64 : architecture is "x86" or "arm" ? 32 : null
                };
            });
    }

    /// <summary>
    /// Captures top-frame registers for the specified OS thread identifiers.
    /// </summary>
    /// <param name="threadIds">OS thread identifiers to inspect.</param>
    /// <returns>Captured register sets keyed by OS thread identifier.</returns>
    public Dictionary<uint, Analysis.ClrRegisterSet> GetTopFrameRegisters(IEnumerable<uint> threadIds)
    {
        var context = GetCurrentContext();
        if (!context.IsInitialized || !context.IsDumpOpen)
        {
            return new Dictionary<uint, Analysis.ClrRegisterSet>();
        }

        return ExecuteOperation(
            "read WinDbg top-frame registers",
            context,
            () =>
            {
                var result = new Dictionary<uint, Analysis.ClrRegisterSet>();
                var threadMap = BuildThreadIdToDebuggerThreadIndexMap(context);
                foreach (var threadId in threadIds)
                {
                    if (!threadMap.TryGetValue(threadId, out var debuggerThreadIndex))
                    {
                        continue;
                    }

                    try
                    {
                        ExecuteCommandCore(context, $"~{debuggerThreadIndex}s");
                        var registers = ParseWinDbgRegisters(ExecuteCommandCore(context, "r"));
                        if (registers != null)
                        {
                            result[threadId] = registers;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[WinDbg] Failed to read top-frame registers for thread {ThreadId}", threadId);
                    }
                }

                return result;
            });
    }

    /// <summary>
    /// Captures per-frame registers for a specific OS thread using <c>kv</c> and <c>.frame</c>.
    /// </summary>
    /// <param name="threadId">The OS thread identifier to inspect.</param>
    /// <returns>Per-frame register payloads for the thread.</returns>
    public IReadOnlyList<DebuggerFrameRegisters> GetPerFrameRegisters(uint threadId)
    {
        var context = GetCurrentContext();
        if (!context.IsInitialized || !context.IsDumpOpen)
        {
            return Array.Empty<DebuggerFrameRegisters>();
        }

        return ExecuteOperation(
            $"read WinDbg frame registers for thread {threadId}",
            context,
            () =>
            {
                var result = new List<DebuggerFrameRegisters>();
                var threadMap = BuildThreadIdToDebuggerThreadIndexMap(context);
                if (!threadMap.TryGetValue(threadId, out var debuggerThreadIndex))
                {
                    return result;
                }

                try
                {
                    ExecuteCommandCore(context, $"~{debuggerThreadIndex}s");
                    var stackOutput = ExecuteCommandCore(context, "kv 200");
                    var frames = ParseWinDbgFrameStackPointers(stackOutput);

                    foreach (var (frameIndex, stackPointer) in frames)
                    {
                        try
                        {
                            ExecuteCommandCore(context, $".frame {frameIndex}");
                            var registers = ParseWinDbgRegisterValues(ExecuteCommandCore(context, "r"));
                            if (registers.Count == 0)
                            {
                                continue;
                            }

                            result.Add(new DebuggerFrameRegisters
                            {
                                ThreadId = threadId,
                                StackPointer = stackPointer,
                                Registers = registers
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "[WinDbg] Failed to read registers for thread {ThreadId} frame {FrameIndex}", threadId, frameIndex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[WinDbg] Failed to read per-frame registers for thread {ThreadId}", threadId);
                }

                return (IReadOnlyList<DebuggerFrameRegisters>)result;
            });
    }

    /// <summary>
    /// Applies the executable directory to WinDbg's executable search path when available.
    /// </summary>
    /// <param name="executablePath">The standalone executable path supplied by metadata.</param>
    private void TryApplyExecutableSearchPath(WinDbgEngineContext context, string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return;
        }

        var executableDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(executableDirectory))
        {
            return;
        }

        try
        {
            ExecuteCommandCore(context, $".exepath+ \"{executableDirectory}\"", requiresOpenDump: false);
            _logger.LogInformation("[WinDbg] Added executable search path {ExecutableDirectory}", executableDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[WinDbg] Failed to apply executable search path {ExecutableDirectory}", executableDirectory);
        }
    }

    /// <summary>
    /// Tries to load SOS from explicitly resolved locations when <c>.loadby</c> is insufficient.
    /// </summary>
    /// <returns><c>true</c> when SOS loaded successfully from a resolved path; otherwise <c>false</c>.</returns>
    private bool TryLoadSosFromKnownLocations(WinDbgEngineContext context)
    {
        foreach (var candidatePath in EnumerateSosCandidates(context))
        {
            try
            {
                var loadResult = ExecuteCommandCore(context, $".load \"{candidatePath}\"");
                if (!loadResult.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                    !loadResult.Contains("Unable to", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("[WinDbg] Loaded SOS from {SosPath}", candidatePath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[WinDbg] Failed to load SOS from {SosPath}", candidatePath);
            }
        }

        return false;
    }

    /// <summary>
    /// Enumerates candidate SOS locations in product-defined search order.
    /// </summary>
    /// <returns>Existing SOS paths that should be tried as explicit loads.</returns>
    private IEnumerable<string> EnumerateSosCandidates(WinDbgEngineContext context)
    {
        static IEnumerable<string> ExistingFiles(IEnumerable<string> paths)
            => paths.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void YieldDirectoryCandidates(List<string> target, string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            foreach (var candidate in Directory.GetFiles(directory, "sos.dll", SearchOption.TopDirectoryOnly))
            {
                if (seen.Add(candidate))
                {
                    target.Add(candidate);
                }
            }
        }

        var candidates = new List<string>();

        var configuredPath = Environment.GetEnvironmentVariable(Configuration.EnvironmentConfig.SosPluginPath);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            foreach (var candidate in ExistingFiles([configuredPath]))
            {
                if (seen.Add(candidate))
                {
                    candidates.Add(candidate);
                }
            }

            YieldDirectoryCandidates(candidates, configuredPath);
        }

        YieldDirectoryCandidates(candidates, context.SymbolCacheDirectory);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        YieldDirectoryCandidates(candidates, Path.Combine(userProfile, ".dotnet", "sos"));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        AddRuntimeCandidates(candidates, Path.Combine(programFiles, "dotnet", "shared", "Microsoft.NETCore.App"), context);

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        AddRuntimeCandidates(candidates, Path.Combine(windowsDirectory, "Microsoft.NET", "Framework64"), context);
        AddRuntimeCandidates(candidates, Path.Combine(windowsDirectory, "Microsoft.NET", "Framework"), context);

        return candidates;
    }

    /// <summary>
    /// Adds SOS candidates from runtime directories, preferring the detected runtime version when known.
    /// </summary>
    /// <param name="candidates">The list receiving candidate paths.</param>
    /// <param name="baseDirectory">The runtime base directory to inspect.</param>
    private void AddRuntimeCandidates(List<string> candidates, string baseDirectory, WinDbgEngineContext context)
    {
        if (!Directory.Exists(baseDirectory))
        {
            return;
        }

        var runtimeDirectories = Directory.GetDirectories(baseDirectory)
            .OrderByDescending(directory =>
                string.Equals(Path.GetFileName(directory), context.DetectedRuntimeVersion, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(directory => directory, StringComparer.OrdinalIgnoreCase);

        foreach (var runtimeDirectory in runtimeDirectories)
        {
            var sosPath = Path.Combine(runtimeDirectory, "sos.dll");
            if (File.Exists(sosPath))
            {
                candidates.Add(sosPath);
            }
        }
    }

    /// <summary>
    /// Detects the normalized architecture of the current target.
    /// </summary>
    /// <returns>The normalized architecture string, or <c>null</c> when it cannot be determined.</returns>
    private string? DetectArchitecture(WinDbgEngineContext context)
    {
        if (!context.IsInitialized)
        {
            return null;
        }

        try
        {
            if (context.Control!.GetExecutingProcessorType(out var processorType) == 0)
            {
                return processorType switch
                {
                    0x8664 => "x64",
                    0x014c => "x86",
                    0xAA64 => "arm64",
                    0x01c0 or 0x01c4 => "arm",
                    _ => null
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[WinDbg] Failed to detect architecture from DbgEng");
        }

        return null;
    }

    /// <summary>
    /// Detects the .NET runtime version for the current dump using module metadata.
    /// </summary>
    /// <returns>The runtime version when it can be extracted; otherwise <c>null</c>.</returns>
    private string? DetectRuntimeVersion(WinDbgEngineContext context)
    {
        if (!context.IsDumpOpen)
        {
            return null;
        }

        var versionPatterns = new[]
        {
            @"Microsoft\.NETCore\.App[\\/](\d+\.\d+\.\d+)[\\/]",
            @"File version:\s*(\d+\.\d+\.\d+(?:\.\d+)?)",
            @"Image version:\s*(\d+\.\d+\.\d+(?:\.\d+)?)"
        };

        foreach (var command in new[] { "lmv m coreclr", "lmv m clr" })
        {
            try
            {
                var output = ExecuteCommandCore(context, command);
                foreach (var pattern in versionPatterns)
                {
                    var match = Regex.Match(output, pattern, RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[WinDbg] Failed to detect runtime version using {Command}", command);
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the local symbol cache directory from a WinDbg symbol path string.
    /// </summary>
    /// <param name="symbolPath">The symbol path to inspect.</param>
    /// <returns>The local cache directory when present; otherwise <c>null</c>.</returns>
    private static string? TryExtractSymbolCacheDirectory(string symbolPath)
    {
        foreach (var segment in symbolPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!segment.StartsWith("srv*", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = segment.Split('*', StringSplitOptions.None);
            if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                return parts[1];
            }
        }

        return null;
    }

    /// <summary>
    /// Builds a mapping from OS thread ID to WinDbg debugger thread index using <c>~</c> output.
    /// </summary>
    /// <returns>The thread mapping extracted from the current dump context.</returns>
    internal Dictionary<uint, int> BuildThreadIdToDebuggerThreadIndexMap()
        => BuildThreadIdToDebuggerThreadIndexMap(GetCurrentContext());

    /// <summary>
    /// Builds a mapping from OS thread ID to WinDbg debugger thread index using <c>~</c> output.
    /// </summary>
    /// <param name="context">The context whose thread list should be parsed.</param>
    /// <returns>The thread mapping extracted from the current dump context.</returns>
    private Dictionary<uint, int> BuildThreadIdToDebuggerThreadIndexMap(WinDbgEngineContext context)
    {
        var result = new Dictionary<uint, int>();
        if (!context.IsDumpOpen)
        {
            return result;
        }

        var threadsOutput = ExecuteCommandCore(context, "~");
        var lines = threadsOutput.Split('\n');
        foreach (var line in lines)
        {
            var match = Regex.Match(
                line,
                @"^\s*[.#]?\s*(\d+)\s+Id:\s*[0-9a-fA-F]+\.([0-9a-fA-F]+)",
                RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            if (int.TryParse(match.Groups[1].Value, out var debuggerThreadIndex) &&
                uint.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.HexNumber, null, out var osThreadId))
            {
                result[osThreadId] = debuggerThreadIndex;
            }
        }

        return result;
    }

    /// <summary>
    /// Parses WinDbg stack output to extract frame indices and child stack pointers.
    /// </summary>
    /// <param name="stackOutput">The raw <c>kv</c> output.</param>
    /// <returns>Frame indices paired with their child stack pointers.</returns>
    internal static List<(int FrameIndex, ulong StackPointer)> ParseWinDbgFrameStackPointers(string stackOutput)
    {
        var result = new List<(int FrameIndex, ulong StackPointer)>();
        var lines = stackOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var match = Regex.Match(
                line,
                @"^\s*([0-9a-fA-F]+)\s+([0-9a-fA-F`]+)\s+[0-9a-fA-F`]+",
                RegexOptions.IgnoreCase);
            if (!match.Success ||
                !int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var frameIndex) ||
                !ulong.TryParse(match.Groups[2].Value.Replace("`", string.Empty), System.Globalization.NumberStyles.HexNumber, null, out var stackPointer))
            {
                continue;
            }

            result.Add((frameIndex, stackPointer));
        }

        return result;
    }

    /// <summary>
    /// Parses WinDbg register output into a structured register set.
    /// </summary>
    /// <param name="output">The raw register output.</param>
    /// <returns>The parsed register set, or <c>null</c> when no useful registers were present.</returns>
    internal static Analysis.ClrRegisterSet? ParseWinDbgRegisters(string output)
    {
        var values = ParseWinDbgRegisterValues(output);
        if (values.Count == 0)
        {
            return null;
        }

        var registers = new Analysis.ClrRegisterSet
        {
            GeneralPurpose = new Dictionary<string, ulong>()
        };

        foreach (var (name, valueString) in values)
        {
            if (!ulong.TryParse(valueString.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var value))
            {
                continue;
            }

            switch (name)
            {
                case "rbp":
                    registers.FramePointer = value;
                    break;
                case "rsp":
                    registers.StackPointer = value;
                    break;
                case "rip":
                    registers.ProgramCounter = value;
                    break;
                case "rflags":
                case "efl":
                    registers.StatusRegister = (uint)value;
                    break;
                default:
                    registers.GeneralPurpose[name] = value;
                    break;
            }
        }

        return registers;
    }

    /// <summary>
    /// Parses WinDbg register output into normalized lower-case name/value pairs.
    /// </summary>
    /// <param name="output">The raw register output.</param>
    /// <returns>The normalized register map.</returns>
    internal static Dictionary<string, string> ParseWinDbgRegisterValues(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(output, @"\b([a-z][a-z0-9]*)=([0-9a-fA-F`]+)", RegexOptions.IgnoreCase))
        {
            var name = match.Groups[1].Value.ToLowerInvariant();
            var value = match.Groups[2].Value.Replace("`", string.Empty);
            result[name] = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value : $"0x{value}";
        }

        return result;
    }

    /// <summary>
    /// Detects if the currently open dump is a .NET dump by checking loaded modules.
    /// </summary>
    /// <returns>True if .NET runtime modules are found; otherwise, false.</returns>
    private bool DetectDotNetDumpCore(WinDbgEngineContext context)
    {
        try
        {
            var moduleList = ExecuteCommandCore(context, "lm");
            return IsDotNetModuleList(moduleList);
        }
        catch
        {
            // If we can't get module list, assume not .NET
            return false;
        }
    }

    /// <summary>
    /// Detects whether the active debugger context currently points at a managed dump.
    /// </summary>
    /// <returns><c>true</c> when .NET runtime modules are present; otherwise <c>false</c>.</returns>
    private bool DetectDotNetDump() => DetectDotNetDumpCore(GetCurrentContext());

    /// <summary>
    /// Determines whether a WinDbg <c>lm</c> module list indicates a .NET dump.
    /// </summary>
    /// <param name="moduleList">The raw output from the <c>lm</c> command.</param>
    /// <returns><c>true</c> if known .NET runtime indicators are present; otherwise, <c>false</c>.</returns>
    internal static bool IsDotNetModuleList(string? moduleList)
    {
        if (string.IsNullOrWhiteSpace(moduleList))
        {
            return false;
        }

        // Check for .NET Core/.NET 5+ runtime (most common modern case)
        if (moduleList.Contains("coreclr", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check for .NET Framework runtime - look for clr module name with word boundary
        // Pattern: "clr " at start of module name or " clr " as whole word
        // The lm output format is: "start end module_name"
        // We need to avoid false positives like "aclr" or "clrjit" (handled separately)
        var lines = moduleList.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(
                line,
                @"\bclr\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                // Make sure it's not clrjit (which is a separate module)
                if (!line.Contains("clrjit", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // Check for other .NET indicators
        var dotNetIndicators = new[]
        {
            "mscorwks",      // .NET Framework 2.0
            "clrjit",        // JIT compiler (present in both Core and Framework)
            "hostpolicy",    // .NET Core host
            "hostfxr",       // .NET Core framework resolver
            "System.Private.CoreLib", // .NET Core BCL
        };

        foreach (var indicator in dotNetIndicators)
        {
            if (moduleList.Contains(indicator, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }



    /// <summary>
    /// Releases all resources used by the <see cref="WinDbgManager"/>.
    /// </summary>
    /// <remarks>
    /// This method closes any open dumps and releases COM interfaces.
    /// It is safe to call this method multiple times.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        WinDbgStaDispatcher? dispatcher;
        WinDbgEngineContext context;

        lock (_executionLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            dispatcher = _dispatcher;
            context = _context;
            _dispatcher = null;
            _context = new WinDbgEngineContext();
        }

        try
        {
            if (dispatcher != null)
            {
                try
                {
                    var cleanupTask = dispatcher.InvokeAsync(() =>
                    {
                        if (context.IsDumpOpen)
                        {
                            CloseDumpCore(context);
                        }

                        ReleaseContextResources(context);
                        return true;
                    });

                    cleanupTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Best-effort only. We still dispose the dispatcher below.
                }

                dispatcher.Dispose();
            }
        }
        catch
        {
            // Never throw from Dispose.
        }
        finally
        {
            ReleaseContextResources(context);
        }
    }

    /// <summary>
    /// Asynchronously releases all resources used by the <see cref="WinDbgManager"/>.
    /// </summary>
    /// <remarks>
    /// This method provides async disposal for ASP.NET Core scenarios.
    /// For WinDbg, the disposal is synchronous since COM operations don't have async variants,
    /// but this method is provided for consistency with the interface contract.
    /// </remarks>
    /// <returns>A ValueTask representing the asynchronous dispose operation.</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Releases the COM resources held by a debugger context.
    /// </summary>
    /// <param name="context">The context whose resources should be released.</param>
    private static void ReleaseContextResources(WinDbgEngineContext context)
    {
        try
        {
            if (context.Client != null)
            {
                Marshal.ReleaseComObject(context.Client);
                context.Client = null;
            }
        }
        catch
        {
            // Best-effort release only.
        }

        try
        {
            if (context.Control != null)
            {
                Marshal.ReleaseComObject(context.Control);
                context.Control = null;
            }
        }
        catch
        {
            // Best-effort release only.
        }

        try
        {
            if (context.OutputCallbacksPtr != IntPtr.Zero)
            {
                Marshal.Release(context.OutputCallbacksPtr);
                context.OutputCallbacksPtr = IntPtr.Zero;
            }
        }
        catch
        {
            // Best-effort release only.
        }

        context.OutputCallbacks = null;
        context.IsDumpOpen = false;
        context.IsSosLoaded = false;
        context.IsDotNetDump = false;
        if (!string.IsNullOrWhiteSpace(context.CurrentDumpPath))
        {
            ClearObjectInspectionCache();
        }
        context.CurrentDumpPath = null;
        context.CurrentExecutablePath = null;
        context.DetectedRuntimeVersion = null;
    }

    /// <summary>
    /// Holds the mutable DbgEng state for one debugger generation.
    /// </summary>
    private sealed class WinDbgEngineContext
    {
        /// <summary>
        /// Gets or sets the main debugger client COM object.
        /// </summary>
        public IDebugClient? Client { get; set; }

        /// <summary>
        /// Gets or sets the debugger control COM object.
        /// </summary>
        public IDebugControl? Control { get; set; }

        /// <summary>
        /// Gets or sets the output-callback receiver bound to the current debugger generation.
        /// </summary>
        public OutputCallbacks? OutputCallbacks { get; set; }

        /// <summary>
        /// Gets or sets the COM interface pointer for the output callbacks.
        /// </summary>
        public IntPtr OutputCallbacksPtr { get; set; }

        /// <summary>
        /// Gets or sets the currently open dump path.
        /// </summary>
        public string? CurrentDumpPath { get; set; }

        /// <summary>
        /// Gets or sets the standalone executable path associated with the current dump.
        /// </summary>
        public string? CurrentExecutablePath { get; set; }

        /// <summary>
        /// Gets or sets the last symbol path applied to the debugger.
        /// </summary>
        public string? LastSymbolPath { get; set; }

        /// <summary>
        /// Gets or sets the local symbol cache directory extracted from the symbol path.
        /// </summary>
        public string? SymbolCacheDirectory { get; set; }

        /// <summary>
        /// Gets or sets the detected runtime version for the current dump.
        /// </summary>
        public string? DetectedRuntimeVersion { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether a dump is currently open.
        /// </summary>
        public bool IsDumpOpen { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether SOS has been loaded successfully.
        /// </summary>
        public bool IsSosLoaded { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the current dump is managed.
        /// </summary>
        public bool IsDotNetDump { get; set; }

        /// <summary>
        /// Gets a value indicating whether the debugger context has initialized COM objects.
        /// </summary>
        public bool IsInitialized => Client != null && Control != null;

        /// <summary>
        /// Captures the fields that must survive a recovery cycle.
        /// </summary>
        /// <returns>The recovery snapshot for this debugger generation.</returns>
        public WinDbgRecoverySnapshot CreateRecoverySnapshot()
        {
            return new WinDbgRecoverySnapshot
            {
                DumpPath = CurrentDumpPath,
                ExecutablePath = CurrentExecutablePath,
                LastSymbolPath = LastSymbolPath,
                SymbolCacheDirectory = SymbolCacheDirectory,
                DetectedRuntimeVersion = DetectedRuntimeVersion,
                WasDumpOpen = IsDumpOpen
            };
        }
    }

    /// <summary>
    /// Captures the subset of debugger state that recovery must preserve across engine replacement.
    /// </summary>
    private sealed class WinDbgRecoverySnapshot
    {
        /// <summary>
        /// Gets or sets the dump path that was open when failure happened.
        /// </summary>
        public string? DumpPath { get; set; }

        /// <summary>
        /// Gets or sets the standalone executable path associated with the dump.
        /// </summary>
        public string? ExecutablePath { get; set; }

        /// <summary>
        /// Gets or sets the most recent symbol path applied to the debugger.
        /// </summary>
        public string? LastSymbolPath { get; set; }

        /// <summary>
        /// Gets or sets the symbol-cache directory extracted from that symbol path.
        /// </summary>
        public string? SymbolCacheDirectory { get; set; }

        /// <summary>
        /// Gets or sets the most recently detected runtime version.
        /// </summary>
        public string? DetectedRuntimeVersion { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether a dump was open when failure happened.
        /// </summary>
        public bool WasDumpOpen { get; set; }
    }

    /// <summary>
    /// Executes queued debugger work on a dedicated STA thread.
    /// </summary>
    private sealed class WinDbgStaDispatcher : IDisposable
    {
        /// <summary>
        /// Queue of work items waiting for execution on the dispatcher thread.
        /// </summary>
        private readonly BlockingCollection<Action> _workItems = new();

        /// <summary>
        /// Signaled once the STA thread has started and recorded its managed thread ID.
        /// </summary>
        private readonly ManualResetEventSlim _started = new();

        /// <summary>
        /// Dedicated STA thread that executes all queued work.
        /// </summary>
        private readonly Thread _thread;

        /// <summary>
        /// Managed thread ID of the dispatcher thread after startup.
        /// </summary>
        private int _threadId;

        /// <summary>
        /// Indicates whether the dispatcher has been disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="WinDbgStaDispatcher"/> class.
        /// </summary>
        /// <param name="name">Friendly thread name for diagnostics.</param>
        public WinDbgStaDispatcher(string name)
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = name
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _started.Wait();
        }

        /// <summary>
        /// Queues work for execution on the dispatcher thread.
        /// </summary>
        /// <typeparam name="T">The result type produced by the work item.</typeparam>
        /// <param name="action">The work item to execute.</param>
        /// <returns>A task that completes when the work item finishes.</returns>
        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(action);

            if (Thread.CurrentThread.ManagedThreadId == _threadId)
            {
                try
                {
                    return Task.FromResult(action());
                }
                catch (Exception ex)
                {
                    return Task.FromException<T>(ex);
                }
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _workItems.Add(() =>
            {
                try
                {
                    completion.SetResult(action());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });

            return completion.Task;
        }

        /// <summary>
        /// Releases the dispatcher queue and stops accepting new work.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _workItems.CompleteAdding();
            }
            catch
            {
                // Best-effort only.
            }

            try
            {
                _thread.Join(1000);
            }
            catch
            {
                // Best-effort only.
            }

            _started.Dispose();
            _workItems.Dispose();
        }

        /// <summary>
        /// Runs the dispatcher loop on the dedicated STA thread.
        /// </summary>
        private void Run()
        {
            _threadId = Thread.CurrentThread.ManagedThreadId;
            _started.Set();

            foreach (var workItem in _workItems.GetConsumingEnumerable())
            {
                workItem();
            }
        }
    }

}


/// <summary>
/// COM interface for receiving output from the debugger engine.
/// </summary>
/// <remarks>
/// This interface must be implemented to capture debugger output.
/// GUID: 4bf58045-d654-4c40-b0af-683090f356dc
/// </remarks>
[ComVisible(true)]
[Guid("4bf58045-d654-4c40-b0af-683090f356dc")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugOutputCallbacks
{
    /// <summary>
    /// Called by the debugger engine when output is generated.
    /// </summary>
    /// <param name="Mask">The output mask indicating the type of output.</param>
    /// <param name="Text">The output text.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    [PreserveSig]
    int Output(uint Mask, [MarshalAs(UnmanagedType.LPStr)] string Text);
}

/// <summary>
/// Implementation of <see cref="IDebugOutputCallbacks"/> that captures debugger output to a string.
/// </summary>
/// <remarks>
/// This class accumulates output in a thread-safe manner and provides methods
/// to retrieve and clear the accumulated output.
/// </remarks>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public class OutputCallbacks : IDebugOutputCallbacks
{

    /// <summary>
    /// StringBuilder for accumulating output text.
    /// </summary>
    private readonly StringBuilder _output = new();

    /// <summary>
    /// Lock object for thread-safe access to the output buffer.
    /// </summary>
    private readonly object _lock = new();



    /// <summary>
    /// Receives output from the debugger and appends it to the internal buffer.
    /// </summary>
    /// <param name="mask">The output mask indicating the type of output.</param>
    /// <param name="text">The output text to capture.</param>
    /// <returns>S_OK (0) to indicate success.</returns>
    /// <remarks>
    /// This method is called by the debugger engine on potentially different threads,
    /// so it uses locking to ensure thread safety.
    /// </remarks>
    public int Output(uint mask, string text)
    {
        // Use lock to ensure thread-safe access to the StringBuilder
        lock (_lock)
        {
            _output.Append(text);
        }

        // Return S_OK to indicate success
        return 0;
    }



    /// <summary>
    /// Retrieves the accumulated output text.
    /// </summary>
    /// <returns>The complete output captured since the last clear operation.</returns>
    /// <remarks>
    /// This method is thread-safe and can be called while output is being captured.
    /// </remarks>
    public string GetOutput()
    {
        // Use lock to ensure thread-safe access to the StringBuilder
        lock (_lock)
        {
            return _output.ToString();
        }
    }

    /// <summary>
    /// Clears the accumulated output text.
    /// </summary>
    /// <remarks>
    /// This method should be called before executing a new command to avoid
    /// mixing output from different commands.
    /// </remarks>
    public void ClearOutput()
    {
        // Use lock to ensure thread-safe access to the StringBuilder
        lock (_lock)
        {
            _output.Clear();
        }
    }

}
