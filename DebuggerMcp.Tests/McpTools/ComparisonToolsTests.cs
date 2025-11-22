using DebuggerMcp;
using DebuggerMcp.McpTools;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.McpTools;

/// <summary>
/// Tests for ComparisonTools MCP tool class.
/// </summary>
public class ComparisonToolsTests : IDisposable
{
    private readonly string _tempPath;
    private readonly DebuggerSessionManager _sessionManager;
    private readonly SymbolManager _symbolManager;
    private readonly WatchStore _watchStore;
    private readonly ComparisonTools _tools;

    public ComparisonToolsTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempPath);
        
        _sessionManager = new DebuggerSessionManager(_tempPath);
        _symbolManager = new SymbolManager(_tempPath);
        _watchStore = new WatchStore(_tempPath);
        _tools = new ComparisonTools(_sessionManager, _symbolManager, _watchStore, NullLogger<ComparisonTools>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            try { Directory.Delete(_tempPath, true); } catch { }
        }
    }

    // ============================================================
    // CompareDumps Tests - Input Validation
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CompareDumps_WithNullOrEmptyBaselineSessionId_ThrowsArgumentException(string? sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.CompareDumps(sessionId!, "user", "comparison-session", "user"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CompareDumps_WithNullOrEmptyComparisonSessionId_ThrowsArgumentException(string? sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.CompareDumps("baseline-session", "user", sessionId!, "user"));
    }

    [Fact]
    public async Task CompareDumps_WithPathTraversalInBaselineUserId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.CompareDumps("session1", "../etc/passwd", "session2", "user"));
    }

    [Fact]
    public async Task CompareDumps_WithPathTraversalInComparisonUserId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.CompareDumps("session1", "user", "session2", "../etc/passwd"));
    }

    [Fact]
    public async Task CompareDumps_WithNonExistentBaselineSession_ThrowsInvalidOperationException()
    {
        var comparisonSessionId = _sessionManager.CreateSession("user");
        
        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.CompareDumps("non-existent", "user", comparisonSessionId, "user"));
    }

    [Fact]
    public async Task CompareDumps_WithNonExistentComparisonSession_ThrowsInvalidOperationException()
    {
        var baselineSessionId = _sessionManager.CreateSession("user");
        
        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.CompareDumps(baselineSessionId, "user", "non-existent", "user"));
    }

    [Fact]
    public async Task CompareDumps_WithWrongBaselineUserId_ThrowsUnauthorizedAccessException()
    {
        var baselineSessionId = _sessionManager.CreateSession("owner1");
        var comparisonSessionId = _sessionManager.CreateSession("owner2");
        
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => 
            _tools.CompareDumps(baselineSessionId, "wrong-user", comparisonSessionId, "owner2"));
    }

    [Fact]
    public async Task CompareDumps_WithWrongComparisonUserId_ThrowsUnauthorizedAccessException()
    {
        var baselineSessionId = _sessionManager.CreateSession("owner1");
        var comparisonSessionId = _sessionManager.CreateSession("owner2");
        
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => 
            _tools.CompareDumps(baselineSessionId, "owner1", comparisonSessionId, "wrong-user"));
    }

    // ============================================================
    // CompareHeaps Tests - Input Validation
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CompareHeaps_WithNullOrEmptyBaselineSessionId_ThrowsArgumentException(string? sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.CompareHeaps(sessionId!, "user", "comparison-session", "user"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CompareHeaps_WithNullOrEmptyComparisonSessionId_ThrowsArgumentException(string? sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.CompareHeaps("baseline-session", "user", sessionId!, "user"));
    }

    [Fact]
    public async Task CompareHeaps_WithPathTraversalInUserId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.CompareHeaps("session1", "..\\..\\etc", "session2", "user"));
    }

    [Fact]
    public async Task CompareHeaps_WithNonExistentSession_ThrowsInvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.CompareHeaps("non-existent", "user", "also-non-existent", "user"));
    }

    [Fact]
    public async Task CompareHeaps_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        var sessionId = _sessionManager.CreateSession("owner");
        
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => 
            _tools.CompareHeaps(sessionId, "wrong-user", sessionId, "owner"));
    }

    // ============================================================
    // CompareThreads Tests - Input Validation
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CompareThreads_WithNullOrEmptyBaselineSessionId_ThrowsArgumentException(string? sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.CompareThreads(sessionId!, "user", "comparison-session", "user"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CompareThreads_WithNullOrEmptyComparisonSessionId_ThrowsArgumentException(string? sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.CompareThreads("baseline-session", "user", sessionId!, "user"));
    }

    [Fact]
    public async Task CompareThreads_WithPathTraversalInUserId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.CompareThreads("session1", "/etc/passwd", "session2", "user"));
    }

    [Fact]
    public async Task CompareThreads_WithNonExistentSession_ThrowsInvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.CompareThreads("non-existent", "user", "also-non-existent", "user"));
    }

    [Fact]
    public async Task CompareThreads_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        var sessionId = _sessionManager.CreateSession("owner");
        
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => 
            _tools.CompareThreads(sessionId, "owner", sessionId, "wrong-user"));
    }

    // ============================================================
    // CompareModules Tests - Input Validation
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CompareModules_WithNullOrEmptyBaselineSessionId_ThrowsArgumentException(string? sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.CompareModules(sessionId!, "user", "comparison-session", "user"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CompareModules_WithNullOrEmptyComparisonSessionId_ThrowsArgumentException(string? sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.CompareModules("baseline-session", "user", sessionId!, "user"));
    }

    [Fact]
    public async Task CompareModules_WithPathTraversalInUserId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.CompareModules("session1", "user", "session2", "C:\\Windows\\System32"));
    }

    [Fact]
    public async Task CompareModules_WithNonExistentBaselineSession_ThrowsInvalidOperationException()
    {
        var comparisonSessionId = _sessionManager.CreateSession("user");
        
        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.CompareModules("non-existent", "user", comparisonSessionId, "user"));
    }

    [Fact]
    public async Task CompareModules_WithNonExistentComparisonSession_ThrowsInvalidOperationException()
    {
        var baselineSessionId = _sessionManager.CreateSession("user");
        
        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.CompareModules(baselineSessionId, "user", "non-existent", "user"));
    }

    [Fact]
    public async Task CompareModules_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        var baselineSessionId = _sessionManager.CreateSession("owner1");
        var comparisonSessionId = _sessionManager.CreateSession("owner2");
        
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => 
            _tools.CompareModules(baselineSessionId, "owner1", comparisonSessionId, "wrong-user"));
    }

    // ============================================================
    // Cross-User Comparison Tests (Valid Scenarios)
    // ============================================================

    [Fact]
    public async Task CompareDumps_WithDifferentOwnersButCorrectCredentials_ThrowsWhenDumpNotOpen()
    {
        // Arrange - Create two sessions owned by different users
        var baselineSessionId = _sessionManager.CreateSession("user1");
        var comparisonSessionId = _sessionManager.CreateSession("user2");
        
        // Act & Assert - Should fail because dumps aren't open, not because of auth
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.CompareDumps(baselineSessionId, "user1", comparisonSessionId, "user2"));
        
        Assert.Contains("does not have a dump file open", ex.Message);
    }

    [Fact]
    public async Task CompareHeaps_WithSameSessionTwice_ThrowsWhenDumpNotOpen()
    {
        // Arrange - Create one session, try to compare it with itself
        var sessionId = _sessionManager.CreateSession("user");
        
        // Act & Assert - Should fail because dump isn't open
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.CompareHeaps(sessionId, "user", sessionId, "user"));
        
        Assert.Contains("does not have a dump file open", ex.Message);
    }
}

