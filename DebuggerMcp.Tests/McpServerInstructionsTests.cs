using DebuggerMcp;
using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for MCP server initialization instructions.
/// </summary>
public class McpServerInstructionsTests
{
    [Fact]
    public void ServerInstructions_ContainCanonicalToolReference()
    {
        Assert.Contains("debugger://mcp-tools", McpServerInstructions.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerInstructions_RecommendJsonReports()
    {
        Assert.Contains("format=\"json\"", McpServerInstructions.Text, StringComparison.Ordinal);
    }
}

