using System.Text.RegularExpressions;

namespace DebuggerMcp.ObjectInspection;

/// <summary>
/// Detects collection types from type names and provides helper methods for collection inspection.
/// </summary>
public static partial class CollectionTypeDetector
{
    /// <summary>
    /// Detects the collection type from a full type name.
    /// </summary>
    /// <param name="typeName">The full type name (e.g., "System.Collections.Generic.List`1[[System.String]]")</param>
    /// <returns>The detected collection type, or None if not a collection.</returns>
    public static CollectionType Detect(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return CollectionType.None;

        // Arrays (highest priority - check first)
        if (typeName.EndsWith("[]") || typeName.Contains("[,"))
            return CollectionType.Array;

        // Extract base type name (before generic parameters)
        var baseTypeName = GetBaseTypeName(typeName);

        return baseTypeName switch
        {
            // Tier 1
            "System.Collections.Generic.List`1" => CollectionType.List,
            "System.Collections.Generic.Stack`1" => CollectionType.Stack,
            "System.Collections.Generic.Queue`1" => CollectionType.Queue,
            "System.Collections.Generic.HashSet`1" => CollectionType.HashSet,

            // Tier 2
            "System.Collections.Generic.Dictionary`2" => CollectionType.Dictionary,
            "System.Collections.Generic.SortedDictionary`2" => CollectionType.SortedDictionary,
            "System.Collections.Generic.SortedList`2" => CollectionType.SortedList,

            // Tier 3
            "System.Collections.Concurrent.ConcurrentDictionary`2" => CollectionType.ConcurrentDictionary,
            "System.Collections.Concurrent.ConcurrentQueue`1" => CollectionType.ConcurrentQueue,
            "System.Collections.Concurrent.ConcurrentStack`1" => CollectionType.ConcurrentStack,
            "System.Collections.Concurrent.ConcurrentBag`1" => CollectionType.ConcurrentBag,

            // Tier 4
            "System.Collections.Immutable.ImmutableArray`1" => CollectionType.ImmutableArray,
            "System.Collections.Immutable.ImmutableList`1" => CollectionType.ImmutableList,
            "System.Collections.Immutable.ImmutableDictionary`2" => CollectionType.ImmutableDictionary,
            "System.Collections.Immutable.ImmutableHashSet`1" => CollectionType.ImmutableHashSet,

            _ => CollectionType.None
        };
    }

    /// <summary>
    /// Extracts the base type name without generic parameters.
    /// "System.Collections.Generic.List`1[[System.String]]" → "System.Collections.Generic.List`1"
    /// </summary>
    private static string GetBaseTypeName(string typeName)
    {
        var bracketIndex = typeName.IndexOf('[');
        if (bracketIndex > 0)
            return typeName[..bracketIndex];
        return typeName;
    }

    /// <summary>
    /// Checks if a collection type is a key-value collection.
    /// </summary>
    public static bool IsKeyValueCollection(CollectionType type)
    {
        return type is CollectionType.Dictionary
            or CollectionType.SortedDictionary
            or CollectionType.SortedList
            or CollectionType.ConcurrentDictionary
            or CollectionType.ImmutableDictionary;
    }

    /// <summary>
    /// Extracts the element type from a generic collection type name.
    /// </summary>
    /// <param name="typeName">Full type name like "System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]]"</param>
    /// <returns>The element type name, or null if not extractable.</returns>
    /// <example>
    /// "List`1[[System.Int32]]" → "System.Int32"
    /// "List`1[[MyApp.User, MyApp]]" → "MyApp.User"
    /// </example>
    public static string? ExtractElementType(string typeName)
    {
        // Find the first [[ which starts the generic parameter
        var startIndex = typeName.IndexOf("[[", StringComparison.Ordinal);
        if (startIndex < 0)
            return null;

        startIndex += 2; // Skip [[

        // Find the end - either ]], or , (for assembly qualified names)
        var endIndex = typeName.IndexOf(',', startIndex);
        var bracketEnd = typeName.IndexOf("]]", startIndex, StringComparison.Ordinal);

        if (endIndex < 0 || (bracketEnd >= 0 && bracketEnd < endIndex))
            endIndex = bracketEnd;

        if (endIndex < 0)
            return null;

        return typeName[startIndex..endIndex].Trim();
    }

    /// <summary>
    /// Extracts key and value types from a dictionary type name.
    /// </summary>
    /// <param name="typeName">Full type name like "Dictionary`2[[System.String],[System.Int32]]"</param>
    /// <returns>Tuple of (keyType, valueType) or null if not extractable.</returns>
    public static (string KeyType, string ValueType)? ExtractKeyValueTypes(string typeName)
    {
        // Pattern: Dictionary`2[[KeyType, Assembly],[ValueType, Assembly]]
        var match = KeyValueTypesRegex().Match(typeName);
        if (!match.Success)
            return null;

        return (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim());
    }

    [GeneratedRegex(@"\[\[([^\],]+)(?:,[^\]]+)?\],\s*\[([^\],]+)(?:,[^\]]+)?\]\]")]
    private static partial Regex KeyValueTypesRegex();

    /// <summary>
    /// Checks if a type name represents a primitive or inlineable type.
    /// </summary>
    public static bool IsInlineableType(string typeName)
    {
        return typeName switch
        {
            // Primitives
            "System.Boolean" or "Boolean" or "bool" => true,
            "System.Byte" or "Byte" or "byte" => true,
            "System.SByte" or "SByte" or "sbyte" => true,
            "System.Int16" or "Int16" or "short" => true,
            "System.UInt16" or "UInt16" or "ushort" => true,
            "System.Int32" or "Int32" or "int" => true,
            "System.UInt32" or "UInt32" or "uint" => true,
            "System.Int64" or "Int64" or "long" => true,
            "System.UInt64" or "UInt64" or "ulong" => true,
            "System.Single" or "Single" or "float" => true,
            "System.Double" or "Double" or "double" => true,
            "System.Decimal" or "Decimal" or "decimal" => true,
            "System.Char" or "Char" or "char" => true,
            "System.String" or "String" or "string" => true,
            "System.IntPtr" or "IntPtr" or "nint" => true,
            "System.UIntPtr" or "UIntPtr" or "nuint" => true,
            _ => false
        };
    }

    /// <summary>
    /// Gets the MethodTable for array elements.
    /// </summary>
    /// <param name="manager">Debugger manager.</param>
    /// <param name="arrayAddress">Address of the array.</param>
    /// <returns>Element MethodTable address, or null if not found.</returns>
    public static string? GetArrayElementMethodTable(IDebuggerManager manager, string arrayAddress)
    {
        // First, get the array's MT
        var dumpObjOutput = manager.ExecuteCommand($"dumpobj {arrayAddress}");
        var mtMatch = Regex.Match(dumpObjOutput, @"MethodTable:\s+([0-9a-fA-Fx]+)");
        if (!mtMatch.Success)
            return null;

        var arrayMt = mtMatch.Groups[1].Value;

        // Now dump the MT to get component info
        var dumpMtOutput = manager.ExecuteCommand($"dumpmt {arrayMt}");

        // Look for "Element Methodtable" or "ComponentMethodTable" or "Component Type"
        var elementMtMatch = Regex.Match(dumpMtOutput,
            @"(?:Element Methodtable|ComponentMethodTable|Component Type|Element Type):\s+([0-9a-fA-Fx]+)",
            RegexOptions.IgnoreCase);

        return elementMtMatch.Success ? elementMtMatch.Groups[1].Value : null;
    }

    /// <summary>
    /// Calculates the address of an array element for value type arrays.
    /// </summary>
    /// <param name="baseAddress">Base address of the array.</param>
    /// <param name="index">Element index.</param>
    /// <param name="elementSize">Size of each element in bytes.</param>
    /// <param name="pointerSize">Pointer size (4 for x86, 8 for x64) to determine header size.</param>
    /// <returns>Address of the element.</returns>
    /// <remarks>
    /// Array header layout:
    /// - x64 (8-byte pointers): 16 bytes header (8 MT + 4 length + 4 padding)
    /// - x86 (4-byte pointers): 8 bytes header (4 MT + 4 length)
    /// </remarks>
    public static string CalculateArrayElementAddress(string baseAddress, int index, int elementSize, int pointerSize)
    {
        // Determine header size based on platform
        var headerSize = pointerSize == 8 ? 16 : 8;

        // Parse base address
        var addrStr = baseAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? baseAddress[2..]
            : baseAddress;

        var addr = Convert.ToUInt64(addrStr, 16);

        // Calculate: baseAddr + headerSize + (index * elementSize)
        var elementAddr = addr + (ulong)headerSize + (ulong)(index * elementSize);

        return $"0x{elementAddr:x}";
    }

    /// <summary>
    /// Gets the pointer size from the debugger manager.
    /// </summary>
    public static int GetPointerSize(IDebuggerManager manager)
    {
        // Try to get from manager property if available
        try
        {
            // Most managers expose this via property
            var pointerSizeProperty = manager.GetType().GetProperty("PointerSize");
            if (pointerSizeProperty != null)
            {
                var value = pointerSizeProperty.GetValue(manager);
                if (value is int intValue)
                    return intValue;
                if (value is long longValue)
                    return (int)longValue;
            }

            // Fallback: execute a command to detect (e.g., check address width)
            var output = manager.ExecuteCommand("!eeversion");
            // If addresses are 8 chars, it's x86; 16 chars is x64
            var addrMatch = Regex.Match(output, @"0x([0-9a-fA-F]+)");
            if (addrMatch.Success)
            {
                return addrMatch.Groups[1].Value.Length > 8 ? 8 : 4;
            }
        }
        catch
        {
            // Ignore detection errors
        }

        // Default to x64
        return 8;
    }

    /// <summary>
    /// Gets the size of a type from dumpmt output.
    /// </summary>
    public static int GetTypeSize(IDebuggerManager manager, string methodTable)
    {
        var output = manager.ExecuteCommand($"dumpmt {methodTable}");

        // Try BaseSize first (for reference types)
        var sizeMatch = Regex.Match(output, @"BaseSize:\s+0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
        if (sizeMatch.Success && int.TryParse(sizeMatch.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var size))
        {
            return size;
        }

        // Try ComponentSize (for value type arrays)
        sizeMatch = Regex.Match(output, @"ComponentSize:\s+0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
        if (sizeMatch.Success && int.TryParse(sizeMatch.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out size))
        {
            return size;
        }

        return -1;
    }
}

