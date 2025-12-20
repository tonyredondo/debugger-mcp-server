using DebuggerMcp.Security;
using Xunit;

namespace DebuggerMcp.Tests.Security;

/// <summary>
/// Tests for the SymbolFileValidator class.
/// </summary>
public class SymbolFileValidatorTests
{

    [Fact]
    public void IsValidSymbolHeader_PortablePdb_ReturnsTrue()
    {
        // Portable PDB signature: "BSJB" (0x42 0x53 0x4A 0x42)
        var header = new byte[] { 0x42, 0x53, 0x4A, 0x42, 0x00, 0x00, 0x00, 0x00 };

        var result = SymbolFileValidator.IsValidSymbolHeader(header, "test.pdb");

        Assert.True(result);
    }

    [Fact]
    public void GetSymbolFormat_PortablePdb_ReturnsCorrectFormat()
    {
        var header = new byte[] { 0x42, 0x53, 0x4A, 0x42, 0x00, 0x00, 0x00, 0x00 };

        var format = SymbolFileValidator.GetSymbolFormat(header);

        Assert.Equal("Portable PDB (.NET)", format);
    }



    [Fact]
    public void IsValidSymbolHeader_WindowsPdbMsf70_ReturnsTrue()
    {
        // MSF 7.0 signature: "Microsoft C/C++ MSF 7.00\r\n\x1aDS"
        var header = new byte[]
        {
            0x4D, 0x69, 0x63, 0x72, 0x6F, 0x73, 0x6F, 0x66, // "Microsof"
            0x74, 0x20, 0x43, 0x2F, 0x43, 0x2B, 0x2B, 0x20, // "t C/C++ "
            0x4D, 0x53, 0x46, 0x20, 0x37, 0x2E, 0x30, 0x30, // "MSF 7.00"
            0x0D, 0x0A, 0x1A, 0x44, 0x53, 0x00, 0x00, 0x00  // "\r\n\x1aDS..."
        };

        var result = SymbolFileValidator.IsValidSymbolHeader(header, "native.pdb");

        Assert.True(result);
    }

    [Fact]
    public void GetSymbolFormat_WindowsPdbMsf70_ReturnsCorrectFormat()
    {
        var header = new byte[]
        {
            0x4D, 0x69, 0x63, 0x72, 0x6F, 0x73, 0x6F, 0x66,
            0x74, 0x20, 0x43, 0x2F, 0x43, 0x2B, 0x2B, 0x20,
            0x4D, 0x53, 0x46, 0x20, 0x37, 0x2E, 0x30, 0x30,
            0x0D, 0x0A, 0x1A, 0x44, 0x53, 0x00, 0x00, 0x00
        };

        var format = SymbolFileValidator.GetSymbolFormat(header);

        Assert.Equal("Windows PDB (MSF 7.0, Native)", format);
    }



    [Fact]
    public void IsValidSymbolHeader_WindowsPdbMsf20_ReturnsTrue()
    {
        // MSF 2.0 signature: "Microsoft C/C++ program database 2.00\r\n\x1a"
        var header = new byte[]
        {
            0x4D, 0x69, 0x63, 0x72, 0x6F, 0x73, 0x6F, 0x66, // "Microsof"
            0x74, 0x20, 0x43, 0x2F, 0x43, 0x2B, 0x2B, 0x20, // "t C/C++ "
            0x70, 0x72, 0x6F, 0x67, 0x72, 0x61, 0x6D, 0x20, // "program "
            0x64, 0x61, 0x74, 0x61, 0x62, 0x61, 0x73, 0x65, // "database"
            0x20, 0x32, 0x2E, 0x30, 0x30, 0x0D, 0x0A, 0x1A, // " 2.00\r\n\x1a"
            0x00, 0x00                                       // padding
        };

        var result = SymbolFileValidator.IsValidSymbolHeader(header, "legacy.pdb");

        Assert.True(result);
    }

    [Fact]
    public void GetSymbolFormat_WindowsPdbMsf20_ReturnsCorrectFormat()
    {
        var header = new byte[]
        {
            0x4D, 0x69, 0x63, 0x72, 0x6F, 0x73, 0x6F, 0x66,
            0x74, 0x20, 0x43, 0x2F, 0x43, 0x2B, 0x2B, 0x20,
            0x70, 0x72, 0x6F, 0x67, 0x72, 0x61, 0x6D, 0x20,
            0x64, 0x61, 0x74, 0x61, 0x62, 0x61, 0x73, 0x65,
            0x20, 0x32, 0x2E, 0x30, 0x30, 0x0D, 0x0A, 0x1A,
            0x00, 0x00
        };

        var format = SymbolFileValidator.GetSymbolFormat(header);

        Assert.Equal("Windows PDB (MSF 2.0, Legacy)", format);
    }



    [Fact]
    public void IsValidSymbolHeader_ElfFile_ReturnsTrue()
    {
        // ELF signature: 0x7F "ELF"
        var header = new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00 };

        var result = SymbolFileValidator.IsValidSymbolHeader(header, "libtest.so");

        Assert.True(result);
    }

    [Fact]
    public void IsValidSymbolHeader_ElfDebugFile_ReturnsTrue()
    {
        var header = new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00 };

        var result = SymbolFileValidator.IsValidSymbolHeader(header, "test.debug");

        Assert.True(result);
    }

    [Fact]
    public void GetSymbolFormat_ElfFile_ReturnsCorrectFormat()
    {
        var header = new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00 };

        var format = SymbolFileValidator.GetSymbolFormat(header);

        Assert.Equal("ELF (Linux)", format);
    }



    [Fact]
    public void IsValidSymbolHeader_MachO64LittleEndian_ReturnsTrue()
    {
        // Mach-O 64-bit LE: 0xCF 0xFA 0xED 0xFE
        var header = new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 0x07, 0x00, 0x00, 0x01 };

        var result = SymbolFileValidator.IsValidSymbolHeader(header, "libtest.dylib");

        Assert.True(result);
    }

    [Fact]
    public void IsValidSymbolHeader_MachO64BigEndian_ReturnsTrue()
    {
        // Mach-O 64-bit BE: 0xFE 0xED 0xFA 0xCF
        var header = new byte[] { 0xFE, 0xED, 0xFA, 0xCF, 0x00, 0x00, 0x00, 0x07 };

        var result = SymbolFileValidator.IsValidSymbolHeader(header, "libtest.dylib");

        Assert.True(result);
    }

    [Fact]
    public void IsValidSymbolHeader_MachO32LittleEndian_ReturnsTrue()
    {
        // Mach-O 32-bit LE: 0xCE 0xFA 0xED 0xFE
        var header = new byte[] { 0xCE, 0xFA, 0xED, 0xFE, 0x07, 0x00, 0x00, 0x00 };

        var result = SymbolFileValidator.IsValidSymbolHeader(header, "libtest.dylib");

        Assert.True(result);
    }

    [Fact]
    public void IsValidSymbolHeader_MachO32BigEndian_ReturnsTrue()
    {
        // Mach-O 32-bit BE: 0xFE 0xED 0xFA 0xCE
        var header = new byte[] { 0xFE, 0xED, 0xFA, 0xCE, 0x00, 0x00, 0x00, 0x07 };

        var result = SymbolFileValidator.IsValidSymbolHeader(header, "libtest.dylib");

        Assert.True(result);
    }

    [Fact]
    public void IsValidSymbolHeader_MachOUniversal_ReturnsTrue()
    {
        // Mach-O Universal/Fat: 0xCA 0xFE 0xBA 0xBE
        var header = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x02 };

        var result = SymbolFileValidator.IsValidSymbolHeader(header, "libtest.dylib");

        Assert.True(result);
    }

    [Fact]
    public void GetSymbolFormat_MachO64_ReturnsCorrectFormat()
    {
        var header = new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 0x07, 0x00, 0x00, 0x01 };

        var format = SymbolFileValidator.GetSymbolFormat(header);

        Assert.Equal("Mach-O 64-bit (macOS)", format);
    }

    [Fact]
    public void GetSymbolFormat_MachO32_ReturnsCorrectFormat()
    {
        var header = new byte[] { 0xCE, 0xFA, 0xED, 0xFE, 0x07, 0x00, 0x00, 0x00 };

        var format = SymbolFileValidator.GetSymbolFormat(header);

        Assert.Equal("Mach-O 32-bit (macOS)", format);
    }

    [Fact]
    public void GetSymbolFormat_MachOUniversal_ReturnsCorrectFormat()
    {
        var header = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x02 };

        var format = SymbolFileValidator.GetSymbolFormat(header);

        Assert.Equal("Mach-O Universal Binary (macOS)", format);
    }



    [Fact]
    public void IsValidSymbolHeader_ArArchive_ReturnsTrue()
    {
        // ar archive: "!<arch>\n"
        var header = new byte[] { 0x21, 0x3C, 0x61, 0x72, 0x63, 0x68, 0x3E, 0x0A };

        var result = SymbolFileValidator.IsValidSymbolHeader(header, "libtest.a");

        Assert.True(result);
    }

    [Fact]
    public void GetSymbolFormat_ArArchive_ReturnsCorrectFormat()
    {
        var header = new byte[] { 0x21, 0x3C, 0x61, 0x72, 0x63, 0x68, 0x3E, 0x0A };

        var format = SymbolFileValidator.GetSymbolFormat(header);

        Assert.Equal("ar Archive", format);
    }



    [Fact]
    public void IsValidSymbolHeader_NullHeader_ReturnsFalse()
    {
        var result = SymbolFileValidator.IsValidSymbolHeader(null!, "test.pdb");

        Assert.False(result);
    }

    [Fact]
    public void IsValidSymbolHeader_EmptyHeader_ReturnsFalse()
    {
        var header = Array.Empty<byte>();

        var result = SymbolFileValidator.IsValidSymbolHeader(header, "test.pdb");

        Assert.False(result);
    }

    [Fact]
    public void IsValidSymbolHeader_TooShortHeader_ReturnsFalse()
    {
        var header = new byte[] { 0x42, 0x53, 0x4A }; // Only 3 bytes

        var result = SymbolFileValidator.IsValidSymbolHeader(header, "test.pdb");

        Assert.False(result);
    }

    [Fact]
    public void IsValidSymbolHeader_InvalidSignature_ReturnsFalse()
    {
        var header = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        var result = SymbolFileValidator.IsValidSymbolHeader(header, "test.pdb");

        Assert.False(result);
    }

    [Fact]
    public void IsValidSymbolHeader_RandomData_ReturnsFalse()
    {
        var header = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xD0 };

        var result = SymbolFileValidator.IsValidSymbolHeader(header, "test.pdb");

        Assert.False(result);
    }

    [Fact]
    public void GetSymbolFormat_InvalidHeader_ReturnsUnknown()
    {
        var header = new byte[] { 0x00, 0x00, 0x00, 0x00 };

        var format = SymbolFileValidator.GetSymbolFormat(header);

        Assert.Equal("Unknown", format);
    }

    [Fact]
    public void GetSymbolFormat_NullHeader_ReturnsUnknown()
    {
        var format = SymbolFileValidator.GetSymbolFormat(null!);

        Assert.Equal("Unknown", format);
    }



    [Fact]
    public void IsValidSymbolHeader_PdbExtension_OnlyAcceptsPdbFormats()
    {
        // ELF file with .pdb extension should fail
        var elfHeader = new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00 };

        var result = SymbolFileValidator.IsValidSymbolHeader(elfHeader, "wrong.pdb");

        Assert.False(result);
    }

    [Fact]
    public void IsValidSymbolHeader_SoExtension_OnlyAcceptsElf()
    {
        // Portable PDB with .so extension should fail
        var pdbHeader = new byte[] { 0x42, 0x53, 0x4A, 0x42, 0x00, 0x00, 0x00, 0x00 };

        var result = SymbolFileValidator.IsValidSymbolHeader(pdbHeader, "wrong.so");

        Assert.False(result);
    }

    [Fact]
    public void IsValidSymbolHeader_DylibExtension_OnlyAcceptsMachO()
    {
        // ELF with .dylib extension should fail
        var elfHeader = new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00 };

        var result = SymbolFileValidator.IsValidSymbolHeader(elfHeader, "wrong.dylib");

        Assert.False(result);
    }

    [Fact]
    public void IsValidSymbolHeader_AExtension_OnlyAcceptsArArchive()
    {
        // Portable PDB with .a extension should fail
        var pdbHeader = new byte[] { 0x42, 0x53, 0x4A, 0x42, 0x00, 0x00, 0x00, 0x00 };

        var result = SymbolFileValidator.IsValidSymbolHeader(pdbHeader, "wrong.a");

        Assert.False(result);
    }

    [Fact]
    public void IsValidSymbolHeader_UnknownExtension_AcceptsAnyValidFormat()
    {
        // Unknown extension should accept any valid format
        var pdbHeader = new byte[] { 0x42, 0x53, 0x4A, 0x42, 0x00, 0x00, 0x00, 0x00 };
        var elfHeader = new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00 };
        var machOHeader = new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 0x07, 0x00, 0x00, 0x01 };

        Assert.True(SymbolFileValidator.IsValidSymbolHeader(pdbHeader, "test.xyz"));
        Assert.True(SymbolFileValidator.IsValidSymbolHeader(elfHeader, "test.xyz"));
        Assert.True(SymbolFileValidator.IsValidSymbolHeader(machOHeader, "test.xyz"));
    }



    [Fact]
    public void MinimumBytesNeeded_Is8()
    {
        Assert.Equal(8, SymbolFileValidator.MinimumBytesNeeded);
    }

    [Fact]
    public void MinimumBytesNeededNativePdb_Is29()
    {
        Assert.Equal(29, SymbolFileValidator.MinimumBytesNeededNativePdb);
    }

    [Fact]
    public void MinimumBytesNeededOldPdb_Is40()
    {
        Assert.Equal(40, SymbolFileValidator.MinimumBytesNeededOldPdb);
    }

    [Fact]
    public void MinimumBytesNeededPortablePdb_Is4()
    {
        Assert.Equal(4, SymbolFileValidator.MinimumBytesNeededPortablePdb);
    }

    [Fact]
    public void MaxHeaderSize_Is40()
    {
        Assert.Equal(40, SymbolFileValidator.MaxHeaderSize);
    }

    [Fact]
    public void IsValidSymbolStream_WhenTooShort_ReturnsFalseAndRestoresPosition()
    {
        var bytes = new byte[] { 1, 2, 3 };
        using var stream = new MemoryStream(bytes);
        stream.Position = 2;

        var ok = SymbolFileValidator.IsValidSymbolStream(stream, "test.pdb");

        Assert.False(ok);
        Assert.Equal(2, stream.Position);
    }

    [Fact]
    public void IsValidSymbolStream_WhenPortablePdbHeader_ReturnsTrue()
    {
        // "BSJB" portable PDB signature
        var header = new byte[] { 0x42, 0x53, 0x4A, 0x42, 0, 0, 0, 0 };
        using var stream = new MemoryStream(header);

        var ok = SymbolFileValidator.IsValidSymbolStream(stream, "test.pdb");

        Assert.True(ok);
    }

}
