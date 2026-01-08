using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

public class LlmAgentRunnerTests
{
    [Fact]
    public async Task RunAsync_WhenSeedMessagesNull_Throws()
    {
        var state = LlmAgentSessionStateStore.GetOrCreate("server", "session", "dump");
        var runner = new LlmAgentRunner(
            (_, _) => Task.FromResult(new ChatCompletionResult { Text = "ok" }),
            (_, _) => Task.FromResult("ok"),
            state);

        _ = await Assert.ThrowsAsync<ArgumentNullException>(() => runner.RunAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_WhenNoTextAndNoToolCalls_ReturnsPlaceholder()
    {
        Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> _, CancellationToken __)
            => Task.FromResult(new ChatCompletionResult { Text = null, ToolCalls = [] });

        var state = LlmAgentSessionStateStore.GetOrCreate("server2", "session2", "dump2");
        var runner = new LlmAgentRunner(CompleteAsync, (_, _) => Task.FromResult("ok"), state, maxIterations: 5);
        var result = await runner.RunAsync([new ChatMessage("user", "hi")], CancellationToken.None);

        Assert.Equal("(LLM returned no content)", result.FinalText);
        Assert.Equal(1, result.Iterations);
        Assert.Equal(0, result.ToolCallsExecuted);
    }

    [Fact]
    public async Task RunAsync_ExecutesToolCallsAndReturnsFinalText()
    {
        var state = LlmAgentSessionStateStore.GetOrCreate("server3", "session3", "dump3");
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

        var runner = new LlmAgentRunner(CompleteAsync, ExecuteToolAsync, state, maxIterations: 5);
        var result = await runner.RunAsync([new ChatMessage("user", "hi")], CancellationToken.None);

        Assert.Equal("Done", result.FinalText);
        Assert.Equal(2, result.Iterations);
        Assert.Single(calls);
        Assert.Equal("exec", calls[0].Name);
        Assert.NotNull(state.LastCheckpointJson);
    }

    [Fact]
    public async Task RunAsync_StopsAfterMaxIterations()
    {
        var state = LlmAgentSessionStateStore.GetOrCreate("server4", "session4", "dump4");
        Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> _, CancellationToken __)
            => Task.FromResult(new ChatCompletionResult
            {
                Text = "loop",
                ToolCalls = [new ChatToolCall("c1", "exec", "{\"command\":\"k\"}")]
            });

        Task<string> ExecuteToolAsync(ChatToolCall _, CancellationToken __) => Task.FromResult("ok");

        var runner = new LlmAgentRunner(CompleteAsync, ExecuteToolAsync, state, maxIterations: 2);
        var result = await runner.RunAsync([new ChatMessage("user", "hi")], CancellationToken.None);

        Assert.Contains("stopped after 2 steps", result.FinalText);
        Assert.Equal(2, result.Iterations);
        Assert.Equal(2, result.ToolCallsExecuted);
    }

    [Fact]
    public async Task RunAsync_WhenToolCallRepeated_DoesNotCache_AndUpdatesEvidenceSeenCount()
    {
        var state = LlmAgentSessionStateStore.GetOrCreate("server5", "session5", "dump5");
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
            Assert.Contains(messages, m => m.Role == "tool" && m.ToolCallId == "c2" && m.Content.Contains("bt-output", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new ChatCompletionResult { Text = "Done", ToolCalls = [] });
        }

        Task<string> ExecuteToolAsync(ChatToolCall call, CancellationToken _)
        {
            executed.Add(call);
            return Task.FromResult("bt-output");
        }

        var runner = new LlmAgentRunner(CompleteAsync, ExecuteToolAsync, state, maxIterations: 5);
        var result = await runner.RunAsync([new ChatMessage("user", "hi")], CancellationToken.None);

        Assert.Equal("Done", result.FinalText);
        Assert.Equal(2, result.Iterations);
        Assert.Equal(2, executed.Count);
        Assert.Equal(2, result.ToolCallsExecuted);

        var entries = state.Evidence.Entries;
        Assert.Single(entries);
        Assert.Equal(2, entries[0].SeenCount);
    }

    [Fact]
    public async Task RunAsync_TruncatesLargeToolOutput_ForModel()
    {
        var state = LlmAgentSessionStateStore.GetOrCreate("server6", "session6", "dump6");
        var completions = 0;

        Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken __)
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

        var runner = new LlmAgentRunner(CompleteAsync, ExecuteToolAsync, state, maxIterations: 5, maxToolResultChars: 600);
        var result = await runner.RunAsync([new ChatMessage("user", "hi")], CancellationToken.None);

        Assert.Equal("Done", result.FinalText);
    }

    [Fact]
    public async Task RunAsync_WhenNonSpecialToolArgsHaveDifferentPropertyOrder_UsesCanonicalJsonForEvidenceKey()
    {
        var completions = 0;
        var state = LlmAgentSessionStateStore.GetOrCreate("server7", "session7", "dump7");
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
            Assert.Contains(messages, m => m.Role == "tool" && m.ToolCallId == "c2" && m.Content.Contains("tool-output", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new ChatCompletionResult { Text = "Done", ToolCalls = [] });
        }

        Task<string> ExecuteToolAsync(ChatToolCall call, CancellationToken _)
        {
            executed.Add(call);
            return Task.FromResult("tool-output");
        }

        var runner = new LlmAgentRunner(CompleteAsync, ExecuteToolAsync, state, maxIterations: 5);
        var result = await runner.RunAsync([new ChatMessage("user", "hi")], CancellationToken.None);

        Assert.Equal("Done", result.FinalText);
        Assert.Equal(2, executed.Count);
        Assert.Equal(2, result.ToolCallsExecuted);

        var entries = state.Evidence.Entries;
        Assert.Single(entries);
        Assert.Equal(2, entries[0].SeenCount);
    }

    [Fact]
    public async Task RunAsync_WhenToolArgsAreArrays_CanonicalizesWhitespaceForEvidenceKey()
    {
        var state = LlmAgentSessionStateStore.GetOrCreate("server8", "session8", "dump8");
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
            Assert.Contains(messages, m => m.Role == "tool" && m.ToolCallId == "c2");
            return Task.FromResult(new ChatCompletionResult { Text = "Done", ToolCalls = [] });
        }

        Task<string> ExecuteToolAsync(ChatToolCall _, CancellationToken __)
        {
            executed++;
            return Task.FromResult("tool-output");
        }

        var runner = new LlmAgentRunner(CompleteAsync, ExecuteToolAsync, state, maxIterations: 5);
        var result = await runner.RunAsync([new ChatMessage("user", "hi")], CancellationToken.None);

        Assert.Equal("Done", result.FinalText);
        Assert.Equal(2, executed);

        var entries = state.Evidence.Entries;
        Assert.Single(entries);
        Assert.Equal(2, entries[0].SeenCount);
    }

    [Fact]
    public async Task RunAsync_WhenAgentLoopsWithoutNewEvidence_ReturnsGuidance()
    {
        var state = LlmAgentSessionStateStore.GetOrCreate("server9", "session9", "dump9");
        Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> _, CancellationToken __)
            => Task.FromResult(new ChatCompletionResult
            {
                Text = "loop",
                ToolCalls = [new ChatToolCall(Guid.NewGuid().ToString("N"), "report_get", "{\"path\":\"metadata\"}")]
            });

        var executed = 0;
        Task<string> ExecuteToolAsync(ChatToolCall _, CancellationToken __)
        {
            executed++;
            return Task.FromResult("""{ "path":"metadata","value":{"dumpId":"d","generatedAt":"t"} }""");
        }

        var runner = new LlmAgentRunner(CompleteAsync, ExecuteToolAsync, state, maxIterations: 50);
        var result = await runner.RunAsync([new ChatMessage("user", "analyze this crash")], CancellationToken.None);

        Assert.Contains("stuck", result.FinalText, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Iterations < 50);
        Assert.True(executed > 0);
        Assert.False(string.IsNullOrWhiteSpace(state.LastCheckpointJson));
    }

    [Fact]
    public async Task RunAsync_WhenConclusionPromptAndBaselineMissing_DoesNotReturnConclusion()
    {
        var state = LlmAgentSessionStateStore.GetOrCreate("server10", "session10", "dump10");

        Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> _, CancellationToken __)
            => Task.FromResult(new ChatCompletionResult { Text = "Here is my conclusion.", ToolCalls = [] });

        var runner = new LlmAgentRunner(
            CompleteAsync,
            (_, _) => Task.FromResult("ok"),
            state,
            maxIterations: 5);

        var result = await runner.RunAsync([new ChatMessage("user", "analyze this crash")], CancellationToken.None);

        Assert.Contains("Baseline is incomplete", result.FinalText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result.ToolCallsExecuted);
    }
}
