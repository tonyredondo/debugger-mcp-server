using DebuggerMcp.Analysis;

namespace DebuggerMcp.Reporting;

/// <summary>
/// Interface for report generators that convert crash analysis results to various formats.
/// </summary>
public interface IReportGenerator
{
    /// <summary>
    /// Generates a report from the crash analysis result.
    /// </summary>
    /// <param name="analysis">The crash analysis result to report on.</param>
    /// <param name="options">Options controlling what to include in the report.</param>
    /// <param name="metadata">Metadata about the report (dump ID, timestamps, etc.).</param>
    /// <returns>The generated report content as a string.</returns>
    string Generate(CrashAnalysisResult analysis, ReportOptions options, ReportMetadata metadata);
}

