using Xunit;

#pragma warning disable CA1416 // Platform compatibility - WinDbgManager is Windows-only

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for WinDbg module list parsing helpers.
/// </summary>
public class WinDbgManagerDotNetModuleListTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsDotNetModuleList_WhenEmpty_ReturnsFalse(string? moduleList, bool expected)
    {
        Assert.Equal(expected, WinDbgManager.IsDotNetModuleList(moduleList));
    }

    [Fact]
    public void IsDotNetModuleList_WhenCoreClrPresent_ReturnsTrue()
    {
        var moduleList = "00007ffa`12340000 00007ffa`12350000 coreclr";

        Assert.True(WinDbgManager.IsDotNetModuleList(moduleList));
    }

    [Fact]
    public void IsDotNetModuleList_WhenClrModulePresent_ReturnsTrue()
    {
        var moduleList = string.Join(
            '\n',
            "start             end                 module name",
            "00007ffa`11110000 00007ffa`22220000 clr",
            "00007ffa`33330000 00007ffa`44440000 ntdll");

        Assert.True(WinDbgManager.IsDotNetModuleList(moduleList));
    }

    [Fact]
    public void IsDotNetModuleList_WhenOnlyAclrPresent_ReturnsFalse()
    {
        var moduleList = string.Join(
            '\n',
            "start             end                 module name",
            "00007ffa`11110000 00007ffa`22220000 aclr",
            "00007ffa`33330000 00007ffa`44440000 ntdll");

        Assert.False(WinDbgManager.IsDotNetModuleList(moduleList));
    }

    [Theory]
    [InlineData("hostfxr", true)]
    [InlineData("hostpolicy", true)]
    [InlineData("System.Private.CoreLib", true)]
    [InlineData("mscorwks", true)]
    public void IsDotNetModuleList_WhenKnownIndicatorsPresent_ReturnsTrue(string indicator, bool expected)
    {
        var moduleList = $"00007ffa`11110000 00007ffa`22220000 {indicator}";

        Assert.Equal(expected, WinDbgManager.IsDotNetModuleList(moduleList));
    }
}

