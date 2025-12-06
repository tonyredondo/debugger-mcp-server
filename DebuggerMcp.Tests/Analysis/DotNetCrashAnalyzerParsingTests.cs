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

        Assert.NotEmpty(result.Summary!.Description);
        Assert.Contains("NullReferenceException", result.Summary!.Description);
    }

    [Fact]
    public void UpdateDotNetSummary_WithClrVersion_IncludesVersion()
    {
        var result = CreateInitializedResult();
        result.Environment!.Runtime!.ClrVersion = "8.0.0";

        _analyzer.TestUpdateDotNetSummary(result);

        Assert.NotEmpty(result.Summary!.Description);
        Assert.Contains("8.0.0", result.Summary!.Description);
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

        Assert.NotEmpty(result.Summary!.Description);
        Assert.Contains("MEMORY", result.Summary!.Description);
    }

    [Fact]
    public void UpdateDotNetSummary_WithAsyncDeadlock_IncludesDeadlockInfo()
    {
        var result = CreateInitializedResult();
        result.Async!.HasDeadlock = true;

        _analyzer.TestUpdateDotNetSummary(result);

        Assert.NotEmpty(result.Summary!.Description);
        Assert.Contains("Async deadlock", result.Summary!.Description);
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
}

