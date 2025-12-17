using System.Text.Json;
using DebuggerMcp.Analysis;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

public class AiSamplingPromptBuilderTests
{
    [Fact]
    public void Build_ExcludesRecommendationsAndEnvironmentVariables()
    {
        var report = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary
            {
                CrashType = "Crash",
                Severity = "high",
                ThreadCount = 1,
                ModuleCount = 1,
                AssemblyCount = 1,
                Recommendations = ["do-not-include"],
                Warnings = ["warn"],
                Errors = ["err"]
            },
            Environment = new EnvironmentInfo
            {
                Process = new ProcessInfo
                {
                    Arguments = ["app", "--token", "secret"],
                    EnvironmentVariables = ["SECRET=abc", "PATH=/bin"],
                    SensitiveDataFiltered = true
                }
            }
        };

        var prompt = AiSamplingPromptBuilder.Build(report);
        using var doc = JsonDocument.Parse(prompt);

        Assert.True(doc.RootElement.TryGetProperty("summary", out var summary));
        Assert.False(summary.TryGetProperty("recommendations", out _));
        Assert.True(summary.TryGetProperty("warnings", out _));
        Assert.True(summary.TryGetProperty("errors", out _));

        Assert.True(doc.RootElement.TryGetProperty("environment", out var env));
        Assert.True(env.TryGetProperty("process", out var process));
        Assert.True(process.TryGetProperty("arguments", out var args));
        Assert.Equal("app", args[0].GetString());
        Assert.False(process.TryGetProperty("environmentVariables", out _));
    }

    [Fact]
    public void Build_TopFrame_UsesMeaningfulTopFrameCandidate()
    {
        var report = new CrashAnalysisResult
        {
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo
                    {
                        ThreadId = "1",
                        State = "stopped",
                        IsFaulting = false,
                        TopFunction = "m!Real",
                        CallStack =
                        [
                            new StackFrame { FrameNumber = 0, Module = "", Function = "[JIT Code @ 0x1234]", IsManaged = true },
                            new StackFrame { FrameNumber = 1, Module = "m", Function = "Real", IsManaged = true }
                        ]
                    }
                ]
            }
        };

        var prompt = AiSamplingPromptBuilder.Build(report);
        using var doc = JsonDocument.Parse(prompt);

        var threads = doc.RootElement.GetProperty("threads");
        var all = threads.GetProperty("all");
        Assert.Equal(1, all.GetArrayLength());

        var first = all[0];
        var topFrame = first.GetProperty("topFrame");
        Assert.Equal("m", topFrame.GetProperty("module").GetString());
        Assert.Equal("Real", topFrame.GetProperty("function").GetString());
    }

    [Fact]
    public void Build_CapsThreadListAndReportsTruncation()
    {
        var allThreads = new List<ThreadInfo>();
        for (var i = 0; i < 201; i++)
        {
            allThreads.Add(new ThreadInfo
            {
                ThreadId = i.ToString(),
                State = "stopped",
                TopFunction = "t",
                CallStack = []
            });
        }

        var report = new CrashAnalysisResult
        {
            Threads = new ThreadsInfo
            {
                All = allThreads
            }
        };

        var prompt = AiSamplingPromptBuilder.Build(report);
        using var doc = JsonDocument.Parse(prompt);

        var threads = doc.RootElement.GetProperty("threads");
        var all = threads.GetProperty("all");
        Assert.Equal(200, all.GetArrayLength());

        var truncation = threads.GetProperty("truncation");
        Assert.True(truncation.GetProperty("threadsCapped").GetBoolean());
        Assert.Equal(200, truncation.GetProperty("maxThreads").GetInt32());
    }

    [Fact]
    public void Build_CapsFaultingThreadCallStackAndReportsTruncation()
    {
        var frames = new List<StackFrame>();
        for (var i = 0; i < 61; i++)
        {
            frames.Add(new StackFrame { FrameNumber = i, Module = "m", Function = $"f{i}", IsManaged = true });
        }

        var report = new CrashAnalysisResult
        {
            Threads = new ThreadsInfo
            {
                FaultingThread = new ThreadInfo
                {
                    ThreadId = "fault",
                    State = "stopped",
                    IsFaulting = true,
                    TopFunction = "m!f0",
                    CallStack = frames
                },
                All =
                [
                    new ThreadInfo
                    {
                        ThreadId = "fault",
                        State = "stopped",
                        IsFaulting = true,
                        TopFunction = "m!f0",
                        CallStack = frames
                    }
                ]
            }
        };

        var prompt = AiSamplingPromptBuilder.Build(report);
        using var doc = JsonDocument.Parse(prompt);

        var faulting = doc.RootElement.GetProperty("threads").GetProperty("faultingThread");
        var callStack = faulting.GetProperty("callStack");
        Assert.Equal(60, callStack.GetArrayLength());

        var truncation = faulting.GetProperty("truncation");
        Assert.True(truncation.GetProperty("callStackCapped").GetBoolean());
        Assert.Equal(60, truncation.GetProperty("maxFrames").GetInt32());
    }
}

