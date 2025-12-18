#nullable enable

namespace DebuggerMcp.Analysis;

/// <summary>
/// Provides managed object inspection capabilities (e.g., via ClrMD) for dump analysis.
/// </summary>
public interface IManagedObjectInspector
{
    /// <summary>
    /// Gets a value indicating whether inspection is available for the current dump.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Inspects an object/value at the specified address.
    /// </summary>
    /// <param name="address">Object/value address.</param>
    /// <param name="methodTable">Optional method table for value types.</param>
    /// <param name="maxDepth">Maximum recursion depth.</param>
    /// <param name="maxArrayElements">Maximum array elements to include.</param>
    /// <param name="maxStringLength">Maximum string length before truncation.</param>
    /// <returns>Inspection result or null when inspection is unavailable.</returns>
    ClrMdObjectInspection? InspectObject(
        ulong address,
        ulong? methodTable = null,
        int maxDepth = 5,
        int maxArrayElements = 10,
        int maxStringLength = 1024);
}

