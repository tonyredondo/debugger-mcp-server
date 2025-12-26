using System.Text.Json.Nodes;
using DebuggerMcp.Analysis;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

public class AiAnalysisDiskCacheTests
{
    [Fact]
    public async Task WriteThenRead_RoundTrips_WhenRequirementsAreSatisfied()
    {
        var root = CreateTempDirectory();
        var cache = new AiAnalysisDiskCache(root, NullLogger<AiAnalysisDiskCache>.Instance);
        var llmKey = AiAnalysisDiskCacheLlmKey.TryCreate("openai", "gpt-test", "medium");
        Assert.NotNull(llmKey);

        var metadata = new AiAnalysisDiskCacheMetadata
        {
            GeneratedAtUtc = DateTime.UtcNow,
            IncludesWatches = true,
            IncludesSecurity = true,
            MaxStackFrames = 0,
            IncludesAiAnalysis = true,
            Model = "gpt-test"
        };

        const string reportJson = "{\"metadata\":{\"dumpId\":\"dump-1\",\"userId\":\"user\"},\"analysis\":{\"aiAnalysis\":{\"rootCause\":\"cached\"}}}";

        await cache.WriteAsync("user", "dump-1", llmKey, metadata, reportJson);

        var read = await cache.TryReadAsync("user", "dump-1", llmKey, requireWatches: true, requireSecurity: true, requireAllFrames: true);

        Assert.NotNull(read);
        Assert.Equal(reportJson, read!.ReportJson);
        Assert.Equal("dump-1", read.Metadata.DumpId);
        Assert.Equal("user", read.Metadata.UserId);
        Assert.True(read.Metadata.IncludesAiAnalysis);
        Assert.Equal("gpt-test", read.Metadata.Model);
    }

    [Fact]
    public async Task TryRead_ReturnsNull_WhenRequirementsAreNotSatisfied()
    {
        var root = CreateTempDirectory();
        var cache = new AiAnalysisDiskCache(root, NullLogger<AiAnalysisDiskCache>.Instance);

        var metadata = new AiAnalysisDiskCacheMetadata
        {
            GeneratedAtUtc = DateTime.UtcNow,
            IncludesWatches = false,
            IncludesSecurity = true,
            MaxStackFrames = 0,
            IncludesAiAnalysis = true
        };

        await cache.WriteAsync("user", "dump-1", llmKey: null, metadata, "{\"metadata\":{},\"analysis\":{\"aiAnalysis\":{}}}");

        var read = await cache.TryReadAsync("user", "dump-1", llmKey: null, requireWatches: true, requireSecurity: false, requireAllFrames: true);

        Assert.Null(read);
    }

    [Fact]
    public async Task TryRead_ReturnsNull_WhenSchemaVersionDoesNotMatch()
    {
        var root = CreateTempDirectory();
        var cache = new AiAnalysisDiskCache(root, NullLogger<AiAnalysisDiskCache>.Instance);

        await cache.WriteAsync(
            "user",
            "dump-1",
            llmKey: null,
            new AiAnalysisDiskCacheMetadata
            {
                GeneratedAtUtc = DateTime.UtcNow,
                IncludesWatches = true,
                IncludesSecurity = true,
                MaxStackFrames = 0,
                IncludesAiAnalysis = true
            },
            "{\"metadata\":{},\"analysis\":{\"aiAnalysis\":{}}}");

        var metadataPath = Path.Combine(root, "user", "dump-1", "ai-analysis", "report.meta.json");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath)) as JsonObject;
        Assert.NotNull(node);

        node!["schemaVersion"] = 999;
        await File.WriteAllTextAsync(metadataPath, node.ToJsonString(new() { WriteIndented = true }));

        var read = await cache.TryReadAsync("user", "dump-1", llmKey: null, requireWatches: true, requireSecurity: true, requireAllFrames: true);

        Assert.Null(read);
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dbg-mcp-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
