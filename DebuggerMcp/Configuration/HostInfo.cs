using System.Runtime.InteropServices;

namespace DebuggerMcp.Configuration;

/// <summary>
/// Provides information about the host system where the server is running.
/// </summary>
/// <remarks>
/// This information is particularly important for .NET debugging because:
/// - Alpine Linux dumps can only be debugged on Alpine hosts (SOS/DAC limitation)
/// - Different Linux distributions may have different library versions
/// - Architecture (x64/arm64) affects which dumps can be analyzed
/// </remarks>
public class HostInfo
{
    /// <summary>
    /// Gets the operating system name (e.g., "Linux", "Windows", "macOS").
    /// </summary>
    public string OsName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the OS version string.
    /// </summary>
    public string OsVersion { get; init; } = string.Empty;

    /// <summary>
    /// Gets the Linux distribution name if running on Linux (e.g., "Alpine", "Debian", "Ubuntu").
    /// </summary>
    public string? LinuxDistribution { get; init; }

    /// <summary>
    /// Gets the Linux distribution version if available.
    /// </summary>
    public string? LinuxDistributionVersion { get; init; }

    /// <summary>
    /// Gets whether the host is running on Alpine Linux.
    /// </summary>
    /// <remarks>
    /// This is critical because Alpine Linux uses musl libc instead of glibc,
    /// which means .NET dumps from Alpine can only be debugged on Alpine hosts.
    /// </remarks>
    public bool IsAlpine { get; init; }

    /// <summary>
    /// Gets whether the host is running in a Docker container.
    /// </summary>
    public bool IsDocker { get; init; }

    /// <summary>
    /// Gets the processor architecture (e.g., "x64", "arm64").
    /// </summary>
    public string Architecture { get; init; } = string.Empty;

    /// <summary>
    /// Gets the .NET runtime version running the server.
    /// </summary>
    public string DotNetVersion { get; init; } = string.Empty;

    /// <summary>
    /// Gets the list of installed .NET runtimes available for debugging.
    /// </summary>
    public List<string> InstalledRuntimes { get; init; } = [];

    /// <summary>
    /// Gets the debugger type being used (LLDB or WinDbg).
    /// </summary>
    public string DebuggerType { get; init; } = string.Empty;

    /// <summary>
    /// Gets the hostname of the server.
    /// </summary>
    public string Hostname { get; init; } = string.Empty;

    /// <summary>
    /// Gets the server start time.
    /// </summary>
    public DateTime ServerStartTime { get; init; }

    /// <summary>
    /// Gets a short description of the host suitable for display.
    /// </summary>
    public string Description => IsAlpine
        ? $"Alpine Linux {LinuxDistributionVersion} ({Architecture})"
        : !string.IsNullOrEmpty(LinuxDistribution)
            ? $"{LinuxDistribution} {LinuxDistributionVersion} ({Architecture})"
            : $"{OsName} {OsVersion} ({Architecture})";

    /// <summary>
    /// Detects and returns information about the current host system.
    /// </summary>
    public static HostInfo Detect()
    {
        var osName = GetOsName();
        var (distro, distroVersion, isAlpine) = GetLinuxDistributionInfo();

        return new HostInfo
        {
            OsName = osName,
            OsVersion = Environment.OSVersion.Version.ToString(),
            LinuxDistribution = distro,
            LinuxDistributionVersion = distroVersion,
            IsAlpine = isAlpine,
            IsDocker = DetectDocker(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            DotNetVersion = RuntimeInformation.FrameworkDescription,
            InstalledRuntimes = GetInstalledRuntimes(),
            DebuggerType = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "WinDbg" : "LLDB",
            Hostname = Environment.MachineName,
            ServerStartTime = DateTime.UtcNow
        };
    }

    private static string GetOsName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "Linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "macOS";
        return RuntimeInformation.OSDescription;
    }

    private static (string? distro, string? version, bool isAlpine) GetLinuxDistributionInfo()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return (null, null, false);

        // Try to read /etc/os-release (standard on most Linux distributions)
        try
        {
            if (File.Exists("/etc/os-release"))
            {
                var lines = File.ReadAllLines("/etc/os-release");
                string? id = null;
                string? versionId = null;
                string? prettyName = null;

                foreach (var line in lines)
                {
                    if (line.StartsWith("ID=", StringComparison.OrdinalIgnoreCase))
                    {
                        id = line[3..].Trim('"', '\'');
                    }
                    else if (line.StartsWith("VERSION_ID=", StringComparison.OrdinalIgnoreCase))
                    {
                        versionId = line[11..].Trim('"', '\'');
                    }
                    else if (line.StartsWith("PRETTY_NAME=", StringComparison.OrdinalIgnoreCase))
                    {
                        prettyName = line[12..].Trim('"', '\'');
                    }
                }

                // Determine distribution name
                var distroName = id?.ToLowerInvariant() switch
                {
                    "alpine" => "Alpine",
                    "debian" => "Debian",
                    "ubuntu" => "Ubuntu",
                    "fedora" => "Fedora",
                    "centos" => "CentOS",
                    "rhel" => "RHEL",
                    "arch" => "Arch",
                    "opensuse" => "openSUSE",
                    "opensuse-leap" => "openSUSE Leap",
                    "opensuse-tumbleweed" => "openSUSE Tumbleweed",
                    _ => prettyName ?? id
                };

                var isAlpine = string.Equals(id, "alpine", StringComparison.OrdinalIgnoreCase);

                return (distroName, versionId, isAlpine);
            }

            // Fallback: check for Alpine-specific file
            if (File.Exists("/etc/alpine-release"))
            {
                var version = File.ReadAllText("/etc/alpine-release").Trim();
                return ("Alpine", version, true);
            }
        }
        catch
        {
            // Ignore errors reading OS info
        }

        return (null, null, false);
    }

    private static bool DetectDocker()
    {
        // Check for /.dockerenv file (common indicator)
        if (File.Exists("/.dockerenv"))
            return true;

        // Check for docker in cgroup (Linux)
        try
        {
            if (File.Exists("/proc/1/cgroup"))
            {
                var content = File.ReadAllText("/proc/1/cgroup");
                if (content.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("kubepods", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("containerd", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return false;
    }

    private static List<string> GetInstalledRuntimes()
    {
        var runtimes = new List<string>();

        // Check common .NET runtime locations
        var runtimePaths = new[]
        {
            "/usr/share/dotnet/shared/Microsoft.NETCore.App",
            "/usr/local/share/dotnet/shared/Microsoft.NETCore.App",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "shared", "Microsoft.NETCore.App"),
            // Windows paths
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.NETCore.App")
        };

        foreach (var basePath in runtimePaths)
        {
            if (Directory.Exists(basePath))
            {
                try
                {
                    var versions = Directory.GetDirectories(basePath)
                        .Select(Path.GetFileName)
                        .Where(v => !string.IsNullOrEmpty(v))
                        .OrderByDescending(v => v)
                        .ToList();

                    foreach (var version in versions)
                    {
                        if (!string.IsNullOrEmpty(version) && !runtimes.Contains(version))
                        {
                            runtimes.Add(version!);
                        }
                    }
                }
                catch
                {
                    // Ignore errors enumerating directories
                }
            }
        }

        return runtimes;
    }
}
