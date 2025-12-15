using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Models;
using DebuggerMcp.Cli.Shell;
using System.Text.Json;

namespace DebuggerMcp.Cli.Client;

/// <summary>
/// Handles connection recovery for the CLI client.
/// </summary>
/// <remarks>
/// Provides automatic reconnection when:
/// <list type="bullet">
/// <item><description>Connection is lost during operation</description></item>
/// <item><description>Server becomes temporarily unavailable</description></item>
/// <item><description>Network issues occur</description></item>
/// </list>
/// </remarks>
public class ConnectionRecovery
{
    private readonly IHttpApiClient _httpClient;
    private readonly IMcpClient _mcpClient;
    private readonly ShellState _state;
    private readonly ConsoleOutput _output;

    /// <summary>
    /// Maximum number of reconnection attempts.
    /// </summary>
    public int MaxReconnectAttempts { get; set; } = 3;

    /// <summary>
    /// Delay between reconnection attempts.
    /// </summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Creates a new connection recovery handler.
    /// </summary>
    public ConnectionRecovery(
        IHttpApiClient httpClient,
        IMcpClient mcpClient,
        ShellState state,
        ConsoleOutput output)
    {
        _httpClient = httpClient;
        _mcpClient = mcpClient;
        _state = state;
        _output = output;
    }

    /// <summary>
    /// Checks if the connection is healthy.
    /// </summary>
    public async Task<bool> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!_state.IsConnected)
        {
            return false;
        }

        try
        {
            var health = await _httpClient.CheckHealthAsync(cancellationToken);
            return health?.IsHealthy == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to recover a lost connection.
    /// </summary>
    /// <returns>True if connection was recovered, false otherwise.</returns>
    public async Task<bool> TryRecoverAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_state.Settings.ServerUrl))
        {
            _output.Warning("No server URL available for reconnection.");
            return false;
        }

        _output.Warning("Connection lost. Attempting to reconnect...");

        for (var attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            try
            {
                _output.Info($"Reconnection attempt {attempt}/{MaxReconnectAttempts}...");

                // Try to reconnect HTTP client
                var health = await _httpClient.CheckHealthAsync(cancellationToken);
                if (health?.IsHealthy != true)
                {
                    throw new Exception("Server not healthy");
                }

                // Try to reconnect MCP client
                await _mcpClient.ConnectAsync(_state.Settings.ServerUrl!, _state.Settings.ApiKey, cancellationToken);

                _output.Success("Reconnected successfully!");

                // Try to restore session if we had one
                if (!string.IsNullOrEmpty(_state.SessionId))
                {
                    await TryRestoreSessionAsync(cancellationToken);
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                _output.Warning("Reconnection cancelled.");
                return false;
            }
            catch (Exception ex)
            {
                _output.Dim($"Attempt {attempt} failed: {ex.Message}");

                if (attempt < MaxReconnectAttempts)
                {
                    _output.Info($"Waiting {ReconnectDelay.TotalSeconds}s before next attempt...");
                    await Task.Delay(ReconnectDelay, cancellationToken);
                }
            }
        }

        _output.Error($"Failed to reconnect after {MaxReconnectAttempts} attempts.");
        ErrorHandler.SuggestConnectionRecovery(_output, _state);
        return false;
    }

    /// <summary>
    /// Tries to restore a previous session.
    /// </summary>
    private async Task TryRestoreSessionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_state.SessionId))
        {
            return;
        }

        try
        {
            _output.Info($"Attempting to restore session {_state.SessionId[..8]}...");

            // Check if session still exists by listing sessions
            var sessionsResult = await _mcpClient.ListSessionsAsync(_state.Settings.UserId, cancellationToken);
            var sessionExists = false;

            try
            {
                var parsed = JsonSerializer.Deserialize<SessionListResponse>(
                    sessionsResult,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed != null)
                {
                    sessionExists = parsed.Sessions?.Any(s => string.Equals(s.SessionId, _state.SessionId, StringComparison.OrdinalIgnoreCase)) == true;
                    if (sessionExists)
                    {
                        SessionStateSynchronizer.TrySyncCurrentDumpFromSessionList(_state, parsed);
                    }
                }
            }
            catch
            {
                // If parsing fails, keep the existing session state and report the issue to the user.
                _output.Warning("Could not parse session list response while validating session state.");
            }

            if (sessionExists)
            {
                _output.Success($"Session {_state.SessionId[..8]} is still active.");
            }
            else
            {
                _output.Warning($"Previous session {_state.SessionId[..8]} no longer exists.");
                _output.Dim("You may need to create a new session and reopen your dump.");
                _state.ClearSession();
            }
        }
        catch (Exception ex)
        {
            _output.Warning($"Could not verify session state: {ex.Message}");
        }
    }

    /// <summary>
    /// Executes an operation with automatic recovery on failure.
    /// </summary>
    public async Task<T> ExecuteWithRecoveryAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (ErrorHandler.IsRecoverable(ex))
        {
            _output.Warning($"Operation '{operationName}' failed due to connection issue.");

            if (await TryRecoverAsync(cancellationToken))
            {
                _output.Info($"Retrying '{operationName}'...");
                return await operation();
            }

            throw;
        }
    }

    /// <summary>
    /// Executes an operation with automatic recovery on failure (void version).
    /// </summary>
    public async Task ExecuteWithRecoveryAsync(
        Func<Task> operation,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithRecoveryAsync(async () =>
        {
            await operation();
            return true;
        }, operationName, cancellationToken);
    }
}
