using DebuggerMcp;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for parsing and cleaning LLDB output.
/// </summary>
public class LldbManagerOutputParsingTests
{
    [Fact]
    public void TransformSosCommand_RemovesBangPrefix()
    {
        Assert.Equal("pe", LldbManager.TransformSosCommand("!pe"));
        Assert.Equal("dumpheap -stat", LldbManager.TransformSosCommand("!dumpheap -stat"));
        Assert.Equal("bt", LldbManager.TransformSosCommand("bt"));
    }

    [Fact]
    public void CleanCommandOutput_RemovesEchoSentinelAndPrompt()
    {
        var raw = "(lldb) bt\nframe #0: 0x0000\nframe #1: 0x0001\n(lldb) ---MCP-END---\n";
        var cleaned = LldbManager.CleanCommandOutput(raw);

        Assert.DoesNotContain("(lldb)", cleaned);
        Assert.DoesNotContain("---MCP-END---", cleaned);
        Assert.Contains("frame #0", cleaned);
        Assert.Contains("frame #1", cleaned);
    }

    [Fact]
    public void CleanCommandOutput_WhenNoSentinel_KeepsAllOutputAfterEcho()
    {
        var raw = "(lldb) image list\n[  0] ...\n[  1] ...\n(lldb)";
        var cleaned = LldbManager.CleanCommandOutput(raw);

        Assert.DoesNotContain("(lldb)", cleaned);
        Assert.Contains("[  0]", cleaned);
        Assert.Contains("[  1]", cleaned);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\n")]
    public void CleanCommandOutput_WithEmpty_ReturnsEmpty(string raw)
    {
        Assert.Equal(string.Empty, LldbManager.CleanCommandOutput(raw));
    }

    [Fact]
    public void ContainsLldbCrashIndicators_ReturnsTrueForBugReportSignature()
    {
        Assert.True(LldbManager.ContainsLldbCrashIndicators("PLEASE submit a bug report"));
        Assert.True(LldbManager.ContainsLldbCrashIndicators("Segmentation fault"));
    }

    [Fact]
    public void ContainsLldbCrashIndicators_ReturnsFalseForNormalOutput()
    {
        Assert.False(LldbManager.ContainsLldbCrashIndicators("frame #0: 0x0000"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ContainsLldbCrashIndicators_WithEmpty_ReturnsFalse(string output)
    {
        Assert.False(LldbManager.ContainsLldbCrashIndicators(output));
    }
}
