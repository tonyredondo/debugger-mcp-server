using System.Text.Json.Serialization;
using DebuggerMcp.Cli.Llm;

namespace DebuggerMcp.Cli.Configuration;

/// <summary>
/// Settings for the CLI-integrated LLM client (OpenRouter/OpenAI).
/// </summary>
public sealed class LlmSettings
{
    /// <summary>
    /// Gets or sets the LLM provider.
    /// </summary>
    /// <remarks>
    /// Supported values: <c>openrouter</c>, <c>openai</c>.
    /// </remarks>
    public string Provider { get; set; } = "openrouter";

    /// <summary>
    /// Gets or sets the OpenRouter API key.
    /// </summary>
    public string? OpenRouterApiKey { get; set; }

    /// <summary>
    /// Gets the OpenRouter API key provided via environment variables (not persisted).
    /// </summary>
    [JsonIgnore]
    public string? OpenRouterApiKeyFromEnvironment { get; private set; }

    /// <summary>
    /// Gets or sets the OpenRouter model identifier.
    /// </summary>
    /// <remarks>
    /// Example: <c>openrouter/auto</c>, <c>openai/gpt-4o-mini</c>.
    /// </remarks>
    public string OpenRouterModel { get; set; } = "openrouter/auto";

    /// <summary>
    /// Gets or sets the OpenRouter base URL.
    /// </summary>
    public string OpenRouterBaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    /// <summary>
    /// Gets or sets the OpenRouter reasoning effort (when supported by the selected model/provider).
    /// </summary>
    /// <remarks>
    /// Supported values: <c>low</c>, <c>medium</c>, <c>high</c>.
    /// Use <c>null</c> to omit the field (provider default).
    /// </remarks>
    public string? OpenRouterReasoningEffort { get; set; }

    /// <summary>
    /// Gets or sets the OpenAI API key.
    /// </summary>
    public string? OpenAiApiKey { get; set; }

    /// <summary>
    /// Gets the OpenAI API key provided via environment variables (not persisted).
    /// </summary>
    [JsonIgnore]
    public string? OpenAiApiKeyFromEnvironment { get; private set; }

    /// <summary>
    /// Gets the OpenAI API key loaded from <c>~/.codex/auth.json</c> when available (not persisted).
    /// </summary>
    [JsonIgnore]
    public string? OpenAiApiKeyFromCodexAuth { get; private set; }

    /// <summary>
    /// Gets a value indicating whether <see cref="OpenAiApiKeyFromCodexAuth"/> came from an override path rather than the default <c>~/.codex/auth.json</c>.
    /// </summary>
    [JsonIgnore]
    public bool OpenAiApiKeyFromCodexAuthUsedOverridePath { get; private set; }

    /// <summary>
    /// Gets or sets the OpenAI model identifier.
    /// </summary>
    /// <remarks>
    /// Example: <c>gpt-4o-mini</c>, <c>gpt-4.1-mini</c>.
    /// </remarks>
    public string OpenAiModel { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Gets or sets the OpenAI base URL.
    /// </summary>
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// Gets or sets the OpenAI reasoning effort (when supported by the selected model).
    /// </summary>
    /// <remarks>
    /// Supported values: <c>low</c>, <c>medium</c>, <c>high</c>.
    /// Use <c>null</c> to omit the field (provider default).
    /// </remarks>
    public string? OpenAiReasoningEffort { get; set; }

    /// <summary>
    /// Gets or sets the LLM request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Gets or sets a value indicating whether the CLI LLM runs in agent mode.
    /// When enabled, the model can request tool calls and the CLI will execute them and continue iterating.
    /// </summary>
    public bool AgentModeEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether agent tool calls require confirmation.
    /// </summary>
    public bool AgentModeConfirmToolCalls { get; set; } = true;

    /// <summary>
    /// Applies environment variable overrides (if set).
    /// </summary>
    public void ApplyEnvironmentOverrides()
    {
        // Reset ephemeral env-derived fields each time this runs.
        OpenRouterApiKeyFromEnvironment = null;
        OpenAiApiKeyFromEnvironment = null;
        OpenAiApiKeyFromCodexAuth = null;
        OpenAiApiKeyFromCodexAuthUsedOverridePath = false;

        var provider =
            Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_PROVIDER") ??
            Environment.GetEnvironmentVariable("LLM_PROVIDER");
        if (!string.IsNullOrWhiteSpace(provider))
        {
            var normalized = NormalizeProvider(provider);
            if (normalized is "openrouter" or "openai")
            {
                Provider = normalized;
            }
        }

        // Support both OpenRouter-standard and DebuggerMcp-prefixed env vars.
        var apiKey =
            Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ??
            Environment.GetEnvironmentVariable("DEBUGGER_MCP_OPENROUTER_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            OpenRouterApiKeyFromEnvironment = apiKey;
        }

        var model =
            Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ??
            Environment.GetEnvironmentVariable("DEBUGGER_MCP_OPENROUTER_MODEL");
        if (!string.IsNullOrWhiteSpace(model))
        {
            OpenRouterModel = model.Trim();
        }

        var baseUrl =
            Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL") ??
            Environment.GetEnvironmentVariable("DEBUGGER_MCP_OPENROUTER_BASE_URL");
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            OpenRouterBaseUrl = baseUrl.Trim().TrimEnd('/');
        }

        var openAiApiKey =
            Environment.GetEnvironmentVariable("OPENAI_API_KEY") ??
            Environment.GetEnvironmentVariable("DEBUGGER_MCP_OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(openAiApiKey))
        {
            OpenAiApiKeyFromEnvironment = openAiApiKey;
        }

        var openAiModel =
            Environment.GetEnvironmentVariable("OPENAI_MODEL") ??
            Environment.GetEnvironmentVariable("DEBUGGER_MCP_OPENAI_MODEL");
        if (!string.IsNullOrWhiteSpace(openAiModel))
        {
            OpenAiModel = openAiModel.Trim();
        }

        var openAiBaseUrl =
            Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ??
            Environment.GetEnvironmentVariable("DEBUGGER_MCP_OPENAI_BASE_URL");
        if (!string.IsNullOrWhiteSpace(openAiBaseUrl))
        {
            OpenAiBaseUrl = openAiBaseUrl.Trim().TrimEnd('/');
        }

        var reasoningEffort =
            Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_REASONING_EFFORT") ??
            Environment.GetEnvironmentVariable("LLM_REASONING_EFFORT");
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            ApplyReasoningEffortOverride(reasoningEffort, set: SetReasoningEffortForCurrentProvider);
        }

        var openRouterReasoningEffort =
            Environment.GetEnvironmentVariable("OPENROUTER_REASONING_EFFORT") ??
            Environment.GetEnvironmentVariable("DEBUGGER_MCP_OPENROUTER_REASONING_EFFORT");
        if (!string.IsNullOrWhiteSpace(openRouterReasoningEffort))
        {
            ApplyReasoningEffortOverride(openRouterReasoningEffort, set: v => OpenRouterReasoningEffort = v);
        }

        var openAiReasoningEffort =
            Environment.GetEnvironmentVariable("OPENAI_REASONING_EFFORT") ??
            Environment.GetEnvironmentVariable("DEBUGGER_MCP_OPENAI_REASONING_EFFORT");
        if (!string.IsNullOrWhiteSpace(openAiReasoningEffort))
        {
            ApplyReasoningEffortOverride(openAiReasoningEffort, set: v => OpenAiReasoningEffort = v);
        }

        var timeoutSeconds =
            Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_TIMEOUT_SECONDS") ??
            Environment.GetEnvironmentVariable("LLM_TIMEOUT_SECONDS") ??
            Environment.GetEnvironmentVariable("OPENROUTER_TIMEOUT_SECONDS") ??
            Environment.GetEnvironmentVariable("DEBUGGER_MCP_OPENROUTER_TIMEOUT_SECONDS") ??
            Environment.GetEnvironmentVariable("OPENAI_TIMEOUT_SECONDS") ??
            Environment.GetEnvironmentVariable("DEBUGGER_MCP_OPENAI_TIMEOUT_SECONDS");
        if (!string.IsNullOrWhiteSpace(timeoutSeconds) && int.TryParse(timeoutSeconds, out var seconds) && seconds > 0)
        {
            TimeoutSeconds = seconds;
        }

        var agentMode =
            Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_AGENT_MODE") ??
            Environment.GetEnvironmentVariable("LLM_AGENT_MODE");
        if (BoolParsing.TryParse(agentMode, out var enabled))
        {
            AgentModeEnabled = enabled;
        }

        var confirm =
            Environment.GetEnvironmentVariable("DEBUGGER_MCP_LLM_AGENT_CONFIRM") ??
            Environment.GetEnvironmentVariable("LLM_AGENT_CONFIRM");
        if (BoolParsing.TryParse(confirm, out var confirmEnabled))
        {
            AgentModeConfirmToolCalls = confirmEnabled;
        }

        if (GetProviderKind() == LlmProviderKind.OpenAi &&
            string.IsNullOrWhiteSpace(OpenAiApiKeyFromEnvironment) &&
            string.IsNullOrWhiteSpace(OpenAiApiKey))
        {
            var overridePath = Environment.GetEnvironmentVariable("DEBUGGER_MCP_CODEX_AUTH_PATH");
            OpenAiApiKeyFromCodexAuthUsedOverridePath = !string.IsNullOrWhiteSpace(overridePath);
            OpenAiApiKeyFromCodexAuth = CodexAuthReader.TryReadOpenAiApiKey(overridePath);
        }
    }

    /// <summary>
    /// Gets the effective OpenRouter API key (environment overrides config).
    /// </summary>
    public string? GetEffectiveOpenRouterApiKey()
        => OpenRouterApiKeyFromEnvironment ?? OpenRouterApiKey;

    /// <summary>
    /// Gets the effective OpenAI API key (environment overrides config; falls back to <c>~/.codex/auth.json</c> when available).
    /// </summary>
    public string? GetEffectiveOpenAiApiKey()
        => OpenAiApiKeyFromEnvironment ?? OpenAiApiKey ?? OpenAiApiKeyFromCodexAuth;

    public string? GetEffectiveApiKey()
        => GetProviderKind() == LlmProviderKind.OpenAi
            ? GetEffectiveOpenAiApiKey()
            : GetEffectiveOpenRouterApiKey();

    public string GetEffectiveApiKeySource()
    {
        if (GetProviderKind() == LlmProviderKind.OpenAi)
        {
            if (!string.IsNullOrWhiteSpace(OpenAiApiKeyFromEnvironment)) return "env";
            if (!string.IsNullOrWhiteSpace(OpenAiApiKey)) return "config";
            if (!string.IsNullOrWhiteSpace(OpenAiApiKeyFromCodexAuth))
            {
                return OpenAiApiKeyFromCodexAuthUsedOverridePath
                    ? "codex auth (override path)"
                    : "~/.codex/auth.json";
            }
            return "(not set)";
        }

        if (!string.IsNullOrWhiteSpace(OpenRouterApiKeyFromEnvironment)) return "env";
        if (!string.IsNullOrWhiteSpace(OpenRouterApiKey)) return "config";
        return "(not set)";
    }

    public LlmProviderKind GetProviderKind()
        => NormalizeProvider(Provider) switch
        {
            "openai" => LlmProviderKind.OpenAi,
            _ => LlmProviderKind.OpenRouter
        };

    public string GetEffectiveModel()
        => GetProviderKind() == LlmProviderKind.OpenAi ? OpenAiModel : OpenRouterModel;

    public string? GetEffectiveReasoningEffort()
        => GetProviderKind() == LlmProviderKind.OpenAi ? OpenAiReasoningEffort : OpenRouterReasoningEffort;

    public string GetProviderDisplayName()
        => GetProviderKind() == LlmProviderKind.OpenAi ? "OpenAI" : "OpenRouter";

    public void SetReasoningEffortForCurrentProvider(string? value)
    {
        var normalized = NormalizeReasoningEffort(value);
        if (GetProviderKind() == LlmProviderKind.OpenAi)
        {
            OpenAiReasoningEffort = normalized;
        }
        else
        {
            OpenRouterReasoningEffort = normalized;
        }
    }

    internal static string NormalizeProvider(string? provider)
        => string.IsNullOrWhiteSpace(provider) ? "openrouter" : provider.Trim().ToLowerInvariant();

    internal static string? NormalizeReasoningEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        var v = effort.Trim().ToLowerInvariant();
        if (IsReasoningEffortUnsetToken(v))
        {
            return null;
        }

        return v is "low" or "medium" or "high" ? v : null;
    }

    internal static bool IsReasoningEffortUnsetToken(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return false;
        }

        var v = effort.Trim().ToLowerInvariant();
        return v is "unset" or "default" or "auto" or "none";
    }

    private static void ApplyReasoningEffortOverride(string rawValue, Action<string?> set)
    {
        if (set == null)
        {
            throw new ArgumentNullException(nameof(set));
        }

        if (IsReasoningEffortUnsetToken(rawValue))
        {
            set(null);
            return;
        }

        var normalized = NormalizeReasoningEffort(rawValue);
        if (normalized != null)
        {
            set(normalized);
        }
        // Invalid values are ignored (do not override existing config).
    }
}

public enum LlmProviderKind
{
    OpenRouter = 0,
    OpenAi = 1
}
