using System.Reflection;
using DebuggerMcp.Analysis;
using DebuggerMcp.SourceLink;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Tests for Source Link fallback behavior when Portable PDB Source Link data is unavailable.
/// </summary>
public class CrashAnalyzerSourceLinkFallbackTests
{
    [Fact]
    public void ResolveSourceLinks_WhenPdbNotFound_UsesAssemblyRepositoryMetadataFallback()
    {
        var frame = new StackFrame
        {
            FrameNumber = 17,
            Module = "System.Threading",
            Function = "System.Threading.ManualResetEventSlim.Wait(Int32, System.Threading.CancellationToken)",
            SourceFile = "/_/src/libraries/System.Private.CoreLib/src/System/Threading/PortableThreadPool.ThreadCounts.cs",
            LineNumber = 38,
            Source = "/_/src/libraries/System.Private.CoreLib/src/System/Threading/PortableThreadPool.ThreadCounts.cs:38",
            IsManaged = true
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
            Assemblies = new AssembliesInfo
            {
                Count = 1,
                Items =
                [
                    new AssemblyVersionInfo
                    {
                        Name = "System.Threading",
                        RepositoryUrl = "https://github.com/dotnet/dotnet",
                        CommitHash = "b0f34d51fccc69fd334253924abd8d6853fad7aa",
                        SourceUrl = "https://github.com/dotnet/dotnet/tree/b0f34d51fccc69fd334253924abd8d6853fad7aa"
                    }
                ]
            },
            Modules = []
        };

        // No symbol search paths configured -> SourceLinkResolver cannot find a PDB and must fall back.
        var analyzer = new TestCrashAnalyzer(new FakeDebuggerManager(), new SourceLinkResolver(logger: NullLogger.Instance));
        analyzer.ResolveSourceLinksForTest(result);

        Assert.Equal(
            "https://github.com/dotnet/dotnet/blob/b0f34d51fccc69fd334253924abd8d6853fad7aa/src/runtime/src/libraries/System.Private.CoreLib/src/System/Threading/PortableThreadPool.ThreadCounts.cs#L38",
            frame.SourceUrl);
        Assert.Equal("GitHub", frame.SourceProvider);
    }

    [Fact]
    public void FindModulePath_WhenAssembliesContainDottedName_ReturnsAssemblyPath()
    {
        var result = new CrashAnalysisResult
        {
            Assemblies = new AssembliesInfo
            {
                Count = 1,
                Items =
                [
                    new AssemblyVersionInfo
                    {
                        Name = "System.Threading.Tasks",
                        Path = "/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Threading.Tasks.dll"
                    }
                ]
            },
            Modules = []
        };

        var method = typeof(CrashAnalyzer).GetMethod("FindModulePath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var modulePath = (string?)method!.Invoke(null, new object?[] { "System.Threading.Tasks", result });

        Assert.Equal("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Threading.Tasks.dll", modulePath);
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
        public bool IsSosLoaded => true;
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
