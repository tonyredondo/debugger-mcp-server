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
    /// Gets the CLR runtime for advanced analysis (null if not open).
    /// </summary>
    public ClrRuntime? Runtime => _runtime;

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
    // Object Inspection Methods (ClrMD-based dumpobj replacement)
    // ============================================================================
    
    /// <summary>
    /// Inspects a .NET object at the given address using ClrMD.
    /// This is a safe alternative to SOS dumpobj that runs in managed code.
    /// </summary>
    /// <param name="address">The memory address of the object.</param>
    /// <param name="maxDepth">Maximum recursion depth for nested objects.</param>
    /// <param name="maxArrayElements">Maximum array elements to show.</param>
    /// <param name="maxStringLength">Maximum string length before truncation.</param>
    /// <returns>Object inspection result, or null if inspection failed.</returns>
    /// <summary>
    /// Inspects a .NET object or value type at the specified address.
    /// This is a unified replacement for SOS dumpobj and dumpvc commands.
    /// </summary>
    /// <param name="address">The object/value address.</param>
    /// <param name="methodTable">Optional method table for value types. If provided, tries VT first, then reference type.</param>
    /// <param name="maxDepth">1 for flat representation, 5+ for full tree.</param>
    /// <param name="maxArrayElements">Max array elements to show.</param>
    /// <param name="maxStringLength">Max string length before truncation.</param>
    /// <returns>Inspection result or null if runtime not initialized.</returns>
    public ClrMdObjectInspection? InspectObject(
        ulong address,
        ulong? methodTable = null,
        int maxDepth = 5,
        int maxArrayElements = 10,
        int maxStringLength = 1024)
    {
        lock (_lock)
        {
            if (_runtime == null)
            {
                _logger?.LogWarning("[ClrMD] Cannot inspect object - runtime not initialized");
                return null;
            }

            var seenAddresses = new HashSet<ulong>();
            
            // If method table is provided, try value type extraction first
            if (methodTable.HasValue)
            {
                var vtResult = TryInspectValueType(address, methodTable.Value, maxDepth, maxArrayElements, maxStringLength, seenAddresses);
                if (vtResult != null && vtResult.Error == null)
                {
                    _logger?.LogDebug("[ClrMD] Successfully inspected value type at 0x{Address:X} with MT 0x{MT:X}", address, methodTable.Value);
                    return vtResult;
                }
                
                // Value type failed, try as reference type
                _logger?.LogDebug("[ClrMD] Value type inspection failed for 0x{Address:X}, trying as reference type", address);
                seenAddresses.Clear();
            }
            
            // Try reference type extraction
            var refResult = TryInspectReferenceType(address, maxDepth, maxArrayElements, maxStringLength, seenAddresses);
            if (refResult != null)
            {
                return refResult;
            }
            
            // Both failed
            return new ClrMdObjectInspection
            {
                Address = $"0x{address:x}",
                Error = methodTable.HasValue 
                    ? $"Failed to inspect as value type (MT=0x{methodTable.Value:x}) or reference type"
                    : "Invalid object address"
            };
        }
    }
    
    /// <summary>
    /// Tries to inspect a value type at the given address using the provided method table.
    /// </summary>
    private ClrMdObjectInspection? TryInspectValueType(
        ulong address,
        ulong methodTable,
        int maxDepth,
        int maxArrayElements,
        int maxStringLength,
        HashSet<ulong> seenAddresses)
    {
        try
        {
            // Get the type from the method table
            var type = _runtime!.GetTypeByMethodTable(methodTable);
            if (type == null)
            {
                _logger?.LogDebug("[ClrMD] Could not find type for MT 0x{MT:X}", methodTable);
                return new ClrMdObjectInspection
                {
                    Address = $"0x{address:x}",
                    MethodTable = $"0x{methodTable:x}",
                    Error = $"Type not found for method table 0x{methodTable:x}"
                };
            }
            
            if (!type.IsValueType)
            {
                _logger?.LogDebug("[ClrMD] Type {Type} is not a value type, will try as reference", type.Name);
                return null; // Let caller try as reference type
            }
            
            // Create value type inspection result
            var result = new ClrMdObjectInspection
            {
                Address = $"0x{address:x}",
                Type = type.Name ?? "Unknown",
                MethodTable = $"0x{type.MethodTable:x}",
                Size = (ulong)type.StaticSize
            };
            
            if (maxDepth <= 0)
            {
                result.Value = "[max depth reached]";
                return result;
            }
            
            // Read fields from the value type
            result.Fields = new List<ClrMdFieldInspection>();
            foreach (var field in type.Fields)
            {
                var fieldInspection = new ClrMdFieldInspection
                {
                    Name = field.Name ?? "[unnamed]",
                    Type = field.Type?.Name ?? "Unknown",
                    IsStatic = false, // Instance fields for value types
                    Offset = field.Offset
                };
                
                try
                {
                    if (field.IsPrimitive)
                    {
                        fieldInspection.Value = ReadPrimitiveFieldFromAddress(field, address);
                    }
                    else if (field.Type?.IsString == true)
                    {
                        // For value types, use interior: true
                        var fieldObj = field.ReadObject(address, interior: true);
                        if (fieldObj.IsValid)
                        {
                            var str = fieldObj.AsString();
                            if (str != null && str.Length > maxStringLength)
                                str = str.Substring(0, maxStringLength) + "...";
                            fieldInspection.Value = str;
                        }
                        else
                        {
                            fieldInspection.Value = "null";
                        }
                    }
                    else if (field.IsObjectReference)
                    {
                        // For value types, use interior: true
                        var fieldObj = field.ReadObject(address, interior: true);
                        if (fieldObj.IsValid && fieldObj.Address != 0)
                        {
                            if (maxDepth > 1)
                            {
                                fieldInspection.NestedObject = InspectObjectInternal(fieldObj, maxDepth - 1, maxArrayElements, maxStringLength, seenAddresses);
                            }
                            else
                            {
                                fieldInspection.Value = $"0x{fieldObj.Address:x}";
                            }
                        }
                        else
                        {
                            fieldInspection.Value = "null";
                        }
                    }
                    else if (field.IsValueType)
                    {
                        // Nested value type - calculate address and recursively inspect
                        var nestedVtAddress = address + (ulong)field.Offset;
                        var nestedVtType = field.Type;
                        
                        if (nestedVtType != null && nestedVtType.MethodTable != 0 && maxDepth > 1)
                        {
                            // Recursively inspect the nested value type
                            var nestedResult = TryInspectValueType(
                                nestedVtAddress, 
                                nestedVtType.MethodTable, 
                                maxDepth - 1, 
                                maxArrayElements, 
                                maxStringLength, 
                                seenAddresses);
                            
                            if (nestedResult != null && nestedResult.Error == null)
                            {
                                fieldInspection.NestedObject = nestedResult;
                            }
                            else
                            {
                                // Fallback to showing address if recursive inspection fails
                                fieldInspection.Value = $"0x{nestedVtAddress:x}";
                            }
                        }
                        else if (nestedVtType != null)
                        {
                            // Max depth reached or no MT - show address only (type is in Type field)
                            fieldInspection.Value = $"0x{nestedVtAddress:x}";
                        }
                        else
                        {
                            fieldInspection.Value = $"[value type at 0x{nestedVtAddress:x}]";
                        }
                    }
                    else
                    {
                        fieldInspection.Value = "[unknown field type]";
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "[ClrMD] Error reading VT field {Field}", field.Name);
                    fieldInspection.Value = $"[error: {ex.Message}]";
                }
                
                result.Fields.Add(fieldInspection);
            }
            
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[ClrMD] Error inspecting value type at 0x{Address:X}", address);
            return null; // Let caller try as reference type
        }
    }
    
    /// <summary>
    /// Reads a primitive field value from a value type at the given address.
    /// </summary>
    private object? ReadPrimitiveFieldFromAddress(ClrInstanceField field, ulong address)
    {
        var fieldType = field.Type;
        if (fieldType == null) return null;
        
        return fieldType.Name switch
        {
            "System.Boolean" => field.Read<bool>(address, interior: true),
            "System.Byte" => field.Read<byte>(address, interior: true),
            "System.SByte" => field.Read<sbyte>(address, interior: true),
            "System.Int16" => field.Read<short>(address, interior: true),
            "System.UInt16" => field.Read<ushort>(address, interior: true),
            "System.Int32" => field.Read<int>(address, interior: true),
            "System.UInt32" => field.Read<uint>(address, interior: true),
            "System.Int64" => field.Read<long>(address, interior: true),
            "System.UInt64" => field.Read<ulong>(address, interior: true),
            "System.Single" => field.Read<float>(address, interior: true),
            "System.Double" => field.Read<double>(address, interior: true),
            "System.Char" => field.Read<char>(address, interior: true),
            "System.IntPtr" => $"0x{field.Read<nint>(address, interior: true):x}",
            "System.UIntPtr" => $"0x{field.Read<nuint>(address, interior: true):x}",
            _ => null
        };
    }
    
    /// <summary>
    /// Tries to inspect a reference type object at the given address.
    /// </summary>
    private ClrMdObjectInspection? TryInspectReferenceType(
        ulong address,
        int maxDepth,
        int maxArrayElements,
        int maxStringLength,
        HashSet<ulong> seenAddresses)
    {
        try
        {
            var heap = _runtime!.Heap;
            var obj = heap.GetObject(address);
            
            if (!obj.IsValid)
            {
                _logger?.LogDebug("[ClrMD] Object at 0x{Address:X} is not valid", address);
                return null;
            }

            return InspectObjectInternal(obj, maxDepth, maxArrayElements, maxStringLength, seenAddresses);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[ClrMD] Error inspecting reference type at 0x{Address:X}", address);
            return new ClrMdObjectInspection
            {
                Address = $"0x{address:x}",
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Inspects a CLR module at the given address, returning detailed module information.
    /// This is a safe replacement for SOS !dumpmodule that won't crash LLDB.
    /// </summary>
    /// <param name="moduleAddress">The address of the module to inspect.</param>
    /// <returns>Module inspection result, or null if not found.</returns>
    public ClrMdModuleInspection? InspectModule(ulong moduleAddress)
    {
        lock (_lock)
        {
            try
            {
                if (_runtime == null)
                {
                    _logger?.LogWarning("[ClrMD] Cannot inspect module - runtime not initialized");
                    return null;
                }

                // Find the module by address
                ClrModule? targetModule = null;
                foreach (var module in _runtime.EnumerateModules())
                {
                    if (module.Address == moduleAddress)
                    {
                        targetModule = module;
                        break;
                    }
                }

                if (targetModule == null)
                {
                    _logger?.LogDebug("[ClrMD] Module at 0x{Address:X} not found", moduleAddress);
                    return new ClrMdModuleInspection
                    {
                        Address = $"0x{moduleAddress:X16}",
                        Error = "Module not found at specified address"
                    };
                }

                return InspectModuleInternal(targetModule);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[ClrMD] Error inspecting module at 0x{Address:X}", moduleAddress);
                return new ClrMdModuleInspection
                {
                    Address = $"0x{moduleAddress:X16}",
                    Error = ex.Message
                };
            }
        }
    }

    /// <summary>
    /// Inspects a CLR module by name, returning detailed module information.
    /// </summary>
    /// <param name="moduleName">The module name (can be partial, case-insensitive).</param>
    /// <returns>Module inspection result, or null if not found.</returns>
    public ClrMdModuleInspection? InspectModuleByName(string moduleName)
    {
        lock (_lock)
        {
            try
            {
                if (_runtime == null)
                {
                    _logger?.LogWarning("[ClrMD] Cannot inspect module - runtime not initialized");
                    return null;
                }

                // Find the module by name (case-insensitive, supports partial match)
                ClrModule? targetModule = null;
                foreach (var module in _runtime.EnumerateModules())
                {
                    var name = module.Name;
                    if (name != null && name.Contains(moduleName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetModule = module;
                        break;
                    }
                }

                if (targetModule == null)
                {
                    _logger?.LogDebug("[ClrMD] Module '{Name}' not found", moduleName);
                    return new ClrMdModuleInspection
                    {
                        Address = "0x0",
                        Name = moduleName,
                        Error = "Module not found"
                    };
                }

                return InspectModuleInternal(targetModule);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[ClrMD] Error inspecting module '{Name}'", moduleName);
                return new ClrMdModuleInspection
                {
                    Address = "0x0",
                    Name = moduleName,
                    Error = ex.Message
                };
            }
        }
    }

    /// <summary>
    /// Internal module inspection implementation.
    /// </summary>
    private ClrMdModuleInspection InspectModuleInternal(ClrModule module)
    {
        var result = new ClrMdModuleInspection
        {
            Address = $"0x{module.Address:X16}",
            Name = module.Name,
            Size = module.Size,
            IsPEFile = module.IsPEFile,
            IsDynamic = module.IsDynamic,
            Layout = module.Layout.ToString(),
            MetadataLength = module.MetadataLength
        };

        // Image base (only for PE files)
        if (module.ImageBase != 0)
        {
            result.ImageBase = $"0x{module.ImageBase:X16}";
        }

        // Assembly info
        var assembly = module.AssemblyAddress;
        if (assembly != 0)
        {
            result.AssemblyAddress = $"0x{assembly:X16}";
        }

        // Get assembly name from module path
        if (!string.IsNullOrEmpty(module.Name))
        {
            try
            {
                result.AssemblyName = System.IO.Path.GetFileNameWithoutExtension(module.Name);
            }
            catch
            {
                // Ignore errors getting assembly name from path
            }
        }

        // Metadata address
        if (module.MetadataAddress != 0)
        {
            result.MetadataAddress = $"0x{module.MetadataAddress:X16}";
        }

        // PDB info
        var pdb = module.Pdb;
        if (pdb != null)
        {
            result.Pdb = new ClrMdPdbInfo
            {
                Path = pdb.Path,
                Guid = pdb.Guid.ToString("D"),
                Revision = pdb.Revision
            };
        }

        // Type count
        try
        {
            result.TypeCount = module.EnumerateTypeDefToMethodTableMap().Count();
        }
        catch
        {
            result.TypeCount = 0;
        }

        // Try to get version from assembly attributes
        try
        {
            var (attributes, version) = ReadAttributesFromMemory(module);
            if (!string.IsNullOrEmpty(version))
            {
                result.Version = version;
            }
            else
            {
                // Try to find AssemblyInformationalVersionAttribute or AssemblyVersionAttribute
                var infoVersion = attributes.FirstOrDefault(a => 
                    a.AttributeType.Contains("InformationalVersion", StringComparison.Ordinal));
                if (infoVersion != null && !string.IsNullOrEmpty(infoVersion.Value))
                {
                    result.Version = infoVersion.Value;
                }
                else
                {
                    var asmVersion = attributes.FirstOrDefault(a =>
                        a.AttributeType.Equals("AssemblyVersionAttribute", StringComparison.Ordinal) ||
                        a.AttributeType.Equals("AssemblyFileVersionAttribute", StringComparison.Ordinal));
                    if (asmVersion != null && !string.IsNullOrEmpty(asmVersion.Value))
                    {
                        result.Version = asmVersion.Value;
                    }
                }
            }
        }
        catch
        {
            // Ignore errors getting version
        }

        return result;
    }

    /// <summary>
    /// Lists all modules in the runtime.
    /// </summary>
    /// <returns>List of module inspection results.</returns>
    public List<ClrMdModuleInspection> ListModules()
    {
        lock (_lock)
        {
            var results = new List<ClrMdModuleInspection>();

            if (_runtime == null)
            {
                _logger?.LogWarning("[ClrMD] Cannot list modules - runtime not initialized");
                return results;
            }

            try
            {
                foreach (var module in _runtime.EnumerateModules())
                {
                    try
                    {
                        var info = InspectModuleInternal(module);
                        results.Add(info);
                    }
                    catch (Exception ex)
                    {
                        results.Add(new ClrMdModuleInspection
                        {
                            Address = $"0x{module.Address:X16}",
                            Name = module.Name,
                            Error = ex.Message
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[ClrMD] Error listing modules");
            }

            return results;
        }
    }

    /// <summary>
    /// Searches for a type by name across all modules (ClrMD equivalent of SOS !name2ee).
    /// Enhanced to support generic types that SOS !name2ee struggles with.
    /// </summary>
    /// <param name="typeName">The fully qualified type name to search for.</param>
    /// <param name="moduleName">Optional module name filter (use "*" or null for all modules).</param>
    /// <param name="searchHeapForGenerics">If true, also search heap for constructed generic types.</param>
    /// <returns>Search results with type information.</returns>
    public Name2EEResult Name2EE(string typeName, string? moduleName = null, bool searchHeapForGenerics = true)
    {
        lock (_lock)
        {
            var result = new Name2EEResult
            {
                SearchedTypeName = typeName,
                ModuleFilter = moduleName ?? "*"
            };

            if (_runtime == null)
            {
                _logger?.LogWarning("[ClrMD] Cannot search for type - runtime not initialized");
                result.Error = "Runtime not initialized";
                return result;
            }

            try
            {
                var searchAllModules = string.IsNullOrEmpty(moduleName) || moduleName == "*";
                var moduleEntries = new List<Name2EEModuleEntry>();
                Name2EETypeMatch? foundType = null;
                
                // Normalize the search pattern for generic types
                var normalizedTypeName = NormalizeGenericTypeName(typeName);
                var isGenericSearch = typeName.Contains('<') || typeName.Contains('`');

                // Phase 1: Search modules for type definitions
                foreach (var module in _runtime.EnumerateModules())
                {
                    // Filter by module name if specified
                    if (!searchAllModules)
                    {
                        var modName = module.Name ?? "";
                        if (!modName.Contains(moduleName!, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    var entry = new Name2EEModuleEntry
                    {
                        ModuleAddress = $"0x{module.Address:X16}",
                        AssemblyName = !string.IsNullOrEmpty(module.Name) 
                            ? System.IO.Path.GetFileName(module.Name) 
                            : null
                    };

                    // Search for the type in this module
                    try
                    {
                        ClrType? matchedType = null;
                        
                        foreach (var type in module.EnumerateTypeDefToMethodTableMap())
                        {
                            var clrType = _runtime.GetTypeByMethodTable(type.MethodTable);
                            if (clrType?.Name == null)
                                continue;
                            
                            // Try different matching strategies
                            if (MatchesTypeName(clrType.Name, typeName, normalizedTypeName, isGenericSearch))
                            {
                                matchedType = clrType;
                                break;
                            }
                        }

                        if (matchedType != null)
                        {
                            entry.TypeFound = CreateTypeMatch(matchedType, typeName);
                            
                            // Record first found type
                            if (foundType == null)
                            {
                                foundType = entry.TypeFound;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "[ClrMD] Error searching type in module {Module}", module.Name);
                    }

                    moduleEntries.Add(entry);
                }

                // Phase 2: If not found and searching for generics, scan the heap for constructed generic types
                if (foundType == null && isGenericSearch && searchHeapForGenerics && _runtime.Heap != null)
                {
                    _logger?.LogDebug("[ClrMD] Type not found in modules, searching heap for generic type '{TypeName}'", typeName);
                    
                    var heapMatch = SearchHeapForGenericType(typeName, normalizedTypeName);
                    if (heapMatch != null)
                    {
                        foundType = heapMatch;
                        result.FoundViaHeapSearch = true;
                        
                        // Add a synthetic module entry for the heap-found type
                        moduleEntries.Add(new Name2EEModuleEntry
                        {
                            ModuleAddress = heapMatch.MethodTable, // Use MT as identifier
                            AssemblyName = "(found via heap search)",
                            TypeFound = heapMatch
                        });
                    }
                }

                result.Modules = moduleEntries;
                result.FoundType = foundType;
                result.TotalModulesSearched = moduleEntries.Count;
                result.ModulesWithMatch = moduleEntries.Count(m => m.TypeFound != null);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[ClrMD] Error in Name2EE for type '{TypeName}'", typeName);
                result.Error = ex.Message;
            }

            return result;
        }
    }
    
    /// <summary>
    /// Normalizes a generic type name to the CLR format (e.g., List&lt;T&gt; → List`1).
    /// </summary>
    private static string NormalizeGenericTypeName(string typeName)
    {
        // Handle C#-style generics like "List<T>" or "Dictionary<string, int>"
        if (typeName.Contains('<'))
        {
            // Extract base name and count type parameters
            var angleBracketIndex = typeName.IndexOf('<');
            var baseName = typeName[..angleBracketIndex];
            
            // Count generic parameters by counting commas + 1, handling nested generics
            var paramSection = typeName[(angleBracketIndex + 1)..];
            var paramCount = CountGenericParameters(paramSection);
            
            return $"{baseName}`{paramCount}";
        }
        
        return typeName;
    }
    
    /// <summary>
    /// Counts the number of generic type parameters, handling nested generics.
    /// </summary>
    private static int CountGenericParameters(string paramSection)
    {
        // Remove the closing bracket
        if (paramSection.EndsWith(">", StringComparison.Ordinal))
        {
            paramSection = paramSection[..^1];
        }
        
        var count = 1;
        var depth = 0;
        
        foreach (var c in paramSection)
        {
            switch (c)
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                    count++;
                    break;
            }
        }
        
        return count;
    }
    
    /// <summary>
    /// Checks if a CLR type name matches the search pattern.
    /// </summary>
    private static bool MatchesTypeName(string clrTypeName, string searchName, string normalizedName, bool isGenericSearch)
    {
        // Exact match
        if (clrTypeName.Equals(searchName, StringComparison.Ordinal))
            return true;
        
        // Normalized generic match (e.g., "List`1" matches search for "List<T>")
        if (isGenericSearch && clrTypeName.Equals(normalizedName, StringComparison.Ordinal))
            return true;
        
        // Ends-with for unqualified names (e.g., "MyClass" matches "Namespace.MyClass")
        if (clrTypeName.EndsWith("." + searchName, StringComparison.Ordinal) ||
            clrTypeName.EndsWith("+" + searchName, StringComparison.Ordinal))
            return true;
        
        // Ends-with for normalized generic names
        if (isGenericSearch)
        {
            if (clrTypeName.EndsWith("." + normalizedName, StringComparison.Ordinal) ||
                clrTypeName.EndsWith("+" + normalizedName, StringComparison.Ordinal))
                return true;
            
            // Match base name with generic arity (e.g., "List" matches "System.Collections.Generic.List`1")
            var baseSearchName = searchName.Contains('<') 
                ? searchName[..searchName.IndexOf('<')] 
                : (searchName.Contains('`') ? searchName[..searchName.IndexOf('`')] : searchName);
            
            if (!string.IsNullOrEmpty(baseSearchName))
            {
                // Check if CLR name contains the base name followed by backtick
                var clrBaseName = clrTypeName.Contains('`') 
                    ? clrTypeName[..clrTypeName.LastIndexOf('`')] 
                    : clrTypeName;
                
                if (clrBaseName.EndsWith("." + baseSearchName, StringComparison.Ordinal) ||
                    clrBaseName.EndsWith("+" + baseSearchName, StringComparison.Ordinal) ||
                    clrBaseName.Equals(baseSearchName, StringComparison.Ordinal))
                    return true;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Creates a Name2EETypeMatch from a ClrType.
    /// </summary>
    private static Name2EETypeMatch CreateTypeMatch(ClrType clrType, string searchedName)
    {
        return new Name2EETypeMatch
        {
            Token = $"0x{clrType.MetadataToken:X8}",
            MethodTable = $"0x{clrType.MethodTable:X16}",
            Name = clrType.Name ?? searchedName,
            EEClass = $"0x{clrType.MethodTable:X16}", // In ClrMD, EEClass ≈ MethodTable
            IsGeneric = clrType.Name?.Contains('`') == true
        };
    }
    
    /// <summary>
    /// Searches the heap for constructed generic types matching the search pattern.
    /// This finds instantiated generics like List&lt;string&gt; when the open generic List`1 isn't found.
    /// </summary>
    private Name2EETypeMatch? SearchHeapForGenericType(string typeName, string normalizedTypeName)
    {
        if (_runtime?.Heap == null)
            return null;
        
        var heap = _runtime.Heap;
        
        try
        {
            // Extract the base generic type name for matching
            var baseTypeName = normalizedTypeName.Contains('`') 
                ? normalizedTypeName[..normalizedTypeName.IndexOf('`')] 
                : (typeName.Contains('<') ? typeName[..typeName.IndexOf('<')] : typeName);
            
            var matchedTypes = new List<(ClrType Type, int Score)>();
            var seenMethodTables = new HashSet<ulong>();
            var objectsScanned = 0;
            const int maxObjectsToScan = 100000; // Limit heap scan
            
            foreach (var obj in heap.EnumerateObjects())
            {
                if (++objectsScanned > maxObjectsToScan)
                    break;
                
                var objType = obj.Type;
                if (objType?.Name == null || !objType.Name.Contains('`'))
                    continue;
                
                // Skip if we've already seen this method table
                if (!seenMethodTables.Add(objType.MethodTable))
                    continue;
                
                // Check if this type matches our search pattern
                var score = ScoreGenericTypeMatch(objType.Name, baseTypeName, typeName);
                if (score > 0)
                {
                    matchedTypes.Add((objType, score));
                    
                    // If we found a high-confidence match, stop early
                    if (score >= 100)
                        break;
                }
            }
            
            if (matchedTypes.Count == 0)
                return null;
            
            // Return the best match
            var bestMatch = matchedTypes.OrderByDescending(x => x.Score).First().Type;
            
            _logger?.LogDebug("[ClrMD] Found generic type via heap: {TypeName}", bestMatch.Name);
            
            return new Name2EETypeMatch
            {
                Token = $"0x{bestMatch.MetadataToken:X8}",
                MethodTable = $"0x{bestMatch.MethodTable:X16}",
                Name = bestMatch.Name ?? typeName,
                EEClass = $"0x{bestMatch.MethodTable:X16}",
                IsGeneric = true,
                FoundViaHeapSearch = true
            };
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[ClrMD] Error searching heap for generic type");
            return null;
        }
    }
    
    /// <summary>
    /// Scores how well a CLR type name matches a generic type search pattern.
    /// Returns 0 for no match, higher scores for better matches.
    /// </summary>
    private static int ScoreGenericTypeMatch(string clrTypeName, string baseTypeName, string fullSearchName)
    {
        // Extract CLR base name (before the backtick)
        var clrBaseName = clrTypeName.Contains('`') 
            ? clrTypeName[..clrTypeName.LastIndexOf('`')] 
            : clrTypeName;
        
        // Exact base name match (highest priority)
        if (clrBaseName.Equals(baseTypeName, StringComparison.Ordinal))
            return 100;
        
        // Ends-with base name match
        if (clrBaseName.EndsWith("." + baseTypeName, StringComparison.Ordinal))
            return 90;
        
        // Nested type match
        if (clrBaseName.EndsWith("+" + baseTypeName, StringComparison.Ordinal))
            return 85;
        
        // Contains the base name (lower priority)
        if (clrBaseName.Contains(baseTypeName, StringComparison.Ordinal))
            return 50;
        
        return 0;
    }

    /// <summary>
    /// Searches for a method by name within a type (extended name2ee functionality).
    /// </summary>
    /// <param name="typeName">The fully qualified type name.</param>
    /// <param name="methodName">The method name to search for.</param>
    /// <returns>Search results with method information.</returns>
    public Name2EEMethodResult Name2EEMethod(string typeName, string methodName)
    {
        lock (_lock)
        {
            var result = new Name2EEMethodResult
            {
                TypeName = typeName,
                MethodName = methodName
            };

            if (_runtime == null)
            {
                result.Error = "Runtime not initialized";
                return result;
            }

            try
            {
                // First find the type
                var typeResult = Name2EE(typeName);
                if (typeResult.FoundType == null)
                {
                    result.Error = $"Type '{typeName}' not found";
                    return result;
                }

                // Parse the method table address
                var mtAddress = typeResult.FoundType.MethodTable;
                if (mtAddress != null && mtAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    mtAddress = mtAddress[2..];
                }

                if (!ulong.TryParse(mtAddress, System.Globalization.NumberStyles.HexNumber, null, out var mtAddr))
                {
                    result.Error = "Invalid method table address";
                    return result;
                }

                var clrType = _runtime.GetTypeByMethodTable(mtAddr);
                if (clrType == null)
                {
                    result.Error = "Could not get type from method table";
                    return result;
                }

                result.TypeMethodTable = typeResult.FoundType.MethodTable;
                result.Methods = [];

                // Search for methods matching the name
                foreach (var method in clrType.Methods)
                {
                    if (method.Name != null && 
                        (method.Name.Equals(methodName, StringComparison.Ordinal) ||
                         method.Name.Contains(methodName, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Methods.Add(new Name2EEMethodInfo
                        {
                            Name = method.Name,
                            MethodDesc = $"0x{method.MethodDesc:X16}",
                            NativeCode = method.NativeCode != 0 ? $"0x{method.NativeCode:X16}" : null,
                            Token = $"0x{method.MetadataToken:X8}",
                            Signature = method.Signature
                        });
                    }
                }

                result.TotalMethodsFound = result.Methods.Count;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[ClrMD] Error in Name2EEMethod for '{Type}.{Method}'", typeName, methodName);
                result.Error = ex.Message;
            }

            return result;
        }
    }
    
    /// <summary>
    /// Internal recursive object inspection.
    /// </summary>
    private ClrMdObjectInspection InspectObjectInternal(
        ClrObject obj,
        int depth,
        int maxArrayElements,
        int maxStringLength,
        HashSet<ulong> seenAddresses)
    {
        var objType = obj.Type;
        var result = new ClrMdObjectInspection
        {
            Address = $"0x{obj.Address:x}",
            Type = objType?.Name ?? "Unknown",
            MethodTable = objType?.MethodTable != 0 ? $"0x{objType!.MethodTable:x}" : null,
            Size = obj.Size
        };

        // Handle null type
        if (obj.Type == null)
        {
            result.Error = "Type information unavailable";
            return result;
        }

        // Check for circular references
        if (seenAddresses.Contains(obj.Address))
        {
            result.Value = "[circular reference]";
            return result;
        }
        seenAddresses.Add(obj.Address);

        // Check depth limit
        if (depth <= 0)
        {
            result.Value = "[max depth reached]";
            return result;
        }

        try
        {
            // Handle strings specially
            if (obj.Type.IsString)
            {
                var str = obj.AsString();
                if (str != null && str.Length > maxStringLength)
                {
                    str = str.Substring(0, maxStringLength) + $"... [truncated, total {str.Length} chars]";
                }
                result.Value = str;
                result.IsString = true;
                return result;
            }

            // Handle arrays
            if (obj.IsArray)
            {
                result.IsArray = true;
                result.ArrayLength = obj.AsArray().Length;
                result.ArrayElementType = obj.Type.ComponentType?.Name;
                
                var elements = new List<object?>();
                var arr = obj.AsArray();
                var elementsToShow = Math.Min(arr.Length, maxArrayElements);
                var componentType = obj.Type.ComponentType;
                
                // Determine array element category:
                // 1. Primitive (int, bool, etc.) - use GetValue<T>
                // 2. Simple value type (no pointers) - use GetValue<T> or show as bytes
                // 3. Complex value type (with pointers) - can't use GetObjectValue, show summary
                // 4. Reference type - use GetObjectValue
                var isPrimitive = componentType?.IsPrimitive == true;
                var isSimpleValueType = componentType?.IsValueType == true && componentType?.ContainsPointers == false;
                var isComplexValueType = componentType?.IsValueType == true && componentType?.ContainsPointers == true;
                var isReferenceType = !componentType?.IsValueType == true && !isPrimitive;
                
                for (int i = 0; i < elementsToShow; i++)
                {
                    try
                    {
                        if (isPrimitive || isSimpleValueType)
                        {
                            // For primitive/simple value type arrays, use GetValue directly
                            var primValue = GetArrayPrimitiveValue(arr, i, componentType);
                            elements.Add(primValue);
                        }
                        else if (isComplexValueType)
                        {
                            // Complex value types (structs with reference fields) cannot be read via GetObjectValue
                            // Try to read the struct's fields by computing the element address
                            try
                            {
                                var elementSize = componentType!.StaticSize;
                                var arrayDataStart = arr.Address + (ulong)IntPtr.Size * 2; // Skip header
                                var elementAddress = arrayDataStart + (ulong)(i * elementSize);
                                
                                // Create a summary showing the struct type and address
                                elements.Add(new ClrMdObjectInspection
                                {
                                    Address = $"0x{elementAddress:x}",
                                    Type = componentType.Name ?? "ValueType",
                                    Value = $"[value type at index {i}]"
                                });
                            }
                            catch
                            {
                                elements.Add($"[{componentType?.Name ?? "ValueType"} at index {i}]");
                            }
                        }
                        else
                        {
                            // Reference type array - use GetObjectValue
                            var elemObj = arr.GetObjectValue(i);
                            if (elemObj.IsValid)
                            {
                                if (elemObj.Type?.IsString == true)
                                {
                                    var str = elemObj.AsString();
                                    if (str != null && str.Length > maxStringLength)
                                    {
                                        str = str.Substring(0, maxStringLength) + "...";
                                    }
                                    elements.Add(str);
                                }
                                else if (elemObj.Type?.IsPrimitive == true)
                                {
                                    elements.Add(GetPrimitiveValue(elemObj));
                                }
                                else
                                {
                                    elements.Add(InspectObjectInternal(elemObj, depth - 1, maxArrayElements, maxStringLength, seenAddresses));
                                }
                            }
                            else
                            {
                                elements.Add(null);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log but don't pollute output with full error messages
                        _logger?.LogDebug(ex, "[ClrMD] Error reading array element {Index} of type {Type}", i, componentType?.Name);
                        elements.Add($"[{componentType?.Name ?? "element"} at index {i}]");
                    }
                }
                
                if (arr.Length > maxArrayElements)
                {
                    elements.Add($"[... {arr.Length - maxArrayElements} more elements]");
                }
                
                result.Elements = elements;
                return result;
            }

            // Handle regular objects - enumerate fields
            // Note: obj.Type.Fields returns instance fields only (ClrInstanceField)
            // Static fields would need to be accessed via obj.Type.StaticFields
            result.Fields = new List<ClrMdFieldInspection>();
            
            foreach (var field in obj.Type.Fields)
            {
                var fieldInspection = new ClrMdFieldInspection
                {
                    Name = field.Name ?? "[unnamed]",
                    Type = field.Type?.Name ?? "Unknown",
                    IsStatic = false, // Instance fields are never static
                    Offset = field.Offset
                };

                try
                {
                    if (field.IsPrimitive)
                    {
                        fieldInspection.Value = GetFieldPrimitiveValue(obj, field);
                    }
                    else if (field.Type?.IsString == true)
                    {
                        var fieldObj = field.ReadObject(obj.Address, interior: false);
                        if (fieldObj.IsValid)
                        {
                            var str = fieldObj.AsString();
                            if (str != null && str.Length > maxStringLength)
                            {
                                str = str.Substring(0, maxStringLength) + $"... [truncated]";
                            }
                            fieldInspection.Value = str;
                        }
                        else
                        {
                            fieldInspection.Value = "null";
                        }
                    }
                    else if (field.IsObjectReference)
                    {
                        var fieldObj = field.ReadObject(obj.Address, interior: false);
                        if (fieldObj.IsValid && fieldObj.Address != 0)
                        {
                            if (depth > 1)
                            {
                                fieldInspection.NestedObject = InspectObjectInternal(
                                    fieldObj, depth - 1, maxArrayElements, maxStringLength, seenAddresses);
                            }
                            else
                            {
                                fieldInspection.Value = $"0x{fieldObj.Address:x}";
                            }
                        }
                        else
                        {
                            fieldInspection.Value = "null";
                        }
                    }
                    else if (field.IsValueType)
                    {
                        // Value type embedded in the reference type object
                        // Calculate address: object address + field offset
                        var embeddedVtAddress = obj.Address + (ulong)field.Offset;
                        var embeddedVtType = field.Type;
                        
                        if (embeddedVtType != null && embeddedVtType.MethodTable != 0 && depth > 1)
                        {
                            // Recursively inspect the embedded value type
                            var embeddedResult = TryInspectValueType(
                                embeddedVtAddress, 
                                embeddedVtType.MethodTable, 
                                depth - 1, 
                                maxArrayElements, 
                                maxStringLength, 
                                seenAddresses);
                            
                            if (embeddedResult != null && embeddedResult.Error == null)
                            {
                                fieldInspection.NestedObject = embeddedResult;
                            }
                            else
                            {
                                // Fallback - show address only (type is in Type field)
                                fieldInspection.Value = $"0x{embeddedVtAddress:x}";
                            }
                        }
                        else if (embeddedVtType != null)
                        {
                            // Max depth or no MT - show address only (type is in Type field)
                            fieldInspection.Value = $"0x{embeddedVtAddress:x}";
                        }
                        else
                        {
                            fieldInspection.Value = $"[embedded value type at 0x{embeddedVtAddress:x}]";
                        }
                    }
                    else
                    {
                        fieldInspection.Value = $"[{field.ElementType}]";
                    }
                }
                catch (Exception ex)
                {
                    fieldInspection.Value = $"[error: {ex.Message}]";
                }

                result.Fields.Add(fieldInspection);
            }

            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }
    }
    
    /// <summary>
    /// Gets the primitive value from a ClrObject.
    /// </summary>
    private static object? GetPrimitiveValue(ClrObject obj)
    {
        if (obj.Type == null) return null;
        
        try
        {
            return obj.Type.ElementType switch
            {
                ClrElementType.Boolean => obj.ReadField<bool>("m_value"),
                ClrElementType.Char => obj.ReadField<char>("m_value"),
                ClrElementType.Int8 => obj.ReadField<sbyte>("m_value"),
                ClrElementType.UInt8 => obj.ReadField<byte>("m_value"),
                ClrElementType.Int16 => obj.ReadField<short>("m_value"),
                ClrElementType.UInt16 => obj.ReadField<ushort>("m_value"),
                ClrElementType.Int32 => obj.ReadField<int>("m_value"),
                ClrElementType.UInt32 => obj.ReadField<uint>("m_value"),
                ClrElementType.Int64 => obj.ReadField<long>("m_value"),
                ClrElementType.UInt64 => obj.ReadField<ulong>("m_value"),
                ClrElementType.Float => obj.ReadField<float>("m_value"),
                ClrElementType.Double => obj.ReadField<double>("m_value"),
                ClrElementType.NativeInt => $"0x{obj.ReadField<nint>("m_value"):x}",
                ClrElementType.NativeUInt => $"0x{obj.ReadField<nuint>("m_value"):x}",
                _ => $"0x{obj.Address:x}"
            };
        }
        catch
        {
            return $"0x{obj.Address:x}";
        }
    }
    
    /// <summary>
    /// Gets a primitive value from an array element.
    /// </summary>
    private static object? GetArrayPrimitiveValue(ClrArray arr, int index, ClrType? elementType)
    {
        if (elementType == null) return null;
        
        try
        {
            return elementType.ElementType switch
            {
                ClrElementType.Boolean => arr.GetValue<bool>(index),
                ClrElementType.Char => arr.GetValue<char>(index),
                ClrElementType.Int8 => arr.GetValue<sbyte>(index),
                ClrElementType.UInt8 => arr.GetValue<byte>(index),
                ClrElementType.Int16 => arr.GetValue<short>(index),
                ClrElementType.UInt16 => arr.GetValue<ushort>(index),
                ClrElementType.Int32 => arr.GetValue<int>(index),
                ClrElementType.UInt32 => arr.GetValue<uint>(index),
                ClrElementType.Int64 => arr.GetValue<long>(index),
                ClrElementType.UInt64 => arr.GetValue<ulong>(index),
                ClrElementType.Float => arr.GetValue<float>(index),
                ClrElementType.Double => arr.GetValue<double>(index),
                ClrElementType.NativeInt => $"0x{arr.GetValue<nint>(index):x}",
                ClrElementType.NativeUInt => $"0x{arr.GetValue<nuint>(index):x}",
                _ => $"[{elementType.ElementType}]"
            };
        }
        catch
        {
            return $"[{elementType.ElementType}]";
        }
    }
    
    /// <summary>
    /// Gets a primitive field value.
    /// </summary>
    private static object? GetFieldPrimitiveValue(ClrObject obj, ClrInstanceField field)
    {
        try
        {
            return field.ElementType switch
            {
                ClrElementType.Boolean => field.Read<bool>(obj.Address, interior: false),
                ClrElementType.Char => field.Read<char>(obj.Address, interior: false),
                ClrElementType.Int8 => field.Read<sbyte>(obj.Address, interior: false),
                ClrElementType.UInt8 => field.Read<byte>(obj.Address, interior: false),
                ClrElementType.Int16 => field.Read<short>(obj.Address, interior: false),
                ClrElementType.UInt16 => field.Read<ushort>(obj.Address, interior: false),
                ClrElementType.Int32 => field.Read<int>(obj.Address, interior: false),
                ClrElementType.UInt32 => field.Read<uint>(obj.Address, interior: false),
                ClrElementType.Int64 => field.Read<long>(obj.Address, interior: false),
                ClrElementType.UInt64 => field.Read<ulong>(obj.Address, interior: false),
                ClrElementType.Float => field.Read<float>(obj.Address, interior: false),
                ClrElementType.Double => field.Read<double>(obj.Address, interior: false),
                ClrElementType.NativeInt => $"0x{field.Read<nint>(obj.Address, interior: false):x}",
                ClrElementType.NativeUInt => $"0x{field.Read<nuint>(obj.Address, interior: false):x}",
                _ => $"[{field.ElementType}]"
            };
        }
        catch (Exception ex)
        {
            return $"[error: {ex.Message}]";
        }
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
        
        // Translate numeric state to human-readable description
        info.StateDescription = info.CurrentState switch
        {
            -2 => "Completed",
            -1 => "Not started",
            -99 => "Unknown (could not read state)",
            >= 0 => $"Awaiting at await point {info.CurrentState}",
            _ => $"Unknown state ({info.CurrentState})"
        };
        
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
                    .Sum(kvp =>
                    {
                        var sizePerInstance = kvp.Value.Size / kvp.Value.Count;
                        return kvp.Value.Size - sizePerInstance;
                    });
                analysis.Summary.WastedPercentage = analysis.Summary.TotalSize > 0 
                    ? Math.Round(analysis.Summary.WastedSize * 100.0 / analysis.Summary.TotalSize, 2) 
                    : 0;
                
                // Top duplicates (by wasted bytes)
                analysis.TopDuplicates = stringCounts
                    .Where(kvp => kvp.Value.Count > 1)
                    .OrderByDescending(kvp =>
                    {
                        var sizePerInstance = kvp.Value.Size / kvp.Value.Count;
                        return kvp.Value.Size - sizePerInstance;
                    })
                    .Take(topN)
                    .Select(kvp =>
                    {
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
            .Sum(kvp =>
            {
                var sizePerInstance = kvp.Value.Size / kvp.Value.Count;
                return kvp.Value.Size - sizePerInstance;
            });
        
        var topDuplicates = stringCounts
            .Where(kvp => kvp.Value.Count > 1)
            .OrderByDescending(kvp =>
            {
                var sizePerInstance = kvp.Value.Size / kvp.Value.Count;
                return kvp.Value.Size - sizePerInstance;
            })
            .Take(topN)
            .Select(kvp =>
            {
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

    /// <summary>
    /// Gets PDB GUIDs for the specified module names from the dump.
    /// </summary>
    /// <param name="moduleNames">List of module names (without extension) to look up.</param>
    /// <returns>Dictionary mapping module name to its PDB GUID.</returns>
    public Dictionary<string, Guid> GetModulePdbGuids(IEnumerable<string> moduleNames)
    {
        var guids = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            if (_runtime == null)
            {
                _logger?.LogWarning("[ClrMD] Runtime not available, cannot extract PDB GUIDs");
                return guids;
            }

            var moduleNameSet = new HashSet<string>(moduleNames, StringComparer.OrdinalIgnoreCase);

            foreach (var module in _runtime.EnumerateModules())
            {
                try
                {
                    var moduleName = Path.GetFileNameWithoutExtension(module.Name);
                    if (string.IsNullOrEmpty(moduleName) || !moduleNameSet.Contains(moduleName))
                        continue;

                    // Get PDB info from the module
                    var pdbInfo = module.Pdb;
                    if (pdbInfo == null || pdbInfo.Guid == Guid.Empty)
                    {
                        _logger?.LogDebug("[ClrMD] No PDB info for module: {Name}", moduleName);
                        continue;
                    }

                    guids[moduleName] = pdbInfo.Guid;
                    _logger?.LogInformation("[ClrMD] PDB GUID for {Name}: {Guid}", moduleName, pdbInfo.Guid);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "[ClrMD] Error getting PDB info for module: {Name}", module.Name);
                }
            }
        }

        return guids;
    }
    
    /// <summary>
    /// Gets all native module paths from the dump.
    /// This includes native libraries like ld-musl, libcoreclr, etc.
    /// Useful for platform detection (Alpine vs glibc).
    /// </summary>
    /// <returns>List of native module paths.</returns>
    public List<string> GetNativeModulePaths()
    {
        var paths = new List<string>();
        
        lock (_lock)
        {
            if (_dataTarget == null)
            {
                _logger?.LogWarning("[ClrMD] DataTarget not available, cannot get native modules");
                return paths;
            }
            
            try
            {
                foreach (var module in _dataTarget.EnumerateModules())
                {
                    if (!string.IsNullOrEmpty(module.FileName))
                    {
                        paths.Add(module.FileName);
                    }
                }
                
                _logger?.LogDebug("[ClrMD] Found {Count} native modules in dump", paths.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[ClrMD] Error enumerating native modules");
            }
        }
        
        return paths;
    }
    
    /// <summary>
    /// Detects if the dump is from an Alpine/musl system by checking native module paths.
    /// </summary>
    /// <returns>True if musl is detected, false otherwise.</returns>
    public bool DetectIsAlpine()
    {
        var nativeModules = GetNativeModulePaths();
        
        // Look for musl indicators in native module paths
        // e.g., /lib/ld-musl-x86_64.so.1, /lib/ld-musl-aarch64.so.1
        foreach (var path in nativeModules)
        {
            var pathLower = path.ToLowerInvariant();
            if (pathLower.Contains("ld-musl") || pathLower.Contains("musl-"))
            {
                _logger?.LogInformation("[ClrMD] Detected Alpine/musl from module: {Path}", path);
                return true;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Detects the architecture from native module paths.
    /// </summary>
    /// <returns>Architecture string (x64, arm64, x86) or null if not detected.</returns>
    public string? DetectArchitecture()
    {
        var nativeModules = GetNativeModulePaths();
        
        foreach (var path in nativeModules)
        {
            var pathLower = path.ToLowerInvariant();
            
            // Check for architecture indicators in paths
            if (pathLower.Contains("aarch64") || pathLower.Contains("-arm64") || pathLower.Contains("/arm64/"))
            {
                _logger?.LogInformation("[ClrMD] Detected arm64 architecture from module: {Path}", path);
                return "arm64";
            }
            if (pathLower.Contains("x86_64") || pathLower.Contains("x86-64") || pathLower.Contains("amd64") || pathLower.Contains("/x64/"))
            {
                _logger?.LogInformation("[ClrMD] Detected x64 architecture from module: {Path}", path);
                return "x64";
            }
            if (pathLower.Contains("i386") || pathLower.Contains("i686") || pathLower.Contains("/x86/"))
            {
                _logger?.LogInformation("[ClrMD] Detected x86 architecture from module: {Path}", path);
                return "x86";
            }
        }
        
        return null;
    }

    #region ClrStack Implementation

    private SourceLink.SequencePointResolver? _sequencePointResolver;

    /// <summary>
    /// Sets the sequence point resolver for source location resolution.
    /// </summary>
    /// <param name="resolver">The resolver instance.</param>
    public void SetSequencePointResolver(SourceLink.SequencePointResolver resolver)
    {
        _sequencePointResolver = resolver;
    }

    /// <summary>
    /// Walks the stack for all threads, returning managed frame information.
    /// This is a fast ClrMD-based alternative to SOS clrstack command.
    /// </summary>
    /// <param name="includeArguments">Include method arguments in output.</param>
    /// <param name="includeLocals">Include local variables in output.</param>
    /// <returns>Stack information for all threads.</returns>
    public ClrStackResult GetAllThreadStacks(bool includeArguments = true, bool includeLocals = true)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new ClrStackResult();

        lock (_lock)
        {
            if (_runtime == null)
            {
                result.Error = "Runtime not initialized";
                return result;
            }

            try
            {
                foreach (var thread in _runtime.Threads)
                {
                    try
                    {
                        // Check if this thread has an unhandled exception (faulting thread)
                        var isFaulting = thread.CurrentException != null;
                        
                        var threadInfo = new ClrThreadStack
                        {
                            OSThreadId = thread.OSThreadId,
                            ManagedThreadId = thread.ManagedThreadId,
                            IsAlive = thread.IsAlive,
                            IsBackground = thread.IsFinalizer || thread.IsGc, // Approximation: GC/Finalizer are background
                            IsFaulting = isFaulting
                        };

                        // Collect frames first
                        var frames = thread.EnumerateStackTrace().ToList();

                        // Build frame→roots lookup (one pass per thread) if we need args/locals
                        Dictionary<ulong, List<ClrStackRoot>>? frameRoots = null;
                        if (includeArguments || includeLocals)
                        {
                            try
                            {
                                frameRoots = BuildFrameRootsLookup(thread, frames);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogDebug(ex, "[ClrStack] Failed to build frame roots for thread {ThreadId}", thread.OSThreadId);
                            }
                        }

                        int frameIndex = 0;
                        foreach (var frame in frames)
                        {
                            try
                            {
                                var roots = frameRoots?.GetValueOrDefault(frame.StackPointer)
                                            ?? new List<ClrStackRoot>();

                                var frameInfo = new ClrFrameInfo
                                {
                                    FrameIndex = frameIndex++,
                                    StackPointer = frame.StackPointer,
                                    InstructionPointer = frame.InstructionPointer,
                                    Kind = frame.Kind.ToString()
                                };

                                // Get method info
                                if (frame.Method != null)
                                {
                                    frameInfo.Method = new ClrMethodInfo
                                    {
                                        Signature = frame.Method.Signature,
                                        TypeName = frame.Method.Type?.Name,
                                        MethodName = frame.Method.Name,
                                        MetadataToken = (uint)frame.Method.MetadataToken,
                                        NativeCode = frame.Method.NativeCode,
                                        ILOffset = GetILOffset(frame)
                                    };

                                    // Resolve source location from PDB
                                    if (_sequencePointResolver != null)
                                    {
                                        var modulePath = frame.Method.Type?.Module?.Name;
                                        if (!string.IsNullOrEmpty(modulePath))
                                        {
                                            var ilOffset = frameInfo.Method.ILOffset;
                                            
                                            // If IL offset is invalid, try using offset 0 to at least get method start location
                                            if (ilOffset < 0)
                                                ilOffset = 0;
                                            
                                            frameInfo.SourceLocation = _sequencePointResolver.GetSourceLocation(
                                                modulePath,
                                                (uint)(frame.Method.MetadataToken & 0x00FFFFFF), // Row number only
                                                ilOffset
                                            );
                                        }
                                    }
                                }

                                // Get arguments
                                if (includeArguments)
                                {
                                    frameInfo.Arguments = GetStackArguments(roots, frame.Method);
                                }

                                // Get locals
                                if (includeLocals)
                                {
                                    frameInfo.Locals = GetStackLocals(roots, frame.Method);
                                }

                                threadInfo.Frames.Add(frameInfo);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogDebug(ex, "[ClrStack] Error processing frame {Index} for thread {ThreadId}",
                                    frameIndex, thread.OSThreadId);
                            }
                        }

                        result.Threads.Add(threadInfo);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "[ClrStack] Error processing thread {ThreadId}", thread.OSThreadId);
                    }
                }

                result.DurationMs = stopwatch.ElapsedMilliseconds;
                _logger?.LogInformation("[ClrStack] Collected {Threads} threads, {Frames} frames in {Duration}ms",
                    result.TotalThreads, result.TotalFrames, result.DurationMs);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[ClrStack] Error collecting thread stacks");
                result.Error = ex.Message;
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the IL offset for a stack frame.
    /// </summary>
    private int GetILOffset(ClrStackFrame frame)
    {
        if (frame.Method == null)
            return -1;

        try
        {
            // Use ILOffsetMap to find the IL offset for this native IP
            var map = frame.Method.ILOffsetMap;
            if (map == null || map.Length == 0)
                return -1;

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

    /// <summary>
    /// Builds a lookup of stack roots indexed by frame stack pointer.
    /// </summary>
    private Dictionary<ulong, List<ClrStackRoot>> BuildFrameRootsLookup(
        ClrThread thread,
        List<ClrStackFrame> frames)
    {
        var result = new Dictionary<ulong, List<ClrStackRoot>>();

        // Initialize empty lists for each frame
        foreach (var frame in frames)
        {
            if (!result.ContainsKey(frame.StackPointer))
                result[frame.StackPointer] = new List<ClrStackRoot>();
        }

        if (frames.Count == 0)
            return result;

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

    /// <summary>
    /// Gets method arguments from stack roots.
    /// </summary>
    private List<ClrArgumentInfo> GetStackArguments(List<ClrStackRoot> roots, ClrMethod? method)
    {
        var args = new List<ClrArgumentInfo>();

        if (method == null || roots.Count == 0)
            return args;

        try
        {
            // Estimate parameter count from signature (count commas + 1)
            // This is a heuristic - the actual count may differ
            var paramCount = 0;
            if (!string.IsNullOrEmpty(method.Signature))
            {
                var sig = method.Signature;
                var parenStart = sig.IndexOf('(');
                var parenEnd = sig.LastIndexOf(')');
                if (parenStart >= 0 && parenEnd > parenStart)
                {
                    var paramPart = sig.Substring(parenStart + 1, parenEnd - parenStart - 1).Trim();
                    if (!string.IsNullOrEmpty(paramPart))
                    {
                        paramCount = paramPart.Count(c => c == ',') + 1;
                    }
                }
            }

            // Detect if method is instance or static by checking if first root's type
            // matches or is compatible with the method's declaring type
            var isInstanceMethod = false;
            if (roots.Count > 0 && method.Type != null)
            {
                var firstRootType = roots[0].Object.Type;
                if (firstRootType != null)
                {
                    // Check if first root's type matches or inherits from declaring type
                    var declaringTypeName = method.Type.Name;
                    var firstTypeName = firstRootType.Name;
                    isInstanceMethod = firstTypeName == declaringTypeName ||
                                       (firstRootType.BaseType?.Name == declaringTypeName) ||
                                       (declaringTypeName != null && firstTypeName != null &&
                                        firstTypeName.Contains(declaringTypeName));
                }
            }

            // First few roots are typically 'this' (if instance method) + parameters
            // Then come locals
            var maxArgs = Math.Min(roots.Count, paramCount + (isInstanceMethod ? 1 : 0));

            for (int i = 0; i < maxArgs; i++)
            {
                var root = roots[i];
                
                // Name the first argument 'this' only for instance methods
                string argName;
                if (i == 0 && isInstanceMethod)
                    argName = "this";
                else
                    argName = $"arg{(isInstanceMethod ? i - 1 : i)}";

                var argInfo = new ClrArgumentInfo
                {
                    Index = i,
                    Name = argName,
                    TypeName = root.Object.Type?.Name,
                    Address = root.Address,
                    HasValue = root.Address != 0 && root.Object.Address != 0
                };

                if (argInfo.HasValue)
                {
                    argInfo.ValueString = FormatStackValue(root.Object);
                    argInfo.Value = GetStackPrimitiveValue(root.Object);
                }

                args.Add(argInfo);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[ClrStack] Failed to enumerate arguments");
        }

        return args;
    }

    /// <summary>
    /// Gets local variables from stack roots.
    /// </summary>
    private List<ClrLocalInfo> GetStackLocals(List<ClrStackRoot> roots, ClrMethod? method)
    {
        var locals = new List<ClrLocalInfo>();

        if (method == null || roots.Count == 0)
            return locals;

        try
        {
            // Estimate parameter count to skip
            var paramCount = 0;
            if (!string.IsNullOrEmpty(method.Signature))
            {
                var sig = method.Signature;
                var parenStart = sig.IndexOf('(');
                var parenEnd = sig.LastIndexOf(')');
                if (parenStart >= 0 && parenEnd > parenStart)
                {
                    var paramPart = sig.Substring(parenStart + 1, parenEnd - parenStart - 1).Trim();
                    if (!string.IsNullOrEmpty(paramPart))
                    {
                        paramCount = paramPart.Count(c => c == ',') + 1;
                    }
                }
            }

            // Detect if instance method (same logic as GetStackArguments)
            var isInstanceMethod = false;
            if (roots.Count > 0 && method.Type != null)
            {
                var firstRootType = roots[0].Object.Type;
                if (firstRootType != null)
                {
                    var declaringTypeName = method.Type.Name;
                    var firstTypeName = firstRootType.Name;
                    isInstanceMethod = firstTypeName == declaringTypeName ||
                                       (firstRootType.BaseType?.Name == declaringTypeName) ||
                                       (declaringTypeName != null && firstTypeName != null &&
                                        firstTypeName.Contains(declaringTypeName));
                }
            }

            // Skip args (including 'this' for instance methods)
            var argsToSkip = paramCount + (isInstanceMethod ? 1 : 0);
            var localRoots = roots.Skip(argsToSkip);

            int index = 0;
            foreach (var root in localRoots)
            {
                var localInfo = new ClrLocalInfo
                {
                    Index = index,
                    Name = null, // Would require PDB local variable info
                    TypeName = root.Object.Type?.Name,
                    Address = root.Address,
                    HasValue = root.Address != 0 && root.Object.Address != 0
                };

                if (localInfo.HasValue)
                {
                    localInfo.ValueString = FormatStackValue(root.Object);
                    localInfo.Value = GetStackPrimitiveValue(root.Object);
                }

                locals.Add(localInfo);
                index++;

                // Limit number of locals to prevent huge outputs
                if (index >= 20)
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[ClrStack] Failed to enumerate locals");
        }

        return locals;
    }

    /// <summary>
    /// Formats a stack object value for display.
    /// </summary>
    private string FormatStackValue(ClrObject obj)
    {
        if (obj.IsNull)
            return "null";

        var type = obj.Type;
        if (type == null)
            return $"0x{obj.Address:X}";

        try
        {
            // Primitives
            if (type.IsPrimitive)
            {
                return type.ElementType switch
                {
                    ClrElementType.Boolean => obj.ReadBoxedValue<bool>().ToString(),
                    ClrElementType.Int32 => obj.ReadBoxedValue<int>().ToString(),
                    ClrElementType.Int64 => obj.ReadBoxedValue<long>().ToString(),
                    ClrElementType.Double => obj.ReadBoxedValue<double>().ToString("G"),
                    ClrElementType.Float => obj.ReadBoxedValue<float>().ToString("G"),
                    ClrElementType.UInt32 => obj.ReadBoxedValue<uint>().ToString(),
                    ClrElementType.UInt64 => obj.ReadBoxedValue<ulong>().ToString(),
                    ClrElementType.Int16 => obj.ReadBoxedValue<short>().ToString(),
                    ClrElementType.UInt16 => obj.ReadBoxedValue<ushort>().ToString(),
                    ClrElementType.Char => $"'{obj.ReadBoxedValue<char>()}'",
                    ClrElementType.Int8 => obj.ReadBoxedValue<sbyte>().ToString(),
                    ClrElementType.UInt8 => obj.ReadBoxedValue<byte>().ToString(),
                    _ => $"0x{obj.Address:X}"
                };
            }

            // Strings
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

            // Reference types - show address only (type is in TypeName field)
            return $"0x{obj.Address:X}";
        }
        catch
        {
            return $"0x{obj.Address:X}";
        }
    }

    /// <summary>
    /// Gets a primitive value for JSON serialization.
    /// </summary>
    private object? GetStackPrimitiveValue(ClrObject obj)
    {
        if (obj.IsNull || obj.Type == null)
            return null;

        if (!obj.Type.IsPrimitive && !obj.Type.IsString)
            return null;

        try
        {
            if (obj.Type.IsString)
            {
                var str = obj.AsString();
                if (str != null && str.Length > 100)
                    return str[..100] + "...";
                return str;
            }

            return obj.Type.ElementType switch
            {
                ClrElementType.Boolean => obj.ReadBoxedValue<bool>(),
                ClrElementType.Int32 => obj.ReadBoxedValue<int>(),
                ClrElementType.Int64 => obj.ReadBoxedValue<long>(),
                ClrElementType.Double => obj.ReadBoxedValue<double>(),
                ClrElementType.Float => obj.ReadBoxedValue<float>(),
                ClrElementType.UInt32 => obj.ReadBoxedValue<uint>(),
                ClrElementType.UInt64 => obj.ReadBoxedValue<ulong>(),
                ClrElementType.Int16 => obj.ReadBoxedValue<short>(),
                ClrElementType.UInt16 => obj.ReadBoxedValue<ushort>(),
                ClrElementType.Char => obj.ReadBoxedValue<char>().ToString(),
                ClrElementType.Int8 => obj.ReadBoxedValue<sbyte>(),
                ClrElementType.UInt8 => obj.ReadBoxedValue<byte>(),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    #endregion
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

/// <summary>
/// Result of ClrMD object inspection.
/// </summary>
public class ClrMdObjectInspection
{
    /// <summary>
    /// Gets or sets the object address.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the type name.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string? Type { get; set; }
    
    /// <summary>
    /// Gets or sets the method table address.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("methodTable")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? MethodTable { get; set; }
    
    /// <summary>
    /// Gets or sets the object size in bytes.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("size")]
    public ulong Size { get; set; }
    
    /// <summary>
    /// Gets or sets the value (for primitives, strings, or error markers).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("value")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public object? Value { get; set; }
    
    /// <summary>
    /// Gets or sets whether this is a string object.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("isString")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsString { get; set; }
    
    /// <summary>
    /// Gets or sets whether this is an array.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("isArray")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsArray { get; set; }
    
    /// <summary>
    /// Gets or sets the array length (if IsArray).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("arrayLength")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? ArrayLength { get; set; }
    
    /// <summary>
    /// Gets or sets the array element type (if IsArray).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("arrayElementType")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ArrayElementType { get; set; }
    
    /// <summary>
    /// Gets or sets array elements (if IsArray).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("elements")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<object?>? Elements { get; set; }
    
    /// <summary>
    /// Gets or sets the object fields.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("fields")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<ClrMdFieldInspection>? Fields { get; set; }
    
    /// <summary>
    /// Gets or sets any error that occurred during inspection.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("error")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

/// <summary>
/// Result of ClrMD field inspection.
/// </summary>
public class ClrMdFieldInspection
{
    /// <summary>
    /// Gets or sets the field name.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the field type name.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string? Type { get; set; }
    
    /// <summary>
    /// Gets or sets whether the field is static.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("isStatic")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsStatic { get; set; }
    
    /// <summary>
    /// Gets or sets the field offset.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("offset")]
    public int Offset { get; set; }
    
    /// <summary>
    /// Gets or sets the field value (for primitives, strings, or error markers).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("value")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public object? Value { get; set; }
    
    /// <summary>
    /// Gets or sets the nested object inspection (for reference types).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("nestedObject")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ClrMdObjectInspection? NestedObject { get; set; }
}

/// <summary>
/// Result of ClrMD module inspection.
/// </summary>
public class ClrMdModuleInspection
{
    /// <summary>
    /// Gets or sets the module address.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the module file path/name.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the image base address.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("imageBase")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageBase { get; set; }

    /// <summary>
    /// Gets or sets the module size in bytes.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("size")]
    public ulong Size { get; set; }

    /// <summary>
    /// Gets or sets whether this is a PE file.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("isPEFile")]
    public bool IsPEFile { get; set; }

    /// <summary>
    /// Gets or sets whether this module is dynamic (generated at runtime).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("isDynamic")]
    public bool IsDynamic { get; set; }

    /// <summary>
    /// Gets or sets the module layout (Flat, Loaded, Unknown).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("layout")]
    public string Layout { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the assembly address.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("assemblyAddress")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AssemblyAddress { get; set; }

    /// <summary>
    /// Gets or sets the assembly name.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("assemblyName")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AssemblyName { get; set; }

    /// <summary>
    /// Gets or sets the metadata start address.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("metadataAddress")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? MetadataAddress { get; set; }

    /// <summary>
    /// Gets or sets the metadata size in bytes.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("metadataLength")]
    public ulong MetadataLength { get; set; }

    /// <summary>
    /// Gets or sets the PDB information.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("pdb")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ClrMdPdbInfo? Pdb { get; set; }

    /// <summary>
    /// Gets or sets the number of types defined in this module.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("typeCount")]
    public int TypeCount { get; set; }

    /// <summary>
    /// Gets or sets the assembly version.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("version")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets any error that occurred during inspection.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("error")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

/// <summary>
/// PDB information for a module.
/// </summary>
public class ClrMdPdbInfo
{
    /// <summary>
    /// Gets or sets the PDB file path.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("path")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the PDB GUID.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("guid")]
    public string Guid { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the PDB age/revision.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("revision")]
    public int Revision { get; set; }
}

/// <summary>
/// Result of Name2EE type search (ClrMD equivalent of SOS !name2ee).
/// </summary>
public class Name2EEResult
{
    /// <summary>
    /// The type name that was searched for.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("searchedTypeName")]
    public string SearchedTypeName { get; set; } = string.Empty;

    /// <summary>
    /// The module filter used (or "*" for all modules).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("moduleFilter")]
    public string ModuleFilter { get; set; } = "*";

    /// <summary>
    /// Total number of modules searched.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("totalModulesSearched")]
    public int TotalModulesSearched { get; set; }

    /// <summary>
    /// Number of modules where the type was found.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("modulesWithMatch")]
    public int ModulesWithMatch { get; set; }
    
    /// <summary>
    /// Whether the type was found via heap search (for constructed generic types).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("foundViaHeapSearch")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool FoundViaHeapSearch { get; set; }

    /// <summary>
    /// The first/primary type match found.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("foundType")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Name2EETypeMatch? FoundType { get; set; }

    /// <summary>
    /// List of all modules searched with their results.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("modules")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<Name2EEModuleEntry>? Modules { get; set; }

    /// <summary>
    /// Error message if search failed.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("error")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

/// <summary>
/// Module entry in Name2EE search results.
/// </summary>
public class Name2EEModuleEntry
{
    /// <summary>
    /// Module address.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("moduleAddress")]
    public string ModuleAddress { get; set; } = string.Empty;

    /// <summary>
    /// Assembly name (file name).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("assemblyName")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AssemblyName { get; set; }

    /// <summary>
    /// Type match found in this module (null if type not found in this module).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("typeFound")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Name2EETypeMatch? TypeFound { get; set; }
}

/// <summary>
/// Type match information from Name2EE search.
/// </summary>
public class Name2EETypeMatch
{
    /// <summary>
    /// Metadata token.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Method table address.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("methodTable")]
    public string MethodTable { get; set; } = string.Empty;

    /// <summary>
    /// EEClass address.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("eeClass")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? EEClass { get; set; }

    /// <summary>
    /// Full type name.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether this is a generic type.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("isGeneric")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsGeneric { get; set; }
    
    /// <summary>
    /// Whether this type was found via heap search (for constructed generics).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("foundViaHeapSearch")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool FoundViaHeapSearch { get; set; }
}

/// <summary>
/// Result of Name2EE method search.
/// </summary>
public class Name2EEMethodResult
{
    /// <summary>
    /// The type name searched.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("typeName")]
    public string TypeName { get; set; } = string.Empty;

    /// <summary>
    /// The method name searched for.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("methodName")]
    public string MethodName { get; set; } = string.Empty;

    /// <summary>
    /// Method table address of the containing type.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("typeMethodTable")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeMethodTable { get; set; }

    /// <summary>
    /// Number of methods found matching the name.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("totalMethodsFound")]
    public int TotalMethodsFound { get; set; }

    /// <summary>
    /// List of matching methods.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("methods")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<Name2EEMethodInfo>? Methods { get; set; }

    /// <summary>
    /// Error message if search failed.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("error")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

/// <summary>
/// Method information from Name2EE method search.
/// </summary>
public class Name2EEMethodInfo
{
    /// <summary>
    /// Method name.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Method descriptor address.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("methodDesc")]
    public string MethodDesc { get; set; } = string.Empty;

    /// <summary>
    /// Native code address (null if not JIT compiled).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("nativeCode")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? NativeCode { get; set; }

    /// <summary>
    /// Metadata token.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Method signature.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("signature")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; set; }
}

#region ClrStack Data Models

/// <summary>
/// Result of ClrMD-based stack walking for all threads.
/// </summary>
public class ClrStackResult
{
    /// <summary>
    /// Stack information for each thread.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("threads")]
    public List<ClrThreadStack> Threads { get; set; } = new();

    /// <summary>
    /// Total number of threads.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("totalThreads")]
    public int TotalThreads => Threads.Count;

    /// <summary>
    /// Total number of frames across all threads.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("totalFrames")]
    public int TotalFrames => Threads.Sum(t => t.Frames.Count);

    /// <summary>
    /// Time taken to collect stack information (milliseconds).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    /// <summary>
    /// Error message if collection failed.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("error")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

/// <summary>
/// Stack information for a single thread.
/// </summary>
public class ClrThreadStack
{
    /// <summary>
    /// Operating system thread ID.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("osThreadId")]
    public uint OSThreadId { get; set; }

    /// <summary>
    /// Managed thread ID.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("managedThreadId")]
    public int ManagedThreadId { get; set; }

    /// <summary>
    /// Whether the thread is alive.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("isAlive")]
    public bool IsAlive { get; set; }

    /// <summary>
    /// Whether the thread is a background thread.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("isBackground")]
    public bool IsBackground { get; set; }

    /// <summary>
    /// Whether this is the faulting/crashing thread.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("isFaulting")]
    public bool IsFaulting { get; set; }

    /// <summary>
    /// Registers for the top frame (optional, from LLDB).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("topFrameRegisters")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ClrRegisterSet? TopFrameRegisters { get; set; }

    /// <summary>
    /// Stack frames for this thread.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("frames")]
    public List<ClrFrameInfo> Frames { get; set; } = new();
}

/// <summary>
/// Information about a single stack frame.
/// </summary>
public class ClrFrameInfo
{
    /// <summary>
    /// Frame index (0 = top of stack).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("frameIndex")]
    public int FrameIndex { get; set; }

    /// <summary>
    /// Stack pointer value.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("stackPointer")]
    public ulong StackPointer { get; set; }

    /// <summary>
    /// Instruction pointer value.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("instructionPointer")]
    public ulong InstructionPointer { get; set; }

    /// <summary>
    /// Frame kind (Managed, Runtime, Unknown, etc.).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; set; }

    /// <summary>
    /// Method information (null for non-managed frames).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("method")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ClrMethodInfo? Method { get; set; }

    /// <summary>
    /// Source location from PDB (if available).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("sourceLocation")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SourceLink.SourceLocation? SourceLocation { get; set; }

    /// <summary>
    /// Method arguments.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("arguments")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<ClrArgumentInfo>? Arguments { get; set; }

    /// <summary>
    /// Local variables.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("locals")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<ClrLocalInfo>? Locals { get; set; }
}

/// <summary>
/// Method information for a stack frame.
/// </summary>
public class ClrMethodInfo
{
    /// <summary>
    /// Full method signature.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("signature")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; set; }

    /// <summary>
    /// Declaring type name.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("typeName")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeName { get; set; }

    /// <summary>
    /// Method name.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("methodName")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? MethodName { get; set; }

    /// <summary>
    /// Metadata token.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("metadataToken")]
    public uint MetadataToken { get; set; }

    /// <summary>
    /// Native code address.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("nativeCode")]
    public ulong NativeCode { get; set; }

    /// <summary>
    /// IL offset within the method (-1 if not available).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("ilOffset")]
    public int ILOffset { get; set; } = -1;
}

/// <summary>
/// Information about a method argument.
/// </summary>
public class ClrArgumentInfo
{
    /// <summary>
    /// Argument index (0 = first).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>
    /// Argument name (may be "this", "argN", or actual name if available).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// Argument type name.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("typeName")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeName { get; set; }

    /// <summary>
    /// Stack address where the argument is stored.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("address")]
    public ulong Address { get; set; }

    /// <summary>
    /// Primitive value (for JSON serialization).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("value")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public object? Value { get; set; }

    /// <summary>
    /// Formatted value string for display.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("valueString")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ValueString { get; set; }

    /// <summary>
    /// Whether a value was successfully read.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("hasValue")]
    public bool HasValue { get; set; }
}

/// <summary>
/// Information about a local variable.
/// </summary>
public class ClrLocalInfo
{
    /// <summary>
    /// Local slot index.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>
    /// Local variable name (from PDB, may be null).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// Variable type name.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("typeName")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeName { get; set; }

    /// <summary>
    /// Stack address where the variable is stored.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("address")]
    public ulong Address { get; set; }

    /// <summary>
    /// Primitive value (for JSON serialization).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("value")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public object? Value { get; set; }

    /// <summary>
    /// Formatted value string for display.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("valueString")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ValueString { get; set; }

    /// <summary>
    /// Whether a value was successfully read.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("hasValue")]
    public bool HasValue { get; set; }
}

/// <summary>
/// Register set for a thread's top frame.
/// </summary>
public class ClrRegisterSet
{
    /// <summary>
    /// General purpose registers (x0-x28 on ARM64, rax/rbx/etc on x64).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("generalPurpose")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, ulong>? GeneralPurpose { get; set; }

    /// <summary>
    /// Frame pointer (fp on ARM64, rbp on x64).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("framePointer")]
    public ulong FramePointer { get; set; }

    /// <summary>
    /// Link register (lr on ARM64, not applicable on x64).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("linkRegister")]
    public ulong LinkRegister { get; set; }

    /// <summary>
    /// Stack pointer (sp).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("stackPointer")]
    public ulong StackPointer { get; set; }

    /// <summary>
    /// Program counter (pc on ARM64, rip on x64).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("programCounter")]
    public ulong ProgramCounter { get; set; }

    /// <summary>
    /// Status register (cpsr on ARM64, rflags on x64).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("statusRegister")]
    public uint StatusRegister { get; set; }
}

#endregion

