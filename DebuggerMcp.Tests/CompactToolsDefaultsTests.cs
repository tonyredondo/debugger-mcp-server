using System.Reflection;
using DebuggerMcp.McpTools;
using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for default values on compact tool parameters.
/// </summary>
public class CompactToolsDefaultsTests
{
    [Fact]
    public void Report_Format_DefaultsToJson()
    {
        var reportMethod = typeof(CompactTools).GetMethod(
            nameof(CompactTools.Report),
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.NotNull(reportMethod);

        var formatParameter = reportMethod!.GetParameters().Single(p => p.Name == "format");
        Assert.True(formatParameter.HasDefaultValue);
        Assert.Equal("json", formatParameter.DefaultValue);
    }
}

