using Xunit;
using DebuggerMcp;
using System;
using System.IO;
using System.Text;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for the SymbolManager class.
/// </summary>
public class SymbolManagerTests
{
    /// <summary>
    /// Verifies that SymbolManager can be instantiated with default path.
    /// </summary>
    [Fact]
    public void Constructor_CreatesInstance_WithDefaultPath()
    {
        // Act
        var manager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        // Assert
        Assert.NotNull(manager);
    }

    /// <summary>
    /// Verifies that SymbolManager can be instantiated with custom path.
    /// </summary>
    [Fact]
    public void Constructor_CreatesInstance_WithCustomPath()
    {
        // Arrange
        var customPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        // Act
        var manager = new SymbolManager(customPath);

        // Assert
        Assert.NotNull(manager);
    }

    /// <summary>
    /// Verifies that HasSymbols returns false for non-existent dumpId.
    /// </summary>
    [Fact]
    public void HasSymbols_ReturnsFalse_ForNonExistentDumpId()
    {
        // Arrange
        var manager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        // Act
        var hasSymbols = manager.HasSymbols("non-existent-dump");

        // Assert
        Assert.False(hasSymbols);
    }

    /// <summary>
    /// Verifies that ListDumpSymbols returns empty list for non-existent dumpId.
    /// </summary>
    [Fact]
    public void ListDumpSymbols_ReturnsEmptyList_ForNonExistentDumpId()
    {
        // Arrange
        var manager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        // Act
        var symbols = manager.ListDumpSymbols("non-existent-dump");

        // Assert
        Assert.Empty(symbols);
    }

    /// <summary>
    /// Verifies that ConfigureSessionSymbolPaths works with dumpId.
    /// </summary>
    [Fact]
    public void ConfigureSessionSymbolPaths_ConfiguresPaths_WithDumpId()
    {
        // Arrange
        var manager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var sessionId = "test-session";
        var dumpId = "test-dump";

        // Act
        manager.ConfigureSessionSymbolPaths(sessionId, dumpId, includeMicrosoftSymbols: true);
        var paths = manager.GetSessionSymbolPaths(sessionId);

        // Assert
        Assert.NotEmpty(paths);
        Assert.Contains(SymbolManager.MicrosoftSymbolServer, paths);
    }

    /// <summary>
    /// Verifies that ConfigureSessionSymbolPaths works with additional paths.
    /// </summary>
    [Fact]
    public void ConfigureSessionSymbolPaths_ConfiguresPaths_WithAdditionalPaths()
    {
        // Arrange
        var manager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var sessionId = "test-session";
        var additionalPaths = "https://custom-symbols.com,/local/symbols";

        // Act
        manager.ConfigureSessionSymbolPaths(sessionId, dumpId: null, additionalPaths: additionalPaths, includeMicrosoftSymbols: false);
        var paths = manager.GetSessionSymbolPaths(sessionId);

        // Assert
        Assert.NotEmpty(paths);
        Assert.Contains("https://custom-symbols.com", paths);
        Assert.Contains("/local/symbols", paths);
    }

    /// <summary>
    /// Verifies that BuildWinDbgSymbolPath returns correct format.
    /// </summary>
    [Fact]
    public void BuildWinDbgSymbolPath_ReturnsCorrectFormat()
    {
        // Arrange
        var manager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var sessionId = "test-session";
        manager.ConfigureSessionSymbolPaths(sessionId, dumpId: null, additionalPaths: null, includeMicrosoftSymbols: true);

        // Act
        var symbolPath = manager.BuildWinDbgSymbolPath(sessionId);

        // Assert
        Assert.NotNull(symbolPath);
        Assert.Contains("srv*", symbolPath);
    }

    /// <summary>
    /// Verifies that BuildLldbSymbolPath returns correct format.
    /// </summary>
    [Fact]
    public void BuildLldbSymbolPath_ReturnsCorrectFormat()
    {
        // Arrange
        var manager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var sessionId = "test-session";
        manager.ConfigureSessionSymbolPaths(sessionId, dumpId: null, additionalPaths: "/path/to/symbols", includeMicrosoftSymbols: false);

        // Act
        var symbolPath = manager.BuildLldbSymbolPath(sessionId);

        // Assert
        Assert.NotNull(symbolPath);
        Assert.Contains("/path/to/symbols", symbolPath);
    }

    /// <summary>
    /// Verifies that ClearSessionSymbolPaths clears paths.
    /// </summary>
    [Fact]
    public void ClearSessionSymbolPaths_ClearsPaths()
    {
        // Arrange
        var manager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var sessionId = "test-session";
        manager.ConfigureSessionSymbolPaths(sessionId, dumpId: null, additionalPaths: "/path/to/symbols", includeMicrosoftSymbols: false);

        // Act
        manager.ClearSessionSymbolPaths(sessionId);
        var retrievedPaths = manager.GetSessionSymbolPaths(sessionId);

        // Assert
        Assert.Empty(retrievedPaths);
    }

    /// <summary>
    /// Verifies that Microsoft symbol server constant is defined.
    /// </summary>
    [Fact]
    public void MicrosoftSymbolServer_IsDefined()
    {
        // Assert
        Assert.NotNull(SymbolManager.MicrosoftSymbolServer);
        Assert.NotEmpty(SymbolManager.MicrosoftSymbolServer);
        Assert.StartsWith("https://", SymbolManager.MicrosoftSymbolServer);
    }

    /// <summary>
    /// Verifies that NuGet symbol server constant is defined.
    /// </summary>
    [Fact]
    public void NuGetSymbolServer_IsDefined()
    {
        // Assert
        Assert.NotNull(SymbolManager.NuGetSymbolServer);
        Assert.NotEmpty(SymbolManager.NuGetSymbolServer);
        Assert.StartsWith("https://", SymbolManager.NuGetSymbolServer);
    }

    [Fact]
    public async Task StoreSymbolFileAsync_WhenFileNameContainsPathTraversal_StripsPathAndStoresUnderSymbolsDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(tempRoot, "cache");
        var dumpRoot = Path.Combine(tempRoot, "dumps");
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(dumpRoot);

        var manager = new SymbolManager(symbolCacheBasePath: cacheRoot, dumpStorageBasePath: dumpRoot);
        var dumpId = "dump-1";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        var stored = await manager.StoreSymbolFileAsync(dumpId, "../evil.pdb", stream);

        var symbolsDir = Path.Combine(dumpRoot, ".symbols_dump-1");
        var expectedPath = Path.Combine(symbolsDir, "evil.pdb");

        Assert.Equal(Path.GetFullPath(expectedPath), Path.GetFullPath(stored));
        Assert.True(File.Exists(stored));
    }

    [Fact]
    public async Task StoreSymbolFileAsync_WhenFileNameIsWindowsPath_StripsPathAndStoresUnderSymbolsDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(tempRoot, "cache");
        var dumpRoot = Path.Combine(tempRoot, "dumps");
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(dumpRoot);

        var manager = new SymbolManager(symbolCacheBasePath: cacheRoot, dumpStorageBasePath: dumpRoot);
        var dumpId = "dump-2";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        var stored = await manager.StoreSymbolFileAsync(dumpId, @"C:\temp\sym.pdb", stream);

        var symbolsDir = Path.Combine(dumpRoot, ".symbols_dump-2");
        var expectedPath = Path.Combine(symbolsDir, "sym.pdb");

        Assert.Equal(Path.GetFullPath(expectedPath), Path.GetFullPath(stored));
        Assert.True(File.Exists(stored));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task StoreSymbolFileAsync_WhenFileNameInvalid_Throws(string fileName)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(tempRoot, "cache");
        var dumpRoot = Path.Combine(tempRoot, "dumps");
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(dumpRoot);

        var manager = new SymbolManager(symbolCacheBasePath: cacheRoot, dumpStorageBasePath: dumpRoot);

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        _ = await Assert.ThrowsAsync<ArgumentException>(() => manager.StoreSymbolFileAsync("dump-3", fileName, stream));
    }

    [Fact]
    public async Task StoreSymbolZipAsync_SkipsTraversalAndNonSymbolFiles_AndExtractsSymbolsAndDsymDwarf()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(tempRoot, "cache");
        var dumpRoot = Path.Combine(tempRoot, "dumps");
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(dumpRoot);

        var manager = new SymbolManager(symbolCacheBasePath: cacheRoot, dumpStorageBasePath: dumpRoot);

        await using var zipStream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            AddZipEntry(zip, "../evil.pdb", "EVIL");
            AddZipEntry(zip, "notes.txt", "NOT A SYMBOL");
            AddZipEntry(zip, "__MACOSX/._meta", "META");
            AddZipEntry(zip, "good/sym.pdb", "PDBDATA");
            AddZipEntry(zip, "bundle.dSYM/Contents/Resources/DWARF/MyApp", "DWARF");
        }
        zipStream.Position = 0;

        var result = await manager.StoreSymbolZipAsync("dump-zip-1", zipStream);

        Assert.Equal("dump-zip-1", result.DumpId);
        Assert.Contains("good/sym.pdb", result.ExtractedFiles);
        Assert.Contains("bundle.dSYM/Contents/Resources/DWARF/MyApp", result.ExtractedFiles);
        Assert.DoesNotContain(result.ExtractedFiles, p => p.Contains("evil", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.ExtractedFiles, p => p.EndsWith("notes.txt", StringComparison.OrdinalIgnoreCase));

        var symbolsDir = Path.Combine(dumpRoot, ".symbols_dump-zip-1");
        Assert.True(File.Exists(Path.Combine(symbolsDir, "good", "sym.pdb")));
        Assert.True(File.Exists(Path.Combine(symbolsDir, "bundle.dSYM", "Contents", "Resources", "DWARF", "MyApp")));
        Assert.False(File.Exists(Path.Combine(dumpRoot, "evil.pdb")));
    }

    private static void AddZipEntry(System.IO.Compression.ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, Encoding.UTF8, leaveOpen: false);
        writer.Write(content);
    }
}
