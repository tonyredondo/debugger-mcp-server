using System;
using System.Collections.Generic;
using System.IO;
using DebuggerMcp;
using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for symbol download caching decision helpers in <see cref="LldbManager"/>.
/// </summary>
public class LldbManagerSymbolCacheDecisionTests
{
    [Fact]
    public void ShouldSkipSymbolDownload_WhenNoCache_ReturnsFalse()
    {
        Assert.False(LldbManager.ShouldSkipSymbolDownload(null, "/tmp", out var missing));
        Assert.Empty(missing);

        Assert.False(LldbManager.ShouldSkipSymbolDownload(new List<string>(), "/tmp", out missing));
        Assert.Empty(missing);
    }

    [Fact]
    public void ShouldSkipSymbolDownload_WhenAllFilesExist_ReturnsTrue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerSymbolCache_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "a", "b"));
            File.WriteAllText(Path.Combine(tempDir, "a", "b", "c.txt"), "x");

            var cached = new List<string> { Path.Combine("a", "b", "c.txt") };
            Assert.True(LldbManager.ShouldSkipSymbolDownload(cached, tempDir, out var missing));
            Assert.Empty(missing);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ShouldSkipSymbolDownload_WhenMissingFiles_ReturnsFalseAndReportsMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerSymbolCache_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var cached = new List<string> { "exists.txt", "missing.txt" };
            File.WriteAllText(Path.Combine(tempDir, "exists.txt"), "x");

            Assert.False(LldbManager.ShouldSkipSymbolDownload(cached, tempDir, out var missing));
            Assert.Single(missing);
            Assert.Equal("missing.txt", missing[0]);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GetMissingCachedFiles_WhenDirectoryMissing_ReturnsAll()
    {
        var cached = new List<string> { "a.txt", "b.txt" };
        var missing = LldbManager.GetMissingCachedFiles(cached, "/this/does/not/exist");
        Assert.Equal(cached, missing);
    }
}

