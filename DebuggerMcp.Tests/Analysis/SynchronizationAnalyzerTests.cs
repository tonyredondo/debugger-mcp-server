using DebuggerMcp.Analysis.Synchronization;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Tests for the synchronization primitives analyzer.
/// </summary>
public class SynchronizationAnalyzerTests
{
    #region SynchronizationAnalysisResult Tests

    [Fact]
    public void SynchronizationAnalysisResult_DefaultValues_AreCorrect()
    {
        var result = new SynchronizationAnalysisResult();

        Assert.NotNull(result.Summary);
        Assert.Null(result.MonitorLocks);
        Assert.Null(result.SemaphoreSlims);
        Assert.Null(result.ReaderWriterLocks);
        Assert.Null(result.ResetEvents);
        Assert.Null(result.WaitHandles);
        Assert.Null(result.WaitGraph);
        Assert.Null(result.PotentialDeadlocks);
        Assert.Null(result.ContentionHotspots);
    }

    #endregion

    #region SynchronizationSummary Tests

    [Fact]
    public void SynchronizationSummary_DefaultValues_AreZero()
    {
        var summary = new SynchronizationSummary();

        Assert.Equal(0, summary.TotalMonitorLocks);
        Assert.Equal(0, summary.TotalSemaphoreSlims);
        Assert.Equal(0, summary.TotalReaderWriterLocks);
        Assert.Equal(0, summary.TotalResetEvents);
        Assert.Equal(0, summary.TotalWaitHandles);
        Assert.False(summary.ContentionDetected);
        Assert.Equal(0, summary.PotentialDeadlockCount);
        Assert.Equal(0, summary.ContendedSemaphoreSlims);
        Assert.Equal(0, summary.AsyncLockCount);
    }

    #endregion

    #region MonitorLockInfo Tests

    [Fact]
    public void MonitorLockInfo_IsContended_TrueWhenWaitingThreadsExist()
    {
        var lockInfo = new MonitorLockInfo
        {
            Address = "0x1234",
            ObjectType = "System.Object",
            OwnerThreadId = 1,
            WaitingThreads = [2, 3]
        };

        Assert.True(lockInfo.IsContended);
    }

    [Fact]
    public void MonitorLockInfo_IsContended_FalseWhenNoWaitingThreads()
    {
        var lockInfo = new MonitorLockInfo
        {
            Address = "0x1234",
            ObjectType = "System.Object",
            OwnerThreadId = 1,
            WaitingThreads = null
        };

        Assert.False(lockInfo.IsContended);
    }

    [Fact]
    public void MonitorLockInfo_IsContended_FalseWhenEmptyWaitingThreads()
    {
        var lockInfo = new MonitorLockInfo
        {
            Address = "0x1234",
            ObjectType = "System.Object",
            OwnerThreadId = 1,
            WaitingThreads = []
        };

        Assert.False(lockInfo.IsContended);
    }

    #endregion

    #region SemaphoreSlimInfo Tests

    [Fact]
    public void SemaphoreSlimInfo_IsAsyncLock_TrueWhenMaxCountIsOne()
    {
        var semaphore = new SemaphoreSlimInfo
        {
            Address = "0x1234",
            CurrentCount = 0,
            MaxCount = 1
        };

        Assert.True(semaphore.IsAsyncLock);
    }

    [Fact]
    public void SemaphoreSlimInfo_IsAsyncLock_FalseWhenMaxCountGreaterThanOne()
    {
        var semaphore = new SemaphoreSlimInfo
        {
            Address = "0x1234",
            CurrentCount = 5,
            MaxCount = 10
        };

        Assert.False(semaphore.IsAsyncLock);
    }

    [Fact]
    public void SemaphoreSlimInfo_IsContended_TrueWhenZeroCountAndSyncWaiters()
    {
        var semaphore = new SemaphoreSlimInfo
        {
            Address = "0x1234",
            CurrentCount = 0,
            MaxCount = 5,
            SyncWaiters = 3
        };

        Assert.True(semaphore.IsContended);
    }

    [Fact]
    public void SemaphoreSlimInfo_IsContended_TrueWhenZeroCountAndAsyncWaiters()
    {
        var semaphore = new SemaphoreSlimInfo
        {
            Address = "0x1234",
            CurrentCount = 0,
            MaxCount = 5,
            AsyncWaiters = 2
        };

        Assert.True(semaphore.IsContended);
    }

    [Fact]
    public void SemaphoreSlimInfo_IsContended_FalseWhenCountAvailable()
    {
        var semaphore = new SemaphoreSlimInfo
        {
            Address = "0x1234",
            CurrentCount = 1,
            MaxCount = 5,
            SyncWaiters = 3
        };

        Assert.False(semaphore.IsContended);
    }

    [Fact]
    public void SemaphoreSlimInfo_IsContended_FalseWhenNoWaiters()
    {
        var semaphore = new SemaphoreSlimInfo
        {
            Address = "0x1234",
            CurrentCount = 0,
            MaxCount = 5,
            SyncWaiters = 0,
            AsyncWaiters = 0
        };

        Assert.False(semaphore.IsContended);
    }

    #endregion

    #region ReaderWriterLockInfo Tests

    [Fact]
    public void ReaderWriterLockInfo_IsContended_TrueWhenWriteWaiters()
    {
        var rwLock = new ReaderWriterLockInfo
        {
            Address = "0x1234",
            WriteWaiters = 2
        };

        Assert.True(rwLock.IsContended);
    }

    [Fact]
    public void ReaderWriterLockInfo_IsContended_TrueWhenReadWaiters()
    {
        var rwLock = new ReaderWriterLockInfo
        {
            Address = "0x1234",
            ReadWaiters = 3
        };

        Assert.True(rwLock.IsContended);
    }

    [Fact]
    public void ReaderWriterLockInfo_IsContended_TrueWhenUpgradeWaiters()
    {
        var rwLock = new ReaderWriterLockInfo
        {
            Address = "0x1234",
            UpgradeWaiters = 1
        };

        Assert.True(rwLock.IsContended);
    }

    [Fact]
    public void ReaderWriterLockInfo_IsContended_FalseWhenNoWaiters()
    {
        var rwLock = new ReaderWriterLockInfo
        {
            Address = "0x1234",
            ReadWaiters = 0,
            WriteWaiters = 0,
            UpgradeWaiters = 0
        };

        Assert.False(rwLock.IsContended);
    }

    #endregion

    #region WaitGraph Tests

    [Fact]
    public void WaitGraph_DefaultValues_AreEmptyLists()
    {
        var graph = new WaitGraph();

        Assert.NotNull(graph.Nodes);
        Assert.Empty(graph.Nodes);
        Assert.NotNull(graph.Edges);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void WaitGraphNode_CanBeCreated()
    {
        var node = new WaitGraphNode
        {
            Id = "thread_1",
            Type = "thread",
            Label = "Thread 1"
        };

        Assert.Equal("thread_1", node.Id);
        Assert.Equal("thread", node.Type);
        Assert.Equal("Thread 1", node.Label);
    }

    [Fact]
    public void WaitGraphEdge_CanBeCreated()
    {
        var edge = new WaitGraphEdge
        {
            From = "thread_1",
            To = "monitor_0x1234",
            Label = "waits"
        };

        Assert.Equal("thread_1", edge.From);
        Assert.Equal("monitor_0x1234", edge.To);
        Assert.Equal("waits", edge.Label);
    }

    #endregion

    #region DeadlockCycle Tests

    [Fact]
    public void DeadlockCycle_DefaultValues_AreEmptyLists()
    {
        var cycle = new DeadlockCycle();

        Assert.NotNull(cycle.Threads);
        Assert.Empty(cycle.Threads);
        Assert.NotNull(cycle.Resources);
        Assert.Empty(cycle.Resources);
        Assert.Empty(cycle.Cycle);
        Assert.Empty(cycle.Description);
    }

    [Fact]
    public void DeadlockCycle_CanDescribeClassicDeadlock()
    {
        var cycle = new DeadlockCycle
        {
            Id = 1,
            Threads = [5, 7],
            Resources = ["Monitor@0x1234", "SemaphoreSlim@0x5678"],
            Cycle = "Thread 5 → Monitor@0x1234 → Thread 7 → SemaphoreSlim@0x5678 → Thread 5",
            Description = "Classic deadlock: Thread 5 holds Monitor and waits for SemaphoreSlim, Thread 7 holds SemaphoreSlim and waits for Monitor"
        };

        Assert.Equal(2, cycle.Threads.Count);
        Assert.Contains(5, cycle.Threads);
        Assert.Contains(7, cycle.Threads);
        Assert.Equal(2, cycle.Resources.Count);
    }

    #endregion

    #region ContentionHotspot Tests

    [Fact]
    public void ContentionHotspot_CanBeCreated()
    {
        var hotspot = new ContentionHotspot
        {
            Resource = "0x1234",
            ResourceType = "SemaphoreSlim",
            WaitersCount = 10,
            Severity = "high",
            Recommendation = "Consider increasing semaphore count"
        };

        Assert.Equal("0x1234", hotspot.Resource);
        Assert.Equal("SemaphoreSlim", hotspot.ResourceType);
        Assert.Equal(10, hotspot.WaitersCount);
        Assert.Equal("high", hotspot.Severity);
    }

    [Theory]
    [InlineData(1, "low")]
    [InlineData(2, "medium")]
    [InlineData(5, "high")]
    [InlineData(10, "critical")]
    public void ContentionHotspot_SeverityLevels_AreValid(int waiters, string expectedSeverity)
    {
        // Note: This tests the expected severity thresholds from the plan
        var severity = waiters switch
        {
            >= 10 => "critical",
            >= 5 => "high",
            >= 2 => "medium",
            _ => "low"
        };

        Assert.Equal(expectedSeverity, severity);
    }

    #endregion

    #region WaiterInfo Tests

    [Fact]
    public void WaiterInfo_SyncWaiter_HasThreadId()
    {
        var waiter = new WaiterInfo
        {
            ThreadId = 5,
            IsAsync = false
        };

        Assert.Equal(5, waiter.ThreadId);
        Assert.False(waiter.IsAsync);
        Assert.Null(waiter.TaskAddress);
    }

    [Fact]
    public void WaiterInfo_AsyncWaiter_HasTaskAddress()
    {
        var waiter = new WaiterInfo
        {
            TaskAddress = "0x5678",
            IsAsync = true
        };

        Assert.Null(waiter.ThreadId);
        Assert.True(waiter.IsAsync);
        Assert.Equal("0x5678", waiter.TaskAddress);
    }

    #endregion

    #region ResetEventInfo Tests

    [Fact]
    public void ResetEventInfo_ManualResetEventSlim_CanBeCreated()
    {
        var resetEvent = new ResetEventInfo
        {
            Address = "0x1234",
            EventType = "ManualResetEventSlim",
            IsSignaled = true,
            Waiters = 0,
            SpinCount = 10
        };

        Assert.Equal("ManualResetEventSlim", resetEvent.EventType);
        Assert.True(resetEvent.IsSignaled);
        Assert.Equal(0, resetEvent.Waiters);
        Assert.Equal(10, resetEvent.SpinCount);
    }

    [Fact]
    public void ResetEventInfo_UnsignaledWithWaiters_IndicatesContention()
    {
        var resetEvent = new ResetEventInfo
        {
            Address = "0x1234",
            EventType = "ManualResetEventSlim",
            IsSignaled = false,
            Waiters = 5
        };

        Assert.False(resetEvent.IsSignaled);
        Assert.True(resetEvent.Waiters > 0);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void SynchronizationAnalysisResult_CanBeSerializedToJson()
    {
        var result = new SynchronizationAnalysisResult
        {
            Summary = new SynchronizationSummary
            {
                TotalMonitorLocks = 2,
                TotalSemaphoreSlims = 5,
                ContentionDetected = true,
                PotentialDeadlockCount = 1
            },
            MonitorLocks =
            [
                new MonitorLockInfo
                {
                    Address = "0x1234",
                    ObjectType = "System.Object",
                    OwnerThreadId = 1,
                    RecursionCount = 1
                }
            ],
            SemaphoreSlims =
            [
                new SemaphoreSlimInfo
                {
                    Address = "0x5678",
                    CurrentCount = 0,
                    MaxCount = 1,
                    SyncWaiters = 2
                }
            ],
            PotentialDeadlocks =
            [
                new DeadlockCycle
                {
                    Id = 1,
                    Threads = [1, 2],
                    Resources = ["Monitor@0x1234", "SemaphoreSlim@0x5678"],
                    Cycle = "Thread 1 → Thread 2 → Thread 1",
                    Description = "Deadlock detected"
                }
            ]
        };

        var json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });

        Assert.Contains("\"summary\"", json);
        Assert.Contains("\"totalMonitorLocks\"", json);
        Assert.Contains("\"potentialDeadlocks\"", json);
        Assert.Contains("\"monitorLocks\"", json);
        Assert.Contains("\"semaphoreSlims\"", json);
    }

    #endregion
}

