namespace DebuggerMcp.ObjectInspection.Models;

/// <summary>
/// Represents a field from dumpobj or dumpvc output.
/// </summary>
public class DumpFieldInfo
{
    /// <summary>
    /// Gets or sets the method table of the field's type.
    /// </summary>
    public string MethodTable { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the field token.
    /// </summary>
    public string FieldToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the offset of the field within the object.
    /// </summary>
    public int Offset { get; set; }

    /// <summary>
    /// Gets or sets the type name of the field.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this field is a value type (VT=1) or reference type (VT=0).
    /// </summary>
    public bool IsValueType { get; set; }

    /// <summary>
    /// Gets or sets whether this is a static field.
    /// </summary>
    public bool IsStatic { get; set; }

    /// <summary>
    /// Gets or sets the raw value from the output.
    /// For reference types, this is the address.
    /// For value types, this is the value or address depending on the type.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the field name.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}

