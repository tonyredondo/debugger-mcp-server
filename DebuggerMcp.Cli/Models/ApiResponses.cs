using System.Globalization;
using System.Text.Json.Serialization;

namespace DebuggerMcp.Cli.Models;

/// <summary>
/// Session statistics from the server.
/// </summary>
public class SessionStatistics
{
    /// <summary>
    /// Gets or sets the total number of active sessions.
    /// </summary>
    [JsonPropertyName("activeSessions")]
    public int ActiveSessions { get; set; }

    /// <summary>
    /// Gets or sets the total number of stored dumps.
    /// </summary>
    [JsonPropertyName("totalDumps")]
    public int TotalDumps { get; set; }

    /// <summary>
    /// Gets or sets the total storage used in bytes.
    /// </summary>
    [JsonPropertyName("storageUsed")]
    public long StorageUsed { get; set; }

    /// <summary>
    /// Gets or sets the server uptime.
    /// </summary>
    [JsonPropertyName("uptime")]
    public string? Uptime { get; set; }

    /// <summary>
    /// Gets the storage used formatted as a human-readable string.
    /// </summary>
    [JsonIgnore]
    public string FormattedStorageUsed => FormatBytes(StorageUsed);

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        double size = bytes;

        while (size >= 1024 && i < suffixes.Length - 1)
        {
            size /= 1024;
            i++;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", size, suffixes[i]);
    }
}
