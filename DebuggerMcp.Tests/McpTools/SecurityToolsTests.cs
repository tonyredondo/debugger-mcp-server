using DebuggerMcp;
using DebuggerMcp.McpTools;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.McpTools;

/// <summary>
/// Tests for SecurityTools MCP tool class.
/// </summary>
public class SecurityToolsTests : IDisposable
{
    private readonly string _tempPath;
    private readonly DebuggerSessionManager _sessionManager;
    private readonly SymbolManager _symbolManager;
    private readonly WatchStore _watchStore;
    private readonly SecurityTools _tools;

    public SecurityToolsTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempPath);

        _sessionManager = new DebuggerSessionManager(_tempPath);
        _symbolManager = new SymbolManager(_tempPath);
        _watchStore = new WatchStore(_tempPath);
        _tools = new SecurityTools(_sessionManager, _symbolManager, _watchStore, NullLogger<SecurityTools>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            try { Directory.Delete(_tempPath, true); } catch { }
        }
    }

    // ============================================================
    // AnalyzeSecurity Tests - Input Validation
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnalyzeSecurity_WithNullOrEmptySessionId_ThrowsArgumentException(string? sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.AnalyzeSecurity(sessionId!, "user"));
    }

    [Fact]
    public async Task AnalyzeSecurity_WithPathTraversalInUserId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _tools.AnalyzeSecurity("session", "../etc/passwd"));
    }

    [Fact]
    public async Task AnalyzeSecurity_WithNonExistentSession_ThrowsInvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.AnalyzeSecurity("non-existent", "user"));
    }

    [Fact]
    public async Task AnalyzeSecurity_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        var sessionId = _sessionManager.CreateSession("owner");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _tools.AnalyzeSecurity(sessionId, "wrong-user"));
    }

    [Fact]
    public async Task AnalyzeSecurity_WithDumpNotOpen_ThrowsInvalidOperationException()
    {
        var userId = "test-user";
        var sessionId = _sessionManager.CreateSession(userId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _tools.AnalyzeSecurity(sessionId, userId));

        Assert.Contains("No dump file is open", ex.Message);
    }

    // ============================================================
    // GetSecurityCheckCapabilities Tests
    // ============================================================

    [Fact]
    public void GetSecurityCheckCapabilities_ReturnsValidJson()
    {
        var result = _tools.GetSecurityCheckCapabilities();

        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.StartsWith("{", result);
        Assert.EndsWith("}", result);
    }

    [Fact]
    public void GetSecurityCheckCapabilities_ContainsVulnerabilityTypes()
    {
        var result = _tools.GetSecurityCheckCapabilities();

        Assert.Contains("VulnerabilityTypes", result);
        Assert.Contains("BufferOverflow", result);
        Assert.Contains("UseAfterFree", result);
        Assert.Contains("DoubleFree", result);
        Assert.Contains("NullPointerDereference", result);
        Assert.Contains("HeapCorruption", result);
        Assert.Contains("StackCorruption", result);
        Assert.Contains("IntegerOverflow", result);
        Assert.Contains("FormatString", result);
        Assert.Contains("UninitializedMemory", result);
        Assert.Contains("TypeConfusion", result);
    }

    [Fact]
    public void GetSecurityCheckCapabilities_ContainsMemoryProtections()
    {
        var result = _tools.GetSecurityCheckCapabilities();

        Assert.Contains("MemoryProtections", result);
        Assert.Contains("ASLR", result);
        Assert.Contains("DEP/NX", result);
        Assert.Contains("StackCanary", result);
        Assert.Contains("SafeSEH", result);
        Assert.Contains("CFG", result);
    }

    [Fact]
    public void GetSecurityCheckCapabilities_ContainsExploitPatterns()
    {
        var result = _tools.GetSecurityCheckCapabilities();

        Assert.Contains("ExploitPatterns", result);
        Assert.Contains("ROP", result);
        Assert.Contains("Heap spray", result);
        Assert.Contains("Shell code", result);
    }

    [Fact]
    public void GetSecurityCheckCapabilities_ContainsSeverityLevels()
    {
        var result = _tools.GetSecurityCheckCapabilities();

        Assert.Contains("Critical", result);
        Assert.Contains("High", result);
        Assert.Contains("Medium", result);
    }

    [Fact]
    public void GetSecurityCheckCapabilities_DoesNotRequireSession()
    {
        // Create tools without any sessions
        var tools = new SecurityTools(_sessionManager, _symbolManager, _watchStore, NullLogger<SecurityTools>.Instance);

        // Should work without any session setup
        var result = tools.GetSecurityCheckCapabilities();

        Assert.NotEmpty(result);
    }
}

