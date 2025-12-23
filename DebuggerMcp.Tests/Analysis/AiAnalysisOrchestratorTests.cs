using System.IO;
using System.Linq;
using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Sampling;
using DebuggerMcp.Tests.TestDoubles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

public class AiAnalysisOrchestratorTests
{
    [Fact]
    public async Task AnalyzeCrashAsync_WhenModelCallsAnalysisCompleteImmediately_RequiresEvidenceToolBeforeCompleting()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "premature",
                confidence = "high",
                reasoning = "done"
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new
            {
                path = "analysis.exception"
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "root cause after evidence",
                confidence = "low",
                reasoning = "done"
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{\"metadata\":{},\"analysis\":{\"exception\":{\"type\":\"System.Exception\",\"message\":\"boom\"}}}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal("root cause after evidence", result.RootCause);
        Assert.NotNull(result.CommandsExecuted);
        Assert.Contains(result.CommandsExecuted!, c => c.Tool == "report_get");
    }

    [Fact]
    public async Task RewriteSummaryAsync_WhenModelCallsCompletionImmediately_RequiresEvidenceToolBeforeCompleting()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_summary_rewrite_complete", new
            {
                description = "premature",
                recommendations = new[] { "r1" }
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new
            {
                path = "analysis.exception"
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_summary_rewrite_complete", new
            {
                description = "rewritten",
                recommendations = new[] { "r1", "r2" }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3
        };

        var result = await orchestrator.RewriteSummaryAsync(
            new CrashAnalysisResult(),
            "{\"metadata\":{},\"analysis\":{\"exception\":{\"type\":\"System.Exception\",\"message\":\"boom\"}}}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(result);
        Assert.Equal("rewritten", result!.Description);
        Assert.NotNull(result.Recommendations);
        Assert.Equal(2, result.Recommendations!.Count);
        Assert.NotNull(result.CommandsExecuted);
        Assert.Contains(result.CommandsExecuted!, c => c.Tool == "report_get");
    }

    [Fact]
    public async Task RewriteSummaryAsync_WhenToolExecutionThrows_ReturnsErrorToolResultAndCanStillComplete()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "boom" }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_summary_rewrite_complete", new
            {
                description = "rewritten",
                recommendations = new[] { "r1" }
            }));

        var debugger = new FakeDebuggerManager
        {
            DebuggerType = "LLDB",
            CommandHandler = _ => throw new InvalidOperationException("boom")
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5
        };

        var result = await orchestrator.RewriteSummaryAsync(
            new CrashAnalysisResult(),
            "{\"metadata\":{},\"analysis\":{\"summary\":{\"crashType\":\"Managed\"}}}",
            debugger,
            clrMdAnalyzer: null);

        Assert.NotNull(result);
        Assert.Equal("rewritten", result!.Description);
        Assert.Null(result.Error);
        Assert.NotNull(result.CommandsExecuted);
        Assert.Contains(result.CommandsExecuted!, c => c.Tool == "exec" && c.Output.Contains("boom", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GenerateThreadNarrativeAsync_WhenModelCallsCompletionImmediately_RequiresEvidenceToolBeforeCompleting()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_thread_narrative_complete", new
            {
                description = "premature",
                confidence = "high"
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new
            {
                path = "analysis.threads"
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_thread_narrative_complete", new
            {
                description = "narrative",
                confidence = "low"
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3
        };

        var result = await orchestrator.GenerateThreadNarrativeAsync(
            new CrashAnalysisResult(),
            "{\"metadata\":{},\"analysis\":{\"threads\":{\"summary\":{\"total\":1}}}}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(result);
        Assert.Equal("narrative", result!.Description);
        Assert.Equal("low", result.Confidence);
        Assert.NotNull(result.CommandsExecuted);
        Assert.Contains(result.CommandsExecuted!, c => c.Tool == "report_get");
    }

    [Fact]
    public async Task AnalyzeCrashAsync_EmitsSamplingRequestAndResponseLogs()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        var logger = new CollectingLogger<AiAnalysisOrchestrator>((level, message) => logs.Add((level, message)));

        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "done"
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, logger)
        {
            MaxIterations = 1
        };

        _ = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{\"x\":1}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Contains(logs, l => l.Level == LogLevel.Debug && l.Message.Contains("Sampling request", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, l => l.Level == LogLevel.Debug && l.Message.Contains("Sampling response", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, l => l.Level == LogLevel.Debug && l.Message.Contains("Tool requested", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenVerboseTraceEnabled_LogsMessagePreviews()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        var logger = new CollectingLogger<AiAnalysisOrchestrator>((level, message) => logs.Add((level, message)));

        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "done"
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, logger)
        {
            MaxIterations = 1,
            EnableVerboseSamplingTrace = true
        };

        _ = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{\"x\":1}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Contains(logs, l => l.Level == LogLevel.Information && l.Message.Contains("system prompt preview", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, l => l.Level == LogLevel.Information && l.Message.Contains("messages tail preview", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenVerboseTraceEnabled_EmitsSamplingTraceAtInformation()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        var logger = new CollectingLogger<AiAnalysisOrchestrator>((level, message) => logs.Add((level, message)));

        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "done"
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, logger)
        {
            MaxIterations = 1,
            EnableVerboseSamplingTrace = true
        };

        _ = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{\"x\":1}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Contains(logs, l => l.Level == LogLevel.Information && l.Message.Contains("Sampling request", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, l => l.Level == LogLevel.Information && l.Message.Contains("Sampling response", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, l => l.Level == LogLevel.Information && l.Message.Contains("Tool requested", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenToolCallBudgetExceeded_RequestsFinalSynthesisWithoutTools()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new { path = "analysis.exception", pageKind = "object" }))
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!clrstack -a" }))
            .EnqueueResult(CreateMessageResultWithText("""
            {
              "rootCause": "final root cause",
              "confidence": "low",
              "reasoning": "based on collected evidence",
              "recommendations": ["do X"],
              "additionalFindings": ["note Y"]
            }
            """));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 10,
            MaxToolCalls = 1
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{\"metadata\":{},\"analysis\":{\"exception\":{\"type\":\"System.Exception\",\"message\":\"boom\"}}}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal("final root cause", result.RootCause);
        Assert.NotNull(result.CommandsExecuted);
        Assert.Contains(result.CommandsExecuted!, c => c.Tool == "report_get");

        Assert.True(requests.Count >= 3);
        var finalRequest = requests[2];
        Assert.Null(finalRequest.ToolChoice);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenSamplingReturnsEmptyContent_DoesNotCountFailedIteration()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!a" }))
            .EnqueueResult(new CreateMessageResult
            {
                Model = "test",
                Content = []
            })
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "done",
                confidence = "low",
                reasoning = "ok"
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 10,
            MaxSamplingRequestAttempts = 2
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("done", result.RootCause);
        Assert.Equal(2, result.Iterations);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_IncludesSosHelpGuidanceInSystemPrompt()
    {
        CreateMessageRequestParams? seenRequest = null;

        var sampling = new CapturingSamplingClient(
            onRequest: req => seenRequest = req,
            result: CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "done"
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 1
        };

        _ = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(seenRequest);
        Assert.False(string.IsNullOrWhiteSpace(seenRequest!.SystemPrompt));
        Assert.Contains("metadata.debuggertype", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("metadata.sosloaded", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plugin load libsosplugin.so", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NEVER attempt to load SOS", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never run windbg", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("report_get", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sos help", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("!clrstack -a", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Prefer using the inspect tool", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do NOT recommend disabling profilers", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not present speculation as fact", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not assume the .NET runtime is bug-free", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("analysis.sourcecontext", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceurl", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
    }

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
    public async Task AnalyzeCrashAsync_AnalysisCompleteAfterEvidence_ReturnsResult()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new
            {
                path = "analysis.exception"
            }))
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
            "{\"metadata\":{},\"analysis\":{\"exception\":{\"type\":\"System.NullReferenceException\",\"message\":\"boom\"}}}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal("NullReferenceException in Foo.Bar", result.RootCause);
        Assert.Equal("high", result.Confidence);
        Assert.Equal(2, result.Iterations);
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
    public async Task AnalyzeCrashAsync_WhenExecToolCallRepeated_ReusesCachedToolResult()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "  !THREADS  " }))
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!threads" }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "done"
            }));

        var debugger = new FakeDebuggerManager
        {
            DebuggerType = "LLDB",
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

        Assert.Single(debugger.ExecutedCommands);
        Assert.Contains("!THREADS", debugger.ExecutedCommands[0], StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.CommandsExecuted);
        var execCalls = result.CommandsExecuted!.Where(c => c.Tool == "exec").ToList();
        Assert.Equal(2, execCalls.Count);
        Assert.Contains("cached tool result", execCalls[1].Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenExecDumpobjAndInspectorIsOpen_RewritesToInspect()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "sos dumpobj 0x1234" }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "done"
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var inspector = new FakeManagedObjectInspector(isOpen: true, address =>
            new ClrMdObjectInspection
            {
                Address = $"0x{address:x}",
                Type = "System.String",
                Size = 8,
                IsString = true,
                Value = "hello"
            });

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            debugger,
            clrMdAnalyzer: inspector);

        Assert.Empty(debugger.ExecutedCommands);
        Assert.NotNull(result.CommandsExecuted);
        Assert.Contains(result.CommandsExecuted!, c => c.Tool == "inspect" && c.Output.Contains("System.String", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenSamplingTraceFilesEnabled_WritesTraceFiles()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "done"
            }));

        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
            {
                MaxIterations = 1,
                EnableSamplingTraceFiles = true,
                SamplingTraceFilesRootDirectory = tempRoot,
                SamplingTraceLabel = "trace-test",
                SamplingTraceMaxFileBytes = 500_000
            };

            _ = await orchestrator.AnalyzeCrashAsync(
                new CrashAnalysisResult(),
                "{\"x\":1}",
                new FakeDebuggerManager(),
                clrMdAnalyzer: null);

            var runDirs = Directory.GetDirectories(tempRoot);
            Assert.Single(runDirs);

            var runDir = runDirs[0];
            Assert.True(File.Exists(Path.Combine(runDir, "iter-0001-request.json")));
            Assert.True(File.Exists(Path.Combine(runDir, "iter-0001-response.json")));
            Assert.True(File.Exists(Path.Combine(runDir, "final-ai-analysis.json")));
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
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
    public async Task AnalyzeCrashAsync_MaxIterationsReached_PerformsFinalSynthesisIteration()
    {
        var requests = new List<CreateMessageRequestParams>();
        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithText("still thinking"))
            .EnqueueResult(CreateMessageResultWithText("still thinking"))
            .EnqueueResult(CreateMessageResultWithText("still thinking"))
            .EnqueueResult(CreateMessageResultWithText("""
            {
              "rootCause": "final root cause",
              "confidence": "low",
              "reasoning": "final reasoning",
              "recommendations": ["r1"],
              "additionalFindings": ["f1"]
            }
            """));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal("final root cause", result.RootCause);
        Assert.Equal(4, result.Iterations);
        Assert.NotEmpty(requests);
        Assert.Null(requests.Last().ToolChoice);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MaxToolCallsReached_AddsSkippedToolResultsBeforeFinalSynthesis()
    {
        var requests = new List<CreateMessageRequestParams>();
        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUses(
                ("exec", new { command = "!a" }, "tc_a"),
                ("exec", new { command = "!b" }, "tc_b")))
            .EnqueueResult(CreateMessageResultWithText("""
            {
              "rootCause": "final root cause",
              "confidence": "low",
              "reasoning": "final reasoning",
              "recommendations": [],
              "additionalFindings": []
            }
            """));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 10,
            MaxToolCalls = 1,
            CheckpointEveryIterations = 0
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("final root cause", result.RootCause);
        Assert.True(requests.Count >= 2);
        Assert.Null(requests[^1].ToolChoice);

        var finalMessages = requests[^1].Messages?.ToList();
        Assert.NotNull(finalMessages);
        Assert.NotEmpty(finalMessages);

        var assistantIndex = finalMessages.FindIndex(m => m.Role == Role.Assistant && m.Content?.OfType<ToolUseContentBlock>().Any() == true);
        Assert.True(assistantIndex >= 0, "Expected an assistant message with tool_use blocks.");

        var toolUseIds = finalMessages[assistantIndex].Content!.OfType<ToolUseContentBlock>().Select(t => t.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        Assert.Contains("tc_a", toolUseIds);
        Assert.Contains("tc_b", toolUseIds);

        // Tool results must appear after the tool-use message and before the next assistant message (or end).
        var nextAssistantIndex = finalMessages.FindIndex(assistantIndex + 1, m => m.Role == Role.Assistant);
        var endIndexExclusive = nextAssistantIndex >= 0 ? nextAssistantIndex : finalMessages.Count;
        var toolResults = finalMessages
            .Skip(assistantIndex + 1)
            .Take(endIndexExclusive - assistantIndex - 1)
            .SelectMany(m => m.Content?.OfType<ToolResultContentBlock>() ?? [])
            .ToList();

        Assert.Contains(toolResults, tr => tr.ToolUseId == "tc_a");
        var skipped = Assert.Single(toolResults.Where(tr => tr.ToolUseId == "tc_b"));
        Assert.True(skipped.IsError);
        var skippedText = skipped.Content!.OfType<TextContentBlock>().Single().Text;
        Assert.Contains("skipped", skippedText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_CheckpointEveryIterations_PrunesConversationAndContinues()
    {
        var requests = new List<CreateMessageRequestParams>();
        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!a" }))
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!b" }))
            .EnqueueResult(CreateMessageResultWithToolUse("checkpoint_complete", new
            {
                facts = new[] { "f1" },
                hypotheses = new object[]
                {
                    new { hypothesis = "h1", confidence = "low", evidence = new[] { "e1" }, unknowns = new[] { "u1" } }
                },
                evidence = new object[]
                {
                    new { id = "E1", source = "exec(!a)", finding = "x" }
                },
                doNotRepeat = new[] { "exec(!a)" },
                nextSteps = new object[]
                {
                    new { tool = "report_get", call = "report_get(path=\"analysis.exception.type\")", why = "confirm" }
                }
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "done",
                confidence = "low",
                reasoning = "ok"
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5,
            CheckpointEveryIterations = 2,
            CheckpointMaxTokens = 512
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("done", result.RootCause);
        Assert.Equal(2, debugger.ExecutedCommands.Count);
        Assert.Equal(4, requests.Count);

        Assert.NotNull(requests[2].ToolChoice);
        Assert.Equal("required", requests[2].ToolChoice!.Mode);
        Assert.Contains(requests[2].Tools!, t => string.Equals(t.Name, "checkpoint_complete", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(512, requests[2].MaxTokens);
        Assert.Contains(
            requests[2].Messages!.SelectMany(m => m.Content?.OfType<ToolResultContentBlock>() ?? []),
            tr => !string.IsNullOrWhiteSpace(tr.ToolUseId));

        Assert.NotNull(requests[3].Messages);
        Assert.NotEmpty(requests[3].Messages);
        var checkpointCarry = requests[3].Messages[0].Content!.OfType<TextContentBlock>().Single().Text;
        Assert.Contains("Checkpoint JSON", checkpointCarry, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"facts\"", checkpointCarry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenCheckpointSynthesisThrows_PrunesWithDeterministicCheckpointAndContinues()
    {
        var requests = new List<CreateMessageRequestParams>();
        var sampling = new ThrowingCheckpointSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!a" }))
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!b" }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "done",
                confidence = "low",
                reasoning = "ok"
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5,
            CheckpointEveryIterations = 2,
            CheckpointMaxTokens = 128
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("done", result.RootCause);
        Assert.Equal(2, debugger.ExecutedCommands.Count);
        Assert.True(requests.Count >= 4);

        var last = requests.Last();
        var carryText = last.Messages[0].Content!.OfType<TextContentBlock>().Single().Text;
        Assert.Contains("Checkpoint synthesis unavailable", carryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_CheckpointAfterFirst_PrimesFromLastCheckpointInsteadOfFullHistory()
    {
        var requests = new List<CreateMessageRequestParams>();
        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!a" }))
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!b" }))
            .EnqueueResult(CreateMessageResultWithToolUse("checkpoint_complete", new
            {
                facts = new[] { "f1" },
                hypotheses = Array.Empty<object>(),
                evidence = Array.Empty<object>(),
                doNotRepeat = new[] { "exec(!a)" },
                nextSteps = Array.Empty<object>()
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!c" }))
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!d" }))
            .EnqueueResult(CreateMessageResultWithToolUse("checkpoint_complete", new
            {
                facts = new[] { "f2" },
                hypotheses = Array.Empty<object>(),
                evidence = Array.Empty<object>(),
                doNotRepeat = new[] { "exec(!a)", "exec(!c)" },
                nextSteps = Array.Empty<object>()
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "done",
                confidence = "low",
                reasoning = "ok"
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 7,
            CheckpointEveryIterations = 2,
            CheckpointMaxTokens = 512
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("done", result.RootCause);
        Assert.Equal(4, debugger.ExecutedCommands.Count);
        Assert.Equal(7, requests.Count);

        Assert.NotNull(requests[2].Messages);
        Assert.NotEmpty(requests[2].Messages);
        Assert.Contains(
            requests[2].Messages.SelectMany(m => m.Content?.OfType<ToolResultContentBlock>() ?? []),
            tr => !string.IsNullOrWhiteSpace(tr.ToolUseId));

        var secondCheckpointRequest = requests[5];
        Assert.NotNull(secondCheckpointRequest.Messages);
        Assert.Equal(2, secondCheckpointRequest.Messages.Count);
        Assert.DoesNotContain(
            secondCheckpointRequest.Messages.SelectMany(m => m.Content?.OfType<ToolResultContentBlock>() ?? []),
            _ => true);

        var firstMessageText = secondCheckpointRequest.Messages[0].Content!.OfType<TextContentBlock>().Single().Text;
        Assert.Contains("Checkpoint JSON", firstMessageText, StringComparison.OrdinalIgnoreCase);

        var secondMessageText = secondCheckpointRequest.Messages[1].Content!.OfType<TextContentBlock>().Single().Text;
        Assert.Contains("Evidence snapshot", secondMessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctor_MaxTokensPerRequest_DefaultIs8192()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true);
        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance);
        Assert.Equal(8192, orchestrator.MaxTokensPerRequest);
    }

    [Fact]
    public void Ctor_CheckpointMaxTokens_DefaultIs65000()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true);
        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance);
        Assert.Equal(65_000, orchestrator.CheckpointMaxTokens);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenMaxIterationsIsZero_RunsAtLeastOneIteration()
    {
        var requests = new List<CreateMessageRequestParams>();
        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithText("still thinking"))
            .EnqueueResult(CreateMessageResultWithText("""
            {
              "rootCause": "final root cause",
              "confidence": "low",
              "reasoning": "final reasoning",
              "recommendations": [],
              "additionalFindings": []
            }
            """));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 0
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal(2, result.Iterations);
        Assert.Equal("final root cause", result.RootCause);
        Assert.Null(requests.Last().ToolChoice);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenSamplingAlwaysFails_ReturnsFallbackSynthesisInsteadOfHardFailure()
    {
        var sampling = new AlwaysThrowSamplingClient();

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 1,
            MaxSamplingRequestAttempts = 2
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Contains("Sampling failed", result.RootCause, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Reasoning);
    }

    [Fact]
    public async Task RewriteSummaryAsync_CheckpointEveryIterations_PrunesConversationAndContinues()
    {
        var requests = new List<CreateMessageRequestParams>();
        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new { path = "analysis.exception.type" }))
            .EnqueueResult(CreateMessageResultWithToolUse("checkpoint_complete", new
            {
                facts = new[] { "exceptionType=System.Exception" },
                hypotheses = new object[]
                {
                    new
                    {
                        hypothesis = "h1",
                        confidence = "low",
                        evidence = new[] { "analysis.exception.type" },
                        unknowns = Array.Empty<string>()
                    }
                },
                evidence = new object[]
                {
                    new { id = "E1", source = "report_get(analysis.exception.type)", finding = "System.Exception" }
                },
                doNotRepeat = new[] { "report_get(path=\"analysis.exception\")" },
                nextSteps = new object[]
                {
                    new { tool = "analysis_summary_rewrite_complete", call = "(use completion tool)", why = "finish" }
                }
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_summary_rewrite_complete", new
            {
                description = "rewritten",
                recommendations = new[] { "r1" }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3,
            CheckpointEveryIterations = 1,
            CheckpointMaxTokens = 512
        };

        var result = await orchestrator.RewriteSummaryAsync(
            new CrashAnalysisResult(),
            "{\"metadata\":{},\"analysis\":{\"exception\":{\"type\":\"System.Exception\",\"message\":\"boom\"}}}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(result);
        Assert.Equal("rewritten", result!.Description);
        Assert.Equal(3, requests.Count);
        Assert.NotNull(requests[1].ToolChoice);
        Assert.Equal("required", requests[1].ToolChoice!.Mode);
        Assert.Contains(requests[1].Tools!, t => string.Equals(t.Name, "checkpoint_complete", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(requests[2].Messages);
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
        Assert.Equal(3, result.CommandsExecuted!.Count);
        Assert.Contains(result.CommandsExecuted, c => c.Output.Contains("skipped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenMaxIterationsReachedWithToolCalls_PerformsFinalSynthesisIteration()
    {
        var requests = new List<CreateMessageRequestParams>();
        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!threads" }))
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!clrstack" }))
            .EnqueueResult(CreateMessageResultWithText("""
            {
              "rootCause": "final root cause",
              "confidence": "low",
              "reasoning": "final reasoning",
              "recommendations": ["r1"],
              "additionalFindings": []
            }
            """));

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

        Assert.Equal("final root cause", result.RootCause);
        Assert.Equal(3, result.Iterations);
        Assert.Equal(2, debugger.ExecutedCommands.Count);
        Assert.Null(requests.Last().ToolChoice);
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

    [Fact]
    public async Task AnalyzeCrashAsync_AnalysisCompleteWithAdditionalFindings_ParsesStringArrayAndSkipsEmptyValues()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new
            {
                path = "analysis.exception"
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "medium",
                reasoning = "done",
                additionalFindings = new object?[]
                {
                    "first",
                    "   ",
                    null,
                    123,
                    new { x = 1 }
                }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 2
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{\"metadata\":{},\"analysis\":{\"exception\":{\"type\":\"System.Exception\",\"message\":\"boom\"}}}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal("Ok", result.RootCause);
        Assert.NotNull(result.AdditionalFindings);
        Assert.Contains("first", result.AdditionalFindings!);
        Assert.Contains("123", result.AdditionalFindings!);
        Assert.Contains("{\"x\":1}", result.AdditionalFindings!);
        Assert.DoesNotContain(result.AdditionalFindings!, s => string.IsNullOrWhiteSpace(s));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenToolIsRewritten_LogsOriginalToolSummary()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        var logger = new CollectingLogger<AiAnalysisOrchestrator>((level, message) => logs.Add((level, message)));

        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "sos dumpobj 0x1234" }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "done"
            }));

        var inspector = new FakeManagedObjectInspector(isOpen: true, address =>
            new ClrMdObjectInspection
            {
                Address = $"0x{address:x}",
                Type = "System.String",
                IsString = true,
                Value = "hello"
            });

        var orchestrator = new AiAnalysisOrchestrator(sampling, logger)
        {
            MaxIterations = 3
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: inspector);

        Assert.NotNull(result.CommandsExecuted);
        Assert.Contains(result.CommandsExecuted!, c => c.Tool == "inspect");
        Assert.Contains(logs, entry =>
            entry.Message.Contains("original:", StringComparison.OrdinalIgnoreCase) ||
            entry.Message.Contains("(original", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenDuplicateToolCallsOccur_ReusesCachedResultAndAvoidsReExecuting()
    {
        var toolId1 = Guid.NewGuid().ToString("N");
        var toolId2 = Guid.NewGuid().ToString("N");

        var twoToolUses = new CreateMessageResult
        {
            Model = "test-model",
            Role = Role.Assistant,
            Content =
            [
                new ToolUseContentBlock
                {
                    Id = toolId1,
                    Name = "exec",
                    Input = JsonSerializer.SerializeToElement(new { command = "!threads", args = new[] { 1, 2 } })
                },
                new ToolUseContentBlock
                {
                    Id = toolId2,
                    Name = "exec",
                    Input = JsonSerializer.SerializeToElement(new { command = "!threads", args = new[] { 1, 2 } })
                }
            ]
        };

        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(twoToolUses)
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "done"
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

        Assert.Single(debugger.ExecutedCommands);
        Assert.Equal("!threads", debugger.ExecutedCommands[0]);

        Assert.NotNull(result.CommandsExecuted);
        Assert.Equal(2, result.CommandsExecuted!.Count(c => c.Tool == "exec"));
        Assert.Contains(result.CommandsExecuted!, c => c.Output.Contains("[cached tool result]", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_InspectTool_WhenClrMdAnalyzerNotAvailable_ReturnsHintJson()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("inspect", new { address = "0x1234", maxDepth = 4 }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "done"
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(result.CommandsExecuted);
        Assert.Contains(result.CommandsExecuted!, c =>
            c.Tool == "inspect" &&
            c.Output.Contains("ClrMD analyzer not available", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_GetThreadStack_WhenThreadMissing_ReturnsNotFoundJson()
    {
        var report = new CrashAnalysisResult
        {
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo { ThreadId = "1", State = "stopped", IsFaulting = false, TopFunction = "Foo" }
                ]
            }
        };

        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("get_thread_stack", new { threadId = "999" }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "done"
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
        Assert.Contains(result.CommandsExecuted!, c =>
            c.Tool == "get_thread_stack" &&
            c.Output.Contains("Thread not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_GetThreadStack_MatchesOsThreadIdRegardlessOfHexFormatting()
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
                        OsThreadId = "0x00000010",
                        State = "stopped",
                        IsFaulting = false,
                        TopFunction = "Foo"
                    }
                ]
            }
        };

        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("get_thread_stack", new { threadId = "0x10" }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "done"
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
        Assert.Contains(result.CommandsExecuted!, c =>
            c.Tool == "get_thread_stack" &&
            c.Output.Contains("\"threadId\": \"1\"", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenTraceFileByteLimitExceeded_WritesTruncationMarker()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = new string('a', 10_000)
            }));

        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
            {
                MaxIterations = 1,
                EnableSamplingTraceFiles = true,
                SamplingTraceFilesRootDirectory = tempRoot,
                SamplingTraceLabel = "truncate-test",
                SamplingTraceMaxFileBytes = 200
            };

            _ = await orchestrator.AnalyzeCrashAsync(
                new CrashAnalysisResult(),
                "{\"x\":1}",
                new FakeDebuggerManager(),
                clrMdAnalyzer: null);

            var runDir = Assert.Single(Directory.GetDirectories(tempRoot));
            var requestPath = Path.Combine(runDir, "iter-0001-request.json");
            Assert.True(File.Exists(requestPath));

            var content = await File.ReadAllTextAsync(requestPath);
            Assert.Contains("[truncated, totalBytes=", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
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

    private static CreateMessageResult CreateMessageResultWithToolUses(params (string Name, object Input, string? Id)[] toolUses)
    {
        var blocks = new List<ContentBlock>(toolUses.Length);
        foreach (var (name, input, id) in toolUses)
        {
            var json = JsonSerializer.SerializeToElement(input);
            blocks.Add(new ToolUseContentBlock
            {
                Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
                Name = name,
                Input = json
            });
        }

        return new CreateMessageResult
        {
            Model = "test-model",
            Role = Role.Assistant,
            Content = blocks
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

    private sealed class SequencedCapturingSamplingClient(List<CreateMessageRequestParams> requests) : ISamplingClient
    {
        private readonly Queue<CreateMessageResult> _results = new();
        private readonly List<CreateMessageRequestParams> _requests = requests;

        public bool IsSamplingSupported => true;

        public bool IsToolUseSupported => true;

        public SequencedCapturingSamplingClient EnqueueResult(CreateMessageResult result)
        {
            _results.Enqueue(result);
            return this;
        }

        public Task<CreateMessageResult> RequestCompletionAsync(CreateMessageRequestParams request, CancellationToken cancellationToken = default)
        {
            _requests.Add(request);
            if (_results.Count == 0)
            {
                return Task.FromResult(CreateMessageResultWithText("no more scripted responses"));
            }

            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class ThrowingCheckpointSamplingClient(List<CreateMessageRequestParams> requests) : ISamplingClient
    {
        private readonly Queue<CreateMessageResult> _results = new();
        private readonly List<CreateMessageRequestParams> _requests = requests;

        public bool IsSamplingSupported => true;

        public bool IsToolUseSupported => true;

        public ThrowingCheckpointSamplingClient EnqueueResult(CreateMessageResult result)
        {
            _results.Enqueue(result);
            return this;
        }

        public Task<CreateMessageResult> RequestCompletionAsync(CreateMessageRequestParams request, CancellationToken cancellationToken = default)
        {
            _requests.Add(request);

            if (request.ToolChoice != null &&
                string.Equals(request.ToolChoice.Mode, "required", StringComparison.OrdinalIgnoreCase) &&
                request.Tools?.Any(t => string.Equals(t.Name, "checkpoint_complete", StringComparison.OrdinalIgnoreCase)) == true)
            {
                throw new InvalidOperationException("simulated checkpoint failure");
            }

            if (_results.Count == 0)
            {
                return Task.FromResult(CreateMessageResultWithText("no more scripted responses"));
            }

            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class AlwaysThrowSamplingClient : ISamplingClient
    {
        public bool IsSamplingSupported => true;

        public bool IsToolUseSupported => true;

        public Task<CreateMessageResult> RequestCompletionAsync(CreateMessageRequestParams request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated sampling failure");
    }

    private sealed class CapturingSamplingClient(Action<CreateMessageRequestParams> onRequest, CreateMessageResult result) : ISamplingClient
    {
        private readonly Action<CreateMessageRequestParams> _onRequest = onRequest;
        private readonly CreateMessageResult _result = result;

        public bool IsSamplingSupported => true;

        public bool IsToolUseSupported => true;

        public Task<CreateMessageResult> RequestCompletionAsync(CreateMessageRequestParams request, CancellationToken cancellationToken = default)
        {
            _onRequest(request);
            return Task.FromResult(_result);
        }
    }

    private sealed class CollectingLogger<T>(Action<LogLevel, string> sink) : ILogger<T>
    {
        private readonly Action<LogLevel, string> _sink = sink;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _sink(logLevel, formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeManagedObjectInspector(bool isOpen, Func<ulong, ClrMdObjectInspection?> inspect) : IManagedObjectInspector
    {
        private readonly Func<ulong, ClrMdObjectInspection?> _inspect = inspect;

        public bool IsOpen { get; } = isOpen;

        public ClrMdObjectInspection? InspectObject(
            ulong address,
            ulong? methodTable = null,
            int maxDepth = 5,
            int maxArrayElements = 10,
            int maxStringLength = 1024)
            => _inspect(address);
    }
}
