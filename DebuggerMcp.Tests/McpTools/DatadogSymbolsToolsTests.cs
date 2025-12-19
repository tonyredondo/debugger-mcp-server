using System.Text.Json;
using DebuggerMcp.McpTools;
using DebuggerMcp.SourceLink;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.Tests.McpTools;

[Collection("NonParallelEnvironment")]
public class DatadogSymbolsToolsTests
{
    [Fact]
    public void GetDatadogSymbolsConfig_ReturnsJson()
    {
        var tools = CreateTools();

        var json = tools.GetDatadogSymbolsConfig();
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("enabled", out _));
        Assert.True(doc.RootElement.TryGetProperty("azureDevOps", out _));
        Assert.True(doc.RootElement.TryGetProperty("environmentVariables", out _));
    }

    [Fact]
    public async Task PrepareDatadogSymbols_WhenDisabled_ReturnsErrorJson()
    {
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", "false");
            var tools = CreateTools();

            var json = await tools.PrepareDatadogSymbols("s", "u");
            using var doc = JsonDocument.Parse(json);

            Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("disabled", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", original);
        }
    }

    [Fact]
    public async Task ListDatadogArtifacts_WhenDisabled_ReturnsErrorJson()
    {
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", "false");
            var tools = CreateTools();

            var json = await tools.ListDatadogArtifacts(commitSha: "deadbeef");
            using var doc = JsonDocument.Parse(json);

            Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("disabled", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", original);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public async Task DownloadDatadogSymbols_WithShortOrMissingSha_ThrowsArgumentException(string? commitSha)
    {
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", "true");
            var tools = CreateTools();

            await Assert.ThrowsAsync<ArgumentException>(
                () => tools.DownloadDatadogSymbols("s", "u", commitSha!, loadIntoDebugger: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", original);
        }
    }

    [Fact]
    public async Task DownloadDatadogSymbols_WhenDisabled_ReturnsErrorJson()
    {
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", "false");
            var tools = CreateTools();

            var json = await tools.DownloadDatadogSymbols("s", "u", commitSha: "deadbeef", loadIntoDebugger: false);
            using var doc = JsonDocument.Parse(json);

            Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("disabled", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", original);
        }
    }

    [Fact]
    public void ClearDatadogSymbols_WhenDatadogDirectoryExists_DeletesFilesAndClearsApiCache()
    {
        var originalCacheDir = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_CACHE_DIR");

        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", nameof(DatadogSymbolsToolsTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var dumpStoragePath = Path.Combine(tempRoot, "dumps");
            var cacheDir = Path.Combine(tempRoot, "cache");
            Directory.CreateDirectory(cacheDir);

            File.WriteAllText(Path.Combine(cacheDir, "azure_pipelines_cache.json"), "{}");
            File.WriteAllText(Path.Combine(cacheDir, "github_releases_cache.json"), "{}");
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_CACHE_DIR", cacheDir);

            var sessionManager = new DebuggerSessionManager(dumpStoragePath: dumpStoragePath);
            var symbolManager = new SymbolManager();
            var watchStore = new WatchStore();
            var tools = new DatadogSymbolsTools(sessionManager, symbolManager, watchStore, NullLogger<DatadogSymbolsTools>.Instance);

            var userId = "u";
            var sessionId = sessionManager.CreateSession(userId);
            var session = sessionManager.GetSessionInfo(sessionId, userId);
            session.CurrentDumpId = "dump1.dmp";
            session.SetCachedReport(session.CurrentDumpId, DateTime.UtcNow, "{ \"report\": 1 }", includesWatches: true, includesSecurity: true, maxStackFrames: 0, includesAiAnalysis: false);
            _ = session.GetOrCreateSourceLinkResolver(Path.GetFileNameWithoutExtension(session.CurrentDumpId),
                () => new SourceLinkResolver(NullLogger.Instance));
            Assert.NotNull(session.SourceLinkResolver);

            var dumpName = Path.GetFileNameWithoutExtension(session.CurrentDumpId);
            var datadogSymbolsDir = Path.Combine(dumpStoragePath, userId, $".symbols_{dumpName}", ".datadog");
            Directory.CreateDirectory(Path.Combine(datadogSymbolsDir, "nested"));
            File.WriteAllText(Path.Combine(datadogSymbolsDir, "a.txt"), "x");
            File.WriteAllText(Path.Combine(datadogSymbolsDir, "nested", "b.txt"), "y");

            var json = tools.ClearDatadogSymbols(sessionId, userId, clearApiCache: true);
            using var doc = JsonDocument.Parse(json);

            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(2, doc.RootElement.GetProperty("filesDeleted").GetInt32());
            Assert.True(doc.RootElement.GetProperty("apiCacheCleared").GetBoolean());

            Assert.False(Directory.Exists(datadogSymbolsDir));
            Assert.False(File.Exists(Path.Combine(cacheDir, "azure_pipelines_cache.json")));
            Assert.False(File.Exists(Path.Combine(cacheDir, "github_releases_cache.json")));

            Assert.Null(session.CachedReportDumpId);
            Assert.Null(session.SourceLinkResolver);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_CACHE_DIR", originalCacheDir);
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static DatadogSymbolsTools CreateTools()
    {
        // These tests only cover the pure JSON paths that don't require a real session/dump.
        var sessionManager = new DebuggerSessionManager(dumpStoragePath: Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", Guid.NewGuid().ToString("N")));
        var symbolManager = new SymbolManager();
        var watchStore = new WatchStore();

        // Create a session used for API validation paths.
        var sessionId = sessionManager.CreateSession("u");
        _ = sessionManager.GetSessionInfo(sessionId, "u");

        return new DatadogSymbolsTools(sessionManager, symbolManager, watchStore, NullLogger<DatadogSymbolsTools>.Instance);
    }
}
