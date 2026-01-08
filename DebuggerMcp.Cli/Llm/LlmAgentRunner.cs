using DebuggerMcp.Cli.Shell.Transcript;
using System.Buffers;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Runs an iterative tool-calling loop for the CLI <c>llm</c> command agent mode.
/// </summary>
internal sealed class LlmAgentRunner(
    Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<ChatCompletionResult>> completeAsync,
    Func<ChatToolCall, CancellationToken, Task<string>> executeToolAsync,
    LlmAgentSessionState sessionState,
    int maxIterations = 20,
    int maxToolResultChars = 20_000)
{
    private readonly Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<ChatCompletionResult>> _completeAsync =
        completeAsync ?? throw new ArgumentNullException(nameof(completeAsync));

    private readonly Func<ChatToolCall, CancellationToken, Task<string>> _executeToolAsync =
        executeToolAsync ?? throw new ArgumentNullException(nameof(executeToolAsync));

    private readonly LlmAgentSessionState _sessionState = sessionState ?? throw new ArgumentNullException(nameof(sessionState));

    private readonly int _maxIterations = maxIterations <= 0 ? 20 : maxIterations;
    private readonly int _maxToolResultChars = maxToolResultChars <= 512 ? 20_000 : maxToolResultChars;

    public async Task<LlmAgentRunResult> RunAsync(IReadOnlyList<ChatMessage> seedMessages, CancellationToken cancellationToken)
    {
        if (seedMessages == null)
        {
            throw new ArgumentNullException(nameof(seedMessages));
        }

        var messages = new List<ChatMessage>(seedMessages);
        var finalText = string.Empty;
        var toolCallsExecuted = 0;
        var consecutiveNoProgressIterations = 0;
        var loopBreaksIssued = 0;
        var totalNewEvidence = 0;
        var baselinePrefetchAttempts = 0;
        var wantsConclusion = LlmAgentPromptClassifier.IsConclusionSeekingOrContinuation(_sessionState, TryGetLastUserPrompt(seedMessages));

        for (var iteration = 1; iteration <= _maxIterations; iteration++)
        {
            var completion = await _completeAsync(messages, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(completion.Text))
            {
                finalText = completion.Text.Trim();
            }

            if (completion.ToolCalls.Count == 0)
            {
                if (wantsConclusion && !IsBaselineComplete(_sessionState))
                {
                    if (baselinePrefetchAttempts < 1)
                    {
                        baselinePrefetchAttempts++;
                        var didExecute = await TryExecuteMissingBaselineAsync(
                                messages: messages,
                                iteration: iteration,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);

                        toolCallsExecuted += didExecute.ToolCallsExecuted;
                        totalNewEvidence += didExecute.NewEvidenceCount;

                        if (didExecute.ToolCallsExecuted > 0)
                        {
                            continue;
                        }
                    }

                    // Avoid returning a confident conclusion with missing baseline evidence.
                    return new LlmAgentRunResult(
                        FinalText: BuildBaselineIncompleteMessage(_sessionState),
                        Iterations: iteration,
                        ToolCallsExecuted: toolCallsExecuted);
                }

                _sessionState.LastCheckpointJson = LlmAgentCheckpointBuilder.BuildCarryForwardCheckpoint(
                    sessionState: _sessionState,
                    seedMessages: seedMessages,
                    iteration: iteration,
                    toolCallsExecuted: toolCallsExecuted,
                    totalNewEvidence: totalNewEvidence);

                return new LlmAgentRunResult(
                    FinalText: string.IsNullOrWhiteSpace(finalText) ? "(LLM returned no content)" : finalText,
                    Iterations: iteration,
                    ToolCallsExecuted: toolCallsExecuted);
            }

            messages.Add(new ChatMessage(
                "assistant",
                completion.Text ?? string.Empty,
                toolCallId: null,
                toolCalls: completion.ToolCalls,
                contentJson: completion.RawMessageContent,
                providerMessageFields: completion.ProviderMessageFields));

            var newEvidenceThisIteration = 0;
            foreach (var toolCall in completion.ToolCalls)
            {
                // llmagent does not tool-result cache: always execute requested tools (subject to user confirmation).
                var toolResultRaw = await _executeToolAsync(toolCall, cancellationToken).ConfigureAwait(false);
                toolCallsExecuted++;

                // Defense-in-depth: redact sensitive values before sending tool output to the model.
                var toolResultRedacted = TranscriptRedactor.RedactText(toolResultRaw);
                var toolResultForModel = TruncateForModel(toolResultRedacted, _maxToolResultChars);

                // Report snapshot tracking (only for report_get metadata).
                if (string.Equals(toolCall.Name, "report_get", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(TryReadJsonStringProperty(toolCall.ArgumentsJson, "path"), "metadata", StringComparison.OrdinalIgnoreCase))
                {
                    _sessionState.TryUpdateSnapshotFromMetadataToolResult(toolResultForModel, out _);
                }

                var toolKey = BuildToolCacheKey(toolCall);
                var tags = LlmAgentToolTagger.GetTags(toolCall.Name, toolCall.ArgumentsJson);
                var toolWasError = LlmAgentToolResultClassifier.IsError(toolResultForModel);

                var evidenceUpdate = _sessionState.Evidence.AddOrUpdate(
                    toolName: toolCall.Name,
                    argumentsJson: toolCall.ArgumentsJson,
                    toolKey: toolKey,
                    toolResultForHashing: toolResultForModel,
                    toolResultPreview: BuildEvidencePreview(toolResultForModel),
                    tags: tags,
                    toolWasError: toolWasError,
                    timestampUtc: DateTimeOffset.UtcNow);

                if (evidenceUpdate.IsNewEvidence)
                {
                    newEvidenceThisIteration++;
                    totalNewEvidence++;
                }

                messages.Add(new ChatMessage("tool", toolResultForModel, toolCall.Id, toolCalls: null));
            }

            if (newEvidenceThisIteration == 0)
            {
                consecutiveNoProgressIterations++;
                if (consecutiveNoProgressIterations >= 2)
                {
                    loopBreaksIssued++;
                    var checkpoint = LlmAgentCheckpointBuilder.BuildLoopBreakCheckpoint(
                        sessionState: _sessionState,
                        seedMessages: seedMessages,
                        iteration: iteration,
                        toolCallsExecuted: toolCallsExecuted);

                    _sessionState.LastCheckpointJson = checkpoint;
                    messages = ApplyCheckpointPrune(messages, checkpoint);
                    consecutiveNoProgressIterations = 0;

                    if (loopBreaksIssued >= 3)
                    {
                        return new LlmAgentRunResult(
                            FinalText: "(LLM agent appears stuck repeating the same actions. Please guide me: do you want me to follow the latest 'Try:' hint (if any), run report_index() to re-orient, or run a specific report_get/exec command?)",
                            Iterations: iteration,
                            ToolCallsExecuted: toolCallsExecuted);
                    }
                }
            }
            else
            {
                consecutiveNoProgressIterations = 0;
            }
        }

        _sessionState.LastCheckpointJson = LlmAgentCheckpointBuilder.BuildCarryForwardCheckpoint(
            sessionState: _sessionState,
            seedMessages: seedMessages,
            iteration: _maxIterations,
            toolCallsExecuted: toolCallsExecuted,
            totalNewEvidence: totalNewEvidence);

        var iterationLimitCheckpoint = LlmAgentCheckpointBuilder.BuildIterationLimitCheckpoint(
            sessionState: _sessionState,
            seedMessages: seedMessages,
            iteration: _maxIterations,
            toolCallsExecuted: toolCallsExecuted);

        _sessionState.LastCheckpointJson = iterationLimitCheckpoint;
        var nextStep = TryGetFirstCheckpointNextStep(iterationLimitCheckpoint);
        var nextStepHint = nextStep == null ? string.Empty : $"\nSuggested next: {nextStep.Value.Tool}({nextStep.Value.ArgsJson})";

        return new LlmAgentRunResult(
            FinalText: string.IsNullOrWhiteSpace(finalText)
                ? $"(LLM agent stopped after {_maxIterations} steps without a final answer){nextStepHint}"
                : $"(LLM agent stopped after {_maxIterations} steps){nextStepHint}\n{finalText}",
            Iterations: _maxIterations,
            ToolCallsExecuted: toolCallsExecuted);
    }

    private static (string Tool, string ArgsJson)? TryGetFirstCheckpointNextStep(string checkpointJson)
    {
        if (string.IsNullOrWhiteSpace(checkpointJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(checkpointJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("nextSteps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var first = steps.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!first.TryGetProperty("tool", out var toolProp) || toolProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            if (!first.TryGetProperty("argsJson", out var argsProp) || argsProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var tool = toolProp.GetString();
            var argsJson = argsProp.GetString();
            if (string.IsNullOrWhiteSpace(tool) || string.IsNullOrWhiteSpace(argsJson))
            {
                return null;
            }

            if (argsJson.Length > 300)
            {
                argsJson = argsJson[..300] + "...";
            }

            return (tool, argsJson);
        }
        catch
        {
            return null;
        }
    }

    private async Task<(int ToolCallsExecuted, int NewEvidenceCount)> TryExecuteMissingBaselineAsync(
        List<ChatMessage> messages,
        int iteration,
        CancellationToken cancellationToken)
    {
        var missing = new List<LlmAgentBaselineItem>();
        foreach (var item in LlmAgentBaselinePolicy.RequiredBaseline)
        {
            var entry = _sessionState.Evidence.TryGetLatestByTag(item.Tag);
            if (entry == null || entry.ToolWasError)
            {
                missing.Add(item);
            }
        }

        if (missing.Count == 0)
        {
            return (ToolCallsExecuted: 0, NewEvidenceCount: 0);
        }

        var baselineToolCalls = new List<ChatToolCall>(missing.Count);
        foreach (var item in missing)
        {
            baselineToolCalls.Add(new ChatToolCall(
                id: $"baseline-{iteration}-{item.Tag}",
                name: item.PlannedCall.ToolName,
                argumentsJson: item.PlannedCall.ArgumentsJson));
        }

        messages.Add(new ChatMessage(
            "assistant",
            "Gathering missing baseline crash info...",
            toolCallId: null,
            toolCalls: baselineToolCalls));

        var toolCallsExecuted = 0;
        var newEvidence = 0;
        foreach (var toolCall in baselineToolCalls)
        {
            var toolResultRaw = await _executeToolAsync(toolCall, cancellationToken).ConfigureAwait(false);
            toolCallsExecuted++;

            // Defense-in-depth: redact sensitive values before sending tool output to the model.
            var toolResultRedacted = TranscriptRedactor.RedactText(toolResultRaw);
            var toolResultForModel = TruncateForModel(toolResultRedacted, _maxToolResultChars);

            // Report snapshot tracking (only for report_get metadata).
            if (string.Equals(toolCall.Name, "report_get", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(TryReadJsonStringProperty(toolCall.ArgumentsJson, "path"), "metadata", StringComparison.OrdinalIgnoreCase))
            {
                _sessionState.TryUpdateSnapshotFromMetadataToolResult(toolResultForModel, out _);
            }

            var toolKey = BuildToolCacheKey(toolCall);
            var tags = LlmAgentToolTagger.GetTags(toolCall.Name, toolCall.ArgumentsJson);
            var toolWasError = LlmAgentToolResultClassifier.IsError(toolResultForModel);

            var evidenceUpdate = _sessionState.Evidence.AddOrUpdate(
                toolName: toolCall.Name,
                argumentsJson: toolCall.ArgumentsJson,
                toolKey: toolKey,
                toolResultForHashing: toolResultForModel,
                toolResultPreview: BuildEvidencePreview(toolResultForModel),
                tags: tags,
                toolWasError: toolWasError,
                timestampUtc: DateTimeOffset.UtcNow);

            if (evidenceUpdate.IsNewEvidence)
            {
                newEvidence++;
            }

            messages.Add(new ChatMessage("tool", toolResultForModel, toolCall.Id, toolCalls: null));
        }

        return (ToolCallsExecuted: toolCallsExecuted, NewEvidenceCount: newEvidence);
    }

    private static string BuildBaselineIncompleteMessage(LlmAgentSessionState sessionState)
    {
        var missing = new List<string>();
        LlmAgentPlannedToolCall? nextCall = null;
        foreach (var item in LlmAgentBaselinePolicy.RequiredBaseline)
        {
            var entry = sessionState.Evidence.TryGetLatestByTag(item.Tag);
            if (entry != null && !entry.ToolWasError)
            {
                continue;
            }

            missing.Add(item.Tag);
            nextCall ??= item.PlannedCall;
        }

        var next = nextCall == null
            ? string.Empty
            : $" Suggested next: {nextCall.ToolName}({nextCall.ArgumentsJson}).";

        return $"(Baseline is incomplete and the model is not requesting tools. Missing: {string.Join(", ", missing)}.{next})";
    }

    private static string BuildEvidencePreview(string toolResult)
    {
        if (string.IsNullOrWhiteSpace(toolResult))
        {
            return string.Empty;
        }

        var normalized = toolResult.Replace("\r\n", "\n").Trim();
        if (normalized.Length <= 400)
        {
            return normalized;
        }

        return normalized[..400] + "...";
    }

    private static bool IsBaselineComplete(LlmAgentSessionState sessionState)
    {
        foreach (var item in LlmAgentBaselinePolicy.RequiredBaseline)
        {
            var entry = sessionState.Evidence.TryGetLatestByTag(item.Tag);
            if (entry == null || entry.ToolWasError)
            {
                return false;
            }
        }

        return true;
    }

    private static string TryGetLastUserPrompt(IReadOnlyList<ChatMessage> seedMessages)
    {
        for (var i = seedMessages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(seedMessages[i].Role, "user", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(seedMessages[i].Content))
            {
                return seedMessages[i].Content;
            }
        }

        return string.Empty;
    }

    private static List<ChatMessage> ApplyCheckpointPrune(List<ChatMessage> messages, string checkpointJson)
    {
        const int tailKeep = 12;

        var system = messages.FirstOrDefault(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));
        var runtimeContext = messages.Skip(1).FirstOrDefault(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) &&
            (m.Content?.StartsWith("CLI runtime context", StringComparison.OrdinalIgnoreCase) ?? false));

        var tail = messages.TakeLast(tailKeep).Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)).ToList();

        var pruned = new List<ChatMessage>();
        if (system != null)
        {
            pruned.Add(system);
        }

        if (runtimeContext != null)
        {
            pruned.Add(runtimeContext);
        }

        pruned.Add(new ChatMessage("system", "INTERNAL CHECKPOINT (machine-readable JSON, authoritative):\n" + checkpointJson));
        pruned.AddRange(tail);
        return pruned;
    }

    private static string BuildToolCacheKey(ChatToolCall toolCall)
    {
        if (toolCall == null)
        {
            return string.Empty;
        }

        // Prefer name-specific normalization for stability (addresses and command whitespace/case vary in practice).
        if (string.Equals(toolCall.Name, "exec", StringComparison.OrdinalIgnoreCase))
        {
            var command = TryReadJsonStringProperty(toolCall.ArgumentsJson, "command");
            if (!string.IsNullOrWhiteSpace(command))
            {
                return $"exec:{NormalizeDebuggerCommand(command)}";
            }
        }

        if (string.Equals(toolCall.Name, "analyze", StringComparison.OrdinalIgnoreCase))
        {
            var kind = TryReadJsonStringProperty(toolCall.ArgumentsJson, "kind");
            if (!string.IsNullOrWhiteSpace(kind))
            {
                return $"analyze:{kind.Trim().ToLowerInvariant()}";
            }
        }

        // Fallback: canonical JSON for stable equality.
        return $"{toolCall.Name.Trim().ToLowerInvariant()}:{CanonicalizeJson(toolCall.ArgumentsJson)}";
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

    private static string? TryReadJsonStringProperty(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return value.GetString();
        }
        catch
        {
            return null;
        }
    }

    private static string CanonicalizeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var buffer = new ArrayBufferWriter<byte>(json.Length);
            using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
            WriteCanonicalJson(doc.RootElement, writer);
            writer.Flush();
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch
        {
            return json.Trim();
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

    private static string TruncateForModel(string text, int maxChars)
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
}

internal sealed record LlmAgentRunResult(string FinalText, int Iterations, int ToolCallsExecuted);
