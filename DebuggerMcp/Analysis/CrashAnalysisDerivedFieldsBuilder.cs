using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DebuggerMcp.Analysis.Synchronization;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Computes derived fields for <see cref="CrashAnalysisResult"/> from finalized stacks and metadata.
/// </summary>
internal static class CrashAnalysisDerivedFieldsBuilder
{
    private const string JsonPointerAnalysisBase = "/analysis";

    internal static void PopulateDerivedFields(CrashAnalysisResult result)
    {
        if (result.Threads?.All == null)
        {
            return;
        }

        result.StackSelection = BuildStackSelection(result);
        result.Signature = BuildSignature(result);
        result.Symbols = BuildSymbols(result);
        result.Timeline = BuildTimeline(result);
        result.Findings = BuildFindings(result);
        result.RootCause = BuildRootCause(result);
    }

    private static StackSelectionInfo BuildStackSelection(CrashAnalysisResult result)
    {
        var selections = new List<ThreadStackSelection>(result.Threads!.All!.Count);

        foreach (var thread in result.Threads.All)
        {
            var selection = StackFrameSelection.SelectMeaningfulTopFrame(thread.CallStack);
            selections.Add(new ThreadStackSelection
            {
                ThreadId = thread.ThreadId ?? string.Empty,
                SelectedFrameIndex = selection.SelectedFrameIndex,
                SkippedFrames = selection.SkippedFrames.Count > 0 ? selection.SkippedFrames : null
            });
        }

        return new StackSelectionInfo { ThreadSelections = selections };
    }

    private static AnalysisSignature BuildSignature(CrashAnalysisResult result)
    {
        var parts = new AnalysisSignatureParts
        {
            ExceptionType = result.Exception?.Type,
            SignalName = result.Environment?.CrashInfo?.SignalName,
            Runtime = result.Environment?.Runtime?.Version,
            Os = result.Environment?.Platform?.Os
        };

        var topFrames = new List<string>();
        var faulting = result.Threads?.All?.FirstOrDefault(t => t.IsFaulting);
        if (faulting != null)
        {
            var selection = StackFrameSelection.SelectMeaningfulTopFrame(faulting.CallStack);
            if (selection.SelectedFrameIndex.HasValue)
            {
                topFrames.Add(StackFrameSelection.FormatNormalizedFrame(faulting.CallStack[selection.SelectedFrameIndex.Value]));
            }

            // Add a couple more meaningful frames for improved stability.
            foreach (var frame in faulting.CallStack)
            {
                if (topFrames.Count >= 3)
                {
                    break;
                }

                if (StackFrameSelection.IsMeaningfulTopFrameCandidate(frame))
                {
                    var normalized = StackFrameSelection.FormatNormalizedFrame(frame);
                    if (!topFrames.Contains(normalized, StringComparer.Ordinal))
                    {
                        topFrames.Add(normalized);
                    }
                }
            }
        }

        if (topFrames.Count > 0)
        {
            parts.MeaningfulTopFrames = topFrames;
        }

        var kind = ClassifyKind(result);

        // Use '\n' explicitly so the signature is stable across OSes (AppendLine uses Environment.NewLine).
        var payload = new StringBuilder();
        payload.Append($"v=1\n");
        payload.Append($"kind={kind}\n");
        payload.Append($"exception={parts.ExceptionType ?? string.Empty}\n");
        payload.Append($"signal={parts.SignalName ?? string.Empty}\n");
        payload.Append($"runtime={parts.Runtime ?? string.Empty}\n");
        payload.Append($"os={parts.Os ?? string.Empty}\n");
        if (parts.MeaningfulTopFrames != null)
        {
            foreach (var f in parts.MeaningfulTopFrames)
            {
                payload.Append($"frame={f}\n");
            }
        }

        var hash = ComputeSha256Hex(payload.ToString());

        return new AnalysisSignature
        {
            Version = 1,
            Kind = kind,
            Hash = $"sha256:{hash}",
            Parts = parts
        };
    }

    private static string ClassifyKind(CrashAnalysisResult result)
    {
        if (result.Memory?.Oom?.Detected == true)
        {
            return "oom";
        }

        if (!string.IsNullOrWhiteSpace(result.Environment?.CrashInfo?.SignalName) ||
            !string.IsNullOrWhiteSpace(result.Exception?.Type))
        {
            return "crash";
        }

        var faulting = result.Threads?.All?.FirstOrDefault(t => t.IsFaulting);
        if (faulting?.State != null &&
            faulting.State.Contains("SIGSTOP", StringComparison.OrdinalIgnoreCase))
        {
            return "hang";
        }

        return "unknown";
    }

    private static SymbolsHealthSummary BuildSymbols(CrashAnalysisResult result)
    {
        var nativeMissing = result.Modules?.Where(m => m.HasSymbols == false)
            .Select(m => m.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        var nativeExamples = nativeMissing
            .Where(n => !n.StartsWith("[", StringComparison.Ordinal))
            .Take(5)
            .ToList();

        var managedMissingCount = 0;
        var managedMissingModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var thread in result.Threads!.All!)
        {
            foreach (var frame in thread.CallStack)
            {
                if (!frame.IsManaged)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(frame.SourceFile) || (frame.LineNumber ?? 0) <= 0)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(frame.SourceUrl))
                {
                    managedMissingCount++;
                    if (!string.IsNullOrWhiteSpace(frame.Module))
                    {
                        managedMissingModules.Add(frame.Module);
                    }
                }
            }
        }

        var (resolved, unresolved) = CountSourceLinkHealth(result);

        return new SymbolsHealthSummary
        {
            Native = new SymbolsModuleSummary
            {
                MissingCount = nativeMissing.Count,
                Examples = nativeExamples.Count > 0 ? nativeExamples : null
            },
            Managed = new ManagedSymbolsSummary
            {
                PdbMissingCount = managedMissingCount,
                Examples = managedMissingModules.Count > 0 ? managedMissingModules.Take(5).ToList() : null
            },
            SourceLink = new SourceLinkHealthSummary
            {
                ResolvedCount = resolved,
                UnresolvedCount = unresolved
            }
        };
    }

    private static (int resolved, int unresolved) CountSourceLinkHealth(CrashAnalysisResult result)
    {
        var resolved = 0;
        var unresolved = 0;

        foreach (var thread in result.Threads!.All!)
        {
            foreach (var frame in thread.CallStack)
            {
                if (!string.IsNullOrWhiteSpace(frame.SourceUrl))
                {
                    resolved++;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(frame.SourceFile) && (frame.LineNumber ?? 0) > 0)
                {
                    unresolved++;
                }
            }
        }

        return (resolved, unresolved);
    }

    private static AnalysisTimeline BuildTimeline(CrashAnalysisResult result)
    {
        var timeline = new AnalysisTimeline
        {
            Version = 1,
            Kind = "snapshot",
            CaptureReason = InferCaptureReason(result)
        };

        // Cap timeline threads to avoid very large reports in pathological cases.
        var threads = result.Threads!.All!;
        for (var threadIndex = 0; threadIndex < threads.Count && threadIndex < 200; threadIndex++)
        {
            var thread = threads[threadIndex];
            var selection = StackFrameSelection.SelectMeaningfulTopFrame(thread.CallStack);
            TimelineTopFrame? topFrame = null;
            TimelineWaitInfo? wait = null;
            var activity = "unknown";

            if (selection.SelectedFrameIndex is int idx && idx >= 0 && idx < thread.CallStack.Count)
            {
                var frame = thread.CallStack[idx];
                topFrame = new TimelineTopFrame { FrameIndex = idx, Function = frame.Function, Module = frame.Module };
                var waitKind = ClassifyWaitKind(frame);
                if (waitKind != null)
                {
                    activity = "waiting";
                    wait = new TimelineWaitInfo
                    {
                        Kind = waitKind,
                        Evidence = new List<AnalysisEvidence>
                        {
                                    new()
                                    {
                                        JsonPointer = $"{JsonPointerAnalysisBase}/threads/all/{threadIndex}/callStack/{idx}",
                                        Note = "Top frame classified as wait-like"
                                    }
                                }
                            };
                }
                else
                {
                    activity = "running";
                }
            }

            timeline.Threads.Add(new TimelineThread
            {
                ThreadId = thread.ThreadId ?? string.Empty,
                OsThreadId = thread.OsThreadId,
                IsFaulting = thread.IsFaulting,
                State = thread.State,
                Activity = activity,
                TopFrame = topFrame,
                Wait = wait
            });
        }

        if (result.Synchronization?.WaitGraph != null)
        {
            var (chains, deadlocks) = BuildWaitGraphChains(result.Synchronization.WaitGraph);
            if (chains.Count > 0)
            {
                timeline.BlockedChains = chains;
            }

            if (deadlocks.Count > 0)
            {
                timeline.Deadlocks = deadlocks;
            }
        }
        else if (result.Synchronization?.PotentialDeadlocks != null && result.Synchronization.PotentialDeadlocks.Count > 0)
        {
            timeline.Deadlocks = result.Synchronization.PotentialDeadlocks
                .Select(d => new TimelineDeadlock
                {
                    Threads = d.Threads.Select(t => $"thread_{t}").ToList(),
                    Kind = "monitor-cycle",
                    Confidence = 0.6,
                    Evidence =
                    [
                        new AnalysisEvidence
                        {
                            JsonPointer = $"{JsonPointerAnalysisBase}/synchronization/potentialDeadlocks",
                            Note = d.Description
                        }
                    ]
                })
                .Take(10)
                .ToList();
        }

        return timeline;
    }

    private static string? InferCaptureReason(CrashAnalysisResult result)
    {
        var faulting = result.Threads?.All?.FirstOrDefault(t => t.IsFaulting);
        if (faulting?.State != null && faulting.State.Contains("SIGSTOP", StringComparison.OrdinalIgnoreCase))
        {
            return "SIGSTOP";
        }

        if (!string.IsNullOrWhiteSpace(result.Environment?.CrashInfo?.SignalName))
        {
            return "signal";
        }

        if (!string.IsNullOrWhiteSpace(result.Exception?.Type))
        {
            return "exception";
        }

        return null;
    }

    private static string? ClassifyWaitKind(StackFrame frame)
    {
        var f = frame.Function ?? string.Empty;

        if (f.Contains("Monitor.Wait", StringComparison.OrdinalIgnoreCase))
        {
            return "monitor";
        }

        if (f.Contains("WaitHandle", StringComparison.OrdinalIgnoreCase) ||
            f.Contains("ManualResetEvent", StringComparison.OrdinalIgnoreCase) ||
            f.Contains("AutoResetEvent", StringComparison.OrdinalIgnoreCase))
        {
            return "event";
        }

        if (f.Contains("Thread.Sleep", StringComparison.OrdinalIgnoreCase))
        {
            return "sleep";
        }

        if (f.Contains("Join", StringComparison.OrdinalIgnoreCase))
        {
            return "join";
        }

        if (f.Contains("futex", StringComparison.OrdinalIgnoreCase) ||
            f.Contains("pthread_cond_wait", StringComparison.OrdinalIgnoreCase) ||
            f.Contains("WaitForSingleObject", StringComparison.OrdinalIgnoreCase))
        {
            return "native-syscall";
        }

        if (f.Contains("Wait", StringComparison.OrdinalIgnoreCase) &&
            !f.Contains("Await", StringComparison.OrdinalIgnoreCase))
        {
            return frame.IsManaged ? "wait" : "native-syscall";
        }

        return null;
    }

    private static (List<TimelineBlockedChain> chains, List<TimelineDeadlock> deadlocks) BuildWaitGraphChains(WaitGraph graph)
    {
        var chains = new List<TimelineBlockedChain>();
        var deadlocks = new List<TimelineDeadlock>();

        // Build resource -> ownerThread edges.
        var resourceOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (!string.Equals(edge.Label, "owned by", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            resourceOwners[edge.From] = edge.To;
        }

        // Build thread -> ownerThread dependencies via waits edges.
        var threadToOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (!string.Equals(edge.Label, "waits", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (resourceOwners.TryGetValue(edge.To, out var ownerThread))
            {
                threadToOwner[edge.From] = ownerThread;
            }
        }

        foreach (var kvp in threadToOwner.Take(50))
        {
            var root = kvp.Key;
            var chain = new List<string> { root };
            var seen = new HashSet<string>(StringComparer.Ordinal) { root };
            var current = root;

            while (threadToOwner.TryGetValue(current, out var next))
            {
                if (!seen.Add(next))
                {
                    // cycle detected
                    chain.Add(next);
                    deadlocks.Add(new TimelineDeadlock
                    {
                        Threads = chain.Distinct(StringComparer.Ordinal).ToList(),
                        Kind = "waitgraph-cycle",
                        Confidence = 0.6,
                        Evidence =
                        [
                            new AnalysisEvidence
                            {
                                JsonPointer = $"{JsonPointerAnalysisBase}/synchronization/waitGraph",
                                Note = "Cycle detected in wait graph"
                            }
                        ]
                    });
                    break;
                }

                chain.Add(next);
                current = next;
                if (chain.Count >= 16)
                {
                    break;
                }
            }

            if (chain.Count > 1)
            {
                chains.Add(new TimelineBlockedChain
                {
                    RootThreadId = root,
                    Chain = chain,
                    Reason = "waitgraph",
                    Confidence = 0.7,
                    Evidence =
                    [
                        new AnalysisEvidence
                        {
                            JsonPointer = $"{JsonPointerAnalysisBase}/synchronization/waitGraph",
                            Note = "Chain derived from wait graph edges"
                        }
                    ]
                });
            }
        }

        return (chains, deadlocks);
    }

    private static List<AnalysisFinding> BuildFindings(CrashAnalysisResult result)
    {
        var findings = new List<AnalysisFinding>();

        var sigstop = result.Threads?.All?.FirstOrDefault(t => t.IsFaulting)?.State?.Contains("SIGSTOP", StringComparison.OrdinalIgnoreCase) == true;
        if (sigstop && string.IsNullOrWhiteSpace(result.Environment?.CrashInfo?.SignalName) && result.Exception == null)
        {
            findings.Add(new AnalysisFinding
            {
                Id = "capture.sigstop.snapshot",
                Title = "SIGSTOP snapshot capture",
                Category = "hang",
                Severity = "info",
                Confidence = 0.8,
                Summary = "Faulting thread stop reason is SIGSTOP, which commonly indicates a hang/snapshot capture rather than a crash.",
                Evidence =
                [
                    new AnalysisEvidence { JsonPointer = $"{JsonPointerAnalysisBase}/threads/faultingThread/state", Note = "Stop reason contains SIGSTOP" }
                ],
                NextActions =
                [
                    "If you are investigating a crash, capture the dump at the crash signal/exception.",
                    "If investigating a hang, collect multiple snapshots over time to confirm the blocked state."
                ]
            });
        }

        var missingSymbolsFinding = TryBuildMissingSymbolsFinding(result);
        if (missingSymbolsFinding != null)
        {
            findings.Add(missingSymbolsFinding);
        }

        if (result.Synchronization?.Summary?.PotentialDeadlockCount > 0)
        {
            findings.Add(new AnalysisFinding
            {
                Id = "threads.deadlock.detected",
                Title = "Potential deadlock detected",
                Category = "deadlock",
                Severity = "warning",
                Confidence = 0.6,
                Summary = $"Synchronization analyzer detected {result.Synchronization.Summary.PotentialDeadlockCount} potential deadlock cycle(s).",
                    Evidence =
                    [
                        new AnalysisEvidence { JsonPointer = $"{JsonPointerAnalysisBase}/synchronization/potentialDeadlocks", Note = "Potential deadlock cycles" }
                    ],
                    NextActions =
                    [
                        "Inspect the deadlock cycles and identify which locks/resources form the cycle.",
                    "Capture additional dumps to confirm the deadlock persists."
                ]
            });
        }

        if ((result.Async?.Timers?.Count ?? 0) > 50)
        {
            findings.Add(new AnalysisFinding
            {
                Id = "timers.high.count",
                Title = "High timer count",
                Category = "perf",
                Severity = "warning",
                Confidence = 0.7,
                Summary = $"High number of active timers ({result.Async!.Timers!.Count}).",
                Evidence =
                [
                    new AnalysisEvidence { JsonPointer = $"{JsonPointerAnalysisBase}/async/timers", Note = "Timer list" }
                ],
                NextActions =
                [
                    "Investigate undisposed timers and long-lived periodic timers.",
                    "Check whether timer count grows over time."
                ]
            });
        }

        var lohBytes = result.Memory?.Gc?.GenerationSizes?.Loh;
        var totalHeap = result.Memory?.Gc?.TotalHeapSize;
        if (lohBytes.HasValue && totalHeap > 0)
        {
            var ratio = (double)lohBytes.Value / totalHeap.Value;
            if (ratio >= 0.3)
            {
                findings.Add(new AnalysisFinding
                {
                    Id = "memory.loh.pressure",
                    Title = "LOH pressure",
                    Category = "oom",
                    Severity = "warning",
                    Confidence = 0.6,
                    Summary = $"Large Object Heap is {lohBytes.Value:N0} bytes (~{ratio:P0} of managed heap).",
                    Evidence =
                    [
                        new AnalysisEvidence { JsonPointer = $"{JsonPointerAnalysisBase}/memory/gc/generationSizes/loh", Note = "LOH bytes" },
                        new AnalysisEvidence { JsonPointer = $"{JsonPointerAnalysisBase}/memory/gc/totalHeapSize", Note = "Total managed heap bytes" }
                    ],
                    NextActions =
                    [
                        "Prefer ArrayPool<T> for large buffers and avoid frequent >85KB allocations.",
                        "If possible, compare multiple dumps to see if LOH grows over time."
                    ]
                });
            }
        }

        return findings;
    }

    private static AnalysisFinding? TryBuildMissingSymbolsFinding(CrashAnalysisResult result)
    {
        if (result.Modules == null || result.Modules.Count == 0 || result.Threads?.All == null)
        {
            return null;
        }

        var missing = result.Modules
            .Where(m => !m.HasSymbols)
            .Select(m => m.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Where(n => !n.StartsWith("[", StringComparison.OrdinalIgnoreCase))
            .Where(n => !n.Contains("vdso", StringComparison.OrdinalIgnoreCase))
            .Where(n => !n.Equals("libgcc_s.so.1", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0)
        {
            return null;
        }

        var missingSet = missing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evidence = new List<AnalysisEvidence>();

        for (var threadIndex = 0; threadIndex < result.Threads.All.Count; threadIndex++)
        {
            var thread = result.Threads.All[threadIndex];
            for (var frameIndex = 0; frameIndex < thread.CallStack.Count; frameIndex++)
            {
                var frame = thread.CallStack[frameIndex];
                if (frame.IsManaged)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(frame.Module) || !missingSet.Contains(frame.Module))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(frame.SourceUrl) &&
                    string.IsNullOrWhiteSpace(frame.SourceFile) &&
                    string.IsNullOrWhiteSpace(frame.Source))
                {
                    evidence.Add(new AnalysisEvidence
                    {
                        JsonPointer = $"{JsonPointerAnalysisBase}/threads/all/{threadIndex}/callStack/{frameIndex}",
                        Note = "Native frame has no source information"
                    });
                }
            }
        }

        if (evidence.Count == 0)
        {
            return null;
        }

        var examples = missing.Take(3).ToList();

        return new AnalysisFinding
        {
            Id = "symbols.native.missing",
            Title = "Native debug symbols missing",
            Category = "symbols",
            Severity = "info",
            Confidence = 0.7,
            Summary = $"Some native modules are missing debug symbols (e.g., {string.Join(", ", examples)}), preventing deeper native source resolution.",
            Evidence = evidence.Take(5).ToList(),
            NextActions =
            [
                "Upload debug symbols for the affected native modules to improve native stack/source resolution."
            ]
        };
    }

    private static RootCauseAnalysis BuildRootCause(CrashAnalysisResult result)
    {
        var hypotheses = new List<RootCauseHypothesis>();

        if (!string.IsNullOrWhiteSpace(result.Environment?.CrashInfo?.SignalName))
        {
            hypotheses.Add(new RootCauseHypothesis
            {
                Label = $"Native crash signal: {result.Environment.CrashInfo!.SignalName}",
                Confidence = 0.7,
                Evidence =
                [
                    new AnalysisEvidence { JsonPointer = $"{JsonPointerAnalysisBase}/environment/crashInfo/signalName", Note = "Signal name" }
                ]
            });
        }

        if (!string.IsNullOrWhiteSpace(result.Exception?.Type))
        {
            hypotheses.Add(new RootCauseHypothesis
            {
                Label = $"Managed exception: {result.Exception!.Type}",
                Confidence = 0.7,
                Evidence =
                [
                    new AnalysisEvidence { JsonPointer = $"{JsonPointerAnalysisBase}/exception/type", Note = "Exception type" }
                ]
            });
        }

        if (result.Threads?.All?.FirstOrDefault(t => t.IsFaulting)?.State?.Contains("SIGSTOP", StringComparison.OrdinalIgnoreCase) == true)
        {
            hypotheses.Add(new RootCauseHypothesis
            {
                Label = "Hang/snapshot capture (SIGSTOP)",
                Confidence = 0.8,
                Evidence =
                [
                    new AnalysisEvidence { JsonPointer = $"{JsonPointerAnalysisBase}/threads/faultingThread/state", Note = "Stop reason contains SIGSTOP" }
                ]
            });
        }

        if (result.Synchronization?.Summary?.PotentialDeadlockCount > 0)
        {
            hypotheses.Add(new RootCauseHypothesis
            {
                Label = "Potential deadlock",
                Confidence = 0.6,
                Evidence =
                [
                    new AnalysisEvidence { JsonPointer = $"{JsonPointerAnalysisBase}/synchronization/potentialDeadlocks", Note = "Potential deadlock cycles" }
                ]
            });
        }

        // Keep ordering deterministic by confidence then label.
        hypotheses = hypotheses
            .OrderByDescending(h => h.Confidence)
            .ThenBy(h => h.Label, StringComparer.Ordinal)
            .ToList();

        return new RootCauseAnalysis { Hypotheses = hypotheses };
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Deterministic selection utilities for stack frames.
/// </summary>
internal static class StackFrameSelection
{
    internal sealed class SelectionResult
    {
        public int? SelectedFrameIndex { get; init; }
        public List<SkippedFrame> SkippedFrames { get; init; } = [];
    }

    internal static SelectionResult SelectMeaningfulTopFrame(IReadOnlyList<StackFrame> callStack)
    {
        if (callStack.Count == 0)
        {
            return new SelectionResult { SelectedFrameIndex = null };
        }

        var skipped = new List<SkippedFrame>();
        for (var i = 0; i < callStack.Count; i++)
        {
            var frame = callStack[i];
            if (IsMeaningfulTopFrameCandidate(frame))
            {
                return new SelectionResult { SelectedFrameIndex = i, SkippedFrames = skipped };
            }

            skipped.Add(new SkippedFrame { FrameIndex = i, Reason = ClassifySkipReason(frame) });
        }

        // If everything is a placeholder, fall back to index 0 to keep deterministic behavior.
        return new SelectionResult { SelectedFrameIndex = 0, SkippedFrames = skipped };
    }

    internal static bool IsMeaningfulTopFrameCandidate(StackFrame frame)
    {
        var function = frame.Function?.Trim();
        if (string.IsNullOrWhiteSpace(function))
        {
            return false;
        }

        if (function.Equals("[Runtime]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (function.Equals("[ManagedMethod]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (function.StartsWith("[JIT Code @", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (function.StartsWith("[Native Code @", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    internal static string FormatNormalizedFrame(StackFrame frame)
    {
        var module = frame.Module?.Trim() ?? string.Empty;
        var function = frame.Function?.Trim() ?? string.Empty;

        // Keep this intentionally simple and deterministic; avoid addresses/offsets.
        return !string.IsNullOrWhiteSpace(module)
            ? $"{module}: {function}"
            : function;
    }

    private static string ClassifySkipReason(StackFrame frame)
    {
        var function = frame.Function?.Trim();
        if (string.IsNullOrWhiteSpace(function))
        {
            return "empty-function";
        }

        if (function.Equals("[Runtime]", StringComparison.OrdinalIgnoreCase))
        {
            return "runtime-glue";
        }

        if (function.Equals("[ManagedMethod]", StringComparison.OrdinalIgnoreCase))
        {
            return "managed-placeholder";
        }

        if (function.StartsWith("[JIT Code @", StringComparison.OrdinalIgnoreCase) ||
            function.StartsWith("[Native Code @", StringComparison.OrdinalIgnoreCase))
        {
            return "placeholder-jit-code";
        }

        return "unknown";
    }
}
