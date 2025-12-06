using DebuggerMcp.Analysis;
using DebuggerMcp.Reporting;
using DebuggerMcp.Watches;

namespace DebuggerMcp.Tests.Reporting;

/// <summary>
/// Tests for HtmlReportGenerator class.
/// </summary>
public class HtmlReportGeneratorTests
{
    private readonly HtmlReportGenerator _generator;

    public HtmlReportGeneratorTests()
    {
        _generator = new HtmlReportGenerator();
    }

    // ============================================================
    // Basic Generation Tests
    // ============================================================

    [Fact]
    public void Generate_WithNullAnalysis_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _generator.Generate(null!, null!, null!));
    }

    [Fact]
    public void Generate_WithMinimalAnalysis_ReturnsHtml()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();

        // Act
        var result = _generator.Generate(analysis, null!, null!);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.Contains("<!DOCTYPE html>", result);
        Assert.Contains("</html>", result);
    }

    [Fact]
    public void Generate_WithNullOptions_UsesDefaultOptions()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();

        // Act
        var result = _generator.Generate(analysis, null!, null!);

        // Assert
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
        Assert.Contains("Generated", result);
    }

    // ============================================================
    // HTML Structure Tests
    // ============================================================

    [Fact]
    public void Generate_ContainsValidHtmlStructure()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();

        // Act
        var result = _generator.Generate(analysis, null!, null!);

        // Assert
        Assert.Contains("<html", result);
        Assert.Contains("<head>", result);
        Assert.Contains("<body>", result);
        Assert.Contains("</body>", result);
        Assert.Contains("</head>", result);
    }

    [Fact]
    public void Generate_ContainsCssStyles()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();

        // Act
        var result = _generator.Generate(analysis, null!, null!);

        // Assert
        Assert.Contains("<style>", result);
        Assert.Contains("</style>", result);
        Assert.Contains(":root", result); // CSS variables
    }

    [Fact]
    public void Generate_ContainsContainerDiv()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();

        // Act
        var result = _generator.Generate(analysis, null!, null!);

        // Assert
        Assert.Contains("class=\"container\"", result);
    }

    // ============================================================
    // Header Tests
    // ============================================================

    [Fact]
    public void Generate_WithCustomTitle_UsesCustomTitle()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        var options = new ReportOptions { Title = "My Custom Report" };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("<title>My Custom Report</title>", result);
    }

    [Fact]
    public void Generate_IncludesMetadata()
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

    // ============================================================
    // Summary Section Tests
    // ============================================================

    [Fact]
    public void Generate_WithSummary_IncludesSummary()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Summary = new AnalysisSummary { Description = "Critical crash due to null pointer." };

        // Act
        var result = _generator.Generate(analysis, null!, null!);

        // Assert
        Assert.Contains("Critical crash due to null pointer.", result);
        Assert.Contains("summary-box", result);
    }

    [Fact]
    public void Generate_WithEmptySummary_ShowsNoSummaryMessage()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Summary = null;

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
            Message = "Object reference not set",
            Address = "0x00007ff123456789"
        };
        var options = new ReportOptions { IncludeCrashInfo = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("System.NullReferenceException", result);
        Assert.Contains("Object reference not set", result);
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
        Assert.Contains("callstack-table", result);
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
        Assert.Contains("5 of 20 frames", result);
    }

    [Fact]
    public void Generate_WithSourceLinks_IncludesClickableLinks()
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
        Assert.Contains("href=\"https://github.com/user/repo", result);
        Assert.Contains("target=\"_blank\"", result);
        Assert.Contains("source-link", result);
    }

    [Fact]
    public void Generate_WithSourceInfo_IncludesSourceInfo()
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
                Source = "Program.cs:42"
            }
            }
        });
        var options = new ReportOptions { IncludeCallStacks = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("Program.cs:42", result);
        Assert.Contains("source-info", result);
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
        var options = new ReportOptions { IncludeThreadInfo = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("Thread Information", result);
        Assert.Contains("Total Threads", result);
        Assert.Contains("bar-chart", result); // Thread state chart
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

        // Assert - table should be present
        Assert.Contains("<table>", result);
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
        Assert.Contains(".NET Runtime", result);
        Assert.Contains("8.0.0", result);
        Assert.Contains("System.Exception: Test", result);
        Assert.Contains("100", result);
    }

    [Fact]
    public void Generate_WithAsyncDeadlock_IncludesAlert()
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
        Assert.Contains("Async Deadlock", result);
        Assert.Contains("alert-error", result);
    }

    // ============================================================
    // Memory Section Tests
    // ============================================================

    [Fact]
    public void Generate_WithMemoryLeak_IncludesMemoryLeakAlert()
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
        Assert.Contains("bar-chart", result);
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
            InvolvedThreads = new List<string> { "1", "2" }
            }
        };
        var options = new ReportOptions { IncludeDeadlockInfo = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("DEADLOCK DETECTED", result);
        Assert.Contains("Thread 1", result);
        Assert.Contains("alert-error", result);
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
            Insights = new List<string> { "Variable may be null" }
        };
        var options = new ReportOptions { IncludeWatchResults = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.Contains("Watch Expression", result);
        Assert.Contains("myVariable", result);
        Assert.Contains("42", result);
        Assert.Contains("Variable not found", result);
        Assert.Contains("badge-success", result);
        Assert.Contains("badge-error", result);
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

    [Fact]
    public void Generate_WithDifferentRiskLevels_ShowsCorrectAlertClass()
    {
        // Test Critical
        var analysis = CreateMinimalAnalysis();
        analysis.Security = new SecurityInfo
        {
            OverallRisk = "Critical",
            Summary = "Critical vulnerability"
        };
        var options = new ReportOptions { IncludeSecurityAnalysis = true };

        var result = _generator.Generate(analysis, options, null!);
        Assert.Contains("Critical", result);
        Assert.Contains("alert-danger", result);

        // Test Low
        analysis.Security.OverallRisk = "Low";
        result = _generator.Generate(analysis, options, null!);
        Assert.Contains("Low", result);
        Assert.Contains("alert-success", result);

        // Test None
        analysis.Security.OverallRisk = "None";
        result = _generator.Generate(analysis, options, null!);
        Assert.Contains("None", result);
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
        Assert.Contains("recommendation", result); // CSS class
    }

    // ============================================================
    // Footer Tests
    // ============================================================

    [Fact]
    public void Generate_IncludesFooter()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        var metadata = new ReportMetadata { ServerVersion = "1.2.3" };

        // Act
        var result = _generator.Generate(analysis, null!, metadata);

        // Assert
        Assert.Contains("<footer>", result);
        Assert.Contains("Debugger MCP Server", result);
        Assert.Contains("1.2.3", result);
    }

    // ============================================================
    // HTML Encoding Tests
    // ============================================================

    [Fact]
    public void Generate_EncodesHtmlSpecialCharacters()
    {
        // Arrange
        var analysis = CreateMinimalAnalysis();
        analysis.Summary = new AnalysisSummary { Description = "<script>alert('xss')</script>" };
        analysis.Exception = new ExceptionDetails { Message = "Error: <test> & \"quoted\"" };
        var options = new ReportOptions { IncludeCrashInfo = true };

        // Act
        var result = _generator.Generate(analysis, options, null!);

        // Assert
        Assert.DoesNotContain("<script>", result);
        Assert.Contains("&lt;script&gt;", result);
        Assert.Contains("&amp;", result);
        Assert.Contains("&quot;", result);
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

