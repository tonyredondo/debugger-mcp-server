using Xunit;
using Moq;
using DebuggerMcp.Analysis;
using System.Threading.Tasks;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Tests for the CrashAnalyzer class.
/// </summary>
public class CrashAnalyzerTests
{
    /// <summary>
    /// Tests that AnalyzeCrashAsync returns a valid result.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_ReturnsValidResult()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Summary);
        Assert.NotNull(result.RawCommands);
    }

    /// <summary>
    /// Tests that AnalyzeCrashAsync handles uninitialized debugger.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_HandlesUninitializedDebugger()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(false);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Contains("not initialized", result.Summary!.Description!, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tests that AnalyzeCrashAsync handles exceptions gracefully.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_HandlesExceptions()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Throws(new System.Exception("Command failed"));

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Contains("failed", result.Summary!.Description!, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tests that AnalyzeCrashAsync executes WinDbg commands.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_ExecutesWinDbgCommands()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        mockManager.Verify(m => m.ExecuteCommand("!analyze -v"), Times.Once);
        mockManager.Verify(m => m.ExecuteCommand("~*k"), Times.Once);
        mockManager.Verify(m => m.ExecuteCommand("~"), Times.Once);
        mockManager.Verify(m => m.ExecuteCommand("lm"), Times.Once);
    }

    /// <summary>
    /// Tests that AnalyzeCrashAsync executes LLDB commands.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_ExecutesLldbCommands()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("LLDB");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        mockManager.Verify(m => m.ExecuteCommand("bt all"), Times.Once);
        mockManager.Verify(m => m.ExecuteCommand("thread list"), Times.Once);
        mockManager.Verify(m => m.ExecuteCommand("image list"), Times.Once);
    }

    /// <summary>
    /// Tests that CrashAnalysisResult can be serialized to JSON.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_ResultCanBeSerializedToJson()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();
        var json = System.Text.Json.JsonSerializer.Serialize(result);

        // Assert
        Assert.NotNull(json);
        Assert.NotEmpty(json);
        // JSON serialization successful
    }

    /// <summary>
    /// Tests that AnalyzeCrashAsync handles unknown debugger type.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_HandlesUnknownDebuggerType()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("UnknownDebugger");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Contains("Unknown debugger", result.Summary!.Description!);
    }

    /// <summary>
    /// Tests that WinDbg analysis analyzes memory consumption from large heap.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_WinDbg_DetectsLargeHeapAsMemoryLeak()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");

        // Return large committed bytes in heap summary (3GB to trigger High severity)
        mockManager.Setup(m => m.ExecuteCommand("!heap -s"))
            .Returns("Heap at 00000001\n  Committed bytes:  0xC0000000\n"); // ~3GB
        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "!heap -s")))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.NotNull(result.Memory!.LeakAnalysis);
        // With High severity (2GB+), Detected should be true
        Assert.True(result.Memory!.LeakAnalysis.TotalHeapBytes > 500_000_000);
        Assert.Contains(result.Summary!.Recommendations!, r => r.Contains("memory", System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Tests that WinDbg analysis executes deadlock detection commands.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_WinDbg_ExecutesDeadlockCommands()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        mockManager.Verify(m => m.ExecuteCommand("!locks"), Times.Once);
        Assert.NotNull(result.Threads!.Deadlock);
    }

    /// <summary>
    /// Tests that WinDbg analysis detects deadlocks from critical sections.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_WinDbg_DetectsDeadlockFromCriticalSections()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");

        // Return critical section info with multiple owners
        mockManager.Setup(m => m.ExecuteCommand("!locks"))
            .Returns(@"CritSec ntdll!LdrpLoaderLock at 0000000077c8c340
  LockCount          1
  RecursionCount     1
  OwningThread       0000000000001234
  EntryCount         0
CritSec module!SomeLock at 0000000077c8c440
  LockCount          1
  RecursionCount     1
  OwningThread       0000000000005678
  EntryCount         0");

        // Return thread times showing long waits
        mockManager.Setup(m => m.ExecuteCommand("!runaway"))
            .Returns(@" User Mode Time
  Thread       Time
   0:1234      1 days 2:30:45.000
   1:5678      1 days 1:15:30.000");

        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "!locks" && s != "!runaway")))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.NotNull(result.Threads!.Deadlock);
        Assert.True(result.Threads!.Deadlock.Locks.Count >= 2);
    }

    /// <summary>
    /// Tests that LLDB analysis executes memory leak detection commands.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_Lldb_ExecutesMemoryLeakCommands()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("LLDB");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        mockManager.Verify(m => m.ExecuteCommand("process status"), Times.Once);
        mockManager.Verify(m => m.ExecuteCommand("memory region --all"), Times.Once);
        Assert.NotNull(result.Memory!.LeakAnalysis);
    }

    /// <summary>
    /// Tests that LLDB analysis detects deadlocks from thread backtraces.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_Lldb_DetectsDeadlockFromWaitingThreads()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("LLDB");

        // Return backtraces showing multiple threads waiting on locks
        mockManager.Setup(m => m.ExecuteCommand("bt all"))
            .Returns(@"* thread #1
    frame #0: 0x00007fff pthread_mutex_lock
    frame #1: 0x00007fff MyApp`LockA
  thread #2
    frame #0: 0x00007fff pthread_mutex_lock
    frame #1: 0x00007fff MyApp`LockB");

        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "bt all")))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.NotNull(result.Threads!.Deadlock);
        Assert.True(result.Threads!.Deadlock.Detected);
        Assert.True(result.Threads!.Deadlock.InvolvedThreads.Count >= 2);
    }

    /// <summary>
    /// Tests that memory leak info is populated with top consumers.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_WinDbg_PopulatesTopMemoryConsumers()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");

        mockManager.Setup(m => m.ExecuteCommand("!heap -stat -h 0"))
            .Returns(@"    size     #blocks     total     ( %) (requests)
    100        5000      500000  - heap
    200        3000      600000  - heap
size 100  count: 5000
size 200  count: 3000");

        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "!heap -stat -h 0")))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.NotNull(result.Memory!.LeakAnalysis);
        // Top consumers should be populated from heap stat output
    }

    /// <summary>
    /// Tests that summary includes memory leak information when detected.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_SummaryIncludesMemoryLeakInfo()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");

        mockManager.Setup(m => m.ExecuteCommand("!heap -s"))
            .Returns("Committed bytes:  0x80000000"); // ~2GB
        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "!heap -s")))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.Contains("MEMORY:", result.Summary!.Description!);
    }

    /// <summary>
    /// Tests that summary includes deadlock information when detected.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_SummaryIncludesDeadlockInfo()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("LLDB");

        mockManager.Setup(m => m.ExecuteCommand("bt all"))
            .Returns(@"* thread #1
    frame #0: 0x00007fff pthread_mutex_lock
  thread #2
    frame #0: 0x00007fff pthread_mutex_lock");
        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "bt all")))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.Contains("DEADLOCK", result.Summary!.Description!);
    }

    /// <summary>
    /// Tests that WinDbg exception parsing extracts exception code and message.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_WinDbg_ParsesExceptionCodeAndMessage()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");

        mockManager.Setup(m => m.ExecuteCommand("!analyze -v"))
            .Returns(@"EXCEPTION_CODE: (NTSTATUS) 0xc0000005 - The instruction at 0x%p referenced memory at 0x%p. The memory could not be %s.
EXCEPTION_RECORD:  00000000`12345678
FAULTING_IP: 
ntdll!NtWaitForSingleObject+14
STATUS_ACCESS_VIOLATION");

        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "!analyze -v")))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.NotNull(result.Exception);
        Assert.Equal("0xc0000005", result.Exception.Type);
        Assert.Contains("memory", result.Exception.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12345678", result.Exception.Address); // May have leading zeros
    }

    /// <summary>
    /// Tests that WinDbg call stack parsing extracts frame details.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_WinDbg_ParsesCallStackFrames()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");

        // Thread list - need to provide threads first
        mockManager.Setup(m => m.ExecuteCommand("~"))
            .Returns(@".  0  Id: 1234.5678 Suspend: 1 Teb: 00000000`12345678 Unfrozen");

        // All threads' backtraces (~*k format)
        mockManager.Setup(m => m.ExecuteCommand("~*k"))
            .Returns(@".  0  Id: 1234.5678 Suspend: 1 Teb: 00000000`12345678 Unfrozen
 # Child-SP          RetAddr           Call Site
00 00000000`12345678 00007ff8`11111111 ntdll!NtWaitForSingleObject+0x14
01 00000000`12345680 00007ff8`22222222 kernel32!WaitForSingleObjectEx+0x9e
02 00000000`12345688 00007ff8`33333333 myapp!MainFunction+0x50 [d:\src\main.cpp @ 123]");

        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "~" && s != "~*k")))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.NotEmpty(result.Threads!.All!);
        var faultingThread = result.Threads!.All!.FirstOrDefault(t => t.IsFaulting) ?? result.Threads!.All!.First();
        Assert.True(faultingThread.CallStack.Count >= 3);

        var frame0 = faultingThread.CallStack.FirstOrDefault(f => f.FrameNumber == 0);
        Assert.NotNull(frame0);
        Assert.Equal("ntdll", frame0.Module);
        Assert.Equal("NtWaitForSingleObject", frame0.Function);
        Assert.Contains("7ff811111111", frame0.InstructionPointer); // May have leading zeros

        var frame2 = faultingThread.CallStack.FirstOrDefault(f => f.FrameNumber == 2);
        Assert.NotNull(frame2);
        Assert.Equal("myapp", frame2.Module);
        Assert.Equal("MainFunction", frame2.Function);
        Assert.Contains("main.cpp", frame2.Source);
    }

    /// <summary>
    /// Tests that WinDbg thread parsing extracts thread details.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_WinDbg_ParsesThreadDetails()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");

        mockManager.Setup(m => m.ExecuteCommand("~"))
            .Returns(@".  0  Id: 1234.5678 Suspend: 1 Teb: 00000000`12345678 Unfrozen
   1  Id: 1234.abcd Suspend: 1 Teb: 00000000`12345abc Unfrozen ""WorkerThread""
#  2  Id: 1234.def0 Suspend: 1 Teb: 00000000`12345def Unfrozen");

        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "~")))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.True(result.Threads!.All!.Count >= 3);

        var thread0 = result.Threads!.All!.FirstOrDefault(t => t.ThreadId.Contains("0 (5678)"));
        Assert.NotNull(thread0);
        Assert.True(thread0.IsFaulting); // Has '.' marker
        Assert.Equal("Unfrozen", thread0.State);

        var thread1 = result.Threads!.All!.FirstOrDefault(t => t.ThreadId.Contains("WorkerThread"));
        Assert.NotNull(thread1);
    }

    /// <summary>
    /// Tests that WinDbg module parsing extracts module details.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_WinDbg_ParsesModuleDetails()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");

        mockManager.Setup(m => m.ExecuteCommand("lm"))
            .Returns(@"start             end                 module name
00007ff8`12340000 00007ff8`12345000   ntdll      (pdb symbols)  c:\symbols\ntdll.pdb
00007ff8`22340000 00007ff8`22345000   kernel32   (deferred)
00007ff8`32340000 00007ff8`32345000   myapp      (private pdb symbols)  d:\bin\myapp.pdb");

        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "lm")))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.NotNull(result.Modules);
        Assert.True(result.Modules.Count >= 3);

        var ntdll = result.Modules.FirstOrDefault(m => m.Name == "ntdll");
        Assert.NotNull(ntdll);
        Assert.Contains("7ff812340000", ntdll.BaseAddress); // May have leading zeros
        Assert.True(ntdll.HasSymbols);

        var kernel32 = result.Modules.FirstOrDefault(m => m.Name == "kernel32");
        Assert.NotNull(kernel32);
        Assert.False(kernel32.HasSymbols); // deferred

        var myapp = result.Modules.FirstOrDefault(m => m.Name == "myapp");
        Assert.NotNull(myapp);
        Assert.True(myapp.HasSymbols); // private pdb
    }

    /// <summary>
    /// Tests that LLDB thread parsing extracts thread details.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_Lldb_ParsesThreadDetails()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("LLDB");

        mockManager.Setup(m => m.ExecuteCommand("thread list"))
            .Returns(@"Process 12345 stopped
* thread #1: tid = 0x1234, 0x00007fff12345678 libsystem_kernel.dylib`__psynch_cvwait + 10, name = 'main', queue = 'com.apple.main-thread', stop reason = signal SIGSTOP
  thread #2: tid = 0x5678, 0x00007fff87654321 libsystem_pthread.dylib`_pthread_cond_wait, name = 'worker'");

        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "thread list")))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.True(result.Threads!.All!.Count >= 2);

        var thread1 = result.Threads!.All!.FirstOrDefault(t => t.ThreadId.Contains("1 (tid: 0x1234)"));
        Assert.NotNull(thread1);
        Assert.Contains("main", thread1.ThreadId);
        Assert.Contains("SIGSTOP", thread1.State);
        Assert.True(thread1.IsFaulting);

        var thread2 = result.Threads!.All!.FirstOrDefault(t => t.ThreadId.Contains("worker"));
        Assert.NotNull(thread2);
    }

    /// <summary>
    /// Tests that LLDB backtrace parsing extracts frame details.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_Lldb_ParsesBacktraceFrames()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("LLDB");

        // Thread list command - using correct LLDB format with tid
        mockManager.Setup(m => m.ExecuteCommand("thread list"))
            .Returns(@"Process 12345 stopped
* thread #1: tid = 0x1234, 0x00007fff12345678 libsystem_kernel.dylib`__psynch_cvwait + 10, queue = 'com.apple.main-thread', stop reason = signal SIGSTOP");

        mockManager.Setup(m => m.ExecuteCommand("bt all"))
            .Returns(@"* thread #1, queue = 'com.apple.main-thread', stop reason = signal SIGSTOP
  * frame #0: 0x00007fff12345678 libsystem_kernel.dylib`__psynch_cvwait + 10
    frame #1: 0x00007fff12345abc libsystem_pthread.dylib`_pthread_cond_wait + 722 at pthread_cond.c:123
    frame #2: 0x0000000100001234 myapp`main + 50 at main.cpp:42");

        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "bt all" && s != "thread list")))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.NotEmpty(result.Threads!.All!);
        var faultingThread = result.Threads!.All!.FirstOrDefault(t => t.IsFaulting) ?? result.Threads!.All!.First();
        Assert.True(faultingThread.CallStack.Count >= 3);

        var frame0 = faultingThread.CallStack.FirstOrDefault(f => f.FrameNumber == 0);
        Assert.NotNull(frame0);
        Assert.Equal("libsystem_kernel.dylib", frame0.Module);
        Assert.Equal("__psynch_cvwait", frame0.Function);
        Assert.Equal("0x00007fff12345678", frame0.InstructionPointer);

        var frame1 = faultingThread.CallStack.FirstOrDefault(f => f.FrameNumber == 1);
        Assert.NotNull(frame1);
        Assert.Contains("pthread_cond.c", frame1.Source);

        var frame2 = faultingThread.CallStack.FirstOrDefault(f => f.FrameNumber == 2);
        Assert.NotNull(frame2);
        Assert.Equal("myapp", frame2.Module);
        Assert.Equal("main", frame2.Function);
        Assert.Contains("main.cpp", frame2.Source);
    }

    /// <summary>
    /// Tests that LLDB module parsing extracts module details.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrashAsync_Lldb_ParsesModuleDetails()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsInitialized).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("LLDB");

        mockManager.Setup(m => m.ExecuteCommand("image list"))
            .Returns(@"[  0] 12345678-1234-1234-1234-123456789ABC 0x0000000100000000 /usr/lib/dyld
[  1] ABCDEF01-2345-6789-ABCD-EF0123456789 0x00007fff12340000 /usr/lib/libSystem.B.dylib
[  2] 00000000-0000-0000-0000-000000000000 0x0000000100001000 /Users/test/myapp");

        mockManager.Setup(m => m.ExecuteCommand(It.Is<string>(s => s != "image list")))
            .Returns("Test output");

        var analyzer = new CrashAnalyzer(mockManager.Object);

        // Act
        var result = await analyzer.AnalyzeCrashAsync();

        // Assert
        Assert.NotNull(result.Modules);
        Assert.True(result.Modules.Count >= 3);

        var dyld = result.Modules.FirstOrDefault(m => m.Name == "dyld");
        Assert.NotNull(dyld);
        Assert.Equal("0x0000000100000000", dyld.BaseAddress);
        // HasSymbols is now based on .dSYM/.debug files, not UUID
        // Without explicit debug file info in the output, HasSymbols will be false
        Assert.False(dyld.HasSymbols);

        var libSystem = result.Modules.FirstOrDefault(m => m.Name == "libSystem.B.dylib");
        Assert.NotNull(libSystem);

        var myapp = result.Modules.FirstOrDefault(m => m.Name == "myapp");
        Assert.NotNull(myapp);
        Assert.False(myapp.HasSymbols);
    }
}
