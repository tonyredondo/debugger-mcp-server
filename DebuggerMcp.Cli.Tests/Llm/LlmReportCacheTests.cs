using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public void LooksLikeDebuggerMcpReport_ArbitraryJson_ReturnsFalse()
    {
        var json = """
        {
          "metadata": { "dumpId": "abc" },
          "data": { "environment": { "os": "linux" } }
        }
        """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.False(LlmReportCache.LooksLikeDebuggerMcpReport(stream));
    }

    [Fact]
    public void LooksLikeDebuggerMcpReport_LargePrefixWithLongStringToken_ReturnsTrue()
    {
        // Regression test: signature detection must not fail when a long JSON string token appears before analysis,
        // and would previously be truncated by a small fixed prefix read.
        var pad = new string('x', 900_000);
        var json = $$"""
        {
          "metadata": { "dumpId": "abc", "pad": "{{pad}}" },
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
            $"Analyze @./{Path.GetFileName(reportPath)} please",
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
    public void ExtractAndLoad_LargeReport_WithDuplicateStableKeys_DoesNotThrow()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(tempRoot, "cache");
        Directory.CreateDirectory(tempRoot);

        var reportPath = Path.Combine(tempRoot, "dupe-report.json");
        var large = new string('x', 8_000);
        var report = new
        {
            metadata = new { dumpId = "d1" },
            analysis = new
            {
                environment = new { os = "linux" },
                threads = new
                {
                    all = new[]
                    {
                        new { threadId = "7", note = "first", payload = large },
                        new { threadId = "7", note = "second", payload = large }
                    }
                },
                pad = new string('p', 40_000)
            }
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        var (_, attachments, reports) = LlmFileAttachments.ExtractAndLoad(
            $"Analyze @./{Path.GetFileName(reportPath)} please",
            baseDirectory: tempRoot,
            maxBytesPerFile: 10_000,
            maxTotalBytes: 10_000,
            cacheRootDirectory: cacheRoot);

        Assert.Empty(attachments);
        var ctx = Assert.Single(reports);
        Assert.NotEmpty(ctx.PointerToFile);
        Assert.Contains(ctx.PointerToFile.Keys, p => p.StartsWith("/analysis/threads/all/0", StringComparison.Ordinal));
        Assert.Contains(ctx.PointerToFile.Keys, p => p.StartsWith("/analysis/threads/all/1", StringComparison.Ordinal));
        Assert.True(ctx.CachedReport.Sections.Count > ctx.SectionIdToFile.Count);
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
            $"Look @./{Path.GetFileName(reportPath)}",
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

    [Fact]
    public async Task ReportTools_Get_RefusesOutsideCacheDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(tempRoot, "cache");
        Directory.CreateDirectory(cacheDir);

        var outsidePath = Path.Combine(tempRoot, "outside.json");
        File.WriteAllText(outsidePath, "{\"pwn\":true}");

        var cached = new LlmReportCache.CachedReport(
            CacheDirectory: cacheDir,
            Sections: [],
            SummaryJson: "{}",
            ManifestJson: "{}");

        var reportCtx = new LlmFileAttachments.ReportAttachmentContext(
            DisplayPath: "./report.json",
            AbsolutePath: Path.Combine(tempRoot, "report.json"),
            CachedReport: cached,
            MessageForModel: string.Empty,
            SectionIdToFile: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["evil"] = outsidePath },
            PointerToFile: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        var call = new ChatToolCall("1", "get_report_section", "{\"sectionId\":\"evil\"}");
        var result = await LlmReportAgentTools.ExecuteAsync(call, [reportCtx], CancellationToken.None);

        Assert.Contains("Refusing to read section outside report cache directory", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildOrLoadCachedReport_IgnoresManifestEntriesWithPathTraversal()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(tempRoot, "cache-root");
        Directory.CreateDirectory(tempRoot);

        var reportPath = Path.Combine(tempRoot, "report.json");
        File.WriteAllText(reportPath, """
        {
          "metadata": { "dumpId": "d1" },
          "analysis": {
            "environment": { "os": "linux" },
            "threads": { "faultingThread": { "threadId": "7", "callStack": [ { "function": "f" } ] } },
            "pad": "PAD_PLACEHOLDER"
          }
        }
        """.Replace("PAD_PLACEHOLDER", new string('p', 50_000)));

        var cached1 = LlmReportCache.BuildOrLoadCachedReport(reportPath, cacheRoot, maxSectionBytes: 10_000);
        Assert.True(Directory.Exists(cached1.CacheDirectory));

        var manifestPath = Path.Combine(cached1.CacheDirectory, "manifest.json");
        var summaryPath = Path.Combine(cached1.CacheDirectory, "summary.json");
        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(summaryPath));

        var outsideFile = Path.Combine(cached1.CacheDirectory, "..", "outside.json");
        File.WriteAllText(outsideFile, "{\"pwn\":true}");

        var manifestNode = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var sections = manifestNode["sections"]!.AsArray();
        sections.Add(new JsonObject
        {
            ["id"] = "evil",
            ["pointer"] = "/analysis/evil",
            ["file"] = "../outside.json",
            ["sizeBytes"] = 13
        });
        File.WriteAllText(manifestPath, manifestNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var cached2 = LlmReportCache.BuildOrLoadCachedReport(reportPath, cacheRoot, maxSectionBytes: 10_000);

        Assert.DoesNotContain(cached2.Sections, s => string.Equals(Path.GetFileName(s.FilePath), "outside.json", StringComparison.OrdinalIgnoreCase));

        var cacheDirFull = Path.GetFullPath(cached2.CacheDirectory);
        foreach (var section in cached2.Sections)
        {
            var sectionPath = Path.GetFullPath(section.FilePath);
            var rel = Path.GetRelativePath(cacheDirFull, sectionPath);
            Assert.False(Path.IsPathRooted(rel));
            Assert.False(rel.Equals("..", StringComparison.Ordinal));
            Assert.False(rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            Assert.False(rel.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void LooksLikeDebuggerMcpReport_WhenFileMissing_ReturnsFalse()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-report.json");
        Assert.False(LlmReportCache.LooksLikeDebuggerMcpReport(missingPath));
    }

    [Fact]
    public void BuildOrLoadCachedReport_WhenPrimitiveValueExceedsCap_WritesTruncatedPlaceholderSection()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(tempRoot, "cache-root");
        Directory.CreateDirectory(tempRoot);

        var reportPath = Path.Combine(tempRoot, "report.json");
        var pad = new string('p', 50_000);
        File.WriteAllText(reportPath, $$"""
        {
          "metadata": { "dumpId": "d1" },
          "analysis": {
            "environment": { "os": "linux", "pad": "{{pad}}" }
          }
        }
        """);

        var cached = LlmReportCache.BuildOrLoadCachedReport(reportPath, cacheRoot, maxSectionBytes: 10_000);

        var padSection = Assert.Single(cached.Sections, s => s.JsonPointer == "/analysis/environment/pad");
        Assert.True(File.Exists(padSection.FilePath));

        using var doc = JsonDocument.Parse(File.ReadAllText(padSection.FilePath));
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal("/analysis/environment/pad", doc.RootElement.GetProperty("jsonPointer").GetString());
    }

    [Fact]
    public void BuildOrLoadCachedReport_WhenArrayContainsPrimitives_UsesIndexKeysForChildren()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(tempRoot, "cache-root");
        Directory.CreateDirectory(tempRoot);

        var reportPath = Path.Combine(tempRoot, "report.json");
        var numbers = string.Join(", ", Enumerable.Range(0, 200));
        File.WriteAllText(reportPath, $$"""
        {
          "metadata": { "dumpId": "d1" },
          "analysis": {
            "environment": { "os": "linux" },
            "numbers": [ {{numbers}} ]
          }
        }
        """);

        var cached = LlmReportCache.BuildOrLoadCachedReport(reportPath, cacheRoot, maxSectionBytes: 1_000);

        Assert.Contains(cached.Sections, s => s.JsonPointer == "/analysis/numbers/0");
        Assert.Contains(cached.Sections, s => s.JsonPointer == "/analysis/numbers/199");
        Assert.Contains(cached.Sections, s => s.SectionId.Contains("analysis.numbers[0]", StringComparison.Ordinal));
        Assert.Contains(cached.Sections, s => s.SectionId.Contains("analysis.numbers[199]", StringComparison.Ordinal));
    }
}
