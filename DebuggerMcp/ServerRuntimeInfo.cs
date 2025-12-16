namespace DebuggerMcp;

/// <summary>
/// Provides server runtime metadata that must be stable across requests.
/// </summary>
/// <remarks>
/// Avoid capturing process start timestamps in controller statics because controllers
/// may be JIT-loaded on first use, causing uptime to start at first request.
/// </remarks>
public sealed class ServerRuntimeInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ServerRuntimeInfo"/> class.
    /// </summary>
    /// <param name="startedAtUtc">The UTC timestamp when the server started.</param>
    public ServerRuntimeInfo(DateTime startedAtUtc)
    {
        StartedAtUtc = startedAtUtc;
    }

    /// <summary>
    /// Gets the UTC timestamp when the server started.
    /// </summary>
    public DateTime StartedAtUtc { get; }
}

