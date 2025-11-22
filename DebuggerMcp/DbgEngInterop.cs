using System.Runtime.InteropServices;
using System.Text;

namespace DebuggerMcp;

/// <summary>
/// Provides COM interop definitions for the Windows Debugger Engine (DbgEng) API.
/// This file contains interfaces, structures, and constants required to interact with WinDbg programmatically.
/// </summary>
/// <remarks>
/// The DbgEng API is a COM-based interface that allows programmatic control of the Windows debugger.
/// All interfaces and structures in this file are direct mappings to the native DbgEng API.
/// </remarks>


/// <summary>
/// Represents the main client interface for the debugger engine.
/// This interface provides methods for controlling debugging sessions, opening dumps, and managing processes.
/// </summary>
/// <remarks>
/// IDebugClient is the primary interface for interacting with the debugger engine.
/// It must be created using the DebugCreate function from dbgeng.dll.
/// GUID: 27fe5639-8407-4f47-8364-ee118fb08ac8
/// </remarks>
[ComImport, Guid("27fe5639-8407-4f47-8364-ee118fb08ac8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugClient
{
    /// <summary>
    /// Attaches the debugger engine to a kernel target.
    /// </summary>
    /// <param name="Flags">Flags that control the attachment behavior.</param>
    /// <param name="ConnectOptions">Connection string for the kernel target.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    [PreserveSig]
    int AttachKernel(uint Flags, [MarshalAs(UnmanagedType.LPStr)] string? ConnectOptions);
    
    /// <summary>
    /// Retrieves the current kernel connection options.
    /// </summary>
    [PreserveSig]
    int GetKernelConnectionOptions([Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder Buffer, int BufferSize, out uint OptionsSize);
    
    /// <summary>
    /// Sets the kernel connection options.
    /// </summary>
    [PreserveSig]
    int SetKernelConnectionOptions([MarshalAs(UnmanagedType.LPStr)] string Options);
    
    /// <summary>
    /// Starts a process server for remote debugging.
    /// </summary>
    [PreserveSig]
    int StartProcessServer(uint Flags, [MarshalAs(UnmanagedType.LPStr)] string Options, IntPtr Reserved);
    
    /// <summary>
    /// Connects to a remote process server.
    /// </summary>
    [PreserveSig]
    int ConnectProcessServer([MarshalAs(UnmanagedType.LPStr)] string RemoteOptions, out ulong Server);
    
    /// <summary>
    /// Disconnects from a remote process server.
    /// </summary>
    [PreserveSig]
    int DisconnectProcessServer(ulong Server);
    
    /// <summary>
    /// Retrieves the list of running process IDs on the target system.
    /// </summary>
    [PreserveSig]
    int GetRunningProcessSystemIds(ulong Server, [Out, MarshalAs(UnmanagedType.LPArray)] uint[]? Ids, uint Count, out uint ActualCount);
    
    /// <summary>
    /// Gets the process ID for a process with a specific executable name.
    /// </summary>
    [PreserveSig]
    int GetRunningProcessSystemIdByExecutableName(ulong Server, [MarshalAs(UnmanagedType.LPStr)] string ExeName, uint Flags, out uint Id);
    
    /// <summary>
    /// Retrieves description information about a running process.
    /// </summary>
    [PreserveSig]
    int GetRunningProcessDescription(ulong Server, uint SystemId, uint Flags, [Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? ExeName, uint ExeNameSize, out uint ActualExeNameSize, [Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? Description, uint DescriptionSize, out uint ActualDescriptionSize);
    
    /// <summary>
    /// Attaches the debugger to a running process.
    /// </summary>
    [PreserveSig]
    int AttachProcess(ulong Server, uint ProcessId, uint AttachFlags);
    
    /// <summary>
    /// Creates a new process under the debugger.
    /// </summary>
    [PreserveSig]
    int CreateProcess(ulong Server, [MarshalAs(UnmanagedType.LPStr)] string CommandLine, uint CreateFlags);
    
    /// <summary>
    /// Creates a new process and attaches to it, or attaches to an existing process.
    /// </summary>
    [PreserveSig]
    int CreateProcessAndAttach(ulong Server, [MarshalAs(UnmanagedType.LPStr)] string? CommandLine, uint CreateFlags, uint ProcessId, uint AttachFlags);
    
    /// <summary>
    /// Gets the current process options.
    /// </summary>
    [PreserveSig]
    int GetProcessOptions(out uint Options);
    
    /// <summary>
    /// Adds process options to the current set.
    /// </summary>
    [PreserveSig]
    int AddProcessOptions(uint Options);
    
    /// <summary>
    /// Removes process options from the current set.
    /// </summary>
    [PreserveSig]
    int RemoveProcessOptions(uint Options);
    
    /// <summary>
    /// Sets the process options, replacing the current set.
    /// </summary>
    [PreserveSig]
    int SetProcessOptions(uint Options);
    
    /// <summary>
    /// Opens a crash dump file for debugging.
    /// </summary>
    /// <param name="DumpFile">Full path to the dump file to open.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    /// <remarks>
    /// This is one of the most commonly used methods for post-mortem debugging.
    /// The dump file must be in a format recognized by the debugger (e.g., .dmp, .mdmp).
    /// </remarks>
    [PreserveSig]
    int OpenDumpFile([MarshalAs(UnmanagedType.LPStr)] string DumpFile);
    
    /// <summary>
    /// Writes the current debugging session to a dump file.
    /// </summary>
    [PreserveSig]
    int WriteDumpFile([MarshalAs(UnmanagedType.LPStr)] string DumpFile, uint Qualifier);
    
    /// <summary>
    /// Connects to a debugging session.
    /// </summary>
    [PreserveSig]
    int ConnectSession(uint Flags, uint HistoryLimit);
    
    /// <summary>
    /// Starts a debugging server.
    /// </summary>
    [PreserveSig]
    int StartServer([MarshalAs(UnmanagedType.LPStr)] string Options);
    
    /// <summary>
    /// Outputs information about available debugging servers.
    /// </summary>
    [PreserveSig]
    int OutputServers(uint OutputControl, [MarshalAs(UnmanagedType.LPStr)] string Machine, uint Flags);
    
    /// <summary>
    /// Terminates all processes being debugged.
    /// </summary>
    [PreserveSig]
    int TerminateProcesses();
    
    /// <summary>
    /// Detaches from all processes being debugged.
    /// </summary>
    [PreserveSig]
    int DetachProcesses();
    
    /// <summary>
    /// Ends the current debugging session.
    /// </summary>
    /// <param name="Flags">Flags controlling how the session is ended.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    [PreserveSig]
    int EndSession(uint Flags);
    
    /// <summary>
    /// Gets the exit code of the debugged process.
    /// </summary>
    [PreserveSig]
    int GetExitCode(out uint Code);
    
    /// <summary>
    /// Dispatches callbacks for the specified timeout period.
    /// </summary>
    [PreserveSig]
    int DispatchCallbacks(uint Timeout);
    
    /// <summary>
    /// Exits the callback dispatch loop.
    /// </summary>
    [PreserveSig]
    int ExitDispatch([In, MarshalAs(UnmanagedType.Interface)] IDebugClient Client);
    
    /// <summary>
    /// Creates a new client object.
    /// </summary>
    [PreserveSig]
    int CreateClient(out IDebugClient Client);
    
    /// <summary>
    /// Gets the current input callbacks.
    /// </summary>
    [PreserveSig]
    int GetInputCallbacks(out IntPtr Callbacks);
    
    /// <summary>
    /// Sets input callbacks for receiving input from the debugger.
    /// </summary>
    [PreserveSig]
    int SetInputCallbacks(IntPtr Callbacks);
    
    /// <summary>
    /// Gets the current output callbacks.
    /// </summary>
    [PreserveSig]
    int GetOutputCallbacks(out IntPtr Callbacks);
    
    /// <summary>
    /// Sets output callbacks for receiving output from the debugger.
    /// </summary>
    /// <param name="Callbacks">Pointer to the IDebugOutputCallbacks interface.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    /// <remarks>
    /// Output callbacks are essential for capturing debugger output programmatically.
    /// The callbacks object must implement IDebugOutputCallbacks interface.
    /// </remarks>
    [PreserveSig]
    int SetOutputCallbacks(IntPtr Callbacks);
    
    /// <summary>
    /// Gets the current output mask.
    /// </summary>
    [PreserveSig]
    int GetOutputMask(out uint Mask);
    
    /// <summary>
    /// Sets the output mask to filter debugger output.
    /// </summary>
    [PreserveSig]
    int SetOutputMask(uint Mask);
    
    /// <summary>
    /// Gets the output mask for another client.
    /// </summary>
    [PreserveSig]
    int GetOtherOutputMask([In, MarshalAs(UnmanagedType.Interface)] IDebugClient Client, out uint Mask);
    
    /// <summary>
    /// Sets the output mask for another client.
    /// </summary>
    [PreserveSig]
    int SetOtherOutputMask([In, MarshalAs(UnmanagedType.Interface)] IDebugClient Client, uint Mask);
    
    /// <summary>
    /// Gets the output width in columns.
    /// </summary>
    [PreserveSig]
    int GetOutputWidth(out uint Columns);
    
    /// <summary>
    /// Sets the output width in columns.
    /// </summary>
    [PreserveSig]
    int SetOutputWidth(uint Columns);
    
    /// <summary>
    /// Gets the output line prefix string.
    /// </summary>
    [PreserveSig]
    int GetOutputLinePrefix([Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? Buffer, int BufferSize, out uint PrefixSize);
    
    /// <summary>
    /// Sets the output line prefix string.
    /// </summary>
    [PreserveSig]
    int SetOutputLinePrefix([MarshalAs(UnmanagedType.LPStr)] string? Prefix);
    
    /// <summary>
    /// Gets the identity string of the client.
    /// </summary>
    [PreserveSig]
    int GetIdentity([Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? Buffer, int BufferSize, out uint IdentitySize);
    
    /// <summary>
    /// Outputs the identity of the client.
    /// </summary>
    [PreserveSig]
    int OutputIdentity(uint OutputControl, uint Flags, [MarshalAs(UnmanagedType.LPStr)] string Format);
    
    /// <summary>
    /// Gets the current event callbacks.
    /// </summary>
    [PreserveSig]
    int GetEventCallbacks(out IntPtr Callbacks);
    
    /// <summary>
    /// Sets event callbacks for receiving debugger events.
    /// </summary>
    [PreserveSig]
    int SetEventCallbacks(IntPtr Callbacks);
    
    /// <summary>
    /// Flushes any pending callbacks.
    /// </summary>
    [PreserveSig]
    int FlushCallbacks();
}

/// <summary>
/// Represents the control interface for the debugger engine.
/// This interface provides methods for executing commands, controlling execution, and querying debugger state.
/// </summary>
/// <remarks>
/// IDebugControl is obtained by querying IDebugClient for this interface.
/// It provides the primary methods for command execution and debugger control.
/// GUID: 5182e668-105e-416e-ad92-24ef800424ba
/// </remarks>
[ComImport, Guid("5182e668-105e-416e-ad92-24ef800424ba"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugControl
{
    // Note: This interface has been simplified to include only the most commonly used methods.
    // The full IDebugControl interface has over 100 methods.
    
    /// <summary>
    /// Checks if an interrupt has been requested.
    /// </summary>
    [PreserveSig]
    int GetInterrupt();
    
    /// <summary>
    /// Requests an interrupt of the debugger.
    /// </summary>
    [PreserveSig]
    int SetInterrupt(uint Flags);
    
    /// <summary>
    /// Gets the interrupt timeout in seconds.
    /// </summary>
    [PreserveSig]
    int GetInterruptTimeout(out uint Seconds);
    
    /// <summary>
    /// Sets the interrupt timeout in seconds.
    /// </summary>
    [PreserveSig]
    int SetInterruptTimeout(uint Seconds);
    
    /// <summary>
    /// Gets information about the current log file.
    /// </summary>
    [PreserveSig]
    int GetLogFile([Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? Buffer, int BufferSize, out uint FileSize, out int Append);
    
    /// <summary>
    /// Opens a log file for debugger output.
    /// </summary>
    [PreserveSig]
    int OpenLogFile([MarshalAs(UnmanagedType.LPStr)] string File, int Append);
    
    /// <summary>
    /// Closes the current log file.
    /// </summary>
    [PreserveSig]
    int CloseLogFile();
    
    /// <summary>
    /// Gets the current log mask.
    /// </summary>
    [PreserveSig]
    int GetLogMask(out uint Mask);
    
    /// <summary>
    /// Sets the log mask to filter logged output.
    /// </summary>
    [PreserveSig]
    int SetLogMask(uint Mask);
    
    /// <summary>
    /// Reads input from the debugger.
    /// </summary>
    [PreserveSig]
    int Input([Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder Buffer, int BufferSize, out uint InputSize);
    
    /// <summary>
    /// Returns input to the debugger.
    /// </summary>
    [PreserveSig]
    int ReturnInput([MarshalAs(UnmanagedType.LPStr)] string Buffer);
    
    /// <summary>
    /// Outputs text to the debugger.
    /// </summary>
    /// <param name="Mask">Output mask specifying the type of output.</param>
    /// <param name="Format">Format string for the output.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    [PreserveSig]
    int Output(uint Mask, [MarshalAs(UnmanagedType.LPStr)] string Format);
    
    /// <summary>
    /// Outputs formatted text with a variable argument list.
    /// </summary>
    [PreserveSig]
    int OutputVaList(uint Mask, [MarshalAs(UnmanagedType.LPStr)] string Format, IntPtr Args);
    
    /// <summary>
    /// Outputs text with specific output control flags.
    /// </summary>
    [PreserveSig]
    int ControlledOutput(uint OutputControl, uint Mask, [MarshalAs(UnmanagedType.LPStr)] string Format);
    
    /// <summary>
    /// Outputs formatted text with output control and variable arguments.
    /// </summary>
    [PreserveSig]
    int ControlledOutputVaList(uint OutputControl, uint Mask, [MarshalAs(UnmanagedType.LPStr)] string Format, IntPtr Args);
    
    /// <summary>
    /// Outputs a prompt to the debugger.
    /// </summary>
    [PreserveSig]
    int OutputPrompt(uint OutputControl, [MarshalAs(UnmanagedType.LPStr)] string? Format);
    
    /// <summary>
    /// Outputs a prompt with variable arguments.
    /// </summary>
    [PreserveSig]
    int OutputPromptVaList(uint OutputControl, [MarshalAs(UnmanagedType.LPStr)] string Format, IntPtr Args);
    
    /// <summary>
    /// Gets the current prompt text.
    /// </summary>
    [PreserveSig]
    int GetPromptText([Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? Buffer, int BufferSize, out uint TextSize);
    
    /// <summary>
    /// Outputs the current state of the debugger.
    /// </summary>
    [PreserveSig]
    int OutputCurrentState(uint OutputControl, uint Flags);
    
    /// <summary>
    /// Outputs version information about the debugger.
    /// </summary>
    [PreserveSig]
    int OutputVersionInformation(uint OutputControl);
    
    /// <summary>
    /// Gets the handle for event notification.
    /// </summary>
    [PreserveSig]
    int GetNotifyEventHandle(out ulong Handle);
    
    /// <summary>
    /// Sets the handle for event notification.
    /// </summary>
    [PreserveSig]
    int SetNotifyEventHandle(ulong Handle);
    
    /// <summary>
    /// Assembles an instruction at the specified address.
    /// </summary>
    [PreserveSig]
    int Assemble(ulong Offset, [MarshalAs(UnmanagedType.LPStr)] string Instr, out ulong EndOffset);
    
    /// <summary>
    /// Disassembles an instruction at the specified address.
    /// </summary>
    [PreserveSig]
    int Disassemble(ulong Offset, uint Flags, [Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? Buffer, int BufferSize, out uint DisassemblySize, out ulong EndOffset);
    
    /// <summary>
    /// Gets the effective offset for disassembly.
    /// </summary>
    [PreserveSig]
    int GetDisassembleEffectiveOffset(out ulong Offset);
    
    /// <summary>
    /// Outputs disassembly starting at the specified offset.
    /// </summary>
    [PreserveSig]
    int OutputDisassembly(uint OutputControl, ulong Offset, uint Flags, out ulong EndOffset);
    
    /// <summary>
    /// Outputs multiple lines of disassembly.
    /// </summary>
    [PreserveSig]
    int OutputDisassemblyLines(uint OutputControl, uint PreviousLines, uint TotalLines, ulong Offset, uint Flags, out uint OffsetLine, out ulong StartOffset, out ulong EndOffset, [Out, MarshalAs(UnmanagedType.LPArray)] ulong[]? LineOffsets);
    
    /// <summary>
    /// Gets the address of an instruction near the specified offset.
    /// </summary>
    [PreserveSig]
    int GetNearInstruction(ulong Offset, int Delta, out ulong NearOffset);
    
    /// <summary>
    /// Gets the current stack trace.
    /// </summary>
    [PreserveSig]
    int GetStackTrace(ulong FrameOffset, ulong StackOffset, ulong InstructionOffset, [Out, MarshalAs(UnmanagedType.LPArray)] DEBUG_STACK_FRAME[]? Frames, int FramesSize, out uint FramesFilled);
    
    /// <summary>
    /// Gets the return offset for the current function.
    /// </summary>
    [PreserveSig]
    int GetReturnOffset(out ulong Offset);
    
    /// <summary>
    /// Outputs a stack trace.
    /// </summary>
    [PreserveSig]
    int OutputStackTrace(uint OutputControl, [In, MarshalAs(UnmanagedType.LPArray)] DEBUG_STACK_FRAME[]? Frames, int FramesSize, uint Flags);
    
    /// <summary>
    /// Gets the type of the current debuggee (user-mode or kernel-mode).
    /// </summary>
    [PreserveSig]
    int GetDebuggeeType(out uint Class, out uint Qualifier);
    
    /// <summary>
    /// Gets the actual processor type of the target system.
    /// </summary>
    [PreserveSig]
    int GetActualProcessorType(out uint Type);
    
    /// <summary>
    /// Gets the executing processor type.
    /// </summary>
    [PreserveSig]
    int GetExecutingProcessorType(out uint Type);
    
    /// <summary>
    /// Gets the number of possible executing processor types.
    /// </summary>
    [PreserveSig]
    int GetNumberPossibleExecutingProcessorTypes(out uint Number);
    
    /// <summary>
    /// Gets the list of possible executing processor types.
    /// </summary>
    [PreserveSig]
    int GetPossibleExecutingProcessorTypes(uint Start, uint Count, [Out, MarshalAs(UnmanagedType.LPArray)] uint[]? Types);
    
    /// <summary>
    /// Gets the number of processors in the target system.
    /// </summary>
    [PreserveSig]
    int GetNumberProcessors(out uint Number);
    
    /// <summary>
    /// Gets system version information.
    /// </summary>
    [PreserveSig]
    int GetSystemVersion(out uint PlatformId, out uint Major, out uint Minor, [Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? ServicePackString, int ServicePackStringSize, out uint ServicePackStringUsed, out uint ServicePackNumber, [Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? BuildString, int BuildStringSize, out uint BuildStringUsed);
    
    /// <summary>
    /// Gets the page size of the target system.
    /// </summary>
    [PreserveSig]
    int GetPageSize(out uint Size);
    
    /// <summary>
    /// Checks if the target uses 64-bit pointers.
    /// </summary>
    [PreserveSig]
    int IsPointer64Bit();
    
    /// <summary>
    /// Reads bug check data from a kernel dump.
    /// </summary>
    [PreserveSig]
    int ReadBugCheckData(out uint Code, out ulong Arg1, out ulong Arg2, out ulong Arg3, out ulong Arg4);
    
    /// <summary>
    /// Gets the number of supported processor types.
    /// </summary>
    [PreserveSig]
    int GetNumberSupportedProcessorTypes(out uint Number);
    
    /// <summary>
    /// Gets the list of supported processor types.
    /// </summary>
    [PreserveSig]
    int GetSupportedProcessorTypes(uint Start, uint Count, [Out, MarshalAs(UnmanagedType.LPArray)] uint[]? Types);
    
    /// <summary>
    /// Gets the names of a processor type.
    /// </summary>
    [PreserveSig]
    int GetProcessorTypeNames(uint Type, [Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? FullNameBuffer, int FullNameBufferSize, out uint FullNameSize, [Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? AbbrevNameBuffer, int AbbrevNameBufferSize, out uint AbbrevNameSize);
    
    /// <summary>
    /// Gets the effective processor type.
    /// </summary>
    [PreserveSig]
    int GetEffectiveProcessorType(out uint Type);
    
    /// <summary>
    /// Sets the effective processor type.
    /// </summary>
    [PreserveSig]
    int SetEffectiveProcessorType(uint Type);
    
    /// <summary>
    /// Gets the current execution status.
    /// </summary>
    [PreserveSig]
    int GetExecutionStatus(out uint Status);
    
    /// <summary>
    /// Sets the execution status (e.g., to break or continue execution).
    /// </summary>
    [PreserveSig]
    int SetExecutionStatus(uint Status);
    
    /// <summary>
    /// Gets the current code level (source or assembly).
    /// </summary>
    [PreserveSig]
    int GetCodeLevel(out uint Level);
    
    /// <summary>
    /// Sets the code level.
    /// </summary>
    [PreserveSig]
    int SetCodeLevel(uint Level);
    
    /// <summary>
    /// Gets the current engine options.
    /// </summary>
    [PreserveSig]
    int GetEngineOptions(out uint Options);
    
    /// <summary>
    /// Adds engine options to the current set.
    /// </summary>
    [PreserveSig]
    int AddEngineOptions(uint Options);
    
    /// <summary>
    /// Removes engine options from the current set.
    /// </summary>
    [PreserveSig]
    int RemoveEngineOptions(uint Options);
    
    /// <summary>
    /// Sets the engine options, replacing the current set.
    /// </summary>
    [PreserveSig]
    int SetEngineOptions(uint Options);
    
    /// <summary>
    /// Gets the system error control settings.
    /// </summary>
    [PreserveSig]
    int GetSystemErrorControl(out uint OutputLevel, out uint BreakLevel);
    
    /// <summary>
    /// Sets the system error control settings.
    /// </summary>
    [PreserveSig]
    int SetSystemErrorControl(uint OutputLevel, uint BreakLevel);
    
    /// <summary>
    /// Gets a text macro value.
    /// </summary>
    [PreserveSig]
    int GetTextMacro(uint Slot, [Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? Buffer, int BufferSize, out uint MacroSize);
    
    /// <summary>
    /// Sets a text macro value.
    /// </summary>
    [PreserveSig]
    int SetTextMacro(uint Slot, [MarshalAs(UnmanagedType.LPStr)] string Macro);
    
    /// <summary>
    /// Gets the current radix for number display.
    /// </summary>
    [PreserveSig]
    int GetRadix(out uint Radix);
    
    /// <summary>
    /// Sets the radix for number display.
    /// </summary>
    [PreserveSig]
    int SetRadix(uint Radix);
    
    /// <summary>
    /// Evaluates an expression.
    /// </summary>
    [PreserveSig]
    int Evaluate([MarshalAs(UnmanagedType.LPStr)] string Expression, uint DesiredType, out DEBUG_VALUE Value, out uint RemainderIndex);
    
    /// <summary>
    /// Coerces a value to a different type.
    /// </summary>
    [PreserveSig]
    int CoerceValue(in DEBUG_VALUE In, uint OutType, out DEBUG_VALUE Out);
    
    /// <summary>
    /// Coerces multiple values to different types.
    /// </summary>
    [PreserveSig]
    int CoerceValues(uint Count, [In, MarshalAs(UnmanagedType.LPArray)] DEBUG_VALUE[] In, [In, MarshalAs(UnmanagedType.LPArray)] uint[] OutTypes, [Out, MarshalAs(UnmanagedType.LPArray)] DEBUG_VALUE[] Out);
    
    /// <summary>
    /// Executes a debugger command.
    /// </summary>
    /// <param name="OutputControl">Controls where the output is sent.</param>
    /// <param name="Command">The command string to execute.</param>
    /// <param name="Flags">Execution flags.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    /// <remarks>
    /// This is the primary method for executing WinDbg commands programmatically.
    /// The command can be any valid WinDbg command, including extension commands (e.g., !analyze).
    /// </remarks>
    [PreserveSig]
    int Execute(uint OutputControl, [MarshalAs(UnmanagedType.LPStr)] string Command, uint Flags);
    
    /// <summary>
    /// Executes commands from a command file.
    /// </summary>
    [PreserveSig]
    int ExecuteCommandFile(uint OutputControl, [MarshalAs(UnmanagedType.LPStr)] string CommandFile, uint Flags);
    
    /// <summary>
    /// Gets the number of breakpoints.
    /// </summary>
    [PreserveSig]
    int GetNumberBreakpoints(out uint Number);
    
    /// <summary>
    /// Gets a breakpoint by its index.
    /// </summary>
    [PreserveSig]
    int GetBreakpointByIndex(uint Index, out IntPtr Bp);
    
    /// <summary>
    /// Gets a breakpoint by its ID.
    /// </summary>
    [PreserveSig]
    int GetBreakpointById(uint Id, out IntPtr Bp);
    
    /// <summary>
    /// Gets parameters for multiple breakpoints.
    /// </summary>
    [PreserveSig]
    int GetBreakpointParameters(uint Count, [In, MarshalAs(UnmanagedType.LPArray)] uint[]? Ids, uint Start, [Out, MarshalAs(UnmanagedType.LPArray)] DEBUG_BREAKPOINT_PARAMETERS[]? Params);
    
    /// <summary>
    /// Adds a new breakpoint.
    /// </summary>
    [PreserveSig]
    int AddBreakpoint(uint Type, uint DesiredId, out IntPtr Bp);
    
    /// <summary>
    /// Removes a breakpoint.
    /// </summary>
    [PreserveSig]
    int RemoveBreakpoint(IntPtr Bp);
    
    /// <summary>
    /// Adds an extension DLL.
    /// </summary>
    [PreserveSig]
    int AddExtension([MarshalAs(UnmanagedType.LPStr)] string Path, uint Flags, out ulong Handle);
    
    /// <summary>
    /// Removes an extension DLL.
    /// </summary>
    [PreserveSig]
    int RemoveExtension(ulong Handle);
    
    /// <summary>
    /// Gets an extension handle by its path.
    /// </summary>
    [PreserveSig]
    int GetExtensionByPath([MarshalAs(UnmanagedType.LPStr)] string Path, out ulong Handle);
    
    /// <summary>
    /// Calls a function in an extension DLL.
    /// </summary>
    [PreserveSig]
    int CallExtension(ulong Handle, [MarshalAs(UnmanagedType.LPStr)] string Function, [MarshalAs(UnmanagedType.LPStr)] string? Arguments);
    
    /// <summary>
    /// Gets a function pointer from an extension DLL.
    /// </summary>
    [PreserveSig]
    int GetExtensionFunction(ulong Handle, [MarshalAs(UnmanagedType.LPStr)] string FuncName, out IntPtr Function);
    
    /// <summary>
    /// Gets the WinDbg extension APIs for 32-bit.
    /// </summary>
    [PreserveSig]
    int GetWindbgExtensionApis32(IntPtr Api);
    
    /// <summary>
    /// Gets the WinDbg extension APIs for 64-bit.
    /// </summary>
    [PreserveSig]
    int GetWindbgExtensionApis64(IntPtr Api);
    
    /// <summary>
    /// Gets the number of event filters.
    /// </summary>
    [PreserveSig]
    int GetNumberEventFilters(out uint SpecificEvents, out uint SpecificExceptions, out uint ArbitraryExceptions);
    
    /// <summary>
    /// Gets the text for an event filter.
    /// </summary>
    [PreserveSig]
    int GetEventFilterText(uint Index, [Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? Buffer, int BufferSize, out uint TextSize);
    
    /// <summary>
    /// Gets the command for an event filter.
    /// </summary>
    [PreserveSig]
    int GetEventFilterCommand(uint Index, [Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? Buffer, int BufferSize, out uint CommandSize);
    
    /// <summary>
    /// Sets the command for an event filter.
    /// </summary>
    [PreserveSig]
    int SetEventFilterCommand(uint Index, [MarshalAs(UnmanagedType.LPStr)] string Command);
    
    /// <summary>
    /// Gets parameters for specific event filters.
    /// </summary>
    [PreserveSig]
    int GetSpecificFilterParameters(uint Start, uint Count, [Out, MarshalAs(UnmanagedType.LPArray)] DEBUG_SPECIFIC_FILTER_PARAMETERS[]? Params);
    
    /// <summary>
    /// Sets parameters for specific event filters.
    /// </summary>
    [PreserveSig]
    int SetSpecificFilterParameters(uint Start, uint Count, [In, MarshalAs(UnmanagedType.LPArray)] DEBUG_SPECIFIC_FILTER_PARAMETERS[] Params);
    
    /// <summary>
    /// Gets the argument for a specific event filter.
    /// </summary>
    [PreserveSig]
    int GetSpecificFilterArgument(uint Index, [Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? Buffer, int BufferSize, out uint ArgumentSize);
    
    /// <summary>
    /// Sets the argument for a specific event filter.
    /// </summary>
    [PreserveSig]
    int SetSpecificFilterArgument(uint Index, [MarshalAs(UnmanagedType.LPStr)] string Argument);
    
    /// <summary>
    /// Gets parameters for exception filters.
    /// </summary>
    [PreserveSig]
    int GetExceptionFilterParameters(uint Count, [In, MarshalAs(UnmanagedType.LPArray)] uint[]? Codes, uint Start, [Out, MarshalAs(UnmanagedType.LPArray)] DEBUG_EXCEPTION_FILTER_PARAMETERS[]? Params);
    
    /// <summary>
    /// Sets parameters for exception filters.
    /// </summary>
    [PreserveSig]
    int SetExceptionFilterParameters(uint Count, [In, MarshalAs(UnmanagedType.LPArray)] DEBUG_EXCEPTION_FILTER_PARAMETERS[] Params);
    
    /// <summary>
    /// Gets the second command for an exception filter.
    /// </summary>
    [PreserveSig]
    int GetExceptionFilterSecondCommand(uint Index, [Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? Buffer, int BufferSize, out uint CommandSize);
    
    /// <summary>
    /// Sets the second command for an exception filter.
    /// </summary>
    [PreserveSig]
    int SetExceptionFilterSecondCommand(uint Index, [MarshalAs(UnmanagedType.LPStr)] string Command);
    
    /// <summary>
    /// Waits for a debugger event to occur.
    /// </summary>
    /// <param name="Flags">Flags controlling the wait behavior.</param>
    /// <param name="Timeout">Timeout in milliseconds.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    /// <remarks>
    /// This method blocks until an event occurs or the timeout expires.
    /// It's essential for processing dump files after opening them.
    /// </remarks>
    [PreserveSig]
    int WaitForEvent(uint Flags, uint Timeout);
    
    /// <summary>
    /// Gets information about the last event that occurred.
    /// </summary>
    [PreserveSig]
    int GetLastEventInformation(out uint Type, out uint ProcessId, out uint ThreadId, IntPtr ExtraInformation, uint ExtraInformationSize, out uint ExtraInformationUsed, [Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder? Description, int DescriptionSize, out uint DescriptionUsed);
}



/// <summary>
/// Represents a stack frame in a call stack.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DEBUG_STACK_FRAME
{
    /// <summary>
    /// The instruction pointer for this frame.
    /// </summary>
    public ulong InstructionOffset;
    
    /// <summary>
    /// The return address for this frame.
    /// </summary>
    public ulong ReturnOffset;
    
    /// <summary>
    /// The frame pointer (base pointer) for this frame.
    /// </summary>
    public ulong FrameOffset;
    
    /// <summary>
    /// The stack pointer for this frame.
    /// </summary>
    public ulong StackOffset;
    
    /// <summary>
    /// Pointer to the function table entry for this frame.
    /// </summary>
    public ulong FuncTableEntry;
    
    /// <summary>
    /// Parameters passed to the function (up to 4).
    /// </summary>
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public ulong[] Params;
    
    /// <summary>
    /// Reserved for future use.
    /// </summary>
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
    public ulong[] Reserved;
    
    /// <summary>
    /// Indicates if this is a virtual frame.
    /// </summary>
    public int Virtual;
    
    /// <summary>
    /// The frame number in the stack trace.
    /// </summary>
    public uint FrameNumber;
}

/// <summary>
/// Represents a value in the debugger, which can be of various types.
/// </summary>
/// <remarks>
/// This is a union structure that can hold different types of values.
/// The Type field indicates which member is valid.
/// </remarks>
[StructLayout(LayoutKind.Explicit)]
public struct DEBUG_VALUE
{
    /// <summary>8-bit integer value.</summary>
    [FieldOffset(0)]
    public byte I8;
    
    /// <summary>16-bit integer value.</summary>
    [FieldOffset(0)]
    public ushort I16;
    
    /// <summary>32-bit integer value.</summary>
    [FieldOffset(0)]
    public uint I32;
    
    /// <summary>64-bit integer value.</summary>
    [FieldOffset(0)]
    public ulong I64;
    
    /// <summary>32-bit floating point value.</summary>
    [FieldOffset(0)]
    public float F32;
    
    /// <summary>64-bit floating point value.</summary>
    [FieldOffset(0)]
    public double F64;
    
    /// <summary>80-bit floating point value (10 bytes).</summary>
    [FieldOffset(0)]
    public unsafe fixed byte F80Bytes[10];
    
    /// <summary>128-bit floating point value (16 bytes).</summary>
    [FieldOffset(0)]
    public unsafe fixed byte F128Bytes[16];
    
    /// <summary>Vector of 8-bit integers.</summary>
    [FieldOffset(0)]
    public unsafe fixed byte VI8[16];
    
    /// <summary>Vector of 16-bit integers.</summary>
    [FieldOffset(0)]
    public unsafe fixed ushort VI16[8];
    
    /// <summary>Vector of 32-bit integers.</summary>
    [FieldOffset(0)]
    public unsafe fixed uint VI32[4];
    
    /// <summary>Vector of 64-bit integers.</summary>
    [FieldOffset(0)]
    public unsafe fixed ulong VI64[2];
    
    /// <summary>Vector of 32-bit floats.</summary>
    [FieldOffset(0)]
    public unsafe fixed float VF32[4];
    
    /// <summary>Vector of 64-bit floats.</summary>
    [FieldOffset(0)]
    public unsafe fixed double VF64[2];
    
    /// <summary>Tail of raw bytes for extended types.</summary>
    [FieldOffset(24)]
    public uint TailOfRawBytes;
    
    /// <summary>Type indicator for the value.</summary>
    [FieldOffset(28)]
    public uint Type;
}

/// <summary>
/// Contains parameters for a breakpoint.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DEBUG_BREAKPOINT_PARAMETERS
{
    /// <summary>The offset (address) of the breakpoint.</summary>
    public ulong Offset;
    
    /// <summary>The breakpoint ID.</summary>
    public uint Id;
    
    /// <summary>The type of breakpoint.</summary>
    public uint BreakType;
    
    /// <summary>The processor type for the breakpoint.</summary>
    public uint ProcType;
    
    /// <summary>Breakpoint flags.</summary>
    public uint Flags;
    
    /// <summary>Size of data for data breakpoints.</summary>
    public uint DataSize;
    
    /// <summary>Access type for data breakpoints.</summary>
    public uint DataAccessType;
    
    /// <summary>Pass count before the breakpoint triggers.</summary>
    public uint PassCount;
    
    /// <summary>Current pass count.</summary>
    public uint CurrentPassCount;
    
    /// <summary>Thread ID that matches this breakpoint.</summary>
    public uint MatchThread;
    
    /// <summary>Size of the command string.</summary>
    public uint CommandSize;
    
    /// <summary>Size of the offset expression string.</summary>
    public uint OffsetExpressionSize;
}

/// <summary>
/// Contains parameters for a specific event filter.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DEBUG_SPECIFIC_FILTER_PARAMETERS
{
    /// <summary>Execution option when the event occurs.</summary>
    public uint ExecutionOption;
    
    /// <summary>Continue option after handling the event.</summary>
    public uint ContinueOption;
    
    /// <summary>Size of the filter text.</summary>
    public uint TextSize;
    
    /// <summary>Size of the command string.</summary>
    public uint CommandSize;
    
    /// <summary>Size of the argument string.</summary>
    public uint ArgumentSize;
}

/// <summary>
/// Contains parameters for an exception filter.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DEBUG_EXCEPTION_FILTER_PARAMETERS
{
    /// <summary>Execution option when the exception occurs.</summary>
    public uint ExecutionOption;
    
    /// <summary>Continue option after handling the exception.</summary>
    public uint ContinueOption;
    
    /// <summary>Size of the filter text.</summary>
    public uint TextSize;
    
    /// <summary>Size of the first command string.</summary>
    public uint CommandSize;
    
    /// <summary>Size of the second command string.</summary>
    public uint SecondCommandSize;
    
    /// <summary>The exception code this filter applies to.</summary>
    public uint ExceptionCode;
}



/// <summary>
/// Contains constants used with the DbgEng API.
/// </summary>
public static class DbgEngConstants
{
    // Output mask constants
    /// <summary>Normal output.</summary>
    public const uint DEBUG_OUTPUT_NORMAL = 0x00000001;
    
    /// <summary>Error output.</summary>
    public const uint DEBUG_OUTPUT_ERROR = 0x00000002;
    
    /// <summary>Warning output.</summary>
    public const uint DEBUG_OUTPUT_WARNING = 0x00000004;
    
    /// <summary>Verbose output.</summary>
    public const uint DEBUG_OUTPUT_VERBOSE = 0x00000008;
    
    /// <summary>Prompt output.</summary>
    public const uint DEBUG_OUTPUT_PROMPT = 0x00000010;
    
    /// <summary>Prompt with registers output.</summary>
    public const uint DEBUG_OUTPUT_PROMPT_REGISTERS = 0x00000020;
    
    /// <summary>Extension warning output.</summary>
    public const uint DEBUG_OUTPUT_EXTENSION_WARNING = 0x00000040;
    
    /// <summary>Debuggee output.</summary>
    public const uint DEBUG_OUTPUT_DEBUGGEE = 0x00000080;
    
    /// <summary>Debuggee prompt output.</summary>
    public const uint DEBUG_OUTPUT_DEBUGGEE_PROMPT = 0x00000100;
    
    /// <summary>Symbols output.</summary>
    public const uint DEBUG_OUTPUT_SYMBOLS = 0x00000200;
    
    // Output control constants
    /// <summary>Send output to this client only.</summary>
    public const uint DEBUG_OUTCTL_THIS_CLIENT = 0x00000000;
    
    /// <summary>Send output to all clients.</summary>
    public const uint DEBUG_OUTCTL_ALL_CLIENTS = 0x00000001;
    
    /// <summary>Send output to all other clients.</summary>
    public const uint DEBUG_OUTCTL_ALL_OTHER_CLIENTS = 0x00000002;
    
    /// <summary>Ignore output.</summary>
    public const uint DEBUG_OUTCTL_IGNORE = 0x00000003;
    
    /// <summary>Send output to log only.</summary>
    public const uint DEBUG_OUTCTL_LOG_ONLY = 0x00000004;
    
    /// <summary>Mask for send flags.</summary>
    public const uint DEBUG_OUTCTL_SEND_MASK = 0x00000007;
    
    /// <summary>Don't log this output.</summary>
    public const uint DEBUG_OUTCTL_NOT_LOGGED = 0x00000008;
    
    /// <summary>Override output mask.</summary>
    public const uint DEBUG_OUTCTL_OVERRIDE_MASK = 0x00000010;
    
    /// <summary>Output is in DML format.</summary>
    public const uint DEBUG_OUTCTL_DML = 0x00000020;
    
    /// <summary>Use ambient DML setting.</summary>
    public const uint DEBUG_OUTCTL_AMBIENT_DML = 0xfffffffe;
    
    /// <summary>Use ambient text setting.</summary>
    public const uint DEBUG_OUTCTL_AMBIENT_TEXT = 0xffffffff;
    
    // Execute flags
    /// <summary>Default execution flags.</summary>
    public const uint DEBUG_EXECUTE_DEFAULT = 0x00000000;
    
    /// <summary>Echo the command.</summary>
    public const uint DEBUG_EXECUTE_ECHO = 0x00000001;
    
    /// <summary>Don't log the command.</summary>
    public const uint DEBUG_EXECUTE_NOT_LOGGED = 0x00000002;
    
    /// <summary>Don't repeat the command.</summary>
    public const uint DEBUG_EXECUTE_NO_REPEAT = 0x00000004;
}



/// <summary>
/// Provides access to native DbgEng functions.
/// </summary>
public static class DbgEng
{
    /// <summary>
    /// Creates a new instance of a debugger client.
    /// </summary>
    /// <param name="InterfaceId">The GUID of the interface to create.</param>
    /// <param name="Interface">Receives the created interface.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    /// <remarks>
    /// This is the entry point for creating a debugger client.
    /// The dbgeng.dll must be available in the system PATH or application directory.
    /// </remarks>
    [DllImport("dbgeng.dll", PreserveSig = true)]
    public static extern int DebugCreate(ref Guid InterfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object Interface);
    
    /// <summary>
    /// The GUID for the IDebugClient interface.
    /// </summary>
    public static readonly Guid IID_IDebugClient = new Guid("27fe5639-8407-4f47-8364-ee118fb08ac8");
}

