using System.Globalization;

namespace DebuggerMcp.ObjectInspection;

/// <summary>
/// Resolves primitive type values from debugger output.
/// </summary>
public static class PrimitiveResolver
{
    /// <summary>
    /// Set of primitive type names that can be resolved directly from the Value field.
    /// </summary>
    private static readonly HashSet<string> PrimitiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Boolean",
        "System.Byte",
        "System.SByte",
        "System.Int16",
        "System.UInt16",
        "System.Int32",
        "System.UInt32",
        "System.Int64",
        "System.UInt64",
        "System.Single",
        "System.Double",
        "System.Decimal",
        "System.Char",
        "System.IntPtr",
        "System.UIntPtr",
        "System.DateTime",
        "System.TimeSpan",
        "System.Guid",
        "Boolean",
        "Byte",
        "SByte",
        "Int16",
        "UInt16",
        "Int32",
        "UInt32",
        "Int64",
        "UInt64",
        "Single",
        "Double",
        "Decimal",
        "Char",
        "IntPtr",
        "UIntPtr",
        "DateTime",
        "TimeSpan",
        "Guid"
    };

    /// <summary>
    /// Checks if the given type is a primitive type that can be resolved directly.
    /// </summary>
    public static bool IsPrimitiveType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return false;

        // Handle truncated type names (e.g., "System.Bool..." -> might be System.Boolean)
        if (typeName.EndsWith("..."))
        {
            var prefix = typeName[..^3];
            return PrimitiveTypes.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        return PrimitiveTypes.Contains(typeName);
    }

    /// <summary>
    /// Resolves a primitive value from the raw string value.
    /// </summary>
    /// <param name="typeName">The type name.</param>
    /// <param name="rawValue">The raw value string from debugger output.</param>
    /// <returns>The resolved value as the appropriate type, or the raw string if resolution fails.</returns>
    public static object? ResolvePrimitiveValue(string typeName, string rawValue)
    {
        if (string.IsNullOrEmpty(rawValue))
            return null;

        // Normalize type name (remove System. prefix for matching)
        var normalizedType = typeName.Replace("System.", "");
        if (normalizedType.EndsWith("..."))
        {
            // Try to match truncated types
            normalizedType = MatchTruncatedType(normalizedType);
        }

        return normalizedType.ToLowerInvariant() switch
        {
            "boolean" or "bool" => ResolveBoolean(rawValue),
            "byte" => ResolveByte(rawValue),
            "sbyte" => ResolveSByte(rawValue),
            "int16" or "short" => ResolveInt16(rawValue),
            "uint16" or "ushort" => ResolveUInt16(rawValue),
            "int32" or "int" => ResolveInt32(rawValue),
            "uint32" or "uint" => ResolveUInt32(rawValue),
            "int64" or "long" => ResolveInt64(rawValue),
            "uint64" or "ulong" => ResolveUInt64(rawValue),
            "single" or "float" => ResolveSingle(rawValue),
            "double" => ResolveDouble(rawValue),
            "decimal" => ResolveDecimal(rawValue),
            "char" => ResolveChar(rawValue),
            "intptr" or "nint" => ResolveIntPtr(rawValue),
            "uintptr" or "nuint" => ResolveUIntPtr(rawValue),
            "datetime" => ResolveDateTime(rawValue),
            "timespan" => ResolveTimeSpan(rawValue),
            "guid" => ResolveGuid(rawValue),
            _ => rawValue
        };
    }

    private static string MatchTruncatedType(string truncated)
    {
        var prefix = truncated[..^3].ToLowerInvariant();

        if ("boolean".StartsWith(prefix)) return "Boolean";
        if ("byte".StartsWith(prefix)) return "Byte";
        if ("sbyte".StartsWith(prefix)) return "SByte";
        if ("int16".StartsWith(prefix)) return "Int16";
        if ("uint16".StartsWith(prefix)) return "UInt16";
        if ("int32".StartsWith(prefix)) return "Int32";
        if ("uint32".StartsWith(prefix)) return "UInt32";
        if ("int64".StartsWith(prefix)) return "Int64";
        if ("uint64".StartsWith(prefix)) return "UInt64";
        if ("single".StartsWith(prefix)) return "Single";
        if ("double".StartsWith(prefix)) return "Double";
        if ("decimal".StartsWith(prefix)) return "Decimal";
        if ("char".StartsWith(prefix)) return "Char";
        if ("intptr".StartsWith(prefix)) return "IntPtr";
        if ("uintptr".StartsWith(prefix)) return "UIntPtr";
        if ("datetime".StartsWith(prefix)) return "DateTime";
        if ("timespan".StartsWith(prefix)) return "TimeSpan";
        if ("guid".StartsWith(prefix)) return "Guid";

        return truncated;
    }

    private static object ResolveBoolean(string rawValue)
    {
        // Boolean in debugger output is typically 0 or 1
        if (rawValue == "1" || rawValue.Equals("true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (rawValue == "0" || rawValue.Equals("false", StringComparison.OrdinalIgnoreCase))
            return false;
        return rawValue;
    }

    private static object ResolveByte(string rawValue)
    {
        if (byte.TryParse(rawValue, out var value))
            return value;
        if (byte.TryParse(rawValue, NumberStyles.HexNumber, null, out value))
            return value;
        return rawValue;
    }

    private static object ResolveSByte(string rawValue)
    {
        if (sbyte.TryParse(rawValue, out var value))
            return value;
        return rawValue;
    }

    private static object ResolveInt16(string rawValue)
    {
        if (short.TryParse(rawValue, out var value))
            return value;
        if (short.TryParse(rawValue, NumberStyles.HexNumber, null, out value))
            return value;
        return rawValue;
    }

    private static object ResolveUInt16(string rawValue)
    {
        if (ushort.TryParse(rawValue, out var value))
            return value;
        if (ushort.TryParse(rawValue, NumberStyles.HexNumber, null, out value))
            return value;
        return rawValue;
    }

    private static object ResolveInt32(string rawValue)
    {
        if (int.TryParse(rawValue, out var value))
            return value;
        if (int.TryParse(rawValue, NumberStyles.HexNumber, null, out value))
            return value;
        return rawValue;
    }

    private static object ResolveUInt32(string rawValue)
    {
        if (uint.TryParse(rawValue, out var value))
            return value;
        if (uint.TryParse(rawValue, NumberStyles.HexNumber, null, out value))
            return value;
        return rawValue;
    }

    private static object ResolveInt64(string rawValue)
    {
        if (long.TryParse(rawValue, out var value))
            return value;
        if (long.TryParse(rawValue, NumberStyles.HexNumber, null, out value))
            return value;
        return rawValue;
    }

    private static object ResolveUInt64(string rawValue)
    {
        if (ulong.TryParse(rawValue, out var value))
            return value;
        if (ulong.TryParse(rawValue, NumberStyles.HexNumber, null, out value))
            return value;
        return rawValue;
    }

    private static object ResolveSingle(string rawValue)
    {
        if (float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        return rawValue;
    }

    private static object ResolveDouble(string rawValue)
    {
        if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        return rawValue;
    }

    private static object ResolveDecimal(string rawValue)
    {
        if (decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return value;
        return rawValue;
    }

    private static object ResolveChar(string rawValue)
    {
        // Char might be represented as a number or the character itself
        if (rawValue.Length == 1)
            return rawValue[0].ToString();
        if (int.TryParse(rawValue, out var intValue) && intValue >= 0 && intValue <= char.MaxValue)
            return ((char)intValue).ToString();
        return rawValue;
    }

    private static object ResolveIntPtr(string rawValue)
    {
        // IntPtr is typically shown as hex address
        return $"0x{rawValue.TrimStart('0').PadLeft(1, '0')}";
    }

    private static object ResolveUIntPtr(string rawValue)
    {
        // UIntPtr is typically shown as hex address
        return $"0x{rawValue.TrimStart('0').PadLeft(1, '0')}";
    }

    private static object ResolveDateTime(string rawValue)
    {
        // DateTime in debugger is typically shown as ticks (Int64)
        // Try to parse as ticks first
        if (long.TryParse(rawValue, out var ticks) && ticks > 0)
        {
            try
            {
                var dt = new DateTime(ticks);
                return dt.ToString("o"); // ISO 8601 format
            }
            catch
            {
                // Invalid ticks value
            }
        }

        // Try to parse as a date string
        if (DateTime.TryParse(rawValue, out var parsed))
        {
            return parsed.ToString("o");
        }

        return rawValue;
    }

    private static object ResolveTimeSpan(string rawValue)
    {
        // TimeSpan in debugger is typically shown as ticks (Int64)
        if (long.TryParse(rawValue, out var ticks))
        {
            try
            {
                var ts = new TimeSpan(ticks);
                return ts.ToString();
            }
            catch
            {
                // Invalid ticks value
            }
        }

        // Try to parse as a timespan string
        if (TimeSpan.TryParse(rawValue, out var parsed))
        {
            return parsed.ToString();
        }

        return rawValue;
    }

    private static object ResolveGuid(string rawValue)
    {
        // Guid might be shown as hex bytes or as a formatted string
        if (Guid.TryParse(rawValue, out var guid))
        {
            return guid.ToString();
        }

        // Try to parse as hex without dashes
        if (rawValue.Length == 32 && rawValue.All(c => char.IsAsciiHexDigit(c)))
        {
            try
            {
                var guidBytes = Convert.FromHexString(rawValue);
                return new Guid(guidBytes).ToString();
            }
            catch
            {
                // Invalid hex
            }
        }

        return rawValue;
    }

    /// <summary>
    /// Checks if the given type name looks like it could be an enum.
    /// Enums are value types that are not standard primitives.
    /// </summary>
    public static bool IsPotentialEnumType(string typeName, bool isValueType)
    {
        if (string.IsNullOrEmpty(typeName) || !isValueType)
            return false;

        // If it's a known primitive, it's not an enum
        if (IsPrimitiveType(typeName))
            return false;

        // System.Enum is the base type
        if (typeName == "System.Enum" || typeName == "Enum")
            return true;

        // Common patterns for enum types:
        // - Not a System.* primitive type
        // - Is a value type
        // - Usually has a short name or namespace.TypeName pattern

        // Exclude known non-enum value types
        var nonEnumValueTypes = new[]
        {
            "System.Nullable",
            "System.ValueTuple",
            "System.Span",
            "System.ReadOnlySpan",
            "System.Memory",
            "System.ReadOnlyMemory",
            "System.Range",
            "System.Index",
            "System.ArraySegment",
            "System.ValueTask"
        };

        foreach (var nonEnum in nonEnumValueTypes)
        {
            if (typeName.StartsWith(nonEnum, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Formats an enum value with its numeric value.
    /// </summary>
    public static object FormatEnumValue(string typeName, string rawValue, string? enumName = null)
    {
        // If we have the enum name, format as "Name (N)"
        if (!string.IsNullOrEmpty(enumName))
        {
            return $"{enumName} ({rawValue})";
        }

        // Try to parse the value as a number
        if (long.TryParse(rawValue, out var numValue))
        {
            // Return as a dictionary with type info for better JSON representation
            return new Dictionary<string, object>
            {
                ["_value"] = numValue,
                ["_type"] = typeName
            };
        }

        return rawValue;
    }

    /// <summary>
    /// Checks if the given address represents a null reference.
    /// </summary>
    public static bool IsNullAddress(string address)
    {
        if (string.IsNullOrEmpty(address))
            return true;

        // Handle "null" and "(null)" strings
        if (address.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            address.Equals("(null)", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Remove 0x prefix and check if remaining is all zeros
        var normalized = address.TrimStart();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        // If empty or all zeros, it's null
        normalized = normalized.TrimStart('0');
        return string.IsNullOrEmpty(normalized);
    }

    /// <summary>
    /// Normalizes an address string (removes 0x prefix, leading zeros, and type suffix).
    /// Handles formats like: "0x1234", "1234", "1234 (System.String)", "0x1234 (TypeName)"
    /// </summary>
    public static string NormalizeAddress(string address)
    {
        if (string.IsNullOrEmpty(address))
            return address;

        var clean = address.TrimStart();
        
        // Remove any existing 0x prefix
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            clean = clean[2..];

        // Remove type suffix like " (System.String)" - stop at first space or parenthesis
        var spaceIdx = clean.IndexOf(' ');
        if (spaceIdx > 0)
            clean = clean[..spaceIdx];
        
        var parenIdx = clean.IndexOf('(');
        if (parenIdx > 0)
            clean = clean[..parenIdx];

        // Remove leading zeros but keep at least one digit
        clean = clean.TrimStart('0');
        if (string.IsNullOrEmpty(clean))
            clean = "0";

        return clean;
    }
}

