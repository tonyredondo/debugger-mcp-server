using DebuggerMcp.Cli.Client;

namespace DebuggerMcp.Cli.Tests.Client;

/// <summary>
/// Unit tests for <see cref="HttpApiClient"/>.
/// </summary>
public class HttpApiClientTests
{
    [Fact]
    public void IsConfigured_WithoutConfigure_ReturnsFalse()
    {
        // Arrange
        using var client = new HttpApiClient();

        // Act & Assert
        Assert.False(client.IsConfigured);
        Assert.Null(client.ServerUrl);
    }

    [Fact]
    public void Configure_WithValidUrl_SetsIsConfigured()
    {
        // Arrange
        using var client = new HttpApiClient();

        // Act
        client.Configure("http://localhost:5000");

        // Assert
        Assert.True(client.IsConfigured);
        Assert.Equal("http://localhost:5000", client.ServerUrl);
    }

    [Fact]
    public void Configure_WithTrailingSlash_NormalizesUrl()
    {
        // Arrange
        using var client = new HttpApiClient();

        // Act
        client.Configure("http://localhost:5000/");

        // Assert
        Assert.Equal("http://localhost:5000", client.ServerUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Configure_WithEmptyUrl_ThrowsArgumentException(string? invalidUrl)
    {
        // Arrange
        using var client = new HttpApiClient();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => client.Configure(invalidUrl!));
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("://invalid")]
    [InlineData("file:///path/to/file")]
    public void Configure_WithInvalidUrl_ThrowsArgumentException(string invalidUrl)
    {
        // Arrange
        using var client = new HttpApiClient();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => client.Configure(invalidUrl));
    }

    [Theory]
    [InlineData("localhost:5000", "http://localhost:5000")]
    [InlineData("example.com:8080", "http://example.com:8080")]
    [InlineData("192.168.1.1:5000", "http://192.168.1.1:5000")]
    public void Configure_WithoutScheme_DefaultsToHttp(string input, string expected)
    {
        // Arrange
        using var client = new HttpApiClient();

        // Act
        client.Configure(input);

        // Assert
        Assert.True(client.IsConfigured);
        Assert.Equal(expected, client.ServerUrl);
    }

    [Fact]
    public void Configure_WithCustomTimeout_SetsTimeout()
    {
        // Arrange
        using var client = new HttpApiClient();
        var timeout = TimeSpan.FromMinutes(10);

        // Act
        client.Configure("http://localhost:5000", timeout: timeout);

        // Assert
        Assert.True(client.IsConfigured);
    }

    [Fact]
    public async Task CheckHealthAsync_WithoutConfigure_ThrowsInvalidOperationException()
    {
        // Arrange
        using var client = new HttpApiClient();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CheckHealthAsync());
    }

    [Fact]
    public async Task GetAsync_WithoutConfigure_ThrowsInvalidOperationException()
    {
        // Arrange
        using var client = new HttpApiClient();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetAsync<object>("test"));
    }

    [Fact]
    public async Task PostAsync_WithoutConfigure_ThrowsInvalidOperationException()
    {
        // Arrange
        using var client = new HttpApiClient();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostAsync<object, object>("test", new { }));
    }

    [Fact]
    public async Task DeleteAsync_WithoutConfigure_ThrowsInvalidOperationException()
    {
        // Arrange
        using var client = new HttpApiClient();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DeleteAsync("test"));
    }

    [Fact]
    public void Configure_Twice_ReplacesOldConfiguration()
    {
        // Arrange
        using var client = new HttpApiClient();

        // Act
        client.Configure("http://localhost:5000");
        client.Configure("http://localhost:6000");

        // Assert
        Assert.Equal("http://localhost:6000", client.ServerUrl);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var client = new HttpApiClient();
        client.Configure("http://localhost:5000");

        // Act & Assert - should not throw
        client.Dispose();
        client.Dispose();
    }

    #region File Upload Tests

    [Fact]
    public async Task UploadDumpAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        // Arrange
        using var client = new HttpApiClient();
        client.Configure("http://localhost:5000");
        var nonExistentPath = "/path/to/nonexistent/file.dmp";

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => client.UploadDumpAsync(nonExistentPath, "user-123"));
    }

    [Fact]
    public async Task UploadDumpAsync_WithoutConfigure_ThrowsInvalidOperationException()
    {
        // Arrange
        using var client = new HttpApiClient();
        var tempFile = Path.GetTempFileName();

        try
        {
            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.UploadDumpAsync(tempFile, "user-123"));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task UploadSymbolAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        // Arrange
        using var client = new HttpApiClient();
        client.Configure("http://localhost:5000");
        var nonExistentPath = "/path/to/nonexistent/file.pdb";

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => client.UploadSymbolAsync(nonExistentPath, "dump-123"));
    }

    [Fact]
    public async Task UploadSymbolAsync_WithoutConfigure_ThrowsInvalidOperationException()
    {
        // Arrange
        using var client = new HttpApiClient();
        var tempFile = Path.GetTempFileName();

        try
        {
            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.UploadSymbolAsync(tempFile, "dump-123"));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ListDumpsAsync_WithoutConfigure_ThrowsInvalidOperationException()
    {
        // Arrange
        using var client = new HttpApiClient();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListDumpsAsync("user-123"));
    }

    [Fact]
    public async Task GetDumpInfoAsync_WithoutConfigure_ThrowsInvalidOperationException()
    {
        // Arrange
        using var client = new HttpApiClient();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetDumpInfoAsync("user-123", "dump-456"));
    }

    [Fact]
    public async Task DeleteDumpAsync_WithoutConfigure_ThrowsInvalidOperationException()
    {
        // Arrange
        using var client = new HttpApiClient();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DeleteDumpAsync("user-123", "dump-456"));
    }

    [Fact]
    public async Task ListSymbolsAsync_WithoutConfigure_ThrowsInvalidOperationException()
    {
        // Arrange
        using var client = new HttpApiClient();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListSymbolsAsync("dump-123"));
    }

    [Fact]
    public async Task GetStatisticsAsync_WithoutConfigure_ThrowsInvalidOperationException()
    {
        // Arrange
        using var client = new HttpApiClient();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetStatisticsAsync());
    }

    #endregion
}

