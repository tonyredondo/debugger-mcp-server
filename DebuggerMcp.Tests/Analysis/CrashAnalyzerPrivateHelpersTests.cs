using System.Reflection;
using DebuggerMcp.Analysis;
using Moq;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Reflection-driven coverage tests for private parsing helpers in <see cref="CrashAnalyzer"/>.
/// </summary>
public class CrashAnalyzerPrivateHelpersTests
{
    private static StackFrame? ParseSingleFrame(CrashAnalyzer analyzer, string line)
    {
        var method = typeof(CrashAnalyzer).GetMethod("ParseSingleFrame", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (StackFrame?)method!.Invoke(analyzer, new object[] { line });
    }

    private static void RefreshSummaryCounts(CrashAnalysisResult result)
    {
        var method = typeof(CrashAnalyzer).GetMethod(
            "RefreshSummaryCounts",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(null, new object[] { result });
    }

    private static string? NormalizeRepoRelativePath(string sourceFile, string repositoryUrl)
    {
        var method = typeof(CrashAnalyzer).GetMethod(
            "NormalizeRepoRelativePath",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string?)method!.Invoke(null, new object[] { sourceFile, repositoryUrl });
    }

    private static (bool Resolved, string Url, string Provider) TryResolveDotnetRuntimeSourceUrl(
        StackFrame frame,
        CrashAnalysisResult result)
    {
        var method = typeof(CrashAnalyzer).GetMethod(
            "TryResolveDotnetRuntimeSourceUrl",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var args = new object?[] { frame, result, null, null };
        var resolved = (bool)method!.Invoke(null, args)!;
        return (resolved, args[2] as string ?? string.Empty, args[3] as string ?? string.Empty);
    }

    [Fact]
    public void ParseSingleFrame_WithBacktickAndSp_ParsesModuleFunctionAndSource()
    {
        var mockManager = new Mock<IDebuggerManager>();
        var analyzer = new CrashAnalyzer(mockManager.Object);

        var frame = ParseSingleFrame(
            analyzer,
            "  * frame #0: 0x0000000100000000 SP=0x0000000000001000 MyApp`Main + 12 at /src/app.cs:42");

        Assert.NotNull(frame);
        Assert.Equal(0, frame!.FrameNumber);
        Assert.Equal("0x0000000100000000", frame.InstructionPointer);
        Assert.Equal("0x0000000000001000", frame.StackPointer);
        Assert.Equal("MyApp", frame.Module);
        Assert.Contains("Main", frame.Function);
        Assert.Contains("/src/app.cs:42", frame.Source);
    }

    [Fact]
    public void ParseSingleFrame_WithoutBacktick_ParsesNativeLibraryFrame()
    {
        var mockManager = new Mock<IDebuggerManager>();
        var analyzer = new CrashAnalyzer(mockManager.Object);

        var frame = ParseSingleFrame(
            analyzer,
            "    frame #1: 0x0000000100000100 SP=0x0000000000002000 libstdc++.so.6 + 123 at /usr/lib/libstdc++.so.6");

        Assert.NotNull(frame);
        Assert.Equal(1, frame!.FrameNumber);
        Assert.Equal("libstdc++.so.6", frame.Module);
        Assert.False(frame.IsManaged);
    }

    [Fact]
    public void ParseSingleFrame_SpOnlyFormat_ParsesAsManagedJitFrame()
    {
        var mockManager = new Mock<IDebuggerManager>();
        var analyzer = new CrashAnalyzer(mockManager.Object);

        var frame = ParseSingleFrame(
            analyzer,
            "    frame #2: 0x0000000100000200 SP=0x0000000000003000");

        Assert.NotNull(frame);
        Assert.Equal(2, frame!.FrameNumber);
        Assert.True(frame.IsManaged);
        Assert.Contains("[JIT Code", frame.Function);
        Assert.Equal("0x0000000000003000", frame.StackPointer);
    }

    [Fact]
    public void ParseSingleFrame_JitFrameWithRepeatedAddress_ParsesAsManagedJitFrame()
    {
        var mockManager = new Mock<IDebuggerManager>();
        var analyzer = new CrashAnalyzer(mockManager.Object);

        var frame = ParseSingleFrame(
            analyzer,
            "    frame #3: 0x0000000100000300 0x0000000100000300");

        Assert.NotNull(frame);
        Assert.Equal(3, frame!.FrameNumber);
        Assert.True(frame.IsManaged);
        Assert.Null(frame.StackPointer);
    }

    [Fact]
    public void ParseSingleFrame_SimpleFormatWithSpAndSource_ParsesSourceAndCleansOffset()
    {
        var mockManager = new Mock<IDebuggerManager>();
        var analyzer = new CrashAnalyzer(mockManager.Object);

        var frame = ParseSingleFrame(
            analyzer,
            "    frame #5: 0x0000000100000500 SP=0x0000000000005000 someSymbol + 7 at /src/file.c:12");

        Assert.NotNull(frame);
        Assert.Equal(5, frame!.FrameNumber);
        Assert.Equal("0x0000000000005000", frame.StackPointer);
        Assert.Equal(string.Empty, frame.Module);
        Assert.Equal("[someSymbol]", frame.Function);
        Assert.Equal("/src/file.c:12", frame.Source);
    }

    [Fact]
    public void RefreshSummaryCounts_WhenDescriptionHasCounts_UpdatesToFinalThreadAndFrameCounts()
    {
        var result = new CrashAnalysisResult
        {
            Summary = new AnalysisSummary
            {
                Description = "Crash Type: Unknown. Found 47 threads (1280 total frames, 49 in faulting thread), 11 modules.  .NET Analysis: CLR 10.0.0.0. "
            },
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo
                    {
                        ThreadId = "1",
                        IsFaulting = true,
                        CallStack = [new StackFrame(), new StackFrame(), new StackFrame()]
                    },
                    new ThreadInfo
                    {
                        ThreadId = "2",
                        CallStack = [new StackFrame()]
                    }
                ]
            },
            Modules = [new ModuleInfo(), new ModuleInfo()]
        };

        RefreshSummaryCounts(result);

        Assert.Equal(2, result.Threads!.OsThreadCount);
        Assert.Contains("Found 2 threads (4 total frames, 3 in faulting thread), 2 modules.", result.Summary!.Description);
    }

    [Fact]
    public void NormalizeRepoRelativePath_WhenDotnetRepoAndSrcNative_RewritesToSrcRuntime()
    {
        var repo = "https://github.com/dotnet/dotnet";
        var relative = NormalizeRepoRelativePath("/__w/1/s/src/native/corehost/corehost.cpp", repo);
        Assert.Equal("src/runtime/src/native/corehost/corehost.cpp", relative);
    }

    [Fact]
    public void TryResolveDotnetRuntimeSourceUrl_WithDotnetAssemblyMetadata_ResolvesNativeRuntimePath()
    {
        var frame = new StackFrame
        {
            Module = "libcoreclr.so",
            Function = "ManagedThreadBase::KickOff",
            SourceFile = "/__w/1/s/src/runtime/src/coreclr/vm/threads.cpp",
            LineNumber = 7058,
            IsManaged = false
        };

        var result = new CrashAnalysisResult
        {
            Assemblies = new AssembliesInfo
            {
                Items =
                [
                    new AssemblyVersionInfo
                    {
                        Name = "System.Private.CoreLib",
                        RepositoryUrl = "https://github.com/dotnet/dotnet",
                        CommitHash = "b0f34d51fccc69fd334253924abd8d6853fad7aa"
                    }
                ]
            },
            Environment = new EnvironmentInfo { Platform = new PlatformInfo { Os = "Linux" } }
        };

        var (resolved, url, provider) = TryResolveDotnetRuntimeSourceUrl(frame, result);
        Assert.True(resolved);
        Assert.Equal("GitHub", provider);
        Assert.Equal(
            "https://github.com/dotnet/dotnet/blob/b0f34d51fccc69fd334253924abd8d6853fad7aa/src/runtime/src/coreclr/vm/threads.cpp#L7058",
            url);
    }
}
