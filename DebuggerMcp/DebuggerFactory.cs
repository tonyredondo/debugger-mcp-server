using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp;

/// <summary>
/// Factory class for creating the appropriate debugger manager based on the operating system.
/// </summary>
/// <remarks>
/// This factory automatically detects the current operating system and creates the correct
/// debugger implementation:
/// <list type="bullet">
/// <item><description>Windows: Creates <see cref="WinDbgManager"/> using DbgEng COM API</description></item>
/// <item><description>Linux: Creates <see cref="LldbManager"/> using LLDB process</description></item>
/// <item><description>macOS: Creates <see cref="LldbManager"/> using LLDB process</description></item>
/// </list>
/// </remarks>
public static class DebuggerFactory
{
    /// <summary>
    /// Creates a debugger manager appropriate for the current operating system.
    /// </summary>
    /// <returns>
    /// An instance of <see cref="IDebuggerManager"/> configured for the current platform.
    /// </returns>
    /// <remarks>
    /// <para>This method uses <see cref="RuntimeInformation.IsOSPlatform"/> to detect the OS.</para>
    /// <para>The returned instance is not initialized; call <see cref="IDebuggerManager.InitializeAsync"/>
    /// before using it.</para>
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown if the current operating system is not supported.
    /// </exception>
    /// <example>
    /// Basic usage:
    /// <code>
    /// using var debugger = DebuggerFactory.CreateDebugger();
    /// await debugger.InitializeAsync();
    /// debugger.OpenDumpFile("/path/to/dump.dmp");
    /// var output = debugger.ExecuteCommand("k"); // Call stack on WinDbg, use "bt" on LLDB
    /// Console.WriteLine(output);
    /// </code>
    /// 
    /// Full analysis workflow:
    /// <code>
    /// using var debugger = DebuggerFactory.CreateDebugger();
    /// await debugger.InitializeAsync();
    /// 
    /// // Configure symbols (optional - enhances analysis)
    /// debugger.ConfigureSymbolPath("srv*c:\\symbols*https://msdl.microsoft.com/download/symbols");
    /// 
    /// // Open dump file
    /// debugger.OpenDumpFile("C:\\dumps\\crash.dmp");
    /// 
    /// // Analyze crash
    /// var analyzeOutput = debugger.ExecuteCommand("!analyze -v");
    /// var callStack = debugger.ExecuteCommand("k");
    /// var threads = debugger.ExecuteCommand("~");
    /// var modules = debugger.ExecuteCommand("lm");
    /// 
    /// // For .NET dumps, SOS is auto-loaded by OpenDumpFile
    /// // (LoadSosExtension() is still available for manual loading if needed)
    /// var managedThreads = debugger.ExecuteCommand("!threads");
    /// var heapStats = debugger.ExecuteCommand("!dumpheap -stat");
    /// 
    /// // Cleanup
    /// debugger.CloseDump();
    /// </code>
    /// </example>
    public static IDebuggerManager CreateDebugger()
    {
        return CreateDebugger(NullLoggerFactory.Instance);
    }

    /// <summary>
    /// Creates a debugger manager appropriate for the current operating system with logging support.
    /// </summary>
    /// <param name="loggerFactory">The logger factory for creating loggers.</param>
    /// <returns>
    /// An instance of <see cref="IDebuggerManager"/> configured for the current platform.
    /// </returns>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown if the current operating system is not supported.
    /// </exception>
    public static IDebuggerManager CreateDebugger(ILoggerFactory loggerFactory)
    {
        // Detect the operating system and create the appropriate debugger
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: Use WinDbg with DbgEng COM API because LLDB is not first-class here.
            return new WinDbgManager();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Linux: Use LLDB with process-based communication; WinDbg is unavailable.
            var logger = loggerFactory.CreateLogger<LldbManager>();
            return new LldbManager(logger);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS: Use LLDB; WinDbg is Windows-only.
            var logger = loggerFactory.CreateLogger<LldbManager>();
            return new LldbManager(logger);
        }
        else
        {
            // Unsupported platform
            throw new PlatformNotSupportedException(
                $"Debugging is not supported on this platform. " +
                $"Supported platforms: Windows (WinDbg), Linux (LLDB), macOS (LLDB)");
        }
    }

    /// <summary>
    /// Gets the debugger type that would be created for the current operating system.
    /// </summary>
    /// <returns>
    /// A string indicating the debugger type ("WinDbg" or "LLDB").
    /// </returns>
    /// <remarks>
    /// This method can be used to determine which debugger will be used without
    /// actually creating an instance.
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown if the current operating system is not supported.
    /// </exception>
    public static string GetDebuggerType()
    {
        // Detect the operating system and return the debugger type
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "WinDbg";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "LLDB";
        }
        else
        {
            // Explicit throw keeps callers honest about unsupported platforms.
            throw new PlatformNotSupportedException(
                $"Debugging is not supported on this platform");
        }
    }

    /// <summary>
    /// Gets a value indicating whether the current platform supports debugging.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the current platform is supported; otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// This method returns <c>true</c> for Windows, Linux, and macOS.
    /// </remarks>
    public static bool IsPlatformSupported()
    {
        // Check if the current platform is supported
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
               RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
               RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    }

    /// <summary>
    /// Gets the current operating system platform name.
    /// </summary>
    /// <returns>
    /// A string representing the current OS ("Windows", "Linux", "macOS", or "Unknown").
    /// </returns>
    /// <remarks>
    /// This is a utility method for logging and diagnostics.
    /// </remarks>
    public static string GetCurrentPlatform()
    {
        // Determine the current platform
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "Windows";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "Linux";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "macOS";
        }
        else
        {
            return "Unknown";
        }
    }
}
