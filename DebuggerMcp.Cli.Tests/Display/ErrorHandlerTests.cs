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
    public void Handle_HttpApiException_Forbidden_ShowsAccessDenied()
    {
        var ex = new HttpApiException("Forbidden", HttpStatusCode.Forbidden, "DENIED");

        ErrorHandler.Handle(_output, ex, _state, "upload");

        var output = _testConsole.Output;
        Assert.Contains("Access denied", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Handle_HttpApiException_RequestEntityTooLarge_ShowsUploadLimitMessage()
    {
        var ex = new HttpApiException("Too Large", HttpStatusCode.RequestEntityTooLarge, "TOO_LARGE");

        ErrorHandler.Handle(_output, ex, _state, "upload");

        var output = _testConsole.Output;
        Assert.Contains("File too large", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("maximum upload size", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Handle_HttpApiException_InternalServerError_WithErrorCode_ShowsErrorCode()
    {
        var ex = new HttpApiException("Boom", HttpStatusCode.InternalServerError, "E123");

        ErrorHandler.Handle(_output, ex, _state, "analyze");

        var output = _testConsole.Output;
        Assert.Contains("Server error", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Error code", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("E123", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Handle_HttpApiException_DefaultStatus_ShowsHttpErrorAndOptionalErrorCode()
    {
        var ex = new HttpApiException("Teapot", (HttpStatusCode)418, "TEAPOT");

        ErrorHandler.Handle(_output, ex, _state, "brew");

        var output = _testConsole.Output;
        Assert.Contains("HTTP error", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TEAPOT", output, StringComparison.OrdinalIgnoreCase);
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
    public void Handle_McpClientException_UnknownTool_ShowsNotSupportedMessage()
    {
        var ex = new McpClientException("Unknown tool: foo");

        ErrorHandler.Handle(_output, ex, _state, "tool");

        var output = _testConsole.Output;
        Assert.Contains("not supported", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("older version", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tools", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Handle_McpClientException_Timeout_ShowsTimeoutMessage()
    {
        var ex = new McpClientException("timeout while waiting for response");

        ErrorHandler.Handle(_output, ex, _state, "analyze");

        var output = _testConsole.Output;
        Assert.Contains("timed out", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timeout", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Handle_McpClientException_Generic_ShowsMcpError()
    {
        var ex = new McpClientException("some other MCP problem");

        ErrorHandler.Handle(_output, ex, _state, "analyze");

        var output = _testConsole.Output;
        Assert.Contains("MCP error", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("some other MCP problem", output, StringComparison.OrdinalIgnoreCase);
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
    public void Handle_HttpRequestException_ServerNotFound_ShowsNameResolutionMessage()
    {
        var ex = new HttpRequestException("No such host is known");

        ErrorHandler.Handle(_output, ex, _state, "connect");

        var output = _testConsole.Output;
        Assert.Contains("Server not found", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resolve", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Handle_HttpRequestException_SslError_ShowsSslMessage()
    {
        var ex = new HttpRequestException("TLS handshake failed: certificate error");

        ErrorHandler.Handle(_output, ex, _state, "connect");

        var output = _testConsole.Output;
        Assert.Contains("SSL", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("certificate", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Handle_HttpRequestException_Generic_ShowsNetworkError()
    {
        var ex = new HttpRequestException("Some network issue");

        ErrorHandler.Handle(_output, ex, _state, "connect");

        var output = _testConsole.Output;
        Assert.Contains("Network error", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("network connection", output, StringComparison.OrdinalIgnoreCase);
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
    public void Handle_IOException_FileInUse_ShowsFileInUseMessage()
    {
        var ex = new IOException("The process cannot access the file because it is being used by another process");

        ErrorHandler.Handle(_output, ex, _state, "open");

        var output = _testConsole.Output;
        Assert.Contains("File is in use", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Close", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Handle_IOException_Generic_ShowsFileError()
    {
        var ex = new IOException("some IO error");

        ErrorHandler.Handle(_output, ex, _state, "open");

        var output = _testConsole.Output;
        Assert.Contains("File error", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("some IO error", output, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void Handle_GenericException_ShowsExceptionType_InDebugBuild()
    {
        Exception ex;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (Exception caught)
        {
            ex = caught;
        }

        ErrorHandler.Handle(_output, ex, _state, "run");

        var output = _testConsole.Output;
        Assert.Contains("An error occurred", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Exception type", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("InvalidOperationException", output, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void SuggestConnectionRecovery_WhenNoSessionId_DoesNotSuggestSessionUse()
    {
        _state.SetConnected("http://localhost:5000");

        ErrorHandler.SuggestConnectionRecovery(_output, _state);

        var output = _testConsole.Output;
        Assert.Contains("Connection Recovery", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session use", output, StringComparison.OrdinalIgnoreCase);
    }
}
