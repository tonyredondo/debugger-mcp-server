using DebuggerMcp.Cli.Llm;
using DebuggerMcp.Cli.Shell.Transcript;

namespace DebuggerMcp.Cli.Tests.Llm;

public class TranscriptContextBuilderTests
{
    [Fact]
    public void BuildMessages_IncludesSystemAndPromptAndPriorConversation()
    {
        var tail = new List<CliTranscriptEntry>
        {
            new() { Kind = "cli_command", Text = "status", Output = "Connected" },
            new() { Kind = "llm_tool", Text = "exec bt", Output = "bt-output\n```\ninside\n```" },
            new() { Kind = "llm_user", Text = "What is this?" },
            new() { Kind = "llm_assistant", Text = "An answer." }
        };

        var messages = TranscriptContextBuilder.BuildMessages(
            userPrompt: "Next question",
            serverUrl: "http://localhost:5000",
            sessionId: "s1",
            dumpId: null,
            transcriptTail: tail,
            maxContextChars: 10_000,
            agentModeEnabled: false,
            agentConfirmationEnabled: true);

        Assert.True(messages.Count >= 4);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[1].Role);
        Assert.Contains("untrusted", messages[1].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exec bt", messages[1].Content);
        Assert.Contains("bt-output", messages[1].Content);
        // Should not be broken by embedded triple-backticks.
        Assert.Contains("````", messages[1].Content);
        Assert.Equal("user", messages[2].Role);
        Assert.Equal("assistant", messages[3].Role);
        Assert.Equal("Next question", messages[^1].Content);
    }

    [Fact]
    public void BuildMessages_WhenAgentModeEnabled_IncludesAgentPrompting()
    {
        var messages = TranscriptContextBuilder.BuildMessages(
            userPrompt: "x",
            serverUrl: "http://localhost:5000",
            sessionId: "s1",
            dumpId: "d1",
            transcriptTail: [],
            maxContextChars: 10_000,
            agentModeEnabled: true,
            agentConfirmationEnabled: true);

        Assert.Equal("system", messages[0].Role);
        Assert.Contains("Agent mode is enabled", messages[0].Content);
        Assert.Contains("confirm", messages[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tooling:", messages[0].Content);
        Assert.Contains("CALL THE TOOL", messages[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not format tool commands as bash", messages[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prefer this over exec \"sos dumpobj\"", messages[0].Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildMessages_DedupesLastUserMessageAfterRedaction()
    {
        // Simulate the CLI storing a redacted llm_user message before building messages.
        var tail = new List<CliTranscriptEntry>
        {
            new() { Kind = "llm_user", Text = "apiKey=***" }
        };

        var messages = TranscriptContextBuilder.BuildMessages(
            userPrompt: "apiKey=secret",
            serverUrl: "http://localhost:5000",
            sessionId: "s1",
            dumpId: "d1",
            transcriptTail: tail,
            maxContextChars: 10_000,
            agentModeEnabled: false,
            agentConfirmationEnabled: true);

        // Should not include the prior llm_user entry; only the new user prompt.
        Assert.Equal("user", messages[^1].Role);
        Assert.Equal("apiKey=secret", messages[^1].Content);
        Assert.DoesNotContain(messages, m => m.Role == "user" && m.Content == "apiKey=***");
    }

    [Fact]
    public void BuildMessages_WhenResetMarkerPresent_IgnoresEarlierTranscriptContext()
    {
        var tail = new List<CliTranscriptEntry>
        {
            new() { Kind = "cli_command", Text = "exec sos dumpasync -all", Output = "old-output" },
            new() { Kind = "llm_user", Text = "old question" },
            new() { Kind = "llm_assistant", Text = "old answer" },
            new() { Kind = "llm_reset", Text = "reset" },
            new() { Kind = "cli_command", Text = "exec sos dumpexceptions", Output = "new-output" },
            new() { Kind = "llm_user", Text = "new question" },
            new() { Kind = "llm_assistant", Text = "new answer" }
        };

        var messages = TranscriptContextBuilder.BuildMessages(
            userPrompt: "current prompt",
            serverUrl: "http://localhost:5000",
            sessionId: "s1",
            dumpId: "d1",
            transcriptTail: tail,
            maxContextChars: 10_000,
            agentModeEnabled: false,
            agentConfirmationEnabled: true);

        // CLI context should contain only the command after reset.
        Assert.Contains("dumpexceptions", messages[1].Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dumpasync", messages[1].Content, StringComparison.OrdinalIgnoreCase);

        // Prior conversation should be dropped.
        Assert.DoesNotContain(messages, m => m.Role == "user" && m.Content == "old question");
        Assert.DoesNotContain(messages, m => m.Role == "assistant" && m.Content == "old answer");

        // New conversation should remain.
        Assert.Contains(messages, m => m.Role == "user" && m.Content == "new question");
        Assert.Contains(messages, m => m.Role == "assistant" && m.Content == "new answer");
    }
}
