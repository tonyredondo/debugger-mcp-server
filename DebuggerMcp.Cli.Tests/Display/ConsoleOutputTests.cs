using DebuggerMcp.Cli.Display;
using Spectre.Console.Testing;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Display;

/// <summary>
/// Tests for <see cref="ConsoleOutput"/>.
/// </summary>
public class ConsoleOutputTests
{
    [Fact]
    public void Success_WritesSuccessPrefixAndMessage()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        output.Success("ok");

        Assert.Contains("ok", console.Output);
    }

    [Fact]
    public void Error_WritesErrorPrefixAndMessage()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        output.Error("bad");

        Assert.Contains("Error:", console.Output);
        Assert.Contains("bad", console.Output);
    }

    [Fact]
    public void Error_WithException_WhenNotVerbose_WritesExceptionMessage()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console) { Verbose = false };

        output.Error("bad", new InvalidOperationException("details"));

        Assert.Contains("Error:", console.Output);
        Assert.Contains("bad", console.Output);
        Assert.Contains("details", console.Output);
    }

    [Fact]
    public void KeyValue_WhenValueNull_WritesNotSetPlaceholder()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        output.KeyValue("Key", null);

        Assert.Contains("Key", console.Output);
        Assert.Contains("(not set)", console.Output);
    }

    [Fact]
    public void Header_WritesTitle()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        output.Header("Title");

        Assert.Contains("Title", console.Output);
    }

    [Fact]
    public void BeginTranscriptCapture_CapturesEmittedMessages()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var captured = new List<string>();
        using (output.BeginTranscriptCapture(captured.Add))
        {
            output.Success("ok");
            output.KeyValue("Key", "Value");
            output.Markup("[green]hello[/] [[x]]");
        }

        Assert.Contains(captured, line => line.Contains("OK:", StringComparison.Ordinal));
        Assert.Contains(captured, line => line.Contains("Key: Value", StringComparison.Ordinal));
        Assert.Contains(captured, line => line.Contains("hello", StringComparison.Ordinal));
        Assert.Contains(captured, line => line.Contains("[x]", StringComparison.Ordinal));
    }

    [Fact]
    public void PromptLlmAgentToolApproval_RedactsSecretsInTranscriptAndPrompt()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var captured = new List<string>();
        using (output.BeginTranscriptCapture(captured.Add))
        {
            var decision = output.PromptLlmAgentToolApproval("exec", "apiKey=secret");
            Assert.Equal(DebuggerMcp.Cli.Llm.LlmAgentToolApprovalDecision.DenyOnce, decision);
        }

        Assert.DoesNotContain(captured, line => line.Contains("secret", StringComparison.Ordinal));
        Assert.Contains(captured, line => line.Contains("apiKey=***", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("secret", console.Output, StringComparison.Ordinal);
        Assert.Contains("apiKey=***", console.Output, StringComparison.OrdinalIgnoreCase);
    }
}
