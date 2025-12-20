using System.Reflection;
using System.Text;
using DebuggerMcp.Analysis;
using DebuggerMcp.Reporting;
using Xunit;

namespace DebuggerMcp.Tests.Reporting;

/// <summary>
/// Additional coverage tests for <see cref="HtmlReportGenerator"/>.
/// </summary>
public class HtmlReportGeneratorAdditionalCoverageTests
{
    [Fact]
    public void Generate_WhenFaultingThreadHasVariables_RendersFrameVariablesSection()
    {
        var analysis = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { CrashType = "Test", Description = "desc" },
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo
                    {
                        ThreadId = "1",
                        State = "Faulted",
                        IsFaulting = true,
                        CallStack =
                        [
                            new StackFrame
                            {
                                FrameNumber = 0,
                                Module = "MyApp",
                                Function = "MyApp.Program.Main",
                                IsManaged = true,
                                Parameters =
                                [
                                    new LocalVariable { Name = "x", Type = "System.Int32", Value = 42 },
                                    new LocalVariable { Name = "s", Type = "System.String", Value = "\"hi\"", ByRefAddress = "0x10" }
                                ],
                                Locals =
                                [
                                    new LocalVariable { Name = "tmp", Type = "System.Int32", Value = 7 }
                                ]
                            }
                        ]
                    }
                ]
            }
        };

        var options = new ReportOptions
        {
            IncludeCallStacks = true,
            IncludeThreadInfo = false,
            MaxCallStackFrames = 10
        };

        var report = new HtmlReportGenerator().Generate(analysis, options, new ReportMetadata { DumpId = "d", DebuggerType = "dbg" });

        Assert.Contains("Frame Variables", report, StringComparison.Ordinal);
        Assert.Contains("<details>", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Parameters:", report, StringComparison.Ordinal);
        Assert.Contains("Local Variables:", report, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateSections_Render_SecurityAsyncAndStringAnalysis()
    {
        var sb = new StringBuilder();

        var security = new SecurityAnalysisResult
        {
            OverallRisk = SecurityRisk.High,
            Summary = "Issues detected",
            Vulnerabilities =
            [
                new Vulnerability
                {
                    Type = VulnerabilityType.StackCorruption,
                    Severity = VulnerabilitySeverity.High,
                    Description = "Stack corruption",
                    Address = "0xBEEF",
                    Indicators = ["ind1", "ind2"],
                    Remediation = ["r1"],
                    CweIds = ["CWE-121"]
                }
            ],
            MemoryProtections = new MemoryProtectionInfo
            {
                AslrEnabled = true,
                DepEnabled = false,
                StackCanariesPresent = true,
                ModulesWithoutAslr = ["libbar.so"]
            },
            Recommendations = ["Patch deps"]
        };

        InvokePrivateStatic(typeof(HtmlReportGenerator), "AppendSecurityAnalysis", sb, security);

        var asyncAnalysis = new AsyncAnalysis
        {
            Summary = new AsyncSummary { TotalTasks = 1, PendingTasks = 1 },
            FaultedTasks = [new FaultedTaskInfo { Address = "0x1", TaskType = "Task", ExceptionType = "Ex" }],
            PendingStateMachines = [new StateMachineInfo { Address = "0x2", StateMachineType = "SM", CurrentState = 0 }],
            AnalysisTimeMs = 1,
            WasAborted = false
        };

        InvokePrivateStatic(typeof(HtmlReportGenerator), "AppendAsyncAnalysisHtml", sb, asyncAnalysis);

        var stringAnalysis = new StringAnalysis
        {
            Summary = new StringAnalysisSummary
            {
                TotalStrings = 10,
                UniqueStrings = 2,
                DuplicateStrings = 8,
                TotalSize = 100,
                WastedSize = 80,
                WastedPercentage = 80
            },
            TopDuplicates = [new StringDuplicateInfo { Value = "dup", Count = 2, SizePerInstance = 10, WastedBytes = 10 }],
            ByLength = new StringLengthDistribution { Empty = 1, Short = 2, Medium = 3, Long = 4, VeryLong = 0 },
            AnalysisTimeMs = 2,
            WasAborted = false
        };

        InvokePrivateStatic(typeof(HtmlReportGenerator), "AppendStringAnalysisHtml", sb, stringAnalysis);

        var output = sb.ToString();
        Assert.Contains("Security Analysis", output, StringComparison.Ordinal);
        Assert.Contains("Async/Task Analysis", output, StringComparison.Ordinal);
        Assert.Contains("String Duplicate Analysis", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.cs", "csharp")]
    [InlineData("a.fs", "fsharp")]
    [InlineData("a.vb", "vbnet")]
    [InlineData("a.cpp", "cpp")]
    [InlineData("a.c", "c")]
    [InlineData("a.h", "cpp")]
    [InlineData("a.rs", "rust")]
    [InlineData("a.go", "go")]
    [InlineData("a.java", "java")]
    [InlineData("a.kt", "kotlin")]
    [InlineData("a.js", "javascript")]
    [InlineData("a.ts", "typescript")]
    [InlineData("a.py", "python")]
    [InlineData("a.rb", "ruby")]
    [InlineData("a.php", "php")]
    [InlineData("a.swift", "swift")]
    [InlineData("a.m", "objectivec")]
    [InlineData("a.sh", "bash")]
    [InlineData("a.ps1", "powershell")]
    [InlineData("a.json", "json")]
    [InlineData("a.yml", "yaml")]
    [InlineData("a.xml", "xml")]
    [InlineData("a.unknown", "plaintext")]
    public void GuessHighlightJsLanguage_MapsExtensions(string sourceFile, string expected)
    {
        var method = typeof(HtmlReportGenerator).GetMethod("GuessHighlightJsLanguage", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var actual = method!.Invoke(null, new object?[] { sourceFile }) as string;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PrivateSections_Render_TopMemoryConsumersLargeObjectsAndTimeout()
    {
        var sb = new StringBuilder();
        var mem = new TopMemoryConsumers
        {
            Summary = new HeapWalkSummary
            {
                TotalObjects = 10,
                TotalSize = 100,
                UniqueTypes = 2,
                AnalysisTimeMs = 123,
                WasAborted = true
            },
            BySize = [new TypeMemoryStats { Type = "T", Count = 1, TotalSize = 100, Percentage = 99.9 }],
            LargeObjects = [new LargeObjectInfo { Address = "0x1", Type = "Big", Size = 90000, Generation = "Large" }]
        };

        InvokePrivateStatic(typeof(HtmlReportGenerator), "AppendTopMemoryConsumersHtml", sb, mem);

        var output = sb.ToString();
        Assert.Contains("Top Memory Consumers", output, StringComparison.Ordinal);
        Assert.Contains("Large Objects", output, StringComparison.Ordinal);
        Assert.Contains("timeout", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrivateSections_Render_SourceContextAndTruncation()
    {
        var sb = new StringBuilder();
        var entries = new List<SourceContextEntry>
        {
            new()
            {
                ThreadId = "1",
                FrameNumber = 0,
                Module = "m",
                Function = "f",
                SourceFile = "f.cs",
                LineNumber = 10,
                SourceUrl = "https://example.test/f.cs#L10",
                StartLine = 8,
                EndLine = 12,
                Status = "remote",
                Lines = ["a", "b", "c"]
            },
            new()
            {
                ThreadId = "2",
                FrameNumber = 1,
                Status = "error",
                Error = "oops"
            }
        };

        var append = typeof(HtmlReportGenerator).GetMethod("AppendSourceContext", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(append);
        append!.Invoke(null, new object[] { sb, entries });

        var truncate = typeof(HtmlReportGenerator).GetMethod("TruncateString", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(truncate);
        var truncated = truncate!.Invoke(null, new object?[] { new string('a', 200), 30 }) as string;
        Assert.NotNull(truncated);
        Assert.True(truncated!.Length <= 30);

        var output = sb.ToString();
        Assert.Contains("Source Context", output, StringComparison.Ordinal);
        Assert.Contains("<pre", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("oops", output, StringComparison.Ordinal);
    }

    private static void InvokePrivateStatic(Type type, string methodName, StringBuilder sb, object arg)
    {
        var method = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m =>
                m.Name == methodName &&
                m.GetParameters().Length == 2 &&
                m.GetParameters()[0].ParameterType == typeof(StringBuilder));

        Assert.NotNull(method);
        method!.Invoke(null, [sb, arg]);
    }
}
