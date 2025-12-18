using DebuggerMcp.Cli.Shell.Transcript;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Runs an iterative tool-calling loop for the CLI <c>llm</c> command agent mode.
/// </summary>
internal sealed class LlmAgentRunner(
    Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<ChatCompletionResult>> completeAsync,
    Func<ChatToolCall, CancellationToken, Task<string>> executeToolAsync,
    int maxIterations = 10)
{
    private readonly Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<ChatCompletionResult>> _completeAsync =
        completeAsync ?? throw new ArgumentNullException(nameof(completeAsync));

    private readonly Func<ChatToolCall, CancellationToken, Task<string>> _executeToolAsync =
        executeToolAsync ?? throw new ArgumentNullException(nameof(executeToolAsync));

    private readonly int _maxIterations = maxIterations <= 0 ? 10 : maxIterations;

    public async Task<LlmAgentRunResult> RunAsync(IReadOnlyList<ChatMessage> seedMessages, CancellationToken cancellationToken)
    {
        if (seedMessages == null)
        {
            throw new ArgumentNullException(nameof(seedMessages));
        }

        var messages = new List<ChatMessage>(seedMessages);
        var finalText = string.Empty;
        var toolCallsExecuted = 0;

        for (var iteration = 1; iteration <= _maxIterations; iteration++)
        {
            var completion = await _completeAsync(messages, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(completion.Text))
            {
                finalText = completion.Text.Trim();
            }

            if (completion.ToolCalls.Count == 0)
            {
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

            foreach (var toolCall in completion.ToolCalls)
            {
                var toolResult = await _executeToolAsync(toolCall, cancellationToken).ConfigureAwait(false);
                toolCallsExecuted++;
                // Defense-in-depth: redact sensitive values before sending tool output to the model.
                toolResult = TranscriptRedactor.RedactText(toolResult);
                messages.Add(new ChatMessage("tool", toolResult, toolCall.Id, toolCalls: null));
            }
        }

        return new LlmAgentRunResult(
            FinalText: string.IsNullOrWhiteSpace(finalText)
                ? $"(LLM agent stopped after {_maxIterations} steps without a final answer)"
                : $"(LLM agent stopped after {_maxIterations} steps)\n{finalText}",
            Iterations: _maxIterations,
            ToolCallsExecuted: toolCallsExecuted);
    }
}

internal sealed record LlmAgentRunResult(string FinalText, int Iterations, int ToolCallsExecuted);
