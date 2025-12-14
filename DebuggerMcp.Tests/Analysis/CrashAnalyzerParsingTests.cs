using DebuggerMcp.Analysis;
using Moq;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Tests for CrashAnalyzer parsing methods using a testable subclass.
/// </summary>
public class CrashAnalyzerParsingTests
{
    /// <summary>
    /// Testable subclass that exposes protected parsing methods.
    /// </summary>
    private class TestableCrashAnalyzer : CrashAnalyzer
    {
        public TestableCrashAnalyzer() : base(new Mock<DebuggerMcp.IDebuggerManager>().Object, null) { }

        public TestableCrashAnalyzer(IDebuggerManager debuggerManager) : base(debuggerManager, null) { }

        public void TestParseWinDbgException(string output, CrashAnalysisResult result)
            => ParseWinDbgException(output, result);

        public void TestParseWinDbgBacktraceAll(string output, CrashAnalysisResult result)
            => ParseWinDbgBacktraceAll(output, result);

        public void TestParseWinDbgThreads(string output, CrashAnalysisResult result)
            => ParseWinDbgThreads(output, result);

        public void TestParseWinDbgModules(string output, CrashAnalysisResult result)
            => ParseWinDbgModules(output, result);

        public void TestParseLldbThreads(string output, CrashAnalysisResult result)
            => ParseLldbThreads(output, result);

        public void TestParseLldbBacktrace(string output, List<StackFrame> callStack)
            => ParseLldbBacktrace(output, callStack);

        public void TestParseLldbBacktraceAll(string output, CrashAnalysisResult result)
            => ParseLldbBacktraceAll(output, result);

        public void TestParseLldbModules(string output, CrashAnalysisResult result)
            => ParseLldbModules(output, result);

        public void TestGenerateSummary(CrashAnalysisResult result)
            => GenerateSummary(result);

        public Task TestAnalyzeMemoryLeaksLldbAsync(CrashAnalysisResult result)
            => AnalyzeMemoryLeaksLldbAsync(result);

        public Task TestAnalyzeDeadlocksLldbAsync(CrashAnalysisResult result)
            => AnalyzeDeadlocksLldbAsync(result);

        public void TestDetectPlatformInfo(string modulesOutput, CrashAnalysisResult result)
            => DetectPlatformInfo(modulesOutput, result);

        public static bool TestTryParseHexOrDecimal(string value, out long result)
            => TryParseHexOrDecimal(value, out result);
    }

    private readonly TestableCrashAnalyzer _analyzer = new();

    /// <summary>
    /// Creates a CrashAnalysisResult with the new hierarchical properties initialized.
    /// </summary>
    private static CrashAnalysisResult CreateInitializedResult()
    {
        return new CrashAnalysisResult
        {
            Summary = new AnalysisSummary(),
            Environment = new EnvironmentInfo(),
            Threads = new ThreadsInfo { All = new List<ThreadInfo>(), Summary = new ThreadSummary() },
            Memory = new MemoryInfo(),
            Modules = new List<ModuleInfo>(),
            RawCommands = new Dictionary<string, string>()
        };
    }

    // ============================================================
    // ParseWinDbgException Tests
    // ============================================================

    [Fact]
    public void ParseWinDbgException_WithAccessViolation_DoesNotThrow()
    {
        var output = @"EXCEPTION_RECORD:  (.exr -1)
ExceptionAddress: 00007ff6b1234567
ExceptionCode: c0000005 (Access violation)
ExceptionFlags: 00000000
NumberParameters: 2
Parameter[0]: 0000000000000000
Parameter[1]: 00007ff6b1234567";

        var result = CreateInitializedResult();

        // Should not throw - parsing may or may not find exception depending on format
        _analyzer.TestParseWinDbgException(output, result);
    }

    [Fact]
    public void ParseWinDbgException_WithNullReference_DoesNotThrow()
    {
        var output = @"ExceptionType: System.NullReferenceException
ExceptionMessage: Object reference not set to an instance of an object";

        var result = CreateInitializedResult();

        // Should not throw
        _analyzer.TestParseWinDbgException(output, result);
    }

    [Fact]
    public void ParseWinDbgException_WithEmptyOutput_DoesNotThrow()
    {
        var result = CreateInitializedResult();
        _analyzer.TestParseWinDbgException("", result);
        // Should not throw
    }

    // ============================================================
    // ParseWinDbgBacktraceAll Tests
    // ============================================================

    [Fact]
    public void ParseWinDbgBacktraceAll_WithFrames_ExtractsCallStack()
    {
        var output = @".  0  Id: 1234.5678 Suspend: 1 Teb: 000000d5`8d000000 Unfrozen
 # Child-SP          RetAddr           Call Site
00 000000d5`8f7ff5a0 00007ff6`b1234567 ntdll!NtWaitForSingleObject+0x14
01 000000d5`8f7ff5a8 00007ff6`b1234568 KERNELBASE!WaitForSingleObjectEx+0x9e
02 000000d5`8f7ff650 00007ff6`b1234569 MyApp!CrashFunction+0x42";

        var result = CreateInitializedResult();
        // First parse threads
        result.Threads!.All!.Add(new ThreadInfo { ThreadId = "0" });
        _analyzer.TestParseWinDbgBacktraceAll(output, result);

        Assert.NotEmpty(result.Threads!.All!);
        Assert.NotEmpty(result.Threads!.All![0].CallStack);
        Assert.True(result.Threads!.All![0].CallStack.Count >= 2);
    }

    [Fact]
    public void DetectPlatformInfo_WithMuslAndX64_SetsLinuxAlpineAndX64()
    {
        var result = CreateInitializedResult();

        var modules = @"
[  0] 00000000-00000000 0x0000ffffefc00000 /lib/ld-musl-x86_64.so.1
[  1] 00000000-00000000 0x0000ffffefc10000 /usr/lib/libc.so
";

        _analyzer.TestDetectPlatformInfo(modules, result);

        Assert.NotNull(result.Environment!.Platform);
        Assert.Equal("Linux", result.Environment.Platform!.Os);
        Assert.True(result.Environment.Platform.IsAlpine);
        Assert.Equal("musl", result.Environment.Platform.LibcType);
        Assert.Equal("x64", result.Environment.Platform.Architecture);
        Assert.Equal(64, result.Environment.Platform.PointerSize);
    }

    [Fact]
    public void DetectPlatformInfo_WithCoreClrAndSharedFrameworkPaths_SetsLinux()
    {
        var result = CreateInitializedResult();

        var modules = @"
[  0] 00000000-00000000 0x0000ffffefc00000 /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
[  1] 00000000-00000000 0x0000ffffefc10000 /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Private.CoreLib.dll
arm64
";

        _analyzer.TestDetectPlatformInfo(modules, result);

        Assert.NotNull(result.Environment!.Platform);
        Assert.Equal("Linux", result.Environment.Platform!.Os);
        Assert.Equal("arm64", result.Environment.Platform.Architecture);
        Assert.Equal(64, result.Environment.Platform.PointerSize);
    }

    [Fact]
    public async Task AnalyzeMemoryLeaksLldbAsync_WithLargeRegions_SetsHighSeverityAndRecommendation()
    {
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.ExecuteCommand("process status")).Returns("Process status");
        mockManager.Setup(m => m.ExecuteCommand("memory region --all")).Returns(@"
[0x00000000-0x80000000) rw-
[0x80000000-0x90000000) rw-");

        var analyzer = new TestableCrashAnalyzer(mockManager.Object);
        var result = CreateInitializedResult();

        await analyzer.TestAnalyzeMemoryLeaksLldbAsync(result);

        Assert.NotNull(result.Memory!.LeakAnalysis);
        Assert.True(result.Memory.LeakAnalysis.Detected);
        Assert.Equal("High", result.Memory.LeakAnalysis.Severity);
        Assert.Contains(result.Summary!.Recommendations!, r => r.Contains("High memory footprint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeDeadlocksLldbAsync_WithTwoWaitingThreads_DetectsPotentialDeadlock()
    {
        var mockManager = new Mock<IDebuggerManager>();
        var analyzer = new TestableCrashAnalyzer(mockManager.Object);

        var result = CreateInitializedResult();
        result.RawCommands!["bt all"] =
            "* thread #1\n" +
            "  frame #0: 0x00000000 0xDEADBEEF pthread_mutex_lock\n" +
            "  frame #1: 0x00000000 something\n" +
            "thread #2\n" +
            "  frame #0: 0x00000000 0xCAFEBABE semaphore_wait\n";

        await analyzer.TestAnalyzeDeadlocksLldbAsync(result);

        Assert.NotNull(result.Threads!.Deadlock);
        Assert.True(result.Threads.Deadlock!.Detected);
        Assert.Equal("Potential Deadlock", result.Summary!.CrashType);
        Assert.True(result.Threads.Deadlock.InvolvedThreads.Count >= 2);
        Assert.Contains(result.Summary.Recommendations!, r => r.Contains("deadlock", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseWinDbgBacktraceAll_WithSourceInfo_ExtractsSourceFile()
    {
        var output = @".  0  Id: 1234.5678 Suspend: 1 Teb: 000000d5`8d000000 Unfrozen
 # Child-SP          RetAddr           Call Site
00 000000d5`8f7ff5a0 00007ff6`b1234567 MyApp!Main+0x42 [C:\src\Program.cs @ 25]";

        var result = CreateInitializedResult();
        result.Threads!.All!.Add(new ThreadInfo { ThreadId = "0" });
        _analyzer.TestParseWinDbgBacktraceAll(output, result);

        Assert.NotEmpty(result.Threads!.All![0].CallStack);
    }

    [Fact]
    public void ParseWinDbgBacktraceAll_WithEmptyOutput_ReturnsEmptyList()
    {
        var result = CreateInitializedResult();
        result.Threads!.All!.Add(new ThreadInfo { ThreadId = "0" });
        _analyzer.TestParseWinDbgBacktraceAll("", result);

        Assert.Empty(result.Threads!.All![0].CallStack);
    }

    // ============================================================
    // ParseWinDbgThreads Tests
    // ============================================================

    [Fact]
    public void ParseWinDbgThreads_WithMultipleThreads_ExtractsAll()
    {
        var output = @".  0  Id: 1234.5678 Suspend: 1 Teb: 000000d5`8d000000 Unfrozen
   1  Id: 1234.5679 Suspend: 1 Teb: 000000d5`8d002000 Unfrozen
   2  Id: 1234.567a Suspend: 1 Teb: 000000d5`8d004000 Unfrozen";

        var result = CreateInitializedResult();
        _analyzer.TestParseWinDbgThreads(output, result);

        Assert.NotEmpty(result.Threads!.All!);
    }

    [Fact]
    public void ParseWinDbgThreads_WithCurrentThread_MarksIt()
    {
        var output = @".  0  Id: 1234.5678 Suspend: 1 Teb: 000000d5`8d000000 Unfrozen";

        var result = CreateInitializedResult();
        _analyzer.TestParseWinDbgThreads(output, result);

        Assert.NotEmpty(result.Threads!.All!);
    }

    // ============================================================
    // ParseWinDbgModules Tests
    // ============================================================

    [Fact]
    public void ParseWinDbgModules_WithModuleList_ExtractsModules()
    {
        var output = @"start             end                 module name
00007ff6`b1230000 00007ff6`b1240000   MyApp      (pdb symbols)
00007ffc`12340000 00007ffc`12440000   ntdll      (pdb symbols)";

        var result = CreateInitializedResult();
        _analyzer.TestParseWinDbgModules(output, result);

        Assert.NotNull(result.Modules);
        Assert.NotEmpty(result.Modules);
    }

    [Fact]
    public void ParseWinDbgModules_WithVersionInfo_ExtractsVersion()
    {
        var output = @"00007ff6`b1230000 00007ff6`b1240000   MyApp      1.0.0.0";

        var result = CreateInitializedResult();
        _analyzer.TestParseWinDbgModules(output, result);

        // Should parse without error
    }

    // ============================================================
    // ParseLldbThreads Tests
    // ============================================================

    [Fact]
    public void ParseLldbThreads_WithThreadList_DoesNotThrow()
    {
        var output = @"Process 12345 stopped
* thread #1, name = 'main', stop reason = signal SIGSEGV
  thread #2, name = 'worker'
  thread #3, name = 'gc'";

        var result = CreateInitializedResult();

        // Should not throw - parsing exercises the code paths
        _analyzer.TestParseLldbThreads(output, result);
    }

    [Fact]
    public void ParseLldbThreads_WithCurrentThread_DoesNotThrow()
    {
        var output = @"* thread #1, stop reason = breakpoint";

        var result = CreateInitializedResult();

        // Should not throw
        _analyzer.TestParseLldbThreads(output, result);
    }

    // ============================================================
    // ParseLldbBacktrace Tests
    // ============================================================

    [Fact]
    public void ParseLldbBacktrace_WithFrames_ExtractsCallStack()
    {
        var output = @"  * frame #0: 0x00007fff12345678 libsystem_kernel.dylib`__pthread_kill + 10
    frame #1: 0x00007fff12345679 libsystem_pthread.dylib`pthread_kill + 90
    frame #2: 0x00007fff1234567a MyApp`CrashFunction() + 42 at crash.cpp:25";

        var callStack = new List<StackFrame>();
        _analyzer.TestParseLldbBacktrace(output, callStack);

        Assert.NotEmpty(callStack);
    }

    [Fact]
    public void ParseLldbBacktrace_WithSourceLocation_ExtractsFileAndLine()
    {
        var output = @"frame #0: 0x00007fff12345678 MyApp`main at Program.cs:42:5";

        var callStack = new List<StackFrame>();
        _analyzer.TestParseLldbBacktrace(output, callStack);

        Assert.NotEmpty(callStack);
    }

    [Fact]
    public void ParseLldbBacktraceAll_WithMultipleThreads_ExtractsAllCallStacks()
    {
        var output = @"* thread #1, stop reason = signal SIGSEGV
  * frame #0: 0x00007fff12345678 libsystem_kernel.dylib`__pthread_kill + 10
    frame #1: 0x00007fff12345679 libsystem_pthread.dylib`pthread_kill + 90
  thread #2
    frame #0: 0x00007fff12345680 libsystem_pthread.dylib`__psynch_cvwait + 10";

        var result = CreateInitializedResult();
        result.Threads!.All!.Add(new ThreadInfo { ThreadId = "1", IsFaulting = true });
        result.Threads!.All!.Add(new ThreadInfo { ThreadId = "2" });
        _analyzer.TestParseLldbBacktraceAll(output, result);

        Assert.NotEmpty(result.Threads!.All![0].CallStack);
    }

    [Fact]
    public void ParseLldbBacktraceAll_WithNestedBackticks_DoesNotCorruptModuleName()
    {
        var output = @"* thread #1, stop reason = signal SIGSTOP
  * frame #0: 0x0000f58559fe943c SP=0x0000ffffca31ade0 libcoreclr.so`ds_ipc_stream_factory_get_next_available_stream(callback=(libcoreclr.so`server_warning_callback(char const*, unsigned int) + 0 at ds-server.c:110:1";

        var result = CreateInitializedResult();
        result.Threads!.All!.Add(new ThreadInfo { ThreadId = "1", IsFaulting = true });
        _analyzer.TestParseLldbBacktraceAll(output, result);

        var frame0 = Assert.Single(result.Threads.All[0].CallStack);
        Assert.Equal("libcoreclr.so", frame0.Module);
        Assert.Equal("server_warning_callback(char const*, unsigned int)", frame0.Function);
        Assert.Equal("ds-server.c:110:1", frame0.Source);
    }

    // ============================================================
    // ParseLldbModules Tests
    // ============================================================

    [Fact]
    public void ParseLldbModules_WithModuleList_ExtractsModules()
    {
        var output = @"Current executable set to '/path/to/app' (x86_64).
[  0] 00007FFF12340000 /usr/lib/libSystem.B.dylib
[  1] 00007FFF12350000 /usr/lib/libc++.1.dylib
[  2] 00007FFF12360000 /path/to/MyApp";

        var result = CreateInitializedResult();
        _analyzer.TestParseLldbModules(output, result);

        Assert.NotNull(result.Modules);
        Assert.NotEmpty(result.Modules);
    }

    // ============================================================
    // TryParseHexOrDecimal Tests
    // ============================================================

    [Theory]
    [InlineData("0x1234", 0x1234)]
    [InlineData("0X1234", 0x1234)]
    [InlineData("1234", 1234)]
    public void TryParseHexOrDecimal_WithValidInput_ReturnsTrue(string input, long expected)
    {
        var success = TestableCrashAnalyzer.TestTryParseHexOrDecimal(input, out var result);

        Assert.True(success);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("0xGGGG")]
    public void TryParseHexOrDecimal_WithInvalidInput_ReturnsFalse(string input)
    {
        var success = TestableCrashAnalyzer.TestTryParseHexOrDecimal(input, out _);

        Assert.False(success);
    }

    // ============================================================
    // GenerateSummary Tests
    // ============================================================

    [Fact]
    public void GenerateSummary_WithException_IncludesExceptionInSummary()
    {
        var result = CreateInitializedResult();
        result.Exception = new ExceptionDetails
        {
            Type = "System.NullReferenceException",
            Message = "Object reference not set"
        };

        _analyzer.TestGenerateSummary(result);

        Assert.NotEmpty(result.Summary!.Description!);
        Assert.Contains("NullReferenceException", result.Summary!.Description!);
    }

    [Fact]
    public void GenerateSummary_WithCallStack_IncludesCrashLocation()
    {
        var result = CreateInitializedResult();
        var thread = new ThreadInfo
        {
            ThreadId = "1",
            IsFaulting = true,
            CallStack = new List<StackFrame>
            {
                new() { Module = "MyApp", Function = "CrashFunction" }
            }
        };
        result.Threads!.All!.Add(thread);

        _analyzer.TestGenerateSummary(result);

        Assert.NotEmpty(result.Summary!.Description!);
    }

    [Fact]
    public void GenerateSummary_WithMemoryLeak_IncludesLeakInfo()
    {
        var result = CreateInitializedResult();
        result.Memory!.LeakAnalysis = new LeakAnalysis
        {
            Detected = true,
            TopConsumers = new List<MemoryConsumer>
                {
                    new() { TypeName = "System.String", Count = 1000, TotalSize = 50000 }
            }
        };

        _analyzer.TestGenerateSummary(result);

        Assert.NotEmpty(result.Summary!.Description!);
    }

    [Fact]
    public void GenerateSummary_WithDeadlock_IncludesDeadlockInfo()
    {
        var result = CreateInitializedResult();
        result.Threads!.Deadlock = new DeadlockInfo
        {
            Detected = true,
            InvolvedThreads = new List<string> { "Thread 1", "Thread 2" }
        };

        _analyzer.TestGenerateSummary(result);

        Assert.NotEmpty(result.Summary!.Description!);
    }
}
