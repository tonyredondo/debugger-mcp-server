#nullable enable

using System.Diagnostics;
using System.Buffers;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DebuggerMcp.Configuration;
using DebuggerMcp.Reporting;
using DebuggerMcp.Sampling;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Orchestrates iterative AI-powered crash analysis using MCP sampling.
/// </summary>
public sealed class AiAnalysisOrchestrator(
    ISamplingClient samplingClient,
    ILogger<AiAnalysisOrchestrator> logger)
{
    private readonly ISamplingClient _samplingClient = samplingClient ?? throw new ArgumentNullException(nameof(samplingClient));
    private readonly ILogger<AiAnalysisOrchestrator> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ToolHistoryModeCache _toolHistoryModeCache = new();
    private readonly CheckpointSynthesisFailureCache _checkpointSynthesisFailureCache = new();

    private LogLevel SamplingTraceLevel => EnableVerboseSamplingTrace ? LogLevel.Information : LogLevel.Debug;

    private const string CheckpointCompleteToolName = "checkpoint_complete";
    private const string AnalysisJudgeCompleteToolName = "analysis_judge_complete";
    private const string AnalysisEvidenceAddToolName = "analysis_evidence_add";
    private const string AnalysisHypothesisRegisterToolName = "analysis_hypothesis_register";
    private const string AnalysisHypothesisScoreToolName = "analysis_hypothesis_score";
    private const int MaxCheckpointJsonChars = 50_000;
    private const int MinHighConfidenceEvidenceItems = 6;

    private static readonly JsonElement CheckpointCompleteSchema = ParseJson("""
        {
          "type": "object",
          "properties": {
            "facts": { "type": "array", "items": { "type": "string" } },
            "hypotheses": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "hypothesis": { "type": "string" },
                  "confidence": { "type": "string", "enum": ["high", "medium", "low", "unknown"] },
                  "evidence": { "type": "array", "items": { "type": "string" } },
                  "unknowns": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["hypothesis", "confidence"]
              }
            },
            "evidence": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "source": { "type": "string" },
                  "finding": { "type": "string" }
                },
                "required": ["id", "source", "finding"]
              }
            },
            "doNotRepeat": { "type": "array", "items": { "type": "string" } },
            "nextSteps": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "tool": { "type": "string" },
                  "call": { "type": "string" },
                  "why": { "type": "string" }
                },
                "required": ["tool", "call", "why"]
              }
            }
          },
          "required": ["facts", "hypotheses", "evidence", "doNotRepeat", "nextSteps"]
        }
        """);

    /// <summary>
    /// Gets or sets the maximum number of sampling iterations to perform.
    /// </summary>
    public int MaxIterations { get; set; } = 120;

    /// <summary>
    /// Gets or sets the maximum number of sampling iterations for the summary rewrite pass.
    /// </summary>
    /// <remarks>
    /// This pass is a polishing step and should converge quickly. It is also bounded separately to reduce
    /// cost and prevent non-converging models/providers from consuming the full analysis iteration budget.
    /// </remarks>
    public int SummaryRewriteMaxIterations { get; set; } = 25;

    /// <summary>
    /// Gets or sets the maximum number of sampling iterations for the thread narrative pass.
    /// </summary>
    /// <remarks>
    /// This pass is intended to synthesize thread activity from existing report evidence. Keeping this bounded
    /// prevents runaway tool-calling loops when a model fails to call the completion tool.
    /// </remarks>
    public int ThreadNarrativeMaxIterations { get; set; } = 25;

    /// <summary>
    /// Gets or sets the maximum number of tool uses executed across the summary rewrite pass.
    /// </summary>
    /// <remarks>
    /// This counts every tool invocation (including cached duplicates). It is a hard stop to prevent models
    /// from looping indefinitely while repeatedly calling tools without producing a completion.
    /// </remarks>
    public int SummaryRewriteMaxTotalToolUses { get; set; } = 150;

    /// <summary>
    /// Gets or sets the maximum number of tool uses executed across the thread narrative pass.
    /// </summary>
    /// <remarks>
    /// This counts every tool invocation (including cached duplicates). It is a hard stop to prevent models
    /// from looping indefinitely while repeatedly calling tools without producing a completion.
    /// </remarks>
    public int ThreadNarrativeMaxTotalToolUses { get; set; } = 150;

    /// <summary>
    /// Gets or sets the maximum number of attempts to retry a single sampling request when the client returns an error
    /// (e.g., transient provider failures or empty responses).
    /// </summary>
    public int MaxSamplingRequestAttempts { get; set; } = 3;

    /// <summary>
    /// Gets or sets the maximum number of output tokens to request per sampling call.
    /// </summary>
    public int MaxTokensPerRequest { get; set; } = 16384;

    /// <summary>
    /// Gets or sets the maximum number of tool calls to execute across all iterations.
    /// </summary>
    public int MaxToolCalls { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum number of tool uses to execute from a single assistant response (iteration),
    /// including cached duplicates.
    /// </summary>
    /// <remarks>
    /// This guards against models/providers getting stuck emitting a single response with hundreds of repeated tool calls,
    /// which can explode the conversation history even when results are cached.
    /// </remarks>
    public int MaxToolUsesPerIteration { get; set; } = 40;

    /// <summary>
    /// Gets or sets the maximum number of consecutive iterations allowed without making progress
    /// (no new unique evidence tool calls and no evidence/hypothesis ledger changes).
    /// </summary>
    /// <remarks>
    /// This guards against models/providers getting stuck in loops that repeatedly request the same tools (often cached)
    /// or emit non-actionable output, consuming the iteration budget without improving the conclusion.
    /// </remarks>
    public int MaxConsecutiveNoProgressIterations { get; set; } = 4;

    /// <summary>
    /// Gets or sets the maximum number of internal meta-tool calls to execute across all iterations.
    /// </summary>
    /// <remarks>
    /// Meta tools are used to keep a stable working memory (evidence ledger and hypotheses) and should not consume
    /// the main tool-call budget intended for evidence-gathering tools.
    /// </remarks>
    public int MaxMetaToolCalls { get; set; } = 200;

    /// <summary>
    /// Number of sampling iterations between internal checkpoint synthesis steps that condense the current findings
    /// into a compact working memory and prune the conversation history. Set to 0 to disable.
    /// </summary>
    public int CheckpointEveryIterations { get; set; } = 4;

    /// <summary>
    /// Maximum output tokens to request for an internal checkpoint synthesis step.
    /// </summary>
    public int CheckpointMaxTokens { get; set; } = 65_000;

    /// <summary>
    /// Maximum output tokens to request for final synthesis steps (when tools are disabled), such as when the
    /// iteration budget or tool-call budget is reached.
    /// </summary>
    /// <remarks>
    /// Set to 0 to reuse <see cref="MaxTokensPerRequest"/>.
    /// </remarks>
    public int FinalSynthesisMaxTokens { get; set; } = 65_000;

    /// <summary>
    /// Gets or sets a value indicating whether to emit verbose sampling trace logs (prompts/messages previews).
    /// </summary>
    /// <remarks>
    /// This is intended for debugging the AI sampling loop. It can produce large logs and may include
    /// sensitive data from debugger outputs. Keep it off unless you explicitly need it.
    /// </remarks>
    public bool EnableVerboseSamplingTrace { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to write full sampling request/response payloads to trace files on disk.
    /// </summary>
    public bool EnableSamplingTraceFiles { get; set; }

    /// <summary>
    /// Gets or sets the root directory for sampling trace files (a unique subdirectory will be created per run).
    /// </summary>
    public string? SamplingTraceFilesRootDirectory { get; set; }

    /// <summary>
    /// Gets or sets an optional label included in the sampling trace directory name (e.g., session and dump id).
    /// </summary>
    public string? SamplingTraceLabel { get; set; }

    /// <summary>
    /// Gets or sets the maximum bytes written per sampling trace file.
    /// </summary>
    public int SamplingTraceMaxFileBytes { get; set; } = 2_000_000;

    /// <summary>
    /// Gets or sets a value indicating whether evidence provenance is enabled.
    /// </summary>
    /// <remarks>
    /// When enabled, the orchestrator auto-records evidence ledger items from tool outputs and restricts
    /// <c>analysis_evidence_add</c> to annotation-only to prevent evidence poisoning.
    /// </remarks>
    public bool EnableEvidenceProvenance { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum characters stored per auto-generated evidence finding.
    /// </summary>
    /// <remarks>
    /// This applies to auto-recorded evidence items derived from tool outputs. The evidence ledger also enforces
    /// its own truncation bounds; this property provides an additional guardrail for large tool outputs.
    /// </remarks>
    public int EvidenceExcerptMaxChars { get; set; } = 2048;

    /// <summary>
    /// Gets a value indicating whether AI analysis is available for the connected client.
    /// </summary>
    public bool IsSamplingAvailable => _samplingClient.IsSamplingSupported;

    /// <summary>
    /// Performs AI-powered crash analysis with an iterative investigation loop.
    /// </summary>
    /// <param name="initialReport">Initial structured crash report.</param>
    /// <param name="fullReportJson">Canonical JSON report document (source of truth for report_get).</param>
    /// <param name="debugger">Debugger manager used to execute commands.</param>
    /// <param name="clrMdAnalyzer">Optional ClrMD analyzer for managed object inspection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The AI analysis result.</returns>
    public async Task<AiAnalysisResult> AnalyzeCrashAsync(
        CrashAnalysisResult initialReport,
        string fullReportJson,
        IDebuggerManager debugger,
        IManagedObjectInspector? clrMdAnalyzer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialReport);
        ArgumentNullException.ThrowIfNull(fullReportJson);
        ArgumentNullException.ThrowIfNull(debugger);

        if (!_samplingClient.IsSamplingSupported)
        {
            return new AiAnalysisResult
            {
                RootCause = "AI analysis unavailable: MCP client does not support sampling.",
                Confidence = "low",
                Reasoning = "The connected MCP client did not advertise sampling capability.",
                Iterations = 0,
                Hypotheses = [],
                CommandsExecuted = []
            };
        }

        if (!_samplingClient.IsToolUseSupported)
        {
            return new AiAnalysisResult
            {
                RootCause = "AI analysis unavailable: MCP client does not support tool use for sampling.",
                Confidence = "low",
                Reasoning = "The connected MCP client advertises sampling but not tools support. This analysis loop requires tools.",
                Iterations = 0,
                Hypotheses = [],
                CommandsExecuted = []
            };
        }

        var commandsExecuted = new List<ExecutedCommand>();
        var messages = new List<SamplingMessage>
        {
            new()
            {
                Role = Role.User,
                Content =
                [
                    new TextContentBlock
                    {
                        Text = BuildInitialPrompt(initialReport, fullReportJson)
                    }
                ]
            }
        };

        var maxIterations = Math.Max(1, MaxIterations);
        var maxTokens = MaxTokensPerRequest > 0 ? MaxTokensPerRequest : 1024;
        var maxToolCalls = MaxToolCalls > 0 ? MaxToolCalls : 50;
        var maxToolUsesPerIteration = MaxToolUsesPerIteration > 0 ? MaxToolUsesPerIteration : 40;
        var maxMetaToolCalls = MaxMetaToolCalls > 0 ? MaxMetaToolCalls : 200;
        var evidenceExcerptMaxChars = Math.Clamp(EvidenceExcerptMaxChars, 64, 50_000);
        var evidenceProvenanceEnabled = EnableEvidenceProvenance;
        var tools = SamplingTools.GetCrashAnalysisTools();
        var baselineKey = ComputeBaselineKey(fullReportJson);

        var evidenceLedger = new AiEvidenceLedger();
        var hypothesisTracker = new AiHypothesisTracker(evidenceLedger);
        var metaToolCallsExecuted = 0;
        var baselineTracker = new BaselineEvidenceTracker();
        var baselineCompleteLogged = false;
        var metaBookkeepingDone = false;
        var judgeAttemptState = new JudgeAttemptState();
        var internalToolChoiceModeCache = new InternalToolChoiceModeCache();
        var optionalBaselineCallScheduler = new OptionalBaselineCallScheduler();
        var evidenceIdByToolKeyHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var consecutiveNoProgressIterations = 0;

        string? lastIterationAssistantText = null;
        string? lastModel = null;
        var toolIndexByIteration = new Dictionary<int, int>();
        var toolResultCache = new Dictionary<string, string>(StringComparer.Ordinal);
        var toolAttemptCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var analysisCompleteRefusals = 0;
        var analysisCompleteValidationRefusals = 0;
        var consecutiveAnalysisCompleteValidationRefusals = 0;
        int? uniqueToolCallCountAtLastValidationRefusal = null;
        var lastCheckpointIteration = 0;
        var commandsExecutedAtLastCheckpoint = 0;
        string? lastCheckpointProgressSignature = null;
        var consecutiveNoProgressCheckpoints = 0;
        string? lastCheckpointHash = null;
        var consecutiveIdenticalCheckpointHashes = 0;
        var lastDeterministicCheckpointIteration = 0;
        string? lastDeterministicCheckpointHash = null;
        string? lastDeterministicCheckpointReason = null;

        var traceRunDir = InitializeSamplingTraceDirectory();

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            var uniqueToolCallsAtIterationStart = toolResultCache.Count;
            var evidenceCountAtIterationStart = evidenceLedger.Items.Count;
            var hypothesisCountAtIterationStart = hypothesisTracker.Hypotheses.Count;
            var checkpointAppliedThisIteration = false;

            if (_toolHistoryModeCache.IsCheckpointOnly && messages.Count > 1 && commandsExecuted.Count > 0)
            {
                var checkpoint = BuildDeterministicCheckpointJson(
                    passName: "analysis",
                    commandsExecuted: commandsExecuted,
                    commandsExecutedAtLastCheckpoint: commandsExecutedAtLastCheckpoint,
                    baselineKey: baselineKey);

                lastCheckpointIteration = iteration - 1;
                commandsExecutedAtLastCheckpoint = commandsExecuted.Count;

                messages.Clear();
                messages.Add(BuildCheckpointCarryForwardMessage(
                    checkpoint,
                    passName: "analysis",
                    stateJson: BuildStateSnapshotJson(evidenceLedger, hypothesisTracker)));
                checkpointAppliedThisIteration = true;
            }

            CreateMessageResult? response = null;
            Exception? lastSamplingError = null;

            for (var attempt = 1; attempt <= Math.Max(1, MaxSamplingRequestAttempts); attempt++)
            {
                var requestMessages = new List<SamplingMessage>(messages);
                var request = new CreateMessageRequestParams
                {
                    SystemPrompt = SystemPrompt,
                    Messages = requestMessages,
                    MaxTokens = maxTokens,
                    Tools = tools,
                    ToolChoice = new ToolChoice { Mode = "auto" }
                };

                try
                {
                    _logger.LogInformation(
                        "[AI] Sampling iteration {Iteration} (attempt {Attempt}/{MaxAttempts})...",
                        iteration,
                        attempt,
                        MaxSamplingRequestAttempts);
                    LogSamplingRequestSummary(iteration, request);
                    var requestFileName = attempt == 1
                        ? $"iter-{iteration:0000}-request.json"
                        : $"iter-{iteration:0000}-attempt-{attempt:00}-request.json";
                    WriteSamplingTraceFile(traceRunDir, requestFileName, BuildTraceRequest(iteration, request));
                    response = await _samplingClient.RequestCompletionAsync(request, cancellationToken).ConfigureAwait(false);

                    if (response.Content != null && response.Content.Count > 0)
                    {
                        break;
                    }

                    lastSamplingError = new InvalidOperationException("The sampling client returned an empty response.");
                    _logger.LogWarning(
                        "[AI] Sampling returned empty content at iteration {Iteration} (attempt {Attempt}/{MaxAttempts})",
                        iteration,
                        attempt,
                        MaxSamplingRequestAttempts);
                    var responseFileName = attempt == 1
                        ? $"iter-{iteration:0000}-response.json"
                        : $"iter-{iteration:0000}-attempt-{attempt:00}-response.json";
                    WriteSamplingTraceFile(traceRunDir, responseFileName, BuildTraceResponse(iteration, response));
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    lastSamplingError = ex;
                    if (IsStructuredToolHistoryUnsupported(ex) && _toolHistoryModeCache.MarkStructuredToolHistoryUnsupported())
                    {
                        _logger.LogInformation(
                            "[AI] Provider rejected structured tool history; switching to checkpoint-only history mode for the remainder of this run (analysis pass).");
                        WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-history-mode-fallback.json", new
                        {
                            passName = "analysis",
                            iteration,
                            mode = "checkpoint-only",
                            reason = "structured_tool_history_unsupported",
                            message = ex.Message
                        });
                    }
                    _logger.LogWarning(ex, "[AI] Sampling failed at iteration {Iteration} (attempt {Attempt}/{MaxAttempts})", iteration, attempt, MaxSamplingRequestAttempts);
                    var errorFileName = attempt == 1
                        ? $"iter-{iteration:0000}-error.json"
                        : $"iter-{iteration:0000}-attempt-{attempt:00}-error.json";
                    WriteSamplingTraceFile(traceRunDir, errorFileName, new { iteration, attempt, error = ex.ToString(), message = ex.Message });
                }

                if (attempt < Math.Max(1, MaxSamplingRequestAttempts) && messages.Count > 2)
                {
                    var fallbackCheckpoint = BuildDeterministicCheckpointJson(
                        passName: "analysis",
                        commandsExecuted: commandsExecuted,
                        commandsExecutedAtLastCheckpoint: commandsExecutedAtLastCheckpoint,
                        baselineKey: baselineKey);
                    lastCheckpointIteration = iteration;
                    commandsExecutedAtLastCheckpoint = commandsExecuted.Count;
                    messages.Clear();
                    messages.Add(BuildCheckpointCarryForwardMessage(
                        fallbackCheckpoint,
                        passName: "analysis",
                        stateJson: BuildStateSnapshotJson(evidenceLedger, hypothesisTracker)));
                }
            }

            if (response == null || response.Content == null || response.Content.Count == 0)
            {
                var samplingFailureResult = await FinalizeAnalysisAfterSamplingFailureAsync(
                        systemPrompt: SystemPrompt,
                        passName: "analysis",
                        commandsExecuted: commandsExecuted,
                        iteration: iteration,
                        lastModel: lastModel,
                        traceRunDir: traceRunDir,
                        failureMessage: lastSamplingError?.Message,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await ApplyJudgeDrivenFinalizationAsync(
                        result: samplingFailureResult,
                        commandsExecuted: commandsExecuted,
                        evidenceLedger: evidenceLedger,
                        hypothesisTracker: hypothesisTracker,
                        internalToolChoiceModeCache: internalToolChoiceModeCache,
                        passName: "analysis",
                        iteration: iteration,
                        maxTokens: maxTokens,
                        lastModel: lastModel,
                        traceRunDir: traceRunDir,
                        judgeAttemptState: judgeAttemptState,
                        finalizationReason: "sampling-failure",
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await EnforceHighConfidenceJudgeAndValidationAsync(
                        result: samplingFailureResult,
                        commandsExecuted: commandsExecuted,
                        evidenceLedger: evidenceLedger,
                        hypothesisTracker: hypothesisTracker,
                        internalToolChoiceModeCache: internalToolChoiceModeCache,
                        passName: "analysis",
                        iteration: iteration,
                        maxTokens: maxTokens,
                        lastModel: lastModel,
                        traceRunDir: traceRunDir,
                        judgeAttemptState: judgeAttemptState,
                        finalizationReason: "sampling-failure",
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                AttachInvestigationState(samplingFailureResult, evidenceLedger, hypothesisTracker);
                WriteSamplingTraceFile(traceRunDir, "final-ai-analysis.json", samplingFailureResult);
                return samplingFailureResult;
            }

            WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-response.json", BuildTraceResponse(iteration, response));

            lastModel = response.Model;
            lastIterationAssistantText = null;
            var textBlocks = response.Content.OfType<TextContentBlock>()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            if (textBlocks.Count > 0)
            {
                lastIterationAssistantText = string.Join("\n", textBlocks).Trim();
            }

            LogSamplingResponseSummary(iteration, response, lastIterationAssistantText);

            // Persist assistant content in the conversation history before executing any tool calls.
            messages.Add(new SamplingMessage
            {
                Role = Role.Assistant,
                Content = response.Content
            });

            // Tool uses requested by the assistant.
            var toolUses = response.Content.OfType<ToolUseContentBlock>().ToList();
            if (toolUses.Count == 0)
            {
                // No tool calls; continue until max iterations. Capture model in the result to aid debugging.
                _logger.Log(SamplingTraceLevel, "[AI] No tool calls in iteration {Iteration}", iteration);
                consecutiveNoProgressIterations++;
                if (consecutiveNoProgressIterations >= Math.Max(1, MaxConsecutiveNoProgressIterations))
                {
                    _logger.LogInformation(
                        "[AI] No progress detected for {Count} consecutive iterations; finalizing analysis early.",
                        consecutiveNoProgressIterations);

                    var final = await FinalizeAnalysisAfterNoProgressDetectedAsync(
                            systemPrompt: SystemPrompt,
                            messages: messages,
                            commandsExecuted: commandsExecuted,
                            iteration: iteration,
                            maxTokens: maxTokens,
                            consecutiveNoProgressIterations: consecutiveNoProgressIterations,
                            uniqueToolCalls: toolResultCache.Count,
                            lastModel: response.Model,
                            traceRunDir: traceRunDir,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    await ApplyJudgeDrivenFinalizationAsync(
                            result: final,
                            commandsExecuted: commandsExecuted,
                            evidenceLedger: evidenceLedger,
                            hypothesisTracker: hypothesisTracker,
                            internalToolChoiceModeCache: internalToolChoiceModeCache,
                            passName: "analysis",
                            iteration: iteration,
                            maxTokens: maxTokens,
                            lastModel: response.Model,
                            traceRunDir: traceRunDir,
                            judgeAttemptState: judgeAttemptState,
                            finalizationReason: "no-progress",
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    await EnforceHighConfidenceJudgeAndValidationAsync(
                            result: final,
                            commandsExecuted: commandsExecuted,
                            evidenceLedger: evidenceLedger,
                            hypothesisTracker: hypothesisTracker,
                            internalToolChoiceModeCache: internalToolChoiceModeCache,
                            passName: "analysis",
                            iteration: iteration,
                            maxTokens: maxTokens,
                            lastModel: response.Model,
                            traceRunDir: traceRunDir,
                            judgeAttemptState: judgeAttemptState,
                            finalizationReason: "no-progress",
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    AttachInvestigationState(final, evidenceLedger, hypothesisTracker);
                    WriteSamplingTraceFile(traceRunDir, "final-ai-analysis.json", final);
                    return final;
                }
                continue;
            }

            ToolUseContentBlock? pendingAnalysisComplete = null;
            JsonElement pendingAnalysisCompleteInput = default;
            string? pendingAnalysisCompleteName = null;
            string? pendingAnalysisCompleteId = null;

            var rewrittenToolUses = new List<(ToolUseContentBlock ToolUse, string Name, JsonElement Input)>(toolUses.Count);
	            foreach (var toolUse in toolUses)
	            {
	                var (effectiveToolName, effectiveToolInput) = RewriteToolUseIfNeeded(toolUse, clrMdAnalyzer);
	                rewrittenToolUses.Add((toolUse, effectiveToolName, effectiveToolInput));
	            }

	            var respondedToolUseIds = new HashSet<string>(StringComparer.Ordinal);
	            var toolUsesExecutedThisIteration = 0;
	            var toolUsesPrunedDueToLimit = false;

	            foreach (var (toolUse, effectiveToolName, effectiveToolInput) in rewrittenToolUses)
	            {
	                LogRequestedToolSummary(iteration, toolUse, effectiveToolName, effectiveToolInput);

	                if (string.Equals(effectiveToolName, "analysis_complete", StringComparison.OrdinalIgnoreCase))
                {
                    if (pendingAnalysisComplete == null)
                    {
                        pendingAnalysisComplete = toolUse;
                        pendingAnalysisCompleteInput = effectiveToolInput;
                        pendingAnalysisCompleteName = effectiveToolName;
                        pendingAnalysisCompleteId = toolUse.Id;
	                    }
	                    continue;
	                }

	                if (toolUsesExecutedThisIteration >= maxToolUsesPerIteration)
	                {
	                    toolUsesPrunedDueToLimit = true;
	                    _logger.LogWarning(
	                        "[AI] Tool-use limit reached in iteration {Iteration} (limit={Limit}); pruning remaining tool calls.",
	                        iteration,
	                        maxToolUsesPerIteration);
	                    break;
	                }

	                if (IsInternalMetaTool(effectiveToolName))
	                {
	                    var metaSw = Stopwatch.StartNew();
	                    var metaToolName = effectiveToolName ?? string.Empty;
	                    var metaToolInput = CloneToolInput(effectiveToolInput);
                    var metaToolUseId = toolUse.Id;

                    if (string.IsNullOrWhiteSpace(metaToolUseId))
                    {
                        metaSw.Stop();
                        var msg = $"Tool call '{metaToolName}' is missing required id; cannot execute. Please call the tool again with a valid id.";
	                        messages.Add(new SamplingMessage
	                        {
	                            Role = Role.User,
	                            Content =
	                            [
	                                new TextContentBlock { Text = msg }
	                            ]
	                        });
	                        toolUsesExecutedThisIteration++;
	                        continue;
	                    }

                    string outputForModel;
                    if (metaToolCallsExecuted >= maxMetaToolCalls)
                    {
                        metaSw.Stop();
                        outputForModel = TruncateForModel(
                            JsonSerializer.Serialize(new
                            {
                                ignored = true,
                                reason = "meta_tool_budget_exceeded",
                                maxMetaToolCalls,
                                tool = metaToolName
                            }));
                    }
                    else
                    {
                        var output = ExecuteInternalMetaTool(metaToolName, metaToolInput, evidenceLedger, hypothesisTracker, evidenceProvenanceEnabled);
                        metaToolCallsExecuted++;
                        metaSw.Stop();
                        outputForModel = TruncateForModel(output);
                    }

                    commandsExecuted.Add(new ExecutedCommand
                    {
                        Tool = metaToolName,
                        Input = metaToolInput,
                        Output = outputForModel,
                        Iteration = iteration,
                        Duration = metaSw.Elapsed.ToString("c")
                    });

	                    messages.Add(new SamplingMessage
	                    {
	                        Role = Role.User,
	                        Content =
	                        [
                            new ToolResultContentBlock
                            {
                                ToolUseId = metaToolUseId,
                                IsError = false,
                                Content =
                                [
                                    new TextContentBlock { Text = outputForModel }
                                ]
                            }
	                        ]
	                    });
	                    respondedToolUseIds.Add(metaToolUseId);
	                    toolUsesExecutedThisIteration++;
	                    continue;
	                }

                if (toolResultCache.Count >= maxToolCalls)
                {
                    _logger.LogInformation("[AI] Unique tool call budget reached ({MaxToolCalls}); finalizing analysis.", maxToolCalls);
                    PruneUnrespondedToolUsesFromLastAssistantMessage(messages, respondedToolUseIds);
                    var final = await FinalizeAnalysisAfterToolBudgetExceededAsync(
                            systemPrompt: SystemPrompt,
                            messages: messages,
                            commandsExecuted: commandsExecuted,
                            iteration: iteration,
                            maxTokens: maxTokens,
                            maxToolCalls: maxToolCalls,
                            lastModel: response.Model,
                            traceRunDir: traceRunDir,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    await ApplyJudgeDrivenFinalizationAsync(
                            result: final,
                            commandsExecuted: commandsExecuted,
                            evidenceLedger: evidenceLedger,
                            hypothesisTracker: hypothesisTracker,
                            internalToolChoiceModeCache: internalToolChoiceModeCache,
                            passName: "analysis",
                            iteration: iteration,
                            maxTokens: maxTokens,
                            lastModel: response.Model,
                            traceRunDir: traceRunDir,
                            judgeAttemptState: judgeAttemptState,
                            finalizationReason: "tool-budget",
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    await EnforceHighConfidenceJudgeAndValidationAsync(
                            result: final,
                            commandsExecuted: commandsExecuted,
                            evidenceLedger: evidenceLedger,
                            hypothesisTracker: hypothesisTracker,
                            internalToolChoiceModeCache: internalToolChoiceModeCache,
                            passName: "analysis",
                            iteration: iteration,
                            maxTokens: maxTokens,
                            lastModel: response.Model,
                            traceRunDir: traceRunDir,
                            judgeAttemptState: judgeAttemptState,
                            finalizationReason: "tool-budget",
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    AttachInvestigationState(final, evidenceLedger, hypothesisTracker);
                    WriteSamplingTraceFile(traceRunDir, "final-ai-analysis.json", final);
                    return final;
                }

                var sw = Stopwatch.StartNew();
                var toolName = effectiveToolName ?? string.Empty;
                var toolInput = CloneToolInput(effectiveToolInput);
                var toolCacheKey = BuildToolCacheKey(toolName, toolInput);
                var toolUseId = toolUse.Id;
	                if (string.IsNullOrWhiteSpace(toolUseId))
	                {
	                    // Tool results require a tool_use_id. If missing, respond with plain text so the model can retry.
	                    sw.Stop();
	                    var msg = $"Tool call '{toolName}' is missing required id; cannot execute. Please call the tool again with a valid id.";

                    commandsExecuted.Add(new ExecutedCommand
                    {
                        Tool = toolName,
                        Input = toolInput,
                        Output = msg,
                        Iteration = iteration,
                        Duration = sw.Elapsed.ToString("c")
                    });

	                    messages.Add(new SamplingMessage
	                    {
	                        Role = Role.User,
	                        Content =
	                        [
	                            new TextContentBlock { Text = msg }
	                        ]
	                    });
	                    toolUsesExecutedThisIteration++;
	                    continue;
	                }

	                try
	                {
	                    string outputForModel;
	                    string outputForLog;
	                    var wasCached = false;
	                    var duration = sw.Elapsed;
	                    if (toolResultCache.TryGetValue(toolCacheKey, out var cached))
	                    {
	                        sw.Stop();
	                        duration = TimeSpan.Zero;
	                        wasCached = true;
	                        outputForModel = cached;
	                        outputForLog = outputForModel;
	                    }
	                    else
	                    {
                            if (!string.IsNullOrWhiteSpace(toolCacheKey))
                            {
                                toolAttemptCounts[toolCacheKey] = toolAttemptCounts.TryGetValue(toolCacheKey, out var attempts) ? attempts + 1 : 1;
                            }

	                        var output = await ExecuteToolAsync(toolName, toolInput, fullReportJson, initialReport, debugger, clrMdAnalyzer, cancellationToken)
	                            .ConfigureAwait(false);
                        outputForModel = TruncateForModel(output);
                        toolResultCache[toolCacheKey] = outputForModel;
                        sw.Stop();
                        duration = sw.Elapsed;
                        outputForLog = outputForModel;
                    }

                    if (toolName.Equals("report_get", StringComparison.OrdinalIgnoreCase))
                    {
                        baselineTracker.ObserveReportGet(toolInput, outputForModel);
                        if (!baselineCompleteLogged && baselineTracker.IsComplete)
                        {
                            baselineCompleteLogged = true;
                            _logger.LogInformation("[AI] Baseline evidence completed at iteration {Iteration}.", iteration);
                        }
                    }

                    AutoRecordEvidenceFromToolResult(
                        evidenceLedger: evidenceLedger,
                        evidenceIdByToolKeyHash: evidenceIdByToolKeyHash,
                        toolName: toolName,
                        toolInput: toolInput,
                        toolCacheKey: toolCacheKey,
                        outputForModel: outputForModel,
                        wasCached: wasCached,
                        toolWasError: false,
                        includeProvenanceMetadata: evidenceProvenanceEnabled,
                        evidenceExcerptMaxChars: evidenceExcerptMaxChars);

                    var toolOrdinal = toolIndexByIteration.TryGetValue(iteration, out var n) ? n + 1 : 1;
                    toolIndexByIteration[iteration] = toolOrdinal;
	                    commandsExecuted.Add(new ExecutedCommand
	                    {
	                        Tool = toolName,
	                        Input = toolInput,
	                        Output = outputForLog,
	                        Iteration = iteration,
	                        Duration = duration.ToString("c"),
	                        Cached = wasCached ? true : null
	                    });
                    WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-tool-{toolOrdinal:00}-{SanitizeFileComponent(toolName)}.json",
                        new { tool = toolName, input = toolInput.ToString(), output = outputForLog, cached = wasCached, isError = false, duration = duration.ToString("c") });

	                    messages.Add(new SamplingMessage
	                    {
	                        Role = Role.User,
	                        Content =
                        [
                            new ToolResultContentBlock
                            {
                                ToolUseId = toolUseId,
                                IsError = false,
                                Content =
                                [
                                    new TextContentBlock
                                    {
                                        Text = outputForModel
                                    }
                                ]
                            }
	                        ]
	                    });
	                    respondedToolUseIds.Add(toolUseId);
	                    toolUsesExecutedThisIteration++;
	                }
	                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
	                {
	                    throw;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    var message = ex.Message;
                    _logger.LogWarning(ex, "[AI] Tool execution failed: {Tool}", toolName);
                    var isTransient = IsTransientToolException(ex);
                    var attempt = 1;
                    if (!string.IsNullOrWhiteSpace(toolCacheKey) && toolAttemptCounts.TryGetValue(toolCacheKey, out var attempts))
                    {
                        attempt = attempts;
                    }

                    var messageForModel = TruncateForModel(isTransient
                        ? BuildTransientToolErrorPayload(message, attempt, TransientRetryMaxAttempts)
                        : message);
                    var shouldCache = !isTransient || attempt >= TransientRetryMaxAttempts;
                    if (shouldCache && !string.IsNullOrWhiteSpace(toolCacheKey))
                    {
                        toolResultCache[toolCacheKey] = messageForModel;
                    }

                    AutoRecordEvidenceFromToolResult(
                        evidenceLedger: evidenceLedger,
                        evidenceIdByToolKeyHash: evidenceIdByToolKeyHash,
                        toolName: toolName,
                        toolInput: toolInput,
                        toolCacheKey: toolCacheKey,
                        outputForModel: messageForModel,
                        wasCached: false,
                        toolWasError: true,
                        includeProvenanceMetadata: evidenceProvenanceEnabled,
                        evidenceExcerptMaxChars: evidenceExcerptMaxChars);

                    var toolOrdinal = toolIndexByIteration.TryGetValue(iteration, out var n) ? n + 1 : 1;
                    toolIndexByIteration[iteration] = toolOrdinal;
                    commandsExecuted.Add(new ExecutedCommand
                    {
                        Tool = toolName,
                        Input = toolInput,
                        Output = messageForModel,
                        Iteration = iteration,
                        Duration = sw.Elapsed.ToString("c")
                    });
                    WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-tool-{toolOrdinal:00}-{SanitizeFileComponent(toolName)}.json",
                        new { tool = toolName, input = toolInput.ToString(), output = messageForModel, isError = true, duration = sw.Elapsed.ToString("c") });

	                    messages.Add(new SamplingMessage
	                    {
	                        Role = Role.User,
	                        Content =
                        [
                            new ToolResultContentBlock
                            {
                                ToolUseId = toolUseId,
                                IsError = true,
                                Content =
                                [
                                    new TextContentBlock
                                    {
                                        Text = messageForModel
                                    }
                                ]
                            }
	                        ]
	                    });
	                    respondedToolUseIds.Add(toolUseId);
	                    toolUsesExecutedThisIteration++;
	                }
	            }

	            if (toolUsesPrunedDueToLimit)
	            {
	                // If the model emitted an excessive number of tool calls, ignore any co-issued analysis_complete attempt.
	                pendingAnalysisComplete = null;
	                pendingAnalysisCompleteInput = default;
	                pendingAnalysisCompleteName = null;
	                pendingAnalysisCompleteId = null;

	                PruneUnrespondedToolUsesFromLastAssistantMessage(messages, respondedToolUseIds);
	                messages.Add(new SamplingMessage
	                {
	                    Role = Role.User,
	                    Content =
	                    [
	                        new TextContentBlock
	                        {
	                            Text =
	                                $"INTERNAL: Tool-use limit hit in iteration {iteration}. " +
	                                $"Executed {respondedToolUseIds.Count} tool calls and pruned the rest. " +
	                                "Do not repeat identical tool calls; use prior results and proceed with reasoning."
	                        }
	                    ]
	                });
	            }

	            if (pendingAnalysisComplete != null)
	            {
                var otherToolsCoissued = rewrittenToolUses.Any(t =>
                    !string.Equals(t.Name, "analysis_complete", StringComparison.OrdinalIgnoreCase)
                    && !IsInternalMetaTool(t.Name));

                if (!HasEvidenceToolCalls(commandsExecuted) || otherToolsCoissued)
                {
                    analysisCompleteRefusals++;

                    var msg = BuildAnalysisCompleteRefusalMessage(otherToolsCoissued, analysisCompleteRefusals);
                    var toolName = pendingAnalysisCompleteName ?? "analysis_complete";
                    var toolUseId = pendingAnalysisCompleteId;

                    var toolOrdinal = toolIndexByIteration.TryGetValue(iteration, out var n) ? n + 1 : 1;
                    toolIndexByIteration[iteration] = toolOrdinal;
                    WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-tool-{toolOrdinal:00}-{SanitizeFileComponent(toolName)}.json",
                        new { tool = toolName, input = CloneToolInput(pendingAnalysisCompleteInput).ToString(), output = msg, isError = true, duration = TimeSpan.Zero.ToString("c") });

                    if (string.IsNullOrWhiteSpace(toolUseId))
                    {
                        messages.Add(new SamplingMessage
                        {
                            Role = Role.User,
                            Content =
                            [
                                new TextContentBlock { Text = msg }
                            ]
                        });
                        continue;
                    }

                    messages.Add(new SamplingMessage
                    {
                        Role = Role.User,
                        Content =
                        [
                            new ToolResultContentBlock
                            {
                                ToolUseId = toolUseId,
                                IsError = true,
                                Content =
                                [
                                    new TextContentBlock
                                    {
                                        Text = TruncateForModel(msg)
                                    }
                                ]
                            }
                        ]
                    });
                    if (!string.IsNullOrWhiteSpace(toolUseId))
                    {
                        respondedToolUseIds.Add(toolUseId);
                    }
                    continue;
                }

                var completed = ParseAnalysisComplete(pendingAnalysisCompleteInput, commandsExecuted, iteration, response.Model);
                completed.AnalyzedAt = DateTime.UtcNow;

                var completedConfidence = (completed.Confidence ?? string.Empty).Trim();
                if (string.Equals(completedConfidence, "high", StringComparison.OrdinalIgnoreCase)
                    && (completed.Evidence?.Count ?? 0) >= MinHighConfidenceEvidenceItems
                    && HasNonReportEvidenceToolCalls(commandsExecuted)
                    && hypothesisTracker.Hypotheses.Count >= 3
                    && evidenceLedger.Items.Count > 0
                    && !judgeAttemptState.Attempted)
                {
                    judgeAttemptState.Attempted = true;
                    completed.Judge ??= await TryRunJudgeStepAsync(
                            passName: "analysis",
                            systemPrompt: JudgeSystemPrompt,
                            evidenceLedger: evidenceLedger,
                            hypothesisTracker: hypothesisTracker,
                            internalToolChoiceModeCache: internalToolChoiceModeCache,
                            iteration: iteration,
                            maxTokens: maxTokens,
                            lastModel: response.Model,
                            traceRunDir: traceRunDir,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }

                if (TryGetAnalysisCompleteValidationError(completed, commandsExecuted, evidenceLedger, hypothesisTracker, out var validationError))
                {
                    analysisCompleteRefusals++;
                    analysisCompleteValidationRefusals++;

                    // Treat repeated cached tool calls as "no new evidence" for the purpose of breaking out of
                    // analysis_complete refusal loops (some providers will retry the same tools endlessly).
                    if (uniqueToolCallCountAtLastValidationRefusal.HasValue
                        && uniqueToolCallCountAtLastValidationRefusal.Value == toolResultCache.Count)
                    {
                        consecutiveAnalysisCompleteValidationRefusals++;
                    }
                    else
                    {
                        consecutiveAnalysisCompleteValidationRefusals = 1;
                        uniqueToolCallCountAtLastValidationRefusal = toolResultCache.Count;
                    }

                    if (consecutiveAnalysisCompleteValidationRefusals >= 2)
                    {
                        var repaired = TryRepairAnalysisCompleteAfterValidationFailure(
                            completed,
                            commandsExecuted,
                            validationError,
                            consecutiveAnalysisCompleteValidationRefusals);

                        if (repaired != null)
                        {
                            await ApplyJudgeDrivenFinalizationAsync(
                                    result: repaired,
                                    commandsExecuted: commandsExecuted,
                                    evidenceLedger: evidenceLedger,
                                    hypothesisTracker: hypothesisTracker,
                                    internalToolChoiceModeCache: internalToolChoiceModeCache,
                                    passName: "analysis",
                                    iteration: iteration,
                                    maxTokens: maxTokens,
                                    lastModel: response.Model,
                                    traceRunDir: traceRunDir,
                                    judgeAttemptState: judgeAttemptState,
                                    finalizationReason: "analysis-complete-auto-repair",
                                    cancellationToken: cancellationToken)
                                .ConfigureAwait(false);
                            await EnforceHighConfidenceJudgeAndValidationAsync(
                                    result: repaired,
                                    commandsExecuted: commandsExecuted,
                                    evidenceLedger: evidenceLedger,
                                    hypothesisTracker: hypothesisTracker,
                                    internalToolChoiceModeCache: internalToolChoiceModeCache,
                                    passName: "analysis",
                                    iteration: iteration,
                                    maxTokens: maxTokens,
                                    lastModel: response.Model,
                                    traceRunDir: traceRunDir,
                                    judgeAttemptState: judgeAttemptState,
                                    finalizationReason: "analysis-complete-auto-repair",
                                    cancellationToken: cancellationToken)
                                .ConfigureAwait(false);
                            AttachInvestigationState(repaired, evidenceLedger, hypothesisTracker);
                            WriteSamplingTraceFile(traceRunDir, "final-ai-analysis.json", repaired);
                            return repaired;
                        }
                    }

                    var msg = BuildAnalysisCompleteValidationRefusalMessage(validationError, analysisCompleteRefusals);
                    var toolName = pendingAnalysisCompleteName ?? "analysis_complete";
                    var toolUseId = pendingAnalysisCompleteId;

                    var toolOrdinal = toolIndexByIteration.TryGetValue(iteration, out var n) ? n + 1 : 1;
                    toolIndexByIteration[iteration] = toolOrdinal;
                    WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-tool-{toolOrdinal:00}-{SanitizeFileComponent(toolName)}.json",
                        new { tool = toolName, input = CloneToolInput(pendingAnalysisCompleteInput).ToString(), output = msg, isError = true, duration = TimeSpan.Zero.ToString("c") });

                    if (string.IsNullOrWhiteSpace(toolUseId))
                    {
                        messages.Add(new SamplingMessage
                        {
                            Role = Role.User,
                            Content =
                            [
                                new TextContentBlock { Text = msg }
                            ]
                        });
                        continue;
                    }

                    messages.Add(new SamplingMessage
                    {
                        Role = Role.User,
                        Content =
                        [
                            new ToolResultContentBlock
                            {
                                ToolUseId = toolUseId,
                                IsError = true,
                                Content =
                                [
                                    new TextContentBlock
                                    {
                                        Text = TruncateForModel(msg)
                                    }
                                ]
                            }
                        ]
                    });
                    respondedToolUseIds.Add(toolUseId);
                    continue;
                }

                await ApplyJudgeDrivenFinalizationAsync(
                        result: completed,
                        commandsExecuted: commandsExecuted,
                        evidenceLedger: evidenceLedger,
                        hypothesisTracker: hypothesisTracker,
                        internalToolChoiceModeCache: internalToolChoiceModeCache,
                        passName: "analysis",
                        iteration: iteration,
                        maxTokens: maxTokens,
                        lastModel: response.Model,
                        traceRunDir: traceRunDir,
                        judgeAttemptState: judgeAttemptState,
                        finalizationReason: "analysis-complete",
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await EnforceHighConfidenceJudgeAndValidationAsync(
                        result: completed,
                        commandsExecuted: commandsExecuted,
                        evidenceLedger: evidenceLedger,
                        hypothesisTracker: hypothesisTracker,
                        internalToolChoiceModeCache: internalToolChoiceModeCache,
                        passName: "analysis",
                        iteration: iteration,
                        maxTokens: maxTokens,
                        lastModel: response.Model,
                        traceRunDir: traceRunDir,
                        judgeAttemptState: judgeAttemptState,
                        finalizationReason: "analysis-complete",
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                AttachInvestigationState(completed, evidenceLedger, hypothesisTracker);
                WriteSamplingTraceFile(traceRunDir, "final-ai-analysis.json", completed);
                return completed;
            }

            if (!metaBookkeepingDone && baselineTracker.IsComplete)
            {
                var metaBookkeepingResult = await TryRunMetaBookkeepingAsync(
                        passName: "analysis",
                        systemPrompt: SystemPrompt,
                        messages: messages,
                        evidenceLedger: evidenceLedger,
                        hypothesisTracker: hypothesisTracker,
                        metaToolCallsExecuted: metaToolCallsExecuted,
                        maxMetaToolCalls: maxMetaToolCalls,
                        iteration: iteration,
                        maxTokens: maxTokens,
                        traceRunDir: traceRunDir,
                        internalToolChoiceModeCache: internalToolChoiceModeCache,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                metaToolCallsExecuted = metaBookkeepingResult.MetaToolCallsExecuted;
                metaBookkeepingDone = metaBookkeepingResult.Done;
            }

            var shouldCheckpointByInterval = ShouldCreateCheckpoint(
                iteration: iteration,
                maxIterations: maxIterations,
                checkpointEveryIterations: CheckpointEveryIterations,
                lastCheckpointIteration: lastCheckpointIteration,
                commandsExecuted: commandsExecuted,
                commandsExecutedAtLastCheckpoint: commandsExecutedAtLastCheckpoint);

            var checkpointProgressSignature = BuildCheckpointProgressSignature(
                uniqueToolCalls: toolResultCache.Count,
                evidenceCount: evidenceLedger.Items.Count,
                hypothesisCount: hypothesisTracker.Hypotheses.Count);

            var noProgressSinceLastCheckpoint = !string.IsNullOrWhiteSpace(lastCheckpointProgressSignature)
                                                && string.Equals(lastCheckpointProgressSignature, checkpointProgressSignature, StringComparison.OrdinalIgnoreCase);

            var baselinePhase = ComputeBaselinePhaseState(commandsExecuted);
            var baselineBlocked = !baselineTracker.IsComplete && IsBaselineBlocked(iteration, commandsExecuted, baselinePhase.Missing);

            var shouldInjectDeterministic = TryGetDeterministicCheckpointInjectionReason(
                iteration: iteration,
                commandsExecuted: commandsExecuted,
                baselineComplete: baselineTracker.IsComplete,
                consecutiveNoProgressCheckpoints: consecutiveNoProgressCheckpoints,
                consecutiveIdenticalCheckpointHashes: consecutiveIdenticalCheckpointHashes,
                noProgressSinceLastCheckpoint: noProgressSinceLastCheckpoint,
                reason: out var deterministicReason);

            if (baselineBlocked)
            {
                shouldInjectDeterministic = true;
                deterministicReason = "baseline_blocked";
            }

            if (iteration < maxIterations
                && (baselineTracker.IsComplete || baselineBlocked)
                && commandsExecuted.Count > commandsExecutedAtLastCheckpoint
                && (shouldCheckpointByInterval || shouldInjectDeterministic))
            {
                var allowDeterministicInjection = shouldInjectDeterministic
                    && (baselineBlocked
                        || iteration - lastDeterministicCheckpointIteration >= 2
                        || !string.Equals(lastDeterministicCheckpointReason, deterministicReason, StringComparison.OrdinalIgnoreCase));

                string checkpoint;
                if (allowDeterministicInjection)
                {
                    checkpoint = BuildDeterministicLoopBreakCheckpointJson(
                        passName: "analysis",
                        commandsExecuted: commandsExecuted,
                        commandsExecutedAtLastCheckpoint: commandsExecutedAtLastCheckpoint,
                        baselineKey: baselineKey,
                        reason: deterministicReason,
                        evidenceLedger: evidenceLedger,
                        hypothesisTracker: hypothesisTracker,
                        forceFinalizeNow: baselineBlocked);

                    var hash = ComputeSha256Prefixed(checkpoint);
                    if (!string.IsNullOrWhiteSpace(lastDeterministicCheckpointHash)
                        && string.Equals(lastDeterministicCheckpointHash, hash, StringComparison.OrdinalIgnoreCase))
                    {
                        checkpoint = BuildDeterministicLoopBreakCheckpointJson(
                            passName: "analysis",
                            commandsExecuted: commandsExecuted,
                            commandsExecutedAtLastCheckpoint: commandsExecutedAtLastCheckpoint,
                            baselineKey: baselineKey,
                            reason: $"{deterministicReason}:repeat",
                            evidenceLedger: evidenceLedger,
                            hypothesisTracker: hypothesisTracker,
                            forceFinalizeNow: true);
                        hash = ComputeSha256Prefixed(checkpoint);
                    }

                    lastDeterministicCheckpointIteration = iteration;
                    lastDeterministicCheckpointHash = hash;
                    lastDeterministicCheckpointReason = deterministicReason;
                }
	                else
	                {
	                    checkpoint = await TryCreateCheckpointAsync(
	                            passName: "analysis",
	                            systemPrompt: SystemPrompt,
	                            messages: messages,
	                            commandsExecuted: commandsExecuted,
	                            commandsExecutedAtLastCheckpoint: commandsExecutedAtLastCheckpoint,
                            iteration: iteration,
	                            maxTokens: CheckpointMaxTokens,
	                            traceRunDir: traceRunDir,
	                            internalToolChoiceModeCache: internalToolChoiceModeCache,
	                            cancellationToken: cancellationToken)
	                        .ConfigureAwait(false) ?? string.Empty;

	                    if (string.IsNullOrWhiteSpace(checkpoint))
	                    {
	                        checkpoint = BuildDeterministicCheckpointJson(
	                            passName: "analysis",
                            commandsExecuted: commandsExecuted,
                            commandsExecutedAtLastCheckpoint: commandsExecutedAtLastCheckpoint,
                            baselineKey: baselineKey);
                    }
                    else
                    {
                        checkpoint = NormalizeCheckpointJson(checkpoint, commandsExecuted, baselineKey);
                    }

                    checkpoint = AugmentCheckpointWithConvergenceFacts(checkpoint, evidenceLedger, hypothesisTracker);
                }

                checkpoint = AugmentCheckpointWithOptionalBaselineScheduling(
                    checkpointJson: checkpoint,
                    baselineKey: baselineKey,
                    baselineComplete: baselineTracker.IsComplete,
                    baselineBlocked: baselineBlocked,
                    commandsExecuted: commandsExecuted,
                    scheduler: optionalBaselineCallScheduler);

                lastCheckpointIteration = iteration;
                commandsExecutedAtLastCheckpoint = commandsExecuted.Count;
                consecutiveNoProgressCheckpoints = noProgressSinceLastCheckpoint ? consecutiveNoProgressCheckpoints + 1 : 0;
                var checkpointHash = ComputeSha256Prefixed(checkpoint);
                consecutiveIdenticalCheckpointHashes = !string.IsNullOrWhiteSpace(lastCheckpointHash)
                                                       && string.Equals(lastCheckpointHash, checkpointHash, StringComparison.OrdinalIgnoreCase)
                    ? consecutiveIdenticalCheckpointHashes + 1
                    : 0;
                lastCheckpointHash = checkpointHash;

	                messages.Clear();
	                ApplyCheckpointToStateStores(
	                    checkpoint,
	                    evidenceLedger,
	                    hypothesisTracker,
	                    enableEvidenceProvenance: evidenceProvenanceEnabled);
                    lastCheckpointProgressSignature = BuildCheckpointProgressSignature(
                        uniqueToolCalls: toolResultCache.Count,
                        evidenceCount: evidenceLedger.Items.Count,
                        hypothesisCount: hypothesisTracker.Hypotheses.Count);
	                messages.Add(BuildCheckpointCarryForwardMessage(
	                    checkpoint,
	                    passName: "analysis",
                    stateJson: BuildStateSnapshotJson(evidenceLedger, hypothesisTracker)));
                    checkpointAppliedThisIteration = true;
	            }

            var madeProgress = checkpointAppliedThisIteration
                               || toolResultCache.Count != uniqueToolCallsAtIterationStart
                               || evidenceLedger.Items.Count != evidenceCountAtIterationStart
                               || hypothesisTracker.Hypotheses.Count != hypothesisCountAtIterationStart;

            if (madeProgress)
            {
                consecutiveNoProgressIterations = 0;
            }
            else
            {
                consecutiveNoProgressIterations++;
                if (consecutiveNoProgressIterations >= Math.Max(1, MaxConsecutiveNoProgressIterations))
                {
                    _logger.LogInformation(
                        "[AI] No progress detected for {Count} consecutive iterations; finalizing analysis early.",
                        consecutiveNoProgressIterations);

                    var final = await FinalizeAnalysisAfterNoProgressDetectedAsync(
                            systemPrompt: SystemPrompt,
                            messages: messages,
                            commandsExecuted: commandsExecuted,
                            iteration: iteration,
                            maxTokens: maxTokens,
                            consecutiveNoProgressIterations: consecutiveNoProgressIterations,
                            uniqueToolCalls: toolResultCache.Count,
                            lastModel: response.Model,
                            traceRunDir: traceRunDir,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    await ApplyJudgeDrivenFinalizationAsync(
                            result: final,
                            commandsExecuted: commandsExecuted,
                            evidenceLedger: evidenceLedger,
                            hypothesisTracker: hypothesisTracker,
                            internalToolChoiceModeCache: internalToolChoiceModeCache,
                            passName: "analysis",
                            iteration: iteration,
                            maxTokens: maxTokens,
                            lastModel: response.Model,
                            traceRunDir: traceRunDir,
                            judgeAttemptState: judgeAttemptState,
                            finalizationReason: "no-progress",
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    await EnforceHighConfidenceJudgeAndValidationAsync(
                            result: final,
                            commandsExecuted: commandsExecuted,
                            evidenceLedger: evidenceLedger,
                            hypothesisTracker: hypothesisTracker,
                            internalToolChoiceModeCache: internalToolChoiceModeCache,
                            passName: "analysis",
                            iteration: iteration,
                            maxTokens: maxTokens,
                            lastModel: response.Model,
                            traceRunDir: traceRunDir,
                            judgeAttemptState: judgeAttemptState,
                            finalizationReason: "no-progress",
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    AttachInvestigationState(final, evidenceLedger, hypothesisTracker);
                    WriteSamplingTraceFile(traceRunDir, "final-ai-analysis.json", final);
                    return final;
                }
            }
        }

        // Add one final synthesis iteration when the iteration budget is reached.
        // This asks the model to conclude based on already collected evidence without requesting any more tools.
        var synthesized = await FinalizeAnalysisAfterMaxIterationsReachedAsync(
                systemPrompt: SystemPrompt,
                messages: messages,
                commandsExecuted: commandsExecuted,
                iteration: maxIterations + 1,
                maxTokens: maxTokens,
                maxIterations: maxIterations,
                lastModel: lastModel,
                traceRunDir: traceRunDir,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await ApplyJudgeDrivenFinalizationAsync(
                result: synthesized,
                commandsExecuted: commandsExecuted,
                evidenceLedger: evidenceLedger,
                hypothesisTracker: hypothesisTracker,
                internalToolChoiceModeCache: internalToolChoiceModeCache,
                passName: "analysis",
                iteration: maxIterations + 1,
                maxTokens: maxTokens,
                lastModel: lastModel,
                traceRunDir: traceRunDir,
                judgeAttemptState: judgeAttemptState,
                finalizationReason: "iteration-budget",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await EnforceHighConfidenceJudgeAndValidationAsync(
                result: synthesized,
                commandsExecuted: commandsExecuted,
                evidenceLedger: evidenceLedger,
                hypothesisTracker: hypothesisTracker,
                internalToolChoiceModeCache: internalToolChoiceModeCache,
                passName: "analysis",
                iteration: maxIterations + 1,
                maxTokens: maxTokens,
                lastModel: lastModel,
                traceRunDir: traceRunDir,
                judgeAttemptState: judgeAttemptState,
                finalizationReason: "iteration-budget",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AttachInvestigationState(synthesized, evidenceLedger, hypothesisTracker);
        WriteSamplingTraceFile(traceRunDir, "final-ai-analysis.json", synthesized);
        return synthesized;
    }

    private static bool TryParseReportGetResponseHasError(string outputForModel, out bool hasError)
    {
        hasError = false;
        if (string.IsNullOrWhiteSpace(outputForModel))
        {
            return false;
        }

        if (!TryParseFirstJsonObject(outputForModel, out var json))
        {
            return false;
        }

        hasError = json.TryGetProperty("error", out _);
        return true;
    }

    private static bool IsTransientToolException(Exception ex)
    {
        if (ex is ArgumentException)
        {
            return false;
        }

        if (ex is JsonException)
        {
            return false;
        }

        if (ex is InvalidOperationException)
        {
            return false;
        }

        if (ex is NotSupportedException)
        {
            return false;
        }

        if (ex is System.Security.SecurityException)
        {
            return false;
        }

        return true;
    }

    private static string BuildTransientToolErrorPayload(string message, int attempt, int maxAttempts)
    {
        var trimmed = string.IsNullOrWhiteSpace(message) ? "Transient tool error." : message.Trim();
        return JsonSerializer.Serialize(new
        {
            error = new
            {
                code = TransientErrorCode,
                message = trimmed
            },
            retry = new
            {
                attempt = Math.Clamp(attempt, 1, Math.Max(1, maxAttempts)),
                maxAttempts = Math.Max(1, maxAttempts)
            }
        }, new JsonSerializerOptions { WriteIndented = false });
    }

    private sealed class JudgeAttemptState
    {
        public bool Attempted { get; set; }
    }

    private sealed class InternalToolChoiceModeCache
    {
        public string Mode { get; private set; } = "required";

        public void MarkRequiredUnsupported()
        {
            if (!string.Equals(Mode, "auto", StringComparison.OrdinalIgnoreCase))
            {
                Mode = "auto";
            }
        }
    }

    private enum ToolHistoryMode
    {
        Structured,
        CheckpointOnly
    }

    private sealed class ToolHistoryModeCache
    {
        public ToolHistoryMode Mode { get; private set; } = ToolHistoryMode.Structured;

        public bool IsCheckpointOnly => Mode == ToolHistoryMode.CheckpointOnly;

        public bool MarkStructuredToolHistoryUnsupported()
        {
            if (Mode == ToolHistoryMode.CheckpointOnly)
            {
                return false;
            }

            Mode = ToolHistoryMode.CheckpointOnly;
            return true;
        }
    }

    private sealed class CheckpointSynthesisFailureCache
    {
        public bool IsDisabled { get; private set; }

        public bool Disable()
        {
            if (IsDisabled)
            {
                return false;
            }

            IsDisabled = true;
            return true;
        }
    }

    private static readonly string[] BaselineEvidencePaths =
    [
        "metadata",
        "analysis.summary",
        "analysis.environment",
        "analysis.exception.type",
        "analysis.exception.message",
        "analysis.exception.hResult",
        "analysis.exception.stackTrace"
    ];

    private const int TransientRetryMaxAttempts = 2;
    private const string TransientErrorCode = "transient_error";

    private static readonly (string Id, string Path, string Call)[] BaselineCalls =
    [
        ("META", "metadata", "report_get(path=\"metadata\", pageKind=\"object\", limit=50)"),
        ("SUMMARY", "analysis.summary", "report_get(path=\"analysis.summary\", pageKind=\"object\", select=[\"crashType\",\"description\",\"recommendations\",\"threadCount\",\"moduleCount\",\"assemblyCount\"])"),
        ("ENV", "analysis.environment", "report_get(path=\"analysis.environment\", pageKind=\"object\", select=[\"platform\",\"runtime\",\"process\",\"nativeAot\"])"),
        ("EXC_TYPE", "analysis.exception.type", "report_get(path=\"analysis.exception.type\")"),
        ("EXC_MESSAGE", "analysis.exception.message", "report_get(path=\"analysis.exception.message\")"),
        ("EXC_HRESULT", "analysis.exception.hResult", "report_get(path=\"analysis.exception.hResult\")"),
        ("STACK_TOP", "analysis.exception.stackTrace", "report_get(path=\"analysis.exception.stackTrace\", limit=8, select=[\"frameNumber\",\"instructionPointer\",\"module\",\"function\",\"sourceFile\",\"lineNumber\",\"isManaged\"])")
    ];

    private static readonly (string Id, string Path, string Call)[] OptionalCalls =
    [
        ("EXC_ANALYSIS", "analysis.exception.analysis", "report_get(path=\"analysis.exception.analysis\", pageKind=\"object\", limit=200)")
    ];

    private static readonly IReadOnlyDictionary<string, string> BaselineIdByPath =
        BaselineCalls.ToDictionary(c => c.Path, c => c.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> BaselinePathById =
        BaselineCalls.ToDictionary(c => c.Id, c => c.Path, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<string> BaselineCallFacts =
        BaselineCalls.Select(c => $"BASELINE_CALL: {c.Id} = {c.Call}").ToList();

    private static readonly IReadOnlyList<string> OptionalCallFacts =
        OptionalCalls.Select(c => $"OPTIONAL_CALL: {c.Id} = {c.Call}").ToList();

    private readonly record struct BaselinePhaseState(bool Complete, List<string> Completed, List<string> Missing);

    private static BaselinePhaseState ComputeBaselinePhaseState(List<ExecutedCommand> commandsExecuted)
    {
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cmd in commandsExecuted)
        {
            if (!cmd.Tool.Equals("report_get", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = TryGetString(cmd.Input, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            path = path.Trim();
            if (!BaselineIdByPath.TryGetValue(path, out var id))
            {
                continue;
            }

            if (!TryParseReportGetResponseHasError(cmd.Output ?? string.Empty, out var hasError))
            {
                continue;
            }

            if (!hasError)
            {
                completed.Add(id);
            }
        }

        var orderedIds = BaselineCalls.Select(c => c.Id).ToList();
        var completedOrdered = orderedIds.Where(id => completed.Contains(id)).ToList();
        var missing = orderedIds.Where(id => !completed.Contains(id)).ToList();
        return new BaselinePhaseState(missing.Count == 0, completedOrdered, missing);
    }

    private static string BuildBaselineCompleteFact(BaselinePhaseState baselinePhase, List<ExecutedCommand> commandsExecuted)
    {
        if (baselinePhase.Complete)
        {
            return "PHASE: baselineComplete=true";
        }

        var reason = TryGetBaselineIncompleteReasonCode(commandsExecuted, baselinePhase.Missing);
        return string.IsNullOrWhiteSpace(reason)
            ? "PHASE: baselineComplete=false"
            : $"PHASE: baselineComplete=false (reason: {TruncateText(reason.Trim(), maxChars: 64)})";
    }

    private static string? TryGetBaselineIncompleteReasonCode(List<ExecutedCommand> commandsExecuted, IReadOnlyList<string> missingBaselineIds)
    {
        if (commandsExecuted.Count == 0 || missingBaselineIds.Count == 0)
        {
            return null;
        }

        var missingPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in missingBaselineIds)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (BaselinePathById.TryGetValue(id.Trim(), out var path) && !string.IsNullOrWhiteSpace(path))
            {
                missingPaths.Add(path.Trim());
            }
        }

        if (missingPaths.Count == 0)
        {
            return null;
        }

        for (var i = commandsExecuted.Count - 1; i >= 0; i--)
        {
            var cmd = commandsExecuted[i];
            if (!cmd.Tool.Equals("report_get", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = TryGetString(cmd.Input, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            path = path.Trim();
            if (!missingPaths.Contains(path))
            {
                continue;
            }

            var output = cmd.Output ?? string.Empty;
            if (TryGetReportGetErrorCode(output, out var code) && !string.IsNullOrWhiteSpace(code))
            {
                return code;
            }

            if (output.Contains("report_get.path is required", StringComparison.OrdinalIgnoreCase))
            {
                return "missing_required_parameter";
            }

            if (output.Contains("report_get input must be an object", StringComparison.OrdinalIgnoreCase))
            {
                return "invalid_argument";
            }

            if (output.Contains("Cursor does not match", StringComparison.OrdinalIgnoreCase))
            {
                return "invalid_cursor";
            }
        }

        return null;
    }

    private static IReadOnlyList<string> BuildOptionalStatusFacts(List<ExecutedCommand> commandsExecuted)
    {
        var facts = new List<string>();
        foreach (var optional in OptionalCalls)
        {
            var attempts = commandsExecuted
                .Where(c =>
                    c.Tool.Equals("report_get", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(TryGetString(c.Input, "path")?.Trim(), optional.Path, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (attempts.Count == 0)
            {
                facts.Add($"OPTIONAL_STATUS: {optional.Id}=not_attempted");
                continue;
            }

            var succeeded = attempts.Any(a =>
                TryParseReportGetResponseHasError(a.Output ?? string.Empty, out var hasError) && !hasError);

            if (succeeded)
            {
                facts.Add($"OPTIONAL_STATUS: {optional.Id}=done");
                continue;
            }

            var deterministicError = attempts
                .Select(a => a.Output ?? string.Empty)
                .Select(output => TryGetReportGetErrorCode(output, out var code) ? code : string.Empty)
                .Where(code => !string.IsNullOrWhiteSpace(code) && !code.Equals(TransientErrorCode, StringComparison.OrdinalIgnoreCase))
                .LastOrDefault();

            if (!string.IsNullOrWhiteSpace(deterministicError))
            {
                facts.Add($"OPTIONAL_STATUS: {optional.Id}=blocked(reason={deterministicError})");
                continue;
            }

            var transientAttempts = attempts.Count(a => a.Cached != true && IsTransientToolFailure(a));
            if (transientAttempts >= TransientRetryMaxAttempts)
            {
                facts.Add($"OPTIONAL_STATUS: {optional.Id}=blocked(reason=transient_retries_exhausted)");
                continue;
            }

            facts.Add($"OPTIONAL_STATUS: {optional.Id}=not_attempted");
        }

        return facts;
    }

    private sealed class ToolAttemptInfo
    {
        public int Attempts { get; set; }
        public bool HasNonTransientAttempt { get; set; }
        public bool LastAttemptWasTransient { get; set; }
        public ExecutedCommand? LastAttempt { get; set; }
    }

    private static IReadOnlyDictionary<string, ToolAttemptInfo> BuildToolAttemptInfoByKey(List<ExecutedCommand> commandsExecuted)
    {
        var attempts = new Dictionary<string, ToolAttemptInfo>(StringComparer.Ordinal);

        foreach (var cmd in commandsExecuted)
        {
            if (cmd.Cached == true)
            {
                continue;
            }

            var tool = (cmd.Tool ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(tool))
            {
                continue;
            }

            var key = BuildToolCacheKey(tool, cmd.Input);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!attempts.TryGetValue(key, out var info))
            {
                info = new ToolAttemptInfo();
                attempts[key] = info;
            }

            info.Attempts++;

            var isTransient = IsTransientToolFailure(cmd);
            if (!isTransient)
            {
                info.HasNonTransientAttempt = true;
            }

            info.LastAttemptWasTransient = isTransient;
            info.LastAttempt = cmd;
        }

        return attempts;
    }

    private static bool ShouldBlockRepeatingToolKey(string toolCacheKey, IReadOnlyDictionary<string, ToolAttemptInfo> attemptsByKey)
    {
        if (string.IsNullOrWhiteSpace(toolCacheKey))
        {
            return false;
        }

        if (!attemptsByKey.TryGetValue(toolCacheKey, out var info) || info == null)
        {
            return false;
        }

        if (info.HasNonTransientAttempt)
        {
            return true;
        }

        if (info.LastAttemptWasTransient && info.Attempts >= TransientRetryMaxAttempts)
        {
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> BuildRetryFacts(List<ExecutedCommand> commandsExecuted)
    {
        var facts = new List<string>();

        var attemptsByKey = BuildToolAttemptInfoByKey(commandsExecuted);
        foreach (var kvp in attemptsByKey)
        {
            var info = kvp.Value;
            if (info == null || !info.LastAttemptWasTransient || info.LastAttempt == null)
            {
                continue;
            }

            var attemptCount = Math.Clamp(info.Attempts, 1, TransientRetryMaxAttempts);
            var exhausted = info.Attempts >= TransientRetryMaxAttempts;
            var formattedCall = FormatToolCallForFacts(info.LastAttempt.Tool, info.LastAttempt.Input);
            facts.Add($"RETRY: {formattedCall} attempts={attemptCount}/{TransientRetryMaxAttempts} exhausted={exhausted.ToString().ToLowerInvariant()}");

            if (facts.Count >= 10)
            {
                break;
            }
        }

        return facts;
    }

    private static string FormatToolCallForFacts(string toolName, JsonElement toolInput)
    {
        var normalizedTool = (toolName ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedTool))
        {
            normalizedTool = "tool";
        }

        if (normalizedTool.Equals("exec", StringComparison.OrdinalIgnoreCase))
        {
            var command = toolInput.ValueKind == JsonValueKind.Object ? TryGetString(toolInput, "command") : toolInput.ToString();
            command = NormalizeDebuggerCommand(command ?? string.Empty);
            command = TruncateText(command, maxChars: 256);
            return string.IsNullOrWhiteSpace(command)
                ? "exec(command=<missing>)"
                : $"exec(command=\"{command}\")";
        }

        var canonical = CanonicalizeJson(toolInput);
        canonical = TruncateText(canonical, maxChars: 256);
        return $"{normalizedTool}({canonical})";
    }

    private static string ComputeBaselineKey(string fullReportJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(fullReportJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                var dumpId = TryGetString(metadata, "dumpId");
                var generatedAt = TryGetString(metadata, "generatedAt");

                dumpId = string.IsNullOrWhiteSpace(dumpId) ? "<unknown>" : dumpId.Trim();
                generatedAt = string.IsNullOrWhiteSpace(generatedAt) ? "<unknown>" : generatedAt.Trim();

                return $"dumpId={dumpId} generatedAt={generatedAt}";
            }
        }
        catch
        {
            // Ignore and fall back.
        }

        return "dumpId=<unknown> generatedAt=<unknown>";
    }

    private sealed class BaselineEvidenceTracker
    {
        private readonly Dictionary<string, bool> _seen = new(StringComparer.OrdinalIgnoreCase);

        public BaselineEvidenceTracker()
        {
            foreach (var path in BaselineEvidencePaths)
            {
                _seen[path] = false;
            }
        }

        public bool IsComplete => _seen.Values.All(v => v);

        public void ObserveReportGet(JsonElement toolInput, string outputForModel)
        {
            if (toolInput.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!toolInput.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var path = pathEl.GetString();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            path = path.Trim();
            if (!_seen.ContainsKey(path))
            {
                return;
            }

            if (!TryParseReportGetResponseHasError(outputForModel, out var hasError))
            {
                return;
            }

            if (hasError)
            {
                return;
            }

            _seen[path] = true;
        }
    }

    private sealed class OptionalBaselineCallScheduler
    {
        private readonly HashSet<string> _scheduledOptionalIds = new(StringComparer.OrdinalIgnoreCase);
        private string? _baselineKey;

        public bool WasScheduled(string baselineKey, string optionalId)
        {
            EnsureBaselineKey(baselineKey);
            return _scheduledOptionalIds.Contains(optionalId);
        }

        public bool MarkScheduled(string baselineKey, string optionalId)
        {
            EnsureBaselineKey(baselineKey);
            return _scheduledOptionalIds.Add(optionalId);
        }

        private void EnsureBaselineKey(string baselineKey)
        {
            var normalized = baselineKey?.Trim() ?? string.Empty;
            if (string.Equals(_baselineKey, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _baselineKey = normalized;
            _scheduledOptionalIds.Clear();
        }
    }

    private static bool IsBaselineBlocked(int iteration, List<ExecutedCommand> commandsExecuted, IReadOnlyList<string> missingBaselineIds)
    {
        if (iteration < 10 || commandsExecuted.Count == 0 || missingBaselineIds.Count == 0)
        {
            return false;
        }

        var windowStart = Math.Max(1, iteration - 9);
        foreach (var id in missingBaselineIds)
        {
            if (string.IsNullOrWhiteSpace(id) || !BaselinePathById.TryGetValue(id.Trim(), out var path))
            {
                return false;
            }

            var attempts = commandsExecuted
                .Where(c =>
                    c.Iteration >= windowStart
                    && c.Tool.Equals("report_get", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(TryGetString(c.Input, "path")?.Trim(), path, StringComparison.Ordinal))
                .ToList();

            if (attempts.Count < 2)
            {
                return false;
            }

            var failedAttempts = attempts.Count(c =>
                TryParseReportGetResponseHasError(c.Output ?? string.Empty, out var hasError) && hasError);

            if (failedAttempts < 2)
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct MetaBookkeepingResult(bool Done, int MetaToolCallsExecuted);

    private async Task<MetaBookkeepingResult> TryRunMetaBookkeepingAsync(
        string passName,
        string systemPrompt,
        List<SamplingMessage> messages,
        AiEvidenceLedger evidenceLedger,
        AiHypothesisTracker hypothesisTracker,
        int metaToolCallsExecuted,
        int maxMetaToolCalls,
        int iteration,
        int maxTokens,
        string? traceRunDir,
        InternalToolChoiceModeCache internalToolChoiceModeCache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passName);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(evidenceLedger);
        ArgumentNullException.ThrowIfNull(hypothesisTracker);
        ArgumentNullException.ThrowIfNull(internalToolChoiceModeCache);

        if (metaToolCallsExecuted >= maxMetaToolCalls)
        {
            _logger.LogDebug("[AI] Skipping meta bookkeeping: meta tool budget exceeded ({Used}/{Max}).", metaToolCallsExecuted, maxMetaToolCalls);
            return new MetaBookkeepingResult(Done: true, MetaToolCallsExecuted: metaToolCallsExecuted);
        }

        var metaTools = SamplingTools
            .GetCrashAnalysisTools()
            .Where(t => IsInternalMetaTool(t.Name))
            .ToList();

        if (metaTools.Count == 0)
        {
            _logger.LogWarning("[AI] Skipping meta bookkeeping: no meta tools are registered.");
            return new MetaBookkeepingResult(Done: true, MetaToolCallsExecuted: metaToolCallsExecuted);
        }

        var evidenceCountBefore = evidenceLedger.Items.Count;
        var hypothesisCountBefore = hypothesisTracker.Hypotheses.Count;

        var stateSnapshot = BuildStateSnapshotJson(evidenceLedger, hypothesisTracker);
        var metaPrompt = $"""
INTERNAL: Meta bookkeeping step for pass "{passName}".

Goal: stabilize the investigation across iterations by maintaining a bounded evidence ledger and competing hypotheses.
Do NOT gather new evidence in this step: do not call exec/report_get/inspect/get_thread_stack/analysis_complete.

Use the meta tools to:
1) Register 3-4 competing hypotheses (if none exist yet).
2) Evidence is already recorded automatically from tool outputs (E# IDs). Do not add new evidence facts manually.
3) Optionally annotate existing evidence items (whyItMatters/tags/notes) if it helps scoring (do not restate tool outputs as new findings).
4) Score/update hypotheses by linking supports/contradicts evidence IDs.

Constraints:
- Batch updates; do not spam (single assistant message with multiple tool calls is preferred).
- Be grounded: cite sources from already executed tool calls (e.g., report_get(...) or exec(...)).
- Keep strings concise (<=2048 chars each). Avoid repeated meta tool calls with no changes.

        Current state snapshot (JSON):
{stateSnapshot}
""";

        SamplingMessage BuildMetaBookkeepingPromptMessage() => new()
        {
            Role = Role.User,
            Content =
            [
                new TextContentBlock { Text = metaPrompt }
            ]
        };

        List<SamplingMessage> BuildMetaBookkeepingRequestMessages()
        {
            if (!_toolHistoryModeCache.IsCheckpointOnly)
            {
                var request = new List<SamplingMessage>(messages)
                {
                    BuildMetaBookkeepingPromptMessage()
                };
                return request;
            }

            var checkpointMessage = FindLatestCheckpointCarryForwardMessage(messages, passName);
            var checkpointOnly = checkpointMessage == null
                ? new List<SamplingMessage>()
                : new List<SamplingMessage> { checkpointMessage };

            checkpointOnly.Add(BuildMetaBookkeepingPromptMessage());
            return checkpointOnly;
        }

        CreateMessageResult? response = null;
        Exception? lastError = null;
        var toolChoiceMode = internalToolChoiceModeCache.Mode;
        var requestMessages = BuildMetaBookkeepingRequestMessages();

        for (var attempt = 1; attempt <= Math.Max(1, MaxSamplingRequestAttempts); attempt++)
        {
            var request = new CreateMessageRequestParams
            {
                SystemPrompt = systemPrompt,
                Messages = requestMessages,
                MaxTokens = maxTokens,
                Tools = metaTools,
                ToolChoice = new ToolChoice { Mode = toolChoiceMode }
            };

            try
            {
                _logger.LogInformation("[AI] Meta bookkeeping at iteration {Iteration} (attempt {Attempt}/{MaxAttempts})...", iteration, attempt, MaxSamplingRequestAttempts);
                WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-meta-bookkeeping-request.json", BuildTraceRequest(iteration, request));
                response = await _samplingClient.RequestCompletionAsync(request, cancellationToken).ConfigureAwait(false);
                WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-meta-bookkeeping-response.json", BuildTraceResponse(iteration, response));

                if (response.Content != null && response.Content.Count > 0)
                {
                    break;
                }

                lastError = new InvalidOperationException("Meta bookkeeping returned empty content.");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (IsStructuredToolHistoryUnsupported(ex) && _toolHistoryModeCache.MarkStructuredToolHistoryUnsupported())
                {
                    _logger.LogInformation(
                        "[AI] Provider rejected structured tool history; switching to checkpoint-only history mode for the remainder of this run (pass {Pass}).",
                        passName);
                    WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-history-mode-fallback.json", new
                    {
                        passName,
                        iteration,
                        mode = "checkpoint-only",
                        reason = "structured_tool_history_unsupported",
                        message = ex.Message
                    });
                    requestMessages = BuildMetaBookkeepingRequestMessages();
                    lastError = ex;
                    continue;
                }

                if (toolChoiceMode.Equals("required", StringComparison.OrdinalIgnoreCase) &&
                    IsToolChoiceRequiredUnsupported(ex))
                {
                    internalToolChoiceModeCache.MarkRequiredUnsupported();
                    _logger.LogInformation(
                        "[AI] Provider rejected toolChoice=required for meta bookkeeping; retrying with toolChoice=auto (iteration {Iteration}).",
                        iteration);
                    WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-meta-bookkeeping-toolchoice-fallback.json", new
                    {
                        passName,
                        iteration,
                        from = "required",
                        to = "auto",
                        message = ex.Message
                    });
                    toolChoiceMode = "auto";
                    lastError = ex;
                    continue;
                }

                lastError = ex;
                _logger.LogWarning(ex, "[AI] Meta bookkeeping failed at iteration {Iteration} (attempt {Attempt}/{MaxAttempts})", iteration, attempt, MaxSamplingRequestAttempts);
                WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-meta-bookkeeping-error.json", new { passName, iteration, attempt, error = ex.ToString(), message = ex.Message });
            }
        }

        if (response?.Content == null || response.Content.Count == 0)
        {
            _logger.LogWarning("[AI] Meta bookkeeping skipped: no response content. Error={Message}", lastError?.Message);
            return new MetaBookkeepingResult(Done: true, MetaToolCallsExecuted: metaToolCallsExecuted);
        }

        var toolUses = response.Content.OfType<ToolUseContentBlock>().ToList();
        var metaToolUses = toolUses
            .Where(t => IsInternalMetaTool((t.Name ?? string.Empty).Trim()))
            .ToList();

        if (metaToolUses.Count == 0)
        {
            _logger.LogWarning("[AI] Meta bookkeeping response contained no tool calls.");
            return new MetaBookkeepingResult(Done: true, MetaToolCallsExecuted: metaToolCallsExecuted);
        }

        messages.Add(new SamplingMessage
        {
            Role = Role.Assistant,
            Content = response.Content
        });

        var respondedToolUseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var toolUse in metaToolUses)
        {
            var toolName = (toolUse.Name ?? string.Empty).Trim();
            if (!IsInternalMetaTool(toolName))
            {
                continue;
            }

            var toolUseId = toolUse.Id;
            if (string.IsNullOrWhiteSpace(toolUseId))
            {
                continue;
            }

            string outputForModel;
            if (metaToolCallsExecuted >= maxMetaToolCalls)
            {
                outputForModel = TruncateForModel(
                    JsonSerializer.Serialize(new
                    {
                        ignored = true,
                        reason = "meta_tool_budget_exceeded",
                        maxMetaToolCalls,
                        tool = toolName
                    }));
            }
            else
            {
                var output = ExecuteInternalMetaTool(toolName, NormalizeToolInput(toolUse.Input), evidenceLedger, hypothesisTracker, EnableEvidenceProvenance);
                metaToolCallsExecuted++;
                outputForModel = TruncateForModel(output);
            }

            messages.Add(new SamplingMessage
            {
                Role = Role.User,
                Content =
                [
                    new ToolResultContentBlock
                    {
                        ToolUseId = toolUseId,
                        IsError = false,
                        Content =
                        [
                            new TextContentBlock { Text = outputForModel }
                        ]
                    }
                ]
            });

            respondedToolUseIds.Add(toolUseId);
        }

        PruneUnrespondedToolUsesFromLastAssistantMessage(messages, respondedToolUseIds);

        var changed = evidenceLedger.Items.Count != evidenceCountBefore || hypothesisTracker.Hypotheses.Count != hypothesisCountBefore;
        if (!changed)
        {
            _logger.LogWarning(
                "[AI] Meta bookkeeping did not change state (evidence {EvidenceBefore}->{EvidenceAfter}, hypotheses {HypBefore}->{HypAfter}).",
                evidenceCountBefore,
                evidenceLedger.Items.Count,
                hypothesisCountBefore,
                hypothesisTracker.Hypotheses.Count);
        }

        return new MetaBookkeepingResult(Done: true, MetaToolCallsExecuted: metaToolCallsExecuted);
    }

    private async Task<AiJudgeResult?> TryRunJudgeStepAsync(
        string passName,
        string systemPrompt,
        AiEvidenceLedger evidenceLedger,
        AiHypothesisTracker hypothesisTracker,
        InternalToolChoiceModeCache internalToolChoiceModeCache,
        int iteration,
        int maxTokens,
        string? lastModel,
        string? traceRunDir,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passName);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(evidenceLedger);
        ArgumentNullException.ThrowIfNull(hypothesisTracker);
        ArgumentNullException.ThrowIfNull(internalToolChoiceModeCache);

        if (evidenceLedger.Items.Count == 0 || hypothesisTracker.Hypotheses.Count == 0)
        {
            _logger.LogDebug("[AI] Skipping judge step: no evidence/hypotheses have been recorded.");
            return null;
        }

        var hypothesisCount = hypothesisTracker.Hypotheses.Count;
        var requiredRejected = Math.Min(2, Math.Max(0, hypothesisCount - 1));

        var stateSnapshot = BuildStateSnapshotJson(evidenceLedger, hypothesisTracker);
        var judgePrompt = $"""
INTERNAL: Judge step for pass "{passName}".

You are given a stable state snapshot with:
- evidenceLedger: evidence items with IDs (E#)
- hypotheses: competing hypotheses with IDs (H#) and their linked supports/contradicts evidence IDs

Task:
1) Select the single best-supported hypothesis ID.
2) Provide supportsEvidenceIds (existing E#) that directly support the selection.
3) Reject at least {requiredRejected} strongest alternative(s). For each rejected hypothesis, provide contradictsEvidenceIds (existing E#) and a concise reason.

Confidence rubric:
- high: only allowed when there are at least 3 competing hypotheses. Requires >=3 supporting evidence IDs AND >=2 rejected alternatives with concrete contradicting evidence IDs.
- medium: leading hypothesis is supported, but at least one strong alternative cannot be fully rejected.
- low: evidence is sparse/ambiguous.

Constraints:
- Do NOT call any tools except "{AnalysisJudgeCompleteToolName}".
- Every evidence ID you cite must exist in evidenceLedger.
- Do NOT output any additional text.

State snapshot (JSON):
{stateSnapshot}
""";

        var request = new CreateMessageRequestParams
        {
            SystemPrompt = systemPrompt,
            Messages =
            [
                new SamplingMessage
                {
                    Role = Role.User,
                    Content =
                    [
                        new TextContentBlock { Text = judgePrompt }
                    ]
                }
            ],
            MaxTokens = Math.Max(256, maxTokens),
            Tools = SamplingTools.GetJudgeTools(),
            ToolChoice = new ToolChoice { Mode = internalToolChoiceModeCache.Mode }
        };

        CreateMessageResult response;
        try
        {
            _logger.LogInformation("[AI] Running judge step for pass {Pass} at iteration {Iteration}...", passName, iteration);
            WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-judge-request.json", BuildTraceRequest(iteration, request));
            response = await _samplingClient.RequestCompletionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (request.ToolChoice?.Mode?.Equals("required", StringComparison.OrdinalIgnoreCase) == true
                && IsToolChoiceRequiredUnsupported(ex))
            {
                internalToolChoiceModeCache.MarkRequiredUnsupported();
                _logger.LogInformation(
                    "[AI] Provider rejected toolChoice=required for judge step; retrying with toolChoice=auto (pass {Pass}, iteration {Iteration}).",
                    passName,
                    iteration);
                WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-judge-toolchoice-fallback.json", new
                {
                    passName,
                    iteration,
                    from = "required",
                    to = "auto",
                    message = ex.Message
                });

                var fallbackRequest = CloneWithToolChoiceMode(request, "auto");
                try
                {
                    WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-judge-fallback-request.json", BuildTraceRequest(iteration, fallbackRequest));
                    response = await _samplingClient.RequestCompletionAsync(fallbackRequest, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex2) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex2, "[AI] Judge step failed in pass {Pass} at iteration {Iteration}", passName, iteration);
                    WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-judge-error.json", new { passName, iteration, error = ex2.ToString(), message = ex2.Message });
                    return null;
                }
            }
            else
            {
                _logger.LogWarning(ex, "[AI] Judge step failed in pass {Pass} at iteration {Iteration}", passName, iteration);
                WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-judge-error.json", new { passName, iteration, error = ex.ToString(), message = ex.Message });
                return null;
            }
        }

        WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-judge-response.json", BuildTraceResponse(iteration, response));

        var allToolUses = response.Content?
            .OfType<ToolUseContentBlock>()
            .ToList() ?? [];

        var toolUse = allToolUses.FirstOrDefault(b =>
            string.Equals((b.Name ?? string.Empty).Trim(), AnalysisJudgeCompleteToolName, StringComparison.OrdinalIgnoreCase));

        if (toolUse == null && allToolUses.Count == 1)
        {
            toolUse = allToolUses[0];
            _logger.LogWarning(
                "[AI] Judge step returned an unexpected tool call name; attempting to parse the only tool call present (name={Name}).",
                toolUse.Name);
        }

        if (toolUse == null)
        {
            var assistantText = ExtractAssistantText(response);
            if (!string.IsNullOrWhiteSpace(assistantText) && TryParseFirstJsonObject(assistantText, out var json))
            {
                try
                {
                    return ParseJudgeComplete(json, response.Model ?? lastModel);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "[AI] Judge step returned unstructured JSON but it did not match the expected schema.");
                    WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-judge-parse-error.json", new { passName, iteration, error = ex.ToString(), message = ex.Message });
                    return null;
                }
            }

            _logger.LogWarning("[AI] Judge step returned no {Tool} tool call.", AnalysisJudgeCompleteToolName);
            return null;
        }

        try
        {
            var parsed = ParseJudgeComplete(NormalizeToolInput(toolUse.Input), response.Model ?? lastModel);
            return parsed;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "[AI] Judge step returned invalid input for {Tool}.", AnalysisJudgeCompleteToolName);
            WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-judge-parse-error.json", new { passName, iteration, error = ex.ToString(), message = ex.Message });
            return null;
        }
    }

    private async Task ApplyJudgeDrivenFinalizationAsync(
        AiAnalysisResult result,
        List<ExecutedCommand> commandsExecuted,
        AiEvidenceLedger evidenceLedger,
        AiHypothesisTracker hypothesisTracker,
        InternalToolChoiceModeCache internalToolChoiceModeCache,
        string passName,
        int iteration,
        int maxTokens,
        string? lastModel,
        string? traceRunDir,
        JudgeAttemptState judgeAttemptState,
        string finalizationReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(commandsExecuted);
        ArgumentNullException.ThrowIfNull(evidenceLedger);
        ArgumentNullException.ThrowIfNull(hypothesisTracker);
        ArgumentNullException.ThrowIfNull(internalToolChoiceModeCache);
        ArgumentNullException.ThrowIfNull(passName);
        ArgumentNullException.ThrowIfNull(judgeAttemptState);
        ArgumentNullException.ThrowIfNull(finalizationReason);

        if (evidenceLedger.Items.Count == 0 || hypothesisTracker.Hypotheses.Count == 0)
        {
            return;
        }

        if (result.Judge == null && !judgeAttemptState.Attempted)
        {
            judgeAttemptState.Attempted = true;
            result.Judge = await TryRunJudgeStepAsync(
                    passName: passName,
                    systemPrompt: JudgeSystemPrompt,
                    evidenceLedger: evidenceLedger,
                    hypothesisTracker: hypothesisTracker,
                    internalToolChoiceModeCache: internalToolChoiceModeCache,
                    iteration: iteration,
                    maxTokens: maxTokens,
                    lastModel: lastModel,
                    traceRunDir: traceRunDir,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            result.Judge ??= BuildDeterministicJudgeFallback(
                hypothesisTracker: hypothesisTracker,
                model: lastModel,
                rationale: $"Deterministic fallback: judge step failed during finalization ({finalizationReason}).");
        }

        result.Judge ??= BuildDeterministicJudgeFallback(
            hypothesisTracker: hypothesisTracker,
            model: lastModel,
            rationale: $"Deterministic fallback: judge step was already attempted earlier; using deterministic selection during finalization ({finalizationReason}).");

        if (!TryGetHypothesisById(hypothesisTracker, result.Judge.SelectedHypothesisId, out var winner))
        {
            result.Judge = BuildDeterministicJudgeFallback(
                hypothesisTracker: hypothesisTracker,
                model: lastModel,
                rationale: $"Deterministic fallback: judge selected unknown hypothesisId '{result.Judge.SelectedHypothesisId}'.");

            if (!TryGetHypothesisById(hypothesisTracker, result.Judge.SelectedHypothesisId, out winner))
            {
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(winner?.Hypothesis))
        {
            var winnerText = winner.Hypothesis.Trim();
            var winnerCategory = ClassifyHypothesisCategory(winnerText);
            result.RootCause = winnerText;

            var shouldForceJudgeNarrative = ShouldForceJudgeAlignedNarrative(
                finalizationReason,
                reasoning: result.Reasoning,
                recommendations: result.Recommendations);
            var shouldFillReasoning = string.IsNullOrWhiteSpace(result.Reasoning);
            var shouldFillRecommendations = result.Recommendations is null || result.Recommendations.Count == 0;

            var shouldRewriteNarrative = shouldForceJudgeNarrative || ShouldRewriteNarrativeToMatchJudgeWinner(
                winnerCategory: winnerCategory,
                currentReasoning: result.Reasoning,
                currentRecommendations: result.Recommendations,
                currentAdditionalFindings: result.AdditionalFindings);

            if (shouldRewriteNarrative)
            {
                result.Reasoning = BuildJudgeAlignedReasoning(result.Judge, winnerText);
                result.Recommendations = BuildJudgeAlignedRecommendations(winner, winnerCategory);
                result.AdditionalFindings = null;
            }
            else
            {
                if (shouldFillReasoning)
                {
                    result.Reasoning = BuildJudgeAlignedReasoning(result.Judge, winnerText);
                }

                if (shouldFillRecommendations)
                {
                    result.Recommendations = BuildJudgeAlignedRecommendations(winner, winnerCategory);
                }
            }
        }

        result.Recommendations = FilterDisallowedRecommendations(result.Recommendations);
        if (result.Summary?.Recommendations != null)
        {
            result.Summary.Recommendations = FilterDisallowedRecommendations(result.Summary.Recommendations);
        }
    }

    private static bool ShouldForceJudgeAlignedNarrative(string finalizationReason, string? reasoning, List<string>? recommendations)
    {
        if (string.IsNullOrWhiteSpace(finalizationReason))
        {
            return false;
        }

        var normalized = finalizationReason.Trim();
        if (normalized.Equals("sampling-failure", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!normalized.Equals("tool-budget", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Equals("iteration-budget", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (recommendations == null || recommendations.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(reasoning))
        {
            return true;
        }

        return reasoning.Contains("Final synthesis", StringComparison.OrdinalIgnoreCase) ||
               reasoning.Contains("Tool call budget exceeded", StringComparison.OrdinalIgnoreCase) ||
               reasoning.Contains("Sampling failed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetHypothesisById(
        AiHypothesisTracker hypothesisTracker,
        string? hypothesisId,
        out AiHypothesis? hypothesis)
    {
        hypothesis = null;
        if (hypothesisTracker.Hypotheses.Count == 0 || string.IsNullOrWhiteSpace(hypothesisId))
        {
            return false;
        }

        var trimmed = hypothesisId.Trim();
        hypothesis = hypothesisTracker.Hypotheses
            .FirstOrDefault(h => h != null && string.Equals(h.Id, trimmed, StringComparison.OrdinalIgnoreCase));
        return hypothesis != null;
    }

    private static AiJudgeResult BuildDeterministicJudgeFallback(AiHypothesisTracker hypothesisTracker, string? model, string rationale)
    {
        ArgumentNullException.ThrowIfNull(hypothesisTracker);

        var hypotheses = hypothesisTracker.Hypotheses.Where(h => h != null).ToList();
        if (hypotheses.Count == 0)
        {
            return new AiJudgeResult
            {
                SelectedHypothesisId = string.Empty,
                Confidence = "unknown",
                Rationale = string.IsNullOrWhiteSpace(rationale) ? "Deterministic fallback: no hypotheses available." : rationale.Trim(),
                Model = model,
                AnalyzedAt = DateTime.UtcNow
            };
        }

        var winner = hypotheses
            .OrderByDescending(h => GetConfidenceRank(h!.Confidence))
            .ThenByDescending(h => h!.SupportsEvidenceIds?.Count ?? 0)
            .ThenBy(h => h!.ContradictsEvidenceIds?.Count ?? 0)
            .ThenBy(h => ParseHypothesisIdNumber(h!.Id) ?? int.MaxValue)
            .ThenBy(h => h!.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .First()!;

        var rejected = hypotheses
            .Where(h => h != null && !string.Equals(h.Id, winner.Id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(h => GetConfidenceRank(h!.Confidence))
            .ThenByDescending(h => h!.SupportsEvidenceIds?.Count ?? 0)
            .ThenBy(h => ParseHypothesisIdNumber(h!.Id) ?? int.MaxValue)
            .ThenBy(h => h!.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .Select(h => new AiRejectedHypothesis
            {
                HypothesisId = h!.Id ?? string.Empty,
                ContradictsEvidenceIds = h.ContradictsEvidenceIds ?? [],
                Reason = h.ContradictsEvidenceIds is { Count: > 0 }
                    ? "Rejected by linked contradicting evidence IDs."
                    : "Insufficient contradicting evidence IDs were linked; rejection is provisional."
            })
            .ToList();

        var supports = winner.SupportsEvidenceIds;
        var canBeHigh = supports is { Count: >= 3 } && rejected.Count >= 2 && rejected.All(r => r.ContradictsEvidenceIds.Count > 0);

        return new AiJudgeResult
        {
            SelectedHypothesisId = winner.Id ?? string.Empty,
            Confidence = canBeHigh ? "high" : "low",
            Rationale = string.IsNullOrWhiteSpace(rationale)
                ? "Deterministic fallback: selected hypothesis by confidence/supporting evidence links."
                : rationale.Trim(),
            SupportsEvidenceIds = supports,
            RejectedHypotheses = rejected.Count == 0 ? null : rejected,
            Model = model,
            AnalyzedAt = DateTime.UtcNow
        };
    }

    private enum HypothesisCategory
    {
        Unknown = 0,
        AssemblyVersionMismatch,
        ProfilerOrInstrumentation,
        Trimming,
        MemoryCorruption
    }

    private static HypothesisCategory ClassifyHypothesisCategory(string? hypothesis)
    {
        if (string.IsNullOrWhiteSpace(hypothesis))
        {
            return HypothesisCategory.Unknown;
        }

        var text = hypothesis.Trim().ToLowerInvariant();
        if (text.Contains("profiler", StringComparison.Ordinal) ||
            text.Contains("instrumentation", StringComparison.Ordinal) ||
            text.Contains("tracer", StringComparison.Ordinal) ||
            text.Contains("datadog", StringComparison.Ordinal) ||
            text.Contains("il rewriting", StringComparison.Ordinal) ||
            text.Contains("il-rewriting", StringComparison.Ordinal) ||
            text.Contains("aop", StringComparison.Ordinal))
        {
            return HypothesisCategory.ProfilerOrInstrumentation;
        }

        if (text.Contains("trim", StringComparison.Ordinal) ||
            text.Contains("illink", StringComparison.Ordinal) ||
            text.Contains("linker", StringComparison.Ordinal) ||
            text.Contains("publishtrimmed", StringComparison.Ordinal) ||
            text.Contains("nativeaot", StringComparison.Ordinal))
        {
            return HypothesisCategory.Trimming;
        }

        if (text.Contains("version mismatch", StringComparison.Ordinal) ||
            text.Contains("assembly version", StringComparison.Ordinal) ||
            text.Contains("assembly mismatch", StringComparison.Ordinal) ||
            text.Contains("binding redirect", StringComparison.Ordinal) ||
            text.Contains("incompatible version", StringComparison.Ordinal))
        {
            return HypothesisCategory.AssemblyVersionMismatch;
        }

        if (text.Contains("memory corruption", StringComparison.Ordinal) ||
            text.Contains("heap corruption", StringComparison.Ordinal) ||
            text.Contains("method table", StringComparison.Ordinal) && text.Contains("corrupt", StringComparison.Ordinal))
        {
            return HypothesisCategory.MemoryCorruption;
        }

        return HypothesisCategory.Unknown;
    }

    private static bool ShouldRewriteNarrativeToMatchJudgeWinner(
        HypothesisCategory winnerCategory,
        string? currentReasoning,
        List<string>? currentRecommendations,
        List<string>? currentAdditionalFindings)
    {
        var reasoning = currentReasoning ?? string.Empty;
        if (NarrativeNegatesWinnerCategory(reasoning, winnerCategory))
        {
            return true;
        }

        var recommendationsText = string.Join("\n", currentRecommendations ?? []);
        if (NarrativeNegatesWinnerCategory(recommendationsText, winnerCategory))
        {
            return true;
        }

        var additionalText = string.Join("\n", currentAdditionalFindings ?? []);
        if (NarrativeNegatesWinnerCategory(additionalText, winnerCategory))
        {
            return true;
        }

        if (ContainsDisallowedProfilerDisableRecommendation(currentRecommendations))
        {
            return true;
        }

        var narrativeAll = string.Join("\n", new[] { reasoning, recommendationsText, additionalText });
        var narrativeCategories = ExtractNarrativeCategories(narrativeAll);
        if (winnerCategory != HypothesisCategory.Unknown && narrativeCategories.Count > 0 && !narrativeCategories.Contains(winnerCategory))
        {
            return true;
        }

        return false;
    }

    private static HashSet<HypothesisCategory> ExtractNarrativeCategories(string? narrative)
    {
        var set = new HashSet<HypothesisCategory>();
        if (string.IsNullOrWhiteSpace(narrative))
        {
            return set;
        }

        var text = narrative.ToLowerInvariant();
        if (text.Contains("profiler", StringComparison.Ordinal) ||
            text.Contains("instrumentation", StringComparison.Ordinal) ||
            text.Contains("tracer", StringComparison.Ordinal) ||
            text.Contains("datadog", StringComparison.Ordinal) ||
            text.Contains("il rewriting", StringComparison.Ordinal) ||
            text.Contains("il-rewriting", StringComparison.Ordinal) ||
            text.Contains("aop", StringComparison.Ordinal))
        {
            set.Add(HypothesisCategory.ProfilerOrInstrumentation);
        }

        if (text.Contains("trim", StringComparison.Ordinal) ||
            text.Contains("illink", StringComparison.Ordinal) ||
            text.Contains("linker", StringComparison.Ordinal) ||
            text.Contains("publishtrimmed", StringComparison.Ordinal) ||
            text.Contains("nativeaot", StringComparison.Ordinal))
        {
            set.Add(HypothesisCategory.Trimming);
        }

        if (text.Contains("version mismatch", StringComparison.Ordinal) ||
            text.Contains("assembly version", StringComparison.Ordinal) ||
            text.Contains("assembly mismatch", StringComparison.Ordinal) ||
            text.Contains("binding redirect", StringComparison.Ordinal) ||
            text.Contains("incompatible version", StringComparison.Ordinal))
        {
            set.Add(HypothesisCategory.AssemblyVersionMismatch);
        }

        if (text.Contains("memory corruption", StringComparison.Ordinal) ||
            text.Contains("heap corruption", StringComparison.Ordinal) ||
            text.Contains("method table", StringComparison.Ordinal) && text.Contains("corrupt", StringComparison.Ordinal))
        {
            set.Add(HypothesisCategory.MemoryCorruption);
        }

        set.Remove(HypothesisCategory.Unknown);
        return set;
    }

    private static bool NarrativeNegatesWinnerCategory(string narrative, HypothesisCategory winnerCategory)
    {
        if (string.IsNullOrWhiteSpace(narrative) || winnerCategory == HypothesisCategory.Unknown)
        {
            return false;
        }

        var lowered = narrative.ToLowerInvariant();
        return winnerCategory switch
        {
            HypothesisCategory.AssemblyVersionMismatch => ContainsNegatedConcept(lowered, "version mismatch") ||
                                                         ContainsNegatedConcept(lowered, "assembly mismatch") ||
                                                         ContainsNegatedConcept(lowered, "assembly version"),
            HypothesisCategory.ProfilerOrInstrumentation => ContainsNegatedConcept(lowered, "profiler") ||
                                                           ContainsNegatedConcept(lowered, "instrumentation") ||
                                                           ContainsNegatedConcept(lowered, "tracer") ||
                                                           ContainsNegatedConcept(lowered, "il rewriting"),
            HypothesisCategory.Trimming => ContainsNegatedConcept(lowered, "trim") ||
                                          ContainsNegatedConcept(lowered, "publishtrimmed") ||
                                          ContainsNegatedConcept(lowered, "nativeaot"),
            HypothesisCategory.MemoryCorruption => ContainsNegatedConcept(lowered, "memory corruption") ||
                                                  ContainsNegatedConcept(lowered, "heap corruption"),
            _ => false
        };
    }

    private static bool ContainsNegatedConcept(string loweredNarrative, string concept)
    {
        if (string.IsNullOrWhiteSpace(loweredNarrative) || string.IsNullOrWhiteSpace(concept))
        {
            return false;
        }

        var idx = loweredNarrative.IndexOf(concept, StringComparison.Ordinal);
        if (idx < 0)
        {
            return false;
        }

        var windowStart = Math.Max(0, idx - 80);
        var window = loweredNarrative.Substring(windowStart, idx - windowStart);
        return window.Contains("rather than", StringComparison.Ordinal) ||
               window.Contains("instead of", StringComparison.Ordinal) ||
               window.Contains("rule out", StringComparison.Ordinal) ||
               window.Contains("ruling out", StringComparison.Ordinal) ||
               window.Contains("not a ", StringComparison.Ordinal) ||
               window.Contains("not an ", StringComparison.Ordinal) ||
               window.Contains("not due to", StringComparison.Ordinal) ||
               window.Contains("not caused by", StringComparison.Ordinal) ||
               window.Contains("no evidence", StringComparison.Ordinal) ||
               window.Contains("no sign", StringComparison.Ordinal) ||
               window.Contains("no indication", StringComparison.Ordinal) ||
               window.Contains("without evidence", StringComparison.Ordinal);
    }

    private static string BuildJudgeAlignedReasoning(AiJudgeResult? judge, string winnerText)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Selected hypothesis: {winnerText}".TrimEnd());
        if (judge != null)
        {
            if (!string.IsNullOrWhiteSpace(judge.SelectedHypothesisId))
            {
                sb.AppendLine($"Judge selection: {judge.SelectedHypothesisId.Trim()} (confidence={judge.Confidence?.Trim() ?? "unknown"})".TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(judge.Rationale))
            {
                sb.AppendLine();
                sb.AppendLine("Judge rationale:");
                sb.AppendLine(judge.Rationale.Trim());
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static List<string>? BuildJudgeAlignedRecommendations(AiHypothesis winner, HypothesisCategory winnerCategory)
    {
        var recs = new List<string>();

        if (winner.TestsToRun is { Count: > 0 })
        {
            recs.AddRange(winner.TestsToRun.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
        }

        if (recs.Count == 0)
        {
            recs.AddRange(winnerCategory switch
            {
                HypothesisCategory.AssemblyVersionMismatch =>
                [
                    "Verify the exact runtime-loaded assembly versions for the failing type and its caller (report_get(path=\"analysis.assemblies.items\", limit=25, select=[\"name\",\"assemblyVersion\",\"fileVersion\",\"path\"]) and report_get(path=\"analysis.modules\", ...)).",
                    "Confirm the failing type is resolved from the expected assembly and AssemblyLoadContext (exec \"!name2ee * <TypeName>\" / exec \"!dumpmodule <module>\" where available).",
                    "Ensure build-time package versions match the deployed runtime and remove duplicate copies of System.Collections.Concurrent from the deployment image."
                ],
                HypothesisCategory.ProfilerOrInstrumentation =>
                [
                    "Identify which profilers/tracers/instrumentation are active in the dump (report_get(path=\"analysis.modules\") for known profiler DLLs/so files) and verify their versions.",
                    "If instrumentation performs IL rewriting, verify it in-dump by inspecting method/IL/JIT state rather than assuming (e.g., inspect the relevant MethodDesc / IL when possible).",
                    "Adjust instrumentation configuration to exclude core framework assemblies/types from rewriting if corruption is suspected."
                ],
                HypothesisCategory.Trimming =>
                [
                    "If trimming/ILLink is enabled, add explicit preservation rules for the missing member (e.g., descriptor/attribute) and rebuild.",
                    "Confirm whether the app was built with trimming or NativeAOT settings and collect the publish configuration used for the crashed artifact."
                ],
                HypothesisCategory.MemoryCorruption =>
                [
                    "Look for additional signs of memory corruption (invalid MethodTables, inconsistent object headers) and capture relevant diagnostics for upstream investigation.",
                    "Check for native components or unsafe code paths that could corrupt managed memory around the time of the exception."
                ],
                _ => []
            });
        }

        recs = FilterDisallowedRecommendations(recs) ?? [];
        if (recs.Count == 0)
        {
            return null;
        }

        return DeduplicateAndLimit(recs, limit: 8);
    }

    private static List<string>? FilterDisallowedRecommendations(List<string>? recommendations)
    {
        if (recommendations == null || recommendations.Count == 0)
        {
            return recommendations;
        }

        var filtered = new List<string>(recommendations.Count);
        foreach (var rec in recommendations)
        {
            if (string.IsNullOrWhiteSpace(rec))
            {
                continue;
            }

            if (IsDisallowedProfilerDisableRecommendation(rec))
            {
                continue;
            }

            filtered.Add(rec.Trim());
        }

        return filtered.Count == 0 ? null : DeduplicateAndLimit(filtered, limit: 12);
    }

    private static bool ContainsDisallowedProfilerDisableRecommendation(List<string>? recommendations)
        => recommendations != null && recommendations.Any(IsDisallowedProfilerDisableRecommendation);

    private static bool IsDisallowedProfilerDisableRecommendation(string recommendation)
    {
        if (string.IsNullOrWhiteSpace(recommendation))
        {
            return false;
        }

        var text = recommendation.Trim().ToLowerInvariant();
        var containsDisable =
            text.Contains("disable", StringComparison.Ordinal) ||
            text.Contains("turn off", StringComparison.Ordinal) ||
            text.Contains("remove", StringComparison.Ordinal);

        if (!containsDisable)
        {
            return false;
        }

        return text.Contains("profiler", StringComparison.Ordinal) ||
               text.Contains("instrumentation", StringComparison.Ordinal) ||
               text.Contains("tracer", StringComparison.Ordinal) ||
               text.Contains("monitoring", StringComparison.Ordinal) ||
               text.Contains("datadog", StringComparison.Ordinal) ||
               text.Contains("application insights", StringComparison.Ordinal) ||
               text.Contains("dynatrace", StringComparison.Ordinal);
    }

    private static List<string> DeduplicateAndLimit(List<string> items, int limit)
    {
        if (items.Count <= 1)
        {
            return items;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(Math.Min(items.Count, limit));
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            var trimmed = item.Trim();
            if (!seen.Add(trimmed))
            {
                continue;
            }

            result.Add(trimmed);
            if (result.Count >= limit)
            {
                break;
            }
        }

        return result;
    }

    private static int GetConfidenceRank(string? confidence)
    {
        var normalized = (confidence ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "high" => 3,
            "medium" => 2,
            "low" => 1,
            _ => 0
        };
    }

    private static int? ParseHypothesisIdNumber(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var match = Regex.Match(id.Trim(), @"^H(?<n>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups["n"].Value, out var parsed) && parsed > 0 ? parsed : null;
    }

    private async Task EnforceHighConfidenceJudgeAndValidationAsync(
        AiAnalysisResult result,
        List<ExecutedCommand> commandsExecuted,
        AiEvidenceLedger evidenceLedger,
        AiHypothesisTracker hypothesisTracker,
        InternalToolChoiceModeCache internalToolChoiceModeCache,
        string passName,
        int iteration,
        int maxTokens,
        string? lastModel,
        string? traceRunDir,
        JudgeAttemptState judgeAttemptState,
        string finalizationReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(commandsExecuted);
        ArgumentNullException.ThrowIfNull(evidenceLedger);
        ArgumentNullException.ThrowIfNull(hypothesisTracker);
        ArgumentNullException.ThrowIfNull(internalToolChoiceModeCache);
        ArgumentNullException.ThrowIfNull(passName);
        ArgumentNullException.ThrowIfNull(judgeAttemptState);
        ArgumentNullException.ThrowIfNull(finalizationReason);

        var confidence = (result.Confidence ?? string.Empty).Trim();
        if (!string.Equals(confidence, "high", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if ((result.Evidence?.Count ?? 0) >= MinHighConfidenceEvidenceItems
            && HasNonReportEvidenceToolCalls(commandsExecuted)
            && hypothesisTracker.Hypotheses.Count >= 3
            && evidenceLedger.Items.Count > 0
            && !judgeAttemptState.Attempted)
        {
            judgeAttemptState.Attempted = true;
            result.Judge ??= await TryRunJudgeStepAsync(
                    passName: passName,
                    systemPrompt: JudgeSystemPrompt,
                    evidenceLedger: evidenceLedger,
                    hypothesisTracker: hypothesisTracker,
                    internalToolChoiceModeCache: internalToolChoiceModeCache,
                    iteration: iteration,
                    maxTokens: maxTokens,
                    lastModel: lastModel,
                    traceRunDir: traceRunDir,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        if (!TryGetAnalysisCompleteValidationError(result, commandsExecuted, evidenceLedger, hypothesisTracker, out var validationError))
        {
            return;
        }

        // Avoid returning high confidence when we couldn't satisfy proof obligations (e.g., judge step missing).
        result.Confidence = "medium";

        var note = $"Note: confidence downgraded to medium because high-confidence proof obligations were not met during finalization ({finalizationReason}).";
        if (string.IsNullOrWhiteSpace(result.Reasoning))
        {
            result.Reasoning = note;
        }
        else if (!result.Reasoning.Contains(note, StringComparison.OrdinalIgnoreCase))
        {
            result.Reasoning = result.Reasoning.TrimEnd() + "\n\n" + note;
        }

        WriteSamplingTraceFile(
            traceRunDir,
            $"iter-{iteration:0000}-finalization-validation-error.json",
            new { passName, iteration, finalizationReason, validationError });
    }

    private static void PruneUnrespondedToolUsesFromLastAssistantMessage(
        List<SamplingMessage> messages,
        HashSet<string> respondedToolUseIds)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(respondedToolUseIds);

        if (respondedToolUseIds.Count == 0)
        {
            // Fast path: remove all tool_use blocks. This avoids orphan tool calls when we're finalizing immediately.
            var lastAssistantIndex = messages.FindLastIndex(m => m.Role == Role.Assistant);
            if (lastAssistantIndex < 0 || messages[lastAssistantIndex].Content == null)
            {
                return;
            }

            var contentWithoutToolUses = messages[lastAssistantIndex].Content!
                .Where(block => block is not ToolUseContentBlock)
                .ToList();

            messages[lastAssistantIndex].Content = contentWithoutToolUses;
            return;
        }

        // When some tool calls were executed in the current assistant turn, remove any remaining tool_use blocks
        // that don't have a corresponding tool_result, preserving protocol consistency.
        var lastAssistantWithToolUsesIndex = messages.FindLastIndex(m => m.Role == Role.Assistant && m.Content?.OfType<ToolUseContentBlock>().Any() == true);
        if (lastAssistantWithToolUsesIndex < 0 || messages[lastAssistantWithToolUsesIndex].Content == null)
        {
            return;
        }

        var prunedContent = messages[lastAssistantWithToolUsesIndex].Content!
            .Where(block => block is not ToolUseContentBlock toolUse || !string.IsNullOrWhiteSpace(toolUse.Id) && respondedToolUseIds.Contains(toolUse.Id))
            .ToList();

        messages[lastAssistantWithToolUsesIndex].Content = prunedContent;
    }

    /// <summary>
    /// Uses MCP sampling to rewrite <c>analysis.summary.description</c> and <c>analysis.summary.recommendations</c>
    /// based on the full crash report (and existing <c>analysis.aiAnalysis</c>, when present).
    /// </summary>
    public async Task<AiSummaryResult?> RewriteSummaryAsync(
        CrashAnalysisResult report,
        string fullReportJson,
        IDebuggerManager debugger,
        IManagedObjectInspector? clrMdAnalyzer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(fullReportJson);
        ArgumentNullException.ThrowIfNull(debugger);

        if (!_samplingClient.IsSamplingSupported)
        {
            return new AiSummaryResult
            {
                Error = "AI summary rewrite unavailable: MCP client does not support sampling.",
                Description = string.Empty,
                Iterations = 0
            };
        }

        if (!_samplingClient.IsToolUseSupported)
        {
            return new AiSummaryResult
            {
                Error = "AI summary rewrite unavailable: MCP client does not support tool use for sampling.",
                Description = string.Empty,
                Iterations = 0
            };
        }

        var tools = SamplingTools.GetSummaryRewriteTools();
        var initialPrompt = BuildSummaryRewritePrompt(report, fullReportJson);

        var traceRunDir = InitializeSamplingTraceDirectory("summary-rewrite");

        return await RunSamplingPassAsync<AiSummaryResult>(
                passName: "summary-rewrite",
                systemPrompt: SummaryRewriteSystemPrompt,
                initialPrompt: initialPrompt,
                completionToolName: "analysis_summary_rewrite_complete",
                tools: tools,
                fullReportJson: fullReportJson,
                report: report,
                debugger: debugger,
                clrMdAnalyzer: clrMdAnalyzer,
                traceRunDir: traceRunDir,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Uses MCP sampling to generate an evidence-backed narrative of thread activity at the time of the dump.
    /// The result is suitable for storing under <c>analysis.aiAnalysis.threadNarrative</c> and
    /// <c>analysis.threads.summary.description</c>.
    /// </summary>
    public async Task<AiThreadNarrativeResult?> GenerateThreadNarrativeAsync(
        CrashAnalysisResult report,
        string fullReportJson,
        IDebuggerManager debugger,
        IManagedObjectInspector? clrMdAnalyzer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(fullReportJson);
        ArgumentNullException.ThrowIfNull(debugger);

        if (!_samplingClient.IsSamplingSupported)
        {
            return new AiThreadNarrativeResult
            {
                Error = "AI thread narrative unavailable: MCP client does not support sampling.",
                Description = string.Empty,
                Confidence = "low",
                Iterations = 0
            };
        }

        if (!_samplingClient.IsToolUseSupported)
        {
            return new AiThreadNarrativeResult
            {
                Error = "AI thread narrative unavailable: MCP client does not support tool use for sampling.",
                Description = string.Empty,
                Confidence = "low",
                Iterations = 0
            };
        }

        var tools = SamplingTools.GetThreadNarrativeTools();
        var initialPrompt = BuildThreadNarrativePrompt(report, fullReportJson);

        var traceRunDir = InitializeSamplingTraceDirectory("thread-narrative");

        return await RunSamplingPassAsync<AiThreadNarrativeResult>(
                passName: "thread-narrative",
                systemPrompt: ThreadNarrativeSystemPrompt,
                initialPrompt: initialPrompt,
                completionToolName: "analysis_thread_narrative_complete",
                tools: tools,
                fullReportJson: fullReportJson,
                report: report,
                debugger: debugger,
                clrMdAnalyzer: clrMdAnalyzer,
                traceRunDir: traceRunDir,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsInternalMetaTool(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        return toolName.Equals(AnalysisEvidenceAddToolName, StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals(AnalysisHypothesisRegisterToolName, StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals(AnalysisHypothesisScoreToolName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExecuteInternalMetaTool(
        string toolName,
        JsonElement toolInput,
        AiEvidenceLedger evidenceLedger,
        AiHypothesisTracker hypothesisTracker,
        bool evidenceProvenanceEnabled)
    {
        ArgumentNullException.ThrowIfNull(evidenceLedger);
        ArgumentNullException.ThrowIfNull(hypothesisTracker);

        try
        {
            var normalized = (toolName ?? string.Empty).Trim();
            if (normalized.Equals(AnalysisEvidenceAddToolName, StringComparison.OrdinalIgnoreCase))
            {
                if (!evidenceProvenanceEnabled)
                {
                    return ExecuteLegacyEvidenceAdd(toolInput, evidenceLedger);
                }

                const int maxRejectedItems = 25;
                var annotated = new List<string>();
                var rejected = new List<object>();
                var ignoredDuplicates = 0;
                var invalidItems = 0;
                var legacyEvidenceNotes = 0;

                if (toolInput.ValueKind != JsonValueKind.Object)
                {
                    invalidItems = 1;
                    rejected.Add(new
                    {
                        index = 0,
                        reason = "input_must_be_object",
                        hint = "analysis_evidence_add is annotation-only. Provide { items: [ { id: \"E#\", whyItMatters?: \"...\", tags?: [...], notes?: [...] } ] }. Do not retry."
                    });

                    return JsonSerializer.Serialize(new
                    {
                        annotated,
                        ignoredDuplicates,
                        invalidItems,
                        rejected,
                        total = evidenceLedger.Items.Count,
                        advice = "Continue analysis; do not retry analysis_evidence_add unless you have new annotations for existing evidence IDs."
                    });
                }

                if (!toolInput.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
                {
                    invalidItems = 1;
                    rejected.Add(new
                    {
                        index = 0,
                        reason = "items_required",
                        hint = "analysis_evidence_add.items must be an array. This tool is annotation-only; evidence facts are auto-generated from tool outputs. Do not retry."
                    });

                    return JsonSerializer.Serialize(new
                    {
                        annotated,
                        ignoredDuplicates,
                        invalidItems,
                        rejected,
                        total = evidenceLedger.Items.Count,
                        advice = "Continue analysis; do not retry analysis_evidence_add unless you have new annotations for existing evidence IDs."
                    });
                }

                var seenAnnotated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var index = 0;

                foreach (var item in itemsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        invalidItems++;
                        if (rejected.Count < maxRejectedItems)
                        {
                            rejected.Add(new
                            {
                                index,
                                reason = "item_must_be_object",
                                hint = "Each item must be an object. Example: { id: \"E12\", whyItMatters: \"...\", tags: [\"...\"] }."
                            });
                        }

                        index++;
                        continue;
                    }

                    var evidenceId = TryGetString(item, "id") ?? TryGetString(item, "evidenceId") ?? string.Empty;
                    var whyItMatters = TryGetString(item, "whyItMatters");
                    var tags = TryGetStringArray(item, "tags");

                    var notes = TryGetStringArray(item, "notes") ?? [];
                    var note = TryGetString(item, "note");
                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        notes.Add(note);
                    }

                    var legacySource = TryGetString(item, "source");
                    var legacyFinding = TryGetString(item, "finding");
                    if (!string.IsNullOrWhiteSpace(legacySource) || !string.IsNullOrWhiteSpace(legacyFinding))
                    {
                        legacyEvidenceNotes++;
                        var combined = string.IsNullOrWhiteSpace(legacySource)
                            ? legacyFinding ?? string.Empty
                            : string.IsNullOrWhiteSpace(legacyFinding)
                                ? legacySource ?? string.Empty
                                : $"{legacySource!.Trim()} -> {legacyFinding!.Trim()}";

                        if (!string.IsNullOrWhiteSpace(combined))
                        {
                            notes.Add(combined);
                        }
                    }

                    var normalizedNotes = notes.Count == 0 ? null : notes;

                    var status = evidenceLedger.Annotate(
                        evidenceId,
                        whyItMatters,
                        tags,
                        normalizedNotes,
                        out var normalizedId);

                    switch (status)
                    {
                        case AiEvidenceLedgerAnnotationStatus.Annotated:
                            if (seenAnnotated.Add(normalizedId))
                            {
                                annotated.Add(normalizedId);
                            }
                            break;
                        case AiEvidenceLedgerAnnotationStatus.NoChange:
                            ignoredDuplicates++;
                            break;
                        case AiEvidenceLedgerAnnotationStatus.InvalidId:
                            invalidItems++;
                            if (rejected.Count < maxRejectedItems)
                            {
                                rejected.Add(new
                                {
                                    index,
                                    reason = "invalid_or_missing_id",
                                    hint = "Provide an existing evidence ID (E#) from evidenceLedger. This tool cannot create new evidence."
                                });
                            }
                            break;
                        case AiEvidenceLedgerAnnotationStatus.UnknownId:
                            if (rejected.Count < maxRejectedItems)
                            {
                                rejected.Add(new
                                {
                                    index,
                                    id = string.IsNullOrWhiteSpace(evidenceId) ? null : evidenceId.Trim(),
                                    reason = "unknown_evidence_id",
                                    hint = "Evidence ID must exist in evidenceLedger. Use an existing E#; evidence facts are auto-generated from tool outputs."
                                });
                            }
                            break;
                        default:
                            invalidItems++;
                            if (rejected.Count < maxRejectedItems)
                            {
                                rejected.Add(new { index, reason = "unknown_status" });
                            }
                            break;
                    }

                    index++;
                }

                return JsonSerializer.Serialize(new
                {
                    annotated,
                    ignoredDuplicates,
                    invalidItems,
                    legacyEvidenceNotes,
                    rejected,
                    total = evidenceLedger.Items.Count,
                    advice = "analysis_evidence_add is annotation-only; evidence facts are auto-generated from tool outputs. Continue analysis; do not retry unless you have new annotations."
                });
            }

            if (normalized.Equals(AnalysisHypothesisRegisterToolName, StringComparison.OrdinalIgnoreCase))
            {
                var hypotheses = ParseHypotheses(toolInput);
                var registered = hypothesisTracker.Register(hypotheses);
                return JsonSerializer.Serialize(new
                {
                    added = registered.AddedIds,
                    updated = registered.UpdatedIds,
                    invalidItems = registered.InvalidItems,
                    ignoredDuplicates = registered.IgnoredDuplicates,
                    ignoredDuplicateIds = registered.IgnoredDuplicateIds,
                    ignoredAtCapacity = registered.IgnoredAtCapacity,
                    total = hypothesisTracker.Hypotheses.Count
                });
            }

            if (normalized.Equals(AnalysisHypothesisScoreToolName, StringComparison.OrdinalIgnoreCase))
            {
                var updates = ParseHypothesisUpdates(toolInput);
                var updated = hypothesisTracker.Update(updates);
                return JsonSerializer.Serialize(new
                {
                    updated = updated.UpdatedIds,
                    invalidItems = updated.InvalidItems,
                    unknownHypothesisIds = updated.UnknownHypothesisIds,
                    unknownEvidenceIds = updated.UnknownEvidenceIds,
                    total = hypothesisTracker.Hypotheses.Count
                });
            }

            return JsonSerializer.Serialize(new { error = "Unknown meta tool.", tool = toolName });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, tool = toolName });
        }
    }

    private static string ExecuteLegacyEvidenceAdd(JsonElement toolInput, AiEvidenceLedger evidenceLedger)
    {
        ArgumentNullException.ThrowIfNull(evidenceLedger);

        const int maxRejectedItems = 25;
        var rejected = new List<object>();
        var invalidItems = 0;

        if (toolInput.ValueKind != JsonValueKind.Object)
        {
            invalidItems = 1;
            rejected.Add(new
            {
                index = 0,
                reason = "input_must_be_object",
                hint = "Legacy evidence_add mode: provide { items: [ { id?: \"E#\", source: \"...\", finding: \"...\" } ] }."
            });

            return JsonSerializer.Serialize(new
            {
                added = Array.Empty<string>(),
                updated = Array.Empty<string>(),
                invalidItems,
                ignoredDuplicates = 0,
                ignoredDuplicateIds = Array.Empty<string>(),
                ignoredAtCapacity = 0,
                rejected,
                total = evidenceLedger.Items.Count,
                advice = "Legacy evidence_add mode: include source+finding to add evidence. Continue analysis; do not retry without adding missing fields."
            });
        }

        if (!toolInput.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
        {
            invalidItems = 1;
            rejected.Add(new
            {
                index = 0,
                reason = "items_required",
                hint = "Legacy evidence_add mode requires items[]. Provide { items: [ { id?: \"E#\", source: \"...\", finding: \"...\" } ] }."
            });

            return JsonSerializer.Serialize(new
            {
                added = Array.Empty<string>(),
                updated = Array.Empty<string>(),
                invalidItems,
                ignoredDuplicates = 0,
                ignoredDuplicateIds = Array.Empty<string>(),
                ignoredAtCapacity = 0,
                rejected,
                total = evidenceLedger.Items.Count,
                advice = "Legacy evidence_add mode: include source+finding to add evidence. Continue analysis; do not retry without adding missing fields."
            });
        }

        var items = new List<AiEvidenceLedgerItem>();
        var index = 0;

        foreach (var item in itemsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                invalidItems++;
                if (rejected.Count < maxRejectedItems)
                {
                    rejected.Add(new { index, reason = "item_must_be_object" });
                }

                index++;
                continue;
            }

            var source = TryGetString(item, "source");
            var finding = TryGetString(item, "finding");
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(finding))
            {
                invalidItems++;
                if (rejected.Count < maxRejectedItems)
                {
                    rejected.Add(new
                    {
                        index,
                        reason = "source_and_finding_required",
                        hint = "Legacy evidence_add mode requires both source and finding to add evidence."
                    });
                }

                index++;
                continue;
            }

            var notes = TryGetStringArray(item, "notes") ?? [];
            var note = TryGetString(item, "note");
            if (!string.IsNullOrWhiteSpace(note))
            {
                notes.Add(note);
            }

            items.Add(new AiEvidenceLedgerItem
            {
                Id = TryGetString(item, "id") ?? TryGetString(item, "evidenceId") ?? string.Empty,
                Source = source,
                Finding = finding,
                WhyItMatters = TryGetString(item, "whyItMatters"),
                Tags = TryGetStringArray(item, "tags"),
                Notes = notes.Count == 0 ? null : notes
            });

            index++;
        }

        var addResult = items.Count > 0 ? evidenceLedger.AddOrUpdate(items) : new AiEvidenceLedgerAddResult();
        return JsonSerializer.Serialize(new
        {
            added = addResult.AddedIds,
            updated = addResult.UpdatedIds,
            invalidItems = invalidItems + addResult.InvalidItems,
            ignoredDuplicates = addResult.IgnoredDuplicates,
            ignoredDuplicateIds = addResult.IgnoredDuplicateIds,
            ignoredAtCapacity = addResult.IgnoredAtCapacity,
            rejected,
            total = evidenceLedger.Items.Count,
            advice = "Legacy evidence_add mode: continue analysis; do not retry unless you have new evidence items to add."
        });
    }

    private static List<AiHypothesis> ParseHypotheses(JsonElement toolInput)
    {
        if (toolInput.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("analysis_hypothesis_register input must be an object.");
        }

        if (!toolInput.TryGetProperty("hypotheses", out var hypsEl) || hypsEl.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("analysis_hypothesis_register.hypotheses is required.");
        }

        var hypotheses = new List<AiHypothesis>();
        foreach (var item in hypsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            hypotheses.Add(new AiHypothesis
            {
                Id = TryGetString(item, "id") ?? string.Empty,
                Hypothesis = TryGetString(item, "hypothesis") ?? string.Empty,
                Confidence = TryGetString(item, "confidence") ?? "unknown",
                Unknowns = TryGetStringArray(item, "unknowns"),
                TestsToRun = TryGetStringArray(item, "testsToRun")
            });
        }

        return hypotheses;
    }

    private static List<AiHypothesisUpdate> ParseHypothesisUpdates(JsonElement toolInput)
    {
        if (toolInput.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("analysis_hypothesis_score input must be an object.");
        }

        if (!toolInput.TryGetProperty("updates", out var updatesEl) || updatesEl.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("analysis_hypothesis_score.updates is required.");
        }

        var updates = new List<AiHypothesisUpdate>();
        foreach (var item in updatesEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            updates.Add(new AiHypothesisUpdate
            {
                Id = TryGetString(item, "id") ?? string.Empty,
                Confidence = TryGetString(item, "confidence"),
                SupportsEvidenceIds = TryGetStringArray(item, "supportsEvidenceIds"),
                ContradictsEvidenceIds = TryGetStringArray(item, "contradictsEvidenceIds"),
                Notes = TryGetString(item, "notes")
            });
        }

        return updates;
    }

    private static bool HasEvidenceToolCalls(List<ExecutedCommand> commandsExecuted)
    {
        foreach (var cmd in commandsExecuted)
        {
            var tool = cmd.Tool ?? string.Empty;
            if (tool.Equals("exec", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("report_get", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("inspect", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("get_thread_stack", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasNonReportEvidenceToolCalls(List<ExecutedCommand> commandsExecuted)
    {
        foreach (var cmd in commandsExecuted)
        {
            var tool = cmd.Tool ?? string.Empty;
            if (tool.Equals("exec", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("inspect", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("get_thread_stack", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildAnalysisCompleteValidationRefusalMessage(string validationError, int refusalCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Cannot finalize yet: analysis_complete did not meet proof obligations for the requested confidence.");
        sb.AppendLine();
        sb.AppendLine("Fix one of the following:");
        sb.AppendLine("- Gather additional evidence with tools (report_get/exec/inspect/get_thread_stack), then call analysis_complete again.");
        sb.AppendLine("- OR lower confidence if key verification steps are missing.");
        sb.AppendLine("- OR remove/qualify any claims that are not directly supported by tool outputs.");
        sb.AppendLine();
        sb.AppendLine("Validation errors:");
        sb.AppendLine(validationError.TrimEnd());
        sb.AppendLine();
        sb.AppendLine($"Refusal count: {refusalCount}");
        return sb.ToString().TrimEnd();
    }

    private static bool TryGetAnalysisCompleteValidationError(
        AiAnalysisResult completed,
        List<ExecutedCommand> commandsExecuted,
        AiEvidenceLedger? evidenceLedger,
        AiHypothesisTracker? hypothesisTracker,
        out string validationError)
    {
        var errors = new List<string>();

        var confidence = (completed.Confidence ?? string.Empty).Trim().ToLowerInvariant();
        var evidence = completed.Evidence ?? [];
        var validEvidenceIds = BuildValidEvidenceIdSet(evidenceLedger);
        var validHypothesisIds = BuildValidHypothesisIdSet(hypothesisTracker);

        if (evidence.Count == 0)
        {
            errors.Add("- analysis_complete.evidence must be a non-empty list.");
        }
        else
        {
            var evidenceCitationIssues = GetEvidenceCitationIssues(evidence, validEvidenceIds);
            if (evidenceCitationIssues.Count > 0)
            {
                errors.Add($"- Evidence items must cite a tool call or report_get path. Missing citations in items: {string.Join(", ", evidenceCitationIssues)}.");
            }

            var corpus = BuildEvidenceCorpus(commandsExecuted);
            var unsupportedEvidenceItems = GetUnsupportedEvidenceItems(evidence, corpus, validEvidenceIds);
            if (unsupportedEvidenceItems.Count > 0)
            {
                errors.Add($"- Evidence items must be grounded in tool outputs. The following evidence items appear unsupported: {string.Join(", ", unsupportedEvidenceItems)}.");
            }

            if (confidence == "high" && evidence.Count < MinHighConfidenceEvidenceItems)
            {
                errors.Add($"- confidence=high requires at least {MinHighConfidenceEvidenceItems} independent evidence items.");
            }

            if (confidence == "high")
            {
                // High-confidence conclusions must not introduce structured facts (addresses, module names, versions,
                // fully-qualified symbols, etc.) that do not appear anywhere in the tool outputs.
                var unsupportedFacts = FindUnsupportedStructuredFacts(completed.RootCause ?? string.Empty, corpus);
                if (unsupportedFacts.Count > 0)
                {
                    errors.Add($"- rootCause contains structured facts not present in any tool output: {string.Join(", ", unsupportedFacts)}.");
                }

                if (!HasNonReportEvidenceToolCalls(commandsExecuted))
                {
                    errors.Add("- confidence=high requires at least one exec/inspect/get_thread_stack verification step (or lower confidence).");
                }

                if (hypothesisTracker == null || hypothesisTracker.Hypotheses.Count < 3)
                {
                    errors.Add("- confidence=high requires at least 3 competing hypotheses (register via analysis_hypothesis_register) so the judge can reject top alternatives and reduce variance.");
                }
                else
                {
                    var judgeIssues = GetJudgeIssues(completed.Judge, validEvidenceIds, validHypothesisIds);
                    if (judgeIssues.Count > 0)
                    {
                        errors.AddRange(judgeIssues.Select(i => "- " + i));
                    }
                }
            }
        }

        if (errors.Count == 0)
        {
            validationError = string.Empty;
            return false;
        }

        validationError = string.Join("\n", errors);
        return true;
    }

    private static HashSet<string> BuildValidEvidenceIdSet(AiEvidenceLedger? evidenceLedger)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (evidenceLedger == null)
        {
            return set;
        }

        foreach (var item in evidenceLedger.Items)
        {
            var id = item?.Id;
            if (!string.IsNullOrWhiteSpace(id))
            {
                set.Add(id.Trim());
            }
        }

        return set;
    }

    private static HashSet<string> BuildValidHypothesisIdSet(AiHypothesisTracker? hypothesisTracker)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hypothesisTracker == null)
        {
            return set;
        }

        foreach (var hypothesis in hypothesisTracker.Hypotheses)
        {
            if (hypothesis == null)
            {
                continue;
            }

            var id = hypothesis.Id;
            if (!string.IsNullOrWhiteSpace(id))
            {
                set.Add(id.Trim());
            }
        }

        return set;
    }

    private static List<string> GetJudgeIssues(
        AiJudgeResult? judge,
        IReadOnlySet<string> validEvidenceIds,
        IReadOnlySet<string> validHypothesisIds)
    {
        var issues = new List<string>();

        if (judge == null)
        {
            issues.Add("confidence=high requires an internal judge step that selects a hypothesis and explicitly rejects top alternatives with evidence IDs.");
            return issues;
        }

        var judgeConfidence = (judge.Confidence ?? string.Empty).Trim();
        if (!string.Equals(judgeConfidence, "high", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("confidence=high requires judge.confidence=\"high\".");
        }

        if (string.IsNullOrWhiteSpace(judge.SelectedHypothesisId))
        {
            issues.Add("judge.selectedHypothesisId is required for confidence=high.");
        }
        else if (validHypothesisIds.Count > 0 && !validHypothesisIds.Contains(judge.SelectedHypothesisId.Trim()))
        {
            issues.Add($"judge.selectedHypothesisId '{judge.SelectedHypothesisId.Trim()}' does not match any registered hypothesis ID.");
        }

        var supports = judge.SupportsEvidenceIds ?? [];
        if (supports.Count == 0)
        {
            issues.Add("judge.supportsEvidenceIds must be a non-empty list of evidence IDs (E#).");
        }
        else
        {
            var uniqueSupports = supports
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (uniqueSupports.Count < 3)
            {
                issues.Add("confidence=high requires judge.supportsEvidenceIds to contain at least 3 distinct evidence IDs.");
            }

            var unknown = uniqueSupports.Where(id => !IsKnownEvidenceId(id, validEvidenceIds)).Take(5).ToList();
            if (unknown.Count > 0)
            {
                issues.Add($"judge.supportsEvidenceIds contains unknown evidence IDs: {string.Join(", ", unknown)}.");
            }
        }

        var rejected = judge.RejectedHypotheses ?? [];
        if (rejected.Count < 2)
        {
            issues.Add("judge.rejectedHypotheses must contain at least 2 rejected alternatives for confidence=high.");
        }
        else
        {
            var selected = (judge.SelectedHypothesisId ?? string.Empty).Trim();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in rejected)
            {
                if (item == null)
                {
                    continue;
                }

                var hid = (item.HypothesisId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(hid))
                {
                    issues.Add("judge.rejectedHypotheses contains an entry with missing hypothesisId.");
                    continue;
                }

                if (!seen.Add(hid))
                {
                    duplicateIds.Add(hid);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(selected) && hid.Equals(selected, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add("judge.rejectedHypotheses must not reject the selected hypothesis.");
                }

                if (validHypothesisIds.Count > 0 && !validHypothesisIds.Contains(hid))
                {
                    issues.Add($"judge.rejectedHypotheses references unknown hypothesisId '{hid}'.");
                }

                if (item.ContradictsEvidenceIds == null || item.ContradictsEvidenceIds.Count == 0)
                {
                    issues.Add($"judge.rejectedHypotheses '{hid}' must include at least one contradicting evidence ID.");
                }
                else
                {
                    var unknownEvidence = item.ContradictsEvidenceIds
                        .Where(id => !IsKnownEvidenceId(id, validEvidenceIds))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(5)
                        .ToList();
                    if (unknownEvidence.Count > 0)
                    {
                        issues.Add($"judge.rejectedHypotheses '{hid}' contains unknown evidence IDs: {string.Join(", ", unknownEvidence)}.");
                    }
                }

                if (string.IsNullOrWhiteSpace(item.Reason))
                {
                    issues.Add($"judge.rejectedHypotheses '{hid}' must include a non-empty reason.");
                }
            }

            if (seen.Count < 2)
            {
                issues.Add("judge.rejectedHypotheses must contain at least 2 distinct rejected alternatives for confidence=high.");
            }

            if (duplicateIds.Count > 0)
            {
                issues.Add($"judge.rejectedHypotheses contains duplicate hypothesisId values: {string.Join(", ", duplicateIds.Take(3))}.");
            }
        }

        return issues;
    }

    private static bool IsKnownEvidenceId(string? id, IReadOnlySet<string> validEvidenceIds)
    {
        if (string.IsNullOrWhiteSpace(id) || validEvidenceIds.Count == 0)
        {
            return false;
        }

        var trimmed = id.Trim();
        var match = Regex.Match(trimmed, @"^E(?<n>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        var n = match.Groups["n"].Value.TrimStart('0');
        if (string.IsNullOrWhiteSpace(n))
        {
            return false;
        }

        var normalized = "E" + n;
        return validEvidenceIds.Contains(normalized) || validEvidenceIds.Contains(trimmed);
    }

    private static List<string> GetEvidenceCitationIssues(List<string> evidence, IReadOnlySet<string> validEvidenceIds)
    {
        var issues = new List<string>();
        for (var i = 0; i < evidence.Count; i++)
        {
            var item = evidence[i];
            if (!LooksLikeToolCitation(item) && !IsValidEvidenceLedgerCitation(item, validEvidenceIds))
            {
                issues.Add($"#{i + 1}");
                if (issues.Count >= 5)
                {
                    break;
                }
            }
        }

        return issues;
    }

    private static bool IsValidEvidenceLedgerCitation(string? evidenceItem, IReadOnlySet<string> validEvidenceIds)
    {
        if (string.IsNullOrWhiteSpace(evidenceItem) || validEvidenceIds.Count == 0)
        {
            return false;
        }

        var match = Regex.Match(evidenceItem, @"^\s*(E\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        var id = match.Groups[1].Value.Trim();
        if (!Regex.IsMatch(id, @"^E\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return false;
        }

        // Normalize casing ("e12" -> "E12") for set membership.
        var normalized = "E" + id.Substring(1).TrimStart('0');
        if (normalized == "E")
        {
            return false;
        }

        return validEvidenceIds.Contains(normalized) || validEvidenceIds.Contains(id);
    }

    private static bool LooksLikeToolCitation(string? evidenceItem)
    {
        if (string.IsNullOrWhiteSpace(evidenceItem))
        {
            return false;
        }

        var s = evidenceItem;
        return s.Contains("report_get", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("exec", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("inspect", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("get_thread_stack", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("analysis.", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("metadata", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildEvidenceCorpus(List<ExecutedCommand> commandsExecuted)
    {
        // Keep the corpus bounded to avoid pathological memory growth when a provider returns large tool outputs.
        const int maxChars = 250_000;
        var sb = new StringBuilder(Math.Min(32_768, maxChars));

        foreach (var cmd in commandsExecuted)
        {
            if (sb.Length >= maxChars)
            {
                break;
            }

            sb.AppendLine(cmd.Tool ?? string.Empty);
            if (cmd.Input.ValueKind != JsonValueKind.Undefined && cmd.Input.ValueKind != JsonValueKind.Null)
            {
                sb.AppendLine(cmd.Input.ToString());
            }
            sb.AppendLine(cmd.Output ?? string.Empty);
            sb.AppendLine();
        }

        return DecodeAsciiUnicodeEscapes(sb.ToString());
    }

    private static List<string> GetUnsupportedEvidenceItems(List<string> evidence, string corpus, IReadOnlySet<string> validEvidenceIds)
    {
        var unsupported = new List<string>();
        for (var i = 0; i < evidence.Count; i++)
        {
            var item = evidence[i] ?? string.Empty;
            if (IsValidEvidenceLedgerCitation(item, validEvidenceIds))
            {
                continue;
            }

            var facts = ExtractStructuredFacts(item);
            if (facts.Count == 0)
            {
                // If the evidence item contains no structured facts, we can't validate it reliably; allow it.
                continue;
            }

            var hasAnyMatch = facts.Any(f => corpus.Contains(f, StringComparison.OrdinalIgnoreCase));
            if (!hasAnyMatch)
            {
                unsupported.Add($"#{i + 1}");
                if (unsupported.Count >= 5)
                {
                    break;
                }
            }
        }

        return unsupported;
    }

    private static List<string> FindUnsupportedStructuredFacts(string text, string corpus)
    {
        var facts = ExtractStructuredFacts(text);
        if (facts.Count == 0)
        {
            return [];
        }

        var unsupported = new List<string>();
        foreach (var fact in facts)
        {
            if (!corpus.Contains(fact, StringComparison.OrdinalIgnoreCase))
            {
                unsupported.Add(fact);
                if (unsupported.Count >= 10)
                {
                    break;
                }
            }
        }

        return unsupported;
    }

    private static readonly Regex UnicodeEscapeRegex = new(
        @"\\u(?<hex>[0-9a-fA-F]{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string DecodeAsciiUnicodeEscapes(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return UnicodeEscapeRegex.Replace(text, m =>
        {
            var hex = m.Groups["hex"].Value;
            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var codePoint))
            {
                return m.Value;
            }

            // Only decode ASCII printable range to avoid introducing unexpected Unicode/surrogates.
            if (codePoint < 0x20 || codePoint > 0x7E)
            {
                return m.Value;
            }

            return ((char)codePoint).ToString();
        });
    }

    private static readonly Regex HexAddressRegex = new(
        @"\b0x[0-9a-fA-F]{6,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BareHexRegex = new(
        @"\b[0-9a-fA-F]{12,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ModuleFileRegex = new(
        @"\b[\w\-.]+?\.(dll|so|exe)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VersionRegex = new(
        @"\b\d+\.\d+\.\d+(?:\.\d+)?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QualifiedSymbolRegex = new(
        @"\b[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_`+<>]*){2,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RiskKeywordRegex = new(
        @"\b(readytorun|r2r|ngen|publishtrimmed|illink|trimm(?:ed|ing)?|nativeaot|jit)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static HashSet<string> ExtractStructuredFacts(string? text)
    {
        var facts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return facts;
        }

        foreach (Match m in HexAddressRegex.Matches(text))
        {
            facts.Add(m.Value);
        }

        foreach (Match m in BareHexRegex.Matches(text))
        {
            // Avoid treating years/ids as facts by only accepting long hex runs.
            facts.Add(m.Value);
        }

        foreach (Match m in ModuleFileRegex.Matches(text))
        {
            facts.Add(m.Value);
        }

        foreach (Match m in VersionRegex.Matches(text))
        {
            facts.Add(m.Value);
        }

        foreach (Match m in QualifiedSymbolRegex.Matches(text))
        {
            facts.Add(m.Value);
        }

        foreach (Match m in RiskKeywordRegex.Matches(text))
        {
            facts.Add(m.Value);
        }

        return facts;
    }

    private static AiAnalysisResult? TryRepairAnalysisCompleteAfterValidationFailure(
        AiAnalysisResult completed,
        List<ExecutedCommand> commandsExecuted,
        string validationError,
        int validationRefusalCount)
    {
        if (completed == null)
        {
            return null;
        }

        // Only attempt repair when the provider appears stuck repeating analysis_complete without new evidence.
        // We aim to produce a defensible completion by downgrading confidence and grounding evidence in tool outputs.
        var repaired = new AiAnalysisResult
        {
            RootCause = completed.RootCause,
            Confidence = completed.Confidence,
            Reasoning = completed.Reasoning,
            Recommendations = completed.Recommendations,
            AdditionalFindings = completed.AdditionalFindings,
            Iterations = completed.Iterations,
            CommandsExecuted = completed.CommandsExecuted,
            Model = completed.Model,
            AnalyzedAt = completed.AnalyzedAt,
            Summary = completed.Summary,
            ThreadNarrative = completed.ThreadNarrative
        };

        repaired.Evidence = BuildAutoEvidenceList(commandsExecuted);

        if (string.Equals(repaired.Confidence?.Trim(), "high", StringComparison.OrdinalIgnoreCase))
        {
            repaired.Confidence = "medium";
        }

        var note = $"Note: auto-finalized after {validationRefusalCount} consecutive analysis_complete validation refusals without new evidence; confidence may be downgraded and evidence was auto-generated.";
        if (string.IsNullOrWhiteSpace(repaired.Reasoning))
        {
            repaired.Reasoning = note;
        }
        else if (!repaired.Reasoning.Contains(note, StringComparison.OrdinalIgnoreCase))
        {
            repaired.Reasoning = repaired.Reasoning.TrimEnd() + "\n\n" + note;
        }

        // Keep the last validation error for troubleshooting, but avoid making the output unbounded.
        var trimmedValidationError = TruncateText(validationError, maxChars: 2000);
        var errorNote = "Last validation errors (truncated):\n" + trimmedValidationError;
        repaired.Reasoning = repaired.Reasoning.TrimEnd() + "\n\n" + errorNote;

        if (TryGetAnalysisCompleteValidationError(repaired, commandsExecuted, evidenceLedger: null, hypothesisTracker: null, out _))
        {
            // Repair failed; keep refusing.
            return null;
        }

        return repaired;
    }

    private static string BuildAnalysisCompleteRefusalMessage(bool toolCallsWereCoissued, int refusalCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Cannot finalize yet.");
        sb.AppendLine();
        sb.AppendLine("Before calling analysis_complete, gather at least one additional piece of evidence using tools.");
        sb.AppendLine("Minimum recommended sequence:");
        sb.AppendLine("- report_get(path=\"analysis.summary\")");
        sb.AppendLine("- report_get(path=\"analysis.exception\", select=[\"type\",\"message\",\"hResult\"])");
        sb.AppendLine("- report_get(path=\"analysis.threads.faultingThread\")");
        sb.AppendLine("- report_get(path=\"analysis.environment.platform\")");
        sb.AppendLine("- report_get(path=\"analysis.environment.runtime\")");
        sb.AppendLine("- report_get(path=\"analysis.assemblies.items\", limit=25, select=[\"name\",\"assemblyVersion\",\"fileVersion\",\"path\"])");
        sb.AppendLine();
        if (toolCallsWereCoissued)
        {
            sb.AppendLine("Also: do not call analysis_complete in the same message as other tool calls.");
            sb.AppendLine("Execute evidence-gathering tools first, then call analysis_complete in a later message after you've read the tool outputs.");
            sb.AppendLine();
        }
        sb.AppendLine($"Refusal count: {refusalCount}");
        sb.AppendLine("Now call report_get (or exec/inspect/get_thread_stack) to gather evidence, then try analysis_complete again.");
        return sb.ToString().TrimEnd();
    }

    private async Task<T?> RunSamplingPassAsync<T>(
        string passName,
        string systemPrompt,
        string initialPrompt,
        string completionToolName,
        IList<Tool> tools,
        string fullReportJson,
        CrashAnalysisResult report,
        IDebuggerManager debugger,
        IManagedObjectInspector? clrMdAnalyzer,
        string? traceRunDir,
        CancellationToken cancellationToken)
        where T : class
    {
        var commandsExecuted = new List<ExecutedCommand>();
        var messages = new List<SamplingMessage>
        {
            new()
            {
                Role = Role.User,
                Content =
                [
                    new TextContentBlock { Text = initialPrompt }
                ]
            }
        };

        var maxIterations = GetMaxIterationsForSamplingPass(passName);
        var maxTokens = MaxTokensPerRequest > 0 ? MaxTokensPerRequest : 1024;
        var maxToolCalls = MaxToolCalls > 0 ? MaxToolCalls : 50;
        var maxToolUsesPerIteration = MaxToolUsesPerIteration > 0 ? MaxToolUsesPerIteration : 40;
        var maxTotalToolUses = GetMaxTotalToolUsesForSamplingPass(passName);

        string? lastModel = null;
        var toolIndexByIteration = new Dictionary<int, int>();
        var toolResultCache = new Dictionary<string, string>(StringComparer.Ordinal);
        var lastCheckpointIteration = 0;
        var commandsExecutedAtLastCheckpoint = 0;
        var internalToolChoiceModeCache = new InternalToolChoiceModeCache();
        var consecutiveNoProgressIterations = 0;

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            var uniqueToolCallsAtIterationStart = toolResultCache.Count;

            if (_toolHistoryModeCache.IsCheckpointOnly && messages.Count > 1 && commandsExecuted.Count > 0)
            {
                var checkpoint = BuildDeterministicCheckpointJson(
                    passName: passName,
                    commandsExecuted: commandsExecuted,
                    commandsExecutedAtLastCheckpoint: commandsExecutedAtLastCheckpoint);

                lastCheckpointIteration = iteration - 1;
                commandsExecutedAtLastCheckpoint = commandsExecuted.Count;
                messages.Clear();
                messages.Add(BuildCheckpointCarryForwardMessage(checkpoint, passName: passName));
            }

            CreateMessageResult? response = null;
            Exception? lastSamplingError = null;

            for (var attempt = 1; attempt <= Math.Max(1, MaxSamplingRequestAttempts); attempt++)
            {
                var requestMessages = new List<SamplingMessage>(messages);
                var request = new CreateMessageRequestParams
                {
                    SystemPrompt = systemPrompt,
                    Messages = requestMessages,
                    MaxTokens = maxTokens,
                    Tools = tools,
                    ToolChoice = new ToolChoice { Mode = "auto" }
                };

                try
                {
                    _logger.LogInformation(
                        "[AI] Sampling pass {Pass} iteration {Iteration} (attempt {Attempt}/{MaxAttempts})...",
                        passName,
                        iteration,
                        attempt,
                        MaxSamplingRequestAttempts);
                    LogSamplingRequestSummary(iteration, request);
                    var requestFileName = attempt == 1
                        ? $"iter-{iteration:0000}-request.json"
                        : $"iter-{iteration:0000}-attempt-{attempt:00}-request.json";
                    WriteSamplingTraceFile(traceRunDir, requestFileName, BuildTraceRequest(iteration, request));
                    response = await _samplingClient.RequestCompletionAsync(request, cancellationToken).ConfigureAwait(false);
                    if (response.Content != null && response.Content.Count > 0)
                    {
                        break;
                    }

                    lastSamplingError = new InvalidOperationException("The sampling client returned an empty response.");
                    _logger.LogWarning("[AI] Sampling pass {Pass} returned empty content at iteration {Iteration} (attempt {Attempt}/{MaxAttempts})", passName, iteration, attempt, MaxSamplingRequestAttempts);
                    var responseFileName = attempt == 1
                        ? $"iter-{iteration:0000}-response.json"
                        : $"iter-{iteration:0000}-attempt-{attempt:00}-response.json";
                    WriteSamplingTraceFile(traceRunDir, responseFileName, BuildTraceResponse(iteration, response));
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    lastSamplingError = ex;
                    if (IsStructuredToolHistoryUnsupported(ex) && _toolHistoryModeCache.MarkStructuredToolHistoryUnsupported())
                    {
                        _logger.LogInformation(
                            "[AI] Provider rejected structured tool history; switching to checkpoint-only history mode for the remainder of this run (pass {Pass}).",
                            passName);
                        WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-history-mode-fallback.json", new
                        {
                            passName,
                            iteration,
                            mode = "checkpoint-only",
                            reason = "structured_tool_history_unsupported",
                            message = ex.Message
                        });
                    }
                    _logger.LogWarning(ex, "[AI] Sampling pass {Pass} failed at iteration {Iteration} (attempt {Attempt}/{MaxAttempts})", passName, iteration, attempt, MaxSamplingRequestAttempts);
                    var errorFileName = attempt == 1
                        ? $"iter-{iteration:0000}-error.json"
                        : $"iter-{iteration:0000}-attempt-{attempt:00}-error.json";
                    WriteSamplingTraceFile(traceRunDir, errorFileName, new { passName, iteration, attempt, error = ex.ToString(), message = ex.Message });
                }

                if (attempt < Math.Max(1, MaxSamplingRequestAttempts) && messages.Count > 2)
                {
                    var fallbackCheckpoint = BuildDeterministicCheckpointJson(
                        passName: passName,
                        commandsExecuted: commandsExecuted,
                        commandsExecutedAtLastCheckpoint: commandsExecutedAtLastCheckpoint);
                    lastCheckpointIteration = iteration;
                    commandsExecutedAtLastCheckpoint = commandsExecuted.Count;
                    messages.Clear();
                    messages.Add(BuildCheckpointCarryForwardMessage(fallbackCheckpoint, passName: passName));
                }
            }

            if (response == null || response.Content == null || response.Content.Count == 0)
            {
                return passName switch
                {
                    "summary-rewrite" => new AiSummaryResult
                    {
                        Error = "AI summary rewrite failed: sampling request error.",
                        Description = string.Empty,
                        Iterations = iteration - 1,
                        CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                        AnalyzedAt = DateTime.UtcNow
                    } as T,
                    "thread-narrative" => new AiThreadNarrativeResult
                    {
                        Error = "AI thread narrative failed: sampling request error.",
                        Description = string.Empty,
                        Confidence = "low",
                        Iterations = iteration - 1,
                        CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                        AnalyzedAt = DateTime.UtcNow
                    } as T,
                    _ => null
                };
            }

            WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-response.json", BuildTraceResponse(iteration, response));
            lastModel = response.Model;

            messages.Add(new SamplingMessage
            {
                Role = Role.Assistant,
                Content = response.Content
            });

            var toolUses = response.Content?.OfType<ToolUseContentBlock>().ToList() ?? [];
            if (toolUses.Count == 0)
            {
                var assistantText = ExtractAssistantText(response);
                if (!string.IsNullOrWhiteSpace(assistantText) && TryParseFirstJsonObject(assistantText, out var json))
                {
                    var maybeCompletion = ParseCompletionTool<T>(
                        passName,
                        completionToolName,
                        json,
                        commandsExecuted,
                        iteration,
                        lastModel);
                    if (maybeCompletion != null)
                    {
                        return maybeCompletion;
                    }
                }

                continue;
            }

            ToolUseContentBlock? pendingComplete = null;
            JsonElement pendingCompleteInput = default;
            string? pendingCompleteId = null;
            var rewrittenToolUses = new List<(ToolUseContentBlock ToolUse, string Name, JsonElement Input)>(toolUses.Count);
            foreach (var toolUse in toolUses)
            {
                var (effectiveName, effectiveInput) = RewriteToolUseIfNeeded(toolUse, clrMdAnalyzer);
                rewrittenToolUses.Add((toolUse, effectiveName, effectiveInput));
            }

            var respondedToolUseIds = new HashSet<string>(StringComparer.Ordinal);
            var toolUsesExecutedThisIteration = 0;
            var toolUsesPrunedDueToLimit = false;

            foreach (var (toolUse, toolName, toolInput) in rewrittenToolUses)
            {
                LogRequestedToolSummary(iteration, toolUse, toolName, toolInput);

                if (string.Equals(toolName, completionToolName, StringComparison.OrdinalIgnoreCase))
                {
                    if (pendingComplete == null)
                    {
                        pendingComplete = toolUse;
                        pendingCompleteInput = toolInput;
                        pendingCompleteId = toolUse.Id;
                    }
                    continue;
                }

                if (toolUsesExecutedThisIteration >= maxToolUsesPerIteration)
                {
                    toolUsesPrunedDueToLimit = true;
                    _logger.LogWarning(
                        "[AI] Tool-use limit reached in pass {Pass} iteration {Iteration} (limit={Limit}); pruning remaining tool calls.",
                        passName,
                        iteration,
                        maxToolUsesPerIteration);
                    break;
                }

                if (toolResultCache.Count >= maxToolCalls)
                {
                    _logger.LogInformation("[AI] Unique tool call budget reached ({MaxToolCalls}); finalizing pass {Pass}.", maxToolCalls, passName);
                    PruneUnrespondedToolUsesFromLastAssistantMessage(messages, respondedToolUseIds);
                    return await FinalizePassAfterToolBudgetExceededAsync<T>(
                            passName: passName,
                            systemPrompt: systemPrompt,
                            messages: messages,
                            commandsExecuted: commandsExecuted,
                            iteration: iteration,
                            maxTokens: maxTokens,
                            maxToolCalls: maxToolCalls,
                            lastModel: lastModel,
                            traceRunDir: traceRunDir,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }

                if (commandsExecuted.Count >= maxTotalToolUses)
                {
                    _logger.LogInformation(
                        "[AI] Tool-use budget reached in pass {Pass} (totalToolUses={TotalToolUses}, limit={Limit}); finalizing.",
                        passName,
                        commandsExecuted.Count,
                        maxTotalToolUses);
                    PruneUnrespondedToolUsesFromLastAssistantMessage(messages, respondedToolUseIds);
                    return await FinalizePassAfterTotalToolUseBudgetExceededAsync<T>(
                            passName: passName,
                            systemPrompt: systemPrompt,
                            messages: messages,
                            commandsExecuted: commandsExecuted,
                            iteration: iteration,
                            maxTokens: maxTokens,
                            maxTotalToolUses: maxTotalToolUses,
                            lastModel: lastModel,
                            traceRunDir: traceRunDir,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }

                var toolUseId = toolUse.Id;
                if (string.IsNullOrWhiteSpace(toolUseId))
                {
                    var msg = $"Tool call '{toolName}' is missing required id; cannot execute. Please call the tool again with a valid id.";
                    commandsExecuted.Add(new ExecutedCommand
                    {
                        Tool = toolName,
                        Input = CloneToolInput(toolInput),
                        Output = msg,
                        Iteration = iteration,
                        Duration = TimeSpan.Zero.ToString("c")
                    });

                messages.Add(new SamplingMessage
                {
                    Role = Role.User,
                    Content =
                    [
                            new TextContentBlock { Text = msg }
                        ]
                    });
                    toolUsesExecutedThisIteration++;
                    continue;
                }

                var sw = Stopwatch.StartNew();
                var toolCacheKey = BuildToolCacheKey(toolName, toolInput);
	                try
	                {
	                    string outputForModel;
	                    string outputForLog;
	                    var wasCached = false;
	                    var duration = sw.Elapsed;
	                    if (toolResultCache.TryGetValue(toolCacheKey, out var cached))
	                    {
	                        sw.Stop();
	                        duration = TimeSpan.Zero;
	                        wasCached = true;
	                        outputForModel = cached;
	                        outputForLog = outputForModel;
	                    }
	                    else
	                    {
	                        var output = await ExecuteToolAsync(toolName, toolInput, fullReportJson, report, debugger, clrMdAnalyzer, cancellationToken)
	                            .ConfigureAwait(false);
                        outputForModel = TruncateForModel(output);
                        toolResultCache[toolCacheKey] = outputForModel;
                        sw.Stop();
                        duration = sw.Elapsed;
                        outputForLog = outputForModel;
                    }

                    var toolOrdinal = toolIndexByIteration.TryGetValue(iteration, out var n) ? n + 1 : 1;
                    toolIndexByIteration[iteration] = toolOrdinal;
	                    commandsExecuted.Add(new ExecutedCommand
	                    {
	                        Tool = toolName,
	                        Input = CloneToolInput(toolInput),
	                        Output = outputForLog,
	                        Iteration = iteration,
	                        Duration = duration.ToString("c"),
	                        Cached = wasCached ? true : null
	                    });

                    WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-tool-{toolOrdinal:00}-{SanitizeFileComponent(toolName)}.json",
                        new { tool = toolName, input = CloneToolInput(toolInput).ToString(), output = outputForLog, cached = wasCached, isError = false, duration = duration.ToString("c") });

                    messages.Add(new SamplingMessage
                    {
                        Role = Role.User,
                        Content =
                        [
                            new ToolResultContentBlock
                            {
                                ToolUseId = toolUseId,
                                IsError = false,
                                Content =
                                [
                                    new TextContentBlock { Text = outputForModel }
                                ]
                            }
                        ]
                    });
                    respondedToolUseIds.Add(toolUseId);
                    toolUsesExecutedThisIteration++;
                }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogWarning(ex, "[AI] Tool execution failed in pass {Pass}: {Tool}", passName, toolName);
                    var messageForModel = TruncateForModel(ex.Message);
                    if (!string.IsNullOrWhiteSpace(toolCacheKey))
                    {
                        toolResultCache[toolCacheKey] = messageForModel;
                    }

                    var toolOrdinal = toolIndexByIteration.TryGetValue(iteration, out var n) ? n + 1 : 1;
                    toolIndexByIteration[iteration] = toolOrdinal;
                    commandsExecuted.Add(new ExecutedCommand
                    {
                        Tool = toolName,
                        Input = CloneToolInput(toolInput),
                        Output = messageForModel,
                        Iteration = iteration,
                        Duration = sw.Elapsed.ToString("c")
                    });

                    WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-tool-{toolOrdinal:00}-{SanitizeFileComponent(toolName)}.json",
                        new { tool = toolName, input = CloneToolInput(toolInput).ToString(), output = messageForModel, isError = true, duration = sw.Elapsed.ToString("c") });

                messages.Add(new SamplingMessage
                {
                    Role = Role.User,
                    Content =
                    [
                            new ToolResultContentBlock
                            {
                                ToolUseId = toolUseId,
                                IsError = true,
                                Content =
                                [
                                    new TextContentBlock { Text = messageForModel }
                                ]
                            }
                        ]
                    });
                respondedToolUseIds.Add(toolUseId);
                toolUsesExecutedThisIteration++;
            }
        }

            if (pendingComplete != null)
            {
                var otherToolsCoissued = rewrittenToolUses.Any(t => !string.Equals(t.Name, completionToolName, StringComparison.OrdinalIgnoreCase));

                if (otherToolsCoissued)
                {
                    // Some models will prematurely call the completion tool in the same message as other tool requests.
                    // Ignore that completion request and let the pass continue with the executed tool outputs.
                    // This avoids getting stuck in a refusal loop while still enforcing "read tool output before finishing".
                    PruneUnrespondedToolUsesFromLastAssistantMessage(messages, respondedToolUseIds);
                    continue;
            }

                return ParseCompletionTool<T>(passName, completionToolName, pendingCompleteInput, commandsExecuted, iteration, lastModel);
            }

            if (toolUsesPrunedDueToLimit)
            {
                PruneUnrespondedToolUsesFromLastAssistantMessage(messages, respondedToolUseIds);
            }

            var madeProgress = toolResultCache.Count != uniqueToolCallsAtIterationStart;
            if (madeProgress)
            {
                consecutiveNoProgressIterations = 0;
            }
            else
            {
                consecutiveNoProgressIterations++;
                if (consecutiveNoProgressIterations >= Math.Max(1, MaxConsecutiveNoProgressIterations))
                {
                    _logger.LogInformation(
                        "[AI] No progress detected for {Count} consecutive iterations; finalizing pass {Pass} early.",
                        consecutiveNoProgressIterations,
                        passName);
                    return await FinalizePassAfterNoProgressDetectedAsync<T>(
                            passName: passName,
                            systemPrompt: systemPrompt,
                            messages: messages,
                            commandsExecuted: commandsExecuted,
                            iteration: iteration,
                            maxTokens: maxTokens,
                            consecutiveNoProgressIterations: consecutiveNoProgressIterations,
                            uniqueToolCalls: toolResultCache.Count,
                            lastModel: lastModel,
                            traceRunDir: traceRunDir,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (iteration < maxIterations
                && ShouldCreateCheckpoint(
                    iteration: iteration,
                    maxIterations: maxIterations,
                    checkpointEveryIterations: CheckpointEveryIterations,
                    lastCheckpointIteration: lastCheckpointIteration,
                    commandsExecuted: commandsExecuted,
                    commandsExecutedAtLastCheckpoint: commandsExecutedAtLastCheckpoint))
            {
                var checkpoint = await TryCreateCheckpointAsync(
                        passName: passName,
                        systemPrompt: systemPrompt,
                        messages: messages,
                        commandsExecuted: commandsExecuted,
                        commandsExecutedAtLastCheckpoint: commandsExecutedAtLastCheckpoint,
                        iteration: iteration,
                        maxTokens: CheckpointMaxTokens,
                        traceRunDir: traceRunDir,
                        internalToolChoiceModeCache: internalToolChoiceModeCache,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(checkpoint))
                {
                    checkpoint = BuildDeterministicCheckpointJson(
                        passName: passName,
                        commandsExecuted: commandsExecuted,
                        commandsExecutedAtLastCheckpoint: commandsExecutedAtLastCheckpoint);
                }

                lastCheckpointIteration = iteration;
                commandsExecutedAtLastCheckpoint = commandsExecuted.Count;

                messages.Clear();
                messages.Add(BuildCheckpointCarryForwardMessage(checkpoint, passName: passName));
            }
        }

        // Add one final synthesis iteration when the iteration budget is reached.
        return await FinalizePassAfterMaxIterationsReachedAsync<T>(
                passName: passName,
                systemPrompt: systemPrompt,
                messages: messages,
                commandsExecuted: commandsExecuted,
                iteration: maxIterations + 1,
                maxTokens: maxTokens,
                maxIterations: maxIterations,
                lastModel: lastModel,
                traceRunDir: traceRunDir,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private int GetMaxIterationsForSamplingPass(string passName)
    {
        var global = Math.Max(1, MaxIterations);

        var perPass = passName switch
        {
            "summary-rewrite" => SummaryRewriteMaxIterations,
            "thread-narrative" => ThreadNarrativeMaxIterations,
            _ => global
        };

        if (perPass <= 0)
        {
            perPass = global;
        }

        return Math.Max(1, Math.Min(global, perPass));
    }

    private int GetMaxTotalToolUsesForSamplingPass(string passName)
    {
        var perPass = passName switch
        {
            "summary-rewrite" => SummaryRewriteMaxTotalToolUses,
            "thread-narrative" => ThreadNarrativeMaxTotalToolUses,
            _ => 0
        };

        if (perPass > 0)
        {
            return Math.Max(10, perPass);
        }

        var fallbackUnique = MaxToolCalls > 0 ? MaxToolCalls : 100;
        return Math.Max(10, fallbackUnique * 2);
    }

    private static T? ParseCompletionTool<T>(
        string passName,
        string completionToolName,
        JsonElement input,
        List<ExecutedCommand> commandsExecuted,
        int iteration,
        string? model)
        where T : class
        => passName switch
        {
            "summary-rewrite" => ParseSummaryRewriteComplete(input, commandsExecuted, iteration, model) as T,
            "thread-narrative" => ParseThreadNarrativeComplete(input, commandsExecuted, iteration, model) as T,
            _ => null
        };

    private static bool ShouldCreateCheckpoint(
        int iteration,
        int maxIterations,
        int checkpointEveryIterations,
        int lastCheckpointIteration,
        List<ExecutedCommand> commandsExecuted,
        int commandsExecutedAtLastCheckpoint)
    {
        if (iteration <= 0 || maxIterations <= 0 || iteration >= maxIterations)
        {
            return false;
        }

        if (checkpointEveryIterations <= 0)
        {
            return false;
        }

        if (commandsExecuted.Count <= commandsExecutedAtLastCheckpoint)
        {
            return false;
        }

        if (iteration - lastCheckpointIteration < 1)
        {
            return false;
        }

        var shouldByInterval = iteration % checkpointEveryIterations == 0;
        if (shouldByInterval)
        {
            return true;
        }

        if (iteration - lastCheckpointIteration < 2)
        {
            return false;
        }

        var newCommandCount = commandsExecuted.Count - commandsExecutedAtLastCheckpoint;
        if (newCommandCount < 5)
        {
            return false;
        }

        var recent = commandsExecuted.Where(c => c.Iteration == iteration).ToList();
        if (recent.Count == 0)
        {
            return false;
        }

	        var tooLargeCount = recent.Count(c =>
	            c.Tool.Equals("report_get", StringComparison.OrdinalIgnoreCase)
	            && c.Output.Contains("too_large", StringComparison.OrdinalIgnoreCase));

	        var duplicateReuseCount = recent.Count(c => c.Cached == true);

	        return tooLargeCount >= 2 || duplicateReuseCount >= 1;
	    }

    private static bool TryGetDeterministicCheckpointInjectionReason(
        int iteration,
        List<ExecutedCommand> commandsExecuted,
        bool baselineComplete,
        int consecutiveNoProgressCheckpoints,
        int consecutiveIdenticalCheckpointHashes,
        bool noProgressSinceLastCheckpoint,
        out string reason)
    {
        reason = string.Empty;

        if (!baselineComplete || iteration <= 0 || commandsExecuted.Count == 0)
        {
            return false;
        }

        if (consecutiveIdenticalCheckpointHashes >= 1)
        {
            reason = "checkpoint_hash_repeat";
            return true;
        }

        if (noProgressSinceLastCheckpoint && consecutiveNoProgressCheckpoints >= 2)
        {
            reason = "checkpoint_no_progress";
            return true;
        }

        var windowStart = Math.Max(1, iteration - 9);
        var recent = commandsExecuted.Where(c => c.Iteration >= windowStart).ToList();
        if (recent.Count == 0)
        {
            return false;
        }

        var baselineDuplicateRepeats = recent.Count(c =>
            c.Cached == true
            && c.Tool.Equals("report_get", StringComparison.OrdinalIgnoreCase)
            && IsBaselineReportGet(c)
            && TryParseReportGetResponseHasError(c.Output ?? string.Empty, out var hasError)
            && !hasError);

        if (baselineDuplicateRepeats >= 2)
        {
            reason = "baseline_duplicate_loop";
            return true;
        }

        var invalidCursor = recent.Count(c =>
            c.Tool.Equals("report_get", StringComparison.OrdinalIgnoreCase)
            && TryGetReportGetErrorCode(c.Output ?? string.Empty, out var code)
            && code.Equals("invalid_cursor", StringComparison.OrdinalIgnoreCase));

        if (invalidCursor >= 2)
        {
            reason = "invalid_cursor_loop";
            return true;
        }

        var duplicateToolUseCount = recent.Count(c => c.Cached == true);
        if (duplicateToolUseCount >= 4)
        {
            reason = "duplicate_tool_use_loop";
            return true;
        }

        var toolInputContractErrors = recent.Count(IsToolInputContractError);
        if (toolInputContractErrors >= 3)
        {
            reason = "tool_input_contract_loop";
            return true;
        }

        var transientRetryExhausted = CountTransientRetryExhausted(recent);
        if (transientRetryExhausted > 0)
        {
            reason = "transient_retry_exhausted";
            return true;
        }

        var hypothesisNoProgress = CountHypothesisRegisterNoProgress(recent);
        if (hypothesisNoProgress >= 2)
        {
            reason = "hypothesis_register_no_progress";
            return true;
        }

        var hypothesisRegisterCalls = recent.Count(c => c.Tool.Equals("analysis_hypothesis_register", StringComparison.OrdinalIgnoreCase));
        if (hypothesisRegisterCalls > 2)
        {
            var newEvidenceArrived = recent.Any(c => IsEvidenceToolName(c.Tool) && c.Cached != true);
            if (!newEvidenceArrived)
            {
                reason = "hypothesis_register_spam";
                return true;
            }
        }

        return false;
    }

    private static int CountHypothesisRegisterNoProgress(List<ExecutedCommand> recent)
    {
        if (recent.Count == 0)
        {
            return 0;
        }

        var registerCalls = recent
            .Where(c => c.Tool.Equals("analysis_hypothesis_register", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Iteration)
            .ToList();

        if (registerCalls.Count < 2)
        {
            return 0;
        }

        var noProgressCount = 0;
        var lastRegisterIteration = registerCalls[0].Iteration;

        for (var i = 1; i < registerCalls.Count; i++)
        {
            var current = registerCalls[i];
            if (!TryParseHypothesisRegisterResult(current.Output ?? string.Empty, out var addedCount, out var ignoredAtCapacity))
            {
                lastRegisterIteration = current.Iteration;
                continue;
            }

            var isNoProgress = addedCount == 0 || ignoredAtCapacity > 0;
            if (!isNoProgress)
            {
                lastRegisterIteration = current.Iteration;
                continue;
            }

            var hasNewEvidenceSinceLastRegister = recent.Any(c =>
                c.Iteration > lastRegisterIteration
                && c.Iteration <= current.Iteration
                && IsEvidenceToolName(c.Tool)
                && c.Cached != true);

            if (!hasNewEvidenceSinceLastRegister)
            {
                noProgressCount++;
            }

            lastRegisterIteration = current.Iteration;
        }

        return noProgressCount;
    }

    private static bool TryParseHypothesisRegisterResult(string output, out int addedCount, out int ignoredAtCapacity)
    {
        addedCount = 0;
        ignoredAtCapacity = 0;

        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        if (!TryParseFirstJsonObject(output, out var json) || json.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (json.TryGetProperty("added", out var addedEl) && addedEl.ValueKind == JsonValueKind.Array)
        {
            addedCount = addedEl.GetArrayLength();
        }

        if (json.TryGetProperty("ignoredAtCapacity", out var ignoredEl))
        {
            if (ignoredEl.ValueKind == JsonValueKind.Number && ignoredEl.TryGetInt32(out var n))
            {
                ignoredAtCapacity = n;
            }
            else if (int.TryParse(ignoredEl.ToString(), out var parsed))
            {
                ignoredAtCapacity = parsed;
            }
        }

        return true;
    }

    private static int CountTransientRetryExhausted(List<ExecutedCommand> recent)
    {
        if (recent.Count == 0)
        {
            return 0;
        }

        var attemptsByKey = BuildToolAttemptInfoByKey(recent);
        return attemptsByKey.Values.Count(v =>
            v is { HasNonTransientAttempt: false, LastAttemptWasTransient: true } && v.Attempts >= TransientRetryMaxAttempts);
    }

    private static string BuildCheckpointProgressSignature(int uniqueToolCalls, int evidenceCount, int hypothesisCount)
        => $"{uniqueToolCalls}|{evidenceCount}|{hypothesisCount}";

    private static bool IsBaselineReportGet(ExecutedCommand command)
    {
        if (!command.Tool.Equals("report_get", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = TryGetString(command.Input, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return BaselineIdByPath.ContainsKey(path.Trim());
    }

    private static bool TryGetReportGetErrorCode(string outputForModel, out string code)
    {
        code = string.Empty;

        if (string.IsNullOrWhiteSpace(outputForModel))
        {
            return false;
        }

        if (!TryParseFirstJsonObject(outputForModel, out var json) || json.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!json.TryGetProperty("error", out var errorEl) || errorEl.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!errorEl.TryGetProperty("code", out var codeEl))
        {
            return false;
        }

        code = codeEl.ValueKind == JsonValueKind.String ? (codeEl.GetString() ?? string.Empty) : codeEl.ToString();
        code = code.Trim();
        return !string.IsNullOrWhiteSpace(code);
    }

    private static bool IsTransientToolFailure(ExecutedCommand command)
    {
        if (command == null)
        {
            return false;
        }

        if (TryGetReportGetErrorCode(command.Output ?? string.Empty, out var code))
        {
            return code.Equals(TransientErrorCode, StringComparison.OrdinalIgnoreCase)
                   || code.Equals("rate_limited", StringComparison.OrdinalIgnoreCase)
                   || code.Equals("rate_limit", StringComparison.OrdinalIgnoreCase)
                   || code.Equals("timeout", StringComparison.OrdinalIgnoreCase)
                   || code.Equals("temporarily_unavailable", StringComparison.OrdinalIgnoreCase);
        }

        var output = command.Output ?? string.Empty;
        return output.Contains("timeout", StringComparison.OrdinalIgnoreCase)
               || output.Contains("timed out", StringComparison.OrdinalIgnoreCase)
               || output.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase)
               || output.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
               || output.Contains("429", StringComparison.OrdinalIgnoreCase)
               || output.Contains("502", StringComparison.OrdinalIgnoreCase)
               || output.Contains("503", StringComparison.OrdinalIgnoreCase)
               || output.Contains("504", StringComparison.OrdinalIgnoreCase)
               || output.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
               || output.Contains("connection closed", StringComparison.OrdinalIgnoreCase)
               || output.Contains("disconnected", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsToolInputContractError(ExecutedCommand command)
    {
        if (command.Tool.Equals("report_get", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetReportGetErrorCode(command.Output ?? string.Empty, out var code))
            {
                return code.Equals("too_large", StringComparison.OrdinalIgnoreCase)
                       || code.Equals("invalid_path", StringComparison.OrdinalIgnoreCase)
                       || code.Equals("invalid_argument", StringComparison.OrdinalIgnoreCase);
            }

            var output = command.Output ?? string.Empty;
            return output.Contains("report_get.path is required", StringComparison.OrdinalIgnoreCase)
                   || output.Contains("report_get input must be an object", StringComparison.OrdinalIgnoreCase);
        }

        if (command.Tool.Equals("analysis_hypothesis_register", StringComparison.OrdinalIgnoreCase))
        {
            var output = command.Output ?? string.Empty;
            return output.Contains("analysis_hypothesis_register.hypotheses is required", StringComparison.OrdinalIgnoreCase)
                   || output.Contains("\"error\"", StringComparison.OrdinalIgnoreCase);
        }

        if (command.Tool.Equals("analysis_evidence_add", StringComparison.OrdinalIgnoreCase))
        {
            var output = command.Output ?? string.Empty;
            return output.Contains("analysis_evidence_add.items is required", StringComparison.OrdinalIgnoreCase)
                   || output.Contains("\"error\"", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

	    private async Task<string?> TryCreateCheckpointAsync(
	        string passName,
	        string systemPrompt,
	        List<SamplingMessage> messages,
	        List<ExecutedCommand> commandsExecuted,
        int commandsExecutedAtLastCheckpoint,
        int iteration,
        int maxTokens,
        string? traceRunDir,
        InternalToolChoiceModeCache internalToolChoiceModeCache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(internalToolChoiceModeCache);

        if (_checkpointSynthesisFailureCache.IsDisabled)
        {
            _logger.LogDebug("[AI] Skipping checkpoint synthesis for pass {Pass}: disabled after a previous checkpoint failure.", passName);
            return null;
        }

        var priorCheckpointMessage = FindLatestCheckpointCarryForwardMessage(messages, passName);
	        if (priorCheckpointMessage == null && _toolHistoryModeCache.IsCheckpointOnly && commandsExecuted.Count > 0)
	        {
	            var deterministic = BuildDeterministicCheckpointJson(
	                passName: passName,
	                commandsExecuted: commandsExecuted,
	                commandsExecutedAtLastCheckpoint: commandsExecutedAtLastCheckpoint);
	            priorCheckpointMessage = BuildCheckpointCarryForwardMessage(deterministic, passName: passName);
	        }

		        var prompt = $"""
		Create an INTERNAL checkpoint for pass "{passName}".
	
		Goal: preserve a working memory so we can prune older tool outputs and avoid repeating tool calls.
		Do NOT request any debugger tools in this step.
	
		Hard rules:
		- Base this ONLY on executed tool results already returned in this conversation.
		- If the conversation contains tool requests without corresponding tool results, those tool requests were NOT executed and must be ignored.
		- doNotRepeat is a HARD constraint against exact-duplicate tool calls (same tool + same arguments). Include duplicates to stop loops.
		- nextSteps MUST NOT include anything already in doNotRepeat. Prefer 1-5 narrowly-scoped, high-signal steps.
	
		Report/tool contracts to encode in the checkpoint:
		- report_get.path is required; never call report_get with empty args.
		- Array indices must be numeric (e.g., stackTrace[0]); ranges like stackTrace[0:5] are invalid.
		- Cursor is only valid for the IDENTICAL query shape (same path/select/where/pageKind/limit). If any change, drop cursor. Prefer where filters over cursor paging for large arrays.
		- Cursor examples: GOOD = reuse cursor only with identical args; BAD = reusing cursor after changing select/where/pageKind/limit (invalid_cursor).
		- Do not guess ".items" or report shape; query a parent node first (pageKind="object", limit<=50). If you get too_large, follow the tool's "Try:" hints exactly.
		- Recovery: invalid_cursor => retry same query without cursor; invalid_path/missing property => step back to a parent node and query keys; stop after 2 failed corrections.
		- analysis_hypothesis_register.hypotheses is required; analysis_evidence_add.items is required.
		- Hypothesis hygiene: do not register new hypotheses unless new evidence arrived since last hypothesis update; if ignoredAtCapacity>0, stop registering and update/score existing hypotheses.
	
		Working memory requirements:
		- Preserve the LAST tool result in a usable form (either add fact "LAST_TOOL: ..." with a short excerpt including error code/Try hint, or add evidence item id="E_LAST").
		- Convergence: if evidence/hypotheses are already sufficient, add fact "PHASE: finalizeNow=true" and set nextSteps to a single analysis_complete call.
	
		Bounds:
		- facts<=50, hypotheses<=10, evidence<=50, doNotRepeat<=50, nextSteps<=20.
		- Keep strings concise (prefer <=2048 chars each). Keep total JSON <= {MaxCheckpointJsonChars} characters.
	
		Respond by calling the "{CheckpointCompleteToolName}" tool with arguments matching its schema.
		Do NOT output any additional text.
		""";

        List<SamplingMessage> checkpointMessages;
        if (priorCheckpointMessage == null)
        {
            checkpointMessages = new List<SamplingMessage>(messages);

            checkpointMessages.Add(new SamplingMessage
            {
                Role = Role.User,
                Content =
                [
                    new TextContentBlock { Text = prompt }
                ]
            });
        }
        else
        {
            var evidence = BuildCheckpointEvidenceSnapshot(
                passName: passName,
                commandsExecuted: commandsExecuted,
                commandsExecutedAtLastCheckpoint: commandsExecutedAtLastCheckpoint);

            checkpointMessages = [priorCheckpointMessage];

            checkpointMessages.Add(new SamplingMessage
            {
                Role = Role.User,
                Content =
                [
                    new TextContentBlock { Text = $"{prompt}\n\nEvidence snapshot:\n{evidence}" }
                ]
            });
        }

        var checkpointTools = new List<Tool>
        {
            new()
            {
                Name = CheckpointCompleteToolName,
                Description = "Create/extend the internal working-memory checkpoint (facts, hypotheses, evidence, doNotRepeat, nextSteps).",
                InputSchema = CheckpointCompleteSchema
            }
        };

        var request = new CreateMessageRequestParams
        {
            SystemPrompt = systemPrompt,
            Messages = checkpointMessages,
            MaxTokens = Math.Max(256, Math.Min(maxTokens, 65_000)),
            Tools = checkpointTools,
            ToolChoice = new ToolChoice { Mode = internalToolChoiceModeCache.Mode }
        };

        CreateMessageResult response;
        try
        {
            _logger.LogInformation("[AI] Checkpointing pass {Pass} at iteration {Iteration}...", passName, iteration);
            WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-checkpoint-request.json", BuildTraceRequest(iteration, request));
            response = await _samplingClient.RequestCompletionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (IsStreamAlreadyConsumedError(ex) && _checkpointSynthesisFailureCache.Disable())
            {
                _logger.LogWarning(
                    ex,
                    "[AI] Checkpoint synthesis failed due to a stream-consumption error; disabling further checkpoint synthesis for this run (pass {Pass}).",
                    passName);
                WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-checkpoint-disabled.json", new
                {
                    passName,
                    iteration,
                    reason = "stream_already_consumed",
                    message = ex.Message
                });
                return null;
            }

            if (request.ToolChoice?.Mode?.Equals("required", StringComparison.OrdinalIgnoreCase) == true
                && IsToolChoiceRequiredUnsupported(ex))
            {
                internalToolChoiceModeCache.MarkRequiredUnsupported();
                _logger.LogInformation(
                    "[AI] Provider rejected toolChoice=required for checkpoint synthesis; retrying with toolChoice=auto (pass {Pass}, iteration {Iteration}).",
                    passName,
                    iteration);
                WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-checkpoint-toolchoice-fallback.json", new
                {
                    passName,
                    iteration,
                    from = "required",
                    to = "auto",
                    message = ex.Message
                });

                var fallbackRequest = CloneWithToolChoiceMode(request, "auto");
                try
                {
                    WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-checkpoint-fallback-request.json", BuildTraceRequest(iteration, fallbackRequest));
                    response = await _samplingClient.RequestCompletionAsync(fallbackRequest, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex2) when (!cancellationToken.IsCancellationRequested)
                {
                    if (IsStreamAlreadyConsumedError(ex2) && _checkpointSynthesisFailureCache.Disable())
                    {
                        _logger.LogWarning(
                            ex2,
                            "[AI] Checkpoint synthesis failed due to a stream-consumption error; disabling further checkpoint synthesis for this run (pass {Pass}).",
                            passName);
                        WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-checkpoint-disabled.json", new
                        {
                            passName,
                            iteration,
                            reason = "stream_already_consumed",
                            message = ex2.Message
                        });
                        return null;
                    }

                    _logger.LogWarning(ex2, "[AI] Checkpoint synthesis failed in pass {Pass} at iteration {Iteration}", passName, iteration);
                    WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-checkpoint-error.json", new { passName, iteration, error = ex2.ToString(), message = ex2.Message });
                    return null;
                }
            }
            else
            {
                _logger.LogWarning(ex, "[AI] Checkpoint synthesis failed in pass {Pass} at iteration {Iteration}", passName, iteration);
                WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-checkpoint-error.json", new { passName, iteration, error = ex.ToString(), message = ex.Message });
                return null;
            }
        }

        WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-checkpoint-response.json", BuildTraceResponse(iteration, response));

        if (TryExtractCheckpointFromToolUse(response, out var checkpointToolJson))
        {
            return checkpointToolJson;
        }

        var text = ExtractAssistantText(response);
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("[AI] Checkpoint synthesis returned empty content in pass {Pass} at iteration {Iteration}", passName, iteration);
            return null;
        }

	        text = text.Trim();
		        if (!TryParseFirstJsonObject(text, out var json))
		        {
		            _logger.LogWarning("[AI] Checkpoint synthesis returned unstructured output in pass {Pass} at iteration {Iteration}", passName, iteration);
		            return null;
		        }

	        var normalized = JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = false });
	        if (normalized.Length > MaxCheckpointJsonChars)
	        {
	            _logger.LogWarning(
	                "[AI] Checkpoint synthesis output exceeded max chars ({MaxChars}) in pass {Pass} at iteration {Iteration} (chars={Chars}); skipping checkpoint.",
	                MaxCheckpointJsonChars,
	                passName,
	                iteration,
	                normalized.Length);
	            return null;
	        }

        return normalized;
    }

    private static SamplingMessage? FindLatestCheckpointCarryForwardMessage(IReadOnlyList<SamplingMessage> messages, string passName)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.Role != Role.User)
            {
                continue;
            }

            if (message.Content == null || message.Content.Count == 0)
            {
                continue;
            }

            var text = message.Content
                .OfType<TextContentBlock>()
                .Select(b => b.Text)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (text.Contains("Checkpoint JSON:", StringComparison.OrdinalIgnoreCase)
                && text.Contains($"pass \"{passName}\"", StringComparison.OrdinalIgnoreCase))
            {
                return message;
            }
        }

        return null;
    }

    private bool TryExtractCheckpointFromToolUse(CreateMessageResult response, out string checkpointJson)
    {
        checkpointJson = string.Empty;

        if (response.Content == null || response.Content.Count == 0)
        {
            return false;
        }

        var toolUse = response.Content
            .OfType<ToolUseContentBlock>()
            .FirstOrDefault(b => string.Equals(b.Name, CheckpointCompleteToolName, StringComparison.OrdinalIgnoreCase));

        if (toolUse == null)
        {
            return false;
        }

        var raw = toolUse.Input.ToString() ?? string.Empty;
        if (!TryParseFirstJsonObject(raw, out var json))
        {
            _logger.LogWarning("[AI] Checkpoint tool call returned non-JSON input (tool={Tool})", CheckpointCompleteToolName);
            return false;
        }

	        var normalized = JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = false });
	        if (normalized.Length > MaxCheckpointJsonChars)
	        {
	            _logger.LogWarning(
	                "[AI] Checkpoint tool call exceeded max chars ({MaxChars}) (tool={Tool} chars={Chars}); skipping checkpoint.",
	                MaxCheckpointJsonChars,
	                CheckpointCompleteToolName,
	                normalized.Length);
	            return false;
	        }

        checkpointJson = normalized;
        return true;
    }

    private static SamplingMessage BuildCheckpointCarryForwardMessage(string checkpointJson, string passName, string? stateJson = null)
    {
        var stateSection = string.IsNullOrWhiteSpace(stateJson)
            ? string.Empty
            : $"""

                Stable state JSON (evidence ledger + hypotheses):
                {stateJson}
                """;

        var prompt = $"""
            This is the current INTERNAL working memory checkpoint for pass "{passName}".
            Treat it as authoritative.

            Older tool outputs may have been pruned to keep context small.

            Hard constraints:
            - Do NOT call any tool listed in doNotRepeat (exact duplicate tool + arguments), even if you feel you've "forgotten" the result; re-use the checkpoint facts/evidence instead.
            - If facts contain PHASE: baselineComplete=true, do NOT restart baseline calls; continue with post-baseline investigation.
            - If facts contain PHASE: finalizeNow=true, call analysis_complete next (unless you are explicitly blocked by missing required evidence IDs).
            - If analysis_complete was previously rejected, do NOT restart baseline; fix only the rejected/missing fields or evidence formatting and try finalize again.

            report_get contract:
            - report_get.path is required; never call report_get with empty args.
            - Array indices must be numeric (e.g., stackTrace[0]); ranges like stackTrace[0:5] are invalid.
            - Cursor is only valid for the IDENTICAL query shape (same path/select/where/pageKind/limit). If any change, drop cursor. Prefer where filters over cursor paging for large arrays.
            - Do not guess ".items" or report shape; query a parent node first (pageKind="object", limit<=50). If you get too_large, follow the tool's "Try:" hints exactly.
            - Recovery: invalid_cursor => retry same query without cursor; invalid_path/missing property => step back to a parent node and query keys; stop after 2 failed corrections.

            Meta tool contract:
            - analysis_hypothesis_register.hypotheses is required; analysis_evidence_add.items is required.
            - Hypothesis hygiene: do not register new hypotheses unless new evidence arrived since last hypothesis update; if ignoredAtCapacity>0, stop registering and update/score existing hypotheses.

            When you need more evidence, propose narrowly-scoped tool calls (small report_get paths with select/limit/cursor).

            Checkpoint JSON:
            {checkpointJson}
            {stateSection}
            """;

        return new SamplingMessage
        {
            Role = Role.User,
            Content =
            [
                new TextContentBlock { Text = prompt }
            ]
        };
    }

    private static string BuildStateSnapshotJson(AiEvidenceLedger evidenceLedger, AiHypothesisTracker hypothesisTracker)
    {
        ArgumentNullException.ThrowIfNull(evidenceLedger);
        ArgumentNullException.ThrowIfNull(hypothesisTracker);

        // Keep this compact: the full checkpoint JSON already contains richer details.
        const int maxEvidenceSourceChars = 256;
        const int maxEvidenceFindingChars = 512;
        const int maxHypothesisChars = 512;
        const int maxNotesChars = 512;

        var evidence = evidenceLedger.Items
            .Select(e => new
            {
                id = e.Id,
                source = TruncateText(e.Source ?? string.Empty, maxEvidenceSourceChars),
                finding = TruncateText(e.Finding ?? string.Empty, maxEvidenceFindingChars),
                tags = e.Tags
            })
            .ToList();

        var hypotheses = hypothesisTracker.Hypotheses
            .Select(h => new
            {
                id = h.Id,
                hypothesis = TruncateText(h.Hypothesis ?? string.Empty, maxHypothesisChars),
                confidence = h.Confidence,
                supportsEvidenceIds = h.SupportsEvidenceIds,
                contradictsEvidenceIds = h.ContradictsEvidenceIds,
                notes = string.IsNullOrWhiteSpace(h.Notes) ? null : TruncateText(h.Notes, maxNotesChars)
            })
            .ToList();

        var state = new
        {
            evidenceLedger = evidence,
            hypotheses
        };

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            WriteIndented = false
        });

        return json.Length > MaxCheckpointJsonChars
            ? JsonSerializer.Serialize(new
            {
                evidenceLedger = evidence.Select(e => new { e.id, e.finding }).ToList(),
                hypotheses = hypotheses.Select(h => new { h.id, h.hypothesis, h.confidence, h.supportsEvidenceIds, h.contradictsEvidenceIds }).ToList()
            }, new JsonSerializerOptions { WriteIndented = false })
            : json;
    }

    private static void ApplyCheckpointToStateStores(
        string checkpointJson,
        AiEvidenceLedger evidenceLedger,
        AiHypothesisTracker hypothesisTracker,
        bool enableEvidenceProvenance)
    {
        ArgumentNullException.ThrowIfNull(checkpointJson);
        ArgumentNullException.ThrowIfNull(evidenceLedger);
        ArgumentNullException.ThrowIfNull(hypothesisTracker);

        if (string.IsNullOrWhiteSpace(checkpointJson))
        {
            return;
        }

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(checkpointJson);
        }
        catch (JsonException)
        {
            // Best-effort only; checkpoints may be synthesized deterministically or truncated.
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!enableEvidenceProvenance
                && root.TryGetProperty("evidence", out var evidenceEl)
                && evidenceEl.ValueKind == JsonValueKind.Array)
            {
                var items = new List<AiEvidenceLedgerItem>();
                foreach (var item in evidenceEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var id = TryGetString(item, "id");
                    var source = TryGetString(item, "source");
                    var finding = TryGetString(item, "finding");

                    if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(finding))
                    {
                        continue;
                    }

                    items.Add(new AiEvidenceLedgerItem
                    {
                        Id = id ?? string.Empty,
                        Source = source,
                        Finding = finding
                    });
                }

                if (items.Count > 0)
                {
                    evidenceLedger.AddOrUpdate(items);
                }
            }

            if (root.TryGetProperty("hypotheses", out var hypothesesEl) && hypothesesEl.ValueKind == JsonValueKind.Array)
            {
                var list = new List<AiHypothesis>();
                foreach (var h in hypothesesEl.EnumerateArray())
                {
                    if (h.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var hypothesisText = TryGetString(h, "hypothesis");
                    if (string.IsNullOrWhiteSpace(hypothesisText))
                    {
                        continue;
                    }

                    var confidence = TryGetString(h, "confidence");
                    var unknowns = TryGetStringArray(h, "unknowns");
                    var evidenceStrings = TryGetStringArray(h, "evidence");

                    var supportsEvidenceIds = ExtractEvidenceIdsFromStrings(evidenceStrings, evidenceLedger);
                    var notes = BuildCheckpointHypothesisNotes(evidenceStrings, supportsEvidenceIds);

                    list.Add(new AiHypothesis
                    {
                        Hypothesis = hypothesisText,
                        Confidence = string.IsNullOrWhiteSpace(confidence) ? "unknown" : confidence,
                        Unknowns = unknowns,
                        SupportsEvidenceIds = supportsEvidenceIds,
                        Notes = notes
                    });
                }

                if (list.Count > 0)
                {
                    hypothesisTracker.Register(list);
                }
            }
        }
    }

    private static List<string>? ExtractEvidenceIdsFromStrings(List<string>? evidenceStrings, AiEvidenceLedger evidenceLedger)
    {
        if (evidenceStrings == null || evidenceStrings.Count == 0)
        {
            return null;
        }

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in evidenceStrings)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var match = Regex.Match(raw.Trim(), @"^(E(?<n>\d+))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                continue;
            }

            if (!int.TryParse(match.Groups["n"].Value, out var parsed) || parsed <= 0)
            {
                continue;
            }

            var id = $"E{parsed}";
            if (!seen.Add(id))
            {
                continue;
            }

            if (evidenceLedger.ContainsEvidenceId(id))
            {
                ids.Add(id);
            }
        }

        return ids.Count == 0 ? null : ids;
    }

    private static string? BuildCheckpointHypothesisNotes(List<string>? evidenceStrings, List<string>? supportsEvidenceIds)
    {
        if (evidenceStrings == null || evidenceStrings.Count == 0)
        {
            return null;
        }

        var idsSet = supportsEvidenceIds == null ? null : new HashSet<string>(supportsEvidenceIds, StringComparer.OrdinalIgnoreCase);
        var nonIdEvidence = evidenceStrings
            .Where(s =>
            {
                if (string.IsNullOrWhiteSpace(s))
                {
                    return false;
                }

                if (idsSet == null || idsSet.Count == 0)
                {
                    return true;
                }

                var match = Regex.Match(s.Trim(), @"^(E\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return !match.Success || !idsSet.Contains(match.Groups[1].Value);
            })
            .Take(8)
            .ToList();

        if (nonIdEvidence.Count == 0)
        {
            return null;
        }

        var joined = string.Join("; ", nonIdEvidence);
        return TruncateText("Checkpoint evidence: " + joined, maxChars: 2048);
    }

    private static string BuildCheckpointEvidenceSnapshot(
        string passName,
        List<ExecutedCommand> commandsExecuted,
        int commandsExecutedAtLastCheckpoint)
    {
        var start = Math.Clamp(commandsExecutedAtLastCheckpoint, 0, commandsExecuted.Count);
        var recent = commandsExecuted.Skip(start).ToList();
        if (recent.Count == 0)
        {
            return $"pass={passName}\n(no new tool calls since last checkpoint)";
        }

        const int maxCommands = 25;
        const int maxInputChars = 1500;
        const int maxOutputChars = 4000;

        if (recent.Count > maxCommands)
        {
            recent = recent.Skip(recent.Count - maxCommands).ToList();
        }

        var sb = new StringBuilder();
        sb.AppendLine($"pass={passName}");
        sb.AppendLine($"newToolCalls={recent.Count}");
        sb.AppendLine("toolCalls:");

        var idx = 0;
        foreach (var c in recent)
        {
            idx++;
            var input = c.Input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : c.Input.GetRawText();
            input = TruncateText(input, maxInputChars);
            var output = TruncateText(c.Output ?? string.Empty, maxOutputChars);
            sb.AppendLine($"- [{idx}] iter={c.Iteration} tool={c.Tool}");
            sb.AppendLine($"  input: {input}");
            sb.AppendLine($"  output: {output}");
        }

        return sb.ToString().TrimEnd();
    }

    private static IEnumerable<Exception> EnumerateExceptionChain(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static bool IsToolChoiceRequiredUnsupported(Exception ex)
    {
        foreach (var current in EnumerateExceptionChain(ex))
        {
            var message = current.Message ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            // Observed from OpenRouter for some models (e.g., z-ai/glm-4.7):
            // "No endpoints found that support the provided 'tool_choice' value."
            if (message.Contains("tool_choice", StringComparison.OrdinalIgnoreCase)
                && (message.Contains("No endpoints found", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("not support", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("unsupported", StringComparison.OrdinalIgnoreCase))
                && message.Contains("required", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (message.Contains("tool_choice", StringComparison.OrdinalIgnoreCase)
                && message.Contains("No endpoints found", StringComparison.OrdinalIgnoreCase)
                && message.Contains("support", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStructuredToolHistoryUnsupported(Exception ex)
    {
        foreach (var current in EnumerateExceptionChain(ex))
        {
            var message = current.Message ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            // Observed from Gemini via OpenRouter/Google when a request includes tool history messages
            // that don't translate to Gemini "parts" (assistant tool_use / user tool_result blocks).
            if (message.Contains("must include at least one parts field", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (message.Contains("parts field", StringComparison.OrdinalIgnoreCase)
                && message.Contains("gemini", StringComparison.OrdinalIgnoreCase)
                && message.Contains("invalid_argument", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStreamAlreadyConsumedError(Exception ex)
    {
        foreach (var current in EnumerateExceptionChain(ex))
        {
            var message = current.Message ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            if (message.Contains("stream was already consumed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static CreateMessageRequestParams CloneWithToolChoiceMode(CreateMessageRequestParams request, string mode)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Preserve all public writable properties to avoid subtly changing provider behavior on retry.
        var clone = new CreateMessageRequestParams
        {
            // MaxTokens is a required member; seed it in the initializer to satisfy required-member enforcement.
            MaxTokens = request.MaxTokens,
            Messages = request.Messages
        };
        foreach (var prop in typeof(CreateMessageRequestParams).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!prop.CanRead || !prop.CanWrite)
            {
                continue;
            }

            if (prop.GetIndexParameters().Length != 0)
            {
                continue;
            }

            if (string.Equals(prop.Name, nameof(CreateMessageRequestParams.ToolChoice), StringComparison.Ordinal))
            {
                continue;
            }

            var value = prop.GetValue(request);
            prop.SetValue(clone, value);
        }

        clone.ToolChoice = new ToolChoice { Mode = mode };
        return clone;
    }

    private static bool IsBaselineEvidenceComplete(List<ExecutedCommand> commandsExecuted)
    {
        var seenPaths = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var cmd in commandsExecuted)
        {
            if (!cmd.Tool.Equals("report_get", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = TryGetString(cmd.Input, "path");
            if (!string.IsNullOrWhiteSpace(path))
            {
                path = path.Trim();
                if (!TryParseReportGetResponseHasError(cmd.Output ?? string.Empty, out var hasError))
                {
                    continue;
                }

                if (!hasError)
                {
                    seenPaths[path] = true;
                }
            }
        }

        // Keep this aligned with the Phase 1 baseline prompt.
        return BaselineEvidencePaths.All(p => seenPaths.TryGetValue(p, out var ok) && ok);
    }

    private static List<string> BuildDeterministicDoNotRepeatList(List<ExecutedCommand> commandsExecuted)
    {
        var entries = new List<string>(capacity: 50);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var attemptsByKey = BuildToolAttemptInfoByKey(commandsExecuted);

        static string FormatEntry(string toolName, JsonElement toolInput)
        {
            var normalizedTool = (toolName ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalizedTool))
            {
                normalizedTool = "tool";
            }

            if (normalizedTool.Equals("exec", StringComparison.OrdinalIgnoreCase))
            {
                var command = toolInput.ValueKind == JsonValueKind.Object ? TryGetString(toolInput, "command") : toolInput.ToString();
                command = NormalizeDebuggerCommand(command ?? string.Empty);
                command = TruncateText(command, maxChars: 512);
                return string.IsNullOrWhiteSpace(command)
                    ? "exec(command=<missing>)"
                    : $"exec(command=\"{command}\")";
            }

            var canonical = CanonicalizeJson(toolInput);
            canonical = TruncateText(canonical, maxChars: 1024);
            return $"{normalizedTool}({canonical})";
        }

        void AddCommand(ExecutedCommand cmd)
        {
            if (entries.Count >= 50)
            {
                return;
            }

            var tool = (cmd.Tool ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(tool))
            {
                return;
            }

            var key = BuildToolCacheKey(tool, cmd.Input);
            if (string.IsNullOrWhiteSpace(key) || !seenKeys.Add(key))
            {
                return;
            }

            if (!ShouldBlockRepeatingToolKey(key, attemptsByKey))
            {
                return;
            }

            entries.Add(FormatEntry(tool, cmd.Input));
        }

        foreach (var path in BaselineEvidencePaths)
        {
            var cmd = commandsExecuted.FirstOrDefault(c =>
                c.Tool.Equals("report_get", StringComparison.OrdinalIgnoreCase)
                && string.Equals(TryGetString(c.Input, "path")?.Trim(), path, StringComparison.Ordinal));

            if (cmd != null)
            {
                AddCommand(cmd);
            }
        }

        for (var i = commandsExecuted.Count - 1; i >= 0 && entries.Count < 50; i--)
        {
            AddCommand(commandsExecuted[i]);
        }

        return entries;
    }

    private static HashSet<string> BuildExecutedToolKeySet(List<ExecutedCommand> commandsExecuted)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var attemptsByKey = BuildToolAttemptInfoByKey(commandsExecuted);
        foreach (var kvp in attemptsByKey)
        {
            if (ShouldBlockRepeatingToolKey(kvp.Key, attemptsByKey))
            {
                keys.Add(kvp.Key);
            }
        }

        return keys;
    }

    private static JsonElement FilterCheckpointNextSteps(
        JsonElement nextSteps,
        IReadOnlySet<string> executedToolKeys)
    {
        if (nextSteps.ValueKind != JsonValueKind.Array)
        {
            return JsonSerializer.SerializeToElement(Array.Empty<object>());
        }

        var filtered = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in nextSteps.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var tool = TryGetString(item, "tool");
            var call = TryGetString(item, "call");
            var why = TryGetString(item, "why");

            if (string.IsNullOrWhiteSpace(tool) || string.IsNullOrWhiteSpace(call) || string.IsNullOrWhiteSpace(why))
            {
                continue;
            }

            var callKey = string.Empty;
            if (TryParseCheckpointToolCall(tool, call, out var parsed))
            {
                callKey = parsed.ToolCacheKey;
                if (!string.IsNullOrWhiteSpace(callKey))
                {
                    if (executedToolKeys.Contains(callKey))
                    {
                        continue;
                    }

                    if (!seen.Add(callKey))
                    {
                        continue;
                    }
                }
            }

            filtered.Add(new
            {
                tool = tool.Trim(),
                call = TruncateText(call.Trim(), maxChars: 4096),
                why = TruncateText(why.Trim(), maxChars: 2048)
            });

            if (filtered.Count >= 20)
            {
                break;
            }
        }

        return JsonSerializer.SerializeToElement(filtered);
    }

    private static JsonElement AugmentNextStepsWithBaselineCorrections(
        JsonElement nextSteps,
        BaselinePhaseState baselinePhase,
        List<ExecutedCommand> commandsExecuted,
        IReadOnlySet<string> executedToolKeys)
    {
        if (baselinePhase.Complete || baselinePhase.Missing.Count == 0 || commandsExecuted.Count == 0)
        {
            return nextSteps;
        }

        if (nextSteps.ValueKind != JsonValueKind.Array)
        {
            return nextSteps;
        }

        var steps = new List<(string Tool, string Call, string Why)>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in nextSteps.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var tool = TryGetString(item, "tool");
            var call = TryGetString(item, "call");
            var why = TryGetString(item, "why");
            if (string.IsNullOrWhiteSpace(tool) || string.IsNullOrWhiteSpace(call) || string.IsNullOrWhiteSpace(why))
            {
                continue;
            }

            if (TryParseCheckpointToolCall(tool, call, out var parsed) && !string.IsNullOrWhiteSpace(parsed.ToolCacheKey))
            {
                seenKeys.Add(parsed.ToolCacheKey);
            }

            steps.Add((tool.Trim(), call.Trim(), why.Trim()));
            if (steps.Count >= 20)
            {
                break;
            }
        }

        foreach (var missingId in baselinePhase.Missing)
        {
            if (string.IsNullOrWhiteSpace(missingId) || !BaselinePathById.TryGetValue(missingId.Trim(), out var missingPath))
            {
                continue;
            }

            var lastAttempt = commandsExecuted.LastOrDefault(c =>
                c.Tool.Equals("report_get", StringComparison.OrdinalIgnoreCase)
                && string.Equals(TryGetString(c.Input, "path")?.Trim(), missingPath, StringComparison.OrdinalIgnoreCase));

            if (lastAttempt == null)
            {
                continue;
            }

            var output = lastAttempt.Output ?? string.Empty;
            if (!TryParseFirstJsonObject(output, out var root) || root.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!root.TryGetProperty("error", out var errorEl) || errorEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var code = TryGetString(errorEl, "code");
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            if (!TryBuildToolErrorTryHint(root, code, out var tryHint) || string.IsNullOrWhiteSpace(tryHint))
            {
                continue;
            }

            var tool = "report_get";
            if (!TryParseCheckpointToolCall(tool, tryHint, out var parsedTry))
            {
                continue;
            }

            var callKey = parsedTry.ToolCacheKey;
            if (string.IsNullOrWhiteSpace(callKey) || executedToolKeys.Contains(callKey) || !seenKeys.Add(callKey))
            {
                continue;
            }

            var why = $"Recover missing baseline '{missingId.Trim()}': follow the tool 'Try' hint exactly.";
            steps.Insert(0, (tool, tryHint.Trim(), why));
            if (steps.Count > 20)
            {
                steps.RemoveAt(steps.Count - 1);
            }
            break;
        }

        return JsonSerializer.SerializeToElement(steps.Select(s => new { tool = s.Tool, call = s.Call, why = s.Why }).ToList());
    }

    private readonly record struct ParsedToolCall(string ToolName, JsonElement ToolInput, string ToolCacheKey);

    private static bool TryParseCheckpointToolCall(string tool, string call, out ParsedToolCall parsed)
    {
        parsed = default;

        var toolName = (tool ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        var text = (call ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var openParen = text.IndexOf('(');
        var closeParen = text.LastIndexOf(')');
        if (openParen < 0 || closeParen < openParen)
        {
            return false;
        }

        var nameInCall = text.Substring(0, openParen).Trim();
        if (!string.IsNullOrWhiteSpace(nameInCall))
        {
            toolName = nameInCall;
        }

        var args = text.Substring(openParen + 1, closeParen - openParen - 1).Trim();
        if (string.IsNullOrWhiteSpace(args))
        {
            return false;
        }

        JsonElement input;
        if (args.StartsWith('{') && args.EndsWith('}'))
        {
            try
            {
                using var doc = JsonDocument.Parse(args);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }
                input = doc.RootElement.Clone();
            }
            catch
            {
                return false;
            }
        }
        else
        {
            if (!TryParseNamedArgs(args, out input))
            {
                return false;
            }
        }

        var toolCacheKey = BuildToolCacheKey(toolName, input);
        if (string.IsNullOrWhiteSpace(toolCacheKey))
        {
            return false;
        }

        parsed = new ParsedToolCall(toolName, input, toolCacheKey);
        return true;
    }

    private static bool TryParseNamedArgs(string args, out JsonElement input)
    {
        input = default;
        var pairs = SplitTopLevelArguments(args);
        if (pairs.Count == 0)
        {
            return false;
        }

        var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var part in pairs)
        {
            var token = part.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var eq = token.IndexOf('=');
            if (eq < 1)
            {
                continue;
            }

            var key = token.Substring(0, eq).Trim();
            var valueText = token.Substring(eq + 1).Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(valueText))
            {
                continue;
            }

            if (TryParseJsonValue(valueText, out var value))
            {
                obj[key] = value;
                continue;
            }

            obj[key] = valueText.Trim();
        }

        if (obj.Count == 0)
        {
            return false;
        }

        input = JsonSerializer.SerializeToElement(obj);
        return true;
    }

    private static List<string> SplitTopLevelArguments(string args)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(args))
        {
            return parts;
        }

        var depth = 0;
        var inString = false;
        var escape = false;
        var start = 0;

        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (c == '\\')
                {
                    escape = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c is '[' or '{')
            {
                depth++;
                continue;
            }

            if (c is ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (c == ',' && depth == 0)
            {
                parts.Add(args.Substring(start, i - start));
                start = i + 1;
            }
        }

        if (start < args.Length)
        {
            parts.Add(args.Substring(start));
        }

        return parts;
    }

    private static bool TryParseJsonValue(string valueText, out object? value)
    {
        value = null;
        var trimmed = valueText.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (trimmed.StartsWith('{') || trimmed.StartsWith('[') || (trimmed.StartsWith('"') && trimmed.EndsWith('"')))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                value = doc.RootElement.Clone();
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (bool.TryParse(trimmed, out var b))
        {
            value = b;
            return true;
        }

        if (int.TryParse(trimmed, out var i))
        {
            value = i;
            return true;
        }

        if (trimmed.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            value = null;
            return true;
        }

        return false;
    }

    private static string NormalizeCheckpointJson(string checkpointJson, List<ExecutedCommand> commandsExecuted, string baselineKey)
    {
        ArgumentNullException.ThrowIfNull(checkpointJson);
        ArgumentNullException.ThrowIfNull(commandsExecuted);
        ArgumentNullException.ThrowIfNull(baselineKey);

        if (string.IsNullOrWhiteSpace(checkpointJson))
        {
            return checkpointJson;
        }

        if (!TryParseFirstJsonObject(checkpointJson, out var json) || json.ValueKind != JsonValueKind.Object)
        {
            return checkpointJson;
        }

        var baselinePhase = ComputeBaselinePhaseState(commandsExecuted);
        var existingFacts = TryGetStringArray(json, "facts") ?? new List<string>();
        var facts = new List<string>(capacity: 50);
        var factSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static bool IsOverriddenFact(string fact)
        {
            if (string.IsNullOrWhiteSpace(fact))
            {
                return true;
            }

            var trimmed = fact.TrimStart();
            return trimmed.StartsWith("baselineComplete=", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("BASELINE_KEY:", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("BASELINE_CALL:", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("OPTIONAL_STATUS:", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("RETRY:", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("PHASE: baselineComplete=", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("PHASE: baselineCallsCompleted=", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("PHASE: baselineMissing=", StringComparison.OrdinalIgnoreCase);
        }

        void AddFact(string? fact)
        {
            if (string.IsNullOrWhiteSpace(fact))
            {
                return;
            }

            if (facts.Count >= 50)
            {
                return;
            }

            var trimmed = TruncateText(fact.Trim(), maxChars: 2048);
            if (!factSeen.Add(trimmed))
            {
                return;
            }

            facts.Add(trimmed);
        }

        AddFact($"BASELINE_KEY: {baselineKey.Trim()}");
        AddFact(BuildBaselineCompleteFact(baselinePhase, commandsExecuted));
        AddFact($"PHASE: baselineCallsCompleted={JsonSerializer.Serialize(baselinePhase.Completed)}");
        if (!baselinePhase.Complete)
        {
            AddFact($"PHASE: baselineMissing={JsonSerializer.Serialize(baselinePhase.Missing)}");
        }

        foreach (var baselineCallFact in BaselineCallFacts)
        {
            AddFact(baselineCallFact);
        }

        foreach (var optionalCallFact in OptionalCallFacts)
        {
            AddFact(optionalCallFact);
        }

        foreach (var optionalStatusFact in BuildOptionalStatusFacts(commandsExecuted))
        {
            AddFact(optionalStatusFact);
        }

        foreach (var retryFact in BuildRetryFacts(commandsExecuted))
        {
            AddFact(retryFact);
        }

        AddFact("REPORT_GET CURSOR: cursor is valid only for identical query shape (same path/select/where/pageKind/limit). If any change, drop cursor. Prefer where filters over cursor paging for large arrays.");
        AddFact("REPORT_GET SYNTAX: report_get.path is required (never call report_get() / report_get({})). Array indices must be numeric (stackTrace[0] valid; stackTrace[0:5] invalid).");
        AddFact("REPORT_GET STRUCTURE: do not guess .items or report shape. If a segment/property is missing, query the nearest parent node first (pageKind=\"object\", limit<=50). Avoid report_get(path=\"analysis\"); fetch narrow subpaths.");
        AddFact("REPORT_GET RECOVERY: if a tool error includes a \"Try:\" hint, follow it exactly. invalid_cursor => retry same query WITHOUT cursor. invalid_path (missing property/index or .items misuse) => drop .items; if node is an array, page the array node with limit/cursor; otherwise step back and query parent keys. Stop after 2 failed corrections; pick a different step or finalize with limitations.");
        AddFact("META TOOL CONTRACT: analysis_hypothesis_register.hypotheses is required; analysis_evidence_add.items is required. Do not call meta tools with empty args.");
        AddFact("META EXAMPLE: analysis_hypothesis_register({\"hypotheses\":[{\"hypothesis\":\"Assembly version mismatch\",\"confidence\":\"medium\"}]})");
        AddFact("META EXAMPLE: analysis_evidence_add({\"items\":[{\"source\":\"report_get(path=\\\"analysis.modules\\\")\",\"finding\":\"...\"}]})");
        AddFact("HYPOTHESIS HYGIENE: do not register new hypotheses unless new evidence arrived since the last hypothesis update (baseline counts once). If ignoredAtCapacity>0, stop registering; use analysis_hypothesis_score or converge.");
        AddFact("QUALITY: do not assert trimming/version mismatch/instrumentation without at least one supporting evidence item (module/assembly versions, runtime info, profiler indicators).");

        var lastToolFact = BuildLastToolFact(commandsExecuted);
        if (!string.IsNullOrWhiteSpace(lastToolFact))
        {
            AddFact(lastToolFact);
        }

        foreach (var fact in existingFacts)
        {
            if (IsOverriddenFact(fact))
            {
                continue;
            }

            AddFact(fact);
        }

        var deterministicDoNotRepeat = BuildDeterministicDoNotRepeatList(commandsExecuted);
        var existingDoNotRepeat = TryGetStringArray(json, "doNotRepeat") ?? new List<string>();

        var mergedDoNotRepeat = new List<string>(capacity: 50);
        var mergedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? entry)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                return;
            }

            var trimmed = entry.Trim();
            if (!mergedSeen.Add(trimmed))
            {
                return;
            }

            if (mergedDoNotRepeat.Count >= 50)
            {
                return;
            }

            mergedDoNotRepeat.Add(trimmed);
        }

        foreach (var entry in existingDoNotRepeat)
        {
            Add(entry);
        }

        foreach (var entry in deterministicDoNotRepeat)
        {
            Add(entry);
        }

        var hypotheses = json.TryGetProperty("hypotheses", out var hypothesesEl)
            ? hypothesesEl
            : JsonSerializer.SerializeToElement(Array.Empty<object>());
        var evidence = json.TryGetProperty("evidence", out var evidenceEl)
            ? evidenceEl
            : JsonSerializer.SerializeToElement(Array.Empty<object>());
        var executedToolKeys = BuildExecutedToolKeySet(commandsExecuted);
        var nextSteps = json.TryGetProperty("nextSteps", out var nextStepsEl)
            ? FilterCheckpointNextSteps(nextStepsEl, executedToolKeys)
            : JsonSerializer.SerializeToElement(Array.Empty<object>());

        nextSteps = AugmentNextStepsWithBaselineCorrections(nextSteps, baselinePhase, commandsExecuted, executedToolKeys);

        var normalized = JsonSerializer.Serialize(new
        {
            facts,
            hypotheses,
            evidence,
            doNotRepeat = mergedDoNotRepeat,
            nextSteps
        }, new JsonSerializerOptions { WriteIndented = false });

        if (normalized.Length <= MaxCheckpointJsonChars)
        {
            return normalized;
        }

        // Ensure we never return invalid/truncated JSON. Prefer a smaller checkpoint rather than truncating.
        var trimmedFacts = facts.Take(30).Select(f => TruncateText(f, maxChars: 1024)).ToList();
        var trimmedDoNotRepeat = mergedDoNotRepeat.Take(30).Select(d => TruncateText(d, maxChars: 512)).ToList();

        var fallback = JsonSerializer.Serialize(new
        {
            facts = trimmedFacts,
            hypotheses = Array.Empty<object>(),
            evidence = Array.Empty<object>(),
            doNotRepeat = trimmedDoNotRepeat,
            nextSteps = Array.Empty<object>()
        }, new JsonSerializerOptions { WriteIndented = false });

        return fallback.Length <= MaxCheckpointJsonChars
            ? fallback
            : JsonSerializer.Serialize(new
            {
                facts = new[] { "Checkpoint normalization exceeded size limits; using minimal fallback." },
                hypotheses = Array.Empty<object>(),
                evidence = Array.Empty<object>(),
                doNotRepeat = Array.Empty<string>(),
                nextSteps = Array.Empty<object>()
            }, new JsonSerializerOptions { WriteIndented = false });
    }

    private static string BuildDeterministicCheckpointJson(
        string passName,
        List<ExecutedCommand> commandsExecuted,
        int commandsExecutedAtLastCheckpoint,
        string? baselineKey = null)
    {
        var evidence = BuildCheckpointEvidenceSnapshot(passName, commandsExecuted, commandsExecutedAtLastCheckpoint);

        var doNotRepeat = BuildDeterministicDoNotRepeatList(commandsExecuted);
        var baselinePhase = passName.Equals("analysis", StringComparison.OrdinalIgnoreCase)
            ? ComputeBaselinePhaseState(commandsExecuted)
            : new BaselinePhaseState(Complete: false, Completed: [], Missing: []);
        var executedToolKeys = passName.Equals("analysis", StringComparison.OrdinalIgnoreCase)
            ? BuildExecutedToolKeySet(commandsExecuted)
            : new HashSet<string>(StringComparer.Ordinal);
        var nextSteps = passName.Equals("analysis", StringComparison.OrdinalIgnoreCase)
            ? AugmentNextStepsWithBaselineCorrections(
                JsonSerializer.SerializeToElement(Array.Empty<object>()),
                baselinePhase,
                commandsExecuted,
                executedToolKeys)
            : JsonSerializer.SerializeToElement(Array.Empty<object>());

        var facts = new List<string>
        {
            "Checkpoint synthesis unavailable; using deterministic fallback checkpoint.",
            $"pass={passName}",
            $"uniqueToolCalls={doNotRepeat.Count}"
        };

        if (passName.Equals("analysis", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(baselineKey))
            {
                facts.Add($"BASELINE_KEY: {baselineKey.Trim()}");
            }

            facts.Add(BuildBaselineCompleteFact(baselinePhase, commandsExecuted));
            facts.Add($"PHASE: baselineCallsCompleted={JsonSerializer.Serialize(baselinePhase.Completed)}");
            if (!baselinePhase.Complete)
            {
                facts.Add($"PHASE: baselineMissing={JsonSerializer.Serialize(baselinePhase.Missing)}");
            }

            facts.AddRange(BaselineCallFacts);
            facts.AddRange(OptionalCallFacts);
            facts.AddRange(BuildOptionalStatusFacts(commandsExecuted));
            facts.AddRange(BuildRetryFacts(commandsExecuted));

            facts.Add("REPORT_GET CURSOR: cursor is valid only for identical query shape (same path/select/where/pageKind/limit). If any change, drop cursor. Prefer where filters over cursor paging for large arrays.");
            facts.Add("REPORT_GET SYNTAX: report_get.path is required (never call report_get() / report_get({})). Array indices must be numeric (stackTrace[0] valid; stackTrace[0:5] invalid).");
            facts.Add("REPORT_GET STRUCTURE: do not guess .items or report shape. If a segment/property is missing, query the nearest parent node first (pageKind=\"object\", limit<=50). Avoid report_get(path=\"analysis\"); fetch narrow subpaths.");
            facts.Add("REPORT_GET RECOVERY: if a tool error includes a \"Try:\" hint, follow it exactly. invalid_cursor => retry same query WITHOUT cursor. invalid_path (missing property/index or .items misuse) => drop .items; if node is an array, page the array node with limit/cursor; otherwise step back and query parent keys. Stop after 2 failed corrections; pick a different step or finalize with limitations.");
            facts.Add("META TOOL CONTRACT: analysis_hypothesis_register.hypotheses is required; analysis_evidence_add.items is required. Do not call meta tools with empty args.");
            facts.Add("META EXAMPLE: analysis_hypothesis_register({\"hypotheses\":[{\"hypothesis\":\"Assembly version mismatch\",\"confidence\":\"medium\"}]})");
            facts.Add("META EXAMPLE: analysis_evidence_add({\"items\":[{\"source\":\"report_get(path=\\\"analysis.modules\\\")\",\"finding\":\"...\"}]})");
            facts.Add("HYPOTHESIS HYGIENE: do not register new hypotheses unless new evidence arrived since the last hypothesis update (baseline counts once). If ignoredAtCapacity>0, stop registering; use analysis_hypothesis_score or converge.");
            facts.Add("QUALITY: do not assert trimming/version mismatch/instrumentation without at least one supporting evidence item (module/assembly versions, runtime info, profiler indicators).");

            var lastToolFact = BuildLastToolFact(commandsExecuted);
            if (!string.IsNullOrWhiteSpace(lastToolFact))
            {
                facts.Add(lastToolFact);
            }
        }

        var evidenceMaxChars = 12_000;
        var doNotRepeatCount = doNotRepeat.Count;

        while (true)
        {
            var checkpoint = new
            {
                facts = facts.Select(f => TruncateText(f, maxChars: 1024)).ToList(),
                hypotheses = Array.Empty<object>(),
                evidence = new[]
                {
                    new { id = "E0", source = "deterministic", finding = TruncateText(evidence, maxChars: evidenceMaxChars) }
                },
                doNotRepeat = doNotRepeat.Take(doNotRepeatCount).ToList(),
                nextSteps
            };

            var json = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = false });
            if (json.Length <= MaxCheckpointJsonChars)
            {
                return json;
            }

            if (evidenceMaxChars > 1024)
            {
                evidenceMaxChars = Math.Max(1024, evidenceMaxChars / 2);
                continue;
            }

            if (doNotRepeatCount > 10)
            {
                doNotRepeatCount = Math.Max(10, doNotRepeatCount / 2);
                continue;
            }

            var minimal = JsonSerializer.Serialize(new
            {
                facts = facts.Take(10).Select(f => TruncateText(f, maxChars: 256)).ToList(),
                hypotheses = Array.Empty<object>(),
                evidence = Array.Empty<object>(),
                doNotRepeat = doNotRepeat.Take(doNotRepeatCount).ToList(),
                nextSteps = Array.Empty<object>()
            }, new JsonSerializerOptions { WriteIndented = false });

            return minimal.Length <= MaxCheckpointJsonChars
                ? minimal
                : JsonSerializer.Serialize(new
                {
                    facts = new[] { "Deterministic checkpoint exceeded size limits; using minimal fallback." },
                    hypotheses = Array.Empty<object>(),
                    evidence = Array.Empty<object>(),
                    doNotRepeat = Array.Empty<string>(),
                    nextSteps = Array.Empty<object>()
                }, new JsonSerializerOptions { WriteIndented = false });
        }
    }

    private static string BuildDeterministicLoopBreakCheckpointJson(
        string passName,
        List<ExecutedCommand> commandsExecuted,
        int commandsExecutedAtLastCheckpoint,
        string baselineKey,
        string reason,
        AiEvidenceLedger evidenceLedger,
        AiHypothesisTracker hypothesisTracker,
        bool forceFinalizeNow = false)
    {
        var doNotRepeat = BuildDeterministicDoNotRepeatList(commandsExecuted);
        var baselinePhase = ComputeBaselinePhaseState(commandsExecuted);
        var baselineEvidenceFact = BuildBaselineEvidenceMappingFact(evidenceLedger);

        var facts = new List<string>(capacity: 50)
        {
            "Deterministic checkpoint injected to break a tool-calling loop.",
            $"LOOP_GUARD: reason={reason}",
            $"pass={passName}",
            $"BASELINE_KEY: {baselineKey.Trim()}",
            BuildBaselineCompleteFact(baselinePhase, commandsExecuted),
            $"PHASE: baselineCallsCompleted={JsonSerializer.Serialize(baselinePhase.Completed)}"
        };

        if (!baselinePhase.Complete)
        {
            facts.Add($"PHASE: baselineMissing={JsonSerializer.Serialize(baselinePhase.Missing)}");
        }
        if (reason.StartsWith("baseline_blocked", StringComparison.OrdinalIgnoreCase))
        {
            facts.Add("PHASE: baselineIncompleteButBlocked=true");
            facts.Add("LIMITATIONS: Baseline evidence is incomplete and appears blocked; finalize with an explicit limitations section and avoid high confidence.");
        }

        facts.Add(baselineEvidenceFact);
        facts.AddRange(BaselineCallFacts);
        facts.AddRange(OptionalCallFacts);
        facts.AddRange(BuildOptionalStatusFacts(commandsExecuted));
        facts.AddRange(BuildRetryFacts(commandsExecuted));

        facts.Add("REPORT_GET CURSOR: cursor is valid only for identical query shape (same path/select/where/pageKind/limit). If any change, drop cursor. Prefer where filters over cursor paging for large arrays.");
        facts.Add("REPORT_GET SYNTAX: report_get.path is required (never call report_get() / report_get({})). Array indices must be numeric (stackTrace[0] valid; stackTrace[0:5] invalid).");
        facts.Add("REPORT_GET STRUCTURE: do not guess .items or report shape. If a segment/property is missing, query the nearest parent node first (pageKind=\"object\", limit<=50). Avoid report_get(path=\"analysis\"); fetch narrow subpaths.");
        facts.Add("REPORT_GET RECOVERY: if a tool error includes a \"Try:\" hint, follow it exactly. invalid_cursor => retry same query WITHOUT cursor. invalid_path (missing property/index or .items misuse) => drop .items; if node is an array, page the array node with limit/cursor; otherwise step back and query parent keys. Stop after 2 failed corrections; pick a different step or finalize with limitations.");
        facts.Add("META TOOL CONTRACT: analysis_hypothesis_register.hypotheses is required; analysis_evidence_add.items is required. Do not call meta tools with empty args.");
        facts.Add("META EXAMPLE: analysis_hypothesis_register({\"hypotheses\":[{\"hypothesis\":\"Assembly version mismatch\",\"confidence\":\"medium\"}]})");
        facts.Add("META EXAMPLE: analysis_evidence_add({\"items\":[{\"source\":\"report_get(path=\\\"analysis.modules\\\")\",\"finding\":\"...\"}]})");
        facts.Add("HYPOTHESIS HYGIENE: do not register new hypotheses unless new evidence arrived since the last hypothesis update (baseline counts once). If ignoredAtCapacity>0, stop registering; use analysis_hypothesis_score or converge.");
        facts.Add("QUALITY: do not assert trimming/version mismatch/instrumentation without at least one supporting evidence item (module/assembly versions, runtime info, profiler indicators).");

        var lastToolFact = BuildLastToolFact(commandsExecuted);
        if (!string.IsNullOrWhiteSpace(lastToolFact))
        {
            facts.Add(lastToolFact);
        }

        var nextSteps = BuildLoopBreakNextSteps(commandsExecuted, reason, forceFinalizeNow);

        var checkpoint = new
        {
            facts = facts.Take(50).Select(f => TruncateText(f, maxChars: 2048)).ToList(),
            hypotheses = Array.Empty<object>(),
            evidence = BuildLastToolEvidence(commandsExecuted),
            doNotRepeat = doNotRepeat.Take(50).ToList(),
            nextSteps
        };

        var json = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = false });
        if (json.Length <= MaxCheckpointJsonChars)
        {
            return AugmentCheckpointWithConvergenceFacts(json, evidenceLedger, hypothesisTracker, forceFinalizeNow: forceFinalizeNow);
        }

        // If we exceed the max size, shrink doNotRepeat and drop baseline call facts first.
        var reducedFacts = facts.Where(f => !f.StartsWith("BASELINE_CALL:", StringComparison.OrdinalIgnoreCase)).Take(30).ToList();
        var reducedCheckpoint = new
        {
            facts = reducedFacts.Select(f => TruncateText(f, maxChars: 1024)).ToList(),
            hypotheses = Array.Empty<object>(),
            evidence = BuildLastToolEvidence(commandsExecuted),
            doNotRepeat = doNotRepeat.Take(25).ToList(),
            nextSteps
        };

        var reducedJson = JsonSerializer.Serialize(reducedCheckpoint, new JsonSerializerOptions { WriteIndented = false });
        return reducedJson.Length <= MaxCheckpointJsonChars
            ? AugmentCheckpointWithConvergenceFacts(reducedJson, evidenceLedger, hypothesisTracker, forceFinalizeNow: forceFinalizeNow)
            : BuildDeterministicCheckpointJson(passName, commandsExecuted, commandsExecutedAtLastCheckpoint, baselineKey);
    }

    private static List<object> BuildLastToolEvidence(List<ExecutedCommand> commandsExecuted)
    {
        if (commandsExecuted.Count == 0)
        {
            return [];
        }

        var last = commandsExecuted[^1];
        var tool = (last.Tool ?? string.Empty).Trim();
        var input = last.Input;
        var canonical = CanonicalizeJson(input);
        canonical = TruncateText(canonical, maxChars: 512);
        var source = string.IsNullOrWhiteSpace(tool) ? "last_tool" : $"{tool}({canonical})";
        var finding = BuildToolOutputExcerpt(last.Output ?? string.Empty);
        return
        [
            new
            {
                id = "E_LAST",
                source,
                finding
            }
        ];
    }

    private static string? BuildLastToolFact(List<ExecutedCommand> commandsExecuted)
    {
        if (commandsExecuted.Count == 0)
        {
            return null;
        }

        var last = commandsExecuted[^1];
        var tool = (last.Tool ?? string.Empty).Trim();
        var canonical = CanonicalizeJson(last.Input);
        canonical = TruncateText(canonical, maxChars: 256);
        var excerpt = BuildToolOutputExcerpt(last.Output ?? string.Empty);
        return string.IsNullOrWhiteSpace(tool)
            ? $"LAST_TOOL: ({canonical}) -> {excerpt}"
            : $"LAST_TOOL: {tool}({canonical}) -> {excerpt}";
    }

    private static string BuildToolOutputExcerpt(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "(empty)";
        }

        var trimmed = output.Trim();
        if (TryParseFirstJsonObject(trimmed, out var json) && json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var errorEl))
        {
            var code = TryGetString(errorEl, "code");
            var message = TryGetString(errorEl, "message");
            var baseLine = string.IsNullOrWhiteSpace(code) ? (message ?? trimmed) : $"{code}: {message}";

            var errorLines = new List<string>(capacity: 2);
            if (!string.IsNullOrWhiteSpace(baseLine))
            {
                errorLines.Add(baseLine.Trim());
            }

            if (TryBuildToolErrorTryHint(json, code, out var tryHint) && !string.IsNullOrWhiteSpace(tryHint))
            {
                errorLines.Add($"Try: {tryHint.Trim()}");
            }

            var compact = string.Join("\n", errorLines.Where(l => !string.IsNullOrWhiteSpace(l)).Take(3));
            return TruncateText(string.IsNullOrWhiteSpace(compact) ? trimmed : compact, maxChars: 600);
        }

        var lines = trimmed.Split('\n');
        var excerptLines = lines.Take(3).Select(l => l.TrimEnd()).ToList();
        var excerpt = string.Join("\n", excerptLines);
        return TruncateText(excerpt, maxChars: 600);
    }

    private static bool TryBuildToolErrorTryHint(JsonElement root, string? code, out string tryHint)
    {
        tryHint = string.Empty;

        if (root.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        if (!root.TryGetProperty("extra", out var extraEl) || extraEl.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (code.Equals("invalid_cursor", StringComparison.OrdinalIgnoreCase))
        {
            if (extraEl.TryGetProperty("recovery", out var recoveryEl) && recoveryEl.ValueKind == JsonValueKind.Object)
            {
                var retry = TryGetString(recoveryEl, "retryWithoutCursor");
                if (!string.IsNullOrWhiteSpace(retry))
                {
                    tryHint = TruncateText(retry.Trim(), maxChars: 512);
                    return true;
                }
            }

            return false;
        }

        if (code.Equals("too_large", StringComparison.OrdinalIgnoreCase))
        {
            if (extraEl.TryGetProperty("exampleCalls", out var callsEl) && callsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in callsEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var call = item.GetString();
                        if (!string.IsNullOrWhiteSpace(call))
                        {
                            tryHint = TruncateText(call.Trim(), maxChars: 512);
                            return true;
                        }
                    }
                }
            }

            if (extraEl.TryGetProperty("suggestedPaths", out var suggestedEl) && suggestedEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in suggestedEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var path = item.GetString();
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            tryHint = $"report_get(path=\"{TruncateText(path.Trim(), maxChars: 256)}\")";
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        return false;
    }

    private static List<object> BuildLoopBreakNextSteps(List<ExecutedCommand> commandsExecuted, string reason, bool forceFinalizeNow)
    {
        var steps = new List<object>(capacity: 3);

        if (forceFinalizeNow)
        {
            var why = reason.StartsWith("baseline_blocked", StringComparison.OrdinalIgnoreCase)
                ? "Baseline evidence appears blocked; finalize now with explicit limitations and reduced confidence. Do not restart baseline."
                : "Loop guard triggered repeatedly; finalize now using BASELINE_EVIDENCE mapping + evidence IDs. Do not restart baseline.";

            steps.Add(new
            {
                tool = "analysis_complete",
                call = "analysis_complete(rootCause=\"...\", confidence=\"low|medium\", reasoning=\"...\", evidence=[\"E#\", ...])",
                why
            });
            return steps;
        }

        var executedToolKeys = BuildExecutedToolKeySet(commandsExecuted);

        if (reason.StartsWith("invalid_cursor", StringComparison.OrdinalIgnoreCase))
        {
            var lastInvalid = commandsExecuted.LastOrDefault(c =>
                c.Tool.Equals("report_get", StringComparison.OrdinalIgnoreCase)
                && TryGetReportGetErrorCode(c.Output ?? string.Empty, out var code)
                && code.Equals("invalid_cursor", StringComparison.OrdinalIgnoreCase));

            if (lastInvalid != null && lastInvalid.Input.ValueKind == JsonValueKind.Object)
            {
                var inputObj = lastInvalid.Input;
                using var doc = JsonDocument.Parse(CanonicalizeJson(inputObj));
                var root = doc.RootElement.Clone();
                var withoutCursor = RemoveJsonProperty(root, "cursor");
                steps.Add(new
                {
                    tool = "report_get",
                    call = $"report_get({CanonicalizeJson(withoutCursor)})",
                    why = "Retry the same query without cursor (cursor is only valid when query shape is unchanged)."
                });
                return steps;
            }
        }

        if (reason.StartsWith("hypothesis_register_no_progress", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("hypothesis_register_spam", StringComparison.OrdinalIgnoreCase))
        {
            steps.Add(new
            {
                tool = "analysis_hypothesis_score",
                call = "analysis_hypothesis_score(updates=[{id:\"H1\", confidence:\"medium\", supportsEvidenceIds:[\"E#\"], contradictsEvidenceIds:[\"E#\"], notes:\"...\"}])",
                why = "Stop adding new hypotheses (no progress). Update/score existing hypothesis IDs with discriminating evidence, or converge."
            });
            return steps;
        }

        steps.Add(new
        {
            tool = "analysis_complete",
            call = "analysis_complete(rootCause=\"...\", confidence=\"low|medium\", reasoning=\"...\", evidence=[\"E#\", ...])",
            why = "No safe non-duplicate next step found; finalize using current evidence (include limitations if data is missing)."
        });

        var candidates = new List<(string Call, string Why)>
        {
            ("report_get(path=\"analysis.modules\", limit=20, select=[\"name\",\"version\",\"path\",\"baseAddress\"])", "Fetch a compact module list to support/deny version mismatch or native interop hypotheses."),
            ("report_get(path=\"analysis.environment.runtime\", pageKind=\"object\", limit=30)", "Fetch runtime details (CLR type/version) for compatibility/version-mismatch reasoning."),
            ("report_get(path=\"analysis.threads.summary\", pageKind=\"object\", limit=20)", "Fetch thread summary to guide whether this is a faulting thread vs systemic issue."),
            ("report_get(path=\"analysis.synchronization.summary\", pageKind=\"object\", limit=30)", "Fetch synchronization summary to identify deadlocks/contention."),
            ("report_get(path=\"analysis.security\", pageKind=\"object\", limit=30)", "Fetch security summary (if present) to include/ignore security angle.")
        };

        foreach (var candidate in candidates)
        {
            var openParen = candidate.Call.IndexOf('(');
            var closeParen = candidate.Call.LastIndexOf(')');
            if (openParen < 0 || closeParen < openParen)
            {
                continue;
            }

            var args = candidate.Call.Substring(openParen + 1, closeParen - openParen - 1).Trim();
            if (!TryParseNamedArgs(args, out var input))
            {
                continue;
            }

            var key = BuildToolCacheKey("report_get", input);
            if (!string.IsNullOrWhiteSpace(key) && !executedToolKeys.Contains(key))
            {
                steps[0] = new
                {
                    tool = "report_get",
                    call = candidate.Call,
                    why = candidate.Why
                };
                return steps;
            }
        }

        return steps;
    }

    private static JsonElement RemoveJsonProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return element;
        }

        var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.Ordinal))
            {
                continue;
            }

            obj[prop.Name] = prop.Value.Clone();
        }

        return JsonSerializer.SerializeToElement(obj);
    }

    private static string BuildBaselineEvidenceMappingFact(AiEvidenceLedger evidenceLedger)
    {
        var idByBaseline = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in evidenceLedger.Items)
        {
            if (item.Tags == null || item.Tags.Count == 0)
            {
                continue;
            }

            foreach (var tag in item.Tags)
            {
                if (string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }

                var trimmed = tag.Trim();
                if (!trimmed.StartsWith("BASELINE_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var baselineId = trimmed.Substring("BASELINE_".Length);
                if (string.IsNullOrWhiteSpace(baselineId))
                {
                    continue;
                }

                if (!idByBaseline.ContainsKey(baselineId))
                {
                    idByBaseline[baselineId] = item.Id;
                }
            }
        }

        var parts = BaselineCalls
            .Select(c =>
            {
                var id = c.Id;
                return idByBaseline.TryGetValue(id, out var evidenceId) && !string.IsNullOrWhiteSpace(evidenceId)
                    ? $"{id}={evidenceId}"
                    : $"{id}=<missing>";
            })
            .ToList();

        return $"BASELINE_EVIDENCE: {string.Join(' ', parts)}";
    }

    private static string AugmentCheckpointWithConvergenceFacts(
        string checkpointJson,
        AiEvidenceLedger evidenceLedger,
        AiHypothesisTracker hypothesisTracker,
        bool forceFinalizeNow = false)
    {
        if (string.IsNullOrWhiteSpace(checkpointJson))
        {
            return checkpointJson;
        }

        if (!TryParseFirstJsonObject(checkpointJson, out var json) || json.ValueKind != JsonValueKind.Object)
        {
            return checkpointJson;
        }

        var facts = TryGetStringArray(json, "facts") ?? new List<string>();
        var baselineEvidenceFact = BuildBaselineEvidenceMappingFact(evidenceLedger);

        var finalizeNow = forceFinalizeNow || ShouldFinalizeNow(evidenceLedger, hypothesisTracker);

        var updatedFacts = new List<string>(capacity: 50);
        foreach (var fact in facts)
        {
            if (string.IsNullOrWhiteSpace(fact))
            {
                continue;
            }

            var trimmed = fact.Trim();
            if (trimmed.StartsWith("PHASE: finalizeNow=", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("BASELINE_EVIDENCE:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            updatedFacts.Add(trimmed);
            if (updatedFacts.Count >= 45)
            {
                break;
            }
        }

        updatedFacts.Add(baselineEvidenceFact);
        updatedFacts.Add($"PHASE: finalizeNow={finalizeNow.ToString().ToLowerInvariant()}");

        var hypotheses = json.TryGetProperty("hypotheses", out var hypothesesEl)
            ? hypothesesEl
            : JsonSerializer.SerializeToElement(Array.Empty<object>());
        var evidence = json.TryGetProperty("evidence", out var evidenceEl)
            ? evidenceEl
            : JsonSerializer.SerializeToElement(Array.Empty<object>());
        var doNotRepeat = json.TryGetProperty("doNotRepeat", out var dnrEl)
            ? dnrEl
            : JsonSerializer.SerializeToElement(Array.Empty<string>());

        var nextSteps = json.TryGetProperty("nextSteps", out var nextStepsEl)
            ? nextStepsEl
            : JsonSerializer.SerializeToElement(Array.Empty<object>());

        if (finalizeNow)
        {
            nextSteps = JsonSerializer.SerializeToElement(new[]
            {
                new
                {
                    tool = "analysis_complete",
                    call = "analysis_complete(rootCause=\"...\", confidence=\"low|medium|high\", reasoning=\"...\", evidence=[\"E#\", ...])",
                    why = "Finalize now: evidence + hypotheses are sufficient. Use BASELINE_EVIDENCE mapping + evidence IDs; do not restart baseline."
                }
            });
        }

        var augmented = JsonSerializer.Serialize(new
        {
            facts = updatedFacts.Take(50).Select(f => TruncateText(f, maxChars: 2048)).ToList(),
            hypotheses,
            evidence,
            doNotRepeat,
            nextSteps
        }, new JsonSerializerOptions { WriteIndented = false });

        return augmented.Length <= MaxCheckpointJsonChars
            ? augmented
            : checkpointJson;
    }

    private static bool ShouldRemoveOptionalBaselineNextStep(JsonElement step, string optionalToolCacheKey, string optionalCall)
    {
        if (step.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var tool = TryGetString(step, "tool");
        var call = TryGetString(step, "call");
        if (string.IsNullOrWhiteSpace(tool) || string.IsNullOrWhiteSpace(call))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(optionalToolCacheKey)
            && TryParseCheckpointToolCall(tool, call, out var parsed)
            && string.Equals(parsed.ToolCacheKey, optionalToolCacheKey, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(tool.Trim(), "report_get", StringComparison.OrdinalIgnoreCase)
               && string.Equals(call.Trim(), optionalCall, StringComparison.OrdinalIgnoreCase);
    }

    private static string AugmentCheckpointWithOptionalBaselineScheduling(
        string checkpointJson,
        string baselineKey,
        bool baselineComplete,
        bool baselineBlocked,
        List<ExecutedCommand> commandsExecuted,
        OptionalBaselineCallScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(commandsExecuted);
        ArgumentNullException.ThrowIfNull(scheduler);

        if (string.IsNullOrWhiteSpace(checkpointJson))
        {
            return checkpointJson;
        }

        if (!TryParseFirstJsonObject(checkpointJson, out var json) || json.ValueKind != JsonValueKind.Object)
        {
            return checkpointJson;
        }

        var facts = TryGetStringArray(json, "facts") ?? new List<string>();
        var nextSteps = json.TryGetProperty("nextSteps", out var nextStepsEl) ? nextStepsEl : JsonSerializer.SerializeToElement(Array.Empty<object>());

        var isFinalizing = facts.Any(f => string.Equals(f?.Trim(), "PHASE: finalizeNow=true", StringComparison.OrdinalIgnoreCase))
                           || (nextSteps.ValueKind == JsonValueKind.Array
                               && nextSteps.EnumerateArray().Any(s =>
                                   s.ValueKind == JsonValueKind.Object
                                   && string.Equals(TryGetString(s, "tool")?.Trim(), "analysis_complete", StringComparison.OrdinalIgnoreCase)));

        foreach (var optional in OptionalCalls)
        {
            if (string.IsNullOrWhiteSpace(optional.Id))
            {
                continue;
            }

            var attempted = commandsExecuted.Any(c =>
                c.Tool.Equals("report_get", StringComparison.OrdinalIgnoreCase)
                && string.Equals(TryGetString(c.Input, "path")?.Trim(), optional.Path, StringComparison.OrdinalIgnoreCase));

            var hasStatus = facts.Any(f => f != null && f.TrimStart().StartsWith($"OPTIONAL_STATUS: {optional.Id}=", StringComparison.OrdinalIgnoreCase));
            if (!hasStatus)
            {
                continue;
            }

            if (attempted || baselineBlocked || isFinalizing || !baselineComplete)
            {
                continue;
            }

            var optionalStepKey = string.Empty;
            if (TryParseCheckpointToolCall("report_get", optional.Call, out var parsedOptional))
            {
                optionalStepKey = parsedOptional.ToolCacheKey;
            }

            var hasOptionalNextStep = false;
            if (nextSteps.ValueKind == JsonValueKind.Array)
            {
                foreach (var step in nextSteps.EnumerateArray())
                {
                    if (step.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var tool = TryGetString(step, "tool");
                    var call = TryGetString(step, "call");
                    if (string.IsNullOrWhiteSpace(tool) || string.IsNullOrWhiteSpace(call))
                    {
                        continue;
                    }

                    if (TryParseCheckpointToolCall(tool, call, out var parsedStep)
                        && !string.IsNullOrWhiteSpace(optionalStepKey)
                        && string.Equals(parsedStep.ToolCacheKey, optionalStepKey, StringComparison.Ordinal))
                    {
                        hasOptionalNextStep = true;
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(optionalStepKey)
                        && string.Equals(tool.Trim(), "report_get", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(call.Trim(), optional.Call, StringComparison.OrdinalIgnoreCase))
                    {
                        hasOptionalNextStep = true;
                        break;
                    }
                }
            }

            if (hasOptionalNextStep && !string.IsNullOrWhiteSpace(optionalStepKey))
            {
                scheduler.MarkScheduled(baselineKey, optional.Id);
            }

            if (!scheduler.WasScheduled(baselineKey, optional.Id))
            {
                scheduler.MarkScheduled(baselineKey, optional.Id);

                if (hasOptionalNextStep)
                {
                    continue;
                }

                if (nextSteps.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var existing = nextSteps.EnumerateArray().ToList();
                if (existing.Count >= 5)
                {
                    continue;
                }

                existing.Add(JsonSerializer.SerializeToElement(new
                {
                    tool = "report_get",
                    call = optional.Call,
                    why = "Optional baseline (run at most once): fetch exception analysis details if it helps type/method resolution; skip if it errors."
                }));

                nextSteps = JsonSerializer.SerializeToElement(existing);
            }
            else
            {
                // Scheduled once but still not attempted: mark as skipped to avoid repeated low-ROI suggestions.
                facts = facts
                    .Where(f => f == null || !f.TrimStart().StartsWith($"OPTIONAL_STATUS: {optional.Id}=", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                facts.Add($"OPTIONAL_STATUS: {optional.Id}=skipped(reason=low_roi)");

                if (nextSteps.ValueKind == JsonValueKind.Array && hasOptionalNextStep)
                {
                    var filtered = nextSteps.EnumerateArray()
                        .Where(s =>
                            s.ValueKind != JsonValueKind.Object || !ShouldRemoveOptionalBaselineNextStep(s, optionalStepKey, optional.Call))
                        .ToList();

                    nextSteps = JsonSerializer.SerializeToElement(filtered);
                }
            }
        }

        var hypotheses = json.TryGetProperty("hypotheses", out var hypothesesEl) ? hypothesesEl : JsonSerializer.SerializeToElement(Array.Empty<object>());
        var evidence = json.TryGetProperty("evidence", out var evidenceEl) ? evidenceEl : JsonSerializer.SerializeToElement(Array.Empty<object>());
        var doNotRepeat = json.TryGetProperty("doNotRepeat", out var dnrEl) ? dnrEl : JsonSerializer.SerializeToElement(Array.Empty<string>());

        var updated = JsonSerializer.Serialize(new
        {
            facts = facts.Take(50).Select(f => TruncateText(f ?? string.Empty, maxChars: 2048)).Where(f => !string.IsNullOrWhiteSpace(f)).ToList(),
            hypotheses,
            evidence,
            doNotRepeat,
            nextSteps
        }, new JsonSerializerOptions { WriteIndented = false });

        return updated.Length <= MaxCheckpointJsonChars ? updated : checkpointJson;
    }

    private static bool ShouldFinalizeNow(AiEvidenceLedger evidenceLedger, AiHypothesisTracker hypothesisTracker)
    {
        if (evidenceLedger.Items.Count < 6)
        {
            return false;
        }

        var hypotheses = hypothesisTracker.Hypotheses;
        if (hypotheses.Count < 3)
        {
            return false;
        }

        var hasBaselineStackTop = evidenceLedger.Items.Any(e => e.Tags?.Any(t => string.Equals(t, "BASELINE_STACK_TOP", StringComparison.OrdinalIgnoreCase)) == true);
        var hasBaselineExc = evidenceLedger.Items.Any(e =>
            e.Tags?.Any(t =>
                string.Equals(t, "BASELINE_EXC_TYPE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "BASELINE_EXC_MESSAGE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "BASELINE_EXC_HRESULT", StringComparison.OrdinalIgnoreCase)) == true);

        if (!hasBaselineExc || !hasBaselineStackTop)
        {
            return false;
        }

        var hasBaselineMeta = evidenceLedger.Items.Any(e => e.Tags?.Any(t => string.Equals(t, "BASELINE_META", StringComparison.OrdinalIgnoreCase)) == true);
        var hasBaselineEnv = evidenceLedger.Items.Any(e => e.Tags?.Any(t => string.Equals(t, "BASELINE_ENV", StringComparison.OrdinalIgnoreCase)) == true);
        if (!hasBaselineMeta || !hasBaselineEnv)
        {
            return false;
        }

        var mediumOrHigh = hypotheses.Where(h =>
            string.Equals(h.Confidence, "high", StringComparison.OrdinalIgnoreCase)
            || string.Equals(h.Confidence, "medium", StringComparison.OrdinalIgnoreCase)).ToList();

        if (mediumOrHigh.Any(h => string.Equals(h.Confidence, "high", StringComparison.OrdinalIgnoreCase) && h.SupportsEvidenceIds is { Count: > 0 }))
        {
            return true;
        }

        if (mediumOrHigh.Count == 0)
        {
            return false;
        }

        if (mediumOrHigh.Count <= 2
            && mediumOrHigh.Any(h => string.Equals(h.Confidence, "medium", StringComparison.OrdinalIgnoreCase) && h.SupportsEvidenceIds is { Count: > 0 })
            && hypotheses.Any(h => h.ContradictsEvidenceIds is { Count: > 0 }))
        {
            return true;
        }

        return false;
    }

    private static AiSummaryResult ParseSummaryRewriteComplete(
        JsonElement input,
        List<ExecutedCommand> commandsExecuted,
        int iteration,
        string? model)
    {
        var description = TryGetString(input, "description") ?? string.Empty;
        var recommendations = TryGetStringArray(input, "recommendations");

        var result = new AiSummaryResult
        {
            Description = description,
            Recommendations = recommendations,
            Iterations = iteration,
            CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
            Model = model,
            AnalyzedAt = DateTime.UtcNow
        };

        if (string.IsNullOrWhiteSpace(description))
        {
            result.Error = "Summary rewrite completion returned an empty description.";
        }
        else if (recommendations == null || recommendations.Count == 0)
        {
            result.Error = "Summary rewrite completion returned no recommendations.";
        }

        return result;
    }

    private static AiThreadNarrativeResult ParseThreadNarrativeComplete(
        JsonElement input,
        List<ExecutedCommand> commandsExecuted,
        int iteration,
        string? model)
    {
        var description = TryGetString(input, "description") ?? string.Empty;
        var confidence = TryGetString(input, "confidence") ?? "unknown";

        var result = new AiThreadNarrativeResult
        {
            Description = description,
            Confidence = confidence,
            Iterations = iteration,
            CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
            Model = model,
            AnalyzedAt = DateTime.UtcNow
        };

        if (string.IsNullOrWhiteSpace(description))
        {
            result.Error = "Thread narrative completion returned an empty description.";
        }

        return result;
    }

    private static string BuildSummaryRewritePrompt(CrashAnalysisResult report, string fullReportJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Task: Rewrite the report summary fields for humans.");
        sb.AppendLine();
        sb.AppendLine("You must produce an evidence-backed rewrite of:");
        sb.AppendLine("- analysis.summary.description");
        sb.AppendLine("- analysis.summary.recommendations (list of actionable items)");
        sb.AppendLine();
        sb.AppendLine("You are allowed to read the entire report via report_get (paged). Do not assume missing information.");
        sb.AppendLine("Do not recommend disabling monitoring/profilers/tracers (e.g., Datadog).");
        sb.AppendLine();
        sb.AppendLine("Explicitly check for and incorporate (when supported by evidence):");
        sb.AppendLine("- Memory leak indicators / top heap consumers / OOM signals (analysis.memory.*)");
        sb.AppendLine("- Synchronization/lock/contention/deadlock indicators (analysis.synchronization.*, analysis.threads.deadlock, analysis.timeline)");
        sb.AppendLine("- Async/task/faulted task indicators (analysis.async.*)");
        sb.AppendLine("- Security findings if present (analysis.security)");
        sb.AppendLine();
        sb.AppendLine("When done, call analysis_summary_rewrite_complete with:");
        sb.AppendLine("{ description: \"...\", recommendations: [\"...\", ...] }");
        sb.AppendLine("Do not call the completion tool in the same message as other tool calls.");
        sb.AppendLine();
        sb.AppendLine("Report index (summary + TOC):");
        sb.AppendLine(ReportSectionApi.BuildIndex(fullReportJson));
        sb.AppendLine();
        sb.AppendLine("Bounded evidence snapshot:");
        sb.AppendLine(AiSamplingPromptBuilder.Build(report));

        return TruncateInitialPrompt(sb.ToString());
    }

    private static string BuildThreadNarrativePrompt(CrashAnalysisResult report, string fullReportJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Task: Describe what the application/process was doing at the time of the dump.");
        sb.AppendLine();
        sb.AppendLine("You must produce an evidence-backed narrative derived from thread stacks/states.");
        sb.AppendLine("You are allowed to read all threads via report_get (paged) and to use get_thread_stack for full stacks.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Do not claim intent beyond what stacks show; use \"likely\" when uncertain.");
        sb.AppendLine("- Highlight the faulting thread and other dominant thread groups (threadpool, GC/finalizer, locks/waits).");
        sb.AppendLine("- Do not recommend disabling monitoring/profilers/tracers.");
        sb.AppendLine();
        sb.AppendLine("When done, call analysis_thread_narrative_complete with:");
        sb.AppendLine("{ description: \"...\", confidence: \"high|medium|low\" }");
        sb.AppendLine("Do not call the completion tool in the same message as other tool calls.");
        sb.AppendLine();
        sb.AppendLine("Report index (summary + TOC):");
        sb.AppendLine(ReportSectionApi.BuildIndex(fullReportJson));
        sb.AppendLine();
        sb.AppendLine("Bounded evidence snapshot:");
        sb.AppendLine(AiSamplingPromptBuilder.Build(report));

        return TruncateInitialPrompt(sb.ToString());
    }

    private const string SummaryRewriteSystemPrompt = """
You are an expert crash report writer. You are given a structured crash report from a memory dump and access to evidence tools.

Your task is to rewrite analysis.summary.description and analysis.summary.recommendations for humans, based strictly on evidence in the report and tool outputs.

IMPORTANT: Do not present speculation as fact. Every claim must be backed by evidence you retrieved.
IMPORTANT: Prefer report_get over exec whenever possible. Use paging (limit/cursor) and projection (select) to keep responses manageable.
IMPORTANT: Maintain cumulative facts across iterations; do not reset what you know.
IMPORTANT: Treat SOS as already loaded unless the report explicitly says otherwise (metadata.sosLoaded).
IMPORTANT: If metadata.sosLoaded=true, NEVER attempt to load SOS and NEVER claim SOS is not loaded.
IMPORTANT: Do NOT recommend disabling profilers/tracers/monitoring (e.g., Datadog) as a mitigation or “fix”.
IMPORTANT: If something looks like a runtime/ReadyToRun/JIT bug, gather enough evidence for an upstream issue instead of hand-waving.
IMPORTANT: If the report includes source context or Source Link URLs, you may refer to that code as evidence.

Tooling:
- exec: Run a debugger command (LLDB/WinDbg/SOS)
- report_get: Fetch a section of the canonical report JSON by path (dot-path + optional [index]) with paging/projection/filtering
- inspect: Inspect a .NET object by address (when available)
- get_thread_stack: Fetch a full stack for a thread already present in the report
- analysis_summary_rewrite_complete: Call when you have the final rewritten summary fields
""";

    private const string ThreadNarrativeSystemPrompt = """
You are an expert crash dump analyst focused on thread activity. You are given a structured crash report from a memory dump and access to evidence tools.

Your task is to produce an evidence-backed narrative describing what the process was doing at the time of the dump, based on thread stacks/states and related report sections.

IMPORTANT: Do not present speculation as fact. Every hypothesis must be backed by explicit evidence; use \"likely\" when needed.
IMPORTANT: Prefer report_get over exec whenever possible. Use paging (limit/cursor) and projection (select) to keep responses manageable.
IMPORTANT: Maintain cumulative facts across iterations; do not reset what you know.
IMPORTANT: Treat SOS as already loaded unless the report explicitly says otherwise (metadata.sosLoaded).
IMPORTANT: If metadata.sosLoaded=true, NEVER attempt to load SOS and NEVER claim SOS is not loaded.
IMPORTANT: Do NOT recommend disabling profilers/tracers/monitoring (e.g., Datadog) as a mitigation or “fix”.
IMPORTANT: If the report includes source context or Source Link URLs, you may refer to that code as evidence.

Tooling:
- exec: Run a debugger command (LLDB/WinDbg/SOS)
- report_get: Fetch a section of the canonical report JSON by path (dot-path + optional [index]) with paging/projection/filtering
- inspect: Inspect a .NET object by address (when available)
- get_thread_stack: Fetch a full stack for a thread already present in the report
- analysis_thread_narrative_complete: Call when you have the final narrative
""";

    private void LogSamplingRequestSummary(int iteration, CreateMessageRequestParams request)
    {
        var level = SamplingTraceLevel;
        if (!_logger.IsEnabled(level))
        {
            return;
        }

        var messageCount = request.Messages?.Count ?? 0;
        var roles = request.Messages == null
            ? string.Empty
            : string.Join(",", request.Messages.Select(m => m.Role.ToString()));

        var toolNames = request.Tools == null
            ? string.Empty
            : string.Join(",", request.Tools.Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)));

        _logger.Log(
            level,
            "[AI] Sampling request: iteration={Iteration} maxTokens={MaxTokens} messages={MessageCount} roles={Roles} tools={ToolNames} toolChoice={ToolChoiceMode} systemPromptChars={SystemPromptChars}",
            iteration,
            request.MaxTokens,
            messageCount,
            roles,
            toolNames,
            request.ToolChoice?.Mode ?? "(none)",
            request.SystemPrompt?.Length ?? 0);

        if (EnableVerboseSamplingTrace)
        {
            _logger.Log(level, "[AI] Sampling system prompt preview: {Preview}",
                MakePreview(request.SystemPrompt ?? string.Empty, headChars: 800, tailChars: 200));

            _logger.Log(level, "[AI] Sampling messages tail preview:{NewLine}{Preview}",
                Environment.NewLine,
                BuildMessagesTailPreview(request.Messages, tailCount: 4));
        }
    }

    private void LogSamplingResponseSummary(int iteration, CreateMessageResult response, string? assistantText)
    {
        var level = SamplingTraceLevel;
        if (!_logger.IsEnabled(level))
        {
            return;
        }

        var blocks = response.Content?.Count ?? 0;
        var toolUses = response.Content?.OfType<ToolUseContentBlock>().Count() ?? 0;
        var textChars = assistantText?.Length ?? 0;

        _logger.Log(
            level,
            "[AI] Sampling response: iteration={Iteration} model={Model} blocks={Blocks} toolUses={ToolUses} assistantTextChars={TextChars}",
            iteration,
            response.Model ?? "(unknown)",
            blocks,
            toolUses,
            textChars);

        if (EnableVerboseSamplingTrace && !string.IsNullOrWhiteSpace(assistantText))
        {
            _logger.Log(level, "[AI] Sampling assistant text preview: {Preview}",
                MakePreview(assistantText, headChars: 400, tailChars: 0));
        }
    }

    private void LogRequestedToolSummary(int iteration, ToolUseContentBlock toolUse, string effectiveName, JsonElement effectiveInput)
    {
        var level = SamplingTraceLevel;
        if (!_logger.IsEnabled(level))
        {
            return;
        }

        var originalName = toolUse.Name ?? string.Empty;
        var name = effectiveName ?? string.Empty;
        var id = toolUse.Id ?? string.Empty;
        var originalSummary = SummarizeToolInput(originalName, toolUse.Input);
        var summary = SummarizeToolInput(name, effectiveInput);

        if (string.Equals(originalName, name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(originalSummary, summary, StringComparison.Ordinal))
        {
            _logger.Log(
                level,
                "[AI] Tool requested: iteration={Iteration} name={ToolName} id={ToolUseId} input={InputSummary}",
                iteration,
                name,
                id,
                summary);
            return;
        }

        _logger.Log(
            level,
            "[AI] Tool requested: iteration={Iteration} name={ToolName} id={ToolUseId} input={InputSummary} (original: {OriginalName} {OriginalInput})",
            iteration,
            name,
            id,
            summary,
            originalName,
            originalSummary);
    }

    private (string Name, JsonElement Input) RewriteToolUseIfNeeded(ToolUseContentBlock toolUse, IManagedObjectInspector? inspector)
    {
        var name = (toolUse.Name ?? string.Empty).Trim();
        var input = NormalizeToolInput(toolUse.Input);
        if (!string.Equals(name, "exec", StringComparison.OrdinalIgnoreCase))
        {
            return (name, input);
        }

        if (inspector == null || !inspector.IsOpen)
        {
            return (name, input);
        }

        var cmd = TryGetString(input, "command") ?? string.Empty;
        var match = Regex.Match(cmd, @"^\s*sos\s+(dumpobj|dumpvc)\s+(?<addr>0x[0-9a-fA-F]+|[0-9a-fA-F]+)\s*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return (name, input);
        }

        var addr = match.Groups["addr"].Value;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["address"] = addr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? addr : "0x" + addr,
            ["maxDepth"] = 4
        }));

        return ("inspect", doc.RootElement.Clone());
    }

    private string? InitializeSamplingTraceDirectory()
    {
        if (!EnableSamplingTraceFiles)
        {
            return null;
        }

        var root = SamplingTraceFilesRootDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = EnvironmentConfig.GetAiSamplingTraceFilesDirectory();
        }

        var label = string.IsNullOrWhiteSpace(SamplingTraceLabel) ? "run" : SanitizeFileComponent(SamplingTraceLabel);
        var dirName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{label}-{Guid.NewGuid():N}";
        var full = Path.Combine(root, dirName);

        try
        {
            Directory.CreateDirectory(full);
            _logger.LogInformation("[AI] Sampling trace files: {Directory}", full);
            return full;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AI] Failed to create sampling trace directory: {Directory}", full);
            return null;
        }
    }

    private string? InitializeSamplingTraceDirectory(string? kindSuffix)
    {
        if (string.IsNullOrWhiteSpace(kindSuffix))
        {
            return InitializeSamplingTraceDirectory();
        }

        var original = SamplingTraceLabel;
        try
        {
            SamplingTraceLabel = string.IsNullOrWhiteSpace(original)
                ? kindSuffix
                : $"{original}-{kindSuffix}";
            return InitializeSamplingTraceDirectory();
        }
        finally
        {
            SamplingTraceLabel = original;
        }
    }

    private void WriteSamplingTraceFile(string? traceRunDir, string fileName, object payload)
    {
        if (string.IsNullOrWhiteSpace(traceRunDir))
        {
            return;
        }

        try
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var path = Path.Combine(traceRunDir, fileName);
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            var maxBytes = SamplingTraceMaxFileBytes > 0 ? SamplingTraceMaxFileBytes : 2_000_000;
            var bytes = utf8NoBom.GetBytes(json);
            if (bytes.Length > maxBytes)
            {
                var truncated = utf8NoBom.GetString(bytes, 0, maxBytes);
                truncated += $"{Environment.NewLine}... [truncated, totalBytes={bytes.Length}]";
                File.WriteAllText(path, truncated, utf8NoBom);
            }
            else
            {
                File.WriteAllText(path, json, utf8NoBom);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AI] Failed to write sampling trace file: {FileName}", fileName);
        }
    }

    private static string SanitizeFileComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "x";
        }

        var s = value.Trim();
        s = Regex.Replace(s, @"[^a-zA-Z0-9._-]+", "_", RegexOptions.CultureInvariant);
        s = s.Trim('_');
        return string.IsNullOrWhiteSpace(s) ? "x" : s;
    }

    private static object BuildTraceRequest(int iteration, CreateMessageRequestParams request)
        => new
	        {
	            iteration,
	            timestampUtc = DateTime.UtcNow,
	            maxTokens = request.MaxTokens,
	            toolChoice = request.ToolChoice?.Mode,
	            systemPrompt = request.SystemPrompt,
	            tools = request.Tools is { Count: > 0 }
                    ? request.Tools.Select(t => new
                    {
                        name = t.Name,
                        description = t.Description,
                        inputSchema = t.InputSchema.ToString()
                    })
                    : null,
	            messages = request.Messages?.Select(m => new
	            {
	                role = m.Role.ToString(),
	                content = m.Content?.Select<ContentBlock, object>(b => b switch
	                {
	                    TextContentBlock tb => (object)new { type = "text", text = tb.Text },
	                    ToolUseContentBlock tu => (object)new { type = "tool_use", id = tu.Id, name = tu.Name, input = tu.Input.ToString() },
	                    ToolResultContentBlock tr => new
	                    {
	                        type = "tool_result",
	                        tool_use_id = tr.ToolUseId,
	                        isError = tr.IsError,
	                        content = tr.Content?.OfType<TextContentBlock>().Select(x => x.Text).ToList()
	                    },
	                    _ => (object)new { type = b.GetType().Name }
	                })
	            })
	        };

    private static object BuildTraceResponse(int iteration, CreateMessageResult response)
        => new
	        {
	            iteration,
	            timestampUtc = DateTime.UtcNow,
	            model = response.Model,
	            content = response.Content?.Select<ContentBlock, object>(b => b switch
	            {
	                TextContentBlock tb => (object)new { type = "text", text = tb.Text },
	                ToolUseContentBlock tu => (object)new { type = "tool_use", id = tu.Id, name = tu.Name, input = tu.Input.ToString() },
	                ToolResultContentBlock tr => new
	                {
	                    type = "tool_result",
	                    tool_use_id = tr.ToolUseId,
	                    isError = tr.IsError,
	                    content = tr.Content?.OfType<TextContentBlock>().Select(x => x.Text).ToList()
	                },
	                _ => (object)new { type = b.GetType().Name }
	            })
	        };

    private static string SummarizeToolInput(string toolName, JsonElement input)
    {
        try
        {
            if (string.Equals(toolName, "exec", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = TryGetString(input, "command");
                return string.IsNullOrWhiteSpace(cmd) ? "{}" : $"command={CompactOneLine(cmd, 200)}";
            }

            if (string.Equals(toolName, "inspect", StringComparison.OrdinalIgnoreCase))
            {
                var addr = TryGetString(input, "address");
                var depth = TryGetString(input, "maxDepth");
                if (!string.IsNullOrWhiteSpace(addr) && !string.IsNullOrWhiteSpace(depth))
                {
                    return $"address={addr}, maxDepth={depth}";
                }
                return string.IsNullOrWhiteSpace(addr) ? "{}" : $"address={addr}";
            }

            if (string.Equals(toolName, "get_thread_stack", StringComparison.OrdinalIgnoreCase))
            {
                var tid = TryGetString(input, "threadId");
                return string.IsNullOrWhiteSpace(tid) ? "{}" : $"threadId={tid}";
            }

            if (string.Equals(toolName, "analysis_complete", StringComparison.OrdinalIgnoreCase))
            {
                var rc = TryGetString(input, "rootCause");
                return string.IsNullOrWhiteSpace(rc) ? "{}" : $"rootCause={CompactOneLine(rc, 200)}";
            }

            return CompactOneLine(input.ToString(), 200);
        }
        catch
        {
            return "{}";
        }
    }

    private static string CompactOneLine(string value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var s = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        while (s.Contains("  ", StringComparison.Ordinal))
        {
            s = s.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (maxLen > 0 && s.Length > maxLen)
        {
            return s.Substring(0, maxLen) + "...";
        }

        return s;
    }

    private static string MakePreview(string text, int headChars, int tailChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(empty)";
        }

        var clean = text.Replace("\r", "", StringComparison.Ordinal);
        if (headChars < 0) headChars = 0;
        if (tailChars < 0) tailChars = 0;

        if (clean.Length <= headChars + tailChars + 64)
        {
            return $"{clean}\n[chars={clean.Length}]";
        }

        var head = headChars == 0 ? string.Empty : clean.Substring(0, Math.Min(headChars, clean.Length));
        var tail = tailChars == 0 ? string.Empty : clean.Substring(Math.Max(0, clean.Length - tailChars));
        return $"{head}\n... [truncated]\n{tail}\n[chars={clean.Length}]";
    }

    private static string BuildMessagesTailPreview(IList<SamplingMessage>? messages, int tailCount)
    {
        if (messages == null || messages.Count == 0)
        {
            return "(no messages)";
        }

        if (tailCount <= 0)
        {
            tailCount = 1;
        }

        var start = Math.Max(0, messages.Count - tailCount);
        var sb = new StringBuilder();

        for (var i = start; i < messages.Count; i++)
        {
            var m = messages[i];
            sb.Append("  [").Append(i).Append("] role=").Append(m.Role).Append(" ");
            sb.Append(SummarizeMessageContent(m.Content));
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string SummarizeMessageContent(IList<ContentBlock>? blocks)
    {
        if (blocks == null || blocks.Count == 0)
        {
            return "content=(empty)";
        }

        var parts = new List<string>();
        foreach (var b in blocks)
        {
            switch (b)
            {
                case TextContentBlock t:
                    parts.Add($"text[{t.Text?.Length ?? 0}]={CompactOneLine(t.Text ?? string.Empty, 160)}");
                    break;
                case ToolUseContentBlock tu:
                    parts.Add($"tool_use name={tu.Name} id={tu.Id} input={SummarizeToolInput(tu.Name ?? string.Empty, tu.Input)}");
                    break;
                case ToolResultContentBlock tr:
                {
                    var text = tr.Content?.OfType<TextContentBlock>().Select(x => x.Text).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
                    parts.Add($"tool_result id={tr.ToolUseId} isError={tr.IsError} text[{text.Length}]={CompactOneLine(text, 160)}");
                    break;
                }
                default:
                    parts.Add($"block={b.GetType().Name}");
                    break;
            }
        }

        return string.Join(" | ", parts);
    }

    private static string BuildInitialPrompt(CrashAnalysisResult initialReport, string fullReportJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Analyze this crash report.");
        sb.AppendLine("You are provided a small report index (summary + table of contents) and a bounded evidence snapshot.");
        sb.AppendLine("If you need more data, call tools (report_get/exec/inspect/get_thread_stack).");
        sb.AppendLine("Do not call analysis_complete until you've executed at least one evidence tool call.");
        sb.AppendLine("When calling analysis_complete, include an explicit 'evidence' list (each item should cite a tool call or report_get path and the specific finding).");
        sb.AppendLine();
        sb.AppendLine("Mandatory baseline evidence (run in order; do NOT hypothesize before completing):");
        sb.AppendLine("- report_get(path=\"metadata\", pageKind=\"object\", limit=50)");
        sb.AppendLine("- report_get(path=\"analysis.summary\", pageKind=\"object\", select=[\"crashType\",\"description\",\"recommendations\",\"threadCount\",\"moduleCount\",\"assemblyCount\"])");
        sb.AppendLine("- report_get(path=\"analysis.environment\", pageKind=\"object\", select=[\"platform\",\"runtime\",\"process\",\"nativeAot\"])");
        sb.AppendLine("- report_get(path=\"analysis.exception.type\")");
        sb.AppendLine("- report_get(path=\"analysis.exception.message\")");
        sb.AppendLine("- report_get(path=\"analysis.exception.hResult\")");
        sb.AppendLine("- report_get(path=\"analysis.exception.stackTrace\", limit=8, select=[\"frameNumber\",\"instructionPointer\",\"module\",\"function\",\"sourceFile\",\"lineNumber\",\"isManaged\"])");
        sb.AppendLine("- report_get(path=\"analysis.exception.analysis\", pageKind=\"object\", limit=200)");
        sb.AppendLine("If any baseline call returns too_large, retry immediately using suggestedPaths and narrower select/limit before continuing.");
        sb.AppendLine("Avoid rerunning the same tool calls; reuse prior evidence and expand only the specific sections you need.");
        sb.AppendLine();
        sb.AppendLine("After baseline evidence, maintain a stable working set:");
        sb.AppendLine("- Evidence ledger is auto-populated from tool outputs with stable IDs (E#). Do not add new evidence facts manually.");
        sb.AppendLine("- Optionally annotate existing evidence via analysis_evidence_add (whyItMatters/tags/notes); do not restate tool outputs as new findings.");
        sb.AppendLine("- Register 3-4 competing hypotheses via analysis_hypothesis_register (at least 3 for high-confidence conclusions).");
        sb.AppendLine("- Update hypotheses via analysis_hypothesis_score (link existing evidence IDs).");
        sb.AppendLine();
        sb.AppendLine("Report index (summary + TOC):");
        sb.AppendLine(ReportSectionApi.BuildIndex(fullReportJson));
        sb.AppendLine();
        sb.AppendLine("Evidence snapshot (bounded):");
        sb.AppendLine(AiSamplingPromptBuilder.Build(initialReport));

        return TruncateInitialPrompt(sb.ToString());
    }

    private static AiJudgeResult ParseJudgeComplete(JsonElement input, string? model)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("analysis_judge_complete input must be an object.");
        }

        var rejectedHypotheses = new List<AiRejectedHypothesis>();
        if (input.TryGetProperty("rejectedHypotheses", out var rejectedEl) && rejectedEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rejectedEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                rejectedHypotheses.Add(new AiRejectedHypothesis
                {
                    HypothesisId = TryGetString(item, "hypothesisId") ?? string.Empty,
                    ContradictsEvidenceIds = TryGetStringArray(item, "contradictsEvidenceIds") ?? [],
                    Reason = TryGetString(item, "reason") ?? string.Empty
                });
            }
        }

        return new AiJudgeResult
        {
            SelectedHypothesisId = TryGetString(input, "selectedHypothesisId") ?? string.Empty,
            Confidence = TryGetString(input, "confidence") ?? "unknown",
            Rationale = TryGetString(input, "rationale") ?? string.Empty,
            SupportsEvidenceIds = TryGetStringArray(input, "supportsEvidenceIds"),
            RejectedHypotheses = rejectedHypotheses.Count == 0 ? null : rejectedHypotheses,
            Model = model,
            AnalyzedAt = DateTime.UtcNow
        };
    }

    private static AiAnalysisResult ParseAnalysisComplete(
        JsonElement input,
        List<ExecutedCommand> commandsExecuted,
        int iteration,
        string? model)
    {
        var providedEvidence = TryGetStringArray(input, "evidence");
        var evidenceWasAutoGenerated = providedEvidence == null || providedEvidence.Count == 0;

        var result = new AiAnalysisResult
        {
            RootCause = TryGetString(input, "rootCause") ?? string.Empty,
            Confidence = TryGetString(input, "confidence") ?? "unknown",
            Reasoning = TryGetString(input, "reasoning"),
            Evidence = evidenceWasAutoGenerated ? null : providedEvidence,
            Recommendations = TryGetStringArray(input, "recommendations"),
            AdditionalFindings = TryGetStringArray(input, "additionalFindings"),
            Iterations = iteration,
            CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
            Model = model
        };

        if (string.IsNullOrWhiteSpace(result.RootCause))
        {
            result.RootCause = "AI analysis complete, but no rootCause was provided.";
            result.Confidence = "low";
        }

        if (evidenceWasAutoGenerated)
        {
            result.Evidence = BuildAutoEvidenceList(commandsExecuted);

            if (string.Equals(result.Confidence?.Trim(), "high", StringComparison.OrdinalIgnoreCase))
            {
                // If the model didn't provide evidence but requested high confidence, downgrade.
                result.Confidence = "medium";
            }

            var note = "Note: evidence was auto-generated because the model did not provide analysis_complete.evidence.";
            if (string.IsNullOrWhiteSpace(result.Reasoning))
            {
                result.Reasoning = note;
            }
            else if (!result.Reasoning.Contains(note, StringComparison.OrdinalIgnoreCase))
            {
                result.Reasoning = result.Reasoning.TrimEnd() + "\n\n" + note;
            }
        }

        return result;
    }

    private static List<string> BuildAutoEvidenceList(List<ExecutedCommand> commandsExecuted)
    {
        // Keep evidence concise and bounded; this is a fallback when the model doesn't provide citations.
        const int maxItems = 12;
        const int maxOutputSnippetChars = 240;

        var evidence = new List<string>(capacity: Math.Min(maxItems, Math.Max(1, commandsExecuted.Count)));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cmd in commandsExecuted)
        {
            if (evidence.Count >= maxItems)
            {
                break;
            }

            var citation = FormatToolCitation(cmd);
            if (string.IsNullOrWhiteSpace(citation))
            {
                continue;
            }

            if (!seen.Add(citation))
            {
                continue;
            }

            var snippet = SummarizeToolOutputForEvidence(cmd.Output, maxOutputSnippetChars);
            evidence.Add($"{citation} -> {snippet}");
        }

        return evidence;
    }

    private static string? FormatToolCitation(ExecutedCommand cmd)
    {
        var tool = (cmd.Tool ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(tool))
        {
            return null;
        }

        if (tool.Equals("report_get", StringComparison.OrdinalIgnoreCase))
        {
            var path = TryGetString(cmd.Input, "path");
            return string.IsNullOrWhiteSpace(path) ? "report_get" : $"report_get(path=\"{path}\")";
        }

        if (tool.Equals("exec", StringComparison.OrdinalIgnoreCase))
        {
            var command = TryGetString(cmd.Input, "command");
            return string.IsNullOrWhiteSpace(command) ? "exec" : $"exec(command=\"{command}\")";
        }

        if (tool.Equals("inspect", StringComparison.OrdinalIgnoreCase))
        {
            var address = TryGetString(cmd.Input, "address");
            return string.IsNullOrWhiteSpace(address) ? "inspect" : $"inspect(address=\"{address}\")";
        }

        if (tool.Equals("get_thread_stack", StringComparison.OrdinalIgnoreCase))
        {
            var threadId = TryGetString(cmd.Input, "threadId");
            return string.IsNullOrWhiteSpace(threadId) ? "get_thread_stack" : $"get_thread_stack(threadId=\"{threadId}\")";
        }

        return tool;
    }

    private static string SummarizeToolOutputForEvidence(string? output, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "(no output)";
        }

        var s = output.Trim();
        var firstLine = s.Split('\n', 2, StringSplitOptions.None)[0].Trim();
        if (firstLine.Length > maxChars)
        {
            return firstLine.Substring(0, maxChars) + "…";
        }

        return firstLine;
    }

    private async Task<AiAnalysisResult> FinalizeAnalysisAfterSamplingFailureAsync(
        string systemPrompt,
        string passName,
        List<ExecutedCommand> commandsExecuted,
        int iteration,
        string? lastModel,
        string? traceRunDir,
        string? failureMessage,
        CancellationToken cancellationToken)
    {
        const string analysisSchema = """
{
  "rootCause": "string",
  "confidence": "high|medium|low|unknown",
  "reasoning": "string",
  "evidence": ["string"],
  "recommendations": ["string"],
  "additionalFindings": ["string"]
}
""";

        var evidence = BuildCheckpointEvidenceSnapshot(passName, commandsExecuted, commandsExecutedAtLastCheckpoint: 0);

        var finalPrompt = $"""
Sampling failed during pass "{passName}" at iteration {iteration}. Do not request any tools.

Provide the best-effort final conclusion based ONLY on the evidence snapshot below (which contains tool inputs/outputs).
If evidence is insufficient, state what is missing and provide the most likely hypotheses.

Failure: {failureMessage ?? "unknown"}

Evidence snapshot:
{evidence}

Return ONLY valid JSON (no markdown, no code fences) with this schema:
{analysisSchema}
""";

	        var request = new CreateMessageRequestParams
	        {
	            SystemPrompt = systemPrompt,
	            Messages =
            [
                new SamplingMessage
                {
                    Role = Role.User,
                    Content =
                    [
                        new TextContentBlock { Text = finalPrompt }
                    ]
                }
            ],
	            MaxTokens = GetFinalSynthesisMaxTokens(fallbackMaxTokens: MaxTokensPerRequest > 0 ? MaxTokensPerRequest : 1024),
	            Tools = null,
	            ToolChoice = null
	        };

        CreateMessageResult response;
        try
        {
            _logger.LogWarning("[AI] Finalizing analysis after sampling failure at iteration {Iteration}...", iteration);
            WriteSamplingTraceFile(traceRunDir, $"final-sampling-failure-synthesis-request.json", BuildTraceRequest(iteration, request));
            response = await _samplingClient.RequestCompletionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return BuildFallbackSynthesisResult(
                prefix: $"Sampling failed at iteration {iteration}. Final synthesis request failed: {ex.Message}",
                text: string.Empty,
                commandsExecuted: commandsExecuted,
                iteration: iteration,
                model: lastModel);
        }

        WriteSamplingTraceFile(traceRunDir, $"final-sampling-failure-synthesis-response.json", BuildTraceResponse(iteration, response));

        var text = ExtractAssistantText(response) ?? string.Empty;
        if (!TryParseFirstJsonObject(text, out var json))
        {
            return BuildFallbackSynthesisResult(
                prefix: $"Sampling failed at iteration {iteration}. Final synthesis produced unstructured output.",
                text: text,
                commandsExecuted: commandsExecuted,
                iteration: iteration,
                model: response.Model ?? lastModel);
        }

        var parsed = ParseAnalysisComplete(json, commandsExecuted, iteration, response.Model ?? lastModel);
        parsed.AnalyzedAt = DateTime.UtcNow;
        return parsed;
    }

    private async Task<AiAnalysisResult> FinalizeAnalysisAfterToolBudgetExceededAsync(
        string systemPrompt,
        List<SamplingMessage> messages,
        List<ExecutedCommand> commandsExecuted,
        int iteration,
        int maxTokens,
        int maxToolCalls,
        string? lastModel,
        string? traceRunDir,
        CancellationToken cancellationToken)
    {
        if (_toolHistoryModeCache.IsCheckpointOnly && messages.Count > 1 && commandsExecuted.Count > 0)
        {
            var checkpoint = BuildDeterministicCheckpointJson(
                passName: "analysis",
                commandsExecuted: commandsExecuted,
                commandsExecutedAtLastCheckpoint: 0);

            messages = [BuildCheckpointCarryForwardMessage(checkpoint, passName: "analysis")];
        }

        const string analysisSchema = """
{
  "rootCause": "string",
  "confidence": "high|medium|low|unknown",
  "reasoning": "string",
  "evidence": ["string"],
  "recommendations": ["string"],
  "additionalFindings": ["string"]
}
""";

	        var finalPrompt = $"""
	Tool call budget exceeded: you cannot request or use any more tools.

	Based ONLY on the evidence already collected in this conversation (tool outputs already shown), provide your best final conclusion.
	If the conversation contains tool requests without corresponding tool results, those tool requests were NOT executed (due to tool budget) and must be ignored.

	Return ONLY valid JSON (no markdown, no code fences) with this schema:
	{analysisSchema}

	If uncertain, set confidence to "low" and clearly state what additional evidence would help (but do not request tools).
""";

        var finalMessages = new List<SamplingMessage>(messages)
        {
            new()
            {
                Role = Role.User,
                Content =
                [
                    new TextContentBlock { Text = finalPrompt }
                ]
            }
        };

		        var request = new CreateMessageRequestParams
		        {
		            SystemPrompt = systemPrompt,
		            Messages = finalMessages,
		            MaxTokens = GetFinalSynthesisMaxTokens(fallbackMaxTokens: maxTokens),
		            Tools = null,
		            ToolChoice = null
		        };

        CreateMessageResult response;
        try
        {
            _logger.LogInformation("[AI] Finalizing analysis after tool budget exceeded ({MaxToolCalls})...", maxToolCalls);
            WriteSamplingTraceFile(traceRunDir, $"final-synthesis-request.json", BuildTraceRequest(iteration, request));
            response = await _samplingClient.RequestCompletionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return BuildFallbackSynthesisResult(
                prefix: $"Tool call budget exceeded ({maxToolCalls}). Final synthesis request failed: {ex.Message}",
                text: string.Empty,
                commandsExecuted: commandsExecuted,
                iteration: iteration,
                model: lastModel);
        }

        WriteSamplingTraceFile(traceRunDir, $"final-synthesis-response.json", BuildTraceResponse(iteration, response));

        var text = ExtractAssistantText(response);
        if (string.IsNullOrWhiteSpace(text))
        {
            return BuildFallbackSynthesisResult(
                prefix: $"Tool call budget exceeded ({maxToolCalls}). Final synthesis returned empty content.",
                text: string.Empty,
                commandsExecuted: commandsExecuted,
                iteration: iteration,
                model: response.Model ?? lastModel);
        }

        if (!TryParseFirstJsonObject(text, out var json))
        {
            return BuildFallbackSynthesisResult(
                prefix: $"Tool call budget exceeded ({maxToolCalls}). Final synthesis produced unstructured output.",
                text: text,
                commandsExecuted: commandsExecuted,
                iteration: iteration,
                model: response.Model ?? lastModel);
        }

        var parsed = ParseAnalysisComplete(json, commandsExecuted, iteration, response.Model ?? lastModel);
        parsed.AnalyzedAt = DateTime.UtcNow;
        return parsed;
    }

    private async Task<AiAnalysisResult> FinalizeAnalysisAfterNoProgressDetectedAsync(
        string systemPrompt,
        List<SamplingMessage> messages,
        List<ExecutedCommand> commandsExecuted,
        int iteration,
        int maxTokens,
        int consecutiveNoProgressIterations,
        int uniqueToolCalls,
        string? lastModel,
        string? traceRunDir,
        CancellationToken cancellationToken)
    {
        if (_toolHistoryModeCache.IsCheckpointOnly && messages.Count > 1 && commandsExecuted.Count > 0)
        {
            var checkpoint = BuildDeterministicCheckpointJson(
                passName: "analysis",
                commandsExecuted: commandsExecuted,
                commandsExecutedAtLastCheckpoint: 0);

            messages = [BuildCheckpointCarryForwardMessage(checkpoint, passName: "analysis")];
        }

        const string analysisSchema = """
{
  "rootCause": "string",
  "confidence": "high|medium|low|unknown",
  "reasoning": "string",
  "evidence": ["string"],
  "recommendations": ["string"],
  "additionalFindings": ["string"]
}
""";

        var finalPrompt = $"""
The investigation appears to be stuck: there has been no progress for {consecutiveNoProgressIterations} consecutive iteration(s).

You MUST NOT request any tools. Instead, synthesize the best possible conclusion based ONLY on evidence already collected in this conversation
(tool outputs already shown). If evidence is insufficient, set confidence to "low" and list the most important missing evidence items.

Notes:
- Repeating tool calls that already have results is not progress; treat cached/repeated calls as already known.
- If the conversation contains tool requests without corresponding tool results, those tool requests were NOT executed and must be ignored.
- Unique evidence tool calls seen so far: {uniqueToolCalls}

Return ONLY valid JSON (no markdown, no code fences) with this schema:
{analysisSchema}
""";

        var finalMessages = new List<SamplingMessage>(messages)
        {
            new()
            {
                Role = Role.User,
                Content =
                [
                    new TextContentBlock { Text = finalPrompt }
                ]
            }
        };

        var request = new CreateMessageRequestParams
        {
            SystemPrompt = systemPrompt,
            Messages = finalMessages,
            MaxTokens = GetFinalSynthesisMaxTokens(fallbackMaxTokens: maxTokens),
            Tools = null,
            ToolChoice = null
        };

        CreateMessageResult response;
        try
        {
            _logger.LogInformation("[AI] Finalizing analysis early due to no progress...");
            WriteSamplingTraceFile(traceRunDir, $"final-no-progress-synthesis-request.json", BuildTraceRequest(iteration, request));
            response = await _samplingClient.RequestCompletionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return BuildFallbackSynthesisResult(
                prefix: $"No progress detected. Final synthesis request failed: {ex.Message}",
                text: string.Empty,
                commandsExecuted: commandsExecuted,
                iteration: iteration,
                model: lastModel);
        }

        WriteSamplingTraceFile(traceRunDir, $"final-no-progress-synthesis-response.json", BuildTraceResponse(iteration, response));

        var text = ExtractAssistantText(response);
        if (string.IsNullOrWhiteSpace(text))
        {
            return BuildFallbackSynthesisResult(
                prefix: "No progress detected. Final synthesis returned empty content.",
                text: string.Empty,
                commandsExecuted: commandsExecuted,
                iteration: iteration,
                model: response.Model ?? lastModel);
        }

        if (!TryParseFirstJsonObject(text, out var json))
        {
            return BuildFallbackSynthesisResult(
                prefix: "No progress detected. Final synthesis produced unstructured output.",
                text: text,
                commandsExecuted: commandsExecuted,
                iteration: iteration,
                model: response.Model ?? lastModel);
        }

        var parsed = ParseAnalysisComplete(json, commandsExecuted, iteration, response.Model ?? lastModel);
        parsed.AnalyzedAt = DateTime.UtcNow;
        return parsed;
    }

    private async Task<AiAnalysisResult> FinalizeAnalysisAfterMaxIterationsReachedAsync(
        string systemPrompt,
        List<SamplingMessage> messages,
        List<ExecutedCommand> commandsExecuted,
        int iteration,
        int maxTokens,
        int maxIterations,
        string? lastModel,
        string? traceRunDir,
        CancellationToken cancellationToken)
    {
        if (_toolHistoryModeCache.IsCheckpointOnly && messages.Count > 1 && commandsExecuted.Count > 0)
        {
            var checkpoint = BuildDeterministicCheckpointJson(
                passName: "analysis",
                commandsExecuted: commandsExecuted,
                commandsExecutedAtLastCheckpoint: 0);

            messages = [BuildCheckpointCarryForwardMessage(checkpoint, passName: "analysis")];
        }

        const string analysisSchema = """
{
  "rootCause": "string",
  "confidence": "high|medium|low|unknown",
  "reasoning": "string",
  "evidence": ["string"],
  "recommendations": ["string"],
  "additionalFindings": ["string"]
}
""";

	        var finalPrompt = $"""
	Iteration budget reached ({maxIterations}): this is a final synthesis step. Do not request any tools.

	Based ONLY on the evidence already collected in this conversation (tool outputs already shown), provide your best final conclusion.
	If the conversation contains tool requests without corresponding tool results, those tool requests were NOT executed (due to iteration limit) and must be ignored.

	Return ONLY valid JSON (no markdown, no code fences) with this schema:
	{analysisSchema}
	""";

        var finalMessages = new List<SamplingMessage>(messages)
        {
            new()
            {
                Role = Role.User,
                Content =
                [
                    new TextContentBlock { Text = finalPrompt }
                ]
            }
        };

		        var request = new CreateMessageRequestParams
		        {
		            SystemPrompt = systemPrompt,
		            Messages = finalMessages,
		            MaxTokens = GetFinalSynthesisMaxTokens(fallbackMaxTokens: maxTokens),
		            Tools = null,
		            ToolChoice = null
		        };

        CreateMessageResult response;
        try
        {
            _logger.LogInformation("[AI] Finalizing analysis after max iterations ({MaxIterations})...", maxIterations);
            WriteSamplingTraceFile(traceRunDir, $"final-iter-budget-synthesis-request.json", BuildTraceRequest(iteration, request));
            response = await _samplingClient.RequestCompletionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return BuildFallbackSynthesisResult(
                prefix: $"Iteration budget reached ({maxIterations}). Final synthesis request failed: {ex.Message}",
                text: string.Empty,
                commandsExecuted: commandsExecuted,
                iteration: iteration,
                model: lastModel);
        }

        WriteSamplingTraceFile(traceRunDir, $"final-iter-budget-synthesis-response.json", BuildTraceResponse(iteration, response));

        var text = ExtractAssistantText(response) ?? string.Empty;
        if (!TryParseFirstJsonObject(text, out var json))
        {
            return BuildFallbackSynthesisResult(
                prefix: $"Iteration budget reached ({maxIterations}). Final synthesis produced unstructured output.",
                text: text,
                commandsExecuted: commandsExecuted,
                iteration: iteration,
                model: response.Model ?? lastModel);
        }

        var parsed = ParseAnalysisComplete(json, commandsExecuted, iteration, response.Model ?? lastModel);
        parsed.AnalyzedAt = DateTime.UtcNow;
        return parsed;
    }

    private async Task<T?> FinalizePassAfterToolBudgetExceededAsync<T>(
        string passName,
        string systemPrompt,
        List<SamplingMessage> messages,
        List<ExecutedCommand> commandsExecuted,
        int iteration,
        int maxTokens,
        int maxToolCalls,
        string? lastModel,
        string? traceRunDir,
        CancellationToken cancellationToken)
        where T : class
    {
        if (_toolHistoryModeCache.IsCheckpointOnly && messages.Count > 1 && commandsExecuted.Count > 0)
        {
            var checkpoint = BuildDeterministicCheckpointJson(
                passName: passName,
                commandsExecuted: commandsExecuted,
                commandsExecutedAtLastCheckpoint: 0);

            messages = [BuildCheckpointCarryForwardMessage(checkpoint, passName: passName)];
        }

        var expectedSchema = passName switch
        {
            "summary-rewrite" => """
{
  "description": "string",
  "recommendations": ["string"]
}
""",
            "thread-narrative" => """
{
  "description": "string",
  "confidence": "high|medium|low|unknown"
}
""",
            _ => "{}"
        };

	        var finalPrompt = $"""
	Tool call budget exceeded: you cannot request or use any more tools.

	Based ONLY on the evidence already collected in this conversation (tool outputs already shown), provide the best final result for pass '{passName}'.
	If the conversation contains tool requests without corresponding tool results, those tool requests were NOT executed (due to tool budget) and must be ignored.

	Return ONLY valid JSON (no markdown, no code fences) matching:
	{expectedSchema}
	""";

        var finalMessages = new List<SamplingMessage>(messages)
        {
            new()
            {
                Role = Role.User,
                Content =
                [
                    new TextContentBlock { Text = finalPrompt }
                ]
            }
        };

		        var request = new CreateMessageRequestParams
		        {
		            SystemPrompt = systemPrompt,
		            Messages = finalMessages,
		            MaxTokens = GetFinalSynthesisMaxTokens(fallbackMaxTokens: maxTokens),
		            Tools = null,
		            ToolChoice = null
		        };

        CreateMessageResult response;
        try
        {
            _logger.LogInformation("[AI] Finalizing pass {Pass} after tool budget exceeded ({MaxToolCalls})...", passName, maxToolCalls);
            WriteSamplingTraceFile(traceRunDir, $"final-{passName}-synthesis-request.json", BuildTraceRequest(iteration, request));
            response = await _samplingClient.RequestCompletionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return passName switch
            {
                "summary-rewrite" => new AiSummaryResult
                {
                    Error = $"Tool call budget exceeded ({maxToolCalls}). Final synthesis request failed: {ex.Message}",
                    Description = string.Empty,
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                    Model = lastModel,
                    AnalyzedAt = DateTime.UtcNow
                } as T,
                "thread-narrative" => new AiThreadNarrativeResult
                {
                    Error = $"Tool call budget exceeded ({maxToolCalls}). Final synthesis request failed: {ex.Message}",
                    Description = string.Empty,
                    Confidence = "low",
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                    Model = lastModel,
                    AnalyzedAt = DateTime.UtcNow
                } as T,
                _ => null
            };
        }

        WriteSamplingTraceFile(traceRunDir, $"final-{passName}-synthesis-response.json", BuildTraceResponse(iteration, response));

        var text = ExtractAssistantText(response);
        if (string.IsNullOrWhiteSpace(text) || !TryParseFirstJsonObject(text, out var json))
        {
            return passName switch
            {
                "summary-rewrite" => new AiSummaryResult
                {
                    Description = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim(),
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                    Model = response.Model ?? lastModel,
                    AnalyzedAt = DateTime.UtcNow
                } as T,
                "thread-narrative" => new AiThreadNarrativeResult
                {
                    Description = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim(),
                    Confidence = "low",
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                    Model = response.Model ?? lastModel,
                    AnalyzedAt = DateTime.UtcNow
                } as T,
                _ => null
            };
        }

        var parsed = ParseCompletionTool<T>(passName, completionToolName: string.Empty, input: json, commandsExecuted, iteration, response.Model ?? lastModel);
        return parsed;
    }

    private async Task<T?> FinalizePassAfterTotalToolUseBudgetExceededAsync<T>(
        string passName,
        string systemPrompt,
        List<SamplingMessage> messages,
        List<ExecutedCommand> commandsExecuted,
        int iteration,
        int maxTokens,
        int maxTotalToolUses,
        string? lastModel,
        string? traceRunDir,
        CancellationToken cancellationToken)
        where T : class
    {
        if (_toolHistoryModeCache.IsCheckpointOnly && messages.Count > 1 && commandsExecuted.Count > 0)
        {
            var checkpoint = BuildDeterministicCheckpointJson(
                passName: passName,
                commandsExecuted: commandsExecuted,
                commandsExecutedAtLastCheckpoint: 0);

            messages = [BuildCheckpointCarryForwardMessage(checkpoint, passName: passName)];
        }

        var expectedSchema = passName switch
        {
            "summary-rewrite" => """
{
  "description": "string",
  "recommendations": ["string"]
}
""",
            "thread-narrative" => """
{
  "description": "string",
  "confidence": "high|medium|low|unknown"
}
""",
            _ => "{}"
        };

        var finalPrompt = $"""
Tool-use budget exceeded (totalToolUses={commandsExecuted.Count}, limit={maxTotalToolUses}): do not request any tools.

Based ONLY on the evidence already collected in this conversation (tool outputs already shown), provide the best final result for pass '{passName}'.
If the conversation contains tool requests without corresponding tool results, those tool requests were NOT executed (due to tool-use budget) and must be ignored.

Return ONLY valid JSON (no markdown, no code fences) matching:
{expectedSchema}
""";

        var finalMessages = new List<SamplingMessage>(messages)
        {
            new()
            {
                Role = Role.User,
                Content =
                [
                    new TextContentBlock { Text = finalPrompt }
                ]
            }
        };

        var request = new CreateMessageRequestParams
        {
            SystemPrompt = systemPrompt,
            Messages = finalMessages,
            MaxTokens = GetFinalSynthesisMaxTokens(fallbackMaxTokens: maxTokens),
            Tools = null,
            ToolChoice = null
        };

        CreateMessageResult response;
        try
        {
            _logger.LogInformation(
                "[AI] Finalizing pass {Pass} after tool-use budget exceeded (limit={Limit})...",
                passName,
                maxTotalToolUses);
            WriteSamplingTraceFile(traceRunDir, $"final-{passName}-tooluse-budget-synthesis-request.json", BuildTraceRequest(iteration, request));
            response = await _samplingClient.RequestCompletionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return passName switch
            {
                "summary-rewrite" => new AiSummaryResult
                {
                    Error = $"Tool-use budget exceeded ({maxTotalToolUses}). Final synthesis request failed: {ex.Message}",
                    Description = string.Empty,
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                    Model = lastModel,
                    AnalyzedAt = DateTime.UtcNow
                } as T,
                "thread-narrative" => new AiThreadNarrativeResult
                {
                    Error = $"Tool-use budget exceeded ({maxTotalToolUses}). Final synthesis request failed: {ex.Message}",
                    Description = string.Empty,
                    Confidence = "low",
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                    Model = lastModel,
                    AnalyzedAt = DateTime.UtcNow
                } as T,
                _ => null
            };
        }

        WriteSamplingTraceFile(traceRunDir, $"final-{passName}-tooluse-budget-synthesis-response.json", BuildTraceResponse(iteration, response));

        var text = ExtractAssistantText(response);
        if (string.IsNullOrWhiteSpace(text) || !TryParseFirstJsonObject(text, out var json))
        {
            return passName switch
            {
                "summary-rewrite" => new AiSummaryResult
                {
                    Description = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim(),
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                    Model = response.Model ?? lastModel,
                    AnalyzedAt = DateTime.UtcNow
                } as T,
                "thread-narrative" => new AiThreadNarrativeResult
                {
                    Description = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim(),
                    Confidence = "low",
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                    Model = response.Model ?? lastModel,
                    AnalyzedAt = DateTime.UtcNow
                } as T,
                _ => null
            };
        }

        return ParseCompletionTool<T>(passName, completionToolName: string.Empty, input: json, commandsExecuted, iteration, response.Model ?? lastModel);
    }

    private async Task<T?> FinalizePassAfterNoProgressDetectedAsync<T>(
        string passName,
        string systemPrompt,
        List<SamplingMessage> messages,
        List<ExecutedCommand> commandsExecuted,
        int iteration,
        int maxTokens,
        int consecutiveNoProgressIterations,
        int uniqueToolCalls,
        string? lastModel,
        string? traceRunDir,
        CancellationToken cancellationToken)
        where T : class
    {
        if (_toolHistoryModeCache.IsCheckpointOnly && messages.Count > 1 && commandsExecuted.Count > 0)
        {
            var checkpoint = BuildDeterministicCheckpointJson(
                passName: passName,
                commandsExecuted: commandsExecuted,
                commandsExecutedAtLastCheckpoint: 0);

            messages = [BuildCheckpointCarryForwardMessage(checkpoint, passName: passName)];
        }

        var expectedSchema = passName switch
        {
            "summary-rewrite" => """
{
  "description": "string",
  "recommendations": ["string"]
}
""",
            "thread-narrative" => """
{
  "description": "string",
  "confidence": "high|medium|low|unknown"
}
""",
            _ => "{}"
        };

        var finalPrompt = $"""
No progress detected for {consecutiveNoProgressIterations} consecutive iterations (uniqueToolCalls={uniqueToolCalls}): this is a final synthesis step. Do not request any tools.

Based ONLY on the evidence already collected in this conversation (tool outputs already shown), provide the best final result for pass '{passName}'.
If the conversation contains tool requests without corresponding tool results, those tool requests were NOT executed (due to no-progress early stop) and must be ignored.

Return ONLY valid JSON (no markdown, no code fences) matching:
{expectedSchema}
""";

        var finalMessages = new List<SamplingMessage>(messages)
        {
            new()
            {
                Role = Role.User,
                Content =
                [
                    new TextContentBlock { Text = finalPrompt }
                ]
            }
        };

        var request = new CreateMessageRequestParams
        {
            SystemPrompt = systemPrompt,
            Messages = finalMessages,
            MaxTokens = GetFinalSynthesisMaxTokens(fallbackMaxTokens: maxTokens),
            Tools = null,
            ToolChoice = null
        };

        CreateMessageResult response;
        try
        {
            _logger.LogInformation("[AI] Finalizing pass {Pass} after no progress detected...", passName);
            WriteSamplingTraceFile(traceRunDir, $"final-{passName}-no-progress-synthesis-request.json", BuildTraceRequest(iteration, request));
            response = await _samplingClient.RequestCompletionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return passName switch
            {
                "summary-rewrite" => new AiSummaryResult
                {
                    Error = $"No progress detected; final synthesis request failed: {ex.Message}",
                    Description = string.Empty,
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                    Model = lastModel,
                    AnalyzedAt = DateTime.UtcNow
                } as T,
                "thread-narrative" => new AiThreadNarrativeResult
                {
                    Error = $"No progress detected; final synthesis request failed: {ex.Message}",
                    Description = string.Empty,
                    Confidence = "low",
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                    Model = lastModel,
                    AnalyzedAt = DateTime.UtcNow
                } as T,
                _ => null
            };
        }

        WriteSamplingTraceFile(traceRunDir, $"final-{passName}-no-progress-synthesis-response.json", BuildTraceResponse(iteration, response));

        var text = ExtractAssistantText(response) ?? string.Empty;
        if (!TryParseFirstJsonObject(text, out var json))
        {
            return passName switch
            {
                "summary-rewrite" => new AiSummaryResult
                {
                    Error = "No progress detected. Final synthesis produced unstructured output.",
                    Description = text.Trim(),
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                    Model = response.Model ?? lastModel,
                    AnalyzedAt = DateTime.UtcNow
                } as T,
                "thread-narrative" => new AiThreadNarrativeResult
                {
                    Error = "No progress detected. Final synthesis produced unstructured output.",
                    Description = text.Trim(),
                    Confidence = "low",
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                    Model = response.Model ?? lastModel,
                    AnalyzedAt = DateTime.UtcNow
                } as T,
                _ => null
            };
        }

        return ParseCompletionTool<T>(passName, completionToolName: string.Empty, input: json, commandsExecuted, iteration, response.Model ?? lastModel);
    }

    private async Task<T?> FinalizePassAfterMaxIterationsReachedAsync<T>(
        string passName,
        string systemPrompt,
        List<SamplingMessage> messages,
        List<ExecutedCommand> commandsExecuted,
        int iteration,
        int maxTokens,
        int maxIterations,
        string? lastModel,
        string? traceRunDir,
        CancellationToken cancellationToken)
        where T : class
    {
        if (_toolHistoryModeCache.IsCheckpointOnly && messages.Count > 1 && commandsExecuted.Count > 0)
        {
            var checkpoint = BuildDeterministicCheckpointJson(
                passName: passName,
                commandsExecuted: commandsExecuted,
                commandsExecutedAtLastCheckpoint: 0);

            messages = [BuildCheckpointCarryForwardMessage(checkpoint, passName: passName)];
        }

        var expectedSchema = passName switch
        {
            "summary-rewrite" => """
{
  "description": "string",
  "recommendations": ["string"]
}
""",
            "thread-narrative" => """
{
  "description": "string",
  "confidence": "high|medium|low|unknown"
}
""",
            _ => "{}"
        };

	        var finalPrompt = $"""
	Iteration budget reached ({maxIterations}): this is a final synthesis step. Do not request any tools.

	Based ONLY on the evidence already collected in this conversation (tool outputs already shown), provide the best final result for pass '{passName}'.
	If the conversation contains tool requests without corresponding tool results, those tool requests were NOT executed (due to iteration limit) and must be ignored.

	Return ONLY valid JSON (no markdown, no code fences) matching:
	{expectedSchema}
	""";

        var finalMessages = new List<SamplingMessage>(messages)
        {
            new()
            {
                Role = Role.User,
                Content =
                [
                    new TextContentBlock { Text = finalPrompt }
                ]
            }
        };

		        var request = new CreateMessageRequestParams
		        {
		            SystemPrompt = systemPrompt,
		            Messages = finalMessages,
		            MaxTokens = GetFinalSynthesisMaxTokens(fallbackMaxTokens: maxTokens),
		            Tools = null,
		            ToolChoice = null
		        };

        CreateMessageResult response;
        try
        {
            _logger.LogInformation("[AI] Finalizing pass {Pass} after max iterations ({MaxIterations})...", passName, maxIterations);
            WriteSamplingTraceFile(traceRunDir, $"final-{passName}-iter-budget-synthesis-request.json", BuildTraceRequest(iteration, request));
            response = await _samplingClient.RequestCompletionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return passName switch
            {
                "summary-rewrite" => new AiSummaryResult
                {
                    Error = $"Iteration budget reached ({maxIterations}). Final synthesis request failed: {ex.Message}",
                    Description = string.Empty,
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                    Model = lastModel,
                    AnalyzedAt = DateTime.UtcNow
                } as T,
                "thread-narrative" => new AiThreadNarrativeResult
                {
                    Error = $"Iteration budget reached ({maxIterations}). Final synthesis request failed: {ex.Message}",
                    Description = string.Empty,
                    Confidence = "low",
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                    Model = lastModel,
                    AnalyzedAt = DateTime.UtcNow
                } as T,
                _ => null
            };
        }

        WriteSamplingTraceFile(traceRunDir, $"final-{passName}-iter-budget-synthesis-response.json", BuildTraceResponse(iteration, response));

        var text = ExtractAssistantText(response) ?? string.Empty;
        if (!TryParseFirstJsonObject(text, out var json))
        {
            return passName switch
            {
                "summary-rewrite" => new AiSummaryResult
                {
                    Error = $"Iteration budget reached ({maxIterations}). Final synthesis produced unstructured output.",
                    Description = text.Trim(),
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                    Model = response.Model ?? lastModel,
                    AnalyzedAt = DateTime.UtcNow
                } as T,
                "thread-narrative" => new AiThreadNarrativeResult
                {
                    Error = $"Iteration budget reached ({maxIterations}). Final synthesis produced unstructured output.",
                    Description = text.Trim(),
                    Confidence = "low",
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                    Model = response.Model ?? lastModel,
                    AnalyzedAt = DateTime.UtcNow
                } as T,
                _ => null
            };
        }

        return ParseCompletionTool<T>(passName, completionToolName: string.Empty, input: json, commandsExecuted, iteration, response.Model ?? lastModel);
    }

    private int GetFinalSynthesisMaxTokens(int fallbackMaxTokens)
    {
        var preferred = FinalSynthesisMaxTokens > 0 ? FinalSynthesisMaxTokens : fallbackMaxTokens;
        return Math.Max(256, preferred);
    }

    private static void AttachInvestigationState(AiAnalysisResult result, AiEvidenceLedger evidenceLedger, AiHypothesisTracker hypothesisTracker)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(evidenceLedger);
        ArgumentNullException.ThrowIfNull(hypothesisTracker);

        result.EvidenceLedger = evidenceLedger.Items
            .Select(i => new AiEvidenceLedgerItem
            {
                Id = i.Id,
                Source = i.Source,
                Finding = i.Finding,
                WhyItMatters = i.WhyItMatters,
                Tags = i.Tags == null ? null : new List<string>(i.Tags),
                ToolName = i.ToolName,
                ToolKeyHash = i.ToolKeyHash,
                ToolOutputHash = i.ToolOutputHash,
                ToolWasCached = i.ToolWasCached,
                ToolWasError = i.ToolWasError,
                Notes = i.Notes == null ? null : new List<string>(i.Notes)
            })
            .ToList();

        result.Hypotheses = hypothesisTracker.Hypotheses
            .Select(h => new AiHypothesis
            {
                Id = h.Id,
                Hypothesis = h.Hypothesis,
                Confidence = h.Confidence,
                SupportsEvidenceIds = h.SupportsEvidenceIds == null ? null : new List<string>(h.SupportsEvidenceIds),
                ContradictsEvidenceIds = h.ContradictsEvidenceIds == null ? null : new List<string>(h.ContradictsEvidenceIds),
                Unknowns = h.Unknowns == null ? null : new List<string>(h.Unknowns),
                Notes = h.Notes,
                TestsToRun = h.TestsToRun == null ? null : new List<string>(h.TestsToRun)
            })
            .ToList();
    }

    private static AiAnalysisResult BuildFallbackSynthesisResult(
        string prefix,
        string text,
        List<ExecutedCommand> commandsExecuted,
        int iteration,
        string? model)
    {
        var trimmed = (text ?? string.Empty).Trim();
        var rootCause = prefix;

        var rootCauseMatch = Regex.Match(trimmed, @"(?i)\broot\s*cause\s*[:\-]\s*(.+)$", RegexOptions.Multiline);
        if (rootCauseMatch.Success)
        {
            var candidate = rootCauseMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                rootCause = candidate;
            }
        }

        var confidence = "low";
        var confidenceMatch = Regex.Match(trimmed, @"(?i)\bconfidence\s*[:\-]\s*(high|medium|low|unknown)\b");
        if (confidenceMatch.Success)
        {
            confidence = confidenceMatch.Groups[1].Value.ToLowerInvariant();
        }

        var reasoning = string.IsNullOrWhiteSpace(trimmed)
            ? prefix
            : $"{prefix}{Environment.NewLine}{Environment.NewLine}{trimmed}";

        return new AiAnalysisResult
        {
            RootCause = rootCause,
            Confidence = confidence,
            Reasoning = reasoning,
            Iterations = iteration,
            CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
            Model = model,
            AnalyzedAt = DateTime.UtcNow
        };
    }

    private static string? ExtractAssistantText(CreateMessageResult response)
    {
        if (response.Content == null || response.Content.Count == 0)
        {
            return null;
        }

        var texts = response.Content.OfType<TextContentBlock>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        return texts.Count == 0 ? null : string.Join("\n", texts).Trim();
    }

    private static bool TryParseFirstJsonObject(string text, out JsonElement json)
    {
        json = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (TryParseJsonObject(trimmed, out json))
        {
            return true;
        }

        // Handle fenced code blocks.
        trimmed = TrimCodeFences(trimmed);
        if (TryParseJsonObject(trimmed, out json))
        {
            return true;
        }

        // Find the first JSON object in the text.
        if (!TryExtractFirstJsonObjectSubstring(trimmed, out var candidate))
        {
            return false;
        }

        return TryParseJsonObject(candidate, out json);
    }

    private static bool TryParseJsonObject(string candidate, out JsonElement json)
    {
        json = default;
        try
        {
            using var doc = JsonDocument.Parse(candidate);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            json = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string TrimCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
        {
            return trimmed;
        }

        var withoutFirstLine = trimmed.Substring(firstNewline + 1);
        var endFence = withoutFirstLine.LastIndexOf("```", StringComparison.Ordinal);
        if (endFence < 0)
        {
            return withoutFirstLine.Trim();
        }

        return withoutFirstLine.Substring(0, endFence).Trim();
    }

    private static bool TryExtractFirstJsonObjectSubstring(string text, out string json)
    {
        json = string.Empty;
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return false;
        }

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (c == '\\')
                {
                    escape = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '{')
            {
                depth++;
                continue;
            }

            if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    json = text.Substring(start, i - start + 1);
                    return true;
                }
            }
        }

        return false;
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!element.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
    }

    private static List<string>? TryGetStringArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!element.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    result.Add(s!);
                }
            }
            else if (item.ValueKind != JsonValueKind.Null && item.ValueKind != JsonValueKind.Undefined)
            {
                result.Add(item.ToString());
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static async Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement toolInput,
        string fullReportJson,
        CrashAnalysisResult initialReport,
        IDebuggerManager debugger,
        IManagedObjectInspector? clrMdAnalyzer,
        CancellationToken cancellationToken)
    {
        var normalized = (toolName ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "exec" => await ExecuteExecAsync(toolInput, debugger, cancellationToken).ConfigureAwait(false),
            "inspect" => ExecuteInspect(toolInput, clrMdAnalyzer),
            "report_get" => ExecuteReportGet(toolInput, fullReportJson),
            "get_thread_stack" => ExecuteGetThreadStack(toolInput, initialReport),
            _ => throw new InvalidOperationException($"Unknown tool: {toolName}")
        };
    }

    private static string ExecuteReportGet(JsonElement toolInput, string fullReportJson)
    {
        if (toolInput.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("report_get input must be an object.");
        }

        var path = TryGetString(toolInput, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("report_get.path is required.");
        }

        int? limit = null;
        if (toolInput.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind == JsonValueKind.Number && limitEl.TryGetInt32(out var l))
        {
            limit = l;
        }

        var cursor = TryGetString(toolInput, "cursor");

        int? maxChars = null;
        if (toolInput.TryGetProperty("maxChars", out var maxCharsEl) && maxCharsEl.ValueKind == JsonValueKind.Number && maxCharsEl.TryGetInt32(out var mc))
        {
            maxChars = mc;
        }

        var pageKind = TryGetString(toolInput, "pageKind");

        IReadOnlyList<string>? select = null;
        if (toolInput.TryGetProperty("select", out var selectEl) && selectEl.ValueKind == JsonValueKind.Array)
        {
            var fields = new List<string>();
            foreach (var item in selectEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        fields.Add(s!);
                    }
                }
            }
            select = fields.Count > 0 ? fields : null;
        }

        ReportSectionApi.ReportWhere? where = null;
        if (toolInput.TryGetProperty("where", out var whereEl) && whereEl.ValueKind == JsonValueKind.Object)
        {
            var field = TryGetString(whereEl, "field");
            var equals = TryGetString(whereEl, "equals");
            var caseInsensitive = true;
            if (whereEl.TryGetProperty("caseInsensitive", out var ciEl) &&
                (ciEl.ValueKind == JsonValueKind.True || ciEl.ValueKind == JsonValueKind.False))
            {
                caseInsensitive = ciEl.GetBoolean();
            }

            if (!string.IsNullOrWhiteSpace(field) && !string.IsNullOrWhiteSpace(equals))
            {
                where = new ReportSectionApi.ReportWhere(field!, equals!, caseInsensitive);
            }
        }

        return ReportSectionApi.GetSection(fullReportJson, path!, limit, cursor, maxChars, pageKind, select, where);
    }

    private static Task<string> ExecuteExecAsync(JsonElement toolInput, IDebuggerManager debugger, CancellationToken cancellationToken)
    {
        var command = TryGetString(toolInput, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("exec.command is required.");
        }

        command = NormalizeExecCommand(command, debugger);
        EnsureSafeDebuggerCommand(command);
        cancellationToken.ThrowIfCancellationRequested();
        var output = debugger.ExecuteCommand(command);
        return Task.FromResult(output ?? string.Empty);
    }

    private static string NormalizeExecCommand(string command, IDebuggerManager debugger)
    {
        // Be permissive: models sometimes mix WinDbg-style ("!name2ee") and LLDB SOS style ("sos name2ee").
        // For LLDB, "sos !name2ee" is invalid (SOS doesn't include the bang), but it's an easy fix.
        if (!string.Equals(debugger.DebuggerType, "LLDB", StringComparison.OrdinalIgnoreCase))
        {
            return command;
        }

        var trimmed = command.Trim();

        if (trimmed.Length >= 3 &&
            trimmed.StartsWith("sos", StringComparison.OrdinalIgnoreCase) &&
            (trimmed.Length == 3 || char.IsWhiteSpace(trimmed[3])))
        {
            var afterSos = trimmed.Length == 3 ? string.Empty : trimmed[3..];
            var rest = afterSos.TrimStart();
            if (rest.Length > 0 && rest[0] == '!')
            {
                var afterBang = rest[1..].TrimStart();
                return string.IsNullOrWhiteSpace(afterBang) ? "sos" : $"sos {afterBang}";
            }
        }

        return command;
    }

    private static string ExecuteInspect(JsonElement toolInput, IManagedObjectInspector? clrMdAnalyzer)
    {
        var address = TryGetString(toolInput, "address");
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("inspect.address is required.");
        }

        var maxDepth = 3;
        if (toolInput.ValueKind == JsonValueKind.Object &&
            toolInput.TryGetProperty("maxDepth", out var md) &&
            md.ValueKind == JsonValueKind.Number &&
            md.TryGetInt32(out var n) &&
            n > 0)
        {
            maxDepth = Math.Min(5, n);
        }

        if (clrMdAnalyzer == null || !clrMdAnalyzer.IsOpen)
        {
            var hint = new
            {
                error = "ClrMD analyzer not available; cannot inspect managed objects. Use exec with SOS (!dumpobj / !dumpvc) for managed inspection.",
                address,
                maxDepth
            };

            return JsonSerializer.Serialize(hint);
        }

        var addressValue = ParseHexAddress(address);
        if (addressValue == null)
        {
            return JsonSerializer.Serialize(new { error = $"Invalid address format: {address}" });
        }

        var inspected = clrMdAnalyzer.InspectObject(
            addressValue.Value,
            methodTable: null,
            maxDepth: maxDepth,
            maxArrayElements: 10,
            maxStringLength: 1024);

        if (inspected == null)
        {
            return JsonSerializer.Serialize(new { error = "Failed to inspect object.", address });
        }

        return JsonSerializer.Serialize(inspected, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    private static string ExecuteGetThreadStack(JsonElement toolInput, CrashAnalysisResult initialReport)
    {
        var id = TryGetString(toolInput, "threadId");
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("get_thread_stack.threadId is required.");
        }

        var thread = FindThread(initialReport, id);
        if (thread == null)
        {
            return JsonSerializer.Serialize(new
            {
                error = "Thread not found in report.",
                threadId = id
            });
        }

        return JsonSerializer.Serialize(thread, new JsonSerializerOptions { WriteIndented = true });
    }

    private static ThreadInfo? FindThread(CrashAnalysisResult report, string threadIdOrOsId)
    {
        if (report.Threads == null)
        {
            return null;
        }

        var all = new List<ThreadInfo>();
        if (report.Threads.FaultingThread != null)
        {
            all.Add(report.Threads.FaultingThread);
        }
        if (report.Threads.All != null)
        {
            all.AddRange(report.Threads.All);
        }

        if (all.Count == 0)
        {
            return null;
        }

        var needle = threadIdOrOsId.Trim();

        int? needleInt = null;
        if (int.TryParse(needle, out var parsed))
        {
            needleInt = parsed;
        }

        // Prefer matching the debugger thread ID first (exact match).
        var match = all.FirstOrDefault(t => string.Equals(t.ThreadId, needle, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            return match;
        }

        // Next, support "thread index" inputs where ThreadId is formatted like: "16 (tid: 35176)".
        if (needleInt.HasValue)
        {
            match = all.FirstOrDefault(t => TryParseLeadingInt(t.ThreadId) == needleInt.Value);
            if (match != null)
            {
                return match;
            }
        }

        // OS thread ID (hex) match (accepts 0x-prefixed and non-prefixed hex).
        match = all.FirstOrDefault(t =>
            !string.IsNullOrWhiteSpace(t.OsThreadId) &&
            string.Equals(NormalizeHex(t.OsThreadId), NormalizeHex(needle), StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            return match;
        }

        // OS thread ID (decimal) match.
        match = all.FirstOrDefault(t =>
            !string.IsNullOrWhiteSpace(t.OsThreadIdDecimal) &&
            string.Equals(t.OsThreadIdDecimal, needle, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            return match;
        }

        // Finally, allow matching by managed thread ID when the input is numeric.
        if (needleInt.HasValue)
        {
            match = all.FirstOrDefault(t => t.ManagedThreadId == needleInt.Value);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static int? TryParseLeadingInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var span = value.AsSpan().TrimStart();
        var i = 0;
        while (i < span.Length && char.IsDigit(span[i]))
        {
            i++;
        }

        if (i == 0)
        {
            return null;
        }

        return int.TryParse(span[..i], out var parsed) ? parsed : null;
    }

    private static string NormalizeHex(string value)
    {
        var v = value.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            v = v[2..];
        }

        v = v.TrimStart('0');
        return string.IsNullOrEmpty(v) ? "0" : v.ToLowerInvariant();
    }

    private static ulong? ParseHexAddress(string value)
    {
        var clean = value.Trim();
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[2..];
        }

        if (ulong.TryParse(clean, System.Globalization.NumberStyles.HexNumber, null, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static JsonElement CloneToolInput(JsonElement input)
    {
        if (input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }

        try
        {
            return input.Clone();
        }
        catch
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

    private static JsonElement NormalizeToolInput(JsonElement input)
    {
        if (input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }

        if (input.ValueKind == JsonValueKind.Object)
        {
            if (input.TryGetProperty("__raw", out var rawEl) && rawEl.ValueKind == JsonValueKind.String)
            {
                if (TryParseRawJsonObject(rawEl.GetString(), out var parsed))
                {
                    return parsed;
                }
            }

            return input;
        }

        if (input.ValueKind == JsonValueKind.String)
        {
            if (TryParseRawJsonObject(input.GetString(), out var parsed))
            {
                return parsed;
            }

            return input;
        }

        return input;
    }

    private static bool TryParseRawJsonObject(string? raw, out JsonElement parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 50_000)
        {
            return false;
        }

        return TryParseFirstJsonObject(trimmed, out parsed);
    }

    private static string TruncateInitialPrompt(string text)
        => TruncateText(text, maxChars: 200_000);

    private static string TruncateForModel(string text)
        => TruncateText(text, maxChars: 50_000);

    private static string TruncateText(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0 || text.Length <= maxChars)
        {
            return text ?? string.Empty;
        }

        if (maxChars < 128)
        {
            return text.Substring(0, maxChars);
        }

        var marker = $"\n... [truncated, total {text.Length} chars]\n";
        var remaining = maxChars - marker.Length;
        if (remaining <= 0)
        {
            return text.Substring(0, maxChars);
        }

        var head = remaining / 2;
        var tail = remaining - head;

        return text.Substring(0, head) + marker + text.Substring(text.Length - tail);
    }

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static void EnsureSafeDebuggerCommand(string command)
    {
        // AI-driven exec must not be able to run host OS commands.
        // These commands exist in common debuggers and can be abused if an LLM is compromised.
        var trimmed = command.TrimStart();

        if (trimmed.Contains('\n') || trimmed.Contains('\r'))
        {
            throw new InvalidOperationException("Multi-line debugger commands are not allowed in AI analysis.");
        }

        // A single line can contain multiple debugger statements separated by ';' or '|', so detect unsafe
        // commands even when they are not the first token.
        if (UnsafeExecCommandRegex.IsMatch(trimmed))
        {
            throw new InvalidOperationException("Blocked unsafe debugger command.");
        }
    }

    private static readonly Regex UnsafeExecCommandRegex = new(
        // Start-of-command or after common separators (best-effort across WinDbg/LLDB).
        @"(^|[;&|]\s*)\.shell\b" +
        @"|(^|[;&|]\s*)platform\s+shell\b" +
        @"|(^|[;&|]\s*)(command\s+script|script)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string JudgeSystemPrompt = """
You are an internal evaluator that selects the best-supported hypothesis based ONLY on the provided evidence/hypothesis ledger.

Rules:
- You MUST call the tool "analysis_judge_complete" exactly once.
- Do NOT call any other tools.
- Do NOT output any text outside the tool call.
- Tool input MUST be a valid JSON object (not a JSON string).
""";

    private const string SystemPrompt = """
You are an expert crash dump analyst. You've been given an initial crash analysis report from a memory dump.

Your task is to determine the ROOT CAUSE of the crash through systematic investigation.

IMPORTANT: Your primary objective is to determine the ROOT CAUSE of the crash through systematic investigation.
IMPORTANT: Always keep the user's stated goal and the primary objective of the analysis in mind. Do not drift into unrelated investigations.
IMPORTANT: Before using exec, determine the active debugger type from the initial report metadata (metadata.debuggerType, e.g. "LLDB" or "WinDbg") and only issue commands that exist in that debugger. Never run WinDbg-only commands in an LLDB session (or vice versa).
IMPORTANT: Exception: WinDbg-style SOS commands prefixed with '!' (e.g., !pe, !clrstack, !dumpheap) are acceptable in LLDB sessions because the server strips the leading '!'.
IMPORTANT: Do not repeat identical tool calls with the same arguments; reuse prior tool outputs as evidence and move the investigation forward.
IMPORTANT: Tool inputs MUST be valid JSON objects (double-quoted keys, no trailing commas). Do NOT pass JSON as a quoted string.
IMPORTANT: report_get requires a top-level "path" string. Never call report_get without path.
IMPORTANT: Do not assume assembly versions from file paths. Treat paths as hints and verify versions using assembly metadata from the report (prefer report_get for analysis.assemblies.items and analysis.modules where available).
IMPORTANT: When using SOS commands like !dumpmt/!dumpmt -md, confirm the reported type name matches the type you are reasoning about (e.g., do not conclude ConcurrentDictionary.TryGetValue is missing if you inspected ConcurrentDictionary+Enumerator).
IMPORTANT: If you suspect a profiler/tracer rewrote IL, VERIFY it: check whether the executing code is IL/JIT vs R2R/NGen, whether the method is JITted, and (when possible) inspect/dump the current IL to confirm rewriting rather than assuming.
IMPORTANT: Maintain a running, cumulative set of confirmed facts and evidence across iterations; do not “reset” what you know each step.
IMPORTANT: Evidence is automatically recorded from tool outputs into a stable evidence ledger with IDs (E#). Use analysis_evidence_add only to annotate existing evidence items (whyItMatters/tags/notes); do not invent new findings.
IMPORTANT: If you cite an evidence ID (E#) in analysis_complete.evidence, it must exist in the evidence ledger.
IMPORTANT: Treat SOS as already loaded unless the report explicitly says otherwise. The report metadata indicates whether SOS is loaded (metadata.sosLoaded) and is the source of truth. If metadata.sosLoaded is absent, treat SOS load status as unknown and gather evidence (e.g., exec "sos help" and record the exact output/error).
IMPORTANT: If metadata.sosLoaded=true, NEVER attempt to load SOS and NEVER claim SOS is not loaded. Do not run any "plugin load libsosplugin.so", ".load sos", or similar commands.
IMPORTANT: Prefer SOS commands via exec "!<command> ..." for portability (e.g., exec "!clrthreads", exec "!pe", exec "!clrstack -a", exec "!dumpheap -stat"). On LLDB the server strips the leading '!'. If needed, try exec "<command> ..." or exec "sos <command> ...".
IMPORTANT: If metadata.sosLoaded=false (or SOS commands fail), do not guess load steps; instead gather evidence (exec "sos help" and the exact error) and then propose the minimal corrective action.
IMPORTANT: Do NOT recommend disabling profilers/tracers/monitoring (e.g., Datadog) as a mitigation or “fix”; the goal is to find the root cause without turning off features. If instrumentation looks suspicious, gather in-dump evidence and propose corrective actions (version alignment, configuration, or a targeted upstream bug report).
IMPORTANT: Do not present speculation as fact. Every hypothesis must be backed by explicit evidence from tool outputs/report sections; if evidence is insufficient, call tools to gather it before concluding.
IMPORTANT: Do not assume the .NET runtime is bug-free. If something looks like a runtime/ReadyToRun/JIT bug, gather enough evidence for an upstream issue: exact runtime/CLR version, OS/arch, reproducibility, exception details, faulting IP, relevant MethodDesc/IL/native code state (IL vs R2R vs JIT), and the minimal command sequence that reproduces the finding.
IMPORTANT: If the report includes source context or Source Link URLs (analysis.sourceContext and/or stack frames with sourceUrl/sourceContext), use them as evidence: refer to the actual source code around the faulting lines, and fetch more via report_get(...) when needed.

Available tools:
- exec: Run any debugger command (LLDB/WinDbg/SOS)
- report_get: Fetch a section of the canonical report JSON by path (dot-path + optional [index]) with paging, projection, and simple filtering
- inspect: Inspect .NET objects by address (when available)
- get_thread_stack: Get a full stack trace for a specific thread from the report
- analysis_complete: Call when you've determined the root cause (include an explicit 'evidence' list in the tool input)

Internal meta tools (do NOT gather new evidence; keep these concise and batched):
- analysis_evidence_add: Annotate existing evidence items only (whyItMatters/tags/notes). Evidence facts are auto-generated from tool outputs.
- analysis_hypothesis_register: Register 3-4 competing hypotheses early (server assigns H# IDs when omitted). High confidence requires >=3 so the judge can reject top alternatives.
- analysis_hypothesis_score: Update hypothesis confidence and link evidence IDs (supportsEvidenceIds/contradictsEvidenceIds).

report_get notes:
- Path supports dot-path + optional [index] (e.g., analysis.threads.all[0]). Query expressions like items[?name==...] are NOT supported; use where={field,equals} instead.
- Arrays are pageable via limit/cursor.
- Objects can be paged via pageKind="object" + limit/cursor (useful when a single object is too large).
- Use select=[...] to project only needed fields (applies to objects and array items).
- Prefer omitting maxChars (server default is 20000). If you hit too_large, use the returned suggestedPaths/page hints and retry with a narrower request (arrays often suggest path[0] and common sub-fields).

SOS/.NET debugger command notes:
- If SOS is loaded (metadata.sosLoaded=true), prefer SOS commands via: exec "!<command> ..." (e.g., !clrthreads, !pe, !clrstack -a, !dumpheap -stat). On LLDB the server strips the leading '!'.
- Do not guess flags. When unsure, run: exec "sos help <command>" (or exec "sos help") and use the documented arguments.
- Common SOS commands (examples only): !clrstack -a, !printexception, !dumpheap -stat, !dumpobj <addr>, !dumpmt -md <mt>, !dumpmodule <addr>, !name2ee <assembly> <type>.
- If a command errors with "Unrecognized command or argument" or "Unknown option", adapt based on "sos help <command>" instead of retrying randomly.

Managed object inspection notes:
- Prefer using the inspect tool for managed objects: inspect(address=0x..., maxDepth=...).
- If inspect is available, do NOT use sos dumpobj/dumpvc first. Always try inspect first for any object address, then fall back to SOS only if inspect fails or is unavailable.
- Use SOS dumpobj/dumpvc only as a fallback (e.g., inspect unavailable) or to cross-check specific fields.

Investigation approach:
Phase 1 (MANDATORY): Baseline evidence (do NOT hypothesize before completing these in order)
- report_get(path="metadata", pageKind="object", limit=50)
- report_get(path="analysis.summary", pageKind="object", select=["crashType","description","recommendations","threadCount","moduleCount","assemblyCount"])
- report_get(path="analysis.environment", pageKind="object", select=["platform","runtime","process","nativeAot"])
- report_get(path="analysis.exception.type")
- report_get(path="analysis.exception.message")
- report_get(path="analysis.exception.hResult")
- report_get(path="analysis.exception.stackTrace", limit=8, select=["frameNumber","instructionPointer","module","function","sourceFile","lineNumber","isManaged"])

If any baseline call returns too_large, retry immediately using suggestedPaths and narrower select/limit before proceeding. Do not skip baseline evidence.

Optional (use only if needed for further verification; do not loop on it):
- report_get(path="analysis.exception.analysis", pageKind="object", limit=200)

Phase 2: General workflow (follow these steps after baseline evidence)
1. Review the initial crash report carefully
2. Identify the crashing thread and exception type
3. Examine the call stack for suspicious patterns
4. Inspect relevant objects if addresses are available
5. Check for common issues: null references, race conditions, memory corruption
6. Verify key claims with evidence (e.g., if you suspect MissingMethodException, confirm by inspecting the exception and verifying method presence via SOS output)
7. Form a hypothesis and gather evidence to confirm it
8. Call analysis_complete with your findings
9. If you are not sure about the root cause, keep gathering evidence and update the report until you have a final root cause or the maximum number of tools requests is reached.
10. If you have a final root cause, prepare a extended report about your findings and recommendations.

Phase 3: Hypotheses and deep dives (be explicit and falsify alternatives)
- Form 3-4 competing hypotheses (e.g., trimming/linker, assembly version mismatch, instrumentation/IL rewrite, runtime/ReadyToRun/JIT bug).
- For each hypothesis, gather discriminating evidence (small, targeted tool calls). Prefer checks that can falsify alternatives.

Phase 4: Finalization (analysis_complete)
- Always include an explicit evidence list in analysis_complete.evidence; each entry must cite the tool call/report path and the specific finding.
- Confidence rubric:
  - high: >=6 independent evidence items AND you explicitly rule out the top competing hypotheses with evidence.
  - medium: evidence supports a leading hypothesis, but at least one strong alternative remains plausible.
  - low: evidence is sparse/ambiguous or key checks are missing.
- Before calling analysis_complete, perform a falsification step: list 2-3 strongest alternative explanations and cite the evidence that contradicts each. If you cannot falsify, do NOT set confidence to "high".

Be thorough but efficient. Don't run unnecessary commands.
""";

    private static void AutoRecordEvidenceFromToolResult(
        AiEvidenceLedger evidenceLedger,
        Dictionary<string, string> evidenceIdByToolKeyHash,
        string toolName,
        JsonElement toolInput,
        string toolCacheKey,
        string outputForModel,
        bool wasCached,
        bool toolWasError,
        bool includeProvenanceMetadata,
        int evidenceExcerptMaxChars)
    {
        ArgumentNullException.ThrowIfNull(evidenceLedger);
        ArgumentNullException.ThrowIfNull(evidenceIdByToolKeyHash);

        if (!IsEvidenceToolName(toolName) || string.IsNullOrWhiteSpace(toolCacheKey))
        {
            return;
        }

        var toolKeyHash = ComputeSha256Prefixed(toolCacheKey);
        if (evidenceIdByToolKeyHash.ContainsKey(toolKeyHash))
        {
            return;
        }

        var source = BuildEvidenceSource(toolName, toolInput);
        var finding = string.IsNullOrWhiteSpace(outputForModel)
            ? toolWasError
                ? "(tool error; empty output)"
                : "(empty tool output)"
            : outputForModel.Trim();

        var excerptLimit = Math.Clamp(evidenceExcerptMaxChars, 64, 50_000);
        finding = TruncateText(finding, excerptLimit);

        var toolWasErrorFromOutput = toolWasError;
        if (!toolWasErrorFromOutput
            && toolName.Equals("report_get", StringComparison.OrdinalIgnoreCase)
            && TryParseReportGetResponseHasError(outputForModel ?? string.Empty, out var reportGetHasError)
            && reportGetHasError)
        {
            toolWasErrorFromOutput = true;
        }

        List<string>? baselineTags = null;
        if (!toolWasErrorFromOutput
            && toolInput.ValueKind == JsonValueKind.Object
            && toolName.Equals("report_get", StringComparison.OrdinalIgnoreCase))
        {
            var path = TryGetString(toolInput, "path");
            if (!string.IsNullOrWhiteSpace(path) && BaselineIdByPath.TryGetValue(path.Trim(), out var baselineId))
            {
                baselineTags = [$"BASELINE_{baselineId}"];
            }
        }

        var outputHash = ComputeSha256Prefixed(outputForModel ?? string.Empty);
        var addResult = evidenceLedger.AddOrUpdate(
        [
            new AiEvidenceLedgerItem
            {
                Source = source,
                Finding = finding,
                Tags = baselineTags,
                ToolName = includeProvenanceMetadata ? (toolName ?? string.Empty).Trim() : null,
                ToolKeyHash = includeProvenanceMetadata ? toolKeyHash : null,
                ToolOutputHash = includeProvenanceMetadata ? outputHash : null,
                ToolWasCached = includeProvenanceMetadata ? wasCached : null,
                ToolWasError = includeProvenanceMetadata ? toolWasErrorFromOutput : null
            }
        ]);

        var evidenceId = addResult.AddedIds.FirstOrDefault()
                         ?? addResult.IgnoredDuplicateIds.FirstOrDefault()
                         ?? addResult.UpdatedIds.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(evidenceId))
        {
            evidenceIdByToolKeyHash[toolKeyHash] = evidenceId;
        }
    }

    private static bool IsEvidenceToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        var normalized = toolName.Trim();
        return normalized.Equals("exec", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("report_get", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("inspect", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("get_thread_stack", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildEvidenceSource(string toolName, JsonElement toolInput)
    {
        var normalized = (toolName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "tool";
        }

        if (toolInput.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return normalized;
        }

        return $"{normalized}({CanonicalizeJson(toolInput)})";
    }

    private static string ComputeSha256Prefixed(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildToolCacheKey(string toolName, JsonElement toolInput)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return string.Empty;
        }

        if (string.Equals(toolName, "exec", StringComparison.OrdinalIgnoreCase) &&
            toolInput.ValueKind == JsonValueKind.Object &&
            toolInput.TryGetProperty("command", out var cmdEl) &&
            cmdEl.ValueKind == JsonValueKind.String)
        {
            return $"exec:{NormalizeDebuggerCommand(cmdEl.GetString() ?? string.Empty)}";
        }

        return $"{toolName.Trim().ToLowerInvariant()}:{CanonicalizeJson(toolInput)}";
    }

    private static string BuildCachedToolResultMessage(string toolName, JsonElement toolInput)
    {
        var normalized = (toolName ?? string.Empty).Trim();

        if (normalized.Equals("exec", StringComparison.OrdinalIgnoreCase))
        {
            var command = toolInput.ValueKind == JsonValueKind.Object ? TryGetString(toolInput, "command") : null;
            return string.IsNullOrWhiteSpace(command)
                ? "[cached tool result] Duplicate exec call detected; prior output already provided. Do not repeat identical tool calls."
                : $"[cached tool result] Duplicate exec call detected for command: {command.Trim()}. Prior output already provided. Do not repeat identical tool calls.";
        }

        if (normalized.Equals("report_get", StringComparison.OrdinalIgnoreCase))
        {
            var path = toolInput.ValueKind == JsonValueKind.Object ? TryGetString(toolInput, "path") : null;
            return string.IsNullOrWhiteSpace(path)
                ? "[cached tool result] Duplicate report_get call detected; prior output already provided. Do not repeat identical tool calls."
                : $"[cached tool result] Duplicate report_get call detected for path: {path.Trim()}. Prior output already provided. Do not repeat identical tool calls.";
        }

        if (normalized.Equals("inspect", StringComparison.OrdinalIgnoreCase))
        {
            var address = toolInput.ValueKind == JsonValueKind.Object ? TryGetString(toolInput, "address") : null;
            return string.IsNullOrWhiteSpace(address)
                ? "[cached tool result] Duplicate inspect call detected; prior output already provided. Do not repeat identical tool calls."
                : $"[cached tool result] Duplicate inspect call detected for address: {address.Trim()}. Prior output already provided. Do not repeat identical tool calls.";
        }

        if (normalized.Equals("get_thread_stack", StringComparison.OrdinalIgnoreCase))
        {
            var threadId = toolInput.ValueKind == JsonValueKind.Object ? TryGetString(toolInput, "threadId") : null;
            return string.IsNullOrWhiteSpace(threadId)
                ? "[cached tool result] Duplicate get_thread_stack call detected; prior output already provided. Do not repeat identical tool calls."
                : $"[cached tool result] Duplicate get_thread_stack call detected for threadId: {threadId.Trim()}. Prior output already provided. Do not repeat identical tool calls.";
        }

        return $"[cached tool result] Duplicate {normalized} call detected; prior output already provided. Do not repeat identical tool calls.";
    }

    private static string NormalizeDebuggerCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        // Collapse whitespace and make casing stable for matching (debugger commands are case-insensitive).
        var trimmed = command.Trim();
        var sb = new StringBuilder(trimmed.Length);
        var lastWasWhitespace = false;
        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasWhitespace)
                {
                    sb.Append(' ');
                    lastWasWhitespace = true;
                }
                continue;
            }

            lastWasWhitespace = false;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static string CanonicalizeJson(JsonElement element)
    {
        try
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
            WriteCanonicalJson(element, writer);
            writer.Flush();
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch
        {
            return element.ToString() ?? string.Empty;
        }
    }

    private static void WriteCanonicalJson(JsonElement element, Utf8JsonWriter writer)
    {
        WriteCanonicalJson(element, writer, null);
    }

    private static void WriteCanonicalJson(JsonElement element, Utf8JsonWriter writer, string? currentPropertyName)
    {
        if (currentPropertyName is not null && TryWriteCanonicalInteger(element, writer, currentPropertyName))
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                    .Where(p => p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    if (property.Name.Equals("select", StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        WriteCanonicalSelectArray(property.Value, writer);
                    }
                    else if (property.Name.Equals("where", StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        WriteCanonicalJson(property.Value, writer, "where");
                    }
                    else
                    {
                        WriteCanonicalJson(property.Value, writer, property.Name);
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                if (currentPropertyName is not null &&
                    currentPropertyName.Equals("where", StringComparison.Ordinal) &&
                    TryWriteCanonicalWhereArray(element, writer))
                {
                    return;
                }

                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(item, writer, currentPropertyName);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool TryWriteCanonicalInteger(JsonElement element, Utf8JsonWriter writer, string propertyName)
    {
        if (!propertyName.Equals("limit", StringComparison.Ordinal) &&
            !propertyName.Equals("maxChars", StringComparison.Ordinal) &&
            !propertyName.Equals("maxDepth", StringComparison.Ordinal) &&
            !propertyName.Equals("offset", StringComparison.Ordinal))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetInt64(out var value))
            {
                writer.WriteNumberValue(value);
                return true;
            }

            if (element.TryGetDouble(out var floating))
            {
                if (double.IsFinite(floating))
                {
                    var rounded = Math.Round(floating);
                    if (Math.Abs(floating - rounded) < 0.000000001d &&
                        rounded >= long.MinValue &&
                        rounded <= long.MaxValue)
                    {
                        writer.WriteNumberValue((long)rounded);
                        return true;
                    }
                }
            }

            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!long.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        writer.WriteNumberValue(parsed);
        return true;
    }

    private static bool TryWriteCanonicalWhereArray(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var items = new List<(string Key, JsonElement Item)>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                items.Clear();
                break;
            }

            var field = item.TryGetProperty("field", out var fieldEl) && fieldEl.ValueKind == JsonValueKind.String
                ? (fieldEl.GetString() ?? string.Empty).Trim()
                : string.Empty;

            string opName = string.Empty;
            string opValue = string.Empty;

            var opCandidate = item.EnumerateObject()
                .Where(p => !p.Name.Equals("field", StringComparison.Ordinal) && !p.Name.Equals("caseInsensitive", StringComparison.Ordinal))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(opCandidate.Name))
            {
                opName = opCandidate.Name;
                opValue = opCandidate.Value.ValueKind == JsonValueKind.String
                    ? (opCandidate.Value.GetString() ?? string.Empty).Trim()
                    : (opCandidate.Value.ToString() ?? string.Empty);
            }

            items.Add(($"{field}\u001f{opName}\u001f{opValue}", item));
        }

        if (items.Count == 0)
        {
            return false;
        }

        items.Sort((left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));

        writer.WriteStartArray();
        foreach (var (_, item) in items)
        {
            WriteCanonicalJson(item, writer, null);
        }
        writer.WriteEndArray();
        return true;
    }

    private static void WriteCanonicalSelectArray(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            WriteCanonicalJson(element, writer, null);
            return;
        }

        var fields = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    fields.Add(value.Trim());
                }
            }
            else
            {
                fields.Clear();
                break;
            }
        }

        if (fields.Count == 0)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
            {
                WriteCanonicalJson(item, writer, null);
            }
            writer.WriteEndArray();
            return;
        }

        var normalized = fields
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        writer.WriteStartArray();
        foreach (var field in normalized)
        {
            writer.WriteStringValue(field);
        }
        writer.WriteEndArray();
    }
}
