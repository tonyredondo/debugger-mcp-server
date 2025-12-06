using DebuggerMcp.SourceLink;

namespace DebuggerMcp.Tests.SourceLink;

/// <summary>
/// Tests for DatadogArtifactProcessor.
/// </summary>
public class DatadogArtifactProcessorTests : IDisposable
{
    private readonly string _testDir;
    
    public DatadogArtifactProcessorTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"ddproc-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }
    
    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); }
            catch { /* ignore cleanup errors */ }
        }
    }
    
    [Fact]
    public async Task MergeArtifactsAsync_WithEmptyZipPaths_ReturnsEmptyResult()
    {
        // Arrange
        var processor = new DatadogArtifactProcessor();
        var outputDir = Path.Combine(_testDir, "output");
        
        // Act
        var result = await processor.MergeArtifactsAsync(
            new Dictionary<DatadogArtifactType, string>(),
            outputDir,
            "linux-musl-arm64",
            "net6.0");
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(0, result.TotalFilesExtracted);
    }
    
    [Fact]
    public async Task MergeArtifactsAsync_WithNonExistentFiles_ReturnsEmptyResult()
    {
        // Arrange
        var processor = new DatadogArtifactProcessor();
        var outputDir = Path.Combine(_testDir, "output");
        var nonExistentZips = new Dictionary<DatadogArtifactType, string>
        {
            { DatadogArtifactType.TracerSymbols, "/nonexistent/path1.zip" },
            { DatadogArtifactType.ProfilerSymbols, "/nonexistent/path2.zip" }
        };
        
        // Act
        var result = await processor.MergeArtifactsAsync(
            nonExistentZips,
            outputDir,
            "linux-musl-arm64",
            "net6.0");
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(0, result.TotalFilesExtracted);
    }
    
    [Fact]
    public async Task MergeArtifactsAsync_CreatesOutputDirectories()
    {
        // Arrange
        var processor = new DatadogArtifactProcessor();
        var outputDir = Path.Combine(_testDir, "new-output-dir");
        
        // Act
        await processor.MergeArtifactsAsync(
            new Dictionary<DatadogArtifactType, string>(),
            outputDir,
            "linux-musl-arm64",
            "net6.0");
        
        // Assert
        Assert.True(Directory.Exists(outputDir));
    }
    
    [Theory]
    [InlineData("linux-musl-arm64", "net6.0")]
    [InlineData("linux-x64", "netcoreapp3.1")]
    [InlineData("win-x64", "netstandard2.0")]
    [InlineData("osx-arm64", "net8.0")]
    public async Task MergeArtifactsAsync_AcceptsVariousPlatformAndTfmCombinations(string platform, string tfm)
    {
        // Arrange
        var processor = new DatadogArtifactProcessor();
        var outputDir = Path.Combine(_testDir, $"output-{platform}-{tfm}");
        
        // Act
        var result = await processor.MergeArtifactsAsync(
            new Dictionary<DatadogArtifactType, string>(),
            outputDir,
            platform,
            tfm);
        
        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.SymbolDirectory);
        Assert.NotNull(result.NativeSymbolDirectory);
        Assert.NotNull(result.ManagedSymbolDirectory);
    }
    
    [Fact]
    public async Task MergeArtifactsAsync_SetsCorrectDirectoryPaths()
    {
        // Arrange
        var processor = new DatadogArtifactProcessor();
        var outputDir = Path.Combine(_testDir, "output");
        
        // Act
        var result = await processor.MergeArtifactsAsync(
            new Dictionary<DatadogArtifactType, string>(),
            outputDir,
            "linux-musl-arm64",
            "net6.0");
        
        // Assert - symbol directory is a subdirectory of output
        Assert.NotNull(result.SymbolDirectory);
        Assert.StartsWith(outputDir, result.SymbolDirectory);
        Assert.NotNull(result.NativeSymbolDirectory);
        Assert.NotNull(result.ManagedSymbolDirectory);
    }
    
    [Fact]
    public async Task MergeArtifactsAsync_TracksArtifactTypes()
    {
        // Arrange
        var processor = new DatadogArtifactProcessor();
        var outputDir = Path.Combine(_testDir, "output");
        
        // Act
        var result = await processor.MergeArtifactsAsync(
            new Dictionary<DatadogArtifactType, string>(),
            outputDir,
            "linux-x64",
            "net6.0");
        
        // Assert
        Assert.NotNull(result.DebugSymbolFiles);
        Assert.NotNull(result.PdbFiles);
        Assert.NotNull(result.NativeLibraries);
    }
    
    [Theory]
    [InlineData(DatadogArtifactType.TracerSymbols)]
    [InlineData(DatadogArtifactType.ProfilerSymbols)]
    [InlineData(DatadogArtifactType.MonitoringHome)]
    [InlineData(DatadogArtifactType.UniversalSymbols)]
    public async Task MergeArtifactsAsync_HandlesIndividualArtifactTypes(DatadogArtifactType artifactType)
    {
        // Arrange
        var processor = new DatadogArtifactProcessor();
        var outputDir = Path.Combine(_testDir, $"output-{artifactType}");
        var artifacts = new Dictionary<DatadogArtifactType, string>
        {
            { artifactType, "/nonexistent/test.zip" }
        };
        
        // Act - should not throw, just skip missing files
        var result = await processor.MergeArtifactsAsync(
            artifacts,
            outputDir,
            "linux-x64",
            "net6.0");
        
        // Assert
        Assert.NotNull(result);
    }
}
