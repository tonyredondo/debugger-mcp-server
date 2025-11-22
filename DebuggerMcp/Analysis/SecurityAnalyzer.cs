using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Analyzes crash dumps for potential security vulnerabilities.
/// Detects buffer overflows, use-after-free, heap corruption, and other security issues.
/// </summary>
public class SecurityAnalyzer
{
    private readonly IDebuggerManager _debuggerManager;
    private readonly ILogger<SecurityAnalyzer>? _logger;

    // Common exploit patterns
    private static readonly byte[] NopSled = { 0x90, 0x90, 0x90, 0x90 }; // x86/x64 NOPs
    private static readonly string[] SuspiciousPatterns = 
    {
        "41414141", // AAAA - common overflow pattern
        "42424242", // BBBB
        "43434343", // CCCC
        "90909090", // NOP sled
        "cccccccc", // INT3 (debug break)
        "deadbeef", // Common marker
        "cafebabe", // Common marker
        "0badf00d", // Bad food - Windows heap marker
        "feeefeee", // Windows freed heap
        "baadf00d", // Windows uninitialized heap
        "abababab", // Windows heap guard
    };

    // Note: Memory address thresholds are defined in AnalysisConstants
    // - NullPageThreshold: AnalysisConstants.NullPageThreshold
    // - KernelAddressStart: AnalysisConstants.KernelAddressStart

    /// <summary>
    /// Initializes a new instance of the <see cref="SecurityAnalyzer"/> class.
    /// </summary>
    /// <param name="debuggerManager">The debugger manager to use.</param>
    /// <param name="logger">Optional logger instance.</param>
    public SecurityAnalyzer(IDebuggerManager debuggerManager, ILogger<SecurityAnalyzer>? logger = null)
    {
        _debuggerManager = debuggerManager ?? throw new ArgumentNullException(nameof(debuggerManager));
        _logger = logger;
    }

    /// <summary>
    /// Performs a comprehensive security analysis of the current dump.
    /// </summary>
    /// <returns>
    /// A <see cref="SecurityAnalysisResult"/> containing detected vulnerabilities, 
    /// memory protection status, and security recommendations.
    /// </returns>
    /// <remarks>
    /// <para>This method performs platform-specific analysis:</para>
    /// <list type="bullet">
    /// <item><description>WinDbg: Uses !analyze, !heap, and other WinDbg-specific commands</description></item>
    /// <item><description>LLDB: Uses memory read, register read, and other LLDB commands</description></item>
    /// </list>
    /// <para>The analysis includes:</para>
    /// <list type="bullet">
    /// <item><description>Crash pattern analysis for security implications</description></item>
    /// <item><description>Memory protection verification (ASLR, DEP, stack canaries)</description></item>
    /// <item><description>Heap and stack integrity checks</description></item>
    /// <item><description>Exploit pattern detection in memory</description></item>
    /// </list>
    /// </remarks>
    public async Task<SecurityAnalysisResult> AnalyzeSecurityAsync()
    {
        var result = new SecurityAnalysisResult();

        // Guard: Ensure a dump file is open before attempting analysis
        // Without an open dump, there's no memory/state to analyze
        if (!_debuggerManager.IsDumpOpen)
        {
            result.Summary = "No dump file is open. Cannot perform security analysis.";
            return result;
        }

        _logger?.LogInformation("Starting security vulnerability analysis...");

        // Command caching is automatically enabled when dump is opened
        // All commands benefit from caching for the entire session
        try
        {
            // Branch on debugger type to use platform-appropriate commands
            // WinDbg and LLDB have completely different command sets and output formats,
            // requiring separate analysis implementations
            if (_debuggerManager.DebuggerType == "WinDbg")
            {
                // Windows: Use DbgEng commands like !analyze, !heap, !peb
                await AnalyzeWithWinDbgAsync(result);
            }
            else if (_debuggerManager.DebuggerType == "LLDB")
            {
                // macOS/Linux: Use LLDB commands like memory read, register read
                await AnalyzeWithLldbAsync(result);
            }
            // Note: No else branch - unknown debugger types result in empty analysis
            // rather than throwing, allowing graceful degradation

            // Generate the overall security assessment based on all findings
            // This combines individual vulnerabilities into a cohesive risk assessment
            GenerateSecurityAssessment(result);
        }
        catch (Exception ex)
        {
            // Log the full exception for debugging but provide a user-friendly message
            // The analysis should not throw - it returns partial results on error
            _logger?.LogError(ex, "Error during security analysis");
            result.Summary = $"Security analysis encountered an error: {ex.Message}";
        }

        _logger?.LogInformation("Security analysis completed. Found {Count} vulnerabilities.", result.Vulnerabilities.Count);
        return result;
    }

    /// <summary>
    /// Analyzes security using WinDbg commands.
    /// </summary>
    /// <param name="result">The result object to populate with findings.</param>
    /// <remarks>
    /// Executes a series of WinDbg-specific security checks in order of importance:
    /// crash patterns → memory protections → heap → stack → exploit patterns → modules.
    /// Each check appends its findings to the result object.
    /// </remarks>
    private async Task AnalyzeWithWinDbgAsync(SecurityAnalysisResult result)
    {
        // Step 1: Analyze crash/exception patterns for security implications
        // This is the most important check as it identifies the immediate cause
        await AnalyzeCrashPatternWinDbgAsync(result);

        // Step 2: Check memory protection settings (ASLR, DEP, etc.)
        // These determine how exploitable any vulnerabilities might be
        await AnalyzeMemoryProtectionsWinDbgAsync(result);

        // Step 3: Analyze heap integrity for corruption indicators
        // Heap corruption often indicates buffer overflows or use-after-free
        await AnalyzeHeapIntegrityWinDbgAsync(result);

        // Step 4: Analyze stack integrity for overflow indicators
        // Stack corruption indicates stack buffer overflows or ROP attacks
        await AnalyzeStackIntegrityWinDbgAsync(result);

        // Step 5: Search memory for common exploit patterns
        // Detects NOP sleds, shellcode signatures, and overflow markers
        await DetectExploitPatternsWinDbgAsync(result);

        // Step 6: Analyze loaded modules for security features
        // Identifies modules lacking ASLR, SafeSEH, or other protections
        await AnalyzeModuleSecurityWinDbgAsync(result);
    }

    /// <summary>
    /// Analyzes security using LLDB commands.
    /// </summary>
    /// <param name="result">The result object to populate with findings.</param>
    /// <remarks>
    /// Executes LLDB-specific security checks for macOS/Linux environments.
    /// Note: Some checks (like SafeSEH) are Windows-only and not performed here.
    /// </remarks>
    private async Task AnalyzeWithLldbAsync(SecurityAnalysisResult result)
    {
        // Step 1: Analyze crash/exception patterns (signal handlers, segfaults)
        // On Unix, crashes are delivered as signals rather than structured exceptions
        await AnalyzeCrashPatternLldbAsync(result);

        // Step 2: Analyze memory protections (mprotect flags, PIE, NX)
        // Unix memory protections differ from Windows but serve similar purposes
        await AnalyzeMemoryProtectionsLldbAsync(result);

        // Step 3: Analyze heap integrity (malloc metadata, free list)
        // Unix heaps (glibc, jemalloc) have different structures than Windows
        await AnalyzeHeapIntegrityLldbAsync(result);

        // Step 4: Analyze stack integrity (canaries, return addresses)
        // Check for __stack_chk_fail indicators and suspicious patterns
        await AnalyzeStackIntegrityLldbAsync(result);

        // Step 5: Search memory for common exploit patterns
        // Platform-independent pattern matching for known exploit signatures
        await DetectExploitPatternsLldbAsync(result);
    }

    // ============================================================
    // WINDBG ANALYSIS METHODS
    // ============================================================

    /// <summary>
    /// Analyzes crash patterns from WinDbg !analyze output for security implications.
    /// </summary>
    /// <param name="result">The result object to populate with findings.</param>
    /// <remarks>
    /// Parses the verbose crash analysis output to detect:
    /// - Access violations (read/write) that may indicate buffer overflows
    /// - Null pointer dereferences
    /// - Stack buffer overruns (detected by /GS security cookie)
    /// - Heap corruption indicators
    /// </remarks>
    private async Task AnalyzeCrashPatternWinDbgAsync(SecurityAnalysisResult result)
    {
        // Execute WinDbg's verbose crash analysis command
        // This provides detailed exception information including fault addresses
        var analyzeOutput = await ExecuteCommandAsync("!analyze -v");
        result.RawOutput["analyze"] = analyzeOutput;

        // Check for access violation - the most common exploitable crash type
        // Access violations occur when code tries to read/write invalid memory
        if (analyzeOutput.Contains("Access violation", StringComparison.OrdinalIgnoreCase))
        {
            // Parse the fault address from the analysis output
            var faultMatch = Regex.Match(analyzeOutput, @"FAULTING_IP:\s*\n?\s*([^\s]+)\s*", RegexOptions.IgnoreCase);
            
            // Try to get the accessed address - first check for read violation
            var accessMatch = Regex.Match(analyzeOutput, @"READ_ADDRESS:\s*(0x[0-9a-f]+)", RegexOptions.IgnoreCase);
            
            // If no read address, check for write address
            // Write violations are often more serious as they indicate potential code injection
            if (!accessMatch.Success)
            {
                accessMatch = Regex.Match(analyzeOutput, @"WRITE_ADDRESS:\s*(0x[0-9a-f]+)", RegexOptions.IgnoreCase);
            }

            // If we found an access address, analyze it for security implications
            if (accessMatch.Success)
            {
                var address = accessMatch.Groups[1].Value;
                var addressValue = ParseAddress(address);

                // Check if this is a null pointer dereference
                // Addresses below NullPageThreshold (0x10000) indicate null/near-null access
                // This is exploitable on some systems where the null page can be mapped
                if (addressValue < AnalysisConstants.NullPageThreshold)
                {
                    result.Vulnerabilities.Add(new Vulnerability
                    {
                        Type = VulnerabilityType.NullDereference,
                        Severity = VulnerabilitySeverity.Medium,
                        Description = $"Null pointer dereference at address {address}",
                        Address = address,
                        Confidence = DetectionConfidence.High,
                        CweIds = new List<string> { "CWE-476" },
                        Remediation = new List<string>
                        {
                            "Check pointer validity before dereference",
                            "Add null checks in critical code paths",
                            "Use smart pointers or null-safe patterns"
                        }
                    });
                }
            }

            // Write access violations are particularly concerning as they may allow
            // arbitrary write primitives for exploitation (write-what-where condition)
            if (analyzeOutput.Contains("WRITE_ADDRESS", StringComparison.OrdinalIgnoreCase))
            {
                result.Vulnerabilities.Add(new Vulnerability
                {
                    Type = VulnerabilityType.BufferOverflow,
                    Severity = VulnerabilitySeverity.High,
                    Description = "Write access violation detected - potential buffer overflow",
                    Confidence = DetectionConfidence.Medium,
                    CweIds = new List<string> { "CWE-120", "CWE-787" },
                    Indicators = new List<string> { "Write access violation" },
                    Remediation = new List<string>
                    {
                        "Review buffer bounds checking",
                        "Use safe string functions (strncpy, snprintf)",
                        "Enable stack protection (/GS)",
                        "Enable ASLR and DEP"
                    }
                });
            }
        }

        // Check for stack buffer overrun detected by the /GS security cookie mechanism
        // This is a CONFIRMED vulnerability - the compiler's protection has triggered
        // The /GS flag inserts a "canary" value on the stack that is checked before function return
        if (analyzeOutput.Contains("STACK_BUFFER_OVERRUN", StringComparison.OrdinalIgnoreCase) ||
            analyzeOutput.Contains("GS_COOKIE", StringComparison.OrdinalIgnoreCase) ||
            analyzeOutput.Contains("STATUS_STACK_BUFFER_OVERRUN", StringComparison.OrdinalIgnoreCase))
        {
            // This is the most serious type of vulnerability - confirmed stack overflow
            // The security cookie was corrupted, proving a buffer overflow occurred
            result.Vulnerabilities.Add(new Vulnerability
            {
                Type = VulnerabilityType.StackBufferOverflow,
                Severity = VulnerabilitySeverity.Critical,
                Description = "Stack buffer overrun detected (security cookie corruption)",
                Confidence = DetectionConfidence.Confirmed,
                CweIds = new List<string> { "CWE-121", "CWE-787" },
                Indicators = new List<string> { "Security cookie (/GS) corruption detected" },
                Remediation = new List<string>
                {
                    "CRITICAL: This is a confirmed stack buffer overflow",
                    "Review all buffer operations in the affected function",
                    "Ensure bounds checking on all array/buffer access",
                    "Consider using safe container types"
                }
            });

            // Initialize stack integrity info if not already present
            // This provides detailed information about the stack corruption
            if (result.StackIntegrity == null) result.StackIntegrity = new StackIntegrityInfo();
            result.StackIntegrity.CanaryCorrupted = true;
            result.StackIntegrity.CorruptionDetected = true;
        }

        // Check for Windows heap corruption exception
        // This indicates the heap metadata has been corrupted, often by:
        // - Buffer overflow in heap allocation
        // - Use-after-free
        // - Double-free
        if (analyzeOutput.Contains("HEAP_CORRUPTION", StringComparison.OrdinalIgnoreCase) ||
            analyzeOutput.Contains("STATUS_HEAP_CORRUPTION", StringComparison.OrdinalIgnoreCase))
        {
            // Heap corruption is critical as it can lead to arbitrary code execution
            result.Vulnerabilities.Add(new Vulnerability
            {
                Type = VulnerabilityType.HeapCorruption,
                Severity = VulnerabilitySeverity.Critical,
                Description = "Heap corruption detected",
                Confidence = DetectionConfidence.Confirmed,
                CweIds = new List<string> { "CWE-122", "CWE-416", "CWE-415" },
                Indicators = new List<string> { "Windows heap corruption exception" },
                Remediation = new List<string>
                {
                    "Enable Page Heap for detailed analysis (!gflag +hpa)",
                    "Review heap allocations and deallocations",
                    "Check for double-free or use-after-free patterns",
                    "Review buffer overflow in heap allocations"
                }
            });

            // Initialize heap integrity info if not already present
            if (result.HeapIntegrity == null) result.HeapIntegrity = new HeapIntegrityInfo();
            result.HeapIntegrity.CorruptionDetected = true;
        }
    }

    private async Task AnalyzeMemoryProtectionsWinDbgAsync(SecurityAnalysisResult result)
    {
        result.MemoryProtections = new MemoryProtectionInfo();

        // Check process DEP setting
        var depOutput = await ExecuteCommandAsync("!peb");
        result.RawOutput["peb"] = depOutput;

        // DEP is typically enabled by default on modern Windows
        result.MemoryProtections.DepEnabled = !depOutput.Contains("DEP: Disabled", StringComparison.OrdinalIgnoreCase);

        // Check for modules without ASLR using !lmi
        var modulesOutput = await ExecuteCommandAsync("lm");
        result.RawOutput["modules"] = modulesOutput;

        // Look for modules at predictable addresses (no ASLR)
        var moduleLines = modulesOutput.Split('\n');
        foreach (var line in moduleLines)
        {
            // Check for low base addresses (often indicates no ASLR)
            var match = Regex.Match(line, @"([0-9a-f`]+)\s+([0-9a-f`]+)\s+(\S+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var baseAddr = match.Groups[1].Value.Replace("`", "");
                var moduleName = match.Groups[3].Value;
                
                // Check if base address looks like a non-ASLR address
                if (baseAddr.StartsWith("00400") || baseAddr.StartsWith("10000"))
                {
                    result.MemoryProtections.ModulesWithoutAslr.Add(moduleName);
                }
            }
        }

        if (result.MemoryProtections.ModulesWithoutAslr.Any())
        {
            result.Vulnerabilities.Add(new Vulnerability
            {
                Type = VulnerabilityType.InformationDisclosure,
                Severity = VulnerabilitySeverity.Medium,
                Description = $"Modules without ASLR detected: {string.Join(", ", result.MemoryProtections.ModulesWithoutAslr.Take(5))}",
                Confidence = DetectionConfidence.Medium,
                CweIds = new List<string> { "CWE-1188" },
                Remediation = new List<string>
                {
                    "Enable ASLR for all modules (/DYNAMICBASE linker option)",
                    "Ensure all dependencies support ASLR"
                }
            });
        }

        result.MemoryProtections.AslrEnabled = !result.MemoryProtections.ModulesWithoutAslr.Any();
    }

    private async Task AnalyzeHeapIntegrityWinDbgAsync(SecurityAnalysisResult result)
    {
        if (result.HeapIntegrity == null)
        {
            result.HeapIntegrity = new HeapIntegrityInfo();
        }

        // Run heap validation
        var heapOutput = await ExecuteCommandAsync("!heap -s");
        result.RawOutput["heap"] = heapOutput;

        // Check for corruption markers
        if (heapOutput.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
            heapOutput.Contains("Corrupt", StringComparison.OrdinalIgnoreCase))
        {
            result.HeapIntegrity.CorruptionDetected = true;

            // Try to get more details
            var heapDetailOutput = await ExecuteCommandAsync("!heap -a");
            result.RawOutput["heap_detail"] = heapDetailOutput;

            // Extract corrupted addresses
            var corruptMatches = Regex.Matches(heapDetailOutput, @"(0x[0-9a-f]+).*corrupt", RegexOptions.IgnoreCase);
            foreach (Match match in corruptMatches)
            {
                result.HeapIntegrity.CorruptedAddresses.Add(match.Groups[1].Value);
                result.HeapIntegrity.CorruptedEntries++;
            }
        }

        // Check free list
        if (heapOutput.Contains("Free list", StringComparison.OrdinalIgnoreCase) &&
            heapOutput.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            result.HeapIntegrity.FreeListCorruption = true;

            result.Vulnerabilities.Add(new Vulnerability
            {
                Type = VulnerabilityType.HeapCorruption,
                Severity = VulnerabilitySeverity.High,
                Description = "Heap free list corruption detected - potential use-after-free or double-free",
                Confidence = DetectionConfidence.High,
                CweIds = new List<string> { "CWE-416", "CWE-415" },
                Indicators = new List<string> { "Free list corruption" }
            });
        }
    }

    private async Task AnalyzeStackIntegrityWinDbgAsync(SecurityAnalysisResult result)
    {
        if (result.StackIntegrity == null)
        {
            result.StackIntegrity = new StackIntegrityInfo();
        }

        // Get current thread's stack
        var stackOutput = await ExecuteCommandAsync("k");
        result.RawOutput["stack"] = stackOutput;

        // Look for suspicious return addresses
        var stackLines = stackOutput.Split('\n');
        foreach (var line in stackLines)
        {
            // Check for suspicious patterns in return addresses
            foreach (var pattern in SuspiciousPatterns)
            {
                if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    result.StackIntegrity.SuspiciousPatterns.Add($"Pattern '{pattern}' found: {line.Trim()}");
                }
            }
        }

        if (result.StackIntegrity.SuspiciousPatterns.Any())
        {
            result.StackIntegrity.CorruptionDetected = true;
            result.Vulnerabilities.Add(new Vulnerability
            {
                Type = VulnerabilityType.StackCorruption,
                Severity = VulnerabilitySeverity.High,
                Description = "Suspicious patterns detected on stack - potential overflow or exploitation attempt",
                Confidence = DetectionConfidence.Medium,
                CweIds = new List<string> { "CWE-121" },
                Indicators = result.StackIntegrity.SuspiciousPatterns.Take(5).ToList()
            });
        }

        // Check for ROP gadget indicators
        var retAddresses = Regex.Matches(stackOutput, @"RetAddr\s*:\s*(0x[0-9a-f]+)", RegexOptions.IgnoreCase);
        foreach (Match match in retAddresses)
        {
            var addr = ParseAddress(match.Groups[1].Value);
            // Check for return-to-libc or ROP indicators (addresses in known executable regions)
            if (addr != 0 && addr < 0x1000)
            {
                result.Vulnerabilities.Add(new Vulnerability
                {
                    Type = VulnerabilityType.CodeExecution,
                    Severity = VulnerabilitySeverity.Critical,
                    Description = "Suspicious low return address - potential ROP or return-to-libc attack",
                    Address = match.Groups[1].Value,
                    Confidence = DetectionConfidence.Medium,
                    CweIds = new List<string> { "CWE-94" }
                });
            }
        }
    }

    private async Task DetectExploitPatternsWinDbgAsync(SecurityAnalysisResult result)
    {
        // Search memory for common exploit patterns
        foreach (var pattern in SuspiciousPatterns.Take(5)) // Limit for performance
        {
            try
            {
                var searchOutput = await ExecuteCommandAsync($"s -d 0 L?80000000 0x{pattern}");
                
                if (!string.IsNullOrEmpty(searchOutput) && 
                    !searchOutput.Contains("No match", StringComparison.OrdinalIgnoreCase) &&
                    searchOutput.Contains("0x", StringComparison.OrdinalIgnoreCase))
                {
                    var matchCount = Regex.Matches(searchOutput, @"^[0-9a-f`]+", RegexOptions.Multiline | RegexOptions.IgnoreCase).Count;
                    
                    if (matchCount > 10) // Significant number of matches
                    {
                        result.Vulnerabilities.Add(new Vulnerability
                        {
                            Type = VulnerabilityType.BufferOverflow,
                            Severity = VulnerabilitySeverity.Medium,
                            Description = $"Suspicious pattern 0x{pattern} found {matchCount} times in memory",
                            Confidence = DetectionConfidence.Low,
                            Indicators = new List<string> { $"Pattern 0x{pattern} x{matchCount}" }
                        });
                    }
                }
            }
            catch
            {
                // Ignore search errors
            }
        }
    }

    private async Task AnalyzeModuleSecurityWinDbgAsync(SecurityAnalysisResult result)
    {
        // Check for unsigned or suspicious modules
        var lmvOutput = await ExecuteCommandAsync("lmv");
        result.RawOutput["lmv"] = lmvOutput;

        // Check for modules without SafeSEH (32-bit)
        var safeSehMissing = new List<string>();
        var moduleBlocks = lmvOutput.Split(new[] { "Image name:" }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var block in moduleBlocks)
        {
            if (block.Contains("SafeSEH") && block.Contains("NO"))
            {
                var nameMatch = Regex.Match(block, @"^([^\s]+)", RegexOptions.Multiline);
                if (nameMatch.Success)
                {
                    safeSehMissing.Add(nameMatch.Groups[1].Value);
                }
            }
        }

        if (safeSehMissing.Any())
        {
            result.MemoryProtections!.SafeSehEnabled = false;
            result.Vulnerabilities.Add(new Vulnerability
            {
                Type = VulnerabilityType.CodeExecution,
                Severity = VulnerabilitySeverity.Medium,
                Description = $"Modules without SafeSEH: {string.Join(", ", safeSehMissing.Take(5))}",
                Confidence = DetectionConfidence.High,
                CweIds = new List<string> { "CWE-693" },
                Remediation = new List<string>
                {
                    "Enable SafeSEH for all modules (/SAFESEH linker option)",
                    "Consider upgrading to 64-bit where SEH is different"
                }
            });
        }
    }

    // ============================================================
    // LLDB ANALYSIS METHODS
    // ============================================================

    private async Task AnalyzeCrashPatternLldbAsync(SecurityAnalysisResult result)
    {
        // Get crash info
        var btOutput = await ExecuteCommandAsync("bt");
        result.RawOutput["backtrace"] = btOutput;

        var registerOutput = await ExecuteCommandAsync("register read");
        result.RawOutput["registers"] = registerOutput;

        // Check signal/exception
        var threadOutput = await ExecuteCommandAsync("thread info");
        result.RawOutput["thread_info"] = threadOutput;

        // Check for SIGSEGV, SIGBUS, SIGABRT
        if (threadOutput.Contains("SIGSEGV", StringComparison.OrdinalIgnoreCase) ||
            threadOutput.Contains("SIGBUS", StringComparison.OrdinalIgnoreCase) ||
            threadOutput.Contains("EXC_BAD_ACCESS", StringComparison.OrdinalIgnoreCase))
        {
            // Try multiple methods to get the fault address:
            // 1. ARM64: far (Fault Address Register) - requires "register read far"
            // 2. x86_64: cr2 register contains fault address
            // 3. Thread info may contain "address = 0xHEX"
            // 4. LLDB stop description may contain the address
            string? faultAddress = null;
            
            // Try ARM64 FAR register
            var faultAddrMatch = Regex.Match(registerOutput, @"far\s*=\s*(0x[0-9a-f]+)", RegexOptions.IgnoreCase);
            if (faultAddrMatch.Success)
            {
                faultAddress = faultAddrMatch.Groups[1].Value;
            }

            // Try x86_64 cr2 register
            if (faultAddress == null)
            {
                faultAddrMatch = Regex.Match(registerOutput, @"cr2\s*=\s*(0x[0-9a-f]+)", RegexOptions.IgnoreCase);
            if (faultAddrMatch.Success)
            {
                    faultAddress = faultAddrMatch.Groups[1].Value;
                }
            }
            
            // Try thread info "address = 0xHEX" pattern
            if (faultAddress == null)
            {
                faultAddrMatch = Regex.Match(threadOutput, @"address\s*=\s*(0x[0-9a-f]+)", RegexOptions.IgnoreCase);
                if (faultAddrMatch.Success)
                {
                    faultAddress = faultAddrMatch.Groups[1].Value;
                }
            }

            if (faultAddress != null)
            {
                var addressValue = ParseAddress(faultAddress);

                if (addressValue < AnalysisConstants.NullPageThreshold)
                {
                    result.Vulnerabilities.Add(new Vulnerability
                    {
                        Type = VulnerabilityType.NullDereference,
                        Severity = VulnerabilitySeverity.Medium,
                        Description = $"Null pointer dereference at address {faultAddress}",
                        Address = faultAddress,
                        Confidence = DetectionConfidence.High,
                        CweIds = new List<string> { "CWE-476" }
                    });
                }
            }
        }

        if (threadOutput.Contains("SIGABRT", StringComparison.OrdinalIgnoreCase))
        {
            // Check if it's from heap corruption
            // macOS: malloc_error_break, malloc_zone_error
            // Linux glibc: __libc_message, __GI_abort, malloc_printerr
            // musl: various abort paths
            var heapIndicators = new[]
            {
                "malloc", "free", "realloc", "calloc",  // Generic heap operations
                "malloc_error_break", "malloc_zone_error",  // macOS
                "malloc_printerr", "__libc_message",  // Linux glibc
                "tcache", "fastbin", "unsorted",  // glibc heap internals
            };
            
            if (heapIndicators.Any(indicator => btOutput.Contains(indicator, StringComparison.OrdinalIgnoreCase)))
            {
                result.Vulnerabilities.Add(new Vulnerability
                {
                    Type = VulnerabilityType.HeapCorruption,
                    Severity = VulnerabilitySeverity.High,
                    Description = "Abort during heap operation - likely heap corruption",
                    Confidence = DetectionConfidence.High,
                    CweIds = new List<string> { "CWE-122", "CWE-416", "CWE-415" }
                });
            }
        }

        // Check for stack smashing detection (works on both macOS and Linux)
        if (btOutput.Contains("__stack_chk_fail", StringComparison.OrdinalIgnoreCase) ||
            btOutput.Contains("__fortify_fail", StringComparison.OrdinalIgnoreCase) ||
            btOutput.Contains("stack_chk_fail", StringComparison.OrdinalIgnoreCase))  // musl variant
        {
            result.Vulnerabilities.Add(new Vulnerability
            {
                Type = VulnerabilityType.StackBufferOverflow,
                Severity = VulnerabilitySeverity.Critical,
                Description = "Stack buffer overflow detected (stack canary triggered)",
                Confidence = DetectionConfidence.Confirmed,
                CweIds = new List<string> { "CWE-121", "CWE-787" },
                Indicators = new List<string> { "__stack_chk_fail called" }
            });

            if (result.StackIntegrity == null) result.StackIntegrity = new StackIntegrityInfo();
            result.StackIntegrity.CanaryCorrupted = true;
            result.StackIntegrity.CorruptionDetected = true;
        }
    }

    private async Task AnalyzeMemoryProtectionsLldbAsync(SecurityAnalysisResult result)
    {
        result.MemoryProtections = new MemoryProtectionInfo();

        // Check image list for PIE (ASLR)
        var imageOutput = await ExecuteCommandAsync("image list");
        result.RawOutput["image_list"] = imageOutput;

        // Parse image list to check for ASLR
        // LLDB image list format: [idx] UUID ADDRESS PATH
        // Example: [  0] F8BB27F0-... 0x0000c5f644a60000 /usr/share/dotnet/dotnet
        var lines = imageOutput.Split('\n');
        foreach (var line in lines)
        {
            // Extract address from image list line
            var addressMatch = Regex.Match(line, @"0x([0-9a-f]{8,16})", RegexOptions.IgnoreCase);
            if (!addressMatch.Success) continue;
            
            var address = ParseAddress(addressMatch.Value);
            
            // Extract module name
            var moduleMatch = Regex.Match(line, @"(/[^\s]+|\\[^\s]+)$");
            var moduleName = moduleMatch.Success ? Path.GetFileName(moduleMatch.Groups[1].Value) : "unknown";
            
            // Check for non-ASLR indicators:
            // 1. Very low addresses (non-PIE executables on Linux)
            // 2. Predictable base addresses (0x00400000 for x86, 0x00010000 for some systems)
            // 3. Page-aligned round numbers that don't look randomized
            
            // Non-PIE binaries on Linux typically load at 0x400000 (x86_64) or 0x10000 (ARM)
            var suspiciousAddresses = new long[]
            {
                0x00400000,      // x86_64 Linux non-PIE default
                0x00010000,      // ARM Linux non-PIE
                0x00001000,      // Very low address
                0x08048000,      // x86 Linux non-PIE default
                0x00100000,      // Some embedded systems
            };
            
            if (suspiciousAddresses.Any(suspicious => address == suspicious || 
                (address >= suspicious && address < suspicious + 0x1000)))
                {
                result.MemoryProtections.ModulesWithoutAslr.Add(moduleName);
            }
        }

        result.MemoryProtections.AslrEnabled = !result.MemoryProtections.ModulesWithoutAslr.Any();
        
        // DEP/NX is enabled by default on modern Linux and macOS
        // We could check /proc/self/maps or vmmap for actual permissions,
        // but for dump analysis we assume it's enabled unless we see evidence otherwise
        result.MemoryProtections.DepEnabled = true;
        
        // Check if we can detect stack canaries by looking for stack protector functions in modules
        var btOutput = result.RawOutput.GetValueOrDefault("backtrace", "");
        // If the binary has stack canaries and they didn't trigger, we assume they're present
        // (We can't definitively tell without checking the binary itself)
        result.MemoryProtections.StackCanariesPresent = 
            imageOutput.Contains("libssp", StringComparison.OrdinalIgnoreCase) || // SSP library
            btOutput.Contains("__stack_chk", StringComparison.OrdinalIgnoreCase);  // Stack check function
    }

    private async Task AnalyzeHeapIntegrityLldbAsync(SecurityAnalysisResult result)
    {
        if (result.HeapIntegrity == null)
        {
            result.HeapIntegrity = new HeapIntegrityInfo();
        }

        // Try to get heap info (limited on LLDB without special plugins)
        // This command may fail but we catch errors
        await ExecuteCommandAsync("memory read --size 8 --count 10 0x0");
        
        // Look for heap-related functions in backtrace
        var btOutput = result.RawOutput.GetValueOrDefault("backtrace", "");
        
        // macOS heap error indicators
        var macOsHeapErrors = new[]
        {
            "malloc_error_break",
            "malloc_zone_error",
            "nano_zone_error",
            "szone_error"
        };
        
        // Linux glibc heap error indicators
        var glibcHeapErrors = new[]
        {
            "malloc_printerr",
            "__libc_message",
            "malloc_consolidate",
            "corrupted double-linked list",
            "double free or corruption",
            "invalid next size",
            "invalid pointer",
            "corrupted size vs. prev_size",
            "munmap_chunk",
            "free(): invalid size",
            "free(): invalid next size",
            "free(): invalid pointer",
            "malloc(): corrupted",
            "realloc(): invalid",
            "_int_malloc",
            "_int_free"
        };
        
        // musl libc heap error indicators (Alpine Linux)
        var muslHeapErrors = new[]
        {
            "lite_malloc",
            "__malloc_alloc",
            "__expand_heap"
        };
        
        // Check for macOS heap errors
        foreach (var error in macOsHeapErrors)
        {
            if (btOutput.Contains(error, StringComparison.OrdinalIgnoreCase))
        {
            result.HeapIntegrity.CorruptionDetected = true;
                result.HeapIntegrity.MetadataIssues.Add($"macOS heap error: {error}");
                break;
            }
        }
        
        // Check for glibc heap errors
        foreach (var error in glibcHeapErrors)
        {
            if (btOutput.Contains(error, StringComparison.OrdinalIgnoreCase))
            {
                result.HeapIntegrity.CorruptionDetected = true;
                result.HeapIntegrity.MetadataIssues.Add($"glibc heap error: {error}");
                break;
            }
        }
        
        // Check for musl heap errors
        foreach (var error in muslHeapErrors)
        {
            if (btOutput.Contains(error, StringComparison.OrdinalIgnoreCase))
            {
                result.HeapIntegrity.CorruptionDetected = true;
                result.HeapIntegrity.MetadataIssues.Add($"musl heap error: {error}");
                break;
            }
        }
        
        if (result.HeapIntegrity.CorruptionDetected)
        {
            result.Vulnerabilities.Add(new Vulnerability
            {
                Type = VulnerabilityType.HeapCorruption,
                Severity = VulnerabilitySeverity.High,
                Description = "Heap corruption detected",
                Confidence = DetectionConfidence.High,
                CweIds = new List<string> { "CWE-122" },
                Indicators = result.HeapIntegrity.MetadataIssues.ToList()
            });
        }
    }

    private async Task AnalyzeStackIntegrityLldbAsync(SecurityAnalysisResult result)
    {
        if (result.StackIntegrity == null)
        {
            result.StackIntegrity = new StackIntegrityInfo();
        }

        // Get stack frames
        var btOutput = result.RawOutput.GetValueOrDefault("backtrace", "");

        // Look for suspicious patterns in stack
        foreach (var pattern in SuspiciousPatterns)
        {
            if (btOutput.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                result.StackIntegrity.SuspiciousPatterns.Add($"Pattern 0x{pattern} found in stack");
            }
        }

        // Check frame pointers for corruption
        var frameOutput = await ExecuteCommandAsync("frame info");
        result.RawOutput["frame_info"] = frameOutput;

        // Look for suspicious return addresses
        if (btOutput.Contains("0x41414141") || btOutput.Contains("0x42424242"))
        {
            result.StackIntegrity.CorruptionDetected = true;
            result.StackIntegrity.ReturnAddressOverwritten = true;
            
            result.Vulnerabilities.Add(new Vulnerability
            {
                Type = VulnerabilityType.StackCorruption,
                Severity = VulnerabilitySeverity.Critical,
                Description = "Stack return address appears to be overwritten with common exploit pattern",
                Confidence = DetectionConfidence.High,
                CweIds = new List<string> { "CWE-121" }
            });
        }
    }

    private async Task DetectExploitPatternsLldbAsync(SecurityAnalysisResult result)
    {
        // Search for NOP sleds and common shellcode patterns
        var registerOutput = result.RawOutput.GetValueOrDefault("registers", "");
        
        // Get instruction pointer (PC) and stack pointer (SP) to detect stack execution
        // ARM64: pc, sp
        // x86_64: rip, rsp (or pc, sp in LLDB unified format)
        long pcValue = 0;
        long spValue = 0;
        string? pcAddress = null;
        
        // Try ARM64/unified format first: pc = 0xHEX
        var pcMatch = Regex.Match(registerOutput, @"\bpc\s*=\s*(0x[0-9a-f]+)", RegexOptions.IgnoreCase);
        if (pcMatch.Success)
        {
            pcAddress = pcMatch.Groups[1].Value;
            pcValue = ParseAddress(pcAddress);
        }
        else
        {
            // Try x86_64 format: rip = 0xHEX
            pcMatch = Regex.Match(registerOutput, @"\brip\s*=\s*(0x[0-9a-f]+)", RegexOptions.IgnoreCase);
            if (pcMatch.Success)
            {
                pcAddress = pcMatch.Groups[1].Value;
                pcValue = ParseAddress(pcAddress);
            }
        }
        
        // Get stack pointer for comparison
        var spMatch = Regex.Match(registerOutput, @"\bsp\s*=\s*(0x[0-9a-f]+)", RegexOptions.IgnoreCase);
        if (spMatch.Success)
        {
            spValue = ParseAddress(spMatch.Groups[1].Value);
        }
        else
        {
            // Try x86_64 format: rsp = 0xHEX
            spMatch = Regex.Match(registerOutput, @"\brsp\s*=\s*(0x[0-9a-f]+)", RegexOptions.IgnoreCase);
            if (spMatch.Success)
            {
                spValue = ParseAddress(spMatch.Groups[1].Value);
            }
        }
        
        if (pcValue > 0 && pcAddress != null)
        {
            bool suspiciousExecution = false;
            string description = "";
            
            // Method 1: Compare PC to SP - if PC is within 16MB of SP, likely stack execution
            if (spValue > 0)
            {
                var distance = Math.Abs(pcValue - spValue);
                if (distance < 0x1000000) // Within 16MB of stack pointer
                {
                    suspiciousExecution = true;
                    description = "Execution near stack pointer detected - possible shellcode execution";
                }
            }
            
            // Method 2: Check for execution in typical stack regions by architecture
            // These are heuristics and may not catch all cases
            if (!suspiciousExecution)
            {
                // x86_64 Linux: Stack typically at 0x7FFxxxxxx
                // x86_64 macOS: Stack typically at 0x7FFxxxxxx  
                // ARM64 Linux: Stack typically at 0xFFFFxxxxxx or 0x0000FFFFxxxxxx
                // ARM64 macOS: Stack typically at 0x16Fxxxxxxx or higher
                
                // High address check (common for stacks on 64-bit)
                if (pcValue > 0x7F00_0000_0000_0000L ||  // x86_64 high addresses
                    (pcValue > 0x0000_FFFF_0000_0000L && pcValue < 0x0001_0000_0000_0000L) ||  // ARM64 Linux typical range
                    pcValue > 0x0000_0001_6F00_0000L && pcValue < 0x0000_0001_8000_0000L)  // ARM64 macOS stack region
                {
                    suspiciousExecution = true;
                    description = "Execution in high memory region detected - possible stack execution";
                }
            }
            
            // Method 3: Check for execution at address with suspicious patterns
            if (!suspiciousExecution)
            {
                foreach (var pattern in SuspiciousPatterns)
                {
                    if (pcAddress.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        suspiciousExecution = true;
                        description = $"Execution at suspicious address pattern ({pattern}) detected";
                        break;
                    }
                }
            }
            
            if (suspiciousExecution)
            {
                result.Vulnerabilities.Add(new Vulnerability
                {
                    Type = VulnerabilityType.CodeExecution,
                    Severity = VulnerabilitySeverity.Critical,
                    Description = description,
                    Address = pcAddress,
                    Confidence = DetectionConfidence.High,
                    CweIds = new List<string> { "CWE-94" }
                });
            }
        }
    }

    // ============================================================
    // HELPER METHODS
    // ============================================================

    private async Task<string> ExecuteCommandAsync(string command)
    {
        try
        {
            return await Task.Run(() => _debuggerManager.ExecuteCommand(command));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to execute command: {Command}", command);
            return string.Empty;
        }
    }

    private static long ParseAddress(string address)
    {
        try
        {
            var cleanAddr = address.Replace("`", "").Replace("0x", "");
            return Convert.ToInt64(cleanAddr, 16);
        }
        catch
        {
            return 0;
        }
    }

    private void GenerateSecurityAssessment(SecurityAnalysisResult result)
    {
        // Determine overall risk
        if (result.Vulnerabilities.Any(v => v.Severity == VulnerabilitySeverity.Critical))
        {
            result.OverallRisk = SecurityRisk.Critical;
        }
        else if (result.Vulnerabilities.Any(v => v.Severity == VulnerabilitySeverity.High))
        {
            result.OverallRisk = SecurityRisk.High;
        }
        else if (result.Vulnerabilities.Any(v => v.Severity == VulnerabilitySeverity.Medium))
        {
            result.OverallRisk = SecurityRisk.Medium;
        }
        else if (result.Vulnerabilities.Any())
        {
            result.OverallRisk = SecurityRisk.Low;
        }
        else
        {
            result.OverallRisk = SecurityRisk.None;
        }

        // Generate summary
        var criticalCount = result.Vulnerabilities.Count(v => v.Severity == VulnerabilitySeverity.Critical);
        var highCount = result.Vulnerabilities.Count(v => v.Severity == VulnerabilitySeverity.High);
        var mediumCount = result.Vulnerabilities.Count(v => v.Severity == VulnerabilitySeverity.Medium);

        result.Summary = $"Security Analysis: {result.OverallRisk} risk. " +
                        $"Found {result.Vulnerabilities.Count} potential vulnerabilities " +
                        $"({criticalCount} critical, {highCount} high, {mediumCount} medium).";

        // Add general recommendations
        if (result.OverallRisk >= SecurityRisk.High)
        {
            result.Recommendations.Add("URGENT: Critical security vulnerabilities detected. Immediate remediation required.");
        }

        if (result.MemoryProtections != null)
        {
            if (!result.MemoryProtections.AslrEnabled)
            {
                result.Recommendations.Add("Enable ASLR (Address Space Layout Randomization) for all modules.");
            }
            if (!result.MemoryProtections.DepEnabled)
            {
                result.Recommendations.Add("Enable DEP/NX (Data Execution Prevention) for the process.");
            }
        }

        if (result.HeapIntegrity?.CorruptionDetected == true)
        {
            result.Recommendations.Add("Investigate heap corruption. Consider running with Page Heap enabled for detailed analysis.");
        }

        if (result.StackIntegrity?.CorruptionDetected == true)
        {
            result.Recommendations.Add("Investigate stack corruption. Review buffer operations in affected code.");
        }

        if (!result.Recommendations.Any())
        {
            result.Recommendations.Add("No critical security issues detected. Continue monitoring and apply security best practices.");
        }
    }
}

