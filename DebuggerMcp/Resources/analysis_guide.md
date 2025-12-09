# Debugger MCP Server - Analysis Guide

## 🔍 Complete Guide to Automated Analysis Features

This guide covers all automated analysis features available in the Debugger MCP Server, including crash analysis, .NET-specific analysis, and dump comparison capabilities.

---

## 📊 Analysis Features Overview

| Feature | Tool | Description |
|---------|------|-------------|
| **Crash Analysis** | `analyze_crash` | General crash analysis with memory leak and deadlock detection |
| **.NET Analysis** | `analyze_dot_net_crash` | .NET-specific analysis including managed exceptions, heap stats |
| **Dump Comparison** | `compare_dumps` | Compare two dumps for memory, threads, and modules |
| **Heap Comparison** | `compare_heaps` | Memory allocation comparison for leak detection |
| **Thread Comparison** | `compare_threads` | Thread state comparison for deadlock detection |
| **Module Comparison** | `compare_modules` | Loaded module comparison for version tracking |
| **Performance Analysis** | `analyze_performance` | Comprehensive CPU, memory, GC, and contention analysis |
| **CPU Analysis** | `analyze_cpu_usage` | CPU hotspot identification and runaway thread detection |
| **Allocation Analysis** | `analyze_allocations` | Memory allocation patterns and leak detection |
| **GC Analysis** | `analyze_gc` | Garbage collection behavior and heap fragmentation |
| **Contention Analysis** | `analyze_contention` | Thread contention, lock usage, and deadlock detection |
| **Watch Expressions** | `add_watch`, `evaluate_watches` | Track memory/variables across sessions |
| **Report Generation** | `generate_report` | Generate shareable reports in Markdown, HTML, JSON |
| **Source Link** | `resolve_source_link` | Link stack frames to source code (GitHub, GitLab, etc.) |
| **Security Analysis** | `analyze_security` | Detect security vulnerabilities (buffer overflows, UAF, etc.) |

---

## 🔴 Crash Analysis (analyze_crash)

### Overview

The `analyze_crash` tool performs automated crash analysis on an open dump file. It works with both WinDbg (Windows) and LLDB (macOS/Linux) and returns structured JSON output.

### Prerequisites

1. Session created with `create_session`
2. Dump file opened with `open_dump`

### Usage

```
analyze_crash(sessionId: "your-session-id", userId: "your-user-id")
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
  "crashType": "Access violation",
  "exception": {
    "type": "0xc0000005",
    "message": "Access violation reading location 0x0000000000000000",
    "address": "0x00007ff812345678"
  },
  "callStack": [
    {
      "frameNumber": 0,
      "instructionPointer": "0x00007ff812345678",
      "module": "MyApp",
      "function": "ProcessData",
      "source": "processor.cpp:42"
    }
  ],
  "threads": [
    {
      "threadId": "0 (1234)",
      "state": "Unfrozen",
      "isFaulting": true,
      "topFunction": "ntdll!NtWaitForSingleObject"
    }
  ],
  "memoryLeakInfo": {
    "detected": true,
    "totalHeapBytes": 536870912,
    "topConsumers": [
      {"typeName": "Allocation size 4096", "count": 50000, "totalSize": 204800000}
    ]
  },
  "deadlockInfo": {
    "detected": false,
    "involvedThreads": [],
    "locks": []
  },
  "summary": "Crash Type: Access violation. Found 12 threads, 25 stack frames, 45 modules. MEMORY LEAK DETECTED: ~536,870,912 bytes.",
  "recommendations": [
    "Large heap detected (536870912 bytes). This could indicate a memory leak.",
    "Some modules are missing symbols. Upload symbol files for better analysis."
  ]
}
```

---

## 🟣 .NET Crash Analysis (analyze_dot_net_crash)

### Overview

The `analyze_dot_net_crash` tool provides .NET-specific analysis using SOS debugging extension. It includes managed exception analysis, heap statistics, and async deadlock detection.

### Prerequisites

1. Session created with `create_session`
2. Dump file opened with `open_dump` (SOS is **auto-loaded** for .NET dumps)

### Usage

```
# SOS is automatically loaded when open_dump detects a .NET dump
# Just call analyze directly:
analyze_dot_net_crash(sessionId: "your-session-id", userId: "your-user-id")
```

> **Note**: If auto-detection fails, you can manually call `load_sos` before analysis.

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
  "crashType": "System.NullReferenceException",
  "exception": {
    "type": "System.NullReferenceException",
    "message": "Object reference not set to an instance of an object",
    "address": "0x00007ff812345678"
  },
  "dotNetInfo": {
    "clrVersion": "6.0.25",
    "managedException": "System.NullReferenceException: Object reference not set to an instance of an object\n   at MyApp.Processor.Process() in processor.cs:line 42",
    "heapStats": {
      "gen0": 1048576,
      "gen1": 5242880,
      "gen2": 52428800,
      "loh": 10485760
    },
    "asyncDeadlock": false,
    "finalizerQueueCount": 125
  },
  "memoryLeakInfo": {
    "detected": true,
    "topConsumers": [
      {"typeName": "System.String", "count": 150000, "totalSize": 45000000},
      {"typeName": "System.Byte[]", "count": 50000, "totalSize": 25000000}
    ]
  },
  "summary": ".NET Crash Analysis: System.NullReferenceException. CLR: 6.0.25. MEMORY LEAK: 150000 String instances may indicate a leak."
}
```

---

## ⚡ Performance Profiling

### Overview

The performance profiling tools analyze CPU usage, memory allocations, garbage collection behavior, and thread contention to identify performance bottlenecks in your application.

### Comprehensive Performance Analysis (analyze_performance)

Performs all four analyses (CPU, Allocations, GC, Contention) in one call.

```
analyze_performance(sessionId: "your-session-id", userId: "your-user-id")
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

### CPU Usage Analysis (analyze_cpu_usage)

Identifies CPU hotspots, runaway threads, and potential spin loops.

```
analyze_cpu_usage(sessionId: "your-session-id", userId: "your-user-id")
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

### Memory Allocation Analysis (analyze_allocations)

Analyzes memory allocation patterns to find top allocators and potential leaks.

```
analyze_allocations(sessionId: "your-session-id", userId: "your-user-id")
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

### Garbage Collection Analysis (analyze_gc)

Analyzes GC behavior, heap generations, and finalization queue.

```
analyze_gc(sessionId: "your-session-id", userId: "your-user-id")
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

### Thread Contention Analysis (analyze_contention)

Analyzes lock usage, waiting threads, and potential deadlocks.

```
analyze_contention(sessionId: "your-session-id", userId: "your-user-id")
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
1. create_session → session1
2. open_dump(session1, "user1", "perf-dump")  # SOS auto-loaded for .NET dumps
3. analyze_performance(session1, "user1")
4. Review recommendations and drill into specific areas:
   - High CPU? → analyze_cpu_usage for details
   - Memory issues? → analyze_allocations for types
   - GC problems? → analyze_gc for generations
   - Threading issues? → analyze_contention for locks
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
create_session(userId: "user1") → baselineSessionId
create_session(userId: "user1") → comparisonSessionId

# Open baseline dump (e.g., from yesterday)
OpenDump(baselineSessionId, "user1", "dump-before-leak")

# Open comparison dump (e.g., from today)
OpenDump(comparisonSessionId, "user1", "dump-after-leak")

# Compare
CompareDumps(
    baselineSessionId: "baseline-session",
    baselineUserId: "user1",
    comparisonSessionId: "comparison-session",
    comparisonUserId: "user1"
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

## 📈 Heap Comparison (compare_heaps)

### Overview

Specifically compares memory/heap allocations to detect memory leaks and growth patterns.

### Usage

```
compare_heaps(
    baselineSessionId: "baseline-session",
    baselineUserId: "user1",
    comparisonSessionId: "comparison-session",
    comparisonUserId: "user1"
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

## 🧵 Thread Comparison (compare_threads)

### Overview

Compares thread states to detect new threads, terminated threads, state changes, and potential deadlocks.

### Usage

```
compare_threads(
    baselineSessionId: "baseline-session",
    baselineUserId: "user1",
    comparisonSessionId: "comparison-session",
    comparisonUserId: "user1"
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

## 📦 Module Comparison (compare_modules)

### Overview

Compares loaded modules to track DLL/library loading, version changes, and rebasing.

### Usage

```
compare_modules(
    baselineSessionId: "baseline-session",
    baselineUserId: "user1",
    comparisonSessionId: "comparison-session",
    comparisonUserId: "user1"
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

**Response**: Same as `CompareDumps` MCP tool

---

## 📋 Analysis Workflow Examples

### Memory Leak Investigation

```
Day 1: Capture baseline
1. Upload dump-day1 via HTTP API
2. create_session → session1
3. open_dump(session1, "user1", "dump-day1")
4. analyze_crash → Get baseline heap stats

Day 2: After leak observed
5. Upload dump-day2 via HTTP API
6. create_session → session2  
7. open_dump(session2, "user1", "dump-day2")
8. compare_dumps(session1, "user1", session2, "user1")
9. Review memory growth and top growing types
```

### Deadlock Investigation

```
1. Upload dump with suspected deadlock
2. create_session → session1
3. open_dump(session1, "user1", "deadlock-dump")
4. analyze_crash → Check deadlockInfo
5. If deadlock detected, examine:
   - involvedThreads: Which threads are stuck
   - locks: What resources they're waiting for
   - Use execute_command("~*k") for full thread stacks
```

### .NET Memory Analysis

```
1. Upload .NET dump
2. create_session → session1
3. open_dump(session1, "user1", "dotnet-dump")  # SOS auto-loaded
4. analyze_dot_net_crash → Get .NET-specific analysis
5. Review:
   - heapStats for GC generations
   - topConsumers for memory hogs
   - asyncDeadlock for async issues
```

### Before/After Comparison

```
# Useful for testing fixes
1. Upload dump-before-fix
2. Upload dump-after-fix
3. create_session → sessionBefore
4. create_session → sessionAfter
5. open_dump(sessionBefore, "user1", "dump-before-fix")
6. open_dump(sessionAfter, "user1", "dump-after-fix")
7. compare_dumps(sessionBefore, "user1", sessionAfter, "user1")
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

| Tool | Description |
|------|-------------|
| `add_watch` | Add a new watch expression |
| `list_watches` | List all watches for current dump |
| `evaluate_watches` | Evaluate all watches with insights |
| `evaluate_watch` | Evaluate a specific watch by ID |
| `remove_watch` | Remove a watch by ID |
| `clear_watches` | Clear all watches for current dump |

### Usage Examples

#### Adding Watches

```
# Track a suspicious memory address
AddWatch(sessionId, userId, "0x12345678", "Suspicious allocation")

# Track a global variable
AddWatch(sessionId, userId, "g_DataManager", "Global data manager")

# Track a .NET object
AddWatch(sessionId, userId, "0x00007ff812345678", "Exception object", "Object")

# Track a complex expression
AddWatch(sessionId, userId, "poi(esp+8)", "Return address")
```

#### Evaluating Watches

```
# Evaluate all watches
EvaluateWatches(sessionId, userId)
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

When you run `analyze_crash`, `analyze_dot_net_crash`, or `analyze_performance`, watches are automatically evaluated and included in the report:

```json
{
  "crashType": "Access violation",
  "exception": {...},
  "watchResults": {
    "totalWatches": 2,
    "watches": [...],
    "insights": ["⚠️ Watch 'g_DataManager' is NULL - may be relevant to crash"]
  },
  "recommendations": [
    "⚠️ Watch 'g_DataManager' is NULL - may be relevant to crash",
    "..."
  ]
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
| `analyze_security` | Comprehensive security vulnerability analysis | `sessionId`, `userId` |
| `get_security_check_capabilities` | List detectable vulnerability types | (no parameters) |

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
analyze_security(sessionId: "abc", userId: "user1")

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
1. Open dump: open_dump(...)
2. Run crash analysis: analyze_crash(...)  
3. Check security: analyze_security(...)
4. Generate report: generate_report(format: "html")
   → Security findings included automatically!
```

---

## 📄 Report Generation

### Overview

Generate comprehensive, shareable reports from your crash analysis in multiple formats.

### Available Tools

| Tool | Description | Parameters |
|------|-------------|------------|
| `generate_report` | Generate full analysis report | `sessionId`, `userId`, `format`, `includeRawOutput`, `title` |
| `generate_summary_report` | Generate brief summary report | `sessionId`, `userId`, `format` |

### Supported Formats

| Format | Content Type | Use Case |
|--------|-------------|----------|
| `markdown` | text/markdown | Documentation, GitHub, text viewers |
| `html` | text/html | Browser viewing, print to PDF |
| `json` | application/json | Programmatic consumption, APIs |

### Usage Examples

```
// Generate Markdown report (default)
generate_report(sessionId: "abc", userId: "user1")

// Generate HTML report with custom title
generate_report(sessionId: "abc", userId: "user1", format: "html", title: "Production Crash Analysis")

// Generate brief summary in JSON
generate_summary_report(sessionId: "abc", userId: "user1", format: "json")
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
- Ensure `open_dump` was called successfully before analysis

### "SOS extension not loaded"
- SOS is auto-loaded for .NET dumps. If detection failed, call `load_sos` manually
- Check `open_dump` response to verify .NET detection status

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
