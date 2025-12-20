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

        // Only populate deterministic, data-oriented derived fields.
        // Interpretive fields are intentionally excluded from the canonical report.
        result.Signature = BuildSignature(result);
        result.Symbols = BuildSymbols(result);
        result.Timeline = BuildTimeline(result);
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

                if (StackFrameUtilities.IsMeaningfulTopFrameCandidate(frame))
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
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

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
                Examples = nativeMissing.Count > 0 ? nativeMissing : null
            },
            Managed = new ManagedSymbolsSummary
            {
                PdbMissingCount = managedMissingCount,
                Examples = managedMissingModules.Count > 0
                    ? managedMissingModules.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
                    : null
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

        foreach (var kvp in threadToOwner
                     .OrderBy(k => k.Key, StringComparer.Ordinal)
                     .Take(50))
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
    internal sealed class SkippedFrameInfo
    {
        public int FrameIndex { get; init; }
        public string Reason { get; init; } = string.Empty;
    }

    internal sealed class SelectionResult
    {
        public int? SelectedFrameIndex { get; init; }
        public List<SkippedFrameInfo> SkippedFrames { get; init; } = [];
    }

    internal static SelectionResult SelectMeaningfulTopFrame(IReadOnlyList<StackFrame> callStack)
    {
        if (callStack.Count == 0)
        {
            return new SelectionResult { SelectedFrameIndex = null };
        }

        var skipped = new List<SkippedFrameInfo>();
        for (var i = 0; i < callStack.Count; i++)
        {
            var frame = callStack[i];
            if (StackFrameUtilities.IsMeaningfulTopFrameCandidate(frame))
            {
                return new SelectionResult { SelectedFrameIndex = i, SkippedFrames = skipped };
            }

            skipped.Add(new SkippedFrameInfo { FrameIndex = i, Reason = ClassifySkipReason(frame) });
        }

        // If everything is a placeholder, fall back to index 0 to keep deterministic behavior.
        return new SelectionResult { SelectedFrameIndex = 0, SkippedFrames = skipped };
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
