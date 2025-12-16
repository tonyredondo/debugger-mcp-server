namespace DebuggerMcp.Cli.Shell.Transcript;

/// <summary>
/// A persisted transcript record for CLI/LLM interactions.
/// </summary>
public sealed class CliTranscriptEntry
{
    /// <summary>
    /// Gets or sets the UTC timestamp for the entry.
    /// </summary>
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the entry kind (e.g. cli_command, llm_user, llm_assistant).
    /// </summary>
    public string Kind { get; set; } = "cli_command";

    /// <summary>
    /// Gets or sets the primary text payload.
    /// For CLI commands this is the command line; for LLM messages this is the message content.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the captured output (optional).
    /// </summary>
    public string? Output { get; set; }

    /// <summary>
    /// Gets or sets the server URL context (optional).
    /// </summary>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// Gets or sets the session ID context (optional).
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Gets or sets the opened dump ID context (optional).
    /// </summary>
    public string? DumpId { get; set; }
}

