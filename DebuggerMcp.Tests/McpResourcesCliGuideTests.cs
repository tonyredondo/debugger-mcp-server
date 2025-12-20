using DebuggerMcp;
using Xunit;

namespace DebuggerMcp.Tests;

public class McpResourcesCliGuideTests
{
    [Fact]
    public void GetCliGuide_ReturnsNonEmptyContent()
    {
        var content = DebuggerResources.GetCliGuide();

        Assert.False(string.IsNullOrWhiteSpace(content));
    }
}

