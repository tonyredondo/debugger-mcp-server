using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for the <see cref="LldbManager"/> class.
/// </summary>
public class LldbManagerTests
{
    [Fact]
    public void Constructor_CreatesInstanceWithoutInitialization()
    {
        // Act
        var manager = new LldbManager();

        // Assert
        Assert.NotNull(manager);
        Assert.False(manager.IsInitialized);
        Assert.False(manager.IsDumpOpen);
    }

    [Fact]
    public void DebuggerType_ReturnsLLDB()
    {
        // Arrange
        var manager = new LldbManager();

        // Act
        var type = manager.DebuggerType;

        // Assert
        Assert.Equal("LLDB", type);
    }

    [Fact]
    public void IsInitialized_ReturnsFalse_WhenNotInitialized()
    {
        // Arrange
        var manager = new LldbManager();

        // Act & Assert
        Assert.False(manager.IsInitialized);
    }

    [Fact]
    public void IsDumpOpen_ReturnsFalse_WhenNoDumpIsOpen()
    {
        // Arrange
        var manager = new LldbManager();

        // Act & Assert
        Assert.False(manager.IsDumpOpen);
    }

    [Fact]
    public void OpenDumpFile_ThrowsInvalidOperationException_WhenNotInitialized()
    {
        // Arrange
        var manager = new LldbManager();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            manager.OpenDumpFile("/tmp/test.core"));
    }

    [Fact]
    public void OpenDumpFile_ThrowsArgumentException_WhenPathIsNull()
    {
        // Arrange
        var manager = new LldbManager();

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            manager.OpenDumpFile(null!));
    }

    [Fact]
    public void OpenDumpFile_ThrowsArgumentException_WhenPathIsEmpty()
    {
        // Arrange
        var manager = new LldbManager();

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            manager.OpenDumpFile(string.Empty));
    }

    [Fact]
    public void OpenDumpFile_ThrowsArgumentException_WhenPathIsWhitespace()
    {
        // Arrange
        var manager = new LldbManager();

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            manager.OpenDumpFile("   "));
    }

    [Fact]
    public void CloseDump_ThrowsInvalidOperationException_WhenNotInitialized()
    {
        // Arrange
        var manager = new LldbManager();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            manager.CloseDump());
    }

    [Fact]
    public void ExecuteCommand_ThrowsInvalidOperationException_WhenNotInitialized()
    {
        // Arrange
        var manager = new LldbManager();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            manager.ExecuteCommand("bt"));
    }

    [Fact]
    public void ExecuteCommand_ThrowsArgumentException_WhenCommandIsNull()
    {
        // Arrange
        var manager = new LldbManager();

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            manager.ExecuteCommand(null!));
    }

    [Fact]
    public void ExecuteCommand_ThrowsArgumentException_WhenCommandIsEmpty()
    {
        // Arrange
        var manager = new LldbManager();

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            manager.ExecuteCommand(string.Empty));
    }

    [Fact]
    public void LoadSosExtension_ThrowsInvalidOperationException_WhenNotInitialized()
    {
        // Arrange
        var manager = new LldbManager();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            manager.LoadSosExtension());
    }

    [Fact]
    public void ConfigureSymbolPath_ThrowsInvalidOperationException_WhenNotInitialized()
    {
        // Arrange
        var manager = new LldbManager();
        var symbolPath = "/path/to/symbols";

        // Act & Assert
        // Attempting to configure symbol path without initialization should throw
        var exception = Assert.Throws<InvalidOperationException>(() => 
            manager.ConfigureSymbolPath(symbolPath));
        
        // The exception message should indicate LLDB is not initialized
        Assert.Contains("not initialized", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigureSymbolPath_ThrowsArgumentException_WhenSymbolPathIsNull()
    {
        // Arrange
        var manager = new LldbManager();

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            manager.ConfigureSymbolPath(null!));
    }

    [Fact]
    public void ConfigureSymbolPath_ThrowsArgumentException_WhenSymbolPathIsEmpty()
    {
        // Arrange
        var manager = new LldbManager();

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            manager.ConfigureSymbolPath(string.Empty));
    }

    [Fact]
    public void ConfigureSymbolPath_ThrowsArgumentException_WhenSymbolPathIsWhitespace()
    {
        // Arrange
        var manager = new LldbManager();

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            manager.ConfigureSymbolPath("   "));
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var manager = new LldbManager();

        // Act & Assert - Should not throw
        manager.Dispose();
        manager.Dispose();
    }

    [Fact]
    public void Dispose_SetsIsInitializedToFalse()
    {
        // Arrange
        var manager = new LldbManager();

        // Act
        manager.Dispose();

        // Assert
        Assert.False(manager.IsInitialized);
    }

    // Note: Integration tests that actually initialize LLDB and open dumps
    // are skipped here because they require LLDB to be installed and
    // would need actual core dump files. These should be run separately
    // in an environment with LLDB installed.

    [Fact(Skip = "Requires LLDB to be installed")]
    public async Task InitializeAsync_StartsLldbProcess_WhenLldbIsInstalled()
    {
        // This test is skipped by default
        // To run it, LLDB must be installed and in PATH

        // Arrange
        var manager = new LldbManager();

        // Act
        await manager.InitializeAsync();

        // Assert
        Assert.True(manager.IsInitialized);

        // Cleanup
        manager.Dispose();
    }
}
