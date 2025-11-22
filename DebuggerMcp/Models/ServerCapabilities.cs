using System.Runtime.InteropServices;

namespace DebuggerMcp.Models;

/// <summary>
/// Represents the capabilities and characteristics of a Debugger MCP Server instance.
/// </summary>
/// <remarks>
/// This information is used by the CLI to match dumps to appropriate servers.
/// The architecture and Alpine status are critical for proper dump analysis.
/// </remarks>
public class ServerCapabilities
{
    /// <summary>
    /// Gets or sets the operating system platform (e.g., "linux", "windows").
    /// </summary>
    public string Platform { get; set; } = GetPlatform();

    /// <summary>
    /// Gets or sets the processor architecture (e.g., "x64", "arm64").
    /// </summary>
    public string Architecture { get; set; } = GetArchitecture();

    /// <summary>
    /// Gets or sets a value indicating whether this server is running on Alpine Linux.
    /// </summary>
    /// <remarks>
    /// Alpine Linux uses musl libc instead of glibc, which affects .NET symbol resolution.
    /// Dumps from Alpine-based containers should be analyzed on Alpine servers.
    /// </remarks>
    public bool IsAlpine { get; set; } = DetectAlpine();

    /// <summary>
    /// Gets or sets the .NET runtime version.
    /// </summary>
    public string RuntimeVersion { get; set; } = Environment.Version.ToString();

    /// <summary>
    /// Gets or sets the debugger type available on this server.
    /// </summary>
    /// <remarks>
    /// "WinDbg" on Windows, "LLDB" on Linux/macOS.
    /// </remarks>
    public string DebuggerType { get; set; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "WinDbg" : "LLDB";

    /// <summary>
    /// Gets or sets the server hostname.
    /// </summary>
    public string Hostname { get; set; } = Environment.MachineName;

    /// <summary>
    /// Gets or sets the server version.
    /// </summary>
    public string Version { get; set; } = GetServerVersion();

    /// <summary>
    /// Gets or sets the Linux distribution name (e.g., "debian", "alpine", "ubuntu").
    /// </summary>
    public string? Distribution { get; set; } = DetectDistribution();

    /// <summary>
    /// Gets the current operating system platform.
    /// </summary>
    private static string GetPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "macos";
        return "unknown";
    }

    /// <summary>
    /// Gets the processor architecture.
    /// </summary>
    private static string GetArchitecture()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };
    }

    /// <summary>
    /// Detects if running on Alpine Linux.
    /// </summary>
    private static bool DetectAlpine()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        // Check for Alpine-specific file
        if (File.Exists("/etc/alpine-release"))
            return true;

        // Check os-release for Alpine
        return DetectDistribution()?.Equals("alpine", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    /// <summary>
    /// Detects the Linux distribution name.
    /// </summary>
    private static string? DetectDistribution()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return null;

        try
        {
            // Try /etc/os-release first (standard on most distros)
            if (File.Exists("/etc/os-release"))
            {
                var lines = File.ReadAllLines("/etc/os-release");
                foreach (var line in lines)
                {
                    if (line.StartsWith("ID=", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = line[3..].Trim('"', '\'');
                        return value.ToLowerInvariant();
                    }
                }
            }

            // Fallback: check for Alpine-specific file
            if (File.Exists("/etc/alpine-release"))
                return "alpine";

            // Fallback: check for Debian
            if (File.Exists("/etc/debian_version"))
                return "debian";

            // Fallback: check for RedHat-based
            if (File.Exists("/etc/redhat-release"))
                return "rhel";
        }
        catch
        {
            // Ignore errors during detection
        }

        return null;
    }

    /// <summary>
    /// Gets the server version from assembly metadata.
    /// </summary>
    private static string GetServerVersion()
    {
        var assembly = typeof(ServerCapabilities).Assembly;
        var version = assembly.GetName().Version;
        return version?.ToString() ?? "1.0.0";
    }
}

