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
            new() { Kind = "llm_user", Text = "What is this?" },
            new() { Kind = "llm_assistant", Text = "An answer." }
        };

        var messages = TranscriptContextBuilder.BuildMessages(
            userPrompt: "Next question",
            serverUrl: "http://localhost:5000",
            sessionId: "s1",
            dumpId: null,
            transcriptTail: tail,
            maxContextChars: 10_000);

        Assert.True(messages.Count >= 4);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("system", messages[1].Role);
        Assert.Equal("user", messages[2].Role);
        Assert.Equal("assistant", messages[3].Role);
        Assert.Equal("Next question", messages[^1].Content);
    }
}

