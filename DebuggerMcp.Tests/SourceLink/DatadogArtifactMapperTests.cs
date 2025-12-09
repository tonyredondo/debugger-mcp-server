using DebuggerMcp.Analysis;
using DebuggerMcp.SourceLink;
using Xunit;

namespace DebuggerMcp.Tests.SourceLink;

/// <summary>
/// Tests for the DatadogArtifactMapper class.
/// </summary>
public class DatadogArtifactMapperTests
{
    [Theory]
    [InlineData("Linux", "x64", false, "linux-x64")]
    [InlineData("Linux", "arm64", false, "linux-arm64")]
    [InlineData("Linux", "x64", true, "linux-musl-x64")]
    [InlineData("Linux", "arm64", true, "linux-musl-arm64")]
    [InlineData("Windows", "x64", false, "win-x64")]
    [InlineData("Windows", "x86", false, "win-x86")]
    [InlineData("macOS", "x64", false, "osx-x64")]
    [InlineData("macOS", "arm64", false, "osx-arm64")]
    public void GetPlatformSuffix_ReturnsCorrectSuffix(string os, string arch, bool isAlpine, string expected)
    {
        // Arrange
        var platform = new PlatformInfo
        {
            Os = os,
            Architecture = arch,
            IsAlpine = isAlpine
        };

        // Act
        var result = DatadogArtifactMapper.GetPlatformSuffix(platform);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("x64", "x64")]
    [InlineData("amd64", "x64")]
    [InlineData("x86", "x86")]
    [InlineData("i386", "x86")]
    [InlineData("arm64", "arm64")]
    [InlineData("aarch64", "arm64")]
    public void GetPlatformSuffix_NormalizesArchitecture(string inputArch, string expectedInSuffix)
    {
        // Arrange
        var platform = new PlatformInfo
        {
            Os = "Linux",
            Architecture = inputArch,
            IsAlpine = false
        };

        // Act
        var result = DatadogArtifactMapper.GetPlatformSuffix(platform);

        // Assert
        Assert.Contains(expectedInSuffix, result);
    }

    [Fact]
    public void GetArtifactNames_LinuxGlibc_ReturnsCorrectArtifacts()
    {
        // Arrange
        var platform = new PlatformInfo
        {
            Os = "Linux",
            Architecture = "x64",
            IsAlpine = false
        };

        // Act
        var artifacts = DatadogArtifactMapper.GetArtifactNames(platform);

        // Assert
        Assert.Equal(4, artifacts.Count);
        Assert.Contains(DatadogArtifactType.MonitoringHome, artifacts.Keys);
        Assert.Contains(DatadogArtifactType.TracerSymbols, artifacts.Keys);
        Assert.Contains(DatadogArtifactType.ProfilerSymbols, artifacts.Keys);
        Assert.Contains(DatadogArtifactType.UniversalSymbols, artifacts.Keys);

        Assert.Equal("linux-monitoring-home-linux-x64", artifacts[DatadogArtifactType.MonitoringHome]);
        Assert.Equal("linux-tracer-symbols-linux-x64", artifacts[DatadogArtifactType.TracerSymbols]);
        Assert.Equal("linux-profiler-symbols-linux-x64", artifacts[DatadogArtifactType.ProfilerSymbols]);
        Assert.Equal("linux-universal-symbols-linux-x64", artifacts[DatadogArtifactType.UniversalSymbols]);
    }

    [Fact]
    public void GetArtifactNames_LinuxAlpine_ReturnsMuslArtifacts()
    {
        // Arrange
        var platform = new PlatformInfo
        {
            Os = "Linux",
            Architecture = "arm64",
            IsAlpine = true
        };

        // Act
        var artifacts = DatadogArtifactMapper.GetArtifactNames(platform);

        // Assert
        Assert.Contains("linux-musl-arm64", artifacts[DatadogArtifactType.MonitoringHome]);
        Assert.Contains("linux-musl-arm64", artifacts[DatadogArtifactType.TracerSymbols]);
        // Universal symbols don't have musl in the name
        Assert.Contains("linux-arm64", artifacts[DatadogArtifactType.UniversalSymbols]);
    }

    [Fact]
    public void GetArtifactNames_Windows_DoesNotIncludeUniversalSymbols()
    {
        // Arrange
        var platform = new PlatformInfo
        {
            Os = "Windows",
            Architecture = "x64"
        };

        // Act
        var artifacts = DatadogArtifactMapper.GetArtifactNames(platform);

        // Assert
        Assert.Equal(3, artifacts.Count);
        Assert.DoesNotContain(DatadogArtifactType.UniversalSymbols, artifacts.Keys);
        // Windows uses simple names without platform suffix
        Assert.Equal("windows-monitoring-home", artifacts[DatadogArtifactType.MonitoringHome]);
        Assert.Equal("windows-tracer-symbols", artifacts[DatadogArtifactType.TracerSymbols]);
    }

    [Theory]
    [InlineData(".NET 6.0", "net6.0")]
    [InlineData(".NET 7.0", "net6.0")]
    [InlineData(".NET 8.0", "net8.0")]
    [InlineData(".NET 9.0", "net8.0")]
    [InlineData(".NET Core 3.1", "netcoreapp3.1")]
    [InlineData(".NET Core 3.0", "netcoreapp3.1")]
    [InlineData(".NET Core 2.1", "netstandard2.0")]
    [InlineData(".NET Framework 4.8", "netstandard2.0")]
    [InlineData("", "net6.0")] // Default
    [InlineData(null, "net6.0")] // Default
    public void GetTargetTfmFolder_ReturnsCorrectTfm(string? targetFramework, string expected)
    {
        // Act
        var result = DatadogArtifactMapper.GetTargetTfmFolder(targetFramework!);

        // Assert
        Assert.Equal(expected, result);
    }
}

