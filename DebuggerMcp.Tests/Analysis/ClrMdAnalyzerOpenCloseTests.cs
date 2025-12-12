using DebuggerMcp.Analysis;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Tests for basic open/close behavior in <see cref="ClrMdAnalyzer"/> without requiring real dumps.
/// </summary>
public class ClrMdAnalyzerOpenCloseTests
{
    [Fact]
    public void OpenDump_WithMissingFile_ReturnsFalseAndLeavesAnalyzerClosed()
    {
        var analyzer = new ClrMdAnalyzer();

        var ok = analyzer.OpenDump("/path/does/not/exist.dmp");

        Assert.False(ok);
        Assert.False(analyzer.IsOpen);
        Assert.Null(analyzer.Runtime);
    }

    [Fact]
    public void CloseDump_WhenNeverOpened_DoesNotThrow()
    {
        var analyzer = new ClrMdAnalyzer();
        analyzer.CloseDump();
    }

    [Fact]
    public void GetAssemblyAttributes_WhenNotOpen_ReturnsEmpty()
    {
        var analyzer = new ClrMdAnalyzer();

        var attrs = analyzer.GetAssemblyAttributes("DebuggerMcp");

        Assert.NotNull(attrs);
        Assert.Empty(attrs);
    }
}

