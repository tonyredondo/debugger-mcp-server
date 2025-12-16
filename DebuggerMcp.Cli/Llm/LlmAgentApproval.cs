namespace DebuggerMcp.Cli.Llm;

internal enum LlmAgentToolApprovalDecision
{
    AllowOnce,
    AllowToolAlways,
    AllowAllAlways,
    DenyOnce,
    CancelRun
}

/// <summary>
/// Tracks agent-mode tool approval state for a single run.
/// </summary>
internal sealed class LlmAgentApprovalState(bool confirmationsEnabled)
{
    private readonly bool _confirmationsEnabled = confirmationsEnabled;
    private bool _allowAll;
    private readonly HashSet<string> _allowedTools = new(StringComparer.OrdinalIgnoreCase);

    public bool ConfirmationsEnabled => _confirmationsEnabled;

    public bool IsAllowed(string toolName)
    {
        if (!_confirmationsEnabled)
        {
            return true;
        }

        if (_allowAll)
        {
            return true;
        }

        return _allowedTools.Contains(toolName);
    }

    public void ApplyDecision(string toolName, LlmAgentToolApprovalDecision decision)
    {
        switch (decision)
        {
            case LlmAgentToolApprovalDecision.AllowAllAlways:
                _allowAll = true;
                break;
            case LlmAgentToolApprovalDecision.AllowToolAlways:
                if (!string.IsNullOrWhiteSpace(toolName))
                {
                    _allowedTools.Add(toolName);
                }
                break;
        }
    }
}

