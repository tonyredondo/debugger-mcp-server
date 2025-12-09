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

    public WatchToolsTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempPath);

        _sessionManager = new DebuggerSessionManager(_tempPath);
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
}

