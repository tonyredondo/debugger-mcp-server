using System.Reflection;
using DebuggerMcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests;

public class LldbManagerCachedFilesExistCoverageTests
{
    [Fact]
    public void AllCachedFilesExist_WhenAllFilesPresent_ReturnsTrue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerCachedFilesExistCoverageTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "a.txt"), "x");
            File.WriteAllText(Path.Combine(tempDir, "b.txt"), "x");

            using var manager = new LldbManager(NullLogger<LldbManager>.Instance);

            var method = typeof(LldbManager).GetMethod("AllCachedFilesExist", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var ok = (bool)method!.Invoke(manager, new object[] { new List<string> { "a.txt", "b.txt" }, tempDir })!;
            Assert.True(ok);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AllCachedFilesExist_WhenFilesMissing_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerCachedFilesExistCoverageTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "a.txt"), "x");

            using var manager = new LldbManager(NullLogger<LldbManager>.Instance);

            var method = typeof(LldbManager).GetMethod("AllCachedFilesExist", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var ok = (bool)method!.Invoke(manager, new object[] { new List<string> { "a.txt", "b.txt" }, tempDir })!;
            Assert.False(ok);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}

