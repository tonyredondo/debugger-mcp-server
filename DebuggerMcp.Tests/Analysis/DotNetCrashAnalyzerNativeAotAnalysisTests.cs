using DebuggerMcp.Analysis;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Tests for NativeAOT/trimming analysis flow in <see cref="DotNetCrashAnalyzer"/>.
/// </summary>
public class DotNetCrashAnalyzerNativeAotAnalysisTests
{
    private sealed class TestableDotNetCrashAnalyzer(IDebuggerManager debuggerManager)
        : DotNetCrashAnalyzer(debuggerManager, sourceLinkResolver: null, clrMdAnalyzer: null, logger: NullLogger.Instance)
    {
        public Task RunAnalyzeNativeAotAsync(CrashAnalysisResult result) => AnalyzeNativeAotAsync(result);
    }

    private sealed class StubDebuggerManager(string debuggerType, Dictionary<string, string> outputs) : IDebuggerManager
    {
        public bool IsInitialized { get; private set; } = true;
        public bool IsDumpOpen { get; private set; }
        public string? CurrentDumpPath { get; private set; }
        public string DebuggerType { get; } = debuggerType;
        public bool IsSosLoaded { get; private set; }
        public bool IsDotNetDump { get; private set; }
        public List<string> ExecutedCommands { get; } = new();

        public Task InitializeAsync()
        {
            IsInitialized = true;
            return Task.CompletedTask;
        }

        public void OpenDumpFile(string dumpFilePath, string? executablePath = null)
        {
            CurrentDumpPath = dumpFilePath;
            IsDumpOpen = true;
            IsDotNetDump = true;
        }

        public void CloseDump()
        {
            IsDumpOpen = false;
            CurrentDumpPath = null;
        }

        public string ExecuteCommand(string command)
        {
            ExecutedCommands.Add(command);
            return outputs.TryGetValue(command, out var output) ? output : string.Empty;
        }

        public void LoadSosExtension() => IsSosLoaded = true;
        public void ConfigureSymbolPath(string symbolPath) { }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AnalyzeNativeAotAsync_WhenModulesNotPresent_FetchesImageListAndPopulatesHighConfidence()
    {
        var manager = new StubDebuggerManager(
            debuggerType: "LLDB",
            outputs: new Dictionary<string, string>
            {
                ["image list"] = "System.Private.CoreLib.Native\nRuntime.WorkstationGC\n"
            });

        var analyzer = new TestableDotNetCrashAnalyzer(manager);

        var result = new CrashAnalysisResult
        {
            Modules = new List<ModuleInfo>(),
            Threads = new ThreadsInfo
            {
                All = new List<ThreadInfo>
                {
                    new()
                    {
                        ThreadId = "1",
                        CallStack = new List<StackFrame>
                        {
                            new() { Module = "MyApp", Function = "RhpNewArray" },
                            new() { Module = "MyApp", Function = "Type.GetMethod", SourceFile = "/tmp/file.cs", LineNumber = 12 }
                        }
                    }
                }
            },
            Exception = new ExceptionDetails
            {
                Type = "System.MissingMethodException",
                Message = "Method not found: 'System.Void MyNamespace.MyType.Missing(System.Int32)'",
                StackTrace = new List<StackFrame>
                {
                    new() { Module = "MyApp", Function = "Activator.CreateInstance" }
                }
            }
        };

        await analyzer.RunAnalyzeNativeAotAsync(result);

        Assert.Contains("image list", manager.ExecutedCommands);
        Assert.NotNull(result.Environment);
        Assert.NotNull(result.Environment!.NativeAot);
        Assert.True(result.Environment.NativeAot!.IsNativeAot);
        Assert.False(result.Environment.NativeAot.HasJitCompiler);
        Assert.NotNull(result.Environment.NativeAot.TrimmingAnalysis);
        Assert.Equal("high", result.Environment.NativeAot.TrimmingAnalysis!.Confidence);
    }

    [Fact]
    public async Task AnalyzeNativeAotAsync_WhenJitPresent_FiltersAbsenceIndicatorsAndMarksLowConfidence()
    {
        var analyzer = new TestableDotNetCrashAnalyzer(
            new StubDebuggerManager(
                debuggerType: "LLDB",
                outputs: new Dictionary<string, string>()));

        var result = new CrashAnalysisResult
        {
            Modules = new List<ModuleInfo>
            {
                // Missing coreclr triggers an absence indicator that should be filtered when JIT is present.
                new() { Name = "clrjit" },
                new() { Name = "Runtime.WorkstationGC" }
            },
            Threads = new ThreadsInfo
            {
                All = new List<ThreadInfo>
                {
                    new()
                    {
                        ThreadId = "1",
                        CallStack = new List<StackFrame>
                        {
                            new() { Module = "MyApp", Function = "S_P_CoreLib_System_Runtime_EH_DispatchEx" },
                            new() { Module = "MyApp", Function = "Assembly.LoadFrom" }
                        }
                    }
                }
            },
            Exception = new ExceptionDetails
            {
                Type = "System.MissingMemberException",
                Message = "Member 'Foo' not found on type 'MyNamespace.MyType'"
            }
        };

        await analyzer.RunAnalyzeNativeAotAsync(result);

        Assert.NotNull(result.Environment);
        Assert.NotNull(result.Environment!.NativeAot);

        var nativeAot = result.Environment.NativeAot!;
        Assert.True(nativeAot.HasJitCompiler);
        Assert.False(nativeAot.IsNativeAot);
        Assert.NotNull(nativeAot.TrimmingAnalysis);
        Assert.Equal("low", nativeAot.TrimmingAnalysis!.Confidence);
        Assert.False(nativeAot.TrimmingAnalysis.PotentialTrimmingIssue);

        Assert.NotNull(nativeAot.Indicators);
        Assert.DoesNotContain(nativeAot.Indicators!, i => i.Source == "module:absence");
    }
}
