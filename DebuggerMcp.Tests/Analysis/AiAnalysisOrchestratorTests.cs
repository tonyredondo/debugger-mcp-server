using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Sampling;
using DebuggerMcp.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

public class AiAnalysisOrchestratorTests
{
    [Fact]
    public async Task AnalyzeCrashAsync_WhenSamplingNotSupported_ReturnsUnavailableResult()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: false, isToolUseSupported: false);
        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance);

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal(0, result.Iterations);
        Assert.Equal("low", result.Confidence);
        Assert.Contains("does not support sampling", result.RootCause, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenToolUseNotSupported_ReturnsUnavailableResult()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: false);
        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance);

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal(0, result.Iterations);
        Assert.Equal("low", result.Confidence);
        Assert.Contains("does not support tool use", result.RootCause, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_AnalysisCompleteOnFirstIteration_ReturnsResult()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "NullReferenceException in Foo.Bar",
                confidence = "high",
                reasoning = "The report shows a null dereference."
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{\"x\":1}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal("NullReferenceException in Foo.Bar", result.RootCause);
        Assert.Equal("high", result.Confidence);
        Assert.Equal(1, result.Iterations);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_ExecThenComplete_ExecutesCommandAndRecordsIt()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!threads" }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Deadlock detected",
                confidence = "medium",
                reasoning = "Threads are blocked."
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            debugger,
            clrMdAnalyzer: null);

        Assert.Contains("!threads", debugger.ExecutedCommands);
        Assert.NotNull(result.CommandsExecuted);
        Assert.Contains(result.CommandsExecuted!, c => c.Tool == "exec" && c.Output.Contains("OUTPUT:!threads", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_ExecBlockedCommand_DoesNotExecuteAndReturnsToolError()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = ".shell whoami" }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "Blocked unsafe command."
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            debugger,
            clrMdAnalyzer: null);

        Assert.Empty(debugger.ExecutedCommands);
        Assert.NotNull(result.CommandsExecuted);
        Assert.Contains(result.CommandsExecuted!, c => c.Tool == "exec" && c.Output.Contains("Blocked unsafe", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("; .shell whoami")]
    [InlineData("command script import os")]
    [InlineData(";command script import os")]
    [InlineData("platform shell whoami")]
    public async Task AnalyzeCrashAsync_ExecBlockedCommand_WithSeparators_DoesNotExecute(string blocked)
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = blocked }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "Blocked unsafe command."
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            debugger,
            clrMdAnalyzer: null);

        Assert.Empty(debugger.ExecutedCommands);
        Assert.NotNull(result.CommandsExecuted);
        Assert.Contains(result.CommandsExecuted!, c => c.Tool == "exec" && c.Output.Contains("Blocked unsafe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_UnknownTool_ReturnsToolErrorAndContinues()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("does_not_exist", new { x = 1 }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Completed anyway",
                confidence = "low",
                reasoning = "Unknown tool call was rejected."
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(result.CommandsExecuted);
        Assert.Contains(result.CommandsExecuted!, c => c.Tool == "does_not_exist" && c.Output.Contains("Unknown tool", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MaxIterationsReached_ReturnsIncompleteResult()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithText("still thinking"))
            .EnqueueResult(CreateMessageResultWithText("still thinking"))
            .EnqueueResult(CreateMessageResultWithText("still thinking"));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal("low", result.Confidence);
        Assert.Contains("did not call analysis_complete", result.RootCause, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("still thinking", result.Reasoning);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenMaxIterationsIsZero_RunsAtLeastOneIteration()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithText("still thinking"));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 0
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal(1, result.Iterations);
        Assert.Equal("AI returned an answer but did not call analysis_complete.", result.RootCause);
        Assert.Equal("still thinking", result.Reasoning);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenToolBudgetExceeded_StopsAndDoesNotExecuteExtraCommands()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!threads" }))
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!clrstack" }))
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!dumpheap -stat" }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 10,
            MaxToolCalls = 2
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("low", result.Confidence);
        Assert.Contains("tool call budget", result.RootCause, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, debugger.ExecutedCommands.Count);
        Assert.DoesNotContain(debugger.ExecutedCommands, c => c.Contains("dumpheap", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(result.CommandsExecuted);
        Assert.Equal(2, result.CommandsExecuted!.Count);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenMaxIterationsReachedWithToolCalls_ReturnsMaxIterationsResult()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!threads" }))
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!clrstack" }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 2
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("low", result.Confidence);
        Assert.Contains("maximum iterations", result.RootCause, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Reasoning);
        Assert.Contains("did not call analysis_complete", result.Reasoning!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_GetThreadStack_ReturnsThreadFromReport()
    {
        var report = new CrashAnalysisResult
        {
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo
                    {
                        ThreadId = "17",
                        State = "stopped",
                        IsFaulting = false,
                        TopFunction = "Foo",
                        CallStack =
                        [
                            new StackFrame { FrameNumber = 0, InstructionPointer = "0x1", Module = "m", Function = "f", IsManaged = true }
                        ]
                    }
                ]
            }
        };

        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("get_thread_stack", new { threadId = "17" }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "Used thread stack."
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            report,
            "{}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(result.CommandsExecuted);
        Assert.Contains(result.CommandsExecuted!, c => c.Tool == "get_thread_stack" && c.Output.Contains("\"threadId\": \"17\"", StringComparison.OrdinalIgnoreCase));
    }

    private static CreateMessageResult CreateMessageResultWithText(string text) =>
        new()
        {
            Model = "test-model",
            Role = Role.Assistant,
            Content =
            [
                new TextContentBlock { Text = text }
            ]
        };

    private static CreateMessageResult CreateMessageResultWithToolUse(string name, object input)
    {
        var json = JsonSerializer.SerializeToElement(input);
        return new CreateMessageResult
        {
            Model = "test-model",
            Role = Role.Assistant,
            Content =
            [
                new ToolUseContentBlock
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = name,
                    Input = json
                }
            ]
        };
    }

    private sealed class FakeSamplingClient(bool isSamplingSupported, bool isToolUseSupported) : ISamplingClient
    {
        private readonly Queue<CreateMessageResult> _results = new();

        public bool IsSamplingSupported { get; } = isSamplingSupported;

        public bool IsToolUseSupported { get; } = isToolUseSupported;

        public FakeSamplingClient EnqueueResult(CreateMessageResult result)
        {
            _results.Enqueue(result);
            return this;
        }

        public Task<CreateMessageResult> RequestCompletionAsync(CreateMessageRequestParams request, CancellationToken cancellationToken = default)
        {
            if (_results.Count == 0)
            {
                return Task.FromResult(CreateMessageResultWithText("no more scripted responses"));
            }

            return Task.FromResult(_results.Dequeue());
        }
    }
}
