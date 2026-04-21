using Xunit;
using DebuggerMcp;
using DebuggerMcp.Symbols;
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
    /// Verifies that dump-scoped symbol lookup refuses to cross user boundaries when the same dump ID
    /// appears under multiple user-owned symbol directories.
    /// </summary>
    [Fact]
    public void ListDumpSymbols_WhenDumpIdIsAmbiguousAcrossUsers_ThrowsInvalidOperationException()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(tempRoot, "cache");
        var dumpRoot = Path.Combine(tempRoot, "dumps");
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(dumpRoot);

        var userOneSymbols = Path.Combine(dumpRoot, "user-one", ".symbols_same-dump");
        var userTwoSymbols = Path.Combine(dumpRoot, "user-two", ".symbols_same-dump");
        Directory.CreateDirectory(userOneSymbols);
        Directory.CreateDirectory(userTwoSymbols);
        File.WriteAllText(Path.Combine(userOneSymbols, "first.pdb"), "one");
        File.WriteAllText(Path.Combine(userTwoSymbols, "second.pdb"), "two");

        var manager = new SymbolManager(symbolCacheBasePath: cacheRoot, dumpStorageBasePath: dumpRoot);

        var ex = Assert.Throws<InvalidOperationException>(() => manager.ListDumpSymbols("same-dump"));
        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that deleting symbols refuses to touch multiple users when the dump ID is ambiguous.
    /// </summary>
    [Fact]
    public void DeleteDumpSymbols_WhenDumpIdIsAmbiguousAcrossUsers_ThrowsAndLeavesDirectoriesIntact()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(tempRoot, "cache");
        var dumpRoot = Path.Combine(tempRoot, "dumps");
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(dumpRoot);

        var userOneSymbols = Path.Combine(dumpRoot, "user-one", ".symbols_same-dump");
        var userTwoSymbols = Path.Combine(dumpRoot, "user-two", ".symbols_same-dump");
        Directory.CreateDirectory(userOneSymbols);
        Directory.CreateDirectory(userTwoSymbols);
        File.WriteAllText(Path.Combine(userOneSymbols, "first.pdb"), "one");
        File.WriteAllText(Path.Combine(userTwoSymbols, "second.pdb"), "two");

        var manager = new SymbolManager(symbolCacheBasePath: cacheRoot, dumpStorageBasePath: dumpRoot);

        var ex = Assert.Throws<InvalidOperationException>(() => manager.DeleteDumpSymbols("same-dump"));
        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(userOneSymbols));
        Assert.True(Directory.Exists(userTwoSymbols));
    }

    /// <summary>
    /// Verifies that passing a dump path from a sibling directory that merely shares the same prefix
    /// as the managed dump root does not cause user-scoped symbol lookup to cross into the managed root.
    /// </summary>
    [Fact]
    public void GetDumpSymbolDirectories_WhenDumpPathSharesRootPrefix_DoesNotTreatSiblingDirectoryAsManagedOwner()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(tempRoot, "cache");
        var dumpRoot = Path.Combine(tempRoot, "dumps");
        var siblingDumpRoot = Path.Combine(tempRoot, "dumps-archive");
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(dumpRoot);
        Directory.CreateDirectory(siblingDumpRoot);

        var dumpId = "same-dump";
        var outsideDumpDir = Path.Combine(siblingDumpRoot, "user-one");
        Directory.CreateDirectory(outsideDumpDir);
        var outsideDumpPath = Path.Combine(outsideDumpDir, $"{dumpId}.dmp");
        File.WriteAllText(outsideDumpPath, "dump");

        var outsideSymbols = Path.Combine(outsideDumpDir, $".symbols_{dumpId}");
        Directory.CreateDirectory(outsideSymbols);
        File.WriteAllText(Path.Combine(outsideSymbols, "outside.pdb"), "outside");

        var managedSymbols = Path.Combine(dumpRoot, "user-one", $".symbols_{dumpId}");
        Directory.CreateDirectory(managedSymbols);
        File.WriteAllText(Path.Combine(managedSymbols, "managed.pdb"), "managed");

        var manager = new SymbolManager(symbolCacheBasePath: cacheRoot, dumpStorageBasePath: dumpRoot);

        var directories = manager.GetDumpSymbolDirectories(dumpId, dumpPath: outsideDumpPath);

        Assert.Contains(outsideSymbols, directories, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(managedSymbols, directories, StringComparer.OrdinalIgnoreCase);
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
    /// Verifies that user-configured symbol inputs survive later dump-specific recomposition.
    /// </summary>
    [Fact]
    public void ConfigureSessionSymbolPaths_PreservesPersistedInputs_WhenAddingDumpContext()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempRoot);

        var userId = "user1";
        var sessionId = "test-session";
        var dumpId = "test-dump";
        var userDumpDir = Path.Combine(tempRoot, userId);
        Directory.CreateDirectory(userDumpDir);

        var dumpPath = Path.Combine(userDumpDir, $"{dumpId}.dmp");
        File.WriteAllText(dumpPath, "dump");

        var symbolDir = Path.Combine(userDumpDir, $".symbols_{dumpId}");
        Directory.CreateDirectory(symbolDir);
        File.WriteAllText(Path.Combine(symbolDir, "test.pdb"), "x");

        var manager = new SymbolManager(symbolCacheBasePath: tempRoot, dumpStorageBasePath: tempRoot);
        manager.RehydrateSessionSymbolConfiguration(sessionId, new PersistedSessionSymbolConfiguration
        {
            AdditionalLocalDirectories = new List<string> { "/local/symbols" },
            AdditionalRemoteUrls = new List<string> { "https://custom-symbols.com" }
        });

        manager.ConfigureSessionSymbolPaths(sessionId, dumpId, includeMicrosoftSymbols: true, userId: userId, dumpPath: dumpPath);

        var paths = manager.GetSessionSymbolPaths(sessionId);
        Assert.Contains(SymbolManager.MicrosoftSymbolServer, paths);
        Assert.Contains(symbolDir, paths);
        Assert.Contains("/local/symbols", paths);
        Assert.Contains("https://custom-symbols.com", paths);
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

    /// <summary>
    /// Verifies that storing a symbol file fails clearly when the same dump ID is present under more
    /// than one user-owned dump directory.
    /// </summary>
    [Fact]
    public async Task StoreSymbolFileAsync_WhenDumpIdExistsForMultipleUsers_ThrowsInvalidOperationException()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(tempRoot, "cache");
        var dumpRoot = Path.Combine(tempRoot, "dumps");
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(dumpRoot);

        Directory.CreateDirectory(Path.Combine(dumpRoot, "user-one"));
        Directory.CreateDirectory(Path.Combine(dumpRoot, "user-two"));
        File.WriteAllText(Path.Combine(dumpRoot, "user-one", "same-dump.dmp"), "one");
        File.WriteAllText(Path.Combine(dumpRoot, "user-two", "same-dump.dmp"), "two");

        var manager = new SymbolManager(symbolCacheBasePath: cacheRoot, dumpStorageBasePath: dumpRoot);

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StoreSymbolFileAsync("same-dump", "test.pdb", stream));
        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that listing symbols aggregates dump-scoped and root-level fallback directories for
    /// one resolved dump instead of arbitrarily choosing only one of them.
    /// </summary>
    [Fact]
    public void ListDumpSymbols_WhenScopedAndRootFallbackSymbolsExist_ReturnsSymbolsFromBothScopes()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(tempRoot, "cache");
        var dumpRoot = Path.Combine(tempRoot, "dumps");
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(dumpRoot);

        var userDir = Path.Combine(dumpRoot, "user-one");
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(userDir, "same-dump.dmp"), "dump");

        var scopedSymbols = Path.Combine(userDir, ".symbols_same-dump");
        var rootFallbackSymbols = Path.Combine(dumpRoot, ".symbols_same-dump");
        Directory.CreateDirectory(scopedSymbols);
        Directory.CreateDirectory(rootFallbackSymbols);
        File.WriteAllText(Path.Combine(scopedSymbols, "first.pdb"), "one");
        File.WriteAllText(Path.Combine(rootFallbackSymbols, "second.pdb"), "two");

        var manager = new SymbolManager(symbolCacheBasePath: cacheRoot, dumpStorageBasePath: dumpRoot);

        var symbols = manager.ListDumpSymbols("same-dump");
        Assert.Contains("first.pdb", symbols);
        Assert.Contains("second.pdb", symbols);
    }

    private static void AddZipEntry(System.IO.Compression.ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, Encoding.UTF8, leaveOpen: false);
        writer.Write(content);
    }
}
