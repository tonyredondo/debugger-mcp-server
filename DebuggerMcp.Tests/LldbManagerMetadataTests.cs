using DebuggerMcp.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for <see cref="LldbManager"/> metadata helpers.
/// </summary>
public class LldbManagerMetadataTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetMetadataPathForDump_WithEmpty_ReturnsNull(string? dumpPath)
    {
        Assert.Null(LldbManager.GetMetadataPathForDump(dumpPath));
    }

    [Fact]
    public void GetMetadataPathForDump_WithValidPath_ReturnsSiblingJsonPath()
    {
        var path = "/tmp/mydump.core";
        Assert.Equal("/tmp/mydump.json", LldbManager.GetMetadataPathForDump(path));
    }

    [Fact]
    public void TrySaveAndLoadDumpMetadata_RoundTrips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerMetadataTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var metadataPath = Path.Combine(tempDir, "dump.json");
            var metadata = new DumpMetadata
            {
                DumpId = "d1",
                UserId = "u1",
                FileName = "foo.core",
                Size = 123,
                UploadedAt = DateTime.UtcNow,
                RuntimeVersion = "9.0.10",
                SymbolFiles = ["a/b/c", "d/e"]
            };

            Assert.True(LldbManager.TrySaveDumpMetadata(metadataPath, metadata, NullLogger<LldbManager>.Instance));

            var loaded = LldbManager.TryLoadDumpMetadata(metadataPath, NullLogger<LldbManager>.Instance);
            Assert.NotNull(loaded);
            Assert.Equal("d1", loaded.DumpId);
            Assert.Equal("u1", loaded.UserId);
            Assert.Equal("foo.core", loaded.FileName);
            Assert.Equal(123, loaded.Size);
            Assert.Equal("9.0.10", loaded.RuntimeVersion);
            Assert.NotNull(loaded.SymbolFiles);
            Assert.Equal(2, loaded.SymbolFiles.Count);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void TryLoadDumpMetadata_WithInvalidJson_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerMetadataTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var metadataPath = Path.Combine(tempDir, "bad.json");
            File.WriteAllText(metadataPath, "{ invalid json");

            var loaded = LldbManager.TryLoadDumpMetadata(metadataPath, NullLogger<LldbManager>.Instance);
            Assert.Null(loaded);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void TrySaveRuntimeVersionToMetadata_WhenMissingMetadata_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerMetadataTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var metadataPath = Path.Combine(tempDir, "missing.json");

            Assert.False(LldbManager.TrySaveRuntimeVersionToMetadata(metadataPath, "9.0.10", NullLogger<LldbManager>.Instance));
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void TrySaveAndLoadRuntimeVersion_UpdatesExistingMetadata()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerMetadataTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var metadataPath = Path.Combine(tempDir, "dump.json");
            Assert.True(LldbManager.TrySaveDumpMetadata(metadataPath, new DumpMetadata { DumpId = "d1", UserId = "u1" }));

            Assert.True(LldbManager.TrySaveRuntimeVersionToMetadata(metadataPath, "9.0.10", NullLogger<LldbManager>.Instance));
            Assert.Equal("9.0.10", LldbManager.TryLoadRuntimeVersionFromMetadata(metadataPath, NullLogger<LldbManager>.Instance));
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void TrySaveAndLoadSymbolFiles_UpdatesExistingMetadata()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerMetadataTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var metadataPath = Path.Combine(tempDir, "dump.json");
            Assert.True(LldbManager.TrySaveDumpMetadata(metadataPath, new DumpMetadata { DumpId = "d1", UserId = "u1" }));

            var files = new List<string> { "a/b", "c" };
            Assert.True(LldbManager.TrySaveSymbolFilesToMetadata(metadataPath, files, NullLogger<LldbManager>.Instance));

            var loaded = LldbManager.TryLoadSymbolFilesFromMetadata(metadataPath, NullLogger<LldbManager>.Instance);
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("a/b", loaded[0]);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }
}

