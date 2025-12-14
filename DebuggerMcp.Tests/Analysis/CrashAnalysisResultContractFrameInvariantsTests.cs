using System.Collections.Generic;
using DebuggerMcp.Analysis;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

public class CrashAnalysisResultContractFrameInvariantsTests
{
    [Fact]
    public void AssertValid_WhenSourceUrlHasLineAnchor_RequiresMatchingLineNumber()
    {
        var result = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { Description = "Found 1 threads (1 total frames, 1 in faulting thread), 0 modules." },
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
                                Module = "m",
                                Function = "f",
                                IsManaged = true,
                                SourceFile = "file.cs",
                                LineNumber = 42,
                                SourceUrl = "https://github.com/org/repo/blob/sha/file.cs#L42",
                                SourceProvider = "GitHub"
                            }
                        ]
                    }
                ]
            },
            Modules = new List<ModuleInfo>()
        };

        CrashAnalysisResultFinalizer.Finalize(result);
        CrashAnalysisResultContract.AssertValid(result);
    }

    [Fact]
    public void AssertValid_WhenSourceUrlHasNoLineAnchor_AllowsMissingLineNumber()
    {
        var result = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { Description = "Found 1 threads (1 total frames, 1 in faulting thread), 0 modules." },
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
                                Module = "m",
                                Function = "f",
                                IsManaged = false,
                                SourceFile = "/__w/1/s/src/runtime/src/coreclr/vm/jitinterface.cpp",
                                LineNumber = null,
                                SourceUrl = "https://github.com/dotnet/dotnet/blob/sha/src/runtime/src/coreclr/vm/jitinterface.cpp",
                                SourceProvider = "GitHub"
                            }
                        ]
                    }
                ]
            },
            Modules = new List<ModuleInfo>()
        };

        CrashAnalysisResultFinalizer.Finalize(result);
        CrashAnalysisResultContract.AssertValid(result);
    }
}
