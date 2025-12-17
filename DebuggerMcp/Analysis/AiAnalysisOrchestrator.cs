#nullable enable

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    /// Gets a value indicating whether AI analysis is available for the connected client.
    /// </summary>
    public bool IsSamplingAvailable => _samplingClient.IsSamplingSupported;

    /// <summary>
    /// Performs AI-powered crash analysis with an iterative investigation loop.
    /// </summary>
    /// <param name="initialReport">Initial structured crash report.</param>
    /// <param name="initialReportJson">The JSON string used as the source-of-truth prompt for the initial report.</param>
    /// <param name="debugger">Debugger manager used to execute commands.</param>
    /// <param name="clrMdAnalyzer">Optional ClrMD analyzer for managed object inspection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The AI analysis result.</returns>
    public async Task<AiAnalysisResult> AnalyzeCrashAsync(
        CrashAnalysisResult initialReport,
        string initialReportJson,
        IDebuggerManager debugger,
        ClrMdAnalyzer? clrMdAnalyzer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialReport);
        ArgumentNullException.ThrowIfNull(initialReportJson);
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
                        Text = BuildInitialPrompt(initialReportJson)
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
                return new AiAnalysisResult
                {
                    RootCause = "AI analysis failed: empty sampling response.",
                    Confidence = "low",
                    Reasoning = "The sampling client returned an empty response.",
                    Iterations = iteration,
                    CommandsExecuted = commandsExecuted,
                    Model = response.Model,
                    AnalyzedAt = DateTime.UtcNow
                };
            }

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
                _logger.LogDebug("[AI] No tool calls in iteration {Iteration}", iteration);
                continue;
            }

            foreach (var toolUse in toolUses)
            {
                if (string.Equals(toolUse.Name, "analysis_complete", StringComparison.OrdinalIgnoreCase))
                {
                    var completed = ParseAnalysisComplete(toolUse.Input, commandsExecuted, iteration, response.Model);
                    completed.AnalyzedAt = DateTime.UtcNow;
                    return completed;
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
                var toolName = toolUse.Name ?? string.Empty;
                var toolInput = CloneToolInput(toolUse.Input);
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
                    var output = await ExecuteToolAsync(toolName, toolInput, initialReport, debugger, clrMdAnalyzer, cancellationToken)
                        .ConfigureAwait(false);
                    var outputForModel = TruncateForModel(output);

                    sw.Stop();
                    commandsExecuted.Add(new ExecutedCommand
                    {
                        Tool = toolName,
                        Input = toolInput,
                        Output = outputForModel,
                        Iteration = iteration,
                        Duration = sw.Elapsed.ToString("c")
                    });

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

                    commandsExecuted.Add(new ExecutedCommand
                    {
                        Tool = toolName,
                        Input = toolInput,
                        Output = messageForModel,
                        Iteration = iteration,
                        Duration = sw.Elapsed.ToString("c")
                    });

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
        }

        if (!lastIterationHadToolCalls && !string.IsNullOrWhiteSpace(lastIterationAssistantText))
        {
            return new AiAnalysisResult
            {
                RootCause = "AI returned an answer but did not call analysis_complete.",
                Confidence = "low",
                Reasoning = lastIterationAssistantText,
                Iterations = maxIterations,
                CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
                Model = lastModel,
                AnalyzedAt = DateTime.UtcNow
            };
        }

        return new AiAnalysisResult
        {
            RootCause = "Analysis incomplete - maximum iterations reached.",
            Confidence = "low",
            Reasoning = $"The AI did not call analysis_complete within {maxIterations} iterations.",
            Iterations = maxIterations,
            CommandsExecuted = commandsExecuted.Count == 0 ? null : commandsExecuted,
            Model = lastModel,
            AnalyzedAt = DateTime.UtcNow
        };
    }

    private static string BuildInitialPrompt(string initialReportJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Analyze this crash report JSON.");
        sb.AppendLine("If you need more data, call tools (exec/inspect/get_thread_stack).");
        sb.AppendLine("Note: Very large sections may be omitted to keep the prompt bounded; use tools to fetch more evidence.");
        sb.AppendLine();
        sb.AppendLine(TruncateInitialPrompt(initialReportJson));
        return sb.ToString();
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
        CrashAnalysisResult initialReport,
        IDebuggerManager debugger,
        ClrMdAnalyzer? clrMdAnalyzer,
        CancellationToken cancellationToken)
    {
        var normalized = (toolName ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "exec" => await ExecuteExecAsync(toolInput, debugger, cancellationToken).ConfigureAwait(false),
            "inspect" => ExecuteInspect(toolInput, clrMdAnalyzer),
            "get_thread_stack" => ExecuteGetThreadStack(toolInput, initialReport),
            _ => throw new InvalidOperationException($"Unknown tool: {toolName}")
        };
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

    private static string ExecuteInspect(JsonElement toolInput, ClrMdAnalyzer? clrMdAnalyzer)
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

Available tools:
- exec: Run any debugger command (LLDB/WinDbg/SOS)
- inspect: Inspect .NET objects by address (when available)
- get_thread_stack: Get a full stack trace for a specific thread from the report
- analysis_complete: Call when you've determined the root cause

SOS/.NET debugger command notes:
- If SOS is loaded, prefer running SOS commands via: exec "sos <command> <args>".
- Do not guess flags. When unsure, run: exec "sos help <command>" (or exec "sos help") and use the documented arguments.
- Common SOS commands (examples only): sos clrstack -a, sos printexception, sos dumpheap -stat, sos dumpobj <addr>, sos dumpmt -md <mt>, sos dumpmodule <addr>, sos name2ee <assembly> <type>.
- If a command errors with "Unrecognized command or argument" or "Unknown option", adapt based on "sos help <command>" instead of retrying randomly.

Investigation approach:
1. Review the initial crash report carefully
2. Identify the crashing thread and exception type
3. Examine the call stack for suspicious patterns
4. Inspect relevant objects if addresses are available
5. Check for common issues: null references, race conditions, memory corruption
6. Verify key claims with evidence (e.g., if you suspect MissingMethodException, confirm by inspecting the exception and verifying method presence via SOS output)
7. Form a hypothesis and gather evidence to confirm it
8. Call analysis_complete with your findings

Be thorough but efficient. Don't run unnecessary commands.
""";
}
