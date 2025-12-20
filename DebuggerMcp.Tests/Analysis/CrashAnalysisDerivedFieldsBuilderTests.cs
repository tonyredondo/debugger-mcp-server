using System;
using System.Collections.Generic;
using System.Linq;
using DebuggerMcp.Analysis;
using DebuggerMcp.Analysis.Synchronization;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

public class CrashAnalysisDerivedFieldsBuilderTests
{
    [Fact]
    public void PopulateDerivedFields_WhenThreadsMissing_DoesNotThrow()
    {
        var result = new CrashAnalysisResult
        {
            Threads = null
        };

        CrashAnalysisDerivedFieldsBuilder.PopulateDerivedFields(result);

        Assert.Null(result.Signature);
        Assert.Null(result.StackSelection);
        Assert.Null(result.Symbols);
        Assert.Null(result.Timeline);
        Assert.Null(result.Findings);
        Assert.Null(result.RootCause);
    }

    [Fact]
    public void PopulateDerivedFields_WhenWaitGraphIsNullButPotentialDeadlocksPresent_PopulatesTimelineDeadlocks()
    {
        var result = CreateBaselineResult();
        result.Synchronization = new SynchronizationAnalysisResult
        {
            Summary = new SynchronizationSummary { PotentialDeadlockCount = 1 },
            PotentialDeadlocks =
            [
                new DeadlockCycle { Id = 1, Threads = [1, 2], Description = "cycle" }
            ]
        };

        CrashAnalysisDerivedFieldsBuilder.PopulateDerivedFields(result);

        Assert.NotNull(result.Timeline);
        Assert.NotNull(result.Timeline!.Deadlocks);
        Assert.Contains(result.Timeline.Deadlocks!, d => d.Kind == "monitor-cycle");
    }

    [Fact]
    public void PopulateDerivedFields_WhenWaitGraphCycleExists_AddsBlockedChainAndDeadlock()
    {
        var result = CreateBaselineResult();
        result.Synchronization = new SynchronizationAnalysisResult
        {
            Summary = new SynchronizationSummary { PotentialDeadlockCount = 1 },
            WaitGraph = new WaitGraph
            {
                Nodes =
                [
                    new WaitGraphNode { Id = "thread_1", Type = "thread", Label = "Thread 1" },
                    new WaitGraphNode { Id = "thread_2", Type = "thread", Label = "Thread 2" },
                    new WaitGraphNode { Id = "monitor_A", Type = "resource", Label = "Monitor A" },
                    new WaitGraphNode { Id = "monitor_B", Type = "resource", Label = "Monitor B" }
                ],
                Edges =
                [
                    new WaitGraphEdge { From = "thread_1", To = "monitor_A", Label = "waits" },
                    new WaitGraphEdge { From = "monitor_A", To = "thread_2", Label = "owned by" },
                    new WaitGraphEdge { From = "thread_2", To = "monitor_B", Label = "waits" },
                    new WaitGraphEdge { From = "monitor_B", To = "thread_1", Label = "owned by" }
                ]
            }
        };

        CrashAnalysisDerivedFieldsBuilder.PopulateDerivedFields(result);

        Assert.NotNull(result.Timeline);
        Assert.NotNull(result.Timeline!.BlockedChains);
        Assert.NotEmpty(result.Timeline.BlockedChains!);
        Assert.NotNull(result.Timeline.Deadlocks);
        Assert.Contains(result.Timeline.Deadlocks!, d => d.Kind == "waitgraph-cycle");
    }

    [Fact]
    public void PopulateDerivedFields_WhenMultipleSignalsTimersAndLohPressure_PopulatesFindingsAndRootCause()
    {
        var result = CreateBaselineResult();

        result.Environment = new EnvironmentInfo
        {
            CrashInfo = new CrashDiagnosticInfo { SignalName = "SIGSEGV" },
            Runtime = new RuntimeInfo { Version = "9.0.10" },
            Platform = new PlatformInfo { Os = "Linux" }
        };

        result.Exception = new ExceptionDetails { Type = "System.InvalidOperationException" };

        result.Async = new AsyncInfo
        {
            Timers = Enumerable.Range(0, 51).Select(i => new TimerInfo { Address = $"0x{i:x}" }).ToList()
        };

        result.Memory = new MemoryInfo
        {
            Gc = new GcSummary
            {
                TotalHeapSize = 1000,
                GenerationSizes = new GenerationSizes { Gen0 = 100, Gen1 = 100, Gen2 = 100, Loh = 400, Poh = 0 }
            }
        };

        result.Synchronization = new SynchronizationAnalysisResult
        {
            Summary = new SynchronizationSummary { PotentialDeadlockCount = 1 },
            PotentialDeadlocks = [new DeadlockCycle { Id = 1, Threads = [1, 2], Description = "cycle" }]
        };

        result.Modules =
        [
            new ModuleInfo { Name = "libfoo.so", BaseAddress = "0x1", HasSymbols = false },
            new ModuleInfo { Name = "libgcc_s.so.1", BaseAddress = "0x2", HasSymbols = false }
        ];

        result.Threads!.All![1].CallStack =
        [
            new StackFrame { Module = "libfoo.so", Function = "foo", IsManaged = false }
        ];

        CrashAnalysisDerivedFieldsBuilder.PopulateDerivedFields(result);

        Assert.NotNull(result.Signature);
        Assert.Equal("crash", result.Signature!.Kind);
        Assert.StartsWith("sha256:", result.Signature.Hash, StringComparison.Ordinal);

        Assert.NotNull(result.Findings);
        Assert.Contains(result.Findings!, f => f.Id == "threads.deadlock.detected");
        Assert.Contains(result.Findings!, f => f.Id == "timers.high.count");
        Assert.Contains(result.Findings!, f => f.Id == "memory.loh.pressure");
        Assert.Contains(result.Findings!, f => f.Id == "symbols.native.missing");

        Assert.NotNull(result.RootCause);
        Assert.NotEmpty(result.RootCause!.Hypotheses);
        Assert.Equal("Hang/snapshot capture (SIGSTOP)", result.RootCause.Hypotheses[0].Label);
    }

    [Fact]
    public void PopulateDerivedFields_WhenSigStopWithoutSignalOrException_AddsSigStopSnapshotFinding()
    {
        var result = CreateBaselineResult();
        result.Environment = new EnvironmentInfo { CrashInfo = new CrashDiagnosticInfo { SignalName = null } };
        result.Exception = null;

        CrashAnalysisDerivedFieldsBuilder.PopulateDerivedFields(result);

        Assert.NotNull(result.Findings);
        Assert.Contains(result.Findings!, f => f.Id == "capture.sigstop.snapshot");
    }

    [Theory]
    [InlineData("", "empty-function")]
    [InlineData("   ", "empty-function")]
    [InlineData("[Runtime]", "runtime-glue")]
    [InlineData("[ManagedMethod]", "managed-placeholder")]
    [InlineData("[JIT Code @ 0x123]", "placeholder-jit-code")]
    [InlineData("[Native Code @ 0x123]", "placeholder-jit-code")]
    public void StackFrameSelection_SelectMeaningfulTopFrame_RecordsSkipReasons(string function, string expectedReason)
    {
        var frames = new List<StackFrame>
        {
            new() { Module = "m", Function = function, IsManaged = true },
            new() { Module = "m", Function = "Meaningful", IsManaged = true }
        };

        var selection = StackFrameSelection.SelectMeaningfulTopFrame(frames);

        Assert.Equal(1, selection.SelectedFrameIndex);
        Assert.NotEmpty(selection.SkippedFrames);
        Assert.Equal(expectedReason, selection.SkippedFrames[0].Reason);
    }

    private static CrashAnalysisResult CreateBaselineResult()
    {
        return new CrashAnalysisResult
        {
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo
                    {
                        ThreadId = "1",
                        IsFaulting = true,
                        OsThreadId = "0x1",
                        State = "SIGSTOP",
                        CallStack =
                        [
                            new StackFrame { Module = "m", Function = "[Runtime]", IsManaged = true },
                            new StackFrame { Module = "m", Function = "System.Threading.Monitor.Wait", IsManaged = true, SourceFile = "f.cs", LineNumber = 12 },
                            new StackFrame { Module = "m", Function = "App.Work()", IsManaged = true, SourceFile = "f.cs", LineNumber = 13, SourceUrl = "https://example.test/f.cs#L13" }
                        ]
                    },
                    new ThreadInfo
                    {
                        ThreadId = "2",
                        IsFaulting = false,
                        OsThreadId = "0x2",
                        State = "Running",
                        CallStack =
                        [
                            new StackFrame { Module = "m2", Function = "Worker", IsManaged = false }
                        ]
                    }
                ]
            }
        };
    }
}
