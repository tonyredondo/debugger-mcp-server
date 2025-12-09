using DebuggerMcp.Analysis;
using DebuggerMcp.SourceLink;

namespace DebuggerMcp.Tests.SourceLink;

/// <summary>
/// Tests for AzurePipelinesResolver.
/// </summary>
public class AzurePipelinesResolverTests
{
    [Fact]
    public void Constructor_WithNullCacheDirectory_UsesDefault()
    {
        // Arrange & Act
        using var resolver = new AzurePipelinesResolver(null, null);

        // Assert - no exception thrown
        Assert.NotNull(resolver);
    }

    [Fact]
    public void Constructor_WithCacheDirectory_StoresIt()
    {
        // Arrange
        var cacheDir = "/tmp/test-cache";

        // Act
        using var resolver = new AzurePipelinesResolver(cacheDir, null);

        // Assert - no exception thrown
        Assert.NotNull(resolver);
    }

    [Fact]
    public async Task FindBuildByCommitAsync_WithEmptyCommit_ReturnsNull()
    {
        // Arrange
        using var resolver = new AzurePipelinesResolver(null, null);

        // Act
        var result = await resolver.FindBuildByCommitAsync("org", "project", "");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task FindBuildByCommitAsync_WithNullCommit_ReturnsNull()
    {
        // Arrange
        using var resolver = new AzurePipelinesResolver(null, null);

        // Act
        var result = await resolver.FindBuildByCommitAsync("org", "project", null!);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ListArtifactsAsync_WithInvalidBuildId_ReturnsEmptyList()
    {
        // Arrange
        using var resolver = new AzurePipelinesResolver(null, null);

        // Act
        var result = await resolver.ListArtifactsAsync("org", "project", -1);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void SaveCache_DoesNotThrow()
    {
        // Arrange
        using var resolver = new AzurePipelinesResolver(null, null);

        // Act & Assert - no exception
        resolver.SaveCache();
    }

    [Fact]
    public void Dispose_MultipleTimesDoesNotThrow()
    {
        // Arrange
        var resolver = new AzurePipelinesResolver(null, null);

        // Act & Assert - no exception
        resolver.Dispose();
        resolver.Dispose();
    }

    [Theory]
    [InlineData("", "", "commit")]
    [InlineData("org", "", "commit")]
    [InlineData("", "project", "commit")]
    public async Task FindBuildByCommitAsync_WithMissingOrgOrProject_ReturnsNull(string org, string project, string commit)
    {
        // Arrange
        using var resolver = new AzurePipelinesResolver(null, null);

        // Act
        var result = await resolver.FindBuildByCommitAsync(org, project, commit);

        // Assert
        Assert.Null(result);
    }
}
