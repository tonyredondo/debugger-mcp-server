using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DebuggerMcp.Analysis;
using DebuggerMcp.SourceLink;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// End-to-end style tests for <see cref="DotNetCrashAnalyzer"/> using stubbed debugger outputs.
/// </summary>
public class DotNetCrashAnalyzerEndToEndTests
{
    private sealed class StubDebuggerManager : IDebuggerManager
    {
        private readonly Dictionary<string, string> _outputs;

        public StubDebuggerManager(string debuggerType, Dictionary<string, string> outputs)
        {
            DebuggerType = debuggerType;
            _outputs = outputs;
            IsInitialized = true;
        }

        public bool IsInitialized { get; private set; }

        public bool IsDumpOpen { get; private set; }

        public string? CurrentDumpPath { get; private set; }

        public string DebuggerType { get; }

        public bool IsSosLoaded { get; private set; }

        public bool IsDotNetDump { get; private set; }

        public List<string> ExecutedCommands { get; } = new();

        public Task InitializeAsync()
        {
            IsInitialized = true;
            return Task.CompletedTask;
        }

        public void OpenDumpFile(string dumpFilePath, string? executablePath = null)
        {
            CurrentDumpPath = dumpFilePath;
            IsDumpOpen = true;
            IsDotNetDump = true;
            IsSosLoaded = true;
        }

        public void CloseDump()
        {
            IsDumpOpen = false;
            CurrentDumpPath = null;
        }

        public string ExecuteCommand(string command)
        {
            ExecutedCommands.Add(command);

            if (command.StartsWith("memory read", StringComparison.OrdinalIgnoreCase))
            {
                return _outputs.TryGetValue("memory read", out var output) ? output : string.Empty;
            }

            if (_outputs.TryGetValue(command, out var exact))
            {
                return exact;
            }

            return string.Empty;
        }

        public void LoadSosExtension()
        {
            IsSosLoaded = true;
        }

        public void ConfigureSymbolPath(string symbolPath)
        {
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task AnalyzeDotNetCrashAsync_WithStubbedOutputs_CompletesAndPopulatesKeyFields()
    {
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED");
        Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", "false");

        try
        {
            var argvAddress = "0x0000ffffefcba618";

            var outputs = new Dictionary<string, string>
            {
                // Base LLDB analysis.
                ["thread list"] = "* thread #1: tid = 0x8954, 0x0000000100000000 dotnet`abort, name = 'dotnet', stop reason = signal SIGABRT",
                ["bt all"] =
                    "* thread #1, name = 'dotnet', stop reason = signal SIGABRT\n" +
                    $"  * frame #0: 0x0000c5f644a77244 SP=0x0000ffffefcb76a0 dotnet`exe_start(argc=2, argv={argvAddress})\n" +
                    $"    frame #1: 0x0000c5f644a77244 SP=0x0000ffffefcb75a0 dotnet`main(argc=2, argv={argvAddress})\n" +
                    "  thread #2: tid = 0x1234\n" +
                    "    frame #0: 0x0000f7558ffad6a4 SP=0x0000ffffefcb7000 libstdc++.so.6 + 123\n",
                ["image list"] =
                    "[  0] 12345678-1234-1234-1234-123456789ABC 0x0000000100000000 /usr/lib/dyld\n" +
                    "[  1] 23456789-2345-2345-2345-23456789ABCD 0x0000000200000000 /usr/lib/libc.so.6\n",

                // ProcessInfoExtractor memory read / string reads (argv + envp, split by NULL sentinel).
                ["memory read"] =
                    "0xffffefcba618: 0x0000ffffefcbbb24 0x0000ffffefcbbb2b\n" +
                    "0xffffefcba628: 0x0000000000000000 0x0000ffffefcbbb8f\n" +
                    "0xffffefcba638: 0x0000ffffefcbbba0 0x0000000000000000\n",
                ["expr -- (char*)0x0000ffffefcbbb24"] = "(char *) $1 = 0x0000ffffefcbbb24 \"dotnet\"",
                ["expr -- (char*)0x0000ffffefcbbb2b"] = "(char *) $2 = 0x0000ffffefcbbb2b \"/app/MyApp.dll\"",
                ["expr -- (char*)0x0000ffffefcbbb8f"] = "(char *) $3 = 0x0000ffffefcbbb8f \"DD_API_KEY=secret-value\"",
                // Invalid env var (no '='), should be skipped.
                ["expr -- (char*)0x0000ffffefcbbba0"] = "(char *) $4 = 0x0000ffffefcbbba0 \"NOT_AN_ENV_VAR\"",

                // .NET analysis commands.
                ["!eeversion"] = "CLR Version: 8.0.0.0",
                ["clrstack -a -r -all"] =
                    "OS Thread Id: 0x8954 (1)\n" +
                    "Child SP         IP               Call Site\n" +
                    "0000FFFFEFCB76A0 0000F75587765AB4 MyApp.Program.Main(System.String[])\n" +
                    "PARAMETERS:\n" +
                    "this (<CLR reg>) = 0x0000f7158e82d780\n" +
                    "args (<CLR reg>) = 0x0000ffffefcbbb24\n" +
                    "LOCALS:\n" +
                    "<CLR reg> = 0x0000000000000001\n" +
                    "Registers:\n" +
                    "rax=0x1 rbx=0x2\n" +
                    "0000FFFFEFCB76F8                  [GCFrame]\n",
                ["!pe -nested"] =
                    "Exception object: 00007ff6b1234567\n" +
                    "Exception type:   System.NullReferenceException\n" +
                    "Message:          Object reference not set to an instance of an object.\n" +
                    "InnerException:   <none>\n" +
                    "StackTrace (generated):\n" +
                    "    SP               IP               Function\n" +
                    "    000000D58F7FF5A0 00007FF6B1234567 MyApp!Program.Main()+0x42\n",
                ["!dumpheap -stat"] =
                    "Statistics:\n" +
                    "              MT    Count    TotalSize Class Name\n" +
                    "00007ff6b1230000       10          100 System.String\n" +
                    "Total 10 objects, 100 bytes\n",
                ["!clrthreads"] =
                    "ThreadCount:      1\n" +
                    "UnstartedThread:  0\n" +
                    "BackgroundThread: 1\n" +
                    "PendingThread:    0\n" +
                    "DeadThread:       0\n" +
                    "Hosted Runtime:   no\n" +
                    "DBG   ID     OSID ThreadOBJ           State GC Mode     GC Alloc Context                  Domain           Count Apt Exception\n" +
                    "1    1     8954 0000F714F13A4010    102120 Preemptive  0000F7158EE6AA88:0000F7158EE6C1F8 0000F7559002B110 -00001 Ukn System.NullReferenceException\n",
                ["!finalizequeue"] = "generation 0 has 1501 finalizable objects",
                ["!syncblk"] =
                    "Index SyncBlock MonitorHeld Recursion Owning Thread Info  SyncBlock Owner\n" +
                    "  12 0000024453f8a5a8    1         1 0000024453f46720  1c54  10   00000244540a5820 System.Object\n",
                ["!threadpool"] =
                    "Portable thread pool\n" +
                    "CPU utilization:  95%\n" +
                    "Workers Total:    2\n" +
                    "Workers Running:  2\n" +
                    "Workers Idle:     0\n" +
                    "Worker Min Limit: 2\n" +
                    "Worker Max Limit: 32767\n",
                ["!ti"] =
                    "(L) 0x0000F7158EDFD1D0 @    3999 ms every     4000 ms |  0000F7158EDFCE20 (System.Threading.Timer) -> MyApp.Program.TimerCallback\n",
                ["!analyzeoom"] =
                    "OOM detected\n" +
                    "Reason: Allocation request failed\n" +
                    "Gen: 2\n" +
                    "Allocation Size: 131072\n" +
                    "LOH Size: 1048576\n",
                ["!crashinfo"] =
                    "Signal: 11 (SIGSEGV)\n",
                ["!dumpdomain"] = "Domain 1: 00007ff6b1234000\n"
            };

            var manager = new StubDebuggerManager("LLDB", outputs);
            var analyzer = new DotNetCrashAnalyzer(manager, sourceLinkResolver: new SourceLinkResolver(), clrMdAnalyzer: null);

            var result = await analyzer.AnalyzeDotNetCrashAsync();

            Assert.NotNull(result);
            Assert.NotNull(result.Summary);
            Assert.NotNull(result.Environment);
            Assert.NotNull(result.Threads);
            Assert.NotNull(result.Memory);
            Assert.NotNull(result.RawCommands);

            // Process info extraction should run and redact sensitive env vars.
            Assert.NotNull(result.Environment.Process);
            Assert.Contains("DD_API_KEY=<redacted>", result.Environment.Process!.EnvironmentVariables);

            // Threadpool recommendation should be present (saturation + high CPU).
            Assert.Contains(result.Summary!.Recommendations ?? new List<string>(), r => r.Contains("thread pool", StringComparison.OrdinalIgnoreCase));

            // Coverage sanity: ensure key commands were executed.
            Assert.Contains("clrstack -a -r -all", manager.ExecutedCommands);
            Assert.Contains("!dumpheap -stat", manager.ExecutedCommands);
            Assert.Contains("!clrthreads", manager.ExecutedCommands);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", original);
        }
    }

    [Fact]
    public async Task AnalyzeDotNetCrashAsync_WithMissingMethodException_PopulatesTypeResolutionAnalysis()
    {
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED");
        Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", "false");

        try
        {
            var argvAddress = "0x0000ffffefcba618";
            var mt = "f7558924ae98";

            var heapCmd = "!dumpheap -stat -type ConcurrentDictionary";
            var dumpmtCmd = $"!dumpmt -md {mt}";

            var outputs = new Dictionary<string, string>
            {
                ["thread list"] = "* thread #1: tid = 0x8954, 0x0000000100000000 dotnet`abort, name = 'dotnet', stop reason = signal SIGABRT",
                ["bt all"] =
                    "* thread #1, name = 'dotnet', stop reason = signal SIGABRT\n" +
                    $"  * frame #0: 0x0000c5f644a77244 SP=0x0000ffffefcb76a0 dotnet`exe_start(argc=2, argv={argvAddress})\n",
                ["image list"] = "[  0] 12345678-1234-1234-1234-123456789ABC 0x0000000100000000 /usr/lib/dyld\n",

                ["memory read"] =
                    "0xffffefcba618: 0x0000ffffefcbbb24\n" +
                    "0xffffefcba620: 0x0000000000000000\n",
                ["expr -- (char*)0x0000ffffefcbbb24"] = "(char *) $1 = 0x0000ffffefcbbb24 \"dotnet\"",

                ["!eeversion"] = "CLR Version: 8.0.0.0",
                ["clrstack -a -r -all"] =
                    "OS Thread Id: 0x8954 (1)\n" +
                    "Child SP         IP               Call Site\n" +
                    "0000FFFFEFCB76A0 0000F75587765AB4 System.Collections.Concurrent.ConcurrentDictionary`2.TryGetValue(!0, !1 ByRef)\n",

                ["!pe -nested"] =
                    "Exception object: 00007ff6b1234567\n" +
                    "Exception type:   System.MissingMethodException\n" +
                    "Message:          Method not found: 'System.Boolean System.Collections.Concurrent.ConcurrentDictionary`2.TryGetValue(!0, !1 ByRef)'\n",

                [heapCmd] =
                    "MT    Count    TotalSize Class Name\n" +
                    $"{mt}     1        32 System.Collections.Concurrent.ConcurrentDictionary<System.String,System.Int32>\n",
                [dumpmtCmd] =
                    "MethodDesc Table\n" +
                    "Entry       MethodDesc    JIT     Slot      Name\n" +
                    "00007FFD00000001 00007FFD00000002 NONE 00000001 System.Collections.Concurrent.ConcurrentDictionary`2.TryGetValue(!0, !1 ByRef)\n",

                // Keep the remaining pipeline stable with minimal outputs.
                ["!dumpheap -stat"] = "Total 0 objects, 0 bytes\n",
                ["!clrthreads"] = "ThreadCount:      0\n",
                ["!finalizequeue"] = "generation 0 has 0 finalizable objects\n",
                ["!syncblk"] = string.Empty,
                ["!threadpool"] = string.Empty,
                ["!ti"] = string.Empty,
                ["!analyzeoom"] = "There was no managed OOM\n",
                ["!crashinfo"] = "No crash info\n",
                ["!dumpdomain"] = string.Empty
            };

            var manager = new StubDebuggerManager("LLDB", outputs);
            var analyzer = new DotNetCrashAnalyzer(manager, sourceLinkResolver: null, clrMdAnalyzer: null);

            var result = await analyzer.AnalyzeDotNetCrashAsync();

            Assert.NotNull(result.Exception);
            Assert.NotNull(result.Exception!.Analysis);
            Assert.NotNull(result.Exception.Analysis!.TypeResolution);

            Assert.Equal("System.Collections.Concurrent.ConcurrentDictionary`2", result.Exception.Analysis.TypeResolution!.FailedType);
            Assert.True(result.Exception.Analysis.TypeResolution.MethodFound);

            Assert.Contains(heapCmd, result.RawCommands!.Keys);
            Assert.Contains(dumpmtCmd, result.RawCommands!.Keys);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", original);
        }
    }
}
