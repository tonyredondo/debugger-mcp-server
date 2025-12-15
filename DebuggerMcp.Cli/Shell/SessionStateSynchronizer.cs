using DebuggerMcp.Cli.Models;

namespace DebuggerMcp.Cli.Shell;

/// <summary>
/// Synchronizes <see cref="ShellState"/> with server-side session state.
/// </summary>
internal static class SessionStateSynchronizer
{
    /// <summary>
    /// Updates <see cref="ShellState.DumpId"/> based on the server's session list for the current <see cref="ShellState.SessionId"/>.
    /// </summary>
    /// <param name="state">The current shell state.</param>
    /// <param name="response">The parsed session list response.</param>
    /// <returns>True if a matching session was found; otherwise false.</returns>
    internal static bool TrySyncCurrentDumpFromSessionList(ShellState state, SessionListResponse response)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        if (response == null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        if (string.IsNullOrWhiteSpace(state.SessionId))
        {
            state.DumpId = null;
            return false;
        }

        var match = (response.Sessions ?? [])
            .FirstOrDefault(s => string.Equals(s.SessionId, state.SessionId, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            state.DumpId = null;
            return false;
        }

        state.DumpId = string.IsNullOrWhiteSpace(match.CurrentDumpId) ? null : match.CurrentDumpId;
        return true;
    }
}

