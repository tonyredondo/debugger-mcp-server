using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for DebuggerSessionManager class.
/// </summary>
public class SessionManagerTests : IDisposable
{
    private readonly string _testStoragePath;
    private readonly DebuggerSessionManager _sessionManager;

    public SessionManagerTests()
    {
        _testStoragePath = Path.Combine(Path.GetTempPath(), $"SessionManagerTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testStoragePath);
        _sessionManager = new DebuggerSessionManager(_testStoragePath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testStoragePath))
            {
                Directory.Delete(_testStoragePath, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    // ============================================================
    // CreateSession Tests
    // ============================================================

    [Fact]
    public void CreateSession_WithValidUserId_ReturnsSessionId()
    {
        // Act
        var sessionId = _sessionManager.CreateSession("user1");

        // Assert
        Assert.NotNull(sessionId);
        Assert.NotEmpty(sessionId);
    }

    [Fact]
    public void CreateSession_WithNullUserId_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _sessionManager.CreateSession(null!));
    }

    [Fact]
    public void CreateSession_WithEmptyUserId_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _sessionManager.CreateSession(""));
    }

    [Fact]
    public void CreateSession_WithWhitespaceUserId_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _sessionManager.CreateSession("   "));
    }

    [Fact]
    public void CreateSession_CreatesUniqueSessions()
    {
        // Act
        var session1 = _sessionManager.CreateSession("user1");
        var session2 = _sessionManager.CreateSession("user1");

        // Assert
        Assert.NotEqual(session1, session2);
    }

    [Fact]
    public void CreateSession_ExceedingUserLimit_ThrowsInvalidOperationException()
    {
        // Arrange - Create sessions up to the limit (default 5)
        for (int i = 0; i < 5; i++)
        {
            _sessionManager.CreateSession("limitedUser");
        }

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _sessionManager.CreateSession("limitedUser"));
    }

    // ============================================================
    // GetSession Tests
    // ============================================================

    [Fact]
    public void GetSession_WithValidIds_ReturnsManager()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("user1");

        // Act
        var manager = _sessionManager.GetSession(sessionId, "user1");

        // Assert
        Assert.NotNull(manager);
        Assert.IsAssignableFrom<IDebuggerManager>(manager);
    }

    [Fact]
    public void GetSession_WithNullSessionId_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _sessionManager.GetSession(null!, "user1"));
    }

    [Fact]
    public void GetSession_WithEmptySessionId_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _sessionManager.GetSession("", "user1"));
    }

    [Fact]
    public void GetSession_WithNullUserId_ThrowsArgumentException()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("user1");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _sessionManager.GetSession(sessionId, null!));
    }

    [Fact]
    public void GetSession_WithInvalidSessionId_ThrowsInvalidOperationException()
    {
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _sessionManager.GetSession("invalid-session", "user1"));
    }

    [Fact]
    public void GetSession_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("user1");

        // Act & Assert
        Assert.Throws<UnauthorizedAccessException>(() => _sessionManager.GetSession(sessionId, "user2"));
    }

    [Fact]
    public void GetSession_UpdatesLastAccessedTime()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("user1");
        var sessionInfo1 = _sessionManager.GetSessionInfo(sessionId, "user1");
        var firstAccessTime = sessionInfo1.LastAccessedAt;

        // Wait a bit
        System.Threading.Thread.Sleep(50);

        // Act
        _sessionManager.GetSession(sessionId, "user1");
        var sessionInfo2 = _sessionManager.GetSessionInfo(sessionId, "user1");

        // Assert
        Assert.True(sessionInfo2.LastAccessedAt >= firstAccessTime);
    }

    // ============================================================
    // GetSessionInfo Tests
    // ============================================================

    [Fact]
    public void GetSessionInfo_WithValidIds_ReturnsSessionInfo()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("user1");

        // Act
        var sessionInfo = _sessionManager.GetSessionInfo(sessionId, "user1");

        // Assert
        Assert.NotNull(sessionInfo);
        Assert.Equal(sessionId, sessionInfo.SessionId);
        Assert.Equal("user1", sessionInfo.UserId);
    }

    [Fact]
    public void GetSessionInfo_WithNullSessionId_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _sessionManager.GetSessionInfo(null!, "user1"));
    }

    [Fact]
    public void GetSessionInfo_WithNullUserId_ThrowsArgumentException()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("user1");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _sessionManager.GetSessionInfo(sessionId, null!));
    }

    [Fact]
    public void GetSessionInfo_WithInvalidSessionId_ThrowsInvalidOperationException()
    {
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _sessionManager.GetSessionInfo("invalid", "user1"));
    }

    [Fact]
    public void GetSessionInfo_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("user1");

        // Act & Assert
        Assert.Throws<UnauthorizedAccessException>(() => _sessionManager.GetSessionInfo(sessionId, "user2"));
    }

    // ============================================================
    // CloseSession Tests
    // ============================================================

    [Fact]
    public void CloseSession_WithValidIds_RemovesSession()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("user1");

        // Act
        _sessionManager.CloseSession(sessionId, "user1");

        // Assert
        Assert.Throws<InvalidOperationException>(() => _sessionManager.GetSession(sessionId, "user1"));
    }

    [Fact]
    public void CloseSession_WithNullSessionId_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _sessionManager.CloseSession(null!, "user1"));
    }

    [Fact]
    public void CloseSession_WithNullUserId_ThrowsArgumentException()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("user1");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _sessionManager.CloseSession(sessionId, null!));
    }

    [Fact]
    public void CloseSession_WithInvalidSessionId_ThrowsInvalidOperationException()
    {
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _sessionManager.CloseSession("invalid", "user1"));
    }

    [Fact]
    public void CloseSession_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("user1");

        // Act & Assert
        Assert.Throws<UnauthorizedAccessException>(() => _sessionManager.CloseSession(sessionId, "user2"));
    }

    [Fact]
    public void CloseSession_InvokesOnSessionClosedCallback()
    {
        // Arrange
        var callbackInvoked = false;
        var closedSessionId = string.Empty;
        
        _sessionManager.OnSessionClosed = (id) =>
        {
            callbackInvoked = true;
            closedSessionId = id;
        };

        var sessionId = _sessionManager.CreateSession("user1");

        // Act
        _sessionManager.CloseSession(sessionId, "user1");

        // Assert
        Assert.True(callbackInvoked);
        Assert.Equal(sessionId, closedSessionId);
    }

    // ============================================================
    // ListUserSessions Tests
    // ============================================================

    [Fact]
    public void ListUserSessions_WithNoSessions_ReturnsEmptyList()
    {
        // Act
        var sessions = _sessionManager.ListUserSessions("user1");

        // Assert
        Assert.NotNull(sessions);
        Assert.Empty(sessions);
    }

    [Fact]
    public void ListUserSessions_WithSessions_ReturnsUserSessions()
    {
        // Arrange
        var session1 = _sessionManager.CreateSession("user1");
        var session2 = _sessionManager.CreateSession("user1");
        _sessionManager.CreateSession("user2"); // Different user

        // Act
        var sessions = _sessionManager.ListUserSessions("user1");

        // Assert
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, s => Assert.Equal("user1", s.UserId));
    }

    [Fact]
    public void ListUserSessions_WithNullUserId_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _sessionManager.ListUserSessions(null!));
    }

    // ============================================================
    // CleanupInactiveSessions Tests
    // ============================================================

    [Fact]
    public void CleanupInactiveSessions_WithNoInactiveSessions_ReturnsZero()
    {
        // Arrange
        _sessionManager.CreateSession("user1");

        // Act
        var count = _sessionManager.CleanupInactiveSessions(TimeSpan.FromHours(1));

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CleanupInactiveSessions_WithExpiredSessions_RemovesThem()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("user1");
        
        // Wait a small amount of time so the session becomes inactive
        await Task.Delay(10);
        
        // Act - cleanup sessions inactive for more than 1 millisecond
        var count = _sessionManager.CleanupInactiveSessions(TimeSpan.FromMilliseconds(1));

        // Assert - session should be removed
        Assert.Equal(1, count);
        Assert.Throws<InvalidOperationException>(() => _sessionManager.GetSession(sessionId, "user1"));
    }

    // ============================================================
    // GetStatistics Tests
    // ============================================================

    [Fact]
    public void GetStatistics_ReturnsValidStatistics()
    {
        // Arrange
        _sessionManager.CreateSession("user1");
        _sessionManager.CreateSession("user1");
        _sessionManager.CreateSession("user2");

        // Act
        var stats = _sessionManager.GetStatistics();

        // Assert
        Assert.NotNull(stats);
        Assert.Equal(3, stats["TotalSessions"]);
        Assert.Equal(2, stats["UniqueUsers"]);
    }

    [Fact]
    public void GetStatistics_WithNoSessions_ReturnsZeros()
    {
        // Act
        var stats = _sessionManager.GetStatistics();

        // Assert
        Assert.Equal(0, stats["TotalSessions"]);
        Assert.Equal(0, stats["UniqueUsers"]);
    }

    // ============================================================
    // DumpExists Tests
    // ============================================================

    [Fact]
    public void DumpExists_WithNonExistentDump_ReturnsFalse()
    {
        // Act
        var exists = _sessionManager.DumpExists("nonexistent", "user1");

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public void DumpExists_WithExistingDump_ReturnsTrue()
    {
        // Arrange - create a dump file
        var userDir = Path.Combine(_testStoragePath, "user1");
        Directory.CreateDirectory(userDir);
        var dumpPath = Path.Combine(userDir, "test-dump.dmp");
        File.WriteAllText(dumpPath, "test");

        // Act
        var exists = _sessionManager.DumpExists("test-dump", "user1");

        // Assert
        Assert.True(exists);
    }

    // ============================================================
    // GetDumpPath Tests
    // ============================================================

    [Fact]
    public void GetDumpPath_WithNonExistentDump_ThrowsFileNotFoundException()
    {
        // Act & Assert - GetDumpPath throws when file doesn't exist (security feature)
        var ex = Assert.Throws<FileNotFoundException>(() => _sessionManager.GetDumpPath("my-dump", "user1"));
        Assert.Contains("my-dump", ex.Message);
        Assert.Contains("user1", ex.Message);
    }

    // ============================================================
    // GetDumpStoragePath Tests
    // ============================================================

    [Fact]
    public void GetDumpStoragePath_ReturnsCorrectPath()
    {
        // Act
        var path = _sessionManager.GetDumpStoragePath();

        // Assert
        Assert.NotNull(path);
        Assert.False(string.IsNullOrWhiteSpace(path));
    }

    // ============================================================
    // Thread Safety Tests
    // ============================================================

    [Fact]
    public async Task CreateSession_IsThreadSafe()
    {
        // Arrange
        var tasks = new Task<string>[20];

        // Act - Create 20 sessions concurrently for 4 different users
        for (int i = 0; i < 20; i++)
        {
            var userId = $"user{i % 4}"; // 4 different users
            tasks[i] = Task.Run(() => _sessionManager.CreateSession(userId));
        }

        var results = await Task.WhenAll(tasks);

        // Assert - All sessions should be unique
        Assert.Equal(20, results.Distinct().Count());
    }

    [Fact]
    public async Task GetSession_IsThreadSafe()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("user1");
        var tasks = new Task<IDebuggerManager>[10];

        // Act - Access the same session concurrently
        for (int i = 0; i < 10; i++)
        {
            tasks[i] = Task.Run(() => _sessionManager.GetSession(sessionId, "user1"));
        }

        var results = await Task.WhenAll(tasks);

        // Assert - All should return the same manager
        Assert.All(results, m => Assert.NotNull(m));
    }
}
