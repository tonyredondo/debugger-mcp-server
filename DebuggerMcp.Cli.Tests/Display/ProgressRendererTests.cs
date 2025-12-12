using DebuggerMcp.Cli.Display;
using Spectre.Console.Testing;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Display;

/// <summary>
/// Tests for <see cref="ProgressRenderer"/>.
/// </summary>
public class ProgressRendererTests
{
    [Fact]
    public async Task WithSpinnerAsync_WithReturnValue_ReturnsResult()
    {
        // Arrange
        var console = new TestConsole();
        var renderer = new ProgressRenderer(console);

        // Act
        var result = await renderer.WithSpinnerAsync("Working...", () => Task.FromResult(123));

        // Assert
        Assert.Equal(123, result);
    }

    [Fact]
    public async Task WithSpinnerAsync_WithoutReturnValue_InvokesOperation()
    {
        // Arrange
        var console = new TestConsole();
        var renderer = new ProgressRenderer(console);
        var called = false;

        // Act
        await renderer.WithSpinnerAsync("Working...", () =>
        {
            called = true;
            return Task.CompletedTask;
        });

        // Assert
        Assert.True(called);
    }

    [Fact]
    public async Task WithUploadProgressAsync_ReturnsResult()
    {
        // Arrange
        var console = new TestConsole();
        var renderer = new ProgressRenderer(console);

        // Act
        var result = await renderer.WithUploadProgressAsync(
            fileName: "[dump].dmp",
            totalBytes: 10,
            operation: progress =>
            {
                progress.Report(5);
                progress.Report(10);
                return Task.FromResult("ok");
            });

        // Assert
        Assert.Equal("ok", result);
    }
}

