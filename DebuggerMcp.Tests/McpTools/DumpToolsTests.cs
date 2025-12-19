using DebuggerMcp;
using DebuggerMcp.McpTools;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.McpTools;

/// <summary>
/// Tests for DumpTools MCP tool class.
/// </summary>
public class DumpToolsTests : IDisposable
{
    private readonly string _tempPath;
    private readonly DebuggerSessionManager _sessionManager;
    private readonly SymbolManager _symbolManager;
    private readonly WatchStore _watchStore;
    private readonly DumpTools _tools;
    private const string DumpId = "dump-123";

    public DumpToolsTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempPath);

        _sessionManager = new DebuggerSessionManager(_tempPath, debuggerFactory: _ => new FakeSosDebuggerManager());
        _symbolManager = new SymbolManager(_tempPath);
        _watchStore = new WatchStore(_tempPath);
        _tools = new DumpTools(_sessionManager, _symbolManager, _watchStore, NullLogger<DumpTools>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            try { Directory.Delete(_tempPath, true); } catch { }
        }
    }

    // ============================================================
    // OpenDump Tests
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task OpenDump_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.OpenDump(sessionId!, "user", "dump-id"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task OpenDump_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.OpenDump(sessionId, userId!, "dump-id"));
    }

    [Fact]
    public async Task OpenDump_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _tools.OpenDump(sessionId, "wrong-user", "dump-id"));
    }

    [Fact]
    public async Task OpenDump_WithNonExistentDump_ThrowsFileNotFoundException()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _tools.OpenDump(sessionId, userId, "non-existent-dump"));
    }

    [Fact]
    public async Task OpenDump_WithPathTraversalInDumpId_ThrowsArgumentException()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.OpenDump(sessionId, userId, "../etc/passwd"));
    }

    // ============================================================
    // CloseDump Tests
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CloseDump_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _tools.CloseDump(sessionId!, "user"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CloseDump_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _tools.CloseDump(sessionId, userId!));
    }

    [Fact]
    public void CloseDump_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        Assert.Throws<UnauthorizedAccessException>(() => _tools.CloseDump(sessionId, "wrong-user"));
    }

    [Fact]
    public void CloseDump_WithNonExistentSession_ThrowsInvalidOperationException()
    {
        // Act & Assert - Trying to close dump on non-existent session throws
        Assert.Throws<InvalidOperationException>(() =>
            _tools.CloseDump("non-existent-session", "user"));
    }

    // Note: Test for CloseDump with valid session and initialized debugger 
    // requires integration tests because it needs an actual running debugger.

    // ============================================================
    // ExecuteCommand Tests
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExecuteCommand_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _tools.ExecuteCommand(sessionId!, "user", "k"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExecuteCommand_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _tools.ExecuteCommand(sessionId, userId!, "k"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExecuteCommand_WithNullOrEmptyCommand_ThrowsArgumentException(string? command)
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _tools.ExecuteCommand(sessionId, userId, command!));
    }

    [Fact]
    public void ExecuteCommand_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        Assert.Throws<UnauthorizedAccessException>(() => _tools.ExecuteCommand(sessionId, "wrong-user", "k"));
    }

    // ============================================================
    // LoadSos Tests
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LoadSos_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _tools.LoadSos(sessionId!, "user"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LoadSos_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _tools.LoadSos(sessionId, userId!));
    }

    [Fact]
    public void LoadSos_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionId = _sessionManager.CreateSession("owner");

        // Act & Assert
        Assert.Throws<UnauthorizedAccessException>(() => _tools.LoadSos(sessionId, "wrong-user"));
    }

    [Fact]
    public void LoadSos_WhenReportIsCached_ClearsCachedReport()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        var manager = (FakeSosDebuggerManager)_sessionManager.GetSession(sessionId, userId);
        manager.IsDumpOpen = true;
        manager.IsDotNetDump = true;
        manager.IsSosLoaded = false;

        var session = _sessionManager.GetSessionInfo(sessionId, userId);
        session.CurrentDumpId = DumpId;
        session.SetCachedReport(DumpId, DateTime.UtcNow, "{ \"report\": 1 }", includesWatches: true, includesSecurity: true, maxStackFrames: 0);

        // Act
        var result = _tools.LoadSos(sessionId, userId);

        // Assert
        Assert.Contains("loaded successfully", result, StringComparison.OrdinalIgnoreCase);
        Assert.Null(session.CachedReportDumpId);
        Assert.True(manager.IsSosLoaded);
    }

    private sealed class FakeSosDebuggerManager : IDebuggerManager
    {
        public bool IsInitialized => true;
        public bool IsDumpOpen { get; set; }
        public string? CurrentDumpPath { get; set; }
        public string DebuggerType { get; set; } = "LLDB";
        public bool IsSosLoaded { get; set; }
        public bool IsDotNetDump { get; set; }

        public Task InitializeAsync() => Task.CompletedTask;
        public void OpenDumpFile(string dumpFilePath, string? executablePath = null) => throw new NotSupportedException();
        public void CloseDump() => IsDumpOpen = false;
        public string ExecuteCommand(string command) => throw new NotSupportedException();
        public void LoadSosExtension() => IsSosLoaded = true;
        public void ConfigureSymbolPath(string symbolPath) => throw new NotSupportedException();
        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
