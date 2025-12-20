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
        var tools = SamplingTools.GetDebuggerTools();

        string? lastIterationAssistantText = null;
        bool lastIterationHadToolCalls = false;
        string? lastModel = null;
        var toolIndexByIteration = new Dictionary<int, int>();
        var toolResultCache = new Dictionary<string, string>(StringComparer.Ordinal);
        var analysisCompleteRefusals = 0;

        var traceRunDir = InitializeSamplingTraceDirectory();

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            var request = new CreateMessageRequestParams
            {
                SystemPrompt = SystemPrompt,
                Messages = messages,
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
            lastIterationHadToolCalls = false;
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

                lastIterationHadToolCalls = true;

                if (commandsExecuted.Count >= maxToolCalls)
                {
                    _logger.LogWarning("[AI] Tool call budget exceeded ({MaxToolCalls}); stopping analysis.", maxToolCalls);
                    return new AiAnalysisResult
                    {
                        RootCause = "Analysis incomplete - tool call budget exceeded.",
                        Confidence = "low",
                        Reasoning = $"The AI attempted to execute more than {maxToolCalls} tool calls. " +
                                    "Increase MaxToolCalls if you need a longer investigation loop.",
                        Iterations = iteration,
                        CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                        Model = response.Model,
                        AnalyzedAt = DateTime.UtcNow
                    };
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
                    lastIterationHadToolCalls = true;

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
        }

        if (!lastIterationHadToolCalls && !string.IsNullOrWhiteSpace(lastIterationAssistantText))
        {
            var result = new AiAnalysisResult
            {
                RootCause = "AI returned an answer but did not call analysis_complete.",
                Confidence = "low",
                Reasoning = lastIterationAssistantText,
                Iterations = maxIterations,
                CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                Model = lastModel,
                AnalyzedAt = DateTime.UtcNow
            };
            WriteSamplingTraceFile(traceRunDir, "final-ai-analysis.json", result);
            return result;
        }

        var incomplete = new AiAnalysisResult
        {
            RootCause = "Analysis incomplete - maximum iterations reached.",
            Confidence = "low",
            Reasoning = $"The AI did not call analysis_complete within {maxIterations} iterations.",
            Iterations = maxIterations,
            CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
            Model = lastModel,
            AnalyzedAt = DateTime.UtcNow
        };
        WriteSamplingTraceFile(traceRunDir, "final-ai-analysis.json", incomplete);
        return incomplete;
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
- Prefer omitting maxChars (server default is 20000). If you hit too_large, use the returned suggestedPaths/page hints and retry with a narrower request.

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
