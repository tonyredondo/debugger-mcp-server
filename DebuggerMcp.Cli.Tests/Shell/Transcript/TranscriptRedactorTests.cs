using DebuggerMcp.Cli.Shell.Transcript;

namespace DebuggerMcp.Cli.Tests.Shell.Transcript;

public class TranscriptRedactorTests
{
    [Theory]
    [InlineData("llm set-key sk-123", "llm set-key ***")]
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
    public void RedactText_RedactsKeyValueSecrets(string input, string expected)
    {
        var redacted = TranscriptRedactor.RedactText(input);
        Assert.Equal(expected, redacted);
    }
}

