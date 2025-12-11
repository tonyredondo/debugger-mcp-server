using System.Text.Json.Serialization;

namespace DebuggerMcp.Analysis.Synchronization;

/// <summary>
/// Complete result of synchronization primitives analysis.
/// </summary>
public class SynchronizationAnalysisResult
{
    /// <summary>
    /// Summary of synchronization analysis.
    /// </summary>
    [JsonPropertyName("summary")]
    public SynchronizationSummary Summary { get; set; } = new();

    /// <summary>
    /// Monitor locks (C# lock statements) with active ownership.
    /// </summary>
    [JsonPropertyName("monitorLocks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MonitorLockInfo>? MonitorLocks { get; set; }

    /// <summary>
    /// SemaphoreSlim instances found in the heap.
    /// </summary>
    [JsonPropertyName("semaphoreSlims")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SemaphoreSlimInfo>? SemaphoreSlims { get; set; }

    /// <summary>
    /// ReaderWriterLockSlim instances found in the heap.
    /// </summary>
    [JsonPropertyName("readerWriterLocks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ReaderWriterLockInfo>? ReaderWriterLocks { get; set; }

    /// <summary>
    /// ManualResetEventSlim and AutoResetEvent instances.
    /// </summary>
    [JsonPropertyName("resetEvents")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResetEventInfo>? ResetEvents { get; set; }

    /// <summary>
    /// WaitHandle-based primitives (Mutex, Semaphore, EventWaitHandle).
    /// </summary>
    [JsonPropertyName("waitHandles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<WaitHandleInfo>? WaitHandles { get; set; }

    /// <summary>
    /// Wait graph showing thread-resource dependencies.
    /// </summary>
    [JsonPropertyName("waitGraph")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WaitGraph? WaitGraph { get; set; }

    /// <summary>
    /// Detected potential deadlock cycles.
    /// </summary>
    [JsonPropertyName("potentialDeadlocks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DeadlockCycle>? PotentialDeadlocks { get; set; }

    /// <summary>
    /// Lock contention hotspots.
    /// </summary>
    [JsonPropertyName("contentionHotspots")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ContentionHotspot>? ContentionHotspots { get; set; }
}

/// <summary>
/// Summary statistics for synchronization analysis.
/// </summary>
public class SynchronizationSummary
{
    /// <summary>
    /// Total number of active monitor locks.
    /// </summary>
    [JsonPropertyName("totalMonitorLocks")]
    public int TotalMonitorLocks { get; set; }

    /// <summary>
    /// Total SemaphoreSlim instances found.
    /// </summary>
    [JsonPropertyName("totalSemaphoreSlims")]
    public int TotalSemaphoreSlims { get; set; }

    /// <summary>
    /// Total ReaderWriterLockSlim instances found.
    /// </summary>
    [JsonPropertyName("totalReaderWriterLocks")]
    public int TotalReaderWriterLocks { get; set; }

    /// <summary>
    /// Total reset event instances found.
    /// </summary>
    [JsonPropertyName("totalResetEvents")]
    public int TotalResetEvents { get; set; }

    /// <summary>
    /// Total WaitHandle-based primitives found.
    /// </summary>
    [JsonPropertyName("totalWaitHandles")]
    public int TotalWaitHandles { get; set; }

    /// <summary>
    /// Whether any contention was detected.
    /// </summary>
    [JsonPropertyName("contentionDetected")]
    public bool ContentionDetected { get; set; }

    /// <summary>
    /// Number of potential deadlocks detected.
    /// </summary>
    [JsonPropertyName("potentialDeadlockCount")]
    public int PotentialDeadlockCount { get; set; }

    /// <summary>
    /// Number of contended SemaphoreSlim instances.
    /// </summary>
    [JsonPropertyName("contendedSemaphoreSlims")]
    public int ContendedSemaphoreSlims { get; set; }

    /// <summary>
    /// Number of SemaphoreSlim instances used as AsyncLock (maxCount=1).
    /// </summary>
    [JsonPropertyName("asyncLockCount")]
    public int AsyncLockCount { get; set; }
}

/// <summary>
/// Information about a CLR Monitor lock (sync block).
/// </summary>
public class MonitorLockInfo
{
    /// <summary>
    /// Address of the locked object.
    /// </summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Type of the locked object.
    /// </summary>
    [JsonPropertyName("objectType")]
    public string ObjectType { get; set; } = string.Empty;

    /// <summary>
    /// Thread ID that owns the lock.
    /// </summary>
    [JsonPropertyName("ownerThreadId")]
    public int OwnerThreadId { get; set; }

    /// <summary>
    /// OS thread ID of the owner.
    /// </summary>
    [JsonPropertyName("ownerOsThreadId")]
    public uint OwnerOsThreadId { get; set; }

    /// <summary>
    /// Lock recursion count.
    /// </summary>
    [JsonPropertyName("recursionCount")]
    public int RecursionCount { get; set; }

    /// <summary>
    /// Thread IDs waiting on this lock.
    /// </summary>
    [JsonPropertyName("waitingThreads")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? WaitingThreads { get; set; }

    /// <summary>
    /// Whether there are threads waiting on this lock.
    /// </summary>
    [JsonPropertyName("isContended")]
    public bool IsContended => WaitingThreads?.Count > 0;
}

/// <summary>
/// Information about a SemaphoreSlim instance.
/// </summary>
public class SemaphoreSlimInfo
{
    /// <summary>
    /// Address of the SemaphoreSlim object.
    /// </summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// List of objects/types that reference this SemaphoreSlim.
    /// </summary>
    [JsonPropertyName("owners")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SyncPrimitiveOwner>? Owners { get; set; }

    /// <summary>
    /// Current available count.
    /// </summary>
    [JsonPropertyName("currentCount")]
    public int CurrentCount { get; set; }

    /// <summary>
    /// Maximum count.
    /// </summary>
    [JsonPropertyName("maxCount")]
    public int MaxCount { get; set; }

    /// <summary>
    /// Number of synchronous waiters (threads blocked on Wait).
    /// </summary>
    [JsonPropertyName("syncWaiters")]
    public int SyncWaiters { get; set; }

    /// <summary>
    /// Number of asynchronous waiters (from WaitAsync).
    /// </summary>
    [JsonPropertyName("asyncWaiters")]
    public int AsyncWaiters { get; set; }

    /// <summary>
    /// Whether this semaphore is being used as an async lock (maxCount == 1).
    /// </summary>
    [JsonPropertyName("isAsyncLock")]
    public bool IsAsyncLock => MaxCount == 1;

    /// <summary>
    /// Whether there is contention on this semaphore.
    /// </summary>
    [JsonPropertyName("isContended")]
    public bool IsContended => CurrentCount == 0 && (SyncWaiters > 0 || AsyncWaiters > 0);

    /// <summary>
    /// Detailed waiter information.
    /// </summary>
    [JsonPropertyName("waiters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<WaiterInfo>? Waiters { get; set; }
}

/// <summary>
/// Information about an owner of a synchronization primitive.
/// </summary>
public class SyncPrimitiveOwner
{
    /// <summary>
    /// Address of the owner object (null for static fields).
    /// </summary>
    [JsonPropertyName("address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Address { get; set; }

    /// <summary>
    /// Type name of the owner object or declaring type for static fields.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Name of the field that holds the sync primitive.
    /// </summary>
    [JsonPropertyName("fieldName")]
    public string FieldName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is a static field reference.
    /// </summary>
    [JsonPropertyName("isStatic")]
    public bool IsStatic { get; set; }
}

/// <summary>
/// Information about a waiter on a synchronization primitive.
/// </summary>
public class WaiterInfo
{
    /// <summary>
    /// Thread ID of the waiter (null for async waiters).
    /// </summary>
    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ThreadId { get; set; }

    /// <summary>
    /// Address of the task/continuation for async waiters.
    /// </summary>
    [JsonPropertyName("taskAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaskAddress { get; set; }

    /// <summary>
    /// Whether this is an async waiter.
    /// </summary>
    [JsonPropertyName("isAsync")]
    public bool IsAsync { get; set; }
}

/// <summary>
/// Information about a ReaderWriterLockSlim instance.
/// </summary>
public class ReaderWriterLockInfo
{
    /// <summary>
    /// Address of the ReaderWriterLockSlim object.
    /// </summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// List of objects/types that reference this ReaderWriterLockSlim.
    /// </summary>
    [JsonPropertyName("owners")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SyncPrimitiveOwner>? Owners { get; set; }

    /// <summary>
    /// Number of current readers.
    /// </summary>
    [JsonPropertyName("currentReaders")]
    public int CurrentReaders { get; set; }

    /// <summary>
    /// Whether a writer currently holds the lock.
    /// </summary>
    [JsonPropertyName("hasWriter")]
    public bool HasWriter { get; set; }

    /// <summary>
    /// Whether an upgrader currently holds the lock.
    /// </summary>
    [JsonPropertyName("hasUpgrader")]
    public bool HasUpgrader { get; set; }

    /// <summary>
    /// Thread ID of the write lock owner (if any).
    /// </summary>
    [JsonPropertyName("writerThreadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WriterThreadId { get; set; }

    /// <summary>
    /// Number of threads waiting for read access.
    /// </summary>
    [JsonPropertyName("readWaiters")]
    public int ReadWaiters { get; set; }

    /// <summary>
    /// Number of threads waiting for write access.
    /// </summary>
    [JsonPropertyName("writeWaiters")]
    public int WriteWaiters { get; set; }

    /// <summary>
    /// Number of threads waiting for upgradeable access.
    /// </summary>
    [JsonPropertyName("upgradeWaiters")]
    public int UpgradeWaiters { get; set; }

    /// <summary>
    /// Whether there is contention (waiters present).
    /// </summary>
    [JsonPropertyName("isContended")]
    public bool IsContended => ReadWaiters > 0 || WriteWaiters > 0 || UpgradeWaiters > 0;
}

/// <summary>
/// Information about a ManualResetEventSlim or similar reset event.
/// </summary>
public class ResetEventInfo
{
    /// <summary>
    /// Address of the event object.
    /// </summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// List of objects/types that reference this event.
    /// </summary>
    [JsonPropertyName("owners")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SyncPrimitiveOwner>? Owners { get; set; }

    /// <summary>
    /// Type of the event (ManualResetEventSlim, AutoResetEvent, etc.).
    /// </summary>
    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Whether the event is currently signaled.
    /// </summary>
    [JsonPropertyName("isSignaled")]
    public bool IsSignaled { get; set; }

    /// <summary>
    /// Number of threads waiting on this event.
    /// </summary>
    [JsonPropertyName("waiters")]
    public int Waiters { get; set; }

    /// <summary>
    /// Spin count configured for the event.
    /// </summary>
    [JsonPropertyName("spinCount")]
    public int SpinCount { get; set; }
}

/// <summary>
/// Information about a WaitHandle-based primitive (Mutex, Semaphore, EventWaitHandle).
/// </summary>
public class WaitHandleInfo
{
    /// <summary>
    /// Address of the WaitHandle object.
    /// </summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// List of objects/types that reference this WaitHandle.
    /// </summary>
    [JsonPropertyName("owners")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SyncPrimitiveOwner>? Owners { get; set; }

    /// <summary>
    /// Type of the WaitHandle.
    /// </summary>
    [JsonPropertyName("handleType")]
    public string HandleType { get; set; } = string.Empty;

    /// <summary>
    /// Name of the handle (if named, for cross-process synchronization).
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// Thread IDs waiting on this handle (detected via stack analysis).
    /// </summary>
    [JsonPropertyName("waitingThreads")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? WaitingThreads { get; set; }
}

/// <summary>
/// Wait graph representing thread-resource dependencies.
/// </summary>
public class WaitGraph
{
    /// <summary>
    /// Nodes in the wait graph.
    /// </summary>
    [JsonPropertyName("nodes")]
    public List<WaitGraphNode> Nodes { get; set; } = [];

    /// <summary>
    /// Edges representing wait relationships.
    /// </summary>
    [JsonPropertyName("edges")]
    public List<WaitGraphEdge> Edges { get; set; } = [];
}

/// <summary>
/// A node in the wait graph (thread or resource).
/// </summary>
public class WaitGraphNode
{
    /// <summary>
    /// Unique identifier for the node.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Type of node: "thread" or "resource".
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable label for the node.
    /// </summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Resource type (for resource nodes): Monitor, SemaphoreSlim, etc.
    /// </summary>
    [JsonPropertyName("resourceType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResourceType { get; set; }
}

/// <summary>
/// An edge in the wait graph.
/// </summary>
public class WaitGraphEdge
{
    /// <summary>
    /// Source node ID.
    /// </summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>
    /// Target node ID.
    /// </summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>
    /// Relationship type: "waits" or "owns".
    /// </summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// A detected deadlock cycle.
/// </summary>
public class DeadlockCycle
{
    /// <summary>
    /// Unique ID for this deadlock.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Thread IDs involved in the deadlock.
    /// </summary>
    [JsonPropertyName("threads")]
    public List<int> Threads { get; set; } = [];

    /// <summary>
    /// Resources involved in the deadlock.
    /// </summary>
    [JsonPropertyName("resources")]
    public List<string> Resources { get; set; } = [];

    /// <summary>
    /// Cycle representation as a string.
    /// </summary>
    [JsonPropertyName("cycle")]
    public string Cycle { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description of the deadlock.
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// A contention hotspot - a heavily contested synchronization primitive.
/// </summary>
public class ContentionHotspot
{
    /// <summary>
    /// Address of the contended resource.
    /// </summary>
    [JsonPropertyName("resource")]
    public string Resource { get; set; } = string.Empty;

    /// <summary>
    /// Type of the resource.
    /// </summary>
    [JsonPropertyName("resourceType")]
    public string ResourceType { get; set; } = string.Empty;

    /// <summary>
    /// Number of waiters on this resource.
    /// </summary>
    [JsonPropertyName("waitersCount")]
    public int WaitersCount { get; set; }

    /// <summary>
    /// Severity level: "low", "medium", "high", "critical".
    /// </summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "low";

    /// <summary>
    /// Recommendation for addressing the contention.
    /// </summary>
    [JsonPropertyName("recommendation")]
    public string Recommendation { get; set; } = string.Empty;
}

