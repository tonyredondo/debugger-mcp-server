namespace DebuggerMcp.Security;

/// <summary>
/// Validates dump file content by checking magic bytes.
/// </summary>
/// <remarks>
/// Dump files have specific signatures at the beginning of the file:
/// - Windows Minidump: "MDMP" (0x4D 0x44 0x4D 0x50)
/// - Windows Full/Kernel Dump: "PAGE" (0x50 0x41 0x47 0x45)
/// - ELF Core Dump (Linux): 0x7F 0x45 0x4C 0x46
/// - Mach-O Core Dump (macOS): Various magic numbers
/// </remarks>
public static class DumpFileValidator
{
    /// <summary>
    /// Windows Minidump signature: "MDMP"
    /// </summary>
    private static readonly byte[] MdmpSignature = { 0x4D, 0x44, 0x4D, 0x50 };

    /// <summary>
    /// Windows Full/Kernel Dump signature: "PAGE"
    /// </summary>
    private static readonly byte[] PageSignature = { 0x50, 0x41, 0x47, 0x45 };

    /// <summary>
    /// ELF signature (Linux core dumps): 0x7F "ELF"
    /// </summary>
    private static readonly byte[] ElfSignature = { 0x7F, 0x45, 0x4C, 0x46 };

    /// <summary>
    /// Mach-O 64-bit signature (macOS): 0xFE 0xED 0xFA 0xCF (big endian)
    /// </summary>
    private static readonly byte[] MachO64BeSignature = { 0xFE, 0xED, 0xFA, 0xCF };

    /// <summary>
    /// Mach-O 64-bit signature (macOS): 0xCF 0xFA 0xED 0xFE (little endian)
    /// </summary>
    private static readonly byte[] MachO64LeSignature = { 0xCF, 0xFA, 0xED, 0xFE };

    /// <summary>
    /// Mach-O 32-bit signature (macOS): 0xFE 0xED 0xFA 0xCE (big endian)
    /// </summary>
    private static readonly byte[] MachO32BeSignature = { 0xFE, 0xED, 0xFA, 0xCE };

    /// <summary>
    /// Mach-O 32-bit signature (macOS): 0xCE 0xFA 0xED 0xFE (little endian)
    /// </summary>
    private static readonly byte[] MachO32LeSignature = { 0xCE, 0xFA, 0xED, 0xFE };

    /// <summary>
    /// Minimum bytes needed to validate a dump file.
    /// </summary>
    public const int MinimumBytesNeeded = 4;

    /// <summary>
    /// Validates if the provided bytes represent a valid dump file header.
    /// </summary>
    /// <param name="header">The first bytes of the file (at least 4 bytes).</param>
    /// <returns>True if the header matches a known dump format.</returns>
    public static bool IsValidDumpHeader(byte[] header)
    {
        if (header == null || header.Length < MinimumBytesNeeded)
        {
            return false;
        }

        return MatchesSignature(header, MdmpSignature) ||
               MatchesSignature(header, PageSignature) ||
               MatchesSignature(header, ElfSignature) ||
               MatchesSignature(header, MachO64BeSignature) ||
               MatchesSignature(header, MachO64LeSignature) ||
               MatchesSignature(header, MachO32BeSignature) ||
               MatchesSignature(header, MachO32LeSignature);
    }

    /// <summary>
    /// Validates if a stream contains a valid dump file.
    /// </summary>
    /// <param name="stream">The stream to validate (must be seekable).</param>
    /// <returns>True if the stream contains a valid dump header.</returns>
    public static bool IsValidDumpStream(Stream stream)
    {
        if (stream == null || !stream.CanRead)
        {
            return false;
        }

        var originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            var header = new byte[MinimumBytesNeeded];
            int totalBytesRead = 0;
            int bytesRead;
            while (totalBytesRead < header.Length && 
                   (bytesRead = stream.Read(header, totalBytesRead, header.Length - totalBytesRead)) > 0)
            {
                totalBytesRead += bytesRead;
            }

            if (totalBytesRead < MinimumBytesNeeded)
            {
                return false;
            }

            return IsValidDumpHeader(header);
        }
        finally
        {
            // Restore original position
            if (stream.CanSeek)
            {
                stream.Position = originalPosition;
            }
        }
    }

    /// <summary>
    /// Gets a human-readable description of the dump format.
    /// </summary>
    /// <param name="header">The first bytes of the file.</param>
    /// <returns>The dump format name or "Unknown".</returns>
    public static string GetDumpFormat(byte[] header)
    {
        if (header == null || header.Length < MinimumBytesNeeded)
        {
            return "Unknown";
        }

        if (MatchesSignature(header, MdmpSignature))
            return "Windows Minidump";
        if (MatchesSignature(header, PageSignature))
            return "Windows Full/Kernel Dump";
        if (MatchesSignature(header, ElfSignature))
            return "Linux ELF Core Dump";
        if (MatchesSignature(header, MachO64BeSignature) || MatchesSignature(header, MachO64LeSignature))
            return "macOS Mach-O 64-bit Core Dump";
        if (MatchesSignature(header, MachO32BeSignature) || MatchesSignature(header, MachO32LeSignature))
            return "macOS Mach-O 32-bit Core Dump";

        return "Unknown";
    }

    /// <summary>
    /// Checks if the header matches the given signature.
    /// </summary>
    private static bool MatchesSignature(byte[] header, byte[] signature)
    {
        for (int i = 0; i < signature.Length; i++)
        {
            if (header[i] != signature[i])
            {
                return false;
            }
        }
        return true;
    }
}

