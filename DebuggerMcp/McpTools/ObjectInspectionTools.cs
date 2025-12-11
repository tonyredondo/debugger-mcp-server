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
    /// Inspects a .NET object or value type at the given address using ClrMD.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="address">The memory address of the object to inspect.</param>
    /// <param name="methodTable">Optional method table for value types. If provided, tries value type first, then reference type.</param>
    /// <param name="maxDepth">Maximum recursion depth for nested objects (default: 5, use 1 for flat).</param>
    /// <param name="maxArrayElements">Maximum array elements to show (default: 10).</param>
    /// <param name="maxStringLength">Maximum string length before truncation (default: 1024).</param>
    /// <returns>JSON representation of the object structure.</returns>
    /// <remarks>
    /// This is the unified object inspection tool, replacing both dumpobj and dumpvc.
    /// Uses ClrMD exclusively (safe, won't crash the debugger).
    /// 
    /// Features:
    /// - Recursively inspects nested objects up to maxDepth
    /// - Handles circular references with [circular reference] marker
    /// - Expands arrays showing first N elements
    /// - Truncates long strings
    /// - Resolves primitive values directly
    /// - Supports both reference types and value types (via methodTable parameter)
    /// 
    /// When methodTable is provided:
    /// 1. First tries value type extraction (like dumpvc)
    /// 2. If that fails, tries reference type extraction (like dumpobj)
    /// 3. Only returns error if both fail
    /// 
    /// Example output:
    /// {
    ///   "Address": "0xf7158ec79b48",
    ///   "Type": "MyNamespace.MyClass",
    ///   "MethodTable": "0xf755890ba770",
    ///   "Size": 48,
    ///   "Fields": [
    ///     { "Name": "_count", "Type": "System.Int32", "Value": 42 },
    ///     { "Name": "_name", "Type": "System.String", "Value": "Hello" }
    ///   ]
    /// }
    /// </remarks>
    [McpServerTool]
    [Description("Inspects a .NET object or value type. Uses ClrMD (safe, won't crash). For value types, provide methodTable.")]
    public string InspectObject(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Memory address of the object to inspect (hex)")] string address,
        [Description("Method table for value types (optional). If provided, tries VT first then RT.")] string? methodTable = null,
        [Description("Recursion depth: 1=flat, 5=full tree (default: 5)")] int maxDepth = 5,
        [Description("Maximum array elements to show (default: 10)")] int maxArrayElements = 10,
        [Description("Maximum string length before truncation (default: 1024)")] int maxStringLength = 1024)
    {
        // Validate and sanitize parameters
        var sanitizedUserId = SanitizeUserId(userId);

        if (string.IsNullOrWhiteSpace(address))
        {
            return JsonSerializer.Serialize(new { error = "Address is required" }, 
                new JsonSerializerOptions { WriteIndented = true });
        }

        // Get session with user ownership validation
        DebuggerSession session;
        try
        {
            session = GetSessionInfo(sessionId, sanitizedUserId);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, 
                new JsonSerializerOptions { WriteIndented = true });
        }

        // ClrMD is required - no SOS fallback
        if (session.ClrMdAnalyzer == null || !session.ClrMdAnalyzer.IsOpen)
        {
            return JsonSerializer.Serialize(new { error = "ClrMD analyzer not available. Dump may not be a .NET dump." }, 
                new JsonSerializerOptions { WriteIndented = true });
        }

        try
        {
            // Parse address
            var cleanAddress = address.Trim();
            if (cleanAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                cleanAddress = cleanAddress[2..];
            
            if (!ulong.TryParse(cleanAddress, System.Globalization.NumberStyles.HexNumber, null, out var addressValue))
            {
                return JsonSerializer.Serialize(new { error = $"Invalid address format: {address}" }, 
                    new JsonSerializerOptions { WriteIndented = true });
            }

            // Parse optional method table
            ulong? methodTableValue = null;
            if (!string.IsNullOrWhiteSpace(methodTable))
            {
                var cleanMt = methodTable.Trim();
                if (cleanMt.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    cleanMt = cleanMt[2..];
                
                if (ulong.TryParse(cleanMt, System.Globalization.NumberStyles.HexNumber, null, out var mtValue))
                    methodTableValue = mtValue;
                else
                    Logger.LogWarning("Invalid method table format: {MT}, ignoring", methodTable);
            }

            // Inspect using ClrMD with VT/RT fallback when MT is provided
            var result = session.ClrMdAnalyzer.InspectObject(
                addressValue, 
                methodTable: methodTableValue,
                maxDepth: maxDepth, 
                maxArrayElements: maxArrayElements, 
                maxStringLength: maxStringLength);
            
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

    /// <summary>
    /// Dumps a .NET module using ClrMD (managed code, won't crash the debugger).
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="address">The memory address of the module to inspect (hex).</param>
    /// <returns>JSON representation of the module structure.</returns>
    /// <remarks>
    /// This tool uses ClrMD instead of SOS dumpmodule command. ClrMD runs in managed code
    /// and won't crash LLDB.
    /// 
    /// Returns module information including:
    /// - Name and path
    /// - Image base address and size
    /// - PE file status and layout
    /// - Assembly information
    /// - Metadata address and size
    /// - PDB information (path, GUID, revision)
    /// - Type count
    /// - Version information
    /// </remarks>
    [McpServerTool]
    [Description("Dumps a .NET module using ClrMD. Safe alternative to SOS !dumpmodule.")]
    public string DumpModule(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Memory address of the module to inspect (hex)")] string address)
    {
        // Validate and sanitize parameters
        var sanitizedUserId = SanitizeUserId(userId);

        if (string.IsNullOrWhiteSpace(address))
        {
            return JsonSerializer.Serialize(new { error = "Address is required" }, 
                new JsonSerializerOptions { WriteIndented = true });
        }

        // Get session with user ownership validation
        DebuggerSession session;
        try
        {
            session = GetSessionInfo(sessionId, sanitizedUserId);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, 
                new JsonSerializerOptions { WriteIndented = true });
        }

        // Check if ClrMD analyzer is available
        if (session.ClrMdAnalyzer == null || !session.ClrMdAnalyzer.IsOpen)
        {
            return JsonSerializer.Serialize(new { error = "ClrMD analyzer not available. Dump may not be a .NET dump." }, 
                new JsonSerializerOptions { WriteIndented = true });
        }

        try
        {
            // Parse address - only strip 0x prefix, not leading zeros
            var cleanAddress = address.Trim();
            if (cleanAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                cleanAddress = cleanAddress[2..];
            }
            
            if (!ulong.TryParse(cleanAddress, System.Globalization.NumberStyles.HexNumber, null, out var addressValue))
            {
                return JsonSerializer.Serialize(new { error = $"Invalid address format: {address}" }, 
                    new JsonSerializerOptions { WriteIndented = true });
            }

            // Inspect using ClrMD
            var result = session.ClrMdAnalyzer.InspectModule(addressValue);
            
            if (result == null)
            {
                return JsonSerializer.Serialize(new
                {
                    error = "Failed to inspect module",
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
            Logger.LogError(ex, "Error inspecting module at {Address} with ClrMD", address);
            return JsonSerializer.Serialize(new
            {
                error = ex.Message,
                address
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    /// <summary>
    /// Lists all .NET modules using ClrMD.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <returns>JSON array of module information.</returns>
    [McpServerTool]
    [Description("Lists all .NET modules using ClrMD. Safe alternative to enumerating modules via SOS.")]
    public string ListModules(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId)
    {
        // Validate and sanitize parameters
        var sanitizedUserId = SanitizeUserId(userId);

        // Get session with user ownership validation
        DebuggerSession session;
        try
        {
            session = GetSessionInfo(sessionId, sanitizedUserId);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, 
                new JsonSerializerOptions { WriteIndented = true });
        }

        // Check if ClrMD analyzer is available
        if (session.ClrMdAnalyzer == null || !session.ClrMdAnalyzer.IsOpen)
        {
            return JsonSerializer.Serialize(new { error = "ClrMD analyzer not available. Dump may not be a .NET dump." }, 
                new JsonSerializerOptions { WriteIndented = true });
        }

        try
        {
            var modules = session.ClrMdAnalyzer.ListModules();
            
            return JsonSerializer.Serialize(new
            {
                count = modules.Count,
                modules
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error listing modules with ClrMD");
            return JsonSerializer.Serialize(new
            {
                error = ex.Message
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    /// <summary>
    /// Searches for a type by name across modules using ClrMD (safe alternative to SOS !name2ee).
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="typeName">The fully qualified type name to search for.</param>
    /// <param name="moduleName">Optional module name filter (use "*" for all modules).</param>
    /// <param name="includeAllModules">Whether to include all searched modules in the result (default: false, only shows matches).</param>
    /// <returns>JSON with type information including MethodTable, Token, EEClass.</returns>
    /// <remarks>
    /// This is the ClrMD equivalent of SOS !name2ee command.
    /// 
    /// Examples:
    /// - Search all modules: Name2EE("System.String", "*")
    /// - Search specific module: Name2EE("MyClass", "MyAssembly.dll")
    /// 
    /// Returns:
    /// - foundType: The first matching type found (MethodTable, Token, EEClass, Name)
    /// - modules: List of modules searched (optionally filtered to only matches)
    /// - totalModulesSearched: How many modules were searched
    /// - modulesWithMatch: How many modules contained the type
    /// </remarks>
    [McpServerTool]
    [Description("Searches for a type by name across modules. Safe ClrMD alternative to SOS !name2ee.")]
    public string Name2EE(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Fully qualified type name to search for")] string typeName,
        [Description("Module name filter (use '*' for all modules)")] string? moduleName = "*",
        [Description("Include all modules in result, not just matches")] bool includeAllModules = false)
    {
        // Validate and sanitize parameters
        var sanitizedUserId = SanitizeUserId(userId);

        if (string.IsNullOrWhiteSpace(typeName))
        {
            return JsonSerializer.Serialize(new { error = "Type name is required" }, 
                new JsonSerializerOptions { WriteIndented = true });
        }

        // Get session with user ownership validation
        DebuggerSession session;
        try
        {
            session = GetSessionInfo(sessionId, sanitizedUserId);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, 
                new JsonSerializerOptions { WriteIndented = true });
        }

        // Check if ClrMD analyzer is available
        if (session.ClrMdAnalyzer == null || !session.ClrMdAnalyzer.IsOpen)
        {
            return JsonSerializer.Serialize(new { error = "ClrMD analyzer not available. Dump may not be a .NET dump." }, 
                new JsonSerializerOptions { WriteIndented = true });
        }

        try
        {
            var result = session.ClrMdAnalyzer.Name2EE(typeName, moduleName);
            
            // Optionally filter to only modules with matches
            if (!includeAllModules && result.Modules != null)
            {
                result.Modules = result.Modules.Where(m => m.TypeFound != null).ToList();
            }

            return JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in Name2EE for type '{TypeName}'", typeName);
            return JsonSerializer.Serialize(new
            {
                error = ex.Message,
                typeName
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    /// <summary>
    /// Searches for a method by name within a type using ClrMD.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="typeName">The fully qualified type name.</param>
    /// <param name="methodName">The method name to search for.</param>
    /// <returns>JSON with method information including MethodDesc, NativeCode, Token, Signature.</returns>
    [McpServerTool]
    [Description("Searches for a method by name within a type. Returns MethodDesc, native code address, and signature.")]
    public string Name2EEMethod(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Fully qualified type name")] string typeName,
        [Description("Method name to search for")] string methodName)
    {
        // Validate and sanitize parameters
        var sanitizedUserId = SanitizeUserId(userId);

        if (string.IsNullOrWhiteSpace(typeName))
        {
            return JsonSerializer.Serialize(new { error = "Type name is required" }, 
                new JsonSerializerOptions { WriteIndented = true });
        }

        if (string.IsNullOrWhiteSpace(methodName))
        {
            return JsonSerializer.Serialize(new { error = "Method name is required" }, 
                new JsonSerializerOptions { WriteIndented = true });
        }

        // Get session with user ownership validation
        DebuggerSession session;
        try
        {
            session = GetSessionInfo(sessionId, sanitizedUserId);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, 
                new JsonSerializerOptions { WriteIndented = true });
        }

        // Check if ClrMD analyzer is available
        if (session.ClrMdAnalyzer == null || !session.ClrMdAnalyzer.IsOpen)
        {
            return JsonSerializer.Serialize(new { error = "ClrMD analyzer not available. Dump may not be a .NET dump." }, 
                new JsonSerializerOptions { WriteIndented = true });
        }

        try
        {
            var result = session.ClrMdAnalyzer.Name2EEMethod(typeName, methodName);

            return JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in Name2EEMethod for '{TypeName}.{MethodName}'", typeName, methodName);
            return JsonSerializer.Serialize(new
            {
                error = ex.Message,
                typeName,
                methodName
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    /// <summary>
    /// Gets managed call stacks for all threads using ClrMD.
    /// This is a fast alternative to SOS clrstack command.
    /// </summary>
    [McpServerTool]
    [Description("Gets managed call stacks for all threads using ClrMD. Fast alternative to SOS clrstack (~500ms vs 12s). Returns JSON with frames, arguments, locals, and optional registers.")]
    public string ClrStack(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Include method arguments")] bool includeArguments = true,
        [Description("Include local variables")] bool includeLocals = true,
        [Description("Include registers for top frame of each thread")] bool includeRegisters = true,
        [Description("Filter to specific OS thread ID (0 = all threads)")] uint threadId = 0)
    {
        // Validate and sanitize parameters
        var sanitizedUserId = SanitizeUserId(userId);
        
        // Get session with user ownership validation
        DebuggerSession session;
        try
        {
            session = GetSessionInfo(sessionId, sanitizedUserId);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, 
                new JsonSerializerOptions { WriteIndented = true });
        }

        // Check if ClrMD analyzer is available
        if (session.ClrMdAnalyzer?.IsOpen != true)
        {
            return JsonSerializer.Serialize(new { error = "ClrMD analyzer not available. Dump may not be a .NET dump." },
                new JsonSerializerOptions { WriteIndented = true });
        }

        try
        {
            var result = session.ClrMdAnalyzer.GetAllThreadStacks(
                includeArguments,
                includeLocals
            );

            // Filter to specific thread if requested
            if (threadId != 0)
            {
                result.Threads = result.Threads
                    .Where(t => t.OSThreadId == threadId)
                    .ToList();
            }

            // Add registers if requested
            if (includeRegisters && session.Manager is LldbManager lldb)
            {
                var threadIds = result.Threads.Select(t => t.OSThreadId);
                var registers = lldb.GetTopFrameRegisters(threadIds);

                foreach (var thread in result.Threads)
                {
                    if (registers.TryGetValue(thread.OSThreadId, out var regs))
                    {
                        thread.TopFrameRegisters = regs;
                    }
                }
            }

            return JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in ClrStack");
            return JsonSerializer.Serialize(new { error = ex.Message },
                new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
