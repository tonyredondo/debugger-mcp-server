# Plan: ClrMD-based `clrstack` Implementation

## Overview

Replace the slow SOS `clrstack -a -r -all` command (12.8s) with a fast ClrMD-based implementation (~630ms) that provides equivalent functionality without LLDB stability issues.

### Goals

1. **Replace all internal `clrstack` usage** - No more SOS `clrstack` calls anywhere in the codebase
2. **JSON output format** - Structured data like our other ClrMD commands (`dumpobj`, `dumpmodule`, `name2ee`)
3. **CLI command** - New `/clrstack` command in `cmd` mode
4. **MCP tool** - Use `inspect(kind="clr_stack")` for API access
5. **24x faster** - ~630ms vs 12,880ms

### Scope Clarification

**In Scope** - What this plan covers:
| Item | Description |
|------|-------------|
| Replace SOS `clrstack` | New ClrMD-based implementation |
| Same output data | Frames, methods, args, locals, source info |
| JSON format | For MCP tool and CLI |
| Top-frame registers | Via LLDB (optional) |

**Out of Scope** - Existing functionality that remains unchanged:
| Item | Description |
|------|-------------|
| `bt all` collection | Native frame collection stays as-is |
| SP-based merging | Existing merge logic continues to work |
| Native frame handling | Not part of `clrstack` replacement |

The ClrMD `clrstack` replacement outputs the same data structure that we currently parse from SOS `clrstack`. The existing pipeline (bt all → clrstack → merge by SP) continues to work - we're just swapping step 2.

```
Current:  bt all (795ms) → SOS clrstack (12,880ms) → merge
                                   ↓
New:      bt all (795ms) → ClrMD clrstack (~500ms) → merge (unchanged)
```

## Current State

### SOS `clrstack -a -r -all` Performance
- **Duration**: 12,880ms (12.8 seconds)
- **Threads**: 42
- **Frames**: ~200+
- **Issues**: 
  - Slow IPC through LLDB command interface
  - Can crash LLDB/DAC on some platforms (ARM64 .NET 10)
  - `-f` flag (native frames) is particularly unstable

### Current Workaround
We already use a fallback that:
1. Runs `bt all` (native frames with SP) - 795ms
2. Runs `clrstack -a -r -all` (managed frames) - 12,880ms
3. Merges by stack pointer

## Proposed Solution

### Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                    ClrMdAnalyzer.GetAllThreadStacks()               │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────────┐  │
│  │  Stack Walking  │  │ Sequence Point  │  │  Register Fetcher   │  │
│  │    (ClrMD)      │  │    Resolver     │  │     (LLDB)          │  │
│  │                 │  │    (PDB)        │  │                     │  │
│  │ - Threads       │  │                 │  │ - Top frame only    │  │
│  │ - Frames        │  │ - IL offset →   │  │ - Per thread        │  │
│  │ - Methods       │  │   Source:Line   │  │ - ~3ms per thread   │  │
│  │ - Args/Locals   │  │                 │  │                     │  │
│  └────────┬────────┘  └────────┬────────┘  └──────────┬──────────┘  │
│           │                    │                      │              │
│           └────────────────────┴──────────────────────┘              │
│                                │                                     │
│                    ┌───────────▼───────────┐                        │
│                    │   ClrStackResult      │                        │
│                    │   (JSON-serializable) │                        │
│                    └───────────────────────┘                        │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
                                 │
        ┌────────────────────────┼────────────────────────┐
        │                        │                        │
        ▼                        ▼                        ▼
┌───────────────┐    ┌───────────────────┐    ┌──────────────────┐
│ Internal Use  │    │    MCP Tool       │    │   CLI Command    │
│               │    │                   │    │                  │
│ DotNetCrash   │    │ inspect(kind="clr_stack") │    │ /clrstack        │
│ Analyzer      │    │ Returns JSON      │    │ /cs              │
│               │    │                   │    │                  │
│ (replaces all │    │ ObjectInspection  │    │ Pretty-prints    │
│  clrstack     │    │ Tools.cs          │    │ JSON result      │
│  calls)       │    │                   │    │                  │
└───────────────┘    └───────────────────┘    └──────────────────┘
```

### Expected Performance

| Component | Time | Notes |
|-----------|------|-------|
| ClrMD stack walking | ~200ms | All threads, all frames |
| PDB sequence point resolution | ~100ms | Cached per module |
| Argument/Local enumeration | ~100ms | Per frame |
| LLDB register fetch (top frame) | ~130ms | 42 threads × 3ms |
| **Total** | **~530ms** | **24x faster** |

## Implementation Phases

### Phase 1: Core Stack Walking

**File**: `DebuggerMcp/Analysis/ClrMdAnalyzer.cs`

#### 1.1 Add Stack Walking Method

```csharp
/// <summary>
/// Walks the stack for all threads, returning managed frame information.
/// </summary>
public ClrStackResult GetAllThreadStacks(
    bool includeArguments = true,
    bool includeLocals = true)
{
    var stopwatch = Stopwatch.StartNew();
    var result = new ClrStackResult();
    
    // Get faulting thread (if any)
    var faultingThreadId = _runtime.GetCurrentException()?.Thread?.OSThreadId;
    
    foreach (var thread in _runtime.Threads)
    {
        var threadInfo = new ClrThreadStack
        {
            OSThreadId = thread.OSThreadId,
            ManagedThreadId = thread.ManagedThreadId,
            IsAlive = thread.IsAlive,
            IsBackground = thread.IsBackground,
            IsFaulting = thread.OSThreadId == faultingThreadId,
            Frames = new List<ClrFrameInfo>()
        };
        
        // Collect frames first
        var frames = thread.EnumerateStackTrace().ToList();
        
        // Build frame→roots lookup (one pass per thread)
        var frameRoots = (includeArguments || includeLocals)
            ? BuildFrameRootsLookup(thread, frames)
            : null;
        
        int frameIndex = 0;
        foreach (var frame in frames)
        {
            var roots = frameRoots?.GetValueOrDefault(frame.StackPointer) 
                        ?? new List<ClrStackRoot>();
            
            var frameInfo = new ClrFrameInfo
            {
                FrameIndex = frameIndex++,
                StackPointer = frame.StackPointer,
                InstructionPointer = frame.InstructionPointer,
                Kind = frame.Kind.ToString(),
                Method = GetMethodInfo(frame),
                Arguments = includeArguments 
                    ? GetArguments(roots, frame.Method) 
                    : new List<ClrArgumentInfo>(),
                Locals = includeLocals 
                    ? GetLocals(roots, frame.Method) 
                    : new List<ClrLocalInfo>()
            };
            
            // Resolve source location (Phase 2)
            if (frame.Method != null)
            {
                var ilOffset = GetILOffset(frame);
                var modulePath = frame.Method.Type?.Module?.Name;
                
                if (modulePath != null && ilOffset >= 0)
                {
                    frameInfo.SourceLocation = _sequencePointResolver?.GetSourceLocation(
                        modulePath, 
                        (uint)frame.Method.MetadataToken, 
                        ilOffset
                    );
                }
                
                // Store IL offset in method info
                if (frameInfo.Method != null)
                    frameInfo.Method.ILOffset = ilOffset;
            }
            
            threadInfo.Frames.Add(frameInfo);
        }
        
        result.Threads.Add(threadInfo);
    }
    
    result.DurationMs = stopwatch.ElapsedMilliseconds;
    return result;
}
```

#### 1.2 Data Models

```csharp
public class ClrStackResult
{
    public List<ClrThreadStack> Threads { get; set; } = new();
    public int TotalThreads => Threads.Count;
    public int TotalFrames => Threads.Sum(t => t.Frames.Count);
    public long DurationMs { get; set; }  // Time taken to collect
}

public class ClrThreadStack
{
    public uint OSThreadId { get; set; }
    public int ManagedThreadId { get; set; }
    public bool IsAlive { get; set; }
    public bool IsBackground { get; set; }
    public bool IsFaulting { get; set; }  // Is this the crashing thread?
    public RegisterSet? TopFrameRegisters { get; set; }  // Phase 4
    public List<ClrFrameInfo> Frames { get; set; } = new();
}

public class ClrFrameInfo
{
    public int FrameIndex { get; set; }  // 0 = top of stack
    public ulong StackPointer { get; set; }
    public ulong InstructionPointer { get; set; }
    public string? Kind { get; set; }  // Managed, Runtime, Native, etc.
    public ClrMethodInfo? Method { get; set; }
    public SourceLocation? SourceLocation { get; set; }  // Phase 2
    public List<ClrArgumentInfo> Arguments { get; set; } = new();
    public List<ClrLocalInfo> Locals { get; set; } = new();
}

public class ClrMethodInfo
{
    public string? Signature { get; set; }
    public string? TypeName { get; set; }
    public string? MethodName { get; set; }
    public uint MetadataToken { get; set; }
    public ulong NativeCode { get; set; }
    public int ILOffset { get; set; }  // -1 if not available
}

public class ClrArgumentInfo
{
    public int Index { get; set; }  // Argument position
    public string? Name { get; set; }
    public string? TypeName { get; set; }
    public ulong Address { get; set; }
    public object? Value { get; set; }  // For primitives (JSON: number, bool, string)
    public string? ValueString { get; set; }  // Formatted display value
    public bool HasValue { get; set; }
}

public class ClrLocalInfo
{
    public int Index { get; set; }  // Local slot index
    public string? Name { get; set; }  // From PDB, may be null
    public string? TypeName { get; set; }
    public ulong Address { get; set; }
    public object? Value { get; set; }
    public string? ValueString { get; set; }
    public bool HasValue { get; set; }
}
```

### Phase 2: PDB Sequence Point Resolution

**File**: `DebuggerMcp/SourceLink/SequencePointResolver.cs` (new)

#### 2.1 Data Structures

```csharp
/// <summary>
/// Cache entry for a module's sequence points.
/// </summary>
public class ModuleSequencePoints
{
    public string ModuleName { get; set; } = string.Empty;
    public string? PdbPath { get; set; }
    public Dictionary<uint, List<MethodSequencePoint>> Methods { get; set; } = new();
}

/// <summary>
/// A sequence point mapping IL offset to source location.
/// Named differently from System.Reflection.Metadata.SequencePoint to avoid conflicts.
/// </summary>
public class MethodSequencePoint
{
    public int ILOffset { get; set; }
    public string Document { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int StartColumn { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
}
```

#### 2.2 Sequence Point Parser

```csharp
/// <summary>
/// Resolves IL offsets to source file/line using PDB sequence points.
/// </summary>
public class SequencePointResolver
{
    private readonly ConcurrentDictionary<string, ModuleSequencePoints?> _cache = new();
    private readonly List<string> _pdbSearchPaths = new();
    private readonly ILogger? _logger;

    public SequencePointResolver(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Adds a path to search for PDB files.
    /// </summary>
    public void AddPdbSearchPath(string path)
    {
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
        {
            _pdbSearchPaths.Add(path);
        }
    }

    /// <summary>
    /// Gets the source location for a method at a specific IL offset.
    /// </summary>
    public SourceLocation? GetSourceLocation(string modulePath, uint methodToken, int ilOffset)
    {
        if (ilOffset < 0) return null;  // Invalid IL offset
        
        var modulePoints = GetOrLoadModuleSequencePoints(modulePath);
        if (modulePoints == null) return null;
        
        if (!modulePoints.Methods.TryGetValue(methodToken, out var methodPoints))
            return null;
        
        // Find the sequence point that contains this IL offset
        // We want the highest IL offset that is <= our target
        MethodSequencePoint? best = null;
        foreach (var sp in methodPoints)
        {
            if (sp.ILOffset <= ilOffset)
            {
                if (best == null || sp.ILOffset > best.ILOffset)
                    best = sp;
            }
        }
        
        if (best == null) return null;
        
        return new SourceLocation
        {
            SourceFile = best.Document,
            LineNumber = best.StartLine,
            ColumnNumber = best.StartColumn,
            EndLineNumber = best.EndLine,
            EndColumnNumber = best.EndColumn
        };
    }

    /// <summary>
    /// Gets or loads sequence points for a module, with caching.
    /// </summary>
    private ModuleSequencePoints? GetOrLoadModuleSequencePoints(string modulePath)
    {
        var moduleName = Path.GetFileNameWithoutExtension(modulePath);
        
        return _cache.GetOrAdd(moduleName, _ =>
        {
            var pdbPath = FindPdbFile(modulePath);
            if (pdbPath == null)
            {
                _logger?.LogDebug("[SeqPoints] No PDB found for {Module}", moduleName);
                return null;
            }
            
            try
            {
                return LoadSequencePoints(pdbPath);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[SeqPoints] Failed to load PDB {PdbPath}", pdbPath);
                return null;
            }
        });
    }

    /// <summary>
    /// Finds the PDB file for a module.
    /// </summary>
    private string? FindPdbFile(string modulePath)
    {
        var moduleName = Path.GetFileNameWithoutExtension(modulePath);
        var pdbName = moduleName + ".pdb";
        
        // Check alongside module
        var moduleDir = Path.GetDirectoryName(modulePath);
        if (!string.IsNullOrEmpty(moduleDir))
        {
            var sideBySide = Path.Combine(moduleDir, pdbName);
            if (File.Exists(sideBySide)) return sideBySide;
        }
        
        // Check search paths
        foreach (var searchPath in _pdbSearchPaths)
        {
            var pdbPath = Path.Combine(searchPath, pdbName);
            if (File.Exists(pdbPath)) return pdbPath;
            
            // Also check subdirectories (symbol cache structure)
            try
            {
                var found = Directory.GetFiles(searchPath, pdbName, SearchOption.AllDirectories).FirstOrDefault();
                if (found != null) return found;
            }
            catch { /* Ignore search errors */ }
        }
        
        return null;
    }
    
    /// <summary>
    /// Loads sequence points from a PDB file.
    /// </summary>
    private ModuleSequencePoints LoadSequencePoints(string pdbPath)
    {
        using var stream = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        
        var result = new ModuleSequencePoints
        {
            PdbPath = pdbPath
        };
        
        foreach (var methodDebugHandle in reader.MethodDebugInformation)
        {
            var methodDebug = reader.GetMethodDebugInformation(methodDebugHandle);
            var methodToken = (uint)MetadataTokens.GetRowNumber(methodDebugHandle.ToDefinitionHandle());
            
            var points = new List<MethodSequencePoint>();
            foreach (var sp in methodDebug.GetSequencePoints())
            {
                if (sp.IsHidden) continue;
                
                var document = reader.GetDocument(sp.Document);
                var docName = reader.GetString(document.Name);
                
                points.Add(new MethodSequencePoint
                {
                    ILOffset = sp.Offset,
                    Document = docName,
                    StartLine = sp.StartLine,
                    StartColumn = sp.StartColumn,
                    EndLine = sp.EndLine,
                    EndColumn = sp.EndColumn
                });
            }
            
            if (points.Count > 0)
            {
                result.Methods[methodToken] = points;
            }
        }
        
        _logger?.LogDebug("[SeqPoints] Loaded {Count} methods from {PdbPath}", 
            result.Methods.Count, pdbPath);
        
        return result;
    }
}
```

#### 2.3 IL Offset Calculation

Getting the IL offset from a native instruction pointer is non-trivial. ClrMD provides this via:

```csharp
private int GetILOffset(ClrStackFrame frame)
{
    if (frame.Method == null)
        return -1;
    
    // ClrMD can map native IP to IL offset
    // This requires the method's IL mapping table
    try
    {
        // Method 1: Use ClrMethod.GetILOffset (if available in ClrMD version)
        // return frame.Method.GetILOffset(frame.InstructionPointer);
        
        // Method 2: Use ILOffsetMap
        var map = frame.Method.ILOffsetMap;
        if (map == null || map.Length == 0)
            return -1;
        
        // Find the IL offset that corresponds to our native IP
        foreach (var entry in map)
        {
            if (frame.InstructionPointer >= entry.StartAddress && 
                frame.InstructionPointer < entry.EndAddress)
            {
                return entry.ILOffset;
            }
        }
        
        return -1;
    }
    catch
    {
        return -1;
    }
}
```

**Note**: The exact API depends on ClrMD version. We need to verify against our installed version.

#### 2.2 Integration with Stack Walker

```csharp
// In ClrMdAnalyzer.GetAllThreadStacks():
foreach (var frame in thread.EnumerateStackTrace())
{
    var frameInfo = new ClrFrameInfo { ... };
    
    // Resolve source location from PDB
    if (frame.Method != null)
    {
        var ilOffset = GetILOffset(frame);
        var modulePath = frame.Method.Type?.Module?.Name;
        
        if (modulePath != null && ilOffset >= 0)
        {
            frameInfo.SourceLocation = _sequencePointResolver.GetSourceLocation(
                modulePath, 
                (uint)frame.Method.MetadataToken, 
                ilOffset
            );
        }
    }
    
    threadInfo.Frames.Add(frameInfo);
}
```

### Phase 3: Arguments and Locals

**Note**: The ClrMD API for arguments/locals varies by version. We need to check our installed version.

#### 3.1 ClrMD Version Check

```csharp
// ClrMD 2.x API (current):
// ClrStackFrame has: EnumerateArguments(), EnumerateLocals()
// Returns: IEnumerable<ClrStackRoot> or similar

// ClrMD 3.x API (newer):
// May use ClrStackFrameInfo or IClrValue
```

#### 3.2 Argument Enumeration

**Note**: ClrMD 3.x uses `thread.EnumerateStackRoots()` at the thread level, not per-frame.
We need to correlate roots with frames by comparing addresses.

```csharp
/// <summary>
/// Builds a lookup of stack roots indexed by frame (based on SP ranges).
/// Call once per thread, then use for all frames in that thread.
/// </summary>
private Dictionary<ulong, List<ClrStackRoot>> BuildFrameRootsLookup(
    ClrThread thread, 
    List<ClrStackFrame> frames)
{
    var result = new Dictionary<ulong, List<ClrStackRoot>>();
    
    // Initialize empty lists for each frame
    foreach (var frame in frames)
        result[frame.StackPointer] = new List<ClrStackRoot>();
    
    // Sort frames by SP descending (higher SP = earlier frame = closer to stack base)
    var sortedFrames = frames.OrderByDescending(f => f.StackPointer).ToList();
    
    foreach (var root in thread.EnumerateStackRoots())
    {
        // Find which frame this root belongs to based on SP
        // Root belongs to first frame with SP <= root.Address
        foreach (var frame in sortedFrames)
        {
            if (root.Address >= frame.StackPointer)
            {
                result[frame.StackPointer].Add(root);
                break;
            }
        }
    }
    
    return result;
}

private List<ClrArgumentInfo> GetArguments(
    List<ClrStackRoot> frameRoots, 
    ClrMethod? method)
{
    var args = new List<ClrArgumentInfo>();
    
    if (method == null) return args;
    
    try
    {
        int index = 0;
        
        // Stack roots include both args and locals
        // Filter based on method signature parameter count
        var paramCount = method.Signature?.Count(c => c == ',') + 1 ?? 0;
        
        foreach (var root in frameRoots.Take(paramCount + 1)) // +1 for 'this'
        {
            var argInfo = new ClrArgumentInfo
            {
                Index = index,
                Name = GetParameterName(method, index) ?? $"arg{index}",
                TypeName = root.Object.Type?.Name,
                Address = root.Address,
                HasValue = root.Address != 0 && root.Object.Address != 0
            };
            
            if (argInfo.HasValue)
            {
                argInfo.ValueString = FormatObjectValue(root.Object);
                argInfo.Value = GetPrimitiveValue(root.Object);
            }
            
            args.Add(argInfo);
            index++;
        }
    }
    catch (Exception ex)
    {
        _logger?.LogDebug(ex, "Failed to enumerate arguments for frame");
    }
    
    return args;
}

private string? GetParameterName(ClrMethod method, int index)
{
    // Parameter names require PDB info
    // ClrMD may expose this via method.GetParameters() in some versions
    // or we need to read from PDB directly
    return null;  // Will show as arg0, arg1, etc.
}
```

#### 3.3 Local Variable Enumeration

```csharp
private List<ClrLocalInfo> GetLocals(
    List<ClrStackRoot> frameRoots, 
    ClrMethod? method)
{
    var locals = new List<ClrLocalInfo>();
    
    try
    {
        // Skip the first N roots (those are arguments)
        var paramCount = method?.Signature?.Count(c => c == ',') + 1 ?? 0;
        var localRoots = frameRoots.Skip(paramCount + 1);  // +1 for 'this'
        
        int index = 0;
        foreach (var root in localRoots)
        {
            var localInfo = new ClrLocalInfo
            {
                Index = index,
                Name = null,  // Would require PDB local variable info
                TypeName = root.Object.Type?.Name,
                Address = root.Address,
                HasValue = root.Address != 0 && root.Object.Address != 0
            };
            
            if (localInfo.HasValue)
            {
                localInfo.ValueString = FormatObjectValue(root.Object);
                localInfo.Value = GetPrimitiveValue(root.Object);
            }
            
            locals.Add(localInfo);
            index++;
        }
    }
    catch (Exception ex)
    {
        _logger?.LogDebug(ex, "Failed to enumerate locals for frame");
    }
    
    return locals;
}
```

#### 3.4 Value Formatting

```csharp
private string FormatObjectValue(ClrObject obj)
{
    if (obj.IsNull)
        return "null";
    
    var type = obj.Type;
    if (type == null)
        return $"0x{obj.Address:X}";
    
    // Primitives - use our existing GetPrimitiveValue logic
    if (type.IsPrimitive)
    {
        return type.ElementType switch
        {
            ClrElementType.Boolean => obj.ReadField<bool>("m_value").ToString(),
            ClrElementType.Int32 => obj.ReadBoxedValue<int>().ToString(),
            ClrElementType.Int64 => obj.ReadBoxedValue<long>().ToString(),
            ClrElementType.Double => obj.ReadBoxedValue<double>().ToString(),
            // ... etc
            _ => $"0x{obj.Address:X}"
        };
    }
    
    // Strings - reuse existing logic
    if (type.IsString)
    {
        var str = obj.AsString();
        if (str != null)
        {
            if (str.Length > 100)
                return $"\"{str[..100]}...\" (len={str.Length})";
            return $"\"{str}\"";
        }
        return "null";
    }
    
    // Reference types - show address and type
    return $"0x{obj.Address:X16} ({type.Name})";
}

private object? GetPrimitiveValue(ClrObject obj)
{
    if (obj.IsNull || obj.Type == null)
        return null;
    
    if (!obj.Type.IsPrimitive)
        return null;  // Only return primitives for JSON Value field
    
    try
    {
        return obj.Type.ElementType switch
        {
            ClrElementType.Boolean => obj.ReadBoxedValue<bool>(),
            ClrElementType.Int32 => obj.ReadBoxedValue<int>(),
            ClrElementType.Int64 => obj.ReadBoxedValue<long>(),
            ClrElementType.Double => obj.ReadBoxedValue<double>(),
            ClrElementType.Float => obj.ReadBoxedValue<float>(),
            _ => null
        };
    }
    catch
    {
        return null;
    }
}
```

#### 3.5 Alternative: Reuse Existing InspectObject

Since we already have `ClrMdAnalyzer.InspectObject()`, we could reuse it for argument/local values:

```csharp
private string FormatObjectValue(ClrObject obj)
{
    if (obj.IsNull) return "null";
    
    // Use shallow inspection (depth=1) for argument/local values
    var inspection = InspectObject(obj.Address, maxDepth: 1, maxArrayElements: 3);
    
    if (inspection?.Error != null)
        return $"0x{obj.Address:X}";
    
    // Format from inspection result
    return FormatInspectionAsValue(inspection);
}
```

This reuses our robust object inspection logic.

### Phase 4: Register Fetching (Top Frame Only)

**File**: `DebuggerMcp/LldbManager.cs`

#### 4.1 Register Set Model

```csharp
public class RegisterSet
{
    // ARM64 registers
    public Dictionary<string, ulong> GeneralPurpose { get; set; } = new();
    public ulong FramePointer { get; set; }
    public ulong LinkRegister { get; set; }
    public ulong StackPointer { get; set; }
    public ulong ProgramCounter { get; set; }
    public uint CPSR { get; set; }
    
    // x64 registers (alternative)
    // public ulong RAX, RBX, RCX, RDX, RSI, RDI, RBP, RSP, RIP, R8-R15;
}
```

#### 4.2 Fetch Top Frame Registers

```csharp
/// <summary>
/// Fetches registers for the top frame of each thread.
/// Much faster than per-frame register reads (~3ms per thread vs ~3ms per frame).
/// </summary>
public async Task<Dictionary<uint, RegisterSet>> GetTopFrameRegistersAsync(
    IEnumerable<uint> threadIds)
{
    var result = new Dictionary<uint, RegisterSet>();
    
    foreach (var tid in threadIds)
    {
        try
        {
            // Select thread
            ExecuteCommand($"thread select {tid}");
            
            // Read registers
            var regOutput = ExecuteCommand("register read");
            
            result[tid] = ParseRegisters(regOutput);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to read registers for thread {ThreadId}", tid);
        }
    }
    
    return result;
}

private RegisterSet ParseRegisters(string output)
{
    var regs = new RegisterSet();
    
    // Parse ARM64 format:
    // x0 = 0x0000000000000000
    // fp = 0x0000ffffca31ade0
    // lr = 0x0000f5855a9b97e4
    // sp = 0x0000ffffca31ade0
    // pc = 0x0000f5855a9b9800
    // cpsr = 0x80001000
    
    var regex = new Regex(@"(\w+)\s*=\s*(0x[0-9a-fA-F]+)", RegexOptions.Compiled);
    foreach (Match match in regex.Matches(output))
    {
        var name = match.Groups[1].Value.ToLower();
        var value = Convert.ToUInt64(match.Groups[2].Value, 16);
        
        switch (name)
        {
            case "fp": regs.FramePointer = value; break;
            case "lr": regs.LinkRegister = value; break;
            case "sp": regs.StackPointer = value; break;
            case "pc": regs.ProgramCounter = value; break;
            case "cpsr": regs.CPSR = (uint)value; break;
            default:
                if (name.StartsWith("x") || name.StartsWith("r"))
                    regs.GeneralPurpose[name] = value;
                break;
        }
    }
    
    return regs;
}
```

### Phase 5: JSON Output Format

Like our other ClrMD commands (`dumpobj`, `dumpmodule`, `name2ee`), the output will be JSON-serializable for API use.

#### 5.1 Example JSON Output

```json
{
  "threads": [
    {
      "osThreadId": "0x374",
      "managedThreadId": 1,
      "isAlive": true,
      "isBackground": false,
      "topFrameRegisters": {
        "generalPurpose": {
          "x0": "0x0000c29d3cbfa218",
          "x1": "0x0000000000000080",
          "x19": "0x0000c29d3cbfa1f0"
        },
        "framePointer": "0x0000ffffca31ade0",
        "linkRegister": "0x0000f5855a9b97e4",
        "stackPointer": "0x0000ffffca31ade0",
        "programCounter": "0x0000f5855a9b9800"
      },
      "frames": [
        {
          "frameIndex": 0,
          "stackPointer": "0x0000FFFFCA31B4C0",
          "instructionPointer": "0x0000F58519EEA7F8",
          "kind": "Managed",
          "method": {
            "signature": "System.Threading.Monitor.Wait(System.Object, Int32)",
            "typeName": "System.Threading.Monitor",
            "methodName": "Wait",
            "metadataToken": 100663401
          },
          "sourceLocation": {
            "sourceFile": "/_/src/coreclr/System.Private.CoreLib/src/System/Threading/Monitor.CoreCLR.cs",
            "lineNumber": 312,
            "columnNumber": 9
          },
          "arguments": [
            {
              "name": "obj",
              "typeName": "System.Object",
              "address": "0x0000FFFFCA31B4E8",
              "value": "0x0000f57d3c41e258",
              "hasValue": true
            },
            {
              "name": "millisecondsTimeout",
              "typeName": "System.Int32",
              "address": "0x0000FFFFCA31B4E0",
              "value": -1,
              "hasValue": true
            }
          ],
          "locals": [
            {
              "index": 0,
              "name": "lockTaken",
              "typeName": "System.Boolean",
              "address": "0x0000FFFFCA31B4D8",
              "value": true,
              "hasValue": true
            }
          ]
        },
        {
          "frameIndex": 1,
          "stackPointer": "0x0000FFFFCA31B5A0",
          "instructionPointer": "0x0000F58519EFE2E8",
          "kind": "Managed",
          "method": {
            "signature": "System.Threading.ManualResetEventSlim.Wait(Int32, System.Threading.CancellationToken)",
            "typeName": "System.Threading.ManualResetEventSlim",
            "methodName": "Wait"
          },
          "sourceLocation": {
            "sourceFile": "/_/src/libraries/System.Private.CoreLib/src/System/Threading/ManualResetEventSlim.cs",
            "lineNumber": 587
          },
          "arguments": [
            {
              "name": "this",
              "typeName": "System.Threading.ManualResetEventSlim",
              "address": "0x0000FFFFCA31B5C8",
              "value": "0x0000f57d3c41e230",
              "hasValue": true
            },
            {
              "name": "millisecondsTimeout",
              "typeName": "System.Int32",
              "value": -1,
              "hasValue": true
            },
            {
              "name": "cancellationToken",
              "typeName": "System.Threading.CancellationToken",
              "hasValue": false
            }
          ],
          "locals": []
        }
      ]
    }
  ],
  "totalThreads": 42,
  "totalFrames": 215,
  "durationMs": 534
}
```

#### 5.2 JSON Serialization

```csharp
public string GetAllThreadStacksJson(bool includeRegisters = true)
{
    var result = GetAllThreadStacks(includeRegisters);
    
    return JsonSerializer.Serialize(result, new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
}
```

### Phase 6: Integration

#### 6.1 Replace ALL Internal `clrstack` Usage

**Goal**: Remove every `ExecuteCommandAsync("clrstack...")` call from the codebase.

**File**: `DebuggerMcp/Analysis/DotNetCrashAnalyzer.cs`

Current `clrstack` calls to replace:

| Location | Current Command | Replacement |
|----------|-----------------|-------------|
| `AnalyzeDotNetCrashAsync` | `clrstack -a -r -all` | `GetAllThreadStacks()` |
| Thread analysis | `clrstack` parsing | Direct from `ClrStackResult` |
| Exception analysis | Frame inspection | `ClrStackResult.Frames` |

**Important**: We still need `bt all` for native frames!

```csharp
// Current approach (keep native frame collection):
var btAllOutput = await ExecuteCommandAsync("bt all");
result.RawCommands!["bt all"] = btAllOutput;
ParseLldbBacktraceAll(btAllOutput, result);  // Populates native frames

// NEW: Replace clrstack with ClrMD
var clrStackResult = _clrMdAnalyzer.GetAllThreadStacks(
    includeRegisters: true,
    includeArguments: true,
    includeLocals: true
);

// Merge: ClrMD managed frames + LLDB native frames (by SP)
MergeClrStackWithNativeFrames(clrStackResult, result);
```

**Merged approach** - best of both worlds:
1. `bt all` gives us native frames with SP values
2. ClrMD gives us managed frames with args/locals/source
3. Merge by stack pointer for complete picture

**New method to merge ClrMD results with native frames**:

```csharp
/// <summary>
/// Merges ClrMD managed stack data with existing native frames from bt all.
/// Uses stack pointer to interleave correctly.
/// </summary>
private void MergeClrStackWithNativeFrames(ClrStackResult clrStack, CrashAnalysisResult result)
{
    foreach (var clrThread in clrStack.Threads)
    {
        // Find or create thread info
        var threadKey = clrThread.OSThreadId.ToString();
        if (!result.Threads.ContainsKey(threadKey))
        {
            result.Threads[threadKey] = new ThreadInfo
            {
                ThreadId = threadKey,
                OSThreadId = clrThread.OSThreadId,
                ManagedThreadId = clrThread.ManagedThreadId
            };
        }
        var threadInfo = result.Threads[threadKey];
        
        // Build a lookup of managed frames by SP
        var managedFramesBySP = clrThread.Frames
            .ToDictionary(f => f.StackPointer, f => f);
        
        // Merge with existing native frames
        foreach (var existingFrame in threadInfo.CallStack)
        {
            // Try to find matching managed frame by SP
            if (ulong.TryParse(existingFrame.StackPointer?.TrimStart('0', 'x', 'X'), 
                NumberStyles.HexNumber, null, out var sp))
            {
                if (managedFramesBySP.TryGetValue(sp, out var managedFrame))
                {
                    // Enrich native frame with managed info
                    existingFrame.Function = managedFrame.Method?.Signature ?? existingFrame.Function;
                    existingFrame.SourceFile = managedFrame.SourceLocation?.SourceFile;
                    existingFrame.LineNumber = managedFrame.SourceLocation?.LineNumber;
                    existingFrame.IsManagedFrame = true;
                    
                    // Add parameters
                    foreach (var arg in managedFrame.Arguments.Where(a => a.HasValue))
                    {
                        existingFrame.Parameters[arg.Name ?? $"arg{arg.Index}"] = new ParameterInfo
                        {
                            Type = arg.TypeName,
                            Value = arg.ValueString
                        };
                    }
                    
                    // Add locals
                    foreach (var local in managedFrame.Locals.Where(l => l.HasValue))
                    {
                        existingFrame.Locals[local.Name ?? $"local_{local.Index}"] = new LocalInfo
                        {
                            Type = local.TypeName,
                            Value = local.ValueString
                        };
                    }
                    
                    // Remove from lookup (processed)
                    managedFramesBySP.Remove(sp);
                }
            }
        }
        
        // Add any managed-only frames that weren't in bt all
        // (This shouldn't happen often, but handles edge cases)
        foreach (var orphanFrame in managedFramesBySP.Values.OrderBy(f => f.StackPointer))
        {
            threadInfo.CallStack.Add(new StackFrame
            {
                FrameNumber = threadInfo.CallStack.Count,
                StackPointer = $"0x{orphanFrame.StackPointer:X}",
                InstructionPointer = $"0x{orphanFrame.InstructionPointer:X}",
                Function = orphanFrame.Method?.Signature,
                SourceFile = orphanFrame.SourceLocation?.SourceFile,
                LineNumber = orphanFrame.SourceLocation?.LineNumber,
                IsManagedFrame = true
            });
        }
        
        // Re-sort by SP and renumber
        var sorted = threadInfo.CallStack
            .OrderBy(f => ParseSP(f.StackPointer))
            .ToList();
        for (int i = 0; i < sorted.Count; i++)
            sorted[i].FrameNumber = i;
        threadInfo.CallStack = sorted;
        
        // Add registers if available
        if (clrThread.TopFrameRegisters != null)
        {
            threadInfo.TopFrameRegisters = clrThread.TopFrameRegisters;
        }
    }
}
```

#### 6.2 MCP Tool

**File**: `DebuggerMcp/McpTools/ObjectInspectionTools.cs`

```csharp
[McpServerTool]
[Description("Gets managed call stacks for all threads using ClrMD. Returns JSON with frames, arguments, locals, and registers.")]
public string ClrStack(
    [Description("Session ID from CreateSession")] string sessionId,
    [Description("User ID that owns the session")] string userId,
    [Description("Include registers for top frame of each thread")] bool includeRegisters = true,
    [Description("Include method arguments")] bool includeArguments = true,
    [Description("Include local variables")] bool includeLocals = true,
    [Description("Filter to specific thread ID (0 = all threads)")] uint threadId = 0)
{
    var session = GetSession(sessionId, userId);
    
    if (session.ClrMdAnalyzer?.IsOpen != true)
    {
        return JsonSerializer.Serialize(new { error = "ClrMD not available" });
    }
    
    try
    {
        var result = session.ClrMdAnalyzer.GetAllThreadStacks(
            includeRegisters, 
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
        
        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "Error in ClrStack");
        return JsonSerializer.Serialize(new { error = ex.Message });
    }
}
```

#### 6.3 CLI Commands

**File**: `DebuggerMcp.Cli/Program.cs`

Add new commands in `cmd` mode:

```csharp
case "clrstack":
case "cs":
    await HandleClrStackAsync(cliArgs, console, output, state, mcpClient);
    continue;
```

**Help text update**:

```csharp
output.Markup("  [cyan]/clrstack[/]          Get all thread stacks (ClrMD, fast)");
output.Markup("  [cyan]/cs[/]                Alias for /clrstack");
output.Markup("  [cyan]/clrstack <tid>[/]    Get stack for specific thread");
output.Markup("  [cyan]/clrstack --no-regs[/] Skip register fetching (faster)");
```

**Handler implementation**:

```csharp
private static async Task HandleClrStackAsync(
    string[] args,
    IAnsiConsole console,
    ConsoleOutput output,
    ShellState state,
    McpClient mcpClient)
{
    if (string.IsNullOrEmpty(state.CurrentSessionId))
    {
        output.Error("No active session. Use 'open <dumpId>' first.");
        return;
    }

    var includeRegisters = !args.Contains("--no-regs");
    uint threadId = 0;
    
    // Check for thread ID argument
    var tidArg = args.FirstOrDefault(a => !a.StartsWith("-"));
    if (tidArg != null)
    {
        if (tidArg.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uint.TryParse(tidArg[2..], NumberStyles.HexNumber, null, out threadId);
        else
            uint.TryParse(tidArg, out threadId);
    }

    output.Dim("Fetching thread stacks via ClrMD...");
    
    var result = await mcpClient.ClrStackAsync(
        state.CurrentSessionId,
        state.UserId,
        includeRegisters: includeRegisters,
        threadId: threadId
    );
    
    // Parse and display
    var stackResult = JsonSerializer.Deserialize<ClrStackResult>(result);
    if (stackResult == null)
    {
        output.Error("Failed to parse result");
        return;
    }
    
    output.Success($"Found {stackResult.TotalThreads} threads, {stackResult.TotalFrames} frames ({stackResult.DurationMs}ms)");
    output.WriteLine();
    
    foreach (var thread in stackResult.Threads)
    {
        output.Markup($"[yellow]OS Thread Id: 0x{thread.OSThreadId:x}[/] (Managed: {thread.ManagedThreadId})");
        
        foreach (var frame in thread.Frames)
        {
            var method = frame.Method?.Signature ?? frame.Kind ?? "???";
            var source = frame.SourceLocation != null 
                ? $" [dim]@ {Path.GetFileName(frame.SourceLocation.SourceFile)}:{frame.SourceLocation.LineNumber}[/]"
                : "";
            
            output.Markup($"  [green]#{frame.FrameIndex:D2}[/] {method}{source}");
            
            // Show arguments
            foreach (var arg in frame.Arguments.Where(a => a.HasValue))
            {
                output.Markup($"      [dim]{arg.Name}[/] = [cyan]{arg.ValueString}[/]");
            }
            
            // Show locals (first 5 only to avoid clutter)
            foreach (var local in frame.Locals.Where(l => l.HasValue).Take(5))
            {
                var name = local.Name ?? $"local_{local.Index}";
                output.Markup($"      [dim]{name}[/] = [blue]{local.ValueString}[/]");
            }
        }
        
        // Show top frame registers if available
        if (thread.TopFrameRegisters != null)
        {
            output.Dim($"  Registers (top frame): SP={thread.TopFrameRegisters.StackPointer:X} PC={thread.TopFrameRegisters.ProgramCounter:X}");
        }
        
        output.WriteLine();
    }
}
```

#### 6.4 McpClient Method

**File**: `DebuggerMcp.Cli/Client/McpClient.cs`

```csharp
public async Task<string> ClrStackAsync(
    string sessionId,
    string userId,
    bool includeRegisters = true,
    bool includeArguments = true,
    bool includeLocals = true,
    uint threadId = 0,
    CancellationToken cancellationToken = default)
{
    var args = new Dictionary<string, object?>
    {
        ["sessionId"] = sessionId,
        ["userId"] = userId,
        ["includeRegisters"] = includeRegisters,
        ["includeArguments"] = includeArguments,
        ["includeLocals"] = includeLocals,
        ["threadId"] = threadId
    };

    args["kind"] = "clr_stack";
    return await CallToolAsync("inspect", args, cancellationToken);
}
```

## Testing Plan

### Unit Tests

1. **Stack walking correctness**
   - Compare frame count with SOS output
   - Verify SP/IP values match
   - Check method signatures

2. **Sequence point resolution**
   - Test with known PDB files
   - Verify line numbers match source
   - Handle missing PDBs gracefully

3. **Argument/Local enumeration**
   - Test primitive types
   - Test reference types
   - Test optimized code (`<no data>`)

4. **Register parsing**
   - Parse ARM64 format
   - Parse x64 format
   - Handle partial output

### Integration Tests

1. Compare output with SOS `clrstack -a -r -all` for sample dumps
2. Performance benchmarks (target: <700ms)
3. Stability tests (no crashes)

## Rollout Plan

1. **Phase 1-3**: Implement core functionality (stack + source + args/locals)
2. **Phase 4**: Add register support (top frame only via LLDB)
3. **Phase 5**: JSON output format
4. **Phase 6**: Full integration
   - Replace ALL internal `clrstack` calls in `DotNetCrashAnalyzer`
   - Use `inspect(kind="clr_stack")`
   - Add CLI commands `/clrstack`, `/cs`
   - Add `McpClient.ClrStackAsync()`
5. **Testing**: Verify output matches SOS for accuracy
6. **Cleanup**: Remove all SOS `clrstack` parsing code

### Files to Modify

| File | Changes |
|------|---------|
| `ClrMdAnalyzer.cs` | Add `GetAllThreadStacks()`, sequence point resolution |
| `SequencePointResolver.cs` | New file for PDB parsing |
| `LldbManager.cs` | Add `GetTopFrameRegistersAsync()` |
| `DotNetCrashAnalyzer.cs` | Replace all `clrstack` calls |
| `ObjectInspectionTools.cs` | Add `ClrStack` MCP tool |
| `McpClient.cs` | Add `ClrStackAsync()` |
| `Program.cs` (CLI) | Add `/clrstack`, `/cs` commands |

### Internal Calls to Replace

```bash
# Find all clrstack calls in the codebase
grep -r "clrstack" DebuggerMcp/Analysis/ --include="*.cs"
```

Expected locations:
- `DotNetCrashAnalyzer.cs`: Main analysis loop
- Possibly `CrashAnalyzer.cs`: Base class parsing

## Success Metrics

| Metric | Current | Target |
|--------|---------|--------|
| Duration | 12,880ms | <700ms |
| Stability | Can crash | No crashes |
| Accuracy | Baseline | ≥99% match |
| Coverage | 100% | 100% |

## Implementation Notes

### API Clarifications (Verify During Implementation)

1. **Stack Roots Enumeration**
   - The plan shows `frame.EnumerateRoots()` but ClrMD uses `thread.EnumerateStackRoots()` at the thread level
   - Need to correlate roots with frames by comparing `ClrStackRoot.Address` range with `ClrStackFrame.StackPointer`
   - Implementation should handle cases where roots don't map cleanly to frames

2. **JSON Hex Formatting**
   - `OSThreadId` in JSON example shows `"0x374"` (string) but C# model is `uint`
   - Options: Add `[JsonConverter]` for hex formatting, or use string type, or accept numeric output
   - Recommendation: Keep `uint` and accept numeric JSON output (simpler, machine-readable)

3. **Faulting Thread Detection**
   - Use `runtime.GetCurrentException()?.Thread` to identify faulting thread
   - Set `IsFaulting = true` for that thread

4. **ClrMD 3.x API**
   - We use ClrMD 3.1.512801
   - `ClrStackFrame` may not have `EnumerateRoots()` - verify actual API
   - May need to use `ClrThread.EnumerateStackRoots()` with SP correlation

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| ClrMD API differences across versions | Medium | Test with multiple .NET versions |
| PDB format variations | Low | Handle Windows PDB gracefully (skip source) |
| Optimized code missing values | Low | Same as SOS - expected behavior |
| Register fetch failures | Low | Graceful degradation - show frames without registers |
| Stack roots → frame correlation complexity | Medium | Build SP-range lookup table for efficiency |

## Timeline Estimate

| Phase | Effort | Dependencies |
|-------|--------|--------------|
| Phase 1: Core stack walking | 2-3 hours | None |
| Phase 2: Sequence points | 2-3 hours | Phase 1 |
| Phase 3: Args/Locals | 2-3 hours | Phase 1 |
| Phase 4: Registers | 1-2 hours | Phase 1 |
| Phase 5: Formatting | 1-2 hours | All above |
| Phase 6: Integration | 2-3 hours | All above |
| Testing | 2-3 hours | All above |
| **Total** | **12-19 hours** | |

## Appendix: ClrMD API Reference

### Key Types (ClrMD 2.x / 3.x)

```csharp
// Thread enumeration
ClrRuntime.Threads : IEnumerable<ClrThread>

// Thread properties
ClrThread.OSThreadId : uint
ClrThread.ManagedThreadId : int
ClrThread.IsAlive : bool
ClrThread.IsBackground : bool

// Stack walking  
ClrThread.EnumerateStackTrace() : IEnumerable<ClrStackFrame>

// Frame properties
ClrStackFrame.StackPointer : ulong
ClrStackFrame.InstructionPointer : ulong
ClrStackFrame.Kind : ClrStackFrameKind  // Managed, Runtime, Unknown
ClrStackFrame.Method : ClrMethod?

// Method info
ClrMethod.Signature : string
ClrMethod.Name : string
ClrMethod.Type : ClrType  // Declaring type
ClrMethod.MetadataToken : int
ClrMethod.NativeCode : ulong
ClrMethod.ILOffsetMap : ILToNativeMap[]  // For IL offset calculation

// Stack roots (for args/locals) - at THREAD level, not frame level
ClrThread.EnumerateStackRoots() : IEnumerable<ClrStackRoot>
ClrStackRoot.Address : ulong         // Location on stack
ClrStackRoot.Object : ClrObject      // Referenced object
ClrStackRoot.RootKind : ClrRootKind  // Type of root

// NOTE: Must correlate roots to frames by comparing root.Address with frame.StackPointer

// Object access
ClrObject.Address : ulong
ClrObject.Type : ClrType?
ClrObject.IsNull : bool
ClrObject.AsString() : string?
ClrObject.ReadBoxedValue<T>() : T
ClrObject.ReadField<T>(string name) : T
```

### IL Offset Mapping

```csharp
// ILToNativeMap structure
struct ILToNativeMap
{
    public int ILOffset;
    public ulong StartAddress;  // Native start
    public ulong EndAddress;    // Native end
}

// Usage: Find IL offset for a native IP
foreach (var entry in method.ILOffsetMap)
{
    if (ip >= entry.StartAddress && ip < entry.EndAddress)
        return entry.ILOffset;
}
```

### PDB Sequence Points

```csharp
// System.Reflection.Metadata
MetadataReaderProvider.FromPortablePdbStream(stream)
MetadataReader.MethodDebugInformation
MethodDebugInformation.GetSequencePoints()

// Sequence point properties
SequencePoint.Offset : int (IL offset)
SequencePoint.Document : DocumentHandle
SequencePoint.StartLine : int
SequencePoint.StartColumn : int
SequencePoint.EndLine : int
SequencePoint.EndColumn : int
SequencePoint.IsHidden : bool
```

---

## Known Limitations

### Per-Frame Registers for Managed Frames

**Limitation**: LLDB can only provide registers for frames it understands (native frames and some JIT frames with debug info). Managed frames that exist only in ClrMD's view (JIT-compiled code without LLDB symbols) will not have registers.

**Cause**: This is a fundamental limitation of the LLDB/ClrMD integration:
- ClrMD walks the managed stack independently and provides Stack Pointers (SPs) for managed frames
- LLDB walks the native stack and can only read registers for frames it knows about
- JIT-compiled managed frames have SPs that LLDB is not aware of

**Impact**: In a typical crash dump:
- Native frames: ✅ Will have registers
- Managed frames visible to LLDB: ✅ Will have registers  
- Managed-only JIT frames: ❌ No registers (SP from ClrMD doesn't match any LLDB frame)

**Example from testing**: In a 82-frame faulting thread stack, 71 frames had registers and 11 managed-only frames did not.

This is an accepted limitation. The alternative (using nearest-frame heuristic) would provide inaccurate register values.

### Parameters/Locals for Some Frames

**Limitation**: ClrMD's `EnumerateStackRoots()` only returns GC roots, not value types and primitives on the stack.

**Impact**: Some frames may have empty parameters/locals lists even though the method has parameters.

### Source Location Inconsistency

**Limitation**: Some frames may lack source file/line information even when other frames from the same module have it.

**Causes**:
- Missing PDB files for some modules
- Compiler-generated code (async state machines, closures)
- Optimized code with stripped debug info
- Dynamic methods or lightweight code generation
