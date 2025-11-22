using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Compares two memory dumps and identifies differences in memory state,
/// threads, modules, and other diagnostic information.
/// </summary>
/// <remarks>
/// This class enables comparison between two dumps to help identify:
/// - Memory leaks (growing allocations)
/// - Thread state changes
/// - Module loading/unloading
/// - State regression between versions
/// 
/// Example usage:
/// <code>
/// var comparer = new DumpComparer(baselineManager, comparisonManager);
/// var result = await comparer.CompareAsync();
/// Console.WriteLine(result.Summary);
/// </code>
/// </remarks>
public class DumpComparer
{
    private readonly IDebuggerManager _baselineManager;
    private readonly IDebuggerManager _comparisonManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="DumpComparer"/> class.
    /// </summary>
    /// <param name="baselineManager">The debugger manager for the baseline dump.</param>
    /// <param name="comparisonManager">The debugger manager for the comparison dump.</param>
    /// <exception cref="ArgumentNullException">Thrown when either manager is null.</exception>
    public DumpComparer(IDebuggerManager baselineManager, IDebuggerManager comparisonManager)
    {
        _baselineManager = baselineManager ?? throw new ArgumentNullException(nameof(baselineManager));
        _comparisonManager = comparisonManager ?? throw new ArgumentNullException(nameof(comparisonManager));
    }

    /// <summary>
    /// Performs a complete comparison of both dumps.
    /// </summary>
    /// <returns>A comprehensive comparison result.</returns>
    public async Task<DumpComparisonResult> CompareAsync()
    {
        var result = new DumpComparisonResult
        {
            Baseline = new DumpIdentifier
            {
                DebuggerType = _baselineManager.DebuggerType
            },
            Comparison = new DumpIdentifier
            {
                DebuggerType = _comparisonManager.DebuggerType
            }
        };

        // Command caching is automatically enabled when dumps are opened
        try
        {
            // Perform all comparisons
            result.HeapComparison = await CompareHeapsAsync();
            result.ThreadComparison = await CompareThreadsAsync();
            result.ModuleComparison = await CompareModulesAsync();

            // Generate summary and recommendations
            GenerateSummary(result);
        }
        catch (Exception ex)
        {
            result.Summary = $"Comparison failed: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// Compares heap allocations between the two dumps.
    /// </summary>
    /// <returns>Heap comparison results.</returns>
    public async Task<HeapComparison> CompareHeapsAsync()
    {
        var result = new HeapComparison();

        // Get heap stats from both dumps
        var baselineStats = await GetHeapStatsAsync(_baselineManager);
        var comparisonStats = await GetHeapStatsAsync(_comparisonManager);

        result.BaselineRaw = baselineStats.RawOutput;
        result.ComparisonRaw = comparisonStats.RawOutput;

        result.BaselineMemoryBytes = baselineStats.TotalBytes;
        result.ComparisonMemoryBytes = comparisonStats.TotalBytes;
        result.MemoryDeltaBytes = comparisonStats.TotalBytes - baselineStats.TotalBytes;

        if (baselineStats.TotalBytes > 0)
        {
            result.MemoryGrowthPercent = ((double)result.MemoryDeltaBytes / baselineStats.TotalBytes) * 100;
        }

        // Compare types
        var baselineTypes = baselineStats.TypeStats.ToDictionary(t => t.TypeName);
        var comparisonTypes = comparisonStats.TypeStats.ToDictionary(t => t.TypeName);

        // Find new types (in comparison but not baseline)
        foreach (var type in comparisonTypes)
        {
            if (!baselineTypes.ContainsKey(type.Key))
            {
                result.NewTypes.Add(type.Value);
            }
        }

        // Find removed types (in baseline but not comparison)
        foreach (var type in baselineTypes)
        {
            if (!comparisonTypes.ContainsKey(type.Key))
            {
                result.RemovedTypes.Add(type.Value);
            }
        }

        // Calculate growth for types that exist in both
        foreach (var baselineType in baselineTypes)
        {
            if (comparisonTypes.TryGetValue(baselineType.Key, out var comparisonType))
            {
                var growth = new TypeGrowthStats
                {
                    TypeName = baselineType.Key,
                    BaselineCount = baselineType.Value.Count,
                    ComparisonCount = comparisonType.Count,
                    BaselineSizeBytes = baselineType.Value.SizeBytes,
                    ComparisonSizeBytes = comparisonType.SizeBytes
                };

                // Only include types that have changed
                if (growth.CountDelta != 0 || growth.SizeDeltaBytes != 0)
                {
                    result.TypeGrowth.Add(growth);
                }
            }
        }

        // Sort by size delta (descending) to show biggest growers first
        result.TypeGrowth = result.TypeGrowth
            .OrderByDescending(t => t.SizeDeltaBytes)
            .ToList();

        // Enhanced memory leak detection using temporal comparison
        // This is more reliable than single-dump analysis because we can see GROWTH over time
        AnalyzeLeakPatterns(result);

        return result;
    }

    /// <summary>
    /// Analyzes heap comparison for memory leak patterns.
    /// This is the proper way to detect leaks - by comparing two snapshots over time.
    /// </summary>
    private static void AnalyzeLeakPatterns(HeapComparison result)
    {
        var indicators = new List<string>();
        var suspectedLeaks = new List<TypeGrowthStats>();
        var confidence = "None";

        // 1. Check overall memory growth
        if (result.MemoryDeltaBytes > 500_000_000) // > 500MB growth
        {
            indicators.Add($"Significant memory growth: +{result.MemoryDeltaBytes:N0} bytes ({result.MemoryGrowthPercent:F1}%)");
            confidence = "High";
        }
        else if (result.MemoryDeltaBytes > 100_000_000) // > 100MB growth
        {
            indicators.Add($"Notable memory growth: +{result.MemoryDeltaBytes:N0} bytes ({result.MemoryGrowthPercent:F1}%)");
            confidence = "Medium";
        }
        else if (result.MemoryGrowthPercent > 50) // > 50% growth (even if absolute is small)
        {
            indicators.Add($"High percentage growth: {result.MemoryGrowthPercent:F1}%");
            confidence = "Medium";
        }

        // 2. Analyze growing types for leak patterns
        var leakPatternTypes = new[]
        {
            ("EventHandler", "Event handlers growing - common leak source (unsubscribed handlers)"),
            ("Delegate", "Delegates growing - check for unsubscribed event handlers"),
            ("Timer", "Timer instances growing - ensure timers are disposed"),
            ("Stream", "Stream objects growing - check for undisposed streams"),
            ("HttpClient", "HttpClient instances growing - should be reused, not created per request"),
            ("DbConnection", "Database connections growing - check for connection leaks"),
            ("SqlConnection", "SQL connections growing - ensure connections are disposed"),
            ("WebSocket", "WebSocket connections growing - check for undisposed sockets"),
            ("CancellationTokenSource", "CancellationTokenSource growing - must be disposed"),
            ("Task", "Task objects accumulating - check for fire-and-forget tasks"),
        };

        foreach (var growth in result.TypeGrowth.Where(t => t.CountDelta > 0))
        {
            foreach (var (pattern, message) in leakPatternTypes)
            {
                if (growth.TypeName.Contains(pattern, StringComparison.OrdinalIgnoreCase) && growth.CountDelta > 10)
                {
                    indicators.Add($"{message}: +{growth.CountDelta:N0} instances");
                    suspectedLeaks.Add(growth);
                    if (confidence == "None") confidence = "Medium";
                }
            }
        }

        // 3. Check for unbounded collection growth (strong leak indicator)
        var collectionTypes = new[] { "List`", "Dictionary`", "HashSet`", "Queue`", "Stack`", "ConcurrentDictionary`" };
        foreach (var growth in result.TypeGrowth.Where(t => t.CountDelta > 100))
        {
            if (collectionTypes.Any(c => growth.TypeName.Contains(c)))
            {
                indicators.Add($"Collection type growing: {growth.TypeName} +{growth.CountDelta:N0} instances");
                suspectedLeaks.Add(growth);
                if (confidence != "High") confidence = "Medium";
            }
        }

        // 4. Check for large byte array growth (common in buffer leaks)
        var byteArrayGrowth = result.TypeGrowth.FirstOrDefault(t => t.TypeName == "System.Byte[]");
        if (byteArrayGrowth != null && byteArrayGrowth.SizeDeltaBytes > 50_000_000) // > 50MB of byte arrays
        {
            indicators.Add($"Large byte array growth: +{byteArrayGrowth.SizeDeltaBytes:N0} bytes - check for buffer/stream leaks");
            suspectedLeaks.Add(byteArrayGrowth);
            if (confidence != "High") confidence = "Medium";
        }

        // 5. Check for String growth (only suspicious if extreme)
        var stringGrowth = result.TypeGrowth.FirstOrDefault(t => t.TypeName == "System.String");
        if (stringGrowth != null && stringGrowth.SizeDeltaBytes > AnalysisConstants.ExtremeStringGrowthThresholdBytes && stringGrowth.CountDelta > AnalysisConstants.ExtremeStringGrowthCountThreshold)
        {
            indicators.Add($"Extreme string growth: +{stringGrowth.CountDelta:N0} strings (+{stringGrowth.SizeDeltaBytes:N0} bytes) - check for string accumulation in caches");
            suspectedLeaks.Add(stringGrowth);
        }

        // 6. Check for new types that weren't in baseline (could be leak if many instances)
        foreach (var newType in result.NewTypes.Where(t => t.Count > 1000))
        {
            indicators.Add($"New type with many instances: {newType.TypeName} ({newType.Count:N0} instances)");
        }

        // Set results
        result.LeakIndicators = indicators;
        result.SuspectedLeakingTypes = suspectedLeaks.Distinct().ToList();
        result.LeakConfidence = confidence;
        result.MemoryLeakSuspected = confidence != "None";
    }

    /// <summary>
    /// Compares thread states between the two dumps.
    /// </summary>
    /// <returns>Thread comparison results.</returns>
    public async Task<ThreadComparison> CompareThreadsAsync()
    {
        var result = new ThreadComparison();

        // Get thread info from both dumps
        var baselineThreads = await GetThreadInfoAsync(_baselineManager);
        var comparisonThreads = await GetThreadInfoAsync(_comparisonManager);

        result.BaselineThreadCount = baselineThreads.Count;
        result.ComparisonThreadCount = comparisonThreads.Count;

        // Create lookups by thread ID (normalized)
        var baselineByOsId = NormalizeThreadLookup(baselineThreads);
        var comparisonByOsId = NormalizeThreadLookup(comparisonThreads);

        // Find new threads
        foreach (var thread in comparisonByOsId)
        {
            if (!baselineByOsId.ContainsKey(thread.Key))
            {
                result.NewThreads.Add(ToComparisonInfo(thread.Value));
            }
        }

        // Find terminated threads
        foreach (var thread in baselineByOsId)
        {
            if (!comparisonByOsId.ContainsKey(thread.Key))
            {
                result.TerminatedThreads.Add(ToComparisonInfo(thread.Value));
            }
        }

        // Find threads with state changes
        foreach (var baselineThread in baselineByOsId)
        {
            if (comparisonByOsId.TryGetValue(baselineThread.Key, out var comparisonThread))
            {
                // Check if state or top function changed
                if (baselineThread.Value.State != comparisonThread.State ||
                    baselineThread.Value.TopFunction != comparisonThread.TopFunction)
                {
                    result.StateChangedThreads.Add(new ThreadStateChange
                    {
                        ThreadId = baselineThread.Key,
                        BaselineState = baselineThread.Value.State,
                        ComparisonState = comparisonThread.State,
                        BaselineTopFunction = baselineThread.Value.TopFunction,
                        ComparisonTopFunction = comparisonThread.TopFunction
                    });
                }
            }
        }

        // Identify threads waiting on locks in comparison dump
        var lockPatterns = new[] { "mutex", "semaphore", "wait", "lock", "critical", "psynch" };
        foreach (var thread in comparisonThreads)
        {
            var stateAndFunc = $"{thread.State} {thread.TopFunction}".ToLowerInvariant();
            if (lockPatterns.Any(p => stateAndFunc.Contains(p)))
            {
                result.ThreadsWaitingOnLocks.Add(ToComparisonInfo(thread));
            }
        }

        // Detect potential deadlock
        if (result.ThreadsWaitingOnLocks.Count >= 2)
        {
            // Check if more threads are waiting than before
            var baselineWaitingCount = baselineThreads.Count(t =>
            {
                var sf = $"{t.State} {t.TopFunction}".ToLowerInvariant();
                return lockPatterns.Any(p => sf.Contains(p));
            });

            if (result.ThreadsWaitingOnLocks.Count > baselineWaitingCount)
            {
                result.PotentialDeadlock = true;
            }
        }

        return result;
    }

    /// <summary>
    /// Compares loaded modules between the two dumps.
    /// </summary>
    /// <returns>Module comparison results.</returns>
    public async Task<ModuleComparison> CompareModulesAsync()
    {
        var result = new ModuleComparison();

        // Get module info from both dumps
        var baselineModules = await GetModuleInfoAsync(_baselineManager);
        var comparisonModules = await GetModuleInfoAsync(_comparisonManager);

        result.BaselineModuleCount = baselineModules.Count;
        result.ComparisonModuleCount = comparisonModules.Count;

        // Create lookups by module name (normalized)
        var baselineByName = baselineModules
            .GroupBy(m => NormalizeModuleName(m.Name))
            .ToDictionary(g => g.Key, g => g.First());

        var comparisonByName = comparisonModules
            .GroupBy(m => NormalizeModuleName(m.Name))
            .ToDictionary(g => g.Key, g => g.First());

        // Find new modules
        foreach (var module in comparisonByName)
        {
            if (!baselineByName.ContainsKey(module.Key))
            {
                result.NewModules.Add(ToComparisonInfo(module.Value));
            }
        }

        // Find unloaded modules
        foreach (var module in baselineByName)
        {
            if (!comparisonByName.ContainsKey(module.Key))
            {
                result.UnloadedModules.Add(ToComparisonInfo(module.Value));
            }
        }

        // Find version changes and rebases
        foreach (var baselineModule in baselineByName)
        {
            if (comparisonByName.TryGetValue(baselineModule.Key, out var comparisonModule))
            {
                // Check version change
                if (baselineModule.Value.Version != comparisonModule.Version &&
                    (baselineModule.Value.Version != null || comparisonModule.Version != null))
                {
                    result.VersionChanges.Add(new ModuleVersionChange
                    {
                        Name = baselineModule.Value.Name,
                        BaselineVersion = baselineModule.Value.Version,
                        ComparisonVersion = comparisonModule.Version
                    });
                }

                // Check rebase
                if (baselineModule.Value.BaseAddress != comparisonModule.BaseAddress)
                {
                    result.RebasedModules.Add(new ModuleRebaseInfo
                    {
                        Name = baselineModule.Value.Name,
                        BaselineBaseAddress = baselineModule.Value.BaseAddress,
                        ComparisonBaseAddress = comparisonModule.BaseAddress
                    });
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Gets heap statistics from a debugger manager.
    /// </summary>
    private async Task<HeapStatsInternal> GetHeapStatsAsync(IDebuggerManager manager)
    {
        var stats = new HeapStatsInternal();

        if (manager.DebuggerType == "WinDbg")
        {
            stats.RawOutput = await ExecuteCommandAsync(manager, "!heap -s");

            // Parse total committed bytes
            var committedMatches = Regex.Matches(stats.RawOutput, @"Committed bytes:\s+([0-9a-fx]+)", RegexOptions.IgnoreCase);
            foreach (Match match in committedMatches)
            {
                if (TryParseHexOrDecimal(match.Groups[1].Value, out long bytes))
                {
                    stats.TotalBytes += bytes;
                }
            }

            // Try to get .NET heap stats if SOS is loaded
            var sosHeapOutput = await ExecuteCommandAsync(manager, "!dumpheap -stat");
            if (!sosHeapOutput.Contains("No export") && !sosHeapOutput.Contains("Failed"))
            {
                stats.RawOutput += "\n\n=== .NET Heap Stats ===\n" + sosHeapOutput;
                ParseDotNetHeapStats(sosHeapOutput, stats);
            }
        }
        else if (manager.DebuggerType == "LLDB")
        {
            // Get memory regions
            stats.RawOutput = await ExecuteCommandAsync(manager, "memory region --all");

            var regionMatches = Regex.Matches(stats.RawOutput, @"\[(0x[0-9a-f]+)-(0x[0-9a-f]+)\)", RegexOptions.IgnoreCase);
            foreach (Match match in regionMatches)
            {
                if (TryParseHexOrDecimal(match.Groups[1].Value, out long start) &&
                    TryParseHexOrDecimal(match.Groups[2].Value, out long end))
                {
                    stats.TotalBytes += (end - start);
                }
            }

            // Try to get .NET heap stats if SOS is loaded
            var sosHeapOutput = await ExecuteCommandAsync(manager, "dumpheap -stat");
            if (!sosHeapOutput.Contains("error") && !sosHeapOutput.Contains("not found"))
            {
                stats.RawOutput += "\n\n=== .NET Heap Stats ===\n" + sosHeapOutput;
                ParseDotNetHeapStats(sosHeapOutput, stats);
            }
        }

        return stats;
    }

    /// <summary>
    /// Parses .NET heap statistics from !dumpheap -stat output.
    /// </summary>
    private static void ParseDotNetHeapStats(string output, HeapStatsInternal stats)
    {
        // Format: MT    Count    TotalSize Class Name
        // Example: 00007ff812345678    12345    98765432 System.String
        // Note: LLDB/SOS may format large numbers with commas: "11,193" "1,011,728"
        var lines = output.Split('\n');

        foreach (var line in lines)
        {
            // Match pattern: address/MT  count  size  typename
            // Use [\d,]+ to match numbers with or without commas
            var match = Regex.Match(line, @"^\s*([0-9a-f]+)\s+([\d,]+)\s+([\d,]+)\s+(.+)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                // Remove commas before parsing
                var count = long.Parse(match.Groups[2].Value.Replace(",", ""));
                var size = long.Parse(match.Groups[3].Value.Replace(",", ""));
                var typeName = match.Groups[4].Value.Trim();

                // Skip summary lines
                if (typeName.StartsWith("Total ") || typeName.StartsWith("Statistics:"))
                {
                    continue;
                }

                stats.TypeStats.Add(new TypeStats
                {
                    TypeName = typeName,
                    Count = count,
                    SizeBytes = size
                });
            }
        }

        // Update total from .NET heap if available
        // Handle numbers with or without commas
        var totalMatch = Regex.Match(output, @"Total\s+([\d,]+)\s+objects,\s+([\d,]+)\s+bytes", RegexOptions.IgnoreCase);
        if (totalMatch.Success && long.TryParse(totalMatch.Groups[2].Value.Replace(",", ""), out long totalBytes))
        {
            // Use .NET heap size if larger (includes managed heap)
            if (totalBytes > stats.TotalBytes)
            {
                stats.TotalBytes = totalBytes;
            }
        }
    }

    /// <summary>
    /// Gets thread information from a debugger manager.
    /// </summary>
    private async Task<List<ThreadInfo>> GetThreadInfoAsync(IDebuggerManager manager)
    {
        var threads = new List<ThreadInfo>();

        if (manager.DebuggerType == "WinDbg")
        {
            var output = await ExecuteCommandAsync(manager, "~");
            ParseWinDbgThreads(output, threads);
        }
        else if (manager.DebuggerType == "LLDB")
        {
            var output = await ExecuteCommandAsync(manager, "thread list");
            ParseLldbThreads(output, threads);
        }

        return threads;
    }

    /// <summary>
    /// Gets module information from a debugger manager.
    /// </summary>
    private async Task<List<ModuleInfo>> GetModuleInfoAsync(IDebuggerManager manager)
    {
        var modules = new List<ModuleInfo>();

        if (manager.DebuggerType == "WinDbg")
        {
            var output = await ExecuteCommandAsync(manager, "lm");
            ParseWinDbgModules(output, modules);
        }
        else if (manager.DebuggerType == "LLDB")
        {
            var output = await ExecuteCommandAsync(manager, "image list");
            ParseLldbModules(output, modules);
        }

        return modules;
    }

    /// <summary>
    /// Parses WinDbg thread output.
    /// </summary>
    private static void ParseWinDbgThreads(string output, List<ThreadInfo> threads)
    {
        var lines = output.Split('\n');
        foreach (var line in lines)
        {
            var match = Regex.Match(line,
                @"^\s*([.#\s])\s*(\d+)\s+Id:\s*([0-9a-f]+)\.([0-9a-f]+)\s+Suspend:\s*(\d+)\s+Teb:\s*([0-9a-f`]+)\s+(\w+)",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                var marker = match.Groups[1].Value.Trim();
                var debuggerThreadId = match.Groups[2].Value;
                var osThreadId = match.Groups[4].Value;
                var state = match.Groups[7].Value;

                threads.Add(new ThreadInfo
                {
                    ThreadId = $"{debuggerThreadId} ({osThreadId})",
                    State = state,
                    IsFaulting = marker == "#" || marker == "."
                });
            }
        }
    }

    /// <summary>
    /// Parses LLDB thread output.
    /// </summary>
    private static void ParseLldbThreads(string output, List<ThreadInfo> threads)
    {
        var lines = output.Split('\n');
        foreach (var line in lines)
        {
            // Note: tid can be hex (0xHEX) OR decimal depending on LLDB version/platform
            var match = Regex.Match(line,
                @"^(\*?)\s*thread\s*#(\d+):\s*tid\s*=\s*(0x[0-9a-f]+|\d+)",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                var isCurrent = match.Groups[1].Value == "*";
                var threadNum = match.Groups[2].Value;
                var tid = match.Groups[3].Value;

                // Parse stop reason
                var stopMatch = Regex.Match(line, @"stop reason\s*=\s*(.+?)(?:,|$)");
                var state = stopMatch.Success ? stopMatch.Groups[1].Value.Trim() : "Running";

                // Parse top function
                var funcMatch = Regex.Match(line, @"\S+`(\S+)");
                var topFunc = funcMatch.Success ? funcMatch.Groups[1].Value : "";

                threads.Add(new ThreadInfo
                {
                    ThreadId = $"{threadNum} ({tid})",
                    State = state,
                    TopFunction = topFunc,
                    IsFaulting = isCurrent
                });
            }
        }
    }

    /// <summary>
    /// Parses WinDbg module output.
    /// </summary>
    private static void ParseWinDbgModules(string output, List<ModuleInfo> modules)
    {
        var lines = output.Split('\n');
        foreach (var line in lines)
        {
            var match = Regex.Match(line,
                @"^\s*([0-9a-f`]+)\s+([0-9a-f`]+)\s+(\S+)\s+(?:\(([^)]+)\))?",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                var startAddr = match.Groups[1].Value.Replace("`", "");
                var moduleName = match.Groups[3].Value;
                var symbolStatus = match.Groups[4].Success ? match.Groups[4].Value : "";

                if (moduleName.Equals("module", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var hasSymbols = symbolStatus.Contains("pdb", StringComparison.OrdinalIgnoreCase) ||
                                 symbolStatus.Contains("symbols", StringComparison.OrdinalIgnoreCase);

                var versionMatch = Regex.Match(line, @"(\d+\.\d+\.\d+\.\d+)");
                string? version = versionMatch.Success ? versionMatch.Groups[1].Value : null;

                modules.Add(new ModuleInfo
                {
                    Name = moduleName,
                    BaseAddress = $"0x{startAddr}",
                    Version = version,
                    HasSymbols = hasSymbols
                });
            }
        }
    }

    /// <summary>
    /// Parses LLDB module output.
    /// </summary>
    private static void ParseLldbModules(string output, List<ModuleInfo> modules)
    {
        var lines = output.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = Regex.Match(line,
                @"^\s*\[\s*\d+\]\s+(?:([0-9A-F-]+)\s+)?(0x[0-9a-f]+)\s+(.+)$",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                var baseAddress = match.Groups[2].Value;
                var fullPath = match.Groups[3].Value.Trim();

                var moduleName = fullPath;
                var lastSlash = fullPath.LastIndexOf('/');
                if (lastSlash >= 0)
                {
                    moduleName = fullPath.Substring(lastSlash + 1);
                }

                // Check for symbols:
                // 1. .dSYM in path (macOS debug symbols)
                // 2. Check if next line has .debug/.dbg file (Linux debug symbols)
                var hasSymbols = fullPath.Contains(".dSYM", StringComparison.OrdinalIgnoreCase);
                
                // Check next line for debug info (Linux format)
                if (!hasSymbols && i + 1 < lines.Length)
                {
                    var nextLine = lines[i + 1];
                    if (!nextLine.TrimStart().StartsWith("[") && 
                        (nextLine.Contains(".debug", StringComparison.OrdinalIgnoreCase) ||
                         nextLine.Contains(".dbg", StringComparison.OrdinalIgnoreCase) ||
                         nextLine.Contains("/debug/", StringComparison.OrdinalIgnoreCase)))
                    {
                        hasSymbols = true;
                        i++; // Skip the debug line
                    }
                }

                modules.Add(new ModuleInfo
                {
                    Name = moduleName,
                    BaseAddress = baseAddress,
                    HasSymbols = hasSymbols
                });
            }
        }
    }

    /// <summary>
    /// Executes a debugger command asynchronously.
    /// </summary>
    private static async Task<string> ExecuteCommandAsync(IDebuggerManager manager, string command)
    {
        try
        {
            return await Task.Run(() => manager.ExecuteCommand(command));
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Normalizes thread lookup by extracting OS thread ID.
    /// </summary>
    private static Dictionary<string, ThreadInfo> NormalizeThreadLookup(List<ThreadInfo> threads)
    {
        var result = new Dictionary<string, ThreadInfo>();

        foreach (var thread in threads)
        {
            // Extract OS thread ID from formats like "0 (1234)" or "0 (0x1234)"
            var idMatch = Regex.Match(thread.ThreadId, @"\((?:0x)?([0-9a-f]+)\)", RegexOptions.IgnoreCase);
            var key = idMatch.Success ? idMatch.Groups[1].Value.ToLowerInvariant() : thread.ThreadId;

            if (!result.ContainsKey(key))
            {
                result[key] = thread;
            }
        }

        return result;
    }

    /// <summary>
    /// Normalizes module name for comparison.
    /// </summary>
    private static string NormalizeModuleName(string name)
    {
        // Remove common suffixes and normalize case
        var normalized = name.ToLowerInvariant();

        // Remove version suffixes like "_140", file extensions
        normalized = Regex.Replace(normalized, @"_\d+$", "");
        normalized = Regex.Replace(normalized, @"\.(dll|so|dylib|exe)$", "");

        return normalized;
    }

    /// <summary>
    /// Converts ThreadInfo to ThreadComparisonInfo.
    /// </summary>
    private static ThreadComparisonInfo ToComparisonInfo(ThreadInfo thread)
    {
        return new ThreadComparisonInfo
        {
            ThreadId = thread.ThreadId,
            State = thread.State,
            TopFunction = thread.TopFunction,
            IsFaulting = thread.IsFaulting
        };
    }

    /// <summary>
    /// Converts ModuleInfo to ModuleComparisonInfo.
    /// </summary>
    private static ModuleComparisonInfo ToComparisonInfo(ModuleInfo module)
    {
        return new ModuleComparisonInfo
        {
            Name = module.Name,
            BaseAddress = module.BaseAddress,
            Version = module.Version,
            HasSymbols = module.HasSymbols
        };
    }

    /// <summary>
    /// Tries to parse a hex or decimal number.
    /// </summary>
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

    /// <summary>
    /// Generates a summary and recommendations based on comparison results.
    /// </summary>
    private void GenerateSummary(DumpComparisonResult result)
    {
        var summary = new List<string>();
        var recommendations = new List<string>();

        // Heap summary
        if (result.HeapComparison != null)
        {
            var heap = result.HeapComparison;

            if (heap.MemoryDeltaBytes != 0)
            {
                var direction = heap.MemoryDeltaBytes > 0 ? "increased" : "decreased";
                summary.Add($"Memory {direction} by {Math.Abs(heap.MemoryDeltaBytes):N0} bytes ({heap.MemoryGrowthPercent:F1}%)");
            }
            else
            {
                summary.Add("Memory usage unchanged");
            }

            if (heap.MemoryLeakSuspected)
            {
                summary.Add($"⚠️ MEMORY LEAK SUSPECTED (Confidence: {heap.LeakConfidence})");
                
                // Add specific leak indicators as recommendations
                foreach (var indicator in heap.LeakIndicators.Take(5))
                {
                    recommendations.Add(indicator);
                }

                // Highlight suspected leaking types
                if (heap.SuspectedLeakingTypes.Count > 0)
                {
                    recommendations.Add($"Suspected leak sources ({heap.SuspectedLeakingTypes.Count} types):");
                    foreach (var leak in heap.SuspectedLeakingTypes.Take(3))
                    {
                        recommendations.Add($"  • {leak.TypeName}: +{leak.CountDelta:N0} instances (+{leak.SizeDeltaBytes:N0} bytes)");
                    }
                }
                else if (heap.TypeGrowth.Count > 0)
                {
                    var top = heap.TypeGrowth.First();
                    recommendations.Add($"Top growing type: {top.TypeName} (+{top.CountDelta:N0} instances, +{top.SizeDeltaBytes:N0} bytes)");
                }
            }

            if (heap.NewTypes.Count > 0)
            {
                summary.Add($"{heap.NewTypes.Count} new type(s) allocated");
            }

            if (heap.RemovedTypes.Count > 0)
            {
                summary.Add($"{heap.RemovedTypes.Count} type(s) freed");
            }
        }

        // Thread summary
        if (result.ThreadComparison != null)
        {
            var threads = result.ThreadComparison;

            if (threads.ThreadCountDelta != 0)
            {
                var direction = threads.ThreadCountDelta > 0 ? "increased" : "decreased";
                summary.Add($"Thread count {direction} by {Math.Abs(threads.ThreadCountDelta)} ({threads.BaselineThreadCount} → {threads.ComparisonThreadCount})");
            }

            if (threads.NewThreads.Count > 0)
            {
                summary.Add($"{threads.NewThreads.Count} new thread(s) created");
            }

            if (threads.TerminatedThreads.Count > 0)
            {
                summary.Add($"{threads.TerminatedThreads.Count} thread(s) terminated");
            }

            if (threads.StateChangedThreads.Count > 0)
            {
                summary.Add($"{threads.StateChangedThreads.Count} thread(s) changed state");
            }

            if (threads.PotentialDeadlock)
            {
                summary.Add("⚠️ POTENTIAL DEADLOCK DETECTED");
                recommendations.Add($"{threads.ThreadsWaitingOnLocks.Count} threads are waiting on locks. Review thread states and lock acquisition order.");
            }
        }

        // Module summary
        if (result.ModuleComparison != null)
        {
            var modules = result.ModuleComparison;

            if (modules.NewModules.Count > 0)
            {
                summary.Add($"{modules.NewModules.Count} module(s) loaded");
            }

            if (modules.UnloadedModules.Count > 0)
            {
                summary.Add($"{modules.UnloadedModules.Count} module(s) unloaded");
            }

            if (modules.VersionChanges.Count > 0)
            {
                summary.Add($"{modules.VersionChanges.Count} module(s) changed version");
                recommendations.Add("Module version changes detected. Ensure compatibility between versions.");
            }
        }

        result.Summary = string.Join(". ", summary) + ".";
        result.Recommendations = recommendations;
    }

    /// <summary>
    /// Converts the comparison result to JSON.
    /// </summary>
    public static string ToJson(DumpComparisonResult result)
    {
        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    /// <summary>
    /// Internal class for heap statistics collection.
    /// </summary>
    private class HeapStatsInternal
    {
        public long TotalBytes { get; set; }
        public List<TypeStats> TypeStats { get; } = new();
        public string RawOutput { get; set; } = string.Empty;
    }
}

