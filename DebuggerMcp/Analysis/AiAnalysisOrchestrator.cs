#nullable enable

using System.Diagnostics;
using System.Buffers;
using System.Linq;
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

    private LogLevel SamplingTraceLevel => EnableVerboseSamplingTrace ? LogLevel.Information : LogLevel.Debug;

    /// <summary>
    /// Gets or sets the maximum number of sampling iterations to perform.
    /// </summary>
    public int MaxIterations { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum number of output tokens to request per sampling call.
    /// </summary>
    public int MaxTokensPerRequest { get; set; } = 4096;

    /// <summary>
    /// Gets or sets the maximum number of tool calls to execute across all iterations.
    /// </summary>
    public int MaxToolCalls { get; set; } = 50;

    /// <summary>
    /// Number of sampling iterations between internal checkpoint synthesis steps that condense the current findings
    /// into a compact working memory and prune the conversation history. Set to 0 to disable.
    /// </summary>
    public int CheckpointEveryIterations { get; set; } = 4;

    /// <summary>
    /// Maximum output tokens to request for an internal checkpoint synthesis step.
    /// </summary>
    public int CheckpointMaxTokens { get; set; } = 1024;

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
        var tools = SamplingTools.GetCrashAnalysisTools();

        string? lastIterationAssistantText = null;
        string? lastModel = null;
        var toolIndexByIteration = new Dictionary<int, int>();
        var toolResultCache = new Dictionary<string, string>(StringComparer.Ordinal);
        var analysisCompleteRefusals = 0;
        var lastCheckpointIteration = 0;
        var commandsExecutedAtLastCheckpoint = 0;

        var traceRunDir = InitializeSamplingTraceDirectory();

        for (var iteration = 1; iteration <= maxIterations; iteration++)
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

            CreateMessageResult response;
            try
            {
                _logger.LogInformation("[AI] Sampling iteration {Iteration}...", iteration);
                LogSamplingRequestSummary(iteration, request);
                WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-request.json", BuildTraceRequest(iteration, request));
                response = await _samplingClient.RequestCompletionAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "[AI] Sampling failed at iteration {Iteration}", iteration);
                return new AiAnalysisResult
                {
                    RootCause = "AI analysis failed: sampling request error.",
                    Confidence = "low",
                    Reasoning = ex.Message,
                    Iterations = iteration - 1,
                    CommandsExecuted = commandsExecuted,
                    AnalyzedAt = DateTime.UtcNow
                };
            }

            if (response.Content == null || response.Content.Count == 0)
            {
                _logger.LogWarning("[AI] Sampling returned empty content at iteration {Iteration}", iteration);
                WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-response.json", BuildTraceResponse(iteration, response));
                return new AiAnalysisResult
                {
                    RootCause = "AI analysis failed: empty sampling response.",
                    Confidence = "low",
                    Reasoning = "The sampling client returned an empty response.",
                    Iterations = iteration - 1,
                    CommandsExecuted = commandsExecuted,
                    Model = response.Model,
                    AnalyzedAt = DateTime.UtcNow
                };
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

                if (commandsExecuted.Count >= maxToolCalls)
                {
                    _logger.LogWarning("[AI] Tool call budget exceeded ({MaxToolCalls}); stopping analysis.", maxToolCalls);
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
                    WriteSamplingTraceFile(traceRunDir, "final-ai-analysis.json", final);
                    return final;
                }

                var sw = Stopwatch.StartNew();
                var toolName = effectiveToolName ?? string.Empty;
                var toolInput = CloneToolInput(effectiveToolInput);
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
                    continue;
                }

                try
                {
                    var toolCacheKey = BuildToolCacheKey(toolName, toolInput);
                    string outputForModel;
                    var duration = sw.Elapsed;
                    if (toolResultCache.TryGetValue(toolCacheKey, out var cached))
                    {
                        sw.Stop();
                        duration = TimeSpan.Zero;
                        outputForModel = TruncateForModel(
                            "[cached tool result] Duplicate tool call detected; reusing prior output. Do not repeat identical tool calls.\n\n" +
                            cached);
                    }
                    else
                    {
                        var output = await ExecuteToolAsync(toolName, toolInput, fullReportJson, initialReport, debugger, clrMdAnalyzer, cancellationToken)
                            .ConfigureAwait(false);
                        outputForModel = TruncateForModel(output);
                        toolResultCache[toolCacheKey] = outputForModel;
                        sw.Stop();
                        duration = sw.Elapsed;
                    }

                    var toolOrdinal = toolIndexByIteration.TryGetValue(iteration, out var n) ? n + 1 : 1;
                    toolIndexByIteration[iteration] = toolOrdinal;
                    commandsExecuted.Add(new ExecutedCommand
                    {
                        Tool = toolName,
                        Input = toolInput,
                        Output = outputForModel,
                        Iteration = iteration,
                        Duration = duration.ToString("c")
                    });
                    WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-tool-{toolOrdinal:00}-{SanitizeFileComponent(toolName)}.json",
                        new { tool = toolName, input = toolInput.ToString(), output = outputForModel, isError = false, duration = duration.ToString("c") });

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
                    var messageForModel = TruncateForModel(message);

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
                }
            }

            if (pendingAnalysisComplete != null)
            {
                var otherToolsCoissued = rewrittenToolUses.Any(t => !string.Equals(t.Name, "analysis_complete", StringComparison.OrdinalIgnoreCase));

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
                    continue;
                }

                var completed = ParseAnalysisComplete(pendingAnalysisCompleteInput, commandsExecuted, iteration, response.Model);
                completed.AnalyzedAt = DateTime.UtcNow;
                WriteSamplingTraceFile(traceRunDir, "final-ai-analysis.json", completed);
                return completed;
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
                        passName: "analysis",
                        systemPrompt: SystemPrompt,
                        messages: messages,
                        iteration: iteration,
                        maxTokens: CheckpointMaxTokens,
                        traceRunDir: traceRunDir,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(checkpoint))
                {
                    lastCheckpointIteration = iteration;
                    commandsExecutedAtLastCheckpoint = commandsExecuted.Count;

                    messages.Clear();
                    messages.Add(BuildCheckpointCarryForwardMessage(checkpoint, passName: "analysis"));
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
        WriteSamplingTraceFile(traceRunDir, "final-ai-analysis.json", synthesized);
        return synthesized;
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

    private static string BuildAnalysisCompleteRefusalMessage(bool toolCallsWereCoissued, int refusalCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Cannot finalize yet.");
        sb.AppendLine();
        sb.AppendLine("Before calling analysis_complete, gather at least one additional piece of evidence using tools.");
        sb.AppendLine("Minimum recommended sequence:");
        sb.AppendLine("- report_get(path=\"analysis.summary\")");
        sb.AppendLine("- report_get(path=\"analysis.exception\", select=[\"type\",\"message\",\"hresult\"])");
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

        var maxIterations = Math.Max(1, MaxIterations);
        var maxTokens = MaxTokensPerRequest > 0 ? MaxTokensPerRequest : 1024;
        var maxToolCalls = MaxToolCalls > 0 ? MaxToolCalls : 50;

        string? lastModel = null;
        var toolIndexByIteration = new Dictionary<int, int>();
        var toolResultCache = new Dictionary<string, string>(StringComparer.Ordinal);
        var refusalCount = 0;
        var lastCheckpointIteration = 0;
        var commandsExecutedAtLastCheckpoint = 0;

        for (var iteration = 1; iteration <= maxIterations; iteration++)
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

            CreateMessageResult response;
            try
            {
                _logger.LogInformation("[AI] Sampling pass {Pass} iteration {Iteration}...", passName, iteration);
                LogSamplingRequestSummary(iteration, request);
                WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-request.json", BuildTraceRequest(iteration, request));
                response = await _samplingClient.RequestCompletionAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "[AI] Sampling pass {Pass} failed at iteration {Iteration}", passName, iteration);
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

            if (response.Content == null || response.Content.Count == 0)
            {
                _logger.LogWarning("[AI] Sampling pass {Pass} returned empty content at iteration {Iteration}", passName, iteration);
                WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-response.json", BuildTraceResponse(iteration, response));
                return passName switch
                {
                    "summary-rewrite" => new AiSummaryResult
                    {
                        Error = "AI summary rewrite failed: empty sampling response.",
                        Description = string.Empty,
                        Iterations = iteration - 1,
                        CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                        Model = response.Model,
                        AnalyzedAt = DateTime.UtcNow
                    } as T,
                    "thread-narrative" => new AiThreadNarrativeResult
                    {
                        Error = "AI thread narrative failed: empty sampling response.",
                        Description = string.Empty,
                        Confidence = "low",
                        Iterations = iteration - 1,
                        CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                        Model = response.Model,
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

                if (commandsExecuted.Count >= maxToolCalls)
                {
                    _logger.LogWarning("[AI] Tool call budget exceeded ({MaxToolCalls}); stopping pass {Pass}.", maxToolCalls, passName);
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
                    continue;
                }

                var sw = Stopwatch.StartNew();
                try
                {
                    string outputForModel;
                    var duration = sw.Elapsed;
                    var toolCacheKey = BuildToolCacheKey(toolName, toolInput);
                    if (toolResultCache.TryGetValue(toolCacheKey, out var cached))
                    {
                        sw.Stop();
                        duration = TimeSpan.Zero;
                        outputForModel = TruncateForModel(
                            "[cached tool result] Duplicate tool call detected; reusing prior output. Do not repeat identical tool calls.\n\n" +
                            cached);
                    }
                    else
                    {
                        var output = await ExecuteToolAsync(toolName, toolInput, fullReportJson, report, debugger, clrMdAnalyzer, cancellationToken)
                            .ConfigureAwait(false);
                        outputForModel = TruncateForModel(output);
                        toolResultCache[toolCacheKey] = outputForModel;
                        sw.Stop();
                        duration = sw.Elapsed;
                    }

                    var toolOrdinal = toolIndexByIteration.TryGetValue(iteration, out var n) ? n + 1 : 1;
                    toolIndexByIteration[iteration] = toolOrdinal;
                    commandsExecuted.Add(new ExecutedCommand
                    {
                        Tool = toolName,
                        Input = CloneToolInput(toolInput),
                        Output = outputForModel,
                        Iteration = iteration,
                        Duration = duration.ToString("c")
                    });

                    WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-tool-{toolOrdinal:00}-{SanitizeFileComponent(toolName)}.json",
                        new { tool = toolName, input = CloneToolInput(toolInput).ToString(), output = outputForModel, isError = false, duration = duration.ToString("c") });

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
                }
            }

            if (pendingComplete != null)
            {
                var otherToolsCoissued = rewrittenToolUses.Any(t => !string.Equals(t.Name, completionToolName, StringComparison.OrdinalIgnoreCase));

                if (!HasEvidenceToolCalls(commandsExecuted) || otherToolsCoissued)
                {
                    refusalCount++;
                    var msg = BuildGenericCompletionRefusalMessage(passName, completionToolName, otherToolsCoissued, refusalCount);

                    var toolOrdinal = toolIndexByIteration.TryGetValue(iteration, out var n) ? n + 1 : 1;
                    toolIndexByIteration[iteration] = toolOrdinal;
                    WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-tool-{toolOrdinal:00}-{SanitizeFileComponent(completionToolName)}.json",
                        new { tool = completionToolName, input = CloneToolInput(pendingCompleteInput).ToString(), output = msg, isError = true, duration = TimeSpan.Zero.ToString("c") });

                    if (string.IsNullOrWhiteSpace(pendingCompleteId))
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
                                ToolUseId = pendingCompleteId,
                                IsError = true,
                                Content =
                                [
                                    new TextContentBlock { Text = TruncateForModel(msg) }
                                ]
                            }
                        ]
                    });
                    continue;
                }

                return ParseCompletionTool<T>(passName, completionToolName, pendingCompleteInput, commandsExecuted, iteration, lastModel);
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
                        iteration: iteration,
                        maxTokens: CheckpointMaxTokens,
                        traceRunDir: traceRunDir,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(checkpoint))
                {
                    lastCheckpointIteration = iteration;
                    commandsExecutedAtLastCheckpoint = commandsExecuted.Count;

                    messages.Clear();
                    messages.Add(BuildCheckpointCarryForwardMessage(checkpoint, passName: passName));
                }
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

        var duplicateReuseCount = recent.Count(c =>
            c.Output.StartsWith("[cached tool result] Duplicate tool call detected", StringComparison.Ordinal));

        return tooLargeCount >= 2 || duplicateReuseCount >= 1;
    }

    private async Task<string?> TryCreateCheckpointAsync(
        string passName,
        string systemPrompt,
        List<SamplingMessage> messages,
        int iteration,
        int maxTokens,
        string? traceRunDir,
        CancellationToken cancellationToken)
    {
        const string checkpointSchema = """
{
  "facts": ["string"],
  "hypotheses": [
    {
      "hypothesis": "string",
      "confidence": "high|medium|low|unknown",
      "evidence": ["string"],
      "unknowns": ["string"]
    }
  ],
  "evidence": [
    {
      "id": "E1",
      "source": "tool call summary (e.g., report_get(path=...), exec(cmd=...))",
      "finding": "string"
    }
  ],
  "doNotRepeat": ["string"],
  "nextSteps": [
    {
      "tool": "report_get|exec|inspect|get_thread_stack",
      "call": "string",
      "why": "string"
    }
  ]
}
""";

        var prompt = $"""
Create an INTERNAL checkpoint for pass "{passName}".

Goal: preserve a compact working memory so we can prune older tool outputs and avoid repeating tool calls.
Do NOT request any tools in this step.

Rules:
- Base this ONLY on evidence already shown in this conversation (tool results already returned).
- If the conversation contains tool requests without corresponding tool results, those tool requests were NOT executed and must be ignored.
- Be detailed, but bounded: facts<=12, hypotheses<=5, evidence<=12, doNotRepeat<=12, nextSteps<=10.
- Keep strings concise (prefer <=200 chars each).
- In nextSteps, propose narrowly-scoped tool calls (small report_get paths with select/limit/cursor; prefer smaller paths).

Return ONLY valid JSON (no markdown, no code fences) with this schema:
{checkpointSchema}
""";

        var checkpointMessages = new List<SamplingMessage>(messages)
        {
            new()
            {
                Role = Role.User,
                Content =
                [
                    new TextContentBlock { Text = prompt }
                ]
            }
        };

        var request = new CreateMessageRequestParams
        {
            SystemPrompt = systemPrompt,
            Messages = checkpointMessages,
            MaxTokens = Math.Max(256, Math.Min(maxTokens, 2048)),
            Tools = [],
            ToolChoice = null
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
            _logger.LogWarning(ex, "[AI] Checkpoint synthesis failed in pass {Pass} at iteration {Iteration}", passName, iteration);
            return null;
        }

        WriteSamplingTraceFile(traceRunDir, $"iter-{iteration:0000}-checkpoint-response.json", BuildTraceResponse(iteration, response));

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
            return TruncateText(text, maxChars: 20_000);
        }

        var normalized = JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = false });
        if (normalized.Length > 20_000)
        {
            _logger.LogWarning(
                "[AI] Checkpoint synthesis output exceeded max chars ({MaxChars}) in pass {Pass} at iteration {Iteration} (chars={Chars}); skipping checkpoint.",
                20_000,
                passName,
                iteration,
                normalized.Length);
            return null;
        }

        return normalized;
    }

    private static SamplingMessage BuildCheckpointCarryForwardMessage(string checkpointJson, string passName)
    {
        var prompt = $"""
This is the current INTERNAL working memory checkpoint for pass "{passName}".
Treat it as authoritative.

Older tool outputs may have been pruned to keep context small. Do not repeat tool calls listed in doNotRepeat.
When you need more evidence, propose narrowly-scoped tool calls (small report_get paths with select/limit/cursor).

Checkpoint JSON:
{checkpointJson}
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

    private static string BuildGenericCompletionRefusalMessage(string passName, string completionToolName, bool toolCallsWereCoissued, int refusalCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Cannot finalize yet.");
        sb.AppendLine();
        sb.AppendLine("Before calling the completion tool, gather at least one additional piece of evidence using tools (report_get/exec/inspect/get_thread_stack).");
        sb.AppendLine("Minimum recommended sequence:");
        sb.AppendLine("- report_get(path=\"analysis.summary\")");
        sb.AppendLine("- report_get(path=\"analysis.exception\", select=[\"type\",\"message\",\"hresult\"])");
        sb.AppendLine("- report_get(path=\"analysis.threads.faultingThread\")");
        sb.AppendLine("- report_get(path=\"analysis.environment.platform\")");
        sb.AppendLine("- report_get(path=\"analysis.environment.runtime\")");
        sb.AppendLine("- report_get(path=\"analysis.memory\")");
        sb.AppendLine("- report_get(path=\"analysis.synchronization\")");
        sb.AppendLine("- report_get(path=\"analysis.async\")");
        sb.AppendLine();
        if (toolCallsWereCoissued)
        {
            sb.AppendLine($"Also: do not call {completionToolName} in the same message as other tool calls.");
            sb.AppendLine("Execute evidence-gathering tools first, then call the completion tool in a later message after you've read the tool outputs.");
            sb.AppendLine();
        }
        sb.AppendLine($"Pass: {passName}");
        sb.AppendLine($"Refusal count: {refusalCount}");
        sb.AppendLine($"Now call report_get (or exec/inspect/get_thread_stack) to gather evidence, then try {completionToolName} again.");
        return sb.ToString().TrimEnd();
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
        if (!string.Equals(name, "exec", StringComparison.OrdinalIgnoreCase))
        {
            return (name, toolUse.Input);
        }

        if (inspector == null || !inspector.IsOpen)
        {
            return (name, toolUse.Input);
        }

        var cmd = TryGetString(toolUse.Input, "command") ?? string.Empty;
        var match = Regex.Match(cmd, @"^\s*sos\s+(dumpobj|dumpvc)\s+(?<addr>0x[0-9a-fA-F]+|[0-9a-fA-F]+)\s*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return (name, toolUse.Input);
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
	            tools = request.Tools?.Select(t => new
	            {
	                name = t.Name,
	                description = t.Description,
	                inputSchema = t.InputSchema.ToString()
	            }),
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
        sb.AppendLine("Avoid rerunning the same tool calls; reuse prior evidence and expand only the specific sections you need.");
        sb.AppendLine();
        sb.AppendLine("Report index (summary + TOC):");
        sb.AppendLine(ReportSectionApi.BuildIndex(fullReportJson));
        sb.AppendLine();
        sb.AppendLine("Evidence snapshot (bounded):");
        sb.AppendLine(AiSamplingPromptBuilder.Build(initialReport));

        return TruncateInitialPrompt(sb.ToString());
    }

    private static AiAnalysisResult ParseAnalysisComplete(
        JsonElement input,
        List<ExecutedCommand> commandsExecuted,
        int iteration,
        string? model)
    {
        var result = new AiAnalysisResult
        {
            RootCause = TryGetString(input, "rootCause") ?? string.Empty,
            Confidence = TryGetString(input, "confidence") ?? "unknown",
            Reasoning = TryGetString(input, "reasoning"),
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

        return result;
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
        const string analysisSchema = """
{
  "rootCause": "string",
  "confidence": "high|medium|low|unknown",
  "reasoning": "string",
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
	            MaxTokens = Math.Max(256, Math.Min(maxTokens, 2048)),
	            Tools = [],
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
        const string analysisSchema = """
{
  "rootCause": "string",
  "confidence": "high|medium|low|unknown",
  "reasoning": "string",
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
	            MaxTokens = Math.Max(256, Math.Min(maxTokens, 2048)),
	            Tools = [],
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
	            MaxTokens = Math.Max(256, Math.Min(maxTokens, 1024)),
	            Tools = [],
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
	            MaxTokens = Math.Max(256, Math.Min(maxTokens, 1024)),
	            Tools = [],
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

        EnsureSafeDebuggerCommand(command);
        cancellationToken.ThrowIfCancellationRequested();
        var output = debugger.ExecuteCommand(command);
        return Task.FromResult(output ?? string.Empty);
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

        foreach (var t in all)
        {
            if (string.Equals(t.ThreadId, needle, StringComparison.OrdinalIgnoreCase))
            {
                return t;
            }

            if (needleInt.HasValue && t.ManagedThreadId == needleInt.Value)
            {
                return t;
            }

            if (!string.IsNullOrWhiteSpace(t.OsThreadId) &&
                string.Equals(NormalizeHex(t.OsThreadId), NormalizeHex(needle), StringComparison.OrdinalIgnoreCase))
            {
                return t;
            }

            if (!string.IsNullOrWhiteSpace(t.OsThreadIdDecimal) &&
                string.Equals(t.OsThreadIdDecimal, needle, StringComparison.OrdinalIgnoreCase))
            {
                return t;
            }

        }

        return null;
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

    private const string SystemPrompt = """
You are an expert crash dump analyst. You've been given an initial crash analysis report from a memory dump.

Your task is to determine the ROOT CAUSE of the crash through systematic investigation.

IMPORTANT: Your primary objective is to determine the ROOT CAUSE of the crash through systematic investigation.
IMPORTANT: Always keep the user's stated goal and the primary objective of the analysis in mind. Do not drift into unrelated investigations.
IMPORTANT: Before using exec, determine the active debugger type from the initial report metadata (metadata.debuggerType, e.g. "LLDB" or "WinDbg") and only issue commands that exist in that debugger. Never run WinDbg-only commands in an LLDB session (or vice versa).
IMPORTANT: Exception: WinDbg-style SOS commands prefixed with '!' (e.g., !pe, !clrstack, !dumpheap) are acceptable in LLDB sessions because the server strips the leading '!'.
IMPORTANT: Do not repeat identical tool calls with the same arguments; reuse prior tool outputs as evidence and move the investigation forward.
IMPORTANT: Do not assume assembly versions from file paths. Treat paths as hints and verify versions using assembly metadata from the report (prefer report_get for analysis.assemblies/items and analysis.modules where available).
IMPORTANT: If you suspect a profiler/tracer rewrote IL, VERIFY it: check whether the executing code is IL/JIT vs R2R/NGen, whether the method is JITted, and (when possible) inspect/dump the current IL to confirm rewriting rather than assuming.
IMPORTANT: Maintain a running, cumulative set of confirmed facts and evidence across iterations; do not “reset” what you know each step.
IMPORTANT: Treat SOS as already loaded unless the report explicitly says otherwise. The report metadata indicates whether SOS is loaded (metadata.sosLoaded) and is the source of truth.
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
- analysis_complete: Call when you've determined the root cause

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

Be thorough but efficient. Don't run unnecessary commands.
""";

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
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(item, writer);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
