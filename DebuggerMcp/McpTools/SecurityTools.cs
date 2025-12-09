using System.ComponentModel;
using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Security;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DebuggerMcp.McpTools;

/// <summary>
/// MCP tools for security vulnerability detection.
/// </summary>
/// <remarks>
/// Provides tools for:
/// <list type="bullet">
/// <item><description>Full security analysis (vulnerabilities, memory protections, exploit patterns)</description></item>
/// <item><description>Getting information about detectable vulnerability types</description></item>
/// </list>
/// 
/// Security analysis can detect buffer overflows, use-after-free, heap corruption,
/// and other memory safety issues.
/// </remarks>
[McpServerToolType]
public class SecurityTools(
    DebuggerSessionManager sessionManager,
    SymbolManager symbolManager,
    WatchStore watchStore,
    ILogger<SecurityTools> logger)
    : DebuggerToolsBase(sessionManager, symbolManager, watchStore, logger)
{
    /// <summary>
    /// JSON serialization options for security results.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Performs comprehensive security vulnerability analysis.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <returns>Security analysis results in JSON format.</returns>
    /// <remarks>
    /// Analyzes the dump for:
    /// - Buffer overflow indicators
    /// - Use-after-free patterns
    /// - Double-free detection
    /// - Null pointer dereferences
    /// - Heap corruption
    /// - Code execution in non-executable regions
    /// - Memory protection analysis (ASLR, DEP/NX, Stack Canaries, SafeSEH)
    /// - Exploit pattern detection
    /// 
    /// IMPORTANT: A dump file must be open before calling this tool (use OpenDump first).
    /// </remarks>
    [McpServerTool, Description("Analyze dump for security vulnerabilities (buffer overflows, use-after-free, heap corruption, etc.)")]
    public async Task<string> AnalyzeSecurity(
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

        // Create security analyzer and perform analysis
        var analyzer = new SecurityAnalyzer(manager);
        var result = await analyzer.AnalyzeSecurityAsync();

        // Return JSON formatted result
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <summary>
    /// Gets information about the security vulnerability types that can be detected.
    /// </summary>
    /// <returns>List of detectable vulnerability types and their descriptions.</returns>
    /// <remarks>
    /// Returns information about:
    /// - Memory corruption vulnerabilities (buffer overflow, use-after-free, etc.)
    /// - Memory protection mechanisms (ASLR, DEP, Stack Canaries)
    /// - Exploit patterns that can be detected
    /// 
    /// This is informational and doesn't require an open dump.
    /// </remarks>
    [McpServerTool, Description("Get list of security vulnerability types that can be detected")]
    public string GetSecurityCheckCapabilities()
    {
        // Return static information about capabilities - no session needed
        var capabilities = new
        {
            VulnerabilityTypes = new[]
            {
                new { Type = "BufferOverflow", Description = "Stack or heap buffer overrun detection", Severity = "Critical" },
                new { Type = "UseAfterFree", Description = "Access to freed memory detection", Severity = "Critical" },
                new { Type = "DoubleFree", Description = "Multiple free of same memory detection", Severity = "Critical" },
                new { Type = "NullPointerDereference", Description = "Null pointer access detection", Severity = "High" },
                new { Type = "HeapCorruption", Description = "Heap metadata corruption detection", Severity = "Critical" },
                new { Type = "StackCorruption", Description = "Stack frame corruption detection", Severity = "Critical" },
                new { Type = "IntegerOverflow", Description = "Integer overflow leading to memory issues", Severity = "High" },
                new { Type = "FormatString", Description = "Format string vulnerability detection", Severity = "High" },
                new { Type = "UninitializedMemory", Description = "Use of uninitialized memory detection", Severity = "Medium" },
                new { Type = "TypeConfusion", Description = "Object type confusion detection", Severity = "High" }
            },
            MemoryProtections = new[]
            {
                new { Protection = "ASLR", Description = "Address Space Layout Randomization - randomizes memory addresses" },
                new { Protection = "DEP/NX", Description = "Data Execution Prevention - prevents code execution in data regions" },
                new { Protection = "StackCanary", Description = "Stack buffer overflow detection cookies" },
                new { Protection = "SafeSEH", Description = "Safe Structured Exception Handling (Windows)" },
                new { Protection = "CFG", Description = "Control Flow Guard - validates indirect call targets" }
            },
            ExploitPatterns = new[]
            {
                "ROP (Return-Oriented Programming) gadget chains",
                "Heap spray patterns",
                "Shell code signatures",
                "Known vulnerability signatures"
            }
        };

        return JsonSerializer.Serialize(capabilities, JsonOptions);
    }
}
