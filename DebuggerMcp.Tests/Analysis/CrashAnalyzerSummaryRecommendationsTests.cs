using System.Collections.Generic;
using DebuggerMcp.Analysis;
using Moq;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

public class CrashAnalyzerSummaryRecommendationsTests
{
    private sealed class TestCrashAnalyzer : CrashAnalyzer
    {
        public TestCrashAnalyzer(IDebuggerManager manager) : base(manager) { }

        public void TestGenerateSummary(CrashAnalysisResult result) => GenerateSummary(result);
    }

    [Fact]
    public void GenerateSummary_WhenActionableNativeModuleMissingSymbolsAndFrameHasNoSource_AddsRecommendationWithExample()
    {
        var analyzer = new TestCrashAnalyzer(new Mock<IDebuggerManager>().Object);
        var result = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { CrashType = "Unknown" },
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo
                    {
                        ThreadId = "t1",
                        IsFaulting = true,
                        CallStack =
                        [
                            new StackFrame
                            {
                                FrameNumber = 0,
                                InstructionPointer = "0x1",
                                Module = "libcoreclr.so",
                                Function = "f",
                                IsManaged = false,
                                Source = null,
                                SourceFile = null,
                                SourceUrl = null
                            }
                        ]
                    }
                ],
                Summary = new ThreadSummary()
            },
            Modules =
            [
                new ModuleInfo { Name = "libcoreclr.so", HasSymbols = false, BaseAddress = "0x0" }
            ]
        };

        analyzer.TestGenerateSummary(result);

        var recommendation = Assert.Single(result.Summary!.Recommendations!, r => r.Contains("missing debug symbols", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains("libcoreclr.so", recommendation, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateSummary_WhenActionableNativeModuleMissingSymbolsButFrameHasSource_DoesNotRecommendSymbolUpload()
    {
        var analyzer = new TestCrashAnalyzer(new Mock<IDebuggerManager>().Object);
        var result = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { CrashType = "Unknown" },
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo
                    {
                        ThreadId = "t1",
                        IsFaulting = true,
                        CallStack =
                        [
                            new StackFrame
                            {
                                FrameNumber = 0,
                                InstructionPointer = "0x1",
                                Module = "libcoreclr.so",
                                Function = "f",
                                IsManaged = false,
                                SourceFile = "threads.cpp",
                                LineNumber = 10
                            }
                        ]
                    }
                ],
                Summary = new ThreadSummary()
            },
            Modules =
            [
                new ModuleInfo { Name = "libcoreclr.so", HasSymbols = false, BaseAddress = "0x0" }
            ]
        };

        analyzer.TestGenerateSummary(result);

        Assert.DoesNotContain(result.Summary!.Recommendations!, r => r.Contains("missing debug symbols", System.StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Summary.Recommendations!, r => r.Contains("Upload debug symbols", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateSummary_WhenOnlyNonActionableNativeModulesMissingSymbols_DoesNotRecommendSymbolUpload()
    {
        var analyzer = new TestCrashAnalyzer(new Mock<IDebuggerManager>().Object);
        var result = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { CrashType = "Unknown" },
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo
                    {
                        ThreadId = "t1",
                        IsFaulting = true,
                        CallStack =
                        [
                            new StackFrame { FrameNumber = 0, InstructionPointer = "0x1", Module = "libcoreclr.so", Function = "f", IsManaged = false }
                        ]
                    }
                ],
                Summary = new ThreadSummary()
            },
            Modules =
            [
                new ModuleInfo { Name = "[vdso] (0x0)", HasSymbols = false },
                new ModuleInfo { Name = "libgcc_s.so.1", HasSymbols = false }
            ]
        };

        analyzer.TestGenerateSummary(result);

        Assert.DoesNotContain(result.Summary!.Recommendations!, r => r.Contains("missing debug symbols", System.StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Summary.Recommendations!, r => r.Contains("Upload symbol", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateSummary_WhenFaultingThreadIsSigStop_AddsSnapshotRecommendation()
    {
        var analyzer = new TestCrashAnalyzer(new Mock<IDebuggerManager>().Object);
        var result = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { CrashType = "Unknown" },
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo
                    {
                        ThreadId = "t1",
                        IsFaulting = true,
                        State = "signal SIGSTOP",
                        CallStack =
                        [
                            new StackFrame { FrameNumber = 0, InstructionPointer = "0x1", Module = "m", Function = "f", IsManaged = false }
                        ]
                    }
                ],
                Summary = new ThreadSummary()
            },
            Modules = new List<ModuleInfo>()
        };

        analyzer.TestGenerateSummary(result);

        Assert.Contains(result.Summary!.Recommendations!, r => r.Contains("SIGSTOP", System.StringComparison.OrdinalIgnoreCase));
    }
}
