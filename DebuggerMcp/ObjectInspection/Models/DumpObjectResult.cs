namespace DebuggerMcp.ObjectInspection.Models;

/// <summary>
/// Represents the parsed result from dumpobj or dumpvc SOS command.
/// </summary>
public class DumpObjectResult
{
    /// <summary>
    /// Gets or sets the type name of the object.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the method table address.
    /// </summary>
    public string MethodTable { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the canonical method table address.
    /// </summary>
    public string? CanonicalMethodTable { get; set; }

    /// <summary>
    /// Gets or sets the size of the object in bytes.
    /// </summary>
    public int Size { get; set; }

    /// <summary>
    /// Gets or sets the assembly file path.
    /// </summary>
    public string? File { get; set; }

    /// <summary>
    /// Gets or sets whether this is a value type (from dumpvc) or reference type (from dumpobj).
    /// </summary>
    public bool IsValueType { get; set; }

    /// <summary>
    /// Gets or sets whether this is an array type.
    /// </summary>
    public bool IsArray { get; set; }

    /// <summary>
    /// Gets or sets the array length (if IsArray is true).
    /// </summary>
    public int? ArrayLength { get; set; }

    /// <summary>
    /// Gets or sets the array element type (if IsArray is true).
    /// </summary>
    public string? ArrayElementType { get; set; }

    /// <summary>
    /// Gets or sets the array element method table (if IsArray is true).
    /// </summary>
    public string? ArrayElementMethodTable { get; set; }

    /// <summary>
    /// Gets or sets the string value (if this is a System.String).
    /// </summary>
    public string? StringValue { get; set; }

    /// <summary>
    /// Gets or sets the fields of the object.
    /// </summary>
    public List<DumpFieldInfo> Fields { get; set; } = [];

    /// <summary>
    /// Gets or sets whether parsing was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the error message if parsing failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

