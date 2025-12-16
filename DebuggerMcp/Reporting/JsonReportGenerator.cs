using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Serialization;

namespace DebuggerMcp.Reporting;

/// <summary>
/// Generates the canonical JSON report document.
/// </summary>
public sealed class JsonReportGenerator : IReportGenerator
{
    /// <inheritdoc />
    public string Generate(CrashAnalysisResult analysis, ReportOptions options, ReportMetadata metadata)
    {
        if (analysis == null)
        {
            throw new ArgumentNullException(nameof(analysis));
        }

        options ??= ReportOptions.FullReport;
        metadata ??= new ReportMetadata();

        // Default-on: include bounded source context snippets and normalize timeline timestamps.
        SourceContextEnricher.Apply(analysis, metadata.GeneratedAt);

        var document = new ReportDocument
        {
            Metadata = metadata,
            Analysis = analysis
        };

        return JsonSerializer.Serialize(document, JsonSerializationDefaults.IndentedIgnoreNull);
    }
}

