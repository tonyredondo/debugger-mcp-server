using Spectre.Console;

namespace DebuggerMcp.Cli.Shell;

/// <summary>
/// Builds dynamic prompts based on shell state.
/// </summary>
/// <remarks>
/// Prompt format: dbg-mcp [server] session:xxx dump:yyy> 
/// </remarks>
public static class PromptBuilder
{
    /// <summary>
    /// Default prompt text for disconnected state.
    /// </summary>
    public const string DefaultPrompt = "dbg-mcp";

    /// <summary>
    /// Builds a plain text prompt for display.
    /// </summary>
    /// <param name="state">The current shell state.</param>
    /// <returns>Plain text prompt string.</returns>
    public static string BuildPlain(ShellState state)
    {
        var parts = new List<string> { DefaultPrompt };

        if (state.IsConnected && !string.IsNullOrEmpty(state.ServerDisplay))
        {
            parts.Add($"[{state.ServerDisplay}]");
        }

        if (!string.IsNullOrEmpty(state.SessionId))
        {
            // Show first 8 chars of session ID
            var shortId = state.SessionId.Length > 8
                ? state.SessionId[..8]
                : state.SessionId;
            parts.Add($"session:{shortId}");
        }

        if (!string.IsNullOrEmpty(state.DumpId))
        {
            parts.Add($"dump:{state.DumpId}");
        }

        return string.Join(" ", parts) + "> ";
    }

    /// <summary>
    /// Builds a colored markup prompt for Spectre.Console.
    /// </summary>
    /// <param name="state">The current shell state.</param>
    /// <returns>Markup-formatted prompt string.</returns>
    public static string BuildMarkup(ShellState state)
    {
        var parts = new List<string> { "[grey]dbg-mcp[/]" };

        if (state.IsConnected && !string.IsNullOrEmpty(state.ServerDisplay))
        {
            // Use [[ and ]] to escape brackets in Spectre.Console markup
            parts.Add($"[cyan][[{Markup.Escape(state.ServerDisplay)}]][/]");
        }

        if (!string.IsNullOrEmpty(state.SessionId))
        {
            // Show first 8 chars of session ID
            var shortId = state.SessionId.Length > 8
                ? state.SessionId[..8]
                : state.SessionId;
            parts.Add($"[green]session:{Markup.Escape(shortId)}[/]");
        }

        if (!string.IsNullOrEmpty(state.DumpId))
        {
            parts.Add($"[yellow]dump:{Markup.Escape(state.DumpId)}[/]");
        }

        return string.Join(" ", parts) + "[grey]>[/] ";
    }

    /// <summary>
    /// Gets the prompt length in characters (for cursor positioning).
    /// </summary>
    /// <param name="state">The current shell state.</param>
    /// <returns>The number of visible characters in the prompt.</returns>
    public static int GetPromptLength(ShellState state)
    {
        return BuildPlain(state).Length;
    }

    /// <summary>
    /// Builds a status line showing current context.
    /// </summary>
    /// <param name="state">The current shell state.</param>
    /// <returns>Status line markup.</returns>
    public static string BuildStatusLine(ShellState state)
    {
        return state.Level switch
        {
            ShellStateLevel.Initial => "[grey]Not connected. Use 'connect <url>' to start.[/]",
            ShellStateLevel.Connected => "[cyan]Connected.[/] Use 'session create' or 'open <dumpId>'.",
            ShellStateLevel.Session => $"[green]Session active.[/] Debugger: {state.DebuggerType ?? "unknown"}",
            ShellStateLevel.DumpLoaded => $"[yellow]Dump loaded.[/] Use 'exec <cmd>' or 'analyze'.",
            _ => string.Empty
        };
    }

    /// <summary>
    /// Gets contextual help hints based on state.
    /// </summary>
    /// <param name="state">The current shell state.</param>
    /// <returns>List of suggested commands.</returns>
    public static IEnumerable<string> GetContextualHints(ShellState state)
    {
        return state.Level switch
        {
            ShellStateLevel.Initial => ["connect <url>", "help", "exit"],
            ShellStateLevel.Connected => ["upload <file>", "dumps list", "session create", "open <dumpId>"],
            ShellStateLevel.Session => ["open <dumpId>", "session close", "dumps list"],
            ShellStateLevel.DumpLoaded => ["exec <cmd>", "analyze crash -o <file>", "threads", "stack", "close"],
            _ => ["help"]
        };
    }
}
