using DebuggerMcp.Cli.Models;

namespace DebuggerMcp.Cli.Tests.Models;

/// <summary>
/// Unit tests for <see cref="SessionStatistics"/> class.
/// </summary>
public class SessionStatisticsTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(1073741824L, "1 GB")]
    [InlineData(1099511627776L, "1 TB")]
    public void FormattedStorageUsed_ReturnsHumanReadableFormat(long bytes, string expected)
    {
        // Arrange
        var stats = new SessionStatistics { StorageUsed = bytes };

        // Act
        var result = stats.FormattedStorageUsed;

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SessionStatistics_DefaultValues()
    {
        // Arrange & Act
        var stats = new SessionStatistics();

        // Assert
        Assert.Equal(0, stats.ActiveSessions);
        Assert.Equal(0, stats.TotalDumps);
        Assert.Equal(0L, stats.StorageUsed);
        Assert.Null(stats.Uptime);
    }

    [Fact]
    public void SessionStatistics_SetsPropertiesCorrectly()
    {
        // Arrange & Act
        var stats = new SessionStatistics
        {
            ActiveSessions = 5,
            TotalDumps = 100,
            StorageUsed = 10737418240L, // 10 GB
            Uptime = "5d 12h 30m"
        };

        // Assert
        Assert.Equal(5, stats.ActiveSessions);
        Assert.Equal(100, stats.TotalDumps);
        Assert.Equal(10737418240L, stats.StorageUsed);
        Assert.Equal("5d 12h 30m", stats.Uptime);
        Assert.Equal("10 GB", stats.FormattedStorageUsed);
    }
}

