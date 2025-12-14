using DebuggerMcp.Analysis;
using DebuggerMcp.Reporting;
using Xunit;

namespace DebuggerMcp.Tests.Reporting;

/// <summary>
/// Tests for <see cref="ReportEnrichment"/>.
/// </summary>
public class ReportEnrichmentTests
{
    [Fact]
    public void ApplySecurity_WithNullSecurity_DoesNotSetSecurity()
    {
        var analysis = new CrashAnalysisResult();

        ReportEnrichment.ApplySecurity(analysis, securityResult: null);

        Assert.Null(analysis.Security);
    }

    [Fact]
    public void ApplySecurity_WithVulnerability_MapsFields()
    {
        var analysis = new CrashAnalysisResult();

        var security = new SecurityAnalysisResult
        {
            Summary = "summary",
            OverallRisk = SecurityRisk.Critical,
            AnalyzedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Recommendations = new() { "r1" },
            Vulnerabilities =
            {
                new Vulnerability
                {
                    Type = VulnerabilityType.BufferOverflow,
                    Severity = VulnerabilitySeverity.Critical,
                    Description = "desc",
                    Details = "details",
                    Address = "0x123"
                }
            }
        };

        ReportEnrichment.ApplySecurity(analysis, security);

        Assert.NotNull(analysis.Security);
        Assert.True(analysis.Security!.HasVulnerabilities);
        Assert.Equal("Critical", analysis.Security.OverallRisk);
        Assert.Equal("summary", analysis.Security.Summary);
        Assert.Equal("2024-01-02T03:04:05.0000000Z", analysis.Security.AnalyzedAt);
        Assert.Equal(new[] { "r1" }, analysis.Security.Recommendations);

        var finding = Assert.Single(analysis.Security.Findings!);
        Assert.Equal("BufferOverflow", finding.Type);
        Assert.Equal("Critical", finding.Severity);
        Assert.Equal("desc", finding.Description);
        Assert.Equal("0x123", finding.Location);
        Assert.Equal("details", finding.Recommendation);
    }
}
