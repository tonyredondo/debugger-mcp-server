using System.ComponentModel;
using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Security;
using DebuggerMcp.Serialization;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.McpTools;

/// <summary>
/// MCP tools for comparing memory dumps.
/// </summary>
/// <remarks>
/// Provides tools for:
/// <list type="bullet">
/// <item><description>Full dump comparison (heap, threads, modules)</description></item>
/// <item><description>Heap/memory comparison for memory leak detection</description></item>
/// <item><description>Thread state comparison for deadlock detection</description></item>
/// <item><description>Module comparison for version change detection</description></item>
/// </list>
/// 
/// These tools require two sessions with open dumps: a baseline (before) and comparison (after).
/// </remarks>
public class ComparisonTools(
    DebuggerSessionManager sessionManager,
    SymbolManager symbolManager,
    WatchStore watchStore,
    ILogger<ComparisonTools> logger)
    : DebuggerToolsBase(sessionManager, symbolManager, watchStore, logger)
{
    /// <summary>
    /// JSON serialization options for comparison results.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializationDefaults.Indented;

    /// <summary>
    /// Compares two memory dumps to identify differences in memory, threads, and modules.
    /// </summary>
    /// <param name="baselineSessionId">Session ID for the baseline dump.</param>
    /// <param name="baselineUserId">User ID that owns the baseline session.</param>
    /// <param name="comparisonSessionId">Session ID for the comparison dump.</param>
    /// <param name="comparisonUserId">User ID that owns the comparison session.</param>
    /// <returns>Complete comparison results in JSON format.</returns>
    /// <remarks>
    /// This tool performs a comprehensive comparison including:
    /// - Heap/memory comparison (detects memory leaks)
    /// - Thread state comparison (detects deadlocks)
    /// - Module comparison (detects version changes)
    /// 
    /// Both sessions must have dump files open before calling this tool.
    /// 
    /// Example workflow (compact tools):
    /// 1. Create two sessions: session(action="create", userId="user1") → sessionA and sessionB
    /// 2. Open baseline dump: dump(action="open", sessionId=sessionA, userId="user1", dumpId="baseline-dump-id")
    /// 3. Open comparison dump: dump(action="open", sessionId=sessionB, userId="user1", dumpId="comparison-dump-id")
    /// 4. Compare: compare(kind="dumps", baselineSessionId=sessionA, baselineUserId="user1", targetSessionId=sessionB, targetUserId="user1")
    /// </remarks>
    public async Task<string> CompareDumps(
        [Description("Session ID for the baseline (older/before) dump")] string baselineSessionId,
        [Description("User ID that owns the baseline session")] string baselineUserId,
        [Description("Session ID for the comparison (newer/after) dump")] string comparisonSessionId,
        [Description("User ID that owns the comparison session")] string comparisonUserId)
    {
        // Validate input parameters
        ValidateSessionId(baselineSessionId);
        ValidateSessionId(comparisonSessionId);

        // Sanitize user IDs to prevent path traversal attacks
        var sanitizedBaselineUserId = SanitizeUserId(baselineUserId);
        var sanitizedComparisonUserId = SanitizeUserId(comparisonUserId);

        // Get both sessions with user ownership validation
        var baselineManager = GetSessionManager(baselineSessionId, sanitizedBaselineUserId);
        var comparisonManager = GetSessionManager(comparisonSessionId, sanitizedComparisonUserId);

        // Validate that both dumps are open
        ValidateDumpIsOpen(baselineManager, baselineSessionId);
        ValidateDumpIsOpen(comparisonManager, comparisonSessionId);

        // Create comparer and perform full comparison
        var comparer = new DumpComparer(baselineManager, comparisonManager);
        var result = await comparer.CompareAsync();

        // Populate session identifiers for result tracking
        result.Baseline.SessionId = baselineSessionId;
        result.Comparison.SessionId = comparisonSessionId;

        // Return JSON formatted result
        return DumpComparer.ToJson(result);
    }

    /// <summary>
    /// Compares heap/memory allocations between two dumps.
    /// </summary>
    /// <param name="baselineSessionId">Session ID for the baseline dump.</param>
    /// <param name="baselineUserId">User ID that owns the baseline session.</param>
    /// <param name="comparisonSessionId">Session ID for the comparison dump.</param>
    /// <param name="comparisonUserId">User ID that owns the comparison session.</param>
    /// <returns>Heap comparison results in JSON format.</returns>
    /// <remarks>
    /// This tool specifically compares memory usage between dumps to help identify:
    /// - Memory leaks (growing allocations)
    /// - Memory pressure (high memory usage)
    /// - Type growth patterns
    /// 
    /// For .NET dumps, this will show managed heap growth by type.
    /// For native dumps, this shows overall memory growth.
    /// </remarks>
    public async Task<string> CompareHeaps(
        [Description("Session ID for the baseline (older/before) dump")] string baselineSessionId,
        [Description("User ID that owns the baseline session")] string baselineUserId,
        [Description("Session ID for the comparison (newer/after) dump")] string comparisonSessionId,
        [Description("User ID that owns the comparison session")] string comparisonUserId)
    {
        // Validate input parameters
        ValidateSessionId(baselineSessionId);
        ValidateSessionId(comparisonSessionId);

        // Sanitize user IDs to prevent path traversal attacks
        var sanitizedBaselineUserId = SanitizeUserId(baselineUserId);
        var sanitizedComparisonUserId = SanitizeUserId(comparisonUserId);

        // Get both sessions with user ownership validation
        var baselineManager = GetSessionManager(baselineSessionId, sanitizedBaselineUserId);
        var comparisonManager = GetSessionManager(comparisonSessionId, sanitizedComparisonUserId);

        // Validate that both dumps are open
        ValidateDumpIsOpen(baselineManager, baselineSessionId);
        ValidateDumpIsOpen(comparisonManager, comparisonSessionId);

        // Create comparer and perform heap comparison only
        var comparer = new DumpComparer(baselineManager, comparisonManager);
        var heapComparison = await comparer.CompareHeapsAsync();

        // Return JSON formatted result
        return JsonSerializer.Serialize(heapComparison, JsonOptions);
    }

    /// <summary>
    /// Compares thread states between two dumps.
    /// </summary>
    /// <param name="baselineSessionId">Session ID for the baseline dump.</param>
    /// <param name="baselineUserId">User ID that owns the baseline session.</param>
    /// <param name="comparisonSessionId">Session ID for the comparison dump.</param>
    /// <param name="comparisonUserId">User ID that owns the comparison session.</param>
    /// <returns>Thread comparison results in JSON format.</returns>
    /// <remarks>
    /// This tool compares thread states to identify:
    /// - New threads (created between dumps)
    /// - Terminated threads
    /// - Thread state changes (e.g., new waiting threads)
    /// - Potential deadlock situations
    /// </remarks>
    public async Task<string> CompareThreads(
        [Description("Session ID for the baseline (older/before) dump")] string baselineSessionId,
        [Description("User ID that owns the baseline session")] string baselineUserId,
        [Description("Session ID for the comparison (newer/after) dump")] string comparisonSessionId,
        [Description("User ID that owns the comparison session")] string comparisonUserId)
    {
        // Validate input parameters
        ValidateSessionId(baselineSessionId);
        ValidateSessionId(comparisonSessionId);

        // Sanitize user IDs to prevent path traversal attacks
        var sanitizedBaselineUserId = SanitizeUserId(baselineUserId);
        var sanitizedComparisonUserId = SanitizeUserId(comparisonUserId);

        // Get both sessions with user ownership validation
        var baselineManager = GetSessionManager(baselineSessionId, sanitizedBaselineUserId);
        var comparisonManager = GetSessionManager(comparisonSessionId, sanitizedComparisonUserId);

        // Validate that both dumps are open
        ValidateDumpIsOpen(baselineManager, baselineSessionId);
        ValidateDumpIsOpen(comparisonManager, comparisonSessionId);

        // Create comparer and perform thread comparison only
        var comparer = new DumpComparer(baselineManager, comparisonManager);
        var threadComparison = await comparer.CompareThreadsAsync();

        // Return JSON formatted result
        return JsonSerializer.Serialize(threadComparison, JsonOptions);
    }

    /// <summary>
    /// Compares loaded modules between two dumps.
    /// </summary>
    /// <param name="baselineSessionId">Session ID for the baseline dump.</param>
    /// <param name="baselineUserId">User ID that owns the baseline session.</param>
    /// <param name="comparisonSessionId">Session ID for the comparison dump.</param>
    /// <param name="comparisonUserId">User ID that owns the comparison session.</param>
    /// <returns>Module comparison results in JSON format.</returns>
    /// <remarks>
    /// This tool compares loaded modules to identify:
    /// - Newly loaded modules (plugins, updates)
    /// - Unloaded modules
    /// - Module version changes
    /// - Module base address changes (ASLR)
    /// </remarks>
    public async Task<string> CompareModules(
        [Description("Session ID for the baseline (older/before) dump")] string baselineSessionId,
        [Description("User ID that owns the baseline session")] string baselineUserId,
        [Description("Session ID for the comparison (newer/after) dump")] string comparisonSessionId,
        [Description("User ID that owns the comparison session")] string comparisonUserId)
    {
        // Validate input parameters
        ValidateSessionId(baselineSessionId);
        ValidateSessionId(comparisonSessionId);

        // Sanitize user IDs to prevent path traversal attacks
        var sanitizedBaselineUserId = SanitizeUserId(baselineUserId);
        var sanitizedComparisonUserId = SanitizeUserId(comparisonUserId);

        // Get both sessions with user ownership validation
        var baselineManager = GetSessionManager(baselineSessionId, sanitizedBaselineUserId);
        var comparisonManager = GetSessionManager(comparisonSessionId, sanitizedComparisonUserId);

        // Validate that both dumps are open
        ValidateDumpIsOpen(baselineManager, baselineSessionId);
        ValidateDumpIsOpen(comparisonManager, comparisonSessionId);

        // Create comparer and perform module comparison only
        var comparer = new DumpComparer(baselineManager, comparisonManager);
        var moduleComparison = await comparer.CompareModulesAsync();

        // Return JSON formatted result
        return JsonSerializer.Serialize(moduleComparison, JsonOptions);
    }
}
