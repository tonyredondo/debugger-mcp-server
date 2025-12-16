using System.Linq;
using System.Text.Json;
using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

[Collection("NonParallelConsole")]
public class LlmReportCacheTests
{
    [Fact]
    public void LooksLikeDebuggerMcpReport_MinimalSchema_ReturnsTrue()
    {
        var json = """
        {
          "metadata": { "dumpId": "abc" },
          "analysis": { "environment": { "os": "linux" } }
        }
        """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.True(LlmReportCache.LooksLikeDebuggerMcpReport(stream));
    }

    [Fact]
    public void ExtractAndLoad_LargeReport_UsesCacheAndExcludesBiasedFields()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(tempRoot, "cache");
        Directory.CreateDirectory(tempRoot);

        var reportPath = Path.Combine(tempRoot, "big-report.json");

        var report = new
        {
            metadata = new { dumpId = "d1", generatedAt = "2025-01-01T00:00:00Z" },
            analysis = new
            {
                environment = new { os = "linux", arch = "x64" },
                symbols = new { sourcelink = new { resolvedCount = 1, unresolvedCount = 2 } },
                threads = new
                {
                    summary = new { osThreadCount = 3 },
                    faultingThread = new { threadId = "1", callStack = new[] { new { frameNumber = 0, function = "Foo" } } },
                    all = Enumerable.Range(0, 50).Select(i => new { threadId = i.ToString(), callStack = new[] { new { frameNumber = i, function = new string('x', 50) } } }).ToArray()
                },
                // Fields we explicitly do NOT want in the summary to avoid bias.
                recommendations = new[] { "do X" },
                rootCause = new { title = "maybe" },
                findings = new[] { new { title = "finding" } },
                summary = new { title = "biased summary" },
                pad = new string('p', 25_000)
            }
        };

        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        // Force cache path using a realistic per-file cap (>= 4096 to satisfy Utf8JsonWriter buffering).
        var (cleaned, attachments, reports) = LlmFileAttachments.ExtractAndLoad(
            $"Analyze #./{Path.GetFileName(reportPath)} please",
            baseDirectory: tempRoot,
            maxBytesPerFile: 10_000,
            maxTotalBytes: 10_000,
            cacheRootDirectory: cacheRoot);

        Assert.Contains("(<attached:", cleaned);
        Assert.Empty(attachments);
        Assert.Single(reports);

        var cached = reports[0].CachedReport;
        Assert.True(Directory.Exists(cached.CacheDirectory));
        Assert.True(cached.Sections.Count > 0);

        // Summary should not include biased fields.
        Assert.DoesNotContain("\"recommendations\"", cached.SummaryJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"rootCause\"", cached.SummaryJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"findings\"", cached.SummaryJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("biased summary", cached.SummaryJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReportTools_FindAndGet_ReturnExpectedSections()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(tempRoot, "cache");
        Directory.CreateDirectory(tempRoot);

        var reportPath = Path.Combine(tempRoot, "report.json");
        // Make the report large enough to trigger caching and also large enough to force splitting.
        var longFunction = new string('x', 15_000);
        File.WriteAllText(reportPath, """
        {
          "metadata": { "dumpId": "d1" },
          "analysis": {
            "environment": { "os": "linux" },
            "threads": {
              "faultingThread": {
                "threadId": "7",
                "callStack": [ { "function": "FUNCTION_PLACEHOLDER" } ]
              }
            },
            "pad": "PAD_PLACEHOLDER"
          }
        }
        """
            .Replace("FUNCTION_PLACEHOLDER", longFunction)
            .Replace("PAD_PLACEHOLDER", new string('p', 30_000)));

        var (_, _, reports) = LlmFileAttachments.ExtractAndLoad(
            $"Look #./{Path.GetFileName(reportPath)}",
            baseDirectory: tempRoot,
            maxBytesPerFile: 10_000,
            maxTotalBytes: 10_000,
            cacheRootDirectory: cacheRoot);

        var findCall = new ChatToolCall("1", "find_report_sections", "{\"query\":\"faultingThread\"}");
        var findResult = await LlmReportAgentTools.ExecuteAsync(findCall, reports, CancellationToken.None);
        using (var doc = JsonDocument.Parse(findResult))
        {
            Assert.True(doc.RootElement.TryGetProperty("matchCount", out var matchCount));
            Assert.True(matchCount.GetInt32() > 0);
        }

        var reportCtx = Assert.Single(reports);

        // Prefer fetching the leaf threadId section if it exists; otherwise fetch the parent object and validate threadId there.
        var leafPointer = reportCtx.CachedReport.Sections
            .Select(s => s.JsonPointer)
            .FirstOrDefault(p =>
                p.Contains("/analysis/threads/faultingThread", StringComparison.Ordinal) &&
                p.EndsWith("/threadId", StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(leafPointer))
        {
            Assert.True(reportCtx.PointerToFile.TryGetValue(leafPointer, out var sectionPath));
            var sectionText = File.ReadAllText(sectionPath);
            using (var sectionDoc = JsonDocument.Parse(sectionText))
            {
                Assert.Equal(JsonValueKind.String, sectionDoc.RootElement.ValueKind);
                Assert.Equal("7", sectionDoc.RootElement.GetString());
            }

            var getLeaf = new ChatToolCall("2", "get_report_section", $"{{\"jsonPointer\":\"{leafPointer}\"}}");
            var leafResult = await LlmReportAgentTools.ExecuteAsync(getLeaf, reports, CancellationToken.None);
            using var doc = JsonDocument.Parse(leafResult);
            Assert.True(doc.RootElement.TryGetProperty("content", out var content));
            Assert.Equal(JsonValueKind.String, content.ValueKind);
            Assert.Equal("7", content.GetString());
        }
        else
        {
            var getParent = new ChatToolCall("2", "get_report_section", "{\"jsonPointer\":\"/analysis/threads/faultingThread\"}");
            var parentResult = await LlmReportAgentTools.ExecuteAsync(getParent, reports, CancellationToken.None);
            using var doc = JsonDocument.Parse(parentResult);
            Assert.True(doc.RootElement.TryGetProperty("content", out var content));
            Assert.Equal(JsonValueKind.Object, content.ValueKind);
            Assert.True(content.TryGetProperty("threadId", out var threadId));
            Assert.Equal("7", threadId.GetString());
        }
    }
}
