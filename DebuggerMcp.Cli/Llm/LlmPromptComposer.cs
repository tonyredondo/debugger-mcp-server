namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Composes the final message list sent to the LLM by inserting attachment payloads in a safe position.
/// </summary>
internal static class LlmPromptComposer
{
    /// <summary>
    /// Inserts the given attachment messages right before the last user message (the current prompt) when present.
    /// </summary>
    /// <remarks>
    /// We deliberately insert attachments as <c>user</c> messages (not <c>system</c>) to reduce prompt-injection risk.
    /// </remarks>
    internal static IReadOnlyList<ChatMessage> InsertUserAttachmentsBeforeLastUserMessage(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<string> attachmentMessages)
    {
        if (messages == null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        if (attachmentMessages == null)
        {
            throw new ArgumentNullException(nameof(attachmentMessages));
        }

        if (attachmentMessages.Count == 0)
        {
            return messages;
        }

        var list = messages.ToList();

        var insertIndex = list.Count;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (string.Equals(list[i].Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                insertIndex = i;
                break;
            }
        }

        foreach (var attachment in attachmentMessages)
        {
            if (string.IsNullOrWhiteSpace(attachment))
            {
                continue;
            }

            list.Insert(insertIndex++, new ChatMessage("user", attachment));
        }

        return list;
    }
}

