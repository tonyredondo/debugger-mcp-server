using System.Text.Json;
using DebuggerMcp.Cli.Client;
using DebuggerMcp.Cli.Models;
using DebuggerMcp.Cli.Serialization;

namespace DebuggerMcp.Cli.Shell;

/// <summary>
/// Best-effort recovery helpers for syncing local CLI state with server-side session state.
/// </summary>
internal static class DumpStateRecovery
{
    private static readonly JsonSerializerOptions JsonOptions = CliJsonSerializationDefaults.CaseInsensitiveCamelCaseIgnoreNull;

    /// <summary>
    /// Attempts to update <see cref="ShellState.DumpId"/> from the server's session list for the current session.
    /// </summary>
    /// <remarks>
    /// This is used to recover from cases where the CLI timed out waiting for a response, but the server
    /// continued processing the request (e.g., opening a large dump).
    /// </remarks>
    internal static async Task<bool> TrySyncOpenedDumpFromServerAsync(
        ShellState state,
        IMcpClient mcpClient,
        CancellationToken cancellationToken = default)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        if (mcpClient == null)
        {
            throw new ArgumentNullException(nameof(mcpClient));
        }

        if (string.IsNullOrWhiteSpace(state.SessionId))
        {
            state.DumpId = null;
            return false;
        }

        var listJson = await mcpClient.ListSessionsAsync(state.Settings.UserId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(listJson))
        {
            return false;
        }

        SessionListResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SessionListResponse>(listJson, JsonOptions);
        }
        catch
        {
            return false;
        }

        if (parsed?.Sessions == null)
        {
            return false;
        }

        return SessionStateSynchronizer.TrySyncCurrentDumpFromSessionList(state, parsed);
    }
}

