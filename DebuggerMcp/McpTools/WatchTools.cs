using System.ComponentModel;
using System.Text.Json;
using DebuggerMcp.Security;
using DebuggerMcp.Serialization;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.McpTools;

/// <summary>
/// MCP tools for managing watch expressions.
/// </summary>
/// <remarks>
/// Provides tools for:
/// <list type="bullet">
/// <item><description>Adding watch expressions to track memory addresses, variables, or expressions</description></item>
/// <item><description>Listing all watch expressions for a dump</description></item>
/// <item><description>Evaluating watch expressions to get current values</description></item>
/// <item><description>Removing or clearing watch expressions</description></item>
/// </list>
/// 
/// Watch expressions are persisted across sessions, allowing you to track specific
/// values in a dump over multiple analysis sessions.
/// </remarks>
public class WatchTools(
    DebuggerSessionManager sessionManager,
    SymbolManager symbolManager,
    WatchStore watchStore,
    ILogger<WatchTools> logger)
    : DebuggerToolsBase(sessionManager, symbolManager, watchStore, logger)
{
    /// <summary>
    /// JSON serialization options for watch results.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializationDefaults.Indented;

    /// <summary>
    /// Adds a watch expression to track across sessions.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="expression">The expression to watch (memory address, variable, or debugger expression).</param>
    /// <param name="description">Optional description for the watch.</param>
    /// <returns>Confirmation with the watch ID.</returns>
    /// <remarks>
    /// Watch expressions can be:
    /// - Memory addresses: "0x00007ff8a1234567"
    /// - Variables: "myObject.field"
    /// - Debugger expressions: "poi(rsp+8)"
    /// 
    /// Watches are persisted and can be evaluated across different sessions
    /// when the same dump is opened.
    /// </remarks>
    public async Task<string> AddWatch(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Expression to watch (memory address, variable, or debugger expression)")] string expression,
        [Description("Optional description for the watch")] string? description = null)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);

        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        // Get the session to validate ownership and get current dump
        var manager = GetSessionManager(sessionId, sanitizedUserId);
        var session = GetSessionInfo(sessionId, sanitizedUserId);

        // Check if a dump is open - watches are associated with dumps
        ValidateDumpIsOpen(manager);

        // Validate expression is not empty
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new ArgumentException("expression cannot be null or empty", nameof(expression));
        }

        // Detect the watch type based on expression format (static method)
        var watchType = WatchEvaluator.DetectWatchType(expression);

        // Create the watch expression
        var watch = new WatchExpression
        {
            Id = Guid.NewGuid().ToString("N")[..8], // Short unique ID
            DumpId = session.CurrentDumpId!,
            Expression = expression,
            Description = description,
            Type = watchType,
            CreatedAt = DateTime.UtcNow
        };

        // Add to persistent store
        await WatchStore.AddWatchAsync(sanitizedUserId, session.CurrentDumpId!, watch);

        // Watches affect the canonical report document; invalidate any cached report so it can be regenerated.
        session.ClearCachedReport();

        return $"Watch added successfully.\n" +
               $"Watch ID: {watch.Id}\n" +
               $"Expression: {watch.Expression}\n" +
               $"Type: {watch.Type}\n" +
               $"Use EvaluateWatch or EvaluateWatches to get the current value.";
    }

    /// <summary>
    /// Lists all watch expressions for the currently open dump.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <returns>List of watch expressions in JSON format.</returns>
    /// <remarks>
    /// Returns all watches associated with the currently open dump,
    /// including their IDs, expressions, descriptions, and types.
    /// </remarks>
    public async Task<string> ListWatches(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);

        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        // Get the session to validate ownership and get current dump
        var manager = GetSessionManager(sessionId, sanitizedUserId);
        var session = GetSessionInfo(sessionId, sanitizedUserId);

        // Check if a dump is open
        ValidateDumpIsOpen(manager);

        // Get all watches for this dump
        var watches = await WatchStore.GetWatchesAsync(sanitizedUserId, session.CurrentDumpId!);

        // Return no watches message if empty
        if (watches.Count == 0)
        {
            return "No watch expressions found for this dump. Use AddWatch to add one.";
        }

        // Return JSON formatted list
        return JsonSerializer.Serialize(watches, JsonOptions);
    }

    /// <summary>
    /// Evaluates all watch expressions and returns their current values.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <returns>Evaluation results in JSON format with values and insights.</returns>
    /// <remarks>
    /// Evaluates all watches for the current dump and returns:
    /// - Current values for each watch
    /// - Any errors encountered during evaluation
    /// - Insights about suspicious values (null pointers, freed memory, etc.)
    /// </remarks>
    public async Task<string> EvaluateWatches(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);

        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        // Get the session to validate ownership and get current dump
        var manager = GetSessionManager(sessionId, sanitizedUserId);
        var session = GetSessionInfo(sessionId, sanitizedUserId);

        // Check if a dump is open
        ValidateDumpIsOpen(manager);

        // Create evaluator and evaluate all watches
        var evaluator = new WatchEvaluator(manager, WatchStore);
        var report = await evaluator.EvaluateAllAsync(sanitizedUserId, session.CurrentDumpId!);

        // Return JSON formatted results
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    /// <summary>
    /// Evaluates a specific watch expression by its ID.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="watchId">The ID of the watch to evaluate.</param>
    /// <returns>Evaluation result in JSON format.</returns>
    /// <remarks>
    /// Evaluates a single watch expression and returns its current value.
    /// Use ListWatches to find watch IDs.
    /// </remarks>
    public async Task<string> EvaluateWatch(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Watch ID from AddWatch or ListWatches")] string watchId)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);

        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        // Validate watchId is not empty
        if (string.IsNullOrWhiteSpace(watchId))
        {
            throw new ArgumentException("watchId cannot be null or empty", nameof(watchId));
        }

        // Get the session to validate ownership and get current dump
        var manager = GetSessionManager(sessionId, sanitizedUserId);
        var session = GetSessionInfo(sessionId, sanitizedUserId);

        // Check if a dump is open
        ValidateDumpIsOpen(manager);

        // Get the watch by ID
        var watch = await WatchStore.GetWatchAsync(sanitizedUserId, session.CurrentDumpId!, watchId);
        if (watch == null)
        {
            throw new InvalidOperationException($"Watch with ID '{watchId}' not found. Use ListWatches to see available watches.");
        }

        // Evaluate the watch - EvaluateAsync takes only the watch
        var evaluator = new WatchEvaluator(manager, WatchStore);
        var result = await evaluator.EvaluateAsync(watch);

        // Return JSON formatted result
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <summary>
    /// Removes a watch expression by its ID.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="watchId">The ID of the watch to remove.</param>
    /// <returns>Confirmation message.</returns>
    /// <remarks>
    /// Permanently removes the watch expression from the persistent store.
    /// </remarks>
    public async Task<string> RemoveWatch(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Watch ID from AddWatch or ListWatches")] string watchId)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);

        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        // Validate watchId is not empty
        if (string.IsNullOrWhiteSpace(watchId))
        {
            throw new ArgumentException("watchId cannot be null or empty", nameof(watchId));
        }

        // Get the session to validate ownership and get current dump
        var manager = GetSessionManager(sessionId, sanitizedUserId);
        var session = GetSessionInfo(sessionId, sanitizedUserId);

        // Check if a dump is open
        ValidateDumpIsOpen(manager);

        // Remove the watch
        var removed = await WatchStore.RemoveWatchAsync(sanitizedUserId, session.CurrentDumpId!, watchId);

        // Return appropriate message based on whether watch was found
        if (!removed)
        {
            return $"Watch with ID '{watchId}' not found.";
        }

        // Watches affect the canonical report document; invalidate any cached report so it can be regenerated.
        session.ClearCachedReport();

        return $"Watch '{watchId}' removed successfully.";
    }

    /// <summary>
    /// Clears all watch expressions for the currently open dump.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <returns>Confirmation message.</returns>
    /// <remarks>
    /// Permanently removes all watch expressions for the current dump.
    /// This action cannot be undone.
    /// </remarks>
    public async Task<string> ClearWatches(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);

        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        // Get the session to validate ownership and get current dump
        var manager = GetSessionManager(sessionId, sanitizedUserId);
        var session = GetSessionInfo(sessionId, sanitizedUserId);

        // Check if a dump is open
        ValidateDumpIsOpen(manager);

        // Get count before clearing for the message
        var count = await WatchStore.GetWatchCountAsync(sanitizedUserId, session.CurrentDumpId!);

        // Return early if no watches to clear
        if (count == 0)
        {
            return "No watch expressions to clear.";
        }

        // Clear all watches
        await WatchStore.ClearWatchesAsync(sanitizedUserId, session.CurrentDumpId!);

        // Watches affect the canonical report document; invalidate any cached report so it can be regenerated.
        session.ClearCachedReport();

        return $"Cleared {count} watch expression(s) successfully.";
    }
}
