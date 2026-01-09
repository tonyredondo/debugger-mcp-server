# Automated Crash Analysis Examples

This document provides examples of the automated crash analysis functionality in the Debugger MCP Server.

> MCP tools note: the server exposes a compact 11-tool MCP surface. The canonical list is `DebuggerMcp/Resources/mcp_tools.md` (also served as `debugger://mcp-tools`).

## Overview

The Debugger MCP Server includes automated analysis tools that execute relevant debugger commands, parse the output, and return structured JSON results. This makes it easier for LLMs and other tools to understand crash dumps without manually executing and parsing individual commands.

## Tools Used In This Document

| Tool | Description | Key parameters |
|------|-------------|----------------|
| `analyze` | Automated analysis (crash/AI/perf/security) | `kind`, `sessionId`, `userId`, `includeWatches?` |
| `compare` | Compare two sessions/dumps | `kind`, `baselineSessionId`, `baselineUserId`, `targetSessionId`, `targetUserId` |
| `inspect` | Object/module/SOS helpers | `kind`, `sessionId`, `userId` (+ kind-specific args) |
| `report` | Report generation + section fetch | `action`, `sessionId`, `userId`, `format?`, `path?`, `limit?`, `cursor?`, `maxChars?` |
| `dump` | Open/close a dump in a session | `action`, `sessionId`, `userId`, `dumpId` |

---

## JSON Structure Overview

Crash analysis outputs (`analyze` kind `crash`/`ai` and `report` with `format: "json"`) return a canonical JSON report document:

```json
{
  "metadata": { },      // Report metadata (IDs, timestamps, debugger type, server version)
  "analysis": {
    "summary": { },       // Crash summary, severity, recommendations, warnings, errors
    "exception": { },     // Exception details with stack trace and typeResolution
    "environment": { },   // Platform, runtime, process, crashInfo, nativeAot
    "threads": { },       // Thread summary, all threads, deadlock info
    "memory": { },        // GC, heap, leaks, OOM info
    "assemblies": { },    // .NET assemblies with versions
    "modules": [ ],       // Native modules
    "async": { },         // Async/await state machines, tasks, timers
    "security": { },      // Security analysis findings
    "synchronization": { },// Synchronization primitives + ownership/contenders
    "watches": { },       // Watch expression evaluation results
    "signature": { },     // Stable dedup signature for triage
    "sourceContext": [ ], // Bounded source snippets (when available)
    "aiAnalysis": { }     // AI-only (present for analyze(kind="ai"); includes evidenceLedger + hypotheses)
  }
}
```

---

## 1. analyze(kind="crash")

**Purpose**: .NET crash analysis (SOS + ClrMD enrichment where available). Includes security analysis and watch expression evaluations.

**Usage**:
```
analyze(kind="crash", sessionId="session-123", userId="user1", includeWatches=true)
```

**What it does**:
- Extracts managed exception information (including inner exception chains when available)
- Analyzes managed stacks and thread state
- Collects heap statistics and large object allocations
- Detects common deadlock patterns (Tasks, locks, sync blocks, etc.)
- Detects .NET memory leak indicators from heap data
- Runs security vulnerability analysis
- Evaluates watch expressions
- Provides recommendations based on crash type

**Example Output (excerpt)**:
```json
{
  "metadata": {
    "dumpId": "crash-dump-001",
    "userId": "user1",
    "generatedAt": "2024-01-15T10:30:00Z",
    "format": "Json",
    "debuggerType": "LLDB",
    "serverVersion": "1.0.0"
  },
  "analysis": {
    "summary": {
      "crashType": "Access violation (c0000005)",
      "severity": "critical",
      "threadCount": 12,
      "moduleCount": 45
    },
    "exception": {
      "type": "EXCEPTION_ACCESS_VIOLATION (c0000005)",
      "address": "0x00007ff812345678"
    },
    "environment": {
      "platform": {
        "os": "Linux",
        "architecture": "x64"
      }
    },
    "threads": {
      "osThreadCount": 12
    },
    "memory": {
      "leakAnalysis": {
        "detected": true,
        "totalHeapBytes": 536870912
      }
    },
    "modules": [
      {
        "name": "MyApp",
        "version": "1.2.3.0",
        "baseAddress": "0x00007ff812340000",
        "hasSymbols": true
      }
    ],
    "security": {
      "overallRisk": "medium"
    },
    "watches": {
      "totalWatches": 2
    }
  }
}
```

Note: Raw debugger command outputs are intentionally not embedded in the report JSON to keep exports smaller and avoid polluting downstream LLM context.

---

## 1b. analyze(kind="ai")

**Purpose**: AI-assisted crash analysis via MCP sampling. The server generates an initial crash report (JSON) and then runs an iterative LLM investigation loop where the LLM can request additional evidence via tools.

**Usage**:
```
analyze(kind="ai", sessionId="session-123", userId="user1")
```

**Notes**:
- Requires an MCP client that supports sampling (`sampling/createMessage`) with tools enabled.
- When the AI needs object details, prefer the first-class sampling tool (`inspect(address=\"0x...\", maxDepth=3)`) over raw SOS `dumpobj` commands.
- Prefer `report(action=\"index\")` to seed LLM context and `report(action=\"get\", path=...)` (or the sampling tool `report_get(path=...)`) to fetch additional evidence instead of re-running full analysis.
  - Use `select=[...]` to project only needed fields, `where={field,equals}` for simple array filtering, and `pageKind=\"object\"` to page large objects when needed.
  - Arrays do not support slice/range syntax (e.g., `analysis.exception.stackTrace[0:10]` is invalid). To bound responses, use `limit` + (when provided) `cursor`, and/or reduce payload via `select` (example: `report_get(path="analysis.exception.stackTrace", limit=10, select=["frameNumber","function","module","sourceFile","lineNumber"])`).
- To debug sampling prompts/responses on the server, enable `DEBUGGER_MCP_AI_SAMPLING_TRACE` and `DEBUGGER_MCP_AI_SAMPLING_TRACE_FILES` (writes to `LOG_STORAGE_PATH/ai-sampling`).
- Some OpenRouter models reject `tool_choice=\"required\"` during sampling; the server detects this (often a 404 mentioning `tool_choice`) and caches a `tool_choice=\"auto\"` fallback for the rest of the run to avoid repeated failures/budget waste.
- Output matches `analyze(kind="crash")` plus an `analysis.aiAnalysis` section.
  - The server also rewrites `analysis.summary.description` / `analysis.summary.recommendations`, and adds `analysis.aiAnalysis.threadNarrative` + `analysis.threads.summary.description`.

**Example Output (excerpt)**:
```json
{
  "metadata": {
    "dumpId": "crash-dump-001",
    "userId": "user1",
    "generatedAt": "2024-01-15T10:30:00Z",
    "format": "Json",
    "debuggerType": "LLDB",
    "serverVersion": "1.0.0"
  },
  "analysis": {
    "summary": {
      "crashType": "Managed Exception",
      "severity": "high",
      "description": "AI-rewritten summary description...",
      "recommendations": [ "AI-rewritten recommendation 1", "AI-rewritten recommendation 2" ]
    },
    "aiAnalysis": {
      "rootCause": "Race condition in UserService.GetCurrentUser() leading to a null dereference during logout.",
      "confidence": "high",
      "reasoning": "The faulting thread dereferenced a field that another thread set to null; no synchronization was present.",
      "evidence": [
        "E1: report_get(path=\"analysis.exception.type\") → System.NullReferenceException",
        "E4: report_get(path=\"analysis.threads.faultingThread\") → faulting thread has no lock ownership"
      ],
      "evidenceLedger": [
        {
          "id": "E1",
          "source": "report_get(path=\"analysis.exception.type\")",
          "finding": "Exception type is System.NullReferenceException"
        },
        {
          "id": "E4",
          "source": "report_get(path=\"analysis.threads.faultingThread\")",
          "finding": "Faulting thread indicates a null dereference without relevant locks held",
          "whyItMatters": "Supports a race/ordering bug rather than an environment/setup issue.",
          "tags": [ "threads", "exception" ]
        }
      ],
      "hypotheses": [
        {
          "id": "H1",
          "hypothesis": "Race condition / data race",
          "confidence": "high",
          "supportsEvidenceIds": [ "E1", "E4" ],
          "contradictsEvidenceIds": [],
          "unknowns": [],
          "notes": "No synchronization protects the shared field; failure matches stack."
        },
        {
          "id": "H2",
          "hypothesis": "Corrupted dump / heap corruption",
          "confidence": "low",
          "supportsEvidenceIds": [],
          "contradictsEvidenceIds": [ "E1" ],
          "unknowns": [ "Need verifyheap output to rule out corruption conclusively." ],
          "notes": "No evidence of corruption; exception is consistent and repeatable."
        }
      ],
      "summary": {
        "description": "AI-rewritten summary description...",
        "recommendations": [ "AI-rewritten recommendation 1", "AI-rewritten recommendation 2" ],
        "iterations": 2
      },
      "threadNarrative": {
        "description": "At the time of the dump, the process was ...",
        "confidence": "medium",
        "iterations": 2
      },
      "iterations": 2
    }
  }
}
```

---

## 2. analyze(kind="crash") (managed details)

**Purpose**: This section highlights the managed/.NET-specific evidence that `analyze(kind="crash")` collects.

**Prerequisites**:
- Dump must be opened with `dump(action="open", ...)` (SOS is **auto-loaded** for .NET dumps)
- The dump must be from a .NET application

**Usage**:
```
# SOS is auto-loaded by dump(action="open") for .NET dumps
analyze(kind="crash", sessionId="session-123", userId="user1")
```

> **Note**: Avoid manually loading SOS during normal flows; `dump(action="open")` auto-loads it for .NET dumps. Only use `inspect(kind="load_sos", ...)` if you have explicit evidence that SOS is not loaded (e.g., SOS commands consistently fail).

**What it does**:
- Extracts CLR version information
- Analyzes managed exceptions with inner exception chain
- Collects heap statistics
- Detects async/await deadlocks
- Analyzes finalizer queue
- Thread pool analysis
- Assembly version information (useful for version mismatch issues)
- Provides .NET specific recommendations

**Example Output (excerpt)**:
```json
{
  "metadata": {
    "dumpId": "dotnet-crash-001",
    "userId": "user1",
    "generatedAt": "2024-01-15T10:30:00Z",
    "format": "Json",
    "debuggerType": "LLDB",
    "serverVersion": "1.0.0"
  },
  "analysis": {
    "summary": {
      "crashType": ".NET managed exception",
      "severity": "high"
    },
    "environment": {
      "runtime": {
        "clrVersion": "10.0.0"
      }
    },
    "exception": {
      "type": "System.MissingMethodException"
    },
    "assemblies": {
      "count": 45,
      "items": [
        {
          "name": "System.Collections.Concurrent",
          "assemblyVersion": "10.0.0.0",
          "fileVersion": "10.0.0.0",
          "path": "/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Collections.Concurrent.dll"
        }
      ]
    }
  }
}
```

---

## 3. analyze(kind="performance")

**Purpose**: Comprehensive performance analysis focusing on CPU usage, memory allocation patterns, GC behavior, and thread contention.

**Usage**:
```
analyze(kind="performance", sessionId="session-123", userId="user1", includeWatches=true)
```

**What it does**:
- CPU analysis: hot paths, function call counts
- Memory allocation profiling
- GC pressure analysis (gen0/1/2 collections, LOH usage)
- Thread contention detection (lock waits, synchronization issues)
- I/O patterns if available
- Provides performance optimization recommendations

---

## 4. analyze(kind="security")

**Purpose**: Security-focused analysis to detect potential vulnerabilities and security issues.

**Usage**:
```
analyze(kind="security", sessionId="session-123", userId="user1")
```

**What it detects**:
- Buffer overflows (stack/heap)
- Use-after-free conditions
- Null pointer dereferences
- Format string vulnerabilities
- Integer overflows
- Memory protection status (DEP, ASLR, Stack Canaries)
- Suspicious patterns in crash context

**Security findings include**:
- CWE identifiers
- Severity ratings (Critical, High, Medium, Low)
- Confidence levels
- Exploitation indicators
- Remediation recommendations

---

## Notes

### JSON Property Names

The JSON report schema uses explicit `JsonPropertyName` attributes and is predominantly camelCase (e.g., `dumpId`, `threadCount`, `osThreadIdDecimal`).

### Null Handling

Properties that have no value are omitted from the JSON output (using `JsonIgnoreCondition.WhenWritingNull`). This keeps the output clean and reduces size.

### Managed Enrichment

`analyze(kind="crash")` uses SOS and (when available) ClrMD automatically; there is no `deepAnalysis` parameter. For deeper, hypothesis-driven investigation, prefer targeted `inspect` calls (managed objects/stacks) or `exec` SOS commands.

### Source Link Integration

When assemblies have Source Link information embedded, the analysis automatically resolves:
- `sourceUrl`: Direct link to the source file in the repository
- `sourceProvider`: The repository host (GitHub, Azure DevOps, etc.)
- `commit`: Git commit metadata (SHA, author, message)

This works for both exception stack traces and thread call stacks.
