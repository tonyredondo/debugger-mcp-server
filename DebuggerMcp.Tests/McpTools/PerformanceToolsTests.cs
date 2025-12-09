using DebuggerMcp;
using DebuggerMcp.McpTools;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.McpTools;

/// <summary>
/// Tests for PerformanceTools MCP tool class.
/// </summary>
public class PerformanceToolsTests : IDisposable
{
    private readonly string _tempPath;
    private readonly DebuggerSessionManager _sessionManager;
    private readonly SymbolManager _symbolManager;
    private readonly WatchStore _watchStore;
    private readonly PerformanceTools _tools;

    public PerformanceToolsTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempPath);

        _sessionManager = new DebuggerSessionManager(_tempPath);
        _symbolManager = new SymbolManager(_tempPath);
        _watchStore = new WatchStore(_tempPath);
        _tools = new PerformanceTools(_sessionManager, _symbolManager, _watchStore, NullLogger<PerformanceTools>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            try { Directory.Delete(_tempPath, true); } catch { }
        }
    }

    // ============================================================
    // AnalyzePerformance Tests
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnalyzePerformance_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.AnalyzePerformance(sessionId!, "user"));
    }

    [Fact]
    public async Task AnalyzePerformance_WithPathTraversalInUserId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.AnalyzePerformance("session", "../etc/passwd"));
    }

    [Fact]
    public async Task AnalyzePerformance_WithNonExistentSession_ThrowsInvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.AnalyzePerformance("non-existent", "user"));
    }

    [Fact]
    public async Task AnalyzePerformance_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        var sessionId = _sessionManager.CreateSession("owner");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _tools.AnalyzePerformance(sessionId, "wrong-user"));
    }

    [Fact]
    public async Task AnalyzePerformance_WithDumpNotOpen_ThrowsInvalidOperationException()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.AnalyzePerformance(sessionId, userId));

        Assert.Contains("No dump file is open", ex.Message);
    }

    [Fact]
    public async Task AnalyzePerformance_WithIncludeWatchesFalse_DoesNotThrow()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        // Should still fail because dump not open, not watches issue
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.AnalyzePerformance(sessionId, userId, includeWatches: false));

        Assert.Contains("No dump file is open", ex.Message);
    }

    // ============================================================
    // AnalyzeCpuUsage Tests
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnalyzeCpuUsage_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.AnalyzeCpuUsage(sessionId!, "user"));
    }

    [Fact]
    public async Task AnalyzeCpuUsage_WithPathTraversalInUserId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.AnalyzeCpuUsage("session", "..\\..\\Windows"));
    }

    [Fact]
    public async Task AnalyzeCpuUsage_WithNonExistentSession_ThrowsInvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.AnalyzeCpuUsage("non-existent", "user"));
    }

    [Fact]
    public async Task AnalyzeCpuUsage_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        var sessionId = _sessionManager.CreateSession("owner");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _tools.AnalyzeCpuUsage(sessionId, "wrong-user"));
    }

    [Fact]
    public async Task AnalyzeCpuUsage_WithDumpNotOpen_ThrowsInvalidOperationException()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.AnalyzeCpuUsage(sessionId, userId));

        Assert.Contains("No dump file is open", ex.Message);
    }

    // ============================================================
    // AnalyzeAllocations Tests
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnalyzeAllocations_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.AnalyzeAllocations(sessionId!, "user"));
    }

    [Fact]
    public async Task AnalyzeAllocations_WithPathTraversalInUserId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.AnalyzeAllocations("session", "/root/.ssh"));
    }

    [Fact]
    public async Task AnalyzeAllocations_WithNonExistentSession_ThrowsInvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.AnalyzeAllocations("non-existent", "user"));
    }

    [Fact]
    public async Task AnalyzeAllocations_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        var sessionId = _sessionManager.CreateSession("owner");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _tools.AnalyzeAllocations(sessionId, "wrong-user"));
    }

    [Fact]
    public async Task AnalyzeAllocations_WithDumpNotOpen_ThrowsInvalidOperationException()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.AnalyzeAllocations(sessionId, userId));

        Assert.Contains("No dump file is open", ex.Message);
    }

    // ============================================================
    // AnalyzeGc Tests
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnalyzeGc_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.AnalyzeGc(sessionId!, "user"));
    }

    [Fact]
    public async Task AnalyzeGc_WithPathTraversalInUserId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.AnalyzeGc("session", "C:\\Windows\\System32"));
    }

    [Fact]
    public async Task AnalyzeGc_WithNonExistentSession_ThrowsInvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.AnalyzeGc("non-existent", "user"));
    }

    [Fact]
    public async Task AnalyzeGc_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        var sessionId = _sessionManager.CreateSession("owner");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _tools.AnalyzeGc(sessionId, "wrong-user"));
    }

    [Fact]
    public async Task AnalyzeGc_WithDumpNotOpen_ThrowsInvalidOperationException()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.AnalyzeGc(sessionId, userId));

        Assert.Contains("No dump file is open", ex.Message);
    }

    // ============================================================
    // AnalyzeContention Tests
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnalyzeContention_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.AnalyzeContention(sessionId!, "user"));
    }

    [Fact]
    public async Task AnalyzeContention_WithPathTraversalInUserId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.AnalyzeContention("session", "user/../../admin"));
    }

    [Fact]
    public async Task AnalyzeContention_WithNonExistentSession_ThrowsInvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.AnalyzeContention("non-existent", "user"));
    }

    [Fact]
    public async Task AnalyzeContention_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        var sessionId = _sessionManager.CreateSession("owner");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _tools.AnalyzeContention(sessionId, "wrong-user"));
    }

    [Fact]
    public async Task AnalyzeContention_WithDumpNotOpen_ThrowsInvalidOperationException()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.AnalyzeContention(sessionId, userId));

        Assert.Contains("No dump file is open", ex.Message);
    }
}

