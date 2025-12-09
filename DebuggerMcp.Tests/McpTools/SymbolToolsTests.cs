using DebuggerMcp;
using DebuggerMcp.McpTools;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DebuggerMcp.Tests.McpTools;

/// <summary>
/// Tests for SymbolTools MCP tool class.
/// </summary>
public class SymbolToolsTests : IDisposable
{
    private readonly string _tempPath;
    private readonly DebuggerSessionManager _sessionManager;
    private readonly SymbolManager _symbolManager;
    private readonly WatchStore _watchStore;
    private readonly SymbolTools _tools;

    public SymbolToolsTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempPath);

        _sessionManager = new DebuggerSessionManager(_tempPath);
        _symbolManager = new SymbolManager(_tempPath);
        _watchStore = new WatchStore(_tempPath);
        _tools = new SymbolTools(_sessionManager, _symbolManager, _watchStore, NullLogger<SymbolTools>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            try { Directory.Delete(_tempPath, true); } catch { }
        }
    }

    // ============================================================
    // GetSymbolServers Tests
    // ============================================================

    [Fact]
    public void GetSymbolServers_ReturnsSymbolServerInfo()
    {
        // Act
        var result = _tools.GetSymbolServers();

        // Assert
        Assert.Contains("Microsoft Symbol Server", result);
        Assert.Contains("NuGet Symbol Server", result);
        Assert.Contains("AUTO-CONFIGURED", result);
        Assert.Contains("msdl.microsoft.com", result);
        Assert.Contains("symbols.nuget.org", result);
    }

    [Fact]
    public void GetSymbolServers_ContainsUploadInstructions()
    {
        // Act
        var result = _tools.GetSymbolServers();

        // Assert
        Assert.Contains("Custom Symbols:", result);
        Assert.Contains("/api/symbols/upload", result);
    }

    // ============================================================
    // ConfigureAdditionalSymbols Tests
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConfigureAdditionalSymbols_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            _tools.ConfigureAdditionalSymbols(sessionId!, "user", "/path/to/symbols"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConfigureAdditionalSymbols_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            _tools.ConfigureAdditionalSymbols(sessionId, userId!, "/path/to/symbols"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConfigureAdditionalSymbols_WithNullOrEmptyPaths_ThrowsArgumentException(string? paths)
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            _tools.ConfigureAdditionalSymbols(sessionId, userId, paths!));
    }

    [Fact]
    public void ConfigureAdditionalSymbols_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        Assert.Throws<UnauthorizedAccessException>(() =>
            _tools.ConfigureAdditionalSymbols(sessionId, "wrong-user", "/path/to/symbols"));
    }

    [Fact]
    public void ConfigureAdditionalSymbols_WithNonExistentSession_ThrowsInvalidOperationException()
    {
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            _tools.ConfigureAdditionalSymbols("non-existent-session", "user", "/path/to/symbols"));
    }

    // Note: Tests that require an actual initialized debugger are skipped in unit tests
    // because they would need integration tests with real debugger processes.
    // The validation logic (session ID, user ID, paths) is tested above.
}
