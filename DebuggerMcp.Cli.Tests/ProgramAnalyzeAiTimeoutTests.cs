using System.Reflection;
using DebuggerMcp.Cli.Client;

namespace DebuggerMcp.Cli.Tests;

public class ProgramAnalyzeAiTimeoutTests
{
    [Theory]
    [InlineData(5, 60)]
    [InlineData(60, 60)]
    [InlineData(120, 120)]
    public void GetAiAnalyzeToolResponseTimeout_EnforcesMinimumMinutes(int configuredMinutes, int expectedMinutes)
    {
        var method = typeof(DebuggerMcp.Cli.Program).GetMethod(
            "GetAiAnalyzeToolResponseTimeout",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var configured = TimeSpan.FromMinutes(configuredMinutes);
        var result = (TimeSpan)method!.Invoke(null, new object?[] { configured })!;

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), result);
    }

    [Fact]
    public async Task RunWithToolResponseTimeoutAsync_RestoresPreviousTimeout_OnSuccess()
    {
        var method = typeof(DebuggerMcp.Cli.Program).GetMethod(
            "RunWithToolResponseTimeoutAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var client = new McpClient { ToolResponseTimeout = TimeSpan.FromMinutes(5) };
        var expected = TimeSpan.FromMinutes(60);

        Func<Task<string>> action = () =>
        {
            Assert.Equal(expected, client.ToolResponseTimeout);
            return Task.FromResult("ok");
        };

        var task = (Task<string>)method!.Invoke(null, new object?[] { client, expected, action })!;
        var result = await task;

        Assert.Equal("ok", result);
        Assert.Equal(TimeSpan.FromMinutes(5), client.ToolResponseTimeout);
    }

    [Fact]
    public async Task RunWithToolResponseTimeoutAsync_RestoresPreviousTimeout_OnFailure()
    {
        var method = typeof(DebuggerMcp.Cli.Program).GetMethod(
            "RunWithToolResponseTimeoutAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var client = new McpClient { ToolResponseTimeout = TimeSpan.FromMinutes(5) };
        var expected = TimeSpan.FromMinutes(60);

        Func<Task<string>> action = () =>
        {
            Assert.Equal(expected, client.ToolResponseTimeout);
            return Task.FromException<string>(new InvalidOperationException("boom"));
        };

        var task = (Task<string>)method!.Invoke(null, new object?[] { client, expected, action })!;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
        Assert.Equal("boom", ex.Message);

        Assert.Equal(TimeSpan.FromMinutes(5), client.ToolResponseTimeout);
    }
}

