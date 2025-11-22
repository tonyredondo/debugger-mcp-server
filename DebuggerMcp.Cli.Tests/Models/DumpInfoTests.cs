using DebuggerMcp.Cli.Models;

namespace DebuggerMcp.Cli.Tests.Models;

/// <summary>
/// Unit tests for <see cref="DumpInfo"/> class.
/// </summary>
public class DumpInfoTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(100L, "100 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(1572864L, "1.5 MB")]
    [InlineData(1073741824L, "1 GB")]
    [InlineData(1610612736L, "1.5 GB")]
    public void FormattedSize_ReturnsHumanReadableFormat(long bytes, string expected)
    {
        // Arrange
        var dump = new DumpInfo { Size = bytes };

        // Act
        var result = dump.FormattedSize;

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void DumpInfo_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var dump = new DumpInfo();

        // Assert
        Assert.Equal(string.Empty, dump.DumpId);
        Assert.Equal(string.Empty, dump.UserId);
        Assert.Null(dump.FileName);
        Assert.Equal(0L, dump.Size);
        Assert.Equal(default, dump.UploadedAt);
        Assert.Null(dump.Description);
        Assert.Null(dump.DumpFormat);
    }

    [Fact]
    public void DumpInfo_SetsPropertiesCorrectly()
    {
        // Arrange
        var uploadedAt = DateTime.UtcNow;

        // Act
        var dump = new DumpInfo
        {
            DumpId = "dump-123",
            UserId = "user-456",
            FileName = "crash.dmp",
            Size = 1048576,
            UploadedAt = uploadedAt,
            Description = "Production crash",
            DumpFormat = "Windows Minidump"
        };

        // Assert
        Assert.Equal("dump-123", dump.DumpId);
        Assert.Equal("user-456", dump.UserId);
        Assert.Equal("crash.dmp", dump.FileName);
        Assert.Equal(1048576L, dump.Size);
        Assert.Equal(uploadedAt, dump.UploadedAt);
        Assert.Equal("Production crash", dump.Description);
        Assert.Equal("Windows Minidump", dump.DumpFormat);
        Assert.Equal("1 MB", dump.FormattedSize);
    }
}

/// <summary>
/// Unit tests for <see cref="DumpListResponse"/> class.
/// </summary>
public class DumpListResponseTests
{
    [Fact]
    public void DumpListResponse_DefaultDumps_IsEmptyList()
    {
        // Arrange & Act
        var response = new DumpListResponse();

        // Assert
        Assert.NotNull(response.Dumps);
        Assert.Empty(response.Dumps);
    }

    [Fact]
    public void DumpListResponse_CanHoldMultipleDumps()
    {
        // Arrange
        var response = new DumpListResponse();

        // Act
        response.Dumps.Add(new DumpInfo { DumpId = "1" });
        response.Dumps.Add(new DumpInfo { DumpId = "2" });

        // Assert
        Assert.Equal(2, response.Dumps.Count);
    }
}

/// <summary>
/// Unit tests for <see cref="DumpDeleteResponse"/> class.
/// </summary>
public class DumpDeleteResponseTests
{
    [Fact]
    public void DumpDeleteResponse_DefaultValues()
    {
        // Arrange & Act
        var response = new DumpDeleteResponse();

        // Assert
        Assert.False(response.Success);
        Assert.Null(response.Message);
    }

    [Fact]
    public void DumpDeleteResponse_SetsPropertiesCorrectly()
    {
        // Arrange & Act
        var response = new DumpDeleteResponse
        {
            Success = true,
            Message = "Dump deleted successfully"
        };

        // Assert
        Assert.True(response.Success);
        Assert.Equal("Dump deleted successfully", response.Message);
    }
}

