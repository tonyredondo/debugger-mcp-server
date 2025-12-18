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

    [Fact]
    public void ApplyEnvironmentOverrides_ClearsEnvApiKeyWhenUnset()
    {
        var old = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "k1");
            var settings = new LlmSettings();
            settings.ApplyEnvironmentOverrides();
            Assert.Equal("k1", settings.GetEffectiveOpenRouterApiKey());

            Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", null);
            settings.ApplyEnvironmentOverrides();
            Assert.Null(settings.OpenRouterApiKeyFromEnvironment);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", old);
        }
    }

    [Fact]
    public void ApplyEnvironmentOverrides_WhenProviderOpenAi_LoadsApiKeyFromCodexAuth()
    {
        var oldProvider = Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER");
        var oldCodexPath = Environment.GetEnvironmentVariable("DEBUGGER_MCP_CODEX_AUTH_PATH");
        var oldOpenAi = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        var dir = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", nameof(LlmSettingsTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var authPath = Path.Combine(dir, "auth.json");

        try
        {
            File.WriteAllText(authPath, "{\"OPENAI_API_KEY\":\"k-codex\"}");

            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", "openai");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_CODEX_AUTH_PATH", authPath);

            var settings = new LlmSettings();
            settings.ApplyEnvironmentOverrides();

            Assert.Equal("k-codex", settings.GetEffectiveOpenAiApiKey());
            Assert.Equal("codex auth (override path)", settings.GetEffectiveApiKeySource());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", oldProvider);
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_CODEX_AUTH_PATH", oldCodexPath);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", oldOpenAi);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ApplyEnvironmentOverrides_WhenProviderOpenAi_EnvApiKeyOverridesCodexAuth()
    {
        var oldProvider = Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER");
        var oldCodexPath = Environment.GetEnvironmentVariable("DEBUGGER_MCP_CODEX_AUTH_PATH");
        var oldOpenAi = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        var dir = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", nameof(LlmSettingsTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var authPath = Path.Combine(dir, "auth.json");

        try
        {
            File.WriteAllText(authPath, "{\"OPENAI_API_KEY\":\"k-codex\"}");

            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", "openai");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "k-env");
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_CODEX_AUTH_PATH", authPath);

            var settings = new LlmSettings();
            settings.ApplyEnvironmentOverrides();

            Assert.Equal("k-env", settings.GetEffectiveOpenAiApiKey());
            Assert.Equal("env", settings.GetEffectiveApiKeySource());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", oldProvider);
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_CODEX_AUTH_PATH", oldCodexPath);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", oldOpenAi);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
