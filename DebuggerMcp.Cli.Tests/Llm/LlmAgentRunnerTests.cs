using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

public class LlmAgentRunnerTests
{
    [Fact]
    public async Task RunAsync_WhenSeedMessagesNull_Throws()
    {
        var runner = new LlmAgentRunner(
            (_, _) => Task.FromResult(new ChatCompletionResult { Text = "ok" }),
            (_, _) => Task.FromResult("ok"));

        _ = await Assert.ThrowsAsync<ArgumentNullException>(() => runner.RunAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_WhenNoTextAndNoToolCalls_ReturnsPlaceholder()
    {
        Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> _, CancellationToken __)
            => Task.FromResult(new ChatCompletionResult { Text = null, ToolCalls = [] });

        var runner = new LlmAgentRunner(CompleteAsync, (_, _) => Task.FromResult("ok"), maxIterations: 5);
        var result = await runner.RunAsync([new ChatMessage("user", "hi")], CancellationToken.None);

        Assert.Equal("(LLM returned no content)", result.FinalText);
        Assert.Equal(1, result.Iterations);
        Assert.Equal(0, result.ToolCallsExecuted);
    }

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
        Assert.Equal(1, result.ToolCallsExecuted);
    }

    [Fact]
    public async Task RunAsync_WhenToolCallRepeated_ReusesCachedToolResult()
    {
        var executed = new List<ChatToolCall>();
        var completions = 0;

        Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken _)
        {
            completions++;
            if (completions == 1)
            {
                return Task.FromResult(new ChatCompletionResult
                {
                    Text = "Investigating...",
                    ToolCalls =
                    [
                        new ChatToolCall("c1", "exec", "{\"command\":\"  BT  \"}"),
                        new ChatToolCall("c2", "exec", "{\"command\":\"bt\"}")
                    ]
                });
            }

            Assert.Contains(messages, m => m.Role == "tool" && m.ToolCallId == "c1" && m.Content.Contains("bt-output", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(messages, m => m.Role == "tool" && m.ToolCallId == "c2" && m.Content.Contains("cached tool result", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new ChatCompletionResult { Text = "Done", ToolCalls = [] });
        }

        Task<string> ExecuteToolAsync(ChatToolCall call, CancellationToken _)
        {
            executed.Add(call);
            return Task.FromResult("bt-output");
        }

        var runner = new LlmAgentRunner(CompleteAsync, ExecuteToolAsync, maxIterations: 5);
        var result = await runner.RunAsync([new ChatMessage("user", "hi")], CancellationToken.None);

        Assert.Equal("Done", result.FinalText);
        Assert.Equal(2, result.Iterations);
        Assert.Single(executed);
        Assert.Equal(1, result.ToolCallsExecuted);
    }

    [Fact]
    public async Task RunAsync_WhenAgentRepeatsOnlyCachedToolCalls_StopsEarly()
    {
        Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> _, CancellationToken __)
            => Task.FromResult(new ChatCompletionResult
            {
                Text = "loop",
                ToolCalls = [new ChatToolCall(Guid.NewGuid().ToString("N"), "exec", "{\"command\":\"bt\"}")]
            });

        var executed = 0;
        Task<string> ExecuteToolAsync(ChatToolCall _, CancellationToken __)
        {
            executed++;
            return Task.FromResult("bt-output");
        }

        var runner = new LlmAgentRunner(CompleteAsync, ExecuteToolAsync, maxIterations: 20);
        var result = await runner.RunAsync([new ChatMessage("user", "hi")], CancellationToken.None);

        Assert.Contains("stuck", result.FinalText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, executed);
        Assert.Equal(1, result.ToolCallsExecuted);
        Assert.Equal(3, result.Iterations);
    }

    [Fact]
    public async Task RunAsync_WhenAnalyzeToolCallRepeated_ReusesCachedToolResult()
    {
        var executed = 0;
        var completions = 0;

        Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken _)
        {
            completions++;
            if (completions == 1)
            {
                return Task.FromResult(new ChatCompletionResult
                {
                    Text = "Investigating...",
                    ToolCalls =
                    [
                        new ChatToolCall("c1", "analyze", "{\"kind\":\"crash\"}"),
                        new ChatToolCall("c2", "analyze", "{\"kind\":\"CRASH\"}")
                    ]
                });
            }

            Assert.Contains(messages, m => m.Role == "tool" && m.ToolCallId == "c1" && m.Content.Contains("analysis-output", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(messages, m => m.Role == "tool" && m.ToolCallId == "c2" && m.Content.Contains("cached tool result", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new ChatCompletionResult { Text = "Done", ToolCalls = [] });
        }

        Task<string> ExecuteToolAsync(ChatToolCall _, CancellationToken __)
        {
            executed++;
            return Task.FromResult("analysis-output");
        }

        var runner = new LlmAgentRunner(CompleteAsync, ExecuteToolAsync, maxIterations: 10);
        var result = await runner.RunAsync([new ChatMessage("user", "hi")], CancellationToken.None);

        Assert.Equal("Done", result.FinalText);
        Assert.Equal(2, result.Iterations);
        Assert.Equal(1, executed);
        Assert.Equal(1, result.ToolCallsExecuted);
    }

    [Fact]
    public async Task RunAsync_TruncatesLargeToolOutput_ForModel()
    {
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

            Assert.Contains(messages, m => m.Role == "tool" && m.ToolCallId == "c1" && m.Content.Contains("[truncated, total", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new ChatCompletionResult { Text = "Done", ToolCalls = [] });
        }

        Task<string> ExecuteToolAsync(ChatToolCall _, CancellationToken __)
            => Task.FromResult(new string('x', 5000));

        var runner = new LlmAgentRunner(CompleteAsync, ExecuteToolAsync, maxIterations: 5, maxToolResultChars: 600);
        var result = await runner.RunAsync([new ChatMessage("user", "hi")], CancellationToken.None);

        Assert.Equal("Done", result.FinalText);
    }

    [Fact]
    public async Task RunAsync_WhenNonSpecialToolArgsHaveDifferentPropertyOrder_UsesCanonicalJsonForCacheKey()
    {
        var completions = 0;
        var executed = new List<ChatToolCall>();

        Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken _)
        {
            completions++;
            if (completions == 1)
            {
                return Task.FromResult(new ChatCompletionResult
                {
                    Text = "Investigating...",
                    ToolCalls =
                    [
                        new ChatToolCall("c1", "inspect", "{\"address\":\"0x1234\",\"depth\":2}"),
                        new ChatToolCall("c2", "inspect", "{\"depth\":2,\"address\":\"0x1234\"}")
                    ]
                });
            }

            Assert.Contains(messages, m => m.Role == "tool" && m.ToolCallId == "c1" && m.Content.Contains("tool-output", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(messages, m => m.Role == "tool" && m.ToolCallId == "c2" && m.Content.Contains("cached tool result", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new ChatCompletionResult { Text = "Done", ToolCalls = [] });
        }

        Task<string> ExecuteToolAsync(ChatToolCall call, CancellationToken _)
        {
            executed.Add(call);
            return Task.FromResult("tool-output");
        }

        var runner = new LlmAgentRunner(CompleteAsync, ExecuteToolAsync, maxIterations: 5);
        var result = await runner.RunAsync([new ChatMessage("user", "hi")], CancellationToken.None);

        Assert.Equal("Done", result.FinalText);
        Assert.Single(executed);
        Assert.Equal(1, result.ToolCallsExecuted);
    }

    [Fact]
    public async Task RunAsync_WhenToolArgsAreArrays_CanonicalizesWhitespaceForCacheKey()
    {
        var completions = 0;
        var executed = 0;

        Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken _)
        {
            completions++;
            if (completions == 1)
            {
                return Task.FromResult(new ChatCompletionResult
                {
                    Text = "Investigating...",
                    ToolCalls =
                    [
                        new ChatToolCall("c1", "custom", "[1,2]"),
                        new ChatToolCall("c2", "custom", "[ 1, 2 ]")
                    ]
                });
            }

            Assert.Contains(messages, m => m.Role == "tool" && m.ToolCallId == "c1");
            Assert.Contains(messages, m => m.Role == "tool" && m.ToolCallId == "c2" && m.Content.Contains("cached tool result", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new ChatCompletionResult { Text = "Done", ToolCalls = [] });
        }

        Task<string> ExecuteToolAsync(ChatToolCall _, CancellationToken __)
        {
            executed++;
            return Task.FromResult("tool-output");
        }

        var runner = new LlmAgentRunner(CompleteAsync, ExecuteToolAsync, maxIterations: 5);
        var result = await runner.RunAsync([new ChatMessage("user", "hi")], CancellationToken.None);

        Assert.Equal("Done", result.FinalText);
        Assert.Equal(1, executed);
        Assert.Equal(1, result.ToolCallsExecuted);
    }
}
