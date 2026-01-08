using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Shell;
using DebuggerMcp.Cli.Shell.Transcript;
using Spectre.Console;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Implements interactive <c>llmagent</c> slash commands (e.g. <c>/help</c>, <c>/reset</c>, <c>/exit</c>).
/// </summary>
internal static class LlmAgentInteractiveCommands
{
    internal static bool TryHandle(
        string input,
        ConsoleOutput output,
        ShellState state,
        CliTranscriptStore transcript,
        out bool shouldExit)
    {
        shouldExit = false;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.TrimStart();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var commandLine = trimmed[1..].Trim();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            WriteHelp(output);
            return true;
        }

        var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length >= 2 ? parts[1] : string.Empty;

        switch (cmd)
        {
            case "exit":
            case "quit":
            case "q":
                shouldExit = true;
                return true;

            case "help":
                WriteHelp(output);
                return true;

            case "tools":
                WriteToolsList(output, LlmAgentTools.GetDefaultTools());
                return true;

            case "reset":
            {
                var conversationOnly = string.Equals(arg, "conversation", StringComparison.OrdinalIgnoreCase);
                Reset(transcript, state, conversationOnly);
                output.Success(conversationOnly
                    ? "Cleared LLM conversation history for the current session/dump (kept CLI transcript context)."
                    : "Cleared LLM context for the current session/dump (conversation + transcript context).");
                return true;
            }

            default:
                output.Error($"Unknown / command: {cmd}");
                output.Dim("Type '/help' for available commands.");
                return true;
        }
    }

    internal static void Reset(CliTranscriptStore transcript, ShellState state, bool conversationOnly)
    {
        // Reset in-memory orchestration state so checkpoints/evidence do not carry across an explicit reset.
        LlmAgentSessionStateStore.Reset(state.Settings.ServerUrl, state.SessionId, state.DumpId);

        if (conversationOnly)
        {
            transcript.FilterInPlace(e =>
                e.Kind is not ("llm_user" or "llm_assistant" or "llm_tool") ||
                !TranscriptScope.Matches(e, state.Settings.ServerUrl, state.SessionId, state.DumpId));
            return;
        }

        transcript.Append(new CliTranscriptEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Kind = "llm_reset",
            Text = "reset",
            ServerUrl = state.Settings.ServerUrl,
            SessionId = state.SessionId,
            DumpId = state.DumpId
        });
    }

    internal static void WriteToolsList(ConsoleOutput output, IReadOnlyList<ChatTool> tools)
    {
        output.Markup("[bold]TOOLS[/]");
        foreach (var tool in tools)
        {
            output.Markup($"  [cyan]{tool.Name}[/]  [dim]{Markup.Escape(tool.Description ?? string.Empty)}[/]");
        }
        output.Dim("Tip: Attach reports/files with @./path to include additional context.");
    }

    internal static void WriteHelp(ConsoleOutput output)
    {
        output.Dim("Available / commands in llmagent mode:");
        output.Markup("  [cyan]/help[/]                 Show this help");
        output.Markup("  [cyan]/tools[/]                Show available agent tools");
        output.Markup("  [cyan]/reset[/]                Clear LLM context (conversation + transcript context)");
        output.Markup("  [cyan]/reset conversation[/]   Clear only LLM conversation (keep CLI context)");
        output.Markup("  [cyan]/exit[/]                 Exit llmagent mode");
    }
}
