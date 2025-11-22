using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Represents the complete result of comparing two memory dumps.
/// </summary>
public class DumpComparisonResult
{
    /// <summary>
    /// Gets or sets information about the baseline dump.
    /// </summary>
    [JsonPropertyName("baseline")]
    public DumpIdentifier Baseline { get; set; } = new();

    /// <summary>
    /// Gets or sets information about the comparison dump.
    /// </summary>
    [JsonPropertyName("comparison")]
    public DumpIdentifier Comparison { get; set; } = new();

    /// <summary>
    /// Gets or sets the heap comparison results.
    /// </summary>
    [JsonPropertyName("heapComparison")]
    public HeapComparison? HeapComparison { get; set; }

    /// <summary>
    /// Gets or sets the thread comparison results.
    /// </summary>
    [JsonPropertyName("threadComparison")]
    public ThreadComparison? ThreadComparison { get; set; }

    /// <summary>
    /// Gets or sets the module comparison results.
    /// </summary>
    [JsonPropertyName("moduleComparison")]
    public ModuleComparison? ModuleComparison { get; set; }

    /// <summary>
    /// Gets or sets the summary of the comparison.
    /// </summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets recommendations based on the comparison.
    /// </summary>
    [JsonPropertyName("recommendations")]
    public List<string> Recommendations { get; set; } = new();

    /// <summary>
    /// Gets or sets when the comparison was performed.
    /// </summary>
    [JsonPropertyName("comparedAt")]
    public DateTime ComparedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Identifies a dump file used in comparison.
/// </summary>
public class DumpIdentifier
{
    /// <summary>
    /// Gets or sets the session ID.
    /// </summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the dump ID.
    /// </summary>
    [JsonPropertyName("dumpId")]
    public string? DumpId { get; set; }

    /// <summary>
    /// Gets or sets the debugger type used.
    /// </summary>
    [JsonPropertyName("debuggerType")]
    public string DebuggerType { get; set; } = string.Empty;
}

/// <summary>
/// Result of heap comparison between two dumps.
/// </summary>
public class HeapComparison
{
    /// <summary>
    /// Gets or sets the total memory in the baseline dump in bytes.
    /// </summary>
    [JsonPropertyName("baselineMemoryBytes")]
    public long BaselineMemoryBytes { get; set; }

    /// <summary>
    /// Gets or sets the total memory in the comparison dump in bytes.
    /// </summary>
    [JsonPropertyName("comparisonMemoryBytes")]
    public long ComparisonMemoryBytes { get; set; }

    /// <summary>
    /// Gets or sets the memory delta (comparison - baseline) in bytes.
    /// </summary>
    [JsonPropertyName("memoryDeltaBytes")]
    public long MemoryDeltaBytes { get; set; }

    /// <summary>
    /// Gets or sets the memory growth percentage.
    /// </summary>
    [JsonPropertyName("memoryGrowthPercent")]
    public double MemoryGrowthPercent { get; set; }

    /// <summary>
    /// Gets or sets the growth statistics per type.
    /// </summary>
    [JsonPropertyName("typeGrowth")]
    public List<TypeGrowthStats> TypeGrowth { get; set; } = new();

    /// <summary>
    /// Gets or sets types that only exist in the comparison dump.
    /// </summary>
    [JsonPropertyName("newTypes")]
    public List<TypeStats> NewTypes { get; set; } = new();

    /// <summary>
    /// Gets or sets types that only exist in the baseline dump (freed).
    /// </summary>
    [JsonPropertyName("removedTypes")]
    public List<TypeStats> RemovedTypes { get; set; } = new();

    /// <summary>
    /// Gets or sets whether a memory leak is suspected.
    /// For compare command, this is more reliable than single-dump analysis.
    /// </summary>
    [JsonPropertyName("memoryLeakSuspected")]
    public bool MemoryLeakSuspected { get; set; }

    /// <summary>
    /// Gets or sets the leak confidence level (Low, Medium, High).
    /// </summary>
    [JsonPropertyName("leakConfidence")]
    public string LeakConfidence { get; set; } = "None";

    /// <summary>
    /// Gets or sets specific indicators that support the leak suspicion.
    /// </summary>
    [JsonPropertyName("leakIndicators")]
    public List<string> LeakIndicators { get; set; } = new();

    /// <summary>
    /// Gets or sets the types that are most likely leaking (growing unbounded).
    /// </summary>
    [JsonPropertyName("suspectedLeakingTypes")]
    public List<TypeGrowthStats> SuspectedLeakingTypes { get; set; } = new();

    /// <summary>
    /// Gets or sets the raw heap statistics from baseline dump.
    /// </summary>
    [JsonPropertyName("baselineRaw")]
    public string? BaselineRaw { get; set; }

    /// <summary>
    /// Gets or sets the raw heap statistics from comparison dump.
    /// </summary>
    [JsonPropertyName("comparisonRaw")]
    public string? ComparisonRaw { get; set; }
}

/// <summary>
/// Statistics for a type's growth between dumps.
/// </summary>
public class TypeGrowthStats
{
    /// <summary>
    /// Gets or sets the type name.
    /// </summary>
    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the instance count in the baseline dump.
    /// </summary>
    [JsonPropertyName("baselineCount")]
    public long BaselineCount { get; set; }

    /// <summary>
    /// Gets or sets the instance count in the comparison dump.
    /// </summary>
    [JsonPropertyName("comparisonCount")]
    public long ComparisonCount { get; set; }

    /// <summary>
    /// Gets the instance count delta.
    /// </summary>
    [JsonPropertyName("countDelta")]
    public long CountDelta => ComparisonCount - BaselineCount;

    /// <summary>
    /// Gets or sets the total size in the baseline dump in bytes.
    /// </summary>
    [JsonPropertyName("baselineSizeBytes")]
    public long BaselineSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the total size in the comparison dump in bytes.
    /// </summary>
    [JsonPropertyName("comparisonSizeBytes")]
    public long ComparisonSizeBytes { get; set; }

    /// <summary>
    /// Gets the size delta in bytes.
    /// </summary>
    [JsonPropertyName("sizeDeltaBytes")]
    public long SizeDeltaBytes => ComparisonSizeBytes - BaselineSizeBytes;

    /// <summary>
    /// Gets the growth percentage based on count.
    /// </summary>
    [JsonPropertyName("growthPercent")]
    public double GrowthPercent => BaselineCount > 0
        ? ((double)CountDelta / BaselineCount) * 100
        : (ComparisonCount > 0 ? 100 : 0);
}

/// <summary>
/// Statistics for a type in a single dump.
/// </summary>
public class TypeStats
{
    /// <summary>
    /// Gets or sets the type name.
    /// </summary>
    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the instance count.
    /// </summary>
    [JsonPropertyName("count")]
    public long Count { get; set; }

    /// <summary>
    /// Gets or sets the total size in bytes.
    /// </summary>
    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}

/// <summary>
/// Result of thread comparison between two dumps.
/// </summary>
public class ThreadComparison
{
    /// <summary>
    /// Gets or sets the thread count in the baseline dump.
    /// </summary>
    [JsonPropertyName("baselineThreadCount")]
    public int BaselineThreadCount { get; set; }

    /// <summary>
    /// Gets or sets the thread count in the comparison dump.
    /// </summary>
    [JsonPropertyName("comparisonThreadCount")]
    public int ComparisonThreadCount { get; set; }

    /// <summary>
    /// Gets the thread count delta.
    /// </summary>
    [JsonPropertyName("threadCountDelta")]
    public int ThreadCountDelta => ComparisonThreadCount - BaselineThreadCount;

    /// <summary>
    /// Gets or sets threads that only exist in the comparison dump.
    /// </summary>
    [JsonPropertyName("newThreads")]
    public List<ThreadComparisonInfo> NewThreads { get; set; } = new();

    /// <summary>
    /// Gets or sets threads that only exist in the baseline dump.
    /// </summary>
    [JsonPropertyName("terminatedThreads")]
    public List<ThreadComparisonInfo> TerminatedThreads { get; set; } = new();

    /// <summary>
    /// Gets or sets threads whose state changed between dumps.
    /// </summary>
    [JsonPropertyName("stateChangedThreads")]
    public List<ThreadStateChange> StateChangedThreads { get; set; } = new();

    /// <summary>
    /// Gets or sets threads waiting on locks in the comparison dump.
    /// </summary>
    [JsonPropertyName("threadsWaitingOnLocks")]
    public List<ThreadComparisonInfo> ThreadsWaitingOnLocks { get; set; } = new();

    /// <summary>
    /// Gets or sets whether a potential deadlock situation was detected.
    /// </summary>
    [JsonPropertyName("potentialDeadlock")]
    public bool PotentialDeadlock { get; set; }
}

/// <summary>
/// Thread information for comparison purposes.
/// </summary>
public class ThreadComparisonInfo
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
    /// Gets or sets the top function on the stack.
    /// </summary>
    [JsonPropertyName("topFunction")]
    public string TopFunction { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this thread was faulting.
    /// </summary>
    [JsonPropertyName("isFaulting")]
    public bool IsFaulting { get; set; }
}

/// <summary>
/// Represents a thread state change between dumps.
/// </summary>
public class ThreadStateChange
{
    /// <summary>
    /// Gets or sets the thread ID.
    /// </summary>
    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the state in the baseline dump.
    /// </summary>
    [JsonPropertyName("baselineState")]
    public string BaselineState { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the state in the comparison dump.
    /// </summary>
    [JsonPropertyName("comparisonState")]
    public string ComparisonState { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the top function in the baseline dump.
    /// </summary>
    [JsonPropertyName("baselineTopFunction")]
    public string BaselineTopFunction { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the top function in the comparison dump.
    /// </summary>
    [JsonPropertyName("comparisonTopFunction")]
    public string ComparisonTopFunction { get; set; } = string.Empty;
}

/// <summary>
/// Result of module comparison between two dumps.
/// </summary>
public class ModuleComparison
{
    /// <summary>
    /// Gets or sets the module count in the baseline dump.
    /// </summary>
    [JsonPropertyName("baselineModuleCount")]
    public int BaselineModuleCount { get; set; }

    /// <summary>
    /// Gets or sets the module count in the comparison dump.
    /// </summary>
    [JsonPropertyName("comparisonModuleCount")]
    public int ComparisonModuleCount { get; set; }

    /// <summary>
    /// Gets the module count delta.
    /// </summary>
    [JsonPropertyName("moduleCountDelta")]
    public int ModuleCountDelta => ComparisonModuleCount - BaselineModuleCount;

    /// <summary>
    /// Gets or sets modules loaded only in the comparison dump.
    /// </summary>
    [JsonPropertyName("newModules")]
    public List<ModuleComparisonInfo> NewModules { get; set; } = new();

    /// <summary>
    /// Gets or sets modules unloaded (only in baseline dump).
    /// </summary>
    [JsonPropertyName("unloadedModules")]
    public List<ModuleComparisonInfo> UnloadedModules { get; set; } = new();

    /// <summary>
    /// Gets or sets modules with version changes.
    /// </summary>
    [JsonPropertyName("versionChanges")]
    public List<ModuleVersionChange> VersionChanges { get; set; } = new();

    /// <summary>
    /// Gets or sets modules with base address changes (rebased).
    /// </summary>
    [JsonPropertyName("rebasedModules")]
    public List<ModuleRebaseInfo> RebasedModules { get; set; } = new();
}

/// <summary>
/// Module information for comparison purposes.
/// </summary>
public class ModuleComparisonInfo
{
    /// <summary>
    /// Gets or sets the module name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base address.
    /// </summary>
    [JsonPropertyName("baseAddress")]
    public string BaseAddress { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the version.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets whether symbols are loaded.
    /// </summary>
    [JsonPropertyName("hasSymbols")]
    public bool HasSymbols { get; set; }
}

/// <summary>
/// Represents a module version change between dumps.
/// </summary>
public class ModuleVersionChange
{
    /// <summary>
    /// Gets or sets the module name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the version in the baseline dump.
    /// </summary>
    [JsonPropertyName("baselineVersion")]
    public string? BaselineVersion { get; set; }

    /// <summary>
    /// Gets or sets the version in the comparison dump.
    /// </summary>
    [JsonPropertyName("comparisonVersion")]
    public string? ComparisonVersion { get; set; }
}

/// <summary>
/// Represents a module base address change (rebase).
/// </summary>
public class ModuleRebaseInfo
{
    /// <summary>
    /// Gets or sets the module name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base address in the baseline dump.
    /// </summary>
    [JsonPropertyName("baselineBaseAddress")]
    public string BaselineBaseAddress { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base address in the comparison dump.
    /// </summary>
    [JsonPropertyName("comparisonBaseAddress")]
    public string ComparisonBaseAddress { get; set; } = string.Empty;
}

