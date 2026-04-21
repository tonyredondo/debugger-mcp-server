using DebuggerMcp;
using DebuggerMcp.McpTools;
using DebuggerMcp.Tests.SourceLink;
using DebuggerMcp.Tests.TestDoubles;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace DebuggerMcp.Tests.McpTools;

/// <summary>
/// Tests for SourceLinkTools MCP tool class.
/// </summary>
public class SourceLinkToolsTests : IDisposable
{
    private readonly string _tempPath;
    private readonly DebuggerSessionManager _sessionManager;
    private readonly SymbolManager _symbolManager;
    private readonly WatchStore _watchStore;
    private readonly SourceLinkTools _tools;

    public SourceLinkToolsTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempPath);

        _sessionManager = new DebuggerSessionManager(_tempPath);
        _symbolManager = new SymbolManager(_tempPath);
        _watchStore = new WatchStore(_tempPath);
        _tools = new SourceLinkTools(_sessionManager, _symbolManager, _watchStore, NullLogger<SourceLinkTools>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            try { Directory.Delete(_tempPath, true); } catch { }
        }
    }

    // ============================================================
    // ResolveSourceLink Tests - Input Validation
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveSourceLink_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        Assert.Throws<ArgumentException>(() =>
            _tools.ResolveSourceLink(sessionId!, "user", "/path/to/file.cs"));
    }

    [Fact]
    public void ResolveSourceLink_WithPathTraversalInUserId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            _tools.ResolveSourceLink("session", "../etc/passwd", "/path/to/file.cs"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveSourceLink_WithNullOrEmptySourceFile_ThrowsArgumentException(string? sourceFile)
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        Assert.Throws<ArgumentException>(() =>
            _tools.ResolveSourceLink(sessionId, userId, sourceFile!));
    }

    [Fact]
    public void ResolveSourceLink_WithNonExistentSession_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _tools.ResolveSourceLink("non-existent", "user", "/path/to/file.cs"));
    }

    [Fact]
    public void ResolveSourceLink_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        var sessionId = _sessionManager.CreateSession("owner");

        Assert.Throws<UnauthorizedAccessException>(() =>
            _tools.ResolveSourceLink(sessionId, "wrong-user", "/path/to/file.cs"));
    }

    [Fact]
    public void ResolveSourceLink_WithValidInputButNoSourceLink_ReturnsHelpfulMessage()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        var result = _tools.ResolveSourceLink(sessionId, userId, "/path/to/file.cs");

        Assert.Contains("Could not resolve Source Link", result);
        Assert.Contains("Possible reasons", result);
        Assert.Contains("PDB files", result);
    }

    [Fact]
    public void ResolveSourceLink_WithLineNumber_IncludesLineInMessage()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        // Even though it won't resolve, the error message should work
        var result = _tools.ResolveSourceLink(sessionId, userId, "/path/to/file.cs", lineNumber: 42);

        Assert.Contains("Could not resolve Source Link", result);
    }

    [Fact]
    public void ResolveSourceLink_WithMatchingPortablePdbInDumpDirectory_ReturnsResolvedUrl()
    {
        var scenarioRoot = Path.Combine(_tempPath, "resolved-sourcelink");
        Directory.CreateDirectory(scenarioRoot);

        var symbolManager = new SymbolManager(scenarioRoot);
        var watchStore = new WatchStore(scenarioRoot);
        var fakeDebuggerManager = new FakeDebuggerManager
        {
            IsDumpOpen = true
        };

        var sessionManager = new DebuggerSessionManager(
            dumpStoragePath: scenarioRoot,
            debuggerFactory: _ => fakeDebuggerManager,
            symbolManager: symbolManager);

        var tools = new SourceLinkTools(
            sessionManager,
            symbolManager,
            watchStore,
            NullLogger<SourceLinkTools>.Instance);

        var userId = "test-user";
        var sessionId = sessionManager.CreateSession(userId);
        var session = sessionManager.GetSessionInfo(sessionId, userId);

        var dumpDirectory = Path.Combine(scenarioRoot, userId);
        Directory.CreateDirectory(dumpDirectory);

        var dumpId = "testdump";
        var dumpPath = Path.Combine(dumpDirectory, $"{dumpId}.dmp");
        File.WriteAllText(dumpPath, "placeholder dump");
        SourceLinkTestAssemblyBuilder.CompileAssemblyWithSourceLink(dumpDirectory, assemblyName: "ToolSourceLinkAssembly");

        fakeDebuggerManager.CurrentDumpPath = dumpPath;
        session.CurrentDumpId = dumpId;

        var result = tools.ResolveSourceLink(
            sessionId,
            userId,
            SourceLinkTestAssemblyBuilder.DefaultSourceFilePath,
            lineNumber: 42);

        Assert.Contains("Source Link URL:", result);
        Assert.Contains("https://github.com/user/repo/blob/abc123/src/TestClass.cs#L42", result);
    }

    // ============================================================
    // GetSourceLinkInfo Tests - Input Validation
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetSourceLinkInfo_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        Assert.Throws<ArgumentException>(() =>
            _tools.GetSourceLinkInfo(sessionId!, "user"));
    }

    [Fact]
    public void GetSourceLinkInfo_WithPathTraversalInUserId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            _tools.GetSourceLinkInfo("session", "..\\..\\Windows"));
    }

    [Fact]
    public void GetSourceLinkInfo_WithNonExistentSession_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _tools.GetSourceLinkInfo("non-existent", "user"));
    }

    [Fact]
    public void GetSourceLinkInfo_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        var sessionId = _sessionManager.CreateSession("owner");

        Assert.Throws<UnauthorizedAccessException>(() =>
            _tools.GetSourceLinkInfo(sessionId, "wrong-user"));
    }

    [Fact]
    public void GetSourceLinkInfo_WithValidSession_ReturnsJsonWithProviders()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        var result = _tools.GetSourceLinkInfo(sessionId, userId);

        Assert.NotNull(result);
        Assert.Contains("SupportedProviders", result);
        Assert.Contains("GitHub", result);
        Assert.Contains("GitLab", result);
        Assert.Contains("Azure DevOps", result);
        Assert.Contains("Bitbucket", result);
    }

    [Fact]
    public void GetSourceLinkInfo_WithValidSession_ReturnsJsonWithTips()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        var result = _tools.GetSourceLinkInfo(sessionId, userId);

        Assert.Contains("Tips", result);
        Assert.Contains("PublishRepositoryUrl", result);
        Assert.Contains("Microsoft.SourceLink", result);
    }

    [Fact]
    public void GetSourceLinkInfo_WithNoDumpOpen_HasEmptySymbolPaths()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        var result = _tools.GetSourceLinkInfo(sessionId, userId);

        Assert.Contains("HasSymbolPath", result);
        Assert.Contains("false", result);
    }

    [Fact]
    public void GetSourceLinkInfo_WithConfiguredDumpAndExtraLocalDirectory_ReportsEffectiveResolverPaths()
    {
        var scenarioRoot = Path.Combine(_tempPath, "sourcelink-info");
        Directory.CreateDirectory(scenarioRoot);

        var symbolManager = new SymbolManager(scenarioRoot);
        var watchStore = new WatchStore(scenarioRoot);
        var fakeDebuggerManager = new FakeDebuggerManager
        {
            IsDumpOpen = true
        };

        var sessionManager = new DebuggerSessionManager(
            dumpStoragePath: scenarioRoot,
            debuggerFactory: _ => fakeDebuggerManager,
            symbolManager: symbolManager);

        var tools = new SourceLinkTools(
            sessionManager,
            symbolManager,
            watchStore,
            NullLogger<SourceLinkTools>.Instance);

        var userId = "test-user";
        var sessionId = sessionManager.CreateSession(userId);
        var session = sessionManager.GetSessionInfo(sessionId, userId);

        var dumpDirectory = Path.Combine(scenarioRoot, userId);
        Directory.CreateDirectory(dumpDirectory);

        var dumpId = "testdump";
        var dumpPath = Path.Combine(dumpDirectory, $"{dumpId}.dmp");
        File.WriteAllText(dumpPath, "placeholder dump");

        var dumpSymbolsDirectory = Path.Combine(dumpDirectory, $".symbols_{dumpId}");
        Directory.CreateDirectory(dumpSymbolsDirectory);
        File.WriteAllText(Path.Combine(dumpSymbolsDirectory, "test.pdb"), "pdb");

        var extraSymbolsDirectory = Path.Combine(scenarioRoot, "extra symbols");
        Directory.CreateDirectory(extraSymbolsDirectory);

        fakeDebuggerManager.CurrentDumpPath = dumpPath;
        session.CurrentDumpId = dumpId;
        symbolManager.ConfigureSessionSymbolPaths(
            sessionId,
            dumpId,
            additionalPaths: extraSymbolsDirectory,
            includeMicrosoftSymbols: false,
            userId: userId,
            dumpPath: dumpPath);

        var result = tools.GetSourceLinkInfo(sessionId, userId);
        using var document = JsonDocument.Parse(result);

        var searchPaths = document.RootElement.GetProperty("SymbolSearchPaths")
            .EnumerateArray()
            .Select(element => element.GetString())
            .Where(path => path != null)
            .Cast<string>()
            .ToList();
        var effectiveLocalDirectories = document.RootElement.GetProperty("EffectiveLocalSymbolDirectories")
            .EnumerateArray()
            .Select(element => element.GetString())
            .Where(path => path != null)
            .Cast<string>()
            .ToList();

        Assert.Contains(dumpDirectory, searchPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(dumpSymbolsDirectory, searchPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(extraSymbolsDirectory, searchPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(dumpSymbolsDirectory, effectiveLocalDirectories, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(extraSymbolsDirectory, effectiveLocalDirectories, StringComparer.OrdinalIgnoreCase);
    }
}

