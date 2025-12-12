using DebuggerMcp.Analysis;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Unit tests for pure heap summarization helpers in <see cref="ClrMdAnalyzer"/>.
/// </summary>
public class ClrMdAnalyzerCombinedResultTests
{
    [Fact]
    public void BuildCombinedResult_ComputesPercentagesAndTopLists()
    {
        var typeStats = new Dictionary<string, (int Count, long Size, long Largest)>
        {
            ["A"] = (Count: 2, Size: 200, Largest: 120),
            ["B"] = (Count: 10, Size: 100, Largest: 10)
        };
        var stringCounts = new Dictionary<string, (int Count, long Size)>
        {
            ["dup"] = (Count: 3, Size: 300),
            ["uniq"] = (Count: 1, Size: 50)
        };

        var result = ClrMdAnalyzer.BuildCombinedResult(
            typeStats,
            stringCounts,
            largeObjects: [new LargeObjectInfo { Address = "0x1", Type = "A", Size = 100, Generation = "Gen2" }],
            faultedTasks: [],
            stateMachines: [],
            asyncSummary: new AsyncSummary { TotalTasks = 0 },
            stringLengthDist: new StringLengthDistribution { Empty = 1, Short = 2 },
            totalSize: 300,
            freeSize: 100,
            totalCount: 12,
            stringTotalSize: 350,
            stringTotalCount: 4,
            wasAborted: false,
            elapsedMs: 123,
            topN: 10);

        Assert.NotNull(result.TopMemoryConsumers);
        var top = result.TopMemoryConsumers!;

        Assert.NotNull(top.Summary);
        Assert.NotNull(top.BySize);
        Assert.NotNull(top.ByCount);
        var summary = top.Summary!;
        var bySize = top.BySize!;
        var byCount = top.ByCount!;

        Assert.Equal(12, summary.TotalObjects);
        Assert.Equal(300, summary.TotalSize);
        Assert.Equal(100, summary.FreeBytes);
        Assert.True(summary.FragmentationRatio > 0);

        Assert.Equal("A", bySize[0].Type);
        Assert.Equal(200, bySize[0].TotalSize);
        Assert.Equal(66.67, bySize[0].Percentage);

        Assert.Equal("B", byCount[0].Type);
        Assert.Equal(10, byCount[0].Count);

        Assert.NotNull(result.StringAnalysis);
        var stringAnalysis = result.StringAnalysis!;
        Assert.NotNull(stringAnalysis.Summary);
        Assert.NotNull(stringAnalysis.TopDuplicates);
        var stringSummary = stringAnalysis.Summary!;
        var duplicates = stringAnalysis.TopDuplicates!;

        Assert.Equal(4, stringSummary.TotalStrings);
        Assert.Equal(2, stringSummary.UniqueStrings);
        Assert.Equal(2, stringSummary.DuplicateStrings);
        Assert.True(duplicates.Count >= 1);
        Assert.Contains(duplicates, d => d.Value.Contains("dup", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildCombinedResult_WithZeroTotals_DoesNotDivideByZero()
    {
        var typeStats = new Dictionary<string, (int Count, long Size, long Largest)>
        {
            ["Empty"] = (Count: 0, Size: 0, Largest: 0)
        };
        var stringCounts = new Dictionary<string, (int Count, long Size)>();

        var result = ClrMdAnalyzer.BuildCombinedResult(
            typeStats,
            stringCounts,
            largeObjects: [],
            faultedTasks: [],
            stateMachines: [],
            asyncSummary: new AsyncSummary(),
            stringLengthDist: new StringLengthDistribution(),
            totalSize: 0,
            freeSize: 0,
            totalCount: 0,
            stringTotalSize: 0,
            stringTotalCount: 0,
            wasAborted: true,
            elapsedMs: 0,
            topN: 10);

        Assert.NotNull(result.TopMemoryConsumers);
        Assert.NotNull(result.TopMemoryConsumers!.Summary);
        Assert.Equal(0, result.TopMemoryConsumers.Summary!.TotalSize);
        Assert.Equal(0, result.TopMemoryConsumers.Summary.FragmentationRatio);

        Assert.NotNull(result.StringAnalysis);
        Assert.NotNull(result.StringAnalysis!.Summary);
        Assert.Equal(0, result.StringAnalysis.Summary!.WastedPercentage);
    }

    [Fact]
    public void BuildCombinedResult_EscapesControlCharacters_InTopDuplicates()
    {
        var typeStats = new Dictionary<string, (int Count, long Size, long Largest)>
        {
            ["System.String"] = (Count: 3, Size: 300, Largest: 200)
        };

        // Include control characters so EscapeControlCharacters is exercised.
        var stringCounts = new Dictionary<string, (int Count, long Size)>
        {
            ["a\nb\tc"] = (Count: 2, Size: 40),
            ["http://x"] = (Count: 2, Size: 20)
        };

        var result = ClrMdAnalyzer.BuildCombinedResult(
            typeStats,
            stringCounts,
            largeObjects: [],
            faultedTasks: [],
            stateMachines: [],
            asyncSummary: new AsyncSummary(),
            stringLengthDist: new StringLengthDistribution(),
            totalSize: 300,
            freeSize: 0,
            totalCount: 3,
            stringTotalSize: 60,
            stringTotalCount: 4,
            wasAborted: false,
            elapsedMs: 1,
            topN: 10);

        Assert.NotNull(result.StringAnalysis);
        Assert.NotNull(result.StringAnalysis!.TopDuplicates);
        var dupes = result.StringAnalysis.TopDuplicates!;

        Assert.Contains(dupes, d => d.Value.Contains("\\n", StringComparison.Ordinal) && d.Value.Contains("\\t", StringComparison.Ordinal));
        Assert.Contains(dupes, d => d.Value.Contains("http://x", StringComparison.Ordinal) && (d.Suggestion?.Contains("URL", StringComparison.OrdinalIgnoreCase) == true));
    }
}
