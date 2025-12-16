using System.Text.Json.Serialization;

namespace DebuggerMcp.Cli.Configuration;

/// <summary>
/// Settings for the CLI-integrated LLM client (OpenRouter).
/// </summary>
public sealed class LlmSettings
{
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

        var timeoutSeconds =
            Environment.GetEnvironmentVariable("OPENROUTER_TIMEOUT_SECONDS") ??
            Environment.GetEnvironmentVariable("DEBUGGER_MCP_OPENROUTER_TIMEOUT_SECONDS");
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
    }

    /// <summary>
    /// Gets the effective OpenRouter API key (environment overrides config).
    /// </summary>
    public string? GetEffectiveOpenRouterApiKey()
        => OpenRouterApiKeyFromEnvironment ?? OpenRouterApiKey;
}
