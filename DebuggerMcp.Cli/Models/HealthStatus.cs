using System.Text.Json.Serialization;

namespace DebuggerMcp.Cli.Models;

/// <summary>
/// Represents the health status of the Debugger MCP Server.
/// </summary>
public class HealthStatus
{
    /// <summary>
    /// Gets or sets the health status string.
    /// </summary>
    /// <example>Healthy</example>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "Unknown";

    /// <summary>
    /// Gets or sets the server version.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the server uptime.
    /// </summary>
    [JsonPropertyName("uptime")]
    public string? Uptime { get; set; }

    /// <summary>
    /// Gets or sets additional health details.
    /// </summary>
    [JsonPropertyName("details")]
    public Dictionary<string, object>? Details { get; set; }

    /// <summary>
    /// Gets whether the server is healthy.
    /// </summary>
    [JsonIgnore]
    public bool IsHealthy => Status.Equals("Healthy", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Represents an error response from the server.
/// </summary>
public class ErrorResponse
{
    /// <summary>
    /// Gets or sets the error message.
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets additional error details.
    /// </summary>
    [JsonPropertyName("details")]
    public string? Details { get; set; }

    /// <summary>
    /// Gets or sets the error code.
    /// </summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

