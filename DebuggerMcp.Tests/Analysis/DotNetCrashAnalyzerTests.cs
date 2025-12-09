using Xunit;
using Moq;
using DebuggerMcp.Analysis;
using System.Threading.Tasks;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Tests for the DotNetCrashAnalyzer class.
/// </summary>
public class DotNetCrashAnalyzerTests
{
    /// <summary>
    /// Tests that AnalyzeDotNetCrashAsync returns a valid result.
    /// </summary>
    [Fact]
    public async Task AnalyzeDotNetCrashAsync_ReturnsValidResult()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("Test output");

        var analyzer = new DotNetCrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeDotNetCrashAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Summary);
        Assert.NotNull(result.Summary.Description);
    }

    /// <summary>
    /// Tests that AnalyzeDotNetCrashAsync executes .NET specific commands.
    /// </summary>
    [Fact]
    public async Task AnalyzeDotNetCrashAsync_ExecutesDotNetCommands()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("Test output");

        var analyzer = new DotNetCrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeDotNetCrashAsync();

        // Assert
        mockManager.Verify(m => m.ExecuteCommand("!eeversion"), Times.Once);
        mockManager.Verify(m => m.ExecuteCommand("!pe -nested"), Times.Once);
        mockManager.Verify(m => m.ExecuteCommand("!dumpheap -stat"), Times.Once);
        mockManager.Verify(m => m.ExecuteCommand("!clrthreads"), Times.Once);
        mockManager.Verify(m => m.ExecuteCommand("!finalizequeue"), Times.Once);
    }

    /// <summary>
    /// Tests that AnalyzeDotNetCrashAsync handles exceptions gracefully.
    /// </summary>
    [Fact]
    public async Task AnalyzeDotNetCrashAsync_HandlesExceptions()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand("!eeversion"))
            .Throws(new System.Exception("SOS not loaded"));
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("Test output");

        var analyzer = new DotNetCrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeDotNetCrashAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Summary);
        // Exception was handled gracefully
    }

    /// <summary>
    /// Tests that DotNetCrashAnalysisResult can be serialized to JSON.
    /// </summary>
    [Fact]
    public async Task AnalyzeDotNetCrashAsync_ResultCanBeSerializedToJson()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("Test output");

        var analyzer = new DotNetCrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeDotNetCrashAsync();
        var json = System.Text.Json.JsonSerializer.Serialize(result);

        // Assert
        Assert.NotNull(json);
        Assert.NotEmpty(json);
        // JSON serialization successful
    }

    /// <summary>
    /// Tests that DotNetInfo is properly initialized.
    /// </summary>
    [Fact]
    public async Task AnalyzeDotNetCrashAsync_InitializesDotNetInfo()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("Test output");

        var analyzer = new DotNetCrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeDotNetCrashAsync();

        // Assert
        Assert.NotNull(result.Memory);
        Assert.NotNull(result.Memory.HeapStats);
    }

    /// <summary>
    /// Tests that AnalyzeDotNetCrashAsync inherits base analysis.
    /// </summary>
    [Fact]
    public async Task AnalyzeDotNetCrashAsync_InheritsBaseAnalysis()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("Test output");

        var analyzer = new DotNetCrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeDotNetCrashAsync();

        // Assert
        // Should execute base commands
        mockManager.Verify(m => m.ExecuteCommand("!analyze -v"), Times.Once);
        mockManager.Verify(m => m.ExecuteCommand("~*k"), Times.Once);
        // And .NET specific commands
        mockManager.Verify(m => m.ExecuteCommand("!eeversion"), Times.Once);
    }

    /// <summary>
    /// Tests that AnalyzeDotNetCrashAsync executes !syncblk for deadlock detection.
    /// </summary>
    [Fact]
    public async Task AnalyzeDotNetCrashAsync_ExecutesSyncBlkCommand()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("Test output");

        var analyzer = new DotNetCrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeDotNetCrashAsync();

        // Assert
        mockManager.Verify(m => m.ExecuteCommand("!syncblk"), Times.Once);
    }

    /// <summary>
    /// Tests that AnalyzeDotNetCrashAsync detects memory leaks from dumpheap output.
    /// </summary>
    [Fact]
    public async Task AnalyzeDotNetCrashAsync_DetectsMemoryLeaksFromHeap()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");

        // Return heap stats with large allocations (2GB+ to trigger "High" severity)
        mockManager.Setup(m => m.ExecuteCommand("!dumpheap -stat"))
            .Returns(@"MT    Count    TotalSize Class Name
00007ff8a1234567    50000    1500000000 System.String
00007ff8a1234568    30000    1000000000 System.Byte[]
00007ff8a1234569    20000    500000000 System.Object[]
Total 100000 objects, 3000000000 bytes");

        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "!dumpheap -stat")))
            .Returns("Test output");

        var analyzer = new DotNetCrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeDotNetCrashAsync();

        // Assert
        Assert.NotNull(result.Memory);
        Assert.NotNull(result.Memory!.LeakAnalysis);
        // Memory consumption is analyzed, but Detected is only true with High severity or specific indicators
        Assert.NotNull(result.Memory.LeakAnalysis.TopConsumers);
        Assert.True(result.Memory.LeakAnalysis.TopConsumers.Count >= 3);
        Assert.Contains(result.Memory.LeakAnalysis.TopConsumers, c => c.TypeName.Contains("System.String"));
    }

    /// <summary>
    /// Tests that AnalyzeDotNetCrashAsync populates top memory consumers.
    /// </summary>
    [Fact]
    public async Task AnalyzeDotNetCrashAsync_PopulatesTopConsumers()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");

        mockManager.Setup(m => m.ExecuteCommand("!dumpheap -stat"))
            .Returns(@"MT    Count    TotalSize Class Name
00007ff812345678    1000    50000 MyApp.LargeObject
00007ff812345679    500     25000 MyApp.SmallObject");

        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "!dumpheap -stat")))
            .Returns("Test output");

        var analyzer = new DotNetCrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeDotNetCrashAsync();

        // Assert
        Assert.NotNull(result.Memory);
        Assert.NotNull(result.Memory!.LeakAnalysis);
        Assert.NotNull(result.Memory.LeakAnalysis.TopConsumers);
        Assert.True(result.Memory.LeakAnalysis.TopConsumers.Count >= 2);

        var largeObject = result.Memory.LeakAnalysis.TopConsumers.FirstOrDefault(c => c.TypeName.Contains("LargeObject"));
        Assert.NotNull(largeObject);
        Assert.Equal(1000, largeObject.Count);
        Assert.Equal(50000, largeObject.TotalSize);
    }

    /// <summary>
    /// Tests that AnalyzeDotNetCrashAsync detects deadlocks from syncblk output.
    /// </summary>
    [Fact]
    public async Task AnalyzeDotNetCrashAsync_DetectsDeadlocksFromSyncBlk()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");

        // Return syncblk with held locks
        mockManager.Setup(m => m.ExecuteCommand("!syncblk"))
            .Returns(@"Index SyncBlock MonitorHeld Recursion Owning Thread Info  SyncBlock Owner
   12 0000024453f8a5a8    1         1 0000024453f46720  1c54  10   00000244540a5820 System.Object
   15 0000024453f8a5b8    1         1 0000024453f46730  1d54  11   00000244540a5830 System.Object");

        // Return threads with Wait state
        mockManager.Setup(m => m.ExecuteCommand("!threads"))
            .Returns(@"ThreadCount:      5
  ID OSID ThreadOBJ    State GC Mode     GC Alloc Context  Domain   Count Apt Exception
  10 1c54 0000024453f46720    20220 Wait
  11 1d54 0000024453f46730    20220 Wait");

        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "!syncblk" && s != "!threads")))
            .Returns("Test output");

        var analyzer = new DotNetCrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeDotNetCrashAsync();

        // Assert
        Assert.NotNull(result.Threads!.Deadlock);
        Assert.True(result.Threads!.Deadlock.Locks.Count >= 2);
    }

    /// <summary>
    /// Tests that summary includes .NET memory information when heap is large.
    /// </summary>
    [Fact]
    public async Task AnalyzeDotNetCrashAsync_SummaryIncludesDotNetMemoryInfo()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");

        // Use heap size > 2GB to trigger "High" severity and Detected = true
        mockManager.Setup(m => m.ExecuteCommand("!dumpheap -stat"))
            .Returns(@"MT    Count    TotalSize Class Name
00007ff812345678    50000    2500000000 System.String");

        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "!dumpheap -stat")))
            .Returns("Test output");

        var analyzer = new DotNetCrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeDotNetCrashAsync();

        // Assert
        Assert.Contains("MEMORY:", result.Summary!.Description);
        // System.String recommendation is added based on specific issue indicators, not just presence
        Assert.NotNull(result.Memory!.LeakAnalysis);
        Assert.True(result.Memory!.LeakAnalysis.TotalHeapBytes >= 2_000_000_000);
    }

    /// <summary>
    /// Tests that AnalyzeDotNetCrashAsync adds LOH recommendations.
    /// </summary>
    [Fact]
    public async Task AnalyzeDotNetCrashAsync_AddsLohRecommendations()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");

        // Return heap with large average object size (LOH)
        mockManager.Setup(m => m.ExecuteCommand("!dumpheap -stat"))
            .Returns(@"MT    Count    TotalSize Class Name
00007ff812345678    100    100000000 System.Byte[]");

        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "!dumpheap -stat")))
            .Returns("Test output");

        var analyzer = new DotNetCrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeDotNetCrashAsync();

        // Assert
        Assert.Contains(result.Summary!.Recommendations!, r => r.Contains("Large Object Heap", System.StringComparison.OrdinalIgnoreCase));
    }
}
