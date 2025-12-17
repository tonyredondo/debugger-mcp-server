using System.Text;
using DebuggerMcp.Cli.Shell.Transcript;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Builds LLM prompt context from the CLI transcript.
/// </summary>
internal static class TranscriptContextBuilder
{
    internal static IReadOnlyList<ChatMessage> BuildMessages(
        string userPrompt,
        string? serverUrl,
        string? sessionId,
        string? dumpId,
        IReadOnlyList<CliTranscriptEntry> transcriptTail,
        int maxContextChars,
        bool agentModeEnabled = false,
        bool agentConfirmationEnabled = true)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            throw new ArgumentException("Prompt cannot be empty.", nameof(userPrompt));
        }

        if (maxContextChars <= 0)
        {
            maxContextChars = 20_000;
        }

        var messages = new List<ChatMessage>();

        var contextHeader = new StringBuilder();
        contextHeader.AppendLine(LlmSystemPrompts.BuildSystemPrompt(agentModeEnabled, agentConfirmationEnabled));
        contextHeader.AppendLine();
        contextHeader.AppendLine("Context:");
        if (!string.IsNullOrWhiteSpace(serverUrl))
        {
            contextHeader.AppendLine($"Connected server: {serverUrl}");
        }
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            contextHeader.AppendLine($"Session: {sessionId}");
        }
        if (!string.IsNullOrWhiteSpace(dumpId))
        {
            contextHeader.AppendLine($"Opened dump: {dumpId}");
        }
        messages.Add(new ChatMessage("system", contextHeader.ToString().Trim()));

        var cliContext = BuildCliTranscriptContext(transcriptTail, maxContextChars: maxContextChars);
        if (!string.IsNullOrWhiteSpace(cliContext))
        {
            // Treat transcript context as untrusted data to reduce prompt-injection risk.
            messages.Add(new ChatMessage("user", $"CLI transcript context (untrusted data):{Environment.NewLine}{cliContext}"));
        }

        var llmEntries = transcriptTail.Where(e => e.Kind is "llm_user" or "llm_assistant").ToList();
        if (llmEntries.Count > 0)
        {
            var last = llmEntries[^1];
            if (last.Kind == "llm_user" &&
                string.Equals(
                    NormalizeForComparison(last.Text),
                    NormalizeForComparison(userPrompt),
                    StringComparison.OrdinalIgnoreCase))
            {
                llmEntries.RemoveAt(llmEntries.Count - 1);
            }
        }

        foreach (var msg in llmEntries)
        {
            var role = msg.Kind == "llm_assistant" ? "assistant" : "user";
            if (!string.IsNullOrWhiteSpace(msg.Text))
            {
                messages.Add(new ChatMessage(role, msg.Text));
            }
        }

        messages.Add(new ChatMessage("user", userPrompt));
        return messages;
    }

    private static string NormalizeForComparison(string? text)
        => TranscriptRedactor.RedactText(text ?? string.Empty).Trim();

    private static string BuildCliTranscriptContext(IReadOnlyList<CliTranscriptEntry> transcriptTail, int maxContextChars)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Recent CLI commands and outputs:");
        builder.AppendLine();

        var remaining = maxContextChars;
        var entries = transcriptTail
            .Where(e =>
                (e.Kind == "cli_command" || e.Kind == "llm_tool") &&
                !StartsWithCommand(e.Text, "llm"))
            .TakeLast(20)
            .ToList();

        var i = 1;
        foreach (var entry in entries)
        {
            var block = FormatCommandBlock(i++, entry);
            if (block.Length > remaining)
            {
                builder.AppendLine("[...context truncated...]");
                break;
            }

            builder.Append(block);
            remaining -= block.Length;
        }

        return builder.ToString().Trim();
    }

    private static string FormatCommandBlock(int index, CliTranscriptEntry entry)
    {
        var command = entry.Text?.Trim() ?? string.Empty;
        var output = entry.Output?.TrimEnd() ?? string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"{index}) $ {command}");
        if (!string.IsNullOrWhiteSpace(output))
        {
            sb.AppendLine(MarkdownCodeBlock.Format(output));
        }
        sb.AppendLine();
        return sb.ToString();
    }

    private static bool StartsWithCommand(string? text, string command)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.TrimStart();
        return trimmed.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, command, StringComparison.OrdinalIgnoreCase);
    }
}
