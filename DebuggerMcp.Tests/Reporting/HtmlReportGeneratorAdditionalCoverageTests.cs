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

