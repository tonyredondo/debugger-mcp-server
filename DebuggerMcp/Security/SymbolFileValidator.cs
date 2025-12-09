namespace DebuggerMcp.Security;

/// <summary>
/// Validates symbol file content by checking magic bytes.
/// </summary>
/// <remarks>
/// Symbol files have specific signatures:
/// - Windows PDB MSF 7.0 (native): "Microsoft C/C++ MSF 7.00\r\n\x1aDS"
/// - Windows PDB MSF 2.0 (old native): "Microsoft C/C++ program database 2.00\r\n\x1a"
/// - Portable PDB (.NET Core+): ECMA-335 metadata "BSJB" signature
/// - ELF (Linux .so with debug): 0x7F 0x45 0x4C 0x46
/// - Mach-O (macOS .dylib): Various magic numbers
/// - ar archive (.a files): "!&lt;arch&gt;\n"
/// </remarks>
public static class SymbolFileValidator
{
    /// <summary>
    /// Windows PDB MSF 7.0 signature (native code): "Microsoft C/C++ MSF 7.00\r\n\x1aDS"
    /// Used by Visual Studio for native C/C++ code.
    /// </summary>
    private static readonly byte[] PdbMsf70Signature =
    {
        0x4D, 0x69, 0x63, 0x72, 0x6F, 0x73, 0x6F, 0x66, // "Microsof"
        0x74, 0x20, 0x43, 0x2F, 0x43, 0x2B, 0x2B, 0x20, // "t C/C++ "
        0x4D, 0x53, 0x46, 0x20, 0x37, 0x2E, 0x30, 0x30, // "MSF 7.00"
        0x0D, 0x0A, 0x1A, 0x44, 0x53                    // "\r\n\x1aDS"
    };

    /// <summary>
    /// Windows PDB MSF 2.0 signature (old native code): "Microsoft C/C++ program database 2.00\r\n\x1a"
    /// Used by older Visual Studio versions.
    /// </summary>
    private static readonly byte[] PdbMsf20Signature =
    {
        0x4D, 0x69, 0x63, 0x72, 0x6F, 0x73, 0x6F, 0x66, // "Microsof"
        0x74, 0x20, 0x43, 0x2F, 0x43, 0x2B, 0x2B, 0x20, // "t C/C++ "
        0x70, 0x72, 0x6F, 0x67, 0x72, 0x61, 0x6D, 0x20, // "program "
        0x64, 0x61, 0x74, 0x61, 0x62, 0x61, 0x73, 0x65, // "database"
        0x20, 0x32, 0x2E, 0x30, 0x30, 0x0D, 0x0A, 0x1A  // " 2.00\r\n\x1a"
    };

    /// <summary>
    /// Portable PDB signature (ECMA-335 metadata): "BSJB"
    /// Used by .NET Core, .NET 5+ for managed code (cross-platform).
    /// </summary>
    private static readonly byte[] PortablePdbSignature = { 0x42, 0x53, 0x4A, 0x42 }; // "BSJB"

    /// <summary>
    /// ELF signature (Linux shared libraries with debug symbols): 0x7F "ELF"
    /// </summary>
    private static readonly byte[] ElfSignature = { 0x7F, 0x45, 0x4C, 0x46 };

    /// <summary>
    /// Mach-O 64-bit signature (macOS): 0xCF 0xFA 0xED 0xFE (little endian, most common)
    /// </summary>
    private static readonly byte[] MachO64LeSignature = { 0xCF, 0xFA, 0xED, 0xFE };

    /// <summary>
    /// Mach-O 64-bit signature (macOS): 0xFE 0xED 0xFA 0xCF (big endian)
    /// </summary>
    private static readonly byte[] MachO64BeSignature = { 0xFE, 0xED, 0xFA, 0xCF };

    /// <summary>
    /// Mach-O 32-bit signature (macOS): 0xCE 0xFA 0xED 0xFE (little endian)
    /// </summary>
    private static readonly byte[] MachO32LeSignature = { 0xCE, 0xFA, 0xED, 0xFE };

    /// <summary>
    /// Mach-O 32-bit signature (macOS): 0xFE 0xED 0xFA 0xCE (big endian)
    /// </summary>
    private static readonly byte[] MachO32BeSignature = { 0xFE, 0xED, 0xFA, 0xCE };

    /// <summary>
    /// Unix ar archive signature (for .a static libraries): "!&lt;arch&gt;\n"
    /// </summary>
    private static readonly byte[] ArArchiveSignature = { 0x21, 0x3C, 0x61, 0x72, 0x63, 0x68, 0x3E, 0x0A };

    /// <summary>
    /// Mach-O Fat/Universal binary signature: 0xCA 0xFE 0xBA 0xBE
    /// </summary>
    private static readonly byte[] FatBinarySignature = { 0xCA, 0xFE, 0xBA, 0xBE };

    /// <summary>
    /// Minimum bytes needed to validate a native PDB file (MSF 7.0).
    /// </summary>
    public const int MinimumBytesNeededNativePdb = 29;

    /// <summary>
    /// Minimum bytes needed to validate an old native PDB file (MSF 2.0).
    /// </summary>
    public const int MinimumBytesNeededOldPdb = 40;

    /// <summary>
    /// Minimum bytes needed to validate a Portable PDB file.
    /// </summary>
    public const int MinimumBytesNeededPortablePdb = 4;

    /// <summary>
    /// Minimum bytes needed to validate other symbol files.
    /// </summary>
    public const int MinimumBytesNeeded = 8;

    /// <summary>
    /// Maximum header size needed for any format.
    /// </summary>
    public const int MaxHeaderSize = 40;

    /// <summary>
    /// Validates if the provided bytes represent a valid symbol file header.
    /// </summary>
    /// <param name="header">The first bytes of the file.</param>
    /// <param name="fileName">The file name (used for extension-based format hints).</param>
    /// <returns>True if the header matches a known symbol format.</returns>
    public static bool IsValidSymbolHeader(byte[] header, string fileName)
    {
        if (header == null || header.Length < 4)
        {
            return false;
        }

        var extension = Path.GetExtension(fileName)?.ToLowerInvariant();

        // For PDB files, check all PDB formats:
        // - Native MSF 7.0 (Visual Studio native C/C++)
        // - Native MSF 2.0 (older Visual Studio)
        // - Portable PDB (.NET Core/.NET 5+ managed code)
        if (extension == ".pdb")
        {
            return IsValidPdbHeader(header);
        }

        // For ELF-based files (.so, .debug, etc.)
        if (extension is ".so" or ".debug" or ".dbg")
        {
            return MatchesSignature(header, ElfSignature);
        }

        // For Mach-O based files (.dylib, .dSYM contents)
        if (extension is ".dylib")
        {
            return MatchesSignature(header, MachO64LeSignature) ||
                   MatchesSignature(header, MachO64BeSignature) ||
                   MatchesSignature(header, MachO32LeSignature) ||
                   MatchesSignature(header, MachO32BeSignature) ||
                   MatchesSignature(header, FatBinarySignature);
        }

        // For archive files (.a)
        if (extension == ".a")
        {
            return MatchesSignature(header, ArArchiveSignature);
        }

        // For DWARF files or unknown extensions, accept ELF or Mach-O
        if (extension is ".dwarf" or ".sym")
        {
            return MatchesSignature(header, ElfSignature) ||
                   MatchesSignature(header, MachO64LeSignature) ||
                   MatchesSignature(header, MachO64BeSignature);
        }

        // Unknown extension - accept any known format
        return IsValidPdbHeader(header) ||
               MatchesSignature(header, ElfSignature) ||
               MatchesSignature(header, MachO64LeSignature) ||
               MatchesSignature(header, MachO64BeSignature) ||
               MatchesSignature(header, MachO32LeSignature) ||
               MatchesSignature(header, MachO32BeSignature) ||
               MatchesSignature(header, ArArchiveSignature) ||
               MatchesSignature(header, FatBinarySignature);
    }

    /// <summary>
    /// Checks if the header matches any valid PDB format (native or portable).
    /// </summary>
    /// <param name="header">The file header bytes.</param>
    /// <returns>True if it's a valid PDB file.</returns>
    private static bool IsValidPdbHeader(byte[] header)
    {
        // Check Portable PDB first (BSJB signature) - most common in modern .NET
        if (MatchesSignature(header, PortablePdbSignature))
        {
            return true;
        }

        // Check MSF 7.0 (modern native PDB)
        if (header.Length >= MinimumBytesNeededNativePdb && MatchesSignature(header, PdbMsf70Signature))
        {
            return true;
        }

        // Check MSF 2.0 (old native PDB)
        if (header.Length >= MinimumBytesNeededOldPdb && MatchesSignature(header, PdbMsf20Signature))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets a human-readable description of the symbol file format.
    /// </summary>
    /// <param name="header">The first bytes of the file.</param>
    /// <returns>The symbol format name or "Unknown".</returns>
    public static string GetSymbolFormat(byte[] header)
    {
        if (header == null || header.Length < 4)
        {
            return "Unknown";
        }

        // Check Portable PDB first (most common in modern .NET)
        if (MatchesSignature(header, PortablePdbSignature))
            return "Portable PDB (.NET)";

        // Check native PDB formats
        if (header.Length >= MinimumBytesNeededNativePdb && MatchesSignature(header, PdbMsf70Signature))
            return "Windows PDB (MSF 7.0, Native)";
        if (header.Length >= MinimumBytesNeededOldPdb && MatchesSignature(header, PdbMsf20Signature))
            return "Windows PDB (MSF 2.0, Legacy)";

        // Non-Windows formats
        if (MatchesSignature(header, ElfSignature))
            return "ELF (Linux)";
        if (MatchesSignature(header, MachO64LeSignature) || MatchesSignature(header, MachO64BeSignature))
            return "Mach-O 64-bit (macOS)";
        if (MatchesSignature(header, MachO32LeSignature) || MatchesSignature(header, MachO32BeSignature))
            return "Mach-O 32-bit (macOS)";
        if (MatchesSignature(header, FatBinarySignature))
            return "Mach-O Universal Binary (macOS)";
        if (MatchesSignature(header, ArArchiveSignature))
            return "ar Archive";

        return "Unknown";
    }

    /// <summary>
    /// Validates if a stream contains a valid symbol file.
    /// </summary>
    /// <param name="stream">The stream to validate (must be seekable).</param>
    /// <param name="fileName">The file name for extension-based format hints.</param>
    /// <returns>True if the stream contains a valid symbol header.</returns>
    public static bool IsValidSymbolStream(Stream stream, string fileName)
    {
        if (stream == null || !stream.CanRead)
        {
            return false;
        }

        var originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            var header = new byte[MaxHeaderSize];
            var bytesRead = stream.Read(header, 0, MaxHeaderSize);

            if (bytesRead < MinimumBytesNeeded)
            {
                return false;
            }

            return IsValidSymbolHeader(header, fileName);
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
    /// Checks if the header matches the given signature.
    /// </summary>
    private static bool MatchesSignature(byte[] header, byte[] signature)
    {
        if (header.Length < signature.Length)
        {
            return false;
        }

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

