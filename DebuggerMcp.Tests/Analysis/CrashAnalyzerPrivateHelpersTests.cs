using System.Reflection;
using DebuggerMcp.Analysis;
using Moq;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Reflection-driven coverage tests for private parsing helpers in <see cref="CrashAnalyzer"/>.
/// </summary>
public class CrashAnalyzerPrivateHelpersTests
{
    private static StackFrame? ParseSingleFrame(CrashAnalyzer analyzer, string line)
    {
        var method = typeof(CrashAnalyzer).GetMethod("ParseSingleFrame", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (StackFrame?)method!.Invoke(analyzer, new object[] { line });
    }

    [Fact]
    public void ParseSingleFrame_WithBacktickAndSp_ParsesModuleFunctionAndSource()
    {
        var mockManager = new Mock<IDebuggerManager>();
        var analyzer = new CrashAnalyzer(mockManager.Object);

        var frame = ParseSingleFrame(
            analyzer,
            "  * frame #0: 0x0000000100000000 SP=0x0000000000001000 MyApp`Main + 12 at /src/app.cs:42");

        Assert.NotNull(frame);
        Assert.Equal(0, frame!.FrameNumber);
        Assert.Equal("0x0000000100000000", frame.InstructionPointer);
        Assert.Equal("0x0000000000001000", frame.StackPointer);
        Assert.Equal("MyApp", frame.Module);
        Assert.Contains("Main", frame.Function);
        Assert.Contains("/src/app.cs:42", frame.Source);
    }

    [Fact]
    public void ParseSingleFrame_WithoutBacktick_ParsesNativeLibraryFrame()
    {
        var mockManager = new Mock<IDebuggerManager>();
        var analyzer = new CrashAnalyzer(mockManager.Object);

        var frame = ParseSingleFrame(
            analyzer,
            "    frame #1: 0x0000000100000100 SP=0x0000000000002000 libstdc++.so.6 + 123 at /usr/lib/libstdc++.so.6");

        Assert.NotNull(frame);
        Assert.Equal(1, frame!.FrameNumber);
        Assert.Equal("libstdc++.so.6", frame.Module);
        Assert.False(frame.IsManaged);
    }

    [Fact]
    public void ParseSingleFrame_SpOnlyFormat_ParsesAsManagedJitFrame()
    {
        var mockManager = new Mock<IDebuggerManager>();
        var analyzer = new CrashAnalyzer(mockManager.Object);

        var frame = ParseSingleFrame(
            analyzer,
            "    frame #2: 0x0000000100000200 SP=0x0000000000003000");

        Assert.NotNull(frame);
        Assert.Equal(2, frame!.FrameNumber);
        Assert.True(frame.IsManaged);
        Assert.Contains("[JIT Code", frame.Function);
        Assert.Equal("0x0000000000003000", frame.StackPointer);
    }

    [Fact]
    public void ParseSingleFrame_JitFrameWithRepeatedAddress_ParsesAsManagedJitFrame()
    {
        var mockManager = new Mock<IDebuggerManager>();
        var analyzer = new CrashAnalyzer(mockManager.Object);

        var frame = ParseSingleFrame(
            analyzer,
            "    frame #3: 0x0000000100000300 0x0000000100000300");

        Assert.NotNull(frame);
        Assert.Equal(3, frame!.FrameNumber);
        Assert.True(frame.IsManaged);
        Assert.Null(frame.StackPointer);
    }

    [Fact]
    public void ParseSingleFrame_SimpleFormatWithSpAndSource_ParsesSourceAndCleansOffset()
    {
        var mockManager = new Mock<IDebuggerManager>();
        var analyzer = new CrashAnalyzer(mockManager.Object);

        var frame = ParseSingleFrame(
            analyzer,
            "    frame #5: 0x0000000100000500 SP=0x0000000000005000 someSymbol + 7 at /src/file.c:12");

        Assert.NotNull(frame);
        Assert.Equal(5, frame!.FrameNumber);
        Assert.Equal("0x0000000000005000", frame.StackPointer);
        Assert.Equal(string.Empty, frame.Module);
        Assert.Equal("[someSymbol]", frame.Function);
        Assert.Equal("/src/file.c:12", frame.Source);
    }
}
