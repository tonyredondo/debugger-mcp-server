using System.Text.Json;
using DebuggerMcp;
using DebuggerMcp.McpTools;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.McpTools;

/// <summary>
/// Smoke tests for <see cref="CompactTools"/> dispatch and normalization.
/// </summary>
public sealed class CompactToolsTests : IDisposable
{
    private readonly string _tempPath;
    private readonly DebuggerSessionManager _sessionManager;
    private readonly SymbolManager _symbolManager;
    private readonly WatchStore _watchStore;
    private readonly CompactTools _tools;

    public CompactToolsTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", nameof(CompactToolsTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempPath);

        _sessionManager = new DebuggerSessionManager(
            dumpStoragePath: _tempPath,
            debuggerFactory: _ => new StubDebuggerManager());
        _symbolManager = new SymbolManager(_tempPath);
        _watchStore = new WatchStore(_tempPath);
        _tools = new CompactTools(_sessionManager, _symbolManager, _watchStore, NullLoggerFactory.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempPath))
            {
                Directory.Delete(_tempPath, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void Session_CreateListDebuggerInfoClose_WorksViaCompactDispatch()
    {
        var userId = "test-user";

        var created = _tools.Session(action: "create", userId: userId);
        Assert.Contains("SessionId:", created, StringComparison.Ordinal);

        var sessionId = ExtractSessionId(created);

        var listedJson = _tools.Session(action: "list", userId: userId);
        using var doc = JsonDocument.Parse(listedJson);
        Assert.True(doc.RootElement.TryGetProperty("total", out var total));
        Assert.True(total.GetInt32() >= 1);

        // Hyphenated action should normalize to underscore.
        var info = _tools.Session(action: "debugger-info", userId: userId, sessionId: sessionId);
        Assert.Contains("Debugger Type:", info, StringComparison.Ordinal);

        var closed = _tools.Session(action: "close", userId: userId, sessionId: sessionId);
        Assert.Contains("closed successfully", closed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MultipleToolSurfaces_ExerciseCompactDispatch()
    {
        var userId = "test-user";
        var sessionId = ExtractSessionId(_tools.Session(action: "create", userId: userId));

        // Exec should dispatch to DumpTools.ExecuteCommand.
        var exec = _tools.Exec(sessionId, userId, "lm");
        Assert.Equal("module-list", exec);

        // Source link info should serialize even with no dump id.
        var sourceLinkInfo = _tools.SourceLink(action: "info", sessionId: sessionId, userId: userId);
        using (var doc = JsonDocument.Parse(sourceLinkInfo))
        {
            Assert.True(doc.RootElement.TryGetProperty("SupportedProviders", out var providers));
            Assert.Equal(JsonValueKind.Array, providers.ValueKind);
        }

        // Symbols: server info is session-independent.
        var servers = _tools.Symbols(action: "get_servers");
        Assert.False(string.IsNullOrWhiteSpace(servers));

        // Inspect should return a structured error when ClrMD is not open.
        var lookup = _tools.Inspect(kind: "lookup_type", sessionId: sessionId, userId: userId, typeName: "System.String");
        Assert.Contains("ClrMD", lookup, StringComparison.OrdinalIgnoreCase);

        // Datadog symbols config is action-only.
        var datadogConfig = await _tools.DatadogSymbols(action: "get_config");
        Assert.False(string.IsNullOrWhiteSpace(datadogConfig));

        // Simulate an open dump without invoking the full OpenDump pipeline.
        var manager = _sessionManager.GetSession(sessionId, userId);
        manager.OpenDumpFile(Path.Combine(_tempPath, "fake.dmp"));
        var session = _sessionManager.GetSessionInfo(sessionId, userId);
        session.CurrentDumpId = "fake";

        // Watches require a dump + dumpId.
        var add = await _tools.Watch(action: "add", sessionId: sessionId, userId: userId, expression: "lm");
        Assert.Contains("Watch ID:", add, StringComparison.OrdinalIgnoreCase);

        var watchId = ExtractWatchId(add);

        var list = await _tools.Watch(action: "list", sessionId: sessionId, userId: userId);
        Assert.Contains(watchId, list, StringComparison.OrdinalIgnoreCase);

        var evalSingle = await _tools.Watch(action: "evaluate", sessionId: sessionId, userId: userId, watchId: watchId);
        Assert.Contains("ok:p lm", evalSingle, StringComparison.OrdinalIgnoreCase);

        var evalAll = await _tools.Watch(action: "evaluate_all", sessionId: sessionId, userId: userId);
        Assert.Contains("ok:p lm", evalAll, StringComparison.OrdinalIgnoreCase);

        var removed = await _tools.Watch(action: "remove", sessionId: sessionId, userId: userId, watchId: watchId);
        Assert.Contains("removed", removed, StringComparison.OrdinalIgnoreCase);

        var cleared = await _tools.Watch(action: "clear", sessionId: sessionId, userId: userId);
        Assert.Contains("watch expressions", cleared, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Analyze_SecurityCapabilities_WorksWithoutSession()
    {
        var capabilities = await _tools.Analyze(
            server: null!,
            kind: "security",
            action: "capabilities");

        Assert.False(string.IsNullOrWhiteSpace(capabilities));
        using var doc = JsonDocument.Parse(capabilities);
        Assert.True(doc.RootElement.TryGetProperty("VulnerabilityTypes", out var types));
        Assert.True(types.ValueKind == JsonValueKind.Array && types.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Dump_Close_WorksWithoutOpenDump()
    {
        var sessionId = ExtractSessionId(_tools.Session(action: "create", userId: "owner"));
        var result = await _tools.Dump(action: "close", sessionId: sessionId, userId: "owner");
        Assert.Contains("Dump file closed successfully", result, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSessionAction_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => _tools.Session("nope", userId: "owner"));
        Assert.Equal("action", ex.ParamName);
    }

    [Fact]
    public void MissingRequiredSessionId_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => _tools.Session("close", userId: "owner", sessionId: null));
        Assert.Equal("sessionId", ex.ParamName);
    }

    [Fact]
    public void UnknownCompareKind_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
        {
            _ = _tools.Compare(
                kind: "nope",
                baselineSessionId: "s1",
                baselineUserId: "u1",
                targetSessionId: "s2",
                targetUserId: "u2");
        });

        Assert.Equal("kind", ex.ParamName);
    }

    [Fact]
    public async Task Report_UnknownAction_ThrowsArgumentException()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _tools.Report(action: "nope", sessionId: "s1", userId: "u1"));
        Assert.Equal("action", ex.ParamName);
    }

    private static string ExtractSessionId(string message)
    {
        var marker = "SessionId:";
        var index = message.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Expected '{marker}' in message: {message}");

        var start = index + marker.Length;
        var end = message.IndexOf('.', start);
        if (end < 0)
        {
            end = message.Length;
        }

        return message[start..end].Trim();
    }

    private static string ExtractWatchId(string message)
    {
        var marker = "Watch ID:";
        var index = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        Assert.True(index >= 0, $"Expected '{marker}' in message: {message}");

        var start = index + marker.Length;
        var end = message.IndexOf('\n', start);
        if (end < 0)
        {
            end = message.Length;
        }

        return message[start..end].Trim();
    }

    private sealed class StubDebuggerManager : IDebuggerManager
    {
        public bool IsInitialized { get; private set; } = true;
        public bool IsDumpOpen { get; private set; }
        public string? CurrentDumpPath { get; private set; }
        public string DebuggerType => "LLDB";
        public bool IsSosLoaded { get; private set; }
        public bool IsDotNetDump { get; private set; }

        public Task InitializeAsync()
        {
            IsInitialized = true;
            return Task.CompletedTask;
        }

        public void OpenDumpFile(string dumpFilePath, string? executablePath = null)
        {
            CurrentDumpPath = dumpFilePath;
            IsDumpOpen = true;
        }

        public void CloseDump()
        {
            IsDumpOpen = false;
            CurrentDumpPath = null;
        }

        public string ExecuteCommand(string command)
        {
            // Keep deterministic, short output for tool tests.
            if (string.Equals(command, "lm", StringComparison.OrdinalIgnoreCase))
                return "module-list";

            return $"ok:{command}";
        }

        public void LoadSosExtension()
        {
            IsSosLoaded = true;
            IsDotNetDump = true;
        }

        public void ConfigureSymbolPath(string symbolPath)
        {
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
