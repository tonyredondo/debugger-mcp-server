using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DebuggerMcp.SourceLink;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Specialized analyzer for .NET application crashes.
/// Provides detailed analysis of managed code issues.
/// </summary>
public class DotNetCrashAnalyzer : CrashAnalyzer
{
    private readonly ClrMdAnalyzer? _clrMdAnalyzer;
    private readonly ILogger? _logger;
    
    /// <summary>
    /// Initializes a new instance of the <see cref="DotNetCrashAnalyzer"/> class.
    /// </summary>
    /// <param name="debuggerManager">The debugger manager to use.</param>
    /// <param name="sourceLinkResolver">Optional Source Link resolver for resolving source URLs.</param>
    /// <param name="clrMdAnalyzer">Optional ClrMD analyzer for assembly metadata enrichment.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public DotNetCrashAnalyzer(
        IDebuggerManager debuggerManager, 
        SourceLinkResolver? sourceLinkResolver = null,
        ClrMdAnalyzer? clrMdAnalyzer = null,
        ILogger? logger = null)
        : base(debuggerManager, sourceLinkResolver, logger)
    {
        _clrMdAnalyzer = clrMdAnalyzer;
        _logger = logger;
    }
    
    /// <summary>
    /// Inspects an object using ClrMD exclusively. No SOS fallback to prevent LLDB crashes.
    /// </summary>
    /// <param name="address">The object address to inspect.</param>
    /// <returns>The ClrMD inspection result formatted like dumpobj, or error message.</returns>
    private Task<string> DumpObjectViaClrMdAsync(string address)
    {
        // Use ClrMD exclusively - no SOS fallback to prevent LLDB crashes
        if (_clrMdAnalyzer?.IsOpen != true)
        {
            return Task.FromResult($"<ClrMD not available for object at {address}>");
        }
        
        try
        {
            // Clean address - may contain type info like "0x1234 (System.String)"
            var cleanAddress = ExtractHexAddress(address);
            if (string.IsNullOrEmpty(cleanAddress))
            {
                return Task.FromResult($"<Invalid address format: {address}>");
            }
            
            if (!ulong.TryParse(cleanAddress, System.Globalization.NumberStyles.HexNumber, null, out var addressValue))
            {
                return Task.FromResult($"<Invalid address format: {address}>");
            }
            
            var clrMdResult = _clrMdAnalyzer.InspectObject(addressValue, maxDepth: 2, maxArrayElements: 5, maxStringLength: 256);
            
            if (clrMdResult != null && clrMdResult.Error == null)
            {
                // Format ClrMD result to look like dumpobj output for compatibility
                return Task.FromResult(FormatClrMdAsDumpObj(clrMdResult));
            }
            
            _logger?.LogDebug("ClrMD dumpobj skipped for {Address}: {Error}", address, clrMdResult?.Error ?? "null");
            return Task.FromResult($"<ClrMD: Object at {address} could not be inspected: {clrMdResult?.Error ?? "unknown error"}>");
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "ClrMD dumpobj failed for {Address}", address);
            return Task.FromResult($"<ClrMD: Object at {address} inspection threw: {ex.Message}>");
        }
    }
    
    /// <summary>
    /// Populates thread call stacks using ClrMD instead of SOS clrstack command.
    /// This is faster (~500ms vs 12s) and more reliable (no DAC crashes).
    /// Merges managed frames with native frames from bt all by stack pointer.
    /// </summary>
    /// <param name="result">The crash analysis result to populate.</param>
    private void PopulateManagedStacksViaClrMd(CrashAnalysisResult result)
    {
        if (_clrMdAnalyzer?.IsOpen != true)
        {
            _logger?.LogDebug("[ClrStack] ClrMD not available, skipping managed stack population");
            return;
        }

        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var clrStackResult = _clrMdAnalyzer.GetAllThreadStacks(
                includeArguments: true,
                includeLocals: true
            );

            if (clrStackResult.Error != null)
            {
                _logger?.LogWarning("[ClrStack] ClrMD stack walk failed: {Error}", clrStackResult.Error);
                return;
            }

            // Store raw result for debugging
            result.RawCommands ??= new Dictionary<string, string>();
            result.RawCommands["clrmd_clrstack"] = System.Text.Json.JsonSerializer.Serialize(clrStackResult,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            // Ensure thread containers exist
            result.Threads ??= new ThreadsInfo();
            result.Threads.All ??= new List<ThreadInfo>();

            // Find faulting thread ID for register fetching (only fetch registers for faulting thread)
            uint? faultingThreadId = null;
            if (result.Threads.FaultingThread?.IsFaulting == true)
            {
                // Try to extract thread ID from faulting thread info
                var tidMatch = Regex.Match(result.Threads.FaultingThread.ThreadId ?? "", @"tid:\s*(\d+)");
                if (tidMatch.Success && uint.TryParse(tidMatch.Groups[1].Value, out var tid))
                {
                    faultingThreadId = tid;
                }
            }
            
            // Fallback: check ClrMD threads for faulting flag
            if (faultingThreadId == null)
            {
                var faultingClrThread = clrStackResult.Threads.FirstOrDefault(t => t.IsFaulting);
                if (faultingClrThread != null)
                {
                    faultingThreadId = faultingClrThread.OSThreadId;
                }
            }

            // Fetch registers ONLY for faulting thread, for ALL frames (native + managed)
            Dictionary<(uint ThreadId, ulong SP), Dictionary<string, string>>? perFrameRegisters = null;
            if (_debuggerManager is LldbManager lldbManager && faultingThreadId.HasValue)
            {
                perFrameRegisters = FetchAllFrameRegistersForThread(lldbManager, faultingThreadId.Value);
                _logger?.LogDebug("[ClrStack] Fetched registers for faulting thread {ThreadId}: {Count} frames", 
                    faultingThreadId.Value, perFrameRegisters?.Count ?? 0);
            }

            foreach (var clrThread in clrStackResult.Threads)
            {
                // Some dumps can contain ClrMD thread entries with OSThreadId = 0.
                // These don't map to an OS thread and should not create a synthetic "0x0" entry in the report.
                if (!IsValidOsThreadId(clrThread.OSThreadId))
                {
                    _logger?.LogDebug("[ClrStack] Skipping ClrMD thread with OSThreadId=0");
                    continue;
                }

                // Find matching thread by OS thread ID
                // bt all produces thread IDs like "1 (tid: 884) \"dotnet\"" where 884 is the TID
                // ClrMD gives us OSThreadId as a numeric value (e.g., 884)
                var threadIdHex = $"0x{clrThread.OSThreadId:x}";
                var threadIdDec = clrThread.OSThreadId.ToString();
                var existingThread = result.Threads.All.FirstOrDefault(t =>
                {
                    if (string.IsNullOrEmpty(t.ThreadId))
                        return false;
                    
                    // Direct match (hex or decimal)
                    if (string.Equals(t.ThreadId, threadIdHex, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.ThreadId, threadIdDec, StringComparison.OrdinalIgnoreCase))
                        return true;
                    
                    // Parse TID from bt all format: "1 (tid: 884) \"dotnet\""
                    var tidMatch = System.Text.RegularExpressions.Regex.Match(t.ThreadId, @"tid:\s*(\d+)");
                    if (tidMatch.Success && tidMatch.Groups[1].Value == threadIdDec)
                        return true;
                    
                    return false;
                });

                if (existingThread == null)
                {
                    // Create new thread entry if not found from bt all
                    existingThread = new ThreadInfo
                    {
                        ThreadId = threadIdHex,
                        State = clrThread.IsAlive ? "Running" : "Dead",
                        IsDead = !clrThread.IsAlive,
                        IsBackground = clrThread.IsBackground
                    };
                    result.Threads.All.Add(existingThread);
                }
                else
                {
                    // If ClrMD indicates the thread is dead, reflect that consistently.
                    existingThread.IsDead |= !clrThread.IsAlive;
                    existingThread.IsBackground ??= clrThread.IsBackground;
                }

                // Update thread properties
                if (clrThread.IsFaulting)
                {
                    existingThread.IsFaulting = true;
                }


                // Build managed frames indexed by SP for merging
                var managedFramesBySp = new Dictionary<ulong, StackFrame>();
                
                foreach (var clrFrame in clrThread.Frames)
                {
                    // Skip non-managed frames (but keep Runtime frames like GCFrame)
                    if (clrFrame.Method == null && clrFrame.Kind == "Unknown")
                    {
                        continue;
                    }

                    var frame = new StackFrame
                    {
                        StackPointer = $"0x{clrFrame.StackPointer:x16}",
                        InstructionPointer = $"0x{clrFrame.InstructionPointer:x16}",
                        IsManaged = clrFrame.Method != null
                    };

                    if (clrFrame.Method != null)
                    {
                        frame.Function = clrFrame.Method.Signature ?? clrFrame.Method.MethodName ?? string.Empty;
                        frame.Module = ExtractManagedModuleName(
                                         clrFrame.Method.ModuleName,
                                         clrFrame.Method.AssemblyName,
                                         clrFrame.Method.TypeName)
                                     ?? string.Empty;

                        // Source location from sequence points
                        if (clrFrame.SourceLocation != null)
                        {
                            frame.SourceFile = clrFrame.SourceLocation.SourceFile;
                            frame.LineNumber = clrFrame.SourceLocation.LineNumber;
                            frame.Source = $"{frame.SourceFile}:{frame.LineNumber}";
                        }
                        
                        // Add per-frame registers (fetched via LLDB frame select + register read)
                        if (perFrameRegisters != null && 
                            perFrameRegisters.TryGetValue((clrThread.OSThreadId, clrFrame.StackPointer), out var frameRegs) &&
                            frameRegs.Count > 0)
                        {
                            frame.Registers = new Dictionary<string, string>(frameRegs);
                        }
                    }
                    else
                    {
                        // Runtime frame (GC, etc.)
                        frame.Function = $"[{clrFrame.Kind}]";
                        frame.IsManaged = false;
                    }

                    // Add arguments (PARAMETERS)
                    if (clrFrame.Arguments != null)
                    {
                        frame.Parameters ??= new List<LocalVariable>();
                        foreach (var arg in clrFrame.Arguments)
                        {
                            frame.Parameters.Add(new LocalVariable
                            {
                                Name = arg.Name ?? $"arg{arg.Index}",
                                Value = arg.HasValue ? (arg.ValueString ?? $"0x{arg.Address:x}") : "[NO DATA]",
                                Type = arg.TypeName,
                                HasData = arg.HasValue
                            });
                        }
                    }

                    // Add locals (LOCALS)
                    if (clrFrame.Locals != null)
                    {
                        frame.Locals ??= new List<LocalVariable>();
                        foreach (var local in clrFrame.Locals)
                        {
                            frame.Locals.Add(new LocalVariable
                            {
                                Name = local.Name ?? $"local_{local.Index}",
                                Value = local.HasValue ? (local.ValueString ?? $"0x{local.Address:x}") : "[NO DATA]",
                                Type = local.TypeName,
                                HasData = local.HasValue
                            });
                        }
                    }

                    managedFramesBySp[clrFrame.StackPointer] = frame;
                }

                // Merge managed frames into existing native frames by SP
                MergeManagedFramesIntoCallStack(existingThread, managedFramesBySp);
            }

            // Apply registers to ALL frames in faulting thread (including native frames)
            if (perFrameRegisters != null && perFrameRegisters.Count > 0 && faultingThreadId.HasValue)
            {
                ApplyRegistersToFaultingThread(result, perFrameRegisters, faultingThreadId.Value);
            }

            stopwatch.Stop();
            _logger?.LogInformation("[ClrStack] Populated {Threads} threads, {Frames} managed frames via ClrMD in {Duration}ms",
                clrStackResult.TotalThreads, clrStackResult.TotalFrames, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[ClrStack] ClrMD stack population failed, will fallback to SOS");
        }
    }
    
    /// <summary>
    /// Applies fetched registers to all frames in the faulting thread.
    /// This includes both native and managed frames.
    /// </summary>
    private void ApplyRegistersToFaultingThread(
        CrashAnalysisResult result,
        Dictionary<(uint ThreadId, ulong SP), Dictionary<string, string>> registers,
        uint faultingThreadId)
    {
        // Find the faulting thread
        var faultingThread = result.Threads?.FaultingThread;
        if (faultingThread == null)
        {
            // Try to find it in All threads
            var threadIdDec = faultingThreadId.ToString();
            faultingThread = result.Threads?.All?.FirstOrDefault(t =>
            {
                if (string.IsNullOrEmpty(t.ThreadId))
                    return false;
                
                var tidMatch = Regex.Match(t.ThreadId, @"tid:\s*(\d+)");
                if (tidMatch.Success && tidMatch.Groups[1].Value == threadIdDec)
                    return true;
                
                return t.ThreadId.Contains(threadIdDec, StringComparison.OrdinalIgnoreCase);
            });
        }
        
        if (faultingThread?.CallStack == null)
        {
            _logger?.LogDebug("[ClrStack] Faulting thread not found for register application");
            return;
        }
        
        int appliedCount = 0;
        foreach (var frame in faultingThread.CallStack)
        {
            // Parse the frame's stack pointer
            var sp = ParseStackPointer(frame.StackPointer);
            if (sp == null)
                continue;
            
            // Look up registers for this SP
            if (registers.TryGetValue((faultingThreadId, sp.Value), out var frameRegs) && frameRegs.Count > 0)
            {
                frame.Registers ??= new Dictionary<string, string>();
                foreach (var (name, value) in frameRegs)
                {
                    frame.Registers[name] = value;
                }
                appliedCount++;
            }
        }
        
        _logger?.LogDebug("[ClrStack] Applied registers to {Count}/{Total} frames in faulting thread",
            appliedCount, faultingThread.CallStack.Count);
    }
    
    /// <summary>
    /// Fetches registers for frames of a specific thread using LLDB.
    /// Used for the faulting thread to provide register context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Limitation:</b> LLDB can only provide registers for frames it understands (native frames 
    /// and some JIT frames with debug info). Managed frames that exist only in ClrMD's view 
    /// (JIT-compiled code without LLDB symbols) will not have registers because LLDB cannot 
    /// determine their stack pointer.
    /// </para>
    /// <para>
    /// This is a fundamental limitation of the LLDB/ClrMD integration - ClrMD walks the managed 
    /// stack independently and provides SPs that LLDB may not be aware of.
    /// </para>
    /// </remarks>
    private Dictionary<(uint ThreadId, ulong SP), Dictionary<string, string>> FetchAllFrameRegistersForThread(
        LldbManager lldb, 
        uint threadId)
    {
        var result = new Dictionary<(uint, ulong), Dictionary<string, string>>();
        
        try
        {
            // Map thread ID to LLDB index
            var threadIdToIndex = BuildThreadIdToIndexMap(lldb);
            if (!threadIdToIndex.TryGetValue(threadId, out var threadIndex))
            {
                _logger?.LogDebug("[ClrStack] No LLDB thread found for OS thread ID {ThreadId}", threadId);
                return result;
            }
            
            // Select the thread
            var selectOutput = lldb.ExecuteCommand($"thread select {threadIndex}");
            if (selectOutput.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                selectOutput.Contains("invalid", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogDebug("[ClrStack] Failed to select thread {ThreadId} (index {Index})", threadId, threadIndex);
                return result;
            }
            
            // Get backtrace to find all frames and their SPs
            // Use "bt 200" to get more frames than the default limit
            var btOutput = lldb.ExecuteCommand("bt 200");
            var lldbFrames = ParseBacktraceForSPs(btOutput);
            
            _logger?.LogDebug("[ClrStack] Found {Count} frames from bt for faulting thread {ThreadId}", 
                lldbFrames.Count, threadId);
            
            // Create a set of frame indices we've already processed
            var processedFrames = new HashSet<int>();
            
            // First pass: process frames from bt output (these have reliable SPs)
            foreach (var (frameIndex, sp) in lldbFrames)
            {
                try
                {
                    processedFrames.Add(frameIndex);
                    
                    // Select the frame
                    var frameSelectOutput = lldb.ExecuteCommand($"frame select {frameIndex}");
                    if (frameSelectOutput.Contains("error", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    
                    // Read registers for this frame
                    var regOutput = lldb.ExecuteCommand("register read");
                    if (string.IsNullOrEmpty(regOutput))
                        continue;
                    
                    var registers = ParseRegisterOutput(regOutput);
                    if (registers.Count > 0)
                    {
                        result[(threadId, sp)] = registers;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "[ClrStack] Failed to read registers for frame {Frame}", frameIndex);
                }
            }
            
            // Second pass: try frames that weren't in bt output (JIT frames without SP)
            // Find the max frame index from the first pass to know our upper bound
            var maxFrame = lldbFrames.Count > 0 ? lldbFrames.Max(f => f.FrameIndex) : 0;
            var secondPassCount = 0;
            
            for (int frameIndex = 0; frameIndex <= maxFrame + 10; frameIndex++) // +10 for safety
            {
                if (processedFrames.Contains(frameIndex))
                    continue;
                    
                try
                {
                    var frameSelectOutput = lldb.ExecuteCommand($"frame select {frameIndex}");
                    if (frameSelectOutput.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                        frameSelectOutput.Contains("invalid", StringComparison.OrdinalIgnoreCase))
                    {
                        // This frame doesn't exist, but keep trying for frames within range
                        continue;
                    }
                    
                    // Get SP from frame info
                    var frameInfoOutput = lldb.ExecuteCommand("frame info");
                    var spMatch = Regex.Match(frameInfoOutput, @"SP\s*=\s*(0x[0-9a-fA-F]+)", RegexOptions.IgnoreCase);
                    if (!spMatch.Success)
                        continue;
                    
                    if (!ulong.TryParse(spMatch.Groups[1].Value.TrimStart('0', 'x', 'X'), 
                        System.Globalization.NumberStyles.HexNumber, null, out var sp))
                        continue;
                    
                    // Read registers
                    var regOutput = lldb.ExecuteCommand("register read");
                    if (string.IsNullOrEmpty(regOutput))
                        continue;
                    
                    var registers = ParseRegisterOutput(regOutput);
                    if (registers.Count > 0)
                    {
                        result[(threadId, sp)] = registers;
                        secondPassCount++;
                    }
                }
                catch
                {
                    // Ignore errors in second pass
                }
            }
            
            if (secondPassCount > 0)
            {
                _logger?.LogDebug("[ClrStack] Second pass added {Count} frames via frame info", secondPassCount);
            }
            
            _logger?.LogDebug("[ClrStack] Fetched registers for {Count} frames in faulting thread", result.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[ClrStack] Failed to fetch registers for thread {ThreadId}", threadId);
        }
        
        return result;
    }
    
    /// <summary>
    /// Builds a mapping from OS thread ID to LLDB thread index using 'thread list' command.
    /// </summary>
    private static Dictionary<uint, int> BuildThreadIdToIndexMap(LldbManager lldb)
    {
        var result = new Dictionary<uint, int>();
        
        var threadListOutput = lldb.ExecuteCommand("thread list");
        if (string.IsNullOrEmpty(threadListOutput))
            return result;
        
        // Parse thread list output which looks like:
        // * thread #1: tid = 884, 0x0000f5855a9b9800, name = 'dotnet', stop reason = signal SIGSTOP
        //   thread #2: tid = 892, 0x0000f5855a9b9800, name = 'dotnet', stop reason = signal SIGSTOP
        // Or on older LLDB:
        // Process 1234 stopped
        // * thread #1, name = 'dotnet', stop reason = signal SIGSTOP
        //   thread #2, tid = 0x378, 0x0000... 
        
        var lines = threadListOutput.Split('\n');
        foreach (var line in lines)
        {
            // Match pattern: thread #N: tid = DECIMAL or thread #N, tid = 0xHEX
            var indexMatch = Regex.Match(line, @"thread\s+#(\d+)", RegexOptions.IgnoreCase);
            if (!indexMatch.Success)
                continue;
            
            var threadIndex = int.Parse(indexMatch.Groups[1].Value);
            
            // Try to find tid in decimal format first (tid = 884)
            var tidDecMatch = Regex.Match(line, @"tid\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            if (tidDecMatch.Success)
            {
                var tid = uint.Parse(tidDecMatch.Groups[1].Value);
                result[tid] = threadIndex;
                continue;
            }
            
            // Try hex format (tid = 0x378)
            var tidHexMatch = Regex.Match(line, @"tid\s*=\s*0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
            if (tidHexMatch.Success)
            {
                var tid = uint.Parse(tidHexMatch.Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
                result[tid] = threadIndex;
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Parses LLDB backtrace output to extract frame indices and stack pointers.
    /// </summary>
    internal static List<(int FrameIndex, ulong SP)> ParseBacktraceForSPs(string btOutput)
    {
        var result = new List<(int, ulong)>();
        
        // LLDB bt format examples:
        // * frame #0: 0x00007fff... libsystem... `__wait4 + 8
        //   frame #1: 0x00007fff... libsystem... `waitpid + 45
        // With our custom frame-format, SP should be visible
        // We look for patterns like "sp=0x..." or extract from frame info
        
        var lines = btOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            // Match frame number: "frame #N:" or "* frame #N:"
            var frameMatch = Regex.Match(line, @"frame\s*#(\d+)");
            if (!frameMatch.Success)
                continue;
            
            if (!int.TryParse(frameMatch.Groups[1].Value, out var frameIndex))
                continue;
            
            // Only extract frames with explicit SP= pattern from frame-format
            // Frames without SP (like JIT frames) will be handled by the second pass
            // using "frame select" + "frame info" to get the actual SP
            var spMatch = Regex.Match(line, @"SP\s*=\s*(0x[0-9a-fA-F]+)", RegexOptions.IgnoreCase);
            if (spMatch.Success)
            {
                if (ulong.TryParse(spMatch.Groups[1].Value.TrimStart('0', 'x', 'X'), 
                    System.Globalization.NumberStyles.HexNumber, null, out var sp))
                {
                    result.Add((frameIndex, sp));
                }
            }
            // Note: No fallback - frames without SP will be handled by second pass
        }
        
        return result;
    }
    
    /// <summary>
    /// Parses LLDB register output into a dictionary.
    /// </summary>
    internal static Dictionary<string, string> ParseRegisterOutput(string output)
    {
        var result = new Dictionary<string, string>();
        
        // Match patterns like: x0 = 0x0000000000000000 or rax = 0x00007ff812345678
        var matches = Regex.Matches(output, @"(\w+)\s*=\s*(0x[0-9a-fA-F]+)", RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            var name = match.Groups[1].Value.ToLowerInvariant();
            var value = match.Groups[2].Value;

            // Normalize to a consistent pointer-like format with 0x prefix for JSON consumers.
            // LLDB always prints hex with 0x prefix; preserve the digits exactly as returned.
            result[name] = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value : $"0x{value}";
        }
        
        return result;
    }
    
    /// <summary>
    /// Merges managed frames into an existing call stack by stack pointer.
    /// Native frames from bt all are enriched with managed frame data when SP matches.
    /// Managed-only frames are inserted at the correct position.
    /// </summary>
    private void MergeManagedFramesIntoCallStack(ThreadInfo thread, Dictionary<ulong, StackFrame> managedFramesBySp)
    {
        if (managedFramesBySp.Count == 0)
            return;
        
        var mergedFrames = new List<StackFrame>();
        var usedManagedSps = new HashSet<ulong>();
        
        // First pass: Enrich or insert managed frames
        foreach (var nativeFrame in thread.CallStack)
        {
            // Try to parse SP from native frame
            var nativeSp = ParseStackPointer(nativeFrame.StackPointer);
            
            // Check if there's a managed frame for this SP
            if (nativeSp.HasValue && managedFramesBySp.TryGetValue(nativeSp.Value, out var managedFrame))
            {
                // Enrich native frame with managed data
                nativeFrame.Function = managedFrame.Function;
                nativeFrame.Module = managedFrame.Module;
                nativeFrame.SourceFile = managedFrame.SourceFile;
                nativeFrame.LineNumber = managedFrame.LineNumber;
                nativeFrame.Source = managedFrame.Source;
                nativeFrame.IsManaged = true;
                nativeFrame.Registers = managedFrame.Registers;
                nativeFrame.Parameters = managedFrame.Parameters;
                nativeFrame.Locals = managedFrame.Locals;
                
                usedManagedSps.Add(nativeSp.Value);
            }
            
            mergedFrames.Add(nativeFrame);
        }
        
        // Second pass: Add any managed-only frames (not matched to native frames)
        // Insert them in correct SP order (higher SP = earlier in call stack for typical architectures)
        var orphanManagedFrames = managedFramesBySp
            .Where(kv => !usedManagedSps.Contains(kv.Key))
            .OrderByDescending(kv => kv.Key) // Higher SP = earlier frame
            .Select(kv => kv.Value)
            .ToList();
        
        foreach (var orphan in orphanManagedFrames)
        {
            var orphanSp = ParseStackPointer(orphan.StackPointer);
            if (!orphanSp.HasValue)
            {
                // Can't determine position, add at end
                mergedFrames.Add(orphan);
                continue;
            }
            
            // Find insertion point: after the first frame with higher SP
            var insertIndex = mergedFrames.Count;
            for (int i = 0; i < mergedFrames.Count; i++)
            {
                var frameSp = ParseStackPointer(mergedFrames[i].StackPointer);
                if (frameSp.HasValue && frameSp.Value < orphanSp.Value)
                {
                    insertIndex = i;
                    break;
                }
            }
            
            mergedFrames.Insert(insertIndex, orphan);
        }
        
        // Renumber frames
        for (int i = 0; i < mergedFrames.Count; i++)
        {
            mergedFrames[i].FrameNumber = i;
        }
        
        // Replace the call stack
        thread.CallStack.Clear();
        thread.CallStack.AddRange(mergedFrames);
    }
    
    /// <summary>
    /// Parses a stack pointer string to ulong.
    /// Handles formats: "0x0000FFFFCA31B4C0", "0000FFFFCA31B4C0", "[SP=0x0000FFFFCA31B4C0]"
    /// </summary>
    internal static ulong? ParseStackPointer(string? spString)
    {
        if (string.IsNullOrWhiteSpace(spString))
            return null;

        var trimmed = spString.Trim();

        // Common variants we intentionally support:
        // - 0x0000FFFFCA31B4C0
        // - 0000FFFFCA31B4C0
        // - [SP=0x0000FFFFCA31B4C0]
        if (trimmed.StartsWith("[SP=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("SP=", StringComparison.OrdinalIgnoreCase))
        {
            var equalsIndex = trimmed.IndexOf('=');
            if (equalsIndex < 0 || equalsIndex == trimmed.Length - 1)
                return null;
            trimmed = trimmed[(equalsIndex + 1)..].TrimEnd(']');
        }

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];

        if (string.IsNullOrEmpty(trimmed) || !IsValidHexString(trimmed))
            return null;

        return ulong.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out var result)
            ? result
            : null;
    }

    /// <summary>
    /// Determines whether an OS thread ID from ClrMD is usable for report threading.
    /// </summary>
    /// <param name="osThreadId">The operating system thread ID.</param>
    /// <returns><c>true</c> when the thread ID can be used; otherwise <c>false</c>.</returns>
    internal static bool IsValidOsThreadId(uint osThreadId)
        => osThreadId != 0;

    /// <summary>
    /// Extracts a stable module/assembly name for managed frames.
    /// Prefers the ClrMD module path/name, then ClrMD assembly name, then a namespace-derived fallback.
    /// </summary>
    /// <param name="moduleNameOrPath">ClrMD module name or full path.</param>
    /// <param name="assemblyName">ClrMD assembly display name.</param>
    /// <param name="typeName">Declaring type name (fallback only).</param>
    /// <returns>The module name for reporting, or <c>null</c> if unknown.</returns>
    internal static string? ExtractManagedModuleName(string? moduleNameOrPath, string? assemblyName, string? typeName)
    {
        if (!string.IsNullOrWhiteSpace(moduleNameOrPath))
        {
            var trimmed = moduleNameOrPath.Trim();

            // Prefer file stem when the module name is a path or ends with a well-known extension.
            // Example: /usr/share/dotnet/shared/.../System.Private.CoreLib.dll -> System.Private.CoreLib
            var fileName = Path.GetFileName(trimmed);
            if (!string.IsNullOrEmpty(fileName))
            {
                if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFileNameWithoutExtension(fileName);
                }

                // If it's a path without an extension, the filename itself is still a better module name than the full path.
                if (!string.Equals(fileName, trimmed, StringComparison.Ordinal))
                {
                    return fileName;
                }
            }

            return trimmed;
        }

        if (!string.IsNullOrWhiteSpace(assemblyName))
        {
            return assemblyName.Trim();
        }

        return ExtractModuleName(typeName);
    }

    /// <summary>
    /// Extracts module name from a type name.
    /// </summary>
    internal static string? ExtractModuleName(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return null;

        // For types like:
        // - System.String
        // - MyNamespace.MyType
        // - System.Collections.Generic.List`1[[System.__Canon, System.Private.CoreLib]]
        // - System.Collections.Generic.List<System.__Canon>
        //
        // We want the namespace portion, but we must ignore generic arguments
        // (which can contain dots and would otherwise corrupt the result).
        var genericStart = typeName.IndexOfAny(['<', '[']);
        if (genericStart > 0)
        {
            typeName = typeName[..genericStart];
        }

        var lastDot = typeName.LastIndexOf('.');
        if (lastDot > 0)
        {
            // Return the namespace portion as a hint
            return typeName[..lastDot];
        }

        return null;
    }

    /// <summary>
    /// Formats ClrMD inspection result to look similar to dumpobj output.
    /// </summary>
    internal static string FormatClrMdAsDumpObj(ClrMdObjectInspection result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Name:        {result.Type}");
        sb.AppendLine($"MethodTable: {result.MethodTable}");
        sb.AppendLine($"Size:        {result.Size}(0x{result.Size:x}) bytes");
        
        if (result.IsString && result.Value != null)
        {
            sb.AppendLine($"String:      {result.Value}");
        }
        else if (result.IsArray)
        {
            sb.AppendLine($"Array:       Rank 1, Number of elements {result.ArrayLength}, Type {result.ArrayElementType}");
        }
        else if (result.Fields != null && result.Fields.Count > 0)
        {
            sb.AppendLine("Fields:");
            sb.AppendLine("              MT    Field   Offset                 Type VT     Attr            Value Name");
            foreach (var field in result.Fields)
            {
                var value = field.NestedObject != null 
                    ? field.NestedObject.Address 
                    : field.Value?.ToString() ?? "null";
                sb.AppendLine($"0000000000000000  {field.Offset:x8}        0 {field.Type ?? "unknown",-20} 0 instance {value,-16} {field.Name}");
            }
        }
        
        return sb.ToString();
    }

    /// <summary>
    /// Checks if SOS/CLR command output indicates an error or that SOS is not available.
    /// This is used to detect native (non-.NET) dumps where CLR commands won't work.
    /// IMPORTANT: We must be careful not to false-positive on exception messages that
    /// contain words like "not found", "error", etc. (e.g., MissingMethodException).
    /// </summary>
    /// <param name="output">The command output to check.</param>
    /// <returns>True if the output indicates an error or SOS is not available.</returns>
    internal static bool IsSosErrorOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return true;

        // Only check for SOS-specific error patterns, not generic strings that could
        // appear in exception messages (like "not found" in MissingMethodException)
        var sosErrorPatterns = new[]
        {
            "SOS is not loaded",
            "Cannot be determined",
            "Failed to load",
            "Unable to load",
            "not a managed exception",
            "No CLR detected",
            "CLR not available",
            "error: SOS",
            "error: Failed",
            "error: Unable",
            "No managed exception on the current thread",
            "There is no current managed exception",
            "Could not find entry",  // LLDB history error
            "Command not found",     // Command doesn't exist
            "Unrecognized command"   // Unknown command
        };

        foreach (var pattern in sosErrorPatterns)
        {
            if (output.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Performs .NET specific crash analysis.
    /// </summary>
    /// <returns>A crash analysis result with .NET specific information.</returns>
    public async Task<CrashAnalysisResult> AnalyzeDotNetCrashAsync()
    {
        return await AnalyzeDotNetCrashAsync(symbolsOutputDirectory: null);
    }

    /// <summary>
    /// Performs .NET specific crash analysis with optional Datadog symbol download.
    /// </summary>
    /// <param name="symbolsOutputDirectory">Optional directory for storing downloaded symbols.</param>
    /// <returns>A crash analysis result with .NET specific information.</returns>
    public async Task<CrashAnalysisResult> AnalyzeDotNetCrashAsync(string? symbolsOutputDirectory)
    {
        // Command caching is automatically enabled when dump is opened
        // All commands benefit from caching for the entire session
        
        CrashAnalysisResult result;
        try
        {
            // Run base analysis first to reuse parsed environment/threads/modules in the .NET pass.
            result = await AnalyzeCrashCoreAsync(finalizeResult: false); // Base analysis first (caching already enabled)

            // === Datadog Symbol Download ===
            // Download Datadog.Trace symbols BEFORE detailed stack analysis for best stack traces.
            // This uses the platform info from base analysis to download correct artifacts.
            await TryDownloadDatadogSymbolsAsync(result, symbolsOutputDirectory);
            
            // Continue with .NET specific analysis
            // Get CLR version
            var clrVersionOutput = await ExecuteCommandAsync("!eeversion");
            result.RawCommands!["!eeversion"] = clrVersionOutput;
            ParseClrVersion(clrVersionOutput, result);

            // Native stacks from bt all are already parsed by base AnalyzeCrashAsync()
            // We now enrich those native frames with managed frame info from ClrMD
            
            // Try ClrMD first for managed stacks (~500ms vs 12s, no DAC crashes)
            // ClrMD provides direct access to CLR runtime data structures
            var usedClrMd = false;
            if (_clrMdAnalyzer?.IsOpen == true)
            {
                try
                {
                    PopulateManagedStacksViaClrMd(result);
                    usedClrMd = true;
                    _logger?.LogInformation("[Analysis] Used ClrMD for managed stack collection (fast path)");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[Analysis] ClrMD stack collection failed, falling back to SOS");
                }
            }
            
            // Fallback to SOS clrstack if ClrMD is not available or failed
            if (!usedClrMd)
            {
                // Get managed stacks with args/locals (without -f to avoid DAC crashes)
            // -r: include register values
            // -all: all threads
            // -a: include arguments and locals
                var clrStackFullOutput = await ExecuteCommandAsync("clrstack -a -r -all");
                result.RawCommands!["clrstack -a -r -all"] = clrStackFullOutput;
                
                // Parse managed frames and append to existing native frames
                ParseFullCallStacksAllThreads(clrStackFullOutput, result, appendToExisting: true);
            }
            
            // Merge native and managed frames by SP for accurate interleaved stacks
            MergeNativeAndManagedFramesBySP(result);
            
            // Enhance variable values: convert hex to meaningful representations
            // and resolve string values using ClrMD
            await EnhanceVariableValuesAsync(result);

            // Get managed exception with nested exceptions for full exception chain
            // Use !pe -nested which works on WinDbg as-is and gets transformed for LLDB
            var exceptionOutput = await ExecuteCommandAsync("!pe -nested");
            result.RawCommands!["!pe -nested"] = exceptionOutput;
            ParseManagedException(exceptionOutput, result);
            
            // Deep analysis of the exception object (gets Source, Data, FusionLog, etc.)
            await ParseExceptionDeepAnalysisAsync(exceptionOutput, result);
            
            // Try to enrich exception stack frames with source info from thread stacks
            EnrichExceptionStackWithSourceInfo(result);
            
            // For MissingMethodException, TypeLoadException, etc. - analyze what methods exist
            await AnalyzeTypeResolutionAsync(result);
            
            // Analyze NativeAOT and trimming issues
            // This is particularly important for MissingMethodException in NativeAOT apps
            await AnalyzeNativeAotAsync(result);

            // Get heap statistics
            var heapStatsOutput = await ExecuteCommandAsync("!dumpheap -stat");
            result.RawCommands!["!dumpheap -stat"] = heapStatsOutput;
            ParseHeapStats(heapStatsOutput, result);

            // Enhanced memory leak detection for .NET
            AnalyzeDotNetMemoryLeaks(heapStatsOutput, result);

            // Get detailed CLR thread information
            var clrThreadsOutput = await ExecuteCommandAsync("!clrthreads");
            result.RawCommands!["!clrthreads"] = clrThreadsOutput;
            ParseClrThreads(clrThreadsOutput, result);

            // Check for async deadlocks (use clrthreads output)
            DetectAsyncDeadlock(clrThreadsOutput, result);

            // Get finalizer queue
            var finalizerOutput = await ExecuteCommandAsync("!finalizequeue");
            result.RawCommands!["!finalizequeue"] = finalizerOutput;
            ParseFinalizerQueue(finalizerOutput, result);

            // Enhanced deadlock detection using !syncblk
            await AnalyzeDotNetDeadlocksAsync(result);
            
            // Get thread pool information
            var threadPoolOutput = await ExecuteCommandAsync("!threadpool");
            result.RawCommands!["!threadpool"] = threadPoolOutput;
            ParseThreadPool(threadPoolOutput, result);
            
            // Get timer information
            var timerInfoOutput = await ExecuteCommandAsync("!ti");
            result.RawCommands!["!ti"] = timerInfoOutput;
            ParseTimerInfo(timerInfoOutput, result);

            // Analyze OOM (Out of Memory) conditions
            var analyzeOomOutput = await ExecuteCommandAsync("!analyzeoom");
            result.RawCommands!["!analyzeoom"] = analyzeOomOutput;
            ParseAnalyzeOom(analyzeOomOutput, result);

            // Get crash diagnostic info (signals, exception records)
            var crashInfoOutput = await ExecuteCommandAsync("!crashinfo");
            result.RawCommands!["!crashinfo"] = crashInfoOutput;
            ParseCrashInfo(crashInfoOutput, result);

            // Get loaded assemblies with version info (helps diagnose MissingMethodException, etc.)
            var dumpDomainOutput = await ExecuteCommandAsync("!dumpdomain");
            result.RawCommands!["!dumpdomain"] = dumpDomainOutput;
            ParseAssemblyVersions(dumpDomainOutput, result);
            EnrichAssemblyInfo(result);
            
            // Enrich assembly info with version details using !dumpmodule
            await EnrichAssemblyVersionsAsync(result);
            
            // Enrich with ClrMD assembly attributes (Company, Product, Repository, etc.)
            var assemblies = result.Assemblies?.Items;
            if (assemblies != null)
            {
                EnrichAssemblyMetadata(assemblies);
            }
            
            // Enrich with GitHub commit metadata (author, committer, message)
            await EnrichAssemblyGitHubInfoAsync(result);

            // Resolve Source Link URLs for stack frames now that assembly metadata (repo/commit) is available.
            // This populates sourceUrl/sourceProvider even when Portable PDB Source Link data is missing.
            ResolveSourceLinks(result);
            
            // === Phase 2 ClrMD Enrichment ===
            if (_clrMdAnalyzer?.IsOpen == true)
            {
                // Always run fast operations (no heap walk)
                var gcSummary = _clrMdAnalyzer.GetGcSummary();
                
                // Set in new structure
                result.Memory ??= new MemoryInfo();
                result.Memory.Gc = gcSummary;
                
                // Also set in old structure during migration
                
                if (result.Threads?.All != null)
                {
                    EnrichThreadsWithClrMdInfo(result.Threads.All);
                }
                
                // Run optimized single-pass combined analysis for memory, async, and strings
                var combinedAnalysis = _clrMdAnalyzer.GetCombinedHeapAnalysis();
                if (combinedAnalysis != null)
                {
                    // Set in new structure
                    result.Memory.TopConsumers = combinedAnalysis.TopMemoryConsumers;
                    result.Memory.Strings = combinedAnalysis.StringAnalysis;
                    
                    // Async info
                    if (combinedAnalysis.AsyncAnalysis != null)
                    {
                        result.Async ??= new AsyncInfo();
                        result.Async.Summary = combinedAnalysis.AsyncAnalysis.Summary;
                        result.Async.StateMachines = combinedAnalysis.AsyncAnalysis.PendingStateMachines;
                        result.Async.FaultedTasks = combinedAnalysis.AsyncAnalysis.FaultedTasks;
                        result.Async.AnalysisTimeMs = combinedAnalysis.AsyncAnalysis.AnalysisTimeMs;
                        result.Async.WasAborted = combinedAnalysis.AsyncAnalysis.WasAborted;
                    }
                    
                    // Copy fragmentation data to GcSummary (more accurate from heap walk)
                    if (gcSummary != null)
                    {
                        gcSummary.FragmentationBytes = combinedAnalysis.FreeBytes;
                        gcSummary.Fragmentation = combinedAnalysis.FragmentationRatio;
                    }
                }
                
                // === Synchronization Primitives Analysis ===
                // Analyzes locks, semaphores, events, and detects potential deadlocks
                try
                {
                    if (_clrMdAnalyzer.Runtime != null)
                    {
                        _logger?.LogDebug("Starting synchronization primitives analysis...");
                        
                        // Check if sync block enumeration should be skipped (cross-arch, emulation, or env var)
                        var skipSyncBlocks = ShouldSkipSyncBlocks();
                        
                        var syncAnalyzer = new Synchronization.SynchronizationAnalyzer(_clrMdAnalyzer.Runtime, _logger, skipSyncBlocks);
                        result.Synchronization = syncAnalyzer.Analyze();
                        
                        // Add deadlock warnings to recommendations
                        if (result.Synchronization?.PotentialDeadlocks?.Count > 0)
                        {
                            result.Summary ??= new AnalysisSummary();
                            result.Summary.Recommendations ??= [];
                            foreach (var deadlock in result.Synchronization.PotentialDeadlocks)
                            {
                                result.Summary.Recommendations.Add($"⚠️ POTENTIAL DEADLOCK: {deadlock.Description}");
                            }
                        }
                        
                        // Add contention warnings
                        var highContentionCount = result.Synchronization?.ContentionHotspots?.Count(h => h.Severity == "high" || h.Severity == "critical") ?? 0;
                        if (highContentionCount > 0)
                        {
                            result.Summary ??= new AnalysisSummary();
                            result.Summary.Recommendations ??= [];
                            result.Summary.Recommendations.Add($"High lock contention detected on {highContentionCount} synchronization primitive(s)");
                        }
                    }
                }
                catch (Exception syncEx)
                {
                    _logger?.LogWarning(syncEx, "Synchronization analysis failed");
                }
            }

            // Update summary with .NET info
            UpdateDotNetSummary(result);

            CrashAnalysisResultFinalizer.Finalize(result);
        }
        catch (Exception ex)
        {
            result = new CrashAnalysisResult();
            CrashAnalyzer.InitializeNewStructures(result);
            result.Summary!.Description = $".NET analysis failed: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// Parses CLR version from output.
    /// </summary>
    protected void ParseClrVersion(string output, CrashAnalysisResult result)
    {
        // Look for version pattern like "4.0.30319.42000"
        var versionMatch = Regex.Match(output, @"(\d+\.\d+\.\d+\.\d+)");
        if (versionMatch.Success)
        {
            var clrVersion = versionMatch.Groups[1].Value;
            
            // Set in Runtime info (new hierarchical structure)
            result.Environment ??= new EnvironmentInfo { Platform = new PlatformInfo() };
            result.Environment.Runtime ??= new RuntimeInfo { Type = "CoreCLR" };
            result.Environment.Runtime.ClrVersion = clrVersion;
            
            // Also set in platform info for backward compatibility
            result.Environment.Platform ??= new PlatformInfo();
            result.Environment.Platform.RuntimeVersion = clrVersion;
        }
    }

    /// <summary>
    /// Parses managed exception information from '!pe -nested' output.
    /// Extracts exception type, message, HResult, stack trace, and inner exceptions.
    /// </summary>
    protected void ParseManagedException(string output, CrashAnalysisResult result)
    {
        // For native dumps, pe will fail - gracefully skip
        if (IsSosErrorOutput(output))
        {
            return;
        }

        // Check if there's an exception
        if (output.Contains("Exception object:") || output.Contains("Exception type:"))
        {
            // Initialize Exception if not already
            result.Exception ??= new ExceptionDetails();
            
            // Extract exception type
            var typeMatch = Regex.Match(output, @"Exception type:\s+(.+)");
            if (typeMatch.Success)
            {
                var exceptionType = typeMatch.Groups[1].Value.Trim();
                result.Exception.Type = exceptionType;
                result.Summary!.CrashType = ".NET Managed Exception";
                
                // Also update DotNetInfo during migration
            }
            else
            {
                result.Exception.Type = "See raw output for details";
            }

            // Extract exception message
            var messageMatch = Regex.Match(output, @"Message:\s+(.+)");
            if (messageMatch.Success)
            {
                var message = messageMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(message) && message != "<none>")
                {
                    result.Exception.Message = message;
                }
            }

            // Extract HResult
            var hresultMatch = Regex.Match(output, @"HResult:\s+([0-9a-fA-F]+)");
            if (hresultMatch.Success)
            {
                var hResult = hresultMatch.Groups[1].Value.Trim();
                result.Exception.HResult = hResult;
            }

            // Extract exception stack trace
            // Format: "StackTrace (generated):\n    SP               IP               Function\n    ADDR ADDR Module!Method+offset"
            ParseExceptionStackTrace(output, result);

            // Check for inner exceptions (pe -nested shows nested exceptions)
            var innerMatch = Regex.Match(output, @"InnerException:\s+(.+)");
            if (innerMatch.Success)
            {
                var inner = innerMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(inner) && inner != "<none>")
                {
                    result.Exception.HasInnerException = true;
                    
                    // Count nested exceptions
                    var nestedCount = Regex.Matches(output, @"Nested exception").Count;
                    if (nestedCount > 0)
                    {
                        result.Exception.NestedExceptionCount = nestedCount;
                    }
                }
            }

            // Add recommendation with exception details
            if (!string.IsNullOrEmpty(result.Exception.Message))
            {
                var rec = $"Exception: {result.Exception.Type} - {result.Exception.Message}";
                result.Summary?.Recommendations?.Add(rec);
            }
            else
            {
                var rec = "Review the managed exception stack trace and inner exceptions.";
                result.Summary?.Recommendations?.Add(rec);
            }
        }
        else if (output.Contains("There is no current managed exception"))
        {
        }
    }

    /// <summary>
    /// Performs deep analysis of the exception object to extract additional properties.
    /// This gets Source, HelpLink, Data dictionary, FusionLog, inner exceptions, etc.
    /// </summary>
    /// <param name="peOutput">The output from !pe -nested command.</param>
    /// <param name="result">The crash analysis result to populate.</param>
    private async Task ParseExceptionDeepAnalysisAsync(string peOutput, CrashAnalysisResult result)
    {
        // For native dumps or no exception, skip
        if (IsSosErrorOutput(peOutput)) return;
        if (!peOutput.Contains("Exception object:") && !peOutput.Contains("Exception type:")) return;
        
        // Extract exception object address
        var addressMatch = Regex.Match(peOutput, @"Exception object:\s*([0-9a-fA-Fx]+)");
        if (!addressMatch.Success) return;
        
        var exceptionAddress = addressMatch.Groups[1].Value;
        
        // Use new structure for exception info, fall back to old
        var exceptionType = result.Exception?.Type;
        var exceptionMessage = result.Exception?.Message;
        var exceptionHResult = result.Exception?.HResult;
        
        // Start building the ExceptionAnalysis
        var analysis = new ExceptionAnalysis
        {
            ExceptionAddress = exceptionAddress,
            FullTypeName = exceptionType,
            Message = exceptionMessage,
            HResult = FormatHResult(exceptionHResult)
        };
        
        // Get StackTraceString from pe output (multi-line, ends at HResult or next section)
        var stackTraceStringMatch = Regex.Match(peOutput, @"StackTraceString:\s*(.+?)(?=\nHResult:|\nNested exception|$)", RegexOptions.Singleline);
        if (stackTraceStringMatch.Success)
        {
            var stackStr = stackTraceStringMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(stackStr) && stackStr != "<none>")
            {
                analysis.StackTraceString = stackStr;
            }
        }
        
        // Inspect the exception object using dumpobj to get additional fields
        await InspectExceptionObjectAsync(exceptionAddress, analysis, result);
        
        // Parse nested/inner exceptions from the pe -nested output (with circular reference protection)
        await ParseExceptionChainFromPeOutputAsync(peOutput, analysis, result);
        
        // Add exception-type-specific properties
        await AddExceptionTypeSpecificPropertiesAsync(analysis, result);
        
        // Set in new structure
        result.Exception ??= new ExceptionDetails();
        result.Exception.Analysis = analysis;
        
        // Also set in old structure during migration
    }
    
    /// <summary>
    /// Inspects an exception object using dumpobj to get additional fields like Source, Data, etc.
    /// </summary>
    private async Task InspectExceptionObjectAsync(string address, ExceptionAnalysis analysis, CrashAnalysisResult result)
    {
        try
        {
            // Use ClrMD-based dumpobj to inspect the exception object
            var dumpOutput = await DumpObjectViaClrMdAsync(address);
            result.RawCommands![$"ClrMD:InspectObject({address})"] = dumpOutput;
            
            if (IsSosErrorOutput(dumpOutput)) return;
            
            // Parse fields from dumpobj output
            // Format: "      MT    Field   Offset                 Type VT     Attr            Value Name"
            //         "0000... 400001c       10         System.String  0 instance 0000... _className"
            
            var lines = dumpOutput.Split('\n');
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                
                // Parse field lines - look for known exception fields
                // Example: 0000f7558ba8f6b8  4000119       88        System.String  0 instance 0000f7558edfca90 _source
                var fieldMatch = Regex.Match(trimmedLine, 
                    @"[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+(\S+)\s+\d\s+\w+\s+([0-9a-fA-Fx]+|null)\s+(\w+)");
                
                if (!fieldMatch.Success) continue;
                
                // Groups: 1=Type, 2=Value, 3=Name
                var fieldValue = fieldMatch.Groups[2].Value;
                var fieldName = fieldMatch.Groups[3].Value;
                
                // Skip null values
                if (IsNullAddress(fieldValue)) continue;
                
                switch (fieldName.ToLowerInvariant())
                {
                    case "_message":
                        // Always try to get the full message from the object
                        // The pe output might truncate long or multi-line messages
                        var objMessage = await GetStringValueAsync(fieldValue);
                        if (!string.IsNullOrEmpty(objMessage) && 
                            (string.IsNullOrEmpty(analysis.Message) || objMessage.Length > analysis.Message.Length))
                        {
                            analysis.Message = objMessage;
                        }
                        break;
                    case "_source":
                        analysis.Source = await GetStringValueAsync(fieldValue);
                        break;
                    case "_helpurl":
                    case "_helplink":
                        analysis.HelpLink = await GetStringValueAsync(fieldValue);
                        break;
                    case "_innerexception":
                        // Inner exception will be handled by ParseExceptionChainFromPeOutput
                        // but we store the address for reference
                        break;
                    case "_stacktracestring":
                        if (string.IsNullOrEmpty(analysis.StackTraceString))
                        {
                            analysis.StackTraceString = await GetStringValueAsync(fieldValue);
                        }
                        break;
                    case "_remotestacktrace":
                    case "_remotestacktracestring":
                        analysis.RemoteStackTraceString = await GetStringValueAsync(fieldValue);
                        break;
                    case "_data":
                        // Inspect the Data dictionary to get its contents
                        var dataDict = await InspectDataDictionaryAsync(fieldValue);
                        if (dataDict != null && dataDict.Count > 0)
                        {
                            analysis.Data = dataDict;
                        }
                        break;
                    case "_fusionlog":
                        analysis.FusionLog = await GetStringValueAsync(fieldValue);
                        break;
                    case "_watsonbuckets":
                        // Watson buckets are internal crash reporting data
                        // Note: We already skip null values above, but being explicit
                        analysis.WatsonBuckets = fieldValue;
                        break;
                    case "_hresult":
                        // HResult might be stored as an int field
                        // Only update if we don't already have it from pe output
                        if (string.IsNullOrEmpty(analysis.HResult))
                        {
                            analysis.HResult = FormatHResult(fieldValue);
                        }
                        break;
                }
            }
            
            // Parse target site from the exception's _stackTrace field if available
            await ParseTargetSiteAsync(dumpOutput, analysis, result);
        }
        catch
        {
            // Silently ignore failures - don't fail the whole analysis
        }
    }
    
    /// <summary>
    /// Gets a string value from an object address using ClrMD-based dumpobj.
    /// Handles both single-line and multi-line string values.
    /// </summary>
    private async Task<string?> GetStringValueAsync(string address, Dictionary<string, string>? rawCommands = null)
    {
        if (IsNullAddress(address))
        {
            return null;
        }
        
        try
        {
            var output = await DumpObjectViaClrMdAsync(address);
            
            // Store command output if rawCommands provided
            if (rawCommands != null)
            {
                rawCommands[$"ClrMD:InspectObject({address})"] = output;
            }
            
            // Look for the String: line which contains the actual string value
            // Format: String:          Method not found: '...'
            // For multiline strings, content continues on next lines
            var stringIndex = output.IndexOf("String:", StringComparison.OrdinalIgnoreCase);
            if (stringIndex >= 0)
            {
                var afterString = output[(stringIndex + 7)..]; // Skip "String:"
                
                // Find where the string content ends (typically before Fields: or next section)
                var endMarkers = new[] { "\nFields:", "\nMT ", "\nMethodTable:", "\nEEClass:" };
                var endIndex = afterString.Length;
                
                foreach (var marker in endMarkers)
                {
                    var markerIndex = afterString.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (markerIndex >= 0 && markerIndex < endIndex)
                    {
                        endIndex = markerIndex;
                    }
                }
                
                var stringValue = afterString[..endIndex].Trim();
                if (!string.IsNullOrEmpty(stringValue) && stringValue != "<none>")
                {
                    return stringValue;
                }
            }
            
            // Alternate format - value directly after the type
            var valueMatch = Regex.Match(output, @"(?:String|System\.String).*?Value:\s*""?(.+?)""?\s*$", RegexOptions.Multiline);
            if (valueMatch.Success)
            {
                return valueMatch.Groups[1].Value.Trim();
            }
        }
        catch
        {
            // Ignore errors reading string values
        }
        
        return null;
    }
    
    /// <summary>
    /// Inspects the exception's Data dictionary (IDictionary) to extract key-value pairs.
    /// Exception.Data typically uses ListDictionaryInternal (linked list structure).
    /// </summary>
    private async Task<Dictionary<string, string>?> InspectDataDictionaryAsync(string address)
    {
        if (IsNullAddress(address))
        {
            return null;
        }
        
        try
        {
            var output = await DumpObjectViaClrMdAsync(address);
            if (IsSosErrorOutput(output)) return null;
            
            var data = new Dictionary<string, string>();
            
            // ListDictionaryInternal has a linked list structure:
            // - head: DictionaryNode (contains key, value, next)
            var headAddr = ExtractFieldAddress(output, "head");
            if (!string.IsNullOrEmpty(headAddr))
            {
                // Traverse the linked list
                var currentNodeAddr = headAddr;
                var count = 0;
                var visitedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                while (!IsNullAddress(currentNodeAddr) && count < 10) // Limit to 10 entries
                {
                    var normalizedNodeAddr = NormalizeAddress(currentNodeAddr);
                    if (visitedNodes.Contains(normalizedNodeAddr))
                    {
                        break; // Circular reference protection
                    }
                    visitedNodes.Add(normalizedNodeAddr);
                    
                    var nodeOutput = await DumpObjectViaClrMdAsync(currentNodeAddr!);
                    if (IsSosErrorOutput(nodeOutput)) break;
                    
                    var keyAddr = ExtractFieldAddress(nodeOutput, "key");
                    var valueAddr = ExtractFieldAddress(nodeOutput, "value");
                    
                    if (!string.IsNullOrEmpty(keyAddr))
                    {
                        var key = await GetStringValueAsync(keyAddr) ?? $"key_{count}";
                        var value = !string.IsNullOrEmpty(valueAddr) 
                            ? await GetStringValueAsync(valueAddr) ?? valueAddr 
                            : "null";
                        
                        data[key] = value;
                        count++;
                    }
                    
                    // Move to next node
                    currentNodeAddr = ExtractFieldAddress(nodeOutput, "next");
                }
                
                if (count >= 10)
                {
                    data["_truncated"] = "true";
                }
            }
            
            // Fallback: Try array-based dictionary (for other dictionary types)
            if (data.Count == 0)
            {
                var entriesAddr = ExtractFieldAddress(output, "_entries") ?? ExtractFieldAddress(output, "entries");
                if (!string.IsNullOrEmpty(entriesAddr))
                {
                    var entriesOutput = await ExecuteCommandAsync($"!dumparray {entriesAddr}");
                    if (!IsSosErrorOutput(entriesOutput))
                    {
                        // Look for non-null entries
                        var entryPattern = new Regex(@"^\s*\d+\s+([0-9a-fA-Fx]+)", RegexOptions.Multiline);
                        var matches = entryPattern.Matches(entriesOutput);
                        var arrayCount = 0;
                        foreach (Match entry in matches)
                        {
                            if (arrayCount >= 10) break;
                            
                            var entryAddr = entry.Groups[1].Value;
                            if (!IsNullAddress(entryAddr))
                            {
                                var entryOutput = await DumpObjectViaClrMdAsync(entryAddr);
                                if (!IsSosErrorOutput(entryOutput))
                                {
                                    var keyAddr = ExtractFieldAddress(entryOutput, "key");
                                    var valueAddr = ExtractFieldAddress(entryOutput, "value");
                                    
                                    if (!string.IsNullOrEmpty(keyAddr))
                                    {
                                        var key = await GetStringValueAsync(keyAddr) ?? $"key_{arrayCount}";
                                        var value = !string.IsNullOrEmpty(valueAddr) 
                                            ? await GetStringValueAsync(valueAddr) ?? valueAddr 
                                            : "null";
                                        
                                        data[key] = value;
                                        arrayCount++;
                                    }
                                }
                            }
                        }
                        
                        // Mark as truncated if we hit the limit
                        if (arrayCount >= 10 && matches.Count > 10)
                        {
                            data["_truncated"] = "true";
                        }
                    }
                }
            }
            
            // Check for _count to know if there are entries we couldn't read
            if (data.Count == 0)
            {
                var countMatch = Regex.Match(output, 
                    @"[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+System\.Int32\s+\d\s+\w+\s+(\d+)\s+(?:_count|count|Count)");
                if (countMatch.Success)
                {
                    var dictCount = int.Parse(countMatch.Groups[1].Value);
                    if (dictCount > 0)
                    {
                        data["_count"] = dictCount.ToString();
                    }
                }
            }
            
            return data.Count > 0 ? data : null;
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>
    /// Parses target site information from the exception.
    /// Falls back to deriving from the exception stack trace if _exceptionMethod field isn't available.
    /// </summary>
    private async Task ParseTargetSiteAsync(string dumpOutput, ExceptionAnalysis analysis, CrashAnalysisResult result)
    {
        // First try: Look for _exceptionMethod or _targetSite field
        var methodMatch = Regex.Match(dumpOutput, 
            @"[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+\S+\s+\d\s+\w+\s+([0-9a-fA-Fx]+)\s+(?:_exceptionMethod|_targetSite)");
        
        if (methodMatch.Success && !IsNullAddress(methodMatch.Groups[1].Value))
        {
            var methodAddress = methodMatch.Groups[1].Value;
            
            try
            {
                // Dump the MethodBase object to get method info (using ClrMD-based method)
                var methodOutput = await DumpObjectViaClrMdAsync(methodAddress);
                if (!IsSosErrorOutput(methodOutput))
                {
                    analysis.TargetSite = new TargetSiteInfo();
                    
                    // Try to extract method name
                    var nameMatch = Regex.Match(methodOutput, @"Name:\s+(.+)$", RegexOptions.Multiline);
                    if (nameMatch.Success)
                    {
                        analysis.TargetSite.Name = nameMatch.Groups[1].Value.Trim();
                    }
                    
                    // If we can find the declaring type
                    var declaringTypeMatch = Regex.Match(methodOutput, 
                        @"[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+\S+\s+\d\s+\w+\s+([0-9a-fA-Fx]+)\s+(?:m_declaringType|_declaringType|DeclaringType)");
                    
                    if (declaringTypeMatch.Success && !IsNullAddress(declaringTypeMatch.Groups[1].Value))
                    {
                        var typeOutput = await DumpObjectViaClrMdAsync(declaringTypeMatch.Groups[1].Value);
                        var typeNameMatch = Regex.Match(typeOutput, @"Name:\s+(.+)$", RegexOptions.Multiline);
                        if (typeNameMatch.Success)
                        {
                            analysis.TargetSite.DeclaringType = typeNameMatch.Groups[1].Value.Trim();
                        }
                    }
                    
                    // If we got a name, we're done
                    if (!string.IsNullOrEmpty(analysis.TargetSite.Name))
                    {
                        return;
                    }
                }
            }
            catch
            {
                // Fall through to try stack trace fallback
            }
        }
        
        // Fallback: Derive TargetSite from the first frame of the exception stack trace
        // The first frame is typically the method that threw the exception
        var exceptionStackTrace = result.Exception?.StackTrace;
        if (exceptionStackTrace?.Count > 0)
        {
            var firstFrame = exceptionStackTrace[0];
            if (!string.IsNullOrEmpty(firstFrame.Function))
            {
                analysis.TargetSite ??= new TargetSiteInfo();
                
                // Parse "Namespace.Class.Method(args)" format
                var methodName = firstFrame.Function;
                var parenIndex = methodName.IndexOf('(');
                if (parenIndex > 0)
                {
                    // Has signature - extract it
                    analysis.TargetSite.Signature = methodName;
                    methodName = methodName[..parenIndex];
                }
                
                // Split into declaring type and method name
                var lastDotIndex = methodName.LastIndexOf('.');
                if (lastDotIndex > 0)
                {
                    analysis.TargetSite.DeclaringType = methodName[..lastDotIndex];
                    analysis.TargetSite.Name = methodName[(lastDotIndex + 1)..];
                }
                else
                {
                    analysis.TargetSite.Name = methodName;
                }
            }
        }
    }
    
    /// <summary>
    /// Maximum depth for exception chain traversal to prevent excessive command execution.
    /// Most legitimate exception chains are under 10 levels deep.
    /// </summary>
    private const int MaxExceptionChainDepth = 20;
    
    /// <summary>
    /// Parses the exception chain from !pe -nested output.
    /// Uses a visited set to prevent infinite loops from circular references.
    /// Limited to MaxExceptionChainDepth to prevent excessive command execution.
    /// </summary>
    private async Task ParseExceptionChainFromPeOutputAsync(string peOutput, ExceptionAnalysis analysis, CrashAnalysisResult result)
    {
        var chain = new List<ExceptionChainEntry>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var depth = 0;
        
        // Add the root exception (already visited)
        if (!string.IsNullOrEmpty(analysis.ExceptionAddress))
        {
            visited.Add(NormalizeAddress(analysis.ExceptionAddress));
        }
        
        chain.Add(new ExceptionChainEntry
        {
            Depth = depth,
            Type = analysis.FullTypeName,
            Message = analysis.Message,
            HResult = analysis.HResult,
            Address = analysis.ExceptionAddress,
            Source = analysis.Source
        });
        
        // Look for nested exception sections
        // Format: "Nested exception -----------------------------------------------------"
        //         "Exception object: 0000f7158edfca10"
        //         "Exception type:   System.TypeLoadException"
        //         "Message:          Could not load type..."
        
        var nestedSections = peOutput.Split(new[] { "Nested exception" }, StringSplitOptions.RemoveEmptyEntries);
        ExceptionAnalysis? currentInner = null;
        
        for (var i = 1; i < nestedSections.Length && depth < MaxExceptionChainDepth; i++)
        {
            var section = nestedSections[i];
            depth++;
            
            var entry = new ExceptionChainEntry { Depth = depth };
            
            var addressMatch = Regex.Match(section, @"Exception object:\s*([0-9a-fA-Fx]+)");
            if (addressMatch.Success)
            {
                entry.Address = addressMatch.Groups[1].Value;
                var normalizedAddr = NormalizeAddress(entry.Address);
                
                // Check for circular reference
                if (visited.Contains(normalizedAddr))
                {
                    entry.Message = "[Circular reference detected]";
                    chain.Add(entry);
                    break;
                }
                visited.Add(normalizedAddr);
            }
            
            var typeMatch = Regex.Match(section, @"Exception type:\s+(.+)");
            if (typeMatch.Success)
            {
                entry.Type = typeMatch.Groups[1].Value.Trim();
            }
            
            // Message can be multi-line, capture until next field marker
            var messageMatch = Regex.Match(section, @"Message:\s+(.+?)(?=\nInnerException:|\nStackTrace|\nHResult:|\nNested exception|$)", RegexOptions.Singleline);
            if (messageMatch.Success)
            {
                var msg = messageMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(msg) && msg != "<none>")
                {
                    entry.Message = msg;
                }
            }
            
            var hresultMatch = Regex.Match(section, @"HResult:\s+([0-9a-fA-F]+)");
            if (hresultMatch.Success)
            {
                entry.HResult = FormatHResult(hresultMatch.Groups[1].Value.Trim());
            }
            
            chain.Add(entry);
            
            // Build nested InnerException structure with deep inspection
            var innerAnalysis = new ExceptionAnalysis
            {
                ExceptionAddress = entry.Address,
                FullTypeName = entry.Type,
                Message = entry.Message,
                HResult = entry.HResult
            };
            
            // Deep inspect the inner exception to get Source, FusionLog, etc.
            if (!string.IsNullOrEmpty(entry.Address))
            {
                await InspectExceptionObjectAsync(entry.Address, innerAnalysis, result);
                
                // Update chain entry with discovered/improved values
                entry.Source = innerAnalysis.Source;
                // Message might have been updated with full multi-line content
                if (!string.IsNullOrEmpty(innerAnalysis.Message) && 
                    (string.IsNullOrEmpty(entry.Message) || innerAnalysis.Message.Length > entry.Message.Length))
                {
                    entry.Message = innerAnalysis.Message;
                }
                // HResult might have been discovered from the object
                if (!string.IsNullOrEmpty(innerAnalysis.HResult) && string.IsNullOrEmpty(entry.HResult))
                {
                    entry.HResult = innerAnalysis.HResult;
                }
                
                // Also extract type-specific properties for inner exceptions
                await AddExceptionTypeSpecificPropertiesAsync(innerAnalysis, result);
            }
            
            // Link to the chain
            if (currentInner == null)
            {
                analysis.InnerException = innerAnalysis;
                currentInner = innerAnalysis;
            }
            else
            {
                currentInner.InnerException = innerAnalysis;
                currentInner = innerAnalysis;
            }
        }
        
        if (chain.Count > 0)
        {
            analysis.ExceptionChain = chain;
        }
    }
    
    /// <summary>
    /// Adds exception-type-specific properties based on the exception type.
    /// For example: FileNotFoundException has FileName, TypeLoadException has TypeName, etc.
    /// </summary>
    private async Task AddExceptionTypeSpecificPropertiesAsync(ExceptionAnalysis analysis, CrashAnalysisResult result)
    {
        if (string.IsNullOrEmpty(analysis.FullTypeName) || string.IsNullOrEmpty(analysis.ExceptionAddress)) return;
        
        var customProps = new Dictionary<string, object?>();
        var typeName = analysis.FullTypeName;
        
        try
        {
            // Get the exception object details (using ClrMD-based dumpobj)
            var dumpOutput = await DumpObjectViaClrMdAsync(analysis.ExceptionAddress);
            result.RawCommands![$"ClrMD:InspectObject({analysis.ExceptionAddress})"] = dumpOutput;
            if (IsSosErrorOutput(dumpOutput)) return;
            
            // FileNotFoundException / FileLoadException
            if (typeName.Contains("FileNotFoundException") || typeName.Contains("FileLoadException"))
            {
                var fileNameValue = ExtractFieldAddress(dumpOutput, "_fileName");
                if (!string.IsNullOrEmpty(fileNameValue))
                {
                    customProps["fileName"] = await GetStringValueAsync(fileNameValue);
                }
                
                // FusionLog is particularly important for these
                var fusionLogValue = ExtractFieldAddress(dumpOutput, "_fusionLog");
                if (!string.IsNullOrEmpty(fusionLogValue) && string.IsNullOrEmpty(analysis.FusionLog))
                {
                    analysis.FusionLog = await GetStringValueAsync(fusionLogValue);
                }
            }
            // TypeLoadException
            else if (typeName.Contains("TypeLoadException"))
            {
                var typeNameValue = ExtractFieldAddress(dumpOutput, "_className");
                if (!string.IsNullOrEmpty(typeNameValue))
                {
                    customProps["typeName"] = await GetStringValueAsync(typeNameValue);
                }
                
                var assemblyNameValue = ExtractFieldAddress(dumpOutput, "_assemblyName");
                if (!string.IsNullOrEmpty(assemblyNameValue))
                {
                    customProps["assemblyName"] = await GetStringValueAsync(assemblyNameValue);
                }
            }
            // MissingMethodException / MissingFieldException / MissingMemberException
            else if (typeName.Contains("MissingMethodException") || 
                     typeName.Contains("MissingFieldException") || 
                     typeName.Contains("MissingMemberException"))
            {
                var classNameValue = ExtractFieldAddress(dumpOutput, "_className");
                if (!string.IsNullOrEmpty(classNameValue))
                {
                    customProps["className"] = await GetStringValueAsync(classNameValue);
                }
                
                var memberNameValue = ExtractFieldAddress(dumpOutput, "_memberName");
                if (!string.IsNullOrEmpty(memberNameValue))
                {
                    customProps["memberName"] = await GetStringValueAsync(memberNameValue);
                }
                
                var signatureValue = ExtractFieldAddress(dumpOutput, "_signature");
                if (!string.IsNullOrEmpty(signatureValue))
                {
                    customProps["signature"] = await GetStringValueAsync(signatureValue);
                }
            }
            // ArgumentException
            else if (typeName.Contains("ArgumentException") || typeName.Contains("ArgumentNullException") || typeName.Contains("ArgumentOutOfRangeException"))
            {
                var paramNameValue = ExtractFieldAddress(dumpOutput, "_paramName");
                if (!string.IsNullOrEmpty(paramNameValue))
                {
                    customProps["paramName"] = await GetStringValueAsync(paramNameValue);
                }
                
                // ArgumentOutOfRangeException may have actual value
                if (typeName.Contains("ArgumentOutOfRangeException"))
                {
                    var actualValueField = ExtractFieldAddress(dumpOutput, "_actualValue");
                    if (!string.IsNullOrEmpty(actualValueField))
                    {
                        customProps["actualValue"] = actualValueField; // Could be any type
                    }
                }
            }
            // SocketException / WebException
            else if (typeName.Contains("SocketException"))
            {
                // Socket error code
                var errorCodeMatch = Regex.Match(dumpOutput, @"[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+System\.Int32\s+\d\s+\w+\s+(-?\d+)\s+(?:_errorCode|m_errorCode|_nativeErrorCode)");
                if (errorCodeMatch.Success)
                {
                    customProps["socketErrorCode"] = int.Parse(errorCodeMatch.Groups[1].Value);
                }
            }
            // AggregateException
            else if (typeName.Contains("AggregateException"))
            {
                // Count inner exceptions
                var innerExceptionsField = ExtractFieldAddress(dumpOutput, "_innerExceptions");
                if (!string.IsNullOrEmpty(innerExceptionsField))
                {
                    customProps["hasInnerExceptions"] = true;
                }
            }
            // ReflectionTypeLoadException
            else if (typeName.Contains("ReflectionTypeLoadException"))
            {
                var loaderExceptionsField = ExtractFieldAddress(dumpOutput, "_loaderExceptions");
                if (!string.IsNullOrEmpty(loaderExceptionsField))
                {
                    customProps["hasLoaderExceptions"] = true;
                }
            }
            // OperationCanceledException / TaskCanceledException
            else if (typeName.Contains("OperationCanceledException") || typeName.Contains("TaskCanceledException"))
            {
                customProps["isCancellation"] = true;
            }
            // ObjectDisposedException
            else if (typeName.Contains("ObjectDisposedException"))
            {
                var objectNameValue = ExtractFieldAddress(dumpOutput, "_objectName");
                if (!string.IsNullOrEmpty(objectNameValue))
                {
                    customProps["objectName"] = await GetStringValueAsync(objectNameValue);
                }
            }
            // HttpRequestException
            else if (typeName.Contains("HttpRequestException"))
            {
                // Status code might be available
                var statusCodeMatch = Regex.Match(dumpOutput, @"[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+\S*HttpStatusCode\S*\s+\d\s+\w+\s+(\d+)\s+(?:_statusCode|StatusCode)");
                if (statusCodeMatch.Success)
                {
                    customProps["httpStatusCode"] = int.Parse(statusCodeMatch.Groups[1].Value);
                }
            }
            // WebException
            else if (typeName.Contains("WebException"))
            {
                // WebExceptionStatus enum
                var statusMatch = Regex.Match(dumpOutput, @"[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+\S*WebExceptionStatus\S*\s+\d\s+\w+\s+(\d+)\s+(?:_status|m_Status)");
                if (statusMatch.Success)
                {
                    customProps["webExceptionStatus"] = int.Parse(statusMatch.Groups[1].Value);
                }
                
                // Response might have more info
                var responseField = ExtractFieldAddress(dumpOutput, "_response");
                if (!string.IsNullOrEmpty(responseField))
                {
                    customProps["hasResponse"] = true;
                }
            }
            // SecurityException
            else if (typeName.Contains("SecurityException"))
            {
                var demandedValue = ExtractFieldAddress(dumpOutput, "_demanded");
                if (!string.IsNullOrEmpty(demandedValue))
                {
                    customProps["hasDemanded"] = true;
                }
                
                var permissionStateValue = ExtractFieldAddress(dumpOutput, "_permissionState");
                if (!string.IsNullOrEmpty(permissionStateValue))
                {
                    customProps["permissionState"] = await GetStringValueAsync(permissionStateValue);
                }
                
                var actionValue = Regex.Match(dumpOutput, @"[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+\S*SecurityAction\S*\s+\d\s+\w+\s+(\d+)\s+(?:_action|m_action)");
                if (actionValue.Success)
                {
                    customProps["securityAction"] = int.Parse(actionValue.Groups[1].Value);
                }
            }
            // InvalidCastException - add the types involved if available
            else if (typeName.Contains("InvalidCastException"))
            {
                // These exceptions often have limited metadata, but message usually tells the story
                customProps["isInvalidCast"] = true;
            }
            // NotImplementedException / NotSupportedException
            else if (typeName.Contains("NotImplementedException") || typeName.Contains("NotSupportedException"))
            {
                customProps["isNotImplementedOrSupported"] = true;
            }
            // OutOfMemoryException - correlate with OOM analysis
            else if (typeName.Contains("OutOfMemoryException"))
            {
                customProps["isOOM"] = true;
                // Note: OOM details are in result.Memory.Oom
            }
            // StackOverflowException
            else if (typeName.Contains("StackOverflowException"))
            {
                customProps["isStackOverflow"] = true;
            }
            // NullReferenceException - very common, no extra fields but useful to flag
            else if (typeName.Contains("NullReferenceException"))
            {
                customProps["isNullReference"] = true;
            }
            // AccessViolationException
            else if (typeName.Contains("AccessViolationException"))
            {
                customProps["isAccessViolation"] = true;
            }
            // DivideByZeroException
            else if (typeName.Contains("DivideByZeroException"))
            {
                customProps["isDivideByZero"] = true;
            }
            // IndexOutOfRangeException
            else if (typeName.Contains("IndexOutOfRangeException"))
            {
                customProps["isIndexOutOfRange"] = true;
            }
            // FormatException
            else if (typeName.Contains("FormatException"))
            {
                customProps["isFormatException"] = true;
            }
            // TimeoutException
            else if (typeName.Contains("TimeoutException"))
            {
                customProps["isTimeout"] = true;
            }
            // InvalidOperationException - very common, flags invalid state
            else if (typeName.Contains("InvalidOperationException"))
            {
                customProps["isInvalidOperation"] = true;
            }
            // KeyNotFoundException
            else if (typeName.Contains("KeyNotFoundException"))
            {
                customProps["isKeyNotFound"] = true;
            }
            // ArithmeticException (parent of DivideByZeroException, OverflowException)
            else if (typeName.Contains("OverflowException"))
            {
                customProps["isOverflow"] = true;
            }
            
            if (customProps.Count > 0)
            {
                analysis.CustomProperties = customProps;
            }
        }
        catch
        {
            // Silently ignore failures adding type-specific properties
        }
    }
    
    /// <summary>
    /// Extracts a field address from dumpobj output.
    /// </summary>
    private static string? ExtractFieldAddress(string dumpOutput, string fieldName)
    {
        var pattern = $@"[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+[0-9a-fA-Fx]+\s+\S+\s+\d\s+\w+\s+([0-9a-fA-Fx]+)\s+{Regex.Escape(fieldName)}";
        var match = Regex.Match(dumpOutput, pattern, RegexOptions.IgnoreCase);
        
        if (match.Success)
        {
            var value = match.Groups[1].Value;
            if (!IsNullAddress(value))
            {
                return value;
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Extracts just the hex address from a string that may include type info.
    /// Handles formats like "0x1234", "1234", "0x1234 (System.String)", etc.
    /// Returns just the hex digits without 0x prefix.
    /// </summary>
    internal static string? ExtractHexAddress(string? address)
    {
        if (string.IsNullOrEmpty(address))
            return null;
        
        var trimmed = address.Trim();
        
        // Remove 0x prefix if present
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];
        
        // If there's a space (e.g., "1234 (TypeName)"), take only the first part
        var spaceIndex = trimmed.IndexOf(' ');
        if (spaceIndex > 0)
            trimmed = trimmed[..spaceIndex];
        
        // Validate it's a valid hex string
        if (string.IsNullOrEmpty(trimmed) || !IsValidHexString(trimmed))
            return null;
        
        return trimmed;
    }
    
    /// <summary>
    /// Checks if a string is a valid hexadecimal number.
    /// </summary>
    internal static bool IsValidHexString(string s)
    {
        foreach (var c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return s.Length > 0;
    }
    
    /// <summary>
    /// Normalizes an address by removing 0x prefix and converting to lowercase.
    /// Used for consistent address comparisons.
    /// </summary>
    internal static string NormalizeAddress(string? address)
    {
        if (string.IsNullOrEmpty(address)) return string.Empty;
        
        // Remove 0x prefix if present
        var normalized = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) 
            ? address[2..] 
            : address;
        
        return normalized.ToLowerInvariant();
    }
    
    /// <summary>
    /// Formats an HResult code with 0x prefix.
    /// </summary>
    internal static string? FormatHResult(string? hresult)
    {
        if (string.IsNullOrEmpty(hresult)) return null;
        
        // Remove any existing 0x prefix and leading zeros, then reformat
        var cleanHResult = hresult.TrimStart('0', 'x', 'X');
        if (string.IsNullOrEmpty(cleanHResult)) cleanHResult = "0";
        
        return $"0x{cleanHResult.PadLeft(8, '0')}";
    }

    /// <summary>
    /// Parses the exception stack trace from pe -nested output.
    /// Format: "    SP               IP               Function"
    ///         "    0000FFFFEFCB9400 0000F7558B725348 Module.dll!Namespace.Class.Method(args)+0x48"
    /// </summary>
    private void ParseExceptionStackTrace(string output, CrashAnalysisResult result)
    {
        // Find the StackTrace section
        var stackTraceStart = output.IndexOf("StackTrace (generated):", StringComparison.OrdinalIgnoreCase);
        if (stackTraceStart == -1)
        {
            // Try alternate format
            stackTraceStart = output.IndexOf("StackTrace:", StringComparison.OrdinalIgnoreCase);
        }
        
        if (stackTraceStart == -1) return;

        // Find the end of the stack trace (next section or end of output)
        var stackTraceEnd = output.IndexOf("\nStackTraceString:", stackTraceStart, StringComparison.OrdinalIgnoreCase);
        if (stackTraceEnd == -1)
        {
            stackTraceEnd = output.IndexOf("\nHResult:", stackTraceStart, StringComparison.OrdinalIgnoreCase);
        }
        if (stackTraceEnd == -1)
        {
            stackTraceEnd = output.Length;
        }

        var stackTraceSection = output.Substring(stackTraceStart, stackTraceEnd - stackTraceStart);
        var lines = stackTraceSection.Split('\n');
        
        // Initialize stack traces in both old and new structures
        var stackFrames = new List<StackFrame>();
        result.Exception ??= new ExceptionDetails();
        result.Exception.StackTrace = stackFrames;
        
        var frameNumber = 0;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            
            // Skip header lines
            if (string.IsNullOrWhiteSpace(trimmedLine) || 
                trimmedLine.StartsWith("StackTrace", StringComparison.OrdinalIgnoreCase) ||
                trimmedLine.StartsWith("SP ", StringComparison.OrdinalIgnoreCase) ||
                trimmedLine == "SP               IP               Function")
            {
                continue;
            }

            // Parse frame: "0000FFFFEFCB9400 0000F7558B725348 Module.dll!Namespace.Class.Method(args)+0x48"
            var frameMatch = Regex.Match(trimmedLine, 
                @"^([0-9a-fA-F]+)\s+([0-9a-fA-F]+)\s+(.+)$");
            
            if (frameMatch.Success)
            {
                var sp = frameMatch.Groups[1].Value;
                var ip = frameMatch.Groups[2].Value;
                var callSite = frameMatch.Groups[3].Value.Trim();

                // Parse module!method+offset format
                string module = "";
                string function = callSite;

                var bangIndex = callSite.IndexOf('!');
                if (bangIndex > 0)
                {
                    module = callSite.Substring(0, bangIndex);
                    function = callSite.Substring(bangIndex + 1);
                    
                    // Remove +offset from function name for cleaner display
                    var plusIndex = function.LastIndexOf('+');
                    if (plusIndex > 0 && Regex.IsMatch(function.Substring(plusIndex + 1), @"^0x[0-9a-fA-F]+$"))
                    {
                        function = function.Substring(0, plusIndex);
                    }
                }

                stackFrames.Add(new StackFrame
                {
                    FrameNumber = frameNumber++,
                    StackPointer = $"0x{sp}",
                    InstructionPointer = $"0x{ip}",
                    Module = module,
                    Function = function,
                    IsManaged = true
                });
            }
        }
    }
    
    /// <summary>
    /// Enriches exception stack frames with complete info from matching thread stack frames.
    /// The pe -nested output only has basic info (SP, IP, function), but clrstack -a has everything.
    /// We match frames by function name and copy all additional info (source, parameters, locals, etc).
    /// </summary>
    private void EnrichExceptionStackWithSourceInfo(CrashAnalysisResult result)
    {
        // Use new structure, fall back to old if not available
        var exceptionStackTrace = result.Exception?.StackTrace;
        if (exceptionStackTrace == null || exceptionStackTrace.Count == 0)
            return;
        
        // Build a lookup dictionary from all thread stack frames
        // Key: function name (normalized), Value: frame with full info
        var frameLookup = new Dictionary<string, StackFrame>(StringComparer.OrdinalIgnoreCase);
        
        var threads = result.Threads?.All;
        if (threads == null)
        {
            return;
        }

        foreach (var thread in threads)
        {
            if (thread.CallStack == null) continue;
            
            foreach (var frame in thread.CallStack)
            {
                if (string.IsNullOrEmpty(frame.Function))
                    continue;
                
                // Normalize function name by removing parameter types for better matching
                var normalizedFunc = NormalizeFunctionName(frame.Function);
                if (!frameLookup.ContainsKey(normalizedFunc))
                {
                    frameLookup[normalizedFunc] = frame;
                }
            }
        }
        
        // Now enrich exception stack frames with all available info
        foreach (var exFrame in exceptionStackTrace)
        {
            if (string.IsNullOrEmpty(exFrame.Function))
                continue;
            
            var normalizedFunc = NormalizeFunctionName(exFrame.Function);
            
            if (frameLookup.TryGetValue(normalizedFunc, out var matchingFrame))
            {
                // Copy source info
                exFrame.SourceFile = matchingFrame.SourceFile;
                exFrame.LineNumber = matchingFrame.LineNumber;
                exFrame.SourceUrl = matchingFrame.SourceUrl;
                exFrame.SourceProvider = matchingFrame.SourceProvider;
                
                // Copy parameters and locals
                exFrame.Parameters = matchingFrame.Parameters;
                exFrame.Locals = matchingFrame.Locals;
                
                // Copy register info
                exFrame.Registers = matchingFrame.Registers;
            }
        }
    }
    
    /// <summary>
    /// Normalizes a function name for matching by removing parameter type info.
    /// "Namespace.Class.Method(System.String)" -> "Namespace.Class.Method"
    /// </summary>
    private static string NormalizeFunctionName(string function)
    {
        if (string.IsNullOrEmpty(function))
            return function;
        
        // Find the opening parenthesis and truncate
        var parenIndex = function.IndexOf('(');
        if (parenIndex > 0)
        {
            return function.Substring(0, parenIndex);
        }
        
        return function;
    }
    
    /// <summary>
    /// Normalizes location format to use square brackets instead of angle brackets.
    /// Converts "&lt;CLR reg&gt;" to "[CLR reg]" for cleaner JSON output.
    /// </summary>
    private static string NormalizeLocation(string location)
    {
        if (string.IsNullOrEmpty(location))
            return location;
        
        // If already has angle brackets, convert to square brackets
        if (location.StartsWith('<') && location.EndsWith('>'))
        {
            return $"[{location.Substring(1, location.Length - 2)}]";
        }
        
        return location;
    }
    
    /// <summary>
    /// Normalizes type name format for cleaner output.
    /// Converts "Type ByRef" to "Type(ByRef)" to show ByRef as a modifier.
    /// </summary>
    private static string NormalizeTypeName(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return typeName ?? string.Empty;
        
        // Handle "Type ByRef" -> "Type(ByRef)"
        if (typeName.EndsWith(" ByRef", StringComparison.Ordinal))
        {
            return $"{typeName.Substring(0, typeName.Length - 6)}(ByRef)";
        }
        
        // Handle "Type&" -> "Type(ByRef)" (alternative syntax for ByRef)
        if (typeName.EndsWith('&'))
        {
            return $"{typeName.Substring(0, typeName.Length - 1)}(ByRef)";
        }
        
        return typeName;
    }

    /// <summary>
    /// Parses heap statistics.
    /// </summary>
    protected void ParseHeapStats(string output, CrashAnalysisResult result)
    {
        // For native dumps, !dumpheap -stat will fail - gracefully skip
        if (IsSosErrorOutput(output))
        {
            return;
        }

        var heapStats = new Dictionary<string, long>();

        // Parse heap stats table
        var lines = output.Split('\n');
        foreach (var line in lines)
        {
            // Look for lines with MT (MethodTable), Count, TotalSize, Class Name
            // Example: "00007ff8a1234567    12345    1234567 System.String"
            var match = Regex.Match(line, @"([0-9a-f]+)\s+(\d+)\s+(\d+)\s+(.+)");
            if (match.Success)
            {
                var className = match.Groups[4].Value.Trim();
                var totalSize = long.Parse(match.Groups[3].Value);

                if (!heapStats.ContainsKey(className))
                {
                    heapStats[className] = totalSize;
                }
            }
        }

        // Set in new structure
        result.Memory ??= new MemoryInfo();
        result.Memory.HeapStats = heapStats;
        
        // Also set in old structure during migration

        // Add recommendation if heap is large
        var totalHeapSize = heapStats.Values.Sum();
        if (totalHeapSize > 1_000_000_000) // > 1GB
        {
            var rec = $"Large heap detected ({totalHeapSize:N0} bytes). Consider investigating memory usage.";
            result.Summary?.Recommendations?.Add(rec);
        }
    }

    /// <summary>
    /// Detects async/await deadlocks by looking for specific blocking patterns.
    /// </summary>
    protected void DetectAsyncDeadlock(string output, CrashAnalysisResult result)
    {
        // Look for specific async deadlock patterns - must be explicit method calls, not just words
        // Patterns that indicate blocking on async:
        // - Task.Wait(), Task.Result (property access)
        // - .GetAwaiter().GetResult()
        // - WaitAll, WaitAny
        // - AsyncHelpers.RunSync
        
        var deadlockIndicators = new[]
        {
            @"\.Wait\(\)",                          // Task.Wait()
            @"\.get_Result\(",                      // Task.Result property getter
            @"\.GetAwaiter\(\)\.GetResult\(\)",     // GetAwaiter().GetResult()
            @"WaitAll\(",                           // Task.WaitAll()
            @"WaitAny\(",                           // Task.WaitAny()
            @"RunSync",                             // Common async-over-sync helper
            @"SynchronizationContext.*Wait",        // Blocking on SynchronizationContext
            @"ManualResetEvent.*WaitOne",           // Blocking wait
            @"AutoResetEvent.*WaitOne"              // Blocking wait
        };
        
        var indicatorCount = 0;
        var foundPatterns = new List<string>();
        
        foreach (var pattern in deadlockIndicators)
        {
            var matches = Regex.Matches(output, pattern, RegexOptions.IgnoreCase);
            if (matches.Count > 0)
            {
                indicatorCount += matches.Count;
                foundPatterns.Add(pattern);
            }
        }
        
        // Also check for multiple threads blocked on async continuations
        var blockedOnAsyncCount = Regex.Matches(output, @"System\.Threading\.Tasks.*Continuation", RegexOptions.IgnoreCase).Count;
        
        // Only flag as async deadlock if we have strong evidence:
        // - Multiple blocking calls OR
        // - Multiple threads blocked on continuations
        if (indicatorCount >= 2 || blockedOnAsyncCount >= 3)
        {
            // Set in new structure
            result.Async ??= new AsyncInfo();
            result.Async.HasDeadlock = true;
            
            // Also set in old structure during migration
            
            result.Summary!.CrashType = ".NET Async Deadlock";
            var rec = "Possible async/await deadlock detected. Avoid .Wait() and .Result on async methods. Use await instead.";
            result.Summary?.Recommendations?.Add(rec);
        }
    }

    /// <summary>
    /// Parses CLR thread information from !clrthreads output.
    /// Extracts thread statistics and enriches ThreadInfo with CLR-specific data.
    /// </summary>
    protected void ParseClrThreads(string output, CrashAnalysisResult result)
    {
        // For native dumps, !clrthreads will fail - gracefully skip
        if (IsSosErrorOutput(output) || !output.Contains("ThreadCount", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Ensure Threads.Summary is initialized
        result.Threads ??= new ThreadsInfo { All = new List<ThreadInfo>() };
        result.Threads.Summary ??= new ThreadSummary();

        // Parse thread statistics header
        // ThreadCount:      13
        // UnstartedThread:  0
        // BackgroundThread: 10
        // PendingThread:    0
        // DeadThread:       2
        // Hosted Runtime:   no
        var threadCountMatch = Regex.Match(output, @"ThreadCount:\s*(\d+)");
        if (threadCountMatch.Success)
        {
            var count = int.Parse(threadCountMatch.Groups[1].Value);
            result.Threads.Summary.Total = count;
        }

        var unstartedMatch = Regex.Match(output, @"UnstartedThread:\s*(\d+)");
        if (unstartedMatch.Success)
        {
            var count = int.Parse(unstartedMatch.Groups[1].Value);
            result.Threads.Summary.Unstarted = count;
        }

        var backgroundMatch = Regex.Match(output, @"BackgroundThread:\s*(\d+)");
        if (backgroundMatch.Success)
        {
            var count = int.Parse(backgroundMatch.Groups[1].Value);
            result.Threads.Summary.Background = count;
        }

        var pendingMatch = Regex.Match(output, @"PendingThread:\s*(\d+)");
        if (pendingMatch.Success)
        {
            var count = int.Parse(pendingMatch.Groups[1].Value);
            result.Threads.Summary.Pending = count;
        }

        var deadMatch = Regex.Match(output, @"DeadThread:\s*(\d+)");
        if (deadMatch.Success)
        {
            var count = int.Parse(deadMatch.Groups[1].Value);
            result.Threads.Summary.Dead = count;
        }

        // Derive foreground threads so the breakdown matches ThreadCount.
        // In SOS output, ThreadCount includes both foreground and background threads.
        // Some dumps will otherwise show Total != (Background + Unstarted + Pending + Dead).
        result.Threads.Summary.Foreground = Math.Max(
            0,
            result.Threads.Summary.Total
            - result.Threads.Summary.Background
            - result.Threads.Summary.Unstarted
            - result.Threads.Summary.Pending
            - result.Threads.Summary.Dead);

        var hostedMatch = Regex.Match(output, @"Hosted Runtime:\s*(yes|no)", RegexOptions.IgnoreCase);
        if (hostedMatch.Success)
        {
            var isHosted = hostedMatch.Groups[1].Value.Equals("yes", StringComparison.OrdinalIgnoreCase);
            result.Environment ??= new EnvironmentInfo();
            result.Environment.Runtime ??= new RuntimeInfo { Type = "CoreCLR" };
            result.Environment.Runtime.IsHosted = isHosted;
        }

        // Parse individual thread lines by splitting and processing each line
        // Format: DBG   ID     OSID ThreadOBJ           State GC Mode     GC Alloc Context                  Domain           Count Apt Exception
        //         1    1     8954 0000F714F13A4010    20020 Preemptive  0000F7158EE6AA88:0000F7158EE6C1F8 0000F7559002B110 -00001 Ukn System.MissingMethodException
        var lines = output.Split('\n');
        foreach (var line in lines)
        {
            // Skip empty lines and header lines
            var trimmedLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine) || 
                trimmedLine.StartsWith("ThreadCount") ||
                trimmedLine.StartsWith("Unstarted") ||
                trimmedLine.StartsWith("Background") ||
                trimmedLine.StartsWith("Pending") ||
                trimmedLine.StartsWith("Dead") ||
                trimmedLine.StartsWith("Hosted") ||
                trimmedLine.StartsWith("DBG") ||
                trimmedLine.StartsWith("Lock") ||
                trimmedLine.StartsWith("---"))
            {
                continue;
            }

            // Parse thread line: split by whitespace and extract fields
            // We need at least: DBG, ID, OSID, ThreadOBJ, State, GCMode, AllocContext, Domain, Count, Apt
            var parts = trimmedLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 10)
            {
                continue;
            }

            // Extract basic fields
            var dbgId = parts[0];
            if (!int.TryParse(parts[1], out var managedId))
            {
                continue;
            }
            var osId = parts[2];
            var threadObj = parts[3];
            var state = parts[4];
            var gcMode = parts[5];
            
            // Find lock count and apt - they're near the end
            // The GC Alloc Context might have a colon, Domain is next hex, then count, then apt
            int lockCount = 0;
            string apt = "Ukn";
            string extra = "";

            // Search for lock count (negative number starting with -)
            for (int i = 6; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("-") && int.TryParse(parts[i], out lockCount))
                {
                    // Next field is Apt
                    if (i + 1 < parts.Length)
                    {
                        apt = parts[i + 1];
                    }
                    // Everything after is extra (exception, thread type)
                    if (i + 2 < parts.Length)
                    {
                        extra = string.Join(" ", parts.Skip(i + 2));
                    }
                    break;
                }
            }

            var isDead = dbgId == "XXXX";

            // Parse thread type and exception from extra info
            string? threadType = null;
            string? currentException = null;

            if (extra.Contains("(Finalizer)"))
                threadType = "Finalizer";
            else if (extra.Contains("(Threadpool Worker)"))
                threadType = "Threadpool Worker";
            else if (extra.Contains("(Threadpool Completion)"))
                threadType = "Threadpool Completion";
            else if (extra.Contains("(GC)"))
                threadType = "GC";

            // Exception format: "System.ExceptionType 0xaddress" or "System.ExceptionType address" (without 0x)
            var exceptionMatch = Regex.Match(extra, @"(System\.\w+(?:\.\w+)*(?:Exception)?)\s+(0x)?([0-9a-f]+)", RegexOptions.IgnoreCase);
            if (exceptionMatch.Success)
            {
                var exceptionType = exceptionMatch.Groups[1].Value;
                var address = exceptionMatch.Groups[3].Value;
                currentException = $"{exceptionType} @ 0x{address}";
            }

            // Find matching thread in result.Threads!.All by OS thread ID
            // Threads from clrstack output have ThreadId like "0x8954"
            // Skip dead threads (OSID = 0) as they don't have an OS thread
            ThreadInfo? matchingThread = null;
            var osIdDecimalString = (string?)null;
            
            if (osId != "0")
            {
                var osIdDecimal = Convert.ToInt32(osId, 16);
                osIdDecimalString = osIdDecimal.ToString();
                var osIdHex = $"0x{osId}";
                
                var threads = result.Threads?.All;
                matchingThread = threads?.FirstOrDefault(t =>
                    // Direct match: ThreadId == "0x8954"
                    t.ThreadId.Equals(osIdHex, StringComparison.OrdinalIgnoreCase) ||
                    // Contains hex: "Thread 0x8954" or "tid: 0x8954"  
                    t.ThreadId.Contains(osIdHex, StringComparison.OrdinalIgnoreCase) ||
                    // Contains decimal: "tid: 35156" or "(35156)"
                    t.ThreadId.Contains($"tid: {osIdDecimal}", StringComparison.OrdinalIgnoreCase) ||
                    t.ThreadId.Contains($"({osIdDecimal})", StringComparison.OrdinalIgnoreCase) ||
                    // Already has OsThreadId set (from a previous parse)
                    (t.OsThreadId != null && t.OsThreadId.Equals(osId, StringComparison.OrdinalIgnoreCase)) ||
                    (t.OsThreadIdDecimal != null && t.OsThreadIdDecimal.Equals(osIdDecimalString, StringComparison.OrdinalIgnoreCase)));
            }

            if (matchingThread != null)
            {
                // Enrich with CLR thread info
                matchingThread.ManagedThreadId = managedId;
                matchingThread.OsThreadId = osId;
                matchingThread.OsThreadIdDecimal = osIdDecimalString;
                matchingThread.ThreadObject = $"0x{threadObj}";
                matchingThread.ClrThreadState = $"0x{state}";
                matchingThread.GcMode = gcMode;
                matchingThread.LockCount = lockCount;
                matchingThread.ApartmentState = apt;
                matchingThread.ThreadType = threadType;
                matchingThread.CurrentException = currentException;
                matchingThread.IsDead = isDead;
                matchingThread.IsThreadpool = threadType?.Contains("Threadpool") == true;
                
                // CLR thread state is a bitmask - check the TS_Background flag (0x1000, bit 12)
                // State examples: 0x20020 (foreground), 0x21220 (finalizer/background), 0x1021220 (threadpool/background)
                if (long.TryParse(state, System.Globalization.NumberStyles.HexNumber, null, out var stateValue))
                {
                    const long TS_Background = 0x1000; // Background thread flag (bit 12)
                    matchingThread.IsBackground = (stateValue & TS_Background) != 0;
                }
            }
        }

        // Add recommendations based on thread stats
        var deadThreadCount = result.Threads?.Summary?.Dead;
        if (deadThreadCount > 0)
        {
            result.Summary ??= new AnalysisSummary();
            result.Summary.Recommendations ??= [];
            var rec = $"CLR reports {deadThreadCount} dead managed thread(s). This is often benign (threads already terminated); investigate only if correlated with hangs or resource starvation.";
            result.Summary.Recommendations.Add(rec);
        }
    }

    /// <summary>
    /// Parses finalizer queue information.
    /// </summary>
    protected void ParseFinalizerQueue(string output, CrashAnalysisResult result)
    {
        // For native dumps, !finalizequeue will fail - gracefully skip
        if (IsSosErrorOutput(output))
        {
            return;
        }

        // Look for "generation 0 has X finalizable objects"
        var match = Regex.Match(output, @"generation \d+ has (\d+) finalizable objects");
        if (match.Success)
        {
            var finalizerCount = int.Parse(match.Groups[1].Value);
            
            // Set in new structure
            result.Threads ??= new ThreadsInfo { All = new List<ThreadInfo>() };
            result.Threads.Summary ??= new ThreadSummary();
            result.Threads.Summary.FinalizerQueueLength = finalizerCount;
            
            // Also set in old structure during migration

            // Add recommendation if finalizer queue is large
            if (finalizerCount > 1000)
            {
                var rec = $"Large finalizer queue detected ({finalizerCount} objects). This may indicate finalizer issues or memory pressure.";
                result.Summary?.Recommendations?.Add(rec);
            }
        }
    }
    
    /// <summary>
    /// Parses thread pool information from !threadpool output.
    /// </summary>
    protected void ParseThreadPool(string output, CrashAnalysisResult result)
    {
        if (IsSosErrorOutput(output))
            return;
        
        var threadPool = new ThreadPoolInfo();
        
        // Check for portable thread pool
        if (output.Contains("Portable thread pool", StringComparison.OrdinalIgnoreCase))
        {
            threadPool.IsPortableThreadPool = true;
        }
        
        // Parse CPU utilization: "CPU utilization:  31%"
        var cpuMatch = Regex.Match(output, @"CPU utilization:\s*(\d+)%", RegexOptions.IgnoreCase);
        if (cpuMatch.Success)
        {
            threadPool.CpuUtilization = int.Parse(cpuMatch.Groups[1].Value);
        }
        
        // Parse Workers Total: "Workers Total:    3"
        var totalMatch = Regex.Match(output, @"Workers Total:\s*(\d+)", RegexOptions.IgnoreCase);
        if (totalMatch.Success)
        {
            threadPool.WorkersTotal = int.Parse(totalMatch.Groups[1].Value);
        }
        
        // Parse Workers Running: "Workers Running:  0"
        var runningMatch = Regex.Match(output, @"Workers Running:\s*(\d+)", RegexOptions.IgnoreCase);
        if (runningMatch.Success)
        {
            threadPool.WorkersRunning = int.Parse(runningMatch.Groups[1].Value);
        }
        
        // Parse Workers Idle: "Workers Idle:     3"
        var idleMatch = Regex.Match(output, @"Workers Idle:\s*(\d+)", RegexOptions.IgnoreCase);
        if (idleMatch.Success)
        {
            threadPool.WorkersIdle = int.Parse(idleMatch.Groups[1].Value);
        }
        
        // Parse Worker Min Limit: "Worker Min Limit: 4"
        var minMatch = Regex.Match(output, @"Worker Min Limit:\s*(\d+)", RegexOptions.IgnoreCase);
        if (minMatch.Success)
        {
            threadPool.WorkerMinLimit = int.Parse(minMatch.Groups[1].Value);
        }
        
        // Parse Worker Max Limit: "Worker Max Limit: 32767"
        var maxMatch = Regex.Match(output, @"Worker Max Limit:\s*(\d+)", RegexOptions.IgnoreCase);
        if (maxMatch.Success)
        {
            threadPool.WorkerMaxLimit = int.Parse(maxMatch.Groups[1].Value);
        }
        
        // Set in new structure
        result.Threads ??= new ThreadsInfo { All = new List<ThreadInfo>() };
        result.Threads.ThreadPool = threadPool;
        
        // Also set in old structure during migration
        
        // Add recommendations based on thread pool state
        if (threadPool.WorkersRunning == threadPool.WorkersTotal && threadPool.WorkersTotal > 0)
        {
            var rec = $"All {threadPool.WorkersTotal} thread pool workers are running. This may indicate thread pool saturation.";
            result.Summary?.Recommendations?.Add(rec);
        }
        
        if (threadPool.CpuUtilization.HasValue && threadPool.CpuUtilization > 90)
        {
            var rec = $"High CPU utilization ({threadPool.CpuUtilization}%). Consider profiling for CPU-bound operations.";
            result.Summary?.Recommendations?.Add(rec);
        }
    }
    
    /// <summary>
    /// Parses timer information from !ti output.
    /// </summary>
    protected void ParseTimerInfo(string output, CrashAnalysisResult result)
    {
        if (IsSosErrorOutput(output))
            return;
        
        var timers = new List<TimerInfo>();
        
        // Parse timer lines - format:
        // (L) 0x0000F7158EDFD1D0 @    3999 ms every     4000 ms |  0000F7158EDFCE20 (TypeName) -> CallbackName
        // (L) 0x0000F7158ED7CD88 @    2000 ms every   ------ ms |  0000F7158ED7CD40 (TypeName) -> CallbackName
        var timerRegex = new Regex(
            @"\(L\)\s+0x([0-9a-fA-F]+)\s+@\s+(\d+)\s+ms\s+every\s+([\d\-]+)\s+ms\s+\|\s+([0-9a-fA-F]+)\s+\(([^)]+)\)\s*(?:->\s*(.*))?",
            RegexOptions.IgnoreCase);
        
        foreach (var line in output.Split('\n'))
        {
            var match = timerRegex.Match(line);
            if (match.Success)
            {
                var timer = new TimerInfo
                {
                    Address = $"0x{match.Groups[1].Value}",
                    DueTimeMs = int.Parse(match.Groups[2].Value),
                    StateAddress = $"0x{match.Groups[4].Value}",
                    StateType = match.Groups[5].Value.Trim(),
                    Callback = string.IsNullOrWhiteSpace(match.Groups[6].Value) ? null : match.Groups[6].Value.Trim()
                };
                
                // Parse period (------ means one-shot timer)
                var periodStr = match.Groups[3].Value.Trim();
                if (periodStr != "------" && int.TryParse(periodStr, out var period))
                {
                    timer.PeriodMs = period;
                }
                
                // Inspect the state object using ClrMD if available
                if (_clrMdAnalyzer?.IsOpen == true && !string.IsNullOrEmpty(timer.StateAddress))
                {
                    try
                    {
                        var cleanAddress = timer.StateAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            ? timer.StateAddress[2..]
                            : timer.StateAddress;
                        
                        if (ulong.TryParse(cleanAddress, System.Globalization.NumberStyles.HexNumber, null, out var stateAddr))
                        {
                            // Use shallow inspection (depth=2) to avoid excessive nesting
                            timer.StateValue = _clrMdAnalyzer.InspectObject(stateAddr, maxDepth: 2, maxArrayElements: 5, maxStringLength: 256);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Failed to inspect timer state at {Address}", timer.StateAddress);
                    }
                }
                
                timers.Add(timer);
            }
        }
        
        // Also parse the timer count if available: "   11 timers"
        var countMatch = Regex.Match(output, @"(\d+)\s+timers", RegexOptions.IgnoreCase);
        
        if (timers.Count > 0)
        {
            // Set in new structure
            result.Async ??= new AsyncInfo();
            result.Async.Timers = timers;
            
            // Also set in old structure during migration
        }
        
        // Add recommendations based on timer info
        if (timers.Count > 50)
        {
            result.Summary ??= new AnalysisSummary();
            result.Summary.Recommendations ??= [];

            var isTestHost = result.Environment?.Process?.Arguments?.Any(a =>
                a.Contains("testhost.dll", StringComparison.OrdinalIgnoreCase) ||
                a.Contains("vstest", StringComparison.OrdinalIgnoreCase)) == true;

            // Summarize top timer owners by state type (best-effort).
            var topOwners = timers
                .Where(t => !string.IsNullOrWhiteSpace(t.StateType))
                .GroupBy(t => t.StateType!, StringComparer.Ordinal)
                .Select(g => (Type: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count)
                .Take(3)
                .Select(x => $"{x.Type} ({x.Count})")
                .ToList();

            var ownersSuffix = topOwners.Count > 0 ? $" Top timer state types: {string.Join(", ", topOwners)}." : string.Empty;
            var context = isTestHost
                ? " In testhost/CI contexts this can be normal, but if it grows over time or appears in production investigate undisposed timers."
                : " This can indicate timer leaks or inefficient timer usage; investigate undisposed timers.";

            var rec = $"High number of active timers ({timers.Count}).{ownersSuffix}{context}";
            result.Summary.Recommendations.Add(rec);
        }
        
        // Check for very short interval timers
        var shortTimers = timers.Where(t => t.PeriodMs.HasValue && t.PeriodMs < 100).ToList();
        if (shortTimers.Count > 0)
        {
            var rec = $"Found {shortTimers.Count} timer(s) with very short intervals (<100ms). Consider consolidating or using longer intervals.";
            result.Summary?.Recommendations?.Add(rec);
        }
    }

    /// <summary>
    /// Analyzes .NET heap for memory leaks.
    /// </summary>
    /// <param name="heapStatsOutput">The !dumpheap -stat output.</param>
    /// <param name="result">The result to populate.</param>
    protected void AnalyzeDotNetMemoryLeaks(string heapStatsOutput, CrashAnalysisResult result)
    {
        // Initialize if not already done by base class
        result.Memory ??= new MemoryInfo();
        result.Memory.LeakAnalysis ??= new LeakAnalysis();
        result.Memory.LeakAnalysis.TopConsumers ??= new List<MemoryConsumer>();
        result.Memory.LeakAnalysis.PotentialIssueIndicators ??= new List<string>();

        // Parse heap stats table - more accurate than base class native heap analysis
        // Format: "MT    Count    TotalSize Class Name"
        // Example: "00007ff8a1234567    12345    1234567 System.String"
        // Note: LLDB/SOS may format large numbers with commas: "11,193" "1,011,728"
        var lines = heapStatsOutput.Split('\n');
        var typeStats = new List<(string TypeName, long Count, long TotalSize)>();

        foreach (var line in lines)
        {
            // Use [\d,]+ to match numbers with or without commas
            var match = Regex.Match(line, @"([0-9a-f]{8,16})\s+([\d,]+)\s+([\d,]+)\s+(.+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                // Remove commas before parsing
                var count = long.Parse(match.Groups[2].Value.Replace(",", ""));
                var totalSize = long.Parse(match.Groups[3].Value.Replace(",", ""));
                var className = match.Groups[4].Value.Trim();

                typeStats.Add((className, count, totalSize));
            }
        }

        // Sort by total size descending and take top consumers
        var topConsumers = typeStats
            .OrderByDescending(t => t.TotalSize)
            .Take(15)
            .ToList();

        result.Memory!.LeakAnalysis.TopConsumers.Clear();
        foreach (var (typeName, count, totalSize) in topConsumers)
        {
            result.Memory!.LeakAnalysis.TopConsumers.Add(new MemoryConsumer
            {
                TypeName = typeName,
                Count = count,
                TotalSize = totalSize
            });
        }

        // Calculate total managed heap size
        var totalHeapSize = typeStats.Sum(t => t.TotalSize);
        result.Memory!.LeakAnalysis.TotalHeapBytes = totalHeapSize;

        // Determine severity based on heap size
        // Note: These are guidelines - actual thresholds depend on application type
        if (totalHeapSize > 2_000_000_000) // > 2GB
        {
            result.Memory!.LeakAnalysis.Severity = "High";
            result.Memory!.LeakAnalysis.Detected = true;
        }
        else if (totalHeapSize > 500_000_000) // > 500MB
        {
            result.Memory!.LeakAnalysis.Severity = "Elevated";
        }
        else
        {
            result.Memory!.LeakAnalysis.Severity = "Normal";
        }

        // Check for ACTUAL potential leak indicators - these are things that genuinely suggest issues
        // NOT just common types like String/Byte[] which are normal
        
        // 1. Event handlers - these are a common source of leaks when not unsubscribed
        var eventHandlerAllocations = topConsumers
            .Where(t => t.TypeName.Contains("EventHandler") || t.TypeName.Contains("Delegate"))
            .Where(t => t.Count > 1000)
            .ToList();

        if (eventHandlerAllocations.Any())
        {
            result.Memory!.LeakAnalysis.PotentialIssueIndicators.Add("High EventHandler/Delegate count - check for unsubscribed event handlers");
            foreach (var (typeName, count, totalSize) in eventHandlerAllocations)
            {
                var rec = $"Review event subscriptions: {typeName} has {count:N0} instances. Unsubscribed handlers can cause leaks.";
                result.Summary?.Recommendations?.Add(rec);
            }
        }

        // 2. WeakReference - if many, might indicate workarounds for leak issues
        var weakRefAllocations = topConsumers
            .Where(t => t.TypeName.Contains("WeakReference"))
            .Where(t => t.Count > AnalysisConstants.HighWeakReferenceCountThreshold)
            .ToList();

        if (weakRefAllocations.Any())
        {
            result.Memory!.LeakAnalysis.PotentialIssueIndicators.Add("High WeakReference count - may indicate leak mitigation patterns");
        }

        // 3. Timer-related types - timers can hold references
        var timerAllocations = topConsumers
            .Where(t => t.TypeName.Contains("Timer") && !t.TypeName.Contains("TimerCallback"))
            .Where(t => t.Count > 100)
            .ToList();

        if (timerAllocations.Any())
        {
            result.Memory!.LeakAnalysis.PotentialIssueIndicators.Add("Multiple Timer instances - ensure timers are properly disposed");
            var rec = "Review timer usage: undisposed timers can prevent garbage collection.";
            result.Summary?.Recommendations?.Add(rec);
        }

        // 4. Pinned objects - can fragment the heap
        var pinnedAllocations = topConsumers
            .Where(t => t.TypeName.Contains("Pinned"))
            .ToList();

        if (pinnedAllocations.Any())
        {
            result.Memory!.LeakAnalysis.PotentialIssueIndicators.Add("Pinned objects detected - can cause heap fragmentation");
        }

        // 5. Check for large LOH allocations (objects > 85KB go to LOH)
        const int lohThreshold = 85_000;
        var largeSingleAllocations = topConsumers
            .Where(t => t.Count > 0 && t.TotalSize / t.Count > lohThreshold)
            .ToList();

        if (largeSingleAllocations.Any())
        {
            result.Memory!.LeakAnalysis.PotentialIssueIndicators.Add("Large Object Heap allocations present");
            result.Summary ??= new AnalysisSummary();
            result.Summary.Recommendations ??= [];

            var lohBytes = result.Memory?.Gc?.GenerationSizes?.Loh;
            var totalHeap = result.Memory?.Gc?.TotalHeapSize;
            var lohHint = lohBytes.HasValue && totalHeap.HasValue && totalHeap.Value > 0
                ? $" LOH is {lohBytes.Value:N0} bytes (~{(double)lohBytes.Value / totalHeap.Value:P0} of managed heap)."
                : string.Empty;

            var lohRecommendation = $"Large Object Heap allocations detected.{lohHint} Consider ArrayPool<T> for large arrays and avoid frequent >85KB allocations to reduce GC pressure.";
            result.Summary.Recommendations.Add(lohRecommendation);
        }

        // 6. Very large managed heap - this is high consumption, NOT necessarily a leak
        if (totalHeapSize > 2_000_000_000) // > 2GB
        {
            var heapRecommendation = $"High managed heap usage ({totalHeapSize:N0} bytes). This is NOT necessarily a leak - use memory profiling with multiple snapshots to identify actual leaks.";
            result.Summary?.Recommendations?.Add(heapRecommendation);
        }

        // Only set Detected=true for genuine leak indicators, not just high heap usage
        if (result.Memory!.LeakAnalysis.PotentialIssueIndicators.Count > 0)
        {
            result.Memory!.LeakAnalysis.Detected = true;
        }
    }

    /// <summary>
    /// Analyzes for .NET-specific deadlocks using !syncblk.
    /// </summary>
    /// <param name="result">The result to populate.</param>
    protected async Task AnalyzeDotNetDeadlocksAsync(CrashAnalysisResult result)
    {
        // Initialize if not already done by base class
        result.Threads ??= new ThreadsInfo { All = new List<ThreadInfo>(), Summary = new ThreadSummary() };
        result.Threads!.Deadlock ??= new DeadlockInfo();

        // Get sync block information (monitors/locks)
        var syncBlkOutput = await ExecuteCommandAsync("!syncblk");
        result.RawCommands!["!syncblk"] = syncBlkOutput;

        // Parse sync blocks
        // Format: "Index SyncBlock MonitorHeld Recursion Owning Thread Info  SyncBlock Owner"
        // Example: "   12 0000024453f8a5a8    1         1 0000024453f46720  1c54  10   00000244540a5820 System.Object"
        var syncBlkMatches = Regex.Matches(syncBlkOutput, 
            @"(\d+)\s+([0-9a-f]+)\s+(\d+)\s+(\d+)\s+([0-9a-f]+)\s+([0-9a-f]+)\s+(\d+)\s+([0-9a-f]+)\s+(.+)",
            RegexOptions.IgnoreCase);

        var heldLocks = new Dictionary<string, (string Owner, string ObjectType, string SyncBlock)>();
        
        foreach (Match match in syncBlkMatches)
        {
            var monitorHeld = int.Parse(match.Groups[3].Value);
            if (monitorHeld > 0)
            {
                var syncBlock = match.Groups[2].Value;
                var ownerThread = match.Groups[6].Value; // Thread ID
                var objectAddress = match.Groups[8].Value;
                var objectType = match.Groups[9].Value.Trim();

                heldLocks[syncBlock] = (ownerThread, objectType, syncBlock);

                result.Threads!.Deadlock.Locks.Add(new LockInfo
                {
                    Address = syncBlock,
                    Owner = ownerThread,
                    Waiters = new List<string>()
                });
            }
        }

        // Look for threads waiting to enter monitors
        // Check !threads output for "Lock" or "Wait" state
        var threadsOutput = result.RawCommands?.GetValueOrDefault("!clrthreads", "") ?? "";
        var waitingThreadMatches = Regex.Matches(threadsOutput, 
            @"(\d+)\s+(\d+)\s+([0-9a-f]+).*?(Wait|Lock|Preemptive)",
            RegexOptions.IgnoreCase);

        var waitingThreads = new List<string>();
        foreach (Match match in waitingThreadMatches)
        {
            var state = match.Groups[4].Value;
            if (state.Equals("Wait", StringComparison.OrdinalIgnoreCase) || 
                state.Equals("Lock", StringComparison.OrdinalIgnoreCase))
            {
                var threadId = match.Groups[1].Value;
                waitingThreads.Add(threadId);
            }
        }

        // Cross-reference: if threads are waiting and other threads hold locks, potential deadlock
        if (heldLocks.Count > 0 && waitingThreads.Count > 0)
        {
            // Check for classic deadlock pattern: T1 holds L1 waiting for L2, T2 holds L2 waiting for L1
            var lockHolders = heldLocks.Values.Select(v => v.Owner).Distinct().ToList();
            var waitersNotHolding = waitingThreads.Where(w => !lockHolders.Contains(w)).ToList();
            var holdersWaiting = lockHolders.Intersect(waitingThreads).ToList();

            if (holdersWaiting.Count >= 2)
            {
                result.Threads!.Deadlock.Detected = true;
                result.Threads!.Deadlock.InvolvedThreads.AddRange(holdersWaiting);
                result.Summary!.CrashType = ".NET Monitor Deadlock";
                var rec1 = $".NET deadlock detected: {holdersWaiting.Count} threads holding locks while waiting for other locks.";
                var rec2 = "Use consistent lock ordering or consider using System.Threading.Lock (.NET 9+) or SemaphoreSlim.";
                result.Summary?.Recommendations?.Add(rec1);
                result.Summary?.Recommendations?.Add(rec2);
            }
            else if (holdersWaiting.Count == 1)
            {
                var rec = "Thread holding a lock is also waiting. Check for potential deadlock with async/await or external resources.";
                result.Summary?.Recommendations?.Add(rec);
            }
        }

        // Note: !rwlock command is not available in SOS - removed as it was causing errors
    }

    /// <summary>
    /// Updates the summary with .NET specific information.
    /// </summary>
    protected void UpdateDotNetSummary(CrashAnalysisResult result)
    {
        // Ensure Summary is initialized
        result.Summary ??= new AnalysisSummary();
        result.Summary.Description ??= "";

        // Write to new summary structure only
        result.Summary.Description += " .NET Analysis: ";

        // Use new hierarchical structure
        var clrVersion = result.Environment?.Runtime?.ClrVersion;
        if (!string.IsNullOrEmpty(clrVersion))
        {
            result.Summary.Description += $"CLR {clrVersion}. ";
        }

        var exceptionType = result.Exception?.Type;
        if (!string.IsNullOrEmpty(exceptionType))
        {
            result.Summary.Description += $"Managed Exception: {exceptionType}. ";
        }

        if (result.Async?.HasDeadlock == true)
        {
            result.Summary.Description += "Async deadlock detected. ";
        }

        var heapStats = result.Memory?.HeapStats;
        if (heapStats != null && heapStats.Any())
        {
            result.Summary.Description += $"Heap has {heapStats.Count} types. ";
        }

        var finalizerCount = result.Threads?.Summary?.FinalizerQueueLength ?? 0;
        if (finalizerCount > 0)
        {
            result.Summary.Description += $"Finalizer queue: {finalizerCount} objects. ";
        }

        // Add memory consumption summary (may have been updated after base GenerateSummary)
        if (result.Memory?.LeakAnalysis?.Detected == true)
        {
            var severity = result.Memory!.LeakAnalysis.Severity ?? "Elevated";
            var indicators = result.Memory!.LeakAnalysis.PotentialIssueIndicators?.Count ?? 0;
            if (indicators > 0)
            {
                result.Summary.Description += $"MEMORY: {severity} consumption ({result.Memory!.LeakAnalysis.TotalHeapBytes:N0} bytes), {indicators} potential issue indicator(s). ";
            }
            else
            {
                result.Summary.Description += $"MEMORY: {severity} consumption ({result.Memory!.LeakAnalysis.TotalHeapBytes:N0} bytes). ";
            }
        }

        // Add deadlock summary (may have been updated after base GenerateSummary)
        if (result.Threads?.Deadlock?.Detected == true)
        {
            result.Summary.Description += $"DEADLOCK DETECTED: {result.Threads!.Deadlock.InvolvedThreads.Count} threads involved. ";
        }

        // Add warning if any commands caused LLDB to crash
        if (_crashedCommands.Count > 0)
        {
            result.Summary.Description += $"WARNING: {_crashedCommands.Count} debugger command(s) crashed and were recovered. Some data may be incomplete. ";
            result.Summary.Recommendations ??= new List<string>();
            result.Summary.Recommendations.Add($"Some analysis commands crashed the debugger: {string.Join(", ", _crashedCommands)}. Results may be incomplete.");
        }

        // Transfer .NET-specific data to new hierarchical structure
        PopulateDotNetStructure(result);
    }

    /// <summary>
    /// Populates the new hierarchical structure with .NET-specific information.
    /// <summary>
    /// Finalizes the .NET analysis result by ensuring assembly count is set in summary.
    /// NOTE: This method used to copy data from DotNetInfo to the new hierarchical structure,
    /// but now all parsing methods write directly to the new structure, making most of that work redundant.
    /// </summary>
    private void PopulateDotNetStructure(CrashAnalysisResult result)
    {
        // Update assembly count in summary if we have assemblies
        var assemblies = result.Assemblies?.Items;
        if (assemblies?.Count > 0 && result.Summary != null)
        {
            result.Summary.AssemblyCount = assemblies.Count;
        }
    }

    /// <summary>
    /// Parses full call stacks from 'clrstack -a -r -all' output.
    /// This parses managed frames with parameters, locals, and register values.
    /// Used in conjunction with bt all (native frames) and SP-based merging.
    /// </summary>
    /// <param name="clrStackFullOutput">The clrstack command output to parse.</param>
    /// <param name="result">The result object to populate.</param>
    /// <param name="appendToExisting">If true, append frames to existing call stacks instead of clearing them.
    /// This is the default mode when native frames were already parsed from bt all.</param>
    protected void ParseFullCallStacksAllThreads(string clrStackFullOutput, CrashAnalysisResult result, bool appendToExisting = false)
    {
        // Check for various error conditions that indicate SOS/CLR is not available (native dump)
        // In these cases, we preserve the native stacks from bt all
        if (IsSosErrorOutput(clrStackFullOutput) || 
            !clrStackFullOutput.Contains("OS Thread Id:", StringComparison.OrdinalIgnoreCase)) // No valid thread headers
        {
            // Keep existing native stacks from bt all
            return;
        }

        var lines = clrStackFullOutput.Split('\n');
        ThreadInfo? currentThread = null;
        StackFrame? lastFrame = null;
        var frameNumber = 0;
        
        // Track which section we're parsing (PARAMETERS or LOCALS)
        var currentSection = VariableSection.None;

        // Ensure thread containers exist before we start populating them
        result.Threads ??= new ThreadsInfo();
        result.Threads.All ??= new List<ThreadInfo>();
        result.Threads.Summary ??= new ThreadSummary();

        for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var line = lines[lineIdx];

            // Check for thread header: "OS Thread Id: 0x8954" or "OS Thread Id: 0x8954 (1)"
            var threadMatch = Regex.Match(line, @"OS Thread Id:\s*(0x[0-9a-f]+|\d+)(?:\s*\((\d+)\))?", RegexOptions.IgnoreCase);
            if (threadMatch.Success)
            {
                // Find the thread in result.Threads if available
                var threads = result.Threads?.All;
                if (threads != null)
                {
                    currentThread = FindThreadByTid(threads, threadMatch.Groups[1].Value);
                }
                
                // If thread not found, create a new one
                if (currentThread == null)
                {
                    currentThread = new ThreadInfo
                    {
                        ThreadId = threadMatch.Groups[1].Value,
                        State = "Unknown"
                    };
                    result.Threads?.All?.Add(currentThread);
                }
                
                // Clear existing call stack unless we're appending (fallback mode with native frames already parsed)
                if (!appendToExisting)
                {
                currentThread.CallStack.Clear();
                }
                frameNumber = appendToExisting ? currentThread.CallStack.Count : 0;
                lastFrame = null;
                currentSection = VariableSection.None;
                continue;
            }

            // Skip header lines and empty lines
            if (string.IsNullOrWhiteSpace(line) || 
                line.Contains("Child SP", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("(lldb)", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            
            // Check for PARAMETERS: section header
            if (line.TrimStart().StartsWith("PARAMETERS:", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = VariableSection.Parameters;
                continue;
            }
            
            // Check for LOCALS: section header
            if (line.TrimStart().StartsWith("LOCALS:", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = VariableSection.Locals;
                continue;
            }

            // Check if this is a register line or variable line (indented, contains =)
            // Format: "        x0=0x... x1=0x..." or "    rax=0x... rbx=0x..."
            // Variable format: "        this (<CLR reg>) = 0x..."
            if (line.StartsWith("    ") && line.Contains("=") && lastFrame != null)
            {
                var trimmedLine = line.Trim();
                
                // If we're in a variable section and the line has a variable pattern, parse as variable
                // Variable lines typically have: name (location) = value or <no data>
                if (currentSection != VariableSection.None && 
                    (trimmedLine.Contains("(<") || trimmedLine.StartsWith("<") || 
                     Regex.IsMatch(trimmedLine, @"^\w+\s*\([^)]+\)\s*=")))
                {
                    // Pass parameter types only for PARAMETERS section
                    var paramTypes = currentSection == VariableSection.Parameters 
                        ? ExtractParameterTypes(lastFrame.Function) 
                        : null;
                    ParseVariableLine(trimmedLine, lastFrame, currentSection, paramTypes);
                    continue;
                }
                
                // Check if it's a register line (multiple word=hex patterns on same line)
                var registerPatterns = Regex.Matches(trimmedLine, @"\b[a-z]+\d*=[0-9a-f]+", RegexOptions.IgnoreCase);
                if (registerPatterns.Count >= 3)
                {
                    // This is a register line - also marks end of variable sections
                    currentSection = VariableSection.None;
                    ParseRegisterLine(line, lastFrame);
                    continue;
                }
                
                // Could still be a variable line in PARAMETERS/LOCALS section
                if (currentSection != VariableSection.None)
                {
                    var paramTypes = currentSection == VariableSection.Parameters 
                        ? ExtractParameterTypes(lastFrame.Function) 
                        : null;
                    ParseVariableLine(trimmedLine, lastFrame, currentSection, paramTypes);
                    continue;
                }
            }

            // Parse frame line - this also resets the variable section
            var frame = ParseFullStackFrame(line, ref frameNumber);
            if (frame != null && currentThread != null)
            {
                currentThread.CallStack.Add(frame);
                lastFrame = frame;
                currentSection = VariableSection.None;
            }
        }
    }
    
    /// <summary>
    /// Enum for tracking which variable section we're parsing.
    /// </summary>
    private enum VariableSection
    {
        None,
        Parameters,
        Locals
    }
    
    /// <summary>
    /// Parses a variable line from PARAMETERS or LOCALS section.
    /// Formats:
    /// - "this (&lt;CLR reg&gt;) = 0x0000f7158e82d780"  (parameter with name)
    /// - "timeoutMs (&lt;CLR reg&gt;) = 0x0000000000004e20"  (parameter with name)
    /// - "&lt;CLR reg&gt; = 0x000000000023d2f5"  (local with location in angle brackets)
    /// - "0x0000FFFFEFCB9600 = 0x0000f7158e830608"  (local with stack address)
    /// - "&lt;no data&gt;"  (no data available)
    /// </summary>
    private static void ParseVariableLine(string line, StackFrame frame, VariableSection section, List<string>? parameterTypes = null)
    {
        var variable = new LocalVariable();
        
        // Check for <no data> pattern
        if (line.Contains("<no data>", StringComparison.OrdinalIgnoreCase))
        {
            variable.Name = "[unnamed]";
            variable.Value = "[NO DATA]";
            variable.HasData = false;
        }
        // Pattern 1: "name (<location>) = value" - parameters with name and location in parentheses
        else if (Regex.Match(line, @"^(\w+)\s*\(([^)]+)\)\s*=\s*(.+)$") is { Success: true } fullMatch)
        {
            variable.Name = fullMatch.Groups[1].Value;
            variable.Location = NormalizeLocation(fullMatch.Groups[2].Value.Trim());
            variable.Value = fullMatch.Groups[3].Value.Trim();
        }
        // Pattern 2: "<location> = value" - locals with location in angle brackets (e.g., <CLR reg>)
        else if (Regex.Match(line, @"^<([^>]+)>\s*=\s*(.+)$") is { Success: true } angleBracketMatch)
        {
            variable.Name = "[unnamed]";
            variable.Location = $"[{angleBracketMatch.Groups[1].Value.Trim()}]";
            variable.Value = angleBracketMatch.Groups[2].Value.Trim();
        }
        // Pattern 3: "address = value" - locals with hex address as location (e.g., 0x0000FFFFEFCB9600)
        else if (Regex.Match(line, @"^(0x[0-9a-f]+)\s*=\s*(.+)$", RegexOptions.IgnoreCase) is { Success: true } addrMatch)
        {
            variable.Name = "[unnamed]";
            variable.Location = addrMatch.Groups[1].Value; // The hex address is the location
            variable.Value = addrMatch.Groups[2].Value.Trim();
        }
        // Pattern 4: "name = value" - simple format without location
        else if (Regex.Match(line, @"^(\S+)\s*=\s*(.+)$") is { Success: true } simpleMatch)
        {
            variable.Name = simpleMatch.Groups[1].Value;
            variable.Value = simpleMatch.Groups[2].Value.Trim();
        }
        else
        {
            // Fallback: store the whole line
            variable.Name = "[unknown]";
            variable.Value = line;
        }
        
        // Add to the appropriate list
        if (section == VariableSection.Parameters)
        {
            frame.Parameters ??= new List<LocalVariable>();
            
            // Try to assign type from parameter types list
            // Account for 'this' parameter which is not in the signature
            var paramIndex = frame.Parameters.Count;
            if (variable.Name == "this" && frame.IsManaged)
            {
                // 'this' is the containing type - extract from module/function
                variable.Type = NormalizeTypeName(ExtractContainingType(frame.Function));
                variable.IsReferenceType = true; // 'this' is always a reference type
            }
            else if (parameterTypes != null)
            {
                // Adjust index: if first param is 'this', it's not in the signature
                var hasThis = frame.Parameters.Count > 0 && frame.Parameters[0].Name == "this";
                var typeIndex = hasThis ? paramIndex - 1 : paramIndex;
                
                if (typeIndex >= 0 && typeIndex < parameterTypes.Count)
                {
                    variable.Type = NormalizeTypeName(parameterTypes[typeIndex]);
                    variable.IsReferenceType = IsReferenceType(variable.Type);
                }
            }
            
            frame.Parameters.Add(variable);
        }
        else if (section == VariableSection.Locals)
        {
            frame.Locals ??= new List<LocalVariable>();
            frame.Locals.Add(variable);
        }
    }
    
    /// <summary>
    /// Extracts parameter types from a function signature.
    /// E.g., "System.Threading.LowLevelLifoSemaphore.WaitForSignal(Int32)" -> ["Int32"]
    /// </summary>
    private static List<string> ExtractParameterTypes(string function)
    {
        var types = new List<string>();
        
        // Find the parameter list in parentheses
        var parenStart = function.LastIndexOf('(');
        var parenEnd = function.LastIndexOf(')');
        
        if (parenStart < 0 || parenEnd <= parenStart)
            return types;
        
        var paramString = function.Substring(parenStart + 1, parenEnd - parenStart - 1);
        if (string.IsNullOrWhiteSpace(paramString))
            return types;
        
        // Split by comma, handling generic types with nested commas
        var depth = 0;
        var currentParam = new System.Text.StringBuilder();
        
        foreach (var c in paramString)
        {
            if (c == '<' || c == '[') depth++;
            else if (c == '>' || c == ']') depth--;
            else if (c == ',' && depth == 0)
            {
                if (currentParam.Length > 0)
                {
                    types.Add(currentParam.ToString().Trim());
                    currentParam.Clear();
                }
                continue;
            }
            currentParam.Append(c);
        }
        
        if (currentParam.Length > 0)
        {
            types.Add(currentParam.ToString().Trim());
        }
        
        return types;
    }
    
    /// <summary>
    /// Extracts the containing type from a function name.
    /// E.g., "System.Threading.LowLevelLifoSemaphore.WaitForSignal(Int32)" -> "System.Threading.LowLevelLifoSemaphore"
    /// </summary>
    private static string? ExtractContainingType(string function)
    {
        // Remove the module prefix if present (e.g., "System.Private.CoreLib.dll!")
        var bangIndex = function.IndexOf('!');
        var cleanFunc = bangIndex >= 0 ? function.Substring(bangIndex + 1) : function;
        
        // Find the method name (last . before the parentheses)
        var parenIndex = cleanFunc.IndexOf('(');
        var methodPart = parenIndex >= 0 ? cleanFunc.Substring(0, parenIndex) : cleanFunc;
        
        var lastDot = methodPart.LastIndexOf('.');
        if (lastDot > 0)
        {
            return methodPart.Substring(0, lastDot);
        }
        
        return null;
    }
    
    /// <summary>
    /// Determines if a type is a reference type based on its name.
    /// </summary>
    private static bool? IsReferenceType(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return null;
        
        // Known value types (primitives and common structs)
        var valueTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Primitive types
            "Int16", "Int32", "Int64", "UInt16", "UInt32", "UInt64",
            "Byte", "SByte", "Boolean", "Char", "Single", "Double", "Decimal",
            "IntPtr", "UIntPtr", "Void",
            // Common aliases
            "short", "int", "long", "ushort", "uint", "ulong",
            "byte", "sbyte", "bool", "char", "float", "double", "decimal",
            // Common structs
            "DateTime", "DateTimeOffset", "TimeSpan", "Guid", 
            "Nullable`1", "ValueTuple", "Span`1", "ReadOnlySpan`1", "Memory`1",
            "CancellationToken", "Task"
        };
        
        // Check if it's a known value type
        var baseName = typeName.Split('<', '`')[0]; // Handle generics
        if (valueTypes.Contains(baseName))
            return false;
        
        // ByRef parameters point to the value (both old and new format)
        if (typeName.EndsWith(" ByRef", StringComparison.OrdinalIgnoreCase) ||
            typeName.EndsWith("(ByRef)", StringComparison.OrdinalIgnoreCase))
            return true; // The reference itself is an address
        
        // Pointer types are addresses
        if (typeName.EndsWith('*'))
            return true;
        
        // Arrays are reference types
        if (typeName.EndsWith("[]"))
            return true;
        
        // String is a reference type
        if (baseName.Equals("String", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("string", StringComparison.OrdinalIgnoreCase))
            return true;
        
        // Object is a reference type
        if (baseName.Equals("Object", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("object", StringComparison.OrdinalIgnoreCase))
            return true;
        
        // Assume unknown types are reference types (most common case)
        // Unless they look like a struct pattern (start with uppercase, short name)
        return true;
    }

    /// <summary>
    /// Finds a thread by its OS thread ID (hex or decimal).
    /// </summary>
    private static ThreadInfo? FindThreadByTid(List<ThreadInfo>? threads, string tidStr)
    {
        if (threads == null || threads.Count == 0)
            return null;

        // Convert hex to decimal for matching (0x8954 = 35156)
        long tidDecimal = 0;
        if (tidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            long.TryParse(tidStr.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out tidDecimal);
        }
        else
        {
            long.TryParse(tidStr, out tidDecimal);
        }

        // Try to match thread by:
        // 1. ThreadId contains the tid in decimal: "1 (tid: 35156)"
        // 2. ThreadId matches hex format
        return threads.FirstOrDefault(t => 
            t.ThreadId.Contains($"tid: {tidDecimal}") ||
            t.ThreadId.Contains($"tid:{tidDecimal}") ||
            t.ThreadId.Contains($"(tid: {tidDecimal})") ||
            t.ThreadId == tidStr ||
            t.ThreadId == tidDecimal.ToString());
    }

    /// <summary>
    /// Parses a full stack frame line from clrstack output.
    /// Handles both managed and native frames with various formats.
    /// </summary>
    private StackFrame? ParseFullStackFrame(string line, ref int frameNumber)
    {
        // Format variations:
        // 1. SP IP CallSite:     "0000FFFFEFCB76A0 0000F75587765AB4 System.Runtime.EH.DispatchEx(...) [/path @ 123]"
        // 2. SP IP (empty):      "0000FFFFEFCB73D0 0000F7558FFEACE4 " (native crash point, no call site)
        // 3. SP    [Frame]:      "0000FFFFEFCB76F8                  [InlinedCallFrame: ...]" (IP column is spaces)
        
        // First, try the full format with SP, IP, and CallSite
        var fullMatch = Regex.Match(line, 
            @"^\s*([0-9a-f]{8,16})\s+([0-9a-f]{8,16})\s*(.*)$", 
            RegexOptions.IgnoreCase);

        if (fullMatch.Success)
        {
            var sp = fullMatch.Groups[1].Value;
            var ip = fullMatch.Groups[2].Value;
            var callSite = fullMatch.Groups[3].Value.Trim();

            return CreateStackFrame(sp, ip, callSite, ref frameNumber);
        }

        // Try format where IP is empty (spaces) and we have [FrameName]
        // Format: "0000FFFFEFCB76F8                  [InlinedCallFrame: ...]"
        // Note: Lines may have trailing whitespace after ]
        var noIpMatch = Regex.Match(line, 
            @"^\s*([0-9a-f]{8,16})\s+(\[.+\])\s*$", 
            RegexOptions.IgnoreCase);

        if (noIpMatch.Success)
        {
            var sp = noIpMatch.Groups[1].Value;
            var callSite = noIpMatch.Groups[2].Value.Trim();
            
            return CreateStackFrame(sp, null, callSite, ref frameNumber);
        }

        return null;
    }

    /// <summary>
    /// Creates a StackFrame from parsed components.
    /// </summary>
    private StackFrame CreateStackFrame(string sp, string? ip, string callSite, ref int frameNumber)
    {
        // Determine if this is a managed or native frame
        bool isManaged;
        string module = "";
        string function = callSite;
        string? sourceFile = null;
        int? lineNumber = null;

        // Empty call site = native frame at crash point
        if (string.IsNullOrWhiteSpace(callSite))
        {
            isManaged = false;
            function = ip != null ? $"[Native Code @ 0x{ip}]" : "[Unknown]";
        }
        // Frame markers like [InlinedCallFrame], [ExternalMethodFrame], [GCFrame], etc.
        else if (callSite.StartsWith("[") && callSite.EndsWith("]"))
        {
            isManaged = true;  // These are CLR transition frames
            function = callSite;
        }
        // Native library without function (no '!' separator): "libstdc++.so.6" or "libstdc++.so.6 + -1"
        else if (IsNativeLibraryWithoutFunction(callSite))
        {
            isManaged = false;
            (module, function) = ParseNativeLibraryOnly(callSite, ip);
        }
        // Native frame with debug info: "module.so!function(...) + offset at source:line"
        // Key indicators: module ends with .so/.dylib, contains '!' separator
        else if (IsNativeFrameWithSymbols(callSite))
        {
            isManaged = false;
            (module, function, sourceFile, lineNumber) = ParseNativeFrameWithDebugInfo(callSite);
        }
        // Managed frame: .dll module or managed namespace pattern
        else
        {
            isManaged = true;

            // Parse source info if present: [/path/file.cs @ 123]
            // Use [^\[\]] to avoid matching nested generic type brackets like [[System.__Canon]]
            // Source paths don't contain [ or ] characters
            var sourceMatch = Regex.Match(callSite, @"\[([^\[\]]+)\s*@\s*(\d+)\]$");
            if (sourceMatch.Success)
            {
                sourceFile = sourceMatch.Groups[1].Value.Trim();
                if (int.TryParse(sourceMatch.Groups[2].Value, out int ln))
                {
                    lineNumber = ln;
                }
                callSite = callSite.Substring(0, sourceMatch.Index).Trim();
            }

            // Try to parse module!method + offset format
            // Format: "Module.dll!Namespace.Class.Method(args) + offset"
            var managedMatch = Regex.Match(callSite, @"^(\S+\.dll)!(.+?)(?:\s*\+\s*\d+)?$");
            if (managedMatch.Success)
            {
                module = managedMatch.Groups[1].Value;
                function = managedMatch.Groups[2].Value;
            }
            else
            {
                // Fallback: extract module from namespace
                var (extractedModule, extractedMethod) = ExtractModuleAndMethod(callSite);
                module = extractedModule;
                function = extractedMethod;
            }
        }

        return new StackFrame
        {
            FrameNumber = frameNumber++,
            StackPointer = $"0x{sp}",
            InstructionPointer = ip != null ? $"0x{ip}" : "0x0",
            Module = module,
            Function = function,
            SourceFile = sourceFile,
            LineNumber = lineNumber,
            Source = lineNumber.HasValue ? $"{sourceFile}:{lineNumber}" : sourceFile,
            IsManaged = isManaged
        };
    }

    /// <summary>
    /// Checks if the call site is a native library without a function name (no '!' separator).
    /// Examples: "libstdc++.so.6", "libstdc++.so.6 + -1", "ld-linux-aarch64.so.1"
    /// </summary>
    private static bool IsNativeLibraryWithoutFunction(string callSite)
    {
        // No '!' means no module!function format
        if (callSite.Contains('!'))
            return false;

        // Must look like a native library (.so, .dylib, ld-*)
        return Regex.IsMatch(callSite, @"^(lib[a-z0-9_+.-]+\.so|ld-[a-z0-9_.-]+\.so|[a-z0-9_+.-]+\.(so|dylib))(\.\d+)*(\s*\+\s*-?\d+)?$", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Parses a native library-only frame (no function name).
    /// </summary>
    private static (string Module, string Function) ParseNativeLibraryOnly(string callSite, string? ip)
    {
        // Extract library name (everything before optional " + offset")
        // Use " + " (with spaces) to avoid matching ++ in libstdc++.so.6
        var libraryName = callSite;
        var plusIdx = callSite.IndexOf(" + ", StringComparison.Ordinal);
        if (plusIdx > 0)
        {
            libraryName = callSite.Substring(0, plusIdx);
        }
        
        // For display, show the library name or native code marker
        var function = ip != null ? $"[Native Code @ 0x{ip}]" : $"[{libraryName}]";
        return (libraryName, function);
    }

    /// <summary>
    /// Checks if the call site is a native frame with debug symbols.
    /// Native frames have module endings like .so, .dylib, or special names like "dotnet".
    /// </summary>
    private static bool IsNativeFrameWithSymbols(string callSite)
    {
        // Must have '!' separator for module!function format
        if (!callSite.Contains('!'))
            return false;

        // Check if module part looks native (before the '!')
        var bangIndex = callSite.IndexOf('!');
        var modulePart = callSite.Substring(0, bangIndex);

        // Native module patterns: .so, .dylib, dotnet, or ends with .so.N
        return modulePart.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
               modulePart.Contains(".so.", StringComparison.OrdinalIgnoreCase) ||
               modulePart.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) ||
               modulePart.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses a native frame with debug info.
    /// Format: "module.so!function(args) + offset at source:line" or "module.so!function(args)"
    /// </summary>
    private static (string Module, string Function, string? SourceFile, int? LineNumber) ParseNativeFrameWithDebugInfo(string callSite)
    {
        string module = "";
        string function = callSite;
        string? sourceFile = null;
        int? lineNumber = null;

        // Extract source info if present: "at /path/to/file.cpp:123" or "at /path/to/file.cpp:123:45"
        var sourceMatch = Regex.Match(callSite, @"\s+at\s+([^:]+):(\d+)(?::\d+)?(?:\s+\[opt\])?$");
        if (sourceMatch.Success)
        {
            sourceFile = Path.GetFileName(sourceMatch.Groups[1].Value.Trim());
            if (int.TryParse(sourceMatch.Groups[2].Value, out int ln))
            {
                lineNumber = ln;
            }
            callSite = callSite.Substring(0, sourceMatch.Index).Trim();
        }

        // Parse module!function + offset format
        // Format: "libcoreclr.so!PROCCreateCrashDump(...) + 636"
        var nativeMatch = Regex.Match(callSite, @"^([^!]+)!(.+?)(?:\s*\+\s*-?\d+)?$");
        if (nativeMatch.Success)
        {
            module = nativeMatch.Groups[1].Value;
            function = nativeMatch.Groups[2].Value;
        }

        return (module, function, sourceFile, lineNumber);
    }

    /// <summary>
    /// Parses register values from a register line and adds them to the frame.
    /// Format: "    x0=0x... x1=0x..." or "    rax=0x... rbx=0x..."
    /// </summary>
    private static void ParseRegisterLine(string line, StackFrame frame)
    {
        frame.Registers ??= new Dictionary<string, string>();

        // Match register patterns: name=value or name = value
        var regMatches = Regex.Matches(line, @"(\w+)\s*=\s*(0x[0-9a-f]+|\d+)", RegexOptions.IgnoreCase);
        foreach (Match match in regMatches)
        {
            var regName = match.Groups[1].Value.ToLowerInvariant();
            var regValue = match.Groups[2].Value;
            frame.Registers[regName] = regValue;
        }
    }

    /// <summary>
    /// Parses a single managed frame from clrstack output.
    /// </summary>
    private StackFrame? ParseManagedFrame(string line, ref int frameNumber)
    {
        // Parse clrstack frame formats:
        // Format 1 (LLDB/SOS): "000000000012f000 00007ff812345678 Namespace.Class.Method(args)"
        // Format 2 (with source): "000000000012f000 00007ff812345678 Namespace.Class.Method(args) [/path/file.cs @ 42]"
        // Format 3 (simple): "Namespace.Class.Method(args)"
        
        // Try to match full format with addresses
        var fullMatch = Regex.Match(line, 
            @"^\s*([0-9a-f]+)\s+([0-9a-f]+)\s+(.+?)(?:\s*\[(.+?)\s*@\s*(\d+)\])?$", 
            RegexOptions.IgnoreCase);

        if (fullMatch.Success)
        {
            var ip = fullMatch.Groups[2].Value;
            var methodName = fullMatch.Groups[3].Value.Trim();
            var sourceFile = fullMatch.Groups[4].Success ? fullMatch.Groups[4].Value.Trim() : null;
            var lineNum = fullMatch.Groups[5].Success ? int.Parse(fullMatch.Groups[5].Value) : (int?)null;

            // Skip native transition markers
            if (methodName.Contains("[Native", StringComparison.OrdinalIgnoreCase) ||
                methodName.Contains("GCFrame", StringComparison.OrdinalIgnoreCase) ||
                methodName.Contains("DebuggerU2MCatchHandlerFrame", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Extract module and method from full method name
            var (module, method) = ExtractModuleAndMethod(methodName);

            return new StackFrame
            {
                FrameNumber = frameNumber++,
                InstructionPointer = $"0x{ip}",
                Module = module,
                Function = method,
                SourceFile = sourceFile,
                LineNumber = lineNum,
                Source = lineNum.HasValue ? $"{sourceFile}:{lineNum}" : sourceFile,
                IsManaged = true
            };
        }

        // Try simpler format (just method name)
        var simpleMatch = Regex.Match(line, @"^\s*(\S+\.\S+\([^)]*\))\s*(?:\[(.+?)\s*@\s*(\d+)\])?$");
        if (simpleMatch.Success)
        {
            var methodName = simpleMatch.Groups[1].Value.Trim();
            var sourceFile = simpleMatch.Groups[2].Success ? simpleMatch.Groups[2].Value.Trim() : null;
            var lineNum = simpleMatch.Groups[3].Success ? int.Parse(simpleMatch.Groups[3].Value) : (int?)null;

            var (module, method) = ExtractModuleAndMethod(methodName);

            return new StackFrame
            {
                FrameNumber = frameNumber++,
                InstructionPointer = "0x0",
                Module = module,
                Function = method,
                SourceFile = sourceFile,
                LineNumber = lineNum,
                Source = lineNum.HasValue ? $"{sourceFile}:{lineNum}" : sourceFile,
                IsManaged = true
            };
        }

        return null;
    }

    /// <summary>
    /// Extracts module name and method name from a full .NET method signature.
    /// </summary>
    private static (string Module, string Method) ExtractModuleAndMethod(string fullMethodName)
    {
        // Format: "Namespace.ClassName.MethodName(args)" or "Namespace.ClassName+NestedClass.MethodName(args)"
        // We want to extract module (assembly/namespace) and method separately

        var method = fullMethodName;
        var module = "";

        // Find the last dot before the opening parenthesis (that's the class.method separator)
        var parenIndex = fullMethodName.IndexOf('(');
        var searchEnd = parenIndex > 0 ? parenIndex : fullMethodName.Length;
        var lastDot = fullMethodName.LastIndexOf('.', searchEnd - 1);

        if (lastDot > 0)
        {
            // Find second-to-last dot for namespace.class separation
            var secondLastDot = fullMethodName.LastIndexOf('.', lastDot - 1);
            if (secondLastDot > 0)
            {
                module = fullMethodName.Substring(0, secondLastDot);
            }
            else
            {
                module = fullMethodName.Substring(0, lastDot);
            }
        }

        return (module, method);
    }
    
    /// <summary>
    /// Merges native frames (from bt all) and managed frames (from clrstack) by stack pointer values.
    /// This is the default approach for reliable stack collection across all platforms.
    /// Stack grows downward, so lower SP = deeper frame (more recent call).
    /// 
    /// With frame-format configured to include SP=${frame.sp}, native frames have real SP values,
    /// making the merge straightforward: sort all frames by SP ascending.
    /// </summary>
    private void MergeNativeAndManagedFramesBySP(CrashAnalysisResult result)
    {
        if (result.Threads?.All == null)
        {
            return;
        }
        
        foreach (var thread in result.Threads.All)
        {
            if (thread.CallStack == null || thread.CallStack.Count == 0)
            {
                continue;
            }
            
            // Separate managed and native frames
            var managedFrames = thread.CallStack.Where(f => f.IsManaged).ToList();
            var nativeFrames = thread.CallStack.Where(f => !f.IsManaged).ToList();
            
            // If we only have one type, nothing to merge
            if (managedFrames.Count == 0 || nativeFrames.Count == 0)
            {
                continue;
            }
            
            // Parse SP values from all frames
            var allFrames = thread.CallStack
                .Select(f => (frame: f, sp: ParseHexAddress(f.StackPointer ?? ""), originalOrder: f.FrameNumber))
                .ToList();
            
            // Check how many frames have valid SP values
            var framesWithSp = allFrames.Where(x => x.sp > 0).ToList();
            
            if (framesWithSp.Count < 2)
            {
                // Not enough SP values to merge by SP - fall back to frame number ordering
                _logger?.LogWarning("Insufficient SP values for thread {ThreadId} ({Count} frames with SP), merging by frame number", 
                    thread.ThreadId, framesWithSp.Count);
                
                // Merge by frame number only (interleave by original frame numbers)
                var merged = thread.CallStack
                    .OrderBy(f => f.FrameNumber)
                    .ToList();
                
                for (int i = 0; i < merged.Count; i++)
                {
                    merged[i].FrameNumber = i;
                }
                
                thread.CallStack = merged;
                continue;
            }
            
            // For frames without SP, estimate based on surrounding frames
            var framesWithValidSp = framesWithSp.Select(x => (x.frame, x.sp)).ToList();
            var minSp = framesWithSp.Min(x => x.sp);
            var maxSp = framesWithSp.Max(x => x.sp);
            
            var finalFrames = new List<(StackFrame frame, ulong sp, int originalOrder)>();
            
            foreach (var (frame, sp, originalOrder) in allFrames)
            {
                ulong effectiveSp = sp;
                if (sp == 0)
                {
                    // Frame without SP - estimate based on frame number
                    effectiveSp = EstimateSpFromFrameNumber(originalOrder, framesWithValidSp, minSp, maxSp);
                }
                finalFrames.Add((frame, effectiveSp, originalOrder));
            }
            
            // Sort by SP ascending (lower SP = most recent call = frame 0)
            // Stack grows downward on ARM64/x64, so lower SP = deeper in call stack
            var sortedFrames = finalFrames
                .OrderBy(x => x.sp)
                .ThenBy(x => x.originalOrder) // Tie-break by original order
                .Select(x => x.frame)
                .ToList();
            
            // Renumber frames
            for (int i = 0; i < sortedFrames.Count; i++)
            {
                sortedFrames[i].FrameNumber = i;
            }
            
            // Replace call stack with merged version
            thread.CallStack = sortedFrames;
            
            _logger?.LogDebug("Merged {NativeCount} native + {ManagedCount} managed frames for thread {ThreadId} ({WithSp} had SP)",
                nativeFrames.Count, managedFrames.Count, thread.ThreadId, framesWithSp.Count);
        }
    }
    
    /// <summary>
    /// Estimates an SP value for a frame based on its frame number and known SP values from other frames.
    /// Uses linear interpolation between surrounding frames with known SP values.
    /// </summary>
    private static ulong EstimateSpFromFrameNumber(
        int frameNumber, 
        List<(StackFrame frame, ulong sp)> framesWithSp,
        ulong minSp,
        ulong maxSp)
    {
        // Find surrounding frames with known SP
        var above = framesWithSp
            .Where(x => x.frame.FrameNumber < frameNumber)
            .OrderByDescending(x => x.frame.FrameNumber)
            .FirstOrDefault();
        var below = framesWithSp
            .Where(x => x.frame.FrameNumber > frameNumber)
            .OrderBy(x => x.frame.FrameNumber)
            .FirstOrDefault();
        
        if (above.frame != null && below.frame != null)
        {
            // Interpolate between the two (stack grows down, so above has higher SP)
            var frameRange = (ulong)(below.frame.FrameNumber - above.frame.FrameNumber);
            var frameOffset = (ulong)(frameNumber - above.frame.FrameNumber);
            
            // Handle case where SP values are in unexpected order (above.sp < below.sp)
            if (above.sp < below.sp)
            {
                // Swap logic - below has higher SP (unusual)
                var spRange = below.sp - above.sp;
                if (frameRange > 0)
                {
                    return above.sp + (spRange * frameOffset / frameRange);
                }
                return above.sp;
            }
            
            var normalSpRange = above.sp - below.sp; // above has higher SP (normal case)
            if (frameRange > 0)
            {
                return above.sp - (normalSpRange * frameOffset / frameRange);
            }
            return above.sp;
        }
        else if (above.frame != null)
        {
            // Only have frame above - estimate below it
            var offset = (ulong)(frameNumber - above.frame.FrameNumber) * 0x100;
            return above.sp > offset ? above.sp - offset : 0;
        }
        else if (below.frame != null)
        {
            // Only have frame below - estimate above it
            return below.sp + (ulong)(below.frame.FrameNumber - frameNumber) * 0x100;
        }
        else
        {
            // No reference frames - use midpoint
            return (maxSp + minSp) / 2;
        }
    }
    
    /// <summary>
    /// Parses a hex address string (with or without 0x prefix) to ulong.
    /// </summary>
    private static ulong ParseHexAddress(string address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return 0;
        }
        
        var hex = address.TrimStart();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex.Substring(2);
        }
        
        if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var result))
        {
            return result;
        }
        
        return 0;
    }
    
    /// <summary>
    /// Enhances variable values by converting hex values to meaningful representations.
    /// For primitives: converts to actual values (e.g., "0x4e20" -> "20000")
    /// For ByRef types: dereferences the pointer to get the actual value/address
    /// For strings: resolves the actual string content using ClrMD (truncated to 1024 chars)
    /// </summary>
    private async Task EnhanceVariableValuesAsync(CrashAnalysisResult result)
    {
        if (result.Threads?.All == null || result.Threads.All.Count == 0)
        {
            return;
        }

        // Collect ByRef variables that need dereferencing
        var byRefVariables = new List<LocalVariable>();
        
        // First pass: convert primitive values and collect ByRef variables
        foreach (var thread in result.Threads.All)
        {
            foreach (var frame in thread.CallStack ?? Enumerable.Empty<StackFrame>())
            {
                // Process parameters
                if (frame.Parameters != null)
                {
                    foreach (var param in frame.Parameters)
                    {
                        EnhancePrimitiveValue(param);
                        
                        // Collect ByRef variables for dereferencing
                        if (IsByRefVariable(param))
                        {
                            byRefVariables.Add(param);
                        }
                    }
                }
                
                // Process locals
                if (frame.Locals != null)
                {
                    foreach (var local in frame.Locals)
                    {
                        EnhancePrimitiveValue(local);
                        
                        // Collect ByRef variables for dereferencing
                        if (IsByRefVariable(local))
                        {
                            byRefVariables.Add(local);
                        }
                    }
                }
            }
        }
        
        // Second pass: resolve ByRef variables by dereferencing
        if (byRefVariables.Count > 0)
        {
            await ResolveByRefVariablesAsync(byRefVariables);
        }
        
        // Third pass: collect all string addresses (including resolved ByRef strings)
        var stringAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var thread in result.Threads.All)
        {
            foreach (var frame in thread.CallStack ?? Enumerable.Empty<StackFrame>())
            {
                CollectStringAddresses(frame.Parameters, stringAddresses);
                CollectStringAddresses(frame.Locals, stringAddresses);
            }
        }
        
        // Fourth pass: resolve string values using ClrMD
        if (stringAddresses.Count > 0)
        {
            var stringValues = await ResolveStringValuesAsync(stringAddresses, result.RawCommands);
            
            // Apply resolved string values to variables (updates value to actual string content)
            foreach (var thread in result.Threads.All)
            {
                foreach (var frame in thread.CallStack ?? Enumerable.Empty<StackFrame>())
                {
                    ApplyStringValues(frame.Parameters, stringValues);
                    ApplyStringValues(frame.Locals, stringValues);
                }
            }
        }
        
        // Fifth pass: expand reference type objects using showobj
        // This gives full object inspection for complex types in JSON output
        await ExpandReferenceTypeObjectsAsync(result);
    }
    
    /// <summary>
    /// Expands reference type objects using ClrMD to get their full structure.
    /// Uses ClrMD Name2EE to find the method table, then ClrMD InspectObject to expand.
    /// </summary>
    private async Task ExpandReferenceTypeObjectsAsync(CrashAnalysisResult result)
    {
        if (result.Threads?.All == null || result.Threads.All.Count == 0)
        {
            return;
        }

        // Cache method tables to avoid repeated Name2EE calls
        var methodTableCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var thread in result.Threads.All)
        {
            foreach (var frame in thread.CallStack ?? Enumerable.Empty<StackFrame>())
            {
                await ExpandVariablesAsync(frame.Parameters, methodTableCache);
                await ExpandVariablesAsync(frame.Locals, methodTableCache);
            }
        }
    }
    
    /// <summary>
    /// Expands a list of variables by resolving reference type objects.
    /// </summary>
    private async Task ExpandVariablesAsync(List<LocalVariable>? variables, Dictionary<string, string?> methodTableCache)
    {
        if (variables == null) return;
        
        foreach (var variable in variables)
        {
            // Skip if no data
            if (!variable.HasData)
                continue;
            
            // Skip if type is unknown
            if (string.IsNullOrEmpty(variable.Type))
                continue;
            
            // Skip if value is already an expanded object (not a string)
            if (variable.Value != null && variable.Value is not string)
                continue;
            
            // Get base type name (strip ByRef suffix)
            var baseType = GetBaseTypeName(variable.Type);
            
            // Skip primitive/basic BCL types that we already handle inline
            if (IsPrimitiveOrBasicBclType(baseType))
                continue;
            
            // Get the address to inspect
            var valueStr = variable.Value?.ToString();
            if (string.IsNullOrEmpty(valueStr))
                continue;
            
            // Handle [NO DATA] case
            if (valueStr.Equals("[NO DATA]", StringComparison.OrdinalIgnoreCase))
            {
                variable.Value = new { error = "No Data" };
                continue;
            }
            
            // Check for null addresses
            if (IsNullAddress(valueStr))
            {
                variable.Value = null;
                continue;
            }
            
            // For ByRef types, use the resolved address if available
            var addressToInspect = !string.IsNullOrEmpty(variable.ResolvedAddress)
                ? variable.ResolvedAddress
                : valueStr;
            
            // Skip if address doesn't look valid
            if (!addressToInspect.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                continue;
            
            try
            {
                // Look up method table for this type (use cache)
                if (!methodTableCache.TryGetValue(baseType, out var methodTable))
                {
                    methodTable = await LookupMethodTableAsync(baseType);
                    methodTableCache[baseType] = methodTable;
                }
                
                if (string.IsNullOrEmpty(methodTable))
                    continue; // Can't expand without method table
                
                // Expand the object using ClrMD
                var expandedObject = await ExpandObjectAsync(addressToInspect, methodTable);
                
                if (expandedObject != null)
                {
                    variable.RawValue = valueStr;
                    variable.Value = expandedObject;
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail - keep original value
                System.Diagnostics.Debug.WriteLine($"Failed to expand object {baseType} at {addressToInspect}: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Gets the base type name, stripping (ByRef) suffix if present.
    /// </summary>
    private static string GetBaseTypeName(string typeName)
    {
        if (typeName.EndsWith("(ByRef)", StringComparison.OrdinalIgnoreCase))
        {
            return typeName[..^7].Trim(); // Remove "(ByRef)"
        }
        return typeName;
    }
    
    /// <summary>
    /// Primitive and basic BCL types that are already handled inline and don't need showobj expansion.
    /// </summary>
    private static readonly HashSet<string> PrimitiveAndBasicBclTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Primitive types (these are handled by EnhancePrimitiveValue)
        "System.Boolean", "Boolean", "bool",
        "System.Byte", "Byte", "byte",
        "System.SByte", "SByte", "sbyte",
        "System.Int16", "Int16", "short",
        "System.UInt16", "UInt16", "ushort",
        "System.Int32", "Int32", "int",
        "System.UInt32", "UInt32", "uint",
        "System.Int64", "Int64", "long",
        "System.UInt64", "UInt64", "ulong",
        "System.Single", "Single", "float",
        "System.Double", "Double", "double",
        "System.Decimal", "Decimal", "decimal",
        "System.Char", "Char", "char",
        "System.String", "String", "string",
        
        // Pointer types
        "System.IntPtr", "IntPtr", "nint",
        "System.UIntPtr", "UIntPtr", "nuint",
        "PTR",  // Native pointer (cannot be inspected with dumpobj)
        "VALUETYPE", // Generic value type marker
        
        // NOTE: DateTime, TimeSpan, Guid, etc. are NOT skipped!
        // These structs benefit from showobj expansion to show their fields
        // (e.g., _dateData for DateTime, _ticks for TimeSpan)
        
        // Common simple reference types (no useful fields to expand)
        "System.Object", "Object", "object",
        
        // Void
        "System.Void", "Void", "void"
    };
    
    /// <summary>
    /// Checks if a type is a primitive or basic BCL type that doesn't need showobj expansion.
    /// </summary>
    private static bool IsPrimitiveOrBasicBclType(string typeName)
    {
        // Direct match
        if (PrimitiveAndBasicBclTypes.Contains(typeName))
            return true;
        
        // Handle generic type names like "System.Nullable`1[[System.Int32]]"
        var genericIndex = typeName.IndexOf('`');
        if (genericIndex > 0)
        {
            var nonGenericName = typeName[..genericIndex];
            // Nullable<T> of primitive types
            if (nonGenericName.Equals("System.Nullable", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        
        // Handle array notation - we handle arrays separately
        if (typeName.EndsWith("[]"))
            return true;
        
        return false;
    }
    
    /// <summary>
    /// Checks if an address represents null (handles various formats).
    /// </summary>
    private static bool IsNullAddress(string? address)
    {
        if (string.IsNullOrEmpty(address))
            return true;
        
        // Handle literal "null" string
        if (address.Equals("null", StringComparison.OrdinalIgnoreCase))
            return true;
        
        // Common null patterns - normalize and check for all zeros
        var normalized = address.ToUpperInvariant().Replace("0X", "").TrimStart('0');
        return string.IsNullOrEmpty(normalized) || 
               normalized == "0" ||
               address.Equals("0x00000000", StringComparison.OrdinalIgnoreCase) ||
               address.Equals("0x0000000000000000", StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Looks up the method table for a type using ClrMD Name2EE (safe, won't crash debugger).
    /// </summary>
    private Task<string?> LookupMethodTableAsync(string typeName)
    {
        try
        {
            // Use ClrMD Name2EE - it's safe and won't crash the debugger
            if (_clrMdAnalyzer?.IsOpen != true)
            {
                _logger?.LogDebug("[LookupMethodTable] ClrMD not available, cannot lookup type '{TypeName}'", typeName);
                return Task.FromResult<string?>(null);
            }
            
            var result = _clrMdAnalyzer.Name2EE(typeName);
            
            if (result.FoundType == null)
            {
                _logger?.LogDebug("[LookupMethodTable] Type '{TypeName}' not found", typeName);
                return Task.FromResult<string?>(null);
            }
            
            _logger?.LogDebug("[LookupMethodTable] Found type '{TypeName}' with MethodTable {MT}", 
                typeName, result.FoundType.MethodTable);
            return Task.FromResult<string?>(result.FoundType.MethodTable);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[LookupMethodTable] Error looking up type '{TypeName}'", typeName);
            return Task.FromResult<string?>(null);
        }
    }
    
    /// <summary>
    /// Expands an object using ClrMD exclusively.
    /// ClrMD is used because it won't crash the debugger on problematic objects.
    /// Returns null if ClrMD is not available or inspection fails.
    /// </summary>
    private async Task<object?> ExpandObjectAsync(string address, string methodTable)
    {
        // Use lower depth for report expansion to keep JSON manageable
        const int reportMaxDepth = 3;
        const int reportMaxArrayElements = 5;
        const int reportMaxStringLength = 256;
        
        // ClrMD is the only implementation - no SOS dumpobj fallback (it can crash LLDB)
        if (_clrMdAnalyzer?.IsOpen != true)
        {
            _logger?.LogDebug("ClrMD not available for object expansion at {Address}", address);
            return null;
        }
        
        // Clean address - may contain type info like "0x1234 (System.String)"
        var cleanAddress = ExtractHexAddress(address);
        if (string.IsNullOrEmpty(cleanAddress))
        {
            _logger?.LogDebug("Invalid address format for expansion: {Address}", address);
            return null;
        }
        
        try
        {
            if (!ulong.TryParse(cleanAddress, System.Globalization.NumberStyles.HexNumber, null, out var addressValue))
            {
                _logger?.LogDebug("Failed to parse address as hex: {Address}", cleanAddress);
                return null;
            }
            
            var clrMdResult = _clrMdAnalyzer.InspectObject(addressValue, methodTable: null, maxDepth: reportMaxDepth, maxArrayElements: reportMaxArrayElements, maxStringLength: reportMaxStringLength);
            
            if (clrMdResult != null && clrMdResult.Error == null)
            {
                _logger?.LogDebug("Object expansion at {Address} succeeded using ClrMD", address);
                return clrMdResult;
            }
            
            // ClrMD returned an error - object may be invalid/corrupted
            _logger?.LogDebug("ClrMD object expansion skipped for {Address}: {Error}", 
                address, clrMdResult?.Error ?? "null result");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "ClrMD object expansion failed for {Address}", address);
            return null;
        }
    }
    
    /// <summary>
    /// Checks if a variable is a ByRef type that needs dereferencing.
    /// </summary>
    private static bool IsByRefVariable(LocalVariable variable)
    {
        if (variable.Type?.EndsWith("(ByRef)", StringComparison.OrdinalIgnoreCase) != true)
            return false;
        
        if (!variable.HasData)
            return false;
        
        // The ByRef address can be in different places:
        // 1. Location is a hex address (e.g., "0x0000FFFFEFCB76E8") - this IS the ByRef address
        // 2. Location is "[CLR reg]" and Value has the address
        return GetByRefAddress(variable) != null;
    }
    
    /// <summary>
    /// Gets the ByRef address from a variable.
    /// The address can be in Location (if it's a hex) or in Value.
    /// </summary>
    private static string? GetByRefAddress(LocalVariable variable)
    {
        // First, check if Location is a hex address
        if (!string.IsNullOrEmpty(variable.Location) &&
            variable.Location.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return variable.Location;
        }
        
        // Otherwise, check if Value is a hex address
        var valueStr = variable.Value?.ToString();
        if (!string.IsNullOrEmpty(valueStr) &&
            valueStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            valueStr != "0x00000000" && valueStr != "0x0000000000000000")
        {
            return valueStr;
        }
        
        return null;
    }
    
    /// <summary>
    /// Resolves ByRef variables by dereferencing their addresses to get the actual values.
    /// Only dereferences reference types - value types (structs) don't have a pointer to dereference.
    /// </summary>
    private async Task ResolveByRefVariablesAsync(List<LocalVariable> byRefVariables)
    {
        foreach (var variable in byRefVariables)
        {
            try
            {
                var byRefAddress = GetByRefAddress(variable);
                if (string.IsNullOrEmpty(byRefAddress))
                    continue;
                
                // Get the base type (without ByRef modifier)
                var baseType = GetBaseTypeFromByRef(variable.Type);
                
                // For VALUE TYPES (structs), the ByRef address points directly to the struct data
                // We can't dereference it - the data IS at that address
                // Mark it as a value type ByRef and store the address
                if (IsValueTypeByRef(baseType))
                {
                    variable.ByRefAddress = byRefAddress;
                    // For primitive value types, we could read the value directly
                    if (IsPrimitiveValueType(baseType))
                    {
                        var rawValue = await DereferencePointerAsync(byRefAddress);
                        if (!string.IsNullOrEmpty(rawValue))
                        {
                            variable.Value = ConvertHexToValue(rawValue, baseType);
                            variable.RawValue = rawValue;
                        }
                    }
                    // For complex structs, just note the ByRef address (can use !dumpvc to inspect)
                    continue;
                }
                
                // For REFERENCE TYPES, dereference to get the actual object pointer
                var resolvedAddress = await DereferencePointerAsync(byRefAddress);
                
                if (!string.IsNullOrEmpty(resolvedAddress) && 
                    resolvedAddress != "0x00000000" && 
                    resolvedAddress != "0x0000000000000000")
                {
                    // Store both addresses
                    variable.ByRefAddress = byRefAddress;
                    variable.ResolvedAddress = resolvedAddress;
                    
                    // Update value to use the resolved address for further processing (like string resolution)
                    variable.Value = resolvedAddress;
                }
            }
            catch
            {
                // Keep original value on any dereferencing error
            }
        }
    }
    
    /// <summary>
    /// Checks if a type is a value type that should NOT be dereferenced.
    /// This includes structs, enums, and all primitive value types.
    /// </summary>
    private static bool IsValueTypeByRef(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return false;
        
        // Primitive value types
        if (IsPrimitiveValueType(typeName))
            return true;
        
        // Known reference types that should be dereferenced
        var referenceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.String", "String", "string",
            "System.Object", "Object", "object",
            "System.Exception",
            "System.Array"
        };
        
        var baseName = typeName.Split('<', '`')[0];
        var shortName = baseName.Split('.').Last();
        
        // If it's a known reference type, it's NOT a value type
        if (referenceTypes.Contains(baseName) || referenceTypes.Contains(shortName))
            return false;
        
        // Arrays are reference types
        if (typeName.EndsWith("[]"))
            return false;
        
        // Common .NET struct types
        var knownStructs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DateTime", "DateTimeOffset", "TimeSpan", "Guid",
            "Decimal", "TimeOnly", "DateOnly",
            "Span", "ReadOnlySpan", "Memory", "ReadOnlyMemory",
            "Nullable", "ValueTuple",
            "CancellationToken", "CancellationTokenRegistration",
            "KeyValuePair", "ArraySegment",
            // Runtime internal structs
            "StackFrameIterator", "ExInfo", "EHClauseIterator",
            "RegDisplay", "REGDISPLAY", "ExceptionRecord"
        };
        
        if (knownStructs.Contains(shortName))
            return true;
        
        // If type doesn't start with "System." and isn't a known reference type,
        // assume it might be a custom struct (conservative approach)
        // Actually, let's be more permissive - assume unknown types are reference types
        // since most types in .NET are classes
        return false;
    }
    
    /// <summary>
    /// Checks if a type is a primitive value type (int, bool, etc.)
    /// </summary>
    private static bool IsPrimitiveValueType(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return false;
        
        var primitives = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Int16", "Int32", "Int64", "UInt16", "UInt32", "UInt64",
            "Byte", "SByte", "Boolean", "Char", "Single", "Double",
            "IntPtr", "UIntPtr",
            "short", "int", "long", "ushort", "uint", "ulong",
            "byte", "sbyte", "bool", "char", "float", "double"
        };
        
        var baseName = typeName.Split('<', '`', '.').Last();
        return primitives.Contains(baseName);
    }
    
    /// <summary>
    /// Dereferences a pointer address to get the value at that memory location.
    /// </summary>
    private async Task<string?> DereferencePointerAsync(string address)
    {
        try
        {
            // Use memory read command to dereference the pointer
            // LLDB: memory read -s8 -fx -c1 <address> reads one 8-byte (64-bit) value
            // -s8: size = 8 bytes (64-bit pointer)
            // -fx: format as hex
            // -c1: count = 1 value
            var output = await ExecuteCommandAsync($"memory read -s8 -fx -c1 {address}");
            
            // Parse the output to extract the dereferenced value
            // Format: "0x0000ffffefcb7a70: 0x0000f714efe57360"
            var match = Regex.Match(output, @":\s*(0x[0-9a-fA-F]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
            
            // Alternative format: just the value on a line
            match = Regex.Match(output, @"(0x[0-9a-fA-F]+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        catch
        {
            // Dereferencing failed
        }
        
        return null;
    }
    
    /// <summary>
    /// Gets the base type from a ByRef type name.
    /// E.g., "System.String(ByRef)" -> "System.String"
    /// </summary>
    private static string GetBaseTypeFromByRef(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return string.Empty;
        
        if (typeName.EndsWith("(ByRef)", StringComparison.OrdinalIgnoreCase))
        {
            return typeName.Substring(0, typeName.Length - 7);
        }
        
        return typeName;
    }
    
    /// <summary>
    /// Checks if a type is a value type (not a reference type).
    /// </summary>
    private static bool IsValueType(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return false;
        
        var valueTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Int16", "Int32", "Int64", "UInt16", "UInt32", "UInt64",
            "Byte", "SByte", "Boolean", "Char", "Single", "Double", "Decimal",
            "IntPtr", "UIntPtr", "Void",
            "short", "int", "long", "ushort", "uint", "ulong",
            "byte", "sbyte", "bool", "char", "float", "double", "decimal",
            "DateTime", "DateTimeOffset", "TimeSpan", "Guid"
        };
        
        var baseName = typeName.Split('<', '`', '.').Last();
        return valueTypes.Contains(baseName);
    }
    
    /// <summary>
    /// Converts a hex value to a meaningful representation based on type.
    /// </summary>
    private static string ConvertHexToValue(string hexValue, string? typeName)
    {
        if (string.IsNullOrEmpty(typeName) || !hexValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return hexValue;
        
        try
        {
            if (!long.TryParse(hexValue.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var numValue))
                return hexValue;
            
            var baseName = typeName.Split('<', '`', '.').Last().ToLowerInvariant();
            
            return baseName switch
            {
                "int32" or "int" => ((int)numValue).ToString(),
                "int16" or "short" => ((short)numValue).ToString(),
                "int64" or "long" => numValue.ToString(),
                "sbyte" => ((sbyte)numValue).ToString(),
                "uint32" or "uint" => ((uint)numValue).ToString(),
                "uint16" or "ushort" => ((ushort)numValue).ToString(),
                "uint64" or "ulong" => ((ulong)numValue).ToString(),
                "byte" => ((byte)numValue).ToString(),
                "boolean" or "bool" => numValue != 0 ? "true" : "false",
                "char" => numValue is >= 32 and <= 126 ? $"'{(char)numValue}'" : $"'\\u{numValue:X4}'",
                "single" or "float" => BitConverter.Int32BitsToSingle((int)numValue).ToString("G"),
                "double" => BitConverter.Int64BitsToDouble(numValue).ToString("G"),
                _ => hexValue
            };
        }
        catch
        {
            return hexValue;
        }
    }
    
    /// <summary>
    /// Collects string addresses from variables, including resolved ByRef strings.
    /// </summary>
    private static void CollectStringAddresses(List<LocalVariable>? variables, HashSet<string> addresses)
    {
        if (variables == null) return;
        
        foreach (var variable in variables)
        {
            // Direct string parameter
            if (IsStringParameter(variable))
            {
                var valueStr = variable.Value?.ToString();
                if (!string.IsNullOrEmpty(valueStr))
                {
                    addresses.Add(valueStr);
                }
            }
            // ByRef string - use the resolved address
            else if (variable.Type?.StartsWith("System.String(ByRef)", StringComparison.OrdinalIgnoreCase) == true &&
                     !string.IsNullOrEmpty(variable.ResolvedAddress) &&
                     variable.ResolvedAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                addresses.Add(variable.ResolvedAddress);
            }
        }
    }
    
    /// <summary>
    /// Checks if a variable is a string that can be resolved.
    /// </summary>
    private static bool IsStringParameter(LocalVariable variable)
    {
        var valueStr = variable.Value?.ToString();
        return variable.Type?.Equals("System.String", StringComparison.OrdinalIgnoreCase) == true &&
               variable.HasData &&
               !string.IsNullOrEmpty(valueStr) &&
               valueStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Applies resolved string values to a list of variables.
    /// Handles both direct strings and ByRef strings.
    /// </summary>
    private static void ApplyStringValues(List<LocalVariable>? variables, Dictionary<string, string> stringValues)
    {
        if (variables == null) return;
        
        foreach (var variable in variables)
        {
            var valueStr = variable.Value?.ToString();
            
            // Direct string
            if (variable.Type?.Equals("System.String", StringComparison.OrdinalIgnoreCase) == true &&
                variable.HasData &&
                !string.IsNullOrEmpty(valueStr) &&
                stringValues.TryGetValue(valueStr, out var stringValue))
            {
                variable.RawValue = valueStr;
                variable.Value = stringValue;
            }
            // ByRef string - use the resolved address to look up the string value
            else if (variable.Type?.StartsWith("System.String(ByRef)", StringComparison.OrdinalIgnoreCase) == true &&
                     !string.IsNullOrEmpty(variable.ResolvedAddress) &&
                     stringValues.TryGetValue(variable.ResolvedAddress, out var byRefStringValue))
            {
                variable.Value = byRefStringValue;
            }
        }
    }
    
    /// <summary>
    /// Enhances a primitive value by converting hex to meaningful representation.
    /// </summary>
    private static void EnhancePrimitiveValue(LocalVariable variable)
    {
        var valueStr = variable.Value?.ToString();
        if (!variable.HasData || string.IsNullOrEmpty(valueStr) || string.IsNullOrEmpty(variable.Type))
            return;
        
        // Skip if value doesn't look like a hex number
        if (!valueStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return;
        
        var hexValue = valueStr;
        var typeName = variable.Type.Split('<', '`')[0]; // Handle generics
        
        try
        {
            // Parse the hex value
            if (!long.TryParse(hexValue.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var numValue))
                return;
            
            string? convertedValue = typeName.ToLowerInvariant() switch
            {
                // Signed integers
                "int32" or "int" => ((int)numValue).ToString(),
                "int16" or "short" => ((short)numValue).ToString(),
                "int64" or "long" => numValue.ToString(),
                "sbyte" => ((sbyte)numValue).ToString(),
                
                // Unsigned integers  
                "uint32" or "uint" => ((uint)numValue).ToString(),
                "uint16" or "ushort" => ((ushort)numValue).ToString(),
                "uint64" or "ulong" => ((ulong)numValue).ToString(),
                "byte" => ((byte)numValue).ToString(),
                
                // Boolean
                "boolean" or "bool" => numValue != 0 ? "true" : "false",
                
                // Char
                "char" => numValue is >= 32 and <= 126 
                    ? $"'{(char)numValue}'" 
                    : $"'\\u{numValue:X4}'",
                
                // Floating point (interpret bits)
                "single" or "float" => BitConverter.Int32BitsToSingle((int)numValue).ToString("G"),
                "double" => BitConverter.Int64BitsToDouble(numValue).ToString("G"),
                
                // Keep as hex for pointers and unknowns
                "intptr" or "uintptr" or "nint" or "nuint" => null,
                
                _ => null
            };
            
            if (convertedValue != null)
            {
                variable.RawValue = hexValue;
                variable.Value = convertedValue;
            }
        }
        catch
        {
            // Keep original value on any conversion error
        }
    }
    
    /// <summary>
    /// Resolves string values by calling ClrMD InspectObject on each address.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveStringValuesAsync(
        HashSet<string> addresses, 
        Dictionary<string, string>? rawCommands = null)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var address in addresses)
        {
            try
            {
                var output = await DumpObjectViaClrMdAsync(address);
                
                // Store the command output
                if (rawCommands != null)
                {
                    rawCommands[$"ClrMD:InspectObject({address})"] = output;
                }
                
                // Parse the string value from ClrMD output
                var stringValue = ExtractStringFromDumpObj(output);
                
                if (!string.IsNullOrEmpty(stringValue))
                {
                    results[address] = stringValue;
                }
            }
            catch
            {
                // Skip strings that fail to resolve
            }
        }
        
        return results;
    }
    
    /// <summary>
    /// Extracts the string value from ClrMD InspectObject output (formatted like dumpobj).
    /// </summary>
    private static string? ExtractStringFromDumpObj(string output)
    {
        if (string.IsNullOrEmpty(output))
            return null;
        
        const int maxStringLength = 1024;
        
        // Look for "String:" line which contains the actual value
        var stringMatch = Regex.Match(output, @"String:\s*(.+)$", RegexOptions.Multiline);
        if (stringMatch.Success)
        {
            var value = stringMatch.Groups[1].Value.Trim();
            // Truncate very long strings
            if (value.Length > maxStringLength)
            {
                value = value.Substring(0, maxStringLength - 3) + "...";
            }
            return value; // Return raw string without quotes
        }
        
        // Alternative: look for content after the header info
        // Format varies between WinDbg and LLDB
        var contentMatch = Regex.Match(output, @"m_firstChar:\s*[^\n]+\n(.+)", RegexOptions.Singleline);
        if (contentMatch.Success)
        {
            var value = contentMatch.Groups[1].Value.Trim();
            if (value.Length > maxStringLength)
            {
                value = value.Substring(0, maxStringLength - 3) + "...";
            }
            return value; // Return raw string without quotes
        }
        
        return null;
    }

    /// <summary>
    /// Parses the analyzeoom command output to detect OOM conditions.
    /// </summary>
    private static void ParseAnalyzeOom(string output, CrashAnalysisResult result)
    {
        var oomInfo = new OomAnalysisInfo();

        // Check if no OOM was detected
        if (output.Contains("no managed OOM", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("There was no", StringComparison.OrdinalIgnoreCase))
        {
            oomInfo.Detected = false;
            oomInfo.Message = "No managed OOM due to allocations on the GC heap";
        }
        else if (output.Contains("OOM", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("OutOfMemory", StringComparison.OrdinalIgnoreCase))
        {
            oomInfo.Detected = true;

            // Try to parse the reason
            var reasonMatch = Regex.Match(output, @"Reason:\s*(.+)", RegexOptions.IgnoreCase);
            if (reasonMatch.Success)
            {
                oomInfo.Reason = reasonMatch.Groups[1].Value.Trim();
            }

            // Try to parse generation
            var genMatch = Regex.Match(output, @"Gen(?:eration)?:\s*(\d+)", RegexOptions.IgnoreCase);
            if (genMatch.Success && int.TryParse(genMatch.Groups[1].Value, out var gen))
            {
                oomInfo.Generation = gen;
            }

            // Try to parse allocation size
            var sizeMatch = Regex.Match(output, @"Allocation\s+(?:Size|Request):\s*([0-9,]+)", RegexOptions.IgnoreCase);
            if (sizeMatch.Success)
            {
                var sizeStr = sizeMatch.Groups[1].Value.Replace(",", "");
                if (long.TryParse(sizeStr, out var size))
                {
                    oomInfo.AllocationSize = size;
                }
            }

            // Try to parse LOH size
            var lohMatch = Regex.Match(output, @"LOH\s+(?:Size|Usage):\s*([0-9,]+)", RegexOptions.IgnoreCase);
            if (lohMatch.Success)
            {
                var lohStr = lohMatch.Groups[1].Value.Replace(",", "");
                if (long.TryParse(lohStr, out var loh))
                {
                    oomInfo.LohSize = loh;
                }
            }

            // Capture the full message
            oomInfo.Message = output.Trim();

            // Add to recommendations
            var rec = "OOM detected: Consider increasing memory limits or investigating memory leaks";
            result.Summary?.Recommendations?.Add(rec);
        }
        else
        {
            // Unknown format, just store it
            oomInfo.Detected = false;
            oomInfo.Message = output.Trim();
        }

        // Set in new structure
        result.Memory ??= new MemoryInfo();
        result.Memory.Oom = oomInfo;
        
        // Also set in old structure during migration
    }

    /// <summary>
    /// Parses the crashinfo command output to get crash diagnostic information.
    /// </summary>
    private static void ParseCrashInfo(string output, CrashAnalysisResult result)
    {
        var crashInfo = new CrashDiagnosticInfo();

        // Check if no crash info was found
        if (output.Contains("No crash info", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("No exception record", StringComparison.OrdinalIgnoreCase) ||
            (output.Contains("No", StringComparison.OrdinalIgnoreCase) && 
             output.Contains("display", StringComparison.OrdinalIgnoreCase)))
        {
            crashInfo.HasInfo = false;
            crashInfo.Message = "No crash info available";
        }
        else
        {
            crashInfo.HasInfo = true;

            // Try to parse signal info (Linux)
            var signalMatch = Regex.Match(output, @"Signal:\s*(\d+)\s*\(([^)]+)\)", RegexOptions.IgnoreCase);
            if (signalMatch.Success)
            {
                if (int.TryParse(signalMatch.Groups[1].Value, out var sig))
                {
                    crashInfo.Signal = sig;
                }
                crashInfo.SignalName = signalMatch.Groups[2].Value.Trim();
            }
            else
            {
                // Try simpler signal format
                var simpleSignalMatch = Regex.Match(output, @"(SIG[A-Z]+)", RegexOptions.IgnoreCase);
                if (simpleSignalMatch.Success)
                {
                    crashInfo.SignalName = simpleSignalMatch.Groups[1].Value.ToUpperInvariant();
                }
            }

            // Try to parse crash reason
            var reasonMatch = Regex.Match(output, @"(?:Crash\s+)?Reason:\s*(.+)", RegexOptions.IgnoreCase);
            if (reasonMatch.Success)
            {
                crashInfo.CrashReason = reasonMatch.Groups[1].Value.Trim();
            }

            // Try to parse faulting address
            var faultMatch = Regex.Match(output, @"(?:Fault(?:ing)?|Address):\s*([0-9a-fA-Fx]+)", RegexOptions.IgnoreCase);
            if (faultMatch.Success)
            {
                crashInfo.FaultingAddress = faultMatch.Groups[1].Value;
            }

            // Try to parse exception record (Windows)
            var exRecMatch = Regex.Match(output, @"Exception\s+Record:\s*([0-9a-fA-Fx]+)", RegexOptions.IgnoreCase);
            if (exRecMatch.Success)
            {
                crashInfo.ExceptionRecord = exRecMatch.Groups[1].Value;
            }

            // Try to parse crashing thread
            var threadMatch = Regex.Match(output, @"(?:Crash(?:ing)?|Fault(?:ing)?)\s+Thread:\s*(\d+)", RegexOptions.IgnoreCase);
            if (threadMatch.Success)
            {
                crashInfo.CrashingThread = threadMatch.Groups[1].Value;
            }

            // Store full message
            crashInfo.Message = output.Trim();

            // Update crash type in result if we have signal info
            if (!string.IsNullOrEmpty(crashInfo.SignalName))
            {
                result.Summary!.CrashType = crashInfo.SignalName switch
                {
                    "SIGSEGV" => "Segmentation Fault",
                    "SIGABRT" => "Abort Signal",
                    "SIGFPE" => "Floating Point Exception",
                    "SIGILL" => "Illegal Instruction",
                    "SIGBUS" => "Bus Error",
                    _ => $"Signal: {crashInfo.SignalName}"
                };
            }
        }

        // Set in environment (crashInfo is environment/context info)
        result.Environment ??= new EnvironmentInfo();
        result.Environment.CrashInfo = crashInfo;
    }

    /// <summary>
    /// Parses assembly version information from !dumpdomain output.
    /// This helps diagnose version mismatch issues like MissingMethodException.
    /// </summary>
    /// <param name="dumpDomainOutput">Output from !dumpdomain command.</param>
    /// <param name="result">The crash analysis result to populate.</param>
    protected void ParseAssemblyVersions(string dumpDomainOutput, CrashAnalysisResult result)
    {
        // For native dumps or errors, skip
        if (IsSosErrorOutput(dumpDomainOutput)) return;
        
        var assemblies = new List<AssemblyVersionInfo>();
        
        // Parse assembly entries from dumpdomain output
        // Format varies between .NET versions:
        // 
        // Format 1 (with brackets):
        //   Assembly:   0000f7558b725348 [Microsoft.Extensions.FileProviders.Physical]
        //   ClassLoader:        0000f7558b725400
        //   Module Name    0000f7558b7254c8  /path/to/assembly.dll
        //
        // Format 2 (path directly on Assembly line):
        //   Assembly: 0000abcd1234 /path/to/MyAssembly.dll
        //   Module:   0000abcd5678 /path/to/MyAssembly.dll
        
        var lines = dumpDomainOutput.Split('\n');
        AssemblyVersionInfo? currentAssembly = null;
        
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // Match Assembly line with brackets: Assembly: 0000f7558b725348 [AssemblyName]
            var assemblyMatch = Regex.Match(line, @"Assembly:\s+([0-9a-fA-Fx]+)\s+\[([^\]]+)\]");
            if (assemblyMatch.Success)
            {
                // Save previous assembly if exists
                if (currentAssembly != null && !string.IsNullOrWhiteSpace(currentAssembly.Name))
                {
                    assemblies.Add(currentAssembly);
                }
                
                var nameOrPath = assemblyMatch.Groups[2].Value.Trim();
                var (assemblyName, assemblyPath) = ExtractAssemblyNameAndPath(nameOrPath);
                
                // Skip empty or invalid names
                if (string.IsNullOrWhiteSpace(assemblyName))
                {
                    currentAssembly = null;
                    continue;
                }
                
                currentAssembly = new AssemblyVersionInfo
                {
                    Name = assemblyName,
                    AssemblyAddress = assemblyMatch.Groups[1].Value,
                    Path = assemblyPath
                };
                continue;
            }
            
            // Match simpler Assembly format: Assembly: 0000abcd1234 /path/to/assembly.dll or Assembly: 0000abcd1234 MyAssembly
            var simpleAssemblyMatch = Regex.Match(line, @"^Assembly:\s+([0-9a-fA-Fx]+)\s+(.+)$");
            if (simpleAssemblyMatch.Success)
            {
                if (currentAssembly != null && !string.IsNullOrWhiteSpace(currentAssembly.Name))
                {
                    assemblies.Add(currentAssembly);
                }
                
                var nameOrPath = simpleAssemblyMatch.Groups[2].Value.Trim();
                var (assemblyName, assemblyPath) = ExtractAssemblyNameAndPath(nameOrPath);
                
                // Skip empty or invalid names
                if (string.IsNullOrWhiteSpace(assemblyName))
                {
                    currentAssembly = null;
                    continue;
                }
                
                currentAssembly = new AssemblyVersionInfo
                {
                    Name = assemblyName,
                    AssemblyAddress = simpleAssemblyMatch.Groups[1].Value,
                    Path = assemblyPath
                };
                continue;
            }
            
            // If we have a current assembly, look for additional info
            if (currentAssembly != null)
            {
                // Module line with path: Module Name 0000f7558b7254c8 /path/to/assembly.dll
                // or: Module: 0000abcd /path/to/file.dll
                var moduleMatch = Regex.Match(line, @"(?:Module(?:\s+Name)?:?\s+)?([0-9a-fA-Fx]+)\s+(.+\.dll)", RegexOptions.IgnoreCase);
                if (moduleMatch.Success)
                {
                    var path = moduleMatch.Groups[2].Value.Trim();
                    if (path.Contains('/') || path.Contains('\\'))
                    {
                        // Only update path if we don't have one yet
                        if (string.IsNullOrEmpty(currentAssembly.Path))
                        {
                            currentAssembly.Path = path;
                        }
                        currentAssembly.ModuleId = moduleMatch.Groups[1].Value;
                    }
                }
                
                // Check for dynamic assembly indicator
                if (line.Contains("Dynamic", StringComparison.OrdinalIgnoreCase))
                {
                    currentAssembly.IsDynamic = true;
                }
            }
        }
        
        // Add the last assembly
        if (currentAssembly != null && !string.IsNullOrWhiteSpace(currentAssembly.Name))
        {
            assemblies.Add(currentAssembly);
        }
        
        // Sort by name for easier reading
        assemblies = assemblies
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .GroupBy(a => GetAssemblyDedupKey(a), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.Name)
            .ToList();
        
        if (assemblies.Count > 0)
        {
            // Set in new structure
            result.Assemblies = new AssembliesInfo
            {
                Count = assemblies.Count,
                Items = assemblies
            };
            
            // Also set in old structure during migration
        }
    }

    /// <summary>
    /// Produces a stable deduplication key for assembly entries in dumpdomain output.
    /// Prefer the resolved path when available; fall back to module id or name.
    /// </summary>
    /// <param name="assembly">The assembly entry.</param>
    /// <returns>A key suitable for deduplication.</returns>
    private static string GetAssemblyDedupKey(AssemblyVersionInfo assembly)
    {
        if (!string.IsNullOrWhiteSpace(assembly.Path))
            return assembly.Path;

        if (!string.IsNullOrWhiteSpace(assembly.ModuleId))
            return $"{assembly.Name}|{assembly.ModuleId}";

        return assembly.Name;
    }
    
    /// <summary>
    /// Extracts assembly name and path from a string that could be either a name or a path.
    /// </summary>
    /// <param name="nameOrPath">The string to parse (could be "MyAssembly", "/path/to/MyAssembly.dll", etc.)</param>
    /// <returns>A tuple of (assemblyName, path) where path is null if input wasn't a path.</returns>
    private static (string assemblyName, string? path) ExtractAssemblyNameAndPath(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
        {
            return (string.Empty, null);
        }
        
        // Check if this looks like a path (contains path separators or ends with .dll/.exe)
        var isPath = nameOrPath.Contains('/') || nameOrPath.Contains('\\') || 
                     nameOrPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                     nameOrPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        
        if (isPath)
        {
            // Extract just the assembly name from the path (without extension)
            var fileName = System.IO.Path.GetFileNameWithoutExtension(nameOrPath);
            return (fileName, nameOrPath);
        }
        
        // It's just a name
        return (nameOrPath, null);
    }

    /// <summary>
    /// Enriches assembly information by querying module details.
    /// This gets additional information like base address from loaded modules.
    /// </summary>
    /// <param name="result">The crash analysis result to enrich.</param>
    protected void EnrichAssemblyInfo(CrashAnalysisResult result)
    {
        // Use new structure, fall back to old
        var assemblies = result.Assemblies?.Items;
        if (assemblies == null || result.Modules == null) return;
        
        // Create a lookup by module name (case insensitive)
        var modulesByName = result.Modules
            .Where(m => !string.IsNullOrEmpty(m.Name))
            .GroupBy(m => System.IO.Path.GetFileName(m.Name).ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());
        
        foreach (var assembly in assemblies)
        {
            // Try to find matching module
            var assemblyFileName = !string.IsNullOrEmpty(assembly.Path) 
                ? System.IO.Path.GetFileName(assembly.Path).ToLowerInvariant()
                : $"{assembly.Name}.dll".ToLowerInvariant();
            
            if (modulesByName.TryGetValue(assemblyFileName, out var module))
            {
                // Copy info from module
                if (string.IsNullOrEmpty(assembly.BaseAddress))
                {
                    assembly.BaseAddress = module.BaseAddress;
                }
                if (string.IsNullOrEmpty(assembly.Path) && !string.IsNullOrEmpty(module.Name))
                {
                    assembly.Path = module.Name;
                }
                
                // Native image detection from module info
                if (module.Name?.Contains("native", StringComparison.OrdinalIgnoreCase) == true ||
                    module.Name?.Contains(".ni.", StringComparison.OrdinalIgnoreCase) == true)
                {
                    assembly.IsNativeImage = true;
                }
            }
        }
    }
    
    /// <summary>
    /// Enriches assembly information with version details by querying each module.
    /// Uses ClrMD exclusively (safe, won't crash LLDB).
    /// </summary>
    /// <param name="result">The crash analysis result to enrich.</param>
    protected Task EnrichAssemblyVersionsAsync(CrashAnalysisResult result)
    {
        // Use new structure, fall back to old
        var assemblies = result.Assemblies?.Items;
        if (assemblies == null) return Task.CompletedTask;
        
        // ClrMD required for module inspection
        if (_clrMdAnalyzer == null || !_clrMdAnalyzer.IsOpen)
        {
            _logger?.LogDebug("[DotNetCrashAnalyzer] ClrMD not available, skipping module enrichment");
            return Task.CompletedTask;
        }
        
        // Limit to non-dynamic assemblies with a ModuleId (up to 50 to avoid excessive processing)
        var assembliesToEnrich = assemblies
            .Where(a => !string.IsNullOrEmpty(a.ModuleId) && a.IsDynamic != true)
            .Take(50)
            .ToList();
        
        foreach (var assembly in assembliesToEnrich)
        {
            try
            {
                // Parse the module address
                var moduleId = assembly.ModuleId!.Trim();
                if (moduleId.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    moduleId = moduleId[2..];
                }
                
                if (!ulong.TryParse(moduleId, System.Globalization.NumberStyles.HexNumber, null, out var moduleAddr))
                {
                continue;
            }
            
                var moduleInfo = _clrMdAnalyzer.InspectModule(moduleAddr);
                if (moduleInfo == null || moduleInfo.Error != null)
            {
                continue;
            }
            
                // Store module info as raw command for debugging
                result.RawCommands![$"ClrMD:InspectModule({assembly.ModuleId})"] = 
                    System.Text.Json.JsonSerializer.Serialize(moduleInfo, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                
                // Update assembly info from ClrMD result
                if (!string.IsNullOrEmpty(moduleInfo.Version) && string.IsNullOrEmpty(assembly.AssemblyVersion))
                {
                    assembly.AssemblyVersion = moduleInfo.Version;
                }
                
                if (!string.IsNullOrEmpty(moduleInfo.ImageBase) && string.IsNullOrEmpty(assembly.BaseAddress))
                {
                    assembly.BaseAddress = moduleInfo.ImageBase;
                }
                
                if (!string.IsNullOrEmpty(moduleInfo.Name) && string.IsNullOrEmpty(assembly.Path))
                {
                    assembly.Path = moduleInfo.Name;
                }
            }
            catch
            {
                // Silently ignore failures - don't fail the whole analysis
            }
        }
        
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Enriches assembly info with ClrMD metadata from dump memory.
    /// This adds Company, Product, RepositoryUrl, CommitHash, etc. from assembly attributes.
    /// </summary>
    /// <param name="assemblies">The assemblies to enrich.</param>
    private void EnrichAssemblyMetadata(List<AssemblyVersionInfo> assemblies)
    {
        if (_clrMdAnalyzer == null || !_clrMdAnalyzer.IsOpen)
            return;
        
        try
        {
            // Get all modules with attributes in one pass
            // Use GroupBy + First to handle duplicate module names (same assembly loaded from different paths)
            var enrichedModules = _clrMdAnalyzer.GetAllModulesWithAttributes()
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            
            foreach (var assembly in assemblies)
            {
                if (!enrichedModules.TryGetValue(assembly.Name, out var moduleInfo))
                    continue;
                
                // Copy assembly version from metadata definition (if not already set by SOS)
                if (string.IsNullOrEmpty(assembly.AssemblyVersion) && 
                    !string.IsNullOrEmpty(moduleInfo.AssemblyVersion))
                {
                    assembly.AssemblyVersion = moduleInfo.AssemblyVersion;
                }
                
                foreach (var attr in moduleInfo.Attributes)
                {
                    MapAttributeToAssembly(assembly, attr);
                }

                // Only keep commit hashes when we can associate the assembly with a repository.
                if (ShouldExtractCommitHash(assembly))
                {
                    if (string.IsNullOrEmpty(assembly.CommitHash))
                    {
                        ExtractCommitHash(assembly, assembly.InformationalVersion);
                    }
                }
                else
                {
                    assembly.CommitHash = null;
                    assembly.SourceUrl = null;
                }
            }
        }
        catch (Exception)
        {
            // Silently ignore - enrichment is optional and should not fail analysis
        }
    }
    
    /// <summary>
    /// Maps a ClrMD assembly attribute to the appropriate AssemblyVersionInfo property.
    /// </summary>
    private static void MapAttributeToAssembly(AssemblyVersionInfo assembly, AssemblyAttributeInfo attr)
    {
        switch (attr.AttributeType)
        {
            case "System.Reflection.AssemblyCompanyAttribute":
                assembly.Company = attr.Value;
                break;
            case "System.Reflection.AssemblyProductAttribute":
                assembly.Product = attr.Value;
                break;
            case "System.Reflection.AssemblyCopyrightAttribute":
                assembly.Copyright = attr.Value;
                break;
            case "System.Reflection.AssemblyConfigurationAttribute":
                assembly.Configuration = attr.Value;
                break;
            case "System.Reflection.AssemblyTitleAttribute":
                assembly.Title = attr.Value;
                break;
            case "System.Reflection.AssemblyDescriptionAttribute":
                assembly.Description = attr.Value;
                break;
            case "System.Reflection.AssemblyInformationalVersionAttribute":
                assembly.InformationalVersion = attr.Value;
                break;
            case "System.Reflection.AssemblyMetadataAttribute":
                if (attr.Key == "RepositoryUrl")
                    assembly.RepositoryUrl = attr.Value;
                else if (!string.IsNullOrEmpty(attr.Key))
                {
                    assembly.CustomAttributes ??= new();
                    assembly.CustomAttributes[attr.Key] = attr.Value ?? "";
                }
                break;
            case "System.Runtime.Versioning.TargetFrameworkAttribute":
                assembly.TargetFramework = attr.Value;
                break;
            default:
                // Store other attributes
                var shortName = attr.AttributeType.Split('.').Last().Replace("Attribute", "");
                assembly.CustomAttributes ??= new();
                assembly.CustomAttributes[shortName] = attr.Value ?? "";
                break;
        }
    }

    /// <summary>
    /// Determines whether an assembly has enough repository context to treat its informational-version hash as a commit.
    /// </summary>
    /// <param name="assembly">The assembly metadata.</param>
    /// <returns><c>true</c> when a repository can be determined; otherwise <c>false</c>.</returns>
    internal static bool ShouldExtractCommitHash(AssemblyVersionInfo assembly)
    {
        if (!string.IsNullOrWhiteSpace(assembly.RepositoryUrl))
            return true;

        var sourceCommitUrl = assembly.CustomAttributes?.GetValueOrDefault("SourceCommitUrl");
        return !string.IsNullOrWhiteSpace(sourceCommitUrl);
    }
    
    /// <summary>
    /// Extracts commit hash from InformationalVersion (e.g., "1.0.0+abc123def456").
    /// </summary>
    private static void ExtractCommitHash(AssemblyVersionInfo assembly, string? infoVersion)
    {
        if (string.IsNullOrEmpty(infoVersion))
            return;
        
        var plusIndex = infoVersion.IndexOf('+');
        if (plusIndex > 0 && plusIndex < infoVersion.Length - 1)
        {
            var hash = infoVersion[(plusIndex + 1)..];
            if (hash.Length >= 7 && hash.All(c => char.IsAsciiHexDigit(c)))
            {
                assembly.CommitHash = hash;
            }
        }
    }
    
    /// <summary>
    /// Enriches assemblies with GitHub commit metadata (author, committer, message).
    /// Uses caching to avoid hitting GitHub API rate limits.
    /// </summary>
    /// <param name="result">The crash analysis result to enrich.</param>
    protected virtual async Task EnrichAssemblyGitHubInfoAsync(CrashAnalysisResult result)
    {
        // Use new structure, fall back to old
        var assemblies = result.Assemblies?.Items;
        if (assemblies == null || assemblies.Count == 0)
            return;
        
        // Check if GitHub API is enabled
        var apiEnabled = Environment.GetEnvironmentVariable("GITHUB_API_ENABLED");
        if (apiEnabled?.Equals("false", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }
        
        // Get cache directory - use symbol directory if available
        var cacheDir = GetGitHubCacheDirectory();
        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        
        using var resolver = new GitHubCommitResolver(cacheDir, githubToken);
        
        // Filter assemblies with commit hashes
        var assembliesWithCommits = assemblies
            .Where(a => !string.IsNullOrEmpty(a.CommitHash))
            .ToList();
        
        if (assembliesWithCommits.Count == 0)
            return;
        
        foreach (var assembly in assembliesWithCommits)
        {
            try
            {
                // Resolve source URL
                var sourceUrl = resolver.ResolveSourceUrl(assembly);
                if (sourceUrl != null)
                {
                    assembly.SourceUrl = sourceUrl;
                }
                
                // Extract owner/repo for API call
                var ownerRepo = GitHubCommitResolver.ExtractGitHubOwnerRepo(assembly.RepositoryUrl);
                if (ownerRepo == null)
                {
                    // Try from SourceCommitUrl
                    var sourceCommitUrl = assembly.CustomAttributes?.GetValueOrDefault("SourceCommitUrl");
                    ownerRepo = GitHubCommitResolver.ExtractGitHubOwnerRepo(sourceCommitUrl);
                }
                
                // Fetch commit metadata
                if (ownerRepo != null && assembly.CommitHash != null)
                {
                    var commitInfo = await resolver.FetchCommitInfoAsync(ownerRepo, assembly.CommitHash);
                    if (commitInfo != null)
                    {
                        assembly.AuthorName = commitInfo.AuthorName;
                        assembly.AuthorDate = commitInfo.AuthorDate;
                        assembly.CommitterName = commitInfo.CommitterName;
                        assembly.CommitterDate = commitInfo.CommitterDate;
                        assembly.CommitMessage = commitInfo.Message;
                    }
                }
            }
            catch
            {
                // Continue with next assembly - GitHub enrichment is optional
            }
        }
        
        // Cache is saved automatically when resolver is disposed
    }
    
    /// <summary>
    /// Gets the cache directory for GitHub commit data.
    /// Uses the dump storage path to ensure cache persists with dumps.
    /// </summary>
    private static string GetGitHubCacheDirectory()
    {
        var dumpStoragePath = Configuration.EnvironmentConfig.GetDumpStoragePath();
        return Path.Combine(dumpStoragePath, ".github_cache");
    }

    /// <summary>
    /// Attempts to download and load Datadog.Trace symbols from Azure Pipelines.
    /// This is called early in analysis to ensure best stack traces.
    /// Non-fatal: failures are logged but don't stop analysis.
    /// </summary>
    /// <param name="result">The crash analysis result with platform info.</param>
    /// <param name="symbolsOutputDirectory">Optional output directory, defaults to dump symbols folder.</param>
    private async Task TryDownloadDatadogSymbolsAsync(CrashAnalysisResult result, string? symbolsOutputDirectory)
    {
        // Check if feature is enabled
        if (!DatadogTraceSymbolsConfig.IsEnabled())
        {
            _logger?.LogDebug("Datadog symbol download is disabled");
            return;
        }

        // Check if we have platform info and ClrMD analyzer
        var platform = result.Environment?.Platform;
        if (platform == null || _clrMdAnalyzer == null || !_clrMdAnalyzer.IsOpen)
        {
            _logger?.LogDebug("Skipping Datadog symbol download: platform={Platform}, clrMdOpen={ClrMdOpen}",
                platform?.Os ?? "null", _clrMdAnalyzer?.IsOpen ?? false);
            return;
        }

        _logger?.LogInformation("Starting Datadog symbol download for {Os}/{Arch}{Alpine}",
            platform.Os, platform.Architecture, platform.IsAlpine == true ? " (Alpine)" : "");

        try
        {
            // Create service and prepare symbols with logger
            var symbolService = new DatadogSymbolService(_clrMdAnalyzer, _logger);

            // Default output directory to dump storage
            var outputDir = symbolsOutputDirectory;
            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = Configuration.EnvironmentConfig.GetDumpStoragePath();
            }

            var prepResult = await symbolService.PrepareSymbolsAsync(
                platform,
                outputDir,
                cmd => _debuggerManager.ExecuteCommand(cmd));

            // Store result in analysis for reference
            if (prepResult.Success && prepResult.DatadogAssemblies.Count > 0)
            {
                _logger?.LogInformation("Datadog symbols prepared: {Message}", prepResult.Message);

                // Add metadata to the result indicating symbols were loaded
                result.RawCommands ??= new Dictionary<string, string>();
                result.RawCommands["__datadog_symbols_status"] = prepResult.Message ?? "Datadog symbols loaded";

                // Add build URL if available
                if (prepResult.DownloadResult?.BuildUrl != null)
                {
                    result.RawCommands["__datadog_build_url"] = prepResult.DownloadResult.BuildUrl;
                }

                // Mark Datadog assemblies with symbol download info
                EnrichDatadogAssembliesWithSymbolInfo(result, prepResult);
            }
            else if (!string.IsNullOrEmpty(prepResult.Message))
            {
                _logger?.LogDebug("Datadog symbol preparation: {Message}", prepResult.Message);
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail - symbol download is optional
            _logger?.LogWarning(ex, "Failed to download Datadog symbols (continuing without)");
        }
    }

    /// <summary>
    /// Enriches Datadog assemblies in the result with Azure Pipelines build and symbol info.
    /// </summary>
    /// <param name="result">The crash analysis result containing assemblies.</param>
    /// <param name="prepResult">The symbol preparation result with build info.</param>
    private static void EnrichDatadogAssembliesWithSymbolInfo(
        CrashAnalysisResult result,
        DatadogSymbolPreparationResult prepResult)
    {
        var assemblies = result.Assemblies?.Items;
        if (assemblies == null || assemblies.Count == 0)
            return;

        // Get build info from download result
        var downloadResult = prepResult.DownloadResult;
        var buildId = downloadResult?.BuildId;
        var buildNumber = downloadResult?.BuildNumber;
        var buildUrl = downloadResult?.BuildUrl;
        var symbolDirectory = downloadResult?.MergeResult?.SymbolDirectory;

        // Get the set of Datadog assembly names that were found
        var datadogAssemblyNames = new HashSet<string>(
            prepResult.DatadogAssemblies.Select(a => a.Name),
            StringComparer.OrdinalIgnoreCase);

        // Enrich matching assemblies
        foreach (var assembly in assemblies)
        {
            if (!datadogAssemblyNames.Contains(assembly.Name))
                continue;

            assembly.AzurePipelinesBuildId = buildId;
            assembly.AzurePipelinesBuildNumber = buildNumber;
            assembly.AzurePipelinesBuildUrl = buildUrl;
            assembly.SymbolsDownloaded = prepResult.SymbolsLoaded;
            assembly.SymbolsDirectory = symbolDirectory;
        }
    }
    
    /// <summary>
    /// Enriches thread info with ClrMD data (blocking objects, stack usage, etc.).
    /// </summary>
    private void EnrichThreadsWithClrMdInfo(List<ThreadInfo> threads)
    {
        if (_clrMdAnalyzer == null) return;
        
        var clrMdInfo = _clrMdAnalyzer.GetEnhancedThreadInfo();
        
        foreach (var thread in threads)
        {
            // Match by OS thread ID with robust parsing
            if (!string.IsNullOrEmpty(thread.OsThreadId))
            {
                try
                {
                    uint osId;
                    var osIdStr = thread.OsThreadId.Trim();
                    
                    // Try hex format (0x1234 or just 1234 in hex)
                    if (osIdStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        osId = Convert.ToUInt32(osIdStr[2..], 16);
                    }
                    else if (uint.TryParse(osIdStr, System.Globalization.NumberStyles.HexNumber, 
                        System.Globalization.CultureInfo.InvariantCulture, out var hexId))
                    {
                        osId = hexId;
                    }
                    else if (uint.TryParse(osIdStr, out var decId))
                    {
                        osId = decId;
                    }
                    else
                    {
                        continue; // Skip if can't parse
                    }
                    
                    if (clrMdInfo.TryGetValue(osId, out var info))
                    {
                        thread.ClrMdThreadInfo = info;
                    }
                }
                catch
                {
                    // Skip thread if OS ID parsing fails
                }
            }
        }
    }
    
    /// <summary>
    /// Analyzes type/method resolution for MissingMethodException, TypeLoadException, etc.
    /// Shows what methods actually exist on the type vs what was expected.
    /// </summary>
    /// <param name="result">The crash analysis result to populate.</param>
    protected async Task AnalyzeTypeResolutionAsync(CrashAnalysisResult result)
    {
        // Use new structure for exception info
        var exceptionType = result.Exception?.Type;
        var exceptionMessage = result.Exception?.Message;
        
        // Only analyze for resolution-related exceptions
        if (string.IsNullOrEmpty(exceptionType) || string.IsNullOrEmpty(exceptionMessage)) return;
        
        var isResolutionException = 
            exceptionType.Contains("MissingMethodException") ||
            exceptionType.Contains("MissingFieldException") ||
            exceptionType.Contains("MissingMemberException") ||
            exceptionType.Contains("TypeLoadException") ||
            exceptionType.Contains("FileNotFoundException") ||
            exceptionType.Contains("TypeInitializationException") ||
            exceptionType.Contains("BadImageFormatException") ||
            exceptionType.Contains("FileLoadException");
        
        if (!isResolutionException) return;
        
        string? typeName = null;
        try
        {
            var analysis = new TypeResolutionAnalysis();
            
            // Parse the type and member name from the exception message
            // Format: "Method not found: 'Boolean System.Collections.Concurrent.ConcurrentDictionary`2.TryGetValue(!0, !1 ByRef)'"
            // or: "Could not load type 'TypeName' from assembly 'AssemblyName'"
            string? memberName, signature;
            (typeName, memberName, signature) = ParseExceptionForTypeAndMember(exceptionType, exceptionMessage);
            
            if (string.IsNullOrEmpty(typeName))
            {
                // Try to extract from custom properties if available
                if (result.Exception?.Analysis?.CustomProperties != null)
                {
                    var customProps = result.Exception.Analysis.CustomProperties;
                    typeName = customProps.TryGetValue("typeName", out var tn) ? tn?.ToString() : null;
                    typeName ??= customProps.TryGetValue("className", out var cn) ? cn?.ToString() : null;
                    memberName ??= customProps.TryGetValue("memberName", out var mn) ? mn?.ToString() : null;
                }
            }
            
            if (string.IsNullOrEmpty(typeName)) return;
            
            // Sanitize type name to prevent command injection (remove dangerous chars)
            var sanitizedTypeName = SanitizeTypeNameForCommand(typeName);
            if (string.IsNullOrEmpty(sanitizedTypeName)) return;
            
            analysis.FailedType = typeName; // Store original for display
            
            // Only create ExpectedMember if we have meaningful data
            if (!string.IsNullOrEmpty(memberName) || !string.IsNullOrEmpty(signature))
            {
                analysis.ExpectedMember = new ExpectedMemberInfo
                {
                    Name = memberName,
                    Signature = signature,
                    MemberType = exceptionType.Contains("Method") ? "Method" : 
                                exceptionType.Contains("Field") ? "Field" : 
                                exceptionType.Contains("Type") ? "Type" : "Member"
                };
            }
            
            // Try to find the MethodTable using ClrMD Name2EE (safe, won't crash debugger)
            string? validMt = null;
            string? validEeClass = null;
            
            if (_clrMdAnalyzer?.IsOpen == true)
            {
                var name2eeResult = _clrMdAnalyzer.Name2EE(sanitizedTypeName);
                result.RawCommands![$"ClrMD:Name2EE({sanitizedTypeName})"] = 
                    System.Text.Json.JsonSerializer.Serialize(name2eeResult, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                
                if (name2eeResult.FoundType != null)
                {
                    validMt = name2eeResult.FoundType.MethodTable;
                    validEeClass = name2eeResult.FoundType.EEClass;
                }
            }
            else
            {
                _logger?.LogDebug("[TypeResolution] ClrMD not available for Name2EE, skipping type lookup");
            }
            
            if (validMt != null)
            {
                analysis.MethodTable = validMt;
                
                // Use the EEClass from the same result
                if (!string.IsNullOrEmpty(validEeClass) && !IsNullAddress(validEeClass))
                {
                    analysis.EEClass = validEeClass;
                }
                
                // Get all methods from the MethodTable
                var dumpmtCmd = $"!dumpmt -md {analysis.MethodTable}";
                var dumpmtOutput = await ExecuteCommandAsync(dumpmtCmd);
                result.RawCommands![dumpmtCmd] = dumpmtOutput;
                
                // Parse method descriptors
                var allMethods = ParseMethodDescriptors(dumpmtOutput);
                var totalMethodCount = allMethods.Count;
                
                // Check if expected method exists BEFORE truncation
                bool exactMatch = false;
                List<MethodDescriptorInfo> similarMatches = new();
                
                if (!string.IsNullOrEmpty(memberName))
                {
                    exactMatch = allMethods.Any(m => 
                        m.Name?.Equals(memberName, StringComparison.OrdinalIgnoreCase) == true);
                    
                    // Find similar methods (contains the name but not exact match)
                    similarMatches = allMethods.Where(m => 
                        m.Name != null &&
                        !m.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase) &&
                        m.Name.Contains(memberName, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                
                // Keep methods for output but limit to 100
                List<MethodDescriptorInfo> methods;
                if (allMethods.Count > 100)
                {
                    // Prioritize: exact matches, similar matches, then others
                    var matchingMethods = !string.IsNullOrEmpty(memberName) 
                        ? allMethods.Where(m => m.Name?.Contains(memberName, StringComparison.OrdinalIgnoreCase) == true).ToList()
                        : new List<MethodDescriptorInfo>();
                    
                    var remainingSlots = Math.Max(0, 100 - matchingMethods.Count);
                    // Use HashSet for O(1) lookup instead of O(n) Contains
                    var matchingSet = new HashSet<MethodDescriptorInfo>(matchingMethods);
                    var otherMethods = allMethods.Where(m => !matchingSet.Contains(m)).Take(remainingSlots).ToList();
                    methods = matchingMethods.Concat(otherMethods).ToList();
                }
                else
                {
                    methods = allMethods;
                }
                
                analysis.ActualMethods = methods;
                
                // Set analysis results
                if (!string.IsNullOrEmpty(memberName))
                {
                    analysis.MethodFound = exactMatch;
                    
                    // If method found by name, include methods with same name for signature comparison
                    if (exactMatch)
                    {
                        // Show all overloads with matching name (for signature comparison)
                        var matchingOverloads = allMethods.Where(m => 
                            m.Name?.Equals(memberName, StringComparison.OrdinalIgnoreCase) == true).ToList();
                        if (matchingOverloads.Count > 0)
                        {
                            analysis.SimilarMethods = matchingOverloads;
                        }
                    }
                    else if (similarMatches.Count > 0)
                    {
                        analysis.SimilarMethods = similarMatches;
                    }
                    
                    // Generate diagnosis (use totalMethodCount for accurate reporting)
                    analysis.Diagnosis = GenerateResolutionDiagnosis(
                        memberName, exactMatch, similarMatches.Count, totalMethodCount, exceptionType);
                }
                else
                {
                    analysis.MethodFound = false;
                    analysis.Diagnosis = $"Type '{typeName}' found with {totalMethodCount} members. Check if all required members are present.";
                }
                
                // Parse generic instantiation info
                analysis.GenericInstantiation = ParseGenericInstantiation(typeName, dumpmtOutput);
            }
            else
            {
                // Type not found via ClrMD Name2EE - for generic types, try to find a concrete instantiation
                // The methods on any instantiation would show us what methods exist
                var foundViaHeap = await TryFindGenericTypeViaHeapAsync(
                    typeName, sanitizedTypeName, memberName, analysis, result);
                
                if (!foundViaHeap)
                {
                    // Type truly not found
                    analysis.MethodFound = false;
                    analysis.Diagnosis = $"Type '{typeName}' not found in loaded assemblies. " +
                        "Possible causes: 1) Type was trimmed in NativeAOT/PublishTrimmed, " +
                        "2) Assembly not loaded, 3) Type name is different at runtime";
                }
            }
            
            // Set in exception analysis (typeResolution is exception-related)
            result.Exception ??= new ExceptionDetails();
            result.Exception.Analysis ??= new ExceptionAnalysis();
            result.Exception.Analysis.TypeResolution = analysis;
        }
        catch (Exception ex)
        {
            // Don't fail the whole analysis, but record that we tried
            var errorAnalysis = new TypeResolutionAnalysis
            {
                FailedType = typeName,
                Diagnosis = $"Analysis failed: {ex.Message}"
            };
            
            // Set in exception analysis
            result.Exception ??= new ExceptionDetails();
            result.Exception.Analysis ??= new ExceptionAnalysis();
            result.Exception.Analysis.TypeResolution = errorAnalysis;
        }
    }
    
    /// <summary>
    /// Tries to find a generic type by searching for any concrete instantiation in the heap.
    /// This is useful when ClrMD Name2EE doesn't find the open generic type definition.
    /// </summary>
    private async Task<bool> TryFindGenericTypeViaHeapAsync(
        string typeName,
        string sanitizedTypeName,
        string? memberName,
        TypeResolutionAnalysis analysis,
        CrashAnalysisResult result)
    {
        // Only try this for generic types (contain backtick)
        if (!typeName.Contains('`')) return false;
        
        // Extract the base type name without arity (e.g., "ConcurrentDictionary" from "ConcurrentDictionary`2")
        var backtickIndex = typeName.IndexOf('`');
        var baseTypeName = typeName[..backtickIndex];
        var lastDotIndex = baseTypeName.LastIndexOf('.');
        var shortTypeName = lastDotIndex >= 0 ? baseTypeName[(lastDotIndex + 1)..] : baseTypeName;
        
        // Search for any instantiation of this generic type
        var heapCmd = $"!dumpheap -stat -type {shortTypeName}";
        var heapOutput = await ExecuteCommandAsync(heapCmd);
        result.RawCommands![heapCmd] = heapOutput;
        
        // Parse the heap output to find a MethodTable for any instantiation
        // Format: MT    Count    TotalSize Class Name
        //         f7558924ae98     1        32 System.Collections.Concurrent.ConcurrentDictionary<...>
        // Note: MT may not have 0x prefix, and generic types use <> not backtick in display
        var mtPattern = new Regex(
            @"([0-9a-fA-F]{8,16})\s+\d+\s+\d+\s+.*" + Regex.Escape(shortTypeName) + @"(?:`\d+|\<)", 
            RegexOptions.IgnoreCase);
        var mtMatch = mtPattern.Match(heapOutput);
        
        if (mtMatch.Success)
        {
            var foundMt = mtMatch.Groups[1].Value;
            if (!IsNullAddress(foundMt))
            {
                analysis.MethodTable = foundMt;
                
                // Note that we found it via heap search, not direct Name2EE
                var concreteTypeName = ExtractConcreteTypeName(heapOutput, shortTypeName);
                
                // Get methods from this concrete instantiation
                var dumpmtHeapCmd = $"!dumpmt -md {foundMt}";
                var dumpmtOutput = await ExecuteCommandAsync(dumpmtHeapCmd);
                result.RawCommands![dumpmtHeapCmd] = dumpmtOutput;
                
                var allMethods = ParseMethodDescriptors(dumpmtOutput);
                var totalMethodCount = allMethods.Count;
                
                // Check if the expected method exists
                bool exactMatch = false;
                List<MethodDescriptorInfo> similarMatches = new();
                
                if (!string.IsNullOrEmpty(memberName))
                {
                    exactMatch = allMethods.Any(m => 
                        m.Name?.Equals(memberName, StringComparison.OrdinalIgnoreCase) == true);
                    
                    similarMatches = allMethods.Where(m => 
                        m.Name != null &&
                        !m.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase) &&
                        m.Name.Contains(memberName, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                
                // Limit methods for output
                analysis.ActualMethods = allMethods.Count > 100 
                    ? allMethods.Take(100).ToList() 
                    : allMethods;
                
                analysis.MethodFound = exactMatch;
                
                if (exactMatch)
                {
                    var matchingOverloads = allMethods.Where(m => 
                        m.Name?.Equals(memberName, StringComparison.OrdinalIgnoreCase) == true).ToList();
                    if (matchingOverloads.Count > 0)
                    {
                        analysis.SimilarMethods = matchingOverloads;
                    }
                    analysis.Diagnosis = $"Method '{memberName}' exists on concrete instantiation '{concreteTypeName ?? "unknown"}'. " +
                        "The issue may be a signature mismatch or the specific generic instantiation being called wasn't loaded.";
                }
                else if (similarMatches.Count > 0)
                {
                    analysis.SimilarMethods = similarMatches;
                    analysis.Diagnosis = $"Method '{memberName}' not found on '{concreteTypeName ?? typeName}', " +
                        $"but {similarMatches.Count} similar method(s) exist. " +
                        "The method may have been trimmed or renamed.";
                }
                else
                {
                    analysis.Diagnosis = $"Method '{memberName}' not found on any instantiation of '{typeName}' " +
                        $"({totalMethodCount} methods found). The method was likely trimmed.";
                }
                
                // Parse generic instantiation info from the found type
                if (!string.IsNullOrEmpty(concreteTypeName))
                {
                    analysis.GenericInstantiation = ParseGenericInstantiation(concreteTypeName, dumpmtOutput);
                }
                
                return true;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Extracts the concrete type name from dumpheap output.
    /// </summary>
    private static string? ExtractConcreteTypeName(string heapOutput, string shortTypeName)
    {
        // Look for a line containing the full type name
        // Format can be: Namespace.Type`2[[...]] or Namespace.Type<T1, T2>
        var typePattern = new Regex(
            @"([^\s]+\." + Regex.Escape(shortTypeName) + @"(?:`\d+(?:\[\[[^\]]+\]\])?|\<[^>]+\>))", 
            RegexOptions.IgnoreCase);
        var match = typePattern.Match(heapOutput);
        return match.Success ? match.Groups[1].Value : null;
    }
    
    /// <summary>
    /// Parses the exception message to extract type name, member name, and signature.
    /// </summary>
    internal static (string? typeName, string? memberName, string? signature) ParseExceptionForTypeAndMember(
        string exceptionType, string exceptionMessage)
    {
        string? typeName = null;
        string? memberName = null;
        string? signature = null;
        
        // Pattern 1: "Method not found: 'ReturnType Namespace.Type`N.Method(params)'"
        // Handles generic types with backticks like ConcurrentDictionary`2
        var methodNotFoundMatch = Regex.Match(exceptionMessage, 
            @"Method not found:\s*'(?:(\S+)\s+)?(.+?)\.([^.(`]+)\(([^)]*)\)'");
        if (methodNotFoundMatch.Success)
        {
            var returnType = methodNotFoundMatch.Groups[1].Value;
            typeName = methodNotFoundMatch.Groups[2].Value;
            memberName = methodNotFoundMatch.Groups[3].Value;
            var parameters = methodNotFoundMatch.Groups[4].Value;
            signature = string.IsNullOrEmpty(returnType) 
                ? $"{memberName}({parameters})" 
                : $"{returnType} {memberName}({parameters})";
            return (typeName, memberName, signature);
        }
        
        // Pattern 2: "Could not load type 'TypeName' from assembly 'AssemblyName'"
        var typeLoadMatch = Regex.Match(exceptionMessage, 
            @"Could not load type '([^']+)'");
        if (typeLoadMatch.Success)
        {
            typeName = typeLoadMatch.Groups[1].Value;
            return (typeName, null, null);
        }
        
        // Pattern 3: "Field not found: 'TypeName.FieldName'"
        var fieldNotFoundMatch = Regex.Match(exceptionMessage, 
            @"Field not found:\s*'([^.]+(?:\.[^.]+)*)\.([^']+)'");
        if (fieldNotFoundMatch.Success)
        {
            typeName = fieldNotFoundMatch.Groups[1].Value;
            memberName = fieldNotFoundMatch.Groups[2].Value;
            return (typeName, memberName, memberName);
        }
        
        // Pattern 4: Generic "Member 'X' not found on type 'Y'"
        var memberNotFoundMatch = Regex.Match(exceptionMessage, 
            @"[Mm]ember\s*'([^']+)'.*(?:not found|does not exist).*(?:type|class)\s*'([^']+)'",
            RegexOptions.IgnoreCase);
        if (memberNotFoundMatch.Success)
        {
            memberName = memberNotFoundMatch.Groups[1].Value;
            typeName = memberNotFoundMatch.Groups[2].Value;
            return (typeName, memberName, null);
        }
        
        // Pattern 5: Just extract any type-looking name (Namespace.Type`N pattern)
        var genericTypeMatch = Regex.Match(exceptionMessage, 
            @"([A-Z][a-zA-Z0-9_.]*(?:`\d+)?(?:\[[^\]]+\])?)");
        if (genericTypeMatch.Success && genericTypeMatch.Groups[1].Value.Contains('.'))
        {
            typeName = genericTypeMatch.Groups[1].Value;
        }
        
        return (typeName, memberName, signature);
    }
    
    /// <summary>
    /// Parses method descriptors from !dumpmt -md output.
    /// </summary>
    internal static List<MethodDescriptorInfo> ParseMethodDescriptors(string dumpmtOutput)
    {
        var methods = new List<MethodDescriptorInfo>();
        
        // dumpmt -md output format:
        // MethodDesc Table
        //    Entry       MethodDesc    JIT Name
        // 0000f755...  0000f755...  PreJIT System.Collections.Concurrent.ConcurrentDictionary`2.TryAdd(...)
        // Or:
        //    Entry       MethodDesc    JIT     Name
        // 00007FFD...  00007FFD...  NONE     System.Object.ToString()
        
        var lines = dumpmtOutput.Split('\n');
        var inMethodTable = false;
        
        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            
            // Detect start of method table - look for the header line
            if (trimmedLine.Contains("MethodDesc Table"))
            {
                inMethodTable = true;
                continue;
            }
            
            // Skip header line (Entry MethodDesc JIT Name)
            if (trimmedLine.StartsWith("Entry", StringComparison.OrdinalIgnoreCase) &&
                trimmedLine.Contains("MethodDesc", StringComparison.OrdinalIgnoreCase))
            {
                inMethodTable = true;
                continue;
            }
            
            if (!inMethodTable) continue;
            
            // Skip empty lines, separators, and non-method lines
            if (string.IsNullOrWhiteSpace(trimmedLine) || 
                trimmedLine.StartsWith("---") ||
                trimmedLine.StartsWith("Total", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            
            // Parse method line
            // Format: EntryAddr  MethodDescAddr  JITStatus  Slot  FullMethodName
            // Example: 0000F755884B0000 0000F75587424898   NONE 0000000000000000 System.Object.Finalize()
            // Note: JIT status may be followed by extra chars like 'PreJIT' or 'P' prefix
            var methodMatch = Regex.Match(trimmedLine, 
                @"([0-9a-fA-Fx]+)\s+([0-9a-fA-Fx]+)\s+(\S+)\s+([0-9a-fA-Fx]+)\s+(.+)$");
            
            if (methodMatch.Success)
            {
                var fullName = methodMatch.Groups[5].Value.Trim();
                var slotValue = methodMatch.Groups[4].Value;
                
                // Extract just the method name from the full name
                // "System.Collections.Concurrent.ConcurrentDictionary`2.TryAdd(!0, !1)" -> "TryAdd"
                var methodName = ExtractMethodName(fullName);
                
                // Parse slot from hex
                int parsedSlot = 0;
                if (slotValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(slotValue[2..], System.Globalization.NumberStyles.HexNumber, null, out parsedSlot);
                }
                else
                {
                    int.TryParse(slotValue, System.Globalization.NumberStyles.HexNumber, null, out parsedSlot);
                }
                
                methods.Add(new MethodDescriptorInfo
                {
                    CodeAddress = methodMatch.Groups[1].Value,
                    MethodDesc = methodMatch.Groups[2].Value,
                    JitStatus = methodMatch.Groups[3].Value,
                    Signature = fullName,
                    Name = methodName,
                    Slot = parsedSlot
                });
            }
        }
        
        return methods;
    }
    
    /// <summary>
    /// Extracts the method name from a full method signature.
    /// </summary>
    private static string? ExtractMethodName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return null;
        
        // Remove parameters if present
        var parenIndex = fullName.IndexOf('(');
        var nameWithoutParams = parenIndex > 0 ? fullName[..parenIndex] : fullName;
        
        // Get the last part after the last dot (but handle generic types with backticks)
        var lastDotIndex = nameWithoutParams.LastIndexOf('.');
        if (lastDotIndex > 0 && lastDotIndex < nameWithoutParams.Length - 1)
        {
            return nameWithoutParams[(lastDotIndex + 1)..];
        }
        
        return nameWithoutParams;
    }
    
    /// <summary>
    /// Generates a diagnosis message based on the resolution analysis.
    /// </summary>
    internal static string GenerateResolutionDiagnosis(
        string memberName, bool exactMatch, int similarCount, int totalMethods, string exceptionType)
    {
        // Determine member type from exception for accurate messaging
        var memberType = exceptionType.Contains("Field") ? "Field" : 
                        exceptionType.Contains("Method") ? "Method" : "Member";
        
        if (exactMatch)
        {
            return $"{memberType} '{memberName}' exists but signature may not match. " +
                   "Check parameter types, generic arguments, or return type.";
        }
        
        if (similarCount > 0)
        {
            return $"{memberType} '{memberName}' not found exactly, but {similarCount} similar member(s) exist. " +
                   "Possible causes: 1) Member was renamed, 2) Overload was removed/trimmed, " +
                   "3) Signature changed in newer version.";
        }
        
        if (exceptionType.Contains("MissingMethod") || exceptionType.Contains("MissingField") || 
            exceptionType.Contains("MissingMember"))
        {
            return $"{memberType} '{memberName}' not found in MethodTable ({totalMethods} members total). " +
                   "Possible causes: 1) Member was trimmed in NativeAOT/PublishTrimmed, " +
                   "2) Assembly version mismatch, 3) Member doesn't exist on this type.";
        }
        
        return $"Member '{memberName}' not found. {totalMethods} members found on the type.";
    }
    
    /// <summary>
    /// Parses generic instantiation information from the type name and dumpmt output.
    /// </summary>
    internal static GenericInstantiationInfo? ParseGenericInstantiation(string typeName, string dumpmtOutput)
    {
        // Check if it's a generic type (contains backtick)
        if (!typeName.Contains('`')) return null;
        
        var info = new GenericInstantiationInfo
        {
            IsGenericType = true
        };
        
        // Extract type definition (e.g., "ConcurrentDictionary`2" from full name)
        var backtickIndex = typeName.IndexOf('`');
        var dotBeforeBacktick = backtickIndex > 0 ? typeName.LastIndexOf('.', backtickIndex) : -1;
        info.TypeDefinition = dotBeforeBacktick >= 0 
            ? typeName[(dotBeforeBacktick + 1)..] 
            : typeName;
        
        // Extract the arity (number of type parameters)
        var arityMatch = Regex.Match(typeName, @"`(\d+)");
        var arity = arityMatch.Success && int.TryParse(arityMatch.Groups[1].Value, out var parsedArity) ? parsedArity : 0;
        
        // Try to extract type arguments from dumpmt output or type name
        // Multiple formats:
        // 1. [[System.String, ...], [OtherType, ...]] - Two type args with assembly info
        // 2. [System.String, OtherType] - Simple format
        // 3. [System.String] - Single type arg
        var combinedText = typeName + " " + dumpmtOutput;
        
        info.TypeArguments = new List<string>();
        
        // Try to find type arguments in various formats
        // Format 1: [[Type1, Assembly...], [Type2, Assembly...]] - .NET generic instantiation
        var allBracketMatches = Regex.Matches(combinedText, @"\[([^\[\]]+)\]");
        foreach (Match match in allBracketMatches)
        {
            var content = match.Groups[1].Value;
            // Skip if this looks like an array dimension (e.g., "[]" or "[,]") or index
            if (string.IsNullOrWhiteSpace(content) || 
                content.All(c => c == ',' || char.IsDigit(c))) continue;
            
            var cleanArg = CleanTypeArgument(content);
            if (!string.IsNullOrEmpty(cleanArg) && 
                LooksLikeTypeName(cleanArg) && // Must look like a type name
                info.TypeArguments.Count < arity) // Don't exceed expected arity
            {
                info.TypeArguments.Add(cleanArg);
            }
        }
        
        // If no type arguments found and we know the arity, indicate unknown types
        if (info.TypeArguments.Count == 0 && arity > 0)
        {
            for (var i = 0; i < arity; i++)
            {
                info.TypeArguments.Add($"T{i}");
            }
        }
        
        return info;
    }
    
    /// <summary>
    /// Cleans a type argument by removing assembly qualification.
    /// </summary>
    private static string CleanTypeArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return string.Empty;
        
        // Remove assembly info: "System.String, System.Private.CoreLib, ..." -> "System.String"
        var commaIndex = arg.IndexOf(',');
        var cleanArg = commaIndex > 0 ? arg[..commaIndex].Trim() : arg.Trim();
        
        return cleanArg;
    }
    
    /// <summary>
    /// Known primitive and built-in types without namespace (for type name validation).
    /// </summary>
    private static readonly HashSet<string> KnownPrimitiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "String", "Int32", "Int64", "Int16", "Byte", "Boolean", "Double", "Single",
        "Decimal", "Char", "Object", "DateTime", "TimeSpan", "Guid", "UInt32", 
        "UInt64", "UInt16", "SByte", "IntPtr", "UIntPtr", "Void"
    };
    
    /// <summary>
    /// Checks if a string looks like a .NET type name.
    /// </summary>
    private static bool LooksLikeTypeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2) return false;
        
        // Must contain a dot (namespace.type) OR be a known primitive/common type
        if (name.Contains('.')) return true;
        
        return KnownPrimitiveTypes.Contains(name);
    }
    
    /// <summary>
    /// Sanitizes a type name for use in debugger commands.
    /// Removes potentially dangerous characters that could cause command injection.
    /// </summary>
    private static string? SanitizeTypeNameForCommand(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;
        
        // Remove characters that could break or inject commands
        // Keep: alphanumeric, dots, backticks (generics), plus (nested), brackets (arrays/generics)
        var sanitized = new string(typeName.Where(c => 
            char.IsLetterOrDigit(c) || 
            c == '.' || c == '`' || c == '+' || 
            c == '[' || c == ']' || c == ',' || c == '_').ToArray());
        
        // Ensure it still looks like a valid type name
        if (string.IsNullOrWhiteSpace(sanitized) || sanitized.Length < 2)
            return null;
            
        return sanitized;
    }
    
    
    /// <summary>
    /// Analyzes if the application is NativeAOT and detects potential trimming issues.
    /// NativeAOT applications have different failure modes, especially around reflection and trimming.
    /// </summary>
    protected async Task AnalyzeNativeAotAsync(CrashAnalysisResult result)
    {
        var analysis = new NativeAotAnalysis
        {
            Indicators = new List<NativeAotIndicator>()
        };
        
        // 1. Check for JIT compiler presence (NativeAOT apps don't have clrjit)
        // Try both LLDB and WinDbg key formats
        var modulesOutput = result.RawCommands?.TryGetValue("image list", out var mod) == true ? mod 
                          : result.RawCommands?.TryGetValue("lm", out mod) == true ? mod 
                          : null;
        if (string.IsNullOrEmpty(modulesOutput))
        {
            // Try to get modules list
            modulesOutput = await ExecuteCommandAsync("image list");
            if (!string.IsNullOrEmpty(modulesOutput))
            {
                result.RawCommands!["image list"] = modulesOutput;
            }
        }
        
        analysis.HasJitCompiler = HasJitCompiler(modulesOutput ?? string.Empty);
        
        // 2. Detect NativeAOT from stack frames (returns actual frame data)
        var stackFrameIndicators = DetectNativeAotFromStackFrames(result);
        if (stackFrameIndicators.Count > 0)
        {
            analysis.Indicators.AddRange(stackFrameIndicators);
        }
        
        // 3. Check modules for NativeAOT-specific patterns
        var moduleIndicators = DetectNativeAotFromModules(modulesOutput ?? string.Empty);
        if (moduleIndicators.Count > 0)
        {
            // If JIT is present, filter out "absence" indicators since they're likely false negatives
            if (analysis.HasJitCompiler)
            {
                moduleIndicators = moduleIndicators.Where(i => i.Source != "module:absence").ToList();
            }
            analysis.Indicators.AddRange(moduleIndicators);
        }
        
        // Determine if NativeAOT based on indicators
        // CRITICAL: If JIT compiler is present, this is NOT NativeAOT - JIT presence is definitive
        // NativeAOT by definition has no JIT, all code is AOT compiled
        if (analysis.HasJitCompiler)
        {
            // JIT is loaded - this cannot be NativeAOT
            // The indicators might be false positives or similar patterns in CoreCLR
            analysis.IsNativeAot = false;
        }
        else
        {
            // No JIT - any NativeAOT indicator is significant since JIT absence is strong evidence
            // Just having one indicator when JIT is missing strongly suggests NativeAOT
            analysis.IsNativeAot = (analysis.Indicators?.Count ?? 0) >= 1;
        }
        
        // 4. Detect reflection usage patterns in stack traces
        var reflectionUsage = DetectReflectionUsage(result);
        if (reflectionUsage.Count > 0)
        {
            analysis.ReflectionUsage = reflectionUsage;
        }
        
        // 5. Analyze trimming-related exceptions (MissingMethodException, TypeLoadException, etc.)
        // These are useful diagnostics regardless of NativeAOT status
        var exceptionType = result.Exception?.Type;
        var exceptionMessage = result.Exception?.Message;
        
        if (!string.IsNullOrEmpty(exceptionType) && !string.IsNullOrEmpty(exceptionMessage))
        {
            // Always analyze trimming-related exceptions for diagnostic value
            var trimmingAnalysis = AnalyzeTrimmingIssue(exceptionType, exceptionMessage, result);
            if (trimmingAnalysis != null)
            {
                // Count evidence to determine confidence level
                var evidenceCount = 0;
                
                // NativeAOT detection is strong evidence
                if (analysis.IsNativeAot) evidenceCount += 3;
                
                // No JIT compiler is strong evidence for NativeAOT/trimming
                if (!analysis.HasJitCompiler) evidenceCount += 2;
                
                // NativeAOT-like indicators (only count if no JIT, otherwise likely false positives)
                if (!analysis.HasJitCompiler)
                {
                    evidenceCount += analysis.Indicators?.Count ?? 0;
                }
                
                // Reflection usage in stack is weak evidence (only if multiple)
                if ((analysis.ReflectionUsage?.Count ?? 0) >= 2) evidenceCount += 1;
                
                // Set confidence based on evidence
                if (analysis.IsNativeAot)
                {
                    trimmingAnalysis.Confidence = "high";
                }
                else if (!analysis.HasJitCompiler && evidenceCount >= 4)
                {
                    trimmingAnalysis.Confidence = "high";
                }
                else if (!analysis.HasJitCompiler)
                {
                    trimmingAnalysis.Confidence = "medium";
                }
                else
                {
                    // JIT is present - likely version mismatch, not trimming
                    trimmingAnalysis.Confidence = "low";
                    trimmingAnalysis.PotentialTrimmingIssue = false; // Not trimming, but still useful diagnostic
                }
                
                analysis.TrimmingAnalysis = trimmingAnalysis;
            }
        }
        
        // Only add to result if we found meaningful findings
        // - NativeAOT detection (no JIT + indicators)
        // - Trimming analysis (exception parsing found useful data)
        // - With JIT: many indicators + reflection usage (informational)
        var hasSignificantFindings = 
            analysis.IsNativeAot || 
            analysis.TrimmingAnalysis != null ||
            (analysis.HasJitCompiler && (analysis.Indicators?.Count ?? 0) >= 3 && (analysis.ReflectionUsage?.Count ?? 0) >= 2);
            
        if (hasSignificantFindings)
        {
            // Set in environment (nativeAot is runtime/environment info)
            result.Environment ??= new EnvironmentInfo();
            result.Environment.NativeAot = analysis;
        }
    }
    
    /// <summary>
    /// Checks if a JIT compiler module is loaded.
    /// NativeAOT applications don't have clrjit since all code is AOT compiled.
    /// </summary>
    internal static bool HasJitCompiler(string modulesOutput)
    {
        if (string.IsNullOrEmpty(modulesOutput)) return true; // Assume JIT if we can't check
        
        return modulesOutput.Contains("clrjit", StringComparison.OrdinalIgnoreCase) ||
               modulesOutput.Contains("libclrjit", StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Detects NativeAOT indicators from stack frames with actual frame data.
    /// </summary>
    internal static List<NativeAotIndicator> DetectNativeAotFromStackFrames(CrashAnalysisResult result)
    {
        var indicators = new List<NativeAotIndicator>();
        
        // NativeAOT-specific patterns in stack frames
        // These are ordered from most specific to least specific
        var nativeAotPatterns = new[]
        {
            // NativeAOT runtime patterns
            "nativeaot/Runtime.Base",
            "System.Runtime.EH.DispatchEx",
            "Internal.Runtime.CompilerHelpers",
            
            // NativeAOT mangled names (S_P_ prefix is used for mangled symbols)
            "S_P_CoreLib_",
            "S_P_System_",
            "S_P_Microsoft_",
            
            // NativeAOT runtime helper functions (Rhp = Runtime Helper Portable)
            // Note: Order matters! More specific patterns first to avoid double-counting
            "RhpCallInterceptor",
            "RhpNewFast",
            "RhpNewArray",
            "RhThrowEx",
            "RhRethrow",
            "RhpGcStress",
            "RhpCheckedAssignRef",
            "RhpByRefAssignRef",
            "RhpAssignRef",
            
            // NativeAOT internal patterns
            "__GetMethodTable",
            "__managedcode_segment",
            "__type_check_",
            "__vtable_",
        };
        
        // Collect all stack frames from all threads
        var allFrames = new List<StackFrame>();
        
        if (result.Threads!.All != null)
        {
            foreach (var thread in result.Threads!.All)
            {
                if (thread.CallStack != null)
                {
                    allFrames.AddRange(thread.CallStack);
                }
            }
        }
        
        // Also check exception stack trace frames if available
        if (result.Exception?.StackTrace != null)
        {
            allFrames.AddRange(result.Exception.StackTrace);
        }
        
        // Track which frames have been matched to avoid double-counting
        var matchedFrames = new HashSet<string>();
        var matchedPatterns = new HashSet<string>();
        
        // Check for patterns (patterns are ordered most-specific first)
        foreach (var pattern in nativeAotPatterns)
        {
            foreach (var frame in allFrames)
            {
                // Create a unique key for this frame
                var frameKey = $"{frame.Function}|{frame.Module}|{frame.Source}";
                
                // Skip if this frame was already matched by a more specific pattern
                if (matchedFrames.Contains(frameKey)) continue;
                
                string? matchedValue = null;
                string? matchedIn = null;
                
                if (frame.Function?.Contains(pattern, StringComparison.OrdinalIgnoreCase) == true)
                {
                    matchedValue = frame.Function;
                    matchedIn = "function";
                }
                else if (frame.Module?.Contains(pattern, StringComparison.OrdinalIgnoreCase) == true)
                {
                    matchedValue = frame.Module;
                    matchedIn = "module";
                }
                else if (frame.Source?.Contains(pattern, StringComparison.OrdinalIgnoreCase) == true)
                {
                    matchedValue = frame.Source;
                    matchedIn = "source";
                }
                
                if (matchedValue != null && !matchedPatterns.Contains(pattern))
                {
                    matchedFrames.Add(frameKey);
                    matchedPatterns.Add(pattern);
                    
                    indicators.Add(new NativeAotIndicator
                    {
                        Source = $"stackFrame:{matchedIn}",
                        Pattern = pattern,
                        MatchedValue = matchedValue,
                        Frame = frame
                    });
                    
                    break; // Found one match for this pattern, move to next pattern
                }
            }
        }
        
        return indicators;
    }
    
    /// <summary>
    /// Detects NativeAOT indicators from loaded modules with actual data.
    /// </summary>
    internal static List<NativeAotIndicator> DetectNativeAotFromModules(string modulesOutput)
    {
        var indicators = new List<NativeAotIndicator>();
        
        if (string.IsNullOrEmpty(modulesOutput)) return indicators;
        
        // NativeAOT-specific module patterns
        var modulePatterns = new[]
        {
            "System.Private.CoreLib.Native",
            "Runtime.WorkstationGC",
            "Runtime.ServerGC",
            "standalonegc"
        };
        
        // Search for each pattern in the modules output
        foreach (var pattern in modulePatterns)
        {
            if (modulesOutput.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                // Try to extract the matching line from modules output
                var matchedLine = ExtractMatchingLine(modulesOutput, pattern);
                
                indicators.Add(new NativeAotIndicator
                {
                    Source = "module",
                    Pattern = pattern,
                    MatchedValue = matchedLine ?? pattern
                });
            }
        }
        
        // Check for absence of CoreCLR (negative indicator - less useful as raw data)
        var hasCoreCLR = modulesOutput.Contains("coreclr", StringComparison.OrdinalIgnoreCase) ||
                         modulesOutput.Contains("libcoreclr", StringComparison.OrdinalIgnoreCase);
        
        if (!hasCoreCLR)
        {
            indicators.Add(new NativeAotIndicator
            {
                Source = "module:absence",
                Pattern = "coreclr|libcoreclr",
                MatchedValue = "NOT_FOUND"
            });
        }
        
        return indicators;
    }
    
    /// <summary>
    /// Extracts the line containing a pattern from output.
    /// </summary>
    private static string? ExtractMatchingLine(string output, string pattern)
    {
        var lines = output.Split('\n');
        foreach (var line in lines)
        {
            if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return line.Trim();
            }
        }
        return null;
    }
    
    /// <summary>
    /// Analyzes potential trimming issues from exception type and message.
    /// </summary>
    private static TrimmingAnalysis? AnalyzeTrimmingIssue(
        string exceptionType, 
        string exceptionMessage, 
        CrashAnalysisResult result)
    {
        // Only analyze for resolution-related exceptions
        var isTrimmingRelated = 
            exceptionType.Contains("MissingMethodException") ||
            exceptionType.Contains("MissingFieldException") ||
            exceptionType.Contains("MissingMemberException") ||
            exceptionType.Contains("TypeLoadException") ||
            exceptionType.Contains("TypeInitializationException") ||
            exceptionType.Contains("EntryPointNotFoundException") ||
            exceptionType.Contains("FileNotFoundException") ||
            exceptionType.Contains("BadImageFormatException") ||
            exceptionType.Contains("PlatformNotSupportedException");
        
        if (!isTrimmingRelated) return null;
        
        var analysis = new TrimmingAnalysis
        {
            PotentialTrimmingIssue = true,
            ExceptionType = exceptionType
        };
        
        // Parse the missing member from the message using multiple patterns
        string? missingMember = null;
        
        // Pattern 1: "Method not found: 'ReturnType Namespace.Type.Method(params)'"
        var methodMatch = Regex.Match(exceptionMessage, @"Method not found:\s*'([^']+)'");
        if (methodMatch.Success)
        {
            missingMember = methodMatch.Groups[1].Value;
        }
        
        // Pattern 2: "Could not load type 'TypeName' from assembly 'AssemblyName'"
        if (missingMember == null)
        {
            var typeMatch = Regex.Match(exceptionMessage, @"Could not load type '([^']+)'");
            if (typeMatch.Success)
            {
                missingMember = typeMatch.Groups[1].Value;
            }
        }
        
        // Pattern 3: "Field not found: 'FieldName'"
        if (missingMember == null)
        {
            var fieldMatch = Regex.Match(exceptionMessage, @"Field not found:\s*'([^']+)'");
            if (fieldMatch.Success)
            {
                missingMember = fieldMatch.Groups[1].Value;
            }
        }
        
        // Pattern 4: "Member 'MemberName' not found" (MissingMemberException)
        if (missingMember == null)
        {
            var memberMatch = Regex.Match(exceptionMessage, @"Member\s*'([^']+)'\s*not found", RegexOptions.IgnoreCase);
            if (memberMatch.Success)
            {
                missingMember = memberMatch.Groups[1].Value;
            }
        }
        
        // Pattern 5: "Unable to find an entry point named 'xxx'" (EntryPointNotFoundException)
        if (missingMember == null)
        {
            var entryPointMatch = Regex.Match(exceptionMessage, @"Unable to find an entry point named '([^']+)'");
            if (entryPointMatch.Success)
            {
                missingMember = entryPointMatch.Groups[1].Value;
            }
        }
        
        // Pattern 6: TypeInitializationException - extract the type that failed to initialize
        if (missingMember == null && exceptionType.Contains("TypeInitializationException"))
        {
            var typeInitMatch = Regex.Match(exceptionMessage, @"type initializer for '([^']+)'");
            if (typeInitMatch.Success)
            {
                missingMember = typeInitMatch.Groups[1].Value;
            }
        }
        
        // Fallback to the truncated message
        analysis.MissingMember = missingMember ?? (exceptionMessage.Length > 200 
            ? exceptionMessage[..200] + "..." 
            : exceptionMessage);
        
        // Find the calling frame from exception stack trace or thread stack
        var stackToSearch = result.Exception?.StackTrace ?? 
                           result.Threads!.All?.FirstOrDefault(t => t.IsFaulting)?.CallStack ??
                           result.Threads!.All?.FirstOrDefault(t => t.CallStack?.Count > 0)?.CallStack;
        
        if (stackToSearch?.Count > 0)
        {
            // Filter out runtime and system modules to find the user code frame
            var firstManagedFrame = stackToSearch.FirstOrDefault(f => 
                !string.IsNullOrEmpty(f.Module) && 
                !f.Module.Contains("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase) &&
                !f.Module.Contains("coreclr", StringComparison.OrdinalIgnoreCase) &&
                !f.Module.Contains("libcoreclr", StringComparison.OrdinalIgnoreCase) &&
                // NativeAOT patterns to skip
                !f.Module.StartsWith("S_P_", StringComparison.OrdinalIgnoreCase) &&
                f.Function?.StartsWith("S_P_", StringComparison.OrdinalIgnoreCase) != true);
            
            if (firstManagedFrame != null)
            {
                // Store the actual frame, not just the module name
                analysis.CallingFrame = firstManagedFrame;
            }
        }
        
        // Generate recommendation
        analysis.Recommendation = GenerateTrimmingRecommendation(
            analysis.MissingMember ?? string.Empty, exceptionType);
        
        return analysis;
    }
    
    /// <summary>
    /// Generates a recommendation for fixing trimming issues.
    /// </summary>
    internal static string GenerateTrimmingRecommendation(string missingMember, string exceptionType)
    {
        var recommendations = new List<string>();
        
        if (exceptionType.Contains("MissingMethod"))
        {
            // Parse to check if it's a generic type
            if (missingMember.Contains('`') || missingMember.Contains('<'))
            {
                recommendations.Add("Add [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] to the type parameter");
            }
            
            recommendations.Add("Add [DynamicDependency] attribute to preserve the method");
            recommendations.Add("Use rd.xml file to explicitly preserve the method: <Type Name=\"TypeName\" Dynamic=\"All\" />");
            recommendations.Add("Consider using source generators instead of reflection");
        }
        else if (exceptionType.Contains("TypeLoad"))
        {
            recommendations.Add("Add [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] or preserve in rd.xml");
            recommendations.Add("Ensure the type's assembly is not trimmed - add to TrimmerRootAssembly");
            recommendations.Add("Check if the type uses features incompatible with trimming (e.g., COM, serialization)");
        }
        else if (exceptionType.Contains("MissingField"))
        {
            recommendations.Add("Add [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)]");
            recommendations.Add("Use [DynamicDependency] to preserve the field");
        }
        else if (exceptionType.Contains("MissingMember"))
        {
            recommendations.Add("Add [DynamicallyAccessedMembers] with the appropriate member types");
            recommendations.Add("Use [DynamicDependency] to preserve the specific member");
            recommendations.Add("Check for assembly version mismatch between compile-time and runtime");
        }
        else if (exceptionType.Contains("EntryPoint"))
        {
            recommendations.Add("Ensure native library is deployed with the application");
            recommendations.Add("Check P/Invoke signature matches the native function exactly");
            recommendations.Add("Consider using [LibraryImport] source generator for NativeAOT compatibility");
        }
        else if (exceptionType.Contains("TypeInitialization"))
        {
            recommendations.Add("Check the inner exception for the real error");
            recommendations.Add("Ensure all types used in static constructors are preserved");
            recommendations.Add("Consider lazy initialization instead of static constructors for reflection-heavy code");
        }
        else if (exceptionType.Contains("FileNotFoundException"))
        {
            recommendations.Add("Add the assembly to TrimmerRootAssembly in the project file");
            recommendations.Add("Use [DynamicDependency] to preserve dynamically loaded assemblies");
            recommendations.Add("Consider embedding the assembly as a resource instead of loading dynamically");
        }
        else if (exceptionType.Contains("BadImageFormat"))
        {
            recommendations.Add("Ensure all assemblies are built for the correct target architecture (x64/ARM64)");
            recommendations.Add("NativeAOT cannot load IL assemblies at runtime - all code must be AOT compiled");
            recommendations.Add("Check for mixed-mode assemblies or platform-specific native dependencies");
        }
        else if (exceptionType.Contains("PlatformNotSupported"))
        {
            recommendations.Add("This feature is not supported in NativeAOT");
            recommendations.Add("Avoid runtime code generation (Reflection.Emit, Expression.Compile)");
            recommendations.Add("Use source generators instead of runtime reflection patterns");
            recommendations.Add("Consider using ILLink substitution files to provide alternative implementations");
        }
        
        // General recommendations
        recommendations.Add("Enable trimming warnings during build: <SuppressTrimAnalysisWarnings>false</SuppressTrimAnalysisWarnings>");
        recommendations.Add("Run with IL Linker in analysis mode to find trimming issues");
        
        // Format as bullet list with consistent bullets
        return recommendations.Count > 0 
            ? "• " + string.Join("\n• ", recommendations)
            : string.Empty;
    }
    
    /// <summary>
    /// Detects reflection usage patterns in stack traces that may be problematic in NativeAOT.
    /// </summary>
    internal static List<ReflectionUsageInfo> DetectReflectionUsage(CrashAnalysisResult result)
    {
        var usage = new List<ReflectionUsageInfo>();
        
        // Collect all stack frames from all threads
        var allFrames = new List<StackFrame>();
        
        if (result.Threads!.All != null)
        {
            foreach (var thread in result.Threads!.All)
            {
                if (thread.CallStack != null)
                {
                    allFrames.AddRange(thread.CallStack);
                }
            }
        }
        
        // Also check exception stack trace frames if available
        if (result.Exception?.StackTrace != null)
        {
            allFrames.AddRange(result.Exception.StackTrace);
        }
        
        // Reflection patterns to detect
        var reflectionPatterns = new (string Pattern, string Description, string Risk)[]
        {
            // Type reflection
            ("Type.GetMethod", "Dynamic method lookup via GetMethod", "High - method may be trimmed"),
            ("Type.GetType", "Dynamic type loading via Type.GetType", "High - type may be trimmed"),
            ("Type.GetProperty", "Dynamic property lookup", "High - property may be trimmed"),
            ("Type.GetField", "Dynamic field lookup", "High - field may be trimmed"),
            ("Type.GetConstructor", "Dynamic constructor lookup", "High - constructor may be trimmed"),
            ("Type.GetMember", "Dynamic member lookup", "High - member may be trimmed"),
            ("Type.GetEvent", "Dynamic event lookup", "High - event may be trimmed"),
            ("Type.GetInterface", "Dynamic interface lookup", "Medium - interface may be trimmed"),
            
            // Object instantiation
            ("Activator.CreateInstance", "Dynamic object instantiation", "High - constructor may be trimmed"),
            ("FormatterServices.GetUninitializedObject", "Uninitialized object creation", "High - type metadata may be trimmed"),
            
            // Assembly loading (order matters - more specific first)
            ("Assembly.LoadFrom", "Dynamic assembly loading from path", "High - assembly may not be available"),
            ("Assembly.LoadFile", "Dynamic assembly loading from file", "High - assembly may not be available"),
            ("Assembly.Load(", "Dynamic assembly loading", "Medium - assembly may be trimmed"),
            ("AssemblyLoadContext", "Custom assembly loading", "Medium - dynamic loading may fail"),
            
            // Member invocation
            ("MethodInfo.Invoke", "Reflection-based method invocation", "High - target may be trimmed"),
            ("PropertyInfo.GetValue", "Reflection-based property access", "Medium - property may be trimmed"),
            ("PropertyInfo.SetValue", "Reflection-based property write", "Medium - property may be trimmed"),
            ("FieldInfo.GetValue", "Reflection-based field access", "Medium - field may be trimmed"),
            ("FieldInfo.SetValue", "Reflection-based field write", "Medium - field may be trimmed"),
            ("ConstructorInfo.Invoke", "Reflection-based constructor invocation", "High - constructor may be trimmed"),
            
            // Generic instantiation
            ("MakeGenericType", "Generic type instantiation via reflection", "Critical - generic instantiation may be unavailable"),
            ("MakeGenericMethod", "Generic method instantiation via reflection", "Critical - generic instantiation may be unavailable"),
            
            // Dynamic code generation (NOT supported in NativeAOT)
            ("Expression.Lambda", "Dynamic expression tree compilation", "High - expression targets may be trimmed"),
            ("Expression.Compile", "Expression tree compilation", "Critical - may not work in NativeAOT interpreter mode"),
            ("Emit.", "IL code emission", "Critical - not supported in NativeAOT"),
            ("DynamicMethod", "Dynamic method generation", "Critical - not supported in NativeAOT"),
            ("ModuleBuilder", "Dynamic module generation", "Critical - not supported in NativeAOT"),
            ("TypeBuilder", "Dynamic type generation", "Critical - not supported in NativeAOT"),
            
            // Serialization (order matters - more specific patterns first)
            ("JsonSerializer.Deserialize", "JSON deserialization", "Medium - type metadata may be trimmed"),
            ("JsonSerializer.Serialize", "JSON serialization", "Medium - type metadata may be trimmed"),
            ("JsonSerializer.", "JSON serialization operation", "Medium - ensure [JsonSerializable] is used"),
            ("BinaryFormatter", "Binary serialization (obsolete)", "Critical - not supported in NativeAOT"),
            ("XmlSerializer", "XML serialization", "High - may require runtime code generation"),
            ("DataContractSerializer", "Data contract serialization", "Medium - ensure types are preserved"),
        };
        
        var addedPatterns = new HashSet<string>(); // Avoid duplicates
        var matchedFrameFunctions = new HashSet<string>(); // Prevent double-counting from substring matches
        
        foreach (var frame in allFrames)
        {
            if (string.IsNullOrEmpty(frame.Function)) continue;
            
            // Skip if this frame function was already matched by a pattern
            // (prevents Assembly.LoadFrom matching both "Assembly.LoadFrom" and "Assembly.Load" patterns)
            if (matchedFrameFunctions.Contains(frame.Function)) continue;
            
            foreach (var (pattern, description, risk) in reflectionPatterns)
            {
                if (frame.Function.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    var key = $"{pattern}:{frame.Function}";
                    if (addedPatterns.Add(key)) // Only add if not already seen
                    {
                        matchedFrameFunctions.Add(frame.Function);
                        
                        // Build location string, preferring source file info
                        string location;
                        if (!string.IsNullOrEmpty(frame.SourceFile) && frame.LineNumber > 0)
                        {
                            location = $"{Path.GetFileName(frame.SourceFile)}:{frame.LineNumber}";
                        }
                        else if (!string.IsNullOrEmpty(frame.Module))
                        {
                            location = $"{frame.Module}!{frame.Function}";
                        }
                        else
                        {
                            location = frame.Function;
                        }
                        
                        usage.Add(new ReflectionUsageInfo
                        {
                            Location = location,
                            Pattern = description,
                            Risk = risk,
                            Target = frame.Function
                        });
                    }
                    break; // Found match for this frame, move to next frame
                }
            }
            
            // Limit to prevent too many entries
            if (usage.Count >= 20) break;
        }
        
        return usage;
    }
    
    /// <summary>
    /// Determines if dangerous heap operations should be skipped.
    /// EnumerateSyncBlocks and EnumerateObjects can cause SIGSEGV under:
    /// - Cross-architecture analysis (e.g., x64 dump on arm64 host)
    /// - Running under emulation (e.g., x64 Docker on arm64 Mac via Rosetta 2)
    /// 
    /// Can be forced via environment variable: SKIP_HEAP_ENUM=true (or legacy: SKIP_SYNC_BLOCKS=true)
    /// </summary>
    private bool ShouldSkipSyncBlocks()
    {
        // Check environment variables (allows manual override for emulation scenarios)
        var envVar = Environment.GetEnvironmentVariable("SKIP_HEAP_ENUM") 
                  ?? Environment.GetEnvironmentVariable("SKIP_SYNC_BLOCKS");
        if (!string.IsNullOrEmpty(envVar) && 
            (envVar.Equals("true", StringComparison.OrdinalIgnoreCase) || 
             envVar.Equals("1", StringComparison.OrdinalIgnoreCase)))
        {
            _logger?.LogInformation("SKIP_HEAP_ENUM environment variable set - skipping heap enumeration operations");
            return true;
        }
        
        // Also check for cross-architecture (won't detect Docker emulation but catches native cross-arch)
        try
        {
            var hostArch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
            var hostArchStr = hostArch switch
            {
                System.Runtime.InteropServices.Architecture.X64 => "x64",
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                System.Runtime.InteropServices.Architecture.X86 => "x86",
                System.Runtime.InteropServices.Architecture.Arm => "arm",
                _ => hostArch.ToString().ToLowerInvariant()
            };
            
            var dumpArch = _clrMdAnalyzer?.DetectArchitecture();
            
            if (!string.IsNullOrEmpty(dumpArch) && 
                !string.Equals(hostArchStr, dumpArch, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogInformation("Cross-architecture detected (Host={HostArch}, Dump={DumpArch}) - skipping sync block enumeration", 
                    hostArchStr, dumpArch);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to detect architecture");
        }
        
        return false;
    }
    
}
