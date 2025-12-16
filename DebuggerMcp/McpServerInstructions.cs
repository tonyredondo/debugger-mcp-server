#nullable enable

namespace DebuggerMcp;

/// <summary>
/// Canonical server instructions returned during MCP initialization.
/// </summary>
internal static class McpServerInstructions
{
    /// <summary>
    /// Instructions for MCP clients/LLMs on how to use this server.
    /// </summary>
    internal const string Text =
        """
        Before calling any tools, read `debugger://mcp-tools` and use only the tool names and argument shapes documented there.

        Defaults:
        - Prefer `report(..., format="json")` for LLM consumption (structured and machine-readable).
        - Use `report(..., format="markdown")` for human-readable output, and `html` only for browser viewing.

        Notes:
        - Most actions require both `sessionId` and `userId`.
        - If a tool returns an error about missing required inputs, re-call it with the required fields filled.
        """;
}

