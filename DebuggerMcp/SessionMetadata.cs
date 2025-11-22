using System.Text.Json.Serialization;

namespace DebuggerMcp;

/// <summary>
/// Represents the serializable metadata for a debugging session.
/// </summary>
/// <remarks>
/// This class contains all the information needed to persist and restore
/// a debugging session across server restarts or server switches.
/// The actual debugger manager is recreated when the session is loaded.
/// </remarks>
public class SessionMetadata
{
    /// <summary>
    /// Gets or sets the unique identifier for this session.
    /// </summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user identifier who owns this session.
    /// </summary>
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp when this session was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the timestamp when this session was last accessed.
    /// </summary>
    [JsonPropertyName("lastAccessedAt")]
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the currently open dump ID, if any.
    /// </summary>
    /// <remarks>
    /// When a session is restored, this is used to automatically reopen the dump file.
    /// </remarks>
    [JsonPropertyName("currentDumpId")]
    public string? CurrentDumpId { get; set; }

    /// <summary>
    /// Gets or sets the full path to the dump file, if one is open.
    /// </summary>
    /// <remarks>
    /// This is the resolved path to the dump file, allowing restoration
    /// without needing to re-resolve the dump ID.
    /// </remarks>
    [JsonPropertyName("currentDumpPath")]
    public string? CurrentDumpPath { get; set; }

    /// <summary>
    /// Gets or sets the server ID that last handled this session.
    /// </summary>
    /// <remarks>
    /// Used for debugging and monitoring session migrations between servers.
    /// </remarks>
    [JsonPropertyName("lastServerId")]
    public string? LastServerId { get; set; }

    /// <summary>
    /// Creates a new SessionMetadata from a DebuggerSession.
    /// </summary>
    public static SessionMetadata FromSession(DebuggerSession session, string? serverId = null)
    {
        return new SessionMetadata
        {
            SessionId = session.SessionId,
            UserId = session.UserId,
            CreatedAt = session.CreatedAt,
            LastAccessedAt = session.LastAccessedAt,
            CurrentDumpId = session.CurrentDumpId,
            CurrentDumpPath = session.Manager?.CurrentDumpPath,
            LastServerId = serverId
        };
    }
}

