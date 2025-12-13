using DebuggerMcp.Configuration;
using Xunit;

namespace DebuggerMcp.Tests.Configuration;

/// <summary>
/// Tests for <see cref="HostInfo"/> helpers.
/// </summary>
public class HostInfoTests
{
    [Fact]
    public void ParseOsReleaseLines_WithAlpine_ReturnsIsAlpineTrue()
    {
        // Arrange
        var lines = new[]
        {
            "NAME=\"Alpine Linux\"",
            "ID=alpine",
            "VERSION_ID=3.20.2",
            "PRETTY_NAME=\"Alpine Linux v3.20\""
        };

        // Act
        var (distro, version, isAlpine) = HostInfo.ParseOsReleaseLines(lines);

        // Assert
        Assert.Equal("Alpine", distro);
        Assert.Equal("3.20.2", version);
        Assert.True(isAlpine);
    }

    [Fact]
    public void ParseOsReleaseLines_WithUbuntu_ReturnsDistroAndVersion()
    {
        // Arrange
        var lines = new[]
        {
            "ID=ubuntu",
            "VERSION_ID='24.04'",
            "PRETTY_NAME=\"Ubuntu 24.04 LTS\""
        };

        // Act
        var (distro, version, isAlpine) = HostInfo.ParseOsReleaseLines(lines);

        // Assert
        Assert.Equal("Ubuntu", distro);
        Assert.Equal("24.04", version);
        Assert.False(isAlpine);
    }

    [Fact]
    public void ParseOsReleaseLines_WhenIdUnknown_UsesPrettyNameFallback()
    {
        // Arrange
        var lines = new[]
        {
            "ID=someos",
            "VERSION_ID=1.2.3",
            "PRETTY_NAME=\"SomeOS Deluxe\""
        };

        // Act
        var (distro, version, isAlpine) = HostInfo.ParseOsReleaseLines(lines);

        // Assert
        Assert.Equal("SomeOS Deluxe", distro);
        Assert.Equal("1.2.3", version);
        Assert.False(isAlpine);
    }

    [Theory]
    [InlineData("10:cpuset:/docker/abc", true)]
    [InlineData("12:memory:/kubepods/burstable", true)]
    [InlineData("1:name=systemd:/containerd/xyz", true)]
    [InlineData("10:cpuset:/DoCkEr/abc", true)]
    [InlineData("0::/", false)]
    public void DetectDockerFromCgroupContent_DetectsKnownMarkers(string content, bool expected)
    {
        // Act
        var result = HostInfo.DetectDockerFromCgroupContent(content);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetInstalledRuntimesFromPaths_WhenDirectoriesExist_ReturnsVersions()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), $"HostInfoTests_{Guid.NewGuid():N}");
        var basePath = Path.Combine(root, "shared", "Microsoft.NETCore.App");
        Directory.CreateDirectory(Path.Combine(basePath, "9.0.0"));
        Directory.CreateDirectory(Path.Combine(basePath, "8.0.11"));

        try
        {
            // Act
            var runtimes = HostInfo.GetInstalledRuntimesFromPaths(new[] { basePath });

            // Assert
            Assert.Contains("9.0.0", runtimes);
            Assert.Contains("8.0.11", runtimes);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GetInstalledRuntimesFromPaths_WhenMultiplePathsContainDuplicates_Deduplicates()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), $"HostInfoTests_{Guid.NewGuid():N}");
        var basePath1 = Path.Combine(root, "a", "shared", "Microsoft.NETCore.App");
        var basePath2 = Path.Combine(root, "b", "shared", "Microsoft.NETCore.App");

        Directory.CreateDirectory(Path.Combine(basePath1, "9.0.0"));
        Directory.CreateDirectory(Path.Combine(basePath1, "8.0.11"));
        Directory.CreateDirectory(Path.Combine(basePath2, "9.0.0"));

        try
        {
            // Act
            var runtimes = HostInfo.GetInstalledRuntimesFromPaths(new[] { basePath1, basePath2 });

            // Assert
            Assert.Contains("9.0.0", runtimes);
            Assert.Contains("8.0.11", runtimes);
            Assert.Equal(2, runtimes.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(true, "Alpine", "3.20.2", "x64", "Alpine Linux 3.20.2 (x64)")]
    [InlineData(false, "Debian", "12", "arm64", "Debian 12 (arm64)")]
    public void Description_WhenLinuxDistributionKnown_ReturnsExpected(
        bool isAlpine,
        string distro,
        string version,
        string arch,
        string expected)
    {
        var host = new HostInfo
        {
            OsName = "Linux",
            OsVersion = "0",
            LinuxDistribution = distro,
            LinuxDistributionVersion = version,
            IsAlpine = isAlpine,
            Architecture = arch
        };

        Assert.Equal(expected, host.Description);
    }

    [Fact]
    public void Description_WhenNoLinuxDistribution_FallsBackToOsNameAndVersion()
    {
        var host = new HostInfo
        {
            OsName = "Windows",
            OsVersion = "11.0",
            Architecture = "x64",
            LinuxDistribution = null,
            LinuxDistributionVersion = null,
            IsAlpine = false
        };

        Assert.Equal("Windows 11.0 (x64)", host.Description);
    }

    [Fact]
    public void Detect_ReturnsHostInfoWithExpectedFields()
    {
        var host = HostInfo.Detect();

        Assert.False(string.IsNullOrWhiteSpace(host.OsName));
        Assert.False(string.IsNullOrWhiteSpace(host.OsVersion));
        Assert.False(string.IsNullOrWhiteSpace(host.Architecture));
        Assert.False(string.IsNullOrWhiteSpace(host.DotNetVersion));
        Assert.False(string.IsNullOrWhiteSpace(host.DebuggerType));
        Assert.False(string.IsNullOrWhiteSpace(host.Hostname));
        Assert.NotEqual(default, host.ServerStartTime);
        Assert.NotNull(host.InstalledRuntimes);
    }

    [Fact]
    public void GetInstalledRuntimesFromPaths_WhenPathMissing_ReturnsEmpty()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"HostInfoTests_missing_{Guid.NewGuid():N}");
        var runtimes = HostInfo.GetInstalledRuntimesFromPaths(new[] { missingPath });

        Assert.Empty(runtimes);
    }
}
