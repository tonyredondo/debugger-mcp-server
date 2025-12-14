using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Finalizes a <see cref="CrashAnalysisResult"/> for report generation by ensuring derived fields are
/// consistent and computed once from the final data.
/// </summary>
/// <remarks>
/// This is intended as a single post-processing pass after all parsers/enrichers have finished mutating the result.
/// It improves testability by centralizing invariant enforcement and reduces duplicated recomputation.
/// </remarks>
internal static class CrashAnalysisResultFinalizer
{
    /// <summary>
    /// Performs a best-effort finalization of the analysis result.
    /// </summary>
    /// <param name="result">The analysis result to finalize.</param>
    internal static void Finalize(CrashAnalysisResult result)
    {
        if (result == null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        var threads = result.Threads?.All;
        if (threads == null)
        {
            return;
        }

        var totalFrames = 0;
        ThreadInfo? faultingThread = null;
        var faultingFrames = 0;

        // Ensure thread-level invariants are consistent and computed from final stacks.
        for (var i = 0; i < threads.Count; i++)
        {
            var thread = threads[i];
            if (thread.CallStack == null)
            {
                thread.CallStack = new List<StackFrame>();
            }

            // Normalize per-frame invariants that should be consistent for consumers regardless of upstream parser source.
            // Example: LLDB can emit placeholder frames like "[ManagedMethod]" with incomplete metadata.
            for (var frameIndex = 0; frameIndex < thread.CallStack.Count; frameIndex++)
            {
                var frame = thread.CallStack[frameIndex];
                var function = frame.Function?.Trim();
                if (string.Equals(function, "[ManagedMethod]", StringComparison.OrdinalIgnoreCase) ||
                    (function?.StartsWith("[JIT Code @", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    frame.IsManaged = true;
                }
            }

            // Frame numbers are consumer-facing: keep them a stable 0..n-1 index.
            StackFrameUtilities.RenumberFramesSequential(thread.CallStack);

            // Always recompute topFunction from the final stack (skip placeholders deterministically).
            thread.TopFunction = StackFrameUtilities.ComputeMeaningfulTopFunction(thread.CallStack, thread.TopFunction);

            totalFrames += thread.CallStack.Count;
            if (faultingThread == null && thread.IsFaulting)
            {
                faultingThread = thread;
                faultingFrames = thread.CallStack.Count;
            }
        }

        var moduleCount = result.Modules?.Count ?? 0;

        result.Threads!.OsThreadCount = threads.Count;
        result.Threads.FaultingThread = faultingThread;

        result.Summary ??= new AnalysisSummary();
        result.Summary.ThreadCount = threads.Count;
        result.Summary.ModuleCount = moduleCount;

        var description = result.Summary.Description ?? string.Empty;

        // Replace existing count clause if present.
        // Example: "Found 47 threads (1280 total frames, 49 in faulting thread), 11 modules."
        description = Regex.Replace(
            description,
            @"Found\s+\d+\s+threads\s+\(\d+\s+total\s+frames,\s+\d+\s+in\s+faulting\s+thread\),\s+\d+\s+modules\.",
            $"Found {threads.Count} threads ({totalFrames} total frames, {faultingFrames} in faulting thread), {moduleCount} modules.",
            RegexOptions.IgnoreCase);

        result.Summary.Description = description;

        // Populate derived fields (signature, findings, timeline, etc.) based on the finalized stacks.
        // This is intentionally done at the end so consumers see deterministic data computed from the final report state.
        CrashAnalysisDerivedFieldsBuilder.PopulateDerivedFields(result);
    }
}
