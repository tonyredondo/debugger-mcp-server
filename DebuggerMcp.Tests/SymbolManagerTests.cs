using Xunit;
using DebuggerMcp;
using System;
using System.IO;

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
}
