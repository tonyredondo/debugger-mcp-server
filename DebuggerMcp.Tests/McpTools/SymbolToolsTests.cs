using DebuggerMcp;
using DebuggerMcp.Controllers;
using DebuggerMcp.McpTools;
using DebuggerMcp.Tests.TestDoubles;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
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

        _sessionManager = new DebuggerSessionManager(
            dumpStoragePath: _tempPath,
            sessionStoragePath: Path.Combine(_tempPath, "sessions"),
            debuggerFactory: _ => new FakeDebuggerManager());

        _symbolManager = new SymbolManager(symbolCacheBasePath: _tempPath, dumpStorageBasePath: _tempPath);
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

    [Fact]
    public void ConfigureAdditionalSymbols_WhenDebuggerIsWinDbg_ConfiguresWinDbgSymbolPath()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        var manager = (FakeDebuggerManager)_sessionManager.GetSession(sessionId, userId);
        manager.DebuggerType = "WinDbg";

        var localSymbols = Path.Combine(_tempPath, "local-symbols");

        // Act
        var result = _tools.ConfigureAdditionalSymbols(
            sessionId,
            userId,
            $"{localSymbols},{SymbolManager.NuGetSymbolServer}");

        // Assert
        Assert.Contains("Debugger: WinDbg", result);
        Assert.Contains("Added 2 additional path(s)", result);

        Assert.Single(manager.ConfiguredSymbolPaths);
        var configuredPath = manager.ConfiguredSymbolPaths[0];

        var cacheDir = Path.Combine(_tempPath, "cache");
        Assert.True(Directory.Exists(cacheDir));

        Assert.Contains($"srv*{cacheDir}*{SymbolManager.MicrosoftSymbolServer}", configuredPath);
        Assert.Contains($"srv*{cacheDir}*{SymbolManager.NuGetSymbolServer}", configuredPath);
        Assert.Contains(localSymbols, configuredPath);
    }

    [Fact]
    public void ConfigureAdditionalSymbols_WhenDebuggerIsLldbAndOnlyUrlsProvided_DoesNotConfigureDebuggerSymbolPath()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        var manager = (FakeDebuggerManager)_sessionManager.GetSession(sessionId, userId);
        manager.DebuggerType = "LLDB";

        // Act
        var result = _tools.ConfigureAdditionalSymbols(
            sessionId,
            userId,
            $"{SymbolManager.NuGetSymbolServer}");

        // Assert
        Assert.Contains("Debugger: LLDB", result);
        Assert.Empty(manager.ConfiguredSymbolPaths);
    }

    [Fact]
    public void ConfigureAdditionalSymbols_WhenDumpOpen_InvalidateCachedReportAndSourceLinkResolver()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        var manager = (FakeDebuggerManager)_sessionManager.GetSession(sessionId, userId);
        manager.DebuggerType = "WinDbg";
        manager.IsDumpOpen = true;

        var session = _sessionManager.GetSessionInfo(sessionId, userId);
        session.CurrentDumpId = "dump-123";
        session.SetCachedReport("dump-123", DateTime.UtcNow, "{ \"report\": 1 }", includesWatches: true, includesSecurity: true, maxStackFrames: 0);
        _ = session.GetOrCreateSourceLinkResolver("dump-123", () => new DebuggerMcp.SourceLink.SourceLinkResolver(NullLogger.Instance));
        Assert.NotNull(session.SourceLinkResolver);

        // Act
        _ = _tools.ConfigureAdditionalSymbols(sessionId, userId, "/tmp/symbols");

        // Assert
        Assert.Null(session.CachedReportDumpId);
        Assert.Null(session.SourceLinkResolver);
    }

    // ============================================================
    // ClearSymbolCache Tests
    // ============================================================

    [Fact]
    public void ClearSymbolCache_WhenCacheDoesNotExist_ReturnsIdempotentMessage()
    {
        // Arrange
        var userId = "test-user";
        var dumpId = "dump-123";

        // Act
        var result = _tools.ClearSymbolCache(userId, dumpId);

        // Assert
        Assert.Contains("No symbol cache found", result);
        Assert.Contains(dumpId, result);
    }

    [Fact]
    public void ClearSymbolCache_WhenCacheExists_DeletesCacheAndClearsMetadataSymbolFiles()
    {
        // Arrange
        var userId = "test-user";
        var dumpId = "dump-123";

        var userDir = Path.Combine(_tempPath, userId);
        Directory.CreateDirectory(userDir);

        // Create symbol cache directory that matches SymbolTools convention.
        var cacheDir = Path.Combine(userDir, $".symbols_{dumpId}");
        Directory.CreateDirectory(Path.Combine(cacheDir, "sub"));
        File.WriteAllText(Path.Combine(cacheDir, "a.pdb"), "x");
        File.WriteAllText(Path.Combine(cacheDir, "sub", "b.pdb"), "y");

        // Create dump + metadata so ClearSymbolFilesFromMetadata can invalidate the list.
        var dumpPath = Path.Combine(userDir, $"{dumpId}.dmp");
        File.WriteAllText(dumpPath, "dump");

        var metadataPath = Path.ChangeExtension(dumpPath, ".json");
        var metadata = new DumpMetadata
        {
            DumpId = dumpId,
            UserId = userId,
            SymbolFiles = new List<string> { "a.pdb", "sub/b.pdb" }
        };
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata));

        // Act
        var result = _tools.ClearSymbolCache(userId, dumpId);

        // Assert
        Assert.Contains("Symbol cache cleared", result);
        Assert.False(Directory.Exists(cacheDir));

        var updatedMetadataJson = File.ReadAllText(metadataPath);
        var updatedMetadata = JsonSerializer.Deserialize<DumpMetadata>(updatedMetadataJson);
        Assert.NotNull(updatedMetadata);
        Assert.Null(updatedMetadata!.SymbolFiles);
    }

    // ============================================================
    // ReloadSymbols Tests
    // ============================================================

    [Fact]
    public void ReloadSymbols_WhenNoSymbolDirectoryExists_ReturnsHelpfulMessage()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);
        var manager = (FakeDebuggerManager)_sessionManager.GetSession(sessionId, userId);
        manager.DebuggerType = "LLDB";
        manager.IsInitialized = true;
        manager.IsDumpOpen = true;

        var session = _sessionManager.GetSessionInfo(sessionId, userId);
        session.CurrentDumpId = "dump-123";

        // Act
        var result = _tools.ReloadSymbols(sessionId, userId);

        // Assert
        Assert.Contains("No symbol directory found", result);
    }

    [Fact]
    public void ReloadSymbols_WhenDebuggerIsLldb_AddsSearchPathsAndLoadsDbgFiles()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);
        var manager = (FakeDebuggerManager)_sessionManager.GetSession(sessionId, userId);
        manager.DebuggerType = "LLDB";
        manager.IsInitialized = true;
        manager.IsDumpOpen = true;
        manager.CommandHandler = _ => "ok";

        var session = _sessionManager.GetSessionInfo(sessionId, userId);
        session.CurrentDumpId = "dump-123";

        var symbolDir = Path.Combine(_tempPath, userId, ".symbols_dump-123");
        Directory.CreateDirectory(Path.Combine(symbolDir, "sub"));
        File.WriteAllText(Path.Combine(symbolDir, "a.dbg"), "x");
        File.WriteAllText(Path.Combine(symbolDir, "sub", "b.debug"), "y");
        File.WriteAllText(Path.Combine(symbolDir, "ignored.pdb"), "z");

        session.SetCachedReport("dump-123", DateTime.UtcNow, "{ \"report\": 1 }", includesWatches: true, includesSecurity: true, maxStackFrames: 0);
        _ = session.GetOrCreateSourceLinkResolver("dump-123", () => new DebuggerMcp.SourceLink.SourceLinkResolver(NullLogger.Instance));
        Assert.NotNull(session.SourceLinkResolver);

        // Act
        var result = _tools.ReloadSymbols(sessionId, userId);

        // Assert
        Assert.Contains("Symbol reload completed", result);
        Assert.Contains("Added 2 directories", result);
        Assert.Contains("Loaded 2 of 2 symbol files", result);

        Assert.Contains(manager.ExecutedCommands, c => c.StartsWith("settings append target.debug-file-search-paths "));
        Assert.Contains(manager.ExecutedCommands, c => c.StartsWith("target symbols add "));
        Assert.DoesNotContain(manager.ExecutedCommands, c => c.Contains("ignored.pdb", StringComparison.OrdinalIgnoreCase));

        Assert.Null(session.CachedReportDumpId);
        Assert.Null(session.SourceLinkResolver);
    }

    [Fact]
    public void ReloadSymbols_WhenDebuggerIsWinDbg_AppendsSymPathAndReloads()
    {
        // Arrange
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);
        var manager = (FakeDebuggerManager)_sessionManager.GetSession(sessionId, userId);
        manager.DebuggerType = "WinDbg";
        manager.IsInitialized = true;
        manager.IsDumpOpen = true;
        manager.CommandHandler = command =>
        {
            if (command == "lm")
            {
                return "mod1 foo.pdb\nmod2 bar.pdb\nmod3 nosym";
            }
            return "ok";
        };

        var session = _sessionManager.GetSessionInfo(sessionId, userId);
        session.CurrentDumpId = "dump-123";

        var symbolDir = Path.Combine(_tempPath, userId, ".symbols_dump-123");
        Directory.CreateDirectory(Path.Combine(symbolDir, "sub"));
        File.WriteAllText(Path.Combine(symbolDir, "a.pdb"), "x");
        File.WriteAllText(Path.Combine(symbolDir, "sub", "b.pdb"), "y");

        // Act
        var result = _tools.ReloadSymbols(sessionId, userId);

        // Assert
        Assert.Contains("Symbol reload completed", result);
        Assert.Contains("Executed .reload /f", result);
        Assert.Contains("Modules with symbols: 2", result);

        Assert.Contains(manager.ExecutedCommands, c => c.StartsWith(".sympath+ "));
        Assert.Contains(".reload /f", manager.ExecutedCommands);
        Assert.Contains("lm", manager.ExecutedCommands);
    }

    // Note: Tests that require an actual initialized debugger are skipped in unit tests
    // because they would need integration tests with real debugger processes.
    // The validation logic (session ID, user ID, paths) is tested above.
}
