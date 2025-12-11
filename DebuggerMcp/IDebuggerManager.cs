using System;

namespace DebuggerMcp;

/// <summary>
/// Defines the contract for debugger managers that can control different debugging engines.
/// </summary>
/// <remarks>
/// This interface provides a common abstraction for different debugger implementations:
/// - WinDbg (Windows): Uses DbgEng COM API
/// - LLDB (Linux/macOS): Uses process-based communication
/// 
/// Implementations must handle platform-specific details while providing a consistent API.
/// 
/// Both <see cref="IDisposable"/> and <see cref="IAsyncDisposable"/> are implemented
/// to support both synchronous and asynchronous cleanup scenarios. In ASP.NET Core,
/// prefer using <see cref="IAsyncDisposable.DisposeAsync"/> for proper async cleanup.
/// </remarks>
public interface IDebuggerManager : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Gets a value indicating whether the debugger engine has been initialized.
    /// </summary>
    /// <value>
    /// <c>true</c> if the debugger is initialized; otherwise, <c>false</c>.
    /// </value>
    bool IsInitialized { get; }

    /// <summary>
    /// Gets a value indicating whether a dump file is currently open.
    /// </summary>
    /// <value>
    /// <c>true</c> if a dump is open; otherwise, <c>false</c>.
    /// </value>
    bool IsDumpOpen { get; }

    /// <summary>
    /// Gets the path to the currently open dump file.
    /// </summary>
    /// <value>
    /// The full path to the dump file if one is open; otherwise, <c>null</c>.
    /// </value>
    /// <remarks>
    /// This property is used for session persistence to allow automatic reopening
    /// of the dump file when a session is restored from disk.
    /// </remarks>
    string? CurrentDumpPath { get; }

    /// <summary>
    /// Gets the type of debugger this manager controls.
    /// </summary>
    /// <value>
    /// The debugger type (e.g., "WinDbg", "LLDB").
    /// </value>
    string DebuggerType { get; }

    /// <summary>
    /// Gets a value indicating whether the SOS debugging extension is loaded.
    /// </summary>
    /// <value>
    /// <c>true</c> if SOS is loaded and .NET debugging commands are available; otherwise, <c>false</c>.
    /// </value>
    /// <remarks>
    /// SOS is automatically loaded when a .NET dump is detected during <see cref="OpenDumpFile"/>.
    /// It can also be loaded manually via <see cref="LoadSosExtension"/>.
    /// </remarks>
    bool IsSosLoaded { get; }

    /// <summary>
    /// Gets a value indicating whether the currently open dump is a .NET dump.
    /// </summary>
    /// <value>
    /// <c>true</c> if the dump contains .NET runtime modules (CoreCLR or CLR); otherwise, <c>false</c>.
    /// </value>
    /// <remarks>
    /// This is detected automatically when <see cref="OpenDumpFile"/> is called by checking:
    /// <list type="bullet">
    /// <item><description>For LLDB: Runtime version detection from dotnet-symbol or libcoreclr module</description></item>
    /// <item><description>For WinDbg: Presence of coreclr.dll or clr.dll modules</description></item>
    /// </list>
    /// When a .NET dump is detected, SOS is automatically loaded.
    /// </remarks>
    bool IsDotNetDump { get; }

    /// <summary>
    /// Initializes the debugger engine asynchronously.
    /// </summary>
    /// <remarks>
    /// This method must be called before any other operations can be performed.
    /// For WinDbg, this creates the DbgEng COM objects.
    /// For LLDB, this starts the LLDB process (includes async startup delay).
    /// </remarks>
    /// <returns>A task representing the asynchronous initialization operation.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the debugger is already initialized or if initialization fails.
    /// </exception>
    Task InitializeAsync();

    /// <summary>
    /// Opens a memory dump file for analysis.
    /// </summary>
    /// <param name="dumpFilePath">The absolute path to the dump file (.dmp for Windows, .core for Linux).</param>
    /// <remarks>
    /// The debugger must be initialized before calling this method.
    /// Only one dump can be open at a time per debugger instance.
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
    /// <param name="dumpFilePath">The path to the dump file to open.</param>
    /// <param name="executablePath">
    /// Optional path to the executable for standalone .NET apps.
    /// When specified, LLDB will use this binary instead of the default dotnet host.
    /// </param>
    void OpenDumpFile(string dumpFilePath, string? executablePath = null);

    /// <summary>
    /// Closes the currently open dump file.
    /// </summary>
    /// <remarks>
    /// After closing, another dump can be opened using the same debugger instance.
    /// This method is idempotent - calling it when no dump is open has no effect.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the debugger is not initialized.
    /// </exception>
    void CloseDump();

    /// <summary>
    /// Executes a debugger command and returns the output.
    /// </summary>
    /// <param name="command">The debugger command to execute (e.g., "k" for call stack, "!threads" for SOS).</param>
    /// <returns>The output from the debugger command execution.</returns>
    /// <remarks>
    /// <para>The debugger must be initialized and a dump file must be open before calling this method.</para>
    /// <para>Commands are platform-specific:</para>
    /// <list type="bullet">
    /// <item><description>WinDbg: Standard WinDbg commands (k, lm, r, etc.)</description></item>
    /// <item><description>LLDB: Standard LLDB commands (bt, image list, register read, etc.)</description></item>
    /// </list>
    /// <para>SOS commands work on both platforms when the SOS extension is loaded.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the debugger is not initialized or if no dump file is open.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown if the command is null, empty, or whitespace.
    /// </exception>
    string ExecuteCommand(string command);

    /// <summary>
    /// Loads the SOS (Son of Strike) extension for .NET debugging.
    /// </summary>
    /// <remarks>
    /// <para>This method is idempotent - calling it when SOS is already loaded has no effect.</para>
    /// <para>The debugger must be initialized and a dump must be open before calling this method.</para>
    /// <para>Note: SOS is automatically loaded when <see cref="OpenDumpFile"/> detects a .NET dump,
    /// so explicit calls to this method are typically unnecessary.</para>
    /// <para>Platform-specific behavior:</para>
    /// <list type="bullet">
    /// <item><description>Windows: Executes ".loadby sos coreclr" or ".loadby sos clr"</description></item>
    /// <item><description>Linux/macOS: Executes "plugin load libsosplugin.so"</description></item>
    /// </list>
    /// <para>After loading SOS, commands like !threads, !dumpheap, !pe, etc. become available.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the debugger is not initialized or if no dump is open.
    /// </exception>
    void LoadSosExtension();

    /// <summary>
    /// Configures the symbol path for the debugger.
    /// </summary>
    /// <param name="symbolPath">The symbol path string (platform-specific format).</param>
    /// <remarks>
    /// <para>The debugger must be initialized before calling this method.</para>
    /// <para>This method can be called before opening a dump file, which is the recommended practice
    /// to ensure symbols are available when the dump is loaded.</para>
    /// <para>Platform-specific formats:</para>
    /// <list type="bullet">
    /// <item><description>WinDbg: srv*cache*server;path (e.g., "srv*c:\\symbols*https://msdl.microsoft.com/download/symbols")</description></item>
    /// <item><description>LLDB: space-separated directories (e.g., "/path/to/symbols /another/path")</description></item>
    /// </list>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the debugger is not initialized.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown if the symbol path is null, empty, or whitespace.
    /// </exception>
    void ConfigureSymbolPath(string symbolPath);
}
