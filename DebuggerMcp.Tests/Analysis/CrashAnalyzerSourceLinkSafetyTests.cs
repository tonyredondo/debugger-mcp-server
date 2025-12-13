using DebuggerMcp.Analysis;
using DebuggerMcp.SourceLink;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Regression tests to ensure assembly-metadata SourceLink fallback never produces misleading URLs.
/// </summary>
public class CrashAnalyzerSourceLinkSafetyTests
{
    [Fact]
    public void ResolveSourceLinks_WhenFrameIsNative_DoesNotUseAssemblyMetadataFallback()
    {
        var frame = new StackFrame
        {
            FrameNumber = 1,
            Module = "libcoreclr.so",
            Function = "CorUnix::CPalSynchronizationManager::ThreadNativeWait(...)",
            SourceFile = "synchmanager.cpp",
            LineNumber = 464,
            IsManaged = false
        };

        var result = new CrashAnalysisResult
        {
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo
                    {
                        ThreadId = "1",
                        CallStack = [frame]
                    }
                ]
            },
            // Even if managed assemblies have repo metadata, native frames must not inherit it.
            Assemblies = new AssembliesInfo
            {
                Count = 1,
                Items =
                [
                    new AssemblyVersionInfo
                    {
                        Name = "ReadableExpressions",
                        RepositoryUrl = "https://github.com/AgileObjects/ReadableExpressions",
                        CommitHash = "b7bf9ffe0d81c5a3620859e3cfd71369f82d922c"
                    }
                ]
            },
            Modules = []
        };

        var analyzer = new TestCrashAnalyzer(new FakeDebuggerManager(), new SourceLinkResolver(logger: NullLogger.Instance));
        analyzer.ResolveSourceLinksForTest(result);

        Assert.True(string.IsNullOrWhiteSpace(frame.SourceUrl));
        Assert.True(string.IsNullOrWhiteSpace(frame.SourceProvider));
    }

    private sealed class TestCrashAnalyzer(IDebuggerManager debuggerManager, SourceLinkResolver sourceLinkResolver)
        : CrashAnalyzer(debuggerManager, sourceLinkResolver, logger: NullLogger.Instance)
    {
        public void ResolveSourceLinksForTest(CrashAnalysisResult result) => ResolveSourceLinks(result);
    }

    private sealed class FakeDebuggerManager : IDebuggerManager
    {
        public bool IsInitialized => true;
        public bool IsDumpOpen => true;
        public string? CurrentDumpPath => null;
        public string DebuggerType => "LLDB";
        public bool IsSosLoaded => false;
        public bool IsDotNetDump => true;

        public Task InitializeAsync() => Task.CompletedTask;
        public void OpenDumpFile(string dumpFilePath, string? executablePath = null) => throw new NotSupportedException();
        public void CloseDump() => throw new NotSupportedException();
        public void LoadSosExtension() => throw new NotSupportedException();
        public void ConfigureSymbolPath(string symbolPath) => throw new NotSupportedException();
        public string ExecuteCommand(string command) => throw new NotSupportedException();
        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

