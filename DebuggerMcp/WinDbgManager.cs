using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

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
public class WinDbgManager : IDebuggerManager
{

    /// <summary>
    /// The main debugger client interface.
    /// </summary>
    private IDebugClient? _client;

    /// <summary>
    /// The debugger control interface for executing commands.
    /// </summary>
    private IDebugControl? _control;

    /// <summary>
    /// Callbacks for capturing debugger output.
    /// </summary>
    private OutputCallbacks? _outputCallbacks;

    /// <summary>
    /// COM interface pointer for output callbacks (must be released on disposal).
    /// </summary>
    private IntPtr _outputCallbacksPtr;

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
    /// The path to the currently open dump file.
    /// </summary>
    private string? _currentDumpPath;

    /// <summary>
    /// Gets a value indicating whether the debugger engine has been initialized.
    /// </summary>
    /// <value>
    /// <c>true</c> if both the client and control interfaces are available; otherwise, <c>false</c>.
    /// </value>
    public bool IsInitialized => _client != null && _control != null;

    /// <summary>
    /// Gets a value indicating whether a dump file is currently open.
    /// </summary>
    /// <value>
    /// <c>true</c> if a dump file is open and ready for analysis; otherwise, <c>false</c>.
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
    /// <c>true</c> if the dump contains .NET runtime modules (CoreCLR or CLR); otherwise, <c>false</c>.
    /// </value>
    public bool IsDotNetDump { get; private set; }

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
    public WinDbgManager()
    {
        // Initialization is deferred to the InitializeAsync() method
    }



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
        // Check if already initialized to avoid redundant initialization
        if (IsInitialized)
            return Task.CompletedTask;

        try
        {
            // Create the debug client using the native DebugCreate function
            var iid = DbgEng.IID_IDebugClient;
            int hr = DbgEng.DebugCreate(ref iid, out object clientObj);

            // Check if creation succeeded
            if (hr != 0)
            {
                throw new COMException($"Failed to create IDebugClient. HRESULT: 0x{hr:X8}", hr);
            }

            // Cast to the IDebugClient interface
            _client = (IDebugClient)clientObj;

            // Query for IDebugControl interface from the client
            // Both interfaces point to the same underlying COM object
            _control = (IDebugControl)_client;

            // Set up output callbacks to capture debugger output
            _outputCallbacks = new OutputCallbacks();
            _outputCallbacksPtr = Marshal.GetComInterfaceForObject(_outputCallbacks, typeof(IDebugOutputCallbacks));
            _client.SetOutputCallbacks(_outputCallbacksPtr);

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            // Wrap any exceptions in InvalidOperationException for consistent error handling
            throw new InvalidOperationException($"Failed to initialize WinDbg Manager: {ex.Message}", ex);
        }
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
        // Validate that the manager is initialized
        if (!IsInitialized)
        {
            throw new InvalidOperationException("WinDbg Manager is not initialized");
        }

        // Check if a dump is already open
        if (IsDumpOpen)
        {
            throw new InvalidOperationException("A dump file is already open. Close it first.");
        }

        // Validate that the dump file exists
        if (!File.Exists(dumpFilePath))
        {
            throw new FileNotFoundException($"Dump file not found: {dumpFilePath}");
        }

        try
        {
            // Clear any previous output to avoid confusion
            _outputCallbacks?.ClearOutput();

            // Open the dump file using the IDebugClient interface
            int hr = _client!.OpenDumpFile(dumpFilePath);

            // Check if opening succeeded
            if (hr != 0)
            {
                throw new COMException($"Failed to open dump file. HRESULT: 0x{hr:X8}", hr);
            }

            // Mark the dump as open and store the path
            IsDumpOpen = true;
            _currentDumpPath = dumpFilePath;

            // Wait for the debugger to process the dump
            // This is essential for the dump to be ready for commands
            hr = _control!.WaitForEvent(0, WaitForEventTimeoutMs);

            // Check if the wait succeeded
            if (hr != 0)
            {
                throw new COMException($"Failed to wait for event. HRESULT: 0x{hr:X8}", hr);
            }

            // Execute a simple command to verify the dump is ready
            ExecuteCommand(".echo Dump file opened successfully");

            // Detect if this is a .NET dump by checking loaded modules
            IsDotNetDump = DetectDotNetDump();
            if (IsDotNetDump)
            {
                try
                {
                    LoadSosExtension();
                }
                catch
                {
                    // SOS loading failed, but dump is still usable for native debugging
                    // Note: WinDbgManager doesn't have a logger - silently continue
                }
            }
        }
        catch (Exception ex)
        {
            // If opening failed, mark the dump as not open and reset all state
            IsDumpOpen = false;
            IsDotNetDump = false;
            _currentDumpPath = null;
            throw new InvalidOperationException($"Failed to open dump file: {ex.Message}", ex);
        }
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
        // Validate that the manager is initialized
        if (!IsInitialized)
        {
            throw new InvalidOperationException("WinDbg Manager is not initialized");
        }

        try
        {
            // Only attempt to close if a dump is actually open
            if (IsDumpOpen)
            {
                // End the session with DEBUG_END_PASSIVE flag
                // This flag indicates a passive end without terminating processes
                _client!.EndSession(DebugEndPassive);

                // Mark the dump as closed and reset all dump-specific state
                IsDumpOpen = false;
                IsSosLoaded = false;
                IsDotNetDump = false;
                _currentDumpPath = null;
            }
        }
        catch (Exception ex)
        {
            // Wrap any exceptions for consistent error handling
            throw new InvalidOperationException($"Failed to close dump: {ex.Message}", ex);
        }
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

        // Validate that the manager is initialized
        if (!IsInitialized)
        {
            throw new InvalidOperationException("WinDbg Manager is not initialized");
        }

        // Validate that a dump is open
        if (!IsDumpOpen)
        {
            throw new InvalidOperationException("No dump file is currently open");
        }

        // Execute command directly - caching removed as ClrMD handles most heavy operations
        return ExecuteCommandInternal(command);
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
        // Validate that the manager is initialized
        if (!IsInitialized)
        {
            throw new InvalidOperationException("WinDbg Manager is not initialized");
        }

        try
        {
            // Clear previous output to avoid mixing results from different commands
            _outputCallbacks?.ClearOutput();

            // Execute the command using IDebugControl.Execute
            // - DEBUG_OUTCTL_ALL_CLIENTS: Send output to all connected clients
            // - command: The command string to execute
            // - DEBUG_EXECUTE_DEFAULT: Use default execution flags
            int hr = _control!.Execute(
                DbgEngConstants.DEBUG_OUTCTL_ALL_CLIENTS,
                command,
                DbgEngConstants.DEBUG_EXECUTE_DEFAULT
            );

            // Check if execution succeeded
            if (hr != 0)
            {
                throw new COMException($"Failed to execute command. HRESULT: 0x{hr:X8}", hr);
            }

            // Retrieve the captured output from the callbacks
            var output = _outputCallbacks?.GetOutput() ?? string.Empty;

            return output;
        }
        catch (Exception ex)
        {
            // Provide context about which command failed
            throw new InvalidOperationException($"Failed to execute command '{command}': {ex.Message}", ex);
        }
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

        // Validate that the manager is initialized
        if (!IsInitialized)
        {
            throw new InvalidOperationException("WinDbg Manager is not initialized");
        }

        try
        {
            // Set the symbol path using .sympath command
            // Note: Using internal method because .sympath can be executed before a dump is open
            ExecuteCommandInternal($".sympath {symbolPath}");
        }
        catch (Exception ex)
        {
            // Wrap any exceptions for consistent error handling
            throw new InvalidOperationException($"Failed to configure symbol path: {ex.Message}", ex);
        }
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
        // Validate that the manager is initialized
        if (!IsInitialized)
        {
            throw new InvalidOperationException("WinDbg Manager is not initialized");
        }

        // Validate that a dump is open
        if (!IsDumpOpen)
        {
            throw new InvalidOperationException("No dump file is currently open");
        }

        // Idempotent: if SOS is already loaded, nothing to do
        if (IsSosLoaded)
        {
            return;
        }

        try
        {
            // Try to load SOS extension for CoreCLR (.NET Core/.NET 5+)
            // .loadby automatically finds the correct SOS.dll matching the runtime
            var result = ExecuteCommand(".loadby sos coreclr");

            // Check if CoreCLR loading failed
            var coreclrFailed = result.Contains("Unable to find module", StringComparison.OrdinalIgnoreCase) ||
                               result.Contains("error", StringComparison.OrdinalIgnoreCase);

            if (coreclrFailed)
            {
                // Fall back to CLR for .NET Framework dumps
                var clrResult = ExecuteCommand(".loadby sos clr");

                // Check if CLR loading also failed
                var clrFailed = clrResult.Contains("Unable to find module", StringComparison.OrdinalIgnoreCase) ||
                               clrResult.Contains("error", StringComparison.OrdinalIgnoreCase);

                if (clrFailed)
                {
                    throw new InvalidOperationException(
                        $"Failed to load SOS extension. CoreCLR result: {result.Trim()}. CLR result: {clrResult.Trim()}");
                }
            }

            // Verify SOS is actually loaded by running a simple command
            // In WinDbg, SOS commands use !prefix (e.g., !help, !threads) - not !sos prefix
            var verifyResult = ExecuteCommand("!help");
            if (verifyResult.Contains("No export", StringComparison.OrdinalIgnoreCase) ||
                verifyResult.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                verifyResult.Contains("Unrecognized command", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SOS extension loaded but commands are not available. Verify result: {verifyResult.Trim()}");
            }

            // Mark SOS as loaded only after verification
            IsSosLoaded = true;
        }
        catch (InvalidOperationException)
        {
            // Re-throw our own exceptions
            throw;
        }
        catch (Exception ex)
        {
            // Wrap other exceptions for consistent error handling
            throw new InvalidOperationException($"Failed to load SOS extension: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Detects if the currently open dump is a .NET dump by checking loaded modules.
    /// </summary>
    /// <returns>True if .NET runtime modules are found; otherwise, false.</returns>
    private bool DetectDotNetDump()
    {
        try
        {
            // Get list of loaded modules
            var moduleList = ExecuteCommand("lm");

            return IsDotNetModuleList(moduleList);
        }
        catch
        {
            // If we can't get module list, assume not .NET
            return false;
        }
    }

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
        // Avoid redundant disposal
        if (_disposed)
            return;

        try
        {
            // Close any open dump before disposing
            if (IsDumpOpen)
            {
                CloseDump();
            }

            // Release the COM interface for the client
            if (_client != null)
            {
                Marshal.ReleaseComObject(_client);
                _client = null;
            }

            // Release the COM interface for the control
            if (_control != null)
            {
                Marshal.ReleaseComObject(_control);
                _control = null;
            }

            // Release the COM interface pointer for output callbacks
            if (_outputCallbacksPtr != IntPtr.Zero)
            {
                Marshal.Release(_outputCallbacksPtr);
                _outputCallbacksPtr = IntPtr.Zero;
            }

            // Clear the output callbacks reference
            _outputCallbacks = null;
        }
        catch
        {
            // Silently ignore disposal errors - don't throw from Dispose
        }
        finally
        {
            // Mark as disposed regardless of success
            _disposed = true;
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
