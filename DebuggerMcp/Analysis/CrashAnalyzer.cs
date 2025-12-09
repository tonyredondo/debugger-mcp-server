using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DebuggerMcp.SourceLink;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Base class for crash analysis functionality.
/// Provides automated analysis of memory dumps with structured output.
/// </summary>
public class CrashAnalyzer
{
    protected readonly IDebuggerManager _debuggerManager;
    private readonly SourceLinkResolver? _sourceLinkResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrashAnalyzer"/> class.
    /// </summary>
    /// <param name="debuggerManager">The debugger manager to use for executing commands.</param>
    /// <param name="sourceLinkResolver">Optional Source Link resolver for resolving source URLs.</param>
    public CrashAnalyzer(IDebuggerManager debuggerManager, SourceLinkResolver? sourceLinkResolver = null)
    {
        _debuggerManager = debuggerManager ?? throw new ArgumentNullException(nameof(debuggerManager));
        _sourceLinkResolver = sourceLinkResolver;
    }

    /// <summary>
    /// Performs a general crash analysis.
    /// Detects the type of crash and gathers relevant information.
    /// </summary>
    /// <returns>A structured crash analysis result.</returns>
    public virtual async Task<CrashAnalysisResult> AnalyzeCrashAsync()
    {
        var result = new CrashAnalysisResult();

        // Initialize new hierarchical structures
        InitializeNewStructures(result);

        // Command caching is automatically enabled when dump is opened
        // All commands benefit from caching for the entire session

        try
        {
            // Check if debugger is initialized
            if (!_debuggerManager.IsInitialized)
            {
                // Without initialization we cannot execute commands; return a minimal result.
                result.Summary!.Description = "Debugger not initialized";
                return result;
            }

            // Detect debugger type and run appropriate analysis
            if (_debuggerManager.DebuggerType == "WinDbg")
            {
                await AnalyzeWithWinDbgAsync(result);
            }
            else if (_debuggerManager.DebuggerType == "LLDB")
            {
                await AnalyzeWithLldbAsync(result);
            }
            else
            {
                // Unknown debugger type: avoid throwing and return a descriptive summary.
                result.Summary!.Description = $"Unknown debugger type: {_debuggerManager.DebuggerType}";
            }
        }
        catch (Exception ex)
        {
            result.Summary!.Description = $"Analysis failed: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// Initializes the new hierarchical structures on the result object.
    /// </summary>
    protected static void InitializeNewStructures(CrashAnalysisResult result)
    {
        result.Summary = new AnalysisSummary();
        result.Environment = new EnvironmentInfo();
        result.Threads = new ThreadsInfo { All = new List<ThreadInfo>(), Summary = new ThreadSummary() };
        result.Memory = new MemoryInfo();
        result.Modules = new List<ModuleInfo>();
        result.RawCommands = new Dictionary<string, string>();
    }

    /// <summary>
    /// Performs analysis using WinDbg commands.
    /// </summary>
    /// <param name="result">The result object to populate.</param>
    protected virtual async Task AnalyzeWithWinDbgAsync(CrashAnalysisResult result)
    {
        // Get exception information
        var exceptionOutput = await ExecuteCommandAsync("!analyze -v");
        result.RawCommands!["!analyze -v"] = exceptionOutput;
        ParseWinDbgException(exceptionOutput, result);

        // Get thread information first (so we know which thread is current)
        var threadsOutput = await ExecuteCommandAsync("~");
        result.RawCommands!["~"] = threadsOutput;
        ParseWinDbgThreads(threadsOutput, result);

        // Get all threads' backtraces
        var allBacktraces = await ExecuteCommandAsync("~*k");
        result.RawCommands!["~*k"] = allBacktraces;
        ParseWinDbgBacktraceAll(allBacktraces, result);

        // Get module information
        var modulesOutput = await ExecuteCommandAsync("lm");
        result.RawCommands!["lm"] = modulesOutput;
        ParseWinDbgModules(modulesOutput, result);

        // Analyze for memory leaks
        await AnalyzeMemoryLeaksWinDbgAsync(result);

        // Analyze for deadlocks
        await AnalyzeDeadlocksWinDbgAsync(result);

        // Resolve Source Link URLs for stack frames
        ResolveSourceLinks(result);

        // Generate summary
        GenerateSummary(result);
    }

    /// <summary>
    /// Performs analysis using LLDB commands.
    /// </summary>
    /// <param name="result">The result object to populate.</param>
    protected virtual async Task AnalyzeWithLldbAsync(CrashAnalysisResult result)
    {
        // Get thread information
        var threadsOutput = await ExecuteCommandAsync("thread list");
        result.RawCommands!["thread list"] = threadsOutput;
        ParseLldbThreads(threadsOutput, result);

        // Get all threads' backtraces
        var backtraceAllOutput = await ExecuteCommandAsync("bt all");
        var cleanedBacktrace = CleanLldbOutput(backtraceAllOutput);
        result.RawCommands!["bt all"] = cleanedBacktrace;
        ParseLldbBacktraceAll(backtraceAllOutput, result);

        // Get module information
        var modulesOutput = await ExecuteCommandAsync("image list");
        result.RawCommands!["image list"] = modulesOutput;
        ParseLldbModules(modulesOutput, result);

        // Detect platform info from modules
        DetectPlatformInfo(modulesOutput, result);

        // Extract process information (arguments and environment variables)
        // Must be called after DetectPlatformInfo to have pointer size available
        await ExtractProcessInfoAsync(result, backtraceAllOutput);

        // Analyze for memory leaks
        await AnalyzeMemoryLeaksLldbAsync(result);

        // Analyze for deadlocks
        await AnalyzeDeadlocksLldbAsync(result);

        // Resolve Source Link URLs for stack frames
        ResolveSourceLinks(result);

        // Generate summary
        GenerateSummary(result);
    }

    /// <summary>
    /// Executes a debugger command and returns the output.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <returns>The command output.</returns>
    protected async Task<string> ExecuteCommandAsync(string command)
    {
        return await Task.Run(() => _debuggerManager.ExecuteCommand(command));
    }

    /// <summary>
    /// Parses WinDbg exception output from !analyze -v.
    /// </summary>
    protected virtual void ParseWinDbgException(string output, CrashAnalysisResult result)
    {
        // Use local variables during parsing
        string? exType = null;
        string? exMessage = null;
        string? exAddress = null;

        // Parse exception code (e.g., "EXCEPTION_CODE: (NTSTATUS) 0xc0000005 - ...")
        var codeMatch = Regex.Match(output, @"EXCEPTION_CODE:\s*\([^)]+\)\s*(0x[0-9a-f]+)\s*-\s*(.+)", RegexOptions.IgnoreCase);
        if (codeMatch.Success)
        {
            exType = codeMatch.Groups[1].Value;
            exMessage = codeMatch.Groups[2].Value.Trim();
            result.Summary!.CrashType = "Exception";
        }
        else
        {
            // Try simpler pattern: "EXCEPTION_CODE: (NTSTATUS) 0xc0000005"
            var simpleCodeMatch = Regex.Match(output, @"EXCEPTION_CODE:\s*\([^)]+\)\s*(0x[0-9a-f]+)", RegexOptions.IgnoreCase);
            if (simpleCodeMatch.Success)
            {
                exType = simpleCodeMatch.Groups[1].Value;
                result.Summary!.CrashType = "Exception";
            }
        }

        // Look for exception type name (e.g., "ExceptionCode: c0000005 (Access violation)")
        var typeNameMatch = Regex.Match(output, @"(EXCEPTION_[A-Z_]+|STATUS_[A-Z_]+|Access violation|Stack overflow|Breakpoint)", RegexOptions.IgnoreCase);
        if (typeNameMatch.Success)
        {
            if (string.IsNullOrEmpty(exMessage))
            {
                exMessage = typeNameMatch.Groups[1].Value;
            }
            result.Summary!.CrashType = typeNameMatch.Groups[1].Value;
        }

        // Parse exception address
        var addrMatch = Regex.Match(output, @"EXCEPTION_RECORD:\s*([0-9a-f`]+)", RegexOptions.IgnoreCase);
        if (addrMatch.Success)
        {
            exAddress = addrMatch.Groups[1].Value.Replace("`", "");
        }
        else
        {
            // Try faulting IP
            var ipMatch = Regex.Match(output, @"FAULTING_IP:\s*\n\s*(\S+)", RegexOptions.IgnoreCase);
            if (ipMatch.Success)
            {
                exAddress = ipMatch.Groups[1].Value;
            }
        }

        // Parse fault description if available
        var faultMatch = Regex.Match(output, @"FAULT_INSTR_CODE:\s*([0-9a-f]+)", RegexOptions.IgnoreCase);
        if (faultMatch.Success && string.IsNullOrEmpty(exMessage))
        {
            exMessage = $"Fault instruction code: {faultMatch.Groups[1].Value}";
        }

        // Populate new Exception structure if exception found
        if (!string.IsNullOrEmpty(exType) || !string.IsNullOrEmpty(exMessage))
        {
            result.Exception = new ExceptionDetails
            {
                Type = exType ?? "",
                Message = exMessage,
                Address = exAddress
            };
        }
    }

    /// <summary>
    /// Parses WinDbg '~*k' output (all threads' backtraces) and assigns frames to each thread.
    /// Format: ".  0  Id: 1234.5678 Suspend: 1 Teb: ... Unfrozen"
    ///         " # Child-SP          RetAddr           Call Site"
    ///         "00 00000000`12345678 00007ff8`12345678 module!Function+0x10"
    ///         "#  1  Id: 1234.9abc Suspend: 1 Teb: ... Unfrozen"
    ///         "..."
    /// </summary>
    protected virtual void ParseWinDbgBacktraceAll(string output, CrashAnalysisResult result)
    {
        // Ensure Threads.All is initialized (for tests that call this directly)
        result.Threads ??= new ThreadsInfo { All = new List<ThreadInfo>(), Summary = new ThreadSummary() };
        result.Threads.All ??= new List<ThreadInfo>();

        var lines = output.Split('\n');
        ThreadInfo? currentThread = null;

        foreach (var line in lines)
        {
            // Check for thread header: ". 0 Id:" or "# 1 Id:" (first char is '.' for current, '#' or space for others)
            var threadMatch = Regex.Match(line, @"^[.#\s]\s*(\d+)\s+Id:\s*([0-9a-f]+)\.([0-9a-f]+)", RegexOptions.IgnoreCase);
            if (threadMatch.Success)
            {
                var threadIndex = int.Parse(threadMatch.Groups[1].Value);
                var processId = threadMatch.Groups[2].Value;
                var threadId = threadMatch.Groups[3].Value;

                // Find matching thread in result.Threads.All
                var allThreads = result.Threads!.All!;
                currentThread = allThreads.FirstOrDefault(t =>
                    t.ThreadId == threadIndex.ToString() ||
                    t.ThreadId == threadId ||
                    t.ThreadId.EndsWith($".{threadId}"));

                // If not found by ID, try by index
                if (currentThread == null && threadIndex >= 0 && threadIndex < allThreads.Count)
                {
                    currentThread = allThreads[threadIndex];
                }
                continue;
            }

            // Parse frame and add to current thread
            if (currentThread != null)
            {
                var frame = ParseWinDbgSingleFrame(line);
                if (frame != null)
                {
                    currentThread.CallStack.Add(frame);
                }
            }
        }
    }

    /// <summary>
    /// Parses a single stack frame line from WinDbg output.
    /// </summary>
    private StackFrame? ParseWinDbgSingleFrame(string line)
    {
        // Match stack frame pattern: frame# address address module!function+offset
        // Example: "00 00000000`12345678 00007ff8`12345678 ntdll!NtWaitForSingleObject+0x14"
        var frameMatch = Regex.Match(line,
            @"^\s*([0-9a-f]+)\s+([0-9a-f`]+)\s+([0-9a-f`]+)\s+(.+)$",
            RegexOptions.IgnoreCase);

        if (frameMatch.Success)
        {
            var frameNumStr = frameMatch.Groups[1].Value;
            var retAddr = frameMatch.Groups[3].Value.Replace("`", "");
            var callSite = frameMatch.Groups[4].Value.Trim();

            // Parse module and function from call site
            var moduleFunc = ParseModuleFunction(callSite);

            if (int.TryParse(frameNumStr, System.Globalization.NumberStyles.HexNumber, null, out int frameNum))
            {
                return new StackFrame
                {
                    FrameNumber = frameNum,
                    InstructionPointer = retAddr,
                    Module = moduleFunc.Module,
                    Function = moduleFunc.Function,
                    Source = moduleFunc.Source,
                    IsManaged = false
                };
            }
        }

        // Try alternate format without Child-SP (kp, kv commands may vary)
        var altMatch = Regex.Match(line,
            @"^\s*([0-9a-f]+)\s+([0-9a-f`]+)\s+(.+)$",
            RegexOptions.IgnoreCase);

        if (altMatch.Success && !line.Contains("Child-SP") && !line.Contains("RetAddr"))
        {
            var frameNumStr = altMatch.Groups[1].Value;
            var retAddr = altMatch.Groups[2].Value.Replace("`", "");
            var callSite = altMatch.Groups[3].Value.Trim();

            var moduleFunc = ParseModuleFunction(callSite);

            if (int.TryParse(frameNumStr, System.Globalization.NumberStyles.HexNumber, null, out int frameNum))
            {
                return new StackFrame
                {
                    FrameNumber = frameNum,
                    InstructionPointer = retAddr,
                    Module = moduleFunc.Module,
                    Function = moduleFunc.Function,
                    Source = moduleFunc.Source,
                    IsManaged = false
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Parses module!function+offset format into components.
    /// </summary>
    private static (string Module, string Function, string? Source) ParseModuleFunction(string callSite)
    {
        string module = "";
        string function = callSite;
        string? source = null;

        // Check for source info at the end: [d:\path\file.cpp @ 123]
        var sourceMatch = Regex.Match(callSite, @"\[(.+?)\s*@\s*(\d+)\]$");
        if (sourceMatch.Success)
        {
            source = $"{sourceMatch.Groups[1].Value}:{sourceMatch.Groups[2].Value}";
            callSite = callSite.Substring(0, sourceMatch.Index).Trim();
        }

        // Parse module!function+offset
        var bangIndex = callSite.IndexOf('!');
        if (bangIndex > 0)
        {
            module = callSite.Substring(0, bangIndex);
            function = callSite.Substring(bangIndex + 1);
        }

        // Remove offset from function name for cleaner display
        var plusIndex = function.IndexOf('+');
        if (plusIndex > 0)
        {
            function = function.Substring(0, plusIndex);
        }

        return (module, function.Trim(), source);
    }

    /// <summary>
    /// Parses WinDbg threads output from '~' command.
    /// Format: "   0  Id: 1234.5678 Suspend: 1 Teb: 00000000`12345678 Unfrozen"
    /// Current thread marked with '.' prefix, crashed thread with '#' prefix.
    /// </summary>
    protected virtual void ParseWinDbgThreads(string output, CrashAnalysisResult result)
    {
        var lines = output.Split('\n');

        foreach (var line in lines)
        {
            // Match thread pattern: [.#] num  Id: proc.thread Suspend: n Teb: addr State
            // Examples:
            //    0  Id: 1234.5678 Suspend: 1 Teb: 00000000`12345678 Unfrozen
            //  . 1  Id: 1234.9abc Suspend: 1 Teb: 00000000`12345abc Unfrozen "ThreadName"
            //  # 2  Id: 1234.def0 Suspend: 1 Teb: 00000000`12345def Unfrozen
            var threadMatch = Regex.Match(line,
                @"^\s*([.#\s])\s*(\d+)\s+Id:\s*([0-9a-f]+)\.([0-9a-f]+)\s+Suspend:\s*(\d+)\s+Teb:\s*([0-9a-f`]+)\s+(\w+)",
                RegexOptions.IgnoreCase);

            if (threadMatch.Success)
            {
                var marker = threadMatch.Groups[1].Value.Trim();
                var debuggerThreadId = threadMatch.Groups[2].Value;
                var processId = threadMatch.Groups[3].Value;
                var osThreadId = threadMatch.Groups[4].Value;
                var suspendCount = threadMatch.Groups[5].Value;
                var state = threadMatch.Groups[7].Value;

                // Determine state description
                var stateDesc = state;
                if (suspendCount != "0" && suspendCount != "1")
                {
                    stateDesc = $"{state} (Suspend: {suspendCount})";
                }

                // Check for thread name in quotes at end
                var nameMatch = Regex.Match(line, @"""([^""]+)""$");
                var threadName = nameMatch.Success ? nameMatch.Groups[1].Value : null;

                var threadInfo = new ThreadInfo
                {
                    ThreadId = threadName != null ? $"{debuggerThreadId} ({osThreadId}) \"{threadName}\"" : $"{debuggerThreadId} ({osThreadId})",
                    State = stateDesc,
                    IsFaulting = marker == "#" || marker == ".",
                    TopFunction = threadName ?? ""
                };
                result.Threads!.All!.Add(threadInfo);
            }
            else
            {
                // Try simpler pattern for non-standard output
                var simpleMatch = Regex.Match(line, @"^\s*([.#\s])\s*(\d+)\s+Id:\s*([0-9a-f.]+)", RegexOptions.IgnoreCase);
                if (simpleMatch.Success)
                {
                    var marker = simpleMatch.Groups[1].Value.Trim();
                    var debuggerThreadId = simpleMatch.Groups[2].Value;
                    var ids = simpleMatch.Groups[3].Value;

                    var threadInfo = new ThreadInfo
                    {
                        ThreadId = $"{debuggerThreadId} ({ids})",
                        State = "Unknown",
                        IsFaulting = marker == "#" || marker == "."
                    };
                    result.Threads!.All!.Add(threadInfo);
                }
            }
        }
    }

    /// <summary>
    /// Parses WinDbg modules output from 'lm' command.
    /// Format: "start             end                 module name"
    ///         "00007ff8`12340000 00007ff8`12345000   ntdll      (pdb symbols)  path\ntdll.pdb"
    /// </summary>
    protected virtual void ParseWinDbgModules(string output, CrashAnalysisResult result)
    {
        var lines = output.Split('\n');

        foreach (var line in lines)
        {
            // Match module pattern: start end modulename (symbol status) symbol path
            // Example: "00007ff8`12340000 00007ff8`12345000   ntdll      (pdb symbols)  c:\symbols\ntdll.pdb\...\ntdll.pdb"
            var moduleMatch = Regex.Match(line,
                @"^\s*([0-9a-f`]+)\s+([0-9a-f`]+)\s+(\S+)\s+(?:\(([^)]+)\))?",
                RegexOptions.IgnoreCase);

            if (moduleMatch.Success)
            {
                var startAddr = moduleMatch.Groups[1].Value.Replace("`", "");
                var endAddr = moduleMatch.Groups[2].Value.Replace("`", "");
                var moduleName = moduleMatch.Groups[3].Value;
                var symbolStatus = moduleMatch.Groups[4].Success ? moduleMatch.Groups[4].Value : "";

                // Determine symbol status
                var hasSymbols = symbolStatus.Contains("pdb", StringComparison.OrdinalIgnoreCase) ||
                                 symbolStatus.Contains("symbols", StringComparison.OrdinalIgnoreCase) ||
                                 symbolStatus.Contains("private", StringComparison.OrdinalIgnoreCase);

                // Skip header lines
                if (moduleName.Equals("module", StringComparison.OrdinalIgnoreCase) ||
                    moduleName.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Try to extract version from module name or symbol status
                string? version = null;
                var versionMatch = Regex.Match(line, @"(\d+\.\d+\.\d+\.\d+)");
                if (versionMatch.Success)
                {
                    version = versionMatch.Groups[1].Value;
                }

                result.Modules ??= new List<ModuleInfo>();
                result.Modules.Add(new ModuleInfo
                {
                    Name = moduleName,
                    BaseAddress = $"0x{startAddr}",
                    Version = version,
                    HasSymbols = hasSymbols
                });
            }
            else
            {
                // Try alternate format for deferred modules: "module_name   (deferred)"
                var deferredMatch = Regex.Match(line, @"^\s*([0-9a-f`]+)\s+([0-9a-f`]+)\s+(\S+)\s*$", RegexOptions.IgnoreCase);
                if (deferredMatch.Success)
                {
                    var startAddr = deferredMatch.Groups[1].Value.Replace("`", "");
                    var moduleName = deferredMatch.Groups[3].Value;

                    if (!moduleName.Equals("module", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Modules ??= new List<ModuleInfo>();
                        result.Modules.Add(new ModuleInfo
                        {
                            Name = moduleName,
                            BaseAddress = $"0x{startAddr}",
                            HasSymbols = false
                        });
                    }
                }
            }
        }
    }

    /// <summary>
    /// Parses LLDB threads output from 'thread list' command.
    /// Format: "* thread #1: tid = 0x1234, 0x00007fff12345678 libsystem_kernel.dylib`__psynch_cvwait + 10, name = 'main', queue = 'com.apple.main-thread', stop reason = signal SIGSTOP"
    /// </summary>
    protected virtual void ParseLldbThreads(string output, CrashAnalysisResult result)
    {
        var lines = output.Split('\n');

        foreach (var line in lines)
        {
            // Match thread pattern: [*] thread #N: tid = TID, address function, name = 'name', queue = 'queue', stop reason = reason
            // Note: tid can be hex (0xHEX) OR decimal depending on LLDB version/platform
            var threadMatch = Regex.Match(line,
                @"^(\*?)\s*thread\s*#(\d+):\s*tid\s*=\s*(0x[0-9a-f]+|\d+)(?:,\s*([^,]+))?",
                RegexOptions.IgnoreCase);

            if (threadMatch.Success)
            {
                var isCurrent = threadMatch.Groups[1].Value == "*";
                var threadNum = threadMatch.Groups[2].Value;
                var tid = threadMatch.Groups[3].Value;
                var addressFunction = threadMatch.Groups[4].Success ? threadMatch.Groups[4].Value.Trim() : "";

                // Parse name if present
                var nameMatch = Regex.Match(line, @"name\s*=\s*'([^']*)'");
                var threadName = nameMatch.Success ? nameMatch.Groups[1].Value : null;

                // Parse queue if present
                var queueMatch = Regex.Match(line, @"queue\s*=\s*'([^']*)'");
                var queueName = queueMatch.Success ? queueMatch.Groups[1].Value : null;

                // Parse stop reason if present
                var stopMatch = Regex.Match(line, @"stop reason\s*=\s*(.+)$");
                var stopReason = stopMatch.Success ? stopMatch.Groups[1].Value.Trim() : "";

                // Build state string
                var state = !string.IsNullOrEmpty(stopReason) ? stopReason : "Running";
                if (!string.IsNullOrEmpty(queueName))
                {
                    state += $" (queue: {queueName})";
                }

                // Build thread ID string
                var threadIdStr = $"{threadNum} (tid: {tid})";
                if (!string.IsNullOrEmpty(threadName))
                {
                    threadIdStr += $" \"{threadName}\"";
                }

                // Parse top function from address/function
                var topFunction = addressFunction;
                var funcMatch = Regex.Match(addressFunction, @"(\S+)`(\S+)");
                if (funcMatch.Success)
                {
                    topFunction = $"{funcMatch.Groups[1].Value}!{funcMatch.Groups[2].Value}";
                }

                // Determine if this thread is faulting:
                // - Current thread (*) is typically faulting
                // - Fault signals: SIGABRT, SIGSEGV, SIGBUS, SIGFPE, SIGILL, etc.
                // - "signal 0" just means stopped, NOT faulting
                // - Exceptions indicate faulting
                var isFaulting = isCurrent;
                if (!isFaulting && !string.IsNullOrEmpty(stopReason))
                {
                    // Check for actual fault signals (not just "signal 0" which means stopped)
                    isFaulting = Regex.IsMatch(stopReason, @"SIG(ABRT|SEGV|BUS|FPE|ILL|TRAP|KILL)", RegexOptions.IgnoreCase) ||
                                 stopReason.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                                 stopReason.Contains("EXC_", StringComparison.OrdinalIgnoreCase);
                }

                var threadInfo = new ThreadInfo
                {
                    ThreadId = threadIdStr,
                    State = state,
                    IsFaulting = isFaulting,
                    TopFunction = topFunction
                };
                result.Threads!.All!.Add(threadInfo);
            }
        }
    }

    /// <summary>
    /// Parses LLDB 'bt all' output and assigns frames to each thread.
    /// Format: "* thread #1, queue = '...', stop reason = signal SIGABRT"
    ///         "  * frame #0: 0x... module`function"
    ///         "    frame #1: 0x... module`function"
    ///         "  thread #2, name = '...'"
    ///         "    frame #0: 0x... module`function"
    /// </summary>
    protected virtual void ParseLldbBacktraceAll(string output, CrashAnalysisResult result)
    {
        // Ensure Threads.All is initialized (for tests that call this directly)
        result.Threads ??= new ThreadsInfo { All = new List<ThreadInfo>(), Summary = new ThreadSummary() };
        result.Threads.All ??= new List<ThreadInfo>();

        var lines = output.Split('\n');
        ThreadInfo? currentThread = null;

        foreach (var line in lines)
        {
            // Check for thread header: "* thread #N" or "  thread #N"
            var threadMatch = Regex.Match(line, @"^\s*[*\s]*thread\s+#(\d+)", RegexOptions.IgnoreCase);
            if (threadMatch.Success)
            {
                var threadIndex = int.Parse(threadMatch.Groups[1].Value);
                // Find matching thread in result.Threads.All
                // Thread IDs can be complex like "1 (tid: 35156) \"dotnet\""
                currentThread = FindThreadByIndex(result.Threads!.All!, threadIndex);
                continue;
            }

            // Parse frame and add to current thread
            if (currentThread != null)
            {
                var frame = ParseSingleFrame(line);
                if (frame != null)
                {
                    currentThread.CallStack.Add(frame);
                }
            }
        }
    }

    /// <summary>
    /// Finds a thread by its 1-based index, handling complex thread ID formats.
    /// Thread IDs can be: "1", "1 (tid: 35156)", "1 (tid: 35156) \"name\"", etc.
    /// </summary>
    protected static ThreadInfo? FindThreadByIndex(List<ThreadInfo> threads, int threadIndex)
    {
        // First try direct match: ThreadId starts with the number followed by space or is exactly the number
        var thread = threads.FirstOrDefault(t =>
            t.ThreadId == threadIndex.ToString() ||
            t.ThreadId.StartsWith($"{threadIndex} ") ||
            t.ThreadId.StartsWith($"{threadIndex}("));

        if (thread != null) return thread;

        // Try by position (1-based index to 0-based)
        if (threadIndex > 0 && threadIndex <= threads.Count)
        {
            return threads[threadIndex - 1];
        }

        return null;
    }

    /// <summary>
    /// Parses a single stack frame line from LLDB output.
    /// Format: "frame #N: 0xADDR module`function(args) + offset at file:line"
    /// </summary>
    private StackFrame? ParseSingleFrame(string line)
    {
        // Match frame pattern with module`function (backtick separator)
        // Note: Use (.+?) for function to capture complex C++ signatures with spaces
        var frameMatch = Regex.Match(line,
            @"^\s*[*\s]*frame\s*#(\d+):\s*(0x[0-9a-f]+)\s+(\S+)`(.+?)(?:\s+\+\s+\d+)?(?:\s+at\s+(.+))?$",
            RegexOptions.IgnoreCase);

        if (frameMatch.Success)
        {
            var frameNum = int.Parse(frameMatch.Groups[1].Value);
            var address = frameMatch.Groups[2].Value;
            var moduleName = frameMatch.Groups[3].Value;
            var functionName = frameMatch.Groups[4].Value.Trim();
            var sourceInfo = frameMatch.Groups[5].Success ? frameMatch.Groups[5].Value.Trim() : null;

            // Clean up function name - remove trailing " + N" if regex didn't catch it
            var plusIdx = functionName.LastIndexOf(" + ");
            if (plusIdx > 0 && Regex.IsMatch(functionName.Substring(plusIdx + 3), @"^\d+$"))
            {
                functionName = functionName.Substring(0, plusIdx);
            }

            return new StackFrame
            {
                FrameNumber = frameNum,
                InstructionPointer = address,
                Module = moduleName,
                Function = functionName,
                Source = sourceInfo,
                IsManaged = false
            };
        }

        // Try pattern for frames without backtick separator (e.g., "libstdc++.so.6 + 123")
        var noBacktickMatch = Regex.Match(line,
            @"^\s*[*\s]*frame\s*#(\d+):\s*(0x[0-9a-f]+)\s+(\S+\.(?:so|dylib)(?:\.\d+)*)(?:\s+\+\s+(-?\d+))?(?:\s+at\s+(.+))?$",
            RegexOptions.IgnoreCase);

        if (noBacktickMatch.Success)
        {
            var frameNum = int.Parse(noBacktickMatch.Groups[1].Value);
            var address = noBacktickMatch.Groups[2].Value;
            var libraryName = noBacktickMatch.Groups[3].Value;
            var sourceInfo = noBacktickMatch.Groups[5].Success ? noBacktickMatch.Groups[5].Value.Trim() : null;

            return new StackFrame
            {
                FrameNumber = frameNum,
                InstructionPointer = address,
                Module = libraryName,
                Function = $"[Native Code @ {address}]",
                Source = sourceInfo,
                IsManaged = false
            };
        }

        // Try simpler pattern for any remaining format
        var simpleMatch = Regex.Match(line,
            @"^\s*[*\s]*frame\s*#(\d+):\s*(0x[0-9a-f]+)\s+(.+)$",
            RegexOptions.IgnoreCase);

        if (simpleMatch.Success)
        {
            var frameNum = int.Parse(simpleMatch.Groups[1].Value);
            var address = simpleMatch.Groups[2].Value;
            var rest = simpleMatch.Groups[3].Value.Trim();

            // Try to extract module`function with backtick
            string moduleName = "";
            string functionName = rest;
            string? sourceInfo = null;

            // Check for source info first: "at file:line" at the end
            var srcMatch = Regex.Match(rest, @"\s+at\s+(.+)$");
            if (srcMatch.Success)
            {
                sourceInfo = srcMatch.Groups[1].Value.Trim();
                rest = rest.Substring(0, srcMatch.Index).Trim();
            }

            // Remove " + offset" from end
            var plusMatch = Regex.Match(rest, @"\s+\+\s+-?\d+$");
            if (plusMatch.Success)
            {
                rest = rest.Substring(0, plusMatch.Index).Trim();
            }

            // Try to split on backtick
            var backtickIdx = rest.IndexOf('`');
            if (backtickIdx > 0)
            {
                moduleName = rest.Substring(0, backtickIdx);
                functionName = rest.Substring(backtickIdx + 1);
            }
            else
            {
                // No backtick - might be just a library name
                functionName = $"[{rest}]";
            }

            return new StackFrame
            {
                FrameNumber = frameNum,
                InstructionPointer = address,
                Module = moduleName,
                Function = functionName,
                Source = sourceInfo,
                IsManaged = false
            };
        }

        return null;
    }

    /// <summary>
    /// Parses LLDB backtrace output from 'bt' command (single thread).
    /// Format: "* frame #0: 0x00007fff12345678 libsystem_kernel.dylib`__psynch_cvwait + 10"
    ///         "  frame #1: 0x00007fff12345abc libsystem_pthread.dylib`_pthread_cond_wait + 722 at pthread_cond.c:123"
    /// </summary>
    protected virtual void ParseLldbBacktrace(string output, List<StackFrame> callStack)
    {
        var lines = output.Split('\n');

        foreach (var line in lines)
        {
            var frame = ParseSingleFrame(line);
            if (frame != null)
            {
                callStack.Add(frame);
            }
        }
    }

    /// <summary>
    /// Parses LLDB modules output from 'image list' command.
    /// Format: "[  0] UUID 0x0000000000001000 /path/to/module"
    ///         "[  0] 12345678-1234-1234-1234-123456789ABC 0x0000000100000000 /usr/lib/dyld"
    /// Debug symbols may appear on a following line: "/path/to/debug/module.debug"
    /// </summary>
    protected virtual void ParseLldbModules(string output, CrashAnalysisResult result)
    {
        var lines = output.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Match image list pattern: [idx] UUID address path
            // Example: "[  0] 12345678-1234-1234-1234-123456789ABC 0x0000000100000000 /usr/lib/dyld"
            var moduleMatch = Regex.Match(line,
                @"^\s*\[\s*\d+\]\s+([0-9A-F-]+)\s+(0x[0-9a-f]+)\s+(.+)$",
                RegexOptions.IgnoreCase);

            if (moduleMatch.Success)
            {
                var uuid = moduleMatch.Groups[1].Value;
                var baseAddress = moduleMatch.Groups[2].Value;
                var fullPath = moduleMatch.Groups[3].Value.Trim();

                // Extract module name from path
                var moduleName = fullPath;
                var lastSlash = fullPath.LastIndexOf('/');
                if (lastSlash >= 0)
                {
                    moduleName = fullPath.Substring(lastSlash + 1);
                }

                // Check for symbols:
                // 1. .dSYM in path (macOS debug symbols)
                // 2. "symbols" mentioned in line
                // 3. Check if next line has .debug/.dbg file (Linux debug symbols)
                var hasSymbols = fullPath.Contains(".dSYM", StringComparison.OrdinalIgnoreCase) ||
                                 line.Contains("symbols", StringComparison.OrdinalIgnoreCase);

                // Check next line for debug info (Linux format: debug file on separate line)
                if (!hasSymbols && i + 1 < lines.Length)
                {
                    var nextLine = lines[i + 1];
                    // Debug info line doesn't start with [ and contains .debug or .dbg
                    if (!nextLine.TrimStart().StartsWith("[") &&
                        (nextLine.Contains(".debug", StringComparison.OrdinalIgnoreCase) ||
                         nextLine.Contains(".dbg", StringComparison.OrdinalIgnoreCase) ||
                         nextLine.Contains("/debug/", StringComparison.OrdinalIgnoreCase)))
                    {
                        hasSymbols = true;
                        i++; // Skip the debug line
                    }
                }

                result.Modules ??= new List<ModuleInfo>();
                result.Modules.Add(new ModuleInfo
                {
                    Name = moduleName,
                    BaseAddress = baseAddress,
                    Version = null, // LLDB doesn't show version in image list
                    HasSymbols = hasSymbols
                });
            }
            else
            {
                // Try alternate format: just path or simpler format
                var simpleMatch = Regex.Match(line,
                    @"^\s*\[\s*\d+\]\s+(0x[0-9a-f]+)\s+(.+)$",
                    RegexOptions.IgnoreCase);

                if (simpleMatch.Success)
                {
                    var baseAddress = simpleMatch.Groups[1].Value;
                    var fullPath = simpleMatch.Groups[2].Value.Trim();

                    var moduleName = fullPath;
                    var lastSlash = fullPath.LastIndexOf('/');
                    if (lastSlash >= 0)
                    {
                        moduleName = fullPath.Substring(lastSlash + 1);
                    }

                    result.Modules ??= new List<ModuleInfo>();
                    result.Modules.Add(new ModuleInfo
                    {
                        Name = moduleName,
                        BaseAddress = baseAddress,
                        HasSymbols = fullPath.Contains(".dSYM", StringComparison.OrdinalIgnoreCase) ||
                                     fullPath.Contains(".dbg", StringComparison.OrdinalIgnoreCase) ||
                                     fullPath.Contains(".debug", StringComparison.OrdinalIgnoreCase)
                    });
                }
                else if (line.Contains("/") && (line.Contains(".so") || line.Contains(".dylib") || line.Contains(".framework")))
                {
                    // Fallback: just extract module from path
                    var pathMatch = Regex.Match(line, @"(/\S+)");
                    if (pathMatch.Success)
                    {
                        var fullPath = pathMatch.Groups[1].Value;
                        var moduleName = fullPath.Substring(fullPath.LastIndexOf('/') + 1);

                        // Try to get address
                        var addrMatch = Regex.Match(line, @"(0x[0-9a-f]+)");
                        var baseAddr = addrMatch.Success ? addrMatch.Groups[1].Value : "Unknown";

                        result.Modules ??= new List<ModuleInfo>();
                        result.Modules.Add(new ModuleInfo
                        {
                            Name = moduleName,
                            BaseAddress = baseAddr,
                            HasSymbols = false
                        });
                    }
                }
            }
        }
    }

    /// <summary>
    /// Detects platform and architecture information from the dump.
    /// </summary>
    /// <param name="modulesOutput">The output from image list command.</param>
    /// <param name="result">The result object to populate.</param>
    protected virtual void DetectPlatformInfo(string modulesOutput, CrashAnalysisResult result)
    {
        // Initialize platform info in the new structure
        var platform = new PlatformInfo();
        result.Environment!.Platform = platform;

        // Detect OS and C library type from loader
        if (modulesOutput.Contains("ld-musl-", StringComparison.OrdinalIgnoreCase))
        {
            platform.Os = "Linux";
            platform.IsAlpine = true;
            platform.LibcType = "musl";
            platform.Distribution = "Alpine";
        }
        else if (modulesOutput.Contains("ld-linux-", StringComparison.OrdinalIgnoreCase) ||
                 modulesOutput.Contains("libc.so", StringComparison.OrdinalIgnoreCase))
        {
            platform.Os = "Linux";
            platform.IsAlpine = false;
            platform.LibcType = "glibc";

            // Try to detect distro from paths
            if (modulesOutput.Contains("/debian/", StringComparison.OrdinalIgnoreCase))
                platform.Distribution = "Debian";
            else if (modulesOutput.Contains("/ubuntu/", StringComparison.OrdinalIgnoreCase))
                platform.Distribution = "Ubuntu";
            else if (modulesOutput.Contains("/centos/", StringComparison.OrdinalIgnoreCase))
                platform.Distribution = "CentOS";
            else if (modulesOutput.Contains("/rhel/", StringComparison.OrdinalIgnoreCase))
                platform.Distribution = "RHEL";
            else if (modulesOutput.Contains("/fedora/", StringComparison.OrdinalIgnoreCase))
                platform.Distribution = "Fedora";
        }
        else if (modulesOutput.Contains("/usr/lib/dyld", StringComparison.OrdinalIgnoreCase) ||
                 modulesOutput.Contains(".dylib", StringComparison.OrdinalIgnoreCase))
        {
            platform.Os = "macOS";
        }
        else if (modulesOutput.Contains("ntdll", StringComparison.OrdinalIgnoreCase) ||
                 modulesOutput.Contains("kernel32", StringComparison.OrdinalIgnoreCase))
        {
            platform.Os = "Windows";
        }

        // Detect architecture from loader or module paths
        if (modulesOutput.Contains("aarch64", StringComparison.OrdinalIgnoreCase) ||
            modulesOutput.Contains("arm64", StringComparison.OrdinalIgnoreCase))
        {
            platform.Architecture = "arm64";
            platform.PointerSize = 64;
        }
        else if (modulesOutput.Contains("x86_64", StringComparison.OrdinalIgnoreCase) ||
                 modulesOutput.Contains("x86-64", StringComparison.OrdinalIgnoreCase) ||
                 modulesOutput.Contains("amd64", StringComparison.OrdinalIgnoreCase))
        {
            platform.Architecture = "x64";
            platform.PointerSize = 64;
        }
        else if (modulesOutput.Contains("i386", StringComparison.OrdinalIgnoreCase) ||
                 modulesOutput.Contains("i686", StringComparison.OrdinalIgnoreCase))
        {
            platform.Architecture = "x86";
            platform.PointerSize = 32;
        }
        else if (modulesOutput.Contains("armhf", StringComparison.OrdinalIgnoreCase) ||
                 modulesOutput.Contains("arm-linux", StringComparison.OrdinalIgnoreCase))
        {
            platform.Architecture = "arm";
            platform.PointerSize = 32;
        }

        // Try to detect architecture from address size if not yet determined
        if (string.IsNullOrEmpty(platform.Architecture))
        {
            // Look at base addresses - 64-bit addresses are longer
            var addrMatch = Regex.Match(modulesOutput, @"0x([0-9a-f]+)", RegexOptions.IgnoreCase);
            if (addrMatch.Success)
            {
                var addrLength = addrMatch.Groups[1].Value.TrimStart('0').Length;
                if (addrLength > 8)
                {
                    platform.PointerSize = 64;
                    platform.Architecture = "x64"; // Assume x64 if not detected
                }
                else
                {
                    platform.PointerSize = 32;
                    platform.Architecture = "x86"; // Assume x86 if not detected
                }
            }
        }
    }

    /// <summary>
    /// Extracts process startup information (argv and envp) from LLDB core dumps.
    /// </summary>
    /// <param name="result">The result object to populate.</param>
    /// <param name="backtraceOutput">The backtrace output from 'bt all' command.</param>
    protected virtual async Task ExtractProcessInfoAsync(CrashAnalysisResult result, string? backtraceOutput)
    {
        try
        {
            var extractor = new ProcessInfoExtractor();
            result.Environment!.Process = await extractor.ExtractProcessInfoAsync(
                _debuggerManager,
                result.Environment.Platform,
                backtraceOutput,
                result.RawCommands);
        }
        catch
        {
            // Don't fail the entire analysis - this is optional enrichment.
        }
    }

    /// <summary>
    /// Analyzes for memory leaks using WinDbg commands.
    /// </summary>
    /// <param name="result">The result object to populate.</param>
    protected virtual async Task AnalyzeMemoryLeaksWinDbgAsync(CrashAnalysisResult result)
    {
        // Get heap summary using !heap -s
        var heapOutput = await ExecuteCommandAsync("!heap -s");
        result.RawCommands!["!heap -s"] = heapOutput;

        // Initialize leak analysis in the new structure
        result.Memory!.LeakAnalysis = new LeakAnalysis();

        // Parse heap output for large allocations
        // Format: "Heap at <address>\n  Committed bytes:  <size>"
        var committedMatches = Regex.Matches(heapOutput, @"Committed bytes:\s+([0-9a-fx]+)", RegexOptions.IgnoreCase);
        long totalCommitted = 0;

        foreach (Match match in committedMatches)
        {
            if (TryParseHexOrDecimal(match.Groups[1].Value, out long bytes))
            {
                totalCommitted += bytes;
            }
        }

        result.Memory.LeakAnalysis.TotalHeapBytes = totalCommitted;

        // Check for large heaps - this is high consumption, not necessarily a leak
        if (totalCommitted > 2_000_000_000) // > 2GB
        {
            result.Memory.LeakAnalysis.Detected = true;
            result.Memory.LeakAnalysis.Severity = "High";
            var recMsg = $"High heap usage detected ({totalCommitted:N0} bytes). Use memory profiling with multiple snapshots to identify actual leaks.";
            result.Summary!.Recommendations ??= new List<string>();
            result.Summary.Recommendations.Add(recMsg);
        }
        else if (totalCommitted > 500_000_000) // > 500MB
        {
            result.Memory.LeakAnalysis.Severity = "Elevated";
        }
        else
        {
            result.Memory.LeakAnalysis.Severity = "Normal";
        }

        // Get heap statistics for top consumers
        var heapStatOutput = await ExecuteCommandAsync("!heap -stat -h 0");
        result.RawCommands!["!heap -stat -h 0"] = heapStatOutput;

        // Parse top allocators - format varies, look for size/count patterns
        result.Memory.LeakAnalysis.TopConsumers ??= new List<MemoryConsumer>();
        var allocPatterns = Regex.Matches(heapStatOutput, @"size\s+([0-9a-fx]+)\s+.*?count:\s*(\d+)", RegexOptions.IgnoreCase);
        foreach (Match match in allocPatterns.Take(10))
        {
            if (TryParseHexOrDecimal(match.Groups[1].Value, out long size))
            {
                var count = long.Parse(match.Groups[2].Value);
                result.Memory.LeakAnalysis.TopConsumers.Add(new MemoryConsumer
                {
                    TypeName = $"Allocation size {size}",
                    Count = count,
                    TotalSize = size * count
                });
            }
        }
    }

    /// <summary>
    /// Analyzes for deadlocks using WinDbg commands.
    /// </summary>
    /// <param name="result">The result object to populate.</param>
    protected virtual async Task AnalyzeDeadlocksWinDbgAsync(CrashAnalysisResult result)
    {
        // Get lock information using !locks
        var locksOutput = await ExecuteCommandAsync("!locks");
        result.RawCommands!["!locks"] = locksOutput;

        // Initialize deadlock info in the new structure
        result.Threads!.Deadlock = new DeadlockInfo();
        var deadlockInfo = result.Threads.Deadlock;

        // Parse critical sections
        // Format: "CritSec <name> at <address>\n  LockCount          X\n  OwningThread       Y"
        var critSecMatches = Regex.Matches(locksOutput,
            @"CritSec\s+(\S+)\s+at\s+([0-9a-fx`]+).*?OwningThread\s+([0-9a-fx`]+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var ownerToLocks = new Dictionary<string, List<string>>();

        foreach (Match match in critSecMatches)
        {
            var lockName = match.Groups[1].Value;
            var lockAddress = match.Groups[2].Value;
            var ownerThread = match.Groups[3].Value;

            if (!ownerToLocks.ContainsKey(ownerThread))
            {
                ownerToLocks[ownerThread] = new List<string>();
            }
            ownerToLocks[ownerThread].Add(lockAddress);

            deadlockInfo.Locks.Add(new LockInfo
            {
                Address = lockAddress,
                Owner = ownerThread,
                Waiters = new List<string>()
            });
        }

        // Look for RecursionCount > 0 or multiple locks held by same thread
        var recursionMatches = Regex.Matches(locksOutput, @"RecursionCount\s+(\d+)");
        var hasRecursion = recursionMatches.Cast<Match>().Any(m => int.Parse(m.Groups[1].Value) > 1);

        // Check for potential deadlock indicators
        if (ownerToLocks.Count > 1)
        {
            // Multiple threads holding locks - potential for deadlock
            var waitingOutput = await ExecuteCommandAsync("!runaway");
            result.RawCommands!["!runaway"] = waitingOutput;

            // Look for threads with high wait times
            var waitMatches = Regex.Matches(waitingOutput, @"(\d+):([0-9a-f]+)\s+(\d+)\s+days?\s+(\d+):(\d+):(\d+)", RegexOptions.IgnoreCase);
            foreach (Match match in waitMatches)
            {
                var threadId = match.Groups[1].Value;
                var days = int.Parse(match.Groups[3].Value);
                var hours = int.Parse(match.Groups[4].Value);

                // Threads waiting more than 1 minute could be deadlocked
                if (days > 0 || hours > 0)
                {
                    deadlockInfo.InvolvedThreads.Add(threadId);
                }
            }

            if (deadlockInfo.InvolvedThreads.Count >= 2)
            {
                deadlockInfo.Detected = true;
                result.Summary!.CrashType = "Potential Deadlock";
                result.Summary.Recommendations ??= new List<string>();
                result.Summary.Recommendations.Add($"Potential deadlock detected involving {deadlockInfo.InvolvedThreads.Count} threads. Review lock acquisition order.");
            }
        }

        if (hasRecursion)
        {
            result.Summary!.Recommendations ??= new List<string>();
            result.Summary.Recommendations.Add("Recursive lock acquisition detected. Ensure proper lock release patterns.");
        }
    }

    /// <summary>
    /// Analyzes for memory leaks using LLDB commands.
    /// </summary>
    /// <param name="result">The result object to populate.</param>
    protected virtual async Task AnalyzeMemoryLeaksLldbAsync(CrashAnalysisResult result)
    {
        // Initialize leak analysis in the new structure
        result.Memory!.LeakAnalysis = new LeakAnalysis();

        // Get process memory info
        var processInfoOutput = await ExecuteCommandAsync("process status");
        result.RawCommands!["process status"] = processInfoOutput;

        // Try to get memory regions (may not be available in all dumps)
        var memoryOutput = await ExecuteCommandAsync("memory region --all");
        result.RawCommands!["memory region --all"] = memoryOutput;

        // Parse memory regions for total allocated
        // Format: "[0x00000000-0x00001000) r-x ..."
        var regionMatches = Regex.Matches(memoryOutput, @"\[(0x[0-9a-f]+)-(0x[0-9a-f]+)\)", RegexOptions.IgnoreCase);
        long totalMemory = 0;

        foreach (Match match in regionMatches)
        {
            if (TryParseHexOrDecimal(match.Groups[1].Value, out long start) &&
                TryParseHexOrDecimal(match.Groups[2].Value, out long end))
            {
                totalMemory += (end - start);
            }
        }

        result.Memory.LeakAnalysis.TotalHeapBytes = totalMemory;

        // Check for large memory - this is high consumption, not necessarily a leak
        if (totalMemory > 2_000_000_000) // > 2GB
        {
            result.Memory.LeakAnalysis.Detected = true;
            result.Memory.LeakAnalysis.Severity = "High";
            result.Summary!.Recommendations ??= new List<string>();
            result.Summary.Recommendations.Add($"High memory footprint ({totalMemory:N0} bytes). Use memory profiling with multiple snapshots to identify actual leaks.");
        }
        else if (totalMemory > 500_000_000) // > 500MB
        {
            result.Memory.LeakAnalysis.Severity = "Elevated";
        }
        else
        {
            result.Memory.LeakAnalysis.Severity = "Normal";
        }

        // Note: macOS heap analysis (lldb.macosx.heap) removed - only works on macOS
        // and causes Python errors on Linux. For .NET dumps, use !dumpheap -stat instead.
    }

    /// <summary>
    /// Analyzes for deadlocks using LLDB commands.
    /// </summary>
    /// <param name="result">The result object to populate.</param>
    protected virtual async Task AnalyzeDeadlocksLldbAsync(CrashAnalysisResult result)
    {
        // Initialize deadlock info in the new structure
        result.Threads!.Deadlock = new DeadlockInfo();
        var deadlockInfo = result.Threads.Deadlock;

        // Get detailed thread info with backtraces (all threads)
        // Use actual command as key: "bt all" for LLDB, "~*k" for WinDbg
        var rawCommands = result.RawCommands ?? new Dictionary<string, string>();
        var threadBacktraces = rawCommands.GetValueOrDefault("bt all",
                               rawCommands.GetValueOrDefault("~*k", ""));

        // Look for threads waiting on locks
        // Common patterns: pthread_mutex_lock, __psynch_mutexwait, semaphore_wait
        var waitingThreads = new List<string>();
        var lockPatterns = new[] { "pthread_mutex", "psynch_mutex", "semaphore_wait", "__lock", "OSSpinLock", "os_unfair_lock" };

        var threadSections = Regex.Split(threadBacktraces, @"(?=\* thread #|\s+thread #)");
        foreach (var section in threadSections)
        {
            if (string.IsNullOrWhiteSpace(section)) continue;

            var threadMatch = Regex.Match(section, @"thread #(\d+)");
            if (!threadMatch.Success) continue;

            var threadId = threadMatch.Groups[1].Value;
            var isWaiting = lockPatterns.Any(p => section.Contains(p, StringComparison.OrdinalIgnoreCase));

            if (isWaiting)
            {
                waitingThreads.Add(threadId);
                deadlockInfo.InvolvedThreads.Add(threadId);

                // Try to extract lock address
                var lockAddrMatch = Regex.Match(section, @"(0x[0-9a-f]+).*(?:mutex|lock|semaphore)", RegexOptions.IgnoreCase);
                if (lockAddrMatch.Success)
                {
                    deadlockInfo.Locks.Add(new LockInfo
                    {
                        Address = lockAddrMatch.Groups[1].Value,
                        Owner = "Unknown",
                        Waiters = new List<string> { threadId }
                    });
                }
            }
        }

        // If multiple threads are waiting on locks, potential deadlock
        if (waitingThreads.Count >= 2)
        {
            deadlockInfo.Detected = true;
            result.Summary!.CrashType = "Potential Deadlock";
            result.Summary.Recommendations ??= new List<string>();
            result.Summary.Recommendations.Add($"Potential deadlock detected: {waitingThreads.Count} threads waiting on locks. Check thread backtraces for lock acquisition order.");
        }
        else if (waitingThreads.Count == 1)
        {
            result.Summary!.Recommendations ??= new List<string>();
            result.Summary.Recommendations.Add("One thread waiting on a lock. Check if the lock owner is blocked.");
        }
    }

    /// <summary>
    /// Tries to parse a hex (0x...) or decimal number.
    /// </summary>
    protected static bool TryParseHexOrDecimal(string value, out long result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        value = value.Trim().Replace("`", ""); // WinDbg uses backticks in addresses

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(value.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        }

        return long.TryParse(value, out result);
    }

    /// <summary>
    /// Generates a summary of the analysis.
    /// </summary>
    protected virtual void GenerateSummary(CrashAnalysisResult result)
    {
        // Ensure new hierarchical properties are initialized (for tests that call this directly)
        result.Summary ??= new AnalysisSummary();
        result.Threads ??= new ThreadsInfo { All = new List<ThreadInfo>(), Summary = new ThreadSummary() };
        result.Threads.All ??= new List<ThreadInfo>();
        result.Threads.Summary ??= new ThreadSummary();

        // Count total frames across all threads
        var threads = result.Threads?.All ?? new List<ThreadInfo>();
        var totalFrames = threads.Sum(t => t.CallStack.Count);
        var faultingThread = threads.FirstOrDefault(t => t.IsFaulting);
        var faultingFrames = faultingThread?.CallStack.Count ?? 0;

        // Build description using new properties
        result.Summary ??= new AnalysisSummary();
        var crashType = result.Summary.CrashType ?? "Unknown";
        var description = result.Exception != null
            ? $"Crash Type: {crashType}. Exception: {result.Exception.Type}. "
            : $"Crash Type: {crashType}. ";
        description += $"Found {threads.Count} threads ({totalFrames} total frames, {faultingFrames} in faulting thread), {result.Modules?.Count ?? 0} modules. ";

        // Add memory consumption summary
        if (result.Memory?.LeakAnalysis?.Detected == true)
        {
            var severity = result.Memory.LeakAnalysis.Severity ?? "Elevated";
            description += $"MEMORY: {severity} consumption (~{result.Memory.LeakAnalysis.TotalHeapBytes:N0} bytes). ";
        }

        // Add deadlock summary
        if (result.Threads?.Deadlock?.Detected == true)
        {
            description += $"DEADLOCK DETECTED: {result.Threads.Deadlock.InvolvedThreads.Count} threads involved. ";
        }

        // Initialize recommendations list
        result.Summary.Recommendations ??= new List<string>();

        // Add basic recommendations
        if (totalFrames == 0)
        {
            result.Summary.Recommendations.Add("No call stack found. Ensure symbols are configured correctly.");
        }

        if (result.Modules != null && result.Modules.Any(m => !m.HasSymbols))
        {
            result.Summary.Recommendations.Add("Some modules are missing symbols. Upload symbol files for better analysis.");
        }

        // Populate the new Summary properties
        result.Summary.Description = description;
        result.Summary.Severity = DetermineSeverity(result);
        result.Summary.ThreadCount = threads.Count;
        result.Summary.ModuleCount = result.Modules?.Count ?? 0;

        // Update thread summary
        result.Threads!.Summary!.Total = threads.Count;
        result.Threads.FaultingThread = faultingThread;
    }

    /// <summary>
    /// Populates the new hierarchical structure from the existing flat structure.
    /// This bridges the old and new data models during the incremental migration.
    /// </summary>
    /// <summary>
    /// Determines the severity level based on analysis results.
    /// </summary>
    private static string DetermineSeverity(CrashAnalysisResult result)
    {
        if (result.Security?.OverallRisk == "Critical")
            return "critical";
        if (result.Threads?.Deadlock?.Detected == true)
            return "high";
        if (result.Memory?.LeakAnalysis?.Detected == true)
            return "medium";
        if (result.Exception != null)
            return "medium";
        return "info";
    }

    /// <summary>
    /// Converts the analysis result to JSON.
    /// </summary>
    /// <param name="result">The result to convert.</param>
    /// <returns>JSON string representation.</returns>
    public static string ToJson(CrashAnalysisResult result)
    {
        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    /// <summary>
    /// Resolves Source Link URLs for all stack frames in all threads.
    /// </summary>
    /// <param name="result">The crash analysis result containing stack frames.</param>
    protected virtual void ResolveSourceLinks(CrashAnalysisResult result)
    {
        if (_sourceLinkResolver == null)
        {
            // Log that we're skipping source link resolution
            Console.WriteLine("[SourceLink] ResolveSourceLinks: _sourceLinkResolver is null, skipping");
            return;
        }

        var allThreads = result.Threads?.All ?? new List<ThreadInfo>();
        var totalThreads = allThreads.Count;
        var totalFrames = allThreads.Sum(t => t.CallStack.Count);
        var framesWithSource = 0;
        var framesResolved = 0;

        // Log start of resolution
        Console.WriteLine($"[SourceLink] ResolveSourceLinks: Starting resolution for {totalThreads} threads, {totalFrames} frames");

        // Iterate through all threads and their call stacks
        foreach (var thread in allThreads)
        {
            foreach (var frame in thread.CallStack)
            {
                if (!string.IsNullOrEmpty(frame.SourceFile) || !string.IsNullOrEmpty(frame.Source))
                {
                    framesWithSource++;
                }
                if (ResolveFrameSourceLink(frame, result))
                {
                    framesResolved++;
                }
            }
        }

        // Log summary
        Console.WriteLine($"[SourceLink] ResolveSourceLinks: Completed - {framesWithSource} frames with source info, {framesResolved} resolved");
    }

    /// <summary>
    /// Resolves Source Link for a single frame.
    /// </summary>
    /// <returns>True if source link was resolved, false otherwise.</returns>
    private bool ResolveFrameSourceLink(StackFrame frame, CrashAnalysisResult result)
    {
        // Skip if no source info available
        if (string.IsNullOrEmpty(frame.Source) && (string.IsNullOrEmpty(frame.SourceFile) || !frame.LineNumber.HasValue))
        {
            return false;
        }

        // Extract source file and line number if not already set
        if (string.IsNullOrEmpty(frame.SourceFile) && !string.IsNullOrEmpty(frame.Source))
        {
            // Parse source from "file.cpp:123" or "file.cpp @ 123" format
            var match = Regex.Match(frame.Source, @"^(.+?)[:@]\s*(\d+)");
            if (match.Success)
            {
                frame.SourceFile = match.Groups[1].Value.Trim();
                if (int.TryParse(match.Groups[2].Value, out int line))
                {
                    frame.LineNumber = line;
                }
            }
        }

        // Skip if we still don't have source info
        if (string.IsNullOrEmpty(frame.SourceFile) || !frame.LineNumber.HasValue)
        {
            return false;
        }

        // Find the module path (try to locate it from the modules list)
        var modulePath = FindModulePath(frame.Module, result);
        if (string.IsNullOrEmpty(modulePath))
        {
            // Use module name as path if we can't find the full path
            modulePath = frame.Module;
        }

        // Resolve source link
        var location = _sourceLinkResolver!.Resolve(modulePath, frame.SourceFile, frame.LineNumber.Value);
        if (location.Resolved)
        {
            frame.SourceUrl = location.Url;
            frame.SourceProvider = location.Provider.ToString();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds the full path for a module from the loaded modules list.
    /// </summary>
    private static string? FindModulePath(string moduleName, CrashAnalysisResult result)
    {
        if (string.IsNullOrEmpty(moduleName))
        {
            return null;
        }

        // Try exact match first
        if (result.Modules == null || result.Modules.Count == 0)
        {
            return null;
        }

        var module = result.Modules.FirstOrDefault(m =>
            m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(m.Name).Equals(moduleName, StringComparison.OrdinalIgnoreCase));

        if (module != null)
        {
            return module.Name;
        }

        // Try partial match
        module = result.Modules.FirstOrDefault(m =>
            m.Name.Contains(moduleName, StringComparison.OrdinalIgnoreCase));

        return module?.Name;
    }

    /// <summary>
    /// Cleans up LLDB command output by removing known error patterns that shouldn't be there.
    /// This handles leftover Python errors from previous LLDB sessions or failed commands.
    /// </summary>
    protected static string CleanLldbOutput(string output)
    {
        if (string.IsNullOrEmpty(output))
            return output;

        // Remove Python traceback errors (from failed script commands)
        output = Regex.Replace(output,
            @"Traceback \(most recent call last\):.*?(?:NameError|SyntaxError|TypeError|AttributeError):[^\n]*\n?",
            "",
            RegexOptions.Singleline);

        // Remove common LLDB Python errors
        output = Regex.Replace(output,
            @"(?:File ""<string>"", line \d+, in <module>|name '[^']+' is not defined)\n?",
            "",
            RegexOptions.Multiline);

        return output.Trim();
    }
}
