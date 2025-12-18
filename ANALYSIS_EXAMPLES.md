# Automated Crash Analysis Examples

This document provides examples of the automated crash analysis functionality in the Debugger MCP Server.

> MCP tools note: the server exposes a compact 11-tool MCP surface. The canonical list is `DebuggerMcp/Resources/mcp_tools.md` (also served as `debugger://mcp-tools`).

## Overview

The Debugger MCP Server includes automated analysis tools that execute relevant debugger commands, parse the output, and return structured JSON results. This makes it easier for LLMs and other tools to understand crash dumps without manually executing and parsing individual commands.

## Tools Used In This Document

| Tool | Description | Key parameters |
|------|-------------|----------------|
| `analyze` | Automated analysis (crash/.NET/AI/perf/security) | `kind`, `sessionId`, `userId`, `includeWatches?` |
| `compare` | Compare two sessions/dumps | `kind`, `sessionId`, `userId`, `targetSessionId`, `targetUserId` |
| `inspect` | Object/module/SOS helpers | `kind`, `sessionId`, `userId` (+ kind-specific args) |
| `dump` | Open/close a dump in a session | `action`, `sessionId`, `userId`, `dumpId` |

---

## JSON Structure Overview

All analysis tools return a hierarchical JSON structure with these top-level sections:

```json
{
  "metadata": { },      // Report metadata (IDs, timestamps)
  "summary": { },       // Crash summary, severity, recommendations, warnings, errors
  "exception": { },     // Exception details with stack trace and typeResolution
  "environment": { },   // Platform, runtime, process, crashInfo, nativeAot
  "threads": { },       // Thread summary, all threads, deadlock info
  "memory": { },        // GC, heap, leaks, OOM info
  "assemblies": { },    // .NET assemblies with versions
  "modules": [ ],       // Native modules
  "async": { },         // Async/await state machines, tasks, timers
  "security": { },      // Security analysis findings
  "watches": { }        // Watch expression evaluation results
}
```

---

## 1. analyze(kind="crash")

**Purpose**: General crash analysis for any type of crash dump (native or managed). Includes security analysis and watch expression evaluations.

**Usage**:
```
analyze(kind="crash", sessionId="session-123", userId="user1", includeWatches=true)
```

**What it does**:
- Executes `!analyze -v` (WinDbg) or equivalent LLDB commands
- Extracts exception information
- Analyzes call stacks for all threads
- **Extracts process arguments (argv) and environment variables (Linux/macOS only)**
- Detects memory leak indicators
- Detects deadlock conditions
- Runs security vulnerability analysis
- Evaluates watch expressions
- Provides recommendations based on crash type

**Example Output**:
```json
{
  "metadata": {
    "dumpId": "crash-dump-001",
    "userId": "user1",
    "generatedAt": "2024-01-15T10:30:00Z",
    "debuggerType": "LLDB",
    "serverVersion": "1.0.0"
  },
  "summary": {
    "crashType": "Access Violation (c0000005)",
    "description": "Crash Type: Access Violation. Found 12 threads, 45 modules. MEMORY LEAK DETECTED: ~512 MB.",
    "severity": "Critical",
    "threadCount": 12,
    "moduleCount": 45,
    "recommendations": [
      "Large heap detected (536870912 bytes). This could indicate a memory leak.",
      "Some modules are missing symbols. Upload symbol files for better analysis.",
      "Check for null pointer dereferences in CrashingFunction"
    ]
  },
  "exception": {
    "type": "EXCEPTION_ACCESS_VIOLATION (c0000005)",
    "message": "The thread tried to read from or write to a virtual address for which it does not have the appropriate access.",
    "address": "0x00007ff812345678",
    "stackTrace": [
    {
        "frameNumber": 0,
        "module": "MyApp",
        "function": "CrashingFunction",
        "sourceFile": "processor.cpp",
        "lineNumber": 42,
        "sourceUrl": "https://github.com/myorg/myapp/blob/v1.2.3/processor.cpp#L42"
      }
    ]
  },
  "environment": {
    "platform": {
      "os": "Linux",
      "architecture": "x64",
      "distribution": "Debian",
      "isAlpine": false,
      "runtimeVersion": "8.0.10",
      "pointerSize": 64
    },
    "process": {
      "arguments": [
        "dotnet",
        "/app/MyApp.dll",
        "--port=5000"
      ],
      "environmentVariables": [
        "ASPNETCORE_ENVIRONMENT=Production",
        "DOTNET_ROOT=/usr/share/dotnet",
        "HOME=/root",
        "PATH=/usr/local/bin:/usr/bin"
      ],
      "argc": 3,
      "argvAddress": "0x0000ffffefcba618",
      "sensitiveDataFiltered": true
    },
    "runtime": {
      "clrVersion": "8.0.10",
      "isHosted": false
    }
  },
  "threads": {
    "summary": {
      "total": 12,
      "background": 8,
      "dead": 0,
      "finalizerQueueLength": 150
    },
    "all": [
      {
        "threadId": "0 (1234)",
        "state": "Unfrozen",
        "isFaulting": true,
        "topFunction": "MyApp!CrashingFunction+0x42",
        "callStack": [
    {
            "frameNumber": 0,
            "instructionPointer": "0x00007ff812345678",
            "module": "MyApp",
            "function": "CrashingFunction",
            "source": "processor.cpp:42",
            "sourceFile": "processor.cpp",
            "lineNumber": 42,
            "sourceUrl": "https://github.com/myorg/myapp/blob/v1.2.3/processor.cpp#L42",
            "sourceProvider": "GitHub",
            "isManaged": false
    }
  ],
        "managedThreadId": null,
        "osThreadId": "1234",
        "gcMode": null
      }
    ],
    "deadlock": {
      "detected": false,
      "involvedThreads": [],
      "locks": []
    }
  },
  "memory": {
    "leakAnalysis": {
      "detected": true,
      "severity": "High",
      "topConsumers": [
        {"typeName": "Allocation size 4096", "count": 50000, "totalSize": 204800000}
      ],
      "totalHeapBytes": 536870912,
      "potentialIssueIndicators": [
        "Large heap detected (512 MB). Consider investigating memory usage.",
        "High allocation count for 4096 byte blocks - possible unbounded growth."
      ]
    }
  },
  "modules": [
    {
      "name": "MyApp",
      "version": "1.2.3.0",
      "baseAddress": "0x00007ff812340000",
      "hasSymbols": true
    },
    {
      "name": "ntdll",
      "version": "10.0.19041.1",
      "baseAddress": "0x00007ffa12340000",
      "hasSymbols": true
    }
  ],
  "security": {
    "overallRisk": "Medium",
    "summary": "Found 1 vulnerability (0 critical, 0 high, 1 medium)",
    "findings": [
      {
        "type": "NullPointerDereference",
        "severity": "Medium",
        "description": "Null pointer dereference detected at low address",
        "confidence": "Confirmed",
        "cweIds": ["CWE-476"],
        "indicators": ["Access violation at address 0x0000000000000000"],
        "mitigation": "Check for null before dereferencing pointers"
      }
    ],
    "memoryProtections": {
      "aslrEnabled": true,
      "depEnabled": true,
      "stackCanariesPresent": true
    }
  },
  "watches": {
    "dumpId": "crash-dump-001",
    "totalWatches": 2,
    "successfulEvaluations": 2,
    "failedEvaluations": 0,
    "watches": [
      {
        "watchId": "watch-1",
        "expression": "g_DataManager",
        "type": "Variable",
        "success": true,
        "value": "00000000`00000000 (null)"
      }
    ],
    "insights": [
      "⚠️ Watch 'g_DataManager' is NULL - may be relevant to crash"
    ]
  },
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
- Output matches `analyze(kind="crash")` plus an `aiAnalysis` section.

**Example Output (excerpt)**:
```json
{
  "aiAnalysis": {
    "rootCause": "Race condition in UserService.GetCurrentUser() leading to a null dereference during logout.",
    "confidence": "high",
    "reasoning": "The faulting thread dereferenced a field that another thread set to null; no synchronization was present.",
    "commandsExecuted": [
      { "tool": "exec", "input": { "command": "!threads" }, "output": "...", "iteration": 1, "duration": "00:00:00.123" }
    ]
  }
}
```

---

## 2. analyze(kind="dotnet_crash")

**Purpose**: .NET specific crash analysis with managed code insights, including deep analysis with ClrMD.

**Prerequisites**:
- Dump must be opened with `dump(action="open", ...)` (SOS is **auto-loaded** for .NET dumps)
- The dump must be from a .NET application

**Usage**:
```
# Basic analysis (SOS auto-loaded by dump(action="open"))
analyze(kind="dotnet_crash", sessionId="session-123", userId="user1")

# Deep analysis with ClrMD heap inspection (slower but more detailed)
analyze(kind="dotnet_crash", sessionId="session-123", userId="user1", deepAnalysis=true)
```

> **Note**: If SOS auto-detection failed, you can manually call `inspect(kind=\"load_sos\", ...)` first.

**What it does**:
- Performs all general crash analysis (from `analyze(kind=\"crash\")`)
- Extracts CLR version information
- Analyzes managed exceptions with inner exception chain
- Collects heap statistics
- Detects async/await deadlocks
- Analyzes finalizer queue
- Thread pool analysis
- Assembly version information (useful for version mismatch issues)
- Deep analysis (optional): heap walk, string duplicates, async/task analysis
- Provides .NET specific recommendations

**Example Output**:
```json
{
  "metadata": {
    "dumpId": "dotnet-crash-001",
    "userId": "user1",
    "generatedAt": "2024-01-15T10:30:00Z",
    "debuggerType": "LLDB",
    "serverVersion": "1.0.0"
  },
  "summary": {
    "crashType": ".NET Managed Exception",
    "description": ".NET Analysis: CLR 8.0.10. Managed Exception: System.NullReferenceException. Heap has 1250 types.",
    "severity": "High",
    "threadCount": 15,
    "moduleCount": 87,
    "assemblyCount": 45,
    "recommendations": [
      "Exception: System.NullReferenceException - Object reference not set to an instance of an object.",
      "Review null checks in UserService.GetUser method",
      "Large finalizer queue (150 objects) may indicate resource management issues"
    ]
  },
  "exception": {
    "type": "System.NullReferenceException",
    "message": "Object reference not set to an instance of an object.",
    "address": "0x00007ff8a1234567",
    "hResult": "0x80004003",
    "hasInnerException": false,
    "nestedExceptionCount": 0,
    "stackTrace": [
    {
        "frameNumber": 0,
        "module": "MyApp",
        "function": "MyApp.Services.UserService.GetUser(Int32)",
        "sourceFile": "UserService.cs",
        "lineNumber": 42,
        "sourceUrl": "https://github.com/myorg/myapp/blob/main/src/Services/UserService.cs#L42",
        "sourceProvider": "GitHub",
        "isManaged": true,
        "parameters": [
          {"name": "userId", "type": "Int32", "value": "12345", "hasData": true}
        ],
        "locals": [
          {"name": "user", "type": "MyApp.Models.User", "value": "null", "isReferenceType": true, "hasData": true}
        ]
      }
    ],
    "analysis": {
      "targetSite": {
        "declaringType": "MyApp.Services.UserService",
        "methodName": "GetUser",
        "parameters": ["Int32"]
      },
      "exceptionChain": [
        {
          "depth": 0,
          "type": "System.NullReferenceException",
          "message": "Object reference not set to an instance of an object.",
          "hResult": "0x80004003"
        }
      ]
    }
  },
  "environment": {
    "platform": {
      "os": "Linux",
      "architecture": "arm64",
      "distribution": "Alpine",
      "isAlpine": true,
      "libcType": "musl",
      "runtimeVersion": "8.0.10.23424",
      "pointerSize": 64
    },
    "process": {
      "arguments": ["dotnet", "/app/MyApp.dll"],
      "environmentVariables": [
        "ASPNETCORE_ENVIRONMENT=Production",
        "DOTNET_ROOT=/usr/share/dotnet"
      ],
      "sensitiveDataFiltered": true
    },
    "runtime": {
      "clrVersion": "8.0.10.23424",
      "isHosted": false
    },
    "crashInfo": {
      "signalName": "SIGSEGV",
      "signalCode": 11,
      "faultingAddress": "0x0000000000000000",
      "message": "Segmentation fault at null address"
    },
    "nativeAot": {
      "isNativeAot": false,
      "hasJitCompiler": true,
      "indicators": []
    }
    },
  "threads": {
    "summary": {
      "total": 15,
      "background": 12,
      "unstarted": 0,
      "pending": 0,
      "dead": 0,
      "finalizerQueueLength": 150
  },
    "all": [
      {
        "threadId": "0 (1234)",
        "state": "Exception",
        "isFaulting": true,
        "topFunction": "MyApp.Services.UserService.GetUser(Int32)",
        "callStack": [
          {
            "frameNumber": 0,
            "instructionPointer": "0x00007ff8a1234567",
            "module": "MyApp",
            "function": "MyApp.Services.UserService.GetUser(Int32)",
            "sourceFile": "UserService.cs",
            "lineNumber": 42,
            "isManaged": true
          }
        ],
        "managedThreadId": 1,
        "osThreadId": "1234",
        "threadObject": "0x00007ff800001000",
        "clrThreadState": "0x20220",
        "gcMode": "Preemptive",
        "lockCount": 0,
        "apartmentState": "MTA",
        "currentException": "System.NullReferenceException",
        "isDead": false,
        "isBackground": false,
        "isThreadpool": false
      }
    ],
    "threadPool": {
      "cpuUtilization": 45,
      "workersTotal": 8,
      "workersRunning": 3,
      "workersIdle": 5,
      "workerMinLimit": 4,
      "workerMaxLimit": 32767,
      "isPortableThreadPool": true
    },
    "deadlock": {
      "detected": false,
      "involvedThreads": [],
      "locks": []
    }
  },
  "memory": {
    "gc": {
      "totalHeapSize": 52428800,
      "gen0Size": 1048576,
      "gen1Size": 2097152,
      "gen2Size": 31457280,
      "lohSize": 17825792,
      "pohSize": 0,
      "isServerGc": true,
      "heapCount": 4,
      "fragmentationBytes": 524288,
      "fragmentation": 0.01
    },
    "heapStats": {
      "System.String": 15728640,
      "System.Byte[]": 8388608,
      "MyApp.Models.User": 1048576
    },
    "topConsumers": {
      "bySize": [
        {"typeName": "System.String", "count": 50000, "totalBytes": 15728640},
        {"typeName": "System.Byte[]", "count": 1000, "totalBytes": 8388608}
      ],
      "byCount": [
        {"typeName": "System.String", "count": 50000, "totalBytes": 15728640},
        {"typeName": "System.Object", "count": 25000, "totalBytes": 400000}
      ]
    },
    "strings": {
      "summary": {
        "totalCount": 50000,
        "totalBytes": 15728640,
        "uniqueCount": 35000,
        "duplicateCount": 15000,
        "wastedSize": 2097152
      },
      "topDuplicates": [
        {"value": "null", "count": 5000, "wastedBytes": 50000}
      ]
    },
    "leakAnalysis": {
      "detected": false,
      "severity": null,
      "totalHeapBytes": 52428800
    }
  },
  "assemblies": {
    "count": 45,
    "items": [
      {
        "name": "MyApp",
        "version": "1.0.0.0",
        "assemblyVersion": "1.0.0.0",
        "fileVersion": "1.0.0.0",
        "informationalVersion": "1.0.0+abc123",
        "path": "/app/MyApp.dll",
        "hasSymbols": true,
        "sourceUrl": "https://github.com/myorg/myapp/tree/abc123",
        "commit": {
          "sha": "abc123",
          "authorName": "Developer",
          "authorDate": "2024-01-10T15:30:00Z",
          "committerName": "Developer",
          "committerDate": "2024-01-10T15:30:00Z",
          "message": "feat: Add user service"
        }
      },
      {
        "name": "System.Private.CoreLib",
        "version": "8.0.0.0",
        "path": "/usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.10/System.Private.CoreLib.dll"
      }
    ]
  },
  "async": {
    "hasDeadlock": false,
    "summary": {
      "totalTasks": 25,
      "pendingTasks": 10,
      "completedTasks": 12,
      "faultedTasks": 2,
      "canceledTasks": 1
    },
    "stateMachines": [
      {
        "address": "0x00007ff800002000",
        "stateMachineType": "MyApp.Services.UserService+<GetUserAsync>d__5",
        "currentState": 0,
        "awaitingType": "System.Threading.Tasks.Task"
      }
    ],
    "timers": [
      {
        "address": "0x00007ff800003000",
        "dueTimeMs": 5000,
        "periodMs": 60000,
        "callback": "MyApp.Background.HealthCheck.OnTimer",
        "stateType": "MyApp.Background.HealthCheck"
      }
    ]
  },
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

The JSON output uses camelCase property names for all fields. This is consistent across all analysis types.

### Null Handling

Properties that have no value are omitted from the JSON output (using `JsonIgnoreCondition.WhenWritingNull`). This keeps the output clean and reduces size.

### Deep Analysis

The `deepAnalysis` parameter in `analyze(kind=\"dotnet_crash\")` enables ClrMD-based heap walking, which provides:
- Detailed type memory statistics
- String duplicate detection
- Async state machine analysis
- Large object heap analysis

This is slower but provides significantly more insight for memory-related issues.

### Source Link Integration

When assemblies have Source Link information embedded, the analysis automatically resolves:
- `sourceUrl`: Direct link to the source file in the repository
- `sourceProvider`: The repository host (GitHub, Azure DevOps, etc.)
- `commit`: Git commit metadata (SHA, author, message)

This works for both exception stack traces and thread call stacks.
