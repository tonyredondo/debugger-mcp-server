using System.Text.Json.Serialization;

namespace DebuggerMcp.Cli.Models;

/// <summary>
/// Represents host information about the Debugger MCP Server.
/// </summary>
/// <remarks>
/// This information is crucial for determining which dumps can be analyzed:
/// - Alpine Linux dumps can only be debugged on Alpine hosts (SOS/DAC limitation)
/// - Architecture (x64/arm64) affects which dumps can be analyzed
/// - Installed .NET runtimes determine which dump versions are supported
/// </remarks>
public class ServerInfo
{
    /// <summary>
    /// Gets or sets the operating system name (e.g., "Linux", "Windows", "macOS").
    /// </summary>
    [JsonPropertyName("osName")]
    public string OsName { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the OS version string.
    /// </summary>
    [JsonPropertyName("osVersion")]
    public string OsVersion { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the Linux distribution name if running on Linux (e.g., "Alpine", "Debian", "Ubuntu").
    /// </summary>
    [JsonPropertyName("linuxDistribution")]
    public string? LinuxDistribution { get; set; }
    
    /// <summary>
    /// Gets or sets the Linux distribution version if available.
    /// </summary>
    [JsonPropertyName("linuxDistributionVersion")]
    public string? LinuxDistributionVersion { get; set; }
    
    /// <summary>
    /// Gets or sets whether the host is running on Alpine Linux.
    /// </summary>
    /// <remarks>
    /// This is critical because Alpine Linux uses musl libc instead of glibc,
    /// which means .NET dumps from Alpine can only be debugged on Alpine hosts.
    /// </remarks>
    [JsonPropertyName("isAlpine")]
    public bool IsAlpine { get; set; }
    
    /// <summary>
    /// Gets or sets whether the host is running in a Docker container.
    /// </summary>
    [JsonPropertyName("isDocker")]
    public bool IsDocker { get; set; }
    
    /// <summary>
    /// Gets or sets the processor architecture (e.g., "x64", "arm64").
    /// </summary>
    [JsonPropertyName("architecture")]
    public string Architecture { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the .NET runtime version running the server.
    /// </summary>
    [JsonPropertyName("dotNetVersion")]
    public string DotNetVersion { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the list of installed .NET runtimes available for debugging.
    /// </summary>
    [JsonPropertyName("installedRuntimes")]
    public List<string> InstalledRuntimes { get; set; } = [];
    
    /// <summary>
    /// Gets or sets the debugger type being used (LLDB or WinDbg).
    /// </summary>
    [JsonPropertyName("debuggerType")]
    public string DebuggerType { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the hostname of the server.
    /// </summary>
    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the server start time.
    /// </summary>
    [JsonPropertyName("serverStartTime")]
    public DateTime ServerStartTime { get; set; }
    
    /// <summary>
    /// Gets or sets the short description of the host.
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

