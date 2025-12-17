using DebuggerMcp.SourceLink;
using Xunit;

namespace DebuggerMcp.Tests.SourceLink;

/// <summary>
/// Tests for the DatadogSymbolLoader class.
/// </summary>
public class DatadogSymbolLoaderTests
{
    [Fact]
    public void FindManagedPdbDirectories_ReturnsEmpty_WhenCacheDirectoryDoesNotExist()
    {
        // Arrange
        var missing = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}");

        // Act
        var dirs = DatadogSymbolLoader.FindManagedPdbDirectories(missing);

        // Assert
        Assert.NotNull(dirs);
        Assert.Empty(dirs);
    }

    [Fact]
    public void FindManagedPdbDirectories_ReturnsEmpty_WhenDatadogRootDoesNotExist()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"dd_cache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Act
            var dirs = DatadogSymbolLoader.FindManagedPdbDirectories(tempDir);

            // Assert
            Assert.NotNull(dirs);
            Assert.Empty(dirs);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void FindManagedPdbDirectories_ReturnsDistinctParentDirectories_WithSorting()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"dd_cache_{Guid.NewGuid():N}");
        var datadogRoot = Path.Combine(tempDir, ".datadog");
        var net60 = Path.Combine(datadogRoot, "symbols-linux-x64", "net6.0");
        var net70 = Path.Combine(datadogRoot, "symbols-linux-x64", "net7.0");
        Directory.CreateDirectory(net60);
        Directory.CreateDirectory(net70);

        File.WriteAllText(Path.Combine(net60, "Datadog.Trace.pdb"), "");
        File.WriteAllText(Path.Combine(net60, "Datadog.Trace.MSBuild.pdb"), "");
        File.WriteAllText(Path.Combine(net70, "Datadog.Trace.pdb"), "");

        try
        {
            // Act
            var dirs = DatadogSymbolLoader.FindManagedPdbDirectories(tempDir);

            // Assert
            Assert.NotNull(dirs);
            Assert.Equal(2, dirs.Count);
            Assert.Equal(net60, dirs[0], ignoreCase: true);
            Assert.Equal(net70, dirs[1], ignoreCase: true);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GenerateLldbCommands_ReturnsEmpty_WhenMergeResultIsNull()
    {
        // Arrange
        var loader = new DatadogSymbolLoader();

        // Act
        var commands = loader.GenerateLldbCommands(null!);

        // Assert
        Assert.NotNull(commands);
        Assert.Empty(commands);
    }

    [Fact]
    public void GenerateLldbCommands_ReturnsEmpty_WhenSymbolDirectoryIsNull()
    {
        // Arrange
        var loader = new DatadogSymbolLoader();
        var mergeResult = new ArtifactMergeResult
        {
            SymbolDirectory = null
        };

        // Act
        var commands = loader.GenerateLldbCommands(mergeResult);

        // Assert
        Assert.NotNull(commands);
        Assert.Empty(commands);
    }

    [Fact]
    public void GenerateLldbCommands_AddsSearchPath_WhenNativeDirectoryExists()
    {
        // Arrange
        var loader = new DatadogSymbolLoader();
        var tempDir = Path.Combine(Path.GetTempPath(), $"test_symbols_{Guid.NewGuid()}");
        var nativeDir = Path.Combine(tempDir, "linux-musl-arm64");
        Directory.CreateDirectory(nativeDir);

        try
        {
            var mergeResult = new ArtifactMergeResult
            {
                SymbolDirectory = tempDir,
                NativeSymbolDirectory = nativeDir
            };

            // Act
            var commands = loader.GenerateLldbCommands(mergeResult);

            // Assert
            Assert.Contains(commands, c => c.Contains("settings append target.debug-file-search-paths"));
            Assert.Contains(commands, c => c.Contains(nativeDir));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GenerateLldbCommands_AddsTargetSymbolsAdd_ForDebugFiles()
    {
        // Arrange
        var loader = new DatadogSymbolLoader();
        var tempDir = Path.Combine(Path.GetTempPath(), $"test_symbols_{Guid.NewGuid()}");
        var nativeDir = Path.Combine(tempDir, "linux-musl-arm64");
        Directory.CreateDirectory(nativeDir);
        var debugFile = Path.Combine(nativeDir, "Datadog.Tracer.Native.debug");
        File.WriteAllText(debugFile, "test");

        try
        {
            var mergeResult = new ArtifactMergeResult
            {
                SymbolDirectory = tempDir,
                NativeSymbolDirectory = nativeDir,
                DebugSymbolFiles = { debugFile }
            };

            // Act
            var commands = loader.GenerateLldbCommands(mergeResult);

            // Assert
            Assert.Contains(commands, c => c.Contains("target symbols add") && c.Contains(debugFile));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GenerateLldbCommands_AddsSetsymbolserver_ForManagedDirectory()
    {
        // Arrange
        var loader = new DatadogSymbolLoader();
        var tempDir = Path.Combine(Path.GetTempPath(), $"test_symbols_{Guid.NewGuid()}");
        var managedDir = Path.Combine(tempDir, "net6.0");
        Directory.CreateDirectory(managedDir);

        try
        {
            var mergeResult = new ArtifactMergeResult
            {
                SymbolDirectory = tempDir,
                ManagedSymbolDirectory = managedDir
            };

            // Act
            var commands = loader.GenerateLldbCommands(mergeResult);

            // Assert
            Assert.Contains(commands, c => c.Contains("setsymbolserver -directory") && c.Contains(managedDir));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task LoadSymbolsAsync_ReturnsFailure_WhenNoCommands()
    {
        // Arrange
        var loader = new DatadogSymbolLoader();
        var mergeResult = new ArtifactMergeResult { SymbolDirectory = null };

        // Act
        var result = await loader.LoadSymbolsAsync(mergeResult, _ => "");

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task LoadSymbolsAsync_ExecutesCommands_WhenDirectoriesExist()
    {
        // Arrange
        var loader = new DatadogSymbolLoader();
        var tempDir = Path.Combine(Path.GetTempPath(), $"test_symbols_{Guid.NewGuid()}");
        var managedDir = Path.Combine(tempDir, "net6.0");
        Directory.CreateDirectory(managedDir);
        var executedCommands = new List<string>();

        try
        {
            var mergeResult = new ArtifactMergeResult
            {
                SymbolDirectory = tempDir,
                ManagedSymbolDirectory = managedDir
            };

            // Act
            var result = await loader.LoadSymbolsAsync(mergeResult, cmd =>
            {
                executedCommands.Add(cmd);
                return "OK";
            });

            // Assert
            Assert.True(result.Success);
            Assert.NotEmpty(executedCommands);
            Assert.Single(result.ManagedSymbolPaths);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
