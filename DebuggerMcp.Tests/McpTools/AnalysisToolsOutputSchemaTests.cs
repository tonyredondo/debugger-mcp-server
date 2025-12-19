using System.Text.Json;
using DebuggerMcp.McpTools;
using DebuggerMcp.Tests.TestDoubles;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.McpTools;

public class AnalysisToolsOutputSchemaTests
{
    [Fact]
    public async Task AnalyzeCrash_WhenNoSosOrClrMd_ThrowsInvalidOperationException()
    {
        var fakeManager = new FakeDebuggerManager
        {
            DebuggerType = "LLDB",
            IsDumpOpen = true,
            IsDotNetDump = true,
            IsSosLoaded = false,
            CommandHandler = _ => "Test output"
        };

        var basePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var sessionManager = new DebuggerSessionManager(
            dumpStoragePath: basePath,
            loggerFactory: NullLoggerFactory.Instance,
            debuggerFactory: _ => fakeManager);
        var symbolManager = new SymbolManager(Path.Combine(basePath, "symbols"));
        var watchStore = new WatchStore(Path.Combine(basePath, "watches"));
        var tools = new AnalysisTools(sessionManager, symbolManager, watchStore, NullLogger<AnalysisTools>.Instance);

        var userId = "user1";
        var sessionId = sessionManager.CreateSession(userId);
        var session = sessionManager.GetSessionInfo(sessionId, userId);
        session.CurrentDumpId = "dump-1";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.AnalyzeCrash(sessionId, userId, includeWatches: false));
        Assert.Contains(".NET crash analysis", ex.Message);
    }

    [Fact]
    public async Task AnalyzeCrash_ReturnsCanonicalReportDocument()
    {
        var fakeManager = new FakeDebuggerManager
        {
            DebuggerType = "LLDB",
            IsDumpOpen = true,
            IsDotNetDump = true,
            IsSosLoaded = true,
            CommandHandler = _ => "Test output"
        };

        var basePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var sessionManager = new DebuggerSessionManager(
            dumpStoragePath: basePath,
            loggerFactory: NullLoggerFactory.Instance,
            debuggerFactory: _ => fakeManager);
        var symbolManager = new SymbolManager(Path.Combine(basePath, "symbols"));
        var watchStore = new WatchStore(Path.Combine(basePath, "watches"));
        var tools = new AnalysisTools(sessionManager, symbolManager, watchStore, NullLogger<AnalysisTools>.Instance);

        var userId = "user1";
        var sessionId = sessionManager.CreateSession(userId);
        var session = sessionManager.GetSessionInfo(sessionId, userId);
        session.CurrentDumpId = "dump-1";

        var json = await tools.AnalyzeCrash(sessionId, userId, includeWatches: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("metadata", out var metadata));
        Assert.True(root.TryGetProperty("analysis", out _));
        Assert.False(root.TryGetProperty("summary", out _));
        Assert.False(root.TryGetProperty("exception", out _));

        Assert.Equal("dump-1", metadata.GetProperty("dumpId").GetString());
        Assert.Equal("user1", metadata.GetProperty("userId").GetString());
        Assert.Equal("Json", metadata.GetProperty("format").GetString());
    }
}
