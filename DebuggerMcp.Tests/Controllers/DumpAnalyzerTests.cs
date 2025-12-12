using DebuggerMcp.Controllers;

namespace DebuggerMcp.Tests.Controllers;

public class DumpAnalyzerTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("some text", false)]
    [InlineData("/lib/ld-musl-x86_64.so.1", true)]
    [InlineData("linux-musl-arm64", true)]
    [InlineData("/usr/lib/musl-foo.so", true)]
    public void DetectIsAlpineFromVerifyCoreOutput_DetectsMusl(string output, bool expected)
    {
        Assert.Equal(expected, DumpAnalyzer.DetectIsAlpineFromVerifyCoreOutput(output));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("no runtime here", null)]
    [InlineData("/usr/share/dotnet/shared/Microsoft.NETCore.App/9.0.10/System.Runtime.dll", "9.0.10")]
    [InlineData("/dotnet/shared/Microsoft.NETCore.App/8.0.5/libcoreclr.so", "8.0.5")]
    public void TryExtractRuntimeVersionFromVerifyCoreOutput_ExtractsVersion(string output, string? expected)
    {
        Assert.Equal(expected, DumpAnalyzer.TryExtractRuntimeVersionFromVerifyCoreOutput(output));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("ELF 64-bit LSB core file, ARM aarch64, version 1 (GNU/Linux)", "arm64")]
    [InlineData("ELF 64-bit LSB core file, x86-64, version 1 (GNU/Linux)", "x64")]
    [InlineData("PE32+ executable (console) x86-64", "x64")]
    [InlineData("ELF 32-bit LSB core file, Intel 80386", "x86")]
    public void TryExtractArchitectureFromFileOutput_ExtractsArchitecture(string output, string? expected)
    {
        Assert.Equal(expected, DumpAnalyzer.TryExtractArchitectureFromFileOutput(output));
    }
}
