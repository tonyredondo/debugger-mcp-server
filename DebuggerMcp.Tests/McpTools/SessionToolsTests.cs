using DebuggerMcp;
using DebuggerMcp.McpTools;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
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
    public void ListSessions_WithNoSessions_ReturnsJsonPayloadWithZeroSessions()
    {
        // Act
        var result = _tools.ListSessions("test-user");

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("test-user", doc.RootElement.GetProperty("userId").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("sessions").GetArrayLength());
    }

    [Fact]
    public void ListSessions_WithActiveSessions_ReturnsMachineReadableJson()
    {
        // Arrange
        var userId = "test-user";
        _tools.CreateSession(userId);
        _tools.CreateSession(userId);

        // Act
        var result = _tools.ListSessions(userId);

        // Assert
        using var doc = JsonDocument.Parse(result);
        Assert.Equal(userId, doc.RootElement.GetProperty("userId").GetString());
        Assert.True(doc.RootElement.GetProperty("total").GetInt32() >= 2);
        var sessions = doc.RootElement.GetProperty("sessions");
        Assert.Equal(JsonValueKind.Array, sessions.ValueKind);
        Assert.True(sessions.GetArrayLength() >= 2);

        var first = sessions[0];
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("sessionId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("createdAtUtc").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("lastActivityUtc").GetString()));
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

    // ============================================================
    // RestoreSession Tests
    // ============================================================

    [Fact]
    public void RestoreSession_WithExistingSession_ReturnsStatusMessage()
    {
        var userId = "test-user";
        var createResult = _tools.CreateSession(userId);
        var sessionId = ExtractSessionId(createResult);

        var result = _tools.RestoreSession(sessionId, userId);

        Assert.Contains("Session restored successfully", result);
        Assert.Contains(sessionId, result);
    }

    // ============================================================
    // LoadVerifyCoreModules Tests
    // ============================================================

    [Fact]
    public void LoadVerifyCoreModules_WithSession_ReturnsPlatformAppropriateMessage()
    {
        var userId = "test-user";
        var createResult = _tools.CreateSession(userId);
        var sessionId = ExtractSessionId(createResult);

        var result = _tools.LoadVerifyCoreModules(sessionId, userId);

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("only works with LLDB", result, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains("No modules were loaded", result, StringComparison.OrdinalIgnoreCase);
        }
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
