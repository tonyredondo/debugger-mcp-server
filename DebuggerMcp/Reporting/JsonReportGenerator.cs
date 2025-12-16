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

        // Canonical enrichment step for the report model.
        // This populates source context snippets and normalizes derived fields so all formats
        // (Markdown/HTML/JSON) stay consistent when rendered from the same JSON document.
        SourceContextEnricher.Apply(analysis, metadata.GeneratedAt);

        var document = new ReportDocument
        {
            Metadata = metadata,
            Analysis = analysis
        };

        return JsonSerializer.Serialize(document, JsonSerializationDefaults.IndentedIgnoreNull);
    }
}
