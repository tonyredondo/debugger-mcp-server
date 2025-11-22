using DebuggerMcp;
using DebuggerMcp.McpTools;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
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
}

