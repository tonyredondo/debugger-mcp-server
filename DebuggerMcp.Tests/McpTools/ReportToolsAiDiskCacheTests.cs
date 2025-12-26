using DebuggerMcp.Analysis;
using DebuggerMcp.McpTools;
using DebuggerMcp.Tests.TestDoubles;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.McpTools;

public class ReportToolsAiDiskCacheTests : IDisposable
{
    private readonly string _tempPath;

    public ReportToolsAiDiskCacheTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            try { Directory.Delete(_tempPath, true); } catch { }
        }
    }

    [Fact]
    public async Task GenerateReport_Markdown_UsesDiskAiCacheWhenAvailable()
    {
        var fakeManager = new FakeDebuggerManager
        {
            DebuggerType = "LLDB",
            IsDumpOpen = true,
            IsSosLoaded = false,
            CommandHandler = _ => throw new InvalidOperationException("Debugger command execution should not be needed when using cached JSON.")
        };

        var sessionManager = new DebuggerSessionManager(
            _tempPath,
            loggerFactory: NullLoggerFactory.Instance,
            debuggerFactory: _ => fakeManager);
        var symbolManager = new SymbolManager(_tempPath);
        var watchStore = new WatchStore(_tempPath);
        var tools = new ReportTools(sessionManager, symbolManager, watchStore, NullLogger<ReportTools>.Instance);

        var userId = "test-user";
        var sessionId = sessionManager.CreateSession(userId);
        var session = sessionManager.GetSessionInfo(sessionId, userId);
        session.CurrentDumpId = "dump-123";

        var cache = new AiAnalysisDiskCache(_tempPath, NullLogger<AiAnalysisDiskCache>.Instance);
        var llmKey = AiAnalysisDiskCacheLlmKey.TryCreate("openai", "gpt-test", "medium");
        Assert.NotNull(llmKey);
        await cache.WriteAsync(
            userId,
            session.CurrentDumpId,
            llmKey,
            new AiAnalysisDiskCacheMetadata
            {
                GeneratedAtUtc = DateTime.UtcNow,
                IncludesWatches = true,
                IncludesSecurity = true,
                MaxStackFrames = 0,
                IncludesAiAnalysis = true,
                Model = "gpt-test"
            },
            "{\"metadata\":{\"dumpId\":\"dump-123\",\"userId\":\"test-user\"},\"analysis\":{\"summary\":{\"crashType\":\"test\"},\"aiAnalysis\":{\"rootCause\":\"cached\"}}}");

        var markdown = await tools.GenerateReport(sessionId, userId, format: "markdown");

        Assert.Contains("# Debugger MCP Report", markdown, StringComparison.Ordinal);
        Assert.Empty(fakeManager.ExecutedCommands);
    }
}
