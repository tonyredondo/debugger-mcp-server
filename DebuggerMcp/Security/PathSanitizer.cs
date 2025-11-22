using System.Text.RegularExpressions;

namespace DebuggerMcp.Security;

/// <summary>
/// Utility class for sanitizing path components to prevent path traversal attacks.
/// </summary>
/// <remarks>
/// <para>
/// This class provides defense-in-depth against directory traversal attacks where
/// malicious users attempt to access files outside the intended storage directory
/// by using special characters like ".." or "/" in identifiers.
/// </para>
/// <para>
/// Example attack vectors this prevents:
/// </para>
/// <list type="bullet">
/// <item><description>"../../../etc/passwd" - Attempting to escape the storage directory</description></item>
/// <item><description>"..\..\Windows\System32\config\SAM" - Windows path traversal</description></item>
/// <item><description>"valid/../../../etc/passwd" - Embedded traversal in valid-looking input</description></item>
/// </list>
/// </remarks>
public static partial class PathSanitizer
{
    /// <summary>
    /// Regex pattern for valid identifiers (alphanumeric, dash, underscore, @ and .).
    /// </summary>
    /// <remarks>
    /// The allowed characters are chosen to support common identifier formats:
    /// - Alphanumeric: Standard identifier characters
    /// - Dash (-): Common in UUIDs and slugs
    /// - Underscore (_): Common in variable names
    /// - At (@): Used in email-style user IDs
    /// - Dot (.): Used in domain names and versions
    /// </remarks>
    [GeneratedRegex(@"^[a-zA-Z0-9_\-@.]+$", RegexOptions.Compiled)]
    private static partial Regex ValidIdentifierRegex();

    /// <summary>
    /// Maximum length for a path component.
    /// </summary>
    /// <remarks>
    /// 255 is the maximum filename length on most filesystems (NTFS, ext4, APFS).
    /// </remarks>
    private const int MaxPathComponentLength = 255;

    /// <summary>
    /// Sanitizes a user-provided identifier (userId, dumpId, etc.) for safe use in file paths.
    /// </summary>
    /// <param name="identifier">The identifier to sanitize.</param>
    /// <param name="parameterName">The parameter name for error messages.</param>
    /// <returns>The sanitized identifier, guaranteed to be safe for use in file paths.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown if the identifier is null, empty, too long, contains path traversal characters,
    /// or doesn't match the allowed character pattern.
    /// </exception>
    /// <remarks>
    /// <para>The sanitization process applies multiple layers of validation:</para>
    /// <list type="number">
    /// <item><description>Null/empty check - Reject missing values</description></item>
    /// <item><description>Length check - Prevent filesystem limits from being exceeded</description></item>
    /// <item><description>Traversal check - Reject ".." and path separators</description></item>
    /// <item><description>Pattern check - Allowlist of safe characters</description></item>
    /// <item><description>GetFileName check - Final defense against edge cases</description></item>
    /// </list>
    /// </remarks>
    public static string SanitizeIdentifier(string identifier, string parameterName = "identifier")
    {
        // Layer 1: Reject null or empty input
        // Empty identifiers cannot map to valid paths and may cause unexpected behavior
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException($"{parameterName} cannot be null or empty.", parameterName);
        }

        // Normalize by trimming whitespace
        var sanitized = identifier.Trim();

        // Layer 2: Check length against filesystem limits
        // Excessively long identifiers could cause issues with filesystem path limits
        // and may be an attempt to exploit buffer handling
        if (sanitized.Length > MaxPathComponentLength)
        {
            throw new ArgumentException(
                $"{parameterName} exceeds maximum length of {MaxPathComponentLength} characters.",
                parameterName);
        }

        // Layer 3: Explicitly reject path traversal patterns
        // This is the primary defense against directory escape attacks
        // We check for both Unix (/) and Windows (\) path separators
        if (sanitized.Contains("..") || sanitized.Contains('/') || sanitized.Contains('\\'))
        {
            throw new ArgumentException(
                $"{parameterName} contains invalid characters (path traversal attempt detected).",
                parameterName);
        }

        // Layer 4: Validate against allowlist pattern
        // Only permit known-safe characters to prevent any unknown edge cases
        if (!ValidIdentifierRegex().IsMatch(sanitized))
        {
            throw new ArgumentException(
                $"{parameterName} contains invalid characters. Only alphanumeric, dash, underscore, @ and . are allowed.",
                parameterName);
        }

        // Layer 5: Final safety check using Path.GetFileName
        // This is a defense-in-depth measure that catches any edge cases
        // the regex might miss (e.g., platform-specific path handling quirks)
        var fileName = Path.GetFileName(sanitized);

        // If GetFileName returns a different value, it stripped directory components
        // This should never happen if our earlier checks worked, but we verify anyway
        if (string.IsNullOrEmpty(fileName) || fileName != sanitized)
        {
            throw new ArgumentException(
                $"{parameterName} contains invalid path components.",
                parameterName);
        }

        return sanitized;
    }

    /// <summary>
    /// Checks if an identifier is valid without throwing an exception.
    /// </summary>
    /// <param name="identifier">The identifier to check.</param>
    /// <returns>
    /// <c>true</c> if the identifier passes all validation checks; otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// This method performs the same validation as <see cref="SanitizeIdentifier"/> but
    /// returns a boolean instead of throwing exceptions. Useful for conditional logic
    /// where invalid input is expected and should be handled gracefully.
    /// </remarks>
    public static bool IsValidIdentifier(string identifier)
    {
        // Check 1: Reject null or empty input
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        var trimmed = identifier.Trim();

        // Check 2: Reject overly long identifiers
        if (trimmed.Length > MaxPathComponentLength)
        {
            return false;
        }

        // Check 3: Reject path traversal patterns
        // ".." allows escaping directories, "/" and "\" are path separators
        if (trimmed.Contains("..") || trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            return false;
        }

        // Check 4: Verify against character allowlist
        if (!ValidIdentifierRegex().IsMatch(trimmed))
        {
            return false;
        }

        // Check 5: Final verification using Path.GetFileName
        // Ensures no platform-specific edge cases slip through
        var fileName = Path.GetFileName(trimmed);
        return !string.IsNullOrEmpty(fileName) && fileName == trimmed;
    }
}

