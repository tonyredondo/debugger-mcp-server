using System.Text.Json;
using System.Text.Json.Serialization;

namespace DebuggerMcp.Cli.Serialization;

/// <summary>
/// Centralizes <see cref="JsonSerializerOptions"/> instances used across the CLI.
/// </summary>
/// <remarks>
/// These options are intended to be treated as immutable; do not modify the returned instances at call sites.
/// </remarks>
internal static class CliJsonSerializationDefaults
{
    internal static readonly JsonSerializerOptions CaseInsensitiveCamelCase = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static readonly JsonSerializerOptions CaseInsensitiveCamelCaseIgnoreNull = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static readonly JsonSerializerOptions CaseInsensitiveCamelCaseIndentedIgnoreNull = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static readonly JsonSerializerOptions CamelCaseIndentedIgnoreNull = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

