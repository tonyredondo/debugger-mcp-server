using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Reporting;
using Xunit;

namespace DebuggerMcp.Tests.Reporting;

public class JsonReportPrunerTests
{
    [Fact]
    public void BuildSummaryJson_WhenThreadInfoExcluded_KeepsFaultingThreadOnly()
    {
        var analysis = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { CrashType = "X" },
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo { ThreadId = "t1", IsFaulting = true, CallStack = [new StackFrame { Module = "m", Function = "f" }] },
                    new ThreadInfo { ThreadId = "t2", IsFaulting = false, CallStack = [new StackFrame { Module = "m2", Function = "f2" }] }
                ]
            },
            Environment = new EnvironmentInfo
            {
                Process = new ProcessInfo { EnvironmentVariables = ["A=1", "B=2", "C=3"] }
            },
            Modules = [new ModuleInfo { Name = "x", BaseAddress = "0x1", HasSymbols = false }]
        };
        CrashAnalysisResultFinalizer.Finalize(analysis);

        var service = new ReportService();
        var canonical = service.GenerateReport(
            analysis,
            new ReportOptions { Format = ReportFormat.Json },
            new ReportMetadata { DumpId = "d", UserId = "u", DebuggerType = "LLDB", GeneratedAt = DateTime.UnixEpoch, Format = ReportFormat.Json });

        var options = ReportOptions.SummaryReport;
        options.Format = ReportFormat.Json;

        var summary = JsonReportPruner.BuildSummaryJson(canonical, options);

        using var doc = JsonDocument.Parse(summary);
        var root = doc.RootElement;
        var threads = root.GetProperty("analysis").GetProperty("threads");

        Assert.True(threads.TryGetProperty("faultingThread", out _));
        Assert.False(threads.TryGetProperty("all", out _));
    }

    [Fact]
    public void BuildSummaryJson_WhenMaxEnvironmentVariablesSet_TruncatesEnvironmentVariables()
    {
        var analysis = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { CrashType = "X" },
            Environment = new EnvironmentInfo
            {
                Process = new ProcessInfo { EnvironmentVariables = ["A=1", "B=2", "C=3", "D=4"] }
            }
        };
        CrashAnalysisResultFinalizer.Finalize(analysis);

        var service = new ReportService();
        var canonical = service.GenerateReport(
            analysis,
            new ReportOptions { Format = ReportFormat.Json },
            new ReportMetadata { DumpId = "d", UserId = "u", DebuggerType = "LLDB", GeneratedAt = DateTime.UnixEpoch, Format = ReportFormat.Json });

        var options = ReportOptions.SummaryReport;
        options.Format = ReportFormat.Json;
        options.MaxEnvironmentVariables = 2;

        var summary = JsonReportPruner.BuildSummaryJson(canonical, options);

        using var doc = JsonDocument.Parse(summary);
        var process = doc.RootElement.GetProperty("analysis").GetProperty("environment").GetProperty("process");
        var vars = process.GetProperty("environmentVariables");

        Assert.Equal(JsonValueKind.Array, vars.ValueKind);
        Assert.Equal(2, vars.GetArrayLength());
        Assert.Equal("A=1", vars[0].GetString());
        Assert.Equal("B=2", vars[1].GetString());
    }

    [Fact]
    public void BuildSummaryJson_WhenModulesExcluded_RemovesModules()
    {
        var analysis = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { CrashType = "X" },
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo { ThreadId = "t1", IsFaulting = true, CallStack = [new StackFrame { Module = "m", Function = "f" }] }
                ]
            },
            Modules =
            [
                new ModuleInfo { Name = "m1", BaseAddress = "0x1", HasSymbols = true },
                new ModuleInfo { Name = "m2", BaseAddress = "0x2", HasSymbols = false }
            ]
        };
        CrashAnalysisResultFinalizer.Finalize(analysis);

        var canonical = new ReportService().GenerateReport(
            analysis,
            new ReportOptions { Format = ReportFormat.Json },
            new ReportMetadata { DumpId = "d", UserId = "u", DebuggerType = "LLDB", GeneratedAt = DateTime.UnixEpoch, Format = ReportFormat.Json });

        var options = ReportOptions.FullReport;
        options.Format = ReportFormat.Json;
        options.IncludeModules = false;

        var summary = JsonReportPruner.BuildSummaryJson(canonical, options);

        using var doc = JsonDocument.Parse(summary);
        var analysisObj = doc.RootElement.GetProperty("analysis");
        Assert.False(analysisObj.TryGetProperty("modules", out _));
    }

    [Fact]
    public void BuildSummaryJson_WhenDotNetInfoExcluded_RemovesDotNetSpecificSections()
    {
        var analysis = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { CrashType = "X" },
            Assemblies = new AssembliesInfo { Items = [new AssemblyVersionInfo { Name = "a", AssemblyVersion = "1.0.0" }] },
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo { ThreadId = "t1", IsFaulting = true, CallStack = [new StackFrame { Module = "m", Function = "f" }] }
                ]
            }
        };
        CrashAnalysisResultFinalizer.Finalize(analysis);

        var canonical = new ReportService().GenerateReport(
            analysis,
            new ReportOptions { Format = ReportFormat.Json },
            new ReportMetadata { DumpId = "d", UserId = "u", DebuggerType = "LLDB", GeneratedAt = DateTime.UnixEpoch, Format = ReportFormat.Json });

        var options = ReportOptions.FullReport;
        options.Format = ReportFormat.Json;
        options.IncludeDotNetInfo = false;

        var summary = JsonReportPruner.BuildSummaryJson(canonical, options);

        using var doc = JsonDocument.Parse(summary);
        var analysisObj = doc.RootElement.GetProperty("analysis");
        Assert.False(analysisObj.TryGetProperty("assemblies", out _));
        Assert.False(analysisObj.TryGetProperty("symbols", out _));
        Assert.False(analysisObj.TryGetProperty("signature", out _));
        Assert.False(analysisObj.TryGetProperty("stackSelection", out _));
        Assert.False(analysisObj.TryGetProperty("sourceContext", out _));
    }

    [Fact]
    public void BuildSummaryJson_WhenCallStacksExcluded_RemovesCallStackFromFaultingThread()
    {
        var analysis = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { CrashType = "X" },
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo
                    {
                        ThreadId = "t1",
                        IsFaulting = true,
                        CallStack = [new StackFrame { Module = "m", Function = "f" }]
                    }
                ]
            }
        };
        CrashAnalysisResultFinalizer.Finalize(analysis);

        var canonical = new ReportService().GenerateReport(
            analysis,
            new ReportOptions { Format = ReportFormat.Json },
            new ReportMetadata { DumpId = "d", UserId = "u", DebuggerType = "LLDB", GeneratedAt = DateTime.UnixEpoch, Format = ReportFormat.Json });

        var options = ReportOptions.FullReport;
        options.Format = ReportFormat.Json;
        options.IncludeCallStacks = false;

        var summary = JsonReportPruner.BuildSummaryJson(canonical, options);

        using var doc = JsonDocument.Parse(summary);
        var threads = doc.RootElement.GetProperty("analysis").GetProperty("threads");
        var faulting = threads.GetProperty("faultingThread");

        Assert.False(faulting.TryGetProperty("callStack", out _));
    }

    [Fact]
    public void BuildSummaryJson_WhenThreadAndCallStackLimitsSet_TruncatesThreadsAndFrames()
    {
        var analysis = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { CrashType = "X" },
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo
                    {
                        ThreadId = "t1",
                        IsFaulting = true,
                        CallStack = [new StackFrame { Module = "m", Function = "f0" }, new StackFrame { Module = "m", Function = "f1" }]
                    },
                    new ThreadInfo
                    {
                        ThreadId = "t2",
                        IsFaulting = false,
                        CallStack = [new StackFrame { Module = "m", Function = "g0" }, new StackFrame { Module = "m", Function = "g1" }]
                    },
                    new ThreadInfo
                    {
                        ThreadId = "t3",
                        IsFaulting = false,
                        CallStack = [new StackFrame { Module = "m", Function = "h0" }, new StackFrame { Module = "m", Function = "h1" }]
                    }
                ]
            }
        };
        CrashAnalysisResultFinalizer.Finalize(analysis);

        var canonical = new ReportService().GenerateReport(
            analysis,
            new ReportOptions { Format = ReportFormat.Json },
            new ReportMetadata { DumpId = "d", UserId = "u", DebuggerType = "LLDB", GeneratedAt = DateTime.UnixEpoch, Format = ReportFormat.Json });

        var options = ReportOptions.FullReport;
        options.Format = ReportFormat.Json;
        options.MaxThreadsToShow = 2;
        options.MaxCallStackFrames = 1;

        var summary = JsonReportPruner.BuildSummaryJson(canonical, options);

        using var doc = JsonDocument.Parse(summary);
        var threads = doc.RootElement.GetProperty("analysis").GetProperty("threads");
        var all = threads.GetProperty("all");

        Assert.Equal(2, all.GetArrayLength());

        var faulting = threads.GetProperty("faultingThread");
        Assert.Equal(1, faulting.GetProperty("callStack").GetArrayLength());
        Assert.Equal(1, all[0].GetProperty("callStack").GetArrayLength());
    }

    [Fact]
    public void BuildSummaryJson_WhenCanonicalJsonIsInvalid_ReturnsOriginal()
    {
        var invalid = "{not valid json";
        var summary = JsonReportPruner.BuildSummaryJson(invalid, ReportOptions.FullReport);
        Assert.Equal(invalid, summary);
    }
}
