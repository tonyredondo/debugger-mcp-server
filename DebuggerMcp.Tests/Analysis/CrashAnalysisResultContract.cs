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
            Assert.True(frame.LineNumber.HasValue);

            var lineAnchor = Regex.Match(frame.SourceUrl, @"#L(\d+)$");
            if (lineAnchor.Success)
            {
                Assert.Equal(frame.LineNumber!.Value, int.Parse(lineAnchor.Groups[1].Value));
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
}
