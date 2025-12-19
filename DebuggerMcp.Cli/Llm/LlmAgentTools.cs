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
                Name = "report_index",
                Description = "Get a small crash report index (summary + table of contents) for the currently opened dump.",
                Parameters = JsonDocument.Parse("""
                {
                  "type":"object",
                  "properties":{},
                  "additionalProperties":false
                }
                """).RootElement.Clone()
            },
            new ChatTool
            {
                Name = "report_get",
                Description = "Fetch a section of the canonical crash report JSON by dot-path (e.g., analysis.exception, analysis.threads.all) with paging for arrays.",
                Parameters = JsonDocument.Parse("""
                {
                  "type":"object",
                  "properties":{
                    "path":{"type":"string","description":"Dot-path under metadata/analysis"},
                    "limit":{"type":"integer","description":"Array page size (default 50, max 200)"},
                    "cursor":{"type":"string","description":"Paging cursor from a previous response"},
                    "maxChars":{"type":"integer","description":"Optional response size guardrail; returns an error if exceeded"}
                  },
                  "required":["path"]
                }
                """).RootElement.Clone()
            },
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
