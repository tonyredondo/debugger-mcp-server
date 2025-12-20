using System.Reflection;
using DebuggerMcp.Controllers;
using Xunit;

namespace DebuggerMcp.Tests.Controllers;

public class DumpAnalyzerToolDiscoveryTests
{
    [Fact]
    public void FindDotnetSymbolTool_WhenInPath_ReturnsToolPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"DumpAnalyzerToolDiscoveryTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var toolPath = Path.Combine(tempDir, "dotnet-symbol");
            File.WriteAllText(toolPath, "#!/bin/sh\necho hi\n");

            var originalPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", tempDir + Path.PathSeparator + (originalPath ?? string.Empty));

                var found = InvokeFindDotnetSymbolTool();
                Assert.Equal(toolPath, found);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FindDotnetSymbolTool_WhenInUserDotnetTools_ReturnsToolPath()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"DumpAnalyzerToolDiscoveryTests_home_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        try
        {
            var toolsDir = Path.Combine(tempHome, ".dotnet", "tools");
            Directory.CreateDirectory(toolsDir);

            var toolPath = Path.Combine(toolsDir, "dotnet-symbol");
            File.WriteAllText(toolPath, "#!/bin/sh\necho hi\n");

            var originalHome = Environment.GetEnvironmentVariable("HOME");
            var originalPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("HOME", tempHome);
                Environment.SetEnvironmentVariable("PATH", string.Empty);

                var found = InvokeFindDotnetSymbolTool();
                Assert.Equal(toolPath, found);
            }
            finally
            {
                Environment.SetEnvironmentVariable("HOME", originalHome);
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }
        }
        finally
        {
            try { Directory.Delete(tempHome, recursive: true); } catch { }
        }
    }

    private static string? InvokeFindDotnetSymbolTool()
    {
        var method = typeof(DumpAnalyzer).GetMethod("FindDotnetSymbolTool", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, Array.Empty<object>()) as string;
    }
}

