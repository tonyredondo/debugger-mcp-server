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
    public void GetEffectiveOpenAiApiKey_TrimsWhitespace()
    {
        var settings = new LlmSettings { Provider = "openai", OpenAiApiKey = "  k1  " };
        Assert.Equal("k1", settings.GetEffectiveOpenAiApiKey());
    }

    [Fact]
    public void ApplyEnvironmentOverrides_TrimsEnvApiKeys()
    {
        var old = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "  k2  ");
            var settings = new LlmSettings { Provider = "openai" };
            settings.ApplyEnvironmentOverrides();
            Assert.Equal("k2", settings.GetEffectiveOpenAiApiKey());
            Assert.Equal("env:OPENAI_API_KEY", settings.GetEffectiveApiKeySource());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", old);
        }
    }

    [Fact]
    public void ApplyEnvironmentOverrides_TrimsAnthropicEnvApiKeys()
    {
        var old = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "  k3  ");
            var settings = new LlmSettings { Provider = "anthropic" };
            settings.ApplyEnvironmentOverrides();
            Assert.Equal("k3", settings.GetEffectiveAnthropicApiKey());
            Assert.Equal("env:ANTHROPIC_API_KEY", settings.GetEffectiveApiKeySource());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", old);
        }
    }

    [Fact]
    public void ApplyEnvironmentOverrides_WhenProviderEnvVarSet_NormalizesProvider()
    {
        var old = Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER");
        try
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", " OpenAI ");
            var settings = new LlmSettings();
            settings.ApplyEnvironmentOverrides();
            Assert.Equal("openai", settings.Provider);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", old);
        }
    }

    [Fact]
    public void ApplyEnvironmentOverrides_WhenOpenRouterModelAndBaseUrlProvided_TrimsAndApplies()
    {
        var oldModel = Environment.GetEnvironmentVariable("OPENROUTER_MODEL");
        var oldBaseUrl = Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("OPENROUTER_MODEL", " openrouter/test ");
            Environment.SetEnvironmentVariable("OPENROUTER_BASE_URL", "https://example.openrouter/v1/");
            var settings = new LlmSettings { Provider = "openrouter" };
            settings.ApplyEnvironmentOverrides();
            Assert.Equal("openrouter/test", settings.OpenRouterModel);
            Assert.Equal("https://example.openrouter/v1", settings.OpenRouterBaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENROUTER_MODEL", oldModel);
            Environment.SetEnvironmentVariable("OPENROUTER_BASE_URL", oldBaseUrl);
        }
    }

    [Fact]
    public void ApplyEnvironmentOverrides_WhenOpenAiModelAndBaseUrlProvided_TrimsAndApplies()
    {
        var oldProvider = Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER");
        var oldModel = Environment.GetEnvironmentVariable("OPENAI_MODEL");
        var oldBaseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", "openai");
            Environment.SetEnvironmentVariable("OPENAI_MODEL", " gpt-test ");
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", "https://example.openai/v1/");
            var settings = new LlmSettings();
            settings.ApplyEnvironmentOverrides();
            Assert.Equal("openai", settings.Provider);
            Assert.Equal("gpt-test", settings.OpenAiModel);
            Assert.Equal("https://example.openai/v1", settings.OpenAiBaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", oldProvider);
            Environment.SetEnvironmentVariable("OPENAI_MODEL", oldModel);
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", oldBaseUrl);
        }
    }

    [Fact]
    public void ApplyEnvironmentOverrides_WhenAnthropicModelAndBaseUrlProvided_TrimsAndApplies()
    {
        var oldProvider = Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER");
        var oldModel = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL");
        var oldBaseUrl = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", "anthropic");
            Environment.SetEnvironmentVariable("ANTHROPIC_MODEL", " claude-test ");
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://example.anthropic/v1/");
            var settings = new LlmSettings();
            settings.ApplyEnvironmentOverrides();
            Assert.Equal("anthropic", settings.Provider);
            Assert.Equal("claude-test", settings.AnthropicModel);
            Assert.Equal("https://example.anthropic/v1", settings.AnthropicBaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", oldProvider);
            Environment.SetEnvironmentVariable("ANTHROPIC_MODEL", oldModel);
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", oldBaseUrl);
        }
    }

    [Fact]
    public void ApplyEnvironmentOverrides_WhenTimeoutSecondsProvided_SetsValue()
    {
        var old = Environment.GetEnvironmentVariable("LLM_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("LLM_TIMEOUT_SECONDS", "12");
            var settings = new LlmSettings();
            settings.ApplyEnvironmentOverrides();
            Assert.Equal(12, settings.TimeoutSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLM_TIMEOUT_SECONDS", old);
        }
    }

    [Fact]
    public void ApplyEnvironmentOverrides_WhenProviderSpecificReasoningEffortProvided_Normalizes()
    {
        var oldProvider = Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER");
        var oldOpenRouter = Environment.GetEnvironmentVariable("OPENROUTER_REASONING_EFFORT");
        try
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", "openrouter");
            Environment.SetEnvironmentVariable("OPENROUTER_REASONING_EFFORT", "LOW");
            var settings = new LlmSettings();
            settings.ApplyEnvironmentOverrides();
            Assert.Equal("low", settings.OpenRouterReasoningEffort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", oldProvider);
            Environment.SetEnvironmentVariable("OPENROUTER_REASONING_EFFORT", oldOpenRouter);
        }
    }

    [Fact]
    public void GetEffectiveApiKeySource_WhenOpenRouterEnvApiKeySet_IncludesVariableName()
    {
        var old = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "k1");
            var settings = new LlmSettings { Provider = "openrouter" };
            settings.ApplyEnvironmentOverrides();
            Assert.Equal("env:OPENROUTER_API_KEY", settings.GetEffectiveApiKeySource());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", old);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("unset", true)]
    [InlineData("DEFAULT", true)]
    public void IsReasoningEffortUnsetToken_RecognizesUnsetTokens(string? value, bool expected)
    {
        Assert.Equal(expected, LlmSettings.IsReasoningEffortUnsetToken(value));
    }

    [Fact]
    public void ApplyReasoningEffortOverride_WhenSetterNull_Throws()
    {
        var method = typeof(LlmSettings).GetMethod("ApplyReasoningEffortOverride", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        _ = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            method!.Invoke(null, new object?[] { "low", null }));
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
            Assert.Equal("codex-auth (override)", settings.GetEffectiveApiKeySource());
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
            Assert.Equal("env:OPENAI_API_KEY", settings.GetEffectiveApiKeySource());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", oldProvider);
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_CODEX_AUTH_PATH", oldCodexPath);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", oldOpenAi);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("low", "low")]
    [InlineData("MEDIUM", "medium")]
    [InlineData("High", "high")]
    [InlineData("unset", null)]
    [InlineData("default", null)]
    [InlineData("auto", null)]
    [InlineData("none", null)]
    [InlineData("invalid", null)]
    public void NormalizeReasoningEffort_NormalizesExpectedValues(string input, string? expected)
    {
        Assert.Equal(expected, LlmSettings.NormalizeReasoningEffort(input));
    }

    [Fact]
    public void SetReasoningEffortForCurrentProvider_SetsProviderSpecificValue()
    {
        var settings = new LlmSettings { Provider = "openai" };
        settings.SetReasoningEffortForCurrentProvider("high");
        Assert.Equal("high", settings.OpenAiReasoningEffort);
        Assert.Null(settings.OpenRouterReasoningEffort);

        settings.Provider = "openrouter";
        settings.SetReasoningEffortForCurrentProvider("low");
        Assert.Equal("high", settings.OpenAiReasoningEffort);
        Assert.Equal("low", settings.OpenRouterReasoningEffort);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_WhenReasoningEffortInvalid_DoesNotOverrideExistingValue()
    {
        var old = Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_REASONING_EFFORT");
        try
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_REASONING_EFFORT", "invalid");
            var settings = new LlmSettings { Provider = "openai", OpenAiReasoningEffort = "high" };
            settings.ApplyEnvironmentOverrides();
            Assert.Equal("high", settings.OpenAiReasoningEffort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_REASONING_EFFORT", old);
        }
    }

    [Theory]
    [InlineData("unset")]
    [InlineData("default")]
    [InlineData("auto")]
    [InlineData("none")]
    public void ApplyEnvironmentOverrides_WhenReasoningEffortUnset_ClearsValue(string value)
    {
        var old = Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_REASONING_EFFORT");
        try
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_REASONING_EFFORT", value);
            var settings = new LlmSettings { Provider = "openai", OpenAiReasoningEffort = "high" };
            settings.ApplyEnvironmentOverrides();
            Assert.Null(settings.OpenAiReasoningEffort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_REASONING_EFFORT", old);
        }
    }

    [Fact]
    public void ApplyEnvironmentOverrides_WhenOpenAiReasoningEffortProvided_Normalizes()
    {
        var oldProvider = Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER");
        var oldEffort = Environment.GetEnvironmentVariable("OPENAI_REASONING_EFFORT");
        try
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", "openai");
            Environment.SetEnvironmentVariable("OPENAI_REASONING_EFFORT", "HIGH");
            var settings = new LlmSettings();
            settings.ApplyEnvironmentOverrides();
            Assert.Equal("high", settings.OpenAiReasoningEffort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", oldProvider);
            Environment.SetEnvironmentVariable("OPENAI_REASONING_EFFORT", oldEffort);
        }
    }

    [Fact]
    public void ApplyEnvironmentOverrides_WhenAnthropicReasoningEffortProvided_Normalizes()
    {
        var oldProvider = Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER");
        var oldEffort = Environment.GetEnvironmentVariable("ANTHROPIC_REASONING_EFFORT");
        try
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", "anthropic");
            Environment.SetEnvironmentVariable("ANTHROPIC_REASONING_EFFORT", "Medium");
            var settings = new LlmSettings();
            settings.ApplyEnvironmentOverrides();
            Assert.Equal("medium", settings.AnthropicReasoningEffort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", oldProvider);
            Environment.SetEnvironmentVariable("ANTHROPIC_REASONING_EFFORT", oldEffort);
        }
    }

    [Fact]
    public void GetEffectiveApiKey_WhenProviderVaries_ReturnsProviderSpecificKey()
    {
        var openAi = new LlmSettings { Provider = "openai", OpenAiApiKey = "k1" };
        Assert.Equal("k1", openAi.GetEffectiveApiKey());

        var anthropic = new LlmSettings { Provider = "anthropic", AnthropicApiKey = "k2" };
        Assert.Equal("k2", anthropic.GetEffectiveApiKey());

        var openRouter = new LlmSettings { Provider = "openrouter", OpenRouterApiKey = "k3" };
        Assert.Equal("k3", openRouter.GetEffectiveApiKey());
    }

    [Fact]
    public void GetEffectiveApiKeySource_WhenOpenAiNoKeys_ReturnsNotSet()
    {
        var oldProvider = Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER");
        var oldProvider2 = Environment.GetEnvironmentVariable("LLM_PROVIDER");
        var oldEnvKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var oldEnvKey2 = Environment.GetEnvironmentVariable("DEBUGGER_MCP_OPENAI_API_KEY");
        var oldCodexPath = Environment.GetEnvironmentVariable("DEBUGGER_MCP_CODEX_AUTH_PATH");
        try
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", "openai");
            Environment.SetEnvironmentVariable("LLM_PROVIDER", null);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_CODEX_AUTH_PATH", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "auth.json"));

            var settings = new LlmSettings { Provider = "openai", OpenAiApiKey = null };
            settings.ApplyEnvironmentOverrides();
            Assert.Null(settings.GetEffectiveOpenAiApiKey());
            Assert.Equal("(not set)", settings.GetEffectiveApiKeySource());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER", oldProvider);
            Environment.SetEnvironmentVariable("LLM_PROVIDER", oldProvider2);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", oldEnvKey);
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_OPENAI_API_KEY", oldEnvKey2);
            Environment.SetEnvironmentVariable("DEBUGGER_MCP_CODEX_AUTH_PATH", oldCodexPath);
        }
    }

    [Fact]
    public void SetReasoningEffortForCurrentProvider_WhenAnthropic_SetsAnthropicValue()
    {
        var settings = new LlmSettings { Provider = "anthropic" };
        settings.SetReasoningEffortForCurrentProvider("medium");
        Assert.Equal("medium", settings.AnthropicReasoningEffort);
    }
}
