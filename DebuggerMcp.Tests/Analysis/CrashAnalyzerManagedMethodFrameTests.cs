using DebuggerMcp.Analysis;
using Moq;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Regression tests for LLDB placeholder frames in <see cref="CrashAnalyzer"/>.
/// </summary>
public class CrashAnalyzerManagedMethodFrameTests
{
    [Fact]
    public void ParseSingleFrame_WhenFunctionIsManagedMethod_MarksFrameAsManaged()
    {
        var mockManager = new Mock<IDebuggerManager>();
        var analyzer = new CrashAnalyzer(mockManager.Object);

        var method = typeof(CrashAnalyzer).GetMethod("ParseSingleFrame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var frame = (StackFrame?)method!.Invoke(analyzer, new object[] { "    frame #0: 0x0000000100000000 [ManagedMethod]" });

        Assert.NotNull(frame);
        Assert.True(frame!.IsManaged);
        Assert.Equal("[ManagedMethod]", frame.Function);
        Assert.Equal(string.Empty, frame.Module);
    }
}

