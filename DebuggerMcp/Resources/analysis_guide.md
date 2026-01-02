# Debugger MCP Server - Analysis Guide

## 🔍 Complete Guide to Automated Analysis Features

This guide covers all automated analysis features available in the Debugger MCP Server, including crash analysis, .NET-specific analysis, and dump comparison capabilities.

Tool reference: the canonical MCP tool list is `debugger://mcp-tools`.

---

## 📊 Analysis Features Overview

| Feature | Tool (Compact) | Description |
|---------|-----------------|-------------|
| **Crash Analysis** | `analyze(kind="crash")` | .NET crash analysis (SOS/ClrMD) including managed exceptions, heap stats, deadlocks |
| **AI Crash Analysis** | `analyze(kind="ai")` | AI-assisted root cause analysis via MCP sampling (iterative, tool-driven) |
| **Dump Comparison** | `compare(kind="dumps")` | Compare two dumps for memory, threads, and modules |
| **Heap Comparison** | `compare(kind="heaps")` | Memory allocation comparison for leak detection |
| **Thread Comparison** | `compare(kind="threads")` | Thread state comparison for deadlock detection |
| **Module Comparison** | `compare(kind="modules")` | Loaded module comparison for version tracking |
| **Performance Analysis** | `analyze(kind="performance")` | Comprehensive CPU, memory, GC, and contention analysis |
| **CPU Analysis** | `analyze(kind="cpu")` | CPU hotspot identification and runaway thread detection |
| **Allocation Analysis** | `analyze(kind="allocations")` | Memory allocation patterns and leak detection |
| **GC Analysis** | `analyze(kind="gc")` | Garbage collection behavior and heap fragmentation |
| **Contention Analysis** | `analyze(kind="contention")` | Thread contention, lock usage, and deadlock detection |
| **Watch Expressions** | `watch(action="add")`, `watch(action="evaluate_all")` | Track memory/variables across sessions |
| **Report Generation** | `report(action="full")` | Generate shareable reports in Markdown, HTML, JSON |
| **Source Link** | `source_link(action="resolve")` | Link stack frames to source code (GitHub, GitLab, etc.) |
| **Security Analysis** | `analyze(kind="security")` | Detect security vulnerabilities (buffer overflows, UAF, etc.) |

---

## 🔴 Crash Analysis (`analyze` / kind=`crash`)

### Overview

Use `analyze(kind: "crash")` to perform automated .NET crash analysis on an open .NET dump file. It works with both WinDbg (Windows) and LLDB (macOS/Linux) and returns structured JSON output.

### Prerequisites

1. Session created with `session(action="create")`
2. Dump file opened with `dump(action="open")`

### Usage

```
analyze(kind: "crash", sessionId: "your-session-id", userId: "your-user-id")
```

### What It Detects

#### Exception Information
- Exception type and code
- Exception message
- Faulting address
- Exception record details

#### Call Stack Analysis
- Full stack trace of the faulting thread
- Module and function names
- Instruction pointers
- Source file information (if symbols available)

#### Thread Information
- Total thread count
- Thread states (running, waiting, suspended)
- Faulting thread identification
- Thread names (if available)

#### Module Information
- Loaded modules list
- Module versions
- Symbol availability status
- Base addresses

#### Memory Leak Detection
- Total heap memory usage
- Large allocation detection (>500MB triggers warning)
- Top memory consumers
- Estimated leaked bytes

#### Deadlock Detection
- Lock ownership analysis
- Threads waiting on locks
- Potential deadlock cycles
- Wait time analysis

### Example Output

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
      "severity": "critical"
    },
    "exception": {
      "type": "0xc0000005",
      "message": "Access violation reading location 0x0000000000000000",
      "address": "0x00007ff812345678"
    },
    "threads": {
      "deadlock": {
        "detected": false
      }
    },
    "memory": {
      "leakAnalysis": {
        "detected": true,
        "totalHeapBytes": 536870912
      }
    }
  }
}
```

---

## 🤖 AI Crash Analysis (`analyze` / kind=`ai`)

### Overview

Use `analyze(kind: "ai")` to run an AI-assisted analysis loop. The server will:
1. Build an initial structured crash report (JSON)
2. Use MCP sampling (`sampling/createMessage`) to ask the connected client’s LLM to analyze it
3. Allow the LLM to request additional evidence via tools (e.g., `report_get`, `exec`, `inspect`, `get_thread_stack`)
4. Return the original report enriched with `analysis.aiAnalysis` (root cause + evidence list + evidence ledger + hypotheses)
5. Rewrite `analysis.summary.description` / `analysis.summary.recommendations` using a separate sampling pass over the full report
6. Add `analysis.aiAnalysis.threadNarrative` and populate `analysis.threads.summary.description` with an evidence-backed “what the process was doing” narrative

### Prerequisites

- The connected MCP client must support sampling with tools enabled.
  - The Debugger MCP CLI supports this when an LLM provider is configured.
    - OpenRouter: set `OPENROUTER_API_KEY` (recommended) or `DEBUGGER_MCP_OPENROUTER_API_KEY`.
    - OpenAI: set `OPENAI_API_KEY` (recommended) or `DEBUGGER_MCP_OPENAI_API_KEY`, then `llm provider openai`.
    - Anthropic: set `ANTHROPIC_API_KEY` (recommended) or `DEBUGGER_MCP_ANTHROPIC_API_KEY`, then `llm provider anthropic`.

### Usage

```
analyze(kind: "ai", sessionId: "your-session-id", userId: "your-user-id")
```

### Tool Guidance (for the LLM)

During AI sampling, the model has access to evidence-gathering tools (`report_get`, `exec`, `inspect`, `get_thread_stack`) plus orchestration/meta tools (`analysis_complete`, `checkpoint_complete`, `analysis_evidence_add`, `analysis_hypothesis_register`, `analysis_hypothesis_score`).

When the AI asks for more evidence, prefer:
- `report_get(path: "analysis.exception", select: ["type","message","hResult"])` and `report_get(path: "analysis.threads.faultingThread")` for structured report sections.
- If a section is too large, use the returned `suggestedPaths` and retry with a narrower path. For arrays, page via `limit/cursor` (and reduce payload via `select`). You can also fetch a single element by index (e.g., `analysis.exception.stackTrace[0]`) or request a bounded window via trailing slice syntax (e.g., `analysis.exception.stackTrace[0:10]`). If a slice fails with `invalid_path`, fall back to `limit/cursor`. For objects, page via `pageKind: "object"` + `limit/cursor`.
- For arrays, reduce payload via `select: [...]` and (when applicable) `where: { field: "...", equals: "..." }`.
- If you accidentally request common wrong paths like `analysis.runtime` / `analysis.process` / `analysis.platform` / `analysis.threads.faulting`, the server rewrites them to their canonical equivalents under `analysis.environment.*` / `analysis.threads.faultingThread` (but prefer the canonical paths).
- `inspect(address: "0x...", maxDepth: 3)` for managed object inspection (more complete and safer than `exec "sos dumpobj ..."`).
- `get_thread_stack(threadId: "...")` when you need a full stack for a specific thread already present in the report.
- `exec` only for debugger/SOS commands that don’t have a first-class sampling tool.

#### Stability: checkpoints + evidence/hypotheses

To reduce run-to-run variance and avoid context truncation, the sampling loop maintains:
- An **evidence ledger** (stable IDs like `E12`) **auto-generated from tool outputs** (optionally annotated via `analysis_evidence_add`)
- A set of **competing hypotheses** (stable IDs like `H2`) via `analysis_hypothesis_register` and `analysis_hypothesis_score`
- Periodic **checkpoints** via `checkpoint_complete` (summarize what we know so far and prune the conversation context)

Recommended pattern:
1. Gather baseline evidence first (summary + exception + faulting thread + key assemblies/modules).
2. Call `analysis_hypothesis_register` once with 2–4 competing hypotheses.
3. Optionally call `analysis_evidence_add` to annotate existing evidence items (e.g., tag evidence with `trimming`, add `whyItMatters` for scoring). Do not use it to add new “facts”.
4. On each subsequent iteration: gather *new* evidence (tool calls) and update hypotheses confidence/links (evidence IDs are stable).
5. Use `checkpoint_complete` periodically (default every 4 iterations) so the model carries forward a bounded “state” even if earlier messages are pruned.

The final report includes these under `analysis.aiAnalysis.evidenceLedger` and `analysis.aiAnalysis.hypotheses`, and the AI should reference them in `analysis_complete.evidence` when possible.

### Debugging Sampling Issues

Enable server-side tracing (may include sensitive debugger output):
- `DEBUGGER_MCP_AI_SAMPLING_TRACE=true` (log previews)
- `DEBUGGER_MCP_AI_SAMPLING_TRACE_FILES=true` (write full payloads)
- `DEBUGGER_MCP_AI_SAMPLING_TRACE_MAX_FILE_BYTES=2000000` (per-file cap)
- `DEBUGGER_MCP_AI_SAMPLING_CHECKPOINT_EVERY_ITERATIONS=4` (override checkpoint interval; default is 4)
- `DEBUGGER_MCP_AI_EVIDENCE_PROVENANCE=true` (enable auto evidence provenance and keep `analysis_evidence_add` annotation-only)
- `DEBUGGER_MCP_AI_EVIDENCE_EXCERPT_MAX_CHARS=2048` (max chars stored per auto-generated evidence finding)

Trace files are written under `LOG_STORAGE_PATH/ai-sampling` (in Docker: `/app/logs/ai-sampling`).

If your trace shows errors like “No endpoints found that support the provided `tool_choice` value”, the provider rejected `tool_choice="required"` for tool calls (seen on some OpenRouter models). The server retries with `tool_choice="auto"` and caches that capability for the remainder of the run to avoid repeated failures.

### Optional Environment Variables (Crash Analysis + Source Context)

- `SKIP_HEAP_ENUM=true` (or legacy: `SKIP_SYNC_BLOCKS=true`) skips heap/sync-block enumeration. Use this as a safety valve for cross-architecture/emulation scenarios where heap walks can SIGSEGV.
- `GITHUB_API_ENABLED=false` disables GitHub commit enrichment for assemblies. When enabled, `GITHUB_TOKEN` increases GitHub API rate limits.
- `DEBUGGERMCP_SOURCE_CONTEXT_ROOTS="/repo1;/repo2"` (also supports `:` separators on Linux/macOS) allows the server to resolve `sourceContext` to local files and include code snippets in reports.

---

## 🟣 Managed/.NET Details (`analyze` / kind=`crash`)

### Overview

`analyze(kind: "crash")` performs .NET-specific analysis using the SOS debugging extension. It includes managed exception analysis, heap statistics, and async deadlock detection.

### Prerequisites

1. Session created with `session(action="create")`
2. Dump file opened with `dump(action="open")` (SOS is **auto-loaded** for .NET dumps)

### Usage

```
# SOS is automatically loaded when dump(action="open") detects a .NET dump
analyze(kind: "crash", sessionId: "your-session-id", userId: "your-user-id")
```

> Avoid manually loading SOS during normal flows; `dump(action="open")` auto-loads it for .NET dumps. Only use `inspect(kind: "load_sos", sessionId: "...", userId: "...")` if you have explicit evidence that SOS is not loaded (e.g., SOS commands consistently fail).

### What It Detects

#### CLR Runtime Information
- .NET version
- CLR version
- Runtime type (.NET Framework, .NET Core, .NET 5+)

#### Managed Exceptions
- Exception type (System.NullReferenceException, etc.)
- Exception message
- Inner exception chain
- Managed call stack

#### Heap Statistics
- Generation 0, 1, 2 sizes
- Large Object Heap (LOH) size
- Total managed heap size
- Per-type allocation statistics

#### .NET Memory Leak Detection
- Object count by type
- Types with >10,000 instances flagged
- Duplicate string detection
- Event handler leak patterns

#### Async Deadlock Detection
- SemaphoreSlim/AsyncSemaphore waits
- Task.Wait() and .Result calls
- Async void issues
- Blocking async patterns

#### Finalization Queue
- Pending finalizers count
- Blocked finalizer thread detection

### Example Output

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
    "exception": {
      "type": "System.NullReferenceException",
      "message": "Object reference not set to an instance of an object"
    },
    "environment": {
      "runtime": {
        "clrVersion": "10.0.0"
      }
    },
    "memory": {
      "leakAnalysis": {
        "detected": true
      }
    },
    "assemblies": {
      "count": 45,
      "items": [
        {
          "name": "System.Private.CoreLib",
          "assemblyVersion": "10.0.0.0",
          "fileVersion": "10.0.0.0",
          "path": "/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Private.CoreLib.dll"
        }
      ]
    }
  }
}
```

---

## ⚡ Performance Profiling

### Overview

The performance profiling tools analyze CPU usage, memory allocations, garbage collection behavior, and thread contention to identify performance bottlenecks in your application.

### Comprehensive Performance Analysis (`analyze` / kind=`performance`)

Performs all four analyses (CPU, Allocations, GC, Contention) in one call.

```
analyze(kind: "performance", sessionId: "your-session-id", userId: "your-user-id")
```

#### Example Output

```json
{
  "cpuAnalysis": {
    "totalThreads": 32,
    "activeThreads": 8,
    "hotFunctions": [
      {"module": "MyApp", "function": "ProcessData", "hitCount": 15, "percentage": 46.9}
    ],
    "potentialSpinLoops": [],
    "recommendations": ["Function MyApp!ProcessData appears on 46.9% of stacks. Review for optimization."]
  },
  "allocationAnalysis": {
    "totalHeapSizeBytes": 536870912,
    "totalObjectCount": 250000,
    "topAllocators": [
      {"typeName": "System.String", "count": 100000, "totalSizeBytes": 50000000, "percentageOfHeap": 9.3}
    ],
    "potentialLeaks": [
      {"typeName": "MyApp.CacheEntry", "count": 75000, "totalSizeBytes": 30000000}
    ],
    "recommendations": ["High instance count for MyApp.CacheEntry (75,000 instances). May indicate a memory leak."]
  },
  "gcAnalysis": {
    "gcMode": "Server",
    "concurrentGc": true,
    "gen0SizeBytes": 1048576,
    "gen1SizeBytes": 5242880,
    "gen2SizeBytes": 104857600,
    "lohSizeBytes": 52428800,
    "totalHeapSizeBytes": 163577856,
    "fragmentationPercent": 15.5,
    "finalizerQueueLength": 250,
    "highGcPressure": false,
    "recommendations": ["Large finalizer queue (250 objects). Implement IDisposable and call Dispose()."]
  },
  "contentionAnalysis": {
    "totalLockCount": 5,
    "contentedLockCount": 2,
    "contentedLocks": [
      {"address": "0x12345678", "lockType": "MyApp!DataLock", "waiterCount": 3}
    ],
    "waitingThreads": [
      {"threadId": "4", "waitReason": "Monitor.Enter", "topFunction": "MyApp.DataManager.GetData"}
    ],
    "deadlockDetected": false,
    "highContention": false,
    "recommendations": ["Lock at 0x12345678 (MyApp!DataLock) has 3 waiters."]
  },
  "summary": "CPU: 32 threads, 8 active. Memory: 512.0 MB heap, 250,000 objects. ⚠️ 1 types may be leaking. GC: Server mode, 15.5% fragmentation. Contention: 5 locks, 1 waiting threads.",
  "recommendations": [
    "Function MyApp!ProcessData appears on 46.9% of stacks. Review for optimization.",
    "High instance count for MyApp.CacheEntry (75,000 instances). May indicate a memory leak."
  ]
}
```

---

### CPU Usage Analysis (`analyze` / kind=`cpu`)

Identifies CPU hotspots, runaway threads, and potential spin loops.

```
analyze(kind: "cpu", sessionId: "your-session-id", userId: "your-user-id")
```

#### What It Detects

| Detection | Description |
|-----------|-------------|
| **Hot Functions** | Functions appearing frequently on thread stacks |
| **Thread CPU Time** | User/kernel mode time per thread |
| **Spin Loops** | Functions like SpinWait, SpinLock appearing frequently |
| **Runaway Threads** | Threads consuming excessive CPU time |

#### Example Output

```json
{
  "totalThreads": 16,
  "activeThreads": 4,
  "hotFunctions": [
    {"module": "clr", "function": "JIT_NewFast", "hitCount": 8, "percentage": 50.0},
    {"module": "MyApp", "function": "ParseJson", "hitCount": 6, "percentage": 37.5}
  ],
  "threadCpuUsage": [
    {"threadId": "0", "userTime": 90500, "kernelTime": 30000, "totalTime": 120500}
  ],
  "potentialSpinLoops": [
    "clr!SpinWait (found on 25.0% of stacks)"
  ],
  "recommendations": [
    "Potential spin loops detected: clr!SpinWait. Consider using proper synchronization.",
    "Thread 0 has high CPU time (120500ms). May be a runaway thread."
  ]
}
```

---

### Memory Allocation Analysis (`analyze` / kind=`allocations`)

Analyzes memory allocation patterns to find top allocators and potential leaks.

```
analyze(kind: "allocations", sessionId: "your-session-id", userId: "your-user-id")
```

#### What It Detects

| Detection | Description |
|-----------|-------------|
| **Top Allocators** | Types consuming the most heap memory |
| **Large Objects** | Objects >85KB on Large Object Heap |
| **Potential Leaks** | Types with >10,000 instances |
| **String Stats** | String allocation patterns |
| **Array Stats** | Array allocation patterns |

#### Example Output

```json
{
  "totalHeapSizeBytes": 268435456,
  "totalObjectCount": 150000,
  "topAllocators": [
    {"typeName": "System.Byte[]", "count": 5000, "totalSizeBytes": 100000000, "percentageOfHeap": 37.3},
    {"typeName": "System.String", "count": 75000, "totalSizeBytes": 50000000, "percentageOfHeap": 18.6}
  ],
  "largeObjectAllocations": [
    {"typeName": "System.Byte[]", "count": 100, "totalSizeBytes": 50000000}
  ],
  "potentialLeaks": [
    {"typeName": "System.String", "count": 75000, "totalSizeBytes": 50000000}
  ],
  "stringStats": {
    "count": 75000,
    "totalSizeBytes": 50000000,
    "averageLength": 333.3,
    "excessiveAllocations": true
  },
  "arrayStats": {
    "count": 10000,
    "totalSizeBytes": 120000000,
    "largeArrayCount": 100
  },
  "recommendations": [
    "Excessive string allocations (75,000 strings). Consider using StringBuilder or string pooling.",
    "Large Object Heap has 1 types totaling 50,000,000 bytes. Consider ArrayPool<T> for large arrays.",
    "100 large arrays on LOH. Consider ArrayPool<T> to reduce LOH fragmentation."
  ]
}
```

---

### Garbage Collection Analysis (`analyze` / kind=`gc`)

Analyzes GC behavior, heap generations, and finalization queue.

```
analyze(kind: "gc", sessionId: "your-session-id", userId: "your-user-id")
```

#### What It Detects

| Detection | Description |
|-----------|-------------|
| **GC Mode** | Workstation vs Server GC |
| **Generation Sizes** | Gen0, Gen1, Gen2, LOH, POH sizes |
| **Fragmentation** | Heap fragmentation percentage |
| **Finalizer Queue** | Pending finalizers count |
| **GC Pressure** | High Gen2/LOH ratio indicating pressure |
| **Blocked Finalizer** | Finalizer thread stuck |

#### Example Output

```json
{
  "gcMode": "Server",
  "concurrentGc": true,
  "gen0SizeBytes": 2097152,
  "gen1SizeBytes": 10485760,
  "gen2SizeBytes": 209715200,
  "lohSizeBytes": 104857600,
  "pohSizeBytes": 0,
  "totalHeapSizeBytes": 327155712,
  "fragmentationPercent": 22.5,
  "gcHandleCount": 1500,
  "pinnedObjectCount": 250,
  "finalizerQueueLength": 500,
  "finalizerThreadBlocked": false,
  "highGcPressure": false,
  "recommendations": [
    "Large finalizer queue (500 objects). Implement IDisposable and call Dispose().",
    "High pinned object count (250). Consider reducing pinning or using Pinned Object Heap."
  ]
}
```

---

### Thread Contention Analysis (`analyze` / kind=`contention`)

Analyzes lock usage, waiting threads, and potential deadlocks.

```
analyze(kind: "contention", sessionId: "your-session-id", userId: "your-user-id")
```

#### What It Detects

| Detection | Description |
|-----------|-------------|
| **Contended Locks** | Locks with threads waiting |
| **Waiting Threads** | Threads blocked on Monitor.Enter, WaitOne, etc. |
| **Sync Blocks** | .NET sync block information |
| **Deadlocks** | Circular wait conditions |
| **High Contention** | Many threads waiting on few locks |

#### Example Output

```json
{
  "totalLockCount": 8,
  "contentedLockCount": 3,
  "contentedLocks": [
    {
      "address": "0x12345678",
      "lockType": "MyApp!DataLock",
      "ownerThreadId": "1234",
      "recursionCount": 1,
      "waiterCount": 4
    }
  ],
  "waitingThreads": [
    {"threadId": "5678", "waitReason": "Monitor.Enter", "topFunction": "MyApp.DataManager.GetData"},
    {"threadId": "9abc", "waitReason": "Monitor.Enter", "topFunction": "MyApp.DataManager.SetData"}
  ],
  "syncBlocks": [
    {"index": 1, "objectAddress": "0x12345678", "ownerThreadId": "1234", "objectType": "System.Object"}
  ],
  "deadlockDetected": false,
  "deadlockThreads": [],
  "highContention": true,
  "recommendations": [
    "High lock contention detected. Consider lock-free data structures or finer-grained locking.",
    "Lock at 0x12345678 (MyApp!DataLock) has 4 waiters.",
    "2 threads waiting on Monitor.Enter. Consider using SemaphoreSlim or other async primitives."
  ]
}
```

---

### Performance Analysis Workflow

```
# Full performance profiling workflow
1. session(action="create", userId="user1") → session1
2. dump(action="open", sessionId=session1, userId="user1", dumpId="perf-dump")  # SOS auto-loaded for .NET dumps
3. analyze(kind="performance", sessionId=session1, userId="user1")
4. Review recommendations and drill into specific areas:
   - High CPU? → analyze(kind="cpu") for details
   - Memory issues? → analyze(kind="allocations") for types
   - GC problems? → analyze(kind="gc") for generations
   - Threading issues? → analyze(kind="contention") for locks
```

---

## 🔄 Dump Comparison Features

### Overview

Dump comparison allows you to compare two memory dumps to identify differences in memory usage, thread states, and loaded modules. This is invaluable for:

- **Memory Leak Investigation**: Compare dumps taken over time
- **State Debugging**: Find what changed between working and broken states
- **Regression Analysis**: Compare dumps from different software versions
- **Deadlock Detection**: Identify newly blocked threads

### Prerequisites

For comparison, you need:
1. Two separate sessions (one for each dump)
2. Both dumps open in their respective sessions

### Full Comparison (CompareDumps)

Performs a comprehensive comparison including heap, threads, and modules.

```
# Create two sessions
session(action: "create", userId: "user1") → baselineSessionId
session(action: "create", userId: "user1") → comparisonSessionId

# Open baseline dump (e.g., from yesterday)
dump(action: "open", sessionId: baselineSessionId, userId: "user1", dumpId: "dump-before-leak")

# Open comparison dump (e.g., from today)
dump(action: "open", sessionId: comparisonSessionId, userId: "user1", dumpId: "dump-after-leak")

# Compare
compare(
    kind: "dumps",
    baselineSessionId: "baseline-session",
    baselineUserId: "user1",
    targetSessionId: "comparison-session",
    targetUserId: "user1"
)
```

### Example Full Comparison Output

```json
{
  "baseline": {
    "sessionId": "baseline-session",
    "dumpId": "dump-before-leak",
    "debuggerType": "WinDbg"
  },
  "comparison": {
    "sessionId": "comparison-session",
    "dumpId": "dump-after-leak",
    "debuggerType": "WinDbg"
  },
  "heapComparison": {
    "baselineMemoryBytes": 268435456,
    "comparisonMemoryBytes": 536870912,
    "memoryDeltaBytes": 268435456,
    "memoryGrowthPercent": 100.0,
    "memoryLeakSuspected": true,
    "typeGrowth": [
      {
        "typeName": "System.String",
        "baselineCount": 10000,
        "comparisonCount": 50000,
        "countDelta": 40000,
        "baselineSizeBytes": 500000,
        "comparisonSizeBytes": 2500000,
        "sizeDeltaBytes": 2000000,
        "growthPercent": 400.0
      }
    ],
    "newTypes": [],
    "removedTypes": []
  },
  "threadComparison": {
    "baselineThreadCount": 8,
    "comparisonThreadCount": 12,
    "threadCountDelta": 4,
    "newThreads": [
      {"threadId": "8 (5678)", "state": "Unfrozen", "topFunction": "pthread_mutex_lock"}
    ],
    "terminatedThreads": [],
    "stateChangedThreads": [],
    "threadsWaitingOnLocks": [
      {"threadId": "8 (5678)", "state": "Waiting", "topFunction": "pthread_mutex_lock"}
    ],
    "potentialDeadlock": false
  },
  "moduleComparison": {
    "baselineModuleCount": 45,
    "comparisonModuleCount": 47,
    "moduleCountDelta": 2,
    "newModules": [
      {"name": "newplugin.dll", "baseAddress": "0x7ff812340000", "hasSymbols": false}
    ],
    "unloadedModules": [],
    "versionChanges": [],
    "rebasedModules": []
  },
  "summary": "Memory increased by 268,435,456 bytes (100.0%). ⚠️ MEMORY LEAK SUSPECTED. Thread count increased by 4 (8 → 12). 4 new thread(s) created. 2 module(s) loaded.",
  "recommendations": [
    "Investigate the top growing types for potential memory leaks.",
    "Top growing type: System.String (+40,000 instances, +2,000,000 bytes)"
  ],
  "comparedAt": "2024-01-15T10:30:00Z"
}
```

---

## 📈 Heap Comparison (`compare` / kind=`heaps`)

### Overview

Specifically compares memory/heap allocations to detect memory leaks and growth patterns.

### Usage

```
compare(kind: "heaps",
    baselineSessionId: "baseline-session",
    baselineUserId: "user1",
    targetSessionId: "comparison-session",
    targetUserId: "user1"
)
```

### What It Detects

| Detection | Criteria |
|-----------|----------|
| Memory Leak Suspected | >20% growth OR >100MB increase |
| Type Growth | Per-type instance count and size changes |
| New Types | Types only in comparison dump |
| Removed Types | Types only in baseline dump |

### Key Metrics

- **memoryDeltaBytes**: Total memory change in bytes
- **memoryGrowthPercent**: Percentage growth from baseline
- **typeGrowth**: List of types with changed allocations
- **topGrowingTypes**: Types sorted by size growth

### Interpreting Results

#### Memory Leak Indicators
```json
{
  "memoryLeakSuspected": true,
  "memoryGrowthPercent": 150.0,
  "typeGrowth": [
    {"typeName": "System.String", "growthPercent": 500.0, "countDelta": 100000}
  ]
}
```

**Recommendations**:
- Investigate types with >100% growth
- Check for missing Dispose() calls
- Look for event handler subscriptions
- Review caching implementations

---

## 🧵 Thread Comparison (`compare` / kind=`threads`)

### Overview

Compares thread states to detect new threads, terminated threads, state changes, and potential deadlocks.

### Usage

```
compare(kind: "threads",
    baselineSessionId: "baseline-session",
    baselineUserId: "user1",
    targetSessionId: "comparison-session",
    targetUserId: "user1"
)
```

### What It Detects

| Detection | Description |
|-----------|-------------|
| New Threads | Threads in comparison but not baseline |
| Terminated Threads | Threads in baseline but not comparison |
| State Changes | Threads whose state changed |
| Lock Waiters | Threads waiting on mutex/semaphore |
| Potential Deadlock | Multiple threads waiting on locks |

### Deadlock Detection

The tool flags potential deadlocks when:
1. Multiple threads (≥2) are waiting on locks
2. The number of waiting threads increased from baseline

### Example Output

```json
{
  "baselineThreadCount": 4,
  "comparisonThreadCount": 6,
  "newThreads": [
    {"threadId": "4 (5678)", "state": "Waiting", "topFunction": "mutex_lock"}
  ],
  "threadsWaitingOnLocks": [
    {"threadId": "4 (5678)", "state": "Waiting", "topFunction": "mutex_lock"},
    {"threadId": "5 (9abc)", "state": "Waiting", "topFunction": "mutex_lock"}
  ],
  "potentialDeadlock": true
}
```

---

## 📦 Module Comparison (`compare` / kind=`modules`)

### Overview

Compares loaded modules to track DLL/library loading, version changes, and rebasing.

### Usage

```
compare(kind: "modules",
    baselineSessionId: "baseline-session",
    baselineUserId: "user1",
    targetSessionId: "comparison-session",
    targetUserId: "user1"
)
```

### What It Detects

| Detection | Description |
|-----------|-------------|
| New Modules | Modules loaded in comparison |
| Unloaded Modules | Modules only in baseline |
| Version Changes | Same module, different version |
| Rebased Modules | Same module, different base address |

### Use Cases

- **Plugin Loading**: Track dynamically loaded plugins
- **Version Verification**: Ensure correct DLL versions
- **ASLR Analysis**: Monitor module rebasing
- **Dependency Issues**: Find missing/extra dependencies

---

## 🌐 HTTP API for Comparison

### POST /api/dumps/compare

You can also perform dump comparison via the HTTP API without using MCP tools.

**Request**:
```bash
curl -X POST http://localhost:5000/api/dumps/compare \
  -H "Content-Type: application/json" \
  -H "X-API-Key: your-api-key" \
  -d '{
    "baselineUserId": "user1",
    "baselineDumpId": "dump-before",
    "comparisonUserId": "user1",
    "comparisonDumpId": "dump-after"
  }'
```

**Response**: Same as `compare(kind="dumps")` MCP tool

---

## 📋 Analysis Workflow Examples

### Memory Leak Investigation

```
Day 1: Capture baseline
1. Upload dump-day1 via HTTP API
2. session(action="create", userId="user1") → session1
3. dump(action="open", sessionId=session1, userId="user1", dumpId="dump-day1")
4. analyze(kind="crash", sessionId=session1, userId="user1") → Get baseline heap stats

Day 2: After leak observed
5. Upload dump-day2 via HTTP API
6. session(action="create", userId="user1") → session2  
7. dump(action="open", sessionId=session2, userId="user1", dumpId="dump-day2")
8. compare(kind="dumps", baselineSessionId=session1, baselineUserId="user1", targetSessionId=session2, targetUserId="user1")
9. Review memory growth and top growing types
```

### Deadlock Investigation

```
1. Upload dump with suspected deadlock
2. session(action="create", userId="user1") → session1
3. dump(action="open", sessionId=session1, userId="user1", dumpId="deadlock-dump")
4. analyze(kind="crash", sessionId=session1, userId="user1") → Check analysis.threads.deadlock
5. If deadlock detected, examine:
   - analysis.threads.deadlock.involvedThreads: Which threads are stuck
   - analysis.threads.deadlock.locks: What resources they're waiting for
   - Use exec(sessionId, userId, command="~*k") for full thread stacks
```

### .NET Memory Analysis

```
1. Upload .NET dump
2. session(action="create", userId="user1") → session1
3. dump(action="open", sessionId=session1, userId="user1", dumpId="dotnet-dump")  # SOS auto-loaded
4. analyze(kind="crash", sessionId=session1, userId="user1") → Get crash analysis
5. Review:
   - analysis.memory.gc for GC heap/segment summary (when available)
   - analysis.memory.topConsumers for large managed types (when available)
   - analysis.async.hasDeadlock for async issues (Task/Semaphore waits)
```

### Before/After Comparison

```
# Useful for testing fixes
1. Upload dump-before-fix
2. Upload dump-after-fix
3. session(action="create", userId="user1") → sessionBefore
4. session(action="create", userId="user1") → sessionAfter
5. dump(action="open", sessionId=sessionBefore, userId="user1", dumpId="dump-before-fix")
6. dump(action="open", sessionId=sessionAfter, userId="user1", dumpId="dump-after-fix")
7. compare(kind="dumps", baselineSessionId=sessionBefore, baselineUserId="user1", targetSessionId=sessionAfter, targetUserId="user1")
8. Verify issue is resolved in comparison
```

---

## 📌 Watch Expressions / Bookmarks

### Overview

Watch expressions allow you to bookmark and track specific memory locations, variables, or debugger expressions across debugging sessions. Watches are persisted per-dump and are automatically included in analysis reports.

### Features

| Feature | Description |
|---------|-------------|
| **Persistence** | Watches saved to disk, survive session restarts |
| **Per-Dump Storage** | Each dump has its own watch list |
| **Auto-Detection** | Watch types auto-detected from expression patterns |
| **Analysis Integration** | Watch results included in all analysis reports |
| **Insights** | Automatic detection of problematic values (null, freed memory, etc.) |

### Watch Types

| Type | Description | Example |
|------|-------------|---------|
| `MemoryAddress` | Display memory at an address | `0x12345678` |
| `Variable` | Display a variable or symbol | `g_AppState`, `myModule!myVar` |
| `Object` | Dump a .NET managed object | `0x00007ff812345678` (uses `!do`) |
| `Expression` | Evaluate a debugger expression | `poi(esp+8)`, `@@(myVar.Field)` |

### MCP Tools

Use the `watch` tool with an `action`:

| Tool | Description |
|------|-------------|
| `watch(action="add")` | Add a new watch expression |
| `watch(action="list")` | List all watches for the currently open dump |
| `watch(action="evaluate_all")` | Evaluate all watches with insights |
| `watch(action="evaluate")` | Evaluate a specific watch by ID |
| `watch(action="remove")` | Remove a watch by ID |
| `watch(action="clear")` | Clear all watches for the currently open dump |

### Usage Examples

#### Adding Watches

```
# Track a suspicious memory address
watch(action: "add", sessionId: "session-123", userId: "user1", expression: "0x12345678", description: "Suspicious allocation")

# Track a global variable
watch(action: "add", sessionId: "session-123", userId: "user1", expression: "g_DataManager", description: "Global data manager")

# Track a .NET object
watch(action: "add", sessionId: "session-123", userId: "user1", expression: "0x00007ff812345678", description: "Exception object")

# Track a complex expression
watch(action: "add", sessionId: "session-123", userId: "user1", expression: "poi(esp+8)", description: "Return address")
```

#### Evaluating Watches

```
# Evaluate all watches
watch(action: "evaluate_all", sessionId: "session-123", userId: "user1")
```

**Example Output:**
```json
{
  "dumpId": "crash-dump-001",
  "totalWatches": 3,
  "successfulEvaluations": 2,
  "failedEvaluations": 1,
  "watches": [
    {
      "watchId": "watch-1",
      "expression": "g_DataManager",
      "type": "Variable",
      "success": true,
      "value": "00000000`00000000 (null)"
    },
    {
      "watchId": "watch-2",
      "expression": "0x12345678",
      "type": "MemoryAddress",
      "success": true,
      "value": "48 65 6c 6c 6f 20 57 6f-72 6c 64 00 00 00 00 00  Hello World....."
    }
  ],
  "insights": [
    "⚠️ Watch 'g_DataManager' is NULL - may be relevant to crash"
  ]
}
```

### Integration with Analysis

When you run `analyze(kind="crash")` or `analyze(kind="performance")`, watches are automatically evaluated and included in the report:

```json
{
  "metadata": { "...": "..." },
  "analysis": {
    "exception": { "...": "..." },
    "watches": {
      "totalWatches": 2,
      "watches": [{ "watchId": "...", "expression": "...", "value": "..." }],
      "insights": ["Watch 'g_DataManager' is NULL - may be relevant to crash"]
    },
    "summary": {
      "recommendations": [
        "Watch 'g_DataManager' is NULL - may be relevant to crash",
        "..."
      ]
    }
  }
}
```

### Automatic Insights

The watch evaluator automatically detects problematic patterns:

| Pattern | Insight |
|---------|---------|
| `null` / `00000000` | "Watch is NULL - may be relevant to crash" |
| `0xcdcdcdcd` | "Contains uninitialized memory pattern" |
| `0xdddddddd` | "Contains freed memory pattern" |
| `0xfeeefeee` | "Contains freed heap memory pattern" |
| Exception objects | "Contains an exception object" |
| High failure rate | "High failure rate: symbols may be missing" |

### Best Practices

1. **Add watches early**: When you find interesting addresses during investigation, add them as watches
2. **Use descriptions**: Provide meaningful descriptions to remember why you added each watch
3. **Re-run analysis**: After adding watches, re-run analysis to see watch values in context
4. **Cross-session**: Watches persist, so you can close the session and continue later
5. **Compare dumps**: Same watches can be evaluated on different dumps for comparison

---

## ⚠️ Important Notes

1. **Session Isolation**: Each dump must be in its own session for comparison
2. **Same Platform**: Both dumps should be from the same platform (both Windows OR both macOS/Linux)
3. **Performance**: Large dumps may take time to analyze
4. **Memory**: Comparing very large dumps requires sufficient system memory
5. **Symbols**: Better results with symbols uploaded for both dumps

---

## 🔗 Source Link Integration

### Overview

Source Link integration allows stack frames to link directly to source code in your repository (GitHub, GitLab, Azure DevOps, Bitbucket).

### Prerequisites

- **Portable PDBs**: Symbol files must be Portable PDBs (not Windows native PDBs)
- **Embedded Source Link**: PDBs must have Source Link JSON embedded
- **Symbols Uploaded**: Symbol files should be uploaded via the symbol API

### Available Tools

| Tool | Description | Parameters |
|------|-------------|------------|
| `ResolveSourceLink` | Resolve a source file to a browsable URL | `sessionId`, `userId`, `sourceFile`, `lineNumber` (optional) |
| `GetSourceLinkInfo` | Get Source Link configuration and symbol paths | `sessionId`, `userId` |

### Usage Examples

```
// Resolve a specific source location
ResolveSourceLink(sessionId: "abc", userId: "user1", 
    sourceFile: "Controllers/HomeController.cs", 
    lineNumber: 42)

// Result:
{
  "resolved": true,
  "url": "https://github.com/myorg/myapp/blob/v1.2.3/Controllers/HomeController.cs#L42",
  "provider": "GitHub"
}
```

### Supported Providers

| Provider | URL Pattern |
|----------|-------------|
| **GitHub** | `https://github.com/{user}/{repo}/blob/{commit}/{path}#L{line}` |
| **GitLab** | `https://gitlab.com/{project}/-/blob/{commit}/{path}#L{line}` |
| **Azure DevOps** | `https://dev.azure.com/{org}/{project}/_git/{repo}?path={path}&line={line}` |
| **Bitbucket** | `https://bitbucket.org/{user}/{repo}/src/{commit}/{path}#lines-{line}` |

### Automatic Integration

When Source Link is available:
- **Stack frames** in crash analysis include `sourceUrl` field
- **Markdown reports** show clickable links in the call stack table
- **HTML reports** display source links with 📄 icon

### Example in Reports

**Markdown:**
```markdown
| # | Module | Function | Source |
|---|--------|----------|--------|
| 0 | MyApp | ProcessData | [HomeController.cs:42](https://github.com/...) |
```

**HTML:**
```html
<div class="stack-frame">
  00 MyApp!ProcessData 
  <a href="https://github.com/..." target="_blank">📄 HomeController.cs:42</a>
</div>
```

---

## 🔒 Security Vulnerability Analysis

### Overview

Security analysis scans crash dumps for potential security vulnerabilities, exploitation attempts, and memory protection status.

### Available Tools

| Tool | Description | Parameters |
|------|-------------|------------|
| `analyze(kind="security")` | Comprehensive security vulnerability analysis | `sessionId`, `userId` |
| `analyze(kind="security", action="capabilities")` | List detectable vulnerability types | (no parameters) |

### Detectable Vulnerabilities

| Vulnerability | Detection Method | Severity | CWE |
|--------------|------------------|----------|-----|
| Stack Buffer Overflow | Stack canary corruption | Critical | CWE-121 |
| Heap Buffer Overflow | Heap metadata corruption | Critical | CWE-122 |
| Use-After-Free | Access to freed memory | High | CWE-416 |
| Double-Free | Heap state analysis | High | CWE-415 |
| Null Dereference | Low address access violation | Medium | CWE-476 |
| Heap Corruption | Heap validation commands | High | CWE-122 |
| Stack Corruption | Suspicious stack patterns | High | CWE-121 |
| Code Execution | Execution in stack/heap | Critical | CWE-94 |

### Usage Example

```
// Run comprehensive security analysis
analyze(kind: "security", sessionId: "abc", userId: "user1")

// Output:
{
  "overallRisk": "Critical",
  "summary": "Found 2 vulnerabilities (1 critical, 1 high)",
  "vulnerabilities": [
    {
      "type": "StackBufferOverflow",
      "severity": "Critical",
      "description": "Stack buffer overrun detected (security cookie corruption)",
      "confidence": "Confirmed",
      "cweIds": ["CWE-121", "CWE-787"],
      "indicators": ["Security cookie (/GS) corruption detected"],
      "remediation": [
        "Review all buffer operations in the affected function",
        "Ensure bounds checking on all array/buffer access"
      ]
    }
  ],
  "memoryProtections": {
    "aslrEnabled": true,
    "depEnabled": true,
    "stackCanariesPresent": true,
    "modulesWithoutAslr": []
  },
  "recommendations": [
    "URGENT: Critical security vulnerabilities detected"
  ]
}
```

### Memory Protection Analysis

The analyzer checks for common security mitigations:

| Protection | Description | Platform |
|------------|-------------|----------|
| **ASLR** | Address Space Layout Randomization | Windows, Linux, macOS |
| **DEP/NX** | Data Execution Prevention | Windows, Linux, macOS |
| **Stack Canaries** | /GS (Windows), __stack_chk (Unix) | All |
| **SafeSEH** | Structured Exception Handling | Windows 32-bit |
| **CFG** | Control Flow Guard | Windows |

### Exploit Pattern Detection

Searches memory for common exploitation indicators:

```
Suspicious Patterns:
- 0x41414141, 0x42424242 (Common overflow patterns)
- 0x90909090 (NOP sled)
- 0xDEADBEEF, 0xCAFEBABE (Common markers)
- 0xFEEEFEEE, 0xBAADF00D (Windows heap markers)
```

### Report Integration

Security findings are automatically included in generated reports:

**Markdown:**
```markdown
## 🔒 Security Analysis

**Overall Risk**: 🔴 CRITICAL

| Severity | Type | Description | CWE |
|----------|------|-------------|-----|
| 🔴 Critical | StackBufferOverflow | Stack canary corruption | [CWE-121](https://cwe.mitre.org/...) |
```

**HTML:**
```html
<div class="alert alert-danger">
  🔴 CRITICAL RISK - 2 vulnerabilities detected
</div>
```

### Workflow Integration

```
1. Open dump: dump(action="open", ...)
2. Run crash analysis: analyze(kind="crash", ...)  
3. Check security: analyze(kind="security", ...)
4. Generate report: report(action="full", format="html", ...)
   → Security findings included automatically!
```

---

## 📄 Report Generation

### Overview

Generate comprehensive, shareable reports from your crash analysis in multiple formats.

### Available Tools

| Tool | Description | Parameters |
|------|-------------|------------|
| `report(action="full")` | Generate full analysis report | `sessionId`, `userId`, `format`, `includeWatches`, `includeSecurity`, `maxStackFrames` |
| `report(action="summary")` | Generate brief summary report | `sessionId`, `userId`, `format` |
| `report(action="index")` | Get a small report index (summary + TOC) | `sessionId`, `userId` |
| `report(action="get")` | Fetch a specific report section by path (paged for arrays; objects can be paged via `pageKind="object"`) | `sessionId`, `userId`, `path`, `limit?`, `cursor?`, `pageKind?`, `select?`, `whereField?`, `whereEquals?`, `whereCaseInsensitive?`, `maxChars?` (default: 20000; returns an error if exceeded). `path` supports indices/slices like `analysis.exception.stackTrace[0]` and `analysis.exception.stackTrace[0:10]`. |

### Supported Formats

| Format | Content Type | Use Case |
|--------|-------------|----------|
| `markdown` | text/markdown | Documentation, GitHub, text viewers |
| `html` | text/html | Browser viewing, print to PDF |
| `json` | application/json | Programmatic consumption, APIs |

### Usage Examples

```
// Generate JSON report (default; best for LLMs)
report(action: "full", sessionId: "abc", userId: "user1", format: "json")

// Generate HTML report with custom title
report(action: "full", sessionId: "abc", userId: "user1", format: "html")

// Generate brief summary in JSON
report(action: "summary", sessionId: "abc", userId: "user1", format: "json")

// Get an LLM-friendly index (summary + TOC)
report(action: "index", sessionId: "abc", userId: "user1")

// Fetch a specific report section (paged for arrays)
report(action: "get", sessionId: "abc", userId: "user1", path: "analysis.threads.all", limit: 25)

// Fetch a bounded slice of the exception stack trace (avoid huge responses)
report(action: "get", sessionId: "abc", userId: "user1", path: "analysis.exception.stackTrace[0:10]", select: ["frameNumber","function","module","sourceFile","lineNumber"])

// Filter arrays by exact field match (case-insensitive by default)
report(action: "get", sessionId: "abc", userId: "user1", path: "analysis.assemblies.items", whereField: "name", whereEquals: "System.Private.CoreLib", limit: 5, select: ["name","assemblyVersion","path"])
```

### HTTP Endpoint

Reports can also be downloaded via HTTP:

```http
GET /api/dumps/{userId}/{dumpId}/report?format=html
```

### Report Contents

Reports include (configurable):
- **Executive Summary**: Crash type, timestamp, debugger info
- **Crash Information**: Exception type, message, address
- **Call Stack**: With module and function names
- **Memory Analysis**: Visual charts, top consumers, leak detection
- **Thread Information**: State distribution, thread table
- **.NET Information**: CLR version, managed exceptions
- **Watch Results**: Evaluated expressions with insights
- **Recommendations**: Actionable suggestions

### Visual Charts

**Markdown Reports** use ASCII charts:
```
Memory by Generation:
Gen 0 ████████░░░░░░░░░░░░  40% (10 MB)
Gen 1 ██████░░░░░░░░░░░░░░  30% (7 MB)
Gen 2 ████░░░░░░░░░░░░░░░░  20% (5 MB)
```

**HTML Reports** include styled CSS bar charts that look great in browsers and print well to PDF.

### PDF Generation Tip

For PDF output:
1. Generate HTML report
2. Open in browser
3. Use browser's Print function (File > Print)
4. Select "Save as PDF"

---

## 🆘 Troubleshooting

### "Session does not have a dump file open"
- Ensure `dump(action="open")` was called successfully before analysis

### "SOS extension not loaded"
- SOS is auto-loaded for .NET dumps. Only call `inspect(kind="load_sos")` if you have explicit evidence that SOS is not loaded (e.g., SOS commands consistently fail)
- Check the `dump(action="open")` response to verify .NET detection status

### Memory comparison shows no types
- SOS may not be loaded (for .NET type stats)
- Native heap stats may be limited

### Thread comparison shows mismatched threads
- Thread IDs may change between dumps
- Comparison uses normalized OS thread IDs

---

## 📚 Related Resources

- `debugger://workflow-guide` - Complete workflow guide
- `debugger://windbg-commands` - WinDbg command reference
- `debugger://lldb-commands` - LLDB command reference
- `debugger://sos-commands` - SOS command reference
- `debugger://troubleshooting` - Troubleshooting guide
