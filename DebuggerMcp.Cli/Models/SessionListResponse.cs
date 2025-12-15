using System.Text.Json.Serialization;

namespace DebuggerMcp.Cli.Models;

/// <summary>
/// Structured session list response returned by the MCP server.
/// </summary>
public sealed class SessionListResponse
{
    /// <summary>
    /// Gets or sets the user ID for which sessions were listed.
    /// </summary>
    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    /// <summary>
    /// Gets or sets the total number of sessions returned.
    /// </summary>
    [JsonPropertyName("total")]
    public int Total { get; set; }

    /// <summary>
    /// Gets or sets the list of sessions.
    /// </summary>
    [JsonPropertyName("sessions")]
    public List<SessionListItem> Sessions { get; set; } = [];
}

/// <summary>
/// A single session entry in <see cref="SessionListResponse"/>.
/// </summary>
public sealed class SessionListItem
{
    /// <summary>
    /// Gets or sets the session ID.
    /// </summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    /// <summary>
    /// Gets or sets the UTC creation time (ISO 8601).
    /// </summary>
    [JsonPropertyName("createdAtUtc")]
    public string? CreatedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the UTC last activity time (ISO 8601).
    /// </summary>
    [JsonPropertyName("lastActivityUtc")]
    public string? LastActivityUtc { get; set; }

    /// <summary>
    /// Gets or sets the currently opened dump ID (if any).
    /// </summary>
    [JsonPropertyName("currentDumpId")]
    public string? CurrentDumpId { get; set; }
}

