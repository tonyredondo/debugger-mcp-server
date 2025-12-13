using DebuggerMcp.Models;
using System.Reflection;
using Xunit;

namespace DebuggerMcp.Tests.Models;

/// <summary>
/// Tests for <see cref="ServerCapabilities"/>.
/// </summary>
public class ServerCapabilitiesTests
{
    [Fact]
    public void Constructor_PopulatesDefaults()
    {
        var caps = new ServerCapabilities();

        Assert.False(string.IsNullOrWhiteSpace(caps.Platform));
        Assert.False(string.IsNullOrWhiteSpace(caps.Architecture));
        Assert.False(string.IsNullOrWhiteSpace(caps.RuntimeVersion));
        Assert.False(string.IsNullOrWhiteSpace(caps.DebuggerType));
        Assert.False(string.IsNullOrWhiteSpace(caps.Hostname));
        Assert.False(string.IsNullOrWhiteSpace(caps.Version));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("windows", caps.Platform);
            Assert.Equal("WinDbg", caps.DebuggerType);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Equal("linux", caps.Platform);
            Assert.Equal("LLDB", caps.DebuggerType);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("macos", caps.Platform);
            Assert.Equal("LLDB", caps.DebuggerType);
        }
    }

    [Fact]
    public void PrivateDetectors_ReturnExpectedValues()
    {
        var getPlatform = typeof(ServerCapabilities).GetMethod("GetPlatform", BindingFlags.Static | BindingFlags.NonPublic);
        var getArchitecture = typeof(ServerCapabilities).GetMethod("GetArchitecture", BindingFlags.Static | BindingFlags.NonPublic);
        var detectAlpine = typeof(ServerCapabilities).GetMethod("DetectAlpine", BindingFlags.Static | BindingFlags.NonPublic);
        var detectDistribution = typeof(ServerCapabilities).GetMethod("DetectDistribution", BindingFlags.Static | BindingFlags.NonPublic);
        var getServerVersion = typeof(ServerCapabilities).GetMethod("GetServerVersion", BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(getPlatform);
        Assert.NotNull(getArchitecture);
        Assert.NotNull(detectAlpine);
        Assert.NotNull(detectDistribution);
        Assert.NotNull(getServerVersion);

        var platform = (string)getPlatform!.Invoke(null, Array.Empty<object>())!;
        var arch = (string)getArchitecture!.Invoke(null, Array.Empty<object>())!;
        var alpine = (bool)detectAlpine!.Invoke(null, Array.Empty<object>())!;
        var distribution = (string?)detectDistribution!.Invoke(null, Array.Empty<object>());
        var version = (string)getServerVersion!.Invoke(null, Array.Empty<object>())!;

        Assert.False(string.IsNullOrWhiteSpace(platform));
        Assert.False(string.IsNullOrWhiteSpace(arch));
        Assert.False(string.IsNullOrWhiteSpace(version));

        if (!OperatingSystem.IsLinux())
        {
            Assert.False(alpine);
            Assert.Null(distribution);
        }
    }
}
