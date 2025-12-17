#nullable enable

using System.Text.Json;
using DebuggerMcp.Serialization;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Builds a bounded, evidence-focused JSON payload suitable for sending to an LLM via MCP sampling.
/// </summary>
/// <remarks>
/// The full <see cref="CrashAnalysisResult"/> can be very large (raw debugger output, full stacks for every thread, etc.).
/// This builder produces a deterministic subset that is typically sufficient for root-cause reasoning, while keeping the
/// LLM prompt size under control.
/// </remarks>
internal static class AiSamplingPromptBuilder
{
    internal static string Build(CrashAnalysisResult report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var prompt = new
        {
            summary = BuildSummary(report.Summary),
            exception = report.Exception,
            environment = BuildEnvironment(report.Environment),
            threads = BuildThreads(report.Threads),
            assemblies = BuildAssemblies(report.Assemblies),
            symbols = report.Symbols,
            timeline = report.Timeline,
            signature = report.Signature,
            stackSelection = report.StackSelection
        };

        return JsonSerializer.Serialize(prompt, JsonSerializationDefaults.IndentedIgnoreNull);
    }

    private static object? BuildSummary(AnalysisSummary? summary)
    {
        if (summary == null)
        {
            return null;
        }

        // Avoid biasing the LLM with server-generated recommendations; keep factual counts + warnings/errors.
        return new
        {
            crashType = summary.CrashType,
            severity = summary.Severity,
            threadCount = summary.ThreadCount,
            moduleCount = summary.ModuleCount,
            assemblyCount = summary.AssemblyCount,
            warnings = summary.Warnings,
            errors = summary.Errors
        };
    }

    private static object? BuildEnvironment(EnvironmentInfo? env)
    {
        if (env == null)
        {
            return null;
        }

        return new
        {
            platform = env.Platform,
            runtime = env.Runtime,
            process = BuildProcess(env.Process),
            crashInfo = env.CrashInfo,
            nativeAot = env.NativeAot
        };
    }

    private static object? BuildProcess(ProcessInfo? process)
    {
        if (process == null)
        {
            return null;
        }

        // Environment variables are high-risk for leaking secrets and are typically not necessary for crash triage.
        return new
        {
            arguments = process.Arguments,
            argc = process.Argc,
            argvAddress = process.ArgvAddress,
            sensitiveDataFiltered = process.SensitiveDataFiltered
        };
    }

    private static object? BuildThreads(ThreadsInfo? threads)
    {
        if (threads == null)
        {
            return null;
        }

        var all = threads.All ?? [];
        var maxThreads = 200;
        var threadHeaders = all.Take(maxThreads).Select(t => BuildThreadHeader(t)).ToList();

        return new
        {
            summary = threads.Summary,
            faultingThread = threads.FaultingThread == null ? null : BuildFaultingThread(threads.FaultingThread),
            all = threadHeaders,
            osThreadCount = threads.OsThreadCount,
            threadPool = threads.ThreadPool,
            deadlock = threads.Deadlock,
            truncation = new
            {
                threadsCapped = all.Count > maxThreads,
                maxThreads
            }
        };
    }

    private static object BuildThreadHeader(ThreadInfo thread)
    {
        return new
        {
            threadId = thread.ThreadId,
            managedThreadId = thread.ManagedThreadId,
            osThreadId = thread.OsThreadId,
            osThreadIdDecimal = thread.OsThreadIdDecimal,
            state = thread.State,
            isFaulting = thread.IsFaulting,
            topFunction = thread.TopFunction,
            topFrame = BuildTopFrame(thread.CallStack)
        };
    }

    private static object BuildFaultingThread(ThreadInfo thread)
    {
        var maxFrames = 60;
        var frames = thread.CallStack ?? [];

        return new
        {
            threadId = thread.ThreadId,
            managedThreadId = thread.ManagedThreadId,
            osThreadId = thread.OsThreadId,
            osThreadIdDecimal = thread.OsThreadIdDecimal,
            state = thread.State,
            isFaulting = thread.IsFaulting,
            topFunction = thread.TopFunction,
            callStack = frames.Take(maxFrames).Select(BuildFrame).ToList(),
            truncation = new
            {
                callStackCapped = frames.Count > maxFrames,
                maxFrames
            }
        };
    }

    private static object? BuildTopFrame(List<StackFrame>? callStack)
    {
        if (callStack == null || callStack.Count == 0)
        {
            return null;
        }

        var selection = StackFrameSelection.SelectMeaningfulTopFrame(callStack);
        if (!selection.SelectedFrameIndex.HasValue)
        {
            return null;
        }

        var index = selection.SelectedFrameIndex.Value;
        if (index < 0 || index >= callStack.Count)
        {
            index = 0;
        }

        return BuildFrame(callStack[index]);
    }

    private static object BuildFrame(StackFrame frame)
    {
        return new
        {
            frameNumber = frame.FrameNumber,
            instructionPointer = frame.InstructionPointer,
            module = frame.Module,
            function = frame.Function,
            sourceFile = frame.SourceFile,
            lineNumber = frame.LineNumber,
            sourceUrl = frame.SourceUrl,
            sourceProvider = frame.SourceProvider,
            isManaged = frame.IsManaged,
            stackPointer = frame.StackPointer
        };
    }

    private static object? BuildAssemblies(AssembliesInfo? assemblies)
    {
        if (assemblies == null)
        {
            return null;
        }

        var items = assemblies.Items ?? [];
        var maxAssemblies = 300;
        var trimmed = items.Take(maxAssemblies).ToList();

        return new
        {
            count = assemblies.Count,
            items = trimmed,
            truncation = new
            {
                assembliesCapped = items.Count > maxAssemblies,
                maxAssemblies
            }
        };
    }
}
