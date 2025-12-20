using System.Text.Json;
using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

[Collection("NonParallelConsole")]
public class LlmReportAgentToolsTests
{
    [Fact]
    public void IsReportTool_RecognizesReportTools()
    {
        Assert.True(LlmReportAgentTools.IsReportTool("find_report_sections"));
        Assert.True(LlmReportAgentTools.IsReportTool("get_report_section"));
        Assert.False(LlmReportAgentTools.IsReportTool("exec"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenToolUnknown_ReturnsError()
    {
        var call = new ChatToolCall("c1", "unknown", "{}");
        var result = await LlmReportAgentTools.ExecuteAsync(call, reports: [], CancellationToken.None);
        Assert.Contains("Unknown report tool", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Find_WhenNoReportsAttached_ReturnsError()
    {
        var call = new ChatToolCall("c1", "find_report_sections", "{\"query\":\"threads\"}");
        var result = await LlmReportAgentTools.ExecuteAsync(call, reports: [], CancellationToken.None);
        Assert.Contains("No reports are attached", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Find_WhenArgsInvalidJson_ReturnsError()
    {
        using var dir = new TempDirectory();
        var report = CreateReport(dir.Path, "r1.json");

        var call = new ChatToolCall("c1", "find_report_sections", "{not-json");
        var result = await LlmReportAgentTools.ExecuteAsync(call, [report], CancellationToken.None);
        Assert.Contains("Invalid tool arguments", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Find_WhenArgsNotObject_ReturnsError()
    {
        using var dir = new TempDirectory();
        var report = CreateReport(dir.Path, "r1.json");

        var call = new ChatToolCall("c1", "find_report_sections", "[]");
        var result = await LlmReportAgentTools.ExecuteAsync(call, [report], CancellationToken.None);
        Assert.Contains("must be a JSON object", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Find_WhenQueryMissing_ReturnsError()
    {
        using var dir = new TempDirectory();
        var report = CreateReport(dir.Path, "r1.json");

        var call = new ChatToolCall("c1", "find_report_sections", "{}");
        var result = await LlmReportAgentTools.ExecuteAsync(call, [report], CancellationToken.None);
        Assert.Contains("query is required", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Get_WhenMissingSelectorAndMultipleReports_ReturnsError()
    {
        using var dir = new TempDirectory();
        var report1 = CreateReport(dir.Path, "r1.json");
        var report2 = CreateReport(dir.Path, "r2.json");

        var call = new ChatToolCall("c1", "get_report_section", "{\"sectionId\":\"analysis.exception\"}");
        var result = await LlmReportAgentTools.ExecuteAsync(call, [report1, report2], CancellationToken.None);
        Assert.Contains("Multiple reports are attached", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Find_WhenReportSelectorUnknown_ReturnsErrorWithAvailableReports()
    {
        using var dir = new TempDirectory();
        var report1 = CreateReport(dir.Path, "r1.json");
        var report2 = CreateReport(dir.Path, "r2.json");

        var call = new ChatToolCall("c1", "find_report_sections", "{\"report\":\"missing.json\",\"query\":\"analysis\"}");
        var result = await LlmReportAgentTools.ExecuteAsync(call, [report1, report2], CancellationToken.None);

        Assert.Contains("Unknown report", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("r1.json", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("r2.json", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Find_ReturnsMatchesAndRespectsMaxResultsClamp()
    {
        using var dir = new TempDirectory();
        var report = CreateReport(dir.Path, "r1.json");

        var call = new ChatToolCall("c1", "find_report_sections", "{\"query\":\"analysis\",\"maxResults\":0}");
        var json = await LlmReportAgentTools.ExecuteAsync(call, [report], CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("matches", out var matches));
        Assert.Equal(JsonValueKind.Array, matches.ValueKind);
        Assert.True(matches.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task ExecuteAsync_Get_WhenMissingSectionIdAndPointer_ReturnsError()
    {
        using var dir = new TempDirectory();
        var report = CreateReport(dir.Path, "r1.json");

        var call = new ChatToolCall("c1", "get_report_section", "{}");
        var result = await LlmReportAgentTools.ExecuteAsync(call, [report], CancellationToken.None);
        Assert.Contains("Provide sectionId or jsonPointer", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Get_WhenSectionFound_ReturnsParsedJsonContent()
    {
        using var dir = new TempDirectory();
        var report = CreateReport(dir.Path, "r1.json");

        var call = new ChatToolCall("c1", "get_report_section", "{\"sectionId\":\"analysis.exception\"}");
        var json = await LlmReportAgentTools.ExecuteAsync(call, [report], CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("truncated", out var truncated));
        Assert.False(truncated.GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("content", out var content));
        Assert.Equal("world", content.GetProperty("hello").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_Get_WhenUsingJsonPointer_SelectsSection()
    {
        using var dir = new TempDirectory();
        var report = CreateReport(dir.Path, "r1.json");

        var call = new ChatToolCall("c1", "get_report_section", "{\"jsonPointer\":\"/analysis/exception\"}");
        var json = await LlmReportAgentTools.ExecuteAsync(call, [report], CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("content", out var content));
        Assert.Equal("world", content.GetProperty("hello").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_Get_WhenSectionMissing_ReturnsNotFoundError()
    {
        using var dir = new TempDirectory();
        var report = CreateReport(dir.Path, "r1.json");

        var call = new ChatToolCall("c1", "get_report_section", "{\"sectionId\":\"analysis.missing\"}");
        var result = await LlmReportAgentTools.ExecuteAsync(call, [report], CancellationToken.None);
        Assert.Contains("Section not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Get_WhenSectionFileOutsideCacheDir_ReturnsError()
    {
        using var dir = new TempDirectory();
        var report = CreateReport(dir.Path, "r1.json");

        var outside = Path.Combine(dir.Path, "outside.json");
        await File.WriteAllTextAsync(outside, "{\"x\":1}");

        // Poison the mapping to point outside the cache directory.
        var poisoned = report with
        {
            SectionIdToFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["analysis.exception"] = outside
            }
        };

        var call = new ChatToolCall("c1", "get_report_section", "{\"sectionId\":\"analysis.exception\"}");
        var result = await LlmReportAgentTools.ExecuteAsync(call, [poisoned], CancellationToken.None);

        Assert.Contains("outside report cache directory", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Get_WhenTruncationBreaksJson_ReturnsFallbackTextObject()
    {
        using var dir = new TempDirectory();
        var report = CreateReport(dir.Path, "r1.json");

        var call = new ChatToolCall("c1", "get_report_section", "{\"sectionId\":\"analysis.exception\",\"maxChars\":25}");
        var json = await LlmReportAgentTools.ExecuteAsync(call, [report], CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("truncated", out var truncated));
        Assert.True(truncated.GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("content", out var content));
        Assert.True(content.TryGetProperty("text", out _));
    }

    private static LlmFileAttachments.ReportAttachmentContext CreateReport(string rootDir, string fileName)
    {
        var reportPath = Path.Combine(rootDir, fileName);
        File.WriteAllText(reportPath, "{}");

        var cacheDir = Path.Combine(rootDir, $"cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheDir);

        var sectionPath = Path.Combine(cacheDir, "analysis.exception.json");
        File.WriteAllText(sectionPath, "{\"hello\":\"world\",\"long\":\"" + new string('x', 2048) + "\"}");

        var sections = new List<LlmReportCache.ReportSection>
        {
            new("analysis.exception", "/analysis/exception", sectionPath, new FileInfo(sectionPath).Length > int.MaxValue ? int.MaxValue : (int)new FileInfo(sectionPath).Length),
            new("analysis.threads", "/analysis/threads", sectionPath, 10)
        };

        var cached = new LlmReportCache.CachedReport(
            cacheDir,
            sections,
            SummaryJson: "{\"ok\":true}",
            ManifestJson: "{\"sections\":2}");

        var sectionIdToFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["analysis.exception"] = sectionPath
        };
        var pointerToFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["/analysis/exception"] = sectionPath
        };

        return new LlmFileAttachments.ReportAttachmentContext(
            DisplayPath: fileName,
            AbsolutePath: reportPath,
            CachedReport: cached,
            MessageForModel: string.Empty,
            SectionIdToFile: sectionIdToFile,
            PointerToFile: pointerToFile);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dbg-mcp-tests-{Guid.NewGuid():N}");

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }
}
