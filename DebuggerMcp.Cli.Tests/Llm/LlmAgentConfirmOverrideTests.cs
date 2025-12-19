using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Llm;

namespace DebuggerMcp.Cli.Tests.Llm;

public class LlmAgentConfirmOverrideTests
{
    [Fact]
    public void Dispose_RestoresOriginalValue()
    {
        var settings = new LlmSettings
        {
            AgentModeConfirmToolCalls = true
        };

        using (new LlmAgentConfirmOverride(settings, confirmToolCalls: false))
        {
            Assert.False(settings.AgentModeConfirmToolCalls);
        }

        Assert.True(settings.AgentModeConfirmToolCalls);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var settings = new LlmSettings
        {
            AgentModeConfirmToolCalls = false
        };

        var scope = new LlmAgentConfirmOverride(settings, confirmToolCalls: true);
        Assert.True(settings.AgentModeConfirmToolCalls);

        scope.Dispose();
        scope.Dispose();

        Assert.False(settings.AgentModeConfirmToolCalls);
    }
}

