using System.Globalization;
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Analyzes .NET dumps using ClrMD to extract assembly metadata from dump memory.
/// This complements the SOS-based analysis by providing access to assembly attributes
/// that are not easily accessible via debugger commands.
/// </summary>
public class ClrMdAnalyzer : IDisposable
{
    private readonly ILogger? _logger;
    private DataTarget? _dataTarget;
    private ClrRuntime? _runtime;
    private readonly object _lock = new();
    
    /// <summary>
    /// Skip modules with metadata larger than 50MB to avoid memory issues.
    /// </summary>
    private const int MaxMetadataSize = 50 * 1024 * 1024;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClrMdAnalyzer"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public ClrMdAnalyzer(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether the dump is currently open.
    /// </summary>
    public bool IsOpen => _runtime != null;

    /// <summary>
    /// Opens a dump file for analysis.
    /// </summary>
    /// <param name="dumpPath">Path to the dump file.</param>
    /// <returns>True if the dump was opened successfully; false otherwise.</returns>
    public bool OpenDump(string dumpPath)
    {
        lock (_lock)
        {
            try
            {
                CloseDump();
                
                _dataTarget = DataTarget.LoadDump(dumpPath);
                
                var clrInfo = _dataTarget.ClrVersions.FirstOrDefault();
                if (clrInfo == null)
                {
                    _logger?.LogWarning("[ClrMD] No CLR runtime found in dump: {Path}", dumpPath);
                    CloseDump();
                    return false;
                }
                
                _runtime = clrInfo.CreateRuntime();
                _logger?.LogInformation("[ClrMD] Opened dump: {Path}, CLR: {Version}", 
                    dumpPath, clrInfo.Version);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[ClrMD] Failed to open dump: {Path}", dumpPath);
                CloseDump();
                return false;
            }
        }
    }

    /// <summary>
    /// Closes the dump and releases resources.
    /// </summary>
    public void CloseDump()
    {
        lock (_lock)
        {
            _runtime?.Dispose();
            _dataTarget?.Dispose();
            _runtime = null;
            _dataTarget = null;
        }
    }

    /// <summary>
    /// Gets all modules with their assembly attributes from dump memory.
    /// </summary>
    /// <returns>List of enriched module information.</returns>
    public List<EnrichedModuleInfo> GetAllModulesWithAttributes()
    {
        var result = new List<EnrichedModuleInfo>();
        
        lock (_lock)
        {
            if (_runtime == null || _dataTarget == null)
                return result;
            
            foreach (var module in _runtime.EnumerateModules())
            {
                try
                {
                    var info = new EnrichedModuleInfo
                    {
                        Name = Path.GetFileNameWithoutExtension(module.Name) ?? "Unknown",
                        FullPath = module.Name,
                        ImageBase = module.ImageBase,
                        Size = module.Size,
                        IsDynamic = module.IsDynamic,
                        IsPEFile = module.IsPEFile
                    };
                    
                    // Only read attributes from PE files with metadata
                    if (module.IsPEFile && !module.IsDynamic && module.MetadataAddress != 0)
                    {
                        var (attributes, version) = ReadAttributesFromMemory(module);
                        info.Attributes = attributes;
                        info.AssemblyVersion = version;
                    }
                    
                    result.Add(info);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "[ClrMD] Error processing module: {Name}", module.Name);
                }
            }
        }
        
        return result;
    }

    /// <summary>
    /// Gets assembly attributes for a specific module.
    /// </summary>
    /// <param name="moduleName">The module name (with or without extension).</param>
    /// <returns>List of assembly attributes.</returns>
    public List<AssemblyAttributeInfo> GetAssemblyAttributes(string moduleName)
    {
        lock (_lock)
        {
            if (_runtime == null || _dataTarget == null)
                return new List<AssemblyAttributeInfo>();
            
            var module = _runtime.EnumerateModules()
                .FirstOrDefault(m => 
                    Path.GetFileNameWithoutExtension(m.Name)
                        ?.Equals(Path.GetFileNameWithoutExtension(moduleName), 
                            StringComparison.OrdinalIgnoreCase) == true);
            
            if (module == null)
            {
                _logger?.LogDebug("[ClrMD] Module not found: {Name}", moduleName);
                return new List<AssemblyAttributeInfo>();
            }
            
            return ReadAttributesFromMemory(module).attributes;
        }
    }

    /// <summary>
    /// Reads assembly attributes and version from module metadata stored in dump memory.
    /// </summary>
    private (List<AssemblyAttributeInfo> attributes, string? version) ReadAttributesFromMemory(ClrModule module)
    {
        var result = new List<AssemblyAttributeInfo>();
        string? version = null;
        
        try
        {
            var metadataAddress = module.MetadataAddress;
            var metadataLength = module.MetadataLength;
            
            if (metadataAddress == 0 || metadataLength == 0)
            {
                _logger?.LogDebug("[ClrMD] No metadata for: {Name}", module.Name);
                return (result, version);
            }
            
            // Skip very large metadata to avoid memory issues
            if (metadataLength > MaxMetadataSize)
            {
                _logger?.LogDebug("[ClrMD] Metadata too large ({Size}MB): {Name}", 
                    metadataLength / 1024 / 1024, module.Name);
                return (result, version);
            }
            
            // Read metadata bytes from dump memory
            // Safe to cast to int since we checked against MaxMetadataSize above
            var metadataBytes = new byte[(int)metadataLength];
            int bytesRead = _dataTarget!.DataReader.Read(metadataAddress, metadataBytes);
            
            if (bytesRead == 0)
            {
                _logger?.LogDebug("[ClrMD] Could not read metadata for: {Name}", module.Name);
                return (result, version);
            }
            
            if (bytesRead != (int)metadataLength)
            {
                Array.Resize(ref metadataBytes, bytesRead);
            }
            
            // Parse with MetadataReader
            unsafe
            {
                fixed (byte* ptr = metadataBytes)
                {
                    var reader = new MetadataReader(ptr, metadataBytes.Length);
                    (result, version) = ExtractAttributes(reader);
                }
            }
        }
        catch (BadImageFormatException)
        {
            _logger?.LogDebug("[ClrMD] Invalid metadata for: {Name}", module.Name);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[ClrMD] Error reading metadata for: {Name}", module.Name);
        }
        
        return (result, version);
    }

    /// <summary>
    /// Extracts assembly attributes and version from a MetadataReader.
    /// </summary>
    private (List<AssemblyAttributeInfo> attributes, string? version) ExtractAttributes(MetadataReader reader)
    {
        var result = new List<AssemblyAttributeInfo>();
        string? version = null;
        
        if (!reader.IsAssembly)
            return (result, version);
        
        var assemblyDef = reader.GetAssemblyDefinition();
        
        // Extract assembly version from the assembly definition
        var ver = assemblyDef.Version;
        if (ver.Major != 0 || ver.Minor != 0 || ver.Build != 0 || ver.Revision != 0)
        {
            version = ver.ToString();
        }
        
        foreach (var attrHandle in assemblyDef.GetCustomAttributes())
        {
            try
            {
                var attr = reader.GetCustomAttribute(attrHandle);
                var attrInfo = DecodeAttribute(reader, attr);
                if (attrInfo != null)
                {
                    result.Add(attrInfo);
                }
            }
            catch
            {
                // Skip malformed attributes
            }
        }
        
        return (result, version);
    }

    /// <summary>
    /// Decodes a custom attribute from metadata.
    /// </summary>
    private AssemblyAttributeInfo? DecodeAttribute(MetadataReader reader, CustomAttribute attr)
    {
        string? typeName = null;
        
        if (attr.Constructor.Kind == HandleKind.MemberReference)
        {
            var memberRef = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
            if (memberRef.Parent.Kind == HandleKind.TypeReference)
            {
                var typeRef = reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                var ns = reader.GetString(typeRef.Namespace);
                var name = reader.GetString(typeRef.Name);
                typeName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            }
        }
        else if (attr.Constructor.Kind == HandleKind.MethodDefinition)
        {
            var methodDef = reader.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor);
            var typeDef = reader.GetTypeDefinition(methodDef.GetDeclaringType());
            var ns = reader.GetString(typeDef.Namespace);
            var name = reader.GetString(typeDef.Name);
            typeName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }
        
        if (string.IsNullOrEmpty(typeName))
            return null;
        
        var (value, key) = DecodeBlob(reader, attr, typeName);
        
        return new AssemblyAttributeInfo
        {
            AttributeType = typeName,
            Value = value,
            Key = key
        };
    }

    /// <summary>
    /// Decodes the attribute blob value according to ECMA-335 II.23.3.
    /// Handles known attribute types with specific signatures.
    /// </summary>
    private (string? Value, string? Key) DecodeBlob(MetadataReader reader, CustomAttribute attr, string typeName)
    {
        try
        {
            var blob = reader.GetBlobReader(attr.Value);
            
            if (blob.Length < 2)
                return (null, null);
            
            var prolog = blob.ReadUInt16();
            if (prolog != 0x0001)
                return (null, null);
            
            // Handle known attribute types with specific signatures
            return typeName switch
            {
                "System.Reflection.AssemblyMetadataAttribute" => DecodeKeyValueString(ref blob),
                "System.Diagnostics.DebuggableAttribute" => DecodeDebuggable(ref blob),
                "System.Runtime.CompilerServices.RuntimeCompatibilityAttribute" => DecodeRuntimeCompatibility(ref blob),
                "System.Runtime.CompilerServices.CompilationRelaxationsAttribute" => DecodeCompilationRelaxations(ref blob),
                "System.CLSCompliantAttribute" => DecodeBoolAttribute(ref blob),
                "System.Runtime.InteropServices.ComVisibleAttribute" => DecodeBoolAttribute(ref blob),
                "System.Runtime.Versioning.TargetFrameworkAttribute" => DecodeTargetFramework(ref blob),
                "System.Reflection.AssemblyDelaySignAttribute" => DecodeBoolAttribute(ref blob),
                "System.Reflection.AssemblyKeyFileAttribute" => DecodeStringAttribute(ref blob),
                "System.Reflection.AssemblyKeyNameAttribute" => DecodeStringAttribute(ref blob),
                "System.Reflection.AssemblySignatureKeyAttribute" => DecodeStringAttribute(ref blob),
                "System.Reflection.AssemblyTrademarkAttribute" => DecodeStringAttribute(ref blob),
                "System.Reflection.AssemblyCultureAttribute" => DecodeStringAttribute(ref blob),
                "System.Reflection.AssemblyDefaultAliasAttribute" => DecodeStringAttribute(ref blob),
                _ => DecodeStringOrBinary(ref blob) // Default: try string, return "<binary>" if binary
            };
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Decodes AssemblyMetadataAttribute which has two string params (key, value).
    /// </summary>
    private (string? Value, string? Key) DecodeKeyValueString(ref BlobReader blob)
            {
                var key = ReadString(ref blob);
                var value = ReadString(ref blob);
                return (value, key);
            }

    /// <summary>
    /// Decodes DebuggableAttribute(DebuggingModes modes) - int32 enum flags.
    /// </summary>
    [Flags]
    private enum DebuggingModes
    {
        None = 0,
        Default = 1,
        DisableOptimizations = 256,
        IgnoreSymbolStoreSequencePoints = 2,
        EnableEditAndContinue = 4
    }

    private (string? Value, string? Key) DecodeDebuggable(ref BlobReader blob)
            {
        // DebuggableAttribute has two constructors:
        // 1. DebuggableAttribute(DebuggingModes modes) - 4 bytes (int32 enum) - modern, handled here
        // 2. DebuggableAttribute(bool, bool) - 2 bytes - legacy (.NET Framework), returns <binary>
        
        if (blob.RemainingBytes < 4) return ("<binary>", null);
        
        var modes = (DebuggingModes)blob.ReadInt32();
        
        if (modes == DebuggingModes.None)
            return ("None", null);
        
        var flags = new List<string>();
        if (modes.HasFlag(DebuggingModes.Default)) flags.Add("Default");
        if (modes.HasFlag(DebuggingModes.DisableOptimizations)) flags.Add("DisableOptimizations");
        if (modes.HasFlag(DebuggingModes.IgnoreSymbolStoreSequencePoints)) flags.Add("IgnoreSymbolStoreSequencePoints");
        if (modes.HasFlag(DebuggingModes.EnableEditAndContinue)) flags.Add("EnableEditAndContinue");
        
        return (flags.Count > 0 ? string.Join(", ", flags) : modes.ToString(), null);
    }

    // ECMA-335 II.23.1.16 Element types for custom attribute blob decoding
    private const byte ELEMENT_TYPE_BOOLEAN = 0x02;
    private const byte ELEMENT_TYPE_I1 = 0x04;   // sbyte
    private const byte ELEMENT_TYPE_U1 = 0x05;   // byte
    private const byte ELEMENT_TYPE_I2 = 0x06;   // short
    private const byte ELEMENT_TYPE_U2 = 0x07;   // ushort
    private const byte ELEMENT_TYPE_I4 = 0x08;   // int
    private const byte ELEMENT_TYPE_U4 = 0x09;   // uint
    private const byte ELEMENT_TYPE_I8 = 0x0A;   // long
    private const byte ELEMENT_TYPE_U8 = 0x0B;   // ulong
    private const byte ELEMENT_TYPE_R4 = 0x0C;   // float
    private const byte ELEMENT_TYPE_R8 = 0x0D;   // double
    private const byte ELEMENT_TYPE_STRING = 0x0E;
    private const byte SERIALIZATION_TYPE_FIELD = 0x53;
    private const byte SERIALIZATION_TYPE_PROPERTY = 0x54;

    /// <summary>
    /// Decodes RuntimeCompatibilityAttribute which has named property WrapNonExceptionThrows.
    /// </summary>
    private (string? Value, string? Key) DecodeRuntimeCompatibility(ref BlobReader blob)
    {
        // Skip to named arguments section
        // Format: NumNamed (uint16), then properties
        if (blob.RemainingBytes < 2) return ("<binary>", null);
        var numNamed = blob.ReadUInt16();
        if (numNamed == 0) return ("Default", null);
        
        var properties = new List<string>();
        
        // Each named arg: Kind (Field/Property), Type, Name (string), Value
        for (int i = 0; i < numNamed && blob.RemainingBytes > 2; i++)
        {
            try
            {
                var kind = blob.ReadByte(); // SERIALIZATION_TYPE_FIELD or SERIALIZATION_TYPE_PROPERTY
                var fieldType = blob.ReadByte();
                var name = ReadString(ref blob);
                
                if (string.IsNullOrEmpty(name) || blob.RemainingBytes < 1)
                    break;
                
                switch (fieldType)
                {
                    case ELEMENT_TYPE_BOOLEAN:
                        properties.Add($"{name}={((blob.ReadByte() != 0) ? "true" : "false")}");
                        break;
                    case ELEMENT_TYPE_I1:
                        properties.Add($"{name}={blob.ReadSByte()}");
                        break;
                    case ELEMENT_TYPE_U1:
                        properties.Add($"{name}={blob.ReadByte()}");
                        break;
                    case ELEMENT_TYPE_I2:
                        if (blob.RemainingBytes < 2) goto exitLoop;
                        properties.Add($"{name}={blob.ReadInt16()}");
                        break;
                    case ELEMENT_TYPE_U2:
                        if (blob.RemainingBytes < 2) goto exitLoop;
                        properties.Add($"{name}={blob.ReadUInt16()}");
                        break;
                    case ELEMENT_TYPE_I4:
                        if (blob.RemainingBytes < 4) goto exitLoop;
                        properties.Add($"{name}={blob.ReadInt32()}");
                        break;
                    case ELEMENT_TYPE_U4:
                        if (blob.RemainingBytes < 4) goto exitLoop;
                        properties.Add($"{name}={blob.ReadUInt32()}");
                        break;
                    case ELEMENT_TYPE_I8:
                        if (blob.RemainingBytes < 8) goto exitLoop;
                        properties.Add($"{name}={blob.ReadInt64()}");
                        break;
                    case ELEMENT_TYPE_U8:
                        if (blob.RemainingBytes < 8) goto exitLoop;
                        properties.Add($"{name}={blob.ReadUInt64()}");
                        break;
                    case ELEMENT_TYPE_R4:
                        if (blob.RemainingBytes < 4) goto exitLoop;
                        properties.Add($"{name}={blob.ReadSingle().ToString(CultureInfo.InvariantCulture)}");
                        break;
                    case ELEMENT_TYPE_R8:
                        if (blob.RemainingBytes < 8) goto exitLoop;
                        properties.Add($"{name}={blob.ReadDouble().ToString(CultureInfo.InvariantCulture)}");
                        break;
                    case ELEMENT_TYPE_STRING:
                        var strValue = ReadString(ref blob);
                        if (strValue != null)
                        {
                            // Escape quotes and backslashes for readable output
                            var escaped = strValue.Replace("\\", "\\\\").Replace("\"", "\\\"");
                            properties.Add($"{name}=\"{escaped}\"");
                        }
                        break;
                    default:
                        // Unknown type - can't safely continue parsing named args
                        goto exitLoop;
                }
            }
            catch
            {
                break;
            }
        }
        exitLoop:
        
        return (properties.Count > 0 ? string.Join(", ", properties) : "Default", null);
    }

    /// <summary>
    /// Decodes CompilationRelaxationsAttribute(int relaxations).
    /// </summary>
    private (string? Value, string? Key) DecodeCompilationRelaxations(ref BlobReader blob)
    {
        if (blob.RemainingBytes < 4) return ("<binary>", null);
        var relaxations = blob.ReadInt32();
        // 8 = CompilationRelaxations.NoStringInterning
        return relaxations == 8 ? ("NoStringInterning", null) : (relaxations.ToString(), null);
    }

    /// <summary>
    /// Decodes simple bool attributes (CLSCompliantAttribute, ComVisibleAttribute, etc.).
    /// </summary>
    private (string? Value, string? Key) DecodeBoolAttribute(ref BlobReader blob)
    {
        if (blob.RemainingBytes < 1) return ("<binary>", null);
        return (blob.ReadByte() != 0 ? "true" : "false", null);
    }

    /// <summary>
    /// Decodes simple string attributes.
    /// </summary>
    private (string? Value, string? Key) DecodeStringAttribute(ref BlobReader blob)
    {
                var value = ReadString(ref blob);
                return (value, null);
    }

    /// <summary>
    /// Decodes TargetFrameworkAttribute which has a string param and optional named FrameworkDisplayName.
    /// </summary>
    private (string? Value, string? Key) DecodeTargetFramework(ref BlobReader blob)
    {
        var frameworkName = ReadString(ref blob);
        
        // Try to read optional FrameworkDisplayName named parameter
        if (blob.RemainingBytes >= 2)
        {
            var numNamed = blob.ReadUInt16();
            if (numNamed > 0 && blob.RemainingBytes > 2)
            {
                try
                {
                    blob.ReadByte(); // Kind (SERIALIZATION_TYPE_FIELD or SERIALIZATION_TYPE_PROPERTY)
                    var fieldType = blob.ReadByte();
                    var name = ReadString(ref blob);
                    if (name == "FrameworkDisplayName" && fieldType == ELEMENT_TYPE_STRING)
                    {
                        var displayName = ReadString(ref blob);
                        if (!string.IsNullOrEmpty(displayName))
                            return ($"{frameworkName} ({displayName})", null);
            }
        }
        catch
        {
                    // Ignore parsing errors for optional part
                }
            }
        }
        
        return (frameworkName, null);
    }

    /// <summary>
    /// Attempts to decode as string. Returns "&lt;binary&gt;" if the data contains control characters.
    /// </summary>
    private (string? Value, string? Key) DecodeStringOrBinary(ref BlobReader blob)
    {
        var value = ReadString(ref blob);
        
        if (value == null)
            return ("<binary>", null);
        
        // Check if it contains control characters (likely binary data incorrectly read as string)
        if (value.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
            return ("<binary>", null);
        
        return (value, null);
    }

    /// <summary>
    /// Reads a serialized string from a blob (ECMA-335 II.23.3).
    /// </summary>
    private string? ReadString(ref BlobReader blob)
    {
        if (blob.RemainingBytes == 0)
            return null;
        
        var firstByte = blob.ReadByte();
        if (firstByte == 0xFF)
            return null;
        
        int length;
        if ((firstByte & 0x80) == 0)
        {
            // 1-byte length (0x00-0x7F)
            length = firstByte;
        }
        else if ((firstByte & 0xC0) == 0x80)
        {
            // 2-byte length - need 1 more byte
            if (blob.RemainingBytes < 1)
                return null;
            length = ((firstByte & 0x3F) << 8) | blob.ReadByte();
        }
        else
        {
            // 4-byte length - need 3 more bytes
            if (blob.RemainingBytes < 3)
                return null;
            length = ((firstByte & 0x1F) << 24) | (blob.ReadByte() << 16) | 
                     (blob.ReadByte() << 8) | blob.ReadByte();
        }
        
        if (length == 0)
            return string.Empty;
        
        // Validate we have enough bytes before reading
        if (blob.RemainingBytes < length)
            return null;
        
        var bytes = blob.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Releases all resources used by this instance.
    /// </summary>
    public void Dispose()
    {
        CloseDump();
    }
    
    // ============================================================================
    // Phase 2 ClrMD Enrichment Methods
    // ============================================================================
    
    /// <summary>
    /// Gets GC heap summary using ClrMD 3.x APIs.
    /// </summary>
    public GcSummary? GetGcSummary()
    {
        lock (_lock)
        {
            try
            {
                if (_runtime == null) return null;
                
                var heap = _runtime.Heap;
                
                // Calculate generation sizes by iterating segments
                var genSizes = new GenerationSizes();
                var segments = new List<GcSegmentInfo>();
                long totalSize = 0;
                
                foreach (var segment in heap.Segments)
                {
                    var segmentSize = (long)segment.Length;
                    totalSize += segmentSize;
                    
                    // Categorize by segment kind (GCSegmentKind enum in ClrMD 3.x)
                    switch (segment.Kind)
                    {
                        case GCSegmentKind.Generation0:
                            genSizes.Gen0 += segmentSize;
                            break;
                        case GCSegmentKind.Generation1:
                            genSizes.Gen1 += segmentSize;
                            break;
                        case GCSegmentKind.Generation2:
                            genSizes.Gen2 += segmentSize;
                            break;
                        case GCSegmentKind.Large:
                            genSizes.Loh += segmentSize;
                            break;
                        case GCSegmentKind.Pinned:
                            genSizes.Poh += segmentSize;
                            break;
                        case GCSegmentKind.Frozen:
                            // Frozen segments are read-only, add to Gen2 for simplicity
                            genSizes.Gen2 += segmentSize;
                            break;
                    }
                    
                    segments.Add(new GcSegmentInfo
                    {
                        Address = $"0x{segment.Start:X16}",
                        Size = segmentSize,
                        Kind = segment.Kind.ToString()
                    });
                }
                
                // Get finalizable object count
                int finalizableCount = 0;
                try
                {
                    finalizableCount = heap.EnumerateFinalizableObjects().Count();
                }
                catch
                {
                    // Some dumps may not have finalization data
                }
                
                // Note: Fragmentation is calculated more accurately during the combined heap analysis
                // which is always run. GcSummary.Fragmentation will be updated by DotNetCrashAnalyzer.
                
                return new GcSummary
                {
                    HeapCount = heap.SubHeaps.Length,
                    IsServerGC = heap.IsServer,
                    GcMode = heap.IsServer ? "Server" : "Workstation",
                    TotalHeapSize = totalSize,
                    GenerationSizes = genSizes,
                    Segments = segments,
                    FinalizableObjectCount = finalizableCount
                };
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[ClrMD] Failed to get GC summary");
                return null;
            }
        }
    }
    
    /// <summary>
    /// Gets enhanced thread information from ClrMD.
    /// Returns a dictionary keyed by OS thread ID.
    /// </summary>
    public Dictionary<uint, ClrMdThreadInfo> GetEnhancedThreadInfo()
    {
        var result = new Dictionary<uint, ClrMdThreadInfo>();
        
        lock (_lock)
        {
            try
            {
                if (_runtime == null) return result;
                
                foreach (var thread in _runtime.Threads)
                {
                    var info = new ClrMdThreadInfo
                    {
                        IsGC = thread.IsGc,
                        StackBase = thread.StackBase != 0 ? $"0x{thread.StackBase:X16}" : null,
                        StackLimit = thread.StackLimit != 0 ? $"0x{thread.StackLimit:X16}" : null,
                        StackUsageBytes = thread.StackBase != 0 && thread.StackLimit != 0 
                            ? (long)(thread.StackBase - thread.StackLimit) 
                            : 0
                    };
                    
                    result[thread.OSThreadId] = info;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[ClrMD] Failed to get enhanced thread info");
            }
        }
        
        return result;
    }
    
    // === Phase 2b: Top Memory Consumers ===
    
    /// <summary>
    /// Gets top memory consumers by walking the heap.
    /// </summary>
    /// <param name="topN">Number of top items to return.</param>
    /// <param name="timeoutMs">Maximum time in milliseconds (0 = no limit).</param>
    public TopMemoryConsumers? GetTopMemoryConsumers(int topN = 20, int timeoutMs = 30000)
    {
        lock (_lock)
        {
            try
            {
                if (_runtime == null) return null;
                
                var heap = _runtime.Heap;
                var typeStats = new Dictionary<string, (int Count, long Size, long Largest)>();
                var largeObjects = new List<LargeObjectInfo>();
                long totalSize = 0;
                long freeSize = 0;
                int totalCount = 0;
                bool wasAborted = false;
                
                var sw = System.Diagnostics.Stopwatch.StartNew();
                const long LargeObjectThreshold = 85000; // LOH threshold
                
                foreach (var obj in heap.EnumerateObjects())
                {
                    // Check timeout
                    if (timeoutMs > 0 && sw.ElapsedMilliseconds > timeoutMs)
                    {
                        wasAborted = true;
                        _logger?.LogWarning("[ClrMD] Heap walk aborted after {Ms}ms", sw.ElapsedMilliseconds);
                        break;
                    }
                    
                    if (!obj.IsValid) continue;
                    
                    var size = (long)obj.Size;
                    
                    // Track free space for fragmentation calculation
                    if (obj.IsFree)
                    {
                        freeSize += size;
                        continue;
                    }
                    
                    var typeName = obj.Type?.Name ?? "<Unknown>";
                    totalSize += size;
                    totalCount++;
                    
                    // Track type stats
                    if (typeStats.TryGetValue(typeName, out var stats))
                    {
                        typeStats[typeName] = (stats.Count + 1, stats.Size + size, Math.Max(stats.Largest, size));
                    }
                    else
                    {
                        typeStats[typeName] = (1, size, size);
                    }
                    
                    // Track large objects (>= 85KB threshold)
                    if (size >= LargeObjectThreshold && largeObjects.Count < 50)
                    {
                        var segment = heap.GetSegmentByAddress(obj.Address);
                        var generation = segment?.Kind.ToString() ?? "Unknown";
                        
                        largeObjects.Add(new LargeObjectInfo
                        {
                            Address = $"0x{obj.Address:X16}",
                            Type = typeName,
                            Size = size,
                            Generation = generation
                        });
                    }
                }
                
                sw.Stop();
                
                // Build result
                var bySize = typeStats
                    .OrderByDescending(kvp => kvp.Value.Size)
                    .Take(topN)
                    .Select(kvp => new TypeMemoryStats
                    {
                        Type = kvp.Key,
                        Count = kvp.Value.Count,
                        TotalSize = kvp.Value.Size,
                        AverageSize = kvp.Value.Count > 0 ? kvp.Value.Size / kvp.Value.Count : 0,
                        LargestInstance = kvp.Value.Largest,
                        Percentage = totalSize > 0 ? Math.Round(kvp.Value.Size * 100.0 / totalSize, 2) : 0
                    })
                    .ToList();
                
                var byCount = typeStats
                    .OrderByDescending(kvp => kvp.Value.Count)
                    .Take(topN)
                    .Select(kvp => new TypeMemoryStats
                    {
                        Type = kvp.Key,
                        Count = kvp.Value.Count,
                        TotalSize = kvp.Value.Size,
                        AverageSize = kvp.Value.Count > 0 ? kvp.Value.Size / kvp.Value.Count : 0,
                        LargestInstance = kvp.Value.Largest,
                        Percentage = totalSize > 0 ? Math.Round(kvp.Value.Size * 100.0 / totalSize, 2) : 0
                    })
                    .ToList();
                
                return new TopMemoryConsumers
                {
                    BySize = bySize,
                    ByCount = byCount,
                    LargeObjects = largeObjects.OrderByDescending(o => o.Size).ToList(),
                    Summary = new HeapWalkSummary
                    {
                        TotalObjects = totalCount,
                        TotalSize = totalSize,
                        UniqueTypes = typeStats.Count,
                        AnalysisTimeMs = sw.ElapsedMilliseconds,
                        WasAborted = wasAborted,
                        FreeBytes = freeSize,
                        FragmentationRatio = (totalSize + freeSize) > 0 
                            ? Math.Round(freeSize * 1.0 / (totalSize + freeSize), 4) 
                            : 0
                    }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[ClrMD] Failed to get top memory consumers");
                return null;
            }
        }
    }
    
    // === Phase 2c: Async Analysis ===
    
    // Task state flags from System.Threading.Tasks.Task
    private const int TASK_STATE_RAN_TO_COMPLETION = 0x1000000;
    private const int TASK_STATE_CANCELED = 0x400000;
    private const int TASK_STATE_FAULTED = 0x200000;
    
    private static string GetTaskStatus(int stateFlags)
    {
        if ((stateFlags & TASK_STATE_RAN_TO_COMPLETION) != 0) return "RanToCompletion";
        if ((stateFlags & TASK_STATE_FAULTED) != 0) return "Faulted";
        if ((stateFlags & TASK_STATE_CANCELED) != 0) return "Canceled";
        return "Pending";
    }
    
    private FaultedTaskInfo ExtractFaultedTaskInfo(ClrObject taskObj)
    {
        var info = new FaultedTaskInfo
        {
            Address = $"0x{taskObj.Address:X16}",
            TaskType = taskObj.Type?.Name ?? "<Unknown>",
            Status = "Faulted"
        };
        
        try
        {
            var contingentProps = taskObj.ReadObjectField("m_contingentProperties");
            if (!contingentProps.IsNull)
            {
                var exceptionsHolder = contingentProps.ReadObjectField("m_exceptionsHolder");
                if (!exceptionsHolder.IsNull)
                {
                    var faultException = exceptionsHolder.ReadObjectField("m_faultException");
                    if (!faultException.IsNull && faultException.Type != null)
                    {
                        info.ExceptionType = faultException.Type.Name;
                        
                        var msgField = faultException.Type.GetFieldByName("_message");
                        if (msgField != null)
                        {
                            var msgObj = faultException.ReadObjectField("_message");
                            if (!msgObj.IsNull)
                            {
                                info.ExceptionMessage = msgObj.AsString();
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore errors reading exception details
        }
        
        return info;
    }
    
    private StateMachineInfo ExtractStateMachineInfo(ClrObject smObj)
    {
        var info = new StateMachineInfo
        {
            Address = $"0x{smObj.Address:X16}",
            StateMachineType = smObj.Type?.Name ?? "<Unknown>",
            CurrentState = -99
        };
        
        try
        {
            var stateField = smObj.Type?.GetFieldByName("<>1__state");
            if (stateField != null)
            {
                info.CurrentState = smObj.ReadField<int>("<>1__state");
            }
        }
        catch
        {
            // Ignore errors reading state
        }
        
        return info;
    }
    
    /// <summary>
    /// Analyzes async tasks and state machines.
    /// </summary>
    /// <param name="timeoutMs">Maximum time in milliseconds (0 = no limit).</param>
    public AsyncAnalysis? GetAsyncAnalysis(int timeoutMs = 30000)
    {
        lock (_lock)
        {
            try
            {
                if (_runtime == null) return null;
                
                var heap = _runtime.Heap;
                var analysis = new AsyncAnalysis
                {
                    Summary = new AsyncSummary(),
                    FaultedTasks = new List<FaultedTaskInfo>(),
                    PendingStateMachines = new List<StateMachineInfo>()
                };
                
                var sw = System.Diagnostics.Stopwatch.StartNew();
                
                foreach (var obj in heap.EnumerateObjects())
                {
                    if (timeoutMs > 0 && sw.ElapsedMilliseconds > timeoutMs)
                    {
                        analysis.WasAborted = true;
                        _logger?.LogWarning("[ClrMD] Async analysis aborted after {Ms}ms", sw.ElapsedMilliseconds);
                        break;
                    }
                    
                    if (!obj.IsValid || obj.Type == null) continue;
                    
                    var typeName = obj.Type.Name;
                    
                    // Check for Task or Task<T> (but not TaskCompletionSource, TaskScheduler, etc.)
                    if (typeName != null && 
                        (typeName == "System.Threading.Tasks.Task" || 
                         typeName.StartsWith("System.Threading.Tasks.Task`1")))
                    {
                        analysis.Summary!.TotalTasks++;
                        
                        try
                        {
                            var stateFlags = obj.ReadField<int>("m_stateFlags");
                            var status = GetTaskStatus(stateFlags);
                            
                            switch (status)
                            {
                                case "RanToCompletion":
                                    analysis.Summary.CompletedTasks++;
                                    break;
                                case "Faulted":
                                    analysis.Summary.FaultedTasks++;
                                    if (analysis.FaultedTasks!.Count < 50)
                                    {
                                        analysis.FaultedTasks.Add(ExtractFaultedTaskInfo(obj));
                                    }
                                    break;
                                case "Canceled":
                                    analysis.Summary.CanceledTasks++;
                                    break;
                                default:
                                    analysis.Summary.PendingTasks++;
                                    break;
                            }
                        }
                        catch
                        {
                            // Skip if can't read state
                        }
                    }
                    
                    // Check for async state machines
                    if (typeName != null && typeName.Contains("+<") && typeName.Contains(">d__"))
                    {
                        if (analysis.PendingStateMachines!.Count < 100)
                        {
                            analysis.PendingStateMachines.Add(ExtractStateMachineInfo(obj));
                        }
                    }
                }
                
                sw.Stop();
                analysis.AnalysisTimeMs = sw.ElapsedMilliseconds;
                
                return analysis;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[ClrMD] Failed to get async analysis");
                return null;
            }
        }
    }
    
    // === Phase 2d: String Analysis ===
    
    private static string GetStringSuggestion(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "Use string.Empty instead of \"\"";
        
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
            return "Use bool.TrueString";
        
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
            return "Use bool.FalseString";
        
        if (value.Equals("null", StringComparison.OrdinalIgnoreCase))
            return "Consider using a constant";
        
        if (value.StartsWith("http://") || value.StartsWith("https://"))
            return "Consider caching URL prefixes";
        
        if (value.Length <= 8)
            return "Consider string.Intern() for frequently used short strings";
        
        return "Consider caching or using StringPool";
    }
    
    private static string EscapeControlCharacters(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    if (char.IsControl(c))
                        sb.Append($"\\u{(int)c:X4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
    
    /// <summary>
    /// Analyzes string duplicates in the heap.
    /// </summary>
    /// <param name="topN">Number of top duplicates to return.</param>
    /// <param name="maxStringLength">Maximum string length to read (longer strings truncated).</param>
    /// <param name="timeoutMs">Maximum time in milliseconds (0 = no limit).</param>
    public StringAnalysis? GetStringAnalysis(int topN = 20, int maxStringLength = 200, int timeoutMs = 30000)
    {
        lock (_lock)
        {
            try
            {
                if (_runtime == null) return null;
                
                var heap = _runtime.Heap;
                var stringCounts = new Dictionary<string, (int Count, long Size)>();
                var analysis = new StringAnalysis
                {
                    Summary = new StringAnalysisSummary(),
                    ByLength = new StringLengthDistribution()
                };
                
                var sw = System.Diagnostics.Stopwatch.StartNew();
                
                foreach (var obj in heap.EnumerateObjects())
                {
                    if (timeoutMs > 0 && sw.ElapsedMilliseconds > timeoutMs)
                    {
                        analysis.WasAborted = true;
                        _logger?.LogWarning("[ClrMD] String analysis aborted after {Ms}ms", sw.ElapsedMilliseconds);
                        break;
                    }
                    
                    if (!obj.IsValid || obj.Type?.Name != "System.String") continue;
                    
                    var str = obj.AsString();
                    if (str == null) continue;
                    
                    var truncatedStr = str.Length > maxStringLength 
                        ? str.Substring(0, maxStringLength) 
                        : str;
                    
                    var size = (long)obj.Size;
                    analysis.Summary!.TotalStrings++;
                    analysis.Summary.TotalSize += size;
                    
                    // Categorize by length
                    var len = str.Length;
                    if (len == 0) analysis.ByLength!.Empty++;
                    else if (len <= 10) analysis.ByLength!.Short++;
                    else if (len <= 100) analysis.ByLength!.Medium++;
                    else if (len <= 1000) analysis.ByLength!.Long++;
                    else analysis.ByLength!.VeryLong++;
                    
                    // Track duplicates
                    if (stringCounts.TryGetValue(truncatedStr, out var stats))
                    {
                        stringCounts[truncatedStr] = (stats.Count + 1, stats.Size + size);
                    }
                    else
                    {
                        stringCounts[truncatedStr] = (1, size);
                    }
                }
                
                sw.Stop();
                analysis.AnalysisTimeMs = sw.ElapsedMilliseconds;
                
                // Calculate summary
                analysis.Summary!.UniqueStrings = stringCounts.Count;
                analysis.Summary.DuplicateStrings = stringCounts.Values.Sum(v => Math.Max(0, v.Count - 1));
                analysis.Summary.WastedSize = stringCounts
                    .Where(kvp => kvp.Value.Count > 1)
                    .Sum(kvp => {
                        var sizePerInstance = kvp.Value.Size / kvp.Value.Count;
                        return kvp.Value.Size - sizePerInstance;
                    });
                analysis.Summary.WastedPercentage = analysis.Summary.TotalSize > 0 
                    ? Math.Round(analysis.Summary.WastedSize * 100.0 / analysis.Summary.TotalSize, 2) 
                    : 0;
                
                // Top duplicates (by wasted bytes)
                analysis.TopDuplicates = stringCounts
                    .Where(kvp => kvp.Value.Count > 1)
                    .OrderByDescending(kvp => {
                        var sizePerInstance = kvp.Value.Size / kvp.Value.Count;
                        return kvp.Value.Size - sizePerInstance;
                    })
                    .Take(topN)
                    .Select(kvp => {
                        var sizePerInstance = kvp.Value.Size / kvp.Value.Count;
                        var displayValue = EscapeControlCharacters(kvp.Key);
                        
                        return new StringDuplicateInfo
                        {
                            Value = displayValue,
                            Count = kvp.Value.Count,
                            SizePerInstance = sizePerInstance,
                            WastedBytes = kvp.Value.Size - sizePerInstance,
                            Suggestion = GetStringSuggestion(kvp.Key)
                        };
                    })
                    .ToList();
                
                return analysis;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[ClrMD] Failed to get string analysis");
                return null;
            }
        }
    }
    
    // === Optimized Single-Pass Combined Analysis ===
    
    /// <summary>
    /// Performs all deep heap analysis in a single pass with optional parallelization.
    /// This is significantly faster than calling GetTopMemoryConsumers, GetAsyncAnalysis,
    /// and GetStringAnalysis separately (3x speedup for sequential, 6-12x with parallel).
    /// </summary>
    /// <param name="topN">Number of top items to return for each category.</param>
    /// <param name="maxStringLength">Maximum string length to read.</param>
    /// <param name="timeoutMs">Maximum time in milliseconds (0 = no limit).</param>
    public CombinedHeapAnalysis? GetCombinedHeapAnalysis(int topN = 20, int maxStringLength = 200, int timeoutMs = 30000)
    {
        lock (_lock)
        {
            try
            {
                if (_runtime == null) return null;
                
                var heap = _runtime.Heap;
                var segments = heap.Segments.ToArray();
                var useParallel = heap.IsServer && segments.Length > 1;
                
                _logger?.LogInformation("[ClrMD] Starting combined heap analysis (parallel={Parallel}, segments={Segments})", 
                    useParallel, segments.Length);
                
                var sw = System.Diagnostics.Stopwatch.StartNew();
                
                CombinedHeapAnalysis result;
                if (useParallel)
                {
                    result = GetCombinedHeapAnalysisParallel(heap, segments, topN, maxStringLength, timeoutMs, sw);
                }
                else
                {
                    result = GetCombinedHeapAnalysisSequential(heap, topN, maxStringLength, timeoutMs, sw);
                }
                
                sw.Stop();
                result.TotalAnalysisTimeMs = sw.ElapsedMilliseconds;
                result.UsedParallel = useParallel;
                result.SegmentsProcessed = segments.Length;
                
                _logger?.LogInformation("[ClrMD] Combined heap analysis completed in {Ms}ms (parallel={Parallel})", 
                    sw.ElapsedMilliseconds, useParallel);
                
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[ClrMD] Failed to get combined heap analysis");
                return null;
            }
        }
    }
    
    private CombinedHeapAnalysis GetCombinedHeapAnalysisSequential(
        ClrHeap heap, int topN, int maxStringLength, int timeoutMs, System.Diagnostics.Stopwatch sw)
    {
        var typeStats = new Dictionary<string, (int Count, long Size, long Largest)>();
        var stringCounts = new Dictionary<string, (int Count, long Size)>();
        var largeObjects = new List<LargeObjectInfo>();
        var faultedTasks = new List<FaultedTaskInfo>();
        var stateMachines = new List<StateMachineInfo>();
        
        long totalSize = 0;
        long freeSize = 0;
        int totalCount = 0;
        bool wasAborted = false;
        
        var asyncSummary = new AsyncSummary();
        var stringLengthDist = new StringLengthDistribution();
        long stringTotalSize = 0;
        int stringTotalCount = 0;
        
        const long LargeObjectThreshold = 85000;
        
        foreach (var obj in heap.EnumerateObjects())
        {
            if (timeoutMs > 0 && sw.ElapsedMilliseconds > timeoutMs)
            {
                wasAborted = true;
                _logger?.LogWarning("[ClrMD] Combined analysis aborted after {Ms}ms", sw.ElapsedMilliseconds);
                break;
            }
            
            if (!obj.IsValid) continue;
            
            var size = (long)obj.Size;
            
            // Track free objects for fragmentation
            if (obj.IsFree)
            {
                freeSize += size;
                continue;
            }
            
            var typeName = obj.Type?.Name;
            if (typeName == null) continue;
            
            totalSize += size;
            totalCount++;
            
            // === Memory Consumer Tracking ===
            if (typeStats.TryGetValue(typeName, out var stats))
            {
                typeStats[typeName] = (stats.Count + 1, stats.Size + size, Math.Max(stats.Largest, size));
            }
            else
            {
                typeStats[typeName] = (1, size, size);
            }
            
            // Large objects
            if (size >= LargeObjectThreshold && largeObjects.Count < 50)
            {
                var segment = heap.GetSegmentByAddress(obj.Address);
                largeObjects.Add(new LargeObjectInfo
                {
                    Address = $"0x{obj.Address:X16}",
                    Type = typeName,
                    Size = size,
                    Generation = segment?.Kind.ToString() ?? "Unknown"
                });
            }
            
            // === String Analysis ===
            if (typeName == "System.String")
            {
                var str = obj.AsString();
                if (str != null)
                {
                    stringTotalCount++;
                    stringTotalSize += size;
                    
                    // Length distribution
                    var len = str.Length;
                    if (len == 0) stringLengthDist.Empty++;
                    else if (len <= 10) stringLengthDist.Short++;
                    else if (len <= 100) stringLengthDist.Medium++;
                    else if (len <= 1000) stringLengthDist.Long++;
                    else stringLengthDist.VeryLong++;
                    
                    // Track duplicates
                    var truncatedStr = str.Length > maxStringLength ? str.Substring(0, maxStringLength) : str;
                    if (stringCounts.TryGetValue(truncatedStr, out var sStats))
                    {
                        stringCounts[truncatedStr] = (sStats.Count + 1, sStats.Size + size);
                    }
                    else
                    {
                        stringCounts[truncatedStr] = (1, size);
                    }
                }
            }
            
            // === Task Analysis ===
            if (typeName == "System.Threading.Tasks.Task" || 
                typeName.StartsWith("System.Threading.Tasks.Task`1"))
            {
                asyncSummary.TotalTasks++;
                try
                {
                    var stateFlags = obj.ReadField<int>("m_stateFlags");
                    var status = GetTaskStatus(stateFlags);
                    
                    switch (status)
                    {
                        case "RanToCompletion":
                            asyncSummary.CompletedTasks++;
                            break;
                        case "Faulted":
                            asyncSummary.FaultedTasks++;
                            if (faultedTasks.Count < 50)
                                faultedTasks.Add(ExtractFaultedTaskInfo(obj));
                            break;
                        case "Canceled":
                            asyncSummary.CanceledTasks++;
                            break;
                        default:
                            asyncSummary.PendingTasks++;
                            break;
                    }
                }
                catch { }
            }
            
            // === State Machine Analysis ===
            if (typeName.Contains("+<") && typeName.Contains(">d__"))
            {
                if (stateMachines.Count < 100)
                    stateMachines.Add(ExtractStateMachineInfo(obj));
            }
        }
        
        return BuildCombinedResult(
            typeStats, stringCounts, largeObjects, faultedTasks, stateMachines,
            asyncSummary, stringLengthDist, 
            totalSize, freeSize, totalCount, stringTotalSize, stringTotalCount,
            wasAborted, sw.ElapsedMilliseconds, topN);
    }
    
    private CombinedHeapAnalysis GetCombinedHeapAnalysisParallel(
        ClrHeap heap, ClrSegment[] segments, int topN, int maxStringLength, int timeoutMs, System.Diagnostics.Stopwatch sw)
    {
        var allTypeStats = new System.Collections.Concurrent.ConcurrentBag<Dictionary<string, (int Count, long Size, long Largest)>>();
        var allStringCounts = new System.Collections.Concurrent.ConcurrentBag<Dictionary<string, (int Count, long Size)>>();
        var allLargeObjects = new System.Collections.Concurrent.ConcurrentBag<LargeObjectInfo>();
        var allFaultedTasks = new System.Collections.Concurrent.ConcurrentBag<FaultedTaskInfo>();
        var allStateMachines = new System.Collections.Concurrent.ConcurrentBag<StateMachineInfo>();
        
        long totalSize = 0;
        long freeSize = 0;
        int totalCount = 0;
        int wasAbortedFlag = 0;
        
        int totalTasks = 0, completedTasks = 0, faultedTaskCount = 0, canceledTasks = 0, pendingTasks = 0;
        int emptyStrings = 0, shortStrings = 0, mediumStrings = 0, longStrings = 0, veryLongStrings = 0;
        long stringTotalSize = 0;
        int stringTotalCount = 0;
        
        const long LargeObjectThreshold = 85000;
        
        System.Threading.Tasks.Parallel.ForEach(segments, 
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            segment =>
        {
            if (wasAbortedFlag == 1) return;
            if (timeoutMs > 0 && sw.ElapsedMilliseconds > timeoutMs)
            {
                System.Threading.Interlocked.Exchange(ref wasAbortedFlag, 1);
                return;
            }
            
            var localTypeStats = new Dictionary<string, (int Count, long Size, long Largest)>();
            var localStringCounts = new Dictionary<string, (int Count, long Size)>();
            
            long localTotalSize = 0, localFreeSize = 0;
            int localTotalCount = 0;
            int localTotalTasks = 0, localCompletedTasks = 0, localFaultedTasks = 0, localCanceledTasks = 0, localPendingTasks = 0;
            int localEmpty = 0, localShort = 0, localMedium = 0, localLong = 0, localVeryLong = 0;
            long localStringTotalSize = 0;
            int localStringTotalCount = 0;
            
            foreach (var obj in segment.EnumerateObjects())
            {
                if (wasAbortedFlag == 1) break;
                
                if (!obj.IsValid) continue;
                
                var size = (long)obj.Size;
                
                if (obj.IsFree)
                {
                    localFreeSize += size;
                    continue;
                }
                
                var typeName = obj.Type?.Name;
                if (typeName == null) continue;
                
                localTotalSize += size;
                localTotalCount++;
                
                // Memory stats
                if (localTypeStats.TryGetValue(typeName, out var stats))
                {
                    localTypeStats[typeName] = (stats.Count + 1, stats.Size + size, Math.Max(stats.Largest, size));
                }
                else
                {
                    localTypeStats[typeName] = (1, size, size);
                }
                
                // Large objects
                if (size >= LargeObjectThreshold && allLargeObjects.Count < 50)
                {
                    allLargeObjects.Add(new LargeObjectInfo
                    {
                        Address = $"0x{obj.Address:X16}",
                        Type = typeName,
                        Size = size,
                        Generation = segment.Kind.ToString()
                    });
                }
                
                // String analysis
                if (typeName == "System.String")
                {
                    var str = obj.AsString();
                    if (str != null)
                    {
                        localStringTotalCount++;
                        localStringTotalSize += size;
                        
                        var len = str.Length;
                        if (len == 0) localEmpty++;
                        else if (len <= 10) localShort++;
                        else if (len <= 100) localMedium++;
                        else if (len <= 1000) localLong++;
                        else localVeryLong++;
                        
                        var truncatedStr = str.Length > maxStringLength ? str.Substring(0, maxStringLength) : str;
                        if (localStringCounts.TryGetValue(truncatedStr, out var sStats))
                        {
                            localStringCounts[truncatedStr] = (sStats.Count + 1, sStats.Size + size);
                        }
                        else
                        {
                            localStringCounts[truncatedStr] = (1, size);
                        }
                    }
                }
                
                // Task analysis
                if (typeName == "System.Threading.Tasks.Task" || 
                    typeName.StartsWith("System.Threading.Tasks.Task`1"))
                {
                    localTotalTasks++;
                    try
                    {
                        var stateFlags = obj.ReadField<int>("m_stateFlags");
                        var status = GetTaskStatus(stateFlags);
                        
                        switch (status)
                        {
                            case "RanToCompletion": localCompletedTasks++; break;
                            case "Faulted":
                                localFaultedTasks++;
                                if (allFaultedTasks.Count < 50)
                                    allFaultedTasks.Add(ExtractFaultedTaskInfo(obj));
                                break;
                            case "Canceled": localCanceledTasks++; break;
                            default: localPendingTasks++; break;
                        }
                    }
                    catch { }
                }
                
                // State machines
                if (typeName.Contains("+<") && typeName.Contains(">d__"))
                {
                    if (allStateMachines.Count < 100)
                        allStateMachines.Add(ExtractStateMachineInfo(obj));
                }
            }
            
            // Add local results to concurrent collections
            allTypeStats.Add(localTypeStats);
            allStringCounts.Add(localStringCounts);
            
            // Aggregate counters atomically
            System.Threading.Interlocked.Add(ref totalSize, localTotalSize);
            System.Threading.Interlocked.Add(ref freeSize, localFreeSize);
            System.Threading.Interlocked.Add(ref totalCount, localTotalCount);
            System.Threading.Interlocked.Add(ref totalTasks, localTotalTasks);
            System.Threading.Interlocked.Add(ref completedTasks, localCompletedTasks);
            System.Threading.Interlocked.Add(ref faultedTaskCount, localFaultedTasks);
            System.Threading.Interlocked.Add(ref canceledTasks, localCanceledTasks);
            System.Threading.Interlocked.Add(ref pendingTasks, localPendingTasks);
            System.Threading.Interlocked.Add(ref emptyStrings, localEmpty);
            System.Threading.Interlocked.Add(ref shortStrings, localShort);
            System.Threading.Interlocked.Add(ref mediumStrings, localMedium);
            System.Threading.Interlocked.Add(ref longStrings, localLong);
            System.Threading.Interlocked.Add(ref veryLongStrings, localVeryLong);
            System.Threading.Interlocked.Add(ref stringTotalSize, localStringTotalSize);
            System.Threading.Interlocked.Add(ref stringTotalCount, localStringTotalCount);
        });
        
        // Merge type stats
        var mergedTypeStats = new Dictionary<string, (int Count, long Size, long Largest)>();
        foreach (var localStats in allTypeStats)
        {
            foreach (var kvp in localStats)
            {
                if (mergedTypeStats.TryGetValue(kvp.Key, out var existing))
                {
                    mergedTypeStats[kvp.Key] = (
                        existing.Count + kvp.Value.Count,
                        existing.Size + kvp.Value.Size,
                        Math.Max(existing.Largest, kvp.Value.Largest));
                }
                else
                {
                    mergedTypeStats[kvp.Key] = kvp.Value;
                }
            }
        }
        
        // Merge string counts
        var mergedStringCounts = new Dictionary<string, (int Count, long Size)>();
        foreach (var localStrings in allStringCounts)
        {
            foreach (var kvp in localStrings)
            {
                if (mergedStringCounts.TryGetValue(kvp.Key, out var existing))
                {
                    mergedStringCounts[kvp.Key] = (existing.Count + kvp.Value.Count, existing.Size + kvp.Value.Size);
                }
                else
                {
                    mergedStringCounts[kvp.Key] = kvp.Value;
                }
            }
        }
        
        var asyncSummary = new AsyncSummary
        {
            TotalTasks = totalTasks,
            CompletedTasks = completedTasks,
            FaultedTasks = faultedTaskCount,
            CanceledTasks = canceledTasks,
            PendingTasks = pendingTasks
        };
        
        var stringLengthDist = new StringLengthDistribution
        {
            Empty = emptyStrings,
            Short = shortStrings,
            Medium = mediumStrings,
            Long = longStrings,
            VeryLong = veryLongStrings
        };
        
        return BuildCombinedResult(
            mergedTypeStats, mergedStringCounts, 
            allLargeObjects.ToList(), allFaultedTasks.ToList(), allStateMachines.ToList(),
            asyncSummary, stringLengthDist,
            totalSize, freeSize, totalCount, stringTotalSize, stringTotalCount,
            wasAbortedFlag == 1, sw.ElapsedMilliseconds, topN);
    }
    
    private CombinedHeapAnalysis BuildCombinedResult(
        Dictionary<string, (int Count, long Size, long Largest)> typeStats,
        Dictionary<string, (int Count, long Size)> stringCounts,
        List<LargeObjectInfo> largeObjects,
        List<FaultedTaskInfo> faultedTasks,
        List<StateMachineInfo> stateMachines,
        AsyncSummary asyncSummary,
        StringLengthDistribution stringLengthDist,
        long totalSize, long freeSize, int totalCount,
        long stringTotalSize, int stringTotalCount,
        bool wasAborted, long elapsedMs, int topN)
    {
        // Build top memory consumers
        var bySize = typeStats
            .OrderByDescending(kvp => kvp.Value.Size)
            .Take(topN)
            .Select(kvp => new TypeMemoryStats
            {
                Type = kvp.Key,
                Count = kvp.Value.Count,
                TotalSize = kvp.Value.Size,
                AverageSize = kvp.Value.Count > 0 ? kvp.Value.Size / kvp.Value.Count : 0,
                LargestInstance = kvp.Value.Largest,
                Percentage = totalSize > 0 ? Math.Round(kvp.Value.Size * 100.0 / totalSize, 2) : 0
            })
            .ToList();
        
        var byCount = typeStats
            .OrderByDescending(kvp => kvp.Value.Count)
            .Take(topN)
            .Select(kvp => new TypeMemoryStats
            {
                Type = kvp.Key,
                Count = kvp.Value.Count,
                TotalSize = kvp.Value.Size,
                AverageSize = kvp.Value.Count > 0 ? kvp.Value.Size / kvp.Value.Count : 0,
                LargestInstance = kvp.Value.Largest,
                Percentage = totalSize > 0 ? Math.Round(kvp.Value.Size * 100.0 / totalSize, 2) : 0
            })
            .ToList();
        
        // Build string analysis
        var stringUniqueCount = stringCounts.Count;
        var stringDuplicateCount = stringCounts.Values.Sum(v => Math.Max(0, v.Count - 1));
        var stringWastedSize = stringCounts
            .Where(kvp => kvp.Value.Count > 1)
            .Sum(kvp => {
                var sizePerInstance = kvp.Value.Size / kvp.Value.Count;
                return kvp.Value.Size - sizePerInstance;
            });
        
        var topDuplicates = stringCounts
            .Where(kvp => kvp.Value.Count > 1)
            .OrderByDescending(kvp => {
                var sizePerInstance = kvp.Value.Size / kvp.Value.Count;
                return kvp.Value.Size - sizePerInstance;
            })
            .Take(topN)
            .Select(kvp => {
                var sizePerInstance = kvp.Value.Size / kvp.Value.Count;
                var displayValue = EscapeControlCharacters(kvp.Key);
                
                return new StringDuplicateInfo
                {
                    Value = displayValue,
                    Count = kvp.Value.Count,
                    SizePerInstance = sizePerInstance,
                    WastedBytes = kvp.Value.Size - sizePerInstance,
                    Suggestion = GetStringSuggestion(kvp.Key)
                };
            })
            .ToList();
        
        var fragmentationRatio = (totalSize + freeSize) > 0 
            ? Math.Round(freeSize * 1.0 / (totalSize + freeSize), 4) 
            : 0;
        
        return new CombinedHeapAnalysis
        {
            TopMemoryConsumers = new TopMemoryConsumers
            {
                BySize = bySize,
                ByCount = byCount,
                LargeObjects = largeObjects.OrderByDescending(o => o.Size).ToList(),
                Summary = new HeapWalkSummary
                {
                    TotalObjects = totalCount,
                    TotalSize = totalSize,
                    UniqueTypes = typeStats.Count,
                    AnalysisTimeMs = elapsedMs,
                    WasAborted = wasAborted,
                    FreeBytes = freeSize,
                    FragmentationRatio = fragmentationRatio
                }
            },
            AsyncAnalysis = new AsyncAnalysis
            {
                Summary = asyncSummary,
                FaultedTasks = faultedTasks,
                PendingStateMachines = stateMachines,
                AnalysisTimeMs = elapsedMs,
                WasAborted = wasAborted
            },
            StringAnalysis = new StringAnalysis
            {
                Summary = new StringAnalysisSummary
                {
                    TotalStrings = stringTotalCount,
                    UniqueStrings = stringUniqueCount,
                    DuplicateStrings = stringDuplicateCount,
                    TotalSize = stringTotalSize,
                    WastedSize = stringWastedSize,
                    WastedPercentage = stringTotalSize > 0 
                        ? Math.Round(stringWastedSize * 100.0 / stringTotalSize, 2) 
                        : 0
                },
                TopDuplicates = topDuplicates,
                ByLength = stringLengthDist,
                AnalysisTimeMs = elapsedMs,
                WasAborted = wasAborted
            },
            FreeBytes = freeSize,
            FragmentationRatio = fragmentationRatio
        };
    }
}

/// <summary>
/// Module info with assembly attributes extracted from dump memory.
/// </summary>
public class EnrichedModuleInfo
{
    /// <summary>
    /// Gets or sets the module name (without extension).
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the full path to the module.
    /// </summary>
    public string? FullPath { get; set; }
    
    /// <summary>
    /// Gets or sets the image base address.
    /// </summary>
    public ulong ImageBase { get; set; }
    
    /// <summary>
    /// Gets or sets the module size.
    /// </summary>
    public ulong Size { get; set; }
    
    /// <summary>
    /// Gets or sets whether this is a dynamic assembly.
    /// </summary>
    public bool IsDynamic { get; set; }
    
    /// <summary>
    /// Gets or sets whether this is a PE file.
    /// </summary>
    public bool IsPEFile { get; set; }
    
    /// <summary>
    /// Gets or sets the assembly version from the assembly definition metadata.
    /// </summary>
    public string? AssemblyVersion { get; set; }
    
    /// <summary>
    /// Gets or sets the assembly attributes.
    /// </summary>
    public List<AssemblyAttributeInfo> Attributes { get; set; } = new();
}

/// <summary>
/// Represents an assembly attribute with its type and value(s).
/// </summary>
public class AssemblyAttributeInfo
{
    /// <summary>
    /// Gets or sets the full attribute type name.
    /// </summary>
    public string AttributeType { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the attribute value.
    /// </summary>
    public string? Value { get; set; }
    
    /// <summary>
    /// Gets or sets the key for key-value attributes (like AssemblyMetadataAttribute).
    /// </summary>
    public string? Key { get; set; }
}

