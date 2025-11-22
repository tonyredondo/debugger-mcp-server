using System.Net;
using DebuggerMcp.Cli.Client;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Shell;
using Moq;
using Spectre.Console;
using Spectre.Console.Testing;

namespace DebuggerMcp.Cli.Tests.Display;

/// <summary>
/// Tests for the ErrorHandler class.
/// </summary>
public class ErrorHandlerTests
{
    private readonly TestConsole _testConsole;
    private readonly ConsoleOutput _output;
    private readonly ShellState _state;

    public ErrorHandlerTests()
    {
        _testConsole = new TestConsole();
        _output = new ConsoleOutput(_testConsole);
        _state = new ShellState();
    }

    [Fact]
    public void Handle_HttpApiException_Unauthorized_ShowsAuthMessage()
    {
        // Arrange
        var ex = new HttpApiException("Unauthorized", HttpStatusCode.Unauthorized, "AUTH_FAILED");

        // Act
        ErrorHandler.Handle(_output, ex, _state, "upload");

        // Assert
        var output = _testConsole.Output;
        Assert.Contains("Authentication failed", output);
        Assert.Contains("API key", output);
    }

    [Fact]
    public void Handle_HttpApiException_NotFound_ShowsDumpNotFound()
    {
        // Arrange
        var ex = new HttpApiException("Not Found", HttpStatusCode.NotFound, "NOT_FOUND");

        // Act
        ErrorHandler.Handle(_output, ex, _state, "dump info");

        // Assert
        var output = _testConsole.Output;
        Assert.Contains("not found", output);
        Assert.Contains("dumps list", output);
    }

    [Fact]
    public void Handle_HttpApiException_ServiceUnavailable_ShowsRetryMessage()
    {
        // Arrange
        var ex = new HttpApiException("Service Unavailable", HttpStatusCode.ServiceUnavailable, null!);

        // Act
        ErrorHandler.Handle(_output, ex, _state);

        // Assert
        var output = _testConsole.Output;
        Assert.Contains("unavailable", output);
        Assert.Contains("health", output);
    }

    [Fact]
    public void Handle_McpClientException_SessionNotFound_ShowsSessionMessage()
    {
        // Arrange
        var ex = new McpClientException("Session ID not found");

        // Act
        ErrorHandler.Handle(_output, ex, _state);

        // Assert
        var output = _testConsole.Output;
        Assert.Contains("Session not found", output);
        Assert.Contains("session create", output);
    }

    [Fact]
    public void Handle_McpClientException_NoDump_ShowsDumpMessage()
    {
        // Arrange
        var ex = new McpClientException("No dump file loaded");

        // Act
        ErrorHandler.Handle(_output, ex, _state);

        // Assert
        var output = _testConsole.Output;
        Assert.Contains("No dump loaded", output);
        Assert.Contains("open", output);
    }

    [Fact]
    public void Handle_HttpRequestException_ConnectionRefused_ShowsConnectionMessage()
    {
        // Arrange
        var ex = new HttpRequestException("Connection refused");
        _state.SetConnected("http://localhost:5000");

        // Act
        ErrorHandler.Handle(_output, ex, _state);

        // Assert
        var output = _testConsole.Output;
        Assert.Contains("Connection refused", output);
        Assert.Contains("Server is not running", output);
    }

    [Fact]
    public void Handle_TaskCanceledException_ShowsCancelledMessage()
    {
        // Arrange
        var ex = new TaskCanceledException();

        // Act
        ErrorHandler.Handle(_output, ex, _state, "upload");

        // Assert
        var output = _testConsole.Output;
        Assert.Contains("cancelled", output);
    }

    [Fact]
    public void Handle_IOException_DiskSpace_ShowsDiskMessage()
    {
        // Arrange
        var ex = new IOException("No space left on device");

        // Act
        ErrorHandler.Handle(_output, ex, _state);

        // Assert
        var output = _testConsole.Output;
        Assert.Contains("disk space", output);
    }

    [Fact]
    public void Handle_UnauthorizedAccessException_ShowsPermissionMessage()
    {
        // Arrange
        var ex = new UnauthorizedAccessException("Access denied");

        // Act
        ErrorHandler.Handle(_output, ex, _state);

        // Assert
        var output = _testConsole.Output;
        Assert.Contains("Permission denied", output);
    }

    [Fact]
    public void Handle_ArgumentException_ShowsArgumentMessage()
    {
        // Arrange
        var ex = new ArgumentException("Invalid value", "sessionId");

        // Act
        ErrorHandler.Handle(_output, ex, _state, "session");

        // Assert
        var output = _testConsole.Output;
        Assert.Contains("Invalid argument", output);
        Assert.Contains("sessionId", output);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException), true)]
    [InlineData(typeof(TaskCanceledException), true)]
    [InlineData(typeof(ArgumentException), false)]
    [InlineData(typeof(IOException), false)]
    public void IsRecoverable_ReturnsExpectedResult(Type exceptionType, bool expectedRecoverable)
    {
        // Arrange
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;

        // Act
        var result = ErrorHandler.IsRecoverable(ex);

        // Assert
        Assert.Equal(expectedRecoverable, result);
    }

    [Fact]
    public void IsRecoverable_HttpApiException_ServiceUnavailable_ReturnsTrue()
    {
        // Arrange
        var ex = new HttpApiException("Unavailable", HttpStatusCode.ServiceUnavailable, null!);

        // Act
        var result = ErrorHandler.IsRecoverable(ex);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsRecoverable_HttpApiException_NotFound_ReturnsFalse()
    {
        // Arrange
        var ex = new HttpApiException("Not Found", HttpStatusCode.NotFound, null!);

        // Act
        var result = ErrorHandler.IsRecoverable(ex);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void SuggestConnectionRecovery_ShowsRecoverySteps()
    {
        // Arrange
        _state.SetConnected("http://localhost:5000");
        _state.SetSession("abc123-def456", "WinDbg");

        // Act
        ErrorHandler.SuggestConnectionRecovery(_output, _state);

        // Assert
        var output = _testConsole.Output;
        Assert.Contains("Connection Recovery", output);
        Assert.Contains("health", output);
        Assert.Contains("disconnect", output);
        Assert.Contains("abc123", output);
    }
}

