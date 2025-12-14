using System.Collections.Generic;
using DebuggerMcp.Analysis;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Tests for <see cref="CrashAnalysisResultFinalizer"/> to ensure report invariants are enforced in one place.
/// </summary>
public class CrashAnalysisResultFinalizerTests
{
    [Fact]
    public void Finalize_WithNullThreads_DoesNotThrow()
    {
        var result = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary(),
            Threads = null
        };

        CrashAnalysisResultFinalizer.Finalize(result);
    }

    [Fact]
    public void Finalize_RenumbersFramesAndRecomputesTopFunction()
    {
        var result = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { Description = "Found 2 threads (0 total frames, 0 in faulting thread), 0 modules." },
            Modules = new List<ModuleInfo>(),
            Threads = new ThreadsInfo
            {
                All = new List<ThreadInfo>
                {
                    new()
                    {
                        ThreadId = "t1",
                        TopFunction = string.Empty,
                        CallStack = new List<StackFrame>
                        {
                            new() { FrameNumber = 10, InstructionPointer = "0x1", Module = "", Function = "[JIT Code @ 0x0000]", IsManaged = true },
                            new() { FrameNumber = 20, InstructionPointer = "0x2", Module = "libcoreclr.so", Function = "ThreadNativeWait", IsManaged = false }
                        }
                    }
                }
            }
        };

        CrashAnalysisResultFinalizer.Finalize(result);

        var thread = result.Threads!.All![0];
        Assert.Equal("libcoreclr.so!ThreadNativeWait", thread.TopFunction);
        Assert.Equal(0, thread.CallStack[0].FrameNumber);
        Assert.Equal(1, thread.CallStack[1].FrameNumber);
    }

    [Fact]
    public void Finalize_WhenManagedMethodPlaceholderHasIsManagedFalse_SetsIsManagedTrue()
    {
        var result = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { Description = "Found 1 threads (0 total frames, 0 in faulting thread), 0 modules." },
            Modules = new List<ModuleInfo>(),
            Threads = new ThreadsInfo
            {
                All = new List<ThreadInfo>
                {
                    new()
                    {
                        ThreadId = "t1",
                        CallStack = new List<StackFrame>
                        {
                            new() { FrameNumber = 0, InstructionPointer = "0x1", Module = "", Function = "[ManagedMethod]", IsManaged = false }
                        }
                    }
                }
            }
        };

        CrashAnalysisResultFinalizer.Finalize(result);

        Assert.True(result.Threads!.All![0].CallStack[0].IsManaged);
    }

    [Fact]
    public void Finalize_RefreshesCountsClause_WhenPresent()
    {
        var result = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary
            {
                Description = "Crash Type: Unknown. Found 99 threads (999 total frames, 999 in faulting thread), 999 modules."
            },
            Modules = new List<ModuleInfo> { new(), new() },
            Threads = new ThreadsInfo
            {
                All = new List<ThreadInfo>
                {
                    new()
                    {
                        ThreadId = "t1",
                        IsFaulting = true,
                        CallStack = new List<StackFrame>
                        {
                            new() { FrameNumber = 0, InstructionPointer = "0x1", Module = "m", Function = "f", IsManaged = true }
                        }
                    }
                }
            }
        };

        CrashAnalysisResultFinalizer.Finalize(result);

        Assert.Contains("Found 1 threads (1 total frames, 1 in faulting thread), 2 modules.", result.Summary!.Description);
        Assert.Equal(1, result.Summary.ThreadCount);
        Assert.Equal(2, result.Summary.ModuleCount);
        Assert.Equal(1, result.Threads!.OsThreadCount);
        Assert.NotNull(result.Threads.FaultingThread);
        Assert.Equal("t1", result.Threads.FaultingThread!.ThreadId);
    }

    [Fact]
    public void Finalize_WhenRawCommandsContainLowValueEntries_RemovesThem()
    {
        var result = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { Description = "Found 1 threads (0 total frames, 0 in faulting thread), 0 modules." },
            Modules = new List<ModuleInfo>(),
            Threads = new ThreadsInfo
            {
                All = new List<ThreadInfo>
                {
                    new()
                    {
                        ThreadId = "t1",
                        CallStack = new List<StackFrame>()
                    }
                }
            },
            RawCommands = new Dictionary<string, string>
            {
                ["expr -- (char*)0x123"] = "(char *) $1 = 0x123 \"secret\"",
                ["ClrMD:InspectModule(0xabc)"] = "{ \"huge\": true }",
                ["bt all"] = "frame #0 ...",
                ["!clrthreads"] = "ThreadCount: 1"
            }
        };

        CrashAnalysisResultFinalizer.Finalize(result);

        Assert.DoesNotContain(result.RawCommands!.Keys, k => k.StartsWith("expr -- (char*)"));
        Assert.DoesNotContain(result.RawCommands!.Keys, k => k.StartsWith("ClrMD:InspectModule("));
        Assert.Contains("bt all", result.RawCommands.Keys);
        Assert.Contains("!clrthreads", result.RawCommands.Keys);
    }
}
