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
}

