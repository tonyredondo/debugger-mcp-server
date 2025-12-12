using DebuggerMcp.Analysis.Synchronization;

namespace DebuggerMcp.Tests.Analysis.Synchronization;

/// <summary>
/// Unit tests for summary and contention hotspot logic.
/// </summary>
public class SynchronizationAnalyzerSummaryTests
{
    [Fact]
    public void IdentifyContentionHotspotsFromResult_SortsBySeverityThenWaiterCount()
    {
        var result = new SynchronizationAnalysisResult
        {
            MonitorLocks =
            [
                new MonitorLockInfo
                {
                    Address = "0x1",
                    ObjectType = "A",
                    WaitingThreads = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10] // critical
                }
            ],
            SemaphoreSlims =
            [
                new SemaphoreSlimInfo
                {
                    Address = "0x2",
                    CurrentCount = 0,
                    MaxCount = 1,
                    SyncWaiters = 1,
                    AsyncWaiters = 1 // medium
                },
                new SemaphoreSlimInfo
                {
                    Address = "0x3",
                    CurrentCount = 0,
                    MaxCount = 1,
                    SyncWaiters = 3,
                    AsyncWaiters = 3 // high
                }
            ]
        };

        var hotspots = SynchronizationAnalyzer.IdentifyContentionHotspotsFromResult(result);

        Assert.Equal("critical", hotspots[0].Severity);
        Assert.Equal("0x1", hotspots[0].Resource);
        Assert.Equal("high", hotspots[1].Severity);
        Assert.Equal("0x3", hotspots[1].Resource);
        Assert.Equal("medium", hotspots[2].Severity);
        Assert.Equal("0x2", hotspots[2].Resource);
    }

    [Fact]
    public void BuildSummaryFromResult_ComputesCountsCorrectly()
    {
        var result = new SynchronizationAnalysisResult
        {
            MonitorLocks = [new MonitorLockInfo { Address = "0x1" }],
            SemaphoreSlims =
            [
                new SemaphoreSlimInfo { Address = "0x2", CurrentCount = 0, MaxCount = 1, SyncWaiters = 1, AsyncWaiters = 0 },
                new SemaphoreSlimInfo { Address = "0x3", CurrentCount = 1, MaxCount = 2, SyncWaiters = 0, AsyncWaiters = 0 }
            ],
            ReaderWriterLocks = [new ReaderWriterLockInfo { Address = "0x4" }],
            ResetEvents = [new ResetEventInfo { Address = "0x5" }],
            WaitHandles = [new WaitHandleInfo { Address = "0x6" }],
            PotentialDeadlocks = [new DeadlockCycle { Id = 1, Threads = [1, 2] }],
            ContentionHotspots = [new ContentionHotspot { Resource = "0x2", Severity = "medium" }]
        };

        var summary = SynchronizationAnalyzer.BuildSummaryFromResult(result);

        Assert.Equal(1, summary.TotalMonitorLocks);
        Assert.Equal(2, summary.TotalSemaphoreSlims);
        Assert.Equal(1, summary.TotalReaderWriterLocks);
        Assert.Equal(1, summary.TotalResetEvents);
        Assert.Equal(1, summary.TotalWaitHandles);
        Assert.True(summary.ContentionDetected);
        Assert.Equal(1, summary.PotentialDeadlockCount);
        Assert.Equal(1, summary.ContendedSemaphoreSlims);
        Assert.Equal(1, summary.AsyncLockCount);
    }
}

