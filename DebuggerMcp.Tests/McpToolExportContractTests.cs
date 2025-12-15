using System.Reflection;
using DebuggerMcp.McpTools;
using ModelContextProtocol.Server;
using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Contract tests for the exported MCP tool surface.
/// </summary>
public class McpToolExportContractTests
{
    [Fact]
    public void ExportedMcpTools_AreCompactAndStable()
    {
        var assembly = typeof(CompactTools).Assembly;

        var toolTypes = assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .ToList();

        Assert.Single(toolTypes);
        Assert.Contains(typeof(CompactTools), toolTypes);

        var exportedTools = toolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a != null)
            .Select(a => a!.Name ?? string.Empty)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var expected = new[]
        {
            "analyze",
            "compare",
            "datadog_symbols",
            "dump",
            "exec",
            "inspect",
            "report",
            "session",
            "source_link",
            "symbols",
            "watch"
        }.OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(expected, exportedTools);
        Assert.True(exportedTools.Count <= 15, $"Expected <= 15 exported tools, found {exportedTools.Count}");
    }
}

