using System.Reflection;
using System.Text;
using DebuggerMcp.Analysis;
using DebuggerMcp.Reporting;
using Xunit;

namespace DebuggerMcp.Tests.Reporting;

/// <summary>
/// Additional coverage tests for <see cref="MarkdownReportGenerator"/>.
/// </summary>
public class MarkdownReportGeneratorAdditionalCoverageTests
{
    [Fact]
    public void Generate_WhenManagedThreadInfoPresent_UsesClrThreadTable()
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
                        ManagedThreadId = 1,
                        State = "Running",
                        ThreadType = "GC",
                        GcMode = "Preemptive",
                        LockCount = 2,
                        CurrentException = "System.InvalidOperationException",
                        CallStack = [new StackFrame { FrameNumber = 0, Module = "m", Function = "f" }]
                    }
                ]
            }
        };

        var options = new ReportOptions
        {
            IncludeThreadInfo = true,
            IncludeCallStacks = false,
            IncludeCharts = true
        };

        var report = new MarkdownReportGenerator().Generate(analysis, options, new ReportMetadata { DumpId = "d", DebuggerType = "dbg" });

        Assert.Contains("GC Mode", report, StringComparison.Ordinal);
        Assert.Contains("Locks", report, StringComparison.Ordinal);
    }

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

        var report = new MarkdownReportGenerator().Generate(analysis, options, new ReportMetadata { DumpId = "d", DebuggerType = "dbg" });

        Assert.Contains("Frame Variables", report, StringComparison.Ordinal);
        Assert.Contains("Parameters:", report, StringComparison.Ordinal);
        Assert.Contains("Local Variables:", report, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateSections_Render_SecurityAsyncAndStringAnalysis()
    {
        var sb = new StringBuilder();

        var security = new SecurityAnalysisResult
        {
            OverallRisk = SecurityRisk.Critical,
            Summary = "Critical issues detected",
            Vulnerabilities =
            [
                new Vulnerability
                {
                    Type = VulnerabilityType.HeapCorruption,
                    Severity = VulnerabilitySeverity.Critical,
                    Description = "Heap corruption",
                    Address = "0xDEAD",
                    Module = "libc",
                    Indicators = ["indicator1"],
                    Remediation = ["step1"],
                    CweIds = ["CWE-122"]
                }
            ],
            MemoryProtections = new MemoryProtectionInfo
            {
                AslrEnabled = false,
                DepEnabled = true,
                StackCanariesPresent = false,
                ModulesWithoutAslr = ["libfoo.so"]
            },
            Recommendations = ["Do the thing"]
        };

        InvokePrivateStatic(typeof(MarkdownReportGenerator), "AppendSecurityAnalysis", sb, security);

        var asyncAnalysis = new AsyncAnalysis
        {
            Summary = new AsyncSummary
            {
                TotalTasks = 10,
                CompletedTasks = 5,
                PendingTasks = 3,
                FaultedTasks = 1,
                CanceledTasks = 1
            },
            FaultedTasks = [new FaultedTaskInfo { Address = "0x1", TaskType = "Task", ExceptionType = "Ex", ExceptionMessage = "msg" }],
            PendingStateMachines = [new StateMachineInfo { Address = "0x2", StateMachineType = "SM", CurrentState = -1 }],
            AnalysisTimeMs = 123,
            WasAborted = true
        };

        InvokePrivateStatic(typeof(MarkdownReportGenerator), "AppendAsyncAnalysis", sb, asyncAnalysis);

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
            TopDuplicates = [new StringDuplicateInfo { Value = "a\\nb", Count = 2, SizePerInstance = 10, WastedBytes = 10, Suggestion = "Intern" }],
            ByLength = new StringLengthDistribution { Empty = 1, Short = 2, Medium = 3, Long = 4, VeryLong = 0 },
            AnalysisTimeMs = 77,
            WasAborted = true
        };

        InvokePrivateStatic(typeof(MarkdownReportGenerator), "AppendStringAnalysis", sb, stringAnalysis);

        var output = sb.ToString();
        Assert.Contains("Security Analysis", output, StringComparison.Ordinal);
        Assert.Contains("Async/Task Analysis", output, StringComparison.Ordinal);
        Assert.Contains("String Duplicate Analysis", output, StringComparison.Ordinal);
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

