using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using DebuggerMcp.Watches;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Represents the complete result of a performance analysis.
/// </summary>
public class PerformanceAnalysisResult
{
    /// <summary>
    /// Gets or sets the CPU analysis results.
    /// </summary>
    [JsonPropertyName("cpuAnalysis")]
    public CpuAnalysisResult? CpuAnalysis { get; set; }

    /// <summary>
    /// Gets or sets the memory allocation analysis results.
    /// </summary>
    [JsonPropertyName("allocationAnalysis")]
    public AllocationAnalysisResult? AllocationAnalysis { get; set; }

    /// <summary>
    /// Gets or sets the garbage collection analysis results.
    /// </summary>
    [JsonPropertyName("gcAnalysis")]
    public GcAnalysisResult? GcAnalysis { get; set; }

    /// <summary>
    /// Gets or sets the thread contention analysis results.
    /// </summary>
    [JsonPropertyName("contentionAnalysis")]
    public ContentionAnalysisResult? ContentionAnalysis { get; set; }

    /// <summary>
    /// Gets or sets watch expression evaluation results.
    /// Contains values of tracked memory addresses, variables, and expressions.
    /// </summary>
    [JsonPropertyName("watchResults")]
    public WatchEvaluationReport? WatchResults { get; set; }

    /// <summary>
    /// Gets or sets the overall performance summary.
    /// </summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the overall recommendations.
    /// </summary>
    [JsonPropertyName("recommendations")]
    public List<string> Recommendations { get; set; } = new();

    /// <summary>
    /// Gets or sets the analysis timestamp.
    /// </summary>
    [JsonPropertyName("analyzedAt")]
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the debugger type used for analysis.
    /// </summary>
    [JsonPropertyName("debuggerType")]
    public string DebuggerType { get; set; } = string.Empty;
}

/// <summary>
/// Results from CPU usage analysis.
/// </summary>
public class CpuAnalysisResult
{
    /// <summary>
    /// Gets or sets the total thread count.
    /// </summary>
    [JsonPropertyName("totalThreads")]
    public int TotalThreads { get; set; }

    /// <summary>
    /// Gets or sets the count of threads actively running (not waiting).
    /// </summary>
    [JsonPropertyName("activeThreads")]
    public int ActiveThreads { get; set; }

    /// <summary>
    /// Gets or sets functions identified as CPU hotspots.
    /// </summary>
    [JsonPropertyName("hotFunctions")]
    public List<HotFunction> HotFunctions { get; set; } = new();

    /// <summary>
    /// Gets or sets CPU usage per thread.
    /// </summary>
    [JsonPropertyName("threadCpuUsage")]
    public List<ThreadCpuInfo> ThreadCpuUsage { get; set; } = new();

    /// <summary>
    /// Gets or sets threads that appear to be in tight loops.
    /// </summary>
    [JsonPropertyName("potentialSpinLoops")]
    public List<string> PotentialSpinLoops { get; set; } = new();

    /// <summary>
    /// Gets or sets recommendations based on CPU analysis.
    /// </summary>
    [JsonPropertyName("recommendations")]
    public List<string> Recommendations { get; set; } = new();

    /// <summary>
    /// Gets or sets the raw output from CPU analysis commands.
    /// </summary>
    [JsonPropertyName("rawOutput")]
    public string? RawOutput { get; set; }
}

/// <summary>
/// Represents a function identified as a CPU hotspot.
/// </summary>
public class HotFunction
{
    /// <summary>
    /// Gets or sets the module name.
    /// </summary>
    [JsonPropertyName("module")]
    public string Module { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the function name.
    /// </summary>
    [JsonPropertyName("function")]
    public string Function { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of times this function appears on thread stacks.
    /// </summary>
    [JsonPropertyName("hitCount")]
    public int HitCount { get; set; }

    /// <summary>
    /// Gets or sets the percentage of threads with this function on stack.
    /// </summary>
    [JsonPropertyName("percentage")]
    public double Percentage { get; set; }

    /// <summary>
    /// Gets or sets the instruction pointer address.
    /// </summary>
    [JsonPropertyName("address")]
    public string? Address { get; set; }
}

/// <summary>
/// CPU usage information for a specific thread.
/// </summary>
public class ThreadCpuInfo
{
    /// <summary>
    /// Gets or sets the thread ID.
    /// </summary>
    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the thread state.
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets user mode time (in milliseconds or ticks).
    /// </summary>
    [JsonPropertyName("userTime")]
    public long UserTime { get; set; }

    /// <summary>
    /// Gets or sets kernel mode time (in milliseconds or ticks).
    /// </summary>
    [JsonPropertyName("kernelTime")]
    public long KernelTime { get; set; }

    /// <summary>
    /// Gets or sets total CPU time.
    /// </summary>
    [JsonPropertyName("totalTime")]
    public long TotalTime { get; set; }

    /// <summary>
    /// Gets or sets the top function on the thread's stack.
    /// </summary>
    [JsonPropertyName("topFunction")]
    public string TopFunction { get; set; } = string.Empty;
}

/// <summary>
/// Results from memory allocation analysis.
/// </summary>
public class AllocationAnalysisResult
{
    /// <summary>
    /// Gets or sets the total heap size in bytes.
    /// </summary>
    [JsonPropertyName("totalHeapSizeBytes")]
    public long TotalHeapSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the total object count on the heap.
    /// </summary>
    [JsonPropertyName("totalObjectCount")]
    public long TotalObjectCount { get; set; }

    /// <summary>
    /// Gets or sets the top allocating types.
    /// </summary>
    [JsonPropertyName("topAllocators")]
    public List<AllocationInfo> TopAllocators { get; set; } = new();

    /// <summary>
    /// Gets or sets large object allocations (>85KB).
    /// </summary>
    [JsonPropertyName("largeObjectAllocations")]
    public List<AllocationInfo> LargeObjectAllocations { get; set; } = new();

    /// <summary>
    /// Gets or sets types with high instance counts that may indicate a leak.
    /// </summary>
    [JsonPropertyName("potentialLeaks")]
    public List<AllocationInfo> PotentialLeaks { get; set; } = new();

    /// <summary>
    /// Gets or sets string allocation statistics.
    /// </summary>
    [JsonPropertyName("stringStats")]
    public StringAllocationStats? StringStats { get; set; }

    /// <summary>
    /// Gets or sets array allocation statistics.
    /// </summary>
    [JsonPropertyName("arrayStats")]
    public ArrayAllocationStats? ArrayStats { get; set; }

    /// <summary>
    /// Gets or sets recommendations based on allocation analysis.
    /// </summary>
    [JsonPropertyName("recommendations")]
    public List<string> Recommendations { get; set; } = new();

    /// <summary>
    /// Gets or sets the raw output from allocation analysis commands.
    /// </summary>
    [JsonPropertyName("rawOutput")]
    public string? RawOutput { get; set; }
}

/// <summary>
/// Information about allocations for a specific type.
/// </summary>
public class AllocationInfo
{
    /// <summary>
    /// Gets or sets the type name.
    /// </summary>
    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the method table address (MT).
    /// </summary>
    [JsonPropertyName("methodTable")]
    public string? MethodTable { get; set; }

    /// <summary>
    /// Gets or sets the instance count.
    /// </summary>
    [JsonPropertyName("count")]
    public long Count { get; set; }

    /// <summary>
    /// Gets or sets the total size in bytes.
    /// </summary>
    [JsonPropertyName("totalSizeBytes")]
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the average instance size in bytes.
    /// </summary>
    [JsonPropertyName("averageSizeBytes")]
    public long AverageSizeBytes => Count > 0 ? TotalSizeBytes / Count : 0;

    /// <summary>
    /// Gets or sets the percentage of total heap.
    /// </summary>
    [JsonPropertyName("percentageOfHeap")]
    public double PercentageOfHeap { get; set; }
}

/// <summary>
/// Statistics about string allocations.
/// </summary>
public class StringAllocationStats
{
    /// <summary>
    /// Gets or sets the total string count.
    /// </summary>
    [JsonPropertyName("count")]
    public long Count { get; set; }

    /// <summary>
    /// Gets or sets the total string bytes.
    /// </summary>
    [JsonPropertyName("totalSizeBytes")]
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the average string length.
    /// </summary>
    [JsonPropertyName("averageLength")]
    public double AverageLength { get; set; }

    /// <summary>
    /// Gets or sets whether excessive string allocations were detected.
    /// </summary>
    [JsonPropertyName("excessiveAllocations")]
    public bool ExcessiveAllocations { get; set; }
}

/// <summary>
/// Statistics about array allocations.
/// </summary>
public class ArrayAllocationStats
{
    /// <summary>
    /// Gets or sets the total array count.
    /// </summary>
    [JsonPropertyName("count")]
    public long Count { get; set; }

    /// <summary>
    /// Gets or sets the total array bytes.
    /// </summary>
    [JsonPropertyName("totalSizeBytes")]
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets large arrays (>85KB each).
    /// </summary>
    [JsonPropertyName("largeArrayCount")]
    public int LargeArrayCount { get; set; }

    /// <summary>
    /// Gets or sets array types and counts.
    /// </summary>
    [JsonPropertyName("arrayTypes")]
    public Dictionary<string, long> ArrayTypes { get; set; } = new();
}

/// <summary>
/// Results from garbage collection analysis.
/// </summary>
public class GcAnalysisResult
{
    /// <summary>
    /// Gets or sets the GC mode (Workstation/Server).
    /// </summary>
    [JsonPropertyName("gcMode")]
    public string GcMode { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether concurrent GC is enabled.
    /// </summary>
    [JsonPropertyName("concurrentGc")]
    public bool ConcurrentGc { get; set; }

    /// <summary>
    /// Gets or sets Generation 0 heap size in bytes.
    /// </summary>
    [JsonPropertyName("gen0SizeBytes")]
    public long Gen0SizeBytes { get; set; }

    /// <summary>
    /// Gets or sets Generation 1 heap size in bytes.
    /// </summary>
    [JsonPropertyName("gen1SizeBytes")]
    public long Gen1SizeBytes { get; set; }

    /// <summary>
    /// Gets or sets Generation 2 heap size in bytes.
    /// </summary>
    [JsonPropertyName("gen2SizeBytes")]
    public long Gen2SizeBytes { get; set; }

    /// <summary>
    /// Gets or sets Large Object Heap (LOH) size in bytes.
    /// </summary>
    [JsonPropertyName("lohSizeBytes")]
    public long LohSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets Pinned Object Heap (POH) size in bytes (.NET 5+).
    /// </summary>
    [JsonPropertyName("pohSizeBytes")]
    public long PohSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the total heap size in bytes.
    /// </summary>
    [JsonPropertyName("totalHeapSizeBytes")]
    public long TotalHeapSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the heap fragmentation percentage.
    /// </summary>
    [JsonPropertyName("fragmentationPercent")]
    public double FragmentationPercent { get; set; }

    /// <summary>
    /// Gets or sets the number of GC handles.
    /// </summary>
    [JsonPropertyName("gcHandleCount")]
    public int GcHandleCount { get; set; }

    /// <summary>
    /// Gets or sets pinned objects count.
    /// </summary>
    [JsonPropertyName("pinnedObjectCount")]
    public int PinnedObjectCount { get; set; }

    /// <summary>
    /// Gets or sets the finalizer queue length.
    /// </summary>
    [JsonPropertyName("finalizerQueueLength")]
    public int FinalizerQueueLength { get; set; }

    /// <summary>
    /// Gets or sets whether the finalizer thread appears blocked.
    /// </summary>
    [JsonPropertyName("finalizerThreadBlocked")]
    public bool FinalizerThreadBlocked { get; set; }

    /// <summary>
    /// Gets or sets whether high GC pressure was detected.
    /// </summary>
    [JsonPropertyName("highGcPressure")]
    public bool HighGcPressure { get; set; }

    /// <summary>
    /// Gets or sets recommendations based on GC analysis.
    /// </summary>
    [JsonPropertyName("recommendations")]
    public List<string> Recommendations { get; set; } = new();

    /// <summary>
    /// Gets or sets the raw output from GC analysis commands.
    /// </summary>
    [JsonPropertyName("rawOutput")]
    public string? RawOutput { get; set; }
}

/// <summary>
/// Results from thread contention analysis.
/// </summary>
public class ContentionAnalysisResult
{
    /// <summary>
    /// Gets or sets the total lock count.
    /// </summary>
    [JsonPropertyName("totalLockCount")]
    public int TotalLockCount { get; set; }

    /// <summary>
    /// Gets or sets the contended lock count.
    /// </summary>
    [JsonPropertyName("contentedLockCount")]
    public int ContentedLockCount { get; set; }

    /// <summary>
    /// Gets or sets detailed information about contended locks.
    /// </summary>
    [JsonPropertyName("contentedLocks")]
    public List<ContentedLock> ContentedLocks { get; set; } = new();

    /// <summary>
    /// Gets or sets threads currently waiting on locks.
    /// </summary>
    [JsonPropertyName("waitingThreads")]
    public List<WaitingThread> WaitingThreads { get; set; } = new();

    /// <summary>
    /// Gets or sets sync block information.
    /// </summary>
    [JsonPropertyName("syncBlocks")]
    public List<SyncBlockInfo> SyncBlocks { get; set; } = new();

    /// <summary>
    /// Gets or sets whether a deadlock was detected.
    /// </summary>
    [JsonPropertyName("deadlockDetected")]
    public bool DeadlockDetected { get; set; }

    /// <summary>
    /// Gets or sets threads involved in deadlock if detected.
    /// </summary>
    [JsonPropertyName("deadlockThreads")]
    public List<string> DeadlockThreads { get; set; } = new();

    /// <summary>
    /// Gets or sets whether high contention was detected.
    /// </summary>
    [JsonPropertyName("highContention")]
    public bool HighContention { get; set; }

    /// <summary>
    /// Gets or sets recommendations based on contention analysis.
    /// </summary>
    [JsonPropertyName("recommendations")]
    public List<string> Recommendations { get; set; } = new();

    /// <summary>
    /// Gets or sets the raw output from contention analysis commands.
    /// </summary>
    [JsonPropertyName("rawOutput")]
    public string? RawOutput { get; set; }
}

/// <summary>
/// Information about a contended lock.
/// </summary>
public class ContentedLock
{
    /// <summary>
    /// Gets or sets the lock address.
    /// </summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the lock type/name.
    /// </summary>
    [JsonPropertyName("lockType")]
    public string LockType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the owning thread ID.
    /// </summary>
    [JsonPropertyName("ownerThreadId")]
    public string OwnerThreadId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the recursion count.
    /// </summary>
    [JsonPropertyName("recursionCount")]
    public int RecursionCount { get; set; }

    /// <summary>
    /// Gets or sets the number of threads waiting on this lock.
    /// </summary>
    [JsonPropertyName("waiterCount")]
    public int WaiterCount { get; set; }

    /// <summary>
    /// Gets or sets the thread IDs waiting on this lock.
    /// </summary>
    [JsonPropertyName("waitingThreadIds")]
    public List<string> WaitingThreadIds { get; set; } = new();
}

/// <summary>
/// Information about a thread waiting on a lock.
/// </summary>
public class WaitingThread
{
    /// <summary>
    /// Gets or sets the thread ID.
    /// </summary>
    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the wait reason.
    /// </summary>
    [JsonPropertyName("waitReason")]
    public string WaitReason { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the lock address being waited on.
    /// </summary>
    [JsonPropertyName("lockAddress")]
    public string? LockAddress { get; set; }

    /// <summary>
    /// Gets or sets the wait time indication (if available).
    /// </summary>
    [JsonPropertyName("waitTime")]
    public string? WaitTime { get; set; }

    /// <summary>
    /// Gets or sets the top function in the wait stack.
    /// </summary>
    [JsonPropertyName("topFunction")]
    public string TopFunction { get; set; } = string.Empty;
}

/// <summary>
/// Sync block information for managed locks.
/// </summary>
public class SyncBlockInfo
{
    /// <summary>
    /// Gets or sets the sync block index.
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>
    /// Gets or sets the object address.
    /// </summary>
    [JsonPropertyName("objectAddress")]
    public string ObjectAddress { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the owning thread ID.
    /// </summary>
    [JsonPropertyName("ownerThreadId")]
    public string? OwnerThreadId { get; set; }

    /// <summary>
    /// Gets or sets the object type.
    /// </summary>
    [JsonPropertyName("objectType")]
    public string? ObjectType { get; set; }

    /// <summary>
    /// Gets or sets whether this is a COM+ context lock.
    /// </summary>
    [JsonPropertyName("isComPlus")]
    public bool IsComPlus { get; set; }
}

