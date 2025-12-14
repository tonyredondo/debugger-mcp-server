using System.Text.Json;
using System.Text.Json.Serialization;

namespace DebuggerMcp.Serialization;

/// <summary>
/// Centralizes <see cref="JsonSerializerOptions"/> instances used across the server.
/// </summary>
/// <remarks>
/// These options are intended to be treated as immutable; do not modify the returned instances at call sites.
/// </remarks>
internal static class JsonSerializationDefaults
{
    internal static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true
    };

    internal static readonly JsonSerializerOptions IndentedIgnoreNull = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static readonly JsonSerializerOptions IndentedCamelCase = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static readonly JsonSerializerOptions IndentedCamelCaseIgnoreNull = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

