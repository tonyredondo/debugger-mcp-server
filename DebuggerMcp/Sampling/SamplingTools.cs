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
            "path": { "type": "string", "description": "Path under metadata/analysis (dot-path + optional [index], e.g., analysis.exception, analysis.threads.all[0])." },
            "limit": { "type": "integer", "description": "Page size for arrays (and for objects when pageKind='object') (default: 50, max: 200)." },
            "cursor": { "type": "string", "description": "Paging cursor from a previous response (optional)." },
            "pageKind": { "type": "string", "enum": ["array","object","auto"], "description": "Paging kind: array (default) | object | auto." },
            "select": { "type": "array", "items": { "type": "string" }, "description": "Projection: object fields to include (applies to objects and array items)." },
            "where": { "type": "object", "description": "Filter (arrays only): exact match on a field.", "properties": { "field": { "type": "string" }, "equals": { "type": "string" }, "caseInsensitive": { "type": "boolean", "default": true } }, "required": ["field","equals"] },
            "maxChars": { "type": "integer", "description": "Optional response size guardrail (default: 20000). If exceeded, returns a 'too_large' error with suggested sub-paths and paging hints.", "default": 20000 }
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
            "evidence": { "type": "array", "items": { "type": "string" }, "description": "Key evidence supporting rootCause. Each item should cite a tool call or report_get path and the specific finding." },
            "recommendations": { "type": "array", "items": { "type": "string" }, "description": "Recommended fixes or next steps." },
            "additionalFindings": { "type": "array", "items": { "type": "string" }, "description": "Other observations discovered during analysis." }
          },
          "required": ["rootCause", "confidence", "reasoning", "evidence"]
        }
        """);

    private static readonly JsonElement EvidenceAddSchema = ParseSchema("""
        {
          "type": "object",
          "properties": {
            "items": {
              "type": "array",
              "description": "Evidence items to add to the running evidence ledger (bounded). Batch items; do not spam.",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string", "description": "Optional evidence ID (e.g., E12). If omitted, the server assigns one." },
                  "source": { "type": "string", "description": "Where this evidence came from (e.g., report_get(path=\"analysis.exception.type\"), exec(\"!dumpmt -md 0x...\"))." },
                  "finding": { "type": "string", "description": "What was observed (short, specific, grounded in tool output)." },
                  "whyItMatters": { "type": "string", "description": "Optional: why this finding supports/refutes hypotheses." },
                  "tags": { "type": "array", "items": { "type": "string" }, "description": "Optional tags (e.g., trimming, r2r, mismatch)." }
                },
                "required": ["source", "finding"]
              }
            }
          },
          "required": ["items"]
        }
        """);

    private static readonly JsonElement HypothesisRegisterSchema = ParseSchema("""
        {
          "type": "object",
          "properties": {
            "hypotheses": {
              "type": "array",
              "description": "Register 2-4 competing hypotheses early (bounded). Batch; do not spam.",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string", "description": "Optional hypothesis ID (e.g., H2). If omitted, the server assigns one." },
                  "hypothesis": { "type": "string", "description": "Hypothesis statement." },
                  "confidence": { "type": "string", "enum": ["high", "medium", "low", "unknown"], "description": "Current confidence." },
                  "unknowns": { "type": "array", "items": { "type": "string" }, "description": "Key unknowns to resolve." },
                  "testsToRun": { "type": "array", "items": { "type": "string" }, "description": "Proposed next tool calls to test/falsify this hypothesis." }
                },
                "required": ["hypothesis", "confidence"]
              }
            }
          },
          "required": ["hypotheses"]
        }
        """);

    private static readonly JsonElement HypothesisScoreSchema = ParseSchema("""
        {
          "type": "object",
          "properties": {
            "updates": {
              "type": "array",
              "description": "Update hypothesis confidence and link evidence IDs (bounded). Batch; do not spam.",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string", "description": "Hypothesis ID (e.g., H1)." },
                  "confidence": { "type": "string", "enum": ["high", "medium", "low", "unknown"], "description": "Updated confidence." },
                  "supportsEvidenceIds": { "type": "array", "items": { "type": "string" }, "description": "Evidence IDs that support this hypothesis." },
                  "contradictsEvidenceIds": { "type": "array", "items": { "type": "string" }, "description": "Evidence IDs that contradict this hypothesis." },
                  "notes": { "type": "string", "description": "Optional notes/rationale for the update." }
                },
                "required": ["id"]
              }
            }
          },
          "required": ["updates"]
        }
        """);

    /// <summary>
    /// Returns the list of tools the LLM may call during root-cause AI analysis.
    /// </summary>
    public static IList<Tool> GetCrashAnalysisTools() =>
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
            },
            new()
            {
                Name = "analysis_evidence_add",
                Description = "Internal meta tool: add evidence items to a bounded evidence ledger (does not execute debugger commands).",
                InputSchema = EvidenceAddSchema
            },
            new()
            {
                Name = "analysis_hypothesis_register",
                Description = "Internal meta tool: register competing hypotheses (does not execute debugger commands).",
                InputSchema = HypothesisRegisterSchema
            },
            new()
            {
                Name = "analysis_hypothesis_score",
                Description = "Internal meta tool: update hypothesis confidence and link evidence IDs (does not execute debugger commands).",
                InputSchema = HypothesisScoreSchema
            }
        };

    private static readonly JsonElement SummaryRewriteCompleteSchema = ParseSchema("""
        {
          "type": "object",
          "properties": {
            "description": { "type": "string", "description": "Rewritten analysis.summary.description (human-readable summary; evidence-backed)." },
            "recommendations": { "type": "array", "items": { "type": "string" }, "description": "Rewritten analysis.summary.recommendations (actionable, evidence-backed)." }
          },
          "required": ["description", "recommendations"]
        }
        """);

    private static readonly JsonElement ThreadNarrativeCompleteSchema = ParseSchema("""
        {
          "type": "object",
          "properties": {
            "description": { "type": "string", "description": "Narrative description of what the process was doing at the time of the dump (thread-based; evidence-backed)." },
            "confidence": { "type": "string", "enum": ["high", "medium", "low"], "description": "Confidence level for the narrative." }
          },
          "required": ["description", "confidence"]
        }
        """);

    /// <summary>
    /// Returns the list of tools the LLM may call when rewriting analysis.summary.* fields.
    /// </summary>
    public static IList<Tool> GetSummaryRewriteTools() =>
        new List<Tool>
        {
            new() { Name = "exec", Description = "Execute a debugger command (LLDB/WinDbg/SOS) and return the output.", InputSchema = ExecSchema },
            new() { Name = "report_get", Description = "Fetch a section of the canonical crash report JSON by dot-path (paged for arrays).", InputSchema = ReportGetSchema },
            new() { Name = "inspect", Description = "Inspect a .NET object at an address and return a JSON summary of fields/values (ClrMD-based when available).", InputSchema = InspectSchema },
            new() { Name = "get_thread_stack", Description = "Return the full stack trace for a specific thread from the existing crash report.", InputSchema = GetThreadStackSchema },
            new() { Name = "analysis_summary_rewrite_complete", Description = "Call this when you have rewritten analysis.summary.description and analysis.summary.recommendations.", InputSchema = SummaryRewriteCompleteSchema }
        };

    /// <summary>
    /// Returns the list of tools the LLM may call when generating a thread narrative.
    /// </summary>
    public static IList<Tool> GetThreadNarrativeTools() =>
        new List<Tool>
        {
            new() { Name = "exec", Description = "Execute a debugger command (LLDB/WinDbg/SOS) and return the output.", InputSchema = ExecSchema },
            new() { Name = "report_get", Description = "Fetch a section of the canonical crash report JSON by dot-path (paged for arrays).", InputSchema = ReportGetSchema },
            new() { Name = "inspect", Description = "Inspect a .NET object at an address and return a JSON summary of fields/values (ClrMD-based when available).", InputSchema = InspectSchema },
            new() { Name = "get_thread_stack", Description = "Return the full stack trace for a specific thread from the existing crash report.", InputSchema = GetThreadStackSchema },
            new() { Name = "analysis_thread_narrative_complete", Description = "Call this when you have produced an evidence-backed narrative of what the process was doing.", InputSchema = ThreadNarrativeCompleteSchema }
        };

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
