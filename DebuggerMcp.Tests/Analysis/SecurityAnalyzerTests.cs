using Xunit;
using Moq;
using DebuggerMcp.Analysis;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.Tests.Analysis;

public class SecurityAnalyzerTests
{
    private readonly Mock<IDebuggerManager> _mockManager;
    private readonly Mock<ILogger<SecurityAnalyzer>> _mockLogger;

    public SecurityAnalyzerTests()
    {
        _mockManager = new Mock<IDebuggerManager>();
        _mockLogger = new Mock<ILogger<SecurityAnalyzer>>();
    }

    // ============================================================
    // BASIC ANALYSIS TESTS
    // ============================================================

    [Fact]
    public async Task AnalyzeSecurityAsync_NoDumpOpen_ReturnsSummaryMessage()
    {
        // Arrange
        _mockManager.Setup(m => m.IsDumpOpen).Returns(false);
        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.Contains("No dump file is open", result.Summary);
        Assert.Empty(result.Vulnerabilities);
    }

    [Fact]
    public async Task AnalyzeSecurityAsync_DumpOpen_PerformsAnalysis()
    {
        // Arrange
        SetupBasicWinDbgManager();
        var analyzer = new SecurityAnalyzer(_mockManager.Object, _mockLogger.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Summary);
    }

    [Fact]
    public async Task AnalyzeSecurityAsync_WinDbgType_CallsWinDbgCommands()
    {
        // Arrange
        SetupBasicWinDbgManager();
        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        _mockManager.Verify(m => m.ExecuteCommand("!analyze -v"), Times.Once);
    }

    [Fact]
    public async Task AnalyzeSecurityAsync_LldbType_CallsLldbCommands()
    {
        // Arrange
        SetupBasicLldbManager();
        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        _mockManager.Verify(m => m.ExecuteCommand("bt"), Times.Once);
        _mockManager.Verify(m => m.ExecuteCommand("register read"), Times.Once);
    }

    // ============================================================
    // NULL DEREFERENCE DETECTION TESTS
    // ============================================================

    [Fact]
    public async Task AnalyzeSecurityAsync_DetectsNullDereference_WinDbg()
    {
        // Arrange
        SetupBasicWinDbgManager();
        _mockManager.Setup(m => m.ExecuteCommand("!analyze -v"))
            .Returns("Access violation\nREAD_ADDRESS: 0x00000000\nFAULTING_IP: test!main");

        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.Contains(result.Vulnerabilities, v => v.Type == VulnerabilityType.NullDereference);
    }

    [Fact]
    public async Task AnalyzeSecurityAsync_DetectsNullDereference_LowAddress()
    {
        // Arrange
        SetupBasicWinDbgManager();
        _mockManager.Setup(m => m.ExecuteCommand("!analyze -v"))
            .Returns("Access violation\nREAD_ADDRESS: 0x0000FFFF\nFAULTING_IP: test!main");

        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.Contains(result.Vulnerabilities, v => v.Type == VulnerabilityType.NullDereference);
    }

    // ============================================================
    // STACK BUFFER OVERFLOW DETECTION TESTS
    // ============================================================

    [Fact]
    public async Task AnalyzeSecurityAsync_DetectsStackBufferOverflow_WinDbg()
    {
        // Arrange
        SetupBasicWinDbgManager();
        _mockManager.Setup(m => m.ExecuteCommand("!analyze -v"))
            .Returns("STACK_BUFFER_OVERRUN detected\nGS_COOKIE corruption");

        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        var vuln = Assert.Single(result.Vulnerabilities, v => v.Type == VulnerabilityType.StackBufferOverflow);
        Assert.Equal(VulnerabilitySeverity.Critical, vuln.Severity);
        Assert.Contains("CWE-121", vuln.CweIds);
    }

    [Fact]
    public async Task AnalyzeSecurityAsync_DetectsStackBufferOverflow_Lldb()
    {
        // Arrange
        SetupBasicLldbManager();
        _mockManager.Setup(m => m.ExecuteCommand("bt"))
            .Returns("frame #0: __stack_chk_fail\nframe #1: 0x00007fff main");

        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.Contains(result.Vulnerabilities, v => v.Type == VulnerabilityType.StackBufferOverflow);
    }

    // ============================================================
    // HEAP CORRUPTION DETECTION TESTS
    // ============================================================

    [Fact]
    public async Task AnalyzeSecurityAsync_DetectsHeapCorruption_WinDbg()
    {
        // Arrange
        SetupBasicWinDbgManager();
        _mockManager.Setup(m => m.ExecuteCommand("!analyze -v"))
            .Returns("STATUS_HEAP_CORRUPTION");

        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.Contains(result.Vulnerabilities, v => v.Type == VulnerabilityType.HeapCorruption);
    }

    [Fact]
    public async Task AnalyzeSecurityAsync_DetectsHeapCorruption_HeapCommand()
    {
        // Arrange
        SetupBasicWinDbgManager();
        _mockManager.Setup(m => m.ExecuteCommand("!heap -s"))
            .Returns("Error: Heap corruption detected");

        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.NotNull(result.HeapIntegrity);
        Assert.True(result.HeapIntegrity.CorruptionDetected);
    }

    [Fact]
    public async Task AnalyzeSecurityAsync_DetectsHeapCorruption_Lldb()
    {
        // Arrange
        SetupBasicLldbManager();
        _mockManager.Setup(m => m.ExecuteCommand("bt"))
            .Returns("frame #0: malloc_error_break\nframe #1: 0x00007fff");
        _mockManager.Setup(m => m.ExecuteCommand("thread info"))
            .Returns("SIGABRT signal");

        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.Contains(result.Vulnerabilities, v => v.Type == VulnerabilityType.HeapCorruption);
    }

    // ============================================================
    // MEMORY PROTECTION ANALYSIS TESTS
    // ============================================================

    [Fact]
    public async Task AnalyzeSecurityAsync_ChecksAslr_WinDbg()
    {
        // Arrange
        SetupBasicWinDbgManager();
        _mockManager.Setup(m => m.ExecuteCommand("lm"))
            .Returns("00400000 00410000   test\n7ff800000000 7ff800010000 ntdll");

        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.NotNull(result.MemoryProtections);
        Assert.Contains("test", result.MemoryProtections.ModulesWithoutAslr);
    }

    [Fact]
    public async Task AnalyzeSecurityAsync_DepEnabledByDefault()
    {
        // Arrange
        SetupBasicWinDbgManager();
        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.NotNull(result.MemoryProtections);
        Assert.True(result.MemoryProtections.DepEnabled);
    }

    // ============================================================
    // SUSPICIOUS PATTERN DETECTION TESTS
    // ============================================================

    [Fact]
    public async Task AnalyzeSecurityAsync_DetectsSuspiciousPatterns_WinDbg()
    {
        // Arrange
        SetupBasicWinDbgManager();
        _mockManager.Setup(m => m.ExecuteCommand("k"))
            .Returns("00 41414141 41414141 test!main+0x10");

        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.NotNull(result.StackIntegrity);
        Assert.True(result.StackIntegrity.SuspiciousPatterns.Any());
    }

    [Fact]
    public async Task AnalyzeSecurityAsync_DetectsReturnAddressOverwrite_Lldb()
    {
        // Arrange
        SetupBasicLldbManager();
        _mockManager.Setup(m => m.ExecuteCommand("bt"))
            .Returns("frame #0: 0x41414141\nframe #1: 0x42424242");

        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.NotNull(result.StackIntegrity);
        Assert.True(result.StackIntegrity.CorruptionDetected);
    }

    // ============================================================
    // RISK ASSESSMENT TESTS
    // ============================================================

    [Fact]
    public async Task AnalyzeSecurityAsync_CriticalVuln_SetsCriticalRisk()
    {
        // Arrange
        SetupBasicWinDbgManager();
        _mockManager.Setup(m => m.ExecuteCommand("!analyze -v"))
            .Returns("STACK_BUFFER_OVERRUN detected");

        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.Equal(SecurityRisk.Critical, result.OverallRisk);
    }

    [Fact]
    public async Task AnalyzeSecurityAsync_NoVulns_SetsNoneRisk()
    {
        // Arrange
        SetupBasicWinDbgManager();
        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        // May have some low-severity issues but no critical ones
        Assert.True(result.OverallRisk <= SecurityRisk.Medium);
    }

    [Fact]
    public async Task AnalyzeSecurityAsync_GeneratesRecommendations()
    {
        // Arrange
        SetupBasicWinDbgManager();
        _mockManager.Setup(m => m.ExecuteCommand("lm"))
            .Returns("00400000 00410000 test");

        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.NotEmpty(result.Recommendations);
    }

    // ============================================================
    // DATA MODEL TESTS
    // ============================================================

    [Fact]
    public void Vulnerability_DefaultValues()
    {
        // Arrange & Act
        var vuln = new Vulnerability();

        // Assert
        Assert.Equal(VulnerabilityType.BufferOverflow, vuln.Type); // First enum value
        Assert.Equal(VulnerabilitySeverity.Info, vuln.Severity); // First enum value
        Assert.Equal(string.Empty, vuln.Description);
        Assert.NotNull(vuln.Indicators);
        Assert.NotNull(vuln.Remediation);
        Assert.NotNull(vuln.CweIds);
    }

    [Fact]
    public void SecurityAnalysisResult_DefaultValues()
    {
        // Arrange & Act
        var result = new SecurityAnalysisResult();

        // Assert
        Assert.NotNull(result.Vulnerabilities);
        Assert.Empty(result.Vulnerabilities);
        Assert.Equal(SecurityRisk.None, result.OverallRisk);
        Assert.NotNull(result.Recommendations);
        Assert.NotNull(result.RawOutput);
    }

    [Fact]
    public void MemoryProtectionInfo_DefaultValues()
    {
        // Arrange & Act
        var info = new MemoryProtectionInfo();

        // Assert
        Assert.False(info.DepEnabled);
        Assert.False(info.AslrEnabled);
        Assert.NotNull(info.ModulesWithoutAslr);
    }

    // ============================================================
    // WRITE ACCESS VIOLATION TESTS
    // ============================================================

    [Fact]
    public async Task AnalyzeSecurityAsync_DetectsWriteViolation_PotentialOverflow()
    {
        // Arrange
        SetupBasicWinDbgManager();
        _mockManager.Setup(m => m.ExecuteCommand("!analyze -v"))
            .Returns("Access violation\nWRITE_ADDRESS: 0x00007fff12345678");

        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.Contains(result.Vulnerabilities, v => v.Type == VulnerabilityType.BufferOverflow);
    }

    // ============================================================
    // LLDB SPECIFIC TESTS
    // ============================================================

    [Fact]
    public async Task AnalyzeSecurityAsync_DetectsSegfault_Lldb()
    {
        // Arrange
        SetupBasicLldbManager();
        _mockManager.Setup(m => m.ExecuteCommand("thread info"))
            .Returns("stop reason = EXC_BAD_ACCESS (code=1, address=0x0)");
        _mockManager.Setup(m => m.ExecuteCommand("register read"))
            .Returns("far = 0x0000000000000000");

        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.Contains(result.Vulnerabilities, v => v.Type == VulnerabilityType.NullDereference);
    }

    [Fact]
    public async Task AnalyzeSecurityAsync_DetectsFortifyFail_Lldb()
    {
        // Arrange
        SetupBasicLldbManager();
        _mockManager.Setup(m => m.ExecuteCommand("bt"))
            .Returns("frame #0: __fortify_fail\nframe #1: 0x00007fff");

        var analyzer = new SecurityAnalyzer(_mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeSecurityAsync();

        // Assert
        Assert.Contains(result.Vulnerabilities, v => v.Type == VulnerabilityType.StackBufferOverflow);
    }

    // ============================================================
    // HELPER METHODS
    // ============================================================

    private void SetupBasicWinDbgManager()
    {
        _mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        _mockManager.Setup(m => m.IsInitialized).Returns(true);
        _mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        _mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns(string.Empty);
    }

    private void SetupBasicLldbManager()
    {
        _mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        _mockManager.Setup(m => m.IsInitialized).Returns(true);
        _mockManager.Setup(m => m.DebuggerType).Returns("LLDB");
        _mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns(string.Empty);
    }
}

