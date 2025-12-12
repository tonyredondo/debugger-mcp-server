using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DebuggerMcp;
using DebuggerMcp.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for metadata helper functions in <see cref="LldbManager"/>.
/// </summary>
public class LldbManagerMetadataHelpersTests
{
    [Fact]
    public void GetMetadataPathForDump_WithValidDumpPath_ReturnsJsonPath()
    {
        var path = LldbManager.GetMetadataPathForDump("/tmp/dump.dmp");
        Assert.Equal("/tmp/dump.json", path);
    }

    [Fact]
    public void GetMetadataPathForDump_WhenNoExtension_ReturnsJsonPath()
    {
        var path = LldbManager.GetMetadataPathForDump("/tmp/dump");
        Assert.Equal("/tmp/dump.json", path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetMetadataPathForDump_WithInvalidDumpPath_ReturnsNull(string? dumpPath)
    {
        Assert.Null(LldbManager.GetMetadataPathForDump(dumpPath));
    }

    [Fact]
    public void TryLoadDumpMetadata_WhenMissing_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerMetadata_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var metadataPath = Path.Combine(tempDir, "missing.json");
            Assert.Null(LldbManager.TryLoadDumpMetadata(metadataPath, NullLogger.Instance));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryLoadDumpMetadata_WhenInvalidJson_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerMetadata_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var metadataPath = Path.Combine(tempDir, "bad.json");
            File.WriteAllText(metadataPath, "{not-json");

            Assert.Null(LldbManager.TryLoadDumpMetadata(metadataPath, NullLogger.Instance));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TrySaveAndLoadRuntimeVersion_RoundTrips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerMetadata_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var metadataPath = Path.Combine(tempDir, "dump.json");
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(new DumpMetadata(), new JsonSerializerOptions { WriteIndented = true }));

            Assert.True(LldbManager.TrySaveRuntimeVersionToMetadata(metadataPath, "9.0.10", NullLogger.Instance));
            Assert.Equal("9.0.10", LldbManager.TryLoadRuntimeVersionFromMetadata(metadataPath, NullLogger.Instance));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TrySaveAndLoadSymbolFiles_RoundTrips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerMetadata_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var metadataPath = Path.Combine(tempDir, "dump.json");
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(new DumpMetadata(), new JsonSerializerOptions { WriteIndented = true }));

            var files = new List<string> { "a/b/c.txt", "x.so" };
            Assert.True(LldbManager.TrySaveSymbolFilesToMetadata(metadataPath, files, NullLogger.Instance));

            var loaded = LldbManager.TryLoadSymbolFilesFromMetadata(metadataPath, NullLogger.Instance);
            Assert.NotNull(loaded);
            Assert.Equal(files, loaded);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TrySaveDumpMetadata_WhenPathIsDirectory_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerMetadata_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Assert.False(LldbManager.TrySaveDumpMetadata(tempDir, new DumpMetadata(), NullLogger.Instance));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TrySaveRuntimeVersionToMetadata_WhenMetadataMissing_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerMetadata_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var metadataPath = Path.Combine(tempDir, "missing.json");
            Assert.False(LldbManager.TrySaveRuntimeVersionToMetadata(metadataPath, "9.0.10", NullLogger.Instance));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryLoadRuntimeVersionFromMetadata_WhenRuntimeVersionMissing_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerMetadata_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var metadataPath = Path.Combine(tempDir, "dump.json");
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(new DumpMetadata(), new JsonSerializerOptions { WriteIndented = true }));

            Assert.Null(LldbManager.TryLoadRuntimeVersionFromMetadata(metadataPath, NullLogger.Instance));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TrySaveSymbolFilesToMetadata_WhenMetadataMissing_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerMetadata_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var metadataPath = Path.Combine(tempDir, "missing.json");
            Assert.False(LldbManager.TrySaveSymbolFilesToMetadata(metadataPath, new List<string> { "a.txt" }, NullLogger.Instance));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
