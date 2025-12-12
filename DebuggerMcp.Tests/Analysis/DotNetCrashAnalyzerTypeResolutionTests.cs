using DebuggerMcp.Analysis;
using DebuggerMcp.SourceLink;
using Moq;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Tests for the higher-level type resolution analysis flow in <see cref="DotNetCrashAnalyzer"/>.
/// These tests avoid real dumps by stubbing debugger command outputs.
/// </summary>
public class DotNetCrashAnalyzerTypeResolutionTests
{
    private sealed class TestableDotNetCrashAnalyzer(
        IDebuggerManager debuggerManager,
        SourceLinkResolver? sourceLinkResolver = null,
        ClrMdAnalyzer? clrMdAnalyzer = null)
        : DotNetCrashAnalyzer(debuggerManager, sourceLinkResolver, clrMdAnalyzer)
    {
        public Task RunAnalyzeTypeResolutionAsync(CrashAnalysisResult result) => AnalyzeTypeResolutionAsync(result);
    }

    [Fact]
    public async Task AnalyzeTypeResolutionAsync_WhenGenericTypeNotFoundViaClrMd_UsesHeapSearchAndPopulatesAnalysis()
    {
        // Arrange
        var mt = "f7558924ae98";

        var heapCmd = "!dumpheap -stat -type ConcurrentDictionary";
        var dumpmtCmd = $"!dumpmt -md {mt}";

        var heapOutput = $@"MT    Count    TotalSize Class Name
{mt}     1        32 System.Collections.Concurrent.ConcurrentDictionary<System.String,System.Int32>";

        var dumpmtOutput = @"MethodDesc Table
Entry       MethodDesc    JIT     Slot      Name
00007FFD00000001 00007FFD00000002 NONE 00000001 System.Collections.Concurrent.ConcurrentDictionary`2.TryGetValue(!0, !1 ByRef)
00007FFD00000003 00007FFD00000004 NONE 00000002 System.Collections.Concurrent.ConcurrentDictionary`2.TryAdd(!0, !1)";

        var mockManager = new Mock<IDebuggerManager>(MockBehavior.Strict);
        mockManager.SetupGet(m => m.IsInitialized).Returns(true);
        mockManager.SetupGet(m => m.DebuggerType).Returns("WinDbg");
        mockManager
            .Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns<string>(cmd =>
            {
                if (cmd == heapCmd) return heapOutput;
                if (cmd == dumpmtCmd) return dumpmtOutput;
                return string.Empty;
            });

        var analyzer = new TestableDotNetCrashAnalyzer(mockManager.Object, sourceLinkResolver: null, clrMdAnalyzer: null);

        var result = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary { Recommendations = new List<string>() },
            Exception = new ExceptionDetails
            {
                Type = "System.MissingMethodException",
                Message = "Method not found: 'System.Boolean System.Collections.Concurrent.ConcurrentDictionary`2.TryGetValue(!0, !1 ByRef)'"
            },
            RawCommands = new Dictionary<string, string>()
        };

        // Act
        await analyzer.RunAnalyzeTypeResolutionAsync(result);

        // Assert
        Assert.NotNull(result.Exception!.Analysis);
        Assert.NotNull(result.Exception.Analysis!.TypeResolution);

        var analysis = result.Exception.Analysis.TypeResolution!;
        Assert.Equal("System.Collections.Concurrent.ConcurrentDictionary`2", analysis.FailedType);
        Assert.NotNull(analysis.ExpectedMember);
        Assert.Equal("TryGetValue", analysis.ExpectedMember!.Name);
        Assert.True(analysis.MethodFound);
        Assert.NotNull(analysis.ActualMethods);
        Assert.Contains(analysis.ActualMethods!, m => m.Name == "TryGetValue");

        Assert.Contains(result.RawCommands!, kvp => kvp.Key == heapCmd);
        Assert.Contains(result.RawCommands!, kvp => kvp.Key == dumpmtCmd);
    }
}

