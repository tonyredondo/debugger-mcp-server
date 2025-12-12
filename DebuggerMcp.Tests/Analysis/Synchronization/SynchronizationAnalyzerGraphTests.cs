using DebuggerMcp.Analysis.Synchronization;

namespace DebuggerMcp.Tests.Analysis.Synchronization;

/// <summary>
/// Unit tests for wait-graph building and deadlock detection.
/// </summary>
public class SynchronizationAnalyzerGraphTests
{
    [Fact]
    public void BuildWaitGraphFromResult_IncludesOnlyParticipatingThreads()
    {
        var result = new SynchronizationAnalysisResult
        {
            MonitorLocks =
            [
                new MonitorLockInfo
                {
                    Address = "0x0000000000000001",
                    ObjectType = "MyLock",
                    OwnerThreadId = 1,
                    OwnerOsThreadId = 100,
                    WaitingThreads = [2]
                }
            ]
        };

        var graph = SynchronizationAnalyzer.BuildWaitGraphFromResult(
            result,
            threads: [(1, 100u), (2, 200u), (3, 300u)]);

        Assert.Contains(graph.Nodes, n => n.Id == "thread_1");
        Assert.Contains(graph.Nodes, n => n.Id == "thread_2");
        Assert.DoesNotContain(graph.Nodes, n => n.Id == "thread_3");
        Assert.Contains(graph.Nodes, n => n.Id.StartsWith("monitor_", StringComparison.Ordinal));
        Assert.Contains(graph.Edges, e => e.From.StartsWith("monitor_", StringComparison.Ordinal) && e.To == "thread_1");
        Assert.Contains(graph.Edges, e => e.From == "thread_2" && e.To.StartsWith("monitor_", StringComparison.Ordinal));
    }

    [Fact]
    public void DetectDeadlocksFromWaitGraph_WhenNoCycle_ReturnsEmpty()
    {
        var graph = new WaitGraph
        {
            Nodes =
            [
                new WaitGraphNode { Id = "thread_1", Type = "thread", Label = "Thread 1" },
                new WaitGraphNode { Id = "monitor_0x1", Type = "resource", Label = "Monitor@0x1" }
            ],
            Edges =
            [
                new WaitGraphEdge { From = "thread_1", To = "monitor_0x1", Label = "waits" }
            ]
        };

        var deadlocks = SynchronizationAnalyzer.DetectDeadlocksFromWaitGraph(graph);

        Assert.Empty(deadlocks);
    }

    [Fact]
    public void DetectDeadlocksFromWaitGraph_WhenThreadCycleExists_ReturnsDeadlock()
    {
        // Simulate classic circular wait:
        // Thread 1 waits on resource A owned by Thread 2
        // Thread 2 waits on resource B owned by Thread 1
        var graph = new WaitGraph
        {
            Nodes =
            [
                new WaitGraphNode { Id = "thread_1", Type = "thread", Label = "Thread 1" },
                new WaitGraphNode { Id = "thread_2", Type = "thread", Label = "Thread 2" },
                new WaitGraphNode { Id = "monitor_A", Type = "resource", Label = "Monitor@A" },
                new WaitGraphNode { Id = "monitor_B", Type = "resource", Label = "Monitor@B" }
            ],
            Edges =
            [
                new WaitGraphEdge { From = "thread_1", To = "monitor_A", Label = "waits" },
                new WaitGraphEdge { From = "monitor_A", To = "thread_2", Label = "owned by" },
                new WaitGraphEdge { From = "thread_2", To = "monitor_B", Label = "waits" },
                new WaitGraphEdge { From = "monitor_B", To = "thread_1", Label = "owned by" }
            ]
        };

        var deadlocks = SynchronizationAnalyzer.DetectDeadlocksFromWaitGraph(graph);

        Assert.NotEmpty(deadlocks);
        Assert.Contains(deadlocks[0].Threads, t => t == 1);
        Assert.Contains(deadlocks[0].Threads, t => t == 2);
        Assert.Contains("Thread 1", deadlocks[0].Cycle, StringComparison.Ordinal);
        Assert.Contains("Thread 2", deadlocks[0].Cycle, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWaitGraphFromResult_IncludesContendedSemaphoreSlimAndReaderWriterLockSlim()
    {
        var result = new SynchronizationAnalysisResult
        {
            SemaphoreSlims =
            [
                new SemaphoreSlimInfo
                {
                    Address = "0xSEM",
                    CurrentCount = 0,
                    MaxCount = 1,
                    SyncWaiters = 1,
                    AsyncWaiters = 0,
                    Waiters = [new WaiterInfo { ThreadId = 1, IsAsync = false }]
                },
                new SemaphoreSlimInfo
                {
                    Address = "0xNOTCONTENDED",
                    CurrentCount = 1,
                    MaxCount = 1,
                    SyncWaiters = 0,
                    AsyncWaiters = 0
                }
            ],
            ReaderWriterLocks =
            [
                new ReaderWriterLockInfo
                {
                    Address = "0xRW",
                    WriterThreadId = 2,
                    WriteWaiters = 1
                },
                new ReaderWriterLockInfo
                {
                    Address = "0xRW_NO_WAITERS",
                    WriterThreadId = 3,
                    WriteWaiters = 0,
                    ReadWaiters = 0,
                    UpgradeWaiters = 0
                }
            ]
        };

        var graph = SynchronizationAnalyzer.BuildWaitGraphFromResult(
            result,
            threads: [(1, 101u), (2, 202u), (3, 303u)]);

        Assert.Contains(graph.Nodes, n => n.Id == "semaphore_0xSEM");
        Assert.DoesNotContain(graph.Nodes, n => n.Id == "semaphore_0xNOTCONTENDED");
        Assert.Contains(graph.Edges, e => e.From == "thread_1" && e.To == "semaphore_0xSEM" && e.Label == "waits");

        Assert.Contains(graph.Nodes, n => n.Id == "rwlock_0xRW");
        Assert.DoesNotContain(graph.Nodes, n => n.Id == "rwlock_0xRW_NO_WAITERS");
        Assert.Contains(graph.Edges, e => e.From == "rwlock_0xRW" && e.To == "thread_2" && e.Label == "owned by");
    }
}
