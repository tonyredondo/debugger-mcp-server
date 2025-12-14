using System;
using System.Collections.Generic;
using System.Reflection;
using DebuggerMcp.Analysis;
using DebuggerMcp.SourceLink;
using Moq;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Regression tests for LLDB/DWARF source locations that report an unknown line number (":0").
/// </summary>
public class CrashAnalyzerSourceLineZeroTests
{
    [Fact]
    public void ResolveSourceLinks_WhenNativeFrameHasLineZero_EmitsFileUrlWithoutLineAnchor()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        var analyzer = new CrashAnalyzer(mockManager.Object, new SourceLinkResolver());

        var result = new CrashAnalysisResult
        {
            Environment = new EnvironmentInfo
            {
                Platform = new PlatformInfo { Os = "Linux" }
            },
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
            Threads = new ThreadsInfo
            {
                All =
                [
                    new ThreadInfo
                    {
                        ThreadId = "7",
                        CallStack =
                        [
                            new StackFrame
                            {
                                FrameNumber = 0,
                                IsManaged = false,
                                Module = "libclrjit.so",
                                Source = "/__w/1/s/src/runtime/src/coreclr/jit/importer.cpp:0"
                            }
                        ]
                    }
                ]
            }
        };

        // Act
        var method = typeof(CrashAnalyzer).GetMethod("ResolveSourceLinks", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(analyzer, new object[] { result });

        // Assert
        var frame = result.Threads.All[0].CallStack[0];
        Assert.Null(frame.LineNumber);
        Assert.Equal("/__w/1/s/src/runtime/src/coreclr/jit/importer.cpp", frame.SourceFile);
        Assert.Equal(
            "https://github.com/dotnet/dotnet/blob/b0f34d51fccc69fd334253924abd8d6853fad7aa/src/runtime/src/coreclr/jit/importer.cpp",
            frame.SourceUrl);
        Assert.Equal("GitHub", frame.SourceProvider);
    }
}

