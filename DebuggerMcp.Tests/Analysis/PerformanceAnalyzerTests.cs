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
/// Tests for the PerformanceAnalyzer class.
/// </summary>
public class PerformanceAnalyzerTests
{

    [Fact]
    public void Constructor_WithNullManager_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new PerformanceAnalyzer(null!));
    }

    [Fact]
    public void Constructor_WithValidManager_Succeeds()
    {
        // Arrange
        var mock = new Mock<IDebuggerManager>();

        // Act
        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Assert
        Assert.NotNull(analyzer);
    }



    [Fact]
    public async Task AnalyzePerformanceAsync_WithWinDbg_ReturnsCompleteResult()
    {
        // Arrange
        var mock = CreateMockWinDbgManager();
        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Act
        var result = await analyzer.AnalyzePerformanceAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("WinDbg", result.DebuggerType);
        Assert.NotNull(result.CpuAnalysis);
        Assert.NotNull(result.AllocationAnalysis);
        Assert.NotNull(result.GcAnalysis);
        Assert.NotNull(result.ContentionAnalysis);
        Assert.NotEmpty(result.Summary);
    }

    [Fact]
    public async Task AnalyzePerformanceAsync_WithLldb_ReturnsCompleteResult()
    {
        // Arrange
        var mock = CreateMockLldbManager();
        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Act
        var result = await analyzer.AnalyzePerformanceAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("LLDB", result.DebuggerType);
        Assert.NotNull(result.CpuAnalysis);
        Assert.NotNull(result.AllocationAnalysis);
    }

    [Fact]
    public async Task AnalyzeGcAsync_WithLldbEeheapFormat_ParsesGenerationSizes()
    {
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("LLDB");
        mock.Setup(m => m.ExecuteCommand("eeheap -gc")).Returns(
            "Workstation GC\nConcurrent GC\n" +
            "generation 0:\n" +
            "    segment            begin        allocated        committed allocated size     committed size\n" +
            "    0000000000000001 0000000000000002 0000000000000003 0000000000000004 0x10 (16) 0x20 (32)\n" +
            "generation 1:\n" +
            "    segment            begin        allocated        committed allocated size     committed size\n" +
            "    0000000000000005 0000000000000006 0000000000000007 0000000000000008 0x08 (8) 0x10 (16)\n" +
            "generation 2:\n" +
            "    segment            begin        allocated        committed allocated size     committed size\n" +
            "    0000000000000009 000000000000000a 000000000000000b 000000000000000c 0x04 (4) 0x08 (8)\n" +
            "Large object heap:\n" +
            "    segment            begin        allocated        committed allocated size     committed size\n" +
            "    000000000000000d 000000000000000e 000000000000000f 0000000000000010 0x02 (2) 0x04 (4)\n" +
            "Pinned object heap:\n" +
            "    segment            begin        allocated        committed allocated size     committed size\n" +
            "    0000000000000011 0000000000000012 0000000000000013 0000000000000014 0x01 (1) 0x02 (2)\n" +
            "GC Allocated Heap Size:    Size: 0x1f (31) bytes.\n");

        var analyzer = new PerformanceAnalyzer(mock.Object);
        var result = await analyzer.AnalyzeGcAsync();

        Assert.Equal("Workstation", result.GcMode);
        Assert.True(result.ConcurrentGc);
        Assert.Equal(16, result.Gen0SizeBytes);
        Assert.Equal(8, result.Gen1SizeBytes);
        Assert.Equal(4, result.Gen2SizeBytes);
        Assert.Equal(2, result.LohSizeBytes);
        Assert.Equal(1, result.PohSizeBytes);
        Assert.Equal(31, result.TotalHeapSizeBytes);
    }



    [Fact]
    public async Task AnalyzeCpuUsageAsync_ParsesRunawayOutput()
    {
        // Arrange
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.ExecuteCommand("!runaway")).Returns(
            @"User Mode Time
  Thread       Time
   0:1234      0 days 0:01:30.500
   1:5678      0 days 0:00:45.250
   2:9abc      0 days 0:00:10.100
Kernel Mode Time
  Thread       Time
   0:1234      0 days 0:00:30.000
   1:5678      0 days 0:00:15.000");
        mock.Setup(m => m.ExecuteCommand("~*k")).Returns("# Child-SP");
        mock.Setup(m => m.ExecuteCommand("~")).Returns("   0  Id: 1234.1234 Suspend: 1 Teb: 00000000`12345678 Unfrozen");

        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Act
        var result = await analyzer.AnalyzeCpuUsageAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.ThreadCpuUsage);
        Assert.Equal("0", result.ThreadCpuUsage[0].ThreadId);
        Assert.True(result.ThreadCpuUsage[0].UserTime > 0);
    }

    [Fact]
    public async Task AnalyzeCpuUsageAsync_IdentifiesHotFunctions()
    {
        // Arrange
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.ExecuteCommand("!runaway")).Returns("User Mode Time\n  Thread       Time\n");
        mock.Setup(m => m.ExecuteCommand("~*k")).Returns(
            @"# Child-SP
00 ntdll!NtWaitForSingleObject+0x14
01 kernel32!WaitForSingleObjectEx+0x9c
02 MyApp!HotFunction+0x100
03 MyApp!HotFunction+0x50
# Child-SP
00 ntdll!NtWaitForSingleObject+0x14
01 MyApp!HotFunction+0x100");
        mock.Setup(m => m.ExecuteCommand("~")).Returns("");

        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Act
        var result = await analyzer.AnalyzeCpuUsageAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.HotFunctions);
        Assert.Contains(result.HotFunctions, f => f.Function == "HotFunction");
    }

    [Fact]
    public async Task AnalyzeCpuUsageAsync_DetectsSpinLoops()
    {
        // Arrange
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.ExecuteCommand("!runaway")).Returns("");
        mock.Setup(m => m.ExecuteCommand("~*k")).Returns(
            @"# Child-SP
00 clr!SpinWait+0x10
01 clr!SpinWait+0x20
02 clr!SpinWait+0x30
# Child-SP
00 clr!SpinWait+0x10");
        mock.Setup(m => m.ExecuteCommand("~")).Returns("");

        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Act
        var result = await analyzer.AnalyzeCpuUsageAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.PotentialSpinLoops);
    }



    [Fact]
    public async Task AnalyzeAllocationsAsync_ParsesDumpHeapStats()
    {
        // Arrange
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.ExecuteCommand("!dumpheap -stat")).Returns(
            @"MT    Count    TotalSize Class Name
00007ff812345678    50000    2500000 System.String
00007ff812345680    10000    1000000 System.Object
00007ff812345690    5000     500000 System.Byte[]
Total 65000 objects, 4000000 bytes");
        mock.Setup(m => m.ExecuteCommand("!dumpheap -min 85000 -stat")).Returns("");

        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Act
        var result = await analyzer.AnalyzeAllocationsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(65000, result.TotalObjectCount);
        Assert.Equal(4000000, result.TotalHeapSizeBytes);
        Assert.NotEmpty(result.TopAllocators);
        Assert.Contains(result.TopAllocators, a => a.TypeName == "System.String");
    }

    [Fact]
    public async Task AnalyzeAllocationsAsync_DetectsExcessiveStringAllocations()
    {
        // Arrange
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.ExecuteCommand("!dumpheap -stat")).Returns(
            @"MT    Count    TotalSize Class Name
00007ff812345678    100000    5000000 System.String
Total 100000 objects, 5000000 bytes");
        mock.Setup(m => m.ExecuteCommand("!dumpheap -min 85000 -stat")).Returns("");

        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Act
        var result = await analyzer.AnalyzeAllocationsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.StringStats);
        Assert.True(result.StringStats.ExcessiveAllocations);
        Assert.Equal(100000, result.StringStats.Count);
    }

    [Fact]
    public async Task AnalyzeAllocationsAsync_IdentifiesPotentialLeaks()
    {
        // Arrange
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.ExecuteCommand("!dumpheap -stat")).Returns(
            @"MT    Count    TotalSize Class Name
00007ff812345678    50000    2500000 MyApp.LeakyObject
Total 50000 objects, 2500000 bytes");
        mock.Setup(m => m.ExecuteCommand("!dumpheap -min 85000 -stat")).Returns("");

        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Act
        var result = await analyzer.AnalyzeAllocationsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.PotentialLeaks);
        Assert.Contains(result.PotentialLeaks, l => l.TypeName == "MyApp.LeakyObject");
    }

    [Fact]
    public async Task AnalyzeAllocationsAsync_TracksLargeObjectAllocations()
    {
        // Arrange
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.ExecuteCommand("!dumpheap -stat")).Returns(
            @"MT    Count    TotalSize Class Name
00007ff812345678    1000    1000000 System.Byte[]
Total 1000 objects, 1000000 bytes");
        mock.Setup(m => m.ExecuteCommand("!dumpheap -min 85000 -stat")).Returns(
            @"MT    Count    TotalSize Class Name
00007ff812345678    50    5000000 System.Byte[]");

        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Act
        var result = await analyzer.AnalyzeAllocationsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.LargeObjectAllocations);
        Assert.Contains(result.LargeObjectAllocations, a => a.TypeName == "System.Byte[]");
    }



    [Fact]
    public async Task AnalyzeGcAsync_ParsesEeheapOutput()
    {
        // Arrange
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.ExecuteCommand("!eeheap -gc")).Returns(
            @"Number of GC Heaps: 1
Heap 0 (00000123)
generation 0 starts at 0x00001000
         size:     0x100000 (1048576) bytes
generation 1 starts at 0x00100000
         size:     0x200000 (2097152) bytes
generation 2 starts at 0x00300000
         size:     0x400000 (4194304) bytes
Large object heap starts at 0x00700000
         size:     0x100000 (1048576) bytes
GC Heap Size:    0x800000 (8388608) bytes
Server GC
Concurrent GC enabled");
        mock.Setup(m => m.ExecuteCommand("!gchandles")).Returns("123 handles");
        mock.Setup(m => m.ExecuteCommand("!finalizequeue")).Returns("10 objects ready for finalization");

        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Act
        var result = await analyzer.AnalyzeGcAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Server", result.GcMode);
        Assert.True(result.ConcurrentGc);
        Assert.Equal(1048576, result.Gen0SizeBytes);
        Assert.Equal(2097152, result.Gen1SizeBytes);
        Assert.Equal(4194304, result.Gen2SizeBytes);
        Assert.Equal(1048576, result.LohSizeBytes);
        Assert.Equal(123, result.GcHandleCount);
        Assert.Equal(10, result.FinalizerQueueLength);
    }

    [Fact]
    public async Task AnalyzeGcAsync_DetectsHighGcPressure()
    {
        // Arrange - Gen2 is 80% of total heap
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.ExecuteCommand("!eeheap -gc")).Returns(
            @"generation 0 size: 0x100000
generation 1 size: 0x100000
generation 2 size: 0x800000
Large object heap size: 0x100000
GC Heap Size: 0xB00000 (11534336) bytes
Workstation GC");
        mock.Setup(m => m.ExecuteCommand("!gchandles")).Returns("");
        mock.Setup(m => m.ExecuteCommand("!finalizequeue")).Returns("");

        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Act
        var result = await analyzer.AnalyzeGcAsync();

        // Assert
        Assert.NotNull(result);
        Assert.True(result.HighGcPressure);
        Assert.Contains(result.Recommendations, r => r.Contains("GC pressure"));
    }

    [Fact]
    public async Task AnalyzeGcAsync_DetectsBlockedFinalizer()
    {
        // Arrange
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.ExecuteCommand("!eeheap -gc")).Returns("GC Heap Size: 0x100000");
        mock.Setup(m => m.ExecuteCommand("!gchandles")).Returns("");
        mock.Setup(m => m.ExecuteCommand("!finalizequeue")).Returns(
            "Finalizer thread is blocked waiting for lock");

        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Act
        var result = await analyzer.AnalyzeGcAsync();

        // Assert
        Assert.NotNull(result);
        Assert.True(result.FinalizerThreadBlocked);
        Assert.Contains(result.Recommendations, r => r.Contains("Finalizer thread"));
    }



    [Fact]
    public async Task AnalyzeContentionAsync_ParsesSyncBlocks()
    {
        // Arrange
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.ExecuteCommand("!syncblk")).Returns(
            @"Index SyncBlock MonitorHeld Recursion Owning Thread Info SyncBlock Owner
    1 00000123 1 1 1234 MyApp.SyncObject");
        mock.Setup(m => m.ExecuteCommand("!locks")).Returns("");
        mock.Setup(m => m.ExecuteCommand("~*e !clrstack")).Returns("");

        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Act
        var result = await analyzer.AnalyzeContentionAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.SyncBlocks);
        Assert.Equal(1, result.TotalLockCount);
    }

    [Fact]
    public async Task AnalyzeContentionAsync_WhenSyncblkIsWinDbgExtendedFormat_ParsesOwnerThreadAndCountsLocks()
    {
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.ExecuteCommand("!syncblk")).Returns(
            @"Index SyncBlock MonitorHeld Recursion Owning Thread Info  SyncBlock Owner
  12 0000024453f8a5a8    1         1 0000024453f46720  1c54  10   00000244540a5820 System.Object");
        mock.Setup(m => m.ExecuteCommand("!locks")).Returns("");
        mock.Setup(m => m.ExecuteCommand("~*e !clrstack")).Returns("");

        var analyzer = new PerformanceAnalyzer(mock.Object);

        var result = await analyzer.AnalyzeContentionAsync();

        Assert.NotNull(result);
        Assert.NotEmpty(result.SyncBlocks);
        Assert.Equal(1, result.TotalLockCount);
        Assert.Equal("1c54", result.SyncBlocks[0].OwnerThreadId);
        Assert.Equal("00000244540a5820", result.SyncBlocks[0].ObjectAddress);
        Assert.Equal("System.Object", result.SyncBlocks[0].ObjectType);
    }

    [Fact]
    public async Task AnalyzeContentionAsync_ParsesContentedLocks()
    {
        // Arrange
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.ExecuteCommand("!syncblk")).Returns("");
        mock.Setup(m => m.ExecuteCommand("!locks")).Returns(
            @"CritSec MyApp!g_Lock at 00000123`45678900
LockCount          5
RecursionCount     1
OwningThread       1234");
        mock.Setup(m => m.ExecuteCommand("~*e !clrstack")).Returns("");

        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Act
        var result = await analyzer.AnalyzeContentionAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.ContentedLocks);
        Assert.Equal(1, result.ContentedLockCount);
        Assert.Equal(4, result.ContentedLocks[0].WaiterCount); // 5 - 1 owner
    }

    [Fact]
    public async Task AnalyzeContentionAsync_DetectsDeadlock()
    {
        // Arrange
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.ExecuteCommand("!syncblk")).Returns("");
        mock.Setup(m => m.ExecuteCommand("!locks")).Returns("DEADLOCK DETECTED");
        mock.Setup(m => m.ExecuteCommand("~*e !clrstack")).Returns("");

        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Act
        var result = await analyzer.AnalyzeContentionAsync();

        // Assert
        Assert.NotNull(result);
        Assert.True(result.DeadlockDetected);
        Assert.Contains(result.Recommendations, r => r.Contains("DEADLOCK"));
    }

    [Fact]
    public async Task AnalyzeContentionAsync_IdentifiesWaitingThreads()
    {
        // Arrange
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.ExecuteCommand("!syncblk")).Returns("");
        mock.Setup(m => m.ExecuteCommand("!locks")).Returns("");
        mock.Setup(m => m.ExecuteCommand("~*e !clrstack")).Returns(
            @"OS Thread Id: 0x1234
    Monitor.Enter(...)
OS Thread Id: 0x5678
    Monitor.Enter(...)
OS Thread Id: 0x9abc
    Monitor.Enter(...)
OS Thread Id: 0xdef0
    Monitor.Enter(...)
OS Thread Id: 0x1111
    Monitor.Enter(...)
OS Thread Id: 0x2222
    Monitor.Enter(...)
OS Thread Id: 0x3333
    Running...");

        var analyzer = new PerformanceAnalyzer(mock.Object);

        // Act
        var result = await analyzer.AnalyzeContentionAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(6, result.WaitingThreads.Count);
        Assert.True(result.HighContention);
    }



    [Fact]
    public void ToJson_WithValidResult_ReturnsValidJson()
    {
        // Arrange
        var result = new PerformanceAnalysisResult
        {
            DebuggerType = "WinDbg",
            Summary = "Test summary",
            CpuAnalysis = new CpuAnalysisResult
            {
                TotalThreads = 10,
                ActiveThreads = 5
            },
            AllocationAnalysis = new AllocationAnalysisResult
            {
                TotalHeapSizeBytes = 1000000,
                TotalObjectCount = 5000
            }
        };

        // Act
        var json = PerformanceAnalyzer.ToJson(result);

        // Assert
        Assert.NotEmpty(json);
        Assert.Contains("WinDbg", json);
        Assert.Contains("Test summary", json);

        // Verify it's valid JSON
        var deserialized = JsonSerializer.Deserialize<PerformanceAnalysisResult>(json);
        Assert.NotNull(deserialized);
        Assert.Equal("WinDbg", deserialized.DebuggerType);
    }



    private static Mock<IDebuggerManager> CreateMockWinDbgManager()
    {
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("WinDbg");
        mock.Setup(m => m.IsInitialized).Returns(true);
        mock.Setup(m => m.IsDumpOpen).Returns(true);

        // CPU analysis
        mock.Setup(m => m.ExecuteCommand("!runaway")).Returns("User Mode Time\n  Thread       Time\n   0:1234      0 days 0:00:10.000");
        mock.Setup(m => m.ExecuteCommand("~*k")).Returns("# Child-SP\n00 ntdll!NtWaitForSingleObject");
        mock.Setup(m => m.ExecuteCommand("~")).Returns("   0  Id: 1234.1234 Suspend: 1 Teb: 00000000`12345678 Unfrozen");

        // Allocation analysis
        mock.Setup(m => m.ExecuteCommand("!dumpheap -stat")).Returns(
            "MT    Count    TotalSize Class Name\n00007ff812345678    1000    100000 System.String\nTotal 1000 objects, 100000 bytes");
        mock.Setup(m => m.ExecuteCommand("!dumpheap -min 85000 -stat")).Returns("");

        // GC analysis
        mock.Setup(m => m.ExecuteCommand("!eeheap -gc")).Returns("GC Heap Size: 0x100000\nWorkstation GC");
        mock.Setup(m => m.ExecuteCommand("!gchandles")).Returns("10 handles");
        mock.Setup(m => m.ExecuteCommand("!finalizequeue")).Returns("5 objects ready for finalization");

        // Contention analysis
        mock.Setup(m => m.ExecuteCommand("!syncblk")).Returns("");
        mock.Setup(m => m.ExecuteCommand("!locks")).Returns("");
        mock.Setup(m => m.ExecuteCommand("~*e !clrstack")).Returns("");

        return mock;
    }

    private static Mock<IDebuggerManager> CreateMockLldbManager()
    {
        var mock = new Mock<IDebuggerManager>();
        mock.Setup(m => m.DebuggerType).Returns("LLDB");
        mock.Setup(m => m.IsInitialized).Returns(true);
        mock.Setup(m => m.IsDumpOpen).Returns(true);

        // CPU analysis
        mock.Setup(m => m.ExecuteCommand("thread list")).Returns("* thread #1: tid = 0x1234\n  thread #2: tid = 0x5678");
        mock.Setup(m => m.ExecuteCommand("bt all")).Returns("* thread #1:\n  frame #0: module`function");

        // Allocation analysis
        mock.Setup(m => m.ExecuteCommand("dumpheap -stat")).Returns("error: command not found");
        mock.Setup(m => m.ExecuteCommand("memory region --all")).Returns("[0x00000000-0x10000000) r-x");

        // GC analysis
        mock.Setup(m => m.ExecuteCommand("eeheap -gc")).Returns("error: command not found");
        mock.Setup(m => m.ExecuteCommand("finalizequeue")).Returns("error: command not found");

        // Contention analysis
        mock.Setup(m => m.ExecuteCommand("syncblk")).Returns("error: command not found");

        return mock;
    }

}

/// <summary>
/// Tests for PerformanceAnalysisResult model classes.
/// </summary>
public class PerformanceAnalysisResultTests
{
    [Fact]
    public void AllocationInfo_AverageSizeBytes_CalculatesCorrectly()
    {
        // Arrange
        var info = new AllocationInfo
        {
            Count = 100,
            TotalSizeBytes = 5000
        };

        // Act & Assert
        Assert.Equal(50, info.AverageSizeBytes);
    }

    [Fact]
    public void AllocationInfo_AverageSizeBytes_WithZeroCount_ReturnsZero()
    {
        // Arrange
        var info = new AllocationInfo
        {
            Count = 0,
            TotalSizeBytes = 5000
        };

        // Act & Assert
        Assert.Equal(0, info.AverageSizeBytes);
    }

    [Fact]
    public void HotFunction_PropertiesSetCorrectly()
    {
        // Arrange & Act
        var func = new HotFunction
        {
            Module = "ntdll",
            Function = "NtWaitForSingleObject",
            HitCount = 10,
            Percentage = 25.5
        };

        // Assert
        Assert.Equal("ntdll", func.Module);
        Assert.Equal("NtWaitForSingleObject", func.Function);
        Assert.Equal(10, func.HitCount);
        Assert.Equal(25.5, func.Percentage);
    }

    [Fact]
    public void GcAnalysisResult_DefaultValues()
    {
        // Arrange & Act
        var result = new GcAnalysisResult();

        // Assert
        Assert.Equal(string.Empty, result.GcMode);
        Assert.False(result.ConcurrentGc);
        Assert.Equal(0, result.Gen0SizeBytes);
        Assert.False(result.HighGcPressure);
        Assert.Empty(result.Recommendations);
    }

    [Fact]
    public void ContentionAnalysisResult_DefaultValues()
    {
        // Arrange & Act
        var result = new ContentionAnalysisResult();

        // Assert
        Assert.Equal(0, result.TotalLockCount);
        Assert.Equal(0, result.ContentedLockCount);
        Assert.False(result.DeadlockDetected);
        Assert.False(result.HighContention);
        Assert.Empty(result.ContentedLocks);
        Assert.Empty(result.WaitingThreads);
    }
}
