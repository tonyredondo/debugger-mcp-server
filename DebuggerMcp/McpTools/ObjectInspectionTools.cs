using System.ComponentModel;
using System.Text.Json;
using DebuggerMcp.ObjectInspection;
using DebuggerMcp.Security;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DebuggerMcp.McpTools;

/// <summary>
/// MCP tools for inspecting .NET objects in memory dumps.
/// </summary>
[McpServerToolType]
public class ObjectInspectionTools(
    DebuggerSessionManager sessionManager,
    SymbolManager symbolManager,
    WatchStore watchStore,
    ILogger<ObjectInspectionTools> logger)
    : DebuggerToolsBase(sessionManager, symbolManager, watchStore, logger)
{

    /// <summary>
    /// Inspects a .NET object at the given address and returns its structure as JSON.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="address">The memory address of the object to inspect.</param>
    /// <param name="methodTable">Optional method table for value types (used as fallback if dumpobj fails).</param>
    /// <param name="maxDepth">Maximum recursion depth for nested objects (default: 5).</param>
    /// <param name="maxArrayElements">Maximum array elements to show (default: 10).</param>
    /// <param name="maxStringLength">Maximum string length before truncation (default: 1024).</param>
    /// <returns>JSON representation of the object structure.</returns>
    /// <remarks>
    /// This tool inspects a .NET object and all its fields recursively, producing a JSON representation.
    /// 
    /// Features:
    /// - Recursively inspects nested objects up to maxDepth
    /// - Handles circular references with [this] and [seen] markers
    /// - Expands arrays showing first N elements
    /// - Truncates long strings
    /// - Resolves primitive values directly
    /// 
    /// The tool first tries dumpobj. If that fails and a methodTable is provided, it falls back to dumpvc.
    /// 
    /// Example output:
    /// {
    ///   "address": "f7158ec79b48",
    ///   "type": "MyNamespace.MyClass",
    ///   "mt": "0000f755890ba770",
    ///   "isValueType": false,
    ///   "fields": [
    ///     { "name": "_count", "type": "System.Int32", "isStatic": false, "value": 42 },
    ///     { "name": "_name", "type": "System.String", "isStatic": false, "value": "Hello" }
    ///   ]
    /// }
    /// </remarks>
    [McpServerTool]
    [Description("Inspects a .NET object at the given address and returns its structure as JSON. Recursively expands fields, handles circular references, and resolves primitive values.")]
    public async Task<string> InspectObject(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Memory address of the object to inspect (hex)")] string address,
        [Description("Method table address for value types (optional, fallback if dumpobj fails)")] string? methodTable = null,
        [Description("Maximum recursion depth for nested objects (default: 5)")] int maxDepth = ObjectInspector.DefaultMaxDepth,
        [Description("Maximum array elements to show (default: 10)")] int maxArrayElements = ObjectInspector.DefaultMaxArrayElements,
        [Description("Maximum string length before truncation (default: 1024)")] int maxStringLength = ObjectInspector.DefaultMaxStringLength)
    {
        // Validate and sanitize parameters
        var sanitizedUserId = SanitizeUserId(userId);

        if (string.IsNullOrWhiteSpace(address))
        {
            return "Error: Address is required";
        }

        // Get session with user ownership validation
        var manager = GetSessionManager(sessionId, sanitizedUserId);
        
        // Check if a dump is open
        ValidateDumpIsOpen(manager);

        // Create inspector and inspect the object
        // Command caching is automatically enabled when dump is opened
        var inspector = new ObjectInspector(Logger);

        try
        {
            var result = await inspector.InspectAsync(
                manager,
                address,
                methodTable,
                maxDepth,
                maxArrayElements,
                maxStringLength);

            if (result == null)
            {
                return JsonSerializer.Serialize(new
                {
                    error = "Failed to inspect object",
                    address
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            return JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error inspecting object at {Address}", address);
            return JsonSerializer.Serialize(new
            {
                error = ex.Message,
                address
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}

