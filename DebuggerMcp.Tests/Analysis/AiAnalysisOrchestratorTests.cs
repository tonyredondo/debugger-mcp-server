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
    private const string MinimalBaselineReportJson = """
    {
      "metadata": { "debuggerType": "LLDB" },
      "analysis": {
        "summary": {
          "crashType": ".NET Managed Exception",
          "description": "x",
          "recommendations": [],
          "threadCount": 1,
          "moduleCount": 1,
          "assemblyCount": 1
        },
        "environment": {
          "platform": { "os": "Linux" },
          "runtime": { "type": "CoreCLR" },
          "process": { "arguments": ["dotnet"] },
          "nativeAot": { "isNativeAot": false }
        },
        "exception": {
          "type": "System.Exception",
          "message": "boom",
          "hResult": "0x1",
          "stackTrace": [],
          "analysis": {}
        }
      }
    }
    """;

    [Fact]
    public async Task AnalyzeCrashAsync_WhenBaselineEvidenceCompletes_ForcesMetaBookkeepingWithMetaToolsOnly()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "report_get", Input: new { path = "metadata", pageKind = "object", limit = 50 }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.summary",
                    pageKind = "object",
                    select = new[] { "crashType", "description", "recommendations", "threadCount", "moduleCount", "assemblyCount" }
                }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.environment",
                    pageKind = "object",
                    select = new[] { "platform", "runtime", "process", "nativeAot" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.message" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.hResult" }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.exception.stackTrace",
                    limit = 8,
                    select = new[] { "frameNumber", "instructionPointer", "module", "function", "sourceFile", "lineNumber", "isManaged" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.analysis", pageKind = "object", limit = 200 }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new { hypothesis = "Assembly version mismatch", confidence = "unknown" },
                        new { hypothesis = "ReadyToRun/JIT bug", confidence = "unknown" }
                    }
                }, Id: null),
                (Name: "analysis_evidence_add", Input: new
                {
                    items = new[]
                    {
                        new { source = "report_get(path=\"analysis.exception.type\")", finding = "Exception type is System.Exception" }
                    }
                }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "done",
                confidence = "low",
                reasoning = "ok",
                evidence = new[]
                {
                    "report_get(path=\"analysis.exception.type\") -> System.Exception"
                }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5,
            CheckpointEveryIterations = 0
        };

        var fullReportJson = """
        {
          "metadata": { "debuggerType": "LLDB" },
          "analysis": {
            "summary": {
              "crashType": ".NET Managed Exception",
              "description": "x",
              "recommendations": [],
              "threadCount": 1,
              "moduleCount": 1,
              "assemblyCount": 1
            },
            "environment": {
              "platform": { "os": "Linux" },
              "runtime": { "type": "CoreCLR" },
              "process": { "arguments": ["dotnet"] },
              "nativeAot": { "isNativeAot": false }
            },
            "exception": {
              "type": "System.Exception",
              "message": "boom",
              "hResult": "0x1",
              "stackTrace": [
                {
                  "frameNumber": 0,
                  "instructionPointer": "0x1",
                  "module": "a",
                  "function": "f",
                  "sourceFile": "x.cs",
                  "lineNumber": 1,
                  "isManaged": true
                }
              ],
              "analysis": { "message": "boom" }
            }
          }
        }
        """;

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            fullReportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal("Assembly version mismatch", result.RootCause);
        Assert.NotNull(result.Judge);
        Assert.Equal("H1", result.Judge!.SelectedHypothesisId);
        Assert.NotNull(result.EvidenceLedger);
        Assert.NotEmpty(result.EvidenceLedger!);
        Assert.NotNull(result.Hypotheses);
        Assert.True(result.Hypotheses!.Count >= 1);

        Assert.True(requests.Count >= 2);
        var metaRequest = requests[1];
        Assert.NotNull(metaRequest.ToolChoice);
        Assert.Equal("required", metaRequest.ToolChoice!.Mode);
        Assert.NotNull(metaRequest.Tools);
        var toolNames = metaRequest.Tools!.Select(t => t.Name).ToList();
        Assert.Equal(3, toolNames.Count);
        Assert.Contains(toolNames, name => string.Equals("analysis_evidence_add", name, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(toolNames, name => string.Equals("analysis_hypothesis_register", name, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(toolNames, name => string.Equals("analysis_hypothesis_score", name, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(toolNames, name => string.Equals("report_get", name, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(toolNames, name => string.Equals("exec", name, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(toolNames, name => string.Equals("analysis_complete", name, StringComparison.OrdinalIgnoreCase));

        var judgeRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();
        Assert.Single(judgeRequests);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenMetaToolsPopulateEvidenceAndHypotheses_AttachesStateAndAcceptsEvidenceIds()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new
            {
                path = "analysis.exception.type"
            }))
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_evidence_add", Input: new
                {
                    items = new[]
                    {
                        new
                        {
                            source = "report_get(path=\"analysis.exception.type\")",
                            finding = "System.MissingMethodException"
                        }
                    }
                }, Id: null),
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new
                        {
                            hypothesis = "Assembly mismatch caused MissingMethodException",
                            confidence = "unknown"
                        }
                    }
                }, Id: null),
                (Name: "analysis_complete", Input: new
                {
                    rootCause = "rc",
                    confidence = "low",
                    reasoning = "because",
                    evidence = new[] { "E1" }
                }, Id: null)));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{\"analysis\":{\"exception\":{\"type\":\"System.MissingMethodException\"}}}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

	        Assert.Equal("Assembly mismatch caused MissingMethodException", result.RootCause);
        Assert.NotNull(result.Evidence);
        Assert.Contains(result.Evidence!, e => e.StartsWith("E1", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(result.EvidenceLedger);
        Assert.Contains(result.EvidenceLedger!, e => string.Equals("E1", e.Id, StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(result.Hypotheses);
        Assert.Contains(result.Hypotheses!, h => string.Equals("H1", h.Id, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_AutoRecordsEvidenceFromToolResults_WithProvenanceFields()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new { path = "analysis.exception.type" }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "rc",
                confidence = "low",
                reasoning = "ok",
                evidence = new[] { "E1" }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3,
            CheckpointEveryIterations = 0
        };

        var reportJson = """
        {
          "metadata": { "debuggerType": "LLDB" },
          "analysis": { "exception": { "type": "ORIGINAL" } }
        }
        """;

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            reportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(result.EvidenceLedger);
        var e1 = Assert.Single(result.EvidenceLedger!, e => string.Equals("E1", e.Id, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("report_get", e1.ToolName);
        Assert.StartsWith("sha256:", e1.ToolKeyHash);
        Assert.StartsWith("sha256:", e1.ToolOutputHash);
        Assert.False(e1.ToolWasError ?? false);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenEvidenceAddProvidesFinding_DoesNotOverwriteServerFinding()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new { path = "analysis.exception.type" }))
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_evidence_add", Input: new
                {
                    items = new[]
                    {
                        new
                        {
                            id = "E1",
                            source = "report_get(path=\"analysis.exception.type\")",
                            finding = "FAKE_FINDING",
                            whyItMatters = "model note"
                        }
                    }
                }, Id: null),
                (Name: "analysis_complete", Input: new
                {
                    rootCause = "rc",
                    confidence = "low",
                    reasoning = "ok",
                    evidence = new[] { "E1" }
                }, Id: null)));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3,
            CheckpointEveryIterations = 0
        };

        var reportJson = """
        {
          "metadata": { "debuggerType": "LLDB" },
          "analysis": { "exception": { "type": "ORIGINAL" } }
        }
        """;

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            reportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        var e1 = Assert.Single(result.EvidenceLedger!, e => string.Equals("E1", e.Id, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ORIGINAL", e1.Finding, StringComparison.Ordinal);
        Assert.DoesNotContain("FAKE_FINDING", e1.Finding, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenDuplicateToolCallsOccur_ReusesEvidenceId()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "rc",
                confidence = "low",
                reasoning = "ok",
                evidence = new[] { "E1" }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3,
            CheckpointEveryIterations = 0
        };

        var reportJson = """
        {
          "metadata": { "debuggerType": "LLDB" },
          "analysis": { "exception": { "type": "ORIGINAL" } }
        }
        """;

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            reportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(result.EvidenceLedger);
        Assert.Single(result.EvidenceLedger!);
        Assert.Equal("E1", result.EvidenceLedger![0].Id);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenToolArgsContainConcatenatedJson_ParsesFirstObject()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse(
                "report_get",
                "{\"path\":\"analysis.exception.type\"}{\"path\":\"analysis.exception.message\"}"))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "rc",
                confidence = "low",
                reasoning = "ok",
                evidence = new[] { "E1" }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3,
            CheckpointEveryIterations = 0
        };

        var reportJson = """
        {
          "metadata": { "debuggerType": "LLDB" },
          "analysis": { "exception": { "type": "EXPECTED" } }
        }
        """;

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            reportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(result.EvidenceLedger);
        var e1 = Assert.Single(result.EvidenceLedger!, e => string.Equals("E1", e.Id, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("analysis.exception.type", e1.Source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXPECTED", e1.Finding, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenModelRepeatsInvalidHighConfidenceAnalysisComplete_AutoFinalizesWithDowngradedConfidence()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new
            {
                path = "analysis.exception.type"
            }))
            // First attempt: high confidence but insufficient evidence -> validation refusal.
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "rc1",
                confidence = "high",
                reasoning = "because",
                evidence = new[]
                {
                    "report_get(path=\"analysis.exception.type\") -> System.MissingMethodException"
                }
            }))
            // Second identical attempt without new evidence: orchestrator should auto-finalize.
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "rc2",
                confidence = "high",
                reasoning = "because again",
                evidence = new[]
                {
                    "report_get(path=\"analysis.exception.type\") -> System.MissingMethodException"
                }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{\"analysis\":{\"exception\":{\"type\":\"System.MissingMethodException\"}}}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal("rc2", result.RootCause);
        Assert.Equal("medium", result.Confidence);
        Assert.NotNull(result.Evidence);
        Assert.Contains(result.Evidence!, e => e.Contains("analysis.exception.type", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("auto-finalized", result.Reasoning ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenAnalysisCompleteOmitsEvidence_AutoGeneratesEvidenceAndDownratesHighConfidence()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new
            {
                path = "analysis.exception.type"
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "rc",
                confidence = "high",
                reasoning = "because"
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{\"analysis\":{\"exception\":{\"type\":\"System.MissingMethodException\"}}}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal("rc", result.RootCause);
        Assert.Equal("medium", result.Confidence);
        Assert.NotNull(result.Evidence);
        Assert.Contains(result.Evidence!, e => e.Contains("analysis.exception.type", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("auto-generated", result.Reasoning ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenAnalysisCompleteIncludesEvidence_PopulatesEvidenceList()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new
            {
                path = "analysis.exception.type"
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "rc",
                confidence = "low",
                reasoning = "because",
                evidence = new[]
                {
                    "report_get(path=\"analysis.exception.type\") -> System.MissingMethodException"
                }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{\"analysis\":{\"exception\":{\"type\":\"System.MissingMethodException\"}}}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal("rc", result.RootCause);
        Assert.NotNull(result.Evidence);
        Assert.Contains(result.Evidence!, e => e.Contains("analysis.exception.type", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenModelCallsAnalysisCompleteImmediately_RequiresEvidenceToolBeforeCompleting()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "premature",
                confidence = "high",
                reasoning = "done",
                evidence = new[]
                {
                    "analysis_complete called without any executed evidence tool calls (this will be refused)"
                }
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new
            {
                path = "analysis.exception"
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "root cause after evidence",
                confidence = "low",
                reasoning = "done",
                evidence = new[]
                {
                    "report_get(path=\"analysis.exception\") -> contains exception details"
                }
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
    public async Task RewriteSummaryAsync_WhenModelCallsCompletionImmediately_CompletesWithoutRequiringEvidenceTools()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_summary_rewrite_complete", new
            {
                description = "premature",
                recommendations = new[] { "r1" }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 1
        };

        var result = await orchestrator.RewriteSummaryAsync(
            new CrashAnalysisResult(),
            "{\"metadata\":{},\"analysis\":{\"exception\":{\"type\":\"System.Exception\",\"message\":\"boom\"}}}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(result);
        Assert.Equal("premature", result!.Description);
        Assert.NotNull(result.Recommendations);
        Assert.Single(result.Recommendations!);
        Assert.Null(result.CommandsExecuted);
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
    public async Task RewriteSummaryAsync_WhenProviderRejectsStructuredToolHistory_RetriesWithCheckpointOnlyHistory()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new StructuredToolHistoryRejectingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new { path = "analysis.exception.type" }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_summary_rewrite_complete", new
            {
                description = "rewritten",
                recommendations = new[] { "r1" }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5,
            CheckpointEveryIterations = 0,
            MaxSamplingRequestAttempts = 2
        };

        var result = await orchestrator.RewriteSummaryAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(result);
        Assert.Equal("rewritten", result!.Description);

        Assert.True(requests.Count >= 3, $"Expected at least 3 requests (initial + failed retry + checkpoint-only retry), got {requests.Count}.");
        Assert.True(ContainsStructuredToolHistory(requests[1]), "Expected the second request to contain structured tool history and trigger the provider rejection.");
        Assert.False(ContainsStructuredToolHistory(requests.Last()), "Expected the retry request to use checkpoint-only history without tool blocks.");
    }

    [Fact]
    public async Task RewriteSummaryAsync_WhenToolCallIsRepeated_ReplaysCachedPayloadInCommandLog()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new { path = "analysis.exception.type" }))
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new { path = "analysis.exception.type" }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_summary_rewrite_complete", new
            {
                description = "rewritten",
                recommendations = new[] { "r1" }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5,
            CheckpointEveryIterations = 0
        };

        var result = await orchestrator.RewriteSummaryAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(result);
        Assert.Equal("rewritten", result!.Description);
        Assert.NotNull(result.CommandsExecuted);
        Assert.Equal(2, result.CommandsExecuted!.Count);
        Assert.All(result.CommandsExecuted!, command => Assert.Equal("report_get", command.Tool));
        Assert.Equal(result.CommandsExecuted![0].Output, result.CommandsExecuted[1].Output);
        Assert.False(result.CommandsExecuted[1].Output.Contains("Duplicate report_get call detected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RewriteSummaryAsync_WhenNoProgressPersists_FinalizesEarly()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new { path = "analysis.exception.type" }))
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new { path = "analysis.exception.type" }))
            .EnqueueResult(CreateMessageResultWithText("""
            {
              "description": "rewritten",
              "recommendations": []
            }
            """));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 25,
            CheckpointEveryIterations = 0,
            MaxConsecutiveNoProgressIterations = 1
        };

        var result = await orchestrator.RewriteSummaryAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(result);
        Assert.Equal("rewritten", result!.Description);

        var finalRequest = requests.Last();
        Assert.Null(finalRequest.Tools);
        Assert.Null(finalRequest.ToolChoice);
    }

    [Fact]
    public async Task RewriteSummaryAsync_WhenToolUseBudgetExceeded_FinalizesEarly()
    {
        var requests = new List<CreateMessageRequestParams>();

        var paths = new[]
        {
            "metadata",
            "analysis.summary",
            "analysis.environment",
            "analysis.exception.type",
            "analysis.exception.message",
            "analysis.exception.hResult",
            "analysis.exception.stackTrace",
            "analysis.exception.analysis",
            "analysis.summary.crashType",
            "analysis.environment.runtime",
            "analysis.environment.process"
        };

        var toolUses = paths
            .Select(path => (Name: "report_get", Input: (object)new { path }, Id: (string?)null))
            .ToArray();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUses(toolUses))
            .EnqueueResult(CreateMessageResultWithText("""
            {
              "description": "final",
              "recommendations": []
            }
            """));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 25,
            CheckpointEveryIterations = 0,
            SummaryRewriteMaxTotalToolUses = 10
        };

        var result = await orchestrator.RewriteSummaryAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(result);
        Assert.Equal("final", result!.Description);
        Assert.NotNull(result.CommandsExecuted);
        Assert.Equal(10, result.CommandsExecuted!.Count);

        Assert.Equal(2, requests.Count);
        Assert.Null(requests.Last().Tools);
        Assert.Null(requests.Last().ToolChoice);
    }

    [Fact]
    public async Task GenerateThreadNarrativeAsync_WhenModelCallsCompletionImmediately_CompletesWithoutRequiringEvidenceTools()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_thread_narrative_complete", new
            {
                description = "premature",
                confidence = "high"
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 1
        };

        var result = await orchestrator.GenerateThreadNarrativeAsync(
            new CrashAnalysisResult(),
            "{\"metadata\":{},\"analysis\":{\"threads\":{\"summary\":{\"total\":1}}}}",
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(result);
        Assert.Equal("premature", result!.Description);
        Assert.Equal("high", result.Confidence);
        Assert.Null(result.CommandsExecuted);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenMetaBookkeepingRejectsStructuredToolHistory_RetriesWithCheckpointOnlyHistory()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new StructuredToolHistoryRejectingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "report_get", Input: new { path = "metadata", pageKind = "object", limit = 50 }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.summary",
                    pageKind = "object",
                    select = new[] { "crashType", "description", "recommendations", "threadCount", "moduleCount", "assemblyCount" }
                }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.environment",
                    pageKind = "object",
                    select = new[] { "platform", "runtime", "process", "nativeAot" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.message" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.hResult" }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.exception.stackTrace",
                    limit = 8,
                    select = new[] { "frameNumber", "instructionPointer", "module", "function", "sourceFile", "lineNumber", "isManaged" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.analysis", pageKind = "object", limit = 200 }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_hypothesis_register", new
            {
                hypotheses = new[]
                {
                    new { hypothesis = "Assembly version mismatch", confidence = "unknown" },
                    new { hypothesis = "Profiler/IL rewriting", confidence = "unknown" }
                }
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "done",
                confidence = "low",
                reasoning = "ok",
                evidence = new[]
                {
                    "report_get(path=\"analysis.exception.type\")"
                }
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_judge_complete", new
            {
                selectedHypothesisId = "H1",
                confidence = "low",
                rationale = "ok",
                supportsEvidenceIds = new[] { "E1" },
                rejectedHypotheses = new[]
                {
                    new { hypothesisId = "H2", contradictsEvidenceIds = Array.Empty<string>(), reason = "no contradicting evidence" }
                }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 10,
            CheckpointEveryIterations = 0,
            MaxSamplingRequestAttempts = 2
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal("Assembly version mismatch", result.RootCause);

        var metaRequests = requests
            .Where(IsMetaBookkeepingRequest)
            .ToList();

        Assert.True(metaRequests.Count >= 2, $"Expected at least 2 meta bookkeeping requests (failed + retry), got {metaRequests.Count}.");
        Assert.True(ContainsStructuredToolHistory(metaRequests[0]), "Expected first meta bookkeeping request to include structured tool history.");
        Assert.False(ContainsStructuredToolHistory(metaRequests[1]), "Expected retry meta bookkeeping request to use checkpoint-only history without tool blocks.");
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
                reasoning = "done",
                evidence = new[]
                {
                    "analysis_complete called without evidence tools (this will be refused with MaxIterations=1)"
                }
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

    private static bool IsMetaBookkeepingRequest(CreateMessageRequestParams request)
    {
        if (request.Tools == null || request.Tools.Count == 0)
        {
            return false;
        }

        var names = request.Tools
            .Select(t => (t.Name ?? string.Empty).Trim())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        if (names.Count == 0)
        {
            return false;
        }

        var isMeta = names.All(n =>
            string.Equals(n, "analysis_evidence_add", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "analysis_hypothesis_register", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "analysis_hypothesis_score", StringComparison.OrdinalIgnoreCase));

        return isMeta;
    }

    private static bool ContainsStructuredToolHistory(CreateMessageRequestParams request)
    {
        if (request.Messages == null || request.Messages.Count == 0)
        {
            return false;
        }

        foreach (var message in request.Messages)
        {
            if (message.Content == null)
            {
                continue;
            }

            foreach (var block in message.Content)
            {
                if (block is ToolUseContentBlock || block is ToolResultContentBlock)
                {
                    return true;
                }
            }
        }

        return false;
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
                reasoning = "done",
                evidence = new[]
                {
                    "exec(command=\"!threads\") -> OUTPUT:!threads (cached reuse expected)"
                }
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
                reasoning = "done",
                evidence = new[]
                {
                    "exec(command=\"!threads\") -> OUTPUT:!threads",
                    "exec(command=\"!threads\") -> cached tool result reuse"
                }
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
                reasoning = "ok",
                evidence = new[]
                {
                    "exec(command=\"!a\") -> OUTPUT:!a"
                }
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
                reasoning = "done",
                evidence = new[]
                {
                    "analysis_complete called without evidence tools (this will be refused with MaxIterations=1)"
                }
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
        Assert.Contains("Phase 1", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Baseline evidence", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Confidence rubric", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("falsification", seenRequest.SystemPrompt!, StringComparison.OrdinalIgnoreCase);
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
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new { id = "H1", hypothesis = "NullReferenceException in Foo.Bar", confidence = "unknown" },
                        new { id = "H2", hypothesis = "Assembly version mismatch", confidence = "unknown" },
                        new { id = "H3", hypothesis = "Trimming removed needed member", confidence = "unknown" }
                    }
                }, Id: null),
                (Name: "exec", Input: new { command = "!clrstack -a" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.message" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.hResult" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.environment.runtime", pageKind = "object", limit = 30 }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.summary.crashType" }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "NullReferenceException in Foo.Bar",
                confidence = "high",
                reasoning = "The report shows a null dereference.",
                evidence = new[]
                {
                    "E1: exec(command=\"!clrstack -a\") includes Foo.Bar",
                    "E2: report_get(path=\"analysis.exception.type\") returned System.NullReferenceException",
                    "E3: report_get(path=\"analysis.exception.message\") returned boom",
                    "E4: report_get(path=\"analysis.exception.hResult\") returned 0x1",
                    "E5: report_get(path=\"analysis.environment.runtime\") returned CoreCLR",
                    "E6: report_get(path=\"analysis.summary.crashType\") returned .NET Managed Exception"
                }
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_judge_complete", new
            {
                selectedHypothesisId = "H1",
                confidence = "high",
                rationale = "H1 best fits E1-E3; H2/H3 contradicted by E4/E5.",
                supportsEvidenceIds = new[] { "E1", "E2", "E3" },
                rejectedHypotheses = new[]
                {
                    new { hypothesisId = "H2", contradictsEvidenceIds = new[] { "E4" }, reason = "Assemblies appear consistent in E4." },
                    new { hypothesisId = "H3", contradictsEvidenceIds = new[] { "E5" }, reason = "Runtime is CoreCLR with JIT (E5), not a trimming-only scenario." }
                }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5
        };

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => cmd.Contains("!clrstack", StringComparison.OrdinalIgnoreCase)
                ? $"OUTPUT:{cmd}\nFoo.Bar()\n"
                : $"OUTPUT:{cmd}"
        };

        var reportJson = MinimalBaselineReportJson.Replace("\"System.Exception\"", "\"System.NullReferenceException\"");

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            reportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("NullReferenceException in Foo.Bar", result.RootCause);
        Assert.Equal("high", result.Confidence);
        Assert.NotNull(result.Judge);
        Assert.Equal("H1", result.Judge!.SelectedHypothesisId);

        var judgeRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();
        Assert.Single(judgeRequests);

        var judgeRequest = judgeRequests[0];
        Assert.NotNull(judgeRequest.ToolChoice);
        Assert.Equal("required", judgeRequest.ToolChoice!.Mode);
        Assert.NotNull(judgeRequest.Tools);
        Assert.Single(judgeRequest.Tools!);
        Assert.Equal("analysis_judge_complete", judgeRequest.Tools![0].Name);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MediumConfidenceCompletion_WithEvidenceAndHypotheses_RunsJudgeStepAndAlignsRootCause()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!clrstack -a" }))
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new { id = "H1", hypothesis = "Hypothesis 1", confidence = "low" },
                        new { id = "H2", hypothesis = "Hypothesis 2 (winner)", confidence = "medium" },
                        new { id = "H3", hypothesis = "Hypothesis 3", confidence = "low" }
                    }
                }, Id: null),
                (Name: "analysis_evidence_add", Input: new
                {
                    items = new[]
                    {
                        new { id = "E1", source = "exec(command=\"!clrstack -a\")", finding = "Finding 1" }
                    }
                }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "generic root cause",
                confidence = "medium",
                reasoning = "because",
                evidence = new[]
                {
                    "E1: finding 1"
                }
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_judge_complete", new
            {
                selectedHypothesisId = "H2",
                confidence = "medium",
                rationale = "H2 best fits E1.",
                supportsEvidenceIds = new[] { "E1" },
                rejectedHypotheses = new[]
                {
                    new { hypothesisId = "H1", contradictsEvidenceIds = new[] { "E1" }, reason = "E1 contradicts H1." },
                    new { hypothesisId = "H3", contradictsEvidenceIds = new[] { "E1" }, reason = "E1 contradicts H3." }
                }
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5,
            CheckpointEveryIterations = 0
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("medium", result.Confidence);
        Assert.NotNull(result.Judge);
        Assert.Equal("H2", result.Judge!.SelectedHypothesisId);
        Assert.Equal("Hypothesis 2 (winner)", result.RootCause);

        var judgeRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();
        Assert.Single(judgeRequests);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MediumConfidenceCompletion_WhenJudgeToolInputIsJsonString_ParsesAndAlignsRootCause()
    {
        var requests = new List<CreateMessageRequestParams>();

        var judgeJson = """
        {
          "selectedHypothesisId": "H2",
          "confidence": "medium",
          "rationale": "H2 best fits E1.",
          "supportsEvidenceIds": ["E1"],
          "rejectedHypotheses": [
            { "hypothesisId": "H1", "contradictsEvidenceIds": ["E1"], "reason": "E1 contradicts H1." },
            { "hypothesisId": "H3", "contradictsEvidenceIds": ["E1"], "reason": "E1 contradicts H3." }
          ]
        }
        """;

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!clrstack -a" }))
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new { id = "H1", hypothesis = "Hypothesis 1 (fallback winner)", confidence = "high" },
                        new { id = "H2", hypothesis = "Hypothesis 2 (judge winner)", confidence = "low" },
                        new { id = "H3", hypothesis = "Hypothesis 3", confidence = "low" }
                    }
                }, Id: null),
                (Name: "analysis_evidence_add", Input: new
                {
                    items = new[]
                    {
                        new { id = "E1", source = "exec(command=\"!clrstack -a\")", finding = "Finding 1" }
                    }
                }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "generic root cause",
                confidence = "medium",
                reasoning = "because",
                evidence = new[]
                {
                    "E1: finding 1"
                }
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_judge_complete", judgeJson));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5,
            CheckpointEveryIterations = 0
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("medium", result.Confidence);
        Assert.NotNull(result.Judge);
        Assert.Equal("H2", result.Judge!.SelectedHypothesisId);
        Assert.Equal("H2 best fits E1.", result.Judge.Rationale);
        Assert.Equal("Hypothesis 2 (judge winner)", result.RootCause);

        var judgeRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();
        Assert.Single(judgeRequests);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MediumConfidenceCompletion_WhenJudgeToolInputIsRawJsonProperty_ParsesAndAlignsRootCause()
    {
        var requests = new List<CreateMessageRequestParams>();

        var judgeJson = """
        {
          "selectedHypothesisId": "H2",
          "confidence": "medium",
          "rationale": "H2 best fits E1.",
          "supportsEvidenceIds": ["E1"],
          "rejectedHypotheses": [
            { "hypothesisId": "H1", "contradictsEvidenceIds": ["E1"], "reason": "E1 contradicts H1." },
            { "hypothesisId": "H3", "contradictsEvidenceIds": ["E1"], "reason": "E1 contradicts H3." }
          ]
        }
        """;

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!clrstack -a" }))
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new { id = "H1", hypothesis = "Hypothesis 1 (fallback winner)", confidence = "high" },
                        new { id = "H2", hypothesis = "Hypothesis 2 (judge winner)", confidence = "low" },
                        new { id = "H3", hypothesis = "Hypothesis 3", confidence = "low" }
                    }
                }, Id: null),
                (Name: "analysis_evidence_add", Input: new
                {
                    items = new[]
                    {
                        new { id = "E1", source = "exec(command=\"!clrstack -a\")", finding = "Finding 1" }
                    }
                }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "generic root cause",
                confidence = "medium",
                reasoning = "because",
                evidence = new[]
                {
                    "E1: finding 1"
                }
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_judge_complete", new { __raw = judgeJson }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5,
            CheckpointEveryIterations = 0
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("medium", result.Confidence);
        Assert.NotNull(result.Judge);
        Assert.Equal("H2", result.Judge!.SelectedHypothesisId);
        Assert.Equal("H2 best fits E1.", result.Judge.Rationale);
        Assert.Equal("Hypothesis 2 (judge winner)", result.RootCause);

        var judgeRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();
        Assert.Single(judgeRequests);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MediumConfidenceCompletion_WhenJudgeFails_FallsBackDeterministicallyAndAlignsRootCause()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!clrstack -a" }))
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new { id = "H1", hypothesis = "Hypothesis 1", confidence = "low" },
                        new { id = "H2", hypothesis = "Hypothesis 2 (winner)", confidence = "high" },
                        new { id = "H3", hypothesis = "Hypothesis 3", confidence = "medium" }
                    }
                }, Id: null),
                (Name: "analysis_evidence_add", Input: new
                {
                    items = new[]
                    {
                        new { id = "E1", source = "exec(command=\"!clrstack -a\")", finding = "Finding 1" }
                    }
                }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "generic root cause",
                confidence = "medium",
                reasoning = "because",
                evidence = new[]
                {
                    "E1: finding 1"
                }
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5,
            CheckpointEveryIterations = 0
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("medium", result.Confidence);
        Assert.NotNull(result.Judge);
        Assert.Equal("H2", result.Judge!.SelectedHypothesisId);
        Assert.Contains("Deterministic fallback", result.Judge.Rationale, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Hypothesis 2 (winner)", result.RootCause);

        var judgeRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();
        Assert.Single(judgeRequests);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MediumConfidenceCompletion_WhenJudgeSelectsUnknownHypothesisId_FallsBackAndAlignsRootCause()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!clrstack -a" }))
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new { id = "H1", hypothesis = "Hypothesis 1", confidence = "low" },
                        new { id = "H2", hypothesis = "Hypothesis 2 (winner)", confidence = "high" },
                        new { id = "H3", hypothesis = "Hypothesis 3", confidence = "low" }
                    }
                }, Id: null),
                (Name: "analysis_evidence_add", Input: new
                {
                    items = new[]
                    {
                        new { id = "E1", source = "exec(command=\"!clrstack -a\")", finding = "Finding 1" }
                    }
                }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "generic root cause",
                confidence = "medium",
                reasoning = "because",
                evidence = new[]
                {
                    "E1: finding 1"
                }
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_judge_complete", new
            {
                selectedHypothesisId = "H99",
                confidence = "medium",
                rationale = "H99 best fits E1.",
                supportsEvidenceIds = new[] { "E1" },
                rejectedHypotheses = new[]
                {
                    new { hypothesisId = "H1", contradictsEvidenceIds = new[] { "E1" }, reason = "E1 contradicts H1." },
                    new { hypothesisId = "H3", contradictsEvidenceIds = new[] { "E1" }, reason = "E1 contradicts H3." }
                }
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5,
            CheckpointEveryIterations = 0
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("medium", result.Confidence);
        Assert.NotNull(result.Judge);
        Assert.Equal("H2", result.Judge!.SelectedHypothesisId);
        Assert.Contains("unknown hypothesisId", result.Judge.Rationale, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("H99", result.Judge.Rationale, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Hypothesis 2 (winner)", result.RootCause);

        var judgeRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();
        Assert.Single(judgeRequests);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MediumConfidenceCompletion_WhenNarrativeContradictsJudgeWinner_RewritesReasoningAndFiltersRecommendations()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!clrstack -a" }))
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new
                        {
                            id = "H1",
                            hypothesis = "Assembly version mismatch between compile-time and runtime.",
                            confidence = "medium",
                            testsToRun = new[]
                            {
                                "Verify assembly versions for the caller and System.Collections.Concurrent."
                            }
                        },
                        new
                        {
                            id = "H2",
                            hypothesis = "IL rewriting by a profiler/instrumentation.",
                            confidence = "medium",
                            testsToRun = Array.Empty<string>()
                        },
                        new
                        {
                            id = "H3",
                            hypothesis = "Trimming removed the missing method.",
                            confidence = "low",
                            testsToRun = Array.Empty<string>()
                        }
                    }
                }, Id: null),
                (Name: "analysis_evidence_add", Input: new
                {
                    items = new[]
                    {
                        new { id = "E1", source = "report_get(path=\"analysis.exception.message\")", finding = "Method not found: ...TryGetValue..." }
                    }
                }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "generic root cause",
                confidence = "medium",
                reasoning = "This is characteristic of instrumentation rather than a simple version mismatch.",
                evidence = new[]
                {
                    "E1: MissingMethodException indicates TryGetValue is missing."
                },
                recommendations = new[]
                {
                    "Update or disable the instrumentation tool to prevent corruption."
                },
                additionalFindings = new[]
                {
                    "Profiler IL rewriting appears likely."
                }
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_judge_complete", new
            {
                selectedHypothesisId = "H1",
                confidence = "medium",
                rationale = "The safest conclusion is version mismatch until disproved.",
                supportsEvidenceIds = new[] { "E1" },
                rejectedHypotheses = new[]
                {
                    new { hypothesisId = "H2", contradictsEvidenceIds = new[] { "E1" }, reason = "No direct evidence of rewriting in the dump." },
                    new { hypothesisId = "H3", contradictsEvidenceIds = new[] { "E1" }, reason = "No trimming configuration evidence." }
                }
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5,
            CheckpointEveryIterations = 0
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.NotNull(result.Judge);
        Assert.Equal("H1", result.Judge!.SelectedHypothesisId);
        Assert.Equal("Assembly version mismatch between compile-time and runtime.", result.RootCause);
        Assert.NotNull(result.Reasoning);
        Assert.Contains("Selected hypothesis:", result.Reasoning!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("instrumentation rather than", result.Reasoning!, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(result.Recommendations);
        Assert.DoesNotContain(result.Recommendations!, r => r.Contains("disable", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Recommendations!, r => r.Contains("Verify assembly versions", StringComparison.OrdinalIgnoreCase));

        Assert.Null(result.AdditionalFindings);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MediumConfidenceCompletion_WhenWinnerHasNoTestsToRun_UsesDefaultAssemblyMismatchRecommendations()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!clrstack -a" }))
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new
                        {
                            id = "H1",
                            hypothesis = "Assembly version mismatch between compile-time and runtime.",
                            confidence = "medium"
                        },
                        new
                        {
                            id = "H2",
                            hypothesis = "IL rewriting by a profiler/instrumentation.",
                            confidence = "medium"
                        },
                        new
                        {
                            id = "H3",
                            hypothesis = "Trimming removed the missing method.",
                            confidence = "low"
                        }
                    }
                }, Id: null),
                (Name: "analysis_evidence_add", Input: new
                {
                    items = new[]
                    {
                        new { id = "E1", source = "exec(command=\"!clrstack -a\")", finding = "Finding 1" }
                    }
                }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "generic root cause",
                confidence = "medium",
                reasoning = "This looks like instrumentation rather than a version mismatch.",
                evidence = new[]
                {
                    "E1: finding 1"
                },
                recommendations = new[]
                {
                    "Update or disable the instrumentation tool to prevent type metadata corruption"
                }
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_judge_complete", new
            {
                selectedHypothesisId = "H1",
                confidence = "low",
                rationale = "H1 best fits E1.",
                supportsEvidenceIds = new[] { "E1" },
                rejectedHypotheses = new[]
                {
                    new { hypothesisId = "H2", contradictsEvidenceIds = new[] { "E1" }, reason = "E1 contradicts H2." },
                    new { hypothesisId = "H3", contradictsEvidenceIds = new[] { "E1" }, reason = "E1 contradicts H3." }
                }
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5,
            CheckpointEveryIterations = 0
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("Assembly version mismatch between compile-time and runtime.", result.RootCause);

        Assert.NotNull(result.Recommendations);
        Assert.Contains(result.Recommendations!, r => r.Contains("analysis.assemblies.items", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Recommendations!, r => r.Contains("report_get(path=\"analysis.assemblies\",", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Recommendations!, r => r.Contains("disable", StringComparison.OrdinalIgnoreCase));

        Assert.NotEmpty(requests);
        Assert.NotNull(requests[0].SystemPrompt);
        Assert.Contains("analysis.assemblies.items", requests[0].SystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("analysis.assemblies/items", requests[0].SystemPrompt!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MediumConfidenceCompletion_WhenJudgeReturnsJsonText_ParsesAndAlignsRootCause()
    {
        var requests = new List<CreateMessageRequestParams>();

        var judgeJson = """
        {
          "selectedHypothesisId": "H2",
          "confidence": "medium",
          "rationale": "H2 best fits E1.",
          "supportsEvidenceIds": ["E1"],
          "rejectedHypotheses": [
            { "hypothesisId": "H1", "contradictsEvidenceIds": ["E1"], "reason": "E1 contradicts H1." },
            { "hypothesisId": "H3", "contradictsEvidenceIds": ["E1"], "reason": "E1 contradicts H3." }
          ]
        }
        """;

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!clrstack -a" }))
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new { id = "H1", hypothesis = "Hypothesis 1", confidence = "low" },
                        new { id = "H2", hypothesis = "Hypothesis 2 (winner)", confidence = "medium" },
                        new { id = "H3", hypothesis = "Hypothesis 3", confidence = "low" }
                    }
                }, Id: null),
                (Name: "analysis_evidence_add", Input: new
                {
                    items = new[]
                    {
                        new { id = "E1", source = "exec(command=\"!clrstack -a\")", finding = "Finding 1" }
                    }
                }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "generic root cause",
                confidence = "medium",
                reasoning = "because",
                evidence = new[]
                {
                    "E1: finding 1"
                }
            }))
            .EnqueueResult(CreateMessageResultWithText(judgeJson));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5,
            CheckpointEveryIterations = 0
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.NotNull(result.Judge);
        Assert.Equal("H2", result.Judge!.SelectedHypothesisId);
        Assert.Equal("Hypothesis 2 (winner)", result.RootCause);

        var judgeRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();
        Assert.Single(judgeRequests);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MediumConfidenceCompletion_WhenPrerequisitesMissing_SkipsJudge()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!threads" }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Deadlock detected",
                confidence = "medium",
                reasoning = "Threads are blocked.",
                evidence = new[]
                {
                    "exec(command=\"!threads\") -> thread listing indicates blocked threads"
                }
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5,
            CheckpointEveryIterations = 0
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("Deadlock detected", result.RootCause);
        Assert.Null(result.Judge);

        Assert.DoesNotContain(
            requests,
            r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true);
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
                reasoning = "Threads are blocked.",
                evidence = new[]
                {
                    "exec(command=\"!threads\") -> thread listing indicates blocked threads"
                }
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
                reasoning = "done",
                evidence = new[]
                {
                    "exec(command=\"!THREADS\") -> executed and produced output (normalized command)",
                    "exec(command=\"!threads\") -> cached tool result reuse"
                }
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
        Assert.Equal(execCalls[0].Output, execCalls[1].Output);
        Assert.True(execCalls[1].Cached == true);
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
                reasoning = "done",
                evidence = new[]
                {
                    "inspect(address=\"0x1234\") -> System.String \"hello\" (rewrite from exec dumpobj)"
                }
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
                reasoning = "done",
                evidence = new[]
                {
                    "exec(command=\"!threads\") -> OUTPUT:!threads",
                    "exec(command=\"!threads\") -> cached tool result reuse (duplicate tool call)"
                }
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
                reasoning = "Blocked unsafe command.",
                evidence = new[]
                {
                    "exec(command=\".shell whoami\") -> blocked as unsafe; debugger command not executed"
                }
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
                reasoning = "Blocked unsafe command.",
                evidence = new[]
                {
                    $"exec(command=\"{blocked}\") -> blocked as unsafe; debugger command not executed"
                }
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
        Assert.Equal(orchestrator.FinalSynthesisMaxTokens, requests.Last().MaxTokens);
        Assert.Null(requests.Last().ToolChoice);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MaxToolCallsReached_PrunesUnexecutedToolUsesBeforeFinalSynthesis()
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
        Assert.Equal(orchestrator.FinalSynthesisMaxTokens, requests[^1].MaxTokens);
        Assert.Null(requests[^1].ToolChoice);

        var finalMessages = requests[^1].Messages?.ToList();
        Assert.NotNull(finalMessages);
        Assert.NotEmpty(finalMessages);

        var assistantIndex = finalMessages.FindIndex(m => m.Role == Role.Assistant && m.Content?.OfType<ToolUseContentBlock>().Any() == true);
        Assert.True(assistantIndex >= 0, "Expected an assistant message with tool_use blocks.");

        var toolUseIds = finalMessages[assistantIndex].Content!.OfType<ToolUseContentBlock>().Select(t => t.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        Assert.Contains("tc_a", toolUseIds);
        Assert.DoesNotContain("tc_b", toolUseIds);

        // Tool results must appear after the tool-use message and before the next assistant message (or end).
        var nextAssistantIndex = finalMessages.FindIndex(assistantIndex + 1, m => m.Role == Role.Assistant);
        var endIndexExclusive = nextAssistantIndex >= 0 ? nextAssistantIndex : finalMessages.Count;
        var toolResults = finalMessages
            .Skip(assistantIndex + 1)
            .Take(endIndexExclusive - assistantIndex - 1)
            .SelectMany(m => m.Content?.OfType<ToolResultContentBlock>() ?? [])
            .ToList();

        Assert.Contains(toolResults, tr => tr.ToolUseId == "tc_a");
        Assert.DoesNotContain(toolResults, tr => tr.ToolUseId == "tc_b");
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MaxIterationsReached_HighConfidenceSynthesis_RunsJudgeStep()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new { id = "H1", hypothesis = "NullReferenceException in Foo.Bar", confidence = "medium" },
                        new { id = "H2", hypothesis = "Assembly version mismatch", confidence = "medium" },
                        new { id = "H3", hypothesis = "Trimming removed required member", confidence = "medium" }
                    }
                }, Id: null),
                (Name: "exec", Input: new { command = "!clrstack -a" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.message" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.hResult" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.environment.runtime", pageKind = "object", limit = 30 }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.summary.crashType" }, Id: null)))
            .EnqueueResult(CreateMessageResultWithText("""
            {
              "rootCause": "NullReferenceException in Foo.Bar",
              "confidence": "high",
              "reasoning": "The report shows a null dereference.",
              "evidence": [
                "E1: faulting stack shows Foo.Bar",
                "E2: exception type is System.NullReferenceException",
                "E3: exception message indicates null dereference",
                "E4: assemblies appear consistent",
                "E5: runtime is CoreCLR",
                "E6: top frame resolves to Foo.Bar()"
              ],
              "recommendations": [],
              "additionalFindings": []
            }
            """))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_judge_complete", new
            {
                selectedHypothesisId = "H1",
                confidence = "high",
                rationale = "H1 best fits E1-E3; H2/H3 contradicted by E4/E5.",
                supportsEvidenceIds = new[] { "E1", "E2", "E3" },
                rejectedHypotheses = new[]
                {
                    new { hypothesisId = "H2", contradictsEvidenceIds = new[] { "E4" }, reason = "Assemblies appear consistent in E4." },
                    new { hypothesisId = "H3", contradictsEvidenceIds = new[] { "E5" }, reason = "Runtime is CoreCLR with JIT (E5)." }
                }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 1,
            CheckpointEveryIterations = 0
        };

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => cmd.Contains("!clrstack", StringComparison.OrdinalIgnoreCase)
                ? $"OUTPUT:{cmd}\nFoo.Bar()\n"
                : $"OUTPUT:{cmd}"
        };

        var reportJson = MinimalBaselineReportJson.Replace("\"System.Exception\"", "\"System.NullReferenceException\"");

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            reportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("high", result.Confidence);
        Assert.NotNull(result.Judge);
        Assert.Equal("H1", result.Judge!.SelectedHypothesisId);

        var judgeRequest = requests.Last(r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true);
        Assert.NotNull(judgeRequest.ToolChoice);
        Assert.Equal("required", judgeRequest.ToolChoice!.Mode);
        Assert.NotNull(judgeRequest.Tools);
        Assert.Single(judgeRequest.Tools!);
        Assert.Equal("analysis_judge_complete", judgeRequest.Tools![0].Name);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MaxToolCallsReached_HighConfidenceSynthesis_RunsJudgeStep()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new { id = "H1", hypothesis = "NullReferenceException in Foo.Bar", confidence = "medium" },
                        new { id = "H2", hypothesis = "Assembly version mismatch", confidence = "medium" },
                        new { id = "H3", hypothesis = "Trimming removed required member", confidence = "medium" }
                    }
                }, Id: null),
                (Name: "exec", Input: new { command = "!a" }, Id: "tc_a"),
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: "tc_type"),
                (Name: "report_get", Input: new { path = "analysis.exception.message" }, Id: "tc_msg"),
                (Name: "exec", Input: new { command = "!b" }, Id: "tc_b")))
            .EnqueueResult(CreateMessageResultWithText("""
            {
              "rootCause": "NullReferenceException in Foo.Bar",
              "confidence": "high",
              "reasoning": "The report shows a null dereference.",
              "evidence": [
                "E1: exec(command=\"!a\") output includes Foo.Bar",
                "E2: report_get(path=\"analysis.exception.type\") returned System.NullReferenceException",
                "E3: report_get(path=\"analysis.exception.message\") returned boom",
                "E1: repeated evidence (still grounded)",
                "E2: repeated evidence (still grounded)",
                "E3: repeated evidence (still grounded)"
              ],
              "recommendations": [],
              "additionalFindings": []
            }
            """))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_judge_complete", new
            {
                selectedHypothesisId = "H1",
                confidence = "high",
                rationale = "H1 best fits E1-E3; H2/H3 contradicted by E2/E3.",
                supportsEvidenceIds = new[] { "E1", "E2", "E3" },
                rejectedHypotheses = new[]
                {
                    new { hypothesisId = "H2", contradictsEvidenceIds = new[] { "E2" }, reason = "Exception type evidence does not support mismatch." },
                    new { hypothesisId = "H3", contradictsEvidenceIds = new[] { "E3" }, reason = "Exception message indicates a null dereference, not missing members." }
                }
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => cmd.Contains("!a", StringComparison.OrdinalIgnoreCase)
                ? $"OUTPUT:{cmd}\nFoo.Bar()\n"
                : $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 10,
            MaxToolCalls = 3,
            CheckpointEveryIterations = 0
        };

        var reportJson = MinimalBaselineReportJson.Replace("\"System.Exception\"", "\"System.NullReferenceException\"");

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            reportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("high", result.Confidence);
        Assert.NotNull(result.Judge);
        Assert.Equal("H1", result.Judge!.SelectedHypothesisId);

        var judgeRequest = requests.Last(r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true);
        Assert.NotNull(judgeRequest.ToolChoice);
        Assert.Equal("required", judgeRequest.ToolChoice!.Mode);
        Assert.NotNull(judgeRequest.Tools);
        Assert.Single(judgeRequest.Tools!);
        Assert.Equal("analysis_judge_complete", judgeRequest.Tools![0].Name);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MaxToolCallsReached_HighConfidenceSynthesis_WhenJudgeFails_DowngradesConfidence()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new { id = "H1", hypothesis = "NullReferenceException in Foo.Bar", confidence = "medium" },
                        new { id = "H2", hypothesis = "Assembly version mismatch", confidence = "high" },
                        new { id = "H3", hypothesis = "Trimming removed required member", confidence = "medium" }
                    }
                }, Id: null),
                (Name: "analysis_evidence_add", Input: new
                {
                    items = new[]
                    {
                        new { id = "E1", source = "exec(command=\"!a\")", finding = "Evidence 1" },
                        new { id = "E2", source = "exec(command=\"!a\")", finding = "Evidence 2" },
                        new { id = "E3", source = "exec(command=\"!a\")", finding = "Evidence 3" },
                        new { id = "E4", source = "exec(command=\"!a\")", finding = "Evidence 4" },
                        new { id = "E5", source = "exec(command=\"!a\")", finding = "Evidence 5" },
                        new { id = "E6", source = "exec(command=\"!a\")", finding = "Evidence 6" }
                    }
                }, Id: null),
                (Name: "exec", Input: new { command = "!a" }, Id: "tc_a"),
                (Name: "exec", Input: new { command = "!b" }, Id: "tc_b")))
            .EnqueueResult(CreateMessageResultWithText("""
            {
              "rootCause": "NullReferenceException in Foo.Bar",
              "confidence": "high",
              "reasoning": "The report shows a null dereference.",
              "evidence": [
                "E1: evidence 1",
                "E2: evidence 2",
                "E3: evidence 3",
                "E4: evidence 4",
                "E5: evidence 5",
                "E6: evidence 6"
              ],
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
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("medium", result.Confidence);
        Assert.NotNull(result.Judge);
        Assert.Contains("Deterministic fallback", result.Judge!.Rationale, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Assembly version mismatch", result.RootCause);

        var judgeRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();
        Assert.Single(judgeRequests);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MaxToolCallsReached_HighConfidenceSynthesis_WhenJudgeSupportsEvidenceIdsAreInsufficient_DowngradesConfidence()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new { id = "H1", hypothesis = "NullReferenceException in Foo.Bar", confidence = "medium" },
                        new { id = "H2", hypothesis = "Assembly version mismatch", confidence = "high" },
                        new { id = "H3", hypothesis = "Trimming removed required member", confidence = "medium" }
                    }
                }, Id: null),
                (Name: "analysis_evidence_add", Input: new
                {
                    items = new[]
                    {
                        new { id = "E1", source = "exec(command=\"!a\")", finding = "Evidence 1" },
                        new { id = "E2", source = "exec(command=\"!a\")", finding = "Evidence 2" },
                        new { id = "E3", source = "exec(command=\"!a\")", finding = "Evidence 3" },
                        new { id = "E4", source = "exec(command=\"!a\")", finding = "Evidence 4" },
                        new { id = "E5", source = "exec(command=\"!a\")", finding = "Evidence 5" },
                        new { id = "E6", source = "exec(command=\"!a\")", finding = "Evidence 6" }
                    }
                }, Id: null),
                (Name: "exec", Input: new { command = "!a" }, Id: "tc_a"),
                (Name: "exec", Input: new { command = "!b" }, Id: "tc_b")))
            .EnqueueResult(CreateMessageResultWithText("""
            {
              "rootCause": "NullReferenceException in Foo.Bar",
              "confidence": "high",
              "reasoning": "The report shows a null dereference.",
              "evidence": [
                "E1: evidence 1",
                "E2: evidence 2",
                "E3: evidence 3",
                "E4: evidence 4",
                "E5: evidence 5",
                "E6: evidence 6"
              ],
              "recommendations": [],
              "additionalFindings": []
            }
            """))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_judge_complete", new
            {
                selectedHypothesisId = "H2",
                confidence = "high",
                rationale = "H2 best fits E1; alternatives are contradicted.",
                supportsEvidenceIds = new[] { "E1" },
                rejectedHypotheses = new[]
                {
                    new { hypothesisId = "H1", contradictsEvidenceIds = new[] { "E4" }, reason = "Contradicted by E4." },
                    new { hypothesisId = "H3", contradictsEvidenceIds = new[] { "E5" }, reason = "Contradicted by E5." }
                }
            }));

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
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("medium", result.Confidence);
        Assert.NotNull(result.Judge);
        Assert.Equal("H2", result.Judge!.SelectedHypothesisId);
        Assert.Equal("Assembly version mismatch", result.RootCause);
        Assert.Contains("downgraded", result.Reasoning ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var judgeRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();
        Assert.Single(judgeRequests);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_MaxToolCallsReached_HighConfidenceSynthesis_WhenJudgeRejectsDuplicateHypothesisIds_DowngradesConfidence()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new { id = "H1", hypothesis = "NullReferenceException in Foo.Bar", confidence = "medium" },
                        new { id = "H2", hypothesis = "Assembly version mismatch", confidence = "high" },
                        new { id = "H3", hypothesis = "Trimming removed required member", confidence = "medium" }
                    }
                }, Id: null),
                (Name: "analysis_evidence_add", Input: new
                {
                    items = new[]
                    {
                        new { id = "E1", source = "exec(command=\"!a\")", finding = "Evidence 1" },
                        new { id = "E2", source = "exec(command=\"!a\")", finding = "Evidence 2" },
                        new { id = "E3", source = "exec(command=\"!a\")", finding = "Evidence 3" },
                        new { id = "E4", source = "exec(command=\"!a\")", finding = "Evidence 4" },
                        new { id = "E5", source = "exec(command=\"!a\")", finding = "Evidence 5" },
                        new { id = "E6", source = "exec(command=\"!a\")", finding = "Evidence 6" }
                    }
                }, Id: null),
                (Name: "exec", Input: new { command = "!a" }, Id: "tc_a"),
                (Name: "exec", Input: new { command = "!b" }, Id: "tc_b")))
            .EnqueueResult(CreateMessageResultWithText("""
            {
              "rootCause": "NullReferenceException in Foo.Bar",
              "confidence": "high",
              "reasoning": "The report shows a null dereference.",
              "evidence": [
                "E1: evidence 1",
                "E2: evidence 2",
                "E3: evidence 3",
                "E4: evidence 4",
                "E5: evidence 5",
                "E6: evidence 6"
              ],
              "recommendations": [],
              "additionalFindings": []
            }
            """))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_judge_complete", new
            {
                selectedHypothesisId = "H2",
                confidence = "high",
                rationale = "H2 best fits E1-E3; duplicates should not be accepted.",
                supportsEvidenceIds = new[] { "E1", "E2", "E3" },
                rejectedHypotheses = new[]
                {
                    new { hypothesisId = "H1", contradictsEvidenceIds = new[] { "E4" }, reason = "Contradicted by E4." },
                    new { hypothesisId = "H1", contradictsEvidenceIds = new[] { "E5" }, reason = "Contradicted by E5 (duplicate id)." }
                }
            }));

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
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("medium", result.Confidence);
        Assert.NotNull(result.Judge);
        Assert.Equal("H2", result.Judge!.SelectedHypothesisId);
        Assert.Equal("Assembly version mismatch", result.RootCause);
        Assert.Contains("downgraded", result.Reasoning ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var judgeRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();
        Assert.Single(judgeRequests);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_CheckpointEveryIterations_PrunesConversationAndContinues()
    {
        var requests = new List<CreateMessageRequestParams>();
        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "report_get", Input: new { path = "metadata", pageKind = "object", limit = 50 }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.summary",
                    pageKind = "object",
                    select = new[] { "crashType", "description", "recommendations", "threadCount", "moduleCount", "assemblyCount" }
                }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.environment",
                    pageKind = "object",
                    select = new[] { "platform", "runtime", "process", "nativeAot" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.message" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.hResult" }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.exception.stackTrace",
                    limit = 8,
                    select = new[] { "frameNumber", "instructionPointer", "module", "function", "sourceFile", "lineNumber", "isManaged" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.analysis", pageKind = "object", limit = 200 }, Id: null),
                (Name: "exec", Input: new { command = "!a" }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_evidence_add", new { items = Array.Empty<object>() }))
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
                reasoning = "ok",
                evidence = new[]
                {
                    "exec(command=\"!a\") -> OUTPUT:!a",
                    "exec(command=\"!b\") -> OUTPUT:!b"
                }
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
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("h1", result.RootCause);
        Assert.NotNull(result.Judge);
        Assert.False(string.IsNullOrWhiteSpace(result.Judge!.SelectedHypothesisId));
        Assert.Equal(2, debugger.ExecutedCommands.Count);
        Assert.Equal(6, requests.Count);

        Assert.NotNull(requests[3].ToolChoice);
        Assert.Equal("required", requests[3].ToolChoice!.Mode);
        Assert.Contains(requests[3].Tools!, t => string.Equals(t.Name, "checkpoint_complete", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(512, requests[3].MaxTokens);
        Assert.Contains(
            requests[3].Messages!.SelectMany(m => m.Content?.OfType<ToolResultContentBlock>() ?? []),
            tr => !string.IsNullOrWhiteSpace(tr.ToolUseId));

        Assert.NotNull(requests[4].Messages);
        Assert.NotEmpty(requests[4].Messages);
        var checkpointCarry = requests[4].Messages[0].Content!.OfType<TextContentBlock>().Single().Text;
        Assert.Contains("Checkpoint JSON", checkpointCarry, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"facts\"", checkpointCarry, StringComparison.Ordinal);

        var judgeRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();
        Assert.Single(judgeRequests);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenDuplicateCachedToolCallOccurs_CreatesCheckpointBeforeInterval()
    {
        var requests = new List<CreateMessageRequestParams>();
        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "report_get", Input: new { path = "metadata", pageKind = "object", limit = 50 }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.summary",
                    pageKind = "object",
                    select = new[] { "crashType", "description", "recommendations", "threadCount", "moduleCount", "assemblyCount" }
                }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.environment",
                    pageKind = "object",
                    select = new[] { "platform", "runtime", "process", "nativeAot" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.message" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.hResult" }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.exception.stackTrace",
                    limit = 8,
                    select = new[] { "frameNumber", "instructionPointer", "module", "function", "sourceFile", "lineNumber", "isManaged" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.analysis", pageKind = "object", limit = 200 }, Id: null),
                (Name: "exec", Input: new { command = "!a" }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_evidence_add", new { items = Array.Empty<object>() }))
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!a" }))
            .EnqueueResult(CreateMessageResultWithToolUse("checkpoint_complete", new
            {
                facts = new[] { "f1" },
                hypotheses = Array.Empty<object>(),
                evidence = Array.Empty<object>(),
                doNotRepeat = new[] { "exec(!a)" },
                nextSteps = Array.Empty<object>()
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "done",
                confidence = "low",
                reasoning = "ok",
                evidence = new[]
                {
                    "exec(command=\"!a\") -> OUTPUT:!a"
                }
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 4,
            CheckpointEveryIterations = 10,
            CheckpointMaxTokens = 256
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("done", result.RootCause);
        Assert.Single(debugger.ExecutedCommands);

        var checkpointRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "checkpoint_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();
        Assert.Single(checkpointRequests);
        Assert.NotNull(checkpointRequests[0].ToolChoice);
        Assert.Equal("required", checkpointRequests[0].ToolChoice!.Mode);
        Assert.Equal(256, checkpointRequests[0].MaxTokens);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_CheckpointJsonAbove20k_IsAcceptedAndCarriedForward()
    {
        var facts = Enumerable.Range(0, 50)
            .Select(i => $"FACT-{i:D2}:{new string('a', 600)}")
            .ToArray();

        var checkpointPayload = new
        {
            facts,
            hypotheses = Array.Empty<object>(),
            evidence = Array.Empty<object>(),
            doNotRepeat = Array.Empty<string>(),
            nextSteps = Array.Empty<object>()
        };

        var checkpointJson = JsonSerializer.Serialize(checkpointPayload, new JsonSerializerOptions { WriteIndented = false });
        Assert.True(checkpointJson.Length is > 20_000 and < 50_000, $"Expected checkpoint JSON length between 20k and 50k, got {checkpointJson.Length} chars.");

        var requests = new List<CreateMessageRequestParams>();
        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "report_get", Input: new { path = "metadata", pageKind = "object", limit = 50 }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.summary",
                    pageKind = "object",
                    select = new[] { "crashType", "description", "recommendations", "threadCount", "moduleCount", "assemblyCount" }
                }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.environment",
                    pageKind = "object",
                    select = new[] { "platform", "runtime", "process", "nativeAot" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.message" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.hResult" }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.exception.stackTrace",
                    limit = 8,
                    select = new[] { "frameNumber", "instructionPointer", "module", "function", "sourceFile", "lineNumber", "isManaged" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.analysis", pageKind = "object", limit = 200 }, Id: null),
                (Name: "exec", Input: new { command = "!a" }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_evidence_add", new { items = Array.Empty<object>() }))
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!b" }))
            .EnqueueResult(CreateMessageResultWithToolUse("checkpoint_complete", checkpointPayload))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "done",
                confidence = "low",
                reasoning = "ok",
                evidence = new[]
                {
                    "exec(command=\"!a\") -> OUTPUT:!a",
                    "exec(command=\"!b\") -> OUTPUT:!b"
                }
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
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("done", result.RootCause);
        Assert.Equal(2, debugger.ExecutedCommands.Count);
        Assert.Equal(5, requests.Count);

        var carryText = requests[4].Messages[0].Content!.OfType<TextContentBlock>().Single().Text;
        Assert.True(carryText.Length > 20_000, $"Expected carry-forward message to exceed 20k chars, got {carryText.Length}.");
        Assert.DoesNotContain("Checkpoint synthesis unavailable", carryText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FACT-49:", carryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_CheckpointCarryForward_IncludesStableStateSnapshot()
    {
        var requests = new List<CreateMessageRequestParams>();
        var sampling = new SequencedCapturingSamplingClient(requests)
            // Iteration 1: gather evidence + populate internal state (E1/H1).
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "report_get", Input: new { path = "metadata", pageKind = "object", limit = 50 }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.summary",
                    pageKind = "object",
                    select = new[] { "crashType", "description", "recommendations", "threadCount", "moduleCount", "assemblyCount" }
                }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.environment",
                    pageKind = "object",
                    select = new[] { "platform", "runtime", "process", "nativeAot" }
                }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.exception.type"
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.message" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.hResult" }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.exception.stackTrace",
                    limit = 8,
                    select = new[] { "frameNumber", "instructionPointer", "module", "function", "sourceFile", "lineNumber", "isManaged" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.analysis", pageKind = "object", limit = 200 }, Id: null),
                (Name: "analysis_evidence_add", Input: new
                {
                    items = new[]
                    {
                        new
                        {
                            source = "report_get(path=\"analysis.exception.type\")",
                            finding = "System.MissingMethodException"
                        }
                    }
                }, Id: null),
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new
                        {
                            hypothesis = "Assembly mismatch caused MissingMethodException",
                            confidence = "unknown"
                        }
                    }
                }, Id: null)))
            // Forced meta bookkeeping request (meta tools only).
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_hypothesis_score", new
            {
                updates = new object[]
                {
                    new
                    {
                        id = "H1",
                        confidence = "unknown",
                        supportsEvidenceIds = new[] { "E1" },
                        notes = "Carry forward baseline findings."
                    }
                }
            }))
            // Checkpoint synthesis request.
            .EnqueueResult(CreateMessageResultWithToolUse("checkpoint_complete", new
            {
                facts = new[] { "f1" },
                hypotheses = new object[]
                {
                    new { hypothesis = "Assembly mismatch caused MissingMethodException", confidence = "unknown", evidence = Array.Empty<string>(), unknowns = Array.Empty<string>() }
                },
                evidence = new object[]
                {
                    new { id = "E1", source = "report_get(path=\"analysis.exception.type\")", finding = "System.MissingMethodException" }
                },
                doNotRepeat = new[] { "report_get(path=\"analysis.exception.type\")" },
                nextSteps = new object[]
                {
                    new { tool = "report_get", call = "report_get(path=\"analysis.exception.stackTrace\", limit=10)", why = "Confirm crash context" }
                }
            }))
            // Iteration 2: finish.
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "done",
                confidence = "low",
                reasoning = "ok",
                evidence = new[] { "E1" }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 2,
            CheckpointEveryIterations = 1,
            CheckpointMaxTokens = 256
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal("Assembly mismatch caused MissingMethodException", result.RootCause);
        Assert.NotNull(result.Judge);
        Assert.Equal("H1", result.Judge!.SelectedHypothesisId);
        Assert.Equal(5, requests.Count);

        var iteration2 = requests[3];
        Assert.NotNull(iteration2.Messages);
        Assert.NotEmpty(iteration2.Messages);

        var carryForward = iteration2.Messages[0].Content!.OfType<TextContentBlock>().Single().Text;
        Assert.Contains("Checkpoint JSON", carryForward, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Stable state JSON (evidence ledger + hypotheses):", carryForward, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"evidenceLedger\"", carryForward, StringComparison.Ordinal);
        Assert.Contains("\"hypotheses\"", carryForward, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"E1\"", carryForward, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"id\":\"H1\"", carryForward, StringComparison.OrdinalIgnoreCase);

        var judgeRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();
        Assert.Single(judgeRequests);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenCheckpointEvidenceSwapsExistingIds_DoesNotOverwriteEvidenceLedger()
    {
        var requests = new List<CreateMessageRequestParams>();
        var sampling = new SequencedCapturingSamplingClient(requests)
            // Iteration 1: gather baseline evidence (E1..E8).
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "report_get", Input: new { path = "metadata", pageKind = "object", limit = 50 }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.summary",
                    pageKind = "object",
                    select = new[] { "crashType", "description", "recommendations", "threadCount", "moduleCount", "assemblyCount" }
                }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.environment",
                    pageKind = "object",
                    select = new[] { "platform", "runtime", "process", "nativeAot" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.message" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.hResult" }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.exception.stackTrace",
                    limit = 8,
                    select = new[] { "frameNumber", "instructionPointer", "module", "function", "sourceFile", "lineNumber", "isManaged" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.analysis", pageKind = "object", limit = 200 }, Id: null)))
            // Meta bookkeeping (required by the orchestrator once baseline completes).
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_evidence_add", new { items = Array.Empty<object>() }))
            // Checkpoint synthesis attempts to swap E1/E2.
            .EnqueueResult(CreateMessageResultWithToolUse("checkpoint_complete", new
            {
                facts = new[] { "f1" },
                hypotheses = Array.Empty<object>(),
                evidence = new object[]
                {
                    new { id = "E1", source = "report_get(path=\"analysis.summary\")", finding = "SWAPPED_SUMMARY" },
                    new { id = "E2", source = "report_get(path=\"metadata\")", finding = "SWAPPED_METADATA" }
                },
                doNotRepeat = new[] { "report_get(path=\"metadata\")" },
                nextSteps = Array.Empty<object>()
            }))
            // Iteration 2: finish.
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "done",
                confidence = "low",
                reasoning = "ok",
                evidence = new[] { "E1", "E2" }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 2,
            CheckpointEveryIterations = 1,
            CheckpointMaxTokens = 256,
            EnableEvidenceProvenance = true
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.NotNull(result.EvidenceLedger);

        var e1 = Assert.Single(result.EvidenceLedger!, e => string.Equals("E1", e.Id, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"path\":\"metadata\"", e1.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("analysis.summary", e1.Source, StringComparison.OrdinalIgnoreCase);

        var e2 = Assert.Single(result.EvidenceLedger!, e => string.Equals("E2", e.Id, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"path\":\"analysis.summary\"", e2.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"path\":\"metadata\"", e2.Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenCheckpointSynthesisThrows_PrunesWithDeterministicCheckpointAndContinues()
    {
        var requests = new List<CreateMessageRequestParams>();
        var sampling = new ThrowingCheckpointSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "report_get", Input: new { path = "metadata", pageKind = "object", limit = 50 }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.summary",
                    pageKind = "object",
                    select = new[] { "crashType", "description", "recommendations", "threadCount", "moduleCount", "assemblyCount" }
                }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.environment",
                    pageKind = "object",
                    select = new[] { "platform", "runtime", "process", "nativeAot" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.message" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.hResult" }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.exception.stackTrace",
                    limit = 8,
                    select = new[] { "frameNumber", "instructionPointer", "module", "function", "sourceFile", "lineNumber", "isManaged" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.analysis", pageKind = "object", limit = 200 }, Id: null),
                (Name: "exec", Input: new { command = "!a" }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_evidence_add", new { items = Array.Empty<object>() }))
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new { command = "!b" }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "done",
                confidence = "low",
                reasoning = "ok",
                evidence = new[]
                {
                    "exec(command=\"!a\") -> OUTPUT:!a",
                    "exec(command=\"!b\") -> OUTPUT:!b"
                }
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
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("done", result.RootCause);
        Assert.Equal(2, debugger.ExecutedCommands.Count);
        Assert.True(requests.Count >= 5);

        var last = requests.Last();
        var carryText = last.Messages[0].Content!.OfType<TextContentBlock>().Single().Text;
        Assert.Contains("Checkpoint synthesis unavailable", carryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_CheckpointAfterFirst_PrimesFromLastCheckpointInsteadOfFullHistory()
    {
        var requests = new List<CreateMessageRequestParams>();
        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUses(
                (Name: "report_get", Input: new { path = "metadata", pageKind = "object", limit = 50 }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.summary",
                    pageKind = "object",
                    select = new[] { "crashType", "description", "recommendations", "threadCount", "moduleCount", "assemblyCount" }
                }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.environment",
                    pageKind = "object",
                    select = new[] { "platform", "runtime", "process", "nativeAot" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.message" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.hResult" }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.exception.stackTrace",
                    limit = 8,
                    select = new[] { "frameNumber", "instructionPointer", "module", "function", "sourceFile", "lineNumber", "isManaged" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.analysis", pageKind = "object", limit = 200 }, Id: null),
                (Name: "exec", Input: new { command = "!a" }, Id: null)))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_evidence_add", new { items = Array.Empty<object>() }))
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
                reasoning = "ok",
                evidence = new[]
                {
                    "exec(command=\"!a\") -> OUTPUT:!a",
                    "exec(command=\"!b\") -> OUTPUT:!b",
                    "exec(command=\"!c\") -> OUTPUT:!c",
                    "exec(command=\"!d\") -> OUTPUT:!d"
                }
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
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("done", result.RootCause);
        Assert.Equal(4, debugger.ExecutedCommands.Count);
        Assert.Equal(8, requests.Count);

        Assert.NotNull(requests[3].Messages);
        Assert.NotEmpty(requests[3].Messages);
        Assert.Contains(
            requests[3].Messages.SelectMany(m => m.Content?.OfType<ToolResultContentBlock>() ?? []),
            tr => !string.IsNullOrWhiteSpace(tr.ToolUseId));

        var secondCheckpointRequest = requests[6];
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
    public void Ctor_MaxTokensPerRequest_DefaultIs16384()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true);
        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance);
        Assert.Equal(16_384, orchestrator.MaxTokensPerRequest);
    }

    [Fact]
    public void Ctor_CheckpointMaxTokens_DefaultIs65000()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true);
        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance);
        Assert.Equal(65_000, orchestrator.CheckpointMaxTokens);
    }

    [Fact]
    public void Ctor_FinalSynthesisMaxTokens_DefaultIs65000()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true);
        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance);
        Assert.Equal(65_000, orchestrator.FinalSynthesisMaxTokens);
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
        Assert.Equal(orchestrator.FinalSynthesisMaxTokens, requests.Last().MaxTokens);
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
        Assert.Equal(2, result.CommandsExecuted!.Count);
        Assert.DoesNotContain(result.CommandsExecuted, c => c.Output.Contains("skipped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenDuplicateToolCallsOccur_DoesNotConsumeToolBudget()
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUses(
                ("exec", new { command = "!a" }, "tc_a1"),
                ("exec", new { command = "!a" }, "tc_a2"),
                ("exec", new { command = "!b" }, "tc_b")))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "done",
                evidence = new[] { "exec(command=\"!a\") -> OUTPUT:!a", "exec(command=\"!b\") -> OUTPUT:!b" }
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3,
            MaxToolCalls = 2,
            CheckpointEveryIterations = 0
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal("Ok", result.RootCause);
        Assert.Equal("low", result.Confidence);
        Assert.Equal(2, debugger.ExecutedCommands.Count);
        Assert.Contains("!a", debugger.ExecutedCommands);
        Assert.Contains("!b", debugger.ExecutedCommands);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenToolBudgetSynthesisIsUnstructured_ProducesJudgeAlignedRecommendations()
    {
        const string hypothesis = "Profiler/IL rewriting instrumentation corrupted the ConcurrentDictionary type metadata";

        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUses(
                ("analysis_hypothesis_register", new
                {
                    hypotheses = new[]
                    {
                        new { id = "H1", hypothesis, confidence = "medium" },
                        new { id = "H2", hypothesis = "Assembly version mismatch", confidence = "medium" },
                        new { id = "H3", hypothesis = "Trimming removed required member", confidence = "low" }
                    }
                }, null),
                ("analysis_evidence_add", new
                {
                    items = new[]
                    {
                        new { id = "E1", source = "exec(command=\"!a\")", finding = "Saw MissingMethodException" }
                    }
                }, null),
                ("exec", new { command = "!a" }, "tc_a"),
                ("exec", new { command = "!b" }, "tc_b")))
            .EnqueueResult(CreateMessageResultWithText("not valid json"))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_judge_complete", new
            {
                selectedHypothesisId = "H1",
                confidence = "low",
                rationale = "Deterministic test judge output.",
                supportsEvidenceIds = new[] { "E1" },
                rejectedHypotheses = new[]
                {
                    new { hypothesisId = "H2", contradictsEvidenceIds = Array.Empty<string>(), reason = "No contradicting evidence recorded." },
                    new { hypothesisId = "H3", contradictsEvidenceIds = Array.Empty<string>(), reason = "No contradicting evidence recorded." }
                }
            }));

        var debugger = new FakeDebuggerManager
        {
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 5,
            MaxToolCalls = 1,
            CheckpointEveryIterations = 0
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            debugger,
            clrMdAnalyzer: null);

        Assert.Equal(hypothesis, result.RootCause);
        Assert.NotNull(result.Judge);
        Assert.Equal("H1", result.Judge!.SelectedHypothesisId);
        Assert.NotNull(result.Recommendations);
        Assert.NotEmpty(result.Recommendations!);
        Assert.NotNull(result.Reasoning);
        Assert.Contains("Selected hypothesis:", result.Reasoning!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Final synthesis", result.Reasoning!, StringComparison.OrdinalIgnoreCase);
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
                reasoning = "Used thread stack.",
                evidence = new[]
                {
                    "get_thread_stack(threadId=\"17\") -> includes threadId=17 and top frame Foo"
                }
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
    public async Task AnalyzeCrashAsync_GetThreadStack_WhenInputIsThreadIndex_MatchesLeadingThreadIdNumber()
    {
        var report = new CrashAnalysisResult
        {
            Threads = new ThreadsInfo
            {
                All =
                [
                    // Intentionally put a managedThreadId match first to ensure thread-index matching takes precedence.
                    new ThreadInfo { ThreadId = "99", ManagedThreadId = 16, State = "stopped", IsFaulting = false, TopFunction = "WrongThread" },
                    new ThreadInfo { ThreadId = "16 (tid: 35176)", ManagedThreadId = 100, State = "stopped", IsFaulting = false, TopFunction = "RightThread" }
                ]
            }
        };

        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("get_thread_stack", new { threadId = "16" }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "Used thread stack.",
                evidence = new[]
                {
                    "get_thread_stack(threadId=\"16\") -> matched by thread index"
                }
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
            c.Output.Contains("\"threadId\": \"16 (tid: 35176)\"", StringComparison.OrdinalIgnoreCase));
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
                evidence = new[]
                {
                    "report_get(path=\"analysis.exception\") -> exception present"
                },
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
                reasoning = "done",
                evidence = new[]
                {
                    "inspect(address=\"0x1234\") -> returned a hint because ClrMD analyzer was not available"
                }
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

    [Theory]
    [InlineData("sos !name2ee System.Private.CoreLib System.String")]
    [InlineData("sos  !name2ee System.Private.CoreLib System.String")]
    [InlineData("sos\t!name2ee System.Private.CoreLib System.String")]
    [InlineData("  SOS \t !name2ee System.Private.CoreLib System.String  ")]
    public async Task AnalyzeCrashAsync_WhenExecUsesSosBangOnLldb_RewritesToValidSosCommand(string command)
    {
        var sampling = new FakeSamplingClient(isSamplingSupported: true, isToolUseSupported: true)
            .EnqueueResult(CreateMessageResultWithToolUse("exec", new
            {
                command
            }))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "Ok",
                confidence = "low",
                reasoning = "done",
                evidence = new[]
                {
                    "exec(command=\"sos !name2ee System.Private.CoreLib System.String\") -> invoked SOS type lookup"
                }
            }));

        var debugger = new FakeDebuggerManager
        {
            DebuggerType = "LLDB",
            CommandHandler = cmd => $"OUTPUT:{cmd}"
        };

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3
        };

        _ = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            "{}",
            debugger,
            clrMdAnalyzer: null);

        Assert.Single(debugger.ExecutedCommands);
        Assert.StartsWith("sos name2ee", debugger.ExecutedCommands[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sos !", debugger.ExecutedCommands[0], StringComparison.OrdinalIgnoreCase);
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
                reasoning = "done",
                evidence = new[]
                {
                    "exec(command=\"!threads\") -> executed once; duplicate call reused cached tool result"
                }
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
        var execCalls = result.CommandsExecuted!.Where(c => c.Tool == "exec").ToList();
        Assert.Equal(2, execCalls.Count);
        Assert.Equal(execCalls[0].Output, execCalls[1].Output);
        Assert.Contains(execCalls, c => c.Cached == true);
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
                reasoning = "done",
                evidence = new[]
                {
                    "inspect(address=\"0x1234\") -> returned a hint because ClrMD analyzer was not available"
                }
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
                reasoning = "done",
                evidence = new[]
                {
                    "get_thread_stack(threadId=\"999\") -> Thread not found in report"
                }
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
                reasoning = "done",
                evidence = new[]
                {
                    "get_thread_stack(threadId=\"0x10\") -> matched threadId=1 from report regardless of hex formatting"
                }
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

    [Fact]
    public async Task AnalyzeCrashAsync_WhenMetaBookkeepingToolChoiceRequiredUnsupported_FallsBackToAuto()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new ToolChoiceRequiredRejectingSamplingClient(requests)
            .EnqueueAnalysisResult(CreateMessageResultWithToolUses(
                (Name: "report_get", Input: new { path = "metadata", pageKind = "object", limit = 50 }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.summary",
                    pageKind = "object",
                    select = new[] { "crashType", "description", "recommendations", "threadCount", "moduleCount", "assemblyCount" }
                }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.environment",
                    pageKind = "object",
                    select = new[] { "platform", "runtime", "process", "nativeAot" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.message" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.hResult" }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.exception.stackTrace",
                    limit = 8,
                    select = new[] { "frameNumber", "instructionPointer", "module", "function", "sourceFile", "lineNumber", "isManaged" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.analysis", pageKind = "object", limit = 200 }, Id: null)))
            .WithMetaBookkeepingResult(CreateMessageResultWithToolUse("analysis_hypothesis_register", new
            {
                hypotheses = new[]
                {
                    new { hypothesis = "Assembly mismatch", confidence = "unknown" }
                }
            }))
            .EnqueueAnalysisResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "done",
                confidence = "low",
                reasoning = "ok",
                evidence = Array.Empty<string>()
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3,
            CheckpointEveryIterations = 0
        };

        _ = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        var metaRequests = requests
            .Where(r => r.Tools != null && r.Tools.All(t =>
                string.Equals(t.Name, "analysis_evidence_add", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Name, "analysis_hypothesis_register", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Name, "analysis_hypothesis_score", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(metaRequests.Count >= 2, $"Expected at least two meta bookkeeping requests (required + fallback), got {metaRequests.Count}.");
        Assert.Equal("required", metaRequests[0].ToolChoice?.Mode);
        Assert.Equal("auto", metaRequests[1].ToolChoice?.Mode);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenCheckpointToolChoiceRequiredUnsupported_FallsBackToAuto()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new CheckpointToolChoiceRequiredRejectingSamplingClient(requests)
            .EnqueueAnalysisResult(CreateMessageResultWithToolUses(
                (Name: "report_get", Input: new { path = "metadata", pageKind = "object", limit = 50 }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.summary",
                    pageKind = "object",
                    select = new[] { "crashType", "description", "recommendations", "threadCount", "moduleCount", "assemblyCount" }
                }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.environment",
                    pageKind = "object",
                    select = new[] { "platform", "runtime", "process", "nativeAot" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.message" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.hResult" }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.exception.stackTrace",
                    limit = 8,
                    select = new[] { "frameNumber", "instructionPointer", "module", "function", "sourceFile", "lineNumber", "isManaged" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.analysis", pageKind = "object", limit = 200 }, Id: null)))
            .WithCheckpointResult(CreateMessageResultWithToolUse("checkpoint_complete", new
            {
                facts = new[] { "f1" },
                hypotheses = Array.Empty<object>(),
                evidence = Array.Empty<object>(),
                doNotRepeat = Array.Empty<string>(),
                nextSteps = Array.Empty<object>()
            }))
            .EnqueueAnalysisResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "done",
                confidence = "low",
                reasoning = "ok",
                evidence = Array.Empty<string>()
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3,
            CheckpointEveryIterations = 1,
            MaxMetaToolCalls = 0
        };

        _ = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        var checkpointRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "checkpoint_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();

        Assert.True(checkpointRequests.Count >= 2, $"Expected at least two checkpoint requests (required + fallback), got {checkpointRequests.Count}.");
        Assert.Equal("required", checkpointRequests[0].ToolChoice?.Mode);
        Assert.Equal("auto", checkpointRequests[1].ToolChoice?.Mode);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenToolChoiceRequiredUnsupported_CachesFallbackForSubsequentCheckpointRequests()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new ToolChoiceRequiredRejectingSamplingClient(requests)
            .EnqueueAnalysisResult(CreateMessageResultWithToolUses(
                (Name: "report_get", Input: new { path = "metadata", pageKind = "object", limit = 50 }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.summary",
                    pageKind = "object",
                    select = new[] { "crashType", "description", "recommendations", "threadCount", "moduleCount", "assemblyCount" }
                }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.environment",
                    pageKind = "object",
                    select = new[] { "platform", "runtime", "process", "nativeAot" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.message" }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.hResult" }, Id: null),
                (Name: "report_get", Input: new
                {
                    path = "analysis.exception.stackTrace",
                    limit = 8,
                    select = new[] { "frameNumber", "instructionPointer", "module", "function", "sourceFile", "lineNumber", "isManaged" }
                }, Id: null),
                (Name: "report_get", Input: new { path = "analysis.exception.analysis", pageKind = "object", limit = 200 }, Id: null)))
            .WithCheckpointResult(CreateMessageResultWithToolUse("checkpoint_complete", new
            {
                facts = Array.Empty<string>(),
                hypotheses = Array.Empty<object>(),
                evidence = Array.Empty<object>(),
                doNotRepeat = Array.Empty<string>(),
                nextSteps = Array.Empty<object>()
            }))
            .EnqueueAnalysisResult(CreateMessageResultWithToolUse("report_get", new { path = "analysis.exception.type" }))
            .EnqueueAnalysisResult(CreateMessageResultWithText("no-op"))
            .EnqueueAnalysisResult(CreateMessageResultWithText("""
                {
                  "rootCause": "done",
                  "confidence": "low",
                  "reasoning": "ok",
                  "evidence": [],
                  "recommendations": [],
                  "additionalFindings": []
                }
                """));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3,
            CheckpointEveryIterations = 1,
            MaxMetaToolCalls = 0
        };

        _ = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        var metaRequests = requests
            .Where(r => r.Tools != null && r.Tools.All(t =>
                string.Equals(t.Name, "analysis_evidence_add", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Name, "analysis_hypothesis_register", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Name, "analysis_hypothesis_score", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var checkpointRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "checkpoint_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();

        Assert.True(metaRequests.Count >= 2, $"Expected at least two meta bookkeeping requests (required + fallback), got {metaRequests.Count}.");
        Assert.Equal("required", metaRequests[0].ToolChoice?.Mode);
        Assert.Equal("auto", metaRequests[1].ToolChoice?.Mode);

        Assert.Equal(2, checkpointRequests.Count);
        Assert.All(checkpointRequests, r => Assert.Equal("auto", r.ToolChoice?.Mode));
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenJudgeToolChoiceRequiredUnsupported_FallsBackToAuto()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new ToolChoiceRequiredRejectingSamplingClient(requests)
            .EnqueueAnalysisResult(CreateMessageResultWithToolUses(
                (Name: "report_get", Input: new { path = "analysis.exception.type" }, Id: null),
                (Name: "analysis_evidence_add", Input: new
                {
                    items = new[]
                    {
                        new { source = "report_get(path=\"analysis.exception.type\")", finding = "System.Exception" }
                    }
                }, Id: null),
                (Name: "analysis_hypothesis_register", Input: new
                {
                    hypotheses = new[]
                    {
                        new { hypothesis = "Assembly mismatch", confidence = "unknown" }
                    }
                }, Id: null),
                (Name: "analysis_complete", Input: new
                {
                    rootCause = "done",
                    confidence = "low",
                    reasoning = "ok",
                    evidence = new[] { "E1" }
                }, Id: null)))
            .WithJudgeResult(CreateMessageResultWithToolUse("analysis_judge_complete", new
            {
                selectedHypothesisId = "H1",
                confidence = "low",
                rationale = "E1 supports H1",
                supportsEvidenceIds = new[] { "E1" },
                rejectedHypotheses = new[]
                {
                    new { hypothesisId = "H2", contradictsEvidenceIds = new[] { "E1" }, reason = "Not supported" },
                    new { hypothesisId = "H3", contradictsEvidenceIds = new[] { "E1" }, reason = "Not supported" }
                }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 2,
            CheckpointEveryIterations = 0
        };

        _ = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        var judgeRequests = requests
            .Where(r => r.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();

        Assert.True(judgeRequests.Count >= 2, $"Expected at least two judge requests (required + fallback), got {judgeRequests.Count}.");
        Assert.Equal("required", judgeRequests[0].ToolChoice?.Mode);
        Assert.Equal("auto", judgeRequests[1].ToolChoice?.Mode);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenNoProgressPersists_FinalizesEarly()
    {
        var requests = new List<CreateMessageRequestParams>();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new { path = "metadata" }))
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new { path = "metadata" }))
            .EnqueueResult(CreateMessageResultWithToolUse("report_get", new { path = "metadata" }))
            .EnqueueResult(CreateMessageResultWithText("""
            {
              "rootCause": "stopped",
              "confidence": "low",
              "reasoning": "no progress",
              "evidence": [],
              "recommendations": [],
              "additionalFindings": []
            }
            """));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 50,
            CheckpointEveryIterations = 0,
            MaxMetaToolCalls = 0,
            MaxConsecutiveNoProgressIterations = 2
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal("stopped", result.RootCause);

        var finalRequest = requests.Last();
        Assert.Null(finalRequest.Tools);
        Assert.Null(finalRequest.ToolChoice);
    }

    [Fact]
    public async Task AnalyzeCrashAsync_WhenModelSpamsToolUses_PrunesExcessAndContinues()
    {
        var requests = new List<CreateMessageRequestParams>();

        var toolUses = Enumerable.Range(0, 10)
            .Select(_ => (Name: "report_get", Input: (object)new { path = "metadata" }, Id: (string?)null))
            .ToArray();

        var sampling = new SequencedCapturingSamplingClient(requests)
            .EnqueueResult(CreateMessageResultWithToolUses(toolUses))
            .EnqueueResult(CreateMessageResultWithToolUse("analysis_complete", new
            {
                rootCause = "done",
                confidence = "low",
                reasoning = "ok",
                evidence = new[]
                {
                    "report_get(path=\"metadata\") -> debuggerType=LLDB"
                }
            }));

        var orchestrator = new AiAnalysisOrchestrator(sampling, NullLogger<AiAnalysisOrchestrator>.Instance)
        {
            MaxIterations = 3,
            CheckpointEveryIterations = 0,
            MaxMetaToolCalls = 0,
            MaxToolUsesPerIteration = 2
        };

        var result = await orchestrator.AnalyzeCrashAsync(
            new CrashAnalysisResult(),
            MinimalBaselineReportJson,
            new FakeDebuggerManager(),
            clrMdAnalyzer: null);

        Assert.Equal("done", result.RootCause);
        Assert.NotNull(result.CommandsExecuted);

        var reportGets = result.CommandsExecuted!.Where(c => c.Tool == "report_get").ToList();
        Assert.True(reportGets.Count <= 2, $"Expected report_get calls to be capped to 2, got {reportGets.Count}.");

        var iteration2Messages = requests[1].Messages;
        var sawToolUseLimitMessage = iteration2Messages
            .SelectMany(m => m.Content ?? Array.Empty<ContentBlock>())
            .OfType<TextContentBlock>()
            .Select(b => b.Text)
            .Any(t => t != null && t.Contains("Tool-use limit hit", StringComparison.OrdinalIgnoreCase));

        Assert.True(sawToolUseLimitMessage, "Expected the tool-use limit marker message to be carried into the next iteration request.");
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

    private sealed class StructuredToolHistoryRejectingSamplingClient(List<CreateMessageRequestParams> requests) : ISamplingClient
    {
        private const string GeminiPartsFieldError =
            "Google: message must include at least one parts field";

        private readonly Queue<CreateMessageResult> _results = new();
        private readonly List<CreateMessageRequestParams> _requests = requests;

        private bool _rejected;

        public bool IsSamplingSupported => true;

        public bool IsToolUseSupported => true;

        public StructuredToolHistoryRejectingSamplingClient EnqueueResult(CreateMessageResult result)
        {
            _results.Enqueue(result);
            return this;
        }

        public Task<CreateMessageResult> RequestCompletionAsync(CreateMessageRequestParams request, CancellationToken cancellationToken = default)
        {
            _requests.Add(request);

            if (!_rejected && ContainsStructuredToolHistory(request))
            {
                _rejected = true;
                throw new InvalidOperationException(GeminiPartsFieldError);
            }

            if (_results.Count == 0)
            {
                return Task.FromResult(CreateMessageResultWithText("no more scripted responses"));
            }

            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class ToolChoiceRequiredRejectingSamplingClient(List<CreateMessageRequestParams> requests) : ISamplingClient
    {
        private const string OpenRouterToolChoiceError =
            "OpenRouter request failed (404): {\"error\":{\"message\":\"No endpoints found that support the provided 'tool_choice' value.\",\"code\":404}}";

        private readonly Queue<CreateMessageResult> _analysisResults = new();
        private readonly List<CreateMessageRequestParams> _requests = requests;
        private CreateMessageResult? _metaResult;
        private CreateMessageResult? _checkpointResult;
        private CreateMessageResult? _judgeResult;

        private bool _metaRejected;
        private bool _checkpointRejected;
        private bool _judgeRejected;

        public bool IsSamplingSupported => true;

        public bool IsToolUseSupported => true;

        public ToolChoiceRequiredRejectingSamplingClient EnqueueAnalysisResult(CreateMessageResult result)
        {
            _analysisResults.Enqueue(result);
            return this;
        }

        public ToolChoiceRequiredRejectingSamplingClient WithMetaBookkeepingResult(CreateMessageResult result)
        {
            _metaResult = result;
            return this;
        }

        public ToolChoiceRequiredRejectingSamplingClient WithCheckpointResult(CreateMessageResult result)
        {
            _checkpointResult = result;
            return this;
        }

        public ToolChoiceRequiredRejectingSamplingClient WithJudgeResult(CreateMessageResult result)
        {
            _judgeResult = result;
            return this;
        }

        public Task<CreateMessageResult> RequestCompletionAsync(CreateMessageRequestParams request, CancellationToken cancellationToken = default)
        {
            _requests.Add(request);

            if (IsMetaBookkeepingRequest(request))
            {
                if (!_metaRejected && IsRequired(request))
                {
                    _metaRejected = true;
                    throw new InvalidOperationException(OpenRouterToolChoiceError);
                }

                return Task.FromResult(_metaResult ?? CreateMessageResultWithText("no meta result scripted"));
            }

            if (IsCheckpointRequest(request))
            {
                if (!_checkpointRejected && IsRequired(request))
                {
                    _checkpointRejected = true;
                    throw new InvalidOperationException(OpenRouterToolChoiceError);
                }

                return Task.FromResult(_checkpointResult ?? CreateMessageResultWithText("no checkpoint result scripted"));
            }

            if (IsJudgeRequest(request))
            {
                if (!_judgeRejected && IsRequired(request))
                {
                    _judgeRejected = true;
                    throw new InvalidOperationException(OpenRouterToolChoiceError);
                }

                return Task.FromResult(_judgeResult ?? CreateMessageResultWithText("no judge result scripted"));
            }

            if (_analysisResults.Count == 0)
            {
                return Task.FromResult(CreateMessageResultWithText("no more scripted responses"));
            }

            return Task.FromResult(_analysisResults.Dequeue());
        }

        private static bool IsRequired(CreateMessageRequestParams request)
            => string.Equals(request.ToolChoice?.Mode, "required", StringComparison.OrdinalIgnoreCase);

        private static bool IsCheckpointRequest(CreateMessageRequestParams request)
            => request.Tools?.Any(t => string.Equals(t.Name, "checkpoint_complete", StringComparison.OrdinalIgnoreCase)) == true;

        private static bool IsJudgeRequest(CreateMessageRequestParams request)
            => request.Tools?.Any(t => string.Equals(t.Name, "analysis_judge_complete", StringComparison.OrdinalIgnoreCase)) == true;

        private static bool IsMetaBookkeepingRequest(CreateMessageRequestParams request)
        {
            if (request.Tools == null || request.Tools.Count == 0)
            {
                return false;
            }

            var names = request.Tools
                .Select(t => (t.Name ?? string.Empty).Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            if (names.Count == 0)
            {
                return false;
            }

            var isMeta = names.All(n =>
                string.Equals(n, "analysis_evidence_add", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n, "analysis_hypothesis_register", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n, "analysis_hypothesis_score", StringComparison.OrdinalIgnoreCase));

            return isMeta;
        }
    }

    private sealed class CheckpointToolChoiceRequiredRejectingSamplingClient(List<CreateMessageRequestParams> requests) : ISamplingClient
    {
        private const string OpenRouterToolChoiceError =
            "OpenRouter request failed (404): {\"error\":{\"message\":\"No endpoints found that support the provided 'tool_choice' value.\",\"code\":404}}";

        private readonly Queue<CreateMessageResult> _analysisResults = new();
        private readonly List<CreateMessageRequestParams> _requests = requests;
        private CreateMessageResult? _checkpointResult;

        private bool _checkpointRejected;

        public bool IsSamplingSupported => true;

        public bool IsToolUseSupported => true;

        public CheckpointToolChoiceRequiredRejectingSamplingClient EnqueueAnalysisResult(CreateMessageResult result)
        {
            _analysisResults.Enqueue(result);
            return this;
        }

        public CheckpointToolChoiceRequiredRejectingSamplingClient WithCheckpointResult(CreateMessageResult result)
        {
            _checkpointResult = result;
            return this;
        }

        public Task<CreateMessageResult> RequestCompletionAsync(CreateMessageRequestParams request, CancellationToken cancellationToken = default)
        {
            _requests.Add(request);

            if (IsCheckpointRequest(request))
            {
                if (!_checkpointRejected && IsRequired(request))
                {
                    _checkpointRejected = true;
                    throw new InvalidOperationException(OpenRouterToolChoiceError);
                }

                return Task.FromResult(_checkpointResult ?? CreateMessageResultWithText("no checkpoint result scripted"));
            }

            if (IsMetaBookkeepingRequest(request))
            {
                return Task.FromResult(CreateMessageResultWithText("no meta result scripted"));
            }

            if (_analysisResults.Count == 0)
            {
                return Task.FromResult(CreateMessageResultWithText("no more scripted responses"));
            }

            return Task.FromResult(_analysisResults.Dequeue());
        }

        private static bool IsRequired(CreateMessageRequestParams request)
            => string.Equals(request.ToolChoice?.Mode, "required", StringComparison.OrdinalIgnoreCase);

        private static bool IsCheckpointRequest(CreateMessageRequestParams request)
            => request.Tools?.Any(t => string.Equals(t.Name, "checkpoint_complete", StringComparison.OrdinalIgnoreCase)) == true;

        private static bool IsMetaBookkeepingRequest(CreateMessageRequestParams request)
        {
            if (request.Tools == null || request.Tools.Count == 0)
            {
                return false;
            }

            var names = request.Tools
                .Select(t => (t.Name ?? string.Empty).Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            if (names.Count == 0)
            {
                return false;
            }

            var isMeta = names.All(n =>
                string.Equals(n, "analysis_evidence_add", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n, "analysis_hypothesis_register", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n, "analysis_hypothesis_score", StringComparison.OrdinalIgnoreCase));

            return isMeta;
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
