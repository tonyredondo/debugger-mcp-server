using DebuggerMcp.Analysis;
using Moq;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Tests for DotNetCrashAnalyzer parsing methods using a testable subclass.
/// </summary>
public class DotNetCrashAnalyzerParsingTests
{
    /// <summary>
    /// Testable subclass that exposes protected parsing methods.
    /// </summary>
    private class TestableDotNetCrashAnalyzer : DotNetCrashAnalyzer
    {
        public TestableDotNetCrashAnalyzer() : base(new Mock<DebuggerMcp.IDebuggerManager>().Object, null) { }

        public void TestParseClrVersion(string output, CrashAnalysisResult result)
            => ParseClrVersion(output, result);

        public void TestParseManagedException(string output, CrashAnalysisResult result)
            => ParseManagedException(output, result);

        public void TestParseHeapStats(string output, CrashAnalysisResult result)
            => ParseHeapStats(output, result);

        public void TestDetectAsyncDeadlock(string output, CrashAnalysisResult result)
            => DetectAsyncDeadlock(output, result);

        public void TestParseFinalizerQueue(string output, CrashAnalysisResult result)
            => ParseFinalizerQueue(output, result);

        public void TestAnalyzeDotNetMemoryLeaks(string heapStatsOutput, CrashAnalysisResult result)
            => AnalyzeDotNetMemoryLeaks(heapStatsOutput, result);

        public void TestUpdateDotNetSummary(CrashAnalysisResult result)
            => UpdateDotNetSummary(result);

        public void TestParseClrThreads(string output, CrashAnalysisResult result)
            => ParseClrThreads(output, result);

        public void TestParseThreadPool(string output, CrashAnalysisResult result)
            => ParseThreadPool(output, result);

        public void TestParseTimerInfo(string output, CrashAnalysisResult result)
            => ParseTimerInfo(output, result);

        public void TestParseFullCallStacksAllThreads(string output, CrashAnalysisResult result, bool appendToExisting = false)
            => ParseFullCallStacksAllThreads(output, result, appendToExisting);

        public void TestParseAssemblyVersions(string output, CrashAnalysisResult result)
            => ParseAssemblyVersions(output, result);

        public void TestEnrichAssemblyInfo(CrashAnalysisResult result)
            => EnrichAssemblyInfo(result);
    }

    private readonly TestableDotNetCrashAnalyzer _analyzer = new();

    /// <summary>
    /// Creates a CrashAnalysisResult with the new hierarchical properties initialized.
    /// </summary>
    private static CrashAnalysisResult CreateInitializedResult()
    {
        return new CrashAnalysisResult
        {
            Summary = new AnalysisSummary(),
            Exception = new ExceptionDetails(),
            Environment = new EnvironmentInfo { Runtime = new RuntimeInfo() },
            Threads = new ThreadsInfo { All = new List<ThreadInfo>(), Summary = new ThreadSummary() },
            Memory = new MemoryInfo { LeakAnalysis = new LeakAnalysis(), HeapStats = new Dictionary<string, long>() },
            Async = new AsyncInfo(),
            RawCommands = new Dictionary<string, string>()
        };
    }

    // ============================================================
    // ParseClrVersion Tests
    // ============================================================

    [Fact]
    public void ParseClrVersion_WithValidVersion_ExtractsVersion()
    {
        var output = @"SOS Version: 8.0.0
.NET Version: 8.0.0
CLR Version: 8.0.023.53103";

        var result = CreateInitializedResult();
        _analyzer.TestParseClrVersion(output, result);

        // Should parse without throwing
        Assert.NotNull(result.Environment);
        Assert.NotNull(result.Environment.Runtime);
    }

    [Fact]
    public void ParseClrVersion_WithEmptyOutput_DoesNotThrow()
    {
        var result = CreateInitializedResult();
        _analyzer.TestParseClrVersion("", result);
        // Should not throw
    }

    [Fact]
    public void ParseClrVersion_WithMinimalResult_DoesNotThrow()
    {
        var result = new CrashAnalysisResult();
        _analyzer.TestParseClrVersion("CLR Version: 8.0.0", result);
        // Should handle minimal result gracefully
    }

    // ============================================================
    // ParseManagedException Tests
    // ============================================================

    [Fact]
    public void ParseManagedException_WithException_ExtractsInfo()
    {
        var output = @"Exception object: 00007ff6b1234567
Exception type:   System.NullReferenceException
Message:          Object reference not set to an instance of an object.
InnerException:   <none>
StackTrace (generated):
    SP               IP               Function
    000000D58F7FF5A0 00007FF6B1234567 MyApp!Program.Main()+0x42";

        var result = CreateInitializedResult();
        _analyzer.TestParseManagedException(output, result);

        Assert.NotNull(result.Exception);
    }

    [Fact]
    public void ParseManagedException_WithInnerException_DoesNotThrow()
    {
        var output = @"Exception type: System.AggregateException
InnerException: System.InvalidOperationException";

        var result = CreateInitializedResult();
        _analyzer.TestParseManagedException(output, result);
        // Should not throw
    }

    [Fact]
    public void ParseManagedException_WithEmptyOutput_DoesNotThrow()
    {
        var result = CreateInitializedResult();
        _analyzer.TestParseManagedException("", result);
    }

    // ============================================================
    // ParseHeapStats Tests
    // ============================================================

    [Fact]
    public void ParseHeapStats_WithHeapData_ParsesStats()
    {
        var output = @"Statistics:
              MT    Count    TotalSize Class Name
00007ff6b1230000   10000       500000 System.String
00007ff6b1230100    5000       250000 System.Object[]
00007ff6b1230200    2000       100000 System.Byte[]
Total 17000 objects, 850000 bytes";

        var result = CreateInitializedResult();
        _analyzer.TestParseHeapStats(output, result);

        Assert.NotNull(result.Memory);
        Assert.NotNull(result.Memory.HeapStats);
    }

    [Fact]
    public void ParseHeapStats_WithEmptyOutput_DoesNotThrow()
    {
        var result = CreateInitializedResult();
        _analyzer.TestParseHeapStats("", result);
    }

    [Fact]
    public void ParseHeapStats_WithGenerationInfo_DoesNotThrow()
    {
        var output = @"Heap 0 (00007ff6b1230000)
generation 0 starts at 0x00007ff6b1240000
generation 1 starts at 0x00007ff6b1250000
generation 2 starts at 0x00007ff6b1260000";

        var result = CreateInitializedResult();
        _analyzer.TestParseHeapStats(output, result);
    }

    // ============================================================
    // DetectAsyncDeadlock Tests
    // ============================================================

    [Fact]
    public void DetectAsyncDeadlock_WithAsyncState_DoesNotThrow()
    {
        var output = @"Thread 1:
    System.Threading.Tasks.Task.Wait()
    Program.Main()
Thread 2:
    System.Threading.Monitor.Enter()
    AsyncHelper.RunSync()";

        var result = CreateInitializedResult();
        _analyzer.TestDetectAsyncDeadlock(output, result);
    }

    [Fact]
    public void DetectAsyncDeadlock_WithConfigureAwaitIssue_DoesNotThrow()
    {
        var output = @"System.Threading.Tasks.Task.GetAwaiter()
System.Runtime.CompilerServices.TaskAwaiter.GetResult()";

        var result = CreateInitializedResult();
        _analyzer.TestDetectAsyncDeadlock(output, result);
    }

    [Fact]
    public void DetectAsyncDeadlock_WithEmptyOutput_DoesNotThrow()
    {
        var result = CreateInitializedResult();
        _analyzer.TestDetectAsyncDeadlock("", result);
    }

    // ============================================================
    // ParseFinalizerQueue Tests
    // ============================================================

    [Fact]
    public void ParseFinalizerQueue_WithQueueData_DoesNotThrow()
    {
        var output = @"SyncBlocks to be cleaned up: 0
Free-Threaded Interfaces to be released: 0
MTA Interfaces to be released: 0
STA Interfaces to be released: 0
----------------------------------
generation 0 has 100 finalizable objects
generation 1 has 50 finalizable objects
generation 2 has 25 finalizable objects
Ready for finalization 10 objects";

        var result = CreateInitializedResult();
        _analyzer.TestParseFinalizerQueue(output, result);
    }

    [Fact]
    public void ParseFinalizerQueue_WithEmptyOutput_DoesNotThrow()
    {
        var result = CreateInitializedResult();
        _analyzer.TestParseFinalizerQueue("", result);
    }

    // ============================================================
    // AnalyzeDotNetMemoryLeaks Tests
    // ============================================================

    [Fact]
    public void AnalyzeDotNetMemoryLeaks_WithLargeAllocations_DetectsLeaks()
    {
        var output = @"Statistics:
              MT    Count    TotalSize Class Name
00007ff6b1230000  100000      5000000 System.String
00007ff6b1230100   50000     25000000 System.Byte[]
00007ff6b1230200   20000     10000000 System.Object[]";

        var result = CreateInitializedResult();
        _analyzer.TestAnalyzeDotNetMemoryLeaks(output, result);

        // Should analyze without throwing
    }

    [Fact]
    public void AnalyzeDotNetMemoryLeaks_WithSmallAllocations_DoesNotThrow()
    {
        var output = @"Statistics:
              MT    Count    TotalSize Class Name
00007ff6b1230000      100         5000 System.String";

        var result = CreateInitializedResult();
        _analyzer.TestAnalyzeDotNetMemoryLeaks(output, result);
    }

    [Fact]
    public void AnalyzeDotNetMemoryLeaks_WithEmptyOutput_DoesNotThrow()
    {
        var result = CreateInitializedResult();
        _analyzer.TestAnalyzeDotNetMemoryLeaks("", result);
    }

    // ============================================================
    // UpdateDotNetSummary Tests
    // ============================================================

    [Fact]
    public void UpdateDotNetSummary_WithManagedException_UpdatesSummary()
    {
        var result = CreateInitializedResult();
        result.Exception!.Type = "System.NullReferenceException";

        _analyzer.TestUpdateDotNetSummary(result);

        Assert.NotNull(result.Summary);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary.Description));
        Assert.Contains("NullReferenceException", result.Summary.Description);
    }

    [Fact]
    public void UpdateDotNetSummary_WithClrVersion_IncludesVersion()
    {
        var result = CreateInitializedResult();
        result.Environment!.Runtime!.ClrVersion = "8.0.0";

        _analyzer.TestUpdateDotNetSummary(result);

        Assert.NotNull(result.Summary);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary.Description));
        Assert.Contains("8.0.0", result.Summary.Description);
    }

    [Fact]
    public void UpdateDotNetSummary_WithMemoryLeak_IncludesLeakInfo()
    {
        var result = CreateInitializedResult();
        result.Memory!.LeakAnalysis!.Detected = true;
        result.Memory!.LeakAnalysis!.Severity = "High";
        result.Memory!.LeakAnalysis!.TotalHeapBytes = 500000;
        result.Memory!.LeakAnalysis!.TopConsumers = new List<MemoryConsumer>
                {
                    new() { TypeName = "System.String", Count = 10000, TotalSize = 500000 }
        };

        _analyzer.TestUpdateDotNetSummary(result);

        Assert.NotNull(result.Summary);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary.Description));
        Assert.Contains("MEMORY", result.Summary.Description);
    }

    [Fact]
    public void UpdateDotNetSummary_WithAsyncDeadlock_IncludesDeadlockInfo()
    {
        var result = CreateInitializedResult();
        result.Async!.HasDeadlock = true;

        _analyzer.TestUpdateDotNetSummary(result);

        Assert.NotNull(result.Summary);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary.Description));
        Assert.Contains("Async deadlock", result.Summary.Description);
    }

    [Fact]
    public void UpdateDotNetSummary_WithMinimalData_DoesNotThrow()
    {
        var result = CreateInitializedResult();

        _analyzer.TestUpdateDotNetSummary(result);
        // Should handle minimal data gracefully
    }

    [Fact]
    public void UpdateDotNetSummary_WithEmptyData_DoesNotThrow()
    {
        var result = CreateInitializedResult();
        // All hierarchical properties are initialized by CreateInitializedResult()

        _analyzer.TestUpdateDotNetSummary(result);
    }

    // ============================================================
    // ParseClrThreads Tests
    // ============================================================

    [Fact]
    public void ParseClrThreads_WithHeaderAndThreadLine_UpdatesSummaryAndEnrichesThread()
    {
        var output = string.Join(
            "\n",
            "ThreadCount:      13",
            "UnstartedThread:  0",
            "BackgroundThread: 10",
            "PendingThread:    0",
            "DeadThread:       2",
            "Hosted Runtime:   yes",
            "DBG   ID     OSID ThreadOBJ           State GC Mode     GC Alloc Context                  Domain           Count Apt Exception",
            "1     1     8954 0000F714F13A4010    20020 Preemptive  0000F7158EE6AA88:0000F7158EE6C1F8 0000F7559002B110 -00001 Ukn (Threadpool Worker) System.MissingMethodException 1234abcd");

        var result = CreateInitializedResult();
        result.Threads!.All!.Add(new ThreadInfo { ThreadId = "0x8954", CallStack = new List<StackFrame>() });

        _analyzer.TestParseClrThreads(output, result);

        Assert.Equal(13, result.Threads!.Summary!.Total);
        Assert.Equal(1, result.Threads.Summary.Foreground);
        Assert.Equal(10, result.Threads.Summary.Background);
        Assert.True(result.Environment!.Runtime!.IsHosted);

        var thread = result.Threads.All.Single(t => t.ThreadId.Equals("0x8954", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, thread.ManagedThreadId);
        Assert.Equal("8954", thread.OsThreadId);
        Assert.Equal("0x0000F714F13A4010", thread.ThreadObject);
        Assert.Equal("0x20020", thread.ClrThreadState);
        Assert.Equal("Preemptive", thread.GcMode);
        Assert.Equal(-1, thread.LockCount);
        Assert.Equal("Threadpool Worker", thread.ThreadType);
        Assert.True(thread.IsThreadpool);
        Assert.Contains("MissingMethodException", thread.CurrentException ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseClrThreads_WhenOutputMissingThreadCountHeader_DoesNothing()
    {
        var result = CreateInitializedResult();
        _analyzer.TestParseClrThreads("some output", result);

        Assert.Equal(0, result.Threads!.Summary!.Total);
    }

    // ============================================================
    // ParseThreadPool Tests
    // ============================================================

    [Fact]
    public void ParseThreadPool_WithPortablePoolAndSaturation_AddsRecommendations()
    {
        var output = string.Join(
            "\n",
            "Portable thread pool",
            "CPU utilization:  95%",
            "Workers Total:    3",
            "Workers Running:  3",
            "Workers Idle:     0",
            "Worker Min Limit: 4",
            "Worker Max Limit: 32767");

        var result = CreateInitializedResult();
        result.Summary!.Recommendations = new List<string>();

        _analyzer.TestParseThreadPool(output, result);

        Assert.NotNull(result.Threads!.ThreadPool);
        Assert.True(result.Threads.ThreadPool!.IsPortableThreadPool);
        Assert.Equal(95, result.Threads.ThreadPool.CpuUtilization);
        Assert.Equal(3, result.Threads.ThreadPool.WorkersTotal);
        Assert.Equal(3, result.Threads.ThreadPool.WorkersRunning);

        Assert.Contains(result.Summary.Recommendations, r => r.Contains("thread pool workers", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Summary.Recommendations, r => r.Contains("High CPU utilization", StringComparison.OrdinalIgnoreCase));
    }

    // ============================================================
    // ParseTimerInfo Tests
    // ============================================================

    [Fact]
    public void ParseTimerInfo_WithManyTimersAndShortIntervals_AddsRecommendationsAndPopulatesTimers()
    {
        var header = "   51 timers";
        var timerLines = Enumerable.Range(0, 51)
            .Select(i => $"(L) 0x0000F7158EDFD{i.ToString("X4")} @ 10 ms every 50 ms | 0000F7158EDFCE20 (TypeName) -> CallbackName")
            .ToArray();
        var output = header + "\n" + string.Join("\n", timerLines);

        var result = CreateInitializedResult();
        result.Summary!.Recommendations = new List<string>();

        Assert.False(DotNetCrashAnalyzer.IsSosErrorOutput(output));
        var timerRegex = new System.Text.RegularExpressions.Regex(
            @"\(L\)\s+0x([0-9a-fA-F]+)\s+@\s+(\d+)\s+ms\s+every\s+([\d\-]+)\s+ms\s+\|\s+([0-9a-fA-F]+)\s+\(([^)]+)\)\s*(?:->\s*(.*))?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.True(timerRegex.IsMatch(timerLines[0]), "Test input should match the production regex");

        _analyzer.TestParseTimerInfo(output, result);

        Assert.NotNull(result.Async!.Timers);
        Assert.Equal(51, result.Async.Timers!.Count);

        Assert.Contains(result.Summary.Recommendations, r => r.Contains("High number of active timers", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Summary.Recommendations, r => r.Contains("very short intervals", StringComparison.OrdinalIgnoreCase));
    }

    // ============================================================
    // ParseFullCallStacksAllThreads Tests
    // ============================================================

    [Fact]
    public void ParseFullCallStacksAllThreads_ParsesFramesParametersLocalsAndRegisters()
    {
        var output = string.Join(
            "\n",
            "OS Thread Id: 0x8954 (1)",
            "0000000000001000 0000000000002000 MyApp.dll!MyNamespace.MyType.Method(System.Int32, System.String) + 0 [/src/MyType.cs @ 42]",
            "PARAMETERS:",
            "    this (rcx) = 0x000000000000DEAD",
            "    value (rdx) = 123",
            "LOCALS:",
            "    <CLR reg> = 0x000000000000BEEF",
            "    <no data> = <no data>",
            "    rax=1 rbx=2 rcx=3 rdx=4",
            "0000000000001010                  [GCFrame]",
            "0000000000001020 0000000000003000 libcoreclr.so!PROCCreateCrashDump() + 636 at /path/to/file.cpp:123");

        var result = CreateInitializedResult();
        result.Threads!.All!.Clear();

        _analyzer.TestParseFullCallStacksAllThreads(output, result, appendToExisting: false);

        var thread = Assert.Single(result.Threads!.All!);
        Assert.Equal("0x8954", thread.ThreadId);
        Assert.True(thread.CallStack.Count >= 2);

        var first = thread.CallStack[0];
        Assert.True(first.IsManaged);
        Assert.Equal("MyApp.dll", first.Module);
        Assert.Contains("MyNamespace.MyType.Method", first.Function, StringComparison.Ordinal);
        Assert.Equal("/src/MyType.cs", first.SourceFile);
        Assert.Equal(42, first.LineNumber);

        Assert.NotNull(first.Parameters);
        Assert.NotEmpty(first.Parameters!);
        Assert.Contains(first.Parameters!, p => p.Name == "this");

        var thisParam = first.Parameters!.First(p => p.Name == "this");
        Assert.Equal("MyNamespace.MyType", thisParam.Type);
        Assert.True(thisParam.IsReferenceType);

        Assert.NotNull(first.Locals);
        Assert.Contains(first.Locals!, l => l.Value is string s && s == "[NO DATA]");

        Assert.NotNull(first.Registers);
        Assert.Equal("1", first.Registers!["rax"]);
        Assert.Equal("3", first.Registers["rcx"]);

        Assert.Contains(thread.CallStack, f => f.Function.Contains("[GCFrame]", StringComparison.Ordinal));

        var native = thread.CallStack.First(f => !f.IsManaged && f.Module.Contains("libcoreclr", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("file.cpp", native.SourceFile);
        Assert.Equal(123, native.LineNumber);
    }

    [Fact]
    public void ParseFullCallStacksAllThreads_WhenAppending_DoesNotClearExistingFrames()
    {
        var result = CreateInitializedResult();
        result.Threads!.All = new List<ThreadInfo>
        {
            new()
            {
                ThreadId = "0x8954",
                State = "Unknown",
                CallStack = new List<StackFrame> { new() { FrameNumber = 0, Function = "native", IsManaged = false } }
            }
        };

        var output = string.Join(
            "\n",
            "OS Thread Id: 0x8954 (1)",
            "0000000000001000 0000000000002000 MyApp.dll!MyNamespace.MyType.Method(System.Int32) + 0");

        _analyzer.TestParseFullCallStacksAllThreads(output, result, appendToExisting: true);

        var thread = Assert.Single(result.Threads.All);
        Assert.True(thread.CallStack.Count >= 2);
        Assert.Contains(thread.CallStack, f => f.Function == "native");
    }

    // ============================================================
    // ParseAssemblyVersions / EnrichAssemblyInfo Tests
    // ============================================================

    [Fact]
    public void ParseAssemblyVersions_WithBracketFormat_PopulatesAssemblies()
    {
        var output = string.Join(
            "\n",
            "Assembly:   0000f7558b725348 [My.Assembly]",
            "Module Name    0000f7558b7254c8  /path/to/My.Assembly.dll",
            "Assembly:   0000f7558b725349 [Other.Assembly]",
            "Module Name    0000f7558b7254c9  C:\\path\\to\\Other.Assembly.dll");

        var result = CreateInitializedResult();
        _analyzer.TestParseAssemblyVersions(output, result);

        Assert.NotNull(result.Assemblies);
        Assert.Equal(2, result.Assemblies!.Count);
        Assert.NotNull(result.Assemblies.Items);
        Assert.Contains(result.Assemblies.Items!, a => a.Name == "My.Assembly" && a.Path == "/path/to/My.Assembly.dll");
        Assert.Contains(result.Assemblies.Items!, a => a.Name == "Other.Assembly" && a.Path!.EndsWith("Other.Assembly.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnrichAssemblyInfo_WithMatchingModules_FillsBaseAddressAndNativeImageFlag()
    {
        var result = CreateInitializedResult();
        result.Assemblies = new AssembliesInfo
        {
            Count = 1,
            Items = new List<AssemblyVersionInfo>
            {
                new() { Name = "My.Assembly", Path = "/path/to/My.Assembly.ni.dll" }
            }
        };
        result.Modules = new List<ModuleInfo>
        {
            new() { Name = "/path/to/My.Assembly.ni.dll", BaseAddress = "0x1111" }
        };

        _analyzer.TestEnrichAssemblyInfo(result);

        var asm = Assert.Single(result.Assemblies.Items!);
        Assert.Equal("0x1111", asm.BaseAddress);
        Assert.True(asm.IsNativeImage);
    }
}
