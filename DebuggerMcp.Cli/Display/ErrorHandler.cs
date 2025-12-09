using DebuggerMcp.Cli.Client;
using DebuggerMcp.Cli.Shell;

namespace DebuggerMcp.Cli.Display;

/// <summary>
/// Centralized error handling for the CLI.
/// </summary>
/// <remarks>
/// Provides:
/// <list type="bullet">
/// <item><description>Graceful error messages with context</description></item>
/// <item><description>Suggested actions for common errors</description></item>
/// <item><description>Connection recovery suggestions</description></item>
/// </list>
/// </remarks>
public static class ErrorHandler
{
    /// <summary>
    /// Handles an exception and displays a user-friendly message.
    /// </summary>
    /// <param name="output">Console output helper.</param>
    /// <param name="ex">The exception to handle.</param>
    /// <param name="state">Current shell state for context.</param>
    /// <param name="operation">The operation that was being performed.</param>
    public static void Handle(ConsoleOutput output, Exception ex, ShellState state, string operation = "")
    {
        switch (ex)
        {
            case HttpApiException httpEx:
                HandleHttpApiException(output, httpEx, state, operation);
                break;

            case McpClientException mcpEx:
                HandleMcpClientException(output, mcpEx, state, operation);
                break;

            case HttpRequestException httpReqEx:
                HandleHttpRequestException(output, httpReqEx, state, operation);
                break;

            case TaskCanceledException:
            case OperationCanceledException:
                HandleCancellation(output, operation);
                break;

            case IOException ioEx:
                HandleIOException(output, ioEx, operation);
                break;

            case UnauthorizedAccessException:
                HandleUnauthorized(output, operation);
                break;

            case ArgumentException argEx:
                HandleArgumentException(output, argEx, operation);
                break;

            default:
                HandleGenericException(output, ex, operation);
                break;
        }
    }

    private static void HandleHttpApiException(ConsoleOutput output, HttpApiException ex, ShellState state, string operation)
    {
        var opText = string.IsNullOrEmpty(operation) ? "" : $" during {operation}";

        switch (ex.StatusCode)
        {
            case System.Net.HttpStatusCode.Unauthorized:
                output.Error($"Authentication failed{opText}.");
                output.Dim("Check your API key is correct.");
                if (!string.IsNullOrEmpty(state.Settings.ApiKey))
                {
                    output.Dim("Current API key is set. Try reconnecting with a different key:");
                    output.Dim("  connect <url> --api-key <your-key>");
                }
                else
                {
                    output.Dim("No API key configured. If the server requires auth:");
                    output.Dim("  connect <url> --api-key <your-key>");
                }
                break;

            case System.Net.HttpStatusCode.Forbidden:
                output.Error($"Access denied{opText}.");
                output.Dim("You don't have permission for this operation.");
                break;

            case System.Net.HttpStatusCode.NotFound:
                output.Error($"Resource not found{opText}.");
                if (operation.Contains("dump", StringComparison.OrdinalIgnoreCase))
                {
                    output.Dim("The specified dump may have been deleted or doesn't exist.");
                    output.Dim("Use 'dumps list' to see available dumps.");
                }
                else if (operation.Contains("session", StringComparison.OrdinalIgnoreCase))
                {
                    output.Dim("The session may have expired or been closed.");
                    output.Dim("Use 'session list' to see active sessions.");
                }
                break;

            case System.Net.HttpStatusCode.RequestEntityTooLarge:
                output.Error($"File too large{opText}.");
                output.Dim("The server has a maximum upload size limit.");
                output.Dim("Try compressing the dump file or check server configuration.");
                break;

            case System.Net.HttpStatusCode.ServiceUnavailable:
                output.Error($"Server unavailable{opText}.");
                output.Dim("The server may be overloaded or down for maintenance.");
                output.Dim("Wait a moment and try again, or check server status with 'health'.");
                break;

            case System.Net.HttpStatusCode.InternalServerError:
                output.Error($"Server error{opText}.");
                output.Dim("An internal server error occurred.");
                if (!string.IsNullOrEmpty(ex.ErrorCode))
                {
                    output.Dim($"Error code: {ex.ErrorCode}");
                }
                output.Dim("Check the server logs for more details.");
                break;

            default:
                output.Error($"HTTP error ({(int)ex.StatusCode}){opText}: {ex.Message}");
                if (!string.IsNullOrEmpty(ex.ErrorCode))
                {
                    output.Dim($"Error code: {ex.ErrorCode}");
                }
                break;
        }
    }

    private static void HandleMcpClientException(ConsoleOutput output, McpClientException ex, ShellState state, string operation)
    {
        var opText = string.IsNullOrEmpty(operation) ? "" : $" during {operation}";

        // Parse common MCP errors
        if (ex.Message.Contains("Session ID not found", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("session not found", StringComparison.OrdinalIgnoreCase))
        {
            output.Error($"Session not found{opText}.");
            output.Dim("The session may have expired or been closed.");
            output.Dim("Create a new session with 'session create' or use 'open <dumpId>'.");
        }
        else if (ex.Message.Contains("No dump file", StringComparison.OrdinalIgnoreCase) ||
                 ex.Message.Contains("dump not loaded", StringComparison.OrdinalIgnoreCase))
        {
            output.Error($"No dump loaded{opText}.");
            output.Dim("Open a dump file first with 'open <dumpId>'.");
        }
        else if (ex.Message.Contains("Unknown tool", StringComparison.OrdinalIgnoreCase))
        {
            output.Error($"Command not supported by server{opText}.");
            output.Dim("The server may be running an older version.");
            output.Dim("Use 'tools' to see available server commands.");
        }
        else if (ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            output.Error($"Operation timed out{opText}.");
            output.Dim("The operation took too long to complete.");
            output.Dim("Try increasing the timeout: set timeout 600");
        }
        else
        {
            output.Error($"MCP error{opText}: {ex.Message}");
        }
    }

    private static void HandleHttpRequestException(ConsoleOutput output, HttpRequestException ex, ShellState state, string operation)
    {
        var opText = string.IsNullOrEmpty(operation) ? "" : $" during {operation}";

        if (ex.Message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase))
        {
            output.Error($"Connection refused{opText}.");
            output.Dim("The server is not accepting connections.");
            output.Dim("Possible causes:");
            output.Dim("  • Server is not running");
            output.Dim("  • Wrong URL or port");
            output.Dim("  • Firewall blocking connection");
            if (!string.IsNullOrEmpty(state.ServerDisplay))
            {
                output.Dim($"Try: health {state.ServerDisplay}");
            }
        }
        else if (ex.Message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase) ||
                 ex.Message.Contains("No such host", StringComparison.OrdinalIgnoreCase))
        {
            output.Error($"Server not found{opText}.");
            output.Dim("Could not resolve the server address.");
            output.Dim("Check the URL is correct and try again.");
        }
        else if (ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                 ex.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
                 ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            output.Error($"SSL/TLS error{opText}.");
            output.Dim("Could not establish secure connection.");
            output.Dim("The server's SSL certificate may be invalid or expired.");
        }
        else
        {
            output.Error($"Network error{opText}: {ex.Message}");
            output.Dim("Check your network connection and try again.");
        }
    }

    private static void HandleCancellation(ConsoleOutput output, string operation)
    {
        if (!string.IsNullOrEmpty(operation))
        {
            output.Warning($"Operation cancelled: {operation}");
        }
        else
        {
            output.Warning("Operation cancelled.");
        }
    }

    private static void HandleIOException(ConsoleOutput output, IOException ex, string operation)
    {
        var opText = string.IsNullOrEmpty(operation) ? "" : $" during {operation}";

        if (ex.Message.Contains("disk space", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase))
        {
            output.Error($"Insufficient disk space{opText}.");
            output.Dim("Free up disk space and try again.");
        }
        else if (ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
        {
            output.Error($"File is in use{opText}.");
            output.Dim("Close other applications using the file and try again.");
        }
        else
        {
            output.Error($"File error{opText}: {ex.Message}");
        }
    }

    private static void HandleUnauthorized(ConsoleOutput output, string operation)
    {
        var opText = string.IsNullOrEmpty(operation) ? "" : $" during {operation}";
        output.Error($"Permission denied{opText}.");
        output.Dim("You don't have permission to access this file or directory.");
    }

    private static void HandleArgumentException(ConsoleOutput output, ArgumentException ex, string operation)
    {
        output.Error($"Invalid argument: {ex.Message}");
        if (!string.IsNullOrEmpty(ex.ParamName))
        {
            output.Dim($"Parameter: {ex.ParamName}");
        }
        output.Dim($"Use 'help {operation}' for usage information.");
    }

    private static void HandleGenericException(ConsoleOutput output, Exception ex, string operation)
    {
        var opText = string.IsNullOrEmpty(operation) ? "" : $" during {operation}";
        output.Error($"An error occurred{opText}: {ex.Message}");

#if DEBUG
        output.Dim($"Exception type: {ex.GetType().Name}");
        if (ex.StackTrace != null)
        {
            output.Dim("Stack trace:");
            foreach (var line in ex.StackTrace.Split('\n').Take(5))
            {
                output.Dim($"  {line.Trim()}");
            }
        }
#endif
    }

    /// <summary>
    /// Displays a connection recovery suggestion.
    /// </summary>
    public static void SuggestConnectionRecovery(ConsoleOutput output, ShellState state)
    {
        output.WriteLine();
        output.Markup("[bold yellow]Connection Recovery[/]");
        output.Dim("Try the following:");
        output.Dim("  1. Check server status: health");
        output.Dim("  2. Reconnect: disconnect && connect <url>");
        output.Dim("  3. Check server logs for errors");

        if (!string.IsNullOrEmpty(state.SessionId))
        {
            output.Dim("");
            output.Dim("Your session may still be active on the server.");
            output.Dim($"After reconnecting, use: session use {state.SessionId[..8]}");
        }
    }

    /// <summary>
    /// Checks if an error is recoverable.
    /// </summary>
    public static bool IsRecoverable(Exception ex)
    {
        return ex is HttpRequestException or
               TaskCanceledException or
               HttpApiException
        {
            StatusCode: System.Net.HttpStatusCode.ServiceUnavailable or
                                              System.Net.HttpStatusCode.GatewayTimeout or
                                              System.Net.HttpStatusCode.RequestTimeout
        };
    }
}

