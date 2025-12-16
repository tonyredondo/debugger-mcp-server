using DebuggerMcp.Cli.Shell.Transcript;

namespace DebuggerMcp.Cli.Tests.Shell.Transcript;

public class TranscriptRedactorTests
{
    [Theory]
    [InlineData("llm set-key sk-123", "llm set-key ***")]
    [InlineData("llm   set-key sk-123", "llm set-key ***")]
    [InlineData("llm\tset-key\tsk-123", "llm set-key ***")]
    [InlineData("llm my key is sk-123", "llm ***")]
    [InlineData("llm\tmy key is sk-123", "llm ***")]
    [InlineData("llm Analyze #./report.json", "llm ***")]
    [InlineData("llm model openai/gpt-4o-mini", "llm model openai/gpt-4o-mini")]
    [InlineData("llm\tmodel openai/gpt-4o-mini", "llm\tmodel openai/gpt-4o-mini")]
    [InlineData("llm set-agent true", "llm set-agent true")]
    [InlineData("llm\tset-agent true", "llm\tset-agent true")]
    [InlineData("llm set-agent-confirm false", "llm set-agent-confirm false")]
    [InlineData("llm\tset-agent-confirm false", "llm\tset-agent-confirm false")]
    [InlineData("llm reset", "llm reset")]
    [InlineData("connect http://localhost:5000 -k abc", "connect http://localhost:5000 -k ***")]
    [InlineData("connect http://localhost:5000 --api-key abc", "connect http://localhost:5000 --api-key ***")]
    public void RedactCommand_RedactsCommonSecretArguments(string input, string expected)
    {
        var redacted = TranscriptRedactor.RedactCommand(input);
        Assert.Equal(expected, redacted);
    }

    [Theory]
    [InlineData("Authorization: Bearer abc.def", "Authorization: Bearer ***")]
    [InlineData("OPENROUTER_API_KEY=sk-123", "OPENROUTER_API_KEY=***")]
    [InlineData("apiKey: abc", "apiKey=***")]
    [InlineData("{\"apiKey\":\"abc\"}", "{\"apiKey\":\"***\"}")]
    public void RedactText_RedactsKeyValueSecrets(string input, string expected)
    {
        var redacted = TranscriptRedactor.RedactText(input);
        Assert.Equal(expected, redacted);
    }
}
