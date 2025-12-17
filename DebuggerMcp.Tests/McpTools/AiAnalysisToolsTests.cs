using DebuggerMcp.McpTools;
using Xunit;

namespace DebuggerMcp.Tests.McpTools;

public class AiAnalysisToolsTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void ShouldUseDotNetAnalyzer_UsesDotNetWhenSosOrClrMdAvailable(bool isSosLoaded, bool isClrMdOpen, bool expected)
    {
        var actual = AiAnalysisTools.ShouldUseDotNetAnalyzer(isSosLoaded, isClrMdOpen);
        Assert.Equal(expected, actual);
    }
}

