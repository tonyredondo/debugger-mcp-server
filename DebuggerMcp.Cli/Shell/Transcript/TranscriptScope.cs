namespace DebuggerMcp.Cli.Shell.Transcript;

/// <summary>
/// Helpers for scoping transcript entries to a specific CLI context (server/session/dump).
/// </summary>
internal static class TranscriptScope
{
    /// <summary>
    /// Returns true if the entry belongs to the given scope.
    /// </summary>
    public static bool Matches(CliTranscriptEntry entry, string? serverUrl, string? sessionId, string? dumpId)
    {
        if (entry == null)
        {
            return false;
        }

        return string.Equals(NormalizeServerUrl(entry.ServerUrl), NormalizeServerUrl(serverUrl), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(NormalizeId(entry.SessionId), NormalizeId(sessionId), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(NormalizeId(entry.DumpId), NormalizeId(dumpId), StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeServerUrl(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().TrimEnd('/');

    internal static string NormalizeId(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim();
}

