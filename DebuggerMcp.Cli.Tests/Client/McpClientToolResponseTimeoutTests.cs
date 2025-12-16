using DebuggerMcp.Cli.Client;

namespace DebuggerMcp.Cli.Tests.Client;

public class McpClientToolResponseTimeoutTests
{
    [Fact]
    public void NormalizeToolResponseTimeout_WhenZeroOrNegative_UsesTenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), McpClient.NormalizeToolResponseTimeout(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMinutes(10), McpClient.NormalizeToolResponseTimeout(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void NormalizeToolResponseTimeout_WhenInfinite_PreservesInfinite()
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, McpClient.NormalizeToolResponseTimeout(Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public void NormalizeToolResponseTimeout_WhenValid_ReturnsConfiguredValue()
    {
        var configured = TimeSpan.FromMinutes(42);
        Assert.Equal(configured, McpClient.NormalizeToolResponseTimeout(configured));
    }
}

