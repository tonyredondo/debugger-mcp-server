using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Models;
using DebuggerMcp.Cli.Shell.Transcript;

namespace DebuggerMcp.Cli.Shell;

/// <summary>
/// Tracks the current state of the interactive shell.
/// </summary>
/// <remarks>
/// The shell progresses through states:
/// <list type="number">
/// <item><description>Initial - Not connected to any server</description></item>
/// <item><description>Connected - Connected to server, no session</description></item>
/// <item><description>Session - Has active session, no dump loaded</description></item>
/// <item><description>DumpLoaded - Has session with dump loaded</description></item>
/// </list>
/// </remarks>
public class ShellState
{
    /// <summary>
    /// Gets or sets the connection settings.
    /// </summary>
    public ConnectionSettings Settings { get; set; } = ConnectionSettings.FromEnvironment();

    /// <summary>
    /// Gets or sets the transcript store for capturing commands and outputs (optional).
    /// </summary>
    public CliTranscriptStore? Transcript { get; set; }

    /// <summary>
    /// Gets or sets whether connected to a server.
    /// </summary>
    public bool IsConnected { get; set; }

    /// <summary>
    /// Gets or sets the current server URL (display name).
    /// </summary>
    public string? ServerDisplay { get; set; }

    /// <summary>
    /// Gets or sets the current session ID.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Gets or sets the currently opened dump ID (set after a successful <c>open</c>).
    /// </summary>
    public string? DumpId { get; set; }

    /// <summary>
    /// Gets or sets the currently selected dump ID (e.g., last uploaded or last referenced).
    /// This does not imply the dump is open in the debugger.
    /// </summary>
    public string? SelectedDumpId { get; set; }

    /// <summary>
    /// Gets or sets the debugger type (WinDbg or LLDB).
    /// </summary>
    public string? DebuggerType { get; set; }

    /// <summary>
    /// Gets or sets the cached server host information.
    /// </summary>
    public ServerInfo? ServerInfo { get; set; }

    /// <summary>
    /// Gets or sets the result of the last executed command.
    /// </summary>
    public string? LastCommandResult { get; set; }

    /// <summary>
    /// Gets or sets the name/description of the last executed command.
    /// </summary>
    public string? LastCommandName { get; set; }

    /// <summary>
    /// Gets whether there is a last command result available.
    /// </summary>
    public bool HasLastResult => !string.IsNullOrEmpty(LastCommandResult);

    /// <summary>
    /// Gets whether a session is active.
    /// </summary>
    public bool HasSession => !string.IsNullOrEmpty(SessionId);

    /// <summary>
    /// Gets whether a dump is loaded.
    /// </summary>
    public bool HasDumpLoaded => !string.IsNullOrEmpty(DumpId);

    /// <summary>
    /// Gets whether a dump is selected (but not necessarily loaded).
    /// </summary>
    public bool HasDumpSelected => !string.IsNullOrEmpty(SelectedDumpId);

    /// <summary>
    /// Gets the current shell state level.
    /// </summary>
    public ShellStateLevel Level
    {
        get
        {
            if (!IsConnected)
            {
                return ShellStateLevel.Initial;
            }

            if (!HasSession)
            {
                return ShellStateLevel.Connected;
            }

            if (!HasDumpLoaded)
            {
                return ShellStateLevel.Session;
            }

            return ShellStateLevel.DumpLoaded;
        }
    }

    /// <summary>
    /// Resets the state to initial (not connected).
    /// </summary>
    public void Reset()
    {
        IsConnected = false;
        ServerDisplay = null;
        SessionId = null;
        DumpId = null;
        SelectedDumpId = null;
        DebuggerType = null;
        ServerInfo = null;
    }

    /// <summary>
    /// Sets the connected state.
    /// </summary>
    /// <param name="serverUrl">The server URL.</param>
    public void SetConnected(string serverUrl)
    {
        IsConnected = true;

        // Extract display name from URL
        if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
        {
            ServerDisplay = uri.Port == 80 || uri.Port == 443
                ? uri.Host
                : $"{uri.Host}:{uri.Port}";
        }
        else
        {
            ServerDisplay = serverUrl;
        }
    }

    /// <summary>
    /// Sets the session state.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="debuggerType">Optional debugger type.</param>
    public void SetSession(string sessionId, string? debuggerType = null)
    {
        SessionId = sessionId;
        DebuggerType = debuggerType;
    }

    /// <summary>
    /// Clears the session state.
    /// </summary>
    public void ClearSession()
    {
        SessionId = null;
        DumpId = null;
        DebuggerType = null;
    }

    /// <summary>
    /// Sets the dump loaded state.
    /// </summary>
    /// <param name="dumpId">The dump ID.</param>
    public void SetDumpLoaded(string dumpId)
    {
        DumpId = dumpId;
        SelectedDumpId = dumpId;
    }

    /// <summary>
    /// Sets the selected dump ID (without opening it).
    /// </summary>
    public void SetSelectedDump(string dumpId)
    {
        SelectedDumpId = dumpId;
    }

    /// <summary>
    /// Clears the dump loaded state.
    /// </summary>
    public void ClearDump()
    {
        DumpId = null;
    }

    /// <summary>
    /// Clears the selected dump ID.
    /// </summary>
    public void ClearSelectedDump()
    {
        SelectedDumpId = null;
    }

    /// <summary>
    /// Sets the last command result.
    /// </summary>
    /// <param name="commandName">The name or description of the command.</param>
    /// <param name="result">The result output.</param>
    public void SetLastResult(string commandName, string result)
    {
        LastCommandName = commandName;
        LastCommandResult = result;
    }

    /// <summary>
    /// Clears the last command result.
    /// </summary>
    public void ClearLastResult()
    {
        LastCommandName = null;
        LastCommandResult = null;
    }
}

/// <summary>
/// Represents the current state level of the shell.
/// </summary>
public enum ShellStateLevel
{
    /// <summary>
    /// Not connected to any server.
    /// </summary>
    Initial,

    /// <summary>
    /// Connected to a server, but no session.
    /// </summary>
    Connected,

    /// <summary>
    /// Has an active session, but no dump loaded.
    /// </summary>
    Session,

    /// <summary>
    /// Has a session with a dump loaded.
    /// </summary>
    DumpLoaded
}
