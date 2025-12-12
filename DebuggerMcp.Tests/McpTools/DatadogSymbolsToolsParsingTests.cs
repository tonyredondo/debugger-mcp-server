using DebuggerMcp.McpTools;

namespace DebuggerMcp.Tests.McpTools;

/// <summary>
/// Unit tests for pure helper parsing logic in <see cref="DatadogSymbolsTools"/>.
/// </summary>
public class DatadogSymbolsToolsParsingTests
{
    [Theory]
    [InlineData("/lib/ld-musl-aarch64.so.1", "Linux", "arm64", true, "musl")]
    [InlineData("/lib64/ld-linux-x86-64.so.2", "Linux", "x64", false, "glibc")]
    [InlineData("C:\\Windows\\System32\\kernel32.dll", "Windows", "x64", false, null)]
    [InlineData("/usr/lib/dyld", "macOS", "x64", false, null)]
    public void DetectPlatformFromDebuggerOutput_ExtractsExpectedValues(
        string output,
        string expectedOs,
        string expectedArch,
        bool expectedIsAlpine,
        string? expectedLibc)
    {
        var (platform, detectedArch, detectedAlpine) = DatadogSymbolsTools.DetectPlatformFromDebuggerOutput(output);

        Assert.Equal(expectedOs, platform.Os);
        Assert.Equal(expectedArch, platform.Architecture);
        Assert.Equal(expectedIsAlpine, platform.IsAlpine == true);

        if (expectedLibc != null)
        {
            Assert.Equal(expectedLibc, platform.LibcType);
        }

        // When a platform string contains obvious markers, we should detect at least arch.
        Assert.True(detectedArch || expectedArch == "x64");
        Assert.Equal(expectedIsAlpine, detectedAlpine);
    }

    [Fact]
    public void DetectPlatformFromDebuggerOutput_WhenEmpty_UsesDefaults()
    {
        var (platform, detectedArch, detectedAlpine) = DatadogSymbolsTools.DetectPlatformFromDebuggerOutput(string.Empty);

        Assert.Equal("Linux", platform.Os);
        Assert.Equal("x64", platform.Architecture);
        Assert.False(detectedArch);
        Assert.False(detectedAlpine);
    }

    [Fact]
    public void BuildDatadogSymbolsDirectory_BuildsExpectedPath()
    {
        var dir = DatadogSymbolsTools.BuildDatadogSymbolsDirectory(
            dumpStoragePath: "/dumps",
            sanitizedUserId: "user",
            currentDumpId: "mydump.dmp");

        Assert.Equal("/dumps/user/.symbols_mydump/.datadog", dir);
    }
}

