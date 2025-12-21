#nullable enable

using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Reporting;

namespace DebuggerMcp.Tests.Analysis;

public class AiAnalysisResultSerializationTests
{
    [Fact]
    public void JsonReportGenerator_WritesAiAnalysisSummary_AsSummaryProperty()
    {
        var analysis = new CrashAnalysisResult
        {
            AiAnalysis = new AiAnalysisResult
            {
                RootCause = "rc",
                Summary = new AiSummaryResult
                {
                    Description = "desc",
                    Recommendations = ["r1"]
                }
            }
        };

        var generator = new JsonReportGenerator();
        var json = generator.Generate(analysis, ReportOptions.FullReport, new ReportMetadata());

        using var doc = JsonDocument.Parse(json);
        var ai = doc.RootElement.GetProperty("analysis").GetProperty("aiAnalysis");

        Assert.True(ai.TryGetProperty("summary", out _));
        Assert.False(ai.TryGetProperty("summaryRewrite", out _));
    }

    [Fact]
    public void AiAnalysisResult_DeserializesSummaryRewriteAlias_AndSerializesAsSummary()
    {
        const string inputJson = """
{
  "rootCause": "rc",
  "summaryRewrite": {
    "description": "desc",
    "recommendations": ["r1"]
  }
}
""";

        var parsed = JsonSerializer.Deserialize<AiAnalysisResult>(inputJson);
        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Summary);
        Assert.Equal("desc", parsed.Summary!.Description);

        var roundTrip = JsonSerializer.Serialize(parsed);
        using var doc = JsonDocument.Parse(roundTrip);

        Assert.True(doc.RootElement.TryGetProperty("summary", out _));
        Assert.False(doc.RootElement.TryGetProperty("summaryRewrite", out _));
    }
}

