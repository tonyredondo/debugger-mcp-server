using DebuggerMcp.Security;
using Xunit;

namespace DebuggerMcp.Tests.Security;

public class DumpFileValidatorTests
{
    [Fact]
    public void IsValidDumpHeader_WindowsMinidump_ReturnsTrue()
    {
        // "MDMP" signature
        var header = new byte[] { 0x4D, 0x44, 0x4D, 0x50 };
        Assert.True(DumpFileValidator.IsValidDumpHeader(header));
    }

    [Fact]
    public void IsValidDumpHeader_WindowsFullDump_ReturnsTrue()
    {
        // "PAGE" signature
        var header = new byte[] { 0x50, 0x41, 0x47, 0x45 };
        Assert.True(DumpFileValidator.IsValidDumpHeader(header));
    }

    [Fact]
    public void IsValidDumpHeader_LinuxElfCore_ReturnsTrue()
    {
        // ELF signature: 0x7F "ELF"
        var header = new byte[] { 0x7F, 0x45, 0x4C, 0x46 };
        Assert.True(DumpFileValidator.IsValidDumpHeader(header));
    }

    [Fact]
    public void IsValidDumpHeader_MacOsMachO64Le_ReturnsTrue()
    {
        // Mach-O 64-bit little endian
        var header = new byte[] { 0xCF, 0xFA, 0xED, 0xFE };
        Assert.True(DumpFileValidator.IsValidDumpHeader(header));
    }

    [Fact]
    public void IsValidDumpHeader_MacOsMachO64Be_ReturnsTrue()
    {
        // Mach-O 64-bit big endian
        var header = new byte[] { 0xFE, 0xED, 0xFA, 0xCF };
        Assert.True(DumpFileValidator.IsValidDumpHeader(header));
    }

    [Fact]
    public void IsValidDumpHeader_InvalidHeader_ReturnsFalse()
    {
        // Random bytes
        var header = new byte[] { 0x00, 0x00, 0x00, 0x00 };
        Assert.False(DumpFileValidator.IsValidDumpHeader(header));
    }

    [Fact]
    public void IsValidDumpHeader_PdfFile_ReturnsFalse()
    {
        // PDF signature: %PDF
        var header = new byte[] { 0x25, 0x50, 0x44, 0x46 };
        Assert.False(DumpFileValidator.IsValidDumpHeader(header));
    }

    [Fact]
    public void IsValidDumpHeader_ZipFile_ReturnsFalse()
    {
        // ZIP signature
        var header = new byte[] { 0x50, 0x4B, 0x03, 0x04 };
        Assert.False(DumpFileValidator.IsValidDumpHeader(header));
    }

    [Fact]
    public void IsValidDumpHeader_TooShort_ReturnsFalse()
    {
        var header = new byte[] { 0x4D, 0x44 };
        Assert.False(DumpFileValidator.IsValidDumpHeader(header));
    }

    [Fact]
    public void IsValidDumpHeader_Null_ReturnsFalse()
    {
        Assert.False(DumpFileValidator.IsValidDumpHeader(null!));
    }

    [Theory]
    [InlineData(new byte[] { 0x4D, 0x44, 0x4D, 0x50 }, "Windows Minidump")]
    [InlineData(new byte[] { 0x50, 0x41, 0x47, 0x45 }, "Windows Full/Kernel Dump")]
    [InlineData(new byte[] { 0x7F, 0x45, 0x4C, 0x46 }, "Linux ELF Core Dump")]
    [InlineData(new byte[] { 0xCF, 0xFA, 0xED, 0xFE }, "macOS Mach-O 64-bit Core Dump")]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00 }, "Unknown")]
    public void GetDumpFormat_ReturnsExpectedFormat(byte[] header, string expectedFormat)
    {
        var result = DumpFileValidator.GetDumpFormat(header);
        Assert.Equal(expectedFormat, result);
    }

    [Fact]
    public void IsValidDumpStream_ValidMinidump_ReturnsTrue()
    {
        var dumpData = new byte[] { 0x4D, 0x44, 0x4D, 0x50, 0x00, 0x00, 0x00, 0x00 };
        using var stream = new MemoryStream(dumpData);
        Assert.True(DumpFileValidator.IsValidDumpStream(stream));
    }

    [Fact]
    public void IsValidDumpStream_InvalidData_ReturnsFalse()
    {
        var invalidData = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        using var stream = new MemoryStream(invalidData);
        Assert.False(DumpFileValidator.IsValidDumpStream(stream));
    }

    [Fact]
    public void IsValidDumpStream_RestoresPosition()
    {
        var dumpData = new byte[] { 0x4D, 0x44, 0x4D, 0x50, 0x00, 0x00, 0x00, 0x00 };
        using var stream = new MemoryStream(dumpData);
        stream.Position = 2; // Set initial position

        DumpFileValidator.IsValidDumpStream(stream);

        Assert.Equal(2, stream.Position); // Position should be restored
    }
}

