using DebuggerMcp.SourceLink;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for caching and lifecycle of <see cref="SourceLinkResolver"/> on <see cref="DebuggerSession"/>.
/// </summary>
public class DebuggerSessionSourceLinkResolverTests
{
    [Fact]
    public void GetOrCreateSourceLinkResolver_SameDumpId_ReturnsSameInstance()
    {
        var session = new DebuggerSession
        {
            SessionId = "test-session",
            UserId = "test-user",
            Manager = new FakeDebuggerManager()
        };

        var first = session.GetOrCreateSourceLinkResolver("dump-123", () => new SourceLinkResolver(NullLogger.Instance));
        var second = session.GetOrCreateSourceLinkResolver("dump-123", () => new SourceLinkResolver(NullLogger.Instance));

        Assert.Same(first, second);
        Assert.Equal("dump-123", session.SourceLinkResolverDumpId);
    }

    [Fact]
    public void GetOrCreateSourceLinkResolver_DifferentDumpId_CreatesNewInstance()
    {
        var session = new DebuggerSession
        {
            SessionId = "test-session",
            UserId = "test-user",
            Manager = new FakeDebuggerManager()
        };

        var first = session.GetOrCreateSourceLinkResolver("dump-123", () => new SourceLinkResolver(NullLogger.Instance));
        var second = session.GetOrCreateSourceLinkResolver("dump-456", () => new SourceLinkResolver(NullLogger.Instance));

        Assert.NotSame(first, second);
        Assert.Equal("dump-456", session.SourceLinkResolverDumpId);
    }

    [Fact]
    public void ClearSourceLinkResolver_RemovesCachedInstance()
    {
        var session = new DebuggerSession
        {
            SessionId = "test-session",
            UserId = "test-user",
            Manager = new FakeDebuggerManager()
        };

        var first = session.GetOrCreateSourceLinkResolver("dump-123", () => new SourceLinkResolver(NullLogger.Instance));

        session.ClearSourceLinkResolver();

        var second = session.GetOrCreateSourceLinkResolver("dump-123", () => new SourceLinkResolver(NullLogger.Instance));

        Assert.NotSame(first, second);
        Assert.Equal("dump-123", session.SourceLinkResolverDumpId);
    }

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

