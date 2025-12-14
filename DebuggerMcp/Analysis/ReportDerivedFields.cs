using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Deterministic signature information for crash report deduplication.
/// </summary>
public class AnalysisSignature
{
    /// <summary>
    /// Schema version for the signature algorithm and payload.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// High-level classification for the report (crash, hang, oom, unknown).
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "unknown";

    /// <summary>
    /// SHA-256 hash of the normalized signature payload.
    /// </summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable components used to generate <see cref="Hash"/>.
    /// </summary>
    [JsonPropertyName("parts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnalysisSignatureParts? Parts { get; set; }
}

/// <summary>
/// Components that contribute to the deterministic crash signature.
/// </summary>
public class AnalysisSignatureParts
{
    /// <summary>
    /// Managed exception type (if present).
    /// </summary>
    [JsonPropertyName("exceptionType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExceptionType { get; set; }

    /// <summary>
    /// Native signal name (if present).
    /// </summary>
    [JsonPropertyName("signalName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignalName { get; set; }

    /// <summary>
    /// Selected meaningful top frames (normalized display strings).
    /// </summary>
    [JsonPropertyName("meaningfulTopFrames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? MeaningfulTopFrames { get; set; }

    /// <summary>
    /// Runtime identity string (best-effort).
    /// </summary>
    [JsonPropertyName("runtime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Runtime { get; set; }

    /// <summary>
    /// Operating system string (best-effort).
    /// </summary>
    [JsonPropertyName("os")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Os { get; set; }
}

/// <summary>
/// Explains deterministic per-thread stack frame selection.
/// </summary>
public class StackSelectionInfo
{
    /// <summary>
    /// Schema version for selection semantics.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Per-thread selection information.
    /// </summary>
    [JsonPropertyName("threadSelections")]
    public List<ThreadStackSelection> ThreadSelections { get; set; } = [];
}

/// <summary>
/// Per-thread information about which frame was selected as the "meaningful top frame".
/// </summary>
public class ThreadStackSelection
{
    /// <summary>
    /// Thread ID string (as present in the report).
    /// </summary>
    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = string.Empty;

    /// <summary>
    /// Index of the selected frame within the thread call stack.
    /// </summary>
    [JsonPropertyName("selectedFrameIndex")]
    public int? SelectedFrameIndex { get; set; }

    /// <summary>
    /// Frame skip reasons for transparency/debuggability.
    /// </summary>
    [JsonPropertyName("skippedFrames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SkippedFrame>? SkippedFrames { get; set; }
}

/// <summary>
/// Explains why a frame was skipped during top-frame selection.
/// </summary>
public class SkippedFrame
{
    /// <summary>
    /// Index of the skipped frame within the call stack.
    /// </summary>
    [JsonPropertyName("frameIndex")]
    public int FrameIndex { get; set; }

    /// <summary>
    /// Machine-readable reason code.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// A structured finding produced from the analysis result with evidence pointers for auditability.
/// </summary>
public class AnalysisFinding
{
    /// <summary>
    /// Stable identifier for the finding (suitable for automation).
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable title.
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Category (crash, hang, oom, deadlock, perf, symbols, etc.).
    /// </summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Severity (info, warning, error).
    /// </summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "info";

    /// <summary>
    /// Confidence in [0..1].
    /// </summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>
    /// Short summary of the finding.
    /// </summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Evidence pointers into the JSON report.
    /// </summary>
    [JsonPropertyName("evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AnalysisEvidence>? Evidence { get; set; }

    /// <summary>
    /// Suggested next actions.
    /// </summary>
    [JsonPropertyName("nextActions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? NextActions { get; set; }
}

/// <summary>
/// Evidence entry that points to a location in the JSON report.
/// </summary>
public class AnalysisEvidence
{
    /// <summary>
    /// JSON pointer to relevant data (RFC 6901 style).
    /// </summary>
    [JsonPropertyName("jsonPointer")]
    public string JsonPointer { get; set; } = string.Empty;

    /// <summary>
    /// Short note describing why the pointer is relevant.
    /// </summary>
    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}

/// <summary>
/// Root cause analysis represented as hypotheses, not a single asserted truth.
/// </summary>
public class RootCauseAnalysis
{
    /// <summary>
    /// Ordered hypotheses with confidence and evidence.
    /// </summary>
    [JsonPropertyName("hypotheses")]
    public List<RootCauseHypothesis> Hypotheses { get; set; } = [];
}

/// <summary>
/// A single root cause hypothesis.
/// </summary>
public class RootCauseHypothesis
{
    /// <summary>
    /// Short label for the hypothesis.
    /// </summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Confidence in [0..1].
    /// </summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>
    /// Evidence pointers supporting the hypothesis.
    /// </summary>
    [JsonPropertyName("evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AnalysisEvidence>? Evidence { get; set; }

    /// <summary>
    /// Evidence pointers that may contradict the hypothesis.
    /// </summary>
    [JsonPropertyName("counterEvidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AnalysisEvidence>? CounterEvidence { get; set; }
}

/// <summary>
/// Summary of symbol and source resolution health across the report.
/// </summary>
public class SymbolsHealthSummary
{
    /// <summary>
    /// Native symbol coverage.
    /// </summary>
    [JsonPropertyName("native")]
    public SymbolsModuleSummary Native { get; set; } = new();

    /// <summary>
    /// Managed symbol coverage (best-effort, inferred from frames).
    /// </summary>
    [JsonPropertyName("managed")]
    public ManagedSymbolsSummary Managed { get; set; } = new();

    /// <summary>
    /// Source link resolution coverage (frame-based).
    /// </summary>
    [JsonPropertyName("sourcelink")]
    public SourceLinkHealthSummary SourceLink { get; set; } = new();
}

/// <summary>
/// Best-effort managed symbol coverage summary.
/// </summary>
public class ManagedSymbolsSummary
{
    /// <summary>
    /// Count of managed frames that have file/line metadata but no source URL (often indicates missing SourceLink/PDB data).
    /// </summary>
    [JsonPropertyName("pdbMissingCount")]
    public int PdbMissingCount { get; set; }

    /// <summary>
    /// Example managed module names where source resolution is missing.
    /// </summary>
    [JsonPropertyName("examples")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Examples { get; set; }
}

/// <summary>
/// Summary of module symbol availability.
/// </summary>
public class SymbolsModuleSummary
{
    /// <summary>
    /// Count of missing-symbol modules.
    /// </summary>
    [JsonPropertyName("missingCount")]
    public int MissingCount { get; set; }

    /// <summary>
    /// Example module names.
    /// </summary>
    [JsonPropertyName("examples")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Examples { get; set; }
}

/// <summary>
/// Frame-level Source Link resolution summary.
/// </summary>
public class SourceLinkHealthSummary
{
    /// <summary>
    /// Count of frames with a resolved source URL.
    /// </summary>
    [JsonPropertyName("resolvedCount")]
    public int ResolvedCount { get; set; }

    /// <summary>
    /// Count of frames that have source file info but no source URL.
    /// </summary>
    [JsonPropertyName("unresolvedCount")]
    public int UnresolvedCount { get; set; }
}

/// <summary>
/// A snapshot timeline summarizing thread activity and best-effort wait relationships.
/// </summary>
public class AnalysisTimeline
{
    /// <summary>
    /// Schema version for timeline semantics.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Timeline kind (currently "snapshot").
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "snapshot";

    /// <summary>
    /// When the report was generated (UTC, ISO 8601) when known.
    /// </summary>
    [JsonPropertyName("capturedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CapturedAtUtc { get; set; }

    /// <summary>
    /// Capture reason (best-effort, e.g. SIGSTOP, signal, exception).
    /// </summary>
    [JsonPropertyName("captureReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CaptureReason { get; set; }

    /// <summary>
    /// Per-thread snapshot information.
    /// </summary>
    [JsonPropertyName("threads")]
    public List<TimelineThread> Threads { get; set; } = [];

    /// <summary>
    /// Best-effort blocked chains derived from synchronization graphs when available.
    /// </summary>
    [JsonPropertyName("blockedChains")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TimelineBlockedChain>? BlockedChains { get; set; }

    /// <summary>
    /// Best-effort deadlock cycles when detected.
    /// </summary>
    [JsonPropertyName("deadlocks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TimelineDeadlock>? Deadlocks { get; set; }
}

/// <summary>
/// Thread snapshot entry for the timeline.
/// </summary>
public class TimelineThread
{
    /// <summary>
    /// Thread ID string (as present in the report).
    /// </summary>
    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = string.Empty;

    /// <summary>
    /// OS thread id string (best-effort).
    /// </summary>
    [JsonPropertyName("osThreadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OsThreadId { get; set; }

    /// <summary>
    /// Whether this thread is the faulting thread.
    /// </summary>
    [JsonPropertyName("isFaulting")]
    public bool IsFaulting { get; set; }

    /// <summary>
    /// Thread state string as reported by the debugger.
    /// </summary>
    [JsonPropertyName("state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? State { get; set; }

    /// <summary>
    /// Activity classification (waiting, running, unknown).
    /// </summary>
    [JsonPropertyName("activity")]
    public string Activity { get; set; } = "unknown";

    /// <summary>
    /// Selected top frame used for classification.
    /// </summary>
    [JsonPropertyName("topFrame")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimelineTopFrame? TopFrame { get; set; }

    /// <summary>
    /// Wait classification details (best-effort).
    /// </summary>
    [JsonPropertyName("wait")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimelineWaitInfo? Wait { get; set; }
}

/// <summary>
/// Top frame used in the timeline for a thread.
/// </summary>
public class TimelineTopFrame
{
    /// <summary>
    /// Index of the frame within the call stack.
    /// </summary>
    [JsonPropertyName("frameIndex")]
    public int FrameIndex { get; set; }

    /// <summary>
    /// Function name.
    /// </summary>
    [JsonPropertyName("function")]
    public string Function { get; set; } = string.Empty;

    /// <summary>
    /// Module name.
    /// </summary>
    [JsonPropertyName("module")]
    public string Module { get; set; } = string.Empty;
}

/// <summary>
/// Best-effort wait classification.
/// </summary>
public class TimelineWaitInfo
{
    /// <summary>
    /// Wait kind (monitor, lock, event, join, sleep, io, native-syscall, unknown).
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "unknown";

    /// <summary>
    /// Owner thread identifier when derivable.
    /// </summary>
    [JsonPropertyName("ownerThreadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerThreadId { get; set; }

    /// <summary>
    /// Evidence pointers supporting the wait classification.
    /// </summary>
    [JsonPropertyName("evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AnalysisEvidence>? Evidence { get; set; }
}

/// <summary>
/// A best-effort blocked chain.
/// </summary>
public class TimelineBlockedChain
{
    /// <summary>
    /// Root thread identifier for the chain.
    /// </summary>
    [JsonPropertyName("rootThreadId")]
    public string RootThreadId { get; set; } = string.Empty;

    /// <summary>
    /// Thread identifier chain.
    /// </summary>
    [JsonPropertyName("chain")]
    public List<string> Chain { get; set; } = [];

    /// <summary>
    /// Reason/category for the dependency chain.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Confidence in [0..1].
    /// </summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>
    /// Evidence pointers supporting the chain.
    /// </summary>
    [JsonPropertyName("evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AnalysisEvidence>? Evidence { get; set; }
}

/// <summary>
/// A best-effort deadlock cycle representation.
/// </summary>
public class TimelineDeadlock
{
    /// <summary>
    /// Threads involved in the deadlock.
    /// </summary>
    [JsonPropertyName("threads")]
    public List<string> Threads { get; set; } = [];

    /// <summary>
    /// Deadlock kind (monitor-cycle, waitgraph-cycle, unknown).
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "unknown";

    /// <summary>
    /// Confidence in [0..1].
    /// </summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>
    /// Evidence pointers supporting the deadlock.
    /// </summary>
    [JsonPropertyName("evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AnalysisEvidence>? Evidence { get; set; }
}

/// <summary>
/// Bounded source snippet entry for a selected frame.
/// </summary>
public class SourceContextEntry
{
    /// <summary>
    /// Thread ID in which the frame appears.
    /// </summary>
    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = string.Empty;

    /// <summary>
    /// Frame number within the thread call stack.
    /// </summary>
    [JsonPropertyName("frameNumber")]
    public int FrameNumber { get; set; }

    /// <summary>
    /// Function name for display.
    /// </summary>
    [JsonPropertyName("function")]
    public string Function { get; set; } = string.Empty;

    /// <summary>
    /// Module name for display.
    /// </summary>
    [JsonPropertyName("module")]
    public string Module { get; set; } = string.Empty;

    /// <summary>
    /// Source file path (as reported by the debugger).
    /// </summary>
    [JsonPropertyName("sourceFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFile { get; set; }

    /// <summary>
    /// Line number within <see cref="SourceFile"/>.
    /// </summary>
    [JsonPropertyName("lineNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LineNumber { get; set; }

    /// <summary>
    /// Browsable source URL when available.
    /// </summary>
    [JsonPropertyName("sourceUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; set; }

    /// <summary>
    /// Raw content URL when available.
    /// </summary>
    [JsonPropertyName("sourceRawUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceRawUrl { get; set; }

    /// <summary>
    /// Resolution status (local, remote, unavailable, redacted, error).
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "unavailable";

    /// <summary>
    /// First line number included in <see cref="Lines"/>.
    /// </summary>
    [JsonPropertyName("startLine")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StartLine { get; set; }

    /// <summary>
    /// Last line number included in <see cref="Lines"/>.
    /// </summary>
    [JsonPropertyName("endLine")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EndLine { get; set; }

    /// <summary>
    /// Source lines included in the context window.
    /// </summary>
    [JsonPropertyName("lines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Lines { get; set; }

    /// <summary>
    /// Error message when status is error.
    /// </summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}
