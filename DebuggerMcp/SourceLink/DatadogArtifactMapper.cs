using DebuggerMcp.Analysis;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Types of Datadog artifacts containing symbols or binaries.
/// </summary>
public enum DatadogArtifactType
{
    /// <summary>
    /// Native tracer symbols (*.debug files for Linux, *.pdb for Windows).
    /// Example: linux-tracer-symbols-linux-musl-arm64
    /// </summary>
    TracerSymbols,
    
    /// <summary>
    /// Profiler symbols (*.debug files for Linux, *.pdb for Windows).
    /// Example: linux-profiler-symbols-linux-musl-arm64
    /// </summary>
    ProfilerSymbols,
    
    /// <summary>
    /// Monitoring home with binaries and managed symbols.
    /// Example: linux-monitoring-home-linux-musl-arm64
    /// </summary>
    MonitoringHome,
    
    /// <summary>
    /// Universal symbols (native .debug files with flat structure).
    /// Example: linux-universal-symbols-linux-arm64
    /// Contains: Datadog.Linux.ApiWrapper.x64.debug, Datadog.Trace.ClrProfiler.Native.debug
    /// </summary>
    UniversalSymbols
}

/// <summary>
/// Maps platform information to Datadog artifact names and directories.
/// </summary>
public static class DatadogArtifactMapper
{
    /// <summary>
    /// Gets the platform suffix for artifact names based on OS, architecture, and libc.
    /// </summary>
    /// <param name="platform">Platform information from the dump.</param>
    /// <returns>Platform suffix like "linux-musl-arm64", "linux-x64", "win-x64".</returns>
    public static string GetPlatformSuffix(PlatformInfo platform)
    {
        var os = platform.Os.ToLowerInvariant();
        var arch = NormalizeArchitecture(platform.Architecture);
        
        return os switch
        {
            "linux" => platform.IsAlpine == true ? $"linux-musl-{arch}" : $"linux-{arch}",
            "windows" => $"win-{arch}",
            "macos" or "osx" => $"osx-{arch}",
            _ => $"{os}-{arch}"
        };
    }
    
    /// <summary>
    /// Gets the artifact names to download for a given platform.
    /// </summary>
    /// <param name="platform">Platform information from the dump.</param>
    /// <returns>Dictionary mapping artifact type to artifact name.</returns>
    public static Dictionary<DatadogArtifactType, string> GetArtifactNames(PlatformInfo platform)
    {
        var suffix = GetPlatformSuffix(platform);
        var os = platform.Os.ToLowerInvariant();
        var arch = NormalizeArchitecture(platform.Architecture);
        var osPrefix = GetOsPrefix(os);
        
        // Windows uses simpler artifact names without full platform suffix
        if (os == "windows")
        {
            return new Dictionary<DatadogArtifactType, string>
            {
                [DatadogArtifactType.MonitoringHome] = "windows-monitoring-home",
                [DatadogArtifactType.TracerSymbols] = "windows-tracer-symbols",
                [DatadogArtifactType.ProfilerSymbols] = "windows-profiler-symbols"
            };
        }
        
        // Linux and macOS use full platform suffix
        var artifacts = new Dictionary<DatadogArtifactType, string>
        {
            // Monitoring home - contains managed assemblies with TFM folders
            [DatadogArtifactType.MonitoringHome] = $"{osPrefix}-monitoring-home-{suffix}",
            
            // Tracer symbols - native .debug files
            [DatadogArtifactType.TracerSymbols] = $"{osPrefix}-tracer-symbols-{suffix}",
            
            // Profiler symbols - native .debug files
            [DatadogArtifactType.ProfilerSymbols] = $"{osPrefix}-profiler-symbols-{suffix}",
        };
        
        // Universal symbols only for Linux - contains native .debug files
        // Note: Universal symbols use just arch without musl suffix (flat structure)
        if (os == "linux")
        {
            artifacts[DatadogArtifactType.UniversalSymbols] = $"{osPrefix}-universal-symbols-linux-{arch}";
        }
        
        return artifacts;
    }
    
    /// <summary>
    /// Gets the target TFM folder based on the runtime target framework.
    /// </summary>
    /// <param name="targetFramework">Target framework from the dump (e.g., ".NET 6.0", ".NET Core 3.1").</param>
    /// <returns>TFM folder name (e.g., "net6.0", "netcoreapp3.1", "netstandard2.0").</returns>
    public static string GetTargetTfmFolder(string targetFramework)
    {
        if (string.IsNullOrEmpty(targetFramework))
            return "net6.0"; // Default
        
        var tf = targetFramework.ToLowerInvariant().Trim();
        
        // .NET 8.0, .NET 9.0 -> net8.0, net9.0 (use net6.0 as fallback)
        if (tf.Contains("8.0") || tf.Contains("9.0") || tf.Contains("10.0"))
            return "net8.0";
        
        // .NET 6.0, .NET 7.0 -> net6.0
        if (tf.Contains("6.0") || tf.Contains("7.0"))
            return "net6.0";
        
        // .NET 5.0 -> net6.0 (closest compatible)
        if (tf.Contains("5.0"))
            return "net6.0";
        
        // .NET Core 3.1, 3.0 -> netcoreapp3.1
        if (tf.Contains("core 3.") || tf.Contains("core3.") || tf.Contains("coreapp3."))
            return "netcoreapp3.1";
        
        // .NET Core 2.x -> netstandard2.0
        if (tf.Contains("core 2.") || tf.Contains("core2.") || tf.Contains("coreapp2."))
            return "netstandard2.0";
        
        // .NET Framework -> netstandard2.0
        if (tf.Contains("framework") || tf.StartsWith("4."))
            return "netstandard2.0";
        
        // Default to net6.0 for modern runtimes
        return "net6.0";
    }
    
    /// <summary>
    /// Normalizes architecture string to match artifact naming.
    /// </summary>
    private static string NormalizeArchitecture(string architecture)
    {
        var arch = architecture.ToLowerInvariant();
        
        return arch switch
        {
            "x64" or "amd64" => "x64",
            "x86" or "i386" or "i686" => "x86",
            "arm64" or "aarch64" => "arm64",
            "arm" or "armv7" => "arm",
            _ => arch
        };
    }
    
    /// <summary>
    /// Gets the OS prefix for artifact names.
    /// </summary>
    private static string GetOsPrefix(string os)
    {
        return os switch
        {
            "linux" => "linux",
            "windows" => "windows",
            "macos" or "osx" => "macos",
            _ => os
        };
    }
}

