using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

public class LlmAgentRunnerTests
{
    [Fact]
    public async Task RunAsync_ExecutesToolCallsAndReturnsFinalText()
    {
        var calls = new List<ChatToolCall>();
        var completions = 0;
        using var contentDoc = System.Text.Json.JsonDocument.Parse("""[{ "type":"thought", "thought_signature":"sig1", "text":"x" }]""");
        using var providerDoc = System.Text.Json.JsonDocument.Parse("""{ "thought_signature":"sig1", "text":"x" }""");

        Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken _)
        {
            completions++;
            if (completions == 1)
            {
                return Task.FromResult(new ChatCompletionResult
                {
                    Text = "Investigating...",
                    RawMessageContent = contentDoc.RootElement.Clone(),
                    ProviderMessageFields = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        ["reasoning"] = providerDoc.RootElement.Clone()
                    },
                    ToolCalls = [new ChatToolCall("c1", "exec", "{\"command\":\"bt\"}")]
                });
            }

            // After tool result is injected, stop.
            Assert.Contains(messages, m => m.Role == "tool" && m.ToolCallId == "c1" && m.Content.Contains("apiKey=***", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(messages, m =>
                m.Role == "assistant" &&
                m.ProviderMessageFields != null &&
                m.ProviderMessageFields.ContainsKey("reasoning") &&
                m.ContentJson.HasValue);
            return Task.FromResult(new ChatCompletionResult { Text = "Done", ToolCalls = [] });
        }

        Task<string> ExecuteToolAsync(ChatToolCall call, CancellationToken _)
        {
            calls.Add(call);
            return Task.FromResult("apiKey=secret\nbt-output");
        }

        var runner = new LlmAgentRunner(CompleteAsync, ExecuteToolAsync, maxIterations: 5);
        var result = await runner.RunAsync([new ChatMessage("user", "hi")], CancellationToken.None);

        Assert.Equal("Done", result.FinalText);
        Assert.Equal(2, result.Iterations);
        Assert.Single(calls);
        Assert.Equal("exec", calls[0].Name);
    }

    [Fact]
    public async Task RunAsync_StopsAfterMaxIterations()
    {
        Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> _, CancellationToken __)
            => Task.FromResult(new ChatCompletionResult
            {
                Text = "loop",
                ToolCalls = [new ChatToolCall("c1", "exec", "{\"command\":\"k\"}")]
            });

        Task<string> ExecuteToolAsync(ChatToolCall _, CancellationToken __) => Task.FromResult("ok");

        var runner = new LlmAgentRunner(CompleteAsync, ExecuteToolAsync, maxIterations: 2);
        var result = await runner.RunAsync([new ChatMessage("user", "hi")], CancellationToken.None);

        Assert.Contains("stopped after 2 steps", result.FinalText);
        Assert.Equal(2, result.Iterations);
        Assert.Equal(2, result.ToolCallsExecuted);
    }
}
