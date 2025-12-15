namespace DebuggerMcp.Cli.Shell;

/// <summary>
/// Helpers for detecting expired/missing sessions and keeping local shell state consistent.
/// </summary>
internal static class SessionExpiryHandler
{
    /// <summary>
    /// Returns true when an exception message indicates the current session is missing/expired.
    /// </summary>
    internal static bool IsSessionExpired(Exception ex)
    {
        if (ex == null)
        {
            throw new ArgumentNullException(nameof(ex));
        }

        return IsSessionExpiredMessage(ex.Message);
    }

    /// <summary>
    /// Returns true when a message indicates a session is missing/expired.
    /// </summary>
    internal static bool IsSessionExpiredMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = message.ToLowerInvariant();
        if (!normalized.Contains("session", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Contains("not found", StringComparison.Ordinal) ||
               normalized.Contains("expired", StringComparison.Ordinal) ||
               normalized.Contains("does not exist", StringComparison.Ordinal) ||
               normalized.Contains("no longer exists", StringComparison.Ordinal);
    }

    /// <summary>
    /// Clears the local session/dump state when the server session is missing.
    /// </summary>
    internal static void ClearExpiredSession(ShellState state)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        state.ClearSession();
    }
}

