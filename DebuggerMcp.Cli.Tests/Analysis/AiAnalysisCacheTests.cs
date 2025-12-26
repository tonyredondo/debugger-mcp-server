using DebuggerMcp.Cli.Analysis;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Tests.Collections;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Analysis;

[Collection(nameof(EnvironmentVariableCollection))]
public class AiAnalysisCacheTests
{
    [Fact]
    public async Task WriteAsync_ThenTryReadAsync_ReturnsJson()
    {
        var tempDir = CreateTempDirectory();
        var cache = new AiAnalysisCache(tempDir);
        var llm = new LlmSettings
        {
            Provider = "openai",
            OpenAiModel = "gpt-5.2",
            OpenAiReasoningEffort = "medium"
        };

        var key = AiAnalysisCacheKey.Create("dump-123", llm);
        const string json = "{\"metadata\":{\"dumpId\":\"dump-123\"},\"analysis\":{\"aiAnalysis\":{\"rootCause\":\"ok\"}}}";

        await cache.WriteAsync(key, json);
        var roundTrip = await cache.TryReadAsync(key);

        Assert.Equal(json, roundTrip);
    }

    [Theory]
    [InlineData("openrouter/auto")]
    [InlineData("openai/gpt-5.2")]
    [InlineData("claude-3-5-sonnet-20240620")]
    public void GetCacheFilePath_ProducesStableFileName(string model)
    {
        var tempDir = CreateTempDirectory();
        var cache = new AiAnalysisCache(tempDir);
        var llm = new LlmSettings
        {
            Provider = "openrouter",
            OpenRouterModel = model,
            OpenRouterReasoningEffort = "high"
        };

        var key = AiAnalysisCacheKey.Create("2b31d5d1-9fc9-4928-9f5f-1529207126aa", llm);
        var path = cache.GetCacheFilePath(key);

        Assert.Contains("ai-analysis", path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".json", path, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dbg-mcp-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

