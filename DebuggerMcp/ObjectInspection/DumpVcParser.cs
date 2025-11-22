using System.Text.RegularExpressions;
using DebuggerMcp.ObjectInspection.Models;

namespace DebuggerMcp.ObjectInspection;

/// <summary>
/// Parses the output of the SOS dumpvc command (for value types/structs).
/// </summary>
public static partial class DumpVcParser
{
    // Regex patterns for parsing dumpvc output
    [GeneratedRegex(@"^Name:\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^MethodTable:\s+([0-9a-fA-F]+)$", RegexOptions.Multiline)]
    private static partial Regex MethodTableRegex();

    [GeneratedRegex(@"^Size:\s+(\d+)\s*\(", RegexOptions.Multiline)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"^File:\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex FileRegex();

    // Field line regex - same format as dumpobj
    // Note: Attr can be "static", "instance", or "TLstatic" (thread-local static)
    // Note: Type can be empty (just whitespace) for special fields with null MT
    // Note: TLstatic/null-MT-static fields may have NO Value column - only Name follows Attr
    // Group 7 captures "Value Name" or just "Name", parsed separately in ParseFields()
    [GeneratedRegex(@"^([0-9a-fA-F]+)\s+([0-9a-fA-F]+)\s+([0-9a-fA-F]+)\s+(\S*)\s+(\d+)\s+(static|instance|TLstatic)\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex FieldLineRegex();

    /// <summary>
    /// Parses the output of a dumpvc command.
    /// </summary>
    /// <param name="output">The raw output from dumpvc.</param>
    /// <returns>Parsed result with IsValueType set to true.</returns>
    public static DumpObjectResult Parse(string output)
    {
        var result = new DumpObjectResult
        {
            IsValueType = true
        };

        if (string.IsNullOrWhiteSpace(output))
        {
            result.ErrorMessage = "Empty output";
            return result;
        }

        // Check for error indicators
        if (output.Contains("Usage:") ||
            output.Contains("Invalid") ||
            output.Contains("Error:"))
        {
            result.ErrorMessage = "Invalid value type or address";
            return result;
        }

        // Parse Name
        var nameMatch = NameRegex().Match(output);
        if (nameMatch.Success)
        {
            result.Name = nameMatch.Groups[1].Value.Trim();
            result.Success = true;
        }
        else
        {
            result.ErrorMessage = "Could not parse value type name";
            return result;
        }

        // Parse MethodTable
        var mtMatch = MethodTableRegex().Match(output);
        if (mtMatch.Success)
        {
            result.MethodTable = mtMatch.Groups[1].Value.Trim();
        }

        // Parse Size
        var sizeMatch = SizeRegex().Match(output);
        if (sizeMatch.Success && int.TryParse(sizeMatch.Groups[1].Value, out var size))
        {
            result.Size = size;
        }

        // Parse File
        var fileMatch = FileRegex().Match(output);
        if (fileMatch.Success)
        {
            result.File = fileMatch.Groups[1].Value.Trim();
        }

        // Parse fields
        result.Fields = ParseFields(output);

        return result;
    }

    /// <summary>
    /// Parses the fields section of dumpvc output.
    /// </summary>
    private static List<DumpFieldInfo> ParseFields(string output)
    {
        var fields = new List<DumpFieldInfo>();

        // Find the Fields: section
        var fieldsIndex = output.IndexOf("Fields:", StringComparison.OrdinalIgnoreCase);
        if (fieldsIndex < 0)
        {
            // Try to find the table header
            fieldsIndex = output.IndexOf("MT    Field   Offset", StringComparison.OrdinalIgnoreCase);
        }

        if (fieldsIndex < 0)
        {
            return fields;
        }

        var fieldsSection = output[fieldsIndex..];

        // Check for "None" fields
        if (fieldsSection.Contains("None"))
        {
            return fields;
        }

        // Parse each field line
        var matches = FieldLineRegex().Matches(fieldsSection);
        foreach (Match match in matches)
        {
            var attrValue = match.Groups[6].Value;
            var valueAndName = match.Groups[7].Value.Trim();
            
            // Parse Value and Name from the captured group
            // TLstatic and null-MT-static fields may only have Name (no Value)
            // Normal fields have "Value Name" (space-separated)
            string fieldValue;
            string fieldName;
            
            var spaceIndex = valueAndName.IndexOf(' ');
            if (spaceIndex > 0)
            {
                // Has both Value and Name
                fieldValue = valueAndName[..spaceIndex].Trim();
                fieldName = valueAndName[(spaceIndex + 1)..].Trim();
            }
            else
            {
                // Only Name (no Value) - typical for TLstatic fields
                fieldValue = string.Empty;
                fieldName = valueAndName;
            }
            
            var field = new DumpFieldInfo
            {
                MethodTable = match.Groups[1].Value.Trim(),
                FieldToken = match.Groups[2].Value.Trim(),
                Type = match.Groups[4].Value.Trim(),
                IsValueType = match.Groups[5].Value == "1",
                // TLstatic (thread-local static) is also a static field
                IsStatic = attrValue.Equals("static", StringComparison.OrdinalIgnoreCase) ||
                           attrValue.Equals("TLstatic", StringComparison.OrdinalIgnoreCase),
                Value = fieldValue,
                Name = fieldName
            };

            // Parse offset (hex)
            if (int.TryParse(match.Groups[3].Value, System.Globalization.NumberStyles.HexNumber, null, out var offset))
            {
                field.Offset = offset;
            }

            fields.Add(field);
        }

        return fields;
    }

    /// <summary>
    /// Checks if the output indicates a failed dumpvc command.
    /// </summary>
    public static bool IsFailedOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return true;

        // Check for specific error patterns (avoid matching type names like InvalidOperationException)
        return output.Contains("Usage:") ||
               output.Contains("Invalid MethodTable") ||
               output.Contains("Invalid address") ||
               output.Contains("Invalid value type") ||
               output.Contains("is not a value class") ||
               (output.Contains("Error") && !output.Contains("MethodTable"));
    }
}

