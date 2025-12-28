using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Reporting;
using Xunit;

namespace DebuggerMcp.Tests.Reporting;

/// <summary>
/// Tests for <see cref="JsonReportGenerator"/>.
/// </summary>
public class JsonReportGeneratorTests
{
    [Fact]
    public void Generate_IncludesMetadataAndAnalysis()
    {
        // Arrange
        var generator = new JsonReportGenerator();
        var analysis = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary
            {
                CrashType = "AccessViolation",
                Severity = "critical",
                Recommendations = new() { "Update to latest runtime" }
            }
        };
        var metadata = new ReportMetadata
        {
            DumpId = "dump-123",
            UserId = "user-456",
            GeneratedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Format = ReportFormat.Json,
            DebuggerType = "LLDB",
            SosLoaded = true,
            ServerVersion = "9.9.9"
        };

        // Act
        var json = generator.Generate(analysis, ReportOptions.FullReport, metadata);

        // Assert
        Assert.Contains("\n", json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("metadata", out var metadataElement));
        Assert.Equal("dump-123", metadataElement.GetProperty("dumpId").GetString());
        Assert.Equal("user-456", metadataElement.GetProperty("userId").GetString());
        Assert.Equal("LLDB", metadataElement.GetProperty("debuggerType").GetString());
        Assert.Equal("Json", metadataElement.GetProperty("format").GetString());
        Assert.True(metadataElement.GetProperty("sosLoaded").GetBoolean());

        Assert.True(root.TryGetProperty("analysis", out var analysisElement));
        Assert.Equal("AccessViolation", analysisElement.GetProperty("summary").GetProperty("crashType").GetString());
    }

    [Fact]
    public void Generate_WhenAnalysisHasNullSections_OmitsNullProperties()
    {
        // Arrange
        var generator = new JsonReportGenerator();
        var analysis = new CrashAnalysisResult
        {
            Summary = null,
            Exception = null,
            Environment = null
        };
        var metadata = new ReportMetadata
        {
            DumpId = "dump-1",
            UserId = "user-1",
            Format = ReportFormat.Json,
            DebuggerType = "LLDB"
        };

        // Act
        var json = generator.Generate(analysis, ReportOptions.FullReport, metadata);

        // Assert
        using var doc = JsonDocument.Parse(json);
        var analysisElement = doc.RootElement.GetProperty("analysis");

        Assert.False(analysisElement.TryGetProperty("summary", out _));
        Assert.False(analysisElement.TryGetProperty("exception", out _));
        Assert.False(analysisElement.TryGetProperty("environment", out _));
    }
}
