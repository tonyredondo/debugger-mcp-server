using DebuggerMcp.Cli.Configuration;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Temporarily overrides <see cref="LlmSettings.AgentModeConfirmToolCalls"/> for the lifetime of a scope.
/// </summary>
/// <remarks>
/// This is used by <c>llmagent</c> to run tools autonomously without permanently changing the user's configured preference.
/// </remarks>
internal sealed class LlmAgentConfirmOverride : IDisposable
{
    private readonly LlmSettings _settings;
    private readonly bool _originalValue;
    private bool _disposed;

    /// <summary>
    /// Initializes a new override scope and applies the desired confirmation behavior immediately.
    /// </summary>
    /// <param name="settings">The LLM settings instance to modify.</param>
    /// <param name="confirmToolCalls">Whether tool calls should require confirmation while the scope is active.</param>
    public LlmAgentConfirmOverride(LlmSettings settings, bool confirmToolCalls)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _originalValue = settings.AgentModeConfirmToolCalls;
        settings.AgentModeConfirmToolCalls = confirmToolCalls;
    }

    /// <summary>
    /// Restores the original confirmation setting.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _settings.AgentModeConfirmToolCalls = _originalValue;
        _disposed = true;
    }
}

