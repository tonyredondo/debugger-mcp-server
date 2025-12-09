using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Extracts process startup information (argv, envp) from core dumps using LLDB.
/// This class parses the memory layout of the process to extract command-line arguments
/// and environment variables that were set when the process started.
/// </summary>
public class ProcessInfoExtractor
{
    private readonly ILogger? _logger;

    /// <summary>
    /// Default number of pointers to read from memory.
    /// Should be enough for most cases (argv + envp combined).
    /// </summary>
    private const int DefaultPointerCount = 512;

    /// <summary>
    /// Maximum number of arguments to extract.
    /// </summary>
    private const int MaxArguments = 1000;

    /// <summary>
    /// Maximum number of environment variables to extract.
    /// </summary>
    private const int MaxEnvironmentVariables = 2000;

    /// <summary>
    /// Maximum string length to read (truncate longer strings).
    /// </summary>
    private const int MaxStringLength = 32768;

    /// <summary>
    /// Placeholder text for redacted sensitive values.
    /// </summary>
    private const string RedactedPlaceholder = "<redacted>";

    /// <summary>
    /// Patterns for environment variable names that contain sensitive data.
    /// These patterns are matched case-insensitively against the key part of "KEY=value".
    /// </summary>
    private static readonly string[] SensitiveKeyPatterns = new[]
    {
        // API and authentication
        "API_KEY", "APIKEY", "API_SECRET", "APISECRET",
        "SECRET_KEY", "SECRETKEY", "SECRET",
        "ACCESS_KEY", "ACCESSKEY",
        "PRIVATE_KEY", "PRIVATEKEY",
        
        // Tokens
        "TOKEN", "AUTH_TOKEN", "AUTHTOKEN",
        "ACCESS_TOKEN", "ACCESSTOKEN",
        "REFRESH_TOKEN", "REFRESHTOKEN",
        "BEARER_TOKEN", "BEARERTOKEN",
        "JWT_TOKEN", "JWTTOKEN", "JWT",
        
        // Passwords and credentials
        "PASSWORD", "PASSWD", "PWD",
        "CREDENTIAL", "CREDENTIALS",
        "LOGIN_PASSWORD", "LOGINPASSWORD",
        
        // Connection strings (often contain passwords)
        "CONNECTION_STRING", "CONNECTIONSTRING",
        "CONN_STRING", "CONNSTRING",
        
        // Cloud provider secrets
        "AWS_SECRET", "AZURE_SECRET", "GCP_SECRET",
        "AWS_SESSION_TOKEN",
        
        // Database
        "DB_PASSWORD", "DBPASSWORD", "DATABASE_PASSWORD",
        "MYSQL_PASSWORD", "POSTGRES_PASSWORD", "REDIS_PASSWORD",
        "MONGO_PASSWORD", "MONGODB_PASSWORD",
        
        // Signing and encryption
        "SIGNING_KEY", "SIGNINGKEY",
        "ENCRYPTION_KEY", "ENCRYPTIONKEY",
        "PRIVATE_CERT", "PRIVATECERT",
        "SSL_KEY", "SSLKEY", "TLS_KEY", "TLSKEY",
        
        // Service-specific
        "DD_API_KEY", "DATADOG_API_KEY",
        "NEWRELIC_LICENSE_KEY",
        "SENTRY_DSN",
        "STRIPE_SECRET",
        "TWILIO_AUTH_TOKEN",
        "SENDGRID_API_KEY",
        "GITHUB_TOKEN", "GITLAB_TOKEN",
        "NPM_TOKEN", "NUGET_API_KEY"
    };

    /// <summary>
    /// Compiled regex patterns for efficient matching.
    /// </summary>
    private static readonly Regex[] SensitiveKeyRegexes = SensitiveKeyPatterns
        .Select(p => new Regex($"(^|_){Regex.Escape(p)}(_|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled))
        .ToArray();

    /// <summary>
    /// Initializes a new instance of the <see cref="ProcessInfoExtractor"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public ProcessInfoExtractor(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Extracts process startup information from a core dump using LLDB.
    /// </summary>
    /// <param name="debuggerManager">The debugger manager (must be LLDB).</param>
    /// <param name="platformInfo">Platform info for pointer size detection.</param>
    /// <param name="backtraceOutput">Optional pre-fetched backtrace output (to avoid re-executing if cached).</param>
    /// <param name="rawCommands">Optional dictionary to store executed commands and their output.</param>
    /// <returns>ProcessInfo if extraction succeeded, null otherwise.</returns>
    public async Task<ProcessInfo?> ExtractProcessInfoAsync(
        IDebuggerManager debuggerManager,
        PlatformInfo? platformInfo,
        string? backtraceOutput = null,
        Dictionary<string, string>? rawCommands = null)
    {
        // Only works with LLDB on Linux/macOS
        if (debuggerManager.DebuggerType != "LLDB")
        {
            _logger?.LogDebug("ProcessInfoExtractor: Skipping - not LLDB debugger (type: {Type})",
                debuggerManager.DebuggerType);
            return null;
        }

        try
        {
            // Get all backtraces (may already be cached from CrashAnalyzer)
            backtraceOutput ??= await Task.Run(() => debuggerManager.ExecuteCommand("bt all"));

            // Find main frame and extract argv address
            var (argc, argvAddress) = FindMainFrame(backtraceOutput);
            if (argvAddress == null)
            {
                _logger?.LogDebug("ProcessInfoExtractor: Could not find main frame with argv address, trying stack scan fallback");
                // Fallback: scan the stack directly for environment variables
                return await ExtractFromStackScanAsync(debuggerManager, platformInfo, rawCommands);
            }

            _logger?.LogDebug("ProcessInfoExtractor: Found main frame - argc={Argc}, argv={ArgvAddress}",
                argc, argvAddress);

            // Determine pointer size from platform info (default to 64-bit)
            var pointerBytes = GetPointerByteSize(platformInfo);

            // Read memory block with correct pointer size
            var memoryCmd = $"memory read -fx -s{pointerBytes} -c{DefaultPointerCount} {argvAddress}";
            var memoryOutput = await Task.Run(() => debuggerManager.ExecuteCommand(memoryCmd));

            // Store the command in rawCommands if provided
            if (rawCommands != null && !string.IsNullOrWhiteSpace(memoryOutput))
            {
                rawCommands[memoryCmd] = memoryOutput;
            }

            if (string.IsNullOrWhiteSpace(memoryOutput))
            {
                _logger?.LogWarning("ProcessInfoExtractor: Memory read returned empty output");
                return null;
            }

            // Parse pointers from memory
            var pointerSize = platformInfo?.PointerSize ?? 64;
            var pointers = ParseMemoryBlock(memoryOutput, pointerSize);

            if (pointers.Count == 0)
            {
                _logger?.LogWarning("ProcessInfoExtractor: No pointers found in memory block");
                return null;
            }

            // Separate argv and envp pointers (split by NULL sentinel)
            var (argvPointers, envpPointers) = SplitByNullSentinel(pointers, pointerSize);

            _logger?.LogDebug("ProcessInfoExtractor: Found {ArgvCount} argv pointers and {EnvpCount} envp pointers",
                argvPointers.Count, envpPointers.Count);

            // Read strings for each pointer
            var result = new ProcessInfo
            {
                Argc = argc,
                ArgvAddress = argvAddress
            };

            // Read argv strings
            foreach (var ptr in argvPointers.Take(MaxArguments))
            {
                var value = await ReadStringAtAddressAsync(debuggerManager, ptr, rawCommands);
                if (value != null)
                {
                    result.Arguments.Add(value);
                }
            }

            // Read envp strings and redact sensitive values
            var sensitiveCount = 0;
            foreach (var ptr in envpPointers.Take(MaxEnvironmentVariables))
            {
                var value = await ReadStringAtAddressAsync(debuggerManager, ptr, rawCommands);
                if (value != null)
                {
                    var (redacted, wasRedacted) = RedactSensitiveValue(value);
                    result.EnvironmentVariables.Add(redacted);
                    if (wasRedacted) sensitiveCount++;
                }
            }

            // Set flag if any sensitive data was redacted
            if (sensitiveCount > 0)
            {
                result.SensitiveDataFiltered = true;
                _logger?.LogDebug("ProcessInfoExtractor: Redacted {Count} sensitive environment variables", sensitiveCount);
            }

            // Sort environment variables alphabetically for consistent output
            result.EnvironmentVariables.Sort(StringComparer.Ordinal);

            _logger?.LogInformation(
                "ProcessInfoExtractor: Extracted {ArgCount} arguments and {EnvCount} environment variables",
                result.Arguments.Count, result.EnvironmentVariables.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ProcessInfoExtractor: Failed to extract process information");
            return null;
        }
    }

    /// <summary>
    /// Gets the pointer size in bytes based on platform info.
    /// </summary>
    /// <param name="platformInfo">Platform info with pointer size.</param>
    /// <returns>Pointer size in bytes (8 for 64-bit, 4 for 32-bit).</returns>
    private static int GetPointerByteSize(PlatformInfo? platformInfo)
    {
        // Default to 8 bytes (64-bit) if not detected
        return (platformInfo?.PointerSize ?? 64) / 8;
    }

    /// <summary>
    /// Checks if an environment variable contains sensitive data and redacts the value if so.
    /// The key is preserved so presence of the variable is visible, but the value is hidden.
    /// </summary>
    /// <param name="envVar">Environment variable in "KEY=value" format.</param>
    /// <returns>Tuple of (potentially redacted string, wasRedacted flag).</returns>
    internal static (string redacted, bool wasRedacted) RedactSensitiveValue(string envVar)
    {
        if (string.IsNullOrEmpty(envVar))
            return (envVar, false);

        // Split into key and value
        var equalsIndex = envVar.IndexOf('=');
        if (equalsIndex <= 0)
            return (envVar, false); // No '=' found or empty key

        var key = envVar.Substring(0, equalsIndex);

        // Check if key matches any sensitive pattern
        foreach (var regex in SensitiveKeyRegexes)
        {
            if (regex.IsMatch(key))
            {
                // Redact the value but keep the key
                return ($"{key}={RedactedPlaceholder}", true);
            }
        }

        return (envVar, false);
    }

    /// <summary>
    /// Redacts sensitive values in expr command output.
    /// Handles output like: (char *) $21 = 0x... "KEY=value"
    /// If KEY matches a sensitive pattern, the value is redacted.
    /// </summary>
    /// <param name="output">The raw command output.</param>
    /// <returns>The output with sensitive values redacted.</returns>
    private static string RedactSensitiveOutputValue(string output)
    {
        if (string.IsNullOrEmpty(output))
            return output;

        // Check if this looks like an environment variable (contains "KEY=value" pattern in quotes)
        // Pattern: "SOME_KEY=some_value"
        var match = Regex.Match(output, @"""([^""=]+)=([^""]*)""");
        if (!match.Success)
            return output;

        var key = match.Groups[1].Value;

        // Check if key matches any sensitive pattern
        foreach (var regex in SensitiveKeyRegexes)
        {
            if (regex.IsMatch(key))
            {
                // Replace the value with redacted placeholder in the output
                var originalMatch = match.Value; // e.g., "DD_API_KEY=secret123"
                var redactedMatch = $"\"{key}={RedactedPlaceholder}\"";
                return output.Replace(originalMatch, redactedMatch);
            }
        }

        return output;
    }

    /// <summary>
    /// Finds the main entry point frame in the backtrace and extracts argc/argv.
    /// </summary>
    /// <param name="backtraceOutput">The output from 'bt all' command.</param>
    /// <returns>Tuple of (argc, argvAddress) or (null, null) if not found.</returns>
    internal (int? argc, string? argvAddress) FindMainFrame(string backtraceOutput)
    {
        // Patterns to match main entry points (in order of preference)
        // Examples:
        // frame #48: 0x0000c5f644a77244 dotnet`main(argc=2, argv=0x0000ffffefcba618)
        // frame #47: ... corehost_main(argc=2, argv=0x...)
        // frame #46: ... hostfxr_main(argc=2, argv=0x...)
        // frame #45: ... exe_start(argc=2, argv=0x...)
        var patterns = new[]
        {
            // dotnet`main with visible argc/argv
            @"frame\s+#\d+:.*?dotnet`main\s*\(\s*argc\s*=\s*(\d+)\s*,\s*argv\s*=\s*(0x[0-9a-fA-F]+)",
            // Generic `main (any module)
            @"frame\s+#\d+:.*?`main\s*\(\s*argc\s*=\s*(\d+)\s*,\s*argv\s*=\s*(0x[0-9a-fA-F]+)",
            // corehost_main
            @"frame\s+#\d+:.*?corehost_main\s*\(\s*argc\s*=\s*(\d+)\s*,\s*argv\s*=\s*(0x[0-9a-fA-F]+)",
            // hostfxr_main
            @"frame\s+#\d+:.*?hostfxr_main\s*\(\s*argc\s*=\s*(\d+)\s*,\s*argv\s*=\s*(0x[0-9a-fA-F]+)",
            // exe_start
            @"frame\s+#\d+:.*?exe_start\s*\(\s*argc\s*=\s*(\d+)\s*,\s*argv\s*=\s*(0x[0-9a-fA-F]+)",
            // _main (macOS)
            @"frame\s+#\d+:.*?`_main\s*\(\s*argc\s*=\s*(\d+)\s*,\s*argv\s*=\s*(0x[0-9a-fA-F]+)"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(backtraceOutput, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (match.Success)
            {
                if (int.TryParse(match.Groups[1].Value, out var argc))
                {
                    var argvAddr = match.Groups[2].Value;
                    return (argc, argvAddr);
                }
            }
        }

        // Fallback: Try to find argv address without argc (for optimized builds)
        var fallbackPattern = @"frame\s+#\d+:.*?(?:dotnet`main|`main|corehost_main|hostfxr_main|exe_start|`_main)\s*\([^)]*argv\s*=\s*(0x[0-9a-fA-F]+)";
        var fallbackMatch = Regex.Match(backtraceOutput, fallbackPattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (fallbackMatch.Success)
        {
            var argvAddr = fallbackMatch.Groups[1].Value;
            return (null, argvAddr);
        }

        return (null, null);
    }

    /// <summary>
    /// Parses LLDB memory read output to extract pointer values.
    /// </summary>
    /// <param name="memoryOutput">The output from 'memory read -fx -s[4|8] -c...' command.</param>
    /// <param name="pointerSize">Pointer size in bits (32 or 64).</param>
    /// <returns>List of pointer values as hex strings.</returns>
    internal List<string> ParseMemoryBlock(string memoryOutput, int pointerSize)
    {
        // Parse LLDB memory read output:
        // 64-bit: 0xffffefcba618: 0x0000ffffefcbbb24 0x0000ffffefcbbb2b
        // 32-bit: 0xffffefcba618: 0x12345678 0x87654321

        var pointers = new List<string>();
        var lines = memoryOutput.Split('\n');

        // Regex pattern based on pointer size
        // 64-bit: 16 hex digits, 32-bit: 8 hex digits
        var hexDigits = pointerSize == 64 ? 16 : 8;
        var pattern = $@"0x([0-9a-fA-F]{{{hexDigits}}})";

        foreach (var line in lines)
        {
            // Skip the address prefix (before the colon)
            var colonIndex = line.IndexOf(':');
            var valuePart = colonIndex >= 0 ? line.Substring(colonIndex + 1) : line;

            var matches = Regex.Matches(valuePart, pattern, RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                pointers.Add("0x" + match.Groups[1].Value);
            }
        }

        return pointers;
    }

    /// <summary>
    /// Splits pointer list into argv and envp arrays based on NULL sentinel.
    /// </summary>
    /// <param name="pointers">List of pointer values.</param>
    /// <param name="pointerSize">Pointer size in bits (32 or 64).</param>
    /// <returns>Tuple of (argv pointers, envp pointers).</returns>
    internal (List<string> argv, List<string> envp) SplitByNullSentinel(List<string> pointers, int pointerSize)
    {
        var argv = new List<string>();
        var envp = new List<string>();
        var foundFirstNull = false;

        foreach (var ptr in pointers)
        {
            // Check if pointer is NULL (all zeros)
            if (IsNullPointer(ptr, pointerSize))
            {
                if (!foundFirstNull)
                {
                    foundFirstNull = true;
                    continue; // Skip the null, move to envp
                }
                else
                {
                    break; // End of envp (second NULL)
                }
            }

            // Skip invalid pointers (too low, kernel space, etc.)
            if (!IsValidUserSpacePointer(ptr, pointerSize))
            {
                continue;
            }

            if (!foundFirstNull)
            {
                argv.Add(ptr);
            }
            else
            {
                envp.Add(ptr);
            }
        }

        return (argv, envp);
    }

    /// <summary>
    /// Checks if a pointer value represents NULL.
    /// </summary>
    /// <param name="ptr">Pointer value as hex string.</param>
    /// <param name="pointerSize">Pointer size in bits (32 or 64).</param>
    /// <returns>True if the pointer is NULL.</returns>
    internal static bool IsNullPointer(string ptr, int pointerSize)
    {
        // 64-bit: 0x0000000000000000, 32-bit: 0x00000000
        var nullPattern = pointerSize == 64 ? "0x0000000000000000" : "0x00000000";

        if (ptr.Equals(nullPattern, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Also check for short form or stripped zeros
        var trimmed = ptr.TrimStart('0', 'x', 'X');
        return string.IsNullOrEmpty(trimmed) || trimmed == "0";
    }

    /// <summary>
    /// Validates if a pointer is in valid user space memory.
    /// </summary>
    /// <param name="ptr">Pointer value as hex string.</param>
    /// <param name="pointerSize">Pointer size in bits (32 or 64).</param>
    /// <returns>True if the pointer is valid user space address.</returns>
    internal static bool IsValidUserSpacePointer(string ptr, int pointerSize)
    {
        var hexValue = ptr.TrimStart('0', 'x', 'X');
        if (string.IsNullOrEmpty(hexValue))
        {
            return false;
        }

        if (!ulong.TryParse(hexValue, NumberStyles.HexNumber, null, out var value))
        {
            return false;
        }

        // Skip NULL and very low addresses (< 0x1000 typically reserved)
        if (value < 0x1000)
        {
            return false;
        }

        // Skip kernel space based on pointer size
        if (pointerSize == 64)
        {
            // 64-bit: User space typically below 0x0000800000000000 (x64) or 0x0001000000000000 (ARM64)
            // Using a conservative upper bound that covers both
            if (value > 0x0000ffffffffffff)
            {
                return false;
            }
        }
        else
        {
            // 32-bit: User space typically below 0x80000000 (or 0xC0000000 with 3GB)
            if (value > 0xbfffffff)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads a C string at the given memory address using LLDB.
    /// </summary>
    /// <param name="debuggerManager">The debugger manager.</param>
    /// <param name="address">Memory address as hex string.</param>
    /// <param name="rawCommands">Optional dictionary to store executed commands.</param>
    /// <returns>The string value, or null if reading failed.</returns>
    private async Task<string?> ReadStringAtAddressAsync(
        IDebuggerManager debuggerManager,
        string address,
        Dictionary<string, string>? rawCommands = null)
    {
        try
        {
            // Execute: expr -- (char*)0x0000ffffefcbbb24
            var cmd = $"expr -- (char*){address}";
            var output = await Task.Run(() => debuggerManager.ExecuteCommand(cmd));

            // Store the command in rawCommands if provided (with sensitive data redacted)
            if (rawCommands != null && !string.IsNullOrWhiteSpace(output))
            {
                rawCommands[cmd] = RedactSensitiveOutputValue(output);
            }

            // Parse output: (char *) $317 = 0x0000ffffefcbbb24 "dotnet"
            // Also handle: (char *) $1 = 0x... "value with \"quotes\""
            var match = Regex.Match(output, @"\(char\s*\*\)\s*\$\d+\s*=\s*0x[0-9a-fA-F]+\s+""(.*)""$",
                RegexOptions.Singleline);

            if (match.Success)
            {
                var value = match.Groups[1].Value;

                // Unescape common escape sequences
                value = UnescapeString(value);

                // Limit string length
                if (value.Length > MaxStringLength)
                {
                    value = value.Substring(0, MaxStringLength) + "...";
                }

                return value;
            }

            // Alternative pattern for empty or simpler output
            var simpleMatch = Regex.Match(output, @"""(.*)""", RegexOptions.Singleline);
            if (simpleMatch.Success)
            {
                var value = UnescapeString(simpleMatch.Groups[1].Value);
                if (value.Length > MaxStringLength)
                {
                    value = value.Substring(0, MaxStringLength) + "...";
                }
                return value;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "ProcessInfoExtractor: Failed to read string at {Address}", address);
        }

        return null;
    }

    /// <summary>
    /// Unescapes common escape sequences in LLDB string output.
    /// </summary>
    /// <param name="value">The escaped string.</param>
    /// <returns>The unescaped string.</returns>
    internal static string UnescapeString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        // Handle common escape sequences
        // Note: Order matters - process \\\\ first and use a placeholder to avoid double-unescaping
        // e.g., "a\\nb" should become "a\nb" (backslash-n, then newline),
        // not "a\n" followed by "b"
        const string backslashPlaceholder = "\x01BACKSLASH\x01";
        var result = value
            .Replace("\\\\", backslashPlaceholder)  // Preserve escaped backslashes
            .Replace("\\n", "\n")
            .Replace("\\t", "\t")
            .Replace("\\r", "\r")
            .Replace("\\\"", "\"")
            .Replace("\\'", "'")
            .Replace("\\0", "\0")                   // Null character
            .Replace(backslashPlaceholder, "\\");   // Restore backslashes

        // Handle hex escapes like \x00, \x1a, etc.
        result = Regex.Replace(result, @"\\x([0-9a-fA-F]{2})", m =>
        {
            var hexValue = Convert.ToByte(m.Groups[1].Value, 16);
            return ((char)hexValue).ToString();
        });

        return result;
    }

    /// <summary>
    /// Fallback method to extract process info by scanning the stack directly.
    /// This is used when the main frame approach fails (e.g., for standalone .NET apps).
    /// The stack layout from high to low is: strings -> auxv -> envp pointers -> argv pointers -> argc
    /// </summary>
    /// <param name="debuggerManager">The debugger manager.</param>
    /// <param name="platformInfo">Platform info for pointer size detection.</param>
    /// <param name="rawCommands">Optional dictionary to store executed commands.</param>
    /// <returns>ProcessInfo if extraction succeeded, null otherwise.</returns>
    private async Task<ProcessInfo?> ExtractFromStackScanAsync(
        IDebuggerManager debuggerManager,
        PlatformInfo? platformInfo,
        Dictionary<string, string>? rawCommands = null)
    {
        try
        {
            _logger?.LogInformation("ProcessInfoExtractor: Attempting stack scan fallback for environment extraction");

            // Step 1: Get memory regions to find the stack
            var regionCmd = "memory region --all";
            var regionOutput = await Task.Run(() => debuggerManager.ExecuteCommand(regionCmd));

            if (rawCommands != null && !string.IsNullOrWhiteSpace(regionOutput))
            {
                rawCommands["memory region --all (for stack scan)"] = regionOutput;
            }

            if (string.IsNullOrWhiteSpace(regionOutput))
            {
                _logger?.LogWarning("ProcessInfoExtractor: Stack scan - memory region returned empty");
                return null;
            }

            // Step 2: Find the stack region (look for high-address rw- region)
            var stackRegion = FindStackRegion(regionOutput);
            if (stackRegion == null)
            {
                _logger?.LogWarning("ProcessInfoExtractor: Stack scan - could not identify stack region");
                return null;
            }

            _logger?.LogDebug("ProcessInfoExtractor: Stack scan - found stack region: 0x{Start:X}-0x{End:X}",
                stackRegion.Value.start, stackRegion.Value.end);

            // Step 3: Read memory from near the stack top where strings are stored
            // The string area is typically within the last ~8KB of the stack
            var maxScanSize = 8192ul; // 8KB should be enough for most cases
            var regionSize = stackRegion.Value.end - stackRegion.Value.start;
            
            // Cap scan size to actual region size (minus small buffer to avoid reading past region)
            var scanSize = Math.Min(maxScanSize, regionSize > 256 ? regionSize - 256 : regionSize);
            var scanStart = stackRegion.Value.end - scanSize;

            var memoryCmd = $"memory read -c{scanSize} 0x{scanStart:X}";
            var memoryOutput = await Task.Run(() => debuggerManager.ExecuteCommand(memoryCmd));

            if (rawCommands != null && !string.IsNullOrWhiteSpace(memoryOutput))
            {
                // Don't store raw output as it may contain sensitive data
                rawCommands["memory read (stack scan)"] = $"Read {scanSize} bytes from 0x{scanStart:X}";
            }

            if (string.IsNullOrWhiteSpace(memoryOutput))
            {
                _logger?.LogWarning("ProcessInfoExtractor: Stack scan - memory read returned empty");
                return null;
            }

            // Step 4: Parse the memory output to extract strings
            var strings = ParseMemoryToStrings(memoryOutput);

            if (strings.Count == 0)
            {
                _logger?.LogWarning("ProcessInfoExtractor: Stack scan - no strings found in stack memory");
                return null;
            }

            _logger?.LogDebug("ProcessInfoExtractor: Stack scan - found {Count} strings in stack", strings.Count);

            // Step 5: Separate into arguments and environment variables
            // Arguments come first (no '='), then environment variables (have '=')
            var result = new ProcessInfo();
            var foundFirstEnvVar = false;
            var sensitiveCount = 0;
            var candidateArguments = new List<string>();

            foreach (var str in strings)
            {
                // Skip empty or very short strings
                if (string.IsNullOrWhiteSpace(str) || str.Length < 2)
                    continue;

                // Check if this looks like an environment variable (contains '=')
                // Must have '=' not at start (equalsIdx > 0) and key must be valid env var format
                var equalsIdx = str.IndexOf('=');
                var potentialEnvVar = equalsIdx > 0; // '=' not at position 0 means there's a key
                
                string? envVarKey = null;
                var isValidEnvVar = false;
                
                if (potentialEnvVar)
                {
                    envVarKey = str.Substring(0, equalsIdx);
                    isValidEnvVar = IsValidEnvVarKey(envVarKey);
                }

                if (isValidEnvVar)
                {
                    // This is a real environment variable
                    foundFirstEnvVar = true;
                    
                    var (redacted, wasRedacted) = RedactSensitiveValue(str);
                    result.EnvironmentVariables.Add(redacted);
                    if (wasRedacted) sensitiveCount++;

                    if (result.EnvironmentVariables.Count >= MaxEnvironmentVariables)
                        break;
                }
                else if (!foundFirstEnvVar)
                {
                    // This is a potential argument (before we found any env vars)
                    // Collect as candidates - we'll validate them later
                    if (IsValidArgument(str))
                    {
                        candidateArguments.Add(str);

                        if (candidateArguments.Count >= MaxArguments)
                            continue; // Don't break, keep looking for env vars
                    }
                }
            }

            // Validate candidate arguments: argv[0] should look like an executable
            // Accept paths (absolute or relative) OR executable names (e.g., "Samples.BuggyBits", "dotnet")
            if (candidateArguments.Count > 0)
            {
                var firstArg = candidateArguments[0];
                
                // argv[0] can be:
                // 1. An absolute path: /path/to/executable
                // 2. A relative path: ./executable or ../bin/executable
                // 3. A path with directories: some/path/executable
                // 4. Just the executable name: "Samples.BuggyBits", "dotnet", "node"
                var looksLikeExecutable = firstArg.StartsWith('/') || 
                                          firstArg.StartsWith("./") || 
                                          firstArg.StartsWith("../") ||
                                          (firstArg.Contains('/') && !firstArg.Contains(' ')) ||
                                          IsValidExecutableName(firstArg);
                
                if (looksLikeExecutable)
                {
                    result.Arguments.AddRange(candidateArguments);
                    _logger?.LogDebug("ProcessInfoExtractor: Stack scan - accepted {Count} arguments (argv[0]={Argv0})",
                        candidateArguments.Count, firstArg);
                }
                else
                {
                    _logger?.LogDebug("ProcessInfoExtractor: Stack scan - discarded {Count} candidate arguments (first doesn't look like executable: {First})",
                        candidateArguments.Count, firstArg.Length > 50 ? firstArg.Substring(0, 50) + "..." : firstArg);
                }
            }

            // Set argc based on arguments found
            result.Argc = result.Arguments.Count;

            // Set flag if any sensitive data was redacted
            if (sensitiveCount > 0)
            {
                result.SensitiveDataFiltered = true;
                _logger?.LogDebug("ProcessInfoExtractor: Stack scan - redacted {Count} sensitive environment variables", sensitiveCount);
            }

            // Sort environment variables alphabetically for consistent output
            result.EnvironmentVariables.Sort(StringComparer.Ordinal);

            _logger?.LogInformation(
                "ProcessInfoExtractor: Stack scan - extracted {ArgCount} arguments and {EnvCount} environment variables",
                result.Arguments.Count, result.EnvironmentVariables.Count);

            // Only return if we found something useful
            if (result.Arguments.Count > 0 || result.EnvironmentVariables.Count > 0)
            {
                return result;
            }

            _logger?.LogWarning("ProcessInfoExtractor: Stack scan - no valid arguments or environment variables found");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ProcessInfoExtractor: Stack scan fallback failed");
            return null;
        }
    }

    /// <summary>
    /// Finds the stack region from memory region output.
    /// </summary>
    /// <param name="regionOutput">Output from 'memory region --all' command.</param>
    /// <returns>Stack region start and end addresses, or null if not found.</returns>
    private (ulong start, ulong end)? FindStackRegion(string regionOutput)
    {
        // Look for [stack] annotation or a high-address rw- region
        // Pattern: [0x00007ffc993fb000-0x00007ffc9957e000) rw-
        var lines = regionOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        (ulong start, ulong end)? bestCandidate = null;
        ulong highestRwEnd = 0;

        foreach (var line in lines)
        {
            // Match memory region pattern: [0xSTART-0xEND) permissions
            var match = Regex.Match(line, @"\[0x([0-9a-fA-F]+)-0x([0-9a-fA-F]+)\)\s+rw-");
            if (!match.Success)
                continue;

            if (!ulong.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, null, out var start))
                continue;
            if (!ulong.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, null, out var end))
                continue;

            // Check if explicitly marked as [stack]
            if (line.Contains("[stack]"))
            {
                _logger?.LogDebug("ProcessInfoExtractor: Found explicit [stack] region");
                return (start, end);
            }

            // Track highest rw- region as candidate (stack is typically at high addresses)
            // Check for high address range based on platform:
            // - x64 Linux: 0x7ffc-0x7fff range (start > 0x7f0000000000)
            // - ARM64: 0x0000ffff range (start > 0x0000ff0000000000 or start > 0x7f0000000000)
            // - 32-bit: 0xbf000000-0xff000000 range
            var isHighAddress = start > 0x7f0000000000 ||  // x64 Linux
                               (start > 0x0000ff0000000000 && start < 0x0001000000000000) || // ARM64 variant
                               (start > 0xbf000000 && start < 0x100000000); // 32-bit

            if (end > highestRwEnd && isHighAddress)
            {
                var size = end - start;
                // Stack regions are typically 64KB to 16MB in size
                if (size >= 0x10000 && size <= 0x1000000)
                {
                    highestRwEnd = end;
                    bestCandidate = (start, end);
                }
            }
        }

        return bestCandidate;
    }

    /// <summary>
    /// Parses LLDB memory read output to extract null-terminated strings.
    /// </summary>
    /// <param name="memoryOutput">Output from 'memory read -c...' command.</param>
    /// <returns>List of extracted strings.</returns>
    private List<string> ParseMemoryToStrings(string memoryOutput)
    {
        var strings = new List<string>();
        var currentString = new StringBuilder();

        // LLDB memory read output format:
        // 0x7ffc9957d0c0: 00 00 00 00 00 2f 70 72 6f 6a 65 63 74 2f 70 72  ...../project/pr
        // 0x7ffc9957d0d0: 6f 66 69 6c 65 72 2f 5f 62 75 69 6c 64 2f 62 69  ofiler/_build/bi

        var lines = memoryOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            // Extract the ASCII representation from the right side of the output
            // Or parse the hex bytes directly
            
            // Try to find hex bytes pattern
            var hexMatch = Regex.Match(line, @"0x[0-9a-fA-F]+:\s+((?:[0-9a-fA-F]{2}\s+)+)");
            if (!hexMatch.Success)
                continue;

            var hexPart = hexMatch.Groups[1].Value;
            var hexBytes = hexPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var hexByte in hexBytes)
            {
                if (!byte.TryParse(hexByte, NumberStyles.HexNumber, null, out var byteValue))
                    continue;

                if (byteValue == 0) // Null terminator
                {
                    if (currentString.Length > 0)
                    {
                        var str = currentString.ToString();
                        // Only keep printable strings with reasonable length
                        if (str.Length >= 1 && str.Length <= MaxStringLength && IsPrintableString(str))
                        {
                            strings.Add(str);
                        }
                        currentString.Clear();
                    }
                }
                else if (byteValue >= 0x20 && byteValue < 0x7F) // Printable ASCII
                {
                    currentString.Append((char)byteValue);
                }
                else if (byteValue == 0x09 || byteValue == 0x0A || byteValue == 0x0D) // Tab, LF, CR
                {
                    // Allow whitespace characters
                    currentString.Append((char)byteValue);
                }
                else
                {
                    // Non-printable character - might indicate end of string area or corrupted data
                    if (currentString.Length > 0)
                    {
                        var str = currentString.ToString();
                        if (str.Length >= 1 && str.Length <= MaxStringLength && IsPrintableString(str))
                        {
                            strings.Add(str);
                        }
                        currentString.Clear();
                    }
                }
            }
        }

        // Don't forget the last string if there's no final null
        if (currentString.Length > 0)
        {
            var str = currentString.ToString();
            if (str.Length <= MaxStringLength && IsPrintableString(str))
            {
                strings.Add(str);
            }
        }

        return strings;
    }

    /// <summary>
    /// Checks if a string is printable (contains mostly printable ASCII characters).
    /// </summary>
    /// <param name="str">String to check.</param>
    /// <returns>True if printable, false otherwise.</returns>
    private static bool IsPrintableString(string str)
    {
        if (string.IsNullOrEmpty(str))
            return false;

        var printableCount = 0;
        foreach (var c in str)
        {
            if ((c >= 0x20 && c < 0x7F) || c == '\t' || c == '\n' || c == '\r')
                printableCount++;
        }

        // At least 80% of characters should be printable
        return (double)printableCount / str.Length >= 0.8;
    }

    /// <summary>
    /// Validates if a string looks like a valid environment variable key.
    /// </summary>
    /// <param name="key">The key part of an environment variable.</param>
    /// <returns>True if it looks like a valid key.</returns>
    private static bool IsValidEnvVarKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length < 1)
            return false;

        // First character should be a letter or underscore
        if (!char.IsLetter(key[0]) && key[0] != '_')
            return false;

        // Rest should be alphanumeric or underscore
        foreach (var c in key)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Validates if a string looks like a valid command-line argument.
    /// More lenient than the argv[0] path check - accepts general argument patterns.
    /// </summary>
    /// <param name="arg">The argument string.</param>
    /// <returns>True if it looks like a valid argument.</returns>
    private static bool IsValidArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg))
            return false;

        // Reject very short strings (1-2 chars are likely garbage)
        // But allow 3+ chars as valid args (e.g., "-v", "foo")
        if (arg.Length < 3)
            return false;

        // Reject strings that are too long (likely not real arguments)
        if (arg.Length > 1000)
            return false;

        // Arguments typically:
        // - Start with / for absolute paths (Unix) or options
        // - Start with - or -- for flags/options
        // - Start with http for URLs
        // - Start with . for relative paths
        // - Executable names (like "Samples.BuggyBits" or "dotnet")
        
        // High confidence patterns - accept immediately
        if (arg.StartsWith('/') || arg.StartsWith("./") || arg.StartsWith("../"))
            return true; // Paths
            
        if (arg.StartsWith("--") || (arg.StartsWith('-') && arg.Length >= 2 && char.IsLetter(arg[1])))
            return true; // Command-line flags
            
        if (arg.StartsWith("http://") || arg.StartsWith("https://"))
            return true; // URLs

        // For other strings, check if they look like valid identifiers/arguments
        // Must be mostly alphanumeric with common path/argument characters
        var validChars = 0;
        var letterOrDigitCount = 0;
        
        foreach (var c in arg)
        {
            if (char.IsLetterOrDigit(c))
            {
                validChars++;
                letterOrDigitCount++;
            }
            else if (c == '/' || c == '\\' || c == '.' || c == '-' || c == '_' || 
                     c == ':' || c == '@' || c == ',' || c == ';' ||
                     c == '[' || c == ']' || c == '(' || c == ')' || c == ' ' ||
                     c == '+' || c == '=' || c == '#')
            {
                validChars++;
            }
        }

        // At least 70% of characters should be "valid" for arguments (more lenient)
        var validRatio = (double)validChars / arg.Length;
        if (validRatio < 0.7)
            return false;
        
        // At least 40% should be letters or digits (more lenient)
        var alphanumRatio = (double)letterOrDigitCount / arg.Length;
        if (alphanumRatio < 0.4)
            return false;

        // If it looks like an identifier/filename (e.g., "Samples.BuggyBits", "dotnet"),
        // accept it - these are common for argv[0]
        // Must start with a letter and have reasonable structure
        if (char.IsLetter(arg[0]) && letterOrDigitCount >= 3)
            return true;
        
        return false;
    }

    /// <summary>
    /// Validates if a string looks like a valid executable name (without path).
    /// Examples: "dotnet", "Samples.BuggyBits", "node", "python3"
    /// </summary>
    private static bool IsValidExecutableName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 3)
            return false;

        // Must start with a letter
        if (!char.IsLetter(name[0]))
            return false;

        // Should not contain spaces or special shell characters
        if (name.Contains(' ') || name.Contains('|') || name.Contains('&') || 
            name.Contains('>') || name.Contains('<') || name.Contains('$') ||
            name.Contains('`') || name.Contains('\n') || name.Contains('\r'))
            return false;

        // Count valid characters for an executable name
        var validChars = 0;
        var letterOrDigitCount = 0;
        
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                validChars++;
                letterOrDigitCount++;
            }
            else if (c == '.' || c == '-' || c == '_')
            {
                validChars++;
            }
        }

        // At least 90% should be valid chars (letters, digits, dots, dashes, underscores)
        var validRatio = (double)validChars / name.Length;
        if (validRatio < 0.9)
            return false;

        // At least 60% should be letters or digits
        var alphanumRatio = (double)letterOrDigitCount / name.Length;
        return alphanumRatio >= 0.6;
    }
}

