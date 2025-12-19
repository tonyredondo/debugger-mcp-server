using DebuggerMcp;
using DebuggerMcp.McpTools;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.McpTools;

/// <summary>
/// Tests for WatchTools MCP tool class.
/// </summary>
public class WatchToolsTests : IDisposable
{
    private readonly string _tempPath;
    private readonly DebuggerSessionManager _sessionManager;
    private readonly SymbolManager _symbolManager;
    private readonly WatchStore _watchStore;
    private readonly WatchTools _tools;
    private const string DumpId = "dump-123";

    public WatchToolsTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempPath);

        _sessionManager = new DebuggerSessionManager(_tempPath, debuggerFactory: _ => new TestDebuggerManager());
        _symbolManager = new SymbolManager(_tempPath);
        _watchStore = new WatchStore(_tempPath);
        _tools = new WatchTools(_sessionManager, _symbolManager, _watchStore, NullLogger<WatchTools>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            try { Directory.Delete(_tempPath, true); } catch { }
        }
    }

    // ============================================================
    // AddWatch Tests
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddWatch_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.AddWatch(sessionId!, "user", "0x12345678"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddWatch_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.AddWatch(sessionId, userId!, "0x12345678"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddWatch_WithNullOrEmptyExpression_ThrowsArgumentException(string? expression)
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        // Act & Assert - Without a dump open, it should fail first on that
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.AddWatch(sessionId, userId, expression!));
    }

    [Fact]
    public async Task AddWatch_WithoutOpenDump_ThrowsInvalidOperationException()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.AddWatch(sessionId, userId, "0x12345678"));
    }

    [Fact]
    public async Task AddWatch_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _tools.AddWatch(sessionId, "wrong-user", "0x12345678"));
    }

    // ============================================================
    // ListWatches Tests
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ListWatches_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.ListWatches(sessionId!, "user"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ListWatches_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.ListWatches(sessionId, userId!));
    }

    [Fact]
    public async Task ListWatches_WithoutOpenDump_ThrowsInvalidOperationException()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.ListWatches(sessionId, userId));
    }

    [Fact]
    public async Task ListWatches_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _tools.ListWatches(sessionId, "wrong-user"));
    }

    // ============================================================
    // EvaluateWatches Tests
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EvaluateWatches_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.EvaluateWatches(sessionId!, "user"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EvaluateWatches_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.EvaluateWatches(sessionId, userId!));
    }

    [Fact]
    public async Task EvaluateWatches_WithoutOpenDump_ThrowsInvalidOperationException()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.EvaluateWatches(sessionId, userId));
    }

    // ============================================================
    // EvaluateWatch Tests
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EvaluateWatch_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.EvaluateWatch(sessionId!, "user", "watch-id"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EvaluateWatch_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.EvaluateWatch(sessionId, userId!, "watch-id"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EvaluateWatch_WithNullOrEmptyWatchId_ThrowsArgumentException(string? watchId)
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.EvaluateWatch(sessionId, userId, watchId!));
    }

    [Fact]
    public async Task EvaluateWatch_WithoutOpenDump_ThrowsInvalidOperationException()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.EvaluateWatch(sessionId, userId, "watch-id"));
    }

    // ============================================================
    // RemoveWatch Tests
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RemoveWatch_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.RemoveWatch(sessionId!, "user", "watch-id"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RemoveWatch_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.RemoveWatch(sessionId, userId!, "watch-id"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RemoveWatch_WithNullOrEmptyWatchId_ThrowsArgumentException(string? watchId)
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.RemoveWatch(sessionId, userId, watchId!));
    }

    [Fact]
    public async Task RemoveWatch_WithoutOpenDump_ThrowsInvalidOperationException()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.RemoveWatch(sessionId, userId, "watch-id"));
    }

    // ============================================================
    // ClearWatches Tests
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ClearWatches_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.ClearWatches(sessionId!, "user"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ClearWatches_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.ClearWatches(sessionId, userId!));
    }

    [Fact]
    public async Task ClearWatches_WithoutOpenDump_ThrowsInvalidOperationException()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.ClearWatches(sessionId, userId));
    }

    // ============================================================
    // Report Cache Invalidation Tests
    // ============================================================

    [Fact]
    public async Task AddWatch_WhenReportIsCached_ClearsCachedReport()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);
        SetDumpOpen(sessionId, userId, DumpId);

        var session = _sessionManager.GetSessionInfo(sessionId, userId);
        session.SetCachedReport(DumpId, DateTime.UtcNow, "{ \"report\": 1 }", includesWatches: false, includesSecurity: true);

        // Act
        await _tools.AddWatch(sessionId, userId, "0x12345678");

        // Assert
        Assert.Null(session.CachedReportDumpId);
        Assert.False(session.TryGetCachedReport(DumpId, requireWatches: false, requireSecurity: false, out _));
    }

    [Fact]
    public async Task RemoveWatch_WhenReportIsCached_ClearsCachedReport()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);
        SetDumpOpen(sessionId, userId, DumpId);

        await _tools.AddWatch(sessionId, userId, "0x12345678");
        var existing = await _watchStore.GetWatchesAsync(userId, DumpId);
        Assert.Single(existing);

        var session = _sessionManager.GetSessionInfo(sessionId, userId);
        session.SetCachedReport(DumpId, DateTime.UtcNow, "{ \"report\": 1 }", includesWatches: true, includesSecurity: true);

        // Act
        await _tools.RemoveWatch(sessionId, userId, existing[0].Id);

        // Assert
        Assert.Null(session.CachedReportDumpId);
        Assert.False(session.TryGetCachedReport(DumpId, requireWatches: false, requireSecurity: false, out _));
    }

    [Fact]
    public async Task ClearWatches_WhenReportIsCached_ClearsCachedReport()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);
        SetDumpOpen(sessionId, userId, DumpId);

        await _tools.AddWatch(sessionId, userId, "0x12345678");
        Assert.True(await _watchStore.HasWatchesAsync(userId, DumpId));

        var session = _sessionManager.GetSessionInfo(sessionId, userId);
        session.SetCachedReport(DumpId, DateTime.UtcNow, "{ \"report\": 1 }", includesWatches: true, includesSecurity: true);

        // Act
        await _tools.ClearWatches(sessionId, userId);

        // Assert
        Assert.Null(session.CachedReportDumpId);
        Assert.False(session.TryGetCachedReport(DumpId, requireWatches: false, requireSecurity: false, out _));
    }

    private void SetDumpOpen(string sessionId, string userId, string dumpId)
    {
        var manager = (TestDebuggerManager)_sessionManager.GetSession(sessionId, userId);
        manager.IsDumpOpen = true;

        var session = _sessionManager.GetSessionInfo(sessionId, userId);
        session.CurrentDumpId = dumpId;
    }

    private sealed class TestDebuggerManager : IDebuggerManager
    {
        public bool IsInitialized => true;
        public bool IsDumpOpen { get; set; }
        public string? CurrentDumpPath => null;
        public string DebuggerType => "LLDB";
        public bool IsSosLoaded => false;
        public bool IsDotNetDump => false;

        public Task InitializeAsync() => Task.CompletedTask;
        public void OpenDumpFile(string dumpFilePath, string? executablePath = null) => throw new NotSupportedException();
        public void CloseDump() => IsDumpOpen = false;
        public string ExecuteCommand(string command) => throw new NotSupportedException();
        public void LoadSosExtension() => throw new NotSupportedException();
        public void ConfigureSymbolPath(string symbolPath) => throw new NotSupportedException();
        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
