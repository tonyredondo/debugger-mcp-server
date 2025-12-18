using System;
using System.Collections.Generic;
using DebuggerMcp.Analysis;
using DebuggerMcp.Reporting;
using DebuggerMcp.Watches;
using Xunit;

namespace DebuggerMcp.Tests.Reporting;

/// <summary>
/// Exercises the "kitchen sink" report generation paths to drive coverage across
/// both Markdown and HTML report generators.
/// </summary>
public class ReportGeneratorFullReportCoverageTests
{
    [Fact]
    public void MarkdownGenerator_Generate_WithRichAnalysis_IncludesDeepSections()
    {
        // Arrange
        var analysis = CreateRichAnalysis();
        var generator = new MarkdownReportGenerator();
        var options = new ReportOptions
        {
            IncludeCrashInfo = true,
            IncludeCallStacks = true,
            IncludeThreadInfo = true,
            IncludeModules = true,
            IncludeHeapStats = true,
            IncludeMemoryLeakInfo = true,
            IncludeDeadlockInfo = true,
            IncludeWatchResults = true,
            IncludeSecurityAnalysis = true,
            IncludeDotNetInfo = true,
            IncludeProcessInfo = true,
            IncludeRecommendations = true,
            IncludeCharts = true,
            MaxEnvironmentVariables = 1,
            MaxCallStackFrames = 5,
            MaxThreadsToShow = 2,
            MaxModulesToShow = 1
        };
        var metadata = CreateMetadata();

        // Act
        var report = generator.Generate(analysis, options, metadata);

        // Assert
        Assert.Contains("Crash Analysis Report", report);
        Assert.Contains(".NET Runtime Information", report);
        Assert.Contains("Exception Deep Analysis", report);
        Assert.Contains("Type/Method Resolution Analysis", report);
        Assert.Contains("NativeAOT", report);
        Assert.Contains("GC Heap Summary", report);
        Assert.Contains("Top Memory Consumers", report);
        Assert.Contains("String Duplicate Analysis", report);
        Assert.Contains("Security", report);
    }

    [Fact]
    public void HtmlGenerator_Generate_WithRichAnalysis_IncludesDeepSections()
    {
        // Arrange
        var analysis = CreateRichAnalysis();
        var generator = new HtmlReportGenerator();
        var options = new ReportOptions
        {
            IncludeCrashInfo = true,
            IncludeCallStacks = true,
            IncludeThreadInfo = true,
            IncludeModules = true,
            IncludeHeapStats = true,
            IncludeMemoryLeakInfo = true,
            IncludeDeadlockInfo = true,
            IncludeWatchResults = true,
            IncludeSecurityAnalysis = true,
            IncludeDotNetInfo = true,
            IncludeProcessInfo = true,
            IncludeRecommendations = true,
            IncludeCharts = true,
            MaxEnvironmentVariables = 1,
            MaxCallStackFrames = 5,
            MaxThreadsToShow = 2,
            MaxModulesToShow = 1
        };
        var metadata = CreateMetadata();

        // Act
        var report = generator.Generate(analysis, options, metadata);

        // Assert
        Assert.Contains("<html", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".NET Runtime Information", report);
        Assert.Contains("Exception Deep Analysis", report);
        Assert.Contains("Type/Method Resolution Analysis", report);
        Assert.Contains("NativeAOT", report);
        Assert.Contains("GC Heap Summary", report);
        Assert.Contains("Top Memory Consumers", report);
        Assert.Contains("String Duplicate Analysis", report);
        Assert.Contains("Security", report);
    }

    private static ReportMetadata CreateMetadata()
    {
        return new ReportMetadata
        {
            DumpId = "dump-coverage-test",
            DebuggerType = "LLDB",
            UserId = "coverage-bot",
            ServerVersion = "0.0.0-test",
            GeneratedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    private static CrashAnalysisResult CreateRichAnalysis()
    {
        var threads = new ThreadsInfo
        {
            Summary = new ThreadSummary
            {
                Total = 3,
                Background = 1,
                Unstarted = 1,
                Pending = 1,
                Dead = 1,
                FinalizerQueueLength = 123
            },
            ThreadPool = new ThreadPoolInfo
            {
                CpuUtilization = 95,
                WorkersTotal = 4,
                WorkersRunning = 4,
                WorkersIdle = 0,
                WorkerMinLimit = 4,
                WorkerMaxLimit = 4,
                IsPortableThreadPool = true
            },
            Deadlock = new DeadlockInfo
            {
                Detected = true,
                InvolvedThreads = new List<string> { "1", "2" },
                Locks = new List<LockInfo>
                {
                    new()
                    {
                        Address = "0xDEADBEEF",
                        Owner = "1",
                        Waiters = new List<string> { "2" }
                    }
                }
            },
            All = new List<ThreadInfo>
            {
                new()
                {
                    ThreadId = "1",
                    State = "Running",
                    TopFunction = "MyApp!Main",
                    IsFaulting = true,
                    ThreadType = "GC",
                    GcMode = "Preemptive",
                    LockCount = 2,
                    CurrentException = "System.MissingMethodException",
                    CallStack = new List<StackFrame>
                    {
                        new() { Module = "MyApp", Function = "MyApp.Program.Main" },
                        new() { Module = "System.Private.CoreLib", Function = "System.ThrowHelper.ThrowMissingMethodException" }
                    }
                },
                new()
                {
                    ThreadId = "2",
                    State = "Waiting",
                    TopFunction = "pthread_cond_wait",
                    CallStack = new List<StackFrame>
                    {
                        new() { Module = "libc", Function = "pthread_cond_wait" }
                    }
                }
            }
        };

        var asyncInfo = new AsyncInfo
        {
            HasDeadlock = true,
            Summary = new AsyncSummary
            {
                TotalTasks = 100,
                PendingTasks = 5,
                CompletedTasks = 90,
                FaultedTasks = 3,
                CanceledTasks = 2
            },
            StateMachines = new List<StateMachineInfo>
            {
                new()
                {
                    Address = "0x0000000000000100",
                    StateMachineType = "MyApp.MyStateMachine",
                    CurrentState = 1,
                    StateDescription = "MoveNext"
                }
            },
            FaultedTasks = new List<FaultedTaskInfo>
            {
                new()
                {
                    Address = "0x0000000000000200",
                    TaskType = "System.Threading.Tasks.Task",
                    Status = "Faulted",
                    ExceptionType = "System.InvalidOperationException",
                    ExceptionMessage = "Boom"
                }
            },
            Timers = CreateTimers(count: 12),
            AnalysisTimeMs = 123,
            WasAborted = true
        };

        return new CrashAnalysisResult
        {
            Summary = new AnalysisSummary
            {
                CrashType = "Managed Exception",
                Description = "Rich analysis to exercise report generators.",
                Recommendations = new List<string>
                {
                    "Update to matching assembly versions",
                    "Investigate potential deadlock"
                }
            },
            Exception = new ExceptionDetails
            {
                Type = "System.MissingMethodException",
                Message = "Method not found: Foo.Bar()",
                HResult = "80131513",
                Address = "0x0000000000001234",
                HasInnerException = true,
                NestedExceptionCount = 2,
                StackTrace = new List<StackFrame>
                {
                    new() { Function = "Foo.Bar" },
                    new() { Function = "Baz.Quux" }
                },
                Analysis = new ExceptionAnalysis
                {
                    FullTypeName = "System.MissingMethodException",
                    Message = "Method not found: Foo.Bar()",
                    HResult = "0x80131513",
                    ExceptionAddress = "0000000000001234",
                    Source = "MyApp",
                    TargetSite = new TargetSiteInfo
                    {
                        Name = "Foo.Bar",
                        DeclaringType = "Foo",
                        Signature = "void Bar()"
                    },
                    ExceptionChain = new List<ExceptionChainEntry>
                    {
                        new() { Depth = 0, Type = "System.MissingMethodException", Message = "Missing", HResult = "0x80131513" },
                        new() { Depth = 1, Type = "System.Exception", Message = "Inner", HResult = "0x80131500" }
                    },
                    CustomProperties = new Dictionary<string, object?>
                    {
                        ["AssemblyName"] = "MyApp",
                        ["LongValue"] = new string('x', 200)
                    },
                    TypeResolution = new TypeResolutionAnalysis
                    {
                        FailedType = "Foo",
                        MethodTable = "0000000000000001",
                        EEClass = "0000000000000002",
                        ExpectedMember = new ExpectedMemberInfo
                        {
                            Name = "Bar",
                            Signature = "void Bar()",
                            MemberType = "Method"
                        },
                        SimilarMethods = new List<MethodDescriptorInfo>
                        {
                            new() { Name = "Bar", Signature = "void Bar(int)" },
                            new() { Name = "BarAsync", Signature = "Task BarAsync()" }
                        }
                    }
                }
            },
            Environment = new EnvironmentInfo
            {
                Platform = new PlatformInfo
                {
                    Os = "linux",
                    Distribution = "Alpine",
                    IsAlpine = true,
                    LibcType = "musl",
                    Architecture = "x64",
                    PointerSize = 64,
                    RuntimeVersion = "8.0.1"
                },
                Runtime = new RuntimeInfo
                {
                    Type = "CoreCLR",
                    Version = "8.0.1",
                    ClrVersion = "8.0.0",
                    IsHosted = true
                },
                Process = new ProcessInfo
                {
                    Arguments = new List<string> { "/app/MyApp", "--mode", "test" },
                    EnvironmentVariables = new List<string>
                    {
                        "DOTNET_ENVIRONMENT=Production",
                        "DD_TRACE_ENABLED=true"
                    },
                    SensitiveDataFiltered = true
                },
                NativeAot = new NativeAotAnalysis
                {
                    IsNativeAot = true,
                    HasJitCompiler = false,
                    Indicators = new List<NativeAotIndicator>
                    {
                        new() { Source = "module", Pattern = "*.aot", MatchedValue = "MyApp.aot" }
                    },
                    TrimmingAnalysis = new TrimmingAnalysis
                    {
                        PotentialTrimmingIssue = true,
                        Confidence = "high",
                        ExceptionType = "System.MissingMethodException",
                        MissingMember = "Foo.Bar()",
                        CallingFrame = new StackFrame { Module = "MyApp", Function = "Foo.Bar" },
                        Recommendation = "Consider adding DynamicDependency or trimming annotations"
                    },
                    ReflectionUsage = new List<ReflectionUsageInfo>
                    {
                        new() { Location = "Foo.Bar", Pattern = "GetType", Risk = "high", Target = "Foo" }
                    }
                }
            },
            Threads = threads,
            Async = asyncInfo,
            Modules = new List<ModuleInfo>
            {
                new() { Name = "libc.so", BaseAddress = "0x00007f0000000000", HasSymbols = false },
                new() { Name = "MyApp.dll", BaseAddress = "0x00007f1000000000", HasSymbols = true }
            },
            Assemblies = new AssembliesInfo
            {
                Count = 2,
                Items = new List<AssemblyVersionInfo>
                {
                    new() { Name = "MyApp", AssemblyVersion = "1.2.3.0", FileVersion = "1.2.3.4" },
                    new() { Name = "System.Private.CoreLib", AssemblyVersion = "8.0.0.0", FileVersion = "8.0.1.0" }
                }
            },
            Memory = new MemoryInfo
            {
                HeapStats = new Dictionary<string, long>
                {
                    ["System.String"] = 1024 * 1024,
                    ["System.Byte[]"] = 512 * 1024
                },
                LeakAnalysis = new LeakAnalysis
                {
                    Detected = true,
                    Severity = "high",
                    TotalHeapBytes = 1024L * 1024 * 1024,
                    TopConsumers = new List<MemoryConsumer>
                    {
                        new() { TypeName = "System.String", Count = 100, TotalSize = 1024 * 1024 * 10 },
                        new() { TypeName = "MyApp.LeakyType", Count = 10, TotalSize = 1024 * 1024 }
                    },
                    PotentialIssueIndicators = new List<string> { "High Gen2", "Pinned handles" }
                },
                Gc = new GcSummary
                {
                    HeapCount = 2,
                    GcMode = "Workstation",
                    IsServerGC = false,
                    TotalHeapSize = 1024L * 1024 * 200,
                    Fragmentation = 0.42,
                    FragmentationBytes = 1024L * 1024 * 20,
                    GenerationSizes = new GenerationSizes
                    {
                        Gen0 = 1024 * 1024,
                        Gen1 = 1024 * 1024 * 2,
                        Gen2 = 1024 * 1024 * 50,
                        Loh = 1024 * 1024 * 100,
                        Poh = 1024 * 1024 * 20
                    },
                    Segments = new List<GcSegmentInfo>
                    {
                        new() { Address = "0x00000001", Size = 1024 * 1024, Kind = "Gen0" },
                        new() { Address = "0x00000002", Size = 1024 * 1024 * 100, Kind = "LOH" }
                    },
                    FinalizableObjectCount = 10
                },
                TopConsumers = new TopMemoryConsumers
                {
                    BySize = new List<TypeMemoryStats>
                    {
                        new() { Type = "System.String", Count = 1000, TotalSize = 1024 * 1024 * 20, AverageSize = 2048, LargestInstance = 4096, Percentage = 10.0 },
                        new() { Type = "System.Byte[]", Count = 50, TotalSize = 1024 * 1024 * 10, AverageSize = 204_800, LargestInstance = 400_000, Percentage = 5.0 }
                    },
                    ByCount = new List<TypeMemoryStats>
                    {
                        new() { Type = "System.Object", Count = 10_000, TotalSize = 1024 * 1024, AverageSize = 100, LargestInstance = 1000, Percentage = 0.5 }
                    }
                },
                Strings = new StringAnalysis
                {
                    Summary = new StringAnalysisSummary
                    {
                        TotalStrings = 10_000,
                        UniqueStrings = 100,
                        DuplicateStrings = 9_900,
                        TotalSize = 1024 * 1024 * 10,
                        WastedSize = 1024 * 1024 * 9,
                        WastedPercentage = 90.0
                    },
                    TopDuplicates = new List<StringDuplicateInfo>
                    {
                        new()
                        {
                            Value = "hello",
                            Count = 9999,
                            SizePerInstance = 20,
                            WastedBytes = 200_000,
                            Suggestion = "Intern strings"
                        }
                    },
                    ByLength = new StringLengthDistribution
                    {
                        Empty = 1,
                        Short = 10,
                        Medium = 20,
                        Long = 30,
                        VeryLong = 40
                    },
                    AnalysisTimeMs = 250,
                    WasAborted = false
                }
            },
            Watches = new WatchEvaluationReport
            {
                DumpId = "dump-coverage-test",
                TotalWatches = 2,
                SuccessfulEvaluations = 1,
                FailedEvaluations = 1,
                Watches = new List<WatchEvaluationResult>
                {
                    new() { WatchId = "foo", Expression = "!dumpheap -stat", Type = WatchType.Expression, Success = true, Value = "ok" },
                    new() { WatchId = "bar", Expression = "!clrstack", Type = WatchType.Expression, Success = false, Error = "no sos" }
                },
                Insights = new List<string> { "Watch detected issue" }
            },
            Security = new SecurityInfo
            {
                HasVulnerabilities = true,
                OverallRisk = "high",
                Summary = "Vulnerable package detected",
                Findings = new List<SecurityFinding>
                {
                    new()
                    {
                        Type = "Dependency",
                        Severity = "high",
                        Description = "CVE-1234-5678",
                        Location = "MyApp",
                        Recommendation = "Upgrade"
                    }
                },
                Recommendations = new List<string> { "Upgrade vulnerable dependencies" }
            },
        };
    }

    private static List<TimerInfo> CreateTimers(int count)
    {
        var result = new List<TimerInfo>(capacity: count);
        for (var index = 0; index < count; index++)
        {
            result.Add(new TimerInfo
            {
                Address = $"0x{index + 1:X}",
                DueTimeMs = 1_000,
                PeriodMs = index == 0 ? 50 : 500,
                StateType = index % 2 == 0 ? "MyApp.TimerState" : "System.Threading.TimerQueueTimer"
            });
        }
        return result;
    }
}
