using DebuggerMcp.SourceLink;
using Xunit;

namespace DebuggerMcp.Tests.SourceLink;

/// <summary>
/// Tests for the AzurePipelinesCache class.
/// </summary>
public class AzurePipelinesCacheTests
{
    [Fact]
    public void GetBuildCacheKey_ReturnsLowercaseKey()
    {
        // Act
        var key = AzurePipelinesCache.GetBuildCacheKey("DatadogHQ", "DD-Trace-DOTNET", "ABC123DEF");

        // Assert
        Assert.Equal("datadoghq/dd-trace-dotnet/abc123def", key);
    }

    [Fact]
    public void GetArtifactCacheKey_ReturnsLowercaseKey()
    {
        // Act
        var key = AzurePipelinesCache.GetArtifactCacheKey("DatadogHQ", "DD-Trace-DOTNET", 12345);

        // Assert
        Assert.Equal("datadoghq/dd-trace-dotnet/12345", key);
    }

    [Fact]
    public void GetSymbolsCacheKey_ReturnsLowercaseKey()
    {
        // Act
        var key = AzurePipelinesCache.GetSymbolsCacheKey("ABC123DEF", "linux-musl-arm64");

        // Assert
        Assert.Equal("abc123def/linux-musl-arm64", key);
    }

    [Fact]
    public void TryGetBuild_ReturnsFalse_WhenNotCached()
    {
        // Arrange
        var cache = new AzurePipelinesCache();

        // Act
        var result = cache.TryGetBuild("org", "proj", "commit123", out var build);

        // Assert
        Assert.False(result);
        Assert.Null(build);
    }

    [Fact]
    public void SetBuild_AndTryGetBuild_ReturnsCachedBuild()
    {
        // Arrange
        var cache = new AzurePipelinesCache();
        var build = new AzurePipelinesBuildInfo
        {
            Id = 12345,
            BuildNumber = "1.2.3",
            SourceVersion = "abc123"
        };

        // Act
        cache.SetBuild("org", "proj", "abc123", build);
        var result = cache.TryGetBuild("org", "proj", "abc123", out var retrieved);

        // Assert
        Assert.True(result);
        Assert.NotNull(retrieved);
        Assert.Equal(12345, retrieved.Id);
        Assert.Equal("1.2.3", retrieved.BuildNumber);
    }

    [Fact]
    public void SetBuild_CanCacheNullForNotFound()
    {
        // Arrange
        var cache = new AzurePipelinesCache();

        // Act
        cache.SetBuild("org", "proj", "notfound", null);
        var result = cache.TryGetBuild("org", "proj", "notfound", out var build);

        // Assert
        Assert.True(result); // Key exists
        Assert.Null(build);  // Value is null (not found)
    }

    [Fact]
    public void TryGetArtifacts_ReturnsFalse_WhenNotCached()
    {
        // Arrange
        var cache = new AzurePipelinesCache();

        // Act
        var result = cache.TryGetArtifacts("org", "proj", 12345, out var artifacts);

        // Assert
        Assert.False(result);
        Assert.Null(artifacts);
    }

    [Fact]
    public void SetArtifacts_AndTryGetArtifacts_ReturnsCachedArtifacts()
    {
        // Arrange
        var cache = new AzurePipelinesCache();
        var artifacts = new List<AzurePipelinesArtifact>
        {
            new() { Id = 1, Name = "artifact1" },
            new() { Id = 2, Name = "artifact2" }
        };

        // Act
        cache.SetArtifacts("org", "proj", 12345, artifacts);
        var result = cache.TryGetArtifacts("org", "proj", 12345, out var retrieved);

        // Assert
        Assert.True(result);
        Assert.NotNull(retrieved);
        Assert.Equal(2, retrieved.Count);
    }

    [Fact]
    public void HasDownloadedSymbols_ReturnsFalse_WhenNotDownloaded()
    {
        // Arrange
        var cache = new AzurePipelinesCache();

        // Act
        var result = cache.HasDownloadedSymbols("commit123", "linux-x64", out var dir);

        // Assert
        Assert.False(result);
        Assert.Null(dir);
    }

    [Fact]
    public void SetDownloadedSymbols_AndHasDownloadedSymbols_TracksDownload()
    {
        // Arrange
        var cache = new AzurePipelinesCache();
        var tempDir = Path.Combine(Path.GetTempPath(), $"test_symbols_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Act
            cache.SetDownloadedSymbols("commit123", "linux-x64", tempDir);
            var result = cache.HasDownloadedSymbols("commit123", "linux-x64", out var dir);

            // Assert
            Assert.True(result);
            Assert.Equal(tempDir, dir);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void HasDownloadedSymbols_ReturnsFalse_WhenDirectoryDeleted()
    {
        // Arrange
        var cache = new AzurePipelinesCache();
        var tempDir = Path.Combine(Path.GetTempPath(), $"test_symbols_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        cache.SetDownloadedSymbols("commit123", "linux-x64", tempDir);
        
        // Delete the directory
        Directory.Delete(tempDir, true);

        // Act
        var result = cache.HasDownloadedSymbols("commit123", "linux-x64", out var dir);

        // Assert
        Assert.False(result);
        Assert.Null(dir);
    }

    [Fact]
    public void Load_ReturnsEmptyCache_WhenNoFile()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"cache_test_{Guid.NewGuid()}");

        try
        {
            // Act
            var cache = AzurePipelinesCache.Load(tempDir);

            // Assert
            Assert.NotNull(cache);
            Assert.Empty(cache.Builds);
            Assert.Empty(cache.Artifacts);
            Assert.Empty(cache.DownloadedSymbols);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Save_AndLoad_PersistsCache()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"cache_test_{Guid.NewGuid()}");

        try
        {
            Directory.CreateDirectory(tempDir);
            var cache = new AzurePipelinesCache();
            cache.SetBuild("org", "proj", "abc123", new AzurePipelinesBuildInfo
            {
                Id = 12345,
                BuildNumber = "1.2.3"
            });

            // Act
            cache.Save(tempDir);
            var loaded = AzurePipelinesCache.Load(tempDir);

            // Assert
            Assert.True(loaded.TryGetBuild("org", "proj", "abc123", out var build));
            Assert.NotNull(build);
            Assert.Equal(12345, build.Id);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Load_ReturnsEmptyCache_WhenDirectoryIsNull()
    {
        // Act
        var cache = AzurePipelinesCache.Load(null);

        // Assert
        Assert.NotNull(cache);
        Assert.Empty(cache.Builds);
    }
}

