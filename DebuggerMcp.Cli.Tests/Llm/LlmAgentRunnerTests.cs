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

        Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken _)
        {
            completions++;
            if (completions == 1)
            {
                return Task.FromResult(new ChatCompletionResult
                {
                    Text = "Investigating...",
                    ToolCalls = [new ChatToolCall("c1", "exec", "{\"command\":\"bt\"}")]
                });
            }

            // After tool result is injected, stop.
            Assert.Contains(messages, m => m.Role == "tool" && m.ToolCallId == "c1");
            return Task.FromResult(new ChatCompletionResult { Text = "Done", ToolCalls = [] });
        }

        Task<string> ExecuteToolAsync(ChatToolCall call, CancellationToken _)
        {
            calls.Add(call);
            return Task.FromResult("bt-output");
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
