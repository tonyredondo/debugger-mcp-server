using System.Reflection;
using DebuggerMcp;
using Xunit;

namespace DebuggerMcp.Tests;

public class WinDbgManagerCoverageTests
{
    [Fact]
    public void Properties_ReturnExpectedDefaults()
    {
        using var manager = new WinDbgManager();

        Assert.Equal("WinDbg", manager.DebuggerType);
        Assert.Null(manager.CurrentDumpPath);
        Assert.False(manager.IsInitialized);
    }

    [Fact]
    public async Task InitializeAsync_WhenDbgEngUnavailable_ThrowsInvalidOperationException()
    {
        using var manager = new WinDbgManager();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InitializeAsync());
        Assert.Contains("Failed to initialize WinDbg Manager", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExecuteCommandInternal_WhenNotInitialized_Throws()
    {
        using var manager = new WinDbgManager();

        var method = typeof(WinDbgManager).GetMethod("ExecuteCommandInternal", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var ex = Assert.Throws<TargetInvocationException>(() => method!.Invoke(manager, new object[] { ".echo hi" }));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void DetectDotNetDump_WhenCommandThrows_ReturnsFalse()
    {
        using var manager = new WinDbgManager();

        var method = typeof(WinDbgManager).GetMethod("DetectDotNetDump", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (bool)method!.Invoke(manager, Array.Empty<object>())!;
        Assert.False(result);
    }

    [Fact]
    public async Task DisposeAsync_ReturnsCompletedTask()
    {
        await using var manager = new WinDbgManager();

        await manager.DisposeAsync();
    }
}

