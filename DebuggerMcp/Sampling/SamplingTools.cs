#nullable enable

using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace DebuggerMcp.Sampling;

/// <summary>
/// Defines the tool schema exposed to LLMs via MCP sampling.
/// </summary>
public static class SamplingTools
{
    private static readonly JsonElement ExecSchema = ParseSchema("""
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "Debugger command to execute (LLDB/WinDbg/SOS)." }
          },
          "required": ["command"]
        }
        """);

    private static readonly JsonElement InspectSchema = ParseSchema("""
        {
          "type": "object",
          "properties": {
            "address": { "type": "string", "description": "Object memory address in hex (e.g., 0x7f8a2b3c4d50)." },
            "maxDepth": { "type": "integer", "description": "Maximum recursion depth (default: 3, max: 5)." }
          },
          "required": ["address"]
        }
        """);

    private static readonly JsonElement GetThreadStackSchema = ParseSchema("""
        {
          "type": "object",
          "properties": {
            "threadId": { "type": "string", "description": "Thread ID/index/OSID from the report (e.g., 5, 0x1234, or debugger threadId)." }
          },
          "required": ["threadId"]
        }
        """);

    private static readonly JsonElement ReportGetSchema = ParseSchema("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Dot-path under metadata/analysis (e.g., analysis.exception, analysis.threads.all)." },
            "limit": { "type": "integer", "description": "Array page size (default: 50, max: 200)." },
            "cursor": { "type": "string", "description": "Paging cursor from a previous response (optional)." },
            "maxChars": { "type": "integer", "description": "Optional response size guardrail; returns an error if exceeded." }
          },
          "required": ["path"]
        }
        """);

    private static readonly JsonElement AnalysisCompleteSchema = ParseSchema("""
        {
          "type": "object",
          "properties": {
            "rootCause": { "type": "string", "description": "Identified root cause of the crash." },
            "confidence": { "type": "string", "enum": ["high", "medium", "low"], "description": "Confidence level." },
            "reasoning": { "type": "string", "description": "Step-by-step reasoning and evidence." },
            "recommendations": { "type": "array", "items": { "type": "string" }, "description": "Recommended fixes or next steps." },
            "additionalFindings": { "type": "array", "items": { "type": "string" }, "description": "Other observations discovered during analysis." }
          },
          "required": ["rootCause", "confidence", "reasoning"]
        }
        """);

    /// <summary>
    /// Returns the list of tools the LLM may call during AI analysis.
    /// </summary>
    public static IList<Tool> GetDebuggerTools() =>
        new List<Tool>
        {
            new()
            {
                Name = "exec",
                Description = "Execute a debugger command (LLDB/WinDbg/SOS) and return the output.",
                InputSchema = ExecSchema
            },
            new()
            {
                Name = "report_get",
                Description = "Fetch a section of the canonical crash report JSON by dot-path (paged for arrays).",
                InputSchema = ReportGetSchema
            },
            new()
            {
                Name = "inspect",
                Description = "Inspect a .NET object at an address and return a JSON summary of fields/values (ClrMD-based when available).",
                InputSchema = InspectSchema
            },
            new()
            {
                Name = "get_thread_stack",
                Description = "Return the full stack trace for a specific thread from the existing crash report.",
                InputSchema = GetThreadStackSchema
            },
            new()
            {
                Name = "analysis_complete",
                Description = "Call this when you have gathered enough information to determine the root cause. This ends the analysis loop.",
                InputSchema = AnalysisCompleteSchema
            }
        };

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
