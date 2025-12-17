using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

[Collection("NonParallelConsole")]
public class LlmPromptComposerTests
{
    [Fact]
    public void InsertUserAttachmentsBeforeLastUserMessage_InsertsBeforeLastUser()
    {
        var messages = new List<ChatMessage>
        {
            new("system", "sys"),
            new("system", "ctx"),
            new("user", "prior question"),
            new("assistant", "prior answer"),
            new("user", "current prompt")
        };

        var result = LlmPromptComposer.InsertUserAttachmentsBeforeLastUserMessage(
            messages,
            ["Attached file: a.txt", "Attached file: b.txt"]);

        Assert.Equal(7, result.Count);
        Assert.Equal("current prompt", result[^1].Content);
        Assert.Equal("user", result[^2].Role);
        Assert.Equal("Attached file: b.txt", result[^2].Content);
        Assert.Equal("user", result[^3].Role);
        Assert.Equal("Attached file: a.txt", result[^3].Content);
    }

    [Fact]
    public void InsertUserAttachmentsBeforeLastUserMessage_AppendsWhenNoUserMessages()
    {
        var messages = new List<ChatMessage>
        {
            new("system", "sys"),
            new("assistant", "hello")
        };

        var result = LlmPromptComposer.InsertUserAttachmentsBeforeLastUserMessage(
            messages,
            ["Attached file: a.txt"]);

        Assert.Equal(3, result.Count);
        Assert.Equal("user", result[^1].Role);
        Assert.Equal("Attached file: a.txt", result[^1].Content);
    }

    [Fact]
    public void InsertUserAttachmentsBeforeLastUserMessage_SkipsWhitespaceAttachments()
    {
        var messages = new List<ChatMessage>
        {
            new("user", "current prompt")
        };

        var result = LlmPromptComposer.InsertUserAttachmentsBeforeLastUserMessage(
            messages,
            [" ", "\n", "Attached file: a.txt"]);

        Assert.Equal(2, result.Count);
        Assert.Equal("Attached file: a.txt", result[0].Content);
        Assert.Equal("current prompt", result[1].Content);
    }
}

