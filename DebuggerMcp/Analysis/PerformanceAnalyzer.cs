using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Provides performance profiling and analysis capabilities for memory dumps.
/// Analyzes CPU usage, memory allocations, GC behavior, and thread contention.
/// </summary>
/// <remarks>
/// This analyzer works with both WinDbg and LLDB debuggers and provides
/// structured output suitable for identifying performance bottlenecks.
/// 
/// Example usage:
/// <code>
/// var analyzer = new PerformanceAnalyzer(debuggerManager);
/// var result = await analyzer.AnalyzePerformanceAsync();
/// Console.WriteLine(result.Summary);
/// </code>
/// </remarks>
public class PerformanceAnalyzer
{
    private readonly IDebuggerManager _manager;

    /// <summary>
    /// Large Object Heap threshold in bytes (85KB).
    /// </summary>
    private const int LargeObjectThreshold = 85000;

    /// <summary>
    /// Threshold for excessive string allocations.
    /// </summary>
    private const int ExcessiveStringThreshold = 50000;

    // Note: High instance count threshold is defined in AnalysisConstants.HighInstanceCountThreshold

    /// <summary>
    /// Initializes a new instance of the <see cref="PerformanceAnalyzer"/> class.
    /// </summary>
    /// <param name="manager">The debugger manager to use for executing commands.</param>
    /// <exception cref="ArgumentNullException">Thrown when manager is null.</exception>
    public PerformanceAnalyzer(IDebuggerManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    /// <summary>
    /// Performs a comprehensive performance analysis covering CPU, memory, GC, and contention.
    /// </summary>
    /// <returns>Complete performance analysis results.</returns>
    public async Task<PerformanceAnalysisResult> AnalyzePerformanceAsync()
    {
        var result = new PerformanceAnalysisResult
        {
            DebuggerType = _manager.DebuggerType
        };

        // Command caching is automatically enabled when dump is opened
        try
        {
            // Run all analyses (they will benefit from the cache)
            result.CpuAnalysis = await AnalyzeCpuUsageInternalAsync();
            result.AllocationAnalysis = await AnalyzeAllocationsInternalAsync();
            result.GcAnalysis = await AnalyzeGcInternalAsync();
            result.ContentionAnalysis = await AnalyzeContentionInternalAsync();

            // Generate overall summary
            GenerateOverallSummary(result);
        }
        catch (Exception ex)
        {
            // Keep a partial result rather than throwing so callers get error context.
            result.Summary = $"Performance analysis failed: {ex.Message}";
            result.Recommendations.Add("Ensure the dump file is valid and symbols are loaded.");
        }

        return result;
    }

    /// <summary>
    /// Analyzes CPU usage across all threads.
    /// </summary>
    /// <returns>CPU analysis results.</returns>
    public async Task<CpuAnalysisResult> AnalyzeCpuUsageAsync()
    {
        return await AnalyzeCpuUsageInternalAsync();
    }

    private async Task<CpuAnalysisResult> AnalyzeCpuUsageInternalAsync()
    {
        var result = new CpuAnalysisResult();

        try
        {
            if (_manager.DebuggerType == "WinDbg")
            {
                await AnalyzeCpuWinDbgAsync(result);
            }
            else if (_manager.DebuggerType == "LLDB")
            {
                await AnalyzeCpuLldbAsync(result);
            }

            GenerateCpuRecommendations(result);
        }
        catch (Exception ex)
        {
            // Capture failure as a recommendation so the rest of the analysis can proceed.
            result.Recommendations.Add($"CPU analysis failed: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Analyzes memory allocation patterns.
    /// </summary>
    /// <returns>Allocation analysis results.</returns>
    public async Task<AllocationAnalysisResult> AnalyzeAllocationsAsync()
    {
        return await AnalyzeAllocationsInternalAsync();
    }

    private async Task<AllocationAnalysisResult> AnalyzeAllocationsInternalAsync()
    {
        var result = new AllocationAnalysisResult();

        try
        {
            if (_manager.DebuggerType == "WinDbg")
            {
                await AnalyzeAllocationsWinDbgAsync(result);
            }
            else if (_manager.DebuggerType == "LLDB")
            {
                await AnalyzeAllocationsLldbAsync(result);
            }

            GenerateAllocationRecommendations(result);
        }
        catch (Exception ex)
        {
            // Record failure but keep returning a result to the caller.
            result.Recommendations.Add($"Allocation analysis failed: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Analyzes garbage collection behavior and heap state.
    /// </summary>
    /// <returns>GC analysis results.</returns>
    public async Task<GcAnalysisResult> AnalyzeGcAsync()
    {
        return await AnalyzeGcInternalAsync();
    }

    private async Task<GcAnalysisResult> AnalyzeGcInternalAsync()
    {
        var result = new GcAnalysisResult();

        try
        {
            if (_manager.DebuggerType == "WinDbg")
            {
                await AnalyzeGcWinDbgAsync(result);
            }
            else if (_manager.DebuggerType == "LLDB")
            {
                await AnalyzeGcLldbAsync(result);
            }

            GenerateGcRecommendations(result);
        }
        catch (Exception ex)
        {
            // Surface failure as a recommendation to avoid failing the entire analysis.
            result.Recommendations.Add($"GC analysis failed: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Analyzes thread contention and lock usage.
    /// </summary>
    /// <returns>Contention analysis results.</returns>
    public async Task<ContentionAnalysisResult> AnalyzeContentionAsync()
    {
        return await AnalyzeContentionInternalAsync();
    }

    private async Task<ContentionAnalysisResult> AnalyzeContentionInternalAsync()
    {
        var result = new ContentionAnalysisResult();

        try
        {
            if (_manager.DebuggerType == "WinDbg")
            {
                await AnalyzeContentionWinDbgAsync(result);
            }
            else if (_manager.DebuggerType == "LLDB")
            {
                await AnalyzeContentionLldbAsync(result);
            }

            GenerateContentionRecommendations(result);
        }
        catch (Exception ex)
        {
            // Preserve partial results; caller can see the failure message.
            result.Recommendations.Add($"Contention analysis failed: {ex.Message}");
        }

        return result;
    }

    // === WinDbg Analysis Methods ===

    private async Task AnalyzeCpuWinDbgAsync(CpuAnalysisResult result)
    {
        // Get thread CPU times using !runaway
        var runawayOutput = await ExecuteCommandAsync("!runaway");
        result.RawOutput = runawayOutput;

        // Parse runaway output for user mode times
        ParseWinDbgRunaway(runawayOutput, result);

        // Get all thread stacks to identify hot functions
        var stacksOutput = await ExecuteCommandAsync("~*k");
        IdentifyHotFunctions(stacksOutput, result);

        // Get thread list for state info
        var threadsOutput = await ExecuteCommandAsync("~");
        CountThreadStates(threadsOutput, result);
    }

    private void ParseWinDbgRunaway(string output, CpuAnalysisResult result)
    {
        // Format: " 0:1234      0 days 0:00:01.234"
        var lines = output.Split('\n');
        var userTimeSection = true;

        foreach (var line in lines)
        {
            if (line.Contains("Kernel Mode Time"))
            {
                userTimeSection = false;
                continue;
            }

            // Match thread time: "  threadId:osId   days d:hh:mm:ss.ms"
            var match = Regex.Match(line, @"^\s*(\d+):([0-9a-f]+)\s+(\d+)\s+days?\s+(\d+):(\d+):(\d+)\.(\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var threadId = match.Groups[1].Value;
                var days = int.Parse(match.Groups[3].Value);
                var hours = int.Parse(match.Groups[4].Value);
                var minutes = int.Parse(match.Groups[5].Value);
                var seconds = int.Parse(match.Groups[6].Value);
                var ms = int.Parse(match.Groups[7].Value);

                var totalMs = (days * 24 * 60 * 60 * 1000) +
                              (hours * 60 * 60 * 1000) +
                              (minutes * 60 * 1000) +
                              (seconds * 1000) +
                              ms;

                var existing = result.ThreadCpuUsage.FirstOrDefault(t => t.ThreadId == threadId);
                if (existing != null)
                {
                    if (userTimeSection)
                        existing.UserTime = totalMs;
                    else
                        existing.KernelTime = totalMs;
                    existing.TotalTime = existing.UserTime + existing.KernelTime;
                }
                else
                {
                    result.ThreadCpuUsage.Add(new ThreadCpuInfo
                    {
                        ThreadId = threadId,
                        UserTime = userTimeSection ? totalMs : 0,
                        KernelTime = userTimeSection ? 0 : totalMs,
                        TotalTime = totalMs
                    });
                }
            }
        }

        // Sort by total time descending
        result.ThreadCpuUsage = result.ThreadCpuUsage.OrderByDescending(t => t.TotalTime).ToList();
    }

    private async Task AnalyzeAllocationsWinDbgAsync(AllocationAnalysisResult result)
    {
        // Get heap statistics using !dumpheap -stat
        var heapOutput = await ExecuteCommandAsync("!dumpheap -stat");
        result.RawOutput = heapOutput;

        // Parse !dumpheap -stat output
        ParseDumpHeapStats(heapOutput, result);

        // Look for large objects
        var lohOutput = await ExecuteCommandAsync("!dumpheap -min 85000 -stat");
        ParseLargeObjectAllocations(lohOutput, result);
    }

    private void ParseDumpHeapStats(string output, AllocationAnalysisResult result)
    {
        // Format: "MT    Count    TotalSize Class Name"
        //         "00007ff812345678    12345    98765432 System.String"
        // Note: LLDB/SOS may format large numbers with commas: "11,193" "1,011,728"
        var lines = output.Split('\n');
        var totalObjects = 0L;
        var totalSize = 0L;

        foreach (var line in lines)
        {
            // Use [\d,]+ to match numbers with or without commas
            var match = Regex.Match(line, @"^\s*([0-9a-f]+)\s+([\d,]+)\s+([\d,]+)\s+(.+)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var mt = match.Groups[1].Value;
                // Remove commas before parsing
                var count = long.Parse(match.Groups[2].Value.Replace(",", ""));
                var size = long.Parse(match.Groups[3].Value.Replace(",", ""));
                var typeName = match.Groups[4].Value.Trim();

                // Skip summary lines
                if (typeName.StartsWith("Total ") || typeName.StartsWith("Statistics:") || typeName == "Free")
                {
                    continue;
                }

                totalObjects += count;
                totalSize += size;

                var info = new AllocationInfo
                {
                    MethodTable = mt,
                    Count = count,
                    TotalSizeBytes = size,
                    TypeName = typeName
                };

                result.TopAllocators.Add(info);

                // Track string allocations
                if (typeName == "System.String")
                {
                    result.StringStats = new StringAllocationStats
                    {
                        Count = count,
                        TotalSizeBytes = size,
                        AverageLength = count > 0 ? (double)size / count / 2 : 0, // Approximate (chars are 2 bytes)
                        ExcessiveAllocations = count > ExcessiveStringThreshold
                    };
                }

                // Track array allocations
                if (typeName.EndsWith("[]"))
                {
                    if (result.ArrayStats == null)
                    {
                        result.ArrayStats = new ArrayAllocationStats();
                    }
                    result.ArrayStats.Count += count;
                    result.ArrayStats.TotalSizeBytes += size;
                    result.ArrayStats.ArrayTypes[typeName] = count;
                }

                // Check for potential leaks (high instance count)
                if (count > AnalysisConstants.HighInstanceCountThreshold)
                {
                    result.PotentialLeaks.Add(info);
                }
            }
        }

        // Parse total line - handle numbers with or without commas
        // Format: "Total 48,694 objects, 4,132,329 bytes" or "Total 4146 objects, 224198 bytes"
        var totalMatch = Regex.Match(output, @"Total\s+([\d,]+)\s+objects,\s+([\d,]+)\s+bytes", RegexOptions.IgnoreCase);
        if (totalMatch.Success)
        {
            // Remove commas before parsing
            result.TotalObjectCount = long.Parse(totalMatch.Groups[1].Value.Replace(",", ""));
            result.TotalHeapSizeBytes = long.Parse(totalMatch.Groups[2].Value.Replace(",", ""));
        }
        else
        {
            result.TotalObjectCount = totalObjects;
            result.TotalHeapSizeBytes = totalSize;
        }

        // Calculate percentages and sort
        foreach (var alloc in result.TopAllocators)
        {
            alloc.PercentageOfHeap = result.TotalHeapSizeBytes > 0
                ? (double)alloc.TotalSizeBytes / result.TotalHeapSizeBytes * 100
                : 0;
        }

        result.TopAllocators = result.TopAllocators
            .OrderByDescending(a => a.TotalSizeBytes)
            .Take(20)
            .ToList();

        result.PotentialLeaks = result.PotentialLeaks
            .OrderByDescending(a => a.Count)
            .Take(10)
            .ToList();
    }

    private void ParseLargeObjectAllocations(string output, AllocationAnalysisResult result)
    {
        var lines = output.Split('\n');
        foreach (var line in lines)
        {
            // Use [\d,]+ to match numbers with or without commas
            var match = Regex.Match(line, @"^\s*([0-9a-f]+)\s+([\d,]+)\s+([\d,]+)\s+(.+)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var typeName = match.Groups[4].Value.Trim();
                if (typeName.StartsWith("Total ") || typeName == "Free")
                    continue;

                // Remove commas before parsing
                var count = long.Parse(match.Groups[2].Value.Replace(",", ""));
                var size = long.Parse(match.Groups[3].Value.Replace(",", ""));

                result.LargeObjectAllocations.Add(new AllocationInfo
                {
                    MethodTable = match.Groups[1].Value,
                    Count = count,
                    TotalSizeBytes = size,
                    TypeName = typeName
                });

                // Update array stats for large arrays
                if (typeName.EndsWith("[]") && result.ArrayStats != null)
                {
                    result.ArrayStats.LargeArrayCount += (int)count;
                }
            }
        }

        result.LargeObjectAllocations = result.LargeObjectAllocations
            .OrderByDescending(a => a.TotalSizeBytes)
            .Take(10)
            .ToList();
    }

    private async Task AnalyzeGcWinDbgAsync(GcAnalysisResult result)
    {
        var output = new System.Text.StringBuilder();

        // Get GC heap info
        var eeheapOutput = await ExecuteCommandAsync("!eeheap -gc");
        output.AppendLine("=== !eeheap -gc ===").AppendLine(eeheapOutput);
        ParseEeheapGc(eeheapOutput, result);

        // Get GC handles
        var gchandlesOutput = await ExecuteCommandAsync("!gchandles");
        output.AppendLine("=== !gchandles ===").AppendLine(gchandlesOutput);
        ParseGcHandles(gchandlesOutput, result);

        // Get finalizer queue
        var finalizerOutput = await ExecuteCommandAsync("!finalizequeue");
        output.AppendLine("=== !finalizequeue ===").AppendLine(finalizerOutput);
        ParseFinalizerQueue(finalizerOutput, result);

        result.RawOutput = output.ToString();
    }

    private void ParseEeheapGc(string output, GcAnalysisResult result)
    {
        // Parse GC mode
        if (output.Contains("Server GC", StringComparison.OrdinalIgnoreCase))
        {
            result.GcMode = "Server";
        }
        else if (output.Contains("Workstation GC", StringComparison.OrdinalIgnoreCase))
        {
            result.GcMode = "Workstation";
        }

        result.ConcurrentGc = output.Contains("Concurrent GC", StringComparison.OrdinalIgnoreCase);

        // Detect output format: LLDB/SOS vs WinDbg
        // LLDB/SOS format has "generation 0:" on its own line followed by segment data
        var isLldbFormat = Regex.IsMatch(output, @"^generation\s+\d+:\s*$", RegexOptions.Multiline);

        if (isLldbFormat)
        {
            ParseEeheapGcLldbFormat(output, result);
        }
        else
        {
            ParseEeheapGcWinDbgFormat(output, result);
        }

        // Parse fragmentation
        var fragMatch = Regex.Match(output, @"Fragmented blocks.*?(\d+\.?\d*)\s*%", RegexOptions.IgnoreCase);
        if (fragMatch.Success)
        {
            result.FragmentationPercent = double.Parse(fragMatch.Groups[1].Value);
        }

        // Detect high GC pressure (large Gen2 or LOH relative to total)
        if (result.TotalHeapSizeBytes > 0)
        {
            var gen2Ratio = (double)result.Gen2SizeBytes / result.TotalHeapSizeBytes;
            var lohRatio = (double)result.LohSizeBytes / result.TotalHeapSizeBytes;
            result.HighGcPressure = gen2Ratio > 0.7 || lohRatio > 0.5 || result.FragmentationPercent > 30;
        }
    }

    /// <summary>
    /// Parses LLDB/SOS format of eeheap -gc output.
    /// Format:
    /// generation 0:
    ///     segment            begin        allocated        committed allocated size     committed size
    ///     f7150583f8b8     f7158ec00028     f7158ee6c210     f7158ee81000 0x26c1e8 (2540008) 0x281000 (2625536)
    /// GC Allocated Heap Size:    Size: 0x4037e8 (4208616) bytes.
    /// </summary>
    private void ParseEeheapGcLldbFormat(string output, GcAnalysisResult result)
    {
        // Parse generation sizes from LLDB/SOS format
        // Each generation section starts with "generation N:" and contains segment rows
        // Segment rows have: segment begin allocated committed allocated_size committed_size
        // allocated_size is in format: 0x26c1e8 (2540008)

        // Parse Gen0 - sum all segments under "generation 0:"
        result.Gen0SizeBytes = ParseLldbGenerationSize(output, "generation 0:");

        // Parse Gen1 - sum all segments under "generation 1:"
        result.Gen1SizeBytes = ParseLldbGenerationSize(output, "generation 1:");

        // Parse Gen2 - sum all segments under "generation 2:"
        result.Gen2SizeBytes = ParseLldbGenerationSize(output, "generation 2:");

        // Parse LOH - sum all segments under "Large object heap"
        result.LohSizeBytes = ParseLldbGenerationSize(output, "Large object heap");

        // Parse POH - sum all segments under "Pinned object heap"
        result.PohSizeBytes = ParseLldbGenerationSize(output, "Pinned object heap");

        // Parse total heap size from "GC Allocated Heap Size: Size: 0x4037e8 (4208616) bytes."
        var totalMatch = Regex.Match(output, @"GC Allocated Heap Size:\s*Size:\s*0x[0-9a-f]+\s*\((\d+)\)", RegexOptions.IgnoreCase);
        if (totalMatch.Success && long.TryParse(totalMatch.Groups[1].Value, out var totalSize))
        {
            result.TotalHeapSizeBytes = totalSize;
        }
        else
        {
            result.TotalHeapSizeBytes = result.Gen0SizeBytes + result.Gen1SizeBytes + result.Gen2SizeBytes + result.LohSizeBytes + result.PohSizeBytes;
        }
    }

    /// <summary>
    /// Parses the size of a generation/heap section from LLDB/SOS eeheap output.
    /// </summary>
    private static long ParseLldbGenerationSize(string output, string sectionHeader)
    {
        // Find the section
        var sectionIndex = output.IndexOf(sectionHeader, StringComparison.OrdinalIgnoreCase);
        if (sectionIndex < 0)
        {
            return 0;
        }

        // Find the end of this section (next section header or end of heap info)
        var sectionEnd = output.Length;
        var nextSections = new[] { "generation 0:", "generation 1:", "generation 2:", "Large object heap", "Pinned object heap", "NonGC heap", "GC Allocated Heap Size", "---" };

        foreach (var nextSection in nextSections)
        {
            if (string.Equals(sectionHeader, nextSection, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var nextIndex = output.IndexOf(nextSection, sectionIndex + sectionHeader.Length, StringComparison.OrdinalIgnoreCase);
            if (nextIndex > 0 && nextIndex < sectionEnd)
            {
                sectionEnd = nextIndex;
            }
        }

        var sectionText = output.Substring(sectionIndex, sectionEnd - sectionIndex);

        // Parse segment rows to extract allocated sizes
        // Format for non-empty segments: segment begin allocated committed 0x26c1e8 (2540008) 0x281000 (2625536)
        // Format for empty segments:     segment begin allocated committed                    0x1000 (4096)
        // 
        // Key insight: 
        // - Non-empty segments have TWO "0xHEX (DECIMAL)" patterns (allocated and committed)
        // - Empty segments have ONE "0xHEX (DECIMAL)" pattern (just committed)
        // - We only want to count allocated sizes, not committed sizes for empty segments
        long totalSize = 0;

        var lines = sectionText.Split('\n');
        foreach (var line in lines)
        {
            // Skip header lines and empty lines
            if (string.IsNullOrWhiteSpace(line) || line.Contains("segment") || line.Contains("begin"))
            {
                continue;
            }

            // Match "0xHEX (DECIMAL)" patterns - we need at least 2 for allocated size to exist
            // Pattern: 0x26c1e8 (2540008) 0x281000 (2625536)
            var hexDecimalMatches = Regex.Matches(line, @"0x([0-9a-f]+)\s*\((\d+)\)", RegexOptions.IgnoreCase);

            if (hexDecimalMatches.Count >= 2)
            {
                // First match is allocated size, second is committed size
                if (long.TryParse(hexDecimalMatches[0].Groups[2].Value, out var allocatedSize))
                {
                    totalSize += allocatedSize;
                }
            }
            // If only one match, it's committed size only (empty segment) - skip it
        }

        return totalSize;
    }

    /// <summary>
    /// Parses WinDbg format of eeheap -gc output.
    /// </summary>
    private void ParseEeheapGcWinDbgFormat(string output, GcAnalysisResult result)
    {
        // Parse generation sizes (WinDbg format)
        var gen0Match = Regex.Match(output, @"generation\s+0.*?size:\s*([0-9a-fx]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (gen0Match.Success)
        {
            TryParseHexOrDecimal(gen0Match.Groups[1].Value, out var gen0Size);
            result.Gen0SizeBytes = gen0Size;
        }

        var gen1Match = Regex.Match(output, @"generation\s+1.*?size:\s*([0-9a-fx]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (gen1Match.Success)
        {
            TryParseHexOrDecimal(gen1Match.Groups[1].Value, out var gen1Size);
            result.Gen1SizeBytes = gen1Size;
        }

        var gen2Match = Regex.Match(output, @"generation\s+2.*?size:\s*([0-9a-fx]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (gen2Match.Success)
        {
            TryParseHexOrDecimal(gen2Match.Groups[1].Value, out var gen2Size);
            result.Gen2SizeBytes = gen2Size;
        }

        // Parse LOH
        var lohMatch = Regex.Match(output, @"Large object heap.*?size:\s*([0-9a-fx]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (lohMatch.Success)
        {
            TryParseHexOrDecimal(lohMatch.Groups[1].Value, out var lohSize);
            result.LohSizeBytes = lohSize;
        }

        // Parse POH (.NET 5+)
        var pohMatch = Regex.Match(output, @"Pinned object heap.*?size:\s*([0-9a-fx]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (pohMatch.Success)
        {
            TryParseHexOrDecimal(pohMatch.Groups[1].Value, out var pohSize);
            result.PohSizeBytes = pohSize;
        }

        // Parse total heap size
        var totalMatch = Regex.Match(output, @"GC Heap Size:\s*([0-9a-fx]+)", RegexOptions.IgnoreCase);
        if (totalMatch.Success)
        {
            TryParseHexOrDecimal(totalMatch.Groups[1].Value, out var totalSize);
            result.TotalHeapSizeBytes = totalSize;
        }
        else
        {
            result.TotalHeapSizeBytes = result.Gen0SizeBytes + result.Gen1SizeBytes + result.Gen2SizeBytes + result.LohSizeBytes + result.PohSizeBytes;
        }
    }

    private void ParseGcHandles(string output, GcAnalysisResult result)
    {
        // Count total handles
        var handleMatch = Regex.Match(output, @"(\d+)\s+handles", RegexOptions.IgnoreCase);
        if (handleMatch.Success)
        {
            result.GcHandleCount = int.Parse(handleMatch.Groups[1].Value);
        }

        // Count pinned handles
        var pinnedMatches = Regex.Matches(output, @"Pinned\s+(\d+)", RegexOptions.IgnoreCase);
        foreach (Match match in pinnedMatches)
        {
            result.PinnedObjectCount += int.Parse(match.Groups[1].Value);
        }
    }

    private void ParseFinalizerQueue(string output, GcAnalysisResult result)
    {
        // Parse ready for finalization count
        // WinDbg format: "123 objects ready for finalization"
        var readyMatch = Regex.Match(output, @"(\d+)\s+objects?\s+ready for finalization", RegexOptions.IgnoreCase);
        if (readyMatch.Success)
        {
            result.FinalizerQueueLength = int.Parse(readyMatch.Groups[1].Value);
        }
        else
        {
            // LLDB/SOS format: "Ready for finalization 0 objects"
            var lldbReadyMatch = Regex.Match(output, @"Ready for finalization\s+(\d+)\s+objects?", RegexOptions.IgnoreCase);
            if (lldbReadyMatch.Success)
            {
                result.FinalizerQueueLength = int.Parse(lldbReadyMatch.Groups[1].Value);
            }
            else
            {
                // Alternative format: "Finalizer Queue Length: 123"
                var queueMatch = Regex.Match(output, @"Finalizer Queue Length:\s*(\d+)", RegexOptions.IgnoreCase);
                if (queueMatch.Success)
                {
                    result.FinalizerQueueLength = int.Parse(queueMatch.Groups[1].Value);
                }
            }
        }

        // Parse generation finalizable object counts from LLDB/SOS format
        // "generation 0 has 65 objects"
        var genFinalizableMatch = Regex.Match(output, @"generation\s+0\s+has\s+(\d+)\s+(?:finalizable\s+)?objects?", RegexOptions.IgnoreCase);
        if (genFinalizableMatch.Success)
        {
            var gen0Finalizable = int.Parse(genFinalizableMatch.Groups[1].Value);
            // Gen0 finalizable objects is a good indicator if FinalizerQueueLength wasn't set
            if (result.FinalizerQueueLength == 0 && gen0Finalizable > 0)
            {
                // This represents objects that have finalizers registered, not necessarily ready for finalization
                // Keep it as additional info but don't override FinalizerQueueLength
            }
        }

        // Check for blocked finalizer
        result.FinalizerThreadBlocked = output.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
                                         output.Contains("waiting", StringComparison.OrdinalIgnoreCase);
    }

    private async Task AnalyzeContentionWinDbgAsync(ContentionAnalysisResult result)
    {
        var output = new System.Text.StringBuilder();

        // Get sync blocks (!syncblk)
        var syncblkOutput = await ExecuteCommandAsync("!syncblk");
        output.AppendLine("=== !syncblk ===").AppendLine(syncblkOutput);
        ParseSyncBlocks(syncblkOutput, result);

        // Get locks (!locks)
        var locksOutput = await ExecuteCommandAsync("!locks");
        output.AppendLine("=== !locks ===").AppendLine(locksOutput);
        ParseLocks(locksOutput, result);

        // Get thread wait info
        var threadsOutput = await ExecuteCommandAsync("~*e !clrstack");
        output.AppendLine("=== Thread stacks ===").AppendLine(threadsOutput);
        ParseWaitingThreads(threadsOutput, result);

        result.RawOutput = output.ToString();
    }

    private void ParseSyncBlocks(string output, ContentionAnalysisResult result)
    {
        // SyncBlk output format varies between WinDbg and LLDB:
        // 
        // WinDbg format (more fields):
        //   Index SyncBlock MonitorHeld Recursion Owning Thread Info  SyncBlock Owner
        //      12 0000024453f8a5a8    1         1 0000024453f46720  1c54  10   00000244540a5820 System.Object
        //
        // LLDB format (simpler):
        //   Index         SyncBlock MonitorHeld Recursion Owning Thread Info          SyncBlock Owner
        //       1    f7150583f8b8           1         1        f7158ec00028      f7158ee6c210 System.Object
        //
        // We use a flexible approach: match the first 4 numeric/hex fields, then capture the rest.

        var lines = output.Split('\n');
        foreach (var line in lines)
        {
            // Skip header lines and summary lines (Total, Free, dashes)
            if (line.Contains("Index") || line.Contains("---") ||
                line.Contains("Total") || line.Contains("Free") ||
                string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Try WinDbg extended format first (9 groups):
            // Index SyncBlock MonitorHeld Recursion ThreadPtr ThreadHexId ThreadDecId ObjAddr ObjType
            var winDbgMatch = Regex.Match(line,
                @"^\s*(\d+)\s+([0-9a-f]+)\s+(\d+)\s+(\d+)\s+([0-9a-f]+)\s+([0-9a-f]+)\s+(\d+)\s+([0-9a-f]+)\s+(.+)$",
                RegexOptions.IgnoreCase);

            if (winDbgMatch.Success)
            {
                var index = int.Parse(winDbgMatch.Groups[1].Value);
                var syncBlock = winDbgMatch.Groups[2].Value;
                var monitorHeld = int.Parse(winDbgMatch.Groups[3].Value);
                var ownerThread = winDbgMatch.Groups[6].Value; // Thread hex ID
                var objectAddress = winDbgMatch.Groups[8].Value;
                var objectType = winDbgMatch.Groups[9].Value.Trim();

                if (monitorHeld > 0)
                {
                    result.SyncBlocks.Add(new SyncBlockInfo
                    {
                        Index = index,
                        ObjectAddress = objectAddress,
                        OwnerThreadId = ownerThread,
                        ObjectType = objectType
                    });
                    result.TotalLockCount++;
                }
                continue;
            }

            // Try LLDB/simpler format (6 groups):
            // Index SyncBlock MonitorHeld Recursion ThreadInfo Rest(ObjAddr+Type)
            var lldbMatch = Regex.Match(line,
                @"^\s*(\d+)\s+([0-9a-f]+)\s+(\d+)\s+(\d+)\s+([0-9a-f]+)\s+(.+)$",
                RegexOptions.IgnoreCase);

            if (lldbMatch.Success)
            {
                var index = int.Parse(lldbMatch.Groups[1].Value);
                var syncBlock = lldbMatch.Groups[2].Value;
                var monitorHeld = int.Parse(lldbMatch.Groups[3].Value);
                var ownerThread = lldbMatch.Groups[5].Value;
                var rest = lldbMatch.Groups[6].Value.Trim();

                // Try to extract object type (ClassName with dots or just any word)
                var typeMatch = Regex.Match(rest, @"(\S+\.\S+|\S+)$");
                var objectType = typeMatch.Success ? typeMatch.Groups[1].Value : null;

                // Try to extract object address (hex at start of rest)
                var objAddrMatch = Regex.Match(rest, @"^([0-9a-f]+)", RegexOptions.IgnoreCase);
                var objectAddress = objAddrMatch.Success ? objAddrMatch.Groups[1].Value : syncBlock;

                if (monitorHeld > 0)
                {
                    result.SyncBlocks.Add(new SyncBlockInfo
                    {
                        Index = index,
                        ObjectAddress = objectAddress,
                        OwnerThreadId = ownerThread != "0" && ownerThread != "none" ? ownerThread : null,
                        ObjectType = objectType
                    });
                    result.TotalLockCount++;
                }
            }
        }
    }

    private void ParseLocks(string output, ContentionAnalysisResult result)
    {
        // Parse CritSec info
        var critSecMatches = Regex.Matches(output,
            @"CritSec\s+(\S+)\s+at\s+([0-9a-fx`]+).*?LockCount\s+(\d+).*?RecursionCount\s+(\d+).*?OwningThread\s+([0-9a-fx`]+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in critSecMatches)
        {
            var lockName = match.Groups[1].Value;
            var address = match.Groups[2].Value.Replace("`", "");
            var lockCount = int.Parse(match.Groups[3].Value);
            var recursion = int.Parse(match.Groups[4].Value);
            var ownerThread = match.Groups[5].Value.Replace("`", "");

            if (lockCount > 0)
            {
                result.ContentedLocks.Add(new ContentedLock
                {
                    Address = address,
                    LockType = lockName,
                    OwnerThreadId = ownerThread,
                    RecursionCount = recursion,
                    WaiterCount = lockCount - 1 // LockCount includes owner
                });
                result.ContentedLockCount++;
            }
        }

        // Check for deadlock indication
        result.DeadlockDetected = output.Contains("DEADLOCK", StringComparison.OrdinalIgnoreCase);
    }

    private void ParseWaitingThreads(string output, ContentionAnalysisResult result)
    {
        // Look for threads waiting on locks (Monitor.Enter, WaitOne, etc.)
        var waitPatterns = new[] { "Monitor.Enter", "WaitOne", "WaitAny", "WaitAll", "SpinWait", "Mutex", "Semaphore" };
        var threadSections = Regex.Split(output, @"(?=OS Thread Id:)");

        foreach (var section in threadSections)
        {
            if (string.IsNullOrWhiteSpace(section))
                continue;

            var threadMatch = Regex.Match(section, @"OS Thread Id:\s*0x([0-9a-f]+)", RegexOptions.IgnoreCase);
            if (!threadMatch.Success)
                continue;

            var threadId = threadMatch.Groups[1].Value;
            var isWaiting = waitPatterns.Any(p => section.Contains(p, StringComparison.OrdinalIgnoreCase));

            if (isWaiting)
            {
                var topFunc = "";
                var funcMatch = Regex.Match(section, @"\s+(\S+!?\S+)\s*$", RegexOptions.Multiline);
                if (funcMatch.Success)
                {
                    topFunc = funcMatch.Groups[1].Value;
                }

                var waitReason = waitPatterns.FirstOrDefault(p => section.Contains(p, StringComparison.OrdinalIgnoreCase)) ?? "Unknown";

                result.WaitingThreads.Add(new WaitingThread
                {
                    ThreadId = threadId,
                    WaitReason = waitReason,
                    TopFunction = topFunc
                });
            }
        }

        // Detect high contention
        result.HighContention = result.ContentedLockCount > 3 || result.WaitingThreads.Count > 5;
    }

    // === LLDB Analysis Methods ===

    private async Task AnalyzeCpuLldbAsync(CpuAnalysisResult result)
    {
        // Get thread list
        var threadOutput = await ExecuteCommandAsync("thread list");
        result.RawOutput = threadOutput;

        // Count threads
        var threadMatches = Regex.Matches(threadOutput, @"thread #(\d+):", RegexOptions.IgnoreCase);
        result.TotalThreads = threadMatches.Count;

        // Count stopped/waiting threads
        // LLDB stop reasons include:
        // - "stop reason = signal 0" (generic signal)
        // - "stop reason = SIGABRT" (named signal)
        // - "stop reason = SIGSEGV" (named signal)  
        // - "stop reason = breakpoint X.Y"
        // - "stop reason = watchpoint X"
        // - "stop reason = EXC_BAD_ACCESS" (macOS exception)
        // - "stop reason = EXC_CRASH" (macOS crash)
        var stoppedPatterns = new[]
        {
            @"stop reason\s*=\s*signal\s",           // "signal 0", "signal SIGABRT"
            @"stop reason\s*=\s*SIG[A-Z]+",          // SIGABRT, SIGSEGV, SIGBUS, etc.
            @"stop reason\s*=\s*breakpoint",         // breakpoint
            @"stop reason\s*=\s*watchpoint",         // watchpoint
            @"stop reason\s*=\s*EXC_",               // macOS exceptions (EXC_BAD_ACCESS, EXC_CRASH, etc.)
        };

        var stoppedCount = 0;
        foreach (var pattern in stoppedPatterns)
        {
            stoppedCount += Regex.Matches(threadOutput, pattern, RegexOptions.IgnoreCase).Count;
        }

        // Active threads = total - stopped (but at least 0)
        result.ActiveThreads = Math.Max(0, result.TotalThreads - stoppedCount);

        // Get all backtraces for hot function analysis
        var backtraceOutput = await ExecuteCommandAsync("bt all");
        IdentifyHotFunctions(backtraceOutput, result);
    }

    private async Task AnalyzeAllocationsLldbAsync(AllocationAnalysisResult result)
    {
        // Try SOS commands if loaded
        var heapOutput = await ExecuteCommandAsync("dumpheap -stat");
        result.RawOutput = heapOutput;

        if (!heapOutput.Contains("error") && !heapOutput.Contains("not found"))
        {
            // SOS is loaded, parse managed heap
            ParseDumpHeapStats(heapOutput, result);
        }
        else
        {
            // Fall back to memory analysis
            var memoryOutput = await ExecuteCommandAsync("memory region --all");
            result.RawOutput = memoryOutput;

            // Parse memory regions
            long totalSize = 0;
            var regionMatches = Regex.Matches(memoryOutput, @"\[(0x[0-9a-f]+)-(0x[0-9a-f]+)\)", RegexOptions.IgnoreCase);
            foreach (Match match in regionMatches)
            {
                if (TryParseHexOrDecimal(match.Groups[1].Value, out var start) &&
                    TryParseHexOrDecimal(match.Groups[2].Value, out var end))
                {
                    totalSize += (end - start);
                }
            }

            result.TotalHeapSizeBytes = totalSize;
            result.Recommendations.Add("Load SOS extension for detailed .NET heap analysis.");
        }
    }

    private async Task AnalyzeGcLldbAsync(GcAnalysisResult result)
    {
        // Try SOS commands
        var eeheapOutput = await ExecuteCommandAsync("eeheap -gc");
        result.RawOutput = eeheapOutput;

        if (!eeheapOutput.Contains("error") && !eeheapOutput.Contains("not found"))
        {
            ParseEeheapGc(eeheapOutput, result);
        }

        // Try finalizer queue
        var finalizerOutput = await ExecuteCommandAsync("finalizequeue");
        if (!finalizerOutput.Contains("error"))
        {
            result.RawOutput += "\n=== Finalizer Queue ===\n" + finalizerOutput;
            ParseFinalizerQueue(finalizerOutput, result);
        }
    }

    private async Task AnalyzeContentionLldbAsync(ContentionAnalysisResult result)
    {
        var output = new System.Text.StringBuilder();

        // Get thread list with stop reasons
        var threadOutput = await ExecuteCommandAsync("thread list");
        output.AppendLine("=== thread list ===").AppendLine(threadOutput);

        // Get backtraces to identify waiting threads
        var backtraceOutput = await ExecuteCommandAsync("bt all");
        output.AppendLine("=== bt all ===").AppendLine(backtraceOutput);

        // Try SOS syncblk if available
        var syncblkOutput = await ExecuteCommandAsync("syncblk");
        if (!syncblkOutput.Contains("error") && !syncblkOutput.Contains("not found"))
        {
            output.AppendLine("=== syncblk ===").AppendLine(syncblkOutput);
            ParseSyncBlocks(syncblkOutput, result);
        }

        result.RawOutput = output.ToString();

        // Parse waiting threads from backtraces
        var lockPatterns = new[] { "pthread_mutex", "semaphore", "__psynch", "os_unfair_lock", "dispatch_semaphore" };

        var threadSections = Regex.Split(backtraceOutput, @"(?=\* thread #|\s+thread #)");
        foreach (var section in threadSections)
        {
            if (string.IsNullOrWhiteSpace(section))
                continue;

            var threadMatch = Regex.Match(section, @"thread #(\d+)", RegexOptions.IgnoreCase);
            if (!threadMatch.Success)
                continue;

            var threadId = threadMatch.Groups[1].Value;
            var isWaiting = lockPatterns.Any(p => section.Contains(p, StringComparison.OrdinalIgnoreCase));

            if (isWaiting)
            {
                var waitReason = lockPatterns.FirstOrDefault(p => section.Contains(p, StringComparison.OrdinalIgnoreCase)) ?? "Lock wait";

                result.WaitingThreads.Add(new WaitingThread
                {
                    ThreadId = threadId,
                    WaitReason = waitReason
                });
            }
        }

        // Detect high contention
        result.HighContention = result.WaitingThreads.Count >= 3;
        result.DeadlockDetected = result.WaitingThreads.Count >= 2 &&
                                  result.WaitingThreads.All(t => t.WaitReason.Contains("mutex", StringComparison.OrdinalIgnoreCase));
    }

    // === Helper Methods ===

    private void IdentifyHotFunctions(string stacksOutput, CpuAnalysisResult result)
    {
        // Count function occurrences across all threads
        var functionCounts = new Dictionary<string, int>();
        var stackCount = 0;

        // Match function names in various formats
        // WinDbg: "module!function+0x123" or "module!function"
        //         Example: ntdll!NtWaitForSingleObject+0x14
        // LLDB:   "module`function + 123" or "module`function"
        //         Example: libcoreclr.so`___lldb_unnamed_symbol16749 + 636
        //
        // Module names can contain dots (libcoreclr.so, System.Private.CoreLib.dll)
        // Function names can contain dots, underscores, angle brackets (generic types)

        // Pattern explanation:
        // ([\w.\-]+) - Module name: word chars, dots, hyphens (e.g., libcoreclr.so, System.Private.CoreLib.dll)
        // [!`]       - Separator: ! for WinDbg, ` for LLDB
        // ([\w<>.,\[\]]+) - Function name: word chars, angle brackets (generics), dots, commas, brackets
        // (?:\s*\+|\s*$) - Followed by + (offset) or end of match (with optional whitespace)
        var functionMatches = Regex.Matches(stacksOutput, @"([\w.\-]+)[!`]([\w<>.,\[\]`]+?)(?:\s*\+|\s+at\s|\s*$)", RegexOptions.Multiline);

        foreach (Match match in functionMatches)
        {
            var moduleName = match.Groups[1].Value;
            var functionName = match.Groups[2].Value;

            // Clean up function name - remove trailing backtick if present (LLDB artifact)
            functionName = functionName.TrimEnd('`');

            // Skip if we got invalid matches
            if (string.IsNullOrWhiteSpace(moduleName) || string.IsNullOrWhiteSpace(functionName))
                continue;

            var fullName = $"{moduleName}!{functionName}";

            if (!functionCounts.ContainsKey(fullName))
            {
                functionCounts[fullName] = 0;
            }
            functionCounts[fullName]++;
        }

        // Count total stacks (each thread has its own stack trace)
        // WinDbg: "# Child-SP" header
        // LLDB: "thread #N" or "* thread #N"
        var stackHeaders = Regex.Matches(stacksOutput, @"(#\s*Child-SP|(?:^|\n)\s*\*?\s*thread\s+#|OS Thread Id)", RegexOptions.IgnoreCase);
        stackCount = Math.Max(stackHeaders.Count, 1);

        // Convert to hot functions list
        result.HotFunctions = functionCounts
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .Select(kv =>
            {
                var parts = kv.Key.Split('!');
                return new HotFunction
                {
                    Module = parts[0],
                    Function = parts.Length > 1 ? parts[1] : kv.Key,
                    HitCount = kv.Value,
                    Percentage = stackCount > 0 ? (double)kv.Value / stackCount * 100 : 0
                };
            })
            .ToList();

        // Identify potential spin loops (same function appearing many times on one stack)
        var spinPatterns = new[] { "SpinWait", "SpinLock", "SpinUntil", "Interlocked", "_spin" };
        foreach (var func in result.HotFunctions)
        {
            if (spinPatterns.Any(p => func.Function.Contains(p, StringComparison.OrdinalIgnoreCase)) ||
                func.Percentage > 50)
            {
                result.PotentialSpinLoops.Add($"{func.Module}!{func.Function} ({func.Percentage:F1}% of stacks)");
            }
        }
    }

    private void CountThreadStates(string threadsOutput, CpuAnalysisResult result)
    {
        // Count threads from WinDbg ~ output
        var threadMatches = Regex.Matches(threadsOutput, @"^\s*[.#\s]\s*(\d+)\s+Id:", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        result.TotalThreads = threadMatches.Count;

        // Count active threads (not suspended)
        var frozenCount = Regex.Matches(threadsOutput, @"Frozen|Suspend", RegexOptions.IgnoreCase).Count;
        result.ActiveThreads = result.TotalThreads - frozenCount;
    }

    private async Task<string> ExecuteCommandAsync(string command)
    {
        try
        {
            return await Task.Run(() => _manager.ExecuteCommand(command));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryParseHexOrDecimal(string value, out long result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        value = value.Trim().Replace("`", "");

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(value.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        }

        return long.TryParse(value, out result);
    }

    // === Recommendation Generation ===

    private void GenerateCpuRecommendations(CpuAnalysisResult result)
    {
        if (result.PotentialSpinLoops.Count > 0)
        {
            result.Recommendations.Add($"Potential spin loops detected: {string.Join(", ", result.PotentialSpinLoops.Take(3))}. Consider using proper synchronization.");
        }

        if (result.TotalThreads > 100)
        {
            result.Recommendations.Add($"High thread count ({result.TotalThreads}). Consider using thread pools or reducing concurrency.");
        }

        if (result.HotFunctions.Any(f => f.Percentage > 30))
        {
            var hot = result.HotFunctions.First(f => f.Percentage > 30);
            result.Recommendations.Add($"Function {hot.Module}!{hot.Function} appears on {hot.Percentage:F1}% of stacks. Review for optimization.");
        }

        if (result.ThreadCpuUsage.Any() && result.ThreadCpuUsage[0].TotalTime > 60000)
        {
            result.Recommendations.Add($"Thread {result.ThreadCpuUsage[0].ThreadId} has high CPU time ({result.ThreadCpuUsage[0].TotalTime}ms). May be a runaway thread.");
        }
    }

    private void GenerateAllocationRecommendations(AllocationAnalysisResult result)
    {
        if (result.StringStats?.ExcessiveAllocations == true)
        {
            result.Recommendations.Add($"Excessive string allocations ({result.StringStats.Count:N0} strings). Consider using StringBuilder or string pooling.");
        }

        if (result.LargeObjectAllocations.Count > 0)
        {
            var totalLoh = result.LargeObjectAllocations.Sum(a => a.TotalSizeBytes);
            result.Recommendations.Add($"Large Object Heap has {result.LargeObjectAllocations.Count} types totaling {totalLoh:N0} bytes. Consider ArrayPool<T> for large arrays.");
        }

        if (result.PotentialLeaks.Count > 0)
        {
            var top = result.PotentialLeaks.First();
            result.Recommendations.Add($"High instance count for {top.TypeName} ({top.Count:N0} instances). Review for potential accumulation - use compare command with two dumps to confirm leaks.");
        }

        if (result.ArrayStats?.LargeArrayCount > 10)
        {
            result.Recommendations.Add($"{result.ArrayStats.LargeArrayCount} large arrays on LOH. Consider ArrayPool<T> to reduce LOH fragmentation.");
        }

        if (result.TotalHeapSizeBytes > 1_000_000_000) // > 1GB
        {
            result.Recommendations.Add($"Total heap size is {result.TotalHeapSizeBytes:N0} bytes (>1GB). Review memory usage patterns.");
        }
    }

    private void GenerateGcRecommendations(GcAnalysisResult result)
    {
        if (result.HighGcPressure)
        {
            result.Recommendations.Add("High GC pressure detected. Consider reducing allocations or increasing Gen0 budget.");
        }

        if (result.FragmentationPercent > 30)
        {
            result.Recommendations.Add($"Heap fragmentation is {result.FragmentationPercent:F1}%. Consider compacting GC or object pooling.");
        }

        if (result.FinalizerQueueLength > 100)
        {
            result.Recommendations.Add($"Large finalizer queue ({result.FinalizerQueueLength} objects). Implement IDisposable and call Dispose().");
        }

        if (result.FinalizerThreadBlocked)
        {
            result.Recommendations.Add("Finalizer thread appears blocked. This can cause memory buildup. Check finalizer implementations.");
        }

        if (result.PinnedObjectCount > 100)
        {
            result.Recommendations.Add($"High pinned object count ({result.PinnedObjectCount}). Consider reducing pinning or using Pinned Object Heap.");
        }

        if (result.LohSizeBytes > result.TotalHeapSizeBytes * 0.3)
        {
            result.Recommendations.Add("LOH is over 30% of total heap. Large object allocations may cause fragmentation.");
        }
    }

    private void GenerateContentionRecommendations(ContentionAnalysisResult result)
    {
        if (result.DeadlockDetected)
        {
            result.Recommendations.Add("⚠️ DEADLOCK DETECTED! Review lock acquisition order across threads.");
            if (result.DeadlockThreads.Count > 0)
            {
                result.Recommendations.Add($"Deadlocked threads: {string.Join(", ", result.DeadlockThreads)}");
            }
        }

        if (result.HighContention)
        {
            result.Recommendations.Add("High lock contention detected. Consider lock-free data structures or finer-grained locking.");
        }

        if (result.ContentedLocks.Count > 0)
        {
            var mostContended = result.ContentedLocks.OrderByDescending(l => l.WaiterCount).First();
            result.Recommendations.Add($"Lock at {mostContended.Address} ({mostContended.LockType}) has {mostContended.WaiterCount} waiters.");
        }

        if (result.WaitingThreads.Count > result.TotalLockCount * 2)
        {
            result.Recommendations.Add("Many threads waiting on few locks. Consider reducing lock scope or using reader-writer locks.");
        }

        var monitorWaiters = result.WaitingThreads.Count(t => t.WaitReason.Contains("Monitor", StringComparison.OrdinalIgnoreCase));
        if (monitorWaiters > 3)
        {
            result.Recommendations.Add($"{monitorWaiters} threads waiting on Monitor.Enter. Consider using SemaphoreSlim or other async primitives.");
        }
    }

    private void GenerateOverallSummary(PerformanceAnalysisResult result)
    {
        var summary = new List<string>();

        // CPU summary
        if (result.CpuAnalysis != null)
        {
            summary.Add($"CPU: {result.CpuAnalysis.TotalThreads} threads, {result.CpuAnalysis.ActiveThreads} active");
            if (result.CpuAnalysis.PotentialSpinLoops.Count > 0)
            {
                summary.Add($"⚠️ {result.CpuAnalysis.PotentialSpinLoops.Count} potential spin loops");
            }
        }

        // Memory summary
        if (result.AllocationAnalysis != null)
        {
            var heapMb = result.AllocationAnalysis.TotalHeapSizeBytes / (1024.0 * 1024.0);
            summary.Add($"Memory: {heapMb:F1} MB heap, {result.AllocationAnalysis.TotalObjectCount:N0} objects");
            if (result.AllocationAnalysis.PotentialLeaks.Count > 0)
            {
                summary.Add($"⚠️ {result.AllocationAnalysis.PotentialLeaks.Count} types may be leaking");
            }
        }

        // GC summary
        if (result.GcAnalysis != null)
        {
            summary.Add($"GC: {result.GcAnalysis.GcMode} mode, {result.GcAnalysis.FragmentationPercent:F1}% fragmentation");
            if (result.GcAnalysis.HighGcPressure)
            {
                summary.Add("⚠️ High GC pressure");
            }
        }

        // Contention summary
        if (result.ContentionAnalysis != null)
        {
            summary.Add($"Contention: {result.ContentionAnalysis.TotalLockCount} locks, {result.ContentionAnalysis.WaitingThreads.Count} waiting threads");
            if (result.ContentionAnalysis.DeadlockDetected)
            {
                summary.Add("⚠️ DEADLOCK DETECTED");
            }
            else if (result.ContentionAnalysis.HighContention)
            {
                summary.Add("⚠️ High contention");
            }
        }

        result.Summary = string.Join(". ", summary) + ".";

        // Aggregate recommendations
        if (result.CpuAnalysis?.Recommendations != null)
            result.Recommendations.AddRange(result.CpuAnalysis.Recommendations);
        if (result.AllocationAnalysis?.Recommendations != null)
            result.Recommendations.AddRange(result.AllocationAnalysis.Recommendations);
        if (result.GcAnalysis?.Recommendations != null)
            result.Recommendations.AddRange(result.GcAnalysis.Recommendations);
        if (result.ContentionAnalysis?.Recommendations != null)
            result.Recommendations.AddRange(result.ContentionAnalysis.Recommendations);

        // Deduplicate
        result.Recommendations = result.Recommendations.Distinct().ToList();
    }

    /// <summary>
    /// Converts the analysis result to JSON.
    /// </summary>
    public static string ToJson(PerformanceAnalysisResult result)
    {
        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }
}
