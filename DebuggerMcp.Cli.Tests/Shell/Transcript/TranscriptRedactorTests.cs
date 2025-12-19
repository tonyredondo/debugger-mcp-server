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
    [InlineData("llm Analyze @./report.json", "llm ***")]
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
    [InlineData("x-api-key: abc.def", "x-api-key=***")]
    [InlineData("OPENROUTER_API_KEY=sk-123", "OPENROUTER_API_KEY=***")]
    [InlineData("OPENAI_API_KEY=sk-123", "OPENAI_API_KEY=***")]
    [InlineData("ANTHROPIC_API_KEY=sk-ant-123", "ANTHROPIC_API_KEY=***")]
    [InlineData("DEBUGGER_MCP_OPENROUTER_API_KEY=sk-123", "DEBUGGER_MCP_OPENROUTER_API_KEY=***")]
    [InlineData("DEBUGGER_MCP_OPENAI_API_KEY=sk-123", "DEBUGGER_MCP_OPENAI_API_KEY=***")]
    [InlineData("DEBUGGER_MCP_ANTHROPIC_API_KEY=sk-ant-123", "DEBUGGER_MCP_ANTHROPIC_API_KEY=***")]
    [InlineData("apiKey: abc", "apiKey=***")]
    [InlineData("{\"apiKey\":\"abc\"}", "{\"apiKey\":\"***\"}")]
    [InlineData("{\"openai_api_key\":\"abc\"}", "{\"openai_api_key\":\"***\"}")]
    [InlineData("{\"anthropic_api_key\":\"abc\"}", "{\"anthropic_api_key\":\"***\"}")]
    [InlineData("{\"DEBUGGER_MCP_OPENAI_API_KEY\":\"abc\"}", "{\"DEBUGGER_MCP_OPENAI_API_KEY\":\"***\"}")]
    [InlineData("Incorrect API key provided: sk-123", "Incorrect API key provided: sk-***")]
    [InlineData("Invalid key: rk-live-abc123", "Invalid key: rk-***")]
    [InlineData("token=0x06000001", "token=0x06000001")]
    [InlineData("token=abc", "token=***")]
    [InlineData("{\"token\":\"0x06000001\"}", "{\"token\":\"0x06000001\"}")]
    [InlineData("{\"token\":\"abc\"}", "{\"token\":\"***\"}")]
    public void RedactText_RedactsKeyValueSecrets(string input, string expected)
    {
        var redacted = TranscriptRedactor.RedactText(input);
        Assert.Equal(expected, redacted);
    }
}
