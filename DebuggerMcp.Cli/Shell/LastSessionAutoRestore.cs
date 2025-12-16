using System.Text.Json;
using System.Text.RegularExpressions;
using DebuggerMcp.Cli.Client;
using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Models;

namespace DebuggerMcp.Cli.Shell;

/// <summary>
/// Restores the last-used session automatically after connecting to a server.
/// </summary>
internal static class LastSessionAutoRestore
{
    internal readonly record struct Result(bool Restored, bool ClearedSavedSession);

    internal static async Task<Result> TryRestoreAsync(
        ConsoleOutput output,
        ShellState state,
        IMcpClient mcpClient,
        CancellationToken cancellationToken = default)
    {
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            return new Result(Restored: false, ClearedSavedSession: false);
        }

        if (!string.IsNullOrWhiteSpace(state.SessionId))
        {
            return new Result(Restored: false, ClearedSavedSession: false);
        }

        var serverUrl = state.Settings.ServerUrl;
        var userId = state.Settings.UserId;
        var lastSessionId = state.Settings.GetLastSessionId(serverUrl, userId);
        if (string.IsNullOrWhiteSpace(lastSessionId))
        {
            return new Result(Restored: false, ClearedSavedSession: false);
        }

        string restoreResult;
        try
        {
            restoreResult = await output.WithSpinnerAsync(
                $"Restoring last session {ShortId(lastSessionId)}...",
                () => mcpClient.RestoreSessionAsync(lastSessionId, userId, cancellationToken));
        }
        catch (Exception ex) when (SessionExpiryHandler.IsSessionExpiredMessage(ex.Message))
        {
            state.Settings.ClearLastSessionId(serverUrl, userId, lastSessionId);
            return new Result(Restored: false, ClearedSavedSession: true);
        }

        if (LooksLikeError(restoreResult))
        {
            state.Settings.ClearLastSessionId(serverUrl, userId, lastSessionId);
            return new Result(Restored: false, ClearedSavedSession: true);
        }

        state.SetSession(lastSessionId);

        // Best-effort: sync dump ID from session list (a restored session may already have a dump open).
        try
        {
            var listJson = await mcpClient.ListSessionsAsync(userId, cancellationToken);
            if (!LooksLikeError(listJson))
            {
                var parsed = JsonSerializer.Deserialize<SessionListResponse>(
                    listJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (parsed != null)
                {
                    SessionStateSynchronizer.TrySyncCurrentDumpFromSessionList(state, parsed);
                }
            }
        }
        catch
        {
            // Ignore: keep prompt usable even if list parsing fails.
        }

        // Best-effort: set debugger type.
        try
        {
            var debuggerInfo = await mcpClient.GetDebuggerInfoAsync(lastSessionId, userId, cancellationToken);
            var debuggerTypeMatch = Regex.Match(
                debuggerInfo,
                @"Debugger\s*Type:\s*(\w+)",
                RegexOptions.IgnoreCase);
            if (debuggerTypeMatch.Success)
            {
                state.DebuggerType = debuggerTypeMatch.Groups[1].Value;
            }
        }
        catch
        {
            // Not critical.
        }

        output.Success($"Restored last session: {ShortId(lastSessionId)}");
        if (!string.IsNullOrWhiteSpace(state.DumpId))
        {
            output.Dim($"Dump: {ShortId(state.DumpId)}");
        }

        return new Result(Restored: true, ClearedSavedSession: false);
    }

    private static bool LooksLikeError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.TrimStart();
        if (trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Common server JSON error shape.
        return trimmed.StartsWith("{", StringComparison.Ordinal) &&
               trimmed.Contains("\"error\"", StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortId(string id)
        => id.Length > 8 ? id[..8] : id;
}
