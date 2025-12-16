using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

public class LlmAgentApprovalStateTests
{
    [Fact]
    public void IsAllowed_WhenConfirmationsDisabled_AlwaysTrue()
    {
        var state = new LlmAgentApprovalState(confirmationsEnabled: false);
        Assert.True(state.IsAllowed("exec"));
        Assert.True(state.IsAllowed("analyze"));
    }

    [Fact]
    public void ApplyDecision_AllowToolAlways_AllowsOnlyThatTool()
    {
        var state = new LlmAgentApprovalState(confirmationsEnabled: true);
        Assert.False(state.IsAllowed("exec"));

        state.ApplyDecision("exec", LlmAgentToolApprovalDecision.AllowToolAlways);

        Assert.True(state.IsAllowed("exec"));
        Assert.False(state.IsAllowed("analyze"));
    }

    [Fact]
    public void ApplyDecision_AllowAllAlways_AllowsAnyTool()
    {
        var state = new LlmAgentApprovalState(confirmationsEnabled: true);
        Assert.False(state.IsAllowed("exec"));

        state.ApplyDecision("exec", LlmAgentToolApprovalDecision.AllowAllAlways);

        Assert.True(state.IsAllowed("exec"));
        Assert.True(state.IsAllowed("analyze"));
        Assert.True(state.IsAllowed("inspect_object"));
    }
}

