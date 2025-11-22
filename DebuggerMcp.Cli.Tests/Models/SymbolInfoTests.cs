using DebuggerMcp.Cli.Models;

namespace DebuggerMcp.Cli.Tests.Models;

/// <summary>
/// Unit tests for <see cref="SymbolInfo"/> class.
/// </summary>
public class SymbolInfoTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(2048L, "2 KB")]
    [InlineData(2097152L, "2 MB")]
    [InlineData(2147483648L, "2 GB")]
    public void FormattedSize_ReturnsHumanReadableFormat(long bytes, string expected)
    {
        // Arrange
        var symbol = new SymbolInfo { Size = bytes };

        // Act
        var result = symbol.FormattedSize;

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SymbolInfo_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var symbol = new SymbolInfo();

        // Assert
        Assert.Equal(string.Empty, symbol.FileName);
        Assert.Equal(0L, symbol.Size);
        Assert.Null(symbol.DumpId);
        Assert.Null(symbol.SymbolFormat);
    }

    [Fact]
    public void SymbolInfo_SetsPropertiesCorrectly()
    {
        // Arrange & Act
        var symbol = new SymbolInfo
        {
            FileName = "app.pdb",
            Size = 4194304,
            DumpId = "dump-123",
            SymbolFormat = "Portable PDB"
        };

        // Assert
        Assert.Equal("app.pdb", symbol.FileName);
        Assert.Equal(4194304L, symbol.Size);
        Assert.Equal("dump-123", symbol.DumpId);
        Assert.Equal("Portable PDB", symbol.SymbolFormat);
        Assert.Equal("4 MB", symbol.FormattedSize);
    }
}

/// <summary>
/// Unit tests for <see cref="SymbolUploadResponse"/> class.
/// </summary>
public class SymbolUploadResponseTests
{
    [Fact]
    public void SymbolUploadResponse_DefaultValues()
    {
        // Arrange & Act
        var response = new SymbolUploadResponse();

        // Assert
        Assert.False(response.Success);
        Assert.Null(response.Message);
        Assert.Equal(string.Empty, response.FileName);
    }

    [Fact]
    public void SymbolUploadResponse_InheritsFromSymbolInfo()
    {
        // Arrange & Act
        var response = new SymbolUploadResponse
        {
            FileName = "app.pdb",
            Size = 1024,
            Success = true,
            Message = "Upload successful"
        };

        // Assert
        Assert.Equal("app.pdb", response.FileName);
        Assert.Equal(1024L, response.Size);
        Assert.True(response.Success);
        Assert.Equal("Upload successful", response.Message);
    }
}

/// <summary>
/// Unit tests for <see cref="SymbolListResponse"/> class.
/// </summary>
public class SymbolListResponseTests
{
    [Fact]
    public void SymbolListResponse_DefaultValues()
    {
        // Arrange & Act
        var response = new SymbolListResponse();

        // Assert
        Assert.Equal(string.Empty, response.DumpId);
        Assert.NotNull(response.Symbols);
        Assert.Empty(response.Symbols);
    }

    [Fact]
    public void SymbolListResponse_CanHoldMultipleSymbols()
    {
        // Arrange
        var response = new SymbolListResponse
        {
            DumpId = "dump-123",
            Symbols = new List<string> { "app.pdb", "lib.pdb", "core.pdb" }
        };

        // Assert
        Assert.Equal("dump-123", response.DumpId);
        Assert.Equal(3, response.Symbols.Count);
        Assert.Contains("app.pdb", response.Symbols);
    }
}

/// <summary>
/// Unit tests for <see cref="SymbolBatchUploadResponse"/> class.
/// </summary>
public class SymbolBatchUploadResponseTests
{
    [Fact]
    public void SymbolBatchUploadResponse_DefaultValues()
    {
        // Arrange & Act
        var response = new SymbolBatchUploadResponse();

        // Assert
        Assert.NotNull(response.Results);
        Assert.Empty(response.Results);
        Assert.Equal(0, response.TotalFiles);
        Assert.Equal(0, response.SuccessfulUploads);
        Assert.Equal(0, response.FailedUploads);
    }

    [Fact]
    public void SymbolBatchUploadResponse_TracksUploadResults()
    {
        // Arrange & Act
        var response = new SymbolBatchUploadResponse
        {
            TotalFiles = 3,
            SuccessfulUploads = 2,
            FailedUploads = 1,
            Results = new List<SymbolUploadResult>
            {
                new() { FileName = "a.pdb", Success = true, Message = "OK" },
                new() { FileName = "b.pdb", Success = true, Message = "OK" },
                new() { FileName = "c.pdb", Success = false, Message = "Failed" }
            }
        };

        // Assert
        Assert.Equal(3, response.Results.Count);
        Assert.Equal(3, response.TotalFiles);
        Assert.Equal(2, response.SuccessfulUploads);
        Assert.Equal(1, response.FailedUploads);
    }
}

/// <summary>
/// Tests for <see cref="SymbolUploadResult"/>.
/// </summary>
public class SymbolUploadResultTests
{
    [Fact]
    public void SymbolUploadResult_DefaultValues()
    {
        // Arrange & Act
        var result = new SymbolUploadResult();

        // Assert
        Assert.Equal(string.Empty, result.FileName);
        Assert.False(result.Success);
        Assert.Null(result.Message);
    }

    [Fact]
    public void SymbolUploadResult_TracksUploadStatus()
    {
        // Arrange & Act
        var result = new SymbolUploadResult
        {
            FileName = "test.pdb",
            Success = true,
            Message = "Upload successful"
        };

        // Assert
        Assert.Equal("test.pdb", result.FileName);
        Assert.True(result.Success);
        Assert.Equal("Upload successful", result.Message);
    }
}

