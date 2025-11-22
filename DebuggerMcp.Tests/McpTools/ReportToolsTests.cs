using DebuggerMcp;
using DebuggerMcp.McpTools;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.McpTools;

/// <summary>
/// Tests for ReportTools MCP tool class.
/// </summary>
public class ReportToolsTests : IDisposable
{
    private readonly string _tempPath;
    private readonly DebuggerSessionManager _sessionManager;
    private readonly SymbolManager _symbolManager;
    private readonly WatchStore _watchStore;
    private readonly ReportTools _tools;

    public ReportToolsTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempPath);
        
        _sessionManager = new DebuggerSessionManager(_tempPath);
        _symbolManager = new SymbolManager(_tempPath);
        _watchStore = new WatchStore(_tempPath);
        _tools = new ReportTools(_sessionManager, _symbolManager, _watchStore, NullLogger<ReportTools>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            try { Directory.Delete(_tempPath, true); } catch { }
        }
    }

    // ============================================================
    // GenerateReport Tests - Input Validation
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GenerateReport_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.GenerateReport(sessionId!, "user"));
    }

    [Fact]
    public async Task GenerateReport_WithPathTraversalInUserId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.GenerateReport("session", "../etc/passwd"));
    }

    [Fact]
    public async Task GenerateReport_WithNonExistentSession_ThrowsInvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.GenerateReport("non-existent", "user"));
    }

    [Fact]
    public async Task GenerateReport_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        var sessionId = _sessionManager.CreateSession("owner");
        
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => 
            _tools.GenerateReport(sessionId, "wrong-user"));
    }

    [Fact]
    public async Task GenerateReport_WithDumpNotOpen_ThrowsInvalidOperationException()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);
        
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.GenerateReport(sessionId, userId));
        
        Assert.Contains("No dump file is open", ex.Message);
    }

    [Theory]
    [InlineData("markdown")]
    [InlineData("html")]
    [InlineData("json")]
    [InlineData("MARKDOWN")]
    [InlineData("HTML")]
    [InlineData("JSON")]
    public async Task GenerateReport_WithValidFormat_FailsAtDumpNotOpen(string format)
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);
        
        // Should fail at dump validation, not format parsing
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.GenerateReport(sessionId, userId, format));
        
        Assert.Contains("No dump file is open", ex.Message);
    }

    [Fact]
    public async Task GenerateReport_WithInvalidFormat_DefaultsToMarkdown_FailsAtDumpNotOpen()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);
        
        // Invalid format should default to markdown and fail at dump validation
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.GenerateReport(sessionId, userId, "invalid-format"));
        
        Assert.Contains("No dump file is open", ex.Message);
    }

    [Fact]
    public async Task GenerateReport_WithIncludeWatchesFalse_FailsAtDumpNotOpen()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);
        
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.GenerateReport(sessionId, userId, includeWatches: false));
        
        Assert.Contains("No dump file is open", ex.Message);
    }

    [Fact]
    public async Task GenerateReport_WithIncludeSecurityFalse_FailsAtDumpNotOpen()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);
        
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.GenerateReport(sessionId, userId, includeSecurity: false));
        
        Assert.Contains("No dump file is open", ex.Message);
    }

    [Fact]
    public async Task GenerateReport_WithCustomMaxStackFrames_FailsAtDumpNotOpen()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);
        
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.GenerateReport(sessionId, userId, maxStackFrames: 50));
        
        Assert.Contains("No dump file is open", ex.Message);
    }

    // ============================================================
    // GenerateSummaryReport Tests - Input Validation
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GenerateSummaryReport_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.GenerateSummaryReport(sessionId!, "user"));
    }

    [Fact]
    public async Task GenerateSummaryReport_WithPathTraversalInUserId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _tools.GenerateSummaryReport("session", "..\\..\\Windows"));
    }

    [Fact]
    public async Task GenerateSummaryReport_WithNonExistentSession_ThrowsInvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.GenerateSummaryReport("non-existent", "user"));
    }

    [Fact]
    public async Task GenerateSummaryReport_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        var sessionId = _sessionManager.CreateSession("owner");
        
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => 
            _tools.GenerateSummaryReport(sessionId, "wrong-user"));
    }

    [Fact]
    public async Task GenerateSummaryReport_WithDumpNotOpen_ThrowsInvalidOperationException()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);
        
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.GenerateSummaryReport(sessionId, userId));
        
        Assert.Contains("No dump file is open", ex.Message);
    }

    [Theory]
    [InlineData("markdown")]
    [InlineData("html")]
    [InlineData("json")]
    public async Task GenerateSummaryReport_WithValidFormat_FailsAtDumpNotOpen(string format)
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);
        
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.GenerateSummaryReport(sessionId, userId, format));
        
        Assert.Contains("No dump file is open", ex.Message);
    }

    [Fact]
    public async Task GenerateSummaryReport_WithInvalidFormat_DefaultsToMarkdown_FailsAtDumpNotOpen()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);
        
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _tools.GenerateSummaryReport(sessionId, userId, "pdf"));
        
        Assert.Contains("No dump file is open", ex.Message);
    }
}

