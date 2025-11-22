using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
                _logger?.LogDebug("ProcessInfoExtractor: Could not find main frame with argv address");
                return null;
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
}

