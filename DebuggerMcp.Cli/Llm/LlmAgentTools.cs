using System.Text.Json;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Tool definitions exposed to the OpenRouter model when the CLI LLM runs in agent mode.
/// </summary>
internal static class LlmAgentTools
{
    internal static IReadOnlyList<ChatTool> GetDefaultTools()
        =>
        [
            new ChatTool
            {
                Name = "exec",
                Description = "Execute a debugger command in the current session (LLDB/WinDbg/SOS).",
                Parameters = JsonDocument.Parse("""
                {
                  "type":"object",
                  "properties":{
                    "command":{"type":"string","description":"Debugger command to execute"}
                  },
                  "required":["command"]
                }
                """).RootElement.Clone()
            },
            new ChatTool
            {
                Name = "analyze",
                Description = "Run an automated analysis on the currently opened dump.",
                Parameters = JsonDocument.Parse("""
                {
                  "type":"object",
                  "properties":{
                    "kind":{
                      "type":"string",
                      "description":"Analysis kind",
                      "enum":["crash","performance","cpu","allocations","gc","contention","security"]
                    }
                  },
                  "required":["kind"]
                }
                """).RootElement.Clone()
            },
            new ChatTool
            {
                Name = "inspect_object",
                Description = "Inspect a .NET object at an address and return its structure as JSON.",
                Parameters = JsonDocument.Parse("""
                {
                  "type":"object",
                  "properties":{
                    "address":{"type":"string","description":"Object address (hex)"},
                    "maxDepth":{"type":"integer","description":"Max recursion depth (default 5)"}
                  },
                  "required":["address"]
                }
                """).RootElement.Clone()
            },
            new ChatTool
            {
                Name = "clr_stack",
                Description = "Get managed stack traces for threads using ClrMD (fast).",
                Parameters = JsonDocument.Parse("""
                {
                  "type":"object",
                  "properties":{
                    "threadId":{"type":"integer","description":"Thread id (0 for all)"},
                    "includeArguments":{"type":"boolean"},
                    "includeLocals":{"type":"boolean"},
                    "includeRegisters":{"type":"boolean"}
                  }
                }
                """).RootElement.Clone()
            }
        ];
}
