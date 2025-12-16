using System;
using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Reporting;
using Xunit;

namespace DebuggerMcp.Tests.Reporting;

/// <summary>
/// Regression tests for JSON serialization behavior in <see cref="ReportService"/>.
/// </summary>
public class ReportServiceJsonSerializationTests
{
    [Fact]
    public void GenerateReport_WhenFormatIsJson_SerializesCanonicalDocument()
    {
        // Arrange
        var service = new ReportService();
        var analysis = new CrashAnalysisResult
        {
            Summary = null,
            Exception = null,
            Environment = null
        };
        var options = new ReportOptions { Format = ReportFormat.Json };
        var metadata = new ReportMetadata
        {
            DumpId = "dump-1",
            UserId = "user-1",
            DebuggerType = "LLDB",
            GeneratedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            ServerVersion = "1.2.3"
        };

        // Act
        var json = service.GenerateReport(analysis, options, metadata);

        // Assert
        Assert.Contains("\n", json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var metadataElement = root.GetProperty("metadata");
        Assert.Equal("dump-1", metadataElement.GetProperty("dumpId").GetString());
        Assert.Equal("user-1", metadataElement.GetProperty("userId").GetString());
        Assert.Equal("LLDB", metadataElement.GetProperty("debuggerType").GetString());
        Assert.Equal("1.2.3", metadataElement.GetProperty("serverVersion").GetString());
        Assert.Equal("Json", metadataElement.GetProperty("format").GetString());

        var analysisElement = root.GetProperty("analysis");
        Assert.False(analysisElement.TryGetProperty("summary", out _));
        Assert.False(analysisElement.TryGetProperty("exception", out _));
        Assert.False(analysisElement.TryGetProperty("environment", out _));
    }
}
