using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DebuggerMcp.Analysis;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Contract assertions for <see cref="CrashAnalysisResult"/> to prevent silent JSON report regressions.
/// These checks focus on internal consistency and consumer-facing invariants rather than exact values.
/// </summary>
internal static class CrashAnalysisResultContract
{
    private static readonly Regex SummaryCountsClauseRegex = new(
        @"Found\s+(?<threads>\d+)\s+threads\s+\((?<totalFrames>\d+)\s+total\s+frames,\s+(?<faultingFrames>\d+)\s+in\s+faulting\s+thread\),\s+(?<modules>\d+)\s+modules\.",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void AssertValid(CrashAnalysisResult result)
    {
        Assert.NotNull(result);
        Assert.NotNull(result.Summary);
        Assert.NotNull(result.Threads);
        Assert.NotNull(result.Threads!.All);

        AssertThreadCounts(result);
        AssertFaultingThreadConsistency(result);
        AssertSummaryCountClauseMatchesData(result);
        AssertThreadStacksAndFrames(result);
        AssertAssemblyListConsistency(result);
        AssertEnvironmentSecretsAreRedacted(result);
        AssertDerivedFields(result);
    }

    private static void AssertThreadCounts(CrashAnalysisResult result)
    {
        var threads = result.Threads!.All!;
        var osThreadCount = result.Threads.OsThreadCount;
        if (osThreadCount.HasValue)
        {
            Assert.Equal(threads.Count, osThreadCount.Value);
        }

        Assert.Equal(threads.Count, result.Summary!.ThreadCount);
    }

    private static void AssertFaultingThreadConsistency(CrashAnalysisResult result)
    {
        var threads = result.Threads!.All!;
        var faulting = threads.Where(t => t.IsFaulting).ToList();
        Assert.True(faulting.Count <= 1, "At most one thread should be marked faulting.");

        if (result.Threads.FaultingThread != null)
        {
            Assert.True(result.Threads.FaultingThread.IsFaulting, "threads.faultingThread must have isFaulting=true.");
            if (faulting.Count == 1)
            {
                Assert.Equal(faulting[0].ThreadId, result.Threads.FaultingThread.ThreadId);
            }
        }
    }

    private static void AssertSummaryCountClauseMatchesData(CrashAnalysisResult result)
    {
        var description = result.Summary!.Description ?? string.Empty;
        var match = SummaryCountsClauseRegex.Match(description);
        if (!match.Success)
        {
            return;
        }

        var threads = result.Threads!.All!;
        var totalFrames = threads.Sum(t => t.CallStack.Count);
        var faultingFrames = threads.FirstOrDefault(t => t.IsFaulting)?.CallStack.Count ?? 0;
        var moduleCount = result.Modules?.Count ?? 0;

        Assert.Equal(threads.Count, int.Parse(match.Groups["threads"].Value));
        Assert.Equal(totalFrames, int.Parse(match.Groups["totalFrames"].Value));
        Assert.Equal(faultingFrames, int.Parse(match.Groups["faultingFrames"].Value));
        Assert.Equal(moduleCount, int.Parse(match.Groups["modules"].Value));
    }

    private static void AssertThreadStacksAndFrames(CrashAnalysisResult result)
    {
        var threads = result.Threads!.All!;

        foreach (var thread in threads)
        {
            Assert.NotNull(thread.CallStack);

            // Frame numbers should be sequential (0..n-1) after final merging.
            for (var i = 0; i < thread.CallStack.Count; i++)
            {
                Assert.Equal(i, thread.CallStack[i].FrameNumber);
            }

            // topFunction should be deterministic based on the final call stack.
            if (thread.CallStack.Count > 0)
            {
                var expectedTop = ComputeMeaningfulTopFunction(thread.CallStack);
                Assert.Equal(expectedTop, thread.TopFunction);
            }

            // OSID readability: when both are present, osThreadIdDecimal should match the tid displayed in threadId.
            if (!string.IsNullOrWhiteSpace(thread.OsThreadId) && !string.IsNullOrWhiteSpace(thread.OsThreadIdDecimal))
            {
                var tidFromThreadId = TryParseTidFromThreadId(thread.ThreadId);
                if (tidFromThreadId.HasValue)
                {
                    Assert.Equal(tidFromThreadId.Value.ToString(), thread.OsThreadIdDecimal);
                }
            }

            foreach (var frame in thread.CallStack)
            {
                AssertFrameInvariants(frame);
            }
        }
    }

    private static void AssertFrameInvariants(StackFrame frame)
    {
        Assert.False(string.IsNullOrWhiteSpace(frame.InstructionPointer));
        Assert.Matches("^0x[0-9a-fA-F]+$", frame.InstructionPointer);

        if (!string.IsNullOrWhiteSpace(frame.Module) && frame.Module.Contains('`'))
        {
            Assert.Fail($"Frame module contains backtick: '{frame.Module}' (function='{frame.Function}', sourceFile='{frame.SourceFile}')");
        }

        if (!string.IsNullOrWhiteSpace(frame.StackPointer))
        {
            Assert.Matches("^0x[0-9a-fA-F]+$", frame.StackPointer);
        }

        // Managed/native variable placement must match isManaged.
        if (frame.IsManaged == false)
        {
            Assert.True(frame.Parameters == null || frame.Parameters.Count == 0);
            Assert.True(frame.Locals == null || frame.Locals.Count == 0);
        }
        else
        {
            if ((frame.Parameters != null && frame.Parameters.Count > 0) ||
                (frame.Locals != null && frame.Locals.Count > 0))
            {
                Assert.True(frame.IsManaged);
            }
        }

        // If a source URL exists, it should be complete and consistent.
        if (!string.IsNullOrWhiteSpace(frame.SourceUrl))
        {
            Assert.False(string.IsNullOrWhiteSpace(frame.SourceProvider));
            Assert.False(string.IsNullOrWhiteSpace(frame.SourceFile));

            var lineAnchor = Regex.Match(frame.SourceUrl, @"#L(\d+)$");
            if (lineAnchor.Success)
            {
                Assert.True(frame.LineNumber.HasValue);
                Assert.Equal(frame.LineNumber!.Value, int.Parse(lineAnchor.Groups[1].Value));
                Assert.True(frame.LineNumber.Value > 0);
            }
            else
            {
                // Some native debug formats can provide a file but no stable line mapping (e.g. ":0" from LLDB/DWARF).
                // In these cases we allow a file URL without an anchor and a missing line number.
                if (frame.LineNumber.HasValue)
                {
                    Assert.True(frame.LineNumber.Value > 0);
                }
            }
        }

        // Registers should be normalized (0x prefix) for consumer consistency.
        if (frame.Registers != null)
        {
            foreach (var (name, value) in frame.Registers)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var lowerName = name.ToLowerInvariant();
                var isPointerReg = lowerName is "sp" or "pc" or "fp" or "lr" or "rsp" or "rip" or "rbp" or "esp" or "eip";

                if (isPointerReg)
                {
                    Assert.Matches("^0x[0-9a-fA-F]+$", value);
                    continue;
                }

                if (Regex.IsMatch(value, @"^[0-9a-fA-F]{8,}$"))
                {
                    Assert.Fail($"Register value '{name}={value}' is missing 0x prefix.");
                }
            }
        }
    }

    private static void AssertAssemblyListConsistency(CrashAnalysisResult result)
    {
        if (result.Assemblies?.Items == null || result.Assemblies.Items.Count == 0)
        {
            return;
        }

        Assert.Equal(result.Assemblies.Items.Count, result.Assemblies.Count);

        // Ensure there are no duplicates under the dedup key used by the analyzer.
        var keys = result.Assemblies.Items.Select(a =>
        {
            if (!string.IsNullOrWhiteSpace(a.Path))
                return a.Path!;
            if (!string.IsNullOrWhiteSpace(a.ModuleId))
                return $"{a.Name}|{a.ModuleId}";
            return a.Name;
        }).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // commitHash should only exist when repository context exists.
        foreach (var assembly in result.Assemblies.Items)
        {
            if (string.IsNullOrWhiteSpace(assembly.CommitHash))
            {
                continue;
            }

            var hasRepo = !string.IsNullOrWhiteSpace(assembly.RepositoryUrl);
            var hasSourceCommitUrl = assembly.CustomAttributes != null &&
                                     assembly.CustomAttributes.TryGetValue("SourceCommitUrl", out var sourceCommitUrl) &&
                                     !string.IsNullOrWhiteSpace(sourceCommitUrl);

            Assert.True(hasRepo || hasSourceCommitUrl, $"Assembly '{assembly.Name}' has commitHash without repository context.");
        }
    }

    private static void AssertEnvironmentSecretsAreRedacted(CrashAnalysisResult result)
    {
        var envVars = result.Environment?.Process?.EnvironmentVariables;
        if (envVars == null || envVars.Count == 0)
        {
            return;
        }

        foreach (var line in envVars)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.Contains('='))
            {
                continue;
            }

            var parts = line.Split('=', 2);
            var key = parts[0].Trim();
            var value = parts.Length > 1 ? parts[1].Trim() : string.Empty;

            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var upper = key.ToUpperInvariant();
            var looksSensitive = upper.Contains("API_KEY") ||
                                 upper.Contains("TOKEN") ||
                                 upper.Contains("SECRET") ||
                                 upper.Contains("PASSWORD");

            if (looksSensitive && value.Length > 0)
            {
                Assert.Equal("<redacted>", value);
            }
        }
    }

    private static string ComputeMeaningfulTopFunction(IReadOnlyList<StackFrame> callStack)
    {
        foreach (var frame in callStack)
        {
            if (IsMeaningfulTopFrameCandidate(frame))
            {
                return FormatTopFrameDisplay(frame);
            }
        }

        return FormatTopFrameDisplay(callStack[0]);
    }

    private static bool IsMeaningfulTopFrameCandidate(StackFrame frame)
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

    private static int? TryParseTidFromThreadId(string threadId)
    {
        var match = Regex.Match(threadId ?? string.Empty, @"tid:\s*(0x[0-9a-fA-F]+|\d+)");
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Value;
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt32(value, 16)
            : int.Parse(value);
    }

    private static void AssertDerivedFields(CrashAnalysisResult result)
    {
        Assert.NotNull(result.Signature);
        Assert.Equal(1, result.Signature!.Version);
        Assert.Matches("^sha256:[0-9a-f]{64}$", result.Signature.Hash);
        Assert.Contains(result.Signature.Kind, new[] { "crash", "hang", "oom", "unknown" });
        Assert.NotNull(result.Signature.Parts);

        Assert.NotNull(result.StackSelection);
        Assert.Equal(1, result.StackSelection!.Version);
        Assert.NotNull(result.StackSelection.ThreadSelections);
        Assert.Equal(result.Threads!.All!.Count, result.StackSelection.ThreadSelections.Count);

        for (var i = 0; i < result.Threads.All.Count; i++)
        {
            var thread = result.Threads.All[i];
            var selection = result.StackSelection.ThreadSelections[i];
            Assert.Equal(thread.ThreadId ?? string.Empty, selection.ThreadId);

            if (thread.CallStack.Count == 0)
            {
                Assert.Null(selection.SelectedFrameIndex);
                continue;
            }

            Assert.NotNull(selection.SelectedFrameIndex);
            Assert.InRange(selection.SelectedFrameIndex!.Value, 0, thread.CallStack.Count - 1);
            if (selection.SkippedFrames != null)
            {
                foreach (var skipped in selection.SkippedFrames)
                {
                    Assert.InRange(skipped.FrameIndex, 0, thread.CallStack.Count - 1);
                    Assert.False(string.IsNullOrWhiteSpace(skipped.Reason));
                }
            }
        }

        Assert.NotNull(result.Symbols);
        var expectedMissing = result.Modules?.Count(m => m.HasSymbols == false) ?? 0;
        Assert.Equal(expectedMissing, result.Symbols!.Native.MissingCount);

        var expectedResolved = 0;
        var expectedUnresolved = 0;
        var expectedManagedMissing = 0;
        foreach (var thread in result.Threads.All)
        {
            foreach (var frame in thread.CallStack)
            {
                if (!string.IsNullOrWhiteSpace(frame.SourceUrl))
                {
                    expectedResolved++;
                }
                else if (!string.IsNullOrWhiteSpace(frame.SourceFile) && (frame.LineNumber ?? 0) > 0)
                {
                    expectedUnresolved++;
                }

                if (frame.IsManaged &&
                    !string.IsNullOrWhiteSpace(frame.SourceFile) &&
                    (frame.LineNumber ?? 0) > 0 &&
                    string.IsNullOrWhiteSpace(frame.SourceUrl))
                {
                    expectedManagedMissing++;
                }
            }
        }

        Assert.Equal(expectedResolved, result.Symbols.SourceLink.ResolvedCount);
        Assert.Equal(expectedUnresolved, result.Symbols.SourceLink.UnresolvedCount);
        Assert.Equal(expectedManagedMissing, result.Symbols.Managed.PdbMissingCount);

        Assert.NotNull(result.Timeline);
        Assert.Equal(1, result.Timeline!.Version);
        Assert.Equal("snapshot", result.Timeline.Kind);
        Assert.Equal(Math.Min(result.Threads.All.Count, 200), result.Timeline.Threads.Count);

        Assert.NotNull(result.Findings);
        foreach (var finding in result.Findings!)
        {
            Assert.False(string.IsNullOrWhiteSpace(finding.Id));
            Assert.False(string.IsNullOrWhiteSpace(finding.Title));
            Assert.InRange(finding.Confidence, 0, 1);
            if (finding.Evidence != null)
            {
                foreach (var evidence in finding.Evidence)
                {
                    Assert.StartsWith("/analysis/", evidence.JsonPointer);
                }
            }
        }

        Assert.NotNull(result.RootCause);
        Assert.NotNull(result.RootCause!.Hypotheses);
        foreach (var hypothesis in result.RootCause.Hypotheses)
        {
            Assert.False(string.IsNullOrWhiteSpace(hypothesis.Label));
            Assert.InRange(hypothesis.Confidence, 0, 1);
            if (hypothesis.Evidence != null)
            {
                foreach (var evidence in hypothesis.Evidence)
                {
                    Assert.StartsWith("/analysis/", evidence.JsonPointer);
                }
            }
        }
    }
}
