using System.Text.Json;
using System.Text.RegularExpressions;
using DebuggerMcp.Analysis;
using DebuggerMcp.ObjectInspection.Models;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.ObjectInspection;

/// <summary>
/// Inspects .NET objects and produces a JSON representation of their structure.
/// </summary>
public partial class ObjectInspector
{
    private readonly ILogger _logger;
    private readonly ClrMdAnalyzer? _clrMdAnalyzer;

    // Internal caches for resolved data (cleared per inspection session)
    private readonly Dictionary<string, string?> _typeNameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _enumNameCache = new(StringComparer.OrdinalIgnoreCase);

    // Static cache for full inspection results (shared across all ObjectInspector instances)
    // This dramatically speeds up repeated inspections of the same objects during analysis/reports
    private static readonly Dictionary<string, InspectedObject> s_inspectionCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object s_cacheLock = new();

    /// <summary>
    /// Gets the current number of cached inspection results.
    /// </summary>
    public static int CacheCount
    {
        get
        {
            lock (s_cacheLock)
            {
                return s_inspectionCache.Count;
            }
        }
    }

    /// <summary>
    /// Default maximum recursion depth.
    /// </summary>
    public const int DefaultMaxDepth = 5;

    /// <summary>
    /// Default maximum array elements to show.
    /// </summary>
    public const int DefaultMaxArrayElements = 10;

    /// <summary>
    /// Default maximum string length.
    /// </summary>
    public const int DefaultMaxStringLength = 1024;

    // Marker strings for special cases
    private const string MarkerThis = "[this]";
    private const string MarkerSeen = "[seen]";
    private const string MarkerMaxDepth = "[max depth]";
    private const string MarkerErrorPrefix = "[error: ";

    [GeneratedRegex(@"^[0-9a-fA-F]+\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex ArrayElementRegex();

    /// <summary>
    /// Initializes a new instance of the <see cref="ObjectInspector"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="clrMdAnalyzer">Optional ClrMD analyzer. When provided, uses ClrMD for dumpobj instead of SOS.</param>
    public ObjectInspector(ILogger logger, ClrMdAnalyzer? clrMdAnalyzer = null)
    {
        _logger = logger;
        _clrMdAnalyzer = clrMdAnalyzer;
    }
    
    /// <summary>
    /// Executes dumpobj using ClrMD when available, otherwise falls back to SOS.
    /// </summary>
    private DumpObjectResult ExecuteDumpObj(IDebuggerManager manager, string address)
    {
        // Clean address - may contain type info like "0x1234 (System.String)"
        var cleanAddress = address.Trim();
        if (cleanAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            cleanAddress = cleanAddress[2..];
        var spaceIndex = cleanAddress.IndexOf(' ');
        if (spaceIndex > 0)
            cleanAddress = cleanAddress[..spaceIndex];
        
        // Use ClrMD when available
        if (_clrMdAnalyzer?.IsOpen == true)
        {
            if (ulong.TryParse(cleanAddress, System.Globalization.NumberStyles.HexNumber, null, out var addressValue))
            {
                try
                {
                    var clrMdResult = _clrMdAnalyzer.InspectObject(addressValue, maxDepth: 5, maxArrayElements: 10, maxStringLength: 1024);
                    if (clrMdResult != null && clrMdResult.Error == null)
                    {
                        // Convert ClrMD result to DumpObjectResult format
                        return ConvertClrMdToDumpObjectResult(clrMdResult);
                    }
                    _logger.LogDebug("ClrMD dumpobj failed for {Address}: {Error}", address, clrMdResult?.Error ?? "null");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ClrMD dumpobj threw for {Address}", address);
                }
            }
            
            // ClrMD failed - return error (no SOS fallback when ClrMD is available)
            return new DumpObjectResult { ErrorMessage = "ClrMD inspection failed" };
        }
        
        // Fall back to SOS when ClrMD is not available
        var output = manager.ExecuteCommand($"dumpobj 0x{cleanAddress}");
        if (DumpObjParser.IsFailedOutput(output))
        {
            return new DumpObjectResult { ErrorMessage = "dumpobj failed" };
        }
        return DumpObjParser.Parse(output);
    }
    
    /// <summary>
    /// Converts a ClrMD inspection result to DumpObjectResult format for compatibility.
    /// </summary>
    private static DumpObjectResult ConvertClrMdToDumpObjectResult(ClrMdObjectInspection clrMd)
    {
        var result = new DumpObjectResult
        {
            Success = true,
            Name = clrMd.Type ?? string.Empty,
            MethodTable = clrMd.MethodTable ?? string.Empty,
            Size = (int)clrMd.Size,
            IsArray = clrMd.IsArray,
            ArrayLength = clrMd.ArrayLength,
            StringValue = clrMd.IsString ? clrMd.Value?.ToString() : null
        };
        
        // Convert fields
        if (clrMd.Fields != null)
        {
            result.Fields = clrMd.Fields.Select(f => new DumpFieldInfo
            {
                Name = f.Name ?? string.Empty,
                Type = f.Type ?? string.Empty,
                Value = f.Value?.ToString() ?? f.NestedObject?.Address ?? "null",
                IsStatic = f.IsStatic
            }).ToList();
        }
        
        return result;
    }

    /// <summary>
    /// Clears the static inspection cache.
    /// Call this when closing a dump or starting a new analysis session.
    /// </summary>
    public static void ClearCache()
    {
        lock (s_cacheLock)
        {
            s_inspectionCache.Clear();
        }
    }

    /// <summary>
    /// Inspects an object at the given address and returns its JSON representation.
    /// </summary>
    /// <param name="manager">The debugger manager to execute commands.</param>
    /// <param name="address">The memory address of the object.</param>
    /// <param name="methodTable">Optional method table for value types (fallback if dumpobj fails).</param>
    /// <param name="maxDepth">Maximum recursion depth (default: 5).</param>
    /// <param name="maxArrayElements">Maximum array elements to show (default: 10).</param>
    /// <param name="maxStringLength">Maximum string length (default: 1024).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The inspected object, or null if inspection failed.</returns>
    public async Task<InspectedObject?> InspectAsync(
        IDebuggerManager manager,
        string address,
        string? methodTable = null,
        int maxDepth = DefaultMaxDepth,
        int maxArrayElements = DefaultMaxArrayElements,
        int maxStringLength = DefaultMaxStringLength,
        CancellationToken cancellationToken = default)
    {
        var normalizedAddress = PrimitiveResolver.NormalizeAddress(address);

        // Create cache key including parameters that affect output
        var cacheKey = $"{normalizedAddress}|{methodTable ?? ""}|{maxDepth}|{maxArrayElements}|{maxStringLength}";

        // Check static cache first for full inspection results
        lock (s_cacheLock)
        {
            if (s_inspectionCache.TryGetValue(cacheKey, out var cachedResult))
            {
                // Short-circuit repeated queries for the same object/limits
                _logger.LogDebug("[ObjectInspector] Cache HIT for {Address}", normalizedAddress);
                return cachedResult;
            }
        }

        // Clear internal type/enum caches at the start of each inspection session
        _typeNameCache.Clear();
        _enumNameCache.Clear();

        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = await InspectRecursiveAsync(
            manager,
            normalizedAddress,
            methodTable,
            maxDepth,
            maxArrayElements,
            maxStringLength,
            seenAddresses,
            normalizedAddress, // root address for [this] detection
            cancellationToken);

        // Cache successful results
        if (result != null && !result.Type.StartsWith(MarkerErrorPrefix))
        {
            lock (s_cacheLock)
            {
                s_inspectionCache[cacheKey] = result;
                _logger.LogDebug("[ObjectInspector] Cached result for {Address} (total cached: {Count})",
                    normalizedAddress, s_inspectionCache.Count);
            }
        }

        return result;
    }

    /// <summary>
    /// Inspects an object and returns it as a JSON string.
    /// </summary>
    public async Task<string> InspectToJsonAsync(
        IDebuggerManager manager,
        string address,
        string? methodTable = null,
        int maxDepth = DefaultMaxDepth,
        int maxArrayElements = DefaultMaxArrayElements,
        int maxStringLength = DefaultMaxStringLength,
        CancellationToken cancellationToken = default)
    {
        var result = await InspectAsync(manager, address, methodTable, maxDepth, maxArrayElements, maxStringLength, cancellationToken);

        if (result == null)
        {
            return JsonSerializer.Serialize(new { error = "Failed to inspect object" }, new JsonSerializerOptions { WriteIndented = true });
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    private async Task<InspectedObject?> InspectRecursiveAsync(
        IDebuggerManager manager,
        string address,
        string? methodTable,
        int depth,
        int maxArrayElements,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        // Check for null address
        if (PrimitiveResolver.IsNullAddress(address))
        {
            return null;
        }

        var normalizedAddress = PrimitiveResolver.NormalizeAddress(address);

        // Check depth limit
        if (depth <= 0)
        {
            // Stop recursion to avoid exploding output or infinite loops
            _logger.LogDebug("Max depth reached for address {Address}", normalizedAddress);
            return new InspectedObject
            {
                Address = normalizedAddress,
                Type = MarkerMaxDepth
            };
        }

        // Check for circular reference
        if (seenAddresses.Contains(normalizedAddress))
        {
            var marker = normalizedAddress.Equals(rootAddress, StringComparison.OrdinalIgnoreCase)
                ? MarkerThis
                : MarkerSeen;
            // Mark loops explicitly instead of recursing forever
            _logger.LogDebug("Circular reference detected for address {Address}: {Marker}", normalizedAddress, marker);
            return new InspectedObject
            {
                Address = normalizedAddress,
                Type = marker
            };
        }

        // Add to seen addresses
        seenAddresses.Add(normalizedAddress);

        try
        {
            // Try dumpobj first
            var dumpResult = await TryDumpObjectAsync(manager, normalizedAddress, cancellationToken);

            // If dumpobj failed and we have a method table, try dumpvc
            if (!dumpResult.Success && !string.IsNullOrEmpty(methodTable))
            {
                // Value types sometimes need dumpvc; try that before giving up
                _logger.LogDebug("dumpobj failed for {Address}, trying dumpvc with MT {MethodTable}", normalizedAddress, methodTable);
                dumpResult = await TryDumpValueTypeAsync(manager, methodTable, normalizedAddress, cancellationToken);
            }

            if (!dumpResult.Success)
            {
                // Return structured error instead of throwing so caller can render it
                _logger.LogWarning("Failed to dump object at {Address}: {Error}", normalizedAddress, dumpResult.ErrorMessage);
                return new InspectedObject
                {
                    Address = normalizedAddress,
                    Type = $"{MarkerErrorPrefix}{dumpResult.ErrorMessage ?? "unknown error"}]"
                };
            }

            // Resolve truncated type name using dumpmt if needed
            var typeName = dumpResult.Name;
            if (IsTypeTruncated(typeName) && !string.IsNullOrEmpty(dumpResult.MethodTable))
            {
                var fullTypeName = await ResolveFullTypeNameAsync(manager, dumpResult.MethodTable, cancellationToken);
                if (!string.IsNullOrEmpty(fullTypeName))
                {
                    // Prefer full names so inspectors/clients see complete generic types
                    typeName = fullTypeName;
                }
            }

            // Build the inspected object
            var inspected = new InspectedObject
            {
                Address = normalizedAddress,
                Type = typeName,
                MethodTable = dumpResult.MethodTable,
                IsValueType = dumpResult.IsValueType,
                Size = dumpResult.Size > 0 ? dumpResult.Size : null
            };

            // Handle delegates - add rich delegate info
            // Use resolved typeName for detection (handles truncated names)
            if (IsDelegateType(typeName))
            {
                // Include delegate target/method info for better diagnostics
                inspected.Delegate = await GetDelegateInfoAsync(manager, normalizedAddress, cancellationToken);
            }

            // Handle exceptions - add rich exception info
            if (IsExceptionType(typeName))
            {
                // Capture exception details to reduce extra !pe calls
                inspected.Exception = await GetExceptionInfoAsync(manager, normalizedAddress, cancellationToken);
            }

            // Handle tasks - add rich task info
            if (IsTaskType(typeName))
            {
                // Tasks need custom parsing to surface status/async state
                inspected.Task = await GetTaskInfoAsync(manager, normalizedAddress, dumpResult, cancellationToken);
            }

            // Handle System.Type - show meaningful type info instead of internal fields
            var isSystemType = IsSystemType(typeName);
            if (isSystemType)
            {
                inspected.TypeInfo = await GetTypeReflectionInfoAsync(manager, dumpResult, cancellationToken);
                // Skip fields for System.Type - they're not useful (m_handle, m_cache, etc.)
            }

            // Handle arrays
            if (dumpResult.IsArray)
            {
                inspected.Length = dumpResult.ArrayLength;
                
                // Skip inspecting elements for very large arrays to prevent crashes and hangs
                // Arrays with >10000 elements or >64KB size are too large to safely inspect
                const int MaxArrayElementsToInspect = 10000;
                const int MaxArraySizeBytes = 64 * 1024; // 64KB
                
                var arrayLength = dumpResult.ArrayLength ?? 0;
                var arraySize = dumpResult.Size;
                
                if (arrayLength > MaxArrayElementsToInspect || arraySize > MaxArraySizeBytes)
                {
                    _logger.LogDebug(
                        "Skipping element inspection for large array: {Type} with {Length} elements, {Size} bytes at {Address}",
                        typeName, arrayLength, arraySize, normalizedAddress);
                    
                    // Return summary without elements to avoid potential crashes
                    inspected.Fields = 
                    [
                        new()
                        {
                            Name = "[large array]",
                            Type = dumpResult.ArrayElementType ?? "unknown",
                            IsStatic = false,
                            Value = $"Array too large to inspect ({arrayLength:N0} elements, {arraySize:N0} bytes)"
                        }
                    ];
                }
                else
                {
                    inspected.Elements = await InspectArrayElementsAsync(
                        manager,
                        normalizedAddress,
                        dumpResult,
                        depth - 1,
                        maxArrayElements,
                        maxStringLength,
                        seenAddresses,
                        rootAddress,
                        cancellationToken);
                }
            }
            // Handle collections (List<T>, Dictionary<K,V>, etc.)
            else if (TryHandleCollection(
                manager,
                typeName,
                dumpResult,
                inspected,
                depth - 1,
                maxArrayElements,
                maxStringLength,
                seenAddresses,
                rootAddress,
                cancellationToken,
                out var collectionTask))
            {
                await collectionTask;
            }
            // Handle strings specially
            else if (typeName == "System.String")
            {
                // String value is in StringValue, but we need to handle truncation
                var stringValue = dumpResult.StringValue ?? "";
                if (stringValue.Length > maxStringLength)
                {
                    stringValue = $"{stringValue[..maxStringLength]}[truncated: {stringValue.Length} chars]";
                }
                // For strings, we return the string directly, not as fields
                inspected.Fields =
                [
                    new()
                    {
                        Name = "_value",
                        Type = "System.String",
                        IsStatic = false,
                        Value = stringValue
                    }
                ];
            }
            else if (!isSystemType)
            {
                // Inspect fields (skip for System.Type - typeInfo is more useful)
                inspected.Fields = await InspectFieldsAsync(
                    manager,
                    normalizedAddress,
                    dumpResult,
                    depth - 1,
                    maxArrayElements,
                    maxStringLength,
                    seenAddresses,
                    rootAddress,
                    cancellationToken);

                // Try to format special types like DateTime, TimeSpan, Guid for readability
                // Use resolved typeName for proper detection
                inspected.FormattedValue = TryFormatSpecialType(typeName, inspected.Fields);
            }

            return inspected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inspecting object at {Address}", normalizedAddress);
            return new InspectedObject
            {
                Address = normalizedAddress,
                Type = $"{MarkerErrorPrefix}{ex.Message}]"
            };
        }
    }

    private Task<DumpObjectResult> TryDumpObjectAsync(
        IDebuggerManager manager,
        string address,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ExecuteDumpObj(manager, address));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "dumpobj failed for {Address}", address);
            return Task.FromResult(new DumpObjectResult { ErrorMessage = ex.Message });
        }
    }

    private Task<DumpObjectResult> TryDumpValueTypeAsync(
        IDebuggerManager manager,
        string methodTable,
        string address,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = manager.ExecuteCommand($"dumpvc {methodTable} {address}");

            if (DumpVcParser.IsFailedOutput(output))
            {
                return Task.FromResult(new DumpObjectResult { ErrorMessage = "dumpvc failed" });
            }

            return Task.FromResult(DumpVcParser.Parse(output));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "dumpvc failed for MT {MethodTable} at {Address}", methodTable, address);
            return Task.FromResult(new DumpObjectResult { ErrorMessage = ex.Message });
        }
    }

    private async Task<List<InspectedField>> InspectFieldsAsync(
        IDebuggerManager manager,
        string parentAddress,
        DumpObjectResult dumpResult,
        int depth,
        int maxArrayElements,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        var fields = new List<InspectedField>();

        foreach (var field in dumpResult.Fields)
        {
            // Resolve truncated type names using dumpmt
            var fieldType = field.Type;
            if (IsTypeTruncated(fieldType) && !string.IsNullOrEmpty(field.MethodTable))
            {
                var fullType = await ResolveFullTypeNameAsync(manager, field.MethodTable, cancellationToken);
                if (!string.IsNullOrEmpty(fullType))
                {
                    // Prefer full names so clients see complete generic/namespace info
                    fieldType = fullType;
                }
            }

            var inspectedField = new InspectedField
            {
                Name = field.Name,
                Type = fieldType,
                IsStatic = field.IsStatic
            };

            try
            {
                inspectedField.Value = await ResolveFieldValueAsync(
                    manager,
                    parentAddress,
                    field,
                    fieldType, // Pass resolved type name
                    depth,
                    maxArrayElements,
                    maxStringLength,
                    seenAddresses,
                    rootAddress,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error resolving field {FieldName}", field.Name);
                // Preserve the error in-line so the client knows why this field failed
                inspectedField.Value = $"{MarkerErrorPrefix}{ex.Message}]";
            }

            fields.Add(inspectedField);
        }

        return fields;
    }

    private async Task<object?> ResolveFieldValueAsync(
        IDebuggerManager manager,
        string parentAddress,
        DumpFieldInfo field,
        string resolvedTypeName, // Resolved (non-truncated) type name
        int depth,
        int maxArrayElements,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        // Handle primitive types - use resolved type name
        if (PrimitiveResolver.IsPrimitiveType(resolvedTypeName))
        {
            return PrimitiveResolver.ResolvePrimitiveValue(resolvedTypeName, field.Value);
        }

        // Handle strings - use resolved type name
        if (resolvedTypeName == "System.String" || resolvedTypeName.EndsWith(".String"))
        {
            if (PrimitiveResolver.IsNullAddress(field.Value))
            {
                return "null"; // Show explicit "null" instead of omitting the field
            }

            // Dump the string object to get its value
            return await GetStringValueAsync(manager, field.Value, maxStringLength, cancellationToken);
        }

        // Handle reference types (VT=0)
        if (!field.IsValueType)
        {
            // Skip native pointer types that can't be inspected with dumpobj
            if (IsNativePointerType(resolvedTypeName))
            {
                return field.Value; // Return raw address value
            }

            if (PrimitiveResolver.IsNullAddress(field.Value))
            {
                return "null"; // Show explicit "null" instead of omitting the field
            }

            // Recurse into the reference
            var innerObj = await InspectRecursiveAsync(
                manager,
                field.Value,
                null, // No MT for reference types
                depth,
                maxArrayElements,
                maxStringLength,
                seenAddresses,
                rootAddress,
                cancellationToken);

            return innerObj;
        }

        // Handle embedded value types (VT=1)
        // For value types, we need to calculate the address based on offset
        // The value type is embedded at parentAddress + offset
        // We use dumpvc with the field's MT

        // For static value types, the Value field might be:
        // - A direct value for primitives (e.g., "1", "true")
        // - An address for complex value types (e.g., DateTime, TimeSpan, Guid)
        if (field.IsStatic)
        {
            // Check if it's a potential enum first
            if (PrimitiveResolver.IsPotentialEnumType(resolvedTypeName, true) && IsLikelyEnumValue(field.Value))
            {
                var enumName = await TryGetEnumNameAsync(manager, field.MethodTable, field.Value, cancellationToken);
                return PrimitiveResolver.FormatEnumValue(resolvedTypeName, field.Value, enumName);
            }

            // For complex value types (DateTime, TimeSpan, Guid, etc.) that need expansion,
            // try to dump them if the value looks like an address
            if (IsComplexValueType(resolvedTypeName) && LooksLikeAddress(field.Value))
            {
                try
                {
                    var staticAddr = PrimitiveResolver.NormalizeAddress(field.Value);
                    if (!PrimitiveResolver.IsNullAddress(field.Value) && !string.IsNullOrEmpty(staticAddr))
                    {
                        var innerObj = await InspectRecursiveAsync(
                            manager,
                            staticAddr,
                            field.MethodTable,
                            depth,
                            maxArrayElements,
                            maxStringLength,
                            seenAddresses,
                            rootAddress,
                            cancellationToken);

                        if (innerObj != null && !innerObj.Type.StartsWith("[error:"))
                        {
                            // Prefer expanded view for complex statics over raw value
                            return innerObj;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error expanding static value type {FieldName}", field.Name);
                }
            }

            // For simple primitives, resolve directly
            return PrimitiveResolver.ResolvePrimitiveValue(resolvedTypeName, field.Value);
        }

        // Instance embedded value type - try to recurse using dumpvc
        // For embedded value types, the Value field from dumpobj already contains
        // the address of the embedded value type (parent + offset is pre-calculated)
        try
        {
            // The Value field contains the address of the embedded struct
            // We can use it directly with dumpvc
            var embeddedAddrStr = PrimitiveResolver.NormalizeAddress(field.Value);

            // Skip if the address is null or invalid
            if (!PrimitiveResolver.IsNullAddress(field.Value) && !string.IsNullOrEmpty(embeddedAddrStr))
            {
                // Recurse with the field's method table
                var innerObj = await InspectRecursiveAsync(
                    manager,
                    embeddedAddrStr,
                    field.MethodTable, // Use field's MT for dumpvc fallback
                    depth,
                    maxArrayElements,
                    maxStringLength,
                    seenAddresses,
                    rootAddress,
                    cancellationToken);

                // If recursion succeeded and returned a valid object, use it
                if (innerObj != null && !innerObj.Type.StartsWith("[error:"))
                {
                    // Prefer expanded embedded struct over raw primitive for readability
                    return innerObj;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error inspecting embedded value type {FieldName}", field.Name);
        }

        // Fallback: check if this could be an enum (small numeric value) - use resolved type name
        if (PrimitiveResolver.IsPotentialEnumType(resolvedTypeName, true) && IsLikelyEnumValue(field.Value))
        {
            var enumName = await TryGetEnumNameAsync(manager, field.MethodTable, field.Value, cancellationToken);
            return PrimitiveResolver.FormatEnumValue(resolvedTypeName, field.Value, enumName);
        }

        // Final fallback: return the raw value from the output
        return PrimitiveResolver.ResolvePrimitiveValue(resolvedTypeName, field.Value);
    }

    /// <summary>
    /// Checks if a value looks like an enum value (small integer) vs an address.
    /// </summary>
    private static bool IsLikelyEnumValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        // If it parses as a small integer, it's likely an enum value
        if (long.TryParse(value, out var numValue))
        {
            // Enum values are typically small (less than 10000)
            // Addresses are typically large hex values
            return numValue >= -10000 && numValue <= 10000;
        }

        // If it looks like a hex address (many digits), it's not an enum
        if (value.Length >= 8 && value.All(c => char.IsAsciiHexDigit(c)))
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Checks if a type is a complex value type that needs expansion (DateTime, TimeSpan, Guid, etc.)
    /// </summary>
    private static bool IsComplexValueType(string typeName)
    {
        return typeName switch
        {
            "System.DateTime" or "DateTime" => true,
            "System.DateTimeOffset" or "DateTimeOffset" => true,
            "System.TimeSpan" or "TimeSpan" => true,
            "System.Guid" or "Guid" => true,
            "System.DateOnly" or "DateOnly" => true,
            "System.TimeOnly" or "TimeOnly" => true,
            "System.Decimal" or "Decimal" => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if a type is a native pointer type that cannot be inspected with dumpobj.
    /// </summary>
    private static bool IsNativePointerType(string typeName)
    {
        return typeName switch
        {
            "PTR" => true,                      // Native pointer (SOS marker)
            "VALUETYPE" => true,                // Generic value type marker
            "System.IntPtr" or "IntPtr" => true,
            "System.UIntPtr" or "UIntPtr" => true,
            "nint" or "nuint" => true,
            _ when typeName.EndsWith("*") => true,  // C-style pointers like "void*"
            _ => false
        };
    }

    /// <summary>
    /// Checks if a value looks like a memory address (hex string).
    /// </summary>
    private static bool LooksLikeAddress(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var clean = value;
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            clean = clean[2..];

        // Treat longer hex strings as addresses; short numerics are more likely enum/primitive values
        return clean.Length >= 8 && clean.All(c => char.IsAsciiHexDigit(c));
    }

    private async Task<object?> GetStringValueAsync(
        IDebuggerManager manager,
        string address,
        int maxLength,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ExecuteDumpObj(manager, address);

            if (result.Success && result.StringValue != null)
            {
                var value = result.StringValue;
                if (value.Length > maxLength)
                {
                    return $"{value[..maxLength]}[truncated: {value.Length} chars]";
                }
                return value;
            }

            return $"{MarkerErrorPrefix}could not read string]";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error reading string at {Address}", address);
            return $"{MarkerErrorPrefix}{ex.Message}]";
        }
    }

    /// <summary>
    /// Checks if a type name is truncated (contains "...").
    /// </summary>
    private static bool IsTypeTruncated(string typeName)
    {
        return !string.IsNullOrEmpty(typeName) && typeName.Contains("...");
    }

    /// <summary>
    /// Resolves the full type name using dumpmt command (with internal caching).
    /// </summary>
    private Task<string?> ResolveFullTypeNameAsync(
        IDebuggerManager manager,
        string methodTable,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check cache first
            var normalizedMt = methodTable.ToUpperInvariant();
            if (_typeNameCache.TryGetValue(normalizedMt, out var cachedName))
            {
                return Task.FromResult(cachedName);
            }

            // Use dumpmt to get the full type name
            var output = manager.ExecuteCommand($"dumpmt {methodTable}");

            // Parse the Name: line from dumpmt output
            // Format: Name:                Datadog.Trace.TracerManager
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                {
                    var name = line["Name:".Length..].Trim();
                    if (!string.IsNullOrEmpty(name) && !name.Contains("..."))
                    {
                        _typeNameCache[normalizedMt] = name;
                        return Task.FromResult<string?>(name);
                    }
                }
            }

            _typeNameCache[normalizedMt] = null;
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error resolving type name for MT {MethodTable}", methodTable);
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Tries to get the enum name for a given value using the method table (with internal caching).
    /// </summary>
    private Task<string?> TryGetEnumNameAsync(
        IDebuggerManager manager,
        string methodTable,
        string value,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Try to parse the value as a number
            if (!long.TryParse(value, out var numericValue))
            {
                return Task.FromResult<string?>(null);
            }

            // Check cache first (key is MT:value)
            var cacheKey = $"{methodTable.ToUpperInvariant()}:{numericValue}";
            if (_enumNameCache.TryGetValue(cacheKey, out var cachedName))
            {
                return Task.FromResult(cachedName);
            }

            // Use dumpmt to get enum field information
            // The output contains enum values in the format:
            // Field   Type     Offset  Value  Name
            var output = manager.ExecuteCommand($"dumpmt -md {methodTable}");

            // Look for enum values in the output
            // Enum fields are typically shown with their constant values
            var enumName = ParseEnumNameFromDumpMt(output, numericValue);

            if (string.IsNullOrEmpty(enumName))
            {
                // Try alternative: could use ClrMD or other methods
                // For now, fall back to checking if we can find literal fields
                enumName = TryParseEnumFromFields(manager, methodTable, numericValue);
            }

            _enumNameCache[cacheKey] = enumName;
            return Task.FromResult(enumName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting enum name for MT {MethodTable}, value {Value}", methodTable, value);
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Parses enum member names from dumpmt output.
    /// </summary>
    private static string? ParseEnumNameFromDumpMt(string output, long value)
    {
        // dumpmt -md output contains field information
        // For enums, look for literal/const fields that match our value
        // Format varies but typically includes field name and attributes

        // This is a simplified parser - real enum values might need more sophisticated parsing
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            // Look for lines that contain "Literal" or "static literal"
            // These indicate enum members
            if (line.Contains("literal", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("const", StringComparison.OrdinalIgnoreCase))
            {
                // Try to extract the field name
                // This is heuristic - actual format depends on debugger output
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    // The last part is often the field name
                    var fieldName = parts[^1];

                    // Check if this line contains our value
                    foreach (var part in parts)
                    {
                        if (long.TryParse(part, out var partValue) && partValue == value)
                        {
                            return fieldName;
                        }
                        // Try hex format
                        if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                            long.TryParse(part[2..], System.Globalization.NumberStyles.HexNumber, null, out var hexValue) &&
                            hexValue == value)
                        {
                            return fieldName;
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Tries to get enum name by examining the enum type's fields.
    /// </summary>
    private string? TryParseEnumFromFields(IDebuggerManager manager, string methodTable, long value)
    {
        try
        {
            // Use dumpclass or examine the type to find enum members
            // This is a fallback approach
            var output = manager.ExecuteCommand($"dumpmt {methodTable}");

            // Check if this is actually an enum by looking for "Parent Class" = System.Enum
            if (!output.Contains("System.Enum", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Try to find the module and token to get field info
            // For now, return null - this would need more sophisticated parsing
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<object?>?> InspectArrayElementsAsync(
        IDebuggerManager manager,
        string arrayAddress,
        DumpObjectResult dumpResult,
        int depth,
        int maxArrayElements,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        if (!dumpResult.ArrayLength.HasValue || dumpResult.ArrayLength.Value == 0)
        {
            return [];
        }

        var elements = new List<object?>();
        var elementCount = Math.Min(dumpResult.ArrayLength.Value, maxArrayElements);

        // Try ClrMD first for more reliable array element inspection
        if (_clrMdAnalyzer?.IsOpen == true)
        {
            try
            {
                var normalizedAddr = PrimitiveResolver.NormalizeAddress(arrayAddress);
                if (ulong.TryParse(normalizedAddr, System.Globalization.NumberStyles.HexNumber, null, out var addr))
                {
                    var clrMdResult = _clrMdAnalyzer.InspectObject(addr, methodTable: null, maxDepth: depth, maxArrayElements: maxArrayElements, maxStringLength: maxStringLength);
                    if (clrMdResult?.Elements != null)
                    {
                        // Convert ClrMD elements to InspectedObject format
                        foreach (var elem in clrMdResult.Elements)
                        {
                            if (elem is ClrMdObjectInspection clrMdObj)
                            {
                                elements.Add(ConvertClrMdToInspected(clrMdObj));
                            }
                            else
                            {
                                elements.Add(elem);
                            }
                        }
                        return elements;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ClrMD array inspection failed for {Address}, falling back to SOS", arrayAddress);
            }
        }

        // Fallback to SOS dumparray
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = manager.ExecuteCommand($"dumparray {arrayAddress}");

            // Parse array elements
            var matches = ArrayElementRegex().Matches(output);
            var index = 0;

            foreach (Match match in matches)
            {
                if (index >= elementCount)
                    break;

                var elementValue = match.Groups[1].Value.Trim();

                // For reference type arrays, elementValue is an address
                // For value type arrays, it might be the value directly
                if (dumpResult.ArrayElementType != null && PrimitiveResolver.IsPrimitiveType(dumpResult.ArrayElementType))
                {
                    elements.Add(PrimitiveResolver.ResolvePrimitiveValue(dumpResult.ArrayElementType, elementValue));
                }
                else if (PrimitiveResolver.IsNullAddress(elementValue))
                {
                    elements.Add(null);
                }
                else
                {
                    // Recurse into the element
                    var elementObj = await InspectRecursiveAsync(
                        manager,
                        elementValue,
                        dumpResult.ArrayElementMethodTable,
                        depth,
                        maxArrayElements,
                        maxStringLength,
                        seenAddresses,
                        rootAddress,
                        cancellationToken);
                    elements.Add(elementObj);
                }

                index++;
            }

            // If we have more elements than shown, add a marker
            if (dumpResult.ArrayLength.Value > maxArrayElements)
            {
                elements.Add($"[... {dumpResult.ArrayLength.Value - maxArrayElements} more elements]");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error inspecting array elements at {Address}", arrayAddress);
            elements.Add($"{MarkerErrorPrefix}{ex.Message}]");
        }

        return elements;
    }


    /// <summary>
    /// Attempts to handle the object as a collection and populate the inspected object.
    /// </summary>
    /// <returns>True if this is a recognized collection type, false otherwise.</returns>
    private bool TryHandleCollection(
        IDebuggerManager manager,
        string typeName,
        DumpObjectResult dumpResult,
        InspectedObject inspected,
        int depth,
        int maxArrayElements,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken,
        out Task collectionTask)
    {
        var collectionType = CollectionTypeDetector.Detect(typeName);

        if (collectionType == CollectionType.None || collectionType == CollectionType.Array)
        {
            collectionTask = Task.CompletedTask;
            return false;
        }

        // Mark as collection
        inspected.IsCollection = true;
        inspected.CollectionKind = collectionType.ToString();

        // Resolve fields and extract elements asynchronously
        collectionTask = ResolveCollectionFieldsAndExtractAsync(
            manager,
            typeName,
            inspected,
            collectionType,
            dumpResult.Fields,
            maxArrayElements,
            depth,
            maxStringLength,
            seenAddresses,
            rootAddress,
            cancellationToken);

        return true;
    }

    /// <summary>
    /// Resolves collection fields (with type name resolution) and extracts elements.
    /// </summary>
    private async Task ResolveCollectionFieldsAndExtractAsync(
        IDebuggerManager manager,
        string collectionTypeName,
        InspectedObject inspected,
        CollectionType collectionType,
        List<DumpFieldInfo> fields,
        int maxArrayElements,
        int depth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        // Resolve fields with type name resolution for truncated types
        inspected.Fields = await ResolveFieldsShallowAsync(manager, fields, cancellationToken);

        // Extract collection elements
        await ExtractCollectionElementsAsync(
            manager,
            collectionTypeName,
            inspected,
            collectionType,
            fields,
            maxArrayElements,
            depth,
            maxStringLength,
            seenAddresses,
            rootAddress,
            cancellationToken);
    }

    /// <summary>
    /// Extracts collection elements and populates the InspectedObject.
    /// </summary>
    private async Task ExtractCollectionElementsAsync(
        IDebuggerManager manager,
        string collectionTypeName,
        InspectedObject inspected,
        CollectionType collectionType,
        List<DumpFieldInfo> fields,
        int maxElements,
        int depth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        var inspector = new CollectionInspector(_logger, this);
        CollectionInspector.ExtractionResult? result = null;

        try
        {
            result = collectionType switch
            {
                CollectionType.List => await inspector.ExtractListElementsAsync(
                    manager, collectionTypeName, fields, maxElements, depth, DefaultMaxDepth,
                    maxStringLength, seenAddresses, rootAddress, cancellationToken),

                CollectionType.Stack => await inspector.ExtractStackElementsAsync(
                    manager, collectionTypeName, fields, maxElements, depth, DefaultMaxDepth,
                    maxStringLength, seenAddresses, rootAddress, cancellationToken),

                CollectionType.Queue => await inspector.ExtractQueueElementsAsync(
                    manager, collectionTypeName, fields, maxElements, depth, DefaultMaxDepth,
                    maxStringLength, seenAddresses, rootAddress, cancellationToken),

                CollectionType.HashSet => await inspector.ExtractHashSetElementsAsync(
                    manager, collectionTypeName, fields, maxElements, depth, DefaultMaxDepth,
                    maxStringLength, seenAddresses, rootAddress, cancellationToken),

                CollectionType.Dictionary or CollectionType.SortedDictionary or CollectionType.SortedList
                    => await inspector.ExtractDictionaryEntriesAsync(
                        manager, collectionTypeName, fields, maxElements, depth, DefaultMaxDepth,
                        maxStringLength, seenAddresses, rootAddress, cancellationToken),

                CollectionType.ConcurrentDictionary => await inspector.ExtractConcurrentDictionaryEntriesAsync(
                    manager, collectionTypeName, fields, maxElements, depth, DefaultMaxDepth,
                    maxStringLength, seenAddresses, rootAddress, cancellationToken),

                CollectionType.ImmutableArray => await inspector.ExtractImmutableArrayElementsAsync(
                    manager, collectionTypeName, fields, maxElements, depth, DefaultMaxDepth,
                    maxStringLength, seenAddresses, rootAddress, cancellationToken),

                CollectionType.ImmutableList => await inspector.ExtractImmutableListElementsAsync(
                    manager, collectionTypeName, fields, maxElements, depth, DefaultMaxDepth,
                    maxStringLength, seenAddresses, rootAddress, cancellationToken),

                CollectionType.ConcurrentQueue => await inspector.ExtractConcurrentQueueElementsAsync(
                    manager, collectionTypeName, fields, maxElements, depth, DefaultMaxDepth,
                    maxStringLength, seenAddresses, rootAddress, cancellationToken),

                CollectionType.ConcurrentStack => await inspector.ExtractConcurrentStackElementsAsync(
                    manager, collectionTypeName, fields, maxElements, depth, DefaultMaxDepth,
                    maxStringLength, seenAddresses, rootAddress, cancellationToken),

                CollectionType.ConcurrentBag => await inspector.ExtractConcurrentBagElementsAsync(
                    manager, collectionTypeName, fields, maxElements, depth, DefaultMaxDepth,
                    maxStringLength, seenAddresses, rootAddress, cancellationToken),

                CollectionType.ImmutableDictionary => await inspector.ExtractImmutableDictionaryEntriesAsync(
                    manager, collectionTypeName, fields, maxElements, depth, DefaultMaxDepth,
                    maxStringLength, seenAddresses, rootAddress, cancellationToken),

                CollectionType.ImmutableHashSet => await inspector.ExtractImmutableHashSetElementsAsync(
                    manager, collectionTypeName, fields, maxElements, depth, DefaultMaxDepth,
                    maxStringLength, seenAddresses, rootAddress, cancellationToken),

                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Collection extraction failed for {Type}", collectionTypeName);
            inspected.ExtractionError = $"Extraction failed: {ex.Message}";
        }

        // Populate InspectedObject from result
        if (result != null)
        {
            inspected.Count = result.Count;
            inspected.Capacity = result.Capacity > 0 ? result.Capacity : null;
            inspected.ElementsReturned = result.ElementsReturned;
            inspected.Truncated = result.Truncated;
            inspected.Elements = result.Elements;
            inspected.Entries = result.Entries;

            if (!string.IsNullOrEmpty(result.Error))
            {
                inspected.ExtractionError = result.Error;
            }
        }
    }

    /// <summary>
    /// Resolves fields WITHOUT recursion - used for collection internal fields.
    /// Per plan section 2.5: primitives are inlined; complex types (including strings) show as addresses.
    /// Note: Collection internal fields are typically arrays/primitives, not strings.
    /// String inlining happens in the elements array via ResolveElementAsync.
    /// </summary>
    private List<InspectedField> ResolveFieldsShallow(List<DumpFieldInfo> fields)
    {
        var result = new List<InspectedField>();

        foreach (var field in fields)
        {
            var inspectedField = new InspectedField
            {
                Name = field.Name,
                Type = field.Type,
                IsStatic = field.IsStatic
            };

            // Resolve value based on type
            if (PrimitiveResolver.IsPrimitiveType(field.Type))
            {
                // Inline primitive value
                inspectedField.Value = PrimitiveResolver.ResolvePrimitiveValue(field.Type, field.Value);
            }
            else if (PrimitiveResolver.IsNullAddress(field.Value))
            {
                // Null reference
                inspectedField.Value = null;
            }
            else
            {
                // Complex type (arrays, strings, objects) - show address only
                inspectedField.Value = NormalizeAddressForDisplay(field.Value);
            }

            result.Add(inspectedField);
        }

        return result;
    }

    /// <summary>
    /// Resolves fields WITHOUT recursion but WITH type name resolution for truncated types.
    /// Used for collection internal fields.
    /// </summary>
    private async Task<List<InspectedField>> ResolveFieldsShallowAsync(
        IDebuggerManager manager,
        List<DumpFieldInfo> fields,
        CancellationToken cancellationToken)
    {
        var result = new List<InspectedField>();

        foreach (var field in fields)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Resolve truncated type name if needed
            var fieldType = field.Type;
            if (IsTypeTruncated(fieldType) && !string.IsNullOrEmpty(field.MethodTable))
            {
                var fullType = await ResolveFullTypeNameAsync(manager, field.MethodTable, cancellationToken);
                if (!string.IsNullOrEmpty(fullType))
                {
                    fieldType = fullType;
                }
            }

            var inspectedField = new InspectedField
            {
                Name = field.Name,
                Type = fieldType,
                IsStatic = field.IsStatic
            };

            // Resolve value based on type
            if (PrimitiveResolver.IsPrimitiveType(fieldType))
            {
                // Inline primitive value
                inspectedField.Value = PrimitiveResolver.ResolvePrimitiveValue(fieldType, field.Value);
            }
            else if (PrimitiveResolver.IsNullAddress(field.Value))
            {
                // Null reference
                inspectedField.Value = null;
            }
            else
            {
                // Complex type (arrays, strings, objects) - show address only
                inspectedField.Value = NormalizeAddressForDisplay(field.Value);
            }

            result.Add(inspectedField);
        }

        return result;
    }

    /// <summary>
    /// Normalizes an address for display (ensures 0x prefix).
    /// </summary>
    private static string NormalizeAddressForDisplay(string address)
    {
        var clean = address.Trim();
        if (!clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return $"0x{clean}";
        return clean.ToLowerInvariant();
    }



    /// <summary>
    /// Checks if a type is a delegate type.
    /// </summary>
    private static bool IsDelegateType(string typeName)
    {
        // Check common delegate patterns
        // Note: We check for specific BCL delegates and types ending with "Delegate"
        // Avoid false positives like "ExceptionHandler" or "CallbackResult"
        return typeName.StartsWith("System.Action", StringComparison.OrdinalIgnoreCase) ||
               typeName.StartsWith("System.Func`", StringComparison.OrdinalIgnoreCase) ||
               typeName.Equals("System.Func", StringComparison.OrdinalIgnoreCase) ||
               typeName.StartsWith("System.Predicate", StringComparison.OrdinalIgnoreCase) ||
               typeName.StartsWith("System.Comparison", StringComparison.OrdinalIgnoreCase) ||
               typeName.StartsWith("System.Converter", StringComparison.OrdinalIgnoreCase) ||
               typeName.StartsWith("System.EventHandler", StringComparison.OrdinalIgnoreCase) ||
               typeName.StartsWith("System.AsyncCallback", StringComparison.OrdinalIgnoreCase) ||
               typeName.StartsWith("System.Threading.ThreadStart", StringComparison.OrdinalIgnoreCase) ||
               typeName.StartsWith("System.Threading.ParameterizedThreadStart", StringComparison.OrdinalIgnoreCase) ||
               typeName.StartsWith("System.Threading.WaitCallback", StringComparison.OrdinalIgnoreCase) ||
               typeName.StartsWith("System.Threading.TimerCallback", StringComparison.OrdinalIgnoreCase) ||
               typeName.EndsWith("Delegate", StringComparison.OrdinalIgnoreCase) ||
               typeName.EndsWith("EventHandler", StringComparison.OrdinalIgnoreCase) ||
               typeName.EndsWith("Callback", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets delegate-specific information using dumpdelegate.
    /// </summary>
    private async Task<DelegateInfo?> GetDelegateInfoAsync(
        IDebuggerManager manager,
        string address,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = manager.ExecuteCommand($"dumpdelegate {address}");

            if (string.IsNullOrWhiteSpace(output) ||
                output.Contains("Invalid", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var delegateInfo = ParseDumpDelegate(output);

            // If we have a method descriptor, get additional info via dumpmd
            if (!string.IsNullOrEmpty(delegateInfo?.MethodDesc))
            {
                await EnrichDelegateWithMethodInfoAsync(manager, delegateInfo, cancellationToken);
            }

            return delegateInfo;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting delegate info for {Address}", address);
            return null;
        }
    }

    /// <summary>
    /// Parses dumpdelegate output.
    /// Format: Target           Method           Name
    ///         0000f7158edf2428 0000f7558b784600 Namespace.Class.Method()
    /// </summary>
    [GeneratedRegex(@"^(?<target>[0-9a-fA-F]+)\s+(?<method>[0-9a-fA-F]+)\s+(?<name>.+)$", RegexOptions.Multiline)]
    private static partial Regex DumpDelegateRegex();

    private static DelegateInfo? ParseDumpDelegate(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            // Skip header line
            if (line.Contains("Target") && line.Contains("Method") && line.Contains("Name"))
                continue;

            var match = DumpDelegateRegex().Match(line.Trim());
            if (match.Success)
            {
                return new DelegateInfo
                {
                    Target = PrimitiveResolver.NormalizeAddress(match.Groups["target"].Value),
                    MethodDesc = PrimitiveResolver.NormalizeAddress(match.Groups["method"].Value),
                    MethodName = match.Groups["name"].Value.Trim()
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Enriches delegate info with method details from dumpmd.
    /// </summary>
    private async Task EnrichDelegateWithMethodInfoAsync(
        IDebuggerManager manager,
        DelegateInfo delegateInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = manager.ExecuteCommand($"dumpmd {delegateInfo.MethodDesc}");

            if (string.IsNullOrWhiteSpace(output) || output.Contains("failed", StringComparison.OrdinalIgnoreCase))
                return;

            // Parse Class
            var classMatch = Regex.Match(output, @"Class:\s+([0-9a-fA-Fx]+)", RegexOptions.IgnoreCase);
            if (classMatch.Success)
            {
                // Try to get class name from dumpclass or dumpmt
                var classAddr = classMatch.Groups[1].Value;
                var mtOutput = manager.ExecuteCommand($"dumpmt {classAddr}");
                var nameMatch = Regex.Match(mtOutput, @"Name:\s+(.+)", RegexOptions.IgnoreCase);
                if (nameMatch.Success)
                {
                    delegateInfo.ClassName = nameMatch.Groups[1].Value.Trim();
                }
            }

            // Parse IsJitted
            var jitMatch = Regex.Match(output, @"IsJitted:\s+(\w+)", RegexOptions.IgnoreCase);
            if (jitMatch.Success)
            {
                delegateInfo.IsJitted = jitMatch.Groups[1].Value.Equals("yes", StringComparison.OrdinalIgnoreCase);
            }

            // Parse Current CodeAddr
            var codeMatch = Regex.Match(output, @"Current CodeAddr:\s+([0-9a-fA-Fx]+)", RegexOptions.IgnoreCase);
            if (codeMatch.Success)
            {
                delegateInfo.CodeAddress = PrimitiveResolver.NormalizeAddress(codeMatch.Groups[1].Value);
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error enriching delegate info for MethodDesc {MD}", delegateInfo.MethodDesc);
        }
    }



    /// <summary>
    /// Checks if a type is an exception type.
    /// </summary>
    private static bool IsExceptionType(string typeName)
    {
        return typeName.EndsWith("Exception", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Exception`", StringComparison.OrdinalIgnoreCase) ||
               typeName.Equals("System.Exception", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets exception-specific information using pe (PrintException).
    /// </summary>
    private async Task<ExceptionInfo?> GetExceptionInfoAsync(
        IDebuggerManager manager,
        string address,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = manager.ExecuteCommand($"pe {address}");

            if (string.IsNullOrWhiteSpace(output) ||
                output.Contains("Invalid", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("not a valid", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return ParsePrintException(output);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting exception info for {Address}", address);
            return null;
        }
    }

    /// <summary>
    /// Parses pe (PrintException) output.
    /// </summary>
    private static ExceptionInfo? ParsePrintException(string output)
    {
        var info = new ExceptionInfo();
        var hasData = false;

        // Parse Message
        var messageMatch = Regex.Match(output, @"Message:\s+(.+)", RegexOptions.IgnoreCase);
        if (messageMatch.Success)
        {
            var msg = messageMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(msg) && !msg.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                info.Message = msg;
                hasData = true;
            }
        }

        // Parse HResult
        var hresultMatch = Regex.Match(output, @"HResult:\s+([0-9a-fA-Fx-]+)", RegexOptions.IgnoreCase);
        if (hresultMatch.Success)
        {
            var hrStr = hresultMatch.Groups[1].Value.Trim();
            if (hrStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(hrStr[2..], System.Globalization.NumberStyles.HexNumber, null, out var hr))
                {
                    info.HResult = hr;
                    hasData = true;
                }
            }
            else if (int.TryParse(hrStr, out var hr))
            {
                info.HResult = hr;
                hasData = true;
            }
        }

        // Parse InnerException
        var innerMatch = Regex.Match(output, @"InnerException:\s+([0-9a-fA-Fx]+)", RegexOptions.IgnoreCase);
        if (innerMatch.Success)
        {
            var inner = innerMatch.Groups[1].Value.Trim();
            if (!PrimitiveResolver.IsNullAddress(inner))
            {
                info.InnerException = PrimitiveResolver.NormalizeAddress(inner);
                hasData = true;
            }
        }

        // Parse StackTrace (can be multi-line)
        var stackMatch = Regex.Match(output, @"StackTrace \(generated\):\s*\n([\s\S]+?)(?=\n\n|\nStackTraceString:|\z)", RegexOptions.IgnoreCase);
        if (stackMatch.Success)
        {
            var stack = stackMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(stack) && !stack.Equals("<none>", StringComparison.OrdinalIgnoreCase))
            {
                info.StackTrace = stack;
                hasData = true;
            }
        }

        return hasData ? info : null;
    }



    /// <summary>
    /// Checks if a type is a Task type.
    /// </summary>
    private static bool IsTaskType(string typeName)
    {
        return typeName.StartsWith("System.Threading.Tasks.Task", StringComparison.OrdinalIgnoreCase) ||
               typeName.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Task`1", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Task`2", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("ValueTask`1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets task-specific information by parsing fields and state flags.
    /// </summary>
    private async Task<TaskInfo?> GetTaskInfoAsync(
        IDebuggerManager manager,
        string address,
        DumpObjectResult dumpResult,
        CancellationToken cancellationToken)
    {
        try
        {
            var taskInfo = new TaskInfo();
            var hasData = false;

            // Find m_stateFlags field to determine task status
            var stateFlagsField = dumpResult.Fields.FirstOrDefault(f =>
                f.Name.Equals("m_stateFlags", StringComparison.OrdinalIgnoreCase) ||
                f.Name.Equals("<m_stateFlags>k__BackingField", StringComparison.OrdinalIgnoreCase));

            if (stateFlagsField != null && int.TryParse(stateFlagsField.Value, out var stateFlags))
            {
                ParseTaskStateFlags(stateFlags, taskInfo);
                hasData = true;
            }

            // Find m_taskId field
            var taskIdField = dumpResult.Fields.FirstOrDefault(f =>
                f.Name.Equals("m_taskId", StringComparison.OrdinalIgnoreCase));

            if (taskIdField != null && int.TryParse(taskIdField.Value, out var taskId))
            {
                taskInfo.Id = taskId;
                hasData = true;
            }

            // Find m_result field (for Task<T>)
            var resultField = dumpResult.Fields.FirstOrDefault(f =>
                f.Name.Equals("m_result", StringComparison.OrdinalIgnoreCase));

            if (resultField != null && !PrimitiveResolver.IsNullAddress(resultField.Value))
            {
                taskInfo.ResultAddress = PrimitiveResolver.NormalizeAddress(resultField.Value);
                hasData = true;
            }

            // Find exception holder (m_contingentProperties -> m_exceptionsHolder)
            var contingentField = dumpResult.Fields.FirstOrDefault(f =>
                f.Name.Equals("m_contingentProperties", StringComparison.OrdinalIgnoreCase));

            if (contingentField != null && !PrimitiveResolver.IsNullAddress(contingentField.Value))
            {
                var exceptionAddr = await TryGetTaskExceptionAsync(manager, contingentField.Value, cancellationToken);
                if (exceptionAddr != null)
                {
                    taskInfo.ExceptionAddress = exceptionAddr;
                    hasData = true;
                }
            }

            // Try to find async state machine info using dumpasync
            var asyncInfo = await TryGetAsyncStateMachineInfoAsync(manager, address, cancellationToken);
            if (asyncInfo != null)
            {
                taskInfo.StateMachineType = asyncInfo.Value.Type;
                taskInfo.StateMachineState = asyncInfo.Value.State;
                hasData = true;
            }

            return hasData ? taskInfo : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting task info for {Address}", address);
            return null;
        }
    }

    /// <summary>
    /// Task state flags from .NET runtime.
    /// </summary>
    [Flags]
    private enum TaskStateFlags
    {
        Started = 0x10000,
        DelegateInvoked = 0x20000,
        Disposed = 0x40000,
        ExceptionObservedByParent = 0x80000,
        CancellationAcknowledged = 0x100000,
        Faulted = 0x200000,
        Canceled = 0x400000,
        WaitingOnChildren = 0x800000,
        RanToCompletion = 0x1000000,
        WaitingForActivation = 0x2000000,
        CompletionReserved = 0x4000000,
        WaitCompletionNotification = 0x10000000,
        ExecutionContextIsNull = 0x20000000,
        TaskScheduledWasFired = 0x40000000
    }

    /// <summary>
    /// Parses task state flags to determine status.
    /// </summary>
    private static void ParseTaskStateFlags(int stateFlags, TaskInfo taskInfo)
    {
        var flags = (TaskStateFlags)stateFlags;

        // Determine status string
        if ((flags & TaskStateFlags.Faulted) != 0)
        {
            taskInfo.Status = "Faulted";
            taskInfo.IsCompleted = true;
            taskInfo.IsFaulted = true;
        }
        else if ((flags & TaskStateFlags.Canceled) != 0)
        {
            taskInfo.Status = "Canceled";
            taskInfo.IsCompleted = true;
            taskInfo.IsCanceled = true;
        }
        else if ((flags & TaskStateFlags.RanToCompletion) != 0)
        {
            taskInfo.Status = "RanToCompletion";
            taskInfo.IsCompleted = true;
            taskInfo.IsCompletedSuccessfully = true;
        }
        else if ((flags & TaskStateFlags.WaitingOnChildren) != 0)
        {
            taskInfo.Status = "WaitingOnChildren";
        }
        else if ((flags & TaskStateFlags.WaitingForActivation) != 0)
        {
            taskInfo.Status = "WaitingForActivation";
        }
        else if ((flags & TaskStateFlags.Started) != 0)
        {
            taskInfo.Status = "Running";
        }
        else
        {
            taskInfo.Status = "Created";
        }
    }

    /// <summary>
    /// Tries to get the exception from a faulted task's contingent properties.
    /// </summary>
    private async Task<string?> TryGetTaskExceptionAsync(
        IDebuggerManager manager,
        string contingentPropsAddress,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ExecuteDumpObj(manager, contingentPropsAddress);

            if (!result.Success)
                return null;

            // Look for m_exceptionsHolder field
            var holderField = result.Fields?.FirstOrDefault(f => 
                f.Name.Equals("m_exceptionsHolder", StringComparison.OrdinalIgnoreCase));
            if (holderField == null || string.IsNullOrEmpty(holderField.Value))
                return null;

            var holderAddr = holderField.Value;
            if (PrimitiveResolver.IsNullAddress(holderAddr))
                return null;

            // Get the holder object to find the actual exception
            var holderResult = ExecuteDumpObj(manager, holderAddr);
            if (!holderResult.Success)
                return null;
                
            var exceptionField = holderResult.Fields?.FirstOrDefault(f => 
                f.Name.Equals("m_exception", StringComparison.OrdinalIgnoreCase) ||
                f.Name.Equals("m_exceptions", StringComparison.OrdinalIgnoreCase));
            if (exceptionField != null && !string.IsNullOrEmpty(exceptionField.Value) && 
                !PrimitiveResolver.IsNullAddress(exceptionField.Value))
            {
                return PrimitiveResolver.NormalizeAddress(exceptionField.Value);
            }

            await Task.CompletedTask;
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Tries to get async state machine information using dumpasync.
    /// </summary>
    private async Task<(string Type, int State)?> TryGetAsyncStateMachineInfoAsync(
        IDebuggerManager manager,
        string taskAddress,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // dumpasync -addr <address> shows async state machine for a specific task
            var output = manager.ExecuteCommand($"dumpasync -addr {taskAddress}");

            if (string.IsNullOrWhiteSpace(output) ||
                output.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("No async", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Parse state machine type and state
            // Format varies but typically includes state machine type and current state
            var typeMatch = Regex.Match(output, @"StateMachine:\s+(.+?)(?:\s|$)", RegexOptions.IgnoreCase);
            var stateMatch = Regex.Match(output, @"State:\s+(-?\d+)", RegexOptions.IgnoreCase);

            if (typeMatch.Success || stateMatch.Success)
            {
                var type = typeMatch.Success ? typeMatch.Groups[1].Value.Trim() : null;
                var state = stateMatch.Success && int.TryParse(stateMatch.Groups[1].Value, out var s) ? s : (int?)null;

                if (type != null || state.HasValue)
                {
                    return (type ?? "Unknown", state ?? 0);
                }
            }

            await Task.CompletedTask;
            return null;
        }
        catch
        {
            return null;
        }
    }



    /// <summary>
    /// Tries to format special types (DateTime, TimeSpan, Guid, etc.) to a human-readable string.
    /// </summary>
    private static string? TryFormatSpecialType(string typeName, List<InspectedField>? fields)
    {
        if (fields == null || fields.Count == 0)
            return null;

        try
        {
            return typeName switch
            {
                "System.DateTime" or "DateTime" => FormatDateTime(fields),
                "System.DateTimeOffset" or "DateTimeOffset" => FormatDateTimeOffset(fields),
                "System.TimeSpan" or "TimeSpan" => FormatTimeSpan(fields),
                "System.Guid" or "Guid" => FormatGuid(fields),
                "System.DateOnly" or "DateOnly" => FormatDateOnly(fields),
                "System.TimeOnly" or "TimeOnly" => FormatTimeOnly(fields),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Formats a DateTime from its _dateData field.
    /// </summary>
    private static string? FormatDateTime(List<InspectedField> fields)
    {
        // DateTime has a single _dateData field (UInt64)
        // Bits 62-63 are DateTimeKind, bits 0-61 are ticks
        var dateDataField = fields.FirstOrDefault(f =>
            f.Name.Equals("_dateData", StringComparison.OrdinalIgnoreCase));

        if (dateDataField?.Value == null)
            return null;

        if (!TryParseFieldAsUInt64(dateDataField.Value, out var dateData))
            return null;

        // Extract ticks (bits 0-61) and kind (bits 62-63)
        const ulong TicksMask = 0x3FFFFFFFFFFFFFFF;
        var ticks = (long)(dateData & TicksMask);
        var kind = (DateTimeKind)(dateData >> 62);

        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            return null;

        var dt = new DateTime(ticks, kind);
        return dt.ToString("O"); // ISO 8601 format
    }

    /// <summary>
    /// Formats a DateTimeOffset from its _dateTime and _offsetMinutes fields.
    /// </summary>
    private static string? FormatDateTimeOffset(List<InspectedField> fields)
    {
        var dateTimeField = fields.FirstOrDefault(f =>
            f.Name.Equals("_dateTime", StringComparison.OrdinalIgnoreCase));
        var offsetField = fields.FirstOrDefault(f =>
            f.Name.Equals("_offsetMinutes", StringComparison.OrdinalIgnoreCase));

        if (dateTimeField?.Value == null)
            return null;

        // _dateTime is a DateTime struct - need to get its ticks
        if (dateTimeField.Value is InspectedObject dtObj && dtObj.Fields != null)
        {
            var dtFormatted = FormatDateTime(dtObj.Fields);
            if (dtFormatted != null && offsetField?.Value != null)
            {
                if (TryParseFieldAsInt64(offsetField.Value, out var offsetMinutes))
                {
                    var offset = TimeSpan.FromMinutes(offsetMinutes);
                    return $"{dtFormatted} ({(offset >= TimeSpan.Zero ? "+" : "")}{offset:hh\\:mm})";
                }
            }
            return dtFormatted;
        }

        return null;
    }

    /// <summary>
    /// Formats a TimeSpan from its _ticks field.
    /// </summary>
    private static string? FormatTimeSpan(List<InspectedField> fields)
    {
        var ticksField = fields.FirstOrDefault(f =>
            f.Name.Equals("_ticks", StringComparison.OrdinalIgnoreCase));

        if (ticksField?.Value == null)
            return null;

        if (!TryParseFieldAsInt64(ticksField.Value, out var ticks))
            return null;

        var ts = new TimeSpan(ticks);

        // Use appropriate format based on duration
        if (ts.TotalDays >= 1)
            return ts.ToString(@"d\.hh\:mm\:ss\.fff");
        else if (ts.TotalHours >= 1)
            return ts.ToString(@"h\:mm\:ss\.fff");
        else if (ts.TotalMinutes >= 1)
            return ts.ToString(@"m\:ss\.fff");
        else
            return ts.ToString(@"s\.fff") + "s";
    }

    /// <summary>
    /// Formats a Guid from its fields (_a, _b, _c, _d, _e, _f, _g, _h, _i, _j, _k).
    /// </summary>
    private static string? FormatGuid(List<InspectedField> fields)
    {
        // Guid structure: int _a, short _b, short _c, byte _d through _k
        var aField = fields.FirstOrDefault(f => f.Name == "_a");
        var bField = fields.FirstOrDefault(f => f.Name == "_b");
        var cField = fields.FirstOrDefault(f => f.Name == "_c");
        var dField = fields.FirstOrDefault(f => f.Name == "_d");
        var eField = fields.FirstOrDefault(f => f.Name == "_e");
        var fField = fields.FirstOrDefault(f => f.Name == "_f");
        var gField = fields.FirstOrDefault(f => f.Name == "_g");
        var hField = fields.FirstOrDefault(f => f.Name == "_h");
        var iField = fields.FirstOrDefault(f => f.Name == "_i");
        var jField = fields.FirstOrDefault(f => f.Name == "_j");
        var kField = fields.FirstOrDefault(f => f.Name == "_k");

        if (aField?.Value == null || bField?.Value == null || cField?.Value == null ||
            dField?.Value == null || eField?.Value == null || fField?.Value == null ||
            gField?.Value == null || hField?.Value == null || iField?.Value == null ||
            jField?.Value == null || kField?.Value == null)
        {
            return null;
        }

        try
        {
            if (!TryParseFieldAsInt32(aField.Value, out var a)) return null;
            if (!TryParseFieldAsInt16(bField.Value, out var b)) return null;
            if (!TryParseFieldAsInt16(cField.Value, out var c)) return null;
            if (!TryParseFieldAsByte(dField.Value, out var d)) return null;
            if (!TryParseFieldAsByte(eField.Value, out var e)) return null;
            if (!TryParseFieldAsByte(fField.Value, out var f)) return null;
            if (!TryParseFieldAsByte(gField.Value, out var g)) return null;
            if (!TryParseFieldAsByte(hField.Value, out var h)) return null;
            if (!TryParseFieldAsByte(iField.Value, out var i)) return null;
            if (!TryParseFieldAsByte(jField.Value, out var j)) return null;
            if (!TryParseFieldAsByte(kField.Value, out var k)) return null;

            var guid = new Guid(a, b, c, d, e, f, g, h, i, j, k);
            return guid.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Formats a DateOnly from its _dayNumber field.
    /// </summary>
    private static string? FormatDateOnly(List<InspectedField> fields)
    {
        var dayNumberField = fields.FirstOrDefault(f =>
            f.Name.Equals("_dayNumber", StringComparison.OrdinalIgnoreCase));

        if (dayNumberField?.Value == null)
            return null;

        if (!TryParseFieldAsInt32(dayNumberField.Value, out var dayNumber))
            return null;

        // DateOnly stores day number since 0001-01-01
        var date = DateOnly.FromDayNumber(dayNumber);
        return date.ToString("O"); // ISO 8601 format
    }

    /// <summary>
    /// Formats a TimeOnly from its _ticks field.
    /// </summary>
    private static string? FormatTimeOnly(List<InspectedField> fields)
    {
        var ticksField = fields.FirstOrDefault(f =>
            f.Name.Equals("_ticks", StringComparison.OrdinalIgnoreCase));

        if (ticksField?.Value == null)
            return null;

        if (!TryParseFieldAsInt64(ticksField.Value, out var ticks))
            return null;

        var time = new TimeOnly(ticks);
        return time.ToString("O"); // ISO 8601 format
    }

    // Helper methods for parsing field values

    private static bool TryParseFieldAsUInt64(object? value, out ulong result)
    {
        result = 0;
        if (value == null) return false;

        var str = value.ToString();
        if (string.IsNullOrEmpty(str)) return false;

        // Try direct parse
        if (ulong.TryParse(str, out result)) return true;

        // Try hex parse
        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(str.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        }

        return false;
    }

    private static bool TryParseFieldAsInt64(object? value, out long result)
    {
        result = 0;
        if (value == null) return false;

        var str = value.ToString();
        if (string.IsNullOrEmpty(str)) return false;

        if (long.TryParse(str, out result)) return true;

        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(str.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        }

        return false;
    }

    private static bool TryParseFieldAsInt32(object? value, out int result)
    {
        result = 0;
        if (value == null) return false;

        var str = value.ToString();
        if (string.IsNullOrEmpty(str)) return false;

        if (int.TryParse(str, out result)) return true;

        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(str.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        }

        return false;
    }

    private static bool TryParseFieldAsInt16(object? value, out short result)
    {
        result = 0;
        if (value == null) return false;

        var str = value.ToString();
        if (string.IsNullOrEmpty(str)) return false;

        if (short.TryParse(str, out result)) return true;

        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return short.TryParse(str.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        }

        return false;
    }

    private static bool TryParseFieldAsByte(object? value, out byte result)
    {
        result = 0;
        if (value == null) return false;

        var str = value.ToString();
        if (string.IsNullOrEmpty(str)) return false;

        if (byte.TryParse(str, out result)) return true;

        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return byte.TryParse(str.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        }

        return false;
    }



    /// <summary>
    /// Checks if a type is System.Type or RuntimeType.
    /// </summary>
    private static bool IsSystemType(string typeName)
    {
        return typeName.Equals("System.Type", StringComparison.OrdinalIgnoreCase) ||
               typeName.Equals("System.RuntimeType", StringComparison.OrdinalIgnoreCase) ||
               typeName.EndsWith(".RuntimeType", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets type reflection information by examining the RuntimeType's method table.
    /// </summary>
    private async Task<TypeReflectionInfo?> GetTypeReflectionInfoAsync(
        IDebuggerManager manager,
        DumpObjectResult dumpResult,
        CancellationToken cancellationToken)
    {
        try
        {
            // Find the m_handle field which contains the RuntimeTypeHandle
            // RuntimeTypeHandle contains the method table pointer
            var handleField = dumpResult.Fields.FirstOrDefault(f =>
                f.Name.Equals("m_handle", StringComparison.OrdinalIgnoreCase) ||
                f.Name.Equals("_impl", StringComparison.OrdinalIgnoreCase));

            string? methodTable = null;

            if (handleField != null && !PrimitiveResolver.IsNullAddress(handleField.Value))
            {
                // The handle value might be the MT directly or a pointer to it
                methodTable = handleField.Value;

                // If the value is a RuntimeTypeHandle struct, we need to get m_type from it
                // Try dumpvc on the handle address to get the actual MT
                var handleOutput = manager.ExecuteCommand($"dumpvc {handleField.MethodTable} {handleField.Value}");
                var mtMatch = Regex.Match(handleOutput, @"m_type\s+([0-9a-fA-Fx]+)", RegexOptions.IgnoreCase);
                if (mtMatch.Success)
                {
                    methodTable = mtMatch.Groups[1].Value;
                }
            }

            if (string.IsNullOrEmpty(methodTable) || PrimitiveResolver.IsNullAddress(methodTable))
            {
                return null;
            }

            // Now use dumpmt on the method table to get the actual type info
            cancellationToken.ThrowIfCancellationRequested();
            var output = manager.ExecuteCommand($"dumpmt {methodTable}");

            if (string.IsNullOrWhiteSpace(output) || output.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return ParseDumpMtForTypeInfo(output, methodTable);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting type reflection info");
            return null;
        }
    }

    /// <summary>
    /// Parses dumpmt output to extract type information.
    /// </summary>
    private static TypeReflectionInfo? ParseDumpMtForTypeInfo(string output, string methodTable)
    {
        var info = new TypeReflectionInfo
        {
            MethodTable = PrimitiveResolver.NormalizeAddress(methodTable)
        };
        var hasData = false;

        // Parse Name
        var nameMatch = Regex.Match(output, @"Name:\s+(.+)", RegexOptions.IgnoreCase);
        if (nameMatch.Success)
        {
            var fullName = nameMatch.Groups[1].Value.Trim();
            info.FullName = fullName;
            hasData = true;

            // Extract namespace
            var lastDot = fullName.LastIndexOf('.');
            if (lastDot > 0)
            {
                // Handle generic types - find the class name start
                var genericStart = fullName.IndexOf('`');
                if (genericStart > 0 && genericStart < lastDot)
                {
                    lastDot = fullName.LastIndexOf('.', genericStart);
                }
                if (lastDot > 0)
                {
                    info.Namespace = fullName[..lastDot];
                }
            }

            // Detect type characteristics from name
            info.IsGeneric = fullName.Contains('`');
            info.IsArray = fullName.EndsWith("[]") || fullName.Contains("[,");
        }

        // Parse File (assembly path)
        var fileMatch = Regex.Match(output, @"File:\s+(.+)", RegexOptions.IgnoreCase);
        if (fileMatch.Success)
        {
            var filePath = fileMatch.Groups[1].Value.Trim();
            info.File = filePath;

            // Extract assembly name from path
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (!string.IsNullOrEmpty(fileName))
            {
                info.Assembly = fileName;
            }
            hasData = true;
        }

        // Parse BaseSize
        var baseSizeMatch = Regex.Match(output, @"BaseSize:\s+0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
        if (baseSizeMatch.Success && int.TryParse(baseSizeMatch.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var baseSize))
        {
            info.BaseSize = baseSize;
            hasData = true;
        }

        // Parse ComponentSize (non-zero means it's an array or string)
        var compSizeMatch = Regex.Match(output, @"ComponentSize:\s+0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
        if (compSizeMatch.Success && int.TryParse(compSizeMatch.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var compSize) && compSize > 0)
        {
            info.IsArray = true;
        }

        // Parse Number of Methods
        var methodsMatch = Regex.Match(output, @"Number of Methods:\s+(\d+)", RegexOptions.IgnoreCase);
        if (methodsMatch.Success && int.TryParse(methodsMatch.Groups[1].Value, out var methodCount))
        {
            info.MethodCount = methodCount;
            hasData = true;
        }

        // Parse Number of IFaces
        var ifacesMatch = Regex.Match(output, @"Number of IFaces.*?:\s+(\d+)", RegexOptions.IgnoreCase);
        if (ifacesMatch.Success && int.TryParse(ifacesMatch.Groups[1].Value, out var ifaceCount))
        {
            info.InterfaceCount = ifaceCount;
            hasData = true;
        }

        // Detect value type
        // If parent is System.ValueType or System.Enum, it's a value type
        if (output.Contains("System.ValueType", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("System.Enum", StringComparison.OrdinalIgnoreCase))
        {
            info.IsValueType = true;

            if (output.Contains("System.Enum", StringComparison.OrdinalIgnoreCase))
            {
                info.IsEnum = true;
            }
            hasData = true;
        }

        // Parse parent MethodTable to get base type
        var parentMatch = Regex.Match(output, @"Parent\s*(?:Class|MethodTable):\s+([0-9a-fA-Fx]+)", RegexOptions.IgnoreCase);
        if (parentMatch.Success && !PrimitiveResolver.IsNullAddress(parentMatch.Groups[1].Value))
        {
            // We could dumpmt the parent to get its name, but that's expensive
            // For now, just note that there's a parent
            hasData = true;
        }

        // Check for interface
        // If "Class Attributes" contains "interface" or the type name starts with I and is in Interface pattern
        if (output.Contains("Interface", StringComparison.OrdinalIgnoreCase) ||
            (info.FullName?.StartsWith("I") == true && info.FullName?.Length > 1 && char.IsUpper(info.FullName[1])))
        {
            // This is a heuristic - real interface detection would need class attributes
            var attrMatch = Regex.Match(output, @"Class Attributes:\s+([0-9a-fA-Fx]+)", RegexOptions.IgnoreCase);
            if (attrMatch.Success)
            {
                // Interface attribute flag is 0x20 (mdInterface)
                if (int.TryParse(attrMatch.Groups[1].Value.TrimStart('0', 'x', 'X'),
                    System.Globalization.NumberStyles.HexNumber, null, out var attrs))
                {
                    info.IsInterface = (attrs & 0x20) != 0;
                }
            }
        }

        return hasData ? info : null;
    }

    /// <summary>
    /// Converts a ClrMD object inspection to the ObjectInspector format.
    /// </summary>
    private static InspectedObject ConvertClrMdToInspected(ClrMdObjectInspection clrMdObj)
    {
        var result = new InspectedObject
        {
            Address = clrMdObj.Address ?? "",
            Type = clrMdObj.Type ?? "Unknown",
            MethodTable = clrMdObj.MethodTable ?? "",
            Size = (int?)clrMdObj.Size,
            Length = clrMdObj.ArrayLength
        };

        // Convert fields
        if (clrMdObj.Fields != null)
        {
            result.Fields = clrMdObj.Fields.Select(f => new InspectedField
            {
                Name = f.Name ?? "",
                Type = f.Type ?? "Unknown",
                IsStatic = f.IsStatic,
                Value = f.NestedObject != null 
                    ? ConvertClrMdToInspected(f.NestedObject) 
                    : (object?)f.Value
            }).ToList();
        }

        // Convert array elements
        if (clrMdObj.Elements != null)
        {
            result.Elements = clrMdObj.Elements.Select<object?, object?>(e =>
            {
                if (e is ClrMdObjectInspection nested)
                    return ConvertClrMdToInspected(nested);
                return e;
            }).ToList();
        }

        return result;
    }

}
