using System;
using System.Collections.Generic;
using System.Text;
using DebuggerMcp.Analysis;
using DebuggerMcp.SourceLink;
using DebuggerMcp.Tests.TestDoubles;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

public class DotNetCrashAnalyzerRecommendationsTests
{
    private sealed class TestDotNetCrashAnalyzer : DotNetCrashAnalyzer
    {
        public TestDotNetCrashAnalyzer(IDebuggerManager manager)
            : base(manager, new SourceLinkResolver(), clrMdAnalyzer: null, logger: null)
        {
        }

        public void TestParseClrThreads(string output, CrashAnalysisResult result) => ParseClrThreads(output, result);
        public void TestParseTimerInfo(string output, CrashAnalysisResult result) => ParseTimerInfo(output, result);
        public void TestAnalyzeDotNetMemoryLeaks(string output, CrashAnalysisResult result) => AnalyzeDotNetMemoryLeaks(output, result);
    }

    [Fact]
    public void ParseClrThreads_WhenDeadThreadsPresent_AddsBenignDeadThreadsRecommendation()
    {
        var analyzer = new TestDotNetCrashAnalyzer(new FakeDebuggerManager());
        var result = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { Recommendations = new List<string>() },
            Threads = new ThreadsInfo { Summary = new ThreadSummary() }
        };

        // Minimal output for the summary counters.
        // Only DeadThread is required for this regression.
        var output = """
ThreadCount:      51
BackgroundThread: 41
UnstartedThread:  0
PendingThread:    0
DeadThread:       9
""";

        analyzer.TestParseClrThreads(output, result);

        Assert.Contains(result.Summary.Recommendations, r => r.Contains("dead managed thread", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Summary.Recommendations, r => r.Contains("thread pool exhaustion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseTimerInfo_WhenManyTimersInTestHost_IncludesTopOwnersAndContext()
    {
        var analyzer = new TestDotNetCrashAnalyzer(new FakeDebuggerManager());
        var result = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { Recommendations = new List<string>() },
            Environment = new EnvironmentInfo
            {
                Process = new ProcessInfo
                {
                    Arguments = new List<string> { "/usr/share/dotnet/dotnet", "exec", "testhost.dll" }
                }
            }
        };

        // Emit >50 timer entries so the recommendation triggers and so we can validate owner aggregation.
        var sb = new StringBuilder();
        for (var i = 0; i < 60; i++)
        {
            var type = i < 30 ? "Microsoft.VisualStudio.TestPlatform.CrossPlatEngine.Execution.TestRunCache" :
                i < 50 ? "System.Threading.Tasks.Task+DelayPromise" :
                "System.Threading.TimerQueueTimer";

            sb.AppendLine($"(L) 0x0000F7158EDFD1D0 @    1500 ms every     1500 ms |  0000F7158EDFCE20 ({type}) -> Callback");
        }

        var output = sb.ToString();

        analyzer.TestParseTimerInfo(output, result);

        Assert.Contains(result.Summary.Recommendations, r => r.Contains("active timers (", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Summary.Recommendations, r => r.Contains("testhost", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Summary.Recommendations, r => r.Contains("Top timer state types", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnalyzeDotNetMemoryLeaks_WhenLohHeavy_AddsRecommendationWithLohBytes()
    {
        var analyzer = new TestDotNetCrashAnalyzer(new FakeDebuggerManager());
        var result = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { Recommendations = new List<string>() },
            Memory = new MemoryInfo
            {
                Gc = new GcSummary
                {
                    TotalHeapSize = 200_000_000,
                    GenerationSizes = new GenerationSizes { Loh = 100_000_000 }
                }
            }
        };

        // Minimal !dumpheap -stat-like line to trigger the LOH-sized allocation heuristic.
        // totalSize/count = 100000 (> 85000) so it should trigger the LOH recommendation path.
        var heapOutput = "00007ff8a1234567 1 100000 System.Byte[]";

        analyzer.TestAnalyzeDotNetMemoryLeaks(heapOutput, result);

        Assert.Contains(result.Summary.Recommendations, r => r.Contains("Large Object Heap allocations detected", StringComparison.OrdinalIgnoreCase));
        var recommendation = Assert.Single(result.Summary.Recommendations, r => r.Contains("Large Object Heap allocations detected", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("LOH is", recommendation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bytes", recommendation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("100000000", string.Concat(recommendation.Where(char.IsDigit)));
    }
}
