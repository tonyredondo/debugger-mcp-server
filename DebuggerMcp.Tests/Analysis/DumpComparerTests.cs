using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using DebuggerMcp;
using DebuggerMcp.Analysis;
using Moq;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Tests for the DumpComparer class.
/// </summary>
public class DumpComparerTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullBaselineManager_ThrowsArgumentNullException()
    {
        // Arrange
        var comparisonManager = new Mock<IDebuggerManager>().Object;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new DumpComparer(null!, comparisonManager));
    }

    [Fact]
    public void Constructor_WithNullComparisonManager_ThrowsArgumentNullException()
    {
        // Arrange
        var baselineManager = new Mock<IDebuggerManager>().Object;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new DumpComparer(baselineManager, null!));
    }

    [Fact]
    public void Constructor_WithValidManagers_Succeeds()
    {
        // Arrange
        var baselineManager = new Mock<IDebuggerManager>().Object;
        var comparisonManager = new Mock<IDebuggerManager>().Object;

        // Act
        var comparer = new DumpComparer(baselineManager, comparisonManager);

        // Assert
        Assert.NotNull(comparer);
    }

    #endregion

    #region CompareAsync Tests

    [Fact]
    public async Task CompareAsync_WithWinDbgManagers_ReturnsComparisonResult()
    {
        // Arrange
        var baselineMock = CreateMockWinDbgManager(
            heapOutput: "Committed bytes: 0x10000000",
            threadOutput: "   0  Id: 1234.5678 Suspend: 1 Teb: 00000000`12345678 Unfrozen",
            moduleOutput: "00007ff8`12340000 00007ff8`12345000   ntdll      (pdb symbols)");

        var comparisonMock = CreateMockWinDbgManager(
            heapOutput: "Committed bytes: 0x20000000",
            threadOutput: "   0  Id: 1234.5678 Suspend: 1 Teb: 00000000`12345678 Unfrozen\n   1  Id: 1234.9abc Suspend: 1 Teb: 00000000`12345abc Unfrozen",
            moduleOutput: "00007ff8`12340000 00007ff8`12345000   ntdll      (pdb symbols)\n00007ff8`22340000 00007ff8`22345000   newmodule      (pdb symbols)");

        var comparer = new DumpComparer(baselineMock.Object, comparisonMock.Object);

        // Act
        var result = await comparer.CompareAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("WinDbg", result.Baseline.DebuggerType);
        Assert.Equal("WinDbg", result.Comparison.DebuggerType);
        Assert.NotNull(result.HeapComparison);
        Assert.NotNull(result.ThreadComparison);
        Assert.NotNull(result.ModuleComparison);
        Assert.NotEmpty(result.Summary);
    }

    [Fact]
    public async Task CompareAsync_WithLldbManagers_ReturnsComparisonResult()
    {
        // Arrange
        var baselineMock = CreateMockLldbManager(
            memoryOutput: "[0x00000000-0x10000000) r-x",
            threadOutput: "* thread #1: tid = 0x1234, 0x00007fff12345678 libsystem_kernel.dylib`__psynch_cvwait",
            moduleOutput: "[  0] 12345678-1234-1234-1234-123456789ABC 0x0000000100000000 /usr/lib/dyld");

        var comparisonMock = CreateMockLldbManager(
            memoryOutput: "[0x00000000-0x20000000) r-x",
            threadOutput: "* thread #1: tid = 0x1234\n  thread #2: tid = 0x5678",
            moduleOutput: "[  0] 12345678-1234-1234-1234-123456789ABC 0x0000000100000000 /usr/lib/dyld\n[  1] 87654321-4321-4321-4321-CBA987654321 0x0000000200000000 /usr/lib/newlib.dylib");

        var comparer = new DumpComparer(baselineMock.Object, comparisonMock.Object);

        // Act
        var result = await comparer.CompareAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("LLDB", result.Baseline.DebuggerType);
        Assert.Equal("LLDB", result.Comparison.DebuggerType);
    }

    #endregion

    #region CompareHeapsAsync Tests

    [Fact]
    public async Task CompareHeapsAsync_WithMemoryGrowth_DetectsGrowth()
    {
        // Arrange
        var baselineMock = CreateMockWinDbgManager(
            heapOutput: "Committed bytes: 0x10000000", // 256MB
            threadOutput: "",
            moduleOutput: "");

        var comparisonMock = CreateMockWinDbgManager(
            heapOutput: "Committed bytes: 0x20000000", // 512MB
            threadOutput: "",
            moduleOutput: "");

        var comparer = new DumpComparer(baselineMock.Object, comparisonMock.Object);

        // Act
        var result = await comparer.CompareHeapsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(0x10000000, result.BaselineMemoryBytes);
        Assert.Equal(0x20000000, result.ComparisonMemoryBytes);
        Assert.Equal(0x10000000, result.MemoryDeltaBytes); // 256MB growth
        Assert.Equal(100, result.MemoryGrowthPercent); // 100% growth
        Assert.True(result.MemoryLeakSuspected); // >20% growth
    }

    [Fact]
    public async Task CompareHeapsAsync_WithMemoryDecrease_DoesNotSuspectLeak()
    {
        // Arrange
        var baselineMock = CreateMockWinDbgManager(
            heapOutput: "Committed bytes: 0x20000000", // 512MB
            threadOutput: "",
            moduleOutput: "");

        var comparisonMock = CreateMockWinDbgManager(
            heapOutput: "Committed bytes: 0x10000000", // 256MB
            threadOutput: "",
            moduleOutput: "");

        var comparer = new DumpComparer(baselineMock.Object, comparisonMock.Object);

        // Act
        var result = await comparer.CompareHeapsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(-0x10000000, result.MemoryDeltaBytes); // 256MB decrease
        Assert.False(result.MemoryLeakSuspected);
    }

    [Fact]
    public async Task CompareHeapsAsync_WithDotNetHeap_ParsesTypeStats()
    {
        // Arrange
        var baselineHeapOutput = @"Committed bytes: 0x10000000
=== .NET Heap Stats ===
00007ff812345678    1000    100000 System.String
00007ff812345680    500     50000 System.Object
Total 1500 objects, 150000 bytes";

        var comparisonHeapOutput = @"Committed bytes: 0x20000000
=== .NET Heap Stats ===
00007ff812345678    5000    500000 System.String
00007ff812345680    500     50000 System.Object
00007ff812345690    1000    100000 System.Byte[]
Total 6500 objects, 650000 bytes";

        var baselineMock = new Mock<IDebuggerManager>();
        baselineMock.Setup(m => m.DebuggerType).Returns("WinDbg");
        baselineMock.Setup(m => m.IsInitialized).Returns(true);
        baselineMock.Setup(m => m.ExecuteCommand("!heap -s")).Returns(baselineHeapOutput);
        baselineMock.Setup(m => m.ExecuteCommand("!dumpheap -stat")).Returns(baselineHeapOutput);

        var comparisonMock = new Mock<IDebuggerManager>();
        comparisonMock.Setup(m => m.DebuggerType).Returns("WinDbg");
        comparisonMock.Setup(m => m.IsInitialized).Returns(true);
        comparisonMock.Setup(m => m.ExecuteCommand("!heap -s")).Returns(comparisonHeapOutput);
        comparisonMock.Setup(m => m.ExecuteCommand("!dumpheap -stat")).Returns(comparisonHeapOutput);

        var comparer = new DumpComparer(baselineMock.Object, comparisonMock.Object);

        // Act
        var result = await comparer.CompareHeapsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.TypeGrowth);

        // System.String grew from 1000 to 5000 instances
        var stringGrowth = result.TypeGrowth.Find(t => t.TypeName == "System.String");
        Assert.NotNull(stringGrowth);
        Assert.Equal(1000, stringGrowth.BaselineCount);
        Assert.Equal(5000, stringGrowth.ComparisonCount);
        Assert.Equal(4000, stringGrowth.CountDelta);

        // System.Byte[] is new
        var byteArrayType = result.NewTypes.Find(t => t.TypeName == "System.Byte[]");
        Assert.NotNull(byteArrayType);
        Assert.Equal(1000, byteArrayType.Count);
    }

    #endregion

    #region CompareThreadsAsync Tests

    [Fact]
    public async Task CompareThreadsAsync_WithNewThreads_DetectsNewThreads()
    {
        // Arrange
        var baselineMock = CreateMockWinDbgManager(
            heapOutput: "",
            threadOutput: "   0  Id: 1234.1000 Suspend: 1 Teb: 00000000`12345678 Unfrozen",
            moduleOutput: "");

        var comparisonMock = CreateMockWinDbgManager(
            heapOutput: "",
            threadOutput: @"   0  Id: 1234.1000 Suspend: 1 Teb: 00000000`12345678 Unfrozen
   1  Id: 1234.2000 Suspend: 1 Teb: 00000000`12345abc Unfrozen
   2  Id: 1234.3000 Suspend: 1 Teb: 00000000`12345def Unfrozen",
            moduleOutput: "");

        var comparer = new DumpComparer(baselineMock.Object, comparisonMock.Object);

        // Act
        var result = await comparer.CompareThreadsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1, result.BaselineThreadCount);
        Assert.Equal(3, result.ComparisonThreadCount);
        Assert.Equal(2, result.ThreadCountDelta);
        Assert.Equal(2, result.NewThreads.Count);
    }

    [Fact]
    public async Task CompareThreadsAsync_WithTerminatedThreads_DetectsTerminatedThreads()
    {
        // Arrange
        var baselineMock = CreateMockWinDbgManager(
            heapOutput: "",
            threadOutput: @"   0  Id: 1234.1000 Suspend: 1 Teb: 00000000`12345678 Unfrozen
   1  Id: 1234.2000 Suspend: 1 Teb: 00000000`12345abc Unfrozen
   2  Id: 1234.3000 Suspend: 1 Teb: 00000000`12345def Unfrozen",
            moduleOutput: "");

        var comparisonMock = CreateMockWinDbgManager(
            heapOutput: "",
            threadOutput: "   0  Id: 1234.1000 Suspend: 1 Teb: 00000000`12345678 Unfrozen",
            moduleOutput: "");

        var comparer = new DumpComparer(baselineMock.Object, comparisonMock.Object);

        // Act
        var result = await comparer.CompareThreadsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.BaselineThreadCount);
        Assert.Equal(1, result.ComparisonThreadCount);
        Assert.Equal(-2, result.ThreadCountDelta);
        Assert.Equal(2, result.TerminatedThreads.Count);
    }

    [Fact]
    public async Task CompareThreadsAsync_WithMultipleWaitingThreads_DetectsPotentialDeadlock()
    {
        // Arrange - baseline has no threads waiting on locks
        var baselineMock = CreateMockWinDbgManager(
            heapOutput: "",
            threadOutput: @"   0  Id: 1234.1000 Suspend: 1 Teb: 00000000`12345678 Unfrozen
   1  Id: 1234.2000 Suspend: 1 Teb: 00000000`12345abc Unfrozen",
            moduleOutput: "");

        // Comparison has threads waiting (simulated via mock with state containing "wait")
        var comparisonMock = new Mock<IDebuggerManager>();
        comparisonMock.Setup(m => m.DebuggerType).Returns("LLDB");
        comparisonMock.Setup(m => m.IsInitialized).Returns(true);
        comparisonMock.Setup(m => m.ExecuteCommand("thread list")).Returns(
            @"* thread #1: tid = 0x1000, 0x00007fff12345678 libsystem_pthread.dylib`pthread_mutex_lock, stop reason = signal
  thread #2: tid = 0x2000, 0x00007fff12345679 libsystem_pthread.dylib`pthread_mutex_lock, stop reason = signal
  thread #3: tid = 0x3000, 0x00007fff12345680 libsystem_pthread.dylib`pthread_mutex_lock, stop reason = signal");
        comparisonMock.Setup(m => m.ExecuteCommand(It.Is<string>(cmd => cmd != "thread list"))).Returns("");

        var comparer = new DumpComparer(baselineMock.Object, comparisonMock.Object);

        // Act
        var result = await comparer.CompareThreadsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.True(result.ThreadsWaitingOnLocks.Count >= 2);
        Assert.True(result.PotentialDeadlock);
    }

    #endregion

    #region CompareModulesAsync Tests

    [Fact]
    public async Task CompareModulesAsync_WithNewModules_DetectsNewModules()
    {
        // Arrange
        var baselineMock = CreateMockWinDbgManager(
            heapOutput: "",
            threadOutput: "",
            moduleOutput: "00007ff8`12340000 00007ff8`12345000   ntdll      (pdb symbols)");

        var comparisonMock = CreateMockWinDbgManager(
            heapOutput: "",
            threadOutput: "",
            moduleOutput: @"00007ff8`12340000 00007ff8`12345000   ntdll      (pdb symbols)
00007ff8`22340000 00007ff8`22345000   kernel32   (pdb symbols)
00007ff8`32340000 00007ff8`32345000   newplugin  (pdb symbols)");

        var comparer = new DumpComparer(baselineMock.Object, comparisonMock.Object);

        // Act
        var result = await comparer.CompareModulesAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1, result.BaselineModuleCount);
        Assert.Equal(3, result.ComparisonModuleCount);
        Assert.Equal(2, result.ModuleCountDelta);
        Assert.Equal(2, result.NewModules.Count);
    }

    [Fact]
    public async Task CompareModulesAsync_WithUnloadedModules_DetectsUnloadedModules()
    {
        // Arrange
        var baselineMock = CreateMockWinDbgManager(
            heapOutput: "",
            threadOutput: "",
            moduleOutput: @"00007ff8`12340000 00007ff8`12345000   ntdll      (pdb symbols)
00007ff8`22340000 00007ff8`22345000   kernel32   (pdb symbols)
00007ff8`32340000 00007ff8`32345000   oldplugin  (pdb symbols)");

        var comparisonMock = CreateMockWinDbgManager(
            heapOutput: "",
            threadOutput: "",
            moduleOutput: "00007ff8`12340000 00007ff8`12345000   ntdll      (pdb symbols)");

        var comparer = new DumpComparer(baselineMock.Object, comparisonMock.Object);

        // Act
        var result = await comparer.CompareModulesAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.BaselineModuleCount);
        Assert.Equal(1, result.ComparisonModuleCount);
        Assert.Equal(-2, result.ModuleCountDelta);
        Assert.Equal(2, result.UnloadedModules.Count);
    }

    [Fact]
    public async Task CompareModulesAsync_WithRebasedModules_DetectsRebasedModules()
    {
        // Arrange
        var baselineMock = CreateMockWinDbgManager(
            heapOutput: "",
            threadOutput: "",
            moduleOutput: "00007ff8`12340000 00007ff8`12345000   ntdll      (pdb symbols)");

        var comparisonMock = CreateMockWinDbgManager(
            heapOutput: "",
            threadOutput: "",
            moduleOutput: "00007ff8`AAAA0000 00007ff8`AAAA5000   ntdll      (pdb symbols)");

        var comparer = new DumpComparer(baselineMock.Object, comparisonMock.Object);

        // Act
        var result = await comparer.CompareModulesAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Single(result.RebasedModules);
        Assert.Equal("ntdll", result.RebasedModules[0].Name);
        Assert.Contains("12340000", result.RebasedModules[0].BaselineBaseAddress);
        Assert.Contains("AAAA0000", result.RebasedModules[0].ComparisonBaseAddress.ToUpperInvariant());
    }

    #endregion

    #region ToJson Tests

    [Fact]
    public void ToJson_WithValidResult_ReturnsValidJson()
    {
        // Arrange
        var result = new DumpComparisonResult
        {
            Baseline = new DumpIdentifier { SessionId = "session1", DebuggerType = "WinDbg" },
            Comparison = new DumpIdentifier { SessionId = "session2", DebuggerType = "WinDbg" },
            HeapComparison = new HeapComparison
            {
                BaselineMemoryBytes = 1000000,
                ComparisonMemoryBytes = 2000000,
                MemoryDeltaBytes = 1000000,
                MemoryGrowthPercent = 100
            },
            Summary = "Test summary"
        };

        // Act
        var json = DumpComparer.ToJson(result);

        // Assert
        Assert.NotEmpty(json);
        Assert.Contains("session1", json);
        Assert.Contains("session2", json);
        Assert.Contains("Test summary", json);

        // Verify it's valid JSON
        var deserialized = JsonSerializer.Deserialize<DumpComparisonResult>(json);
        Assert.NotNull(deserialized);
        Assert.Equal("session1", deserialized.Baseline.SessionId);
    }

    #endregion

    #region Summary Generation Tests

    [Fact]
    public async Task CompareAsync_GeneratesSummaryWithMemoryLeakWarning()
    {
        // Arrange - 50% memory growth
        var baselineMock = CreateMockWinDbgManager(
            heapOutput: "Committed bytes: 0x10000000",
            threadOutput: "",
            moduleOutput: "");

        var comparisonMock = CreateMockWinDbgManager(
            heapOutput: "Committed bytes: 0x18000000", // 50% growth
            threadOutput: "",
            moduleOutput: "");

        var comparer = new DumpComparer(baselineMock.Object, comparisonMock.Object);

        // Act
        var result = await comparer.CompareAsync();

        // Assert
        Assert.Contains("MEMORY LEAK SUSPECTED", result.Summary);
        Assert.NotEmpty(result.Recommendations);
    }

    [Fact]
    public async Task CompareAsync_GeneratesSummaryWithThreadChanges()
    {
        // Arrange
        var baselineMock = CreateMockWinDbgManager(
            heapOutput: "",
            threadOutput: "   0  Id: 1234.1000 Suspend: 1 Teb: 00000000`12345678 Unfrozen",
            moduleOutput: "");

        var comparisonMock = CreateMockWinDbgManager(
            heapOutput: "",
            threadOutput: @"   0  Id: 1234.1000 Suspend: 1 Teb: 00000000`12345678 Unfrozen
   1  Id: 1234.2000 Suspend: 1 Teb: 00000000`12345abc Unfrozen",
            moduleOutput: "");

        var comparer = new DumpComparer(baselineMock.Object, comparisonMock.Object);

        // Act
        var result = await comparer.CompareAsync();

        // Assert
        Assert.Contains("Thread count increased", result.Summary);
        Assert.Contains("new thread", result.Summary.ToLowerInvariant());
    }

    #endregion

    #region Helper Methods

    private static Mock<IDebuggerManager> CreateMockWinDbgManager(
        string heapOutput,
        string threadOutput,
        string moduleOutput)
    {
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.IsInitialized).Returns(true);
        mock.Setup(m => m.ExecuteCommand("!heap -s")).Returns(heapOutput);
        mock.Setup(m => m.ExecuteCommand("!dumpheap -stat")).Returns("No export"); // SOS not loaded
        mock.Setup(m => m.ExecuteCommand("~")).Returns(threadOutput);
        mock.Setup(m => m.ExecuteCommand("lm")).Returns(moduleOutput);
        return mock;
    }

    private static Mock<IDebuggerManager> CreateMockLldbManager(
        string memoryOutput,
        string threadOutput,
        string moduleOutput)
    {
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("LLDB");
        mock.Setup(m => m.IsInitialized).Returns(true);
        mock.Setup(m => m.ExecuteCommand("memory region --all")).Returns(memoryOutput);
        mock.Setup(m => m.ExecuteCommand("dumpheap -stat")).Returns("error: not found"); // SOS not loaded
        mock.Setup(m => m.ExecuteCommand("thread list")).Returns(threadOutput);
        mock.Setup(m => m.ExecuteCommand("image list")).Returns(moduleOutput);
        return mock;
    }

    #endregion
}

/// <summary>
/// Tests for DumpComparisonResult model classes.
/// </summary>
public class DumpComparisonResultTests
{
    [Fact]
    public void TypeGrowthStats_CountDelta_CalculatesCorrectly()
    {
        // Arrange
        var stats = new TypeGrowthStats
        {
            TypeName = "System.String",
            BaselineCount = 100,
            ComparisonCount = 150
        };

        // Act & Assert
        Assert.Equal(50, stats.CountDelta);
    }

    [Fact]
    public void TypeGrowthStats_SizeDelta_CalculatesCorrectly()
    {
        // Arrange
        var stats = new TypeGrowthStats
        {
            TypeName = "System.String",
            BaselineSizeBytes = 1000,
            ComparisonSizeBytes = 2500
        };

        // Act & Assert
        Assert.Equal(1500, stats.SizeDeltaBytes);
    }

    [Fact]
    public void TypeGrowthStats_GrowthPercent_CalculatesCorrectly()
    {
        // Arrange
        var stats = new TypeGrowthStats
        {
            TypeName = "System.String",
            BaselineCount = 100,
            ComparisonCount = 150
        };

        // Act & Assert
        Assert.Equal(50.0, stats.GrowthPercent); // 50% growth
    }

    [Fact]
    public void TypeGrowthStats_GrowthPercent_WithZeroBaseline_Returns100()
    {
        // Arrange
        var stats = new TypeGrowthStats
        {
            TypeName = "System.String",
            BaselineCount = 0,
            ComparisonCount = 100
        };

        // Act & Assert
        Assert.Equal(100.0, stats.GrowthPercent);
    }

    [Fact]
    public void TypeGrowthStats_GrowthPercent_WithBothZero_ReturnsZero()
    {
        // Arrange
        var stats = new TypeGrowthStats
        {
            TypeName = "System.String",
            BaselineCount = 0,
            ComparisonCount = 0
        };

        // Act & Assert
        Assert.Equal(0.0, stats.GrowthPercent);
    }

    [Fact]
    public void ThreadComparison_ThreadCountDelta_CalculatesCorrectly()
    {
        // Arrange
        var comparison = new ThreadComparison
        {
            BaselineThreadCount = 5,
            ComparisonThreadCount = 8
        };

        // Act & Assert
        Assert.Equal(3, comparison.ThreadCountDelta);
    }

    [Fact]
    public void ModuleComparison_ModuleCountDelta_CalculatesCorrectly()
    {
        // Arrange
        var comparison = new ModuleComparison
        {
            BaselineModuleCount = 10,
            ComparisonModuleCount = 12
        };

        // Act & Assert
        Assert.Equal(2, comparison.ModuleCountDelta);
    }
}

