using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using DebuggerMcp.Watches;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Represents the result of an automated crash analysis.
/// Contains structured information about the crash for easy consumption by LLMs.
/// </summary>
public class CrashAnalysisResult
{
    /// <summary>
    /// Report metadata.
    /// </summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CrashReportMetadata? Metadata { get; set; }

    /// <summary>
    /// Analysis summary with crash type, severity, and recommendations.
    /// </summary>
    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnalysisSummary? Summary { get; set; }

    /// <summary>
    /// Exception details (lifted to top level for prominence).
    /// </summary>
    [JsonPropertyName("exception")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExceptionDetails? Exception { get; set; }

    /// <summary>
    /// Environment info (platform, runtime, process).
    /// </summary>
    [JsonPropertyName("environment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EnvironmentInfo? Environment { get; set; }

    /// <summary>
    /// Threads container (summary, all threads, threadpool, deadlock).
    /// </summary>
    [JsonPropertyName("threads")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThreadsInfo? Threads { get; set; }

    /// <summary>
    /// Memory container (GC, heap, strings, leaks, OOM).
    /// </summary>
    [JsonPropertyName("memory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MemoryInfo? Memory { get; set; }

    /// <summary>
    /// Assemblies container with count and items.
    /// </summary>
    [JsonPropertyName("assemblies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AssembliesInfo? Assemblies { get; set; }

    /// <summary>
    /// Native modules (kept at top level).
    /// </summary>
    [JsonPropertyName("modules")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ModuleInfo>? Modules { get; set; }

    /// <summary>
    /// Async container (state machines, timers, faulted tasks).
    /// </summary>
    [JsonPropertyName("async")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AsyncInfo? Async { get; set; }

    /// <summary>
    /// Security container (findings, recommendations).
    /// </summary>
    [JsonPropertyName("security")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecurityInfo? Security { get; set; }

    /// <summary>
    /// Synchronization primitives analysis (locks, semaphores, events, deadlocks).
    /// </summary>
    [JsonPropertyName("synchronization")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Synchronization.SynchronizationAnalysisResult? Synchronization { get; set; }

    /// <summary>
    /// Watch expression evaluation results.
    /// </summary>
    [JsonPropertyName("watches")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WatchEvaluationReport? Watches { get; set; }

    /// <summary>
    /// Raw debugger command outputs.
    /// </summary>
    [JsonPropertyName("rawCommands")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? RawCommands { get; set; }

}

/// <summary>
/// Represents a stack frame in a call stack.
/// </summary>
public class StackFrame
{
    /// <summary>
    /// Gets or sets the frame number.
    /// </summary>
    [JsonPropertyName("frameNumber")]
    public int FrameNumber { get; set; }

    /// <summary>
    /// Gets or sets the instruction pointer.
    /// </summary>
    [JsonPropertyName("instructionPointer")]
    public string InstructionPointer { get; set; } = string.Empty;

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
    /// Gets or sets the source file and line if available (combined display string).
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets the source file path (from PDB).
    /// </summary>
    [JsonPropertyName("sourceFile")]
    public string? SourceFile { get; set; }

    /// <summary>
    /// Gets or sets the line number in the source file.
    /// </summary>
    [JsonPropertyName("lineNumber")]
    public int? LineNumber { get; set; }

    /// <summary>
    /// Gets or sets the Source Link URL to browse the source code.
    /// This is a browsable URL (e.g., GitHub blob URL with line number).
    /// </summary>
    [JsonPropertyName("sourceUrl")]
    public string? SourceUrl { get; set; }

    /// <summary>
    /// Gets or sets the source control provider (GitHub, GitLab, etc.).
    /// </summary>
    [JsonPropertyName("sourceProvider")]
    public string? SourceProvider { get; set; }

    /// <summary>
    /// Gets or sets whether this is a managed (.NET) frame vs native frame.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    /// <summary>
    /// Gets or sets the stack pointer for this frame.
    /// </summary>
    [JsonPropertyName("stackPointer")]
    public string? StackPointer { get; set; }

    /// <summary>
    /// Gets or sets the register values for this frame.
    /// Key is register name (e.g., "x0", "rax", "sp"), value is the hex value.
    /// </summary>
    [JsonPropertyName("registers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Registers { get; set; }

    /// <summary>
    /// Gets or sets the parameters passed to this frame's function.
    /// Only available for managed frames when using clrstack -a.
    /// </summary>
    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LocalVariable>? Parameters { get; set; }

    /// <summary>
    /// Gets or sets the local variables in this frame.
    /// Only available for managed frames when using clrstack -a.
    /// </summary>
    [JsonPropertyName("locals")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LocalVariable>? Locals { get; set; }
}

/// <summary>
/// Represents a local variable or parameter in a stack frame.
/// </summary>
public class LocalVariable
{
    /// <summary>
    /// Gets or sets the variable name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the variable type (e.g., "Int32", "String", "System.Object").
    /// </summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets the variable value.
    /// Can be: string (primitive/address), number, boolean, or expanded object from showobj.
    /// For reference types with expanded data, this will be a full InspectedObject.
    /// </summary>
    [JsonPropertyName("value")]
    public object? Value { get; set; }

    /// <summary>
    /// Gets or sets the raw hex value before conversion.
    /// Only set when value has been converted to a meaningful representation.
    /// </summary>
    [JsonPropertyName("rawValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RawValue { get; set; }

    /// <summary>
    /// Gets or sets the storage location (e.g., "CLR reg", "stack", or register name).
    /// </summary>
    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Location { get; set; }

    /// <summary>
    /// Gets or sets whether the variable data is available.
    /// </summary>
    [JsonPropertyName("hasData")]
    public bool HasData { get; set; } = true;

    /// <summary>
    /// Gets or sets whether this value is a reference type (object) that can be inspected with !dumpobj.
    /// Null if type is unknown.
    /// </summary>
    [JsonPropertyName("isReferenceType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsReferenceType { get; set; }

    /// <summary>
    /// For ByRef types: the address where the reference is stored.
    /// The 'value' field contains the dereferenced content.
    /// </summary>
    [JsonPropertyName("byRefAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ByRefAddress { get; set; }

    /// <summary>
    /// For ByRef types: the dereferenced address (the actual object pointer).
    /// Use this with !dumpobj to inspect the object.
    /// </summary>
    [JsonPropertyName("resolvedAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResolvedAddress { get; set; }
}

/// <summary>
/// Represents thread information.
/// </summary>
public class ThreadInfo
{
    /// <summary>
    /// Gets or sets the thread ID (debugger format).
    /// </summary>
    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the thread state (signal/stop reason).
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this is the faulting thread.
    /// </summary>
    [JsonPropertyName("isFaulting")]
    public bool IsFaulting { get; set; }

    /// <summary>
    /// Gets or sets the top function on the thread's stack.
    /// </summary>
    [JsonPropertyName("topFunction")]
    public string TopFunction { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the call stack for this thread.
    /// Contains both native and managed frames (if .NET).
    /// </summary>
    [JsonPropertyName("callStack")]
    public List<StackFrame> CallStack { get; set; } = new();

    // --- CLR Thread Info (from !clrthreads) ---

    /// <summary>
    /// Gets or sets the managed thread ID (from !clrthreads ID column).
    /// </summary>
    [JsonPropertyName("managedThreadId")]
    public int? ManagedThreadId { get; set; }

    /// <summary>
    /// Gets or sets the OS thread ID in hex (from !clrthreads OSID column).
    /// </summary>
    [JsonPropertyName("osThreadId")]
    public string? OsThreadId { get; set; }

    /// <summary>
    /// Gets or sets the CLR thread object address.
    /// </summary>
    [JsonPropertyName("threadObject")]
    public string? ThreadObject { get; set; }

    /// <summary>
    /// Gets or sets the CLR thread state flags (hex).
    /// </summary>
    [JsonPropertyName("clrThreadState")]
    public string? ClrThreadState { get; set; }

    /// <summary>
    /// Gets or sets the GC mode (Preemptive or Cooperative).
    /// </summary>
    [JsonPropertyName("gcMode")]
    public string? GcMode { get; set; }

    /// <summary>
    /// Gets or sets the thread type (Finalizer, Threadpool Worker, etc.).
    /// </summary>
    [JsonPropertyName("threadType")]
    public string? ThreadType { get; set; }

    /// <summary>
    /// Gets or sets the number of locks held by this thread.
    /// </summary>
    [JsonPropertyName("lockCount")]
    public int? LockCount { get; set; }

    /// <summary>
    /// Gets or sets the apartment state (Ukn, STA, MTA).
    /// </summary>
    [JsonPropertyName("apartmentState")]
    public string? ApartmentState { get; set; }

    /// <summary>
    /// Gets or sets the current exception on this thread (type and address).
    /// </summary>
    [JsonPropertyName("currentException")]
    public string? CurrentException { get; set; }

    /// <summary>
    /// Gets or sets whether this is a dead thread (XXXX in DBG column).
    /// </summary>
    [JsonPropertyName("isDead")]
    public bool IsDead { get; set; }

    /// <summary>
    /// Gets or sets whether this is a background thread.
    /// </summary>
    [JsonPropertyName("isBackground")]
    public bool? IsBackground { get; set; }

    /// <summary>
    /// Gets or sets whether this is a threadpool thread.
    /// </summary>
    [JsonPropertyName("isThreadpool")]
    public bool? IsThreadpool { get; set; }

    // === Phase 2 ClrMD Enrichment ===

    /// <summary>
    /// Gets or sets additional thread info from ClrMD.
    /// </summary>
    [JsonPropertyName("clrMdThreadInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClrMdThreadInfo? ClrMdThreadInfo { get; set; }
}

/// <summary>
/// Represents module (DLL/SO) information.
/// </summary>
public class ModuleInfo
{
    /// <summary>
    /// Gets or sets the module name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the module version.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the module base address.
    /// </summary>
    [JsonPropertyName("baseAddress")]
    public string BaseAddress { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether symbols are loaded for this module.
    /// </summary>
    [JsonPropertyName("hasSymbols")]
    public bool HasSymbols { get; set; }
}

/// <summary>
/// Represents OOM (Out of Memory) analysis from analyzeoom command.
/// </summary>
public class OomAnalysisInfo
{
    /// <summary>
    /// Gets or sets whether an OOM condition was detected.
    /// </summary>
    [JsonPropertyName("detected")]
    public bool Detected { get; set; }

    /// <summary>
    /// Gets or sets the reason for the OOM (if detected).
    /// </summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets which generation ran out of memory.
    /// </summary>
    [JsonPropertyName("generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Generation { get; set; }

    /// <summary>
    /// Gets or sets the allocation size that caused OOM.
    /// </summary>
    [JsonPropertyName("allocationSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AllocationSize { get; set; }

    /// <summary>
    /// Gets or sets the Large Object Heap (LOH) usage if relevant.
    /// </summary>
    [JsonPropertyName("lohSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LohSize { get; set; }

    /// <summary>
    /// Gets or sets the raw output message.
    /// </summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}

/// <summary>
/// Represents crash diagnostic information from crashinfo command.
/// </summary>
public class CrashDiagnosticInfo
{
    /// <summary>
    /// Gets or sets whether crash info was found.
    /// </summary>
    [JsonPropertyName("hasInfo")]
    public bool HasInfo { get; set; }

    /// <summary>
    /// Gets or sets the crash reason.
    /// </summary>
    [JsonPropertyName("crashReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CrashReason { get; set; }

    /// <summary>
    /// Gets or sets the signal number (Linux).
    /// </summary>
    [JsonPropertyName("signal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Signal { get; set; }

    /// <summary>
    /// Gets or sets the signal name (e.g., SIGSEGV, SIGABRT).
    /// </summary>
    [JsonPropertyName("signalName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignalName { get; set; }

    /// <summary>
    /// Gets or sets the faulting address.
    /// </summary>
    [JsonPropertyName("faultingAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FaultingAddress { get; set; }

    /// <summary>
    /// Gets or sets the exception record address (Windows).
    /// </summary>
    [JsonPropertyName("exceptionRecord")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExceptionRecord { get; set; }

    /// <summary>
    /// Gets or sets the thread that caused the crash.
    /// </summary>
    [JsonPropertyName("crashingThread")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CrashingThread { get; set; }

    /// <summary>
    /// Gets or sets additional diagnostic message.
    /// </summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}

/// <summary>
/// Represents thread pool information from !threadpool command.
/// </summary>
public class ThreadPoolInfo
{
    /// <summary>
    /// Gets or sets the CPU utilization percentage.
    /// </summary>
    [JsonPropertyName("cpuUtilization")]
    public int? CpuUtilization { get; set; }

    /// <summary>
    /// Gets or sets the total number of worker threads.
    /// </summary>
    [JsonPropertyName("workersTotal")]
    public int? WorkersTotal { get; set; }

    /// <summary>
    /// Gets or sets the number of running worker threads.
    /// </summary>
    [JsonPropertyName("workersRunning")]
    public int? WorkersRunning { get; set; }

    /// <summary>
    /// Gets or sets the number of idle worker threads.
    /// </summary>
    [JsonPropertyName("workersIdle")]
    public int? WorkersIdle { get; set; }

    /// <summary>
    /// Gets or sets the minimum worker thread limit.
    /// </summary>
    [JsonPropertyName("workerMinLimit")]
    public int? WorkerMinLimit { get; set; }

    /// <summary>
    /// Gets or sets the maximum worker thread limit.
    /// </summary>
    [JsonPropertyName("workerMaxLimit")]
    public int? WorkerMaxLimit { get; set; }

    /// <summary>
    /// Gets or sets whether the portable thread pool is used.
    /// </summary>
    [JsonPropertyName("isPortableThreadPool")]
    public bool? IsPortableThreadPool { get; set; }
}

/// <summary>
/// Represents a timer from !ti (timerinfo) command.
/// </summary>
public class TimerInfo
{
    /// <summary>
    /// Gets or sets the timer object address.
    /// </summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the due time in milliseconds.
    /// </summary>
    [JsonPropertyName("dueTimeMs")]
    public int? DueTimeMs { get; set; }

    /// <summary>
    /// Gets or sets the period in milliseconds (null for one-shot timers).
    /// </summary>
    [JsonPropertyName("periodMs")]
    public int? PeriodMs { get; set; }

    /// <summary>
    /// Gets or sets the state object address.
    /// </summary>
    [JsonPropertyName("stateAddress")]
    public string? StateAddress { get; set; }

    /// <summary>
    /// Gets or sets the state object type.
    /// </summary>
    [JsonPropertyName("stateType")]
    public string? StateType { get; set; }
    
    /// <summary>
    /// Gets or sets the inspected state object value (ClrMD inspection result).
    /// </summary>
    [JsonPropertyName("stateValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClrMdObjectInspection? StateValue { get; set; }

    /// <summary>
    /// Gets or sets the callback method name.
    /// </summary>
    [JsonPropertyName("callback")]
    public string? Callback { get; set; }
}

/// <summary>
/// Represents memory analysis information.
/// Note: A true memory leak can only be confirmed by comparing multiple snapshots over time.
/// A single dump can only show high memory consumption, not definitive leaks.
/// </summary>
public class MemoryLeakInfo
{
    /// <summary>
    /// Gets or sets whether high memory consumption was detected.
    /// This does NOT mean a memory leak - just that memory usage is notable.
    /// </summary>
    [JsonPropertyName("detected")]
    public bool Detected { get; set; }

    /// <summary>
    /// Gets or sets the severity level of memory consumption.
    /// </summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "Normal";

    /// <summary>
    /// Gets or sets the top memory consumers.
    /// </summary>
    [JsonPropertyName("topConsumers")]
    public List<MemoryConsumer> TopConsumers { get; set; } = new();

    /// <summary>
    /// Gets or sets the total managed heap size in bytes.
    /// Note: This is heap usage, not necessarily "leaked" memory.
    /// </summary>
    [JsonPropertyName("totalHeapBytes")]
    public long TotalHeapBytes { get; set; }

    /// <summary>
    /// Gets or sets indicators that suggest potential memory issues.
    /// </summary>
    [JsonPropertyName("potentialIssueIndicators")]
    public List<string> PotentialIssueIndicators { get; set; } = new();
}

/// <summary>
/// Represents a memory consumer (type/allocation).
/// </summary>
public class MemoryConsumer
{
    /// <summary>
    /// Gets or sets the type name.
    /// </summary>
    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of instances.
    /// </summary>
    [JsonPropertyName("count")]
    public long Count { get; set; }

    /// <summary>
    /// Gets or sets the total size in bytes.
    /// </summary>
    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }
}

/// <summary>
/// Represents deadlock analysis information.
/// </summary>
public class DeadlockInfo
{
    /// <summary>
    /// Gets or sets whether a deadlock was detected.
    /// </summary>
    [JsonPropertyName("detected")]
    public bool Detected { get; set; }

    /// <summary>
    /// Gets or sets the threads involved in the deadlock.
    /// </summary>
    [JsonPropertyName("involvedThreads")]
    public List<string> InvolvedThreads { get; set; } = new();

    /// <summary>
    /// Gets or sets the locks involved.
    /// </summary>
    [JsonPropertyName("locks")]
    public List<LockInfo> Locks { get; set; } = new();
}

/// <summary>
/// Represents lock information.
/// </summary>
public class LockInfo
{
    /// <summary>
    /// Gets or sets the lock address.
    /// </summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the thread holding the lock.
    /// </summary>
    [JsonPropertyName("owner")]
    public string Owner { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets threads waiting for the lock.
    /// </summary>
    [JsonPropertyName("waiters")]
    public List<string> Waiters { get; set; } = new();
}

/// <summary>
/// Represents detailed assembly version information for diagnosing version mismatch issues.
/// </summary>
public class AssemblyVersionInfo
{
    /// <summary>
    /// Gets or sets the assembly name (e.g., "System.Private.CoreLib").
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the assembly version (e.g., "9.0.0.0").
    /// </summary>
    [JsonPropertyName("assemblyVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssemblyVersion { get; set; }

    /// <summary>
    /// Gets or sets the file version (e.g., "9.0.1025.47515").
    /// </summary>
    [JsonPropertyName("fileVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileVersion { get; set; }

    /// <summary>
    /// Gets or sets the assembly path.
    /// </summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the module base address in memory.
    /// </summary>
    [JsonPropertyName("baseAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseAddress { get; set; }

    /// <summary>
    /// Gets or sets the assembly address (from dumpdomain).
    /// </summary>
    [JsonPropertyName("assemblyAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssemblyAddress { get; set; }

    /// <summary>
    /// Gets or sets whether this is a dynamic assembly.
    /// </summary>
    [JsonPropertyName("isDynamic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsDynamic { get; set; }

    /// <summary>
    /// Gets or sets whether this assembly is in the GAC (Global Assembly Cache).
    /// </summary>
    [JsonPropertyName("isInGac")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsInGac { get; set; }

    /// <summary>
    /// Gets or sets whether this is a native image (NGEN/ReadyToRun/NativeAOT).
    /// </summary>
    [JsonPropertyName("isNativeImage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsNativeImage { get; set; }

    /// <summary>
    /// Gets or sets the module ID.
    /// </summary>
    [JsonPropertyName("moduleId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModuleId { get; set; }

    // === ClrMD enriched properties (from dump memory) ===

    /// <summary>
    /// Gets or sets the informational version (includes semver + commit hash).
    /// Example: "9.0.0+abc123def456"
    /// </summary>
    [JsonPropertyName("informationalVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InformationalVersion { get; set; }

    /// <summary>
    /// Gets or sets the company name from AssemblyCompanyAttribute.
    /// </summary>
    [JsonPropertyName("company")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Company { get; set; }

    /// <summary>
    /// Gets or sets the product name from AssemblyProductAttribute.
    /// </summary>
    [JsonPropertyName("product")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Product { get; set; }

    /// <summary>
    /// Gets or sets the copyright notice from AssemblyCopyrightAttribute.
    /// </summary>
    [JsonPropertyName("copyright")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Copyright { get; set; }

    /// <summary>
    /// Gets or sets the build configuration from AssemblyConfigurationAttribute.
    /// Example: "Release", "Debug"
    /// </summary>
    [JsonPropertyName("configuration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Configuration { get; set; }

    /// <summary>
    /// Gets or sets the assembly title from AssemblyTitleAttribute.
    /// </summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the assembly description from AssemblyDescriptionAttribute.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the repository URL from AssemblyMetadataAttribute("RepositoryUrl", ...).
    /// </summary>
    [JsonPropertyName("repositoryUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RepositoryUrl { get; set; }

    /// <summary>
    /// Gets or sets the commit hash extracted from InformationalVersion.
    /// </summary>
    [JsonPropertyName("commitHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommitHash { get; set; }

    /// <summary>
    /// Gets or sets the target framework from TargetFrameworkAttribute.
    /// Example: ".NETCoreApp,Version=v9.0"
    /// </summary>
    [JsonPropertyName("targetFramework")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetFramework { get; set; }

    /// <summary>
    /// Gets or sets additional custom attributes not mapped to specific properties.
    /// </summary>
    [JsonPropertyName("customAttributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? CustomAttributes { get; set; }

    // === GitHub commit metadata (enriched via GitHub API) ===

    /// <summary>
    /// Gets or sets the source URL pointing to the commit tree on GitHub.
    /// Example: https://github.com/DataDog/dd-trace-dotnet/tree/14fd3a2fe...
    /// </summary>
    [JsonPropertyName("sourceUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; set; }

    /// <summary>
    /// Gets or sets the commit author's name from GitHub.
    /// </summary>
    [JsonPropertyName("authorName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthorName { get; set; }

    /// <summary>
    /// Gets or sets the commit author's date (ISO 8601 format).
    /// </summary>
    [JsonPropertyName("authorDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthorDate { get; set; }

    /// <summary>
    /// Gets or sets the committer's name from GitHub.
    /// </summary>
    [JsonPropertyName("committerName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommitterName { get; set; }

    /// <summary>
    /// Gets or sets the committer's date (ISO 8601 format).
    /// </summary>
    [JsonPropertyName("committerDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommitterDate { get; set; }

    /// <summary>
    /// Gets or sets the commit message (may be truncated for very long messages).
    /// </summary>
    [JsonPropertyName("commitMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommitMessage { get; set; }

    // === Azure Pipelines metadata (for Datadog assemblies) ===

    /// <summary>
    /// Gets or sets the Azure Pipelines build ID that produced this assembly.
    /// </summary>
    [JsonPropertyName("azurePipelinesBuildId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AzurePipelinesBuildId { get; set; }

    /// <summary>
    /// Gets or sets the Azure Pipelines build number (version string).
    /// </summary>
    [JsonPropertyName("azurePipelinesBuildNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AzurePipelinesBuildNumber { get; set; }

    /// <summary>
    /// Gets or sets the URL to the Azure Pipelines build.
    /// </summary>
    [JsonPropertyName("azurePipelinesBuildUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AzurePipelinesBuildUrl { get; set; }

    /// <summary>
    /// Gets or sets whether symbols were downloaded for this assembly.
    /// </summary>
    [JsonPropertyName("symbolsDownloaded")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SymbolsDownloaded { get; set; }

    /// <summary>
    /// Gets or sets the local directory where symbols were downloaded.
    /// </summary>
    [JsonPropertyName("symbolsDirectory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SymbolsDirectory { get; set; }
}

/// <summary>
/// Represents detailed exception analysis for deep crash diagnosis.
/// This provides much more information than the basic exception type/message.
/// </summary>
public class ExceptionAnalysis
{
    /// <summary>
    /// Gets or sets the exception object address in memory.
    /// </summary>
    [JsonPropertyName("exceptionAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExceptionAddress { get; set; }

    /// <summary>
    /// Gets or sets the full type name of the exception.
    /// </summary>
    [JsonPropertyName("fullTypeName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FullTypeName { get; set; }

    /// <summary>
    /// Gets or sets the HResult code (formatted as 0x...).
    /// </summary>
    [JsonPropertyName("hResult")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HResult { get; set; }

    /// <summary>
    /// Gets or sets the exception message.
    /// </summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the source (usually the assembly that threw the exception).
    /// </summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets the help link if any.
    /// </summary>
    [JsonPropertyName("helpLink")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HelpLink { get; set; }

    /// <summary>
    /// Gets or sets the target site information (method that threw the exception).
    /// </summary>
    [JsonPropertyName("targetSite")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TargetSiteInfo? TargetSite { get; set; }

    /// <summary>
    /// Gets or sets the fusion log (for assembly binding exceptions like FileNotFoundException, TypeLoadException).
    /// </summary>
    [JsonPropertyName("fusionLog")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FusionLog { get; set; }

    /// <summary>
    /// Gets or sets the Data dictionary entries from the exception.
    /// </summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Data { get; set; }

    /// <summary>
    /// Gets or sets the inner exception (if any).
    /// </summary>
    [JsonPropertyName("innerException")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExceptionAnalysis? InnerException { get; set; }

    /// <summary>
    /// Gets or sets the stack trace string from the exception object.
    /// </summary>
    [JsonPropertyName("stackTraceString")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StackTraceString { get; set; }

    /// <summary>
    /// Gets or sets the flattened exception chain for easier analysis.
    /// </summary>
    [JsonPropertyName("exceptionChain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExceptionChainEntry>? ExceptionChain { get; set; }

    /// <summary>
    /// Gets or sets custom properties specific to certain exception types.
    /// For example: FileNotFoundException has FileName, TypeLoadException has TypeName, etc.
    /// </summary>
    [JsonPropertyName("customProperties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? CustomProperties { get; set; }

    /// <summary>
    /// Gets or sets the remote stack trace string (for exceptions that were re-thrown across remoting boundaries).
    /// </summary>
    [JsonPropertyName("remoteStackTraceString")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteStackTraceString { get; set; }

    /// <summary>
    /// Gets or sets the Watson buckets (internal .NET crash reporting data).
    /// </summary>
    [JsonPropertyName("watsonBuckets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WatsonBuckets { get; set; }

    /// <summary>
    /// Gets or sets type/method resolution analysis for MissingMethodException, TypeLoadException, etc.
    /// Shows what methods actually exist on the type vs what was expected.
    /// </summary>
    [JsonPropertyName("typeResolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TypeResolutionAnalysis? TypeResolution { get; set; }
}

/// <summary>
/// Represents target site (method) information where the exception was thrown.
/// </summary>
public class TargetSiteInfo
{
    /// <summary>
    /// Gets or sets the method name.
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the declaring type name.
    /// </summary>
    [JsonPropertyName("declaringType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeclaringType { get; set; }

    /// <summary>
    /// Gets or sets the member type (Method, Constructor, etc.).
    /// </summary>
    [JsonPropertyName("memberType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MemberType { get; set; }

    /// <summary>
    /// Gets or sets whether the method is public.
    /// </summary>
    [JsonPropertyName("isPublic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsPublic { get; set; }

    /// <summary>
    /// Gets or sets whether the method is static.
    /// </summary>
    [JsonPropertyName("isStatic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsStatic { get; set; }

    /// <summary>
    /// Gets or sets the return type.
    /// </summary>
    [JsonPropertyName("returnType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReturnType { get; set; }

    /// <summary>
    /// Gets or sets the method signature.
    /// </summary>
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; set; }
}

/// <summary>
/// Represents an entry in the flattened exception chain.
/// </summary>
public class ExceptionChainEntry
{
    /// <summary>
    /// Gets or sets the depth in the chain (0 = outermost exception).
    /// </summary>
    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    /// <summary>
    /// Gets or sets the exception type name.
    /// </summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets the exception message.
    /// </summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the source.
    /// </summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets the HResult.
    /// </summary>
    [JsonPropertyName("hResult")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HResult { get; set; }

    /// <summary>
    /// Gets or sets the exception object address.
    /// </summary>
    [JsonPropertyName("address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Address { get; set; }
}

/// <summary>
/// Represents platform and architecture information about the dump.
/// </summary>
public class PlatformInfo
{
    /// <summary>
    /// Gets or sets the operating system (Linux, Windows, macOS).
    /// </summary>
    [JsonPropertyName("os")]
    public string Os { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the CPU architecture (x64, arm64, x86, arm).
    /// </summary>
    [JsonPropertyName("architecture")]
    public string Architecture { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Linux distribution if applicable (Alpine, Debian, Ubuntu, etc.).
    /// </summary>
    [JsonPropertyName("distribution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Distribution { get; set; }

    /// <summary>
    /// Gets or sets whether this is an Alpine Linux (musl libc) dump.
    /// Important for .NET SOS compatibility.
    /// </summary>
    [JsonPropertyName("isAlpine")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsAlpine { get; set; }

    /// <summary>
    /// Gets or sets the C library type (glibc, musl).
    /// </summary>
    [JsonPropertyName("libcType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LibcType { get; set; }

    /// <summary>
    /// Gets or sets the .NET runtime version from the dump.
    /// </summary>
    [JsonPropertyName("runtimeVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeVersion { get; set; }

    /// <summary>
    /// Gets or sets the pointer size in bits (32 or 64).
    /// </summary>
    [JsonPropertyName("pointerSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PointerSize { get; set; }
}

/// <summary>
/// Represents process startup information extracted from the core dump.
/// Contains command-line arguments and environment variables.
/// Only available for Linux/macOS dumps analyzed with LLDB.
/// </summary>
public class ProcessInfo
{
    /// <summary>
    /// Gets or sets the command-line arguments (argv).
    /// The first element is typically the executable name/path.
    /// </summary>
    [JsonPropertyName("arguments")]
    public List<string> Arguments { get; set; } = new();

    /// <summary>
    /// Gets or sets the environment variables (envp).
    /// Each entry is in "KEY=VALUE" format.
    /// </summary>
    [JsonPropertyName("environmentVariables")]
    public List<string> EnvironmentVariables { get; set; } = new();

    /// <summary>
    /// Gets or sets the argc value from the main function.
    /// </summary>
    [JsonPropertyName("argc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Argc { get; set; }

    /// <summary>
    /// Gets or sets the argv memory address.
    /// </summary>
    [JsonPropertyName("argvAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArgvAddress { get; set; }

    /// <summary>
    /// Gets or sets whether sensitive environment variables were filtered.
    /// When true, environment variables matching sensitive patterns (passwords, tokens, etc.)
    /// have been removed or masked from the output.
    /// </summary>
    [JsonPropertyName("sensitiveDataFiltered")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SensitiveDataFiltered { get; set; }
}

/// <summary>
/// Represents type/method resolution analysis for MissingMethodException, TypeLoadException, etc.
/// Shows what methods actually exist on the type vs what was expected to help diagnose the issue.
/// </summary>
public class TypeResolutionAnalysis
{
    /// <summary>
    /// Gets or sets the type name that failed to resolve.
    /// </summary>
    [JsonPropertyName("failedType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailedType { get; set; }

    /// <summary>
    /// Gets or sets the MethodTable address of the type.
    /// </summary>
    [JsonPropertyName("methodTable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MethodTable { get; set; }

    /// <summary>
    /// Gets or sets the EEClass address of the type.
    /// </summary>
    [JsonPropertyName("eeClass")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EEClass { get; set; }

    /// <summary>
    /// Gets or sets the information about the expected member that was not found.
    /// </summary>
    [JsonPropertyName("expectedMember")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExpectedMemberInfo? ExpectedMember { get; set; }

    /// <summary>
    /// Gets or sets the list of actual methods found on the type.
    /// </summary>
    [JsonPropertyName("actualMethods")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MethodDescriptorInfo>? ActualMethods { get; set; }

    /// <summary>
    /// Gets or sets whether the expected method was found on the type.
    /// </summary>
    [JsonPropertyName("methodFound")]
    public bool MethodFound { get; set; }

    /// <summary>
    /// Gets or sets the diagnosis message explaining why resolution failed.
    /// </summary>
    [JsonPropertyName("diagnosis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Diagnosis { get; set; }

    /// <summary>
    /// Gets or sets the generic instantiation information if the type is generic.
    /// </summary>
    [JsonPropertyName("genericInstantiation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GenericInstantiationInfo? GenericInstantiation { get; set; }

    /// <summary>
    /// Gets or sets similar methods that might be what the caller intended.
    /// Useful when signature differs slightly.
    /// </summary>
    [JsonPropertyName("similarMethods")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MethodDescriptorInfo>? SimilarMethods { get; set; }
}

/// <summary>
/// Represents the expected member that was not found.
/// </summary>
public class ExpectedMemberInfo
{
    /// <summary>
    /// Gets or sets the member name.
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the full signature including return type and parameters.
    /// </summary>
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; set; }

    /// <summary>
    /// Gets or sets the member type (Method, Field, Property, etc.).
    /// </summary>
    [JsonPropertyName("memberType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MemberType { get; set; }
}

/// <summary>
/// Represents a method descriptor from the MethodTable.
/// </summary>
public class MethodDescriptorInfo
{
    /// <summary>
    /// Gets or sets the method name.
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the full method signature.
    /// </summary>
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; set; }

    /// <summary>
    /// Gets or sets the method descriptor address.
    /// </summary>
    [JsonPropertyName("methodDesc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MethodDesc { get; set; }

    /// <summary>
    /// Gets or sets the JIT status (PreJIT, JIT, NONE, etc.).
    /// </summary>
    [JsonPropertyName("jitStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JitStatus { get; set; }

    /// <summary>
    /// Gets or sets the code address if JITted.
    /// </summary>
    [JsonPropertyName("codeAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CodeAddress { get; set; }

    /// <summary>
    /// Gets or sets the method slot number.
    /// </summary>
    [JsonPropertyName("slot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Slot { get; set; }
}

/// <summary>
/// Represents generic instantiation information for a generic type.
/// </summary>
public class GenericInstantiationInfo
{
    /// <summary>
    /// Gets or sets whether this is a generic type.
    /// </summary>
    [JsonPropertyName("isGenericType")]
    public bool IsGenericType { get; set; }

    /// <summary>
    /// Gets or sets the generic type definition name (e.g., "ConcurrentDictionary`2").
    /// </summary>
    [JsonPropertyName("typeDefinition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeDefinition { get; set; }

    /// <summary>
    /// Gets or sets the type arguments.
    /// </summary>
    [JsonPropertyName("typeArguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? TypeArguments { get; set; }
}

/// <summary>
/// Represents NativeAOT / trimming analysis information.
/// NativeAOT applications have different failure modes, especially around reflection and trimming.
/// </summary>
public class NativeAotAnalysis
{
    /// <summary>
    /// Gets or sets whether this is a NativeAOT application.
    /// </summary>
    [JsonPropertyName("isNativeAot")]
    public bool IsNativeAot { get; set; }

    /// <summary>
    /// Gets or sets whether a JIT compiler is present (NativeAOT apps don't have one).
    /// </summary>
    [JsonPropertyName("hasJitCompiler")]
    public bool HasJitCompiler { get; set; }

    /// <summary>
    /// Gets or sets the list of NativeAOT indicators found with actual evidence.
    /// </summary>
    [JsonPropertyName("indicators")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<NativeAotIndicator>? Indicators { get; set; }

    /// <summary>
    /// Gets or sets the trimming analysis if trimming issues are suspected.
    /// </summary>
    [JsonPropertyName("trimmingAnalysis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TrimmingAnalysis? TrimmingAnalysis { get; set; }

    /// <summary>
    /// Gets or sets detected reflection usage patterns that may be problematic in NativeAOT.
    /// </summary>
    [JsonPropertyName("reflectionUsage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ReflectionUsageInfo>? ReflectionUsage { get; set; }
}

/// <summary>
/// Represents a NativeAOT indicator with actual evidence data.
/// </summary>
public class NativeAotIndicator
{
    /// <summary>
    /// Gets or sets the source of this indicator (stackFrame, module, etc.).
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the pattern that was matched.
    /// </summary>
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the matched value (function name, module name, etc.).
    /// </summary>
    [JsonPropertyName("matchedValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MatchedValue { get; set; }

    /// <summary>
    /// Gets or sets the stack frame where this indicator was found (if applicable).
    /// </summary>
    [JsonPropertyName("frame")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StackFrame? Frame { get; set; }
}

/// <summary>
/// Represents analysis of potential trimming issues in NativeAOT.
/// </summary>
public class TrimmingAnalysis
{
    /// <summary>
    /// Gets or sets whether a trimming issue is suspected.
    /// </summary>
    [JsonPropertyName("potentialTrimmingIssue")]
    public bool PotentialTrimmingIssue { get; set; }

    /// <summary>
    /// Gets or sets the confidence level of this analysis.
    /// </summary>
    [JsonPropertyName("confidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Confidence { get; set; }

    /// <summary>
    /// Gets or sets the exception type that triggered the analysis.
    /// </summary>
    [JsonPropertyName("exceptionType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExceptionType { get; set; }

    /// <summary>
    /// Gets or sets the missing member that triggered the issue.
    /// </summary>
    [JsonPropertyName("missingMember")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MissingMember { get; set; }

    /// <summary>
    /// Gets or sets the actual stack frame where the call originated.
    /// </summary>
    [JsonPropertyName("callingFrame")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StackFrame? CallingFrame { get; set; }

    /// <summary>
    /// Gets or sets the recommendation to fix the trimming issue.
    /// </summary>
    [JsonPropertyName("recommendation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Recommendation { get; set; }
}

/// <summary>
/// Represents reflection usage that may be problematic in NativeAOT.
/// </summary>
public class ReflectionUsageInfo
{
    /// <summary>
    /// Gets or sets the location where reflection was detected.
    /// </summary>
    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Location { get; set; }

    /// <summary>
    /// Gets or sets the reflection pattern detected.
    /// </summary>
    [JsonPropertyName("pattern")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pattern { get; set; }

    /// <summary>
    /// Gets or sets the risk level of this reflection usage.
    /// </summary>
    [JsonPropertyName("risk")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Risk { get; set; }

    /// <summary>
    /// Gets or sets the type or method being accessed via reflection.
    /// </summary>
    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; set; }
}

// ============================================================================
// Phase 2 ClrMD Enrichment Classes
// ============================================================================

/// <summary>
/// GC heap summary from ClrMD.
/// </summary>
public class GcSummary
{
    [JsonPropertyName("heapCount")]
    public int HeapCount { get; set; }

    [JsonPropertyName("gcMode")]
    public string GcMode { get; set; } = string.Empty;

    [JsonPropertyName("isServerGC")]
    public bool IsServerGC { get; set; }

    [JsonPropertyName("totalHeapSize")]
    public long TotalHeapSize { get; set; }

    /// <summary>
    /// Fragmentation ratio (0.0 to 1.0). Only populated with deep analysis.
    /// </summary>
    [JsonPropertyName("fragmentation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Fragmentation { get; set; }

    /// <summary>
    /// Fragmentation in bytes. Only populated with deep analysis.
    /// </summary>
    [JsonPropertyName("fragmentationBytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FragmentationBytes { get; set; }

    [JsonPropertyName("generationSizes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GenerationSizes? GenerationSizes { get; set; }

    [JsonPropertyName("segments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<GcSegmentInfo>? Segments { get; set; }

    [JsonPropertyName("finalizableObjectCount")]
    public int FinalizableObjectCount { get; set; }
}

/// <summary>
/// Heap sizes per generation.
/// </summary>
public class GenerationSizes
{
    [JsonPropertyName("gen0")]
    public long Gen0 { get; set; }

    [JsonPropertyName("gen1")]
    public long Gen1 { get; set; }

    [JsonPropertyName("gen2")]
    public long Gen2 { get; set; }

    [JsonPropertyName("loh")]
    public long Loh { get; set; }

    [JsonPropertyName("poh")]
    public long Poh { get; set; }
}

/// <summary>
/// GC segment information.
/// </summary>
public class GcSegmentInfo
{
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;
}

/// <summary>
/// Additional thread info from ClrMD not available via SOS commands.
/// </summary>
public class ClrMdThreadInfo
{
    [JsonPropertyName("isGC")]
    public bool IsGC { get; set; }

    [JsonPropertyName("stackBase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StackBase { get; set; }

    [JsonPropertyName("stackLimit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StackLimit { get; set; }

    [JsonPropertyName("stackUsageBytes")]
    public long StackUsageBytes { get; set; }
}

/// <summary>
/// Top memory consumers from heap analysis.
/// </summary>
public class TopMemoryConsumers
{
    [JsonPropertyName("bySize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TypeMemoryStats>? BySize { get; set; }

    [JsonPropertyName("byCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TypeMemoryStats>? ByCount { get; set; }

    [JsonPropertyName("largeObjects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LargeObjectInfo>? LargeObjects { get; set; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HeapWalkSummary? Summary { get; set; }
}

/// <summary>
/// Memory statistics for a type.
/// </summary>
public class TypeMemoryStats
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }

    [JsonPropertyName("averageSize")]
    public long AverageSize { get; set; }

    [JsonPropertyName("largestInstance")]
    public long LargestInstance { get; set; }

    [JsonPropertyName("percentage")]
    public double Percentage { get; set; }
}

/// <summary>
/// Information about a large object.
/// </summary>
public class LargeObjectInfo
{
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("generation")]
    public string Generation { get; set; } = string.Empty;
}

/// <summary>
/// Summary of heap walk operation.
/// </summary>
public class HeapWalkSummary
{
    [JsonPropertyName("totalObjects")]
    public int TotalObjects { get; set; }

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }

    [JsonPropertyName("uniqueTypes")]
    public int UniqueTypes { get; set; }

    [JsonPropertyName("analysisTimeMs")]
    public long AnalysisTimeMs { get; set; }

    [JsonPropertyName("wasAborted")]
    public bool WasAborted { get; set; }

    /// <summary>
    /// Free space in bytes (fragmentation).
    /// </summary>
    [JsonPropertyName("freeBytes")]
    public long FreeBytes { get; set; }

    /// <summary>
    /// Fragmentation ratio (freeBytes / totalHeapSize).
    /// </summary>
    [JsonPropertyName("fragmentationRatio")]
    public double FragmentationRatio { get; set; }
}

/// <summary>
/// Async/Task analysis from heap inspection.
/// </summary>
public class AsyncAnalysis
{
    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AsyncSummary? Summary { get; set; }

    [JsonPropertyName("faultedTasks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FaultedTaskInfo>? FaultedTasks { get; set; }

    [JsonPropertyName("pendingStateMachines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StateMachineInfo>? PendingStateMachines { get; set; }

    [JsonPropertyName("analysisTimeMs")]
    public long AnalysisTimeMs { get; set; }

    [JsonPropertyName("wasAborted")]
    public bool WasAborted { get; set; }
}

/// <summary>
/// Summary of async/task analysis.
/// </summary>
public class AsyncSummary
{
    [JsonPropertyName("totalTasks")]
    public int TotalTasks { get; set; }

    [JsonPropertyName("pendingTasks")]
    public int PendingTasks { get; set; }

    [JsonPropertyName("completedTasks")]
    public int CompletedTasks { get; set; }

    [JsonPropertyName("faultedTasks")]
    public int FaultedTasks { get; set; }

    [JsonPropertyName("canceledTasks")]
    public int CanceledTasks { get; set; }
}

/// <summary>
/// Information about a faulted task.
/// </summary>
public class FaultedTaskInfo
{
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("taskType")]
    public string TaskType { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("exceptionType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExceptionType { get; set; }

    [JsonPropertyName("exceptionMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExceptionMessage { get; set; }
}

/// <summary>
/// Information about an async state machine.
/// </summary>
public class StateMachineInfo
{
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("stateMachineType")]
    public string StateMachineType { get; set; } = string.Empty;

    [JsonPropertyName("currentState")]
    public int CurrentState { get; set; }
    
    /// <summary>
    /// Human-readable description of the current state.
    /// </summary>
    [JsonPropertyName("stateDescription")]
    public string StateDescription { get; set; } = string.Empty;
}

/// <summary>
/// String duplicate analysis from heap inspection.
/// </summary>
public class StringAnalysis
{
    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StringAnalysisSummary? Summary { get; set; }

    [JsonPropertyName("topDuplicates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StringDuplicateInfo>? TopDuplicates { get; set; }

    [JsonPropertyName("byLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StringLengthDistribution? ByLength { get; set; }

    [JsonPropertyName("analysisTimeMs")]
    public long AnalysisTimeMs { get; set; }

    [JsonPropertyName("wasAborted")]
    public bool WasAborted { get; set; }
}

/// <summary>
/// Summary of string analysis.
/// </summary>
public class StringAnalysisSummary
{
    [JsonPropertyName("totalStrings")]
    public int TotalStrings { get; set; }

    [JsonPropertyName("uniqueStrings")]
    public int UniqueStrings { get; set; }

    [JsonPropertyName("duplicateStrings")]
    public int DuplicateStrings { get; set; }

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }

    [JsonPropertyName("wastedSize")]
    public long WastedSize { get; set; }

    [JsonPropertyName("wastedPercentage")]
    public double WastedPercentage { get; set; }
}

/// <summary>
/// Information about a duplicated string.
/// </summary>
public class StringDuplicateInfo
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("sizePerInstance")]
    public long SizePerInstance { get; set; }

    [JsonPropertyName("wastedBytes")]
    public long WastedBytes { get; set; }

    [JsonPropertyName("suggestion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Suggestion { get; set; }
}

/// <summary>
/// Distribution of strings by length.
/// </summary>
public class StringLengthDistribution
{
    [JsonPropertyName("empty")]
    public int Empty { get; set; }

    [JsonPropertyName("short")]
    public int Short { get; set; }

    [JsonPropertyName("medium")]
    public int Medium { get; set; }

    [JsonPropertyName("long")]
    public int Long { get; set; }

    [JsonPropertyName("veryLong")]
    public int VeryLong { get; set; }
}

/// <summary>
/// Combined result from single-pass heap analysis (optimization).
/// </summary>
public class CombinedHeapAnalysis
{
    [JsonPropertyName("topMemoryConsumers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TopMemoryConsumers? TopMemoryConsumers { get; set; }

    [JsonPropertyName("asyncAnalysis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AsyncAnalysis? AsyncAnalysis { get; set; }

    [JsonPropertyName("stringAnalysis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StringAnalysis? StringAnalysis { get; set; }

    /// <summary>
    /// Free space in bytes (for fragmentation calculation).
    /// </summary>
    [JsonPropertyName("freeBytes")]
    public long FreeBytes { get; set; }

    /// <summary>
    /// Fragmentation ratio.
    /// </summary>
    [JsonPropertyName("fragmentationRatio")]
    public double FragmentationRatio { get; set; }

    /// <summary>
    /// Total analysis time in milliseconds.
    /// </summary>
    [JsonPropertyName("totalAnalysisTimeMs")]
    public long TotalAnalysisTimeMs { get; set; }

    /// <summary>
    /// Whether parallel processing was used.
    /// </summary>
    [JsonPropertyName("usedParallel")]
    public bool UsedParallel { get; set; }

    /// <summary>
    /// Number of segments processed.
    /// </summary>
    [JsonPropertyName("segmentsProcessed")]
    public int SegmentsProcessed { get; set; }
}

// ============================================================================
// NEW HIERARCHICAL STRUCTURE CLASSES (Phase 1)
// These classes will replace the flat structure incrementally
// ============================================================================


/// <summary>
/// Report metadata information for the new hierarchical structure.
/// Named CrashReportMetadata to avoid conflict with DebuggerMcp.Reporting.ReportMetadata.
/// </summary>
public class CrashReportMetadata
{
    /// <summary>
    /// Unique identifier for the dump file.
    /// </summary>
    [JsonPropertyName("dumpId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DumpId { get; set; }

    /// <summary>
    /// User who uploaded/owns the dump.
    /// </summary>
    [JsonPropertyName("userId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserId { get; set; }

    /// <summary>
    /// When the report was generated (ISO8601).
    /// </summary>
    [JsonPropertyName("generatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeneratedAt { get; set; }

    /// <summary>
    /// Report format (Json, Markdown, Html).
    /// </summary>
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; set; }

    /// <summary>
    /// Debugger type used (WinDbg, LLDB).
    /// </summary>
    [JsonPropertyName("debuggerType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DebuggerType { get; set; }

    /// <summary>
    /// Server version that generated this report.
    /// </summary>
    [JsonPropertyName("serverVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerVersion { get; set; }
}

/// <summary>
/// High-level analysis summary for quick triage.
/// </summary>
public class AnalysisSummary
{
    /// <summary>
    /// Type of crash detected.
    /// </summary>
    [JsonPropertyName("crashType")]
    public string CrashType { get; set; } = "Unknown";

    /// <summary>
    /// Short human-readable description.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// Severity level (critical, high, medium, low, info).
    /// </summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "info";

    /// <summary>
    /// Total thread count.
    /// </summary>
    [JsonPropertyName("threadCount")]
    public int ThreadCount { get; set; }

    /// <summary>
    /// Total module count.
    /// </summary>
    [JsonPropertyName("moduleCount")]
    public int ModuleCount { get; set; }

    /// <summary>
    /// Total assembly count.
    /// </summary>
    [JsonPropertyName("assemblyCount")]
    public int AssemblyCount { get; set; }

    /// <summary>
    /// Recommendations for fixing the issue.
    /// </summary>
    [JsonPropertyName("recommendations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Recommendations { get; set; }

    /// <summary>
    /// Analysis warnings.
    /// </summary>
    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Warnings { get; set; }

    /// <summary>
    /// Analysis errors.
    /// </summary>
    [JsonPropertyName("errors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Errors { get; set; }
}

/// <summary>
/// Detailed exception information (lifted to top level for prominence).
/// </summary>
public class ExceptionDetails
{
    /// <summary>
    /// Full exception type name.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Exception message.
    /// </summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    /// <summary>
    /// HResult code (hex).
    /// </summary>
    [JsonPropertyName("hResult")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HResult { get; set; }

    /// <summary>
    /// Exception address.
    /// </summary>
    [JsonPropertyName("address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Address { get; set; }

    /// <summary>
    /// Whether the exception has an inner exception.
    /// </summary>
    [JsonPropertyName("hasInnerException")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasInnerException { get; set; }

    /// <summary>
    /// Count of nested exceptions.
    /// </summary>
    [JsonPropertyName("nestedExceptionCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NestedExceptionCount { get; set; }

    /// <summary>
    /// Exception stack trace.
    /// </summary>
    [JsonPropertyName("stackTrace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StackFrame>? StackTrace { get; set; }

    /// <summary>
    /// Detailed exception analysis.
    /// </summary>
    [JsonPropertyName("analysis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExceptionAnalysis? Analysis { get; set; }
}

/// <summary>
/// Environment information grouping platform, runtime, and process details.
/// </summary>
public class EnvironmentInfo
{
    /// <summary>
    /// Platform information (OS, architecture, distribution).
    /// </summary>
    [JsonPropertyName("platform")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlatformInfo? Platform { get; set; }

    /// <summary>
    /// Runtime information (.NET version, type).
    /// </summary>
    [JsonPropertyName("runtime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeInfo? Runtime { get; set; }

    /// <summary>
    /// Process information (arguments, environment variables).
    /// </summary>
    [JsonPropertyName("process")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProcessInfo? Process { get; set; }

    /// <summary>
    /// Crash diagnostic info (signal, fault address).
    /// </summary>
    [JsonPropertyName("crashInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CrashDiagnosticInfo? CrashInfo { get; set; }

    /// <summary>
    /// Native AOT analysis.
    /// </summary>
    [JsonPropertyName("nativeAot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NativeAotAnalysis? NativeAot { get; set; }
}

/// <summary>
/// Runtime details.
/// </summary>
public class RuntimeInfo
{
    /// <summary>
    /// Runtime type (CoreCLR, Framework, Mono, NativeAOT).
    /// </summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    /// <summary>
    /// Runtime version.
    /// </summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    /// <summary>
    /// CLR version string.
    /// </summary>
    [JsonPropertyName("clrVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClrVersion { get; set; }

    /// <summary>
    /// Whether the runtime is hosted.
    /// </summary>
    [JsonPropertyName("isHosted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsHosted { get; set; }
}

/// <summary>
/// Thread information grouping.
/// </summary>
public class ThreadsInfo
{
    /// <summary>
    /// Thread count summary.
    /// </summary>
    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThreadSummary? Summary { get; set; }

    /// <summary>
    /// The faulting thread (if identified).
    /// </summary>
    [JsonPropertyName("faultingThread")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThreadInfo? FaultingThread { get; set; }

    /// <summary>
    /// All threads in the dump.
    /// </summary>
    [JsonPropertyName("all")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ThreadInfo>? All { get; set; }

    /// <summary>
    /// Thread pool information.
    /// </summary>
    [JsonPropertyName("threadPool")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThreadPoolInfo? ThreadPool { get; set; }

    /// <summary>
    /// Operating system thread count as reported by the debugger thread list.
    /// This may differ from CLR thread statistics (e.g., !clrthreads ThreadCount).
    /// </summary>
    [JsonPropertyName("osThreadCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OsThreadCount { get; set; }

    /// <summary>
    /// Deadlock information.
    /// </summary>
    [JsonPropertyName("deadlock")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeadlockInfo? Deadlock { get; set; }
}

/// <summary>
/// Thread count summary.
/// </summary>
public class ThreadSummary
{
    /// <summary>
    /// Total thread count.
    /// </summary>
    [JsonPropertyName("total")]
    public int Total { get; set; }

    /// <summary>
    /// Foreground thread count.
    /// This is derived from CLR thread statistics when available.
    /// </summary>
    [JsonPropertyName("foreground")]
    public int Foreground { get; set; }

    /// <summary>
    /// Background thread count.
    /// </summary>
    [JsonPropertyName("background")]
    public int Background { get; set; }

    /// <summary>
    /// Unstarted thread count.
    /// </summary>
    [JsonPropertyName("unstarted")]
    public int Unstarted { get; set; }

    /// <summary>
    /// Dead thread count.
    /// </summary>
    [JsonPropertyName("dead")]
    public int Dead { get; set; }

    /// <summary>
    /// Pending thread count.
    /// </summary>
    [JsonPropertyName("pending")]
    public int Pending { get; set; }

    /// <summary>
    /// Finalizer queue length.
    /// </summary>
    [JsonPropertyName("finalizerQueueLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FinalizerQueueLength { get; set; }
}

/// <summary>
/// Memory information grouping.
/// </summary>
public class MemoryInfo
{
    /// <summary>
    /// GC heap information.
    /// </summary>
    [JsonPropertyName("gc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GcSummary? Gc { get; set; }

    /// <summary>
    /// Heap statistics by type.
    /// </summary>
    [JsonPropertyName("heapStats")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, long>? HeapStats { get; set; }

    /// <summary>
    /// Top memory consumers.
    /// </summary>
    [JsonPropertyName("topConsumers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TopMemoryConsumers? TopConsumers { get; set; }

    /// <summary>
    /// String analysis.
    /// </summary>
    [JsonPropertyName("strings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StringAnalysis? Strings { get; set; }

    /// <summary>
    /// Memory leak analysis.
    /// </summary>
    [JsonPropertyName("leakAnalysis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LeakAnalysis? LeakAnalysis { get; set; }

    /// <summary>
    /// OOM analysis.
    /// </summary>
    [JsonPropertyName("oom")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OomAnalysisInfo? Oom { get; set; }
}

/// <summary>
/// Memory leak analysis result.
/// </summary>
public class LeakAnalysis
{
    /// <summary>
    /// Whether leaks were detected.
    /// </summary>
    [JsonPropertyName("detected")]
    public bool Detected { get; set; }

    /// <summary>
    /// Leak severity.
    /// </summary>
    [JsonPropertyName("severity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Severity { get; set; }

    /// <summary>
    /// Total heap size in bytes.
    /// </summary>
    [JsonPropertyName("totalHeapBytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TotalHeapBytes { get; set; }

    /// <summary>
    /// Top memory consumers (suspected leaks).
    /// </summary>
    [JsonPropertyName("topConsumers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MemoryConsumer>? TopConsumers { get; set; }

    /// <summary>
    /// Potential issue indicators.
    /// </summary>
    [JsonPropertyName("potentialIssueIndicators")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? PotentialIssueIndicators { get; set; }
}

/// <summary>
/// Assembly information grouping.
/// </summary>
public class AssembliesInfo
{
    /// <summary>
    /// Total assembly count.
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>
    /// Assembly list.
    /// </summary>
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AssemblyVersionInfo>? Items { get; set; }
}

/// <summary>
/// Async/Task information grouping.
/// </summary>
public class AsyncInfo
{
    /// <summary>
    /// Whether async deadlock was detected.
    /// </summary>
    [JsonPropertyName("hasDeadlock")]
    public bool HasDeadlock { get; set; }

    /// <summary>
    /// Task summary counts.
    /// </summary>
    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AsyncSummary? Summary { get; set; }

    /// <summary>
    /// Pending state machines.
    /// </summary>
    [JsonPropertyName("stateMachines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StateMachineInfo>? StateMachines { get; set; }

    /// <summary>
    /// Faulted tasks.
    /// </summary>
    [JsonPropertyName("faultedTasks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FaultedTaskInfo>? FaultedTasks { get; set; }

    /// <summary>
    /// Active timers.
    /// </summary>
    [JsonPropertyName("timers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TimerInfo>? Timers { get; set; }

    /// <summary>
    /// Analysis duration in milliseconds.
    /// </summary>
    [JsonPropertyName("analysisTimeMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AnalysisTimeMs { get; set; }

    /// <summary>
    /// Whether analysis was aborted.
    /// </summary>
    [JsonPropertyName("wasAborted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WasAborted { get; set; }
}

/// <summary>
/// Security analysis information.
/// </summary>
public class SecurityInfo
{
    /// <summary>
    /// Whether vulnerabilities were detected.
    /// </summary>
    [JsonPropertyName("hasVulnerabilities")]
    public bool HasVulnerabilities { get; set; }

    /// <summary>
    /// Overall risk level.
    /// </summary>
    [JsonPropertyName("overallRisk")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OverallRisk { get; set; }

    /// <summary>
    /// Security analysis summary.
    /// </summary>
    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    /// <summary>
    /// When analysis was performed (ISO8601).
    /// </summary>
    [JsonPropertyName("analyzedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AnalyzedAt { get; set; }

    /// <summary>
    /// Security findings.
    /// </summary>
    [JsonPropertyName("findings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SecurityFinding>? Findings { get; set; }

    /// <summary>
    /// Security recommendations.
    /// </summary>
    [JsonPropertyName("recommendations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Recommendations { get; set; }
}

/// <summary>
/// Individual security finding.
/// </summary>
public class SecurityFinding
{
    /// <summary>
    /// Finding type.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Finding severity.
    /// </summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    /// <summary>
    /// Finding description.
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Location in code/memory.
    /// </summary>
    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Location { get; set; }

    /// <summary>
    /// Recommended fix.
    /// </summary>
    [JsonPropertyName("recommendation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Recommendation { get; set; }
}
