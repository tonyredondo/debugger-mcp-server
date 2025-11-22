using System.Text.RegularExpressions;
using DebuggerMcp.ObjectInspection.Models;

namespace DebuggerMcp.ObjectInspection;

/// <summary>
/// Parses the output of the SOS dumpobj command.
/// </summary>
public static partial class DumpObjParser
{
    // Regex patterns for parsing dumpobj output
    [GeneratedRegex(@"^Name:\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^MethodTable:\s+([0-9a-fA-F]+)$", RegexOptions.Multiline)]
    private static partial Regex MethodTableRegex();

    [GeneratedRegex(@"^Canonical MethodTable:\s+([0-9a-fA-F]+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex CanonicalMethodTableRegex();

    [GeneratedRegex(@"^Size:\s+(\d+)\s*\(", RegexOptions.Multiline)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"^File:\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex FileRegex();

    [GeneratedRegex(@"^String:\s+(.*)$", RegexOptions.Multiline)]
    private static partial Regex StringValueRegex();

    [GeneratedRegex(@"^Array:\s+Rank\s+(\d+),\s+Number of elements\s+(\d+),\s+Type\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex ArrayInfoRegex();

    [GeneratedRegex(@"^Element Methodtable:\s+([0-9a-fA-F]+)$", RegexOptions.Multiline)]
    private static partial Regex ElementMethodTableRegex();

    // Field line regex - matches lines like:
    // 0000f75589b87698  400022e        8 ...ace.TracerManager  0 instance 0000000000000000 _tracerManager
    // 0000000000000000  4000c1d       10              SZARRAY  0 TLstatic  t_safeWaitHandlesForRent
    // MT               Field   Offset                 Type VT     Attr            Value Name
    // Note: Attr can be "static", "instance", or "TLstatic" (thread-local static)
    // Note: Type can be empty (just whitespace) for special fields with null MT
    // Note: TLstatic/null-MT-static fields may have NO Value column - only Name follows Attr
    // Group 7 captures "Value Name" or just "Name", parsed separately in ParseFields()
    [GeneratedRegex(@"^([0-9a-fA-F]+)\s+([0-9a-fA-F]+)\s+([0-9a-fA-F]+)\s+(\S*)\s+(\d+)\s+(static|instance|TLstatic)\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex FieldLineRegex();

    /// <summary>
    /// Parses the output of a dumpobj command.
    /// </summary>
    /// <param name="output">The raw output from dumpobj.</param>
    /// <returns>Parsed result.</returns>
    public static DumpObjectResult Parse(string output)
    {
        var result = new DumpObjectResult();

        if (string.IsNullOrWhiteSpace(output))
        {
            result.ErrorMessage = "Empty output";
            return result;
        }

        // Check for error indicators
        if (output.Contains("Invalid object address") ||
            output.Contains("not a valid object") ||
            output.Contains("<Note: this object has an invalid CLASS field>") ||
            output.Contains("Error:"))
        {
            result.ErrorMessage = "Invalid object or address";
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
            result.ErrorMessage = "Could not parse object name";
            return result;
        }

        // Parse MethodTable
        var mtMatch = MethodTableRegex().Match(output);
        if (mtMatch.Success)
        {
            result.MethodTable = mtMatch.Groups[1].Value.Trim();
        }

        // Parse Canonical MethodTable
        var canonicalMtMatch = CanonicalMethodTableRegex().Match(output);
        if (canonicalMtMatch.Success)
        {
            result.CanonicalMethodTable = canonicalMtMatch.Groups[1].Value.Trim();
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

        // Check if it's a string - parse the string value
        if (result.Name == "System.String")
        {
            var stringMatch = StringValueRegex().Match(output);
            if (stringMatch.Success)
            {
                result.StringValue = stringMatch.Groups[1].Value;
            }
        }

        // Check if it's an array
        var arrayMatch = ArrayInfoRegex().Match(output);
        if (arrayMatch.Success)
        {
            result.IsArray = true;
            if (int.TryParse(arrayMatch.Groups[2].Value, out var arrayLength))
            {
                result.ArrayLength = arrayLength;
            }
            result.ArrayElementType = arrayMatch.Groups[3].Value.Trim();

            // Get element method table
            var elementMtMatch = ElementMethodTableRegex().Match(output);
            if (elementMtMatch.Success)
            {
                result.ArrayElementMethodTable = elementMtMatch.Groups[1].Value.Trim();
            }
        }

        // Parse fields
        result.Fields = ParseFields(output);

        return result;
    }

    /// <summary>
    /// Parses the fields section of dumpobj output.
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
    /// Checks if the output indicates a failed dumpobj command.
    /// </summary>
    public static bool IsFailedOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return true;

        return output.Contains("Invalid object address") ||
               output.Contains("not a valid object") ||
               output.Contains("<Note: this object has an invalid CLASS field>") ||
               output.Contains("is not a managed object") ||
               (output.Contains("Error") && !output.Contains("MethodTable"));
    }
}

