# Automated Crash Analysis Examples

This document provides examples of the automated crash analysis functionality in the Debugger MCP Server.

> MCP tools note: the server exposes a compact 11-tool MCP surface. The canonical list is `DebuggerMcp/Resources/mcp_tools.md` (also served as `debugger://mcp-tools`).

## Overview

The Debugger MCP Server includes automated analysis tools that execute relevant debugger commands, parse the output, and return structured JSON results. This makes it easier for LLMs and other tools to understand crash dumps without manually executing and parsing individual commands.

## Tools Used In This Document

| Tool | Description | Key parameters |
|------|-------------|----------------|
| `analyze` | Automated analysis (crash/AI/perf/security) | `kind`, `sessionId`, `userId`, `includeWatches?` |
| `compare` | Compare two sessions/dumps | `kind`, `sessionId`, `userId`, `targetSessionId`, `targetUserId` |
| `inspect` | Object/module/SOS helpers | `kind`, `sessionId`, `userId` (+ kind-specific args) |
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
    "watches": { },       // Watch expression evaluation results
    "aiAnalysis": { }     // AI-only (present for analyze(kind="ai"))
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
- To debug sampling prompts/responses on the server, enable `DEBUGGER_MCP_AI_SAMPLING_TRACE` and `DEBUGGER_MCP_AI_SAMPLING_TRACE_FILES` (writes to `LOG_STORAGE_PATH/ai-sampling`).
- Output matches `analyze(kind="crash")` plus an `analysis.aiAnalysis` section.

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
    "aiAnalysis": {
      "rootCause": "Race condition in UserService.GetCurrentUser() leading to a null dereference during logout.",
      "confidence": "high",
      "reasoning": "The faulting thread dereferenced a field that another thread set to null; no synchronization was present.",
      "iterations": 2,
      "commandsExecuted": [
        { "tool": "exec", "input": { "command": "!threads" }, "output": "...", "iteration": 1, "duration": "00:00:00.123" }
      ]
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

> **Note**: If SOS auto-detection failed, you can manually call `inspect(kind=\"load_sos\", ...)` first.

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
        "clrVersion": "9.0.10"
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
          "assemblyVersion": "9.0.0.0",
          "fileVersion": "9.0.10.0",
          "path": "/usr/share/dotnet/shared/Microsoft.NETCore.App/9.0.10/System.Collections.Concurrent.dll"
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
