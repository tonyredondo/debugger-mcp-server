using System;
using System.Collections.Generic;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Utility helpers for working with <see cref="StackFrame"/> collections.
/// </summary>
internal static class StackFrameUtilities
{
    /// <summary>
    /// Computes a deterministic "meaningful" top frame display value for a thread.
    /// Skips placeholder frames such as "[JIT Code @ ...]" and "[Runtime]" and chooses the first
    /// non-placeholder frame in call stack order.
    /// </summary>
    /// <param name="callStack">The thread call stack (top frame first).</param>
    /// <param name="existingTopFunction">Optional existing value used when no frames are available.</param>
    /// <returns>A display string such as "Module!Function".</returns>
    internal static string ComputeMeaningfulTopFunction(IReadOnlyList<StackFrame> callStack, string? existingTopFunction = null)
    {
        if (callStack.Count == 0)
        {
            return existingTopFunction ?? string.Empty;
        }

        foreach (var frame in callStack)
        {
            if (IsMeaningfulTopFrameCandidate(frame))
            {
                return FormatTopFrameDisplay(frame);
            }
        }

        // If every frame is a placeholder, fall back to the first frame to keep the value deterministic.
        return FormatTopFrameDisplay(callStack[0]);
    }

    /// <summary>
    /// Renumbers <see cref="StackFrame.FrameNumber"/> to be sequential (0..n-1) in the current list order.
    /// </summary>
    /// <param name="callStack">The call stack to renumber.</param>
    internal static void RenumberFramesSequential(IList<StackFrame> callStack)
    {
        for (var i = 0; i < callStack.Count; i++)
        {
            callStack[i].FrameNumber = i;
        }
    }

    /// <summary>
    /// Determines whether a frame is a candidate for "meaningful top frame" selection.
    /// </summary>
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

    private static string FormatTopFrameDisplay(StackFrame frame)
    {
        var function = frame.Function ?? string.Empty;
        var module = frame.Module ?? string.Empty;

        return !string.IsNullOrWhiteSpace(module)
            ? $"{module}!{function}"
            : function;
    }
}
