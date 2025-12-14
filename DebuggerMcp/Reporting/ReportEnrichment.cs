using System;
using System.Linq;
using DebuggerMcp.Analysis;

namespace DebuggerMcp.Reporting;

/// <summary>
/// Shared helpers for enriching report analysis results (security, metadata, etc.).
/// </summary>
internal static class ReportEnrichment
{
    /// <summary>
    /// Populates <see cref="CrashAnalysisResult.Security"/> from a <see cref="SecurityAnalysisResult"/>.
    /// </summary>
    /// <param name="analysisResult">The crash analysis result to enrich.</param>
    /// <param name="securityResult">The security analysis result.</param>
    internal static void ApplySecurity(CrashAnalysisResult analysisResult, SecurityAnalysisResult? securityResult)
    {
        if (analysisResult == null)
        {
            throw new ArgumentNullException(nameof(analysisResult));
        }

        if (securityResult == null)
        {
            return;
        }

        analysisResult.Security = new SecurityInfo
        {
            HasVulnerabilities = securityResult.Vulnerabilities?.Count > 0,
            OverallRisk = securityResult.OverallRisk.ToString(),
            Summary = securityResult.Summary,
            AnalyzedAt = securityResult.AnalyzedAt.ToString("O"),
            Findings = securityResult.Vulnerabilities?.Select(v => new SecurityFinding
            {
                Type = v.Type.ToString(),
                Severity = v.Severity.ToString(),
                Description = v.Description,
                Location = v.Address,
                Recommendation = v.Details
            }).ToList(),
            Recommendations = securityResult.Recommendations
        };
    }
}

