using DebuggerMcp.Analysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Unit tests for platform detection helpers in <see cref="ClrMdAnalyzer"/>.
/// </summary>
public class ClrMdAnalyzerPlatformDetectionTests
{
    [Fact]
    public void DetectIsAlpineFromNativeModulePaths_WhenMuslFound_ReturnsTrue()
    {
        var modules = new[]
        {
            "/lib/ld-musl-x86_64.so.1",
            "/usr/share/dotnet/shared/Microsoft.NETCore.App/9.0.10/libcoreclr.so"
        };

        Assert.True(ClrMdAnalyzer.DetectIsAlpineFromNativeModulePaths(modules, NullLogger.Instance));
    }

    [Fact]
    public void DetectIsAlpineFromNativeModulePaths_WhenNoMusl_ReturnsFalse()
    {
        var modules = new[]
        {
            "/lib/x86_64-linux-gnu/libc.so.6",
            "/usr/share/dotnet/shared/Microsoft.NETCore.App/9.0.10/libcoreclr.so"
        };

        Assert.False(ClrMdAnalyzer.DetectIsAlpineFromNativeModulePaths(modules, NullLogger.Instance));
    }

    [Theory]
    [InlineData("/lib/ld-musl-aarch64.so.1", "arm64")]
    [InlineData("/lib/x86_64-linux-gnu/libc.so.6", "x64")]
    [InlineData("/lib/i386-linux-gnu/libc.so.6", "x86")]
    public void DetectArchitectureFromNativeModulePaths_DetectsExpected(string modulePath, string expected)
    {
        var modules = new[] { modulePath };

        Assert.Equal(expected, ClrMdAnalyzer.DetectArchitectureFromNativeModulePaths(modules, NullLogger.Instance));
    }
}

