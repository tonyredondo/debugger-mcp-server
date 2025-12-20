using DebuggerMcp.Analysis;
using DebuggerMcp.Reporting;
using Xunit;

namespace DebuggerMcp.Tests.Reporting;

public class ReportServiceAdditionalCoverageTests
{
    [Theory]
    [InlineData(ReportFormat.Json)]
    [InlineData(ReportFormat.Markdown)]
    [InlineData(ReportFormat.Html)]
    public void GenerateReport_WithFormatOverload_ReturnsContent(ReportFormat format)
    {
        var service = new ReportService();
        var analysis = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { CrashType = "Test", Description = "desc" }
        };

        var content = service.GenerateReport(analysis, format, dumpId: "d1", userId: "u1", debuggerType: "LLDB");

        Assert.False(string.IsNullOrWhiteSpace(content));
    }
}

