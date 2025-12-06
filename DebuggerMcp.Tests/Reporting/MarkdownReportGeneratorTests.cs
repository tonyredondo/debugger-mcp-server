using DebuggerMcp.Analysis;
using DebuggerMcp.Reporting;
using DebuggerMcp.Watches;

namespace DebuggerMcp.Tests.Reporting;

/// <summary>
/// Tests for MarkdownReportGenerator class.
/// </summary>
public class MarkdownReportGeneratorTests
{
    private readonly MarkdownReportGenerator _generator;

    public MarkdownReportGeneratorTests()
    {
        _generator = new MarkdownReportGenerator();
    }

    // ============================================================
    // Basic Generation Tests
    // ============================================================

    [Fact]
    public void Generate_WithNullAnalysis_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _generator.Generate(null!, null!, null!));
    }

    [Fact]
    public void Generate_WithMinimalAnalysis_ReturnsMarkdown()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();

        // Act
        var result = _generator.Generate(analysis, null!, null!);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.Contains("# Crash Analysis Report", result);
    }

    [Fact]
    public void Generate_WithNullOptions_UsesDefaultOptions()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();

        // Act
        var result = _generator.Generate(analysis, null!, null!);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("Executive Summary", result);
    }

    [Fact]
    public void Generate_WithNullMetadata_UsesDefaultMetadata()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();

        // Act
        var result = _generator.Generate(analysis, ReportOptions.FullReport, null!);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("Generated", result);
    }

    // ============================================================
    // Header Tests
    // ============================================================

    [Fact]
    public void Generate_IncludesHeader()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Summary!.CrashType = "AccessViolation";
        var metadata = new ReportMetadata
        {
            DumpId = "test-dump-123",
            DebuggerType = "WinDbg"
        };

        // Act
        var result = _generator.Generate(analysis, null!, metadata);

        // Assert
        Assert.Contains("test-dump-123", result);
        Assert.Contains("AccessViolation", result);
        Assert.Contains("WinDbg", result);
    }

    [Fact]
    public void Generate_WithCustomTitle_UsesCustomTitle()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        var options = new ReportOptions { Title = "My Custom Report Title" };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("# My Custom Report Title", result);
    }

    // ============================================================
    // Summary Section Tests
    // ============================================================

    [Fact]
    public void Generate_WithSummary_IncludesSummary()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Summary = new AnalysisSummary { Description = "This is the crash summary." };

        // Act
        var result = _generator.Generate(analysis, null!, null!);

        // Assert
        Assert.Contains("This is the crash summary.", result);
    }

    [Fact]
    public void Generate_WithEmptySummary_ShowsNoSummaryMessage()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Summary = new AnalysisSummary { Description = "" };

        // Act
        var result = _generator.Generate(analysis, null!, null!);

        // Assert
        Assert.Contains("No summary available.", result);
    }

    // ============================================================
    // Crash Info Section Tests
    // ============================================================

    [Fact]
    public void Generate_WithException_IncludesExceptionDetails()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Exception = new ExceptionDetails
        {
            Type = "System.NullReferenceException",
            Message = "Object reference not set to an instance of an object",
            Address = "0x00007ff123456789"
        };
        var options = new ReportOptions { IncludeCrashInfo = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("System.NullReferenceException", result);
        Assert.Contains("Object reference not set to an instance", result);
        Assert.Contains("0x00007ff123456789", result);
    }

    [Fact]
    public void Generate_WithCrashInfoDisabled_ExcludesExceptionDetails()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Exception = new ExceptionDetails { Type = "TestException" };
        var options = new ReportOptions { IncludeCrashInfo = false };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.DoesNotContain("TestException", result);
    }

    // ============================================================
    // Call Stack Section Tests
    // ============================================================

    [Fact]
    public void Generate_WithCallStack_IncludesCallStack()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Threads ??= new ThreadsInfo { All = new List<ThreadInfo>() };
        analysis.Threads.All.Add(new ThreadInfo 
        { 
            ThreadId = "1", 
            IsFaulting = true,
            CallStack = new List<StackFrame>
        {
            new StackFrame { FrameNumber = 0, Module = "kernel32", Function = "RaiseException" },
            new StackFrame { FrameNumber = 1, Module = "myapp", Function = "ProcessData" }
            }
        });
        var options = new ReportOptions { IncludeCallStacks = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("Call Stack", result);
        Assert.Contains("kernel32", result);
        Assert.Contains("RaiseException", result);
    }

    [Fact]
    public void Generate_WithCallStackLimit_TruncatesCallStack()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Threads ??= new ThreadsInfo { All = new List<ThreadInfo>() };
        analysis.Threads.All.Add(new ThreadInfo 
        { 
            ThreadId = "1", 
            IsFaulting = true,
            CallStack = Enumerable.Range(0, 20)
            .Select(i => new StackFrame { FrameNumber = i, Module = "mod", Function = $"Func{i}" })
                .ToList()
        });
        var options = new ReportOptions { IncludeCallStacks = true, MaxCallStackFrames = 5 };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("5 frames of 20 total", result);
    }

    [Fact]
    public void Generate_WithSourceLinks_IncludesSourceLinks()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Threads ??= new ThreadsInfo { All = new List<ThreadInfo>() };
        analysis.Threads.All.Add(new ThreadInfo 
        { 
            ThreadId = "1", 
            IsFaulting = true,
            CallStack = new List<StackFrame>
        {
            new StackFrame
            {
                FrameNumber = 0,
                Module = "myapp",
                Function = "ProcessData",
                SourceFile = "/src/Program.cs",
                LineNumber = 42,
                SourceUrl = "https://github.com/user/repo/blob/abc123/src/Program.cs#L42"
            }
            }
        });
        var options = new ReportOptions { IncludeCallStacks = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("Program.cs:42", result);
        Assert.Contains("https://github.com/user/repo", result);
    }

    // ============================================================
    // Thread Info Section Tests
    // ============================================================

    [Fact]
    public void Generate_WithThreads_IncludesThreadInfo()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Threads = new ThreadsInfo
        {
            All = new List<ThreadInfo>
        {
            new ThreadInfo { ThreadId = "1", State = "Running", TopFunction = "Main" },
            new ThreadInfo { ThreadId = "2", State = "Waiting", TopFunction = "Sleep" }
            }
        };
        var options = new ReportOptions { IncludeThreadInfo = true, IncludeCharts = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("Thread Information", result);
        Assert.Contains("Total Threads", result);
        Assert.Contains("2", result);
    }

    [Fact]
    public void Generate_WithThreadLimit_TruncatesThreadList()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Threads = new ThreadsInfo
        {
            All = Enumerable.Range(0, 50)
            .Select(i => new ThreadInfo { ThreadId = i.ToString(), State = "Running" })
                .ToList()
        };
        var options = new ReportOptions { IncludeThreadInfo = true, MaxThreadsToShow = 10 };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("10 of 50 threads", result);
    }

    // ============================================================
    // .NET Info Section Tests
    // ============================================================

    [Fact]
    public void Generate_WithDotNetInfo_IncludesDotNetInfo()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Environment = new EnvironmentInfo
        {
            Runtime = new RuntimeInfo { ClrVersion = "8.0.0" }
        };
        analysis.Exception = new ExceptionDetails
        {
            Type = "System.Exception: Test"
        };
        analysis.Threads = new ThreadsInfo
        {
            All = new List<ThreadInfo>(),
            Summary = new ThreadSummary { FinalizerQueueLength = 100 }
        };
        var options = new ReportOptions { IncludeDotNetInfo = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains(".NET Runtime Information", result);
        Assert.Contains("8.0.0", result);
        Assert.Contains("System.Exception: Test", result);
        Assert.Contains("100", result);
    }

    [Fact]
    public void Generate_WithAsyncDeadlock_IncludesDeadlockWarning()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Async = new AsyncInfo { HasDeadlock = true };
        // Ensure hasDotNetInfo check passes
        analysis.Environment = new EnvironmentInfo { Runtime = new RuntimeInfo() };
        var options = new ReportOptions { IncludeDotNetInfo = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("Async Deadlock Detected", result);
    }

    // ============================================================
    // Memory Section Tests
    // ============================================================

    [Fact]
    public void Generate_WithHeapStats_IncludesHeapStats()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Memory = new MemoryInfo
        {
            HeapStats = new Dictionary<string, long>
            {
                ["System.String"] = 1024 * 1024,
                ["System.Object"] = 512 * 1024
            }
        };
        var options = new ReportOptions { IncludeHeapStats = true, IncludeCharts = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("Memory Analysis", result);
        Assert.Contains("Heap Statistics", result);
    }

    [Fact]
    public void Generate_WithMemoryLeak_IncludesMemoryLeakInfo()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Memory = new MemoryInfo
        {
            LeakAnalysis = new LeakAnalysis
        {
            Detected = true,
            EstimatedLeakedBytes = 10 * 1024 * 1024,
            TopConsumers = new List<MemoryConsumer>
            {
                new MemoryConsumer { TypeName = "LeakyClass", Count = 1000, TotalSize = 5 * 1024 * 1024 }
                }
            }
        };
        var options = new ReportOptions { IncludeMemoryLeakInfo = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("Memory Analysis", result);
        Assert.Contains("LeakyClass", result);
    }

    // ============================================================
    // Deadlock Section Tests
    // ============================================================

    [Fact]
    public void Generate_WithDeadlock_IncludesDeadlockInfo()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Threads = new ThreadsInfo
        {
            All = new List<ThreadInfo>(),
            Deadlock = new DeadlockInfo
        {
            Detected = true,
            InvolvedThreads = new List<string> { "1", "2" },
            Locks = new List<LockInfo>
            {
                new LockInfo { Address = "0x12345678" },
                new LockInfo { Address = "0x87654321" }
                }
            }
        };
        var options = new ReportOptions { IncludeDeadlockInfo = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("DEADLOCK DETECTED", result);
        Assert.Contains("Thread 1", result);
    }

    [Fact]
    public void Generate_WithNoDeadlock_ExcludesDeadlockSection()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Threads = new ThreadsInfo { All = new List<ThreadInfo>(), Deadlock = new DeadlockInfo { Detected = false } };
        var options = new ReportOptions { IncludeDeadlockInfo = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.DoesNotContain("DEADLOCK DETECTED", result);
    }

    // ============================================================
    // Watch Results Section Tests
    // ============================================================

    [Fact]
    public void Generate_WithWatchResults_IncludesWatchSection()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Watches = new WatchEvaluationReport
        {
            TotalWatches = 2,
            SuccessfulEvaluations = 1,
            FailedEvaluations = 1,
            Watches = new List<WatchEvaluationResult>
            {
                new WatchEvaluationResult
                {
                    Expression = "myVariable",
                    Value = "42",
                    Success = true,
                    Description = "Test variable"
                },
                new WatchEvaluationResult
                {
                    Expression = "badVariable",
                    Success = false,
                    Error = "Variable not found"
                }
            },
            Insights = new List<string> { "Variable myVariable has value 42" }
        };
        var options = new ReportOptions { IncludeWatchResults = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("Watch Expression Results", result);
        Assert.Contains("myVariable", result);
        Assert.Contains("42", result);
        Assert.Contains("Variable not found", result);
        Assert.Contains("Insights", result);
    }

    // ============================================================
    // Security Section Tests
    // ============================================================

    [Fact]
    public void Generate_WithSecurityAnalysis_IncludesSecuritySection()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Security = new SecurityInfo
        {
            OverallRisk = "High",
            Summary = "Potential buffer overflow detected",
            Findings = new List<SecurityFinding>
            {
                new SecurityFinding
                {
                    Type = "BufferOverflow",
                    Severity = "High",
                    Description = "Stack buffer overflow detected"
                }
            },
            Recommendations = new List<string> { "Review buffer handling" }
        };
        var options = new ReportOptions { IncludeSecurityAnalysis = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("Security Analysis", result);
        Assert.Contains("High", result);
        Assert.Contains("BufferOverflow", result);
    }

    // ============================================================
    // Modules Section Tests
    // ============================================================

    [Fact]
    public void Generate_WithModules_IncludesModuleList()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Modules = new List<ModuleInfo>
        {
            new ModuleInfo { Name = "kernel32.dll", BaseAddress = "0x7ffe12340000", HasSymbols = true },
            new ModuleInfo { Name = "myapp.exe", BaseAddress = "0x00400000", HasSymbols = false }
        };
        var options = new ReportOptions { IncludeModules = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("Loaded Modules", result);
        Assert.Contains("kernel32.dll", result);
        Assert.Contains("myapp.exe", result);
    }

    [Fact]
    public void Generate_WithModuleLimit_TruncatesModuleList()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Modules = Enumerable.Range(0, 100)
            .Select(i => new ModuleInfo { Name = $"module{i}.dll" })
            .ToList();
        var options = new ReportOptions { IncludeModules = true, MaxModulesToShow = 20 };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("20 of 100 modules", result);
    }

    // ============================================================
    // Recommendations Section Tests
    // ============================================================

    [Fact]
    public void Generate_WithRecommendations_IncludesRecommendations()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Summary = new AnalysisSummary
        {
            Recommendations = new List<string>
        {
            "Check for null references",
            "Review memory allocation"
            }
        };
        var options = new ReportOptions { IncludeRecommendations = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("Recommendations", result);
        Assert.Contains("Check for null references", result);
        Assert.Contains("Review memory allocation", result);
    }

    // ============================================================
    // Raw Output Section Tests
    // ============================================================

    [Fact]
    public void Generate_WithRawOutput_IncludesRawOutput()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.RawCommands = new Dictionary<string, string>
        {
            ["k"] = "Stack trace output here"
        };
        var options = new ReportOptions { IncludeRawOutput = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("Raw Debugger Output", result);
        Assert.Contains("Stack trace output here", result);
    }

    [Fact]
    public void Generate_WithLongRawOutput_TruncatesOutput()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        var longOutput = new string('x', 10000);
        analysis.RawCommands = new Dictionary<string, string>
        {
            ["test"] = longOutput
        };
        var options = new ReportOptions { IncludeRawOutput = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("truncated", result);
    }

    // ============================================================
    // Helper Methods
    // ============================================================

    private static CrashAnalysisResult CreateMinimalAnalysis()
    {
        return new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { Description = "Test crash analysis" },
            Threads = new ThreadsInfo { All = new List<ThreadInfo>() },
            Modules = new List<ModuleInfo>(),
            RawCommands = new Dictionary<string, string>()
        };
    }
}

