using System.ComponentModel;
using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Security;
using DebuggerMcp.Serialization;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.McpTools;

/// <summary>
/// MCP tools for performance profiling and analysis.
/// </summary>
/// <remarks>
/// Provides tools for:
/// <list type="bullet">
/// <item><description>CPU usage analysis (hot functions, spin loops)</description></item>
/// <item><description>Memory allocation analysis (top allocators, LOH objects)</description></item>
/// <item><description>GC behavior analysis (generations, fragmentation)</description></item>
/// <item><description>Thread contention analysis (lock contention, deadlocks)</description></item>
/// <item><description>Comprehensive performance analysis combining all above</description></item>
/// </list>
/// </remarks>
public class PerformanceTools(
    DebuggerSessionManager sessionManager,
    SymbolManager symbolManager,
    WatchStore watchStore,
    ILogger<PerformanceTools> logger)
    : DebuggerToolsBase(sessionManager, symbolManager, watchStore, logger)
{
    /// <summary>
    /// JSON serialization options for analysis results.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializationDefaults.Indented;

    /// <summary>
    /// Performs comprehensive performance analysis on the currently open dump.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="includeWatches">Include watch expression evaluations in the report.</param>
    /// <returns>JSON formatted performance analysis results.</returns>
    /// <remarks>
    /// This tool analyzes:
    /// - CPU usage (hot functions, runaway threads)
    /// - Memory allocations (top allocators, large objects)
    /// - GC behavior (generations, fragmentation)
    /// - Thread contention (lock waits, potential deadlocks)
    /// 
    /// IMPORTANT: A dump file must be open before calling this tool (use OpenDump first).
    /// SOS is auto-loaded for .NET dumps, enabling .NET specific analysis automatically.
    /// </remarks>
    public async Task<string> AnalyzePerformance(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Include watch expression evaluations in the report (default: true)")] bool includeWatches = true)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);

        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        // Get the session with user ownership validation
        var manager = GetSessionManager(sessionId, sanitizedUserId);
        var session = GetSessionInfo(sessionId, sanitizedUserId);

        // Check if a dump is open
        ValidateDumpIsOpen(manager);

        // Create analyzer and perform comprehensive performance analysis
        var analyzer = new PerformanceAnalyzer(manager);
        var result = await analyzer.AnalyzePerformanceAsync();

        // Include watch evaluations if enabled and dump has watches
        if (includeWatches && !string.IsNullOrEmpty(session.CurrentDumpId))
        {
            var hasWatches = await WatchStore.HasWatchesAsync(sanitizedUserId, session.CurrentDumpId);
            if (hasWatches)
            {
                var evaluator = new WatchEvaluator(manager, WatchStore);
                result.WatchResults = await evaluator.EvaluateAllAsync(sanitizedUserId, session.CurrentDumpId);
            }
        }

        // Return JSON formatted result
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <summary>
    /// Analyzes CPU usage to identify hot functions and runaway threads.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <returns>JSON formatted CPU analysis results.</returns>
    /// <remarks>
    /// This tool identifies:
    /// - Hot functions (high CPU usage)
    /// - Runaway threads (spinning, busy-waiting)
    /// - Potential spin loops
    /// 
    /// For static dumps, this uses thread states and call stacks to estimate CPU activity.
    /// </remarks>
    public async Task<string> AnalyzeCpuUsage(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);

        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        // Get the session with user ownership validation
        var manager = GetSessionManager(sessionId, sanitizedUserId);

        // Check if a dump is open
        ValidateDumpIsOpen(manager);

        // Create analyzer and perform CPU analysis
        var analyzer = new PerformanceAnalyzer(manager);
        var result = await analyzer.AnalyzeCpuUsageAsync();

        // Return JSON formatted result
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <summary>
    /// Analyzes memory allocations to find top allocators and large objects.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <returns>JSON formatted allocation analysis results.</returns>
    /// <remarks>
    /// This tool identifies:
    /// - Top allocating types (by count and size)
    /// - Large Object Heap (LOH) usage
    /// - Potential memory leaks
    /// 
    /// For .NET dumps, this uses !dumpheap -stat for detailed managed heap analysis.
    /// </remarks>
    public async Task<string> AnalyzeAllocations(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);

        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        // Get the session with user ownership validation
        var manager = GetSessionManager(sessionId, sanitizedUserId);

        // Check if a dump is open
        ValidateDumpIsOpen(manager);

        // Create analyzer and perform allocation analysis
        var analyzer = new PerformanceAnalyzer(manager);
        var result = await analyzer.AnalyzeAllocationsAsync();

        // Return JSON formatted result
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <summary>
    /// Analyzes garbage collection behavior and heap generations.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <returns>JSON formatted GC analysis results.</returns>
    /// <remarks>
    /// This tool analyzes:
    /// - Heap generation sizes (Gen0, Gen1, Gen2, LOH)
    /// - Fragmentation levels
    /// - Finalizer queue state
    /// - GC mode (workstation vs server)
    /// 
    /// Requires .NET dump and SOS extension loaded.
    /// </remarks>
    public async Task<string> AnalyzeGc(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);

        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        // Get the session with user ownership validation
        var manager = GetSessionManager(sessionId, sanitizedUserId);

        // Check if a dump is open
        ValidateDumpIsOpen(manager);

        // Create analyzer and perform GC analysis
        var analyzer = new PerformanceAnalyzer(manager);
        var result = await analyzer.AnalyzeGcAsync();

        // Return JSON formatted result
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <summary>
    /// Analyzes thread contention and lock usage.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <returns>JSON formatted contention analysis results.</returns>
    /// <remarks>
    /// This tool identifies:
    /// - Contended locks (high wait counts)
    /// - Threads waiting on locks
    /// - Potential deadlock patterns
    /// 
    /// For .NET dumps, this uses !syncblk for managed lock analysis.
    /// </remarks>
    public async Task<string> AnalyzeContention(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);

        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        // Get the session with user ownership validation
        var manager = GetSessionManager(sessionId, sanitizedUserId);

        // Check if a dump is open
        ValidateDumpIsOpen(manager);

        // Create analyzer and perform contention analysis
        var analyzer = new PerformanceAnalyzer(manager);
        var result = await analyzer.AnalyzeContentionAsync();

        // Return JSON formatted result
        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
