using Xunit;
using DebuggerMcp.Reporting;
using DebuggerMcp.Analysis;
using DebuggerMcp.Watches;

namespace DebuggerMcp.Tests.Reporting;

public class ReportGeneratorTests
{
    private static CrashAnalysisResult CreateSampleAnalysis()
    {
        return new CrashAnalysisResult
        {
            Summary = new AnalysisSummary
            {
                CrashType = "Access Violation",
                Description = "The application crashed due to a null pointer dereference.",
                Recommendations = new List<string>
                {
                    "Check for null pointer dereference in the DoSomething method",
                    "Review memory allocation patterns for System.String objects",
                    "Consider implementing IDisposable for DataObject class"
                }
            },
            Exception = new ExceptionDetails
            {
                Type = "System.NullReferenceException",
                Message = "Object reference not set to an instance of an object.",
                Address = "0x00007ff812345678"
            },
            Threads = new ThreadsInfo
            {
                All = new List<ThreadInfo>
            {
                new()
                {
                    ThreadId = "1",
                    State = "Running",
                    TopFunction = "myapp!DoSomething",
                    IsFaulting = true,
            CallStack = new List<StackFrame>
            {
                new() { Module = "myapp", Function = "DoSomething" },
                new() { Module = "myapp", Function = "ProcessData" },
                new() { Module = "ntdll", Function = "RtlUserThreadStart" }
                    }
            },
                new() { ThreadId = "2", State = "Waiting", TopFunction = "ntdll!NtWaitForSingleObject" },
                new() { ThreadId = "3", State = "Waiting", TopFunction = "kernel32!SleepEx" }
                },
                Summary = new ThreadSummary { FinalizerQueueLength = 150 }
            },
            Modules = new List<ModuleInfo>
            {
                new() { Name = "myapp.dll", BaseAddress = "0x00007ff800000000", HasSymbols = true },
                new() { Name = "ntdll.dll", BaseAddress = "0x00007ffb00000000", HasSymbols = true }
            },
            Memory = new MemoryInfo
            {
                LeakAnalysis = new LeakAnalysis
                {
                    Detected = true,
                    TotalHeapBytes = 1024 * 1024 * 50, // 50 MB
                    TopConsumers = new List<MemoryConsumer>
                {
                    new() { TypeName = "System.String", Count = 10000, TotalSize = 1024 * 1024 * 20 },
                    new() { TypeName = "System.Byte[]", Count = 500, TotalSize = 1024 * 1024 * 15 },
                    new() { TypeName = "MyApp.DataObject", Count = 1000, TotalSize = 1024 * 1024 * 10 }
                }
                }
            },
            Environment = new EnvironmentInfo
            {
                Runtime = new RuntimeInfo { ClrVersion = "8.0.0" }
            },
            Async = new AsyncInfo { HasDeadlock = false }
        };
    }

    private static ReportMetadata CreateSampleMetadata()
    {
        return new ReportMetadata
        {
            DumpId = "test-dump-001",
            UserId = "user123",
            DebuggerType = "WinDbg",
            GeneratedAt = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            ServerVersion = "1.0.0"
        };
    }

    // ============================================================
    // MARKDOWN REPORT GENERATOR TESTS
    // ============================================================

    [Fact]
    public void MarkdownGenerator_Generate_CreatesValidReport()
    {
        // Arrange
        var generator = new MarkdownReportGenerator();
        var analysis = CreateSampleAnalysis();
        var options = ReportOptions.FullReport;
        var metadata = CreateSampleMetadata();

        // Act
        var report = generator.Generate(analysis, options, metadata);

        // Assert
        Assert.NotNull(report);
        Assert.NotEmpty(report);
        Assert.Contains("# Crash Analysis Report", report);
        Assert.Contains("Access Violation", report);
    }

    [Fact]
    public void MarkdownGenerator_Generate_IncludesSummary()
    {
        // Arrange
        var generator = new MarkdownReportGenerator();
        var analysis = CreateSampleAnalysis();
        var options = ReportOptions.FullReport;
        var metadata = CreateSampleMetadata();

        // Act
        var report = generator.Generate(analysis, options, metadata);

        // Assert
        Assert.Contains("Executive Summary", report);
        Assert.Contains("null pointer dereference", report);
    }

    [Fact]
    public void MarkdownGenerator_Generate_IncludesCallStack()
    {
        // Arrange
        var generator = new MarkdownReportGenerator();
        var analysis = CreateSampleAnalysis();
        var options = ReportOptions.FullReport;
        var metadata = CreateSampleMetadata();

        // Act
        var report = generator.Generate(analysis, options, metadata);

        // Assert
        Assert.Contains("Call Stack", report);
        Assert.Contains("myapp!DoSomething", report);
        Assert.Contains("ntdll!RtlUserThreadStart", report);
    }

    [Fact]
    public void MarkdownGenerator_Generate_IncludesMemoryLeakInfo()
    {
        // Arrange
        var generator = new MarkdownReportGenerator();
        var analysis = CreateSampleAnalysis();
        var options = ReportOptions.FullReport;
        var metadata = CreateSampleMetadata();

        // Act
        var report = generator.Generate(analysis, options, metadata);

        // Assert
        Assert.Contains("Memory", report);
        Assert.Contains("System.String", report);
    }

    [Fact]
    public void MarkdownGenerator_Generate_IncludesThreadInfo()
    {
        // Arrange
        var generator = new MarkdownReportGenerator();
        var analysis = CreateSampleAnalysis();
        var options = ReportOptions.FullReport;
        var metadata = CreateSampleMetadata();

        // Act
        var report = generator.Generate(analysis, options, metadata);

        // Assert
        Assert.Contains("Thread Information", report);
        Assert.Contains("Running", report);
        Assert.Contains("Waiting", report);
    }

    [Fact]
    public void MarkdownGenerator_Generate_IncludesRecommendations()
    {
        // Arrange
        var generator = new MarkdownReportGenerator();
        var analysis = CreateSampleAnalysis();
        var options = ReportOptions.FullReport;
        var metadata = CreateSampleMetadata();

        // Act
        var report = generator.Generate(analysis, options, metadata);

        // Assert
        Assert.Contains("Recommendations", report);
        Assert.Contains("null pointer dereference", report);
    }

    [Fact]
    public void MarkdownGenerator_Generate_RespectsMaxCallStackFrames()
    {
        // Arrange
        var generator = new MarkdownReportGenerator();
        var analysis = CreateSampleAnalysis();
        var options = new ReportOptions { MaxCallStackFrames = 2 };
        var metadata = CreateSampleMetadata();

        // Act
        var report = generator.Generate(analysis, options, metadata);

        // Assert
        Assert.Contains("Showing top 2", report);
    }

    [Fact]
    public void MarkdownGenerator_Generate_ThrowsOnNullAnalysis()
    {
        // Arrange
        var generator = new MarkdownReportGenerator();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            generator.Generate(null!, new ReportOptions(), new ReportMetadata()));
    }

    // ============================================================
    // HTML REPORT GENERATOR TESTS
    // ============================================================

    [Fact]
    public void HtmlGenerator_Generate_CreatesValidHtml()
    {
        // Arrange
        var generator = new HtmlReportGenerator();
        var analysis = CreateSampleAnalysis();
        var options = ReportOptions.FullReport;
        var metadata = CreateSampleMetadata();

        // Act
        var report = generator.Generate(analysis, options, metadata);

        // Assert
        Assert.NotNull(report);
        Assert.NotEmpty(report);
        Assert.Contains("<!DOCTYPE html>", report);
        Assert.Contains("<html", report);
        Assert.Contains("</html>", report);
    }

    [Fact]
    public void HtmlGenerator_Generate_IncludesCSS()
    {
        // Arrange
        var generator = new HtmlReportGenerator();
        var analysis = CreateSampleAnalysis();
        var options = ReportOptions.FullReport;
        var metadata = CreateSampleMetadata();

        // Act
        var report = generator.Generate(analysis, options, metadata);

        // Assert
        Assert.Contains("<style>", report);
        Assert.Contains("--bg-primary", report);  // CSS variables
    }

    [Fact]
    public void HtmlGenerator_Generate_IncludesBarCharts()
    {
        // Arrange
        var generator = new HtmlReportGenerator();
        var analysis = CreateSampleAnalysis();
        var options = ReportOptions.FullReport;
        var metadata = CreateSampleMetadata();

        // Act
        var report = generator.Generate(analysis, options, metadata);

        // Assert
        Assert.Contains("bar-chart", report);
        Assert.Contains("bar-fill", report);
    }

    [Fact]
    public void HtmlGenerator_Generate_EscapesHtml()
    {
        // Arrange
        var generator = new HtmlReportGenerator();
        var analysis = CreateSampleAnalysis();
        analysis.Summary = new AnalysisSummary { Description = "<script>alert('xss')</script>" };
        var options = ReportOptions.FullReport;
        var metadata = CreateSampleMetadata();

        // Act
        var report = generator.Generate(analysis, options, metadata);

        // Assert
        Assert.DoesNotContain("<script>alert", report);
        Assert.Contains("&lt;script&gt;", report);
    }

    [Fact]
    public void HtmlGenerator_Generate_IncludesDeadlockAlert_WhenDetected()
    {
        // Arrange
        var generator = new HtmlReportGenerator();
        var analysis = CreateSampleAnalysis();
        analysis.Threads!.Deadlock = new DeadlockInfo
        {
            Detected = true,
            InvolvedThreads = new List<string> { "1", "2" }
        };
        var options = ReportOptions.FullReport;
        var metadata = CreateSampleMetadata();

        // Act
        var report = generator.Generate(analysis, options, metadata);

        // Assert
        Assert.Contains("DEADLOCK DETECTED", report);
        Assert.Contains("alert-error", report);
    }

    // ============================================================
    // REPORT SERVICE TESTS
    // ============================================================

    [Fact]
    public void ReportService_GenerateReport_SelectsMarkdownGenerator()
    {
        // Arrange
        var service = new ReportService();
        var analysis = CreateSampleAnalysis();
        var options = new ReportOptions { Format = ReportFormat.Markdown };
        var metadata = CreateSampleMetadata();

        // Act
        var report = service.GenerateReport(analysis, options, metadata);

        // Assert
        Assert.Contains("# Debugger MCP Report", report);
        Assert.DoesNotContain("<!DOCTYPE html>", report);
    }

    [Fact]
    public void ReportService_GenerateReport_SelectsHtmlGenerator()
    {
        // Arrange
        var service = new ReportService();
        var analysis = CreateSampleAnalysis();
        var options = new ReportOptions { Format = ReportFormat.Html };
        var metadata = CreateSampleMetadata();

        // Act
        var report = service.GenerateReport(analysis, options, metadata);

        // Assert
        Assert.Contains("<!DOCTYPE html>", report);
        Assert.Contains("Debugger MCP Report", report);
    }

    [Fact]
    public void ReportService_GenerateReport_WhenRawJsonDetailsDisabled_OmitsJsonDetailBlocks()
    {
        // Arrange
        var service = new ReportService();
        var analysis = CreateSampleAnalysis();
        var options = ReportOptions.SummaryReport; // IncludeRawJsonDetails=false
        var metadata = CreateSampleMetadata();

        // Act
        var report = service.GenerateReport(analysis, options, metadata);

        // Assert
        Assert.Contains("## Faulting thread", report);
        Assert.DoesNotContain("## Threads", report);
        Assert.DoesNotContain("Threads JSON", report);
        Assert.DoesNotContain("Faulting thread JSON", report);
        Assert.DoesNotContain("Thread JSON", report);
        Assert.DoesNotContain("Frame JSON", report);
    }

    [Fact]
    public void ReportService_GenerateReport_WhenMaxEnvironmentVariablesSet_TruncatesEnvironmentVariableListInMarkdown()
    {
        // Arrange
        var service = new ReportService();
        var analysis = CreateSampleAnalysis();
        analysis.Environment = new EnvironmentInfo
        {
            Process = new ProcessInfo
            {
                SensitiveDataFiltered = true,
                EnvironmentVariables = new List<string>
                {
                    "A=1",
                    "B=2",
                    "C=3",
                    "D=4"
                }
            }
        };

        var options = ReportOptions.SummaryReport;
        options.MaxEnvironmentVariables = 2;
        options.IncludeRawJsonDetails = false;
        var metadata = CreateSampleMetadata();

        // Act
        var report = service.GenerateReport(analysis, options, metadata);

        // Assert
        Assert.Contains("## Environment", report);
        Assert.Contains("Environment variables", report);
        Assert.Contains("A=1", report);
        Assert.Contains("B=2", report);
        Assert.DoesNotContain("C=3", report);
        Assert.DoesNotContain("D=4", report);
    }

    [Fact]
    public void ReportService_GenerateReport_SelectsJsonGenerator()
    {
        // Arrange
        var service = new ReportService();
        var analysis = CreateSampleAnalysis();
        var options = new ReportOptions { Format = ReportFormat.Json };
        var metadata = CreateSampleMetadata();

        // Act
        var report = service.GenerateReport(analysis, options, metadata);

        // Assert
        Assert.Contains("\"metadata\"", report);
        Assert.Contains("\"analysis\"", report);
    }

    [Theory]
    [InlineData("markdown", ReportFormat.Markdown)]
    [InlineData("md", ReportFormat.Markdown)]
    [InlineData("html", ReportFormat.Html)]
    [InlineData("htm", ReportFormat.Html)]
    [InlineData("json", ReportFormat.Json)]
    [InlineData("MARKDOWN", ReportFormat.Markdown)]
    [InlineData("HTML", ReportFormat.Html)]
    public void ReportService_ParseFormat_ParsesValidFormats(string input, ReportFormat expected)
    {
        // Act
        var result = ReportService.ParseFormat(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ReportService_ParseFormat_DefaultsToMarkdown()
    {
        // Act
        var result = ReportService.ParseFormat("");

        // Assert
        Assert.Equal(ReportFormat.Markdown, result);
    }

    [Fact]
    public void ReportService_ParseFormat_ThrowsOnInvalidFormat()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => ReportService.ParseFormat("pdf"));
    }

    [Theory]
    [InlineData(ReportFormat.Markdown, "text/markdown")]
    [InlineData(ReportFormat.Html, "text/html")]
    [InlineData(ReportFormat.Json, "application/json")]
    public void ReportService_GetContentType_ReturnsCorrectType(ReportFormat format, string expected)
    {
        // Act
        var result = ReportService.GetContentType(format);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(ReportFormat.Markdown, "md")]
    [InlineData(ReportFormat.Html, "html")]
    [InlineData(ReportFormat.Json, "json")]
    public void ReportService_GetFileExtension_ReturnsCorrectExtension(ReportFormat format, string expected)
    {
        // Act
        var result = ReportService.GetFileExtension(format);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ReportService_GenerateReportBytes_ReturnsUtf8Bytes()
    {
        // Arrange
        var service = new ReportService();
        var analysis = CreateSampleAnalysis();
        var options = new ReportOptions { Format = ReportFormat.Markdown };
        var metadata = CreateSampleMetadata();

        // Act
        var bytes = service.GenerateReportBytes(analysis, options, metadata);

        // Assert
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("Debugger MCP Report", text);
    }

    // ============================================================
    // ASCII CHARTS TESTS
    // ============================================================

    [Fact]
    public void AsciiCharts_HorizontalBarChart_GeneratesChart()
    {
        // Arrange
        var data = new Dictionary<string, long>
        {
            ["Item A"] = 100,
            ["Item B"] = 50,
            ["Item C"] = 25
        };

        // Act
        var chart = AsciiCharts.HorizontalBarChart(data, "Test Chart");

        // Assert
        Assert.NotNull(chart);
        Assert.Contains("Test Chart", chart);
        Assert.Contains("Item A", chart);
        Assert.Contains("█", chart);
    }

    [Fact]
    public void AsciiCharts_HorizontalBarChart_HandlesEmptyData()
    {
        // Arrange
        var data = new Dictionary<string, long>();

        // Act
        var chart = AsciiCharts.HorizontalBarChart(data);

        // Assert
        Assert.Equal("No data available", chart);
    }

    [Fact]
    public void AsciiCharts_FormatBytes_FormatsCorrectly()
    {
        // Assert (check format contains expected values, locale-independent)
        Assert.Contains("B", AsciiCharts.FormatBytes(0));
        Assert.Contains("KB", AsciiCharts.FormatBytes(1024));
        Assert.Contains("MB", AsciiCharts.FormatBytes(1024 * 1024));
        Assert.Contains("GB", AsciiCharts.FormatBytes((long)(1.5 * 1024 * 1024 * 1024)));
    }

    [Fact]
    public void AsciiCharts_Table_GeneratesTable()
    {
        // Arrange
        var headers = new[] { "Name", "Value" };
        var rows = new List<string[]>
        {
            new[] { "Row 1", "100" },
            new[] { "Row 2", "200" }
        };

        // Act
        var table = AsciiCharts.Table(headers, rows, "Test Table");

        // Assert
        Assert.Contains("Test Table", table);
        Assert.Contains("Name", table);
        Assert.Contains("Value", table);
        Assert.Contains("Row 1", table);
        Assert.Contains("|", table);
    }

    [Fact]
    public void AsciiCharts_ProgressBar_GeneratesBar()
    {
        // Act
        var bar = AsciiCharts.ProgressBar(50, 100, 20, "Progress");

        // Assert
        Assert.Contains("Progress", bar);
        Assert.Contains("50", bar); // Check for value, avoiding locale-specific decimal separator
        Assert.Contains("%", bar);
        Assert.Contains("█", bar);
    }

    [Fact]
    public void AsciiCharts_Sparkline_GeneratesSparkline()
    {
        // Arrange
        var values = new[] { 1.0, 2.0, 3.0, 4.0, 5.0 };

        // Act
        var sparkline = AsciiCharts.Sparkline(values);

        // Assert
        Assert.NotNull(sparkline);
        Assert.Equal(5, sparkline.Length);
    }

    // ============================================================
    // REPORT OPTIONS TESTS
    // ============================================================

    [Fact]
    public void ReportOptions_FullReport_HasAllOptionsEnabled()
    {
        // Act
        var options = ReportOptions.FullReport;

        // Assert
        Assert.True(options.IncludeCrashInfo);
        Assert.True(options.IncludeCallStacks);
        Assert.True(options.IncludeThreadInfo);
        Assert.True(options.IncludeModules);
        Assert.True(options.IncludeHeapStats);
        Assert.True(options.IncludeRecommendations);
        Assert.True(options.IncludeCharts);
    }

    [Fact]
    public void ReportOptions_SummaryReport_HasMinimalOptions()
    {
        // Act
        var options = ReportOptions.SummaryReport;

        // Assert
        Assert.True(options.IncludeCrashInfo);
        Assert.True(options.IncludeCallStacks);
        Assert.False(options.IncludeThreadInfo);
        Assert.False(options.IncludeModules);
        Assert.Equal(10, options.MaxCallStackFrames);
        Assert.False(options.IncludeRawJsonDetails);
    }

    // ============================================================
    // WATCH RESULTS IN REPORTS
    // ============================================================

    [Fact]
    public void MarkdownGenerator_Generate_IncludesWatchResults()
    {
        // Arrange
        var generator = new MarkdownReportGenerator();
        var analysis = CreateSampleAnalysis();
        analysis.Watches = new WatchEvaluationReport
        {
            TotalWatches = 2,
            SuccessfulEvaluations = 1,
            FailedEvaluations = 1,
            Watches = new List<WatchEvaluationResult>
            {
                new() { Expression = "myVar", Success = true, Value = "42" },
                new() { Expression = "badPtr", Success = false, Error = "Access denied" }
            },
            Insights = new List<string> { "Variable myVar is valid" }
        };
        var options = ReportOptions.FullReport;
        var metadata = CreateSampleMetadata();

        // Act
        var report = generator.Generate(analysis, options, metadata);

        // Assert
        Assert.Contains("Watch Expression Results", report);
        Assert.Contains("myVar", report);
        Assert.Contains("42", report);
    }

    [Fact]
    public void HtmlGenerator_Generate_IncludesWatchResults()
    {
        // Arrange
        var generator = new HtmlReportGenerator();
        var analysis = CreateSampleAnalysis();
        analysis.Watches = new WatchEvaluationReport
        {
            TotalWatches = 1,
            SuccessfulEvaluations = 1,
            FailedEvaluations = 0,
            Watches = new List<WatchEvaluationResult>
            {
                new() { Expression = "testVar", Success = true, Value = "Hello World" }
            }
        };
        var options = ReportOptions.FullReport;
        var metadata = CreateSampleMetadata();

        // Act
        var report = generator.Generate(analysis, options, metadata);

        // Assert
        Assert.Contains("Watch Expression Results", report);
        Assert.Contains("testVar", report);
        Assert.Contains("badge-success", report);
    }
}
