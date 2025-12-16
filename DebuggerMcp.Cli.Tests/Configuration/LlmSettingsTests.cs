using DebuggerMcp.Cli.Configuration;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Configuration;

[Collection("NonParallelConsole")]
public class LlmSettingsTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("on", true)]
    [InlineData("off", false)]
    [InlineData("yes", true)]
    [InlineData("no", false)]
    public void ApplyEnvironmentOverrides_ParsesAgentConfirmFlexibleValues(string value, bool expected)
    {
        var old = Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_AGENT_CONFIRM");
        try
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_AGENT_CONFIRM", value);
            var settings = new LlmSettings();
            settings.ApplyEnvironmentOverrides();
            Assert.Equal(expected, settings.AgentModeConfirmToolCalls);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_AGENT_CONFIRM", old);
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public void ApplyEnvironmentOverrides_ParsesAgentModeFlexibleValues(string value, bool expected)
    {
        var old = Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_AGENT_MODE");
        try
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_AGENT_MODE", value);
            var settings = new LlmSettings();
            settings.ApplyEnvironmentOverrides();
            Assert.Equal(expected, settings.AgentModeEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_AGENT_MODE", old);
        }
    }
}

