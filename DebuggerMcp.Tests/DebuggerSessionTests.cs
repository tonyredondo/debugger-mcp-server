using Xunit;

#pragma warning disable CA1416 // Platform compatibility - Tests use WinDbgManager which is Windows-only

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for the DebuggerSession class.
/// </summary>
public class DebuggerSessionTests
{
    /// <summary>
    /// Tests that a session can be created with valid properties.
    /// </summary>
    [Fact]
    public void Session_Creation_SetsPropertiesCorrectly()
    {
        // Arrange
        var sessionId = "test-session-123";
        var userId = "test-user";
        var manager = new WinDbgManager();
        var createdAt = DateTime.UtcNow;

        // Act
        var session = new DebuggerSession
        {
            SessionId = sessionId,
            UserId = userId,
            Manager = manager,
            CreatedAt = createdAt,
            LastAccessedAt = createdAt
        };

        // Assert
        Assert.Equal(sessionId, session.SessionId);
        Assert.Equal(userId, session.UserId);
        Assert.Same(manager, session.Manager);
        Assert.Equal(createdAt, session.CreatedAt);
        Assert.Equal(createdAt, session.LastAccessedAt);
        Assert.Null(session.CurrentDumpId);
    }

    /// <summary>
    /// Tests that CurrentDumpId can be set and retrieved.
    /// </summary>
    [Fact]
    public void Session_CurrentDumpId_CanBeSetAndRetrieved()
    {
        // Arrange
        var session = new DebuggerSession
        {
            SessionId = "test-session",
            UserId = "test-user",
            Manager = new WinDbgManager()
        };
        var dumpId = "dump-123";

        // Act
        session.CurrentDumpId = dumpId;

        // Assert
        Assert.Equal(dumpId, session.CurrentDumpId);
    }

    /// <summary>
    /// Tests that LastAccessedAt can be updated.
    /// </summary>
    [Fact]
    public void Session_LastAccessedAt_CanBeUpdated()
    {
        // Arrange
        var initialTime = DateTime.UtcNow.AddMinutes(-10);
        var session = new DebuggerSession
        {
            SessionId = "test-session",
            UserId = "test-user",
            Manager = new WinDbgManager(),
            LastAccessedAt = initialTime
        };
        var newTime = DateTime.UtcNow;

        // Act
        session.LastAccessedAt = newTime;

        // Assert
        Assert.Equal(newTime, session.LastAccessedAt);
        Assert.NotEqual(initialTime, session.LastAccessedAt);
    }

    /// <summary>
    /// Tests that Dispose releases resources properly.
    /// </summary>
    [Fact]
    public void Session_Dispose_ReleasesResources()
    {
        // Arrange
        var session = new DebuggerSession
        {
            SessionId = "test-session",
            UserId = "test-user",
            Manager = new WinDbgManager()
        };

        // Act - Should not throw
        session.Dispose();

        // Assert - Multiple dispose calls should be safe
        session.Dispose();
    }
}
