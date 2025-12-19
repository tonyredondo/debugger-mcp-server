using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for canonical report caching behavior on <see cref="DebuggerSession"/>.
/// </summary>
public class DebuggerSessionReportCacheTests
{
    [Fact]
    public void TryGetCachedReport_RequiresWatches_ReturnsFalseWhenCacheDoesNotIncludeWatches()
    {
        var session = CreateSession();

        session.SetCachedReport("dump-1", DateTime.UtcNow, "{ \"ok\": true }", includesWatches: false, includesSecurity: true, maxStackFrames: 0);

        var ok = session.TryGetCachedReport("dump-1", requireWatches: true, requireSecurity: false, requireAllFrames: false, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryGetCachedReport_RequiresSecurity_ReturnsFalseWhenCacheDoesNotIncludeSecurity()
    {
        var session = CreateSession();

        session.SetCachedReport("dump-1", DateTime.UtcNow, "{ \"ok\": true }", includesWatches: true, includesSecurity: false, maxStackFrames: 0);

        var ok = session.TryGetCachedReport("dump-1", requireWatches: false, requireSecurity: true, requireAllFrames: false, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryGetCachedReport_ReturnsTrueWhenCacheIsSupersetOfRequirements()
    {
        var session = CreateSession();

        session.SetCachedReport("dump-1", DateTime.UtcNow, "{ \"ok\": true }", includesWatches: true, includesSecurity: true, maxStackFrames: 0);

        Assert.True(session.TryGetCachedReport("dump-1", requireWatches: false, requireSecurity: false, requireAllFrames: false, out _));
        Assert.True(session.TryGetCachedReport("dump-1", requireWatches: true, requireSecurity: false, requireAllFrames: false, out _));
        Assert.True(session.TryGetCachedReport("dump-1", requireWatches: false, requireSecurity: true, requireAllFrames: false, out _));
        Assert.True(session.TryGetCachedReport("dump-1", requireWatches: true, requireSecurity: true, requireAllFrames: false, out _));
    }

    [Fact]
    public void SetCachedReport_DoesNotOverwriteMoreCompleteReportWithLessCompleteReport()
    {
        var session = CreateSession();
        var firstGeneratedAt = DateTime.UtcNow;

        session.SetCachedReport("dump-1", firstGeneratedAt, "{ \"report\": 1 }", includesWatches: true, includesSecurity: true, maxStackFrames: 0);
        session.SetCachedReport("dump-1", firstGeneratedAt.AddMinutes(1), "{ \"report\": 2 }", includesWatches: false, includesSecurity: false, maxStackFrames: 0);

        Assert.True(session.TryGetCachedReport("dump-1", requireWatches: true, requireSecurity: true, requireAllFrames: true, out var cached));
        Assert.Equal("{ \"report\": 1 }", cached);
        Assert.True(session.CachedReportIncludesWatches);
        Assert.True(session.CachedReportIncludesSecurity);
    }

    [Fact]
    public void SetCachedReport_OverwritesWhenIncomingReportIsMoreComplete()
    {
        var session = CreateSession();
        var firstGeneratedAt = DateTime.UtcNow;

        session.SetCachedReport("dump-1", firstGeneratedAt, "{ \"report\": 1 }", includesWatches: false, includesSecurity: false, maxStackFrames: 0);
        session.SetCachedReport("dump-1", firstGeneratedAt.AddMinutes(1), "{ \"report\": 2 }", includesWatches: true, includesSecurity: true, maxStackFrames: 0);

        Assert.True(session.TryGetCachedReport("dump-1", requireWatches: true, requireSecurity: true, requireAllFrames: true, out var cached));
        Assert.Equal("{ \"report\": 2 }", cached);
        Assert.True(session.CachedReportIncludesWatches);
        Assert.True(session.CachedReportIncludesSecurity);
    }

    [Fact]
    public void SetCachedReport_DoesNotOverwriteAllFramesReportWithCappedFramesReport()
    {
        var session = CreateSession();
        var firstGeneratedAt = DateTime.UtcNow;

        session.SetCachedReport("dump-1", firstGeneratedAt, "{ \"report\": \"full\" }", includesWatches: true, includesSecurity: true, maxStackFrames: 0);
        session.SetCachedReport("dump-1", firstGeneratedAt.AddMinutes(1), "{ \"report\": \"capped\" }", includesWatches: true, includesSecurity: true, maxStackFrames: 10);

        Assert.True(session.TryGetCachedReport("dump-1", requireWatches: true, requireSecurity: true, requireAllFrames: true, out var cached));
        Assert.Equal("{ \"report\": \"full\" }", cached);
        Assert.Equal(0, session.CachedReportMaxStackFrames);
    }

    private static DebuggerSession CreateSession()
        => new()
        {
            SessionId = "test-session",
            UserId = "test-user",
            Manager = new FakeDebuggerManager()
        };

    private sealed class FakeDebuggerManager : IDebuggerManager
    {
        public bool IsInitialized => true;
        public bool IsDumpOpen => true;
        public string? CurrentDumpPath => null;
        public string DebuggerType => "LLDB";
        public bool IsSosLoaded => false;
        public bool IsDotNetDump => false;

        public Task InitializeAsync() => Task.CompletedTask;
        public void OpenDumpFile(string dumpFilePath, string? executablePath = null) => throw new NotSupportedException();
        public void CloseDump() => throw new NotSupportedException();
        public string ExecuteCommand(string command) => throw new NotSupportedException();
        public void LoadSosExtension() => throw new NotSupportedException();
        public void ConfigureSymbolPath(string symbolPath) => throw new NotSupportedException();
        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
