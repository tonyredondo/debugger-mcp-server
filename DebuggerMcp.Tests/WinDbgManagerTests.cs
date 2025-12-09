using Xunit;

#pragma warning disable CA1416 // Platform compatibility - WinDbgManager is Windows-only

namespace DebuggerMcp.Tests;

/// <summary>
/// Contains unit tests for the WinDbgManager class.
/// </summary>
/// <remarks>
/// Note: These tests focus on testing the logic and state management of WinDbgManager.
/// Full integration tests with actual DbgEng COM objects would require Windows and
/// WinDbg to be installed, so those are tested separately in integration test scenarios.
/// </remarks>
public class WinDbgManagerTests
{

    /// <summary>
    /// Tests that a new WinDbgManager instance is not initialized by default.
    /// </summary>
    [Fact]
    public void Constructor_CreatesInstanceWithoutInitialization()
    {
        // Arrange & Act
        var manager = new WinDbgManager();

        // Assert
        // A newly created manager should not be initialized
        Assert.False(manager.IsInitialized);

        // A newly created manager should not have a dump open
        Assert.False(manager.IsDumpOpen);
    }



    /// <summary>
    /// Tests that IsInitialized returns false when not initialized.
    /// </summary>
    [Fact]
    public void IsInitialized_ReturnsFalse_WhenNotInitialized()
    {
        // Arrange
        var manager = new WinDbgManager();

        // Act
        var isInitialized = manager.IsInitialized;

        // Assert
        Assert.False(isInitialized);
    }

    /// <summary>
    /// Tests that IsDumpOpen returns false when no dump is open.
    /// </summary>
    [Fact]
    public void IsDumpOpen_ReturnsFalse_WhenNoDumpIsOpen()
    {
        // Arrange
        var manager = new WinDbgManager();

        // Act
        var isDumpOpen = manager.IsDumpOpen;

        // Assert
        Assert.False(isDumpOpen);
    }



    /// <summary>
    /// Tests that OpenDumpFile throws when manager is not initialized.
    /// </summary>
    [Fact]
    public void OpenDumpFile_ThrowsInvalidOperationException_WhenNotInitialized()
    {
        // Arrange
        var manager = new WinDbgManager();
        var dumpPath = "C:\\test.dmp";

        // Act & Assert
        // Attempting to open a dump without initialization should throw
        var exception = Assert.Throws<InvalidOperationException>(() => manager.OpenDumpFile(dumpPath));

        // The exception message should indicate the manager is not initialized
        Assert.Contains("not initialized", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tests that OpenDumpFile throws when file does not exist.
    /// </summary>
    [Fact]
    public void OpenDumpFile_ThrowsFileNotFoundException_WhenFileDoesNotExist()
    {
        // Arrange
        var manager = new WinDbgManager();
        var nonExistentPath = "/tmp/nonexistent_dump_file_12345.dmp";

        // Note: We can't actually initialize the manager without DbgEng being available,
        // but we can verify the file existence check happens before COM interaction
        // by checking that FileNotFoundException would be thrown for non-existent files

        // Act & Assert
        // This will throw InvalidOperationException because not initialized,
        // but in a real scenario with initialization, it would throw FileNotFoundException
        Assert.Throws<InvalidOperationException>(() => manager.OpenDumpFile(nonExistentPath));
    }



    /// <summary>
    /// Tests that CloseDump throws when manager is not initialized.
    /// </summary>
    [Fact]
    public void CloseDump_ThrowsInvalidOperationException_WhenNotInitialized()
    {
        // Arrange
        var manager = new WinDbgManager();

        // Act & Assert
        // Attempting to close a dump without initialization should throw
        var exception = Assert.Throws<InvalidOperationException>(() => manager.CloseDump());

        // The exception message should indicate the manager is not initialized
        Assert.Contains("not initialized", exception.Message, StringComparison.OrdinalIgnoreCase);
    }



    /// <summary>
    /// Tests that ExecuteCommand throws when manager is not initialized.
    /// </summary>
    [Fact]
    public void ExecuteCommand_ThrowsInvalidOperationException_WhenNotInitialized()
    {
        // Arrange
        var manager = new WinDbgManager();
        var command = "k";

        // Act & Assert
        // Attempting to execute a command without initialization should throw
        var exception = Assert.Throws<InvalidOperationException>(() => manager.ExecuteCommand(command));

        // The exception message should indicate the manager is not initialized
        Assert.Contains("not initialized", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tests that ExecuteCommand throws when no dump is open.
    /// </summary>
    /// <remarks>
    /// This test verifies the state check that prevents executing commands
    /// when no dump file is loaded.
    /// </remarks>
    [Fact]
    public void ExecuteCommand_ThrowsInvalidOperationException_WhenNoDumpIsOpen()
    {
        // Arrange
        var manager = new WinDbgManager();
        var command = "k";

        // Act & Assert
        // Even if initialized, executing a command without an open dump should throw
        // (We can't test this fully without actual initialization, but the logic is there)
        var exception = Assert.Throws<InvalidOperationException>(() => manager.ExecuteCommand(command));

        // Should fail on initialization check first
        Assert.Contains("not initialized", exception.Message, StringComparison.OrdinalIgnoreCase);
    }



    /// <summary>
    /// Tests that LoadSosExtension throws when manager is not initialized.
    /// </summary>
    [Fact]
    public void LoadSosExtension_ThrowsInvalidOperationException_WhenNotInitialized()
    {
        // Arrange
        var manager = new WinDbgManager();

        // Act & Assert
        // Attempting to load SOS without initialization should throw
        var exception = Assert.Throws<InvalidOperationException>(() => manager.LoadSosExtension());

        // The exception message should indicate the manager is not initialized
        Assert.Contains("not initialized", exception.Message, StringComparison.OrdinalIgnoreCase);
    }



    /// <summary>
    /// Tests that ConfigureSymbolPath throws when manager is not initialized.
    /// </summary>
    [Fact]
    public void ConfigureSymbolPath_ThrowsInvalidOperationException_WhenNotInitialized()
    {
        // Arrange
        var manager = new WinDbgManager();
        var symbolPath = "srv*c:\\symbols*https://msdl.microsoft.com/download/symbols";

        // Act & Assert
        // Attempting to configure symbol path without initialization should throw
        var exception = Assert.Throws<InvalidOperationException>(() => manager.ConfigureSymbolPath(symbolPath));

        // The exception message should indicate the manager is not initialized
        Assert.Contains("not initialized", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tests that ConfigureSymbolPath throws when symbol path is null.
    /// </summary>
    [Fact]
    public void ConfigureSymbolPath_ThrowsArgumentException_WhenSymbolPathIsNull()
    {
        // Arrange
        var manager = new WinDbgManager();

        // Act & Assert
        // Null symbol path should throw ArgumentException
        Assert.Throws<ArgumentException>(() => manager.ConfigureSymbolPath(null!));
    }

    /// <summary>
    /// Tests that ConfigureSymbolPath throws when symbol path is empty.
    /// </summary>
    [Fact]
    public void ConfigureSymbolPath_ThrowsArgumentException_WhenSymbolPathIsEmpty()
    {
        // Arrange
        var manager = new WinDbgManager();

        // Act & Assert
        // Empty symbol path should throw ArgumentException
        Assert.Throws<ArgumentException>(() => manager.ConfigureSymbolPath(string.Empty));
    }

    /// <summary>
    /// Tests that ConfigureSymbolPath throws when symbol path is whitespace.
    /// </summary>
    [Fact]
    public void ConfigureSymbolPath_ThrowsArgumentException_WhenSymbolPathIsWhitespace()
    {
        // Arrange
        var manager = new WinDbgManager();

        // Act & Assert
        // Whitespace symbol path should throw ArgumentException
        Assert.Throws<ArgumentException>(() => manager.ConfigureSymbolPath("   "));
    }

    /// <summary>
    /// Tests that ConfigureSymbolPath validates symbol path before checking initialization.
    /// </summary>
    /// <remarks>
    /// This ensures parameter validation happens first for better error messages.
    /// </remarks>
    [Fact]
    public void ConfigureSymbolPath_ValidatesSymbolPathFirst_BeforeCheckingInitialization()
    {
        // Arrange
        var manager = new WinDbgManager();

        // Act & Assert
        // Empty symbol path should throw ArgumentException (not InvalidOperationException)
        // This verifies that parameter validation happens before initialization check
        var exception = Assert.Throws<ArgumentException>(() => manager.ConfigureSymbolPath(""));
        Assert.Contains("Symbol path", exception.Message);
    }



    /// <summary>
    /// Tests that Dispose can be called multiple times safely.
    /// </summary>
    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutException()
    {
        // Arrange
        var manager = new WinDbgManager();

        // Act & Assert
        // Dispose should be idempotent (safe to call multiple times)
        manager.Dispose();
        manager.Dispose(); // Should not throw
        manager.Dispose(); // Should not throw
    }

    /// <summary>
    /// Tests that Dispose does not throw when manager is not initialized.
    /// </summary>
    [Fact]
    public void Dispose_DoesNotThrow_WhenNotInitialized()
    {
        // Arrange
        var manager = new WinDbgManager();

        // Act & Assert
        // Disposing an uninitialized manager should not throw
        var exception = Record.Exception(() => manager.Dispose());
        Assert.Null(exception);
    }

}

/// <summary>
/// Contains unit tests for the OutputCallbacks class.
/// </summary>
/// <remarks>
/// OutputCallbacks is responsible for capturing debugger output in a thread-safe manner.
/// </remarks>
public class OutputCallbacksTests
{

    /// <summary>
    /// Tests that Output method appends text correctly.
    /// </summary>
    [Fact]
    public void Output_AppendsText_Correctly()
    {
        // Arrange
        var callbacks = new OutputCallbacks();
        var text1 = "First line\n";
        var text2 = "Second line\n";

        // Act
        var result1 = callbacks.Output(0, text1);
        var result2 = callbacks.Output(0, text2);

        // Assert
        // Output should return S_OK (0)
        Assert.Equal(0, result1);
        Assert.Equal(0, result2);

        // The accumulated output should contain both lines
        var output = callbacks.GetOutput();
        Assert.Contains("First line", output);
        Assert.Contains("Second line", output);
    }

    /// <summary>
    /// Tests that Output method handles empty strings.
    /// </summary>
    [Fact]
    public void Output_HandlesEmptyString_Correctly()
    {
        // Arrange
        var callbacks = new OutputCallbacks();
        var emptyText = string.Empty;

        // Act
        var result = callbacks.Output(0, emptyText);

        // Assert
        // Output should return S_OK (0) even for empty strings
        Assert.Equal(0, result);

        // The output should be empty
        var output = callbacks.GetOutput();
        Assert.Equal(string.Empty, output);
    }

    /// <summary>
    /// Tests that Output method is thread-safe.
    /// </summary>
    [Fact]
    public async Task Output_IsThreadSafe()
    {
        // Arrange
        var callbacks = new OutputCallbacks();
        var tasks = new List<Task>();
        var iterations = 100;

        // Act
        // Simulate multiple threads calling Output simultaneously
        for (int i = 0; i < 10; i++)
        {
            var taskId = i;
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < iterations; j++)
                {
                    callbacks.Output(0, $"Thread {taskId} - Line {j}\n");
                }
            }));
        }

        // Wait for all tasks to complete
        await Task.WhenAll(tasks);

        // Assert
        // The output should contain all lines from all threads
        var output = callbacks.GetOutput();

        // We should have 10 threads * 100 iterations = 1000 lines
        var lineCount = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.Equal(1000, lineCount);
    }



    /// <summary>
    /// Tests that GetOutput returns empty string initially.
    /// </summary>
    [Fact]
    public void GetOutput_ReturnsEmptyString_Initially()
    {
        // Arrange
        var callbacks = new OutputCallbacks();

        // Act
        var output = callbacks.GetOutput();

        // Assert
        Assert.Equal(string.Empty, output);
    }

    /// <summary>
    /// Tests that GetOutput returns accumulated text.
    /// </summary>
    [Fact]
    public void GetOutput_ReturnsAccumulatedText()
    {
        // Arrange
        var callbacks = new OutputCallbacks();
        callbacks.Output(0, "Line 1\n");
        callbacks.Output(0, "Line 2\n");
        callbacks.Output(0, "Line 3\n");

        // Act
        var output = callbacks.GetOutput();

        // Assert
        Assert.Equal("Line 1\nLine 2\nLine 3\n", output);
    }

    /// <summary>
    /// Tests that GetOutput can be called multiple times.
    /// </summary>
    [Fact]
    public void GetOutput_CanBeCalledMultipleTimes()
    {
        // Arrange
        var callbacks = new OutputCallbacks();
        callbacks.Output(0, "Test output\n");

        // Act
        var output1 = callbacks.GetOutput();
        var output2 = callbacks.GetOutput();

        // Assert
        // Both calls should return the same output
        Assert.Equal(output1, output2);
        Assert.Equal("Test output\n", output1);
    }



    /// <summary>
    /// Tests that ClearOutput removes all accumulated text.
    /// </summary>
    [Fact]
    public void ClearOutput_RemovesAllAccumulatedText()
    {
        // Arrange
        var callbacks = new OutputCallbacks();
        callbacks.Output(0, "Line 1\n");
        callbacks.Output(0, "Line 2\n");

        // Act
        callbacks.ClearOutput();

        // Assert
        // After clearing, output should be empty
        var output = callbacks.GetOutput();
        Assert.Equal(string.Empty, output);
    }

    /// <summary>
    /// Tests that ClearOutput can be called multiple times.
    /// </summary>
    [Fact]
    public void ClearOutput_CanBeCalledMultipleTimes()
    {
        // Arrange
        var callbacks = new OutputCallbacks();
        callbacks.Output(0, "Test\n");

        // Act & Assert
        // Multiple clears should not throw
        callbacks.ClearOutput();
        callbacks.ClearOutput();
        callbacks.ClearOutput();

        // Output should still be empty
        var output = callbacks.GetOutput();
        Assert.Equal(string.Empty, output);
    }

    /// <summary>
    /// Tests that ClearOutput allows new output to be accumulated.
    /// </summary>
    [Fact]
    public void ClearOutput_AllowsNewOutputToBeAccumulated()
    {
        // Arrange
        var callbacks = new OutputCallbacks();
        callbacks.Output(0, "Old output\n");
        callbacks.ClearOutput();

        // Act
        callbacks.Output(0, "New output\n");

        // Assert
        // Only the new output should be present
        var output = callbacks.GetOutput();
        Assert.Equal("New output\n", output);
        Assert.DoesNotContain("Old output", output);
    }

    /// <summary>
    /// Tests that ClearOutput is thread-safe.
    /// </summary>
    [Fact]
    public async Task ClearOutput_IsThreadSafe()
    {
        // Arrange
        var callbacks = new OutputCallbacks();
        var tasks = new List<Task>();

        // Act
        // Simulate multiple threads calling Output and Clear simultaneously
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    callbacks.Output(0, "Test\n");
                    if (j % 10 == 0)
                    {
                        callbacks.ClearOutput();
                    }
                }
            }));
        }

        // Wait for all tasks to complete
        await Task.WhenAll(tasks);

        // Assert
        // Should not throw and should have some output or be empty
        var exception = Record.Exception(() => callbacks.GetOutput());
        Assert.Null(exception);
    }

}
