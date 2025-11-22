using DebuggerMcp;
using DebuggerMcp.McpTools;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.McpTools;

/// <summary>
/// Tests for SessionTools MCP tool class.
/// </summary>
public class SessionToolsTests : IDisposable
{
    private readonly string _tempPath;
    private readonly DebuggerSessionManager _sessionManager;
    private readonly SymbolManager _symbolManager;
    private readonly WatchStore _watchStore;
    private readonly SessionTools _tools;

    public SessionToolsTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempPath);
        
        _sessionManager = new DebuggerSessionManager(_tempPath);
        _symbolManager = new SymbolManager(_tempPath);
        _watchStore = new WatchStore(_tempPath);
        _tools = new SessionTools(_sessionManager, _symbolManager, _watchStore, NullLogger<SessionTools>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            try { Directory.Delete(_tempPath, true); } catch { }
        }
    }

    // ============================================================
    // CreateSession Tests
    // ============================================================

    [Fact]
    public void CreateSession_WithValidUserId_ReturnsSessionId()
    {
        // Act
        var result = _tools.CreateSession("test-user");

        // Assert
        Assert.Contains("Session created successfully", result);
        Assert.Contains("SessionId:", result);
    }

    [Fact]
    public void CreateSession_WithSpecialCharacters_SanitizesUserId()
    {
        // Act - userId with special characters should be sanitized
        var result = _tools.CreateSession("test_user123");

        // Assert
        Assert.Contains("Session created successfully", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateSession_WithInvalidUserId_ReturnsErrorString(string? userId)
    {
        // Act
        var result = _tools.CreateSession(userId!);

        // Assert - SessionTools now catches exceptions and returns error strings
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void CreateSession_WithPathTraversalAttempt_ReturnsErrorString()
    {
        // Act
        var result = _tools.CreateSession("../etc/passwd");

        // Assert - SessionTools now catches exceptions and returns error strings
        Assert.StartsWith("Error:", result);
    }

    // ============================================================
    // CloseSession Tests
    // ============================================================

    [Fact]
    public void CloseSession_WithValidSession_ReturnsSuccessMessage()
    {
        // Arrange
        var userId = "test-user";
        var createResult = _tools.CreateSession(userId);
        var sessionId = ExtractSessionId(createResult);

        // Act
        var result = _tools.CloseSession(sessionId, userId);

        // Assert
        Assert.Contains("closed successfully", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CloseSession_WithNullOrEmptySessionId_ReturnsErrorString(string? sessionId)
    {
        // Act
        var result = _tools.CloseSession(sessionId!, "user");

        // Assert - SessionTools now catches exceptions and returns error strings
        Assert.StartsWith("Error:", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CloseSession_WithInvalidUserId_ReturnsErrorString(string? userId)
    {
        // Arrange
        var createResult = _tools.CreateSession("owner");
        var sessionId = ExtractSessionId(createResult);

        // Act
        var result = _tools.CloseSession(sessionId, userId!);

        // Assert - SessionTools now catches exceptions and returns error strings
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void CloseSession_WithWrongUserId_ReturnsUnauthorizedError()
    {
        // Arrange
        var createResult = _tools.CreateSession("owner");
        var sessionId = ExtractSessionId(createResult);

        // Act
        var result = _tools.CloseSession(sessionId, "wrong-user");

        // Assert - SessionTools now catches exceptions and returns error strings
        Assert.StartsWith("Error:", result);
        Assert.Contains("does not have access", result);
    }

    [Fact]
    public void CloseSession_WithNonExistentSession_ReturnsNotFoundError()
    {
        // Act
        var result = _tools.CloseSession("non-existent", "user");

        // Assert - SessionTools now catches exceptions and returns error strings
        Assert.StartsWith("Error:", result);
        Assert.Contains("not found", result);
    }

    // ============================================================
    // ListSessions Tests
    // ============================================================

    [Fact]
    public void ListSessions_WithNoSessions_ReturnsNoSessionsMessage()
    {
        // Act
        var result = _tools.ListSessions("test-user");

        // Assert
        Assert.Contains("No active sessions found", result);
    }

    [Fact]
    public void ListSessions_WithActiveSessions_ReturnsSessionList()
    {
        // Arrange
        var userId = "test-user";
        _tools.CreateSession(userId);
        _tools.CreateSession(userId);

        // Act
        var result = _tools.ListSessions(userId);

        // Assert
        Assert.Contains("Active sessions for user", result);
        Assert.Contains("SessionId:", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ListSessions_WithInvalidUserId_ReturnsErrorString(string? userId)
    {
        // Act
        var result = _tools.ListSessions(userId!);

        // Assert - SessionTools now catches exceptions and returns error strings
        Assert.StartsWith("Error:", result);
    }

    // ============================================================
    // GetDebuggerInfo Tests
    // ============================================================

    [Fact]
    public void GetDebuggerInfo_WithValidSession_ReturnsDebuggerInfo()
    {
        // Arrange
        var userId = "test-user";
        var createResult = _tools.CreateSession(userId);
        var sessionId = ExtractSessionId(createResult);

        // Act
        var result = _tools.GetDebuggerInfo(sessionId, userId);

        // Assert
        Assert.Contains("Debugger Type:", result);
        Assert.Contains("Operating System:", result);
        Assert.Contains("Initialized:", result);
        Assert.Contains("Dump Open:", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetDebuggerInfo_WithNullOrEmptySessionId_ReturnsErrorString(string? sessionId)
    {
        // Act
        var result = _tools.GetDebuggerInfo(sessionId!, "user");

        // Assert - SessionTools now catches exceptions and returns error strings
        Assert.StartsWith("Error:", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetDebuggerInfo_WithInvalidUserId_ReturnsErrorString(string? userId)
    {
        // Arrange
        var createResult = _tools.CreateSession("owner");
        var sessionId = ExtractSessionId(createResult);

        // Act
        var result = _tools.GetDebuggerInfo(sessionId, userId!);

        // Assert - SessionTools now catches exceptions and returns error strings
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void GetDebuggerInfo_WithWrongUserId_ReturnsUnauthorizedError()
    {
        // Arrange
        var createResult = _tools.CreateSession("owner");
        var sessionId = ExtractSessionId(createResult);

        // Act
        var result = _tools.GetDebuggerInfo(sessionId, "wrong-user");

        // Assert - SessionTools now catches exceptions and returns error strings
        Assert.StartsWith("Error:", result);
        Assert.Contains("does not have access", result);
    }

    // ============================================================
    // Helper Methods
    // ============================================================

    private static string ExtractSessionId(string createResult)
    {
        // Parse "SessionId: xxx" from the result
        var start = createResult.IndexOf("SessionId: ") + "SessionId: ".Length;
        var end = createResult.IndexOf(".", start);
        if (end < 0) end = createResult.Length;
        return createResult[start..end].Trim();
    }
}

