using DebuggerMcp.SourceLink;
using System.IO.Compression;

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

    [Fact]
    public async Task MergeArtifactsAsync_ExtractsTracerSymbolsFromPlatformFolder_ToNativeDirectory()
    {
        // Arrange
        var processor = new DatadogArtifactProcessor();
        var outputDir = Path.Combine(_testDir, "output-tracer");
        var zipPath = CreateZip(
            "tracer-symbols.zip",
            new Dictionary<string, byte[]>
            {
                { "linux-musl-arm64/Datadog.Trace.ClrProfiler.Native.debug", "abc"u8.ToArray() },
                { "linux-x64/ShouldNotExtract.debug", "def"u8.ToArray() },
                { "linux-musl-arm64/notes.txt", "ignore"u8.ToArray() }
            });

        // Act
        var result = await processor.MergeArtifactsAsync(
            new Dictionary<DatadogArtifactType, string> { { DatadogArtifactType.TracerSymbols, zipPath } },
            outputDir,
            "linux-musl-arm64",
            "net6.0");

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.NativeSymbolDirectory);
        Assert.Single(result.DebugSymbolFiles);
        Assert.Empty(result.PdbFiles);
        Assert.Empty(result.NativeLibraries);

        var extractedPath = result.DebugSymbolFiles.Single();
        Assert.StartsWith(result.NativeSymbolDirectory, extractedPath);
        Assert.EndsWith("Datadog.Trace.ClrProfiler.Native.debug", extractedPath);
        Assert.True(File.Exists(extractedPath));
    }

    [Fact]
    public async Task MergeArtifactsAsync_MonitoringHome_ExtractsManagedAndNative_ToCorrectDirectories()
    {
        // Arrange
        var processor = new DatadogArtifactProcessor();
        var outputDir = Path.Combine(_testDir, "output-monitoring");
        var zipPath = CreateZip(
            "monitoring-home.zip",
            new Dictionary<string, byte[]>
            {
                { "net6.0/Datadog.Trace.pdb", "pdb"u8.ToArray() },
                { "net6.0/Datadog.Trace.dll", "dll"u8.ToArray() },
                { "linux-musl-arm64/Datadog.Tracer.Native.so", "so"u8.ToArray() },
                { "linux-musl-arm64/Datadog.Tracer.Native.dll", "nd"u8.ToArray() }
            });

        // Act
        var result = await processor.MergeArtifactsAsync(
            new Dictionary<DatadogArtifactType, string> { { DatadogArtifactType.MonitoringHome, zipPath } },
            outputDir,
            "linux-musl-arm64",
            "net6.0");

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.ManagedSymbolDirectory);
        Assert.NotNull(result.NativeSymbolDirectory);

        Assert.Single(result.PdbFiles);
        Assert.All(result.PdbFiles, p => Assert.StartsWith(result.ManagedSymbolDirectory, p));

        // Note: the processor tracks both native binaries and managed .dll files in NativeLibraries.
        Assert.Equal(3, result.NativeLibraries.Count);
        Assert.Contains(result.NativeLibraries, p => p.StartsWith(result.ManagedSymbolDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, result.NativeLibraries.Count(p => p.StartsWith(result.NativeSymbolDirectory, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task MergeArtifactsAsync_UniversalSymbols_ExtractsRootAndShallowDebugFiles_SkipsDeepPaths()
    {
        // Arrange
        var processor = new DatadogArtifactProcessor();
        var outputDir = Path.Combine(_testDir, "output-universal");
        var zipPath = CreateZip(
            "universal-symbols.zip",
            new Dictionary<string, byte[]>
            {
                { "Datadog.Linux.ApiWrapper.x64.debug", "a"u8.ToArray() },
                { "artifact/Datadog.Profiler.Native.debug", "b"u8.ToArray() },
                { "deep/nested/NotExtracted.debug", "c"u8.ToArray() }
            });

        // Act
        var result = await processor.MergeArtifactsAsync(
            new Dictionary<DatadogArtifactType, string> { { DatadogArtifactType.UniversalSymbols, zipPath } },
            outputDir,
            "linux-x64",
            "net6.0");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(2, result.DebugSymbolFiles.Count);
        Assert.DoesNotContain(result.DebugSymbolFiles, p => p.Contains("NotExtracted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MergeArtifactsAsync_WhenExistingFileSizeMatches_SkipsOverwrite()
    {
        // Arrange
        var processor = new DatadogArtifactProcessor();
        var outputDir = Path.Combine(_testDir, "output-existing");
        var platform = "linux-musl-arm64";
        var tfm = "net6.0";

        var nativeDir = Path.Combine(outputDir, $"symbols-{platform}", platform);
        Directory.CreateDirectory(nativeDir);

        var existingPath = Path.Combine(nativeDir, "existing.debug");
        File.WriteAllBytes(existingPath, "AAAA"u8.ToArray());

        var zipPath = CreateZip(
            "existing.zip",
            new Dictionary<string, byte[]>
            {
                { "linux-musl-arm64/existing.debug", "BBBB"u8.ToArray() }
            });

        // Act
        var result = await processor.MergeArtifactsAsync(
            new Dictionary<DatadogArtifactType, string> { { DatadogArtifactType.TracerSymbols, zipPath } },
            outputDir,
            platform,
            tfm);

        // Assert
        Assert.True(result.Success);
        Assert.Single(result.DebugSymbolFiles);
        Assert.Equal("AAAA", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(existingPath)));
    }

    [Fact]
    public async Task MergeArtifactsAsync_WithInvalidZip_SetsErrorMessage()
    {
        // Arrange
        var processor = new DatadogArtifactProcessor();
        var outputDir = Path.Combine(_testDir, "output-invalid");
        var zipPath = Path.Combine(_testDir, "invalid.zip");
        File.WriteAllText(zipPath, "not a zip");

        // Act
        var result = await processor.MergeArtifactsAsync(
            new Dictionary<DatadogArtifactType, string> { { DatadogArtifactType.TracerSymbols, zipPath } },
            outputDir,
            "linux-musl-arm64",
            "net6.0");

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    private string CreateZip(string name, IReadOnlyDictionary<string, byte[]> entries)
    {
        var zipPath = Path.Combine(_testDir, name);

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (entryName, bytes) in entries)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(bytes);
        }

        return zipPath;
    }
}
