namespace DebuggerMcp.Analysis;

/// <summary>
/// Constants used for crash and performance analysis thresholds.
/// </summary>
/// <remarks>
/// Centralizing these constants makes it easier to tune analysis sensitivity
/// and ensures consistent thresholds across all analyzers.
/// </remarks>
public static class AnalysisConstants
{

    /// <summary>
    /// Threshold in bytes for large heap detection (500 MB).
    /// Heaps larger than this may indicate memory leaks.
    /// </summary>
    public const long LargeHeapThresholdBytes = 500L * 1024 * 1024;

    /// <summary>
    /// Threshold in bytes for large object detection (85,000 bytes).
    /// Objects larger than this go directly to the Large Object Heap (LOH).
    /// </summary>
    public const int LargeObjectHeapThreshold = 85_000;

    /// <summary>
    /// Threshold in bytes for extreme string growth detection (100 MB).
    /// String allocations growing faster than this may indicate string accumulation.
    /// </summary>
    public const long ExtremeStringGrowthThresholdBytes = 100_000_000;

    /// <summary>
    /// Threshold count for extreme string growth detection.
    /// More than this many new strings may indicate a leak.
    /// </summary>
    public const int ExtremeStringGrowthCountThreshold = 100_000;



    /// <summary>
    /// Threshold for high instance count that may indicate a memory leak.
    /// Types with more instances than this warrant investigation.
    /// </summary>
    public const int HighInstanceCountThreshold = 10_000;

    /// <summary>
    /// Threshold for WeakReference count that may indicate workarounds for leaks.
    /// </summary>
    public const int HighWeakReferenceCountThreshold = 10_000;

    /// <summary>
    /// Threshold for timer count that may indicate timer leaks.
    /// </summary>
    public const int HighTimerCountThreshold = 100;

    /// <summary>
    /// Threshold for event handler count that may indicate event subscription leaks.
    /// </summary>
    public const int HighEventHandlerCountThreshold = 1_000;



    /// <summary>
    /// Memory addresses below this threshold indicate null/near-null pointer access.
    /// The null page (0x0000 - 0xFFFF) should never be accessed in normal operation.
    /// </summary>
    public const long NullPageThreshold = 0x10000;

    /// <summary>
    /// Start of kernel address space on 64-bit systems.
    /// User-mode code should not be accessing kernel addresses.
    /// </summary>
    public const long KernelAddressStart = 0x7FFF0000_00000000;

    /// <summary>
    /// Distance from stack pointer within which execution is suspicious (16 MB).
    /// Execution near the stack pointer may indicate shellcode.
    /// </summary>
    public const long SuspiciousStackExecutionDistance = 0x1000000;



    /// <summary>
    /// Threshold for high CPU thread detection.
    /// Threads with higher CPU usage than this percentage are flagged.
    /// </summary>
    public const int HighCpuThreadThresholdPercent = 80;

    /// <summary>
    /// Threshold for thread contention detection (milliseconds).
    /// Wait times longer than this may indicate lock contention issues.
    /// </summary>
    public const int ContentionWaitThresholdMs = 1000;

    /// <summary>
    /// Threshold for GC fragmentation percentage.
    /// Fragmentation higher than this may impact performance.
    /// </summary>
    public const int GcFragmentationThresholdPercent = 50;



    /// <summary>
    /// Minimum memory growth in bytes to flag as significant (10 MB).
    /// </summary>
    public const long SignificantMemoryGrowthBytes = 10L * 1024 * 1024;

    /// <summary>
    /// Minimum percentage growth to flag as significant.
    /// </summary>
    public const int SignificantGrowthPercent = 20;

}

