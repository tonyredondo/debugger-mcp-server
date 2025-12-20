using System.Reflection;
using DebuggerMcp;
using DebuggerMcp.Analysis;
using DebuggerMcp.SourceLink;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Coverage-focused tests for private implementation details in <see cref="DotNetCrashAnalyzer"/>.
/// </summary>
public class DotNetCrashAnalyzerPrivateCoverageTests
{
    private sealed class NoOpDebuggerManager : IDebuggerManager
    {
        public bool IsInitialized => true;
        public bool IsDumpOpen => true;
        public string? CurrentDumpPath => null;
        public string DebuggerType => "LLDB";
        public bool IsSosLoaded => false;
        public bool IsDotNetDump => true;

        public Task InitializeAsync() => Task.CompletedTask;
        public void OpenDumpFile(string dumpFilePath, string? executablePath = null) { }
        public void CloseDump() { }
        public string ExecuteCommand(string command) => string.Empty;
        public void LoadSosExtension() { }
        public void ConfigureSymbolPath(string symbolPath) { }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLldbManager : LldbManager
    {
        private readonly Dictionary<string, string> _outputs;
        private int _currentFrameIndex;

        public FakeLldbManager(Dictionary<string, string> outputs)
            : base(NullLogger<LldbManager>.Instance)
        {
            _outputs = outputs;
        }

        public override string ExecuteCommand(string command)
        {
            if (command.StartsWith("frame select ", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(command["frame select ".Length..], out var idx))
                {
                    _currentFrameIndex = idx;
                }
            }

            if (command.Equals("frame info", StringComparison.OrdinalIgnoreCase))
            {
                return _outputs.TryGetValue($"frame info {_currentFrameIndex}", out var frameInfo) ? frameInfo : string.Empty;
            }

            if (command.Equals("register read", StringComparison.OrdinalIgnoreCase))
            {
                return _outputs.TryGetValue($"register read {_currentFrameIndex}", out var regs) ? regs : string.Empty;
            }

            return _outputs.TryGetValue(command, out var output) ? output : string.Empty;
        }
    }

    [Fact]
    public void ApplyRegistersToFaultingThread_WhenFaultingThreadNotSet_FindsThreadAndAppliesRegisters()
    {
        var analyzer = new DotNetCrashAnalyzer(new NoOpDebuggerManager());

        var result = new CrashAnalysisResult
        {
            Threads = new ThreadsInfo
            {
                // Force the code-path that searches in All threads (FaultingThread is null).
                All = new List<ThreadInfo>
                {
                    new()
                    {
                        ThreadId = "tid: 1234",
                        CallStack = new List<StackFrame>
                        {
                            new() { FrameNumber = 0, StackPointer = "0x1111" },
                            new() { FrameNumber = 1, StackPointer = "0x2222" }
                        }
                    }
                }
            }
        };

        var registers = new Dictionary<(uint ThreadId, ulong SP), Dictionary<string, string>>
        {
            [(1234u, 0x1111ul)] = new Dictionary<string, string> { ["x0"] = "0x1" }
        };

        InvokePrivateInstance(analyzer, "ApplyRegistersToFaultingThread", result, registers, 1234u);

        var frame0 = Assert.Single(result.Threads!.All!).CallStack![0];
        Assert.NotNull(frame0.Registers);
        Assert.Equal("0x1", frame0.Registers!["x0"]);

        var frame1 = result.Threads.All[0].CallStack[1];
        Assert.Null(frame1.Registers);
    }

    [Theory]
    [InlineData("0x0000007B", "System.Int32", "123")]
    [InlineData("0x00000001", "System.Boolean", "true")]
    [InlineData("0x00000000", "System.Boolean", "false")]
    [InlineData("0x00000041", "System.Char", "'A'")]
    [InlineData("0x0000001F", "System.Char", "'\\u001F'")]
    [InlineData("0x3F800000", "System.Single", "1")]
    [InlineData("0x3FF0000000000000", "System.Double", "1")]
    public void ConvertHexToValue_ConvertsKnownTypes(string hexValue, string typeName, string expected)
    {
        var actual = (string)InvokePrivateStatic(typeof(DotNetCrashAnalyzer), "ConvertHexToValue", hexValue, typeName);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("1234", "System.Int32", "1234")]
    [InlineData("0xZZ", "System.Int32", "0xZZ")]
    [InlineData("0x1234", null, "0x1234")]
    public void ConvertHexToValue_WhenNotConvertible_ReturnsOriginal(string hexValue, string? typeName, string expected)
    {
        var actual = (string)InvokePrivateStatic(typeof(DotNetCrashAnalyzer), "ConvertHexToValue", hexValue, typeName);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("System.Int32", true)]
    [InlineData("int", true)]
    [InlineData("System.Guid", true)]
    [InlineData("System.String", false)]
    [InlineData(null, false)]
    public void IsValueType_RecognizesCommonValueTypes(string? typeName, bool expected)
    {
        var actual = (bool)InvokePrivateStatic(typeof(DotNetCrashAnalyzer), "IsValueType", typeName);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CollectStringAddresses_FindsDirectAndByRefStringAddresses()
    {
        var vars = new List<LocalVariable>
        {
            new()
            {
                Name = "s",
                Type = "System.String",
                HasData = true,
                Value = "0x1234"
            },
            new()
            {
                Name = "byRef",
                Type = "System.String(ByRef)",
                HasData = true,
                Value = "ignored",
                ResolvedAddress = "0xDEAD"
            },
            new()
            {
                Name = "notString",
                Type = "System.Int32",
                HasData = true,
                Value = "123"
            }
        };

        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        InvokePrivateStatic(typeof(DotNetCrashAnalyzer), "CollectStringAddresses", vars, addresses);

        Assert.Contains("0x1234", addresses);
        Assert.Contains("0xDEAD", addresses);
        Assert.DoesNotContain("123", addresses);
    }

    [Fact]
    public void ApplyStringValues_RewritesDirectAndByRefValues()
    {
        var vars = new List<LocalVariable>
        {
            new()
            {
                Name = "s",
                Type = "System.String",
                HasData = true,
                Value = "0x1234"
            },
            new()
            {
                Name = "byRef",
                Type = "System.String(ByRef)",
                HasData = true,
                Value = "ignored",
                ResolvedAddress = "0xDEAD"
            }
        };

        var values = new Dictionary<string, string>
        {
            ["0x1234"] = "hello",
            ["0xDEAD"] = "world"
        };

        InvokePrivateStatic(typeof(DotNetCrashAnalyzer), "ApplyStringValues", vars, values);

        Assert.Equal("0x1234", vars[0].RawValue);
        Assert.Equal("hello", vars[0].Value);
        Assert.Equal("world", vars[1].Value);
    }

    [Theory]
    [InlineData("TypeLoadException")]
    [InlineData("MissingFieldException")]
    [InlineData("MissingMemberException")]
    [InlineData("EntryPointNotFoundException")]
    [InlineData("TypeInitializationException")]
    [InlineData("FileNotFoundException")]
    [InlineData("BadImageFormatException")]
    [InlineData("PlatformNotSupportedException")]
    public void GenerateTrimmingRecommendation_CoversExceptionSpecificBranches(string exceptionType)
    {
        var rec = DotNetCrashAnalyzer.GenerateTrimmingRecommendation("MyType.MyMember", exceptionType);

        // Always includes general advice.
        Assert.Contains("trimming warnings", rec, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("•", rec, StringComparison.Ordinal);
    }

    [Fact]
    public void FetchAllFrameRegistersForThread_CombinesBtFramesAndFrameInfoSecondPass()
    {
        var analyzer = new DotNetCrashAnalyzer(new NoOpDebuggerManager());

        var outputs = new Dictionary<string, string>
        {
            ["thread list"] =
                "* thread #1: tid = 1234, 0x0000, name = 'dotnet', stop reason = signal SIGSTOP\n" +
                "  thread #2: tid = 0x4d3, 0x0000\n",
            ["thread select 1"] = "selected",
            ["bt 200"] =
                "* frame #0: 0x0000 SP=0x1111 dotnet`foo\n" +
                "  frame #2: 0x0000 SP=0x2222 dotnet`bar\n",
            ["frame select 0"] = "selected",
            ["frame select 2"] = "selected",
            ["frame select 1"] = "selected",
            ["frame select 3"] = "error: invalid frame",
            ["frame info 1"] = "frame #1: 0x0000 SP=0x3333 dotnet`jitframe\n",
            ["register read 0"] = "x0 = 0x0000000000000001\n",
            ["register read 2"] = "x1 = 0x0000000000000002\n",
            ["register read 1"] = "x2 = 0x0000000000000003\n",
        };

        var fakeLldb = new FakeLldbManager(outputs);

        var registers = (Dictionary<(uint ThreadId, ulong SP), Dictionary<string, string>>)InvokePrivateInstance(
            analyzer,
            "FetchAllFrameRegistersForThread",
            fakeLldb,
            1234u);

        Assert.True(registers.ContainsKey((1234u, 0x1111ul)));
        Assert.True(registers.ContainsKey((1234u, 0x2222ul)));
        Assert.True(registers.ContainsKey((1234u, 0x3333ul)));
    }

    [Fact]
    public void MergeManagedFramesIntoCallStack_WhenStackPointersMatch_EnrichesNativeFrameAndInsertsOrphans()
    {
        var analyzer = new DotNetCrashAnalyzer(new NoOpDebuggerManager());

        var thread = new ThreadInfo
        {
            ThreadId = "tid: 1",
            CallStack = new List<StackFrame>
            {
                new() { FrameNumber = 0, StackPointer = "0x3000", Module = "native", Function = "native0" },
                new() { FrameNumber = 1, StackPointer = "0x1000", Module = "native", Function = "native1" },
            }
        };

        var managedFramesBySp = new Dictionary<ulong, StackFrame>
        {
            [0x3000] = new StackFrame
            {
                FrameNumber = 0,
                StackPointer = "0x3000",
                Module = "MyApp",
                Function = "MyApp.Program.Main()",
                IsManaged = true,
                Registers = new Dictionary<string, string> { ["x0"] = "0x1" },
                Parameters = new List<LocalVariable> { new() { Name = "a", Type = "System.Int32", Value = "1", HasData = true } },
                Locals = new List<LocalVariable> { new() { Name = "b", Type = "System.String", Value = "0x1234", HasData = true } },
            },
            [0x2000] = new StackFrame
            {
                FrameNumber = 99,
                StackPointer = "0x2000",
                Module = "MyApp",
                Function = "MyApp.Program.Orphan()",
                IsManaged = true
            }
        };

        InvokePrivateInstance(analyzer, "MergeManagedFramesIntoCallStack", thread, managedFramesBySp);

        Assert.Equal(3, thread.CallStack!.Count);
        Assert.Equal("MyApp.Program.Main()", thread.CallStack[0].Function);
        Assert.True(thread.CallStack[0].IsManaged);
        Assert.NotNull(thread.CallStack[0].Registers);
        Assert.Equal("0x1", thread.CallStack[0].Registers!["x0"]);

        // Orphan frame should be inserted between SP=0x3000 and SP=0x1000.
        Assert.Equal("MyApp.Program.Orphan()", thread.CallStack[1].Function);

        Assert.Equal(0, thread.CallStack[0].FrameNumber);
        Assert.Equal(1, thread.CallStack[1].FrameNumber);
        Assert.Equal(2, thread.CallStack[2].FrameNumber);
    }

    [Fact]
    public void ParseManagedFrame_ParsesFullAndSimpleFormats_AndSkipsNativeMarkers()
    {
        var analyzer = new DotNetCrashAnalyzer(new NoOpDebuggerManager());

        var fullLine = "000000000012f000 00007ff812345678 MyNamespace.MyType.MyMethod(System.Int32) [/tmp/file.cs @ 42]";
        var frameNumber = 0;
        var parsedFull = InvokePrivateInstanceWithRefIntReturn<StackFrame?>(analyzer, "ParseManagedFrame", fullLine, ref frameNumber);

        Assert.NotNull(parsedFull);
        Assert.Equal(0, parsedFull!.FrameNumber);
        Assert.Equal("0x00007ff812345678", parsedFull.InstructionPointer);
        Assert.Equal("MyNamespace", parsedFull.Module);
        Assert.Equal("MyNamespace.MyType.MyMethod(System.Int32)", parsedFull.Function);
        Assert.Equal("file.cs", Path.GetFileName(parsedFull.SourceFile));
        Assert.Equal(42, parsedFull.LineNumber);
        Assert.True(parsedFull.IsManaged);
        Assert.Equal(1, frameNumber);

        var simpleLine = "MyNamespace.MyType.MyOtherMethod()";
        var parsedSimple = InvokePrivateInstanceWithRefIntReturn<StackFrame?>(analyzer, "ParseManagedFrame", simpleLine, ref frameNumber);

        Assert.NotNull(parsedSimple);
        Assert.Equal(1, parsedSimple!.FrameNumber);
        Assert.Equal("0x0", parsedSimple.InstructionPointer);
        Assert.True(parsedSimple.IsManaged);
        Assert.Equal(2, frameNumber);

        var nativeMarkerLine = "000000000012f000 00007ff812345678 [NativeCall]";
        var parsedNative = InvokePrivateInstanceWithRefIntReturn<StackFrame?>(analyzer, "ParseManagedFrame", nativeMarkerLine, ref frameNumber);
        Assert.Null(parsedNative);
    }

    [Fact]
    public void ParseNativeFrameWithDebugInfo_ExtractsModuleFunctionAndSource()
    {
        var callSite = "libcoreclr.so!PROCCreateCrashDump(int) + 636 at /path/to/file.cpp:123:45 [opt]";

        var tuple = (ValueTuple<string, string, string?, int?>)InvokePrivateStatic(
            typeof(DotNetCrashAnalyzer),
            "ParseNativeFrameWithDebugInfo",
            callSite);

        Assert.Equal("libcoreclr.so", tuple.Item1);
        Assert.Equal("PROCCreateCrashDump(int)", tuple.Item2);
        Assert.Equal("file.cpp", tuple.Item3);
        Assert.Equal(123, tuple.Item4);
    }

    [Fact]
    public void ParseRegisterLine_CollectsRegistersOnFrame()
    {
        var frame = new StackFrame();

        InvokePrivateStatic(typeof(DotNetCrashAnalyzer), "ParseRegisterLine", "    x0 = 0x1 rax=123 x1=0x2", frame);

        Assert.NotNull(frame.Registers);
        Assert.Equal("0x1", frame.Registers!["x0"]);
        Assert.Equal("123", frame.Registers["rax"]);
        Assert.Equal("0x2", frame.Registers["x1"]);
    }

    [Fact]
    public void EstimateSpFromFrameNumber_CoversAllInterpolationBranches()
    {
        // Normal case: above.sp > below.sp
        var above = new StackFrame { FrameNumber = 0 };
        var below = new StackFrame { FrameNumber = 2 };
        var framesWithSp = new List<(StackFrame frame, ulong sp)>
        {
            (above, 0x3000),
            (below, 0x1000),
        };

        var interpolated = (ulong)InvokePrivateStatic(
            typeof(DotNetCrashAnalyzer),
            "EstimateSpFromFrameNumber",
            1,
            framesWithSp,
            0x1000ul,
            0x3000ul);
        Assert.InRange(interpolated, 0x1000ul, 0x3000ul);

        // Swap logic: above.sp < below.sp (unusual)
        framesWithSp = new List<(StackFrame frame, ulong sp)>
        {
            (above, 0x1000),
            (below, 0x3000),
        };
        var swapped = (ulong)InvokePrivateStatic(
            typeof(DotNetCrashAnalyzer),
            "EstimateSpFromFrameNumber",
            1,
            framesWithSp,
            0x1000ul,
            0x3000ul);
        Assert.InRange(swapped, 0x1000ul, 0x3000ul);

        // Only above
        framesWithSp = new List<(StackFrame frame, ulong sp)> { (above, 0x2000) };
        var onlyAbove = (ulong)InvokePrivateStatic(
            typeof(DotNetCrashAnalyzer),
            "EstimateSpFromFrameNumber",
            2,
            framesWithSp,
            0x0ul,
            0x0ul);
        Assert.True(onlyAbove <= 0x2000ul);

        // Only below
        framesWithSp = new List<(StackFrame frame, ulong sp)> { (new StackFrame { FrameNumber = 5 }, 0x1000) };
        var onlyBelow = (ulong)InvokePrivateStatic(
            typeof(DotNetCrashAnalyzer),
            "EstimateSpFromFrameNumber",
            3,
            framesWithSp,
            0x0ul,
            0x0ul);
        Assert.True(onlyBelow >= 0x1000ul);

        // None
        framesWithSp = new List<(StackFrame frame, ulong sp)>();
        var midpoint = (ulong)InvokePrivateStatic(
            typeof(DotNetCrashAnalyzer),
            "EstimateSpFromFrameNumber",
            3,
            framesWithSp,
            0x1000ul,
            0x3000ul);
        Assert.Equal(0x2000ul, midpoint);
    }

    [Theory]
    [InlineData("System.Int32", true)]
    [InlineData("int", true)]
    [InlineData("System.DateTime", true)]
    [InlineData("System.String", false)]
    [InlineData("System.Int32[]", false)]
    [InlineData("MyNamespace.MyType", false)]
    [InlineData(null, false)]
    public void IsValueTypeByRef_RecognizesPrimitivesAndKnownStructs(string? typeName, bool expected)
    {
        var actual = (bool)InvokePrivateStatic(typeof(DotNetCrashAnalyzer), "IsValueTypeByRef", typeName);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EnrichDatadogAssembliesWithSymbolInfo_UpdatesMatchingAssemblies()
    {
        var result = new CrashAnalysisResult
        {
            Assemblies = new AssembliesInfo
            {
                Items = new List<AssemblyVersionInfo>
                {
                    new() { Name = "Datadog.Trace" },
                    new() { Name = "Other.Assembly" }
                }
            }
        };

        var prep = new DatadogSymbolPreparationResult
        {
            DatadogAssemblies = new List<DatadogAssemblyInfo>
            {
                new() { Name = "Datadog.Trace" }
            },
            DownloadResult = new DatadogSymbolDownloadResult
            {
                BuildId = 123,
                BuildNumber = "2025.01.01.1",
                BuildUrl = "https://example.invalid/build/123",
                MergeResult = new ArtifactMergeResult { SymbolDirectory = "/tmp/symbols" }
            },
            LoadResult = new SymbolLoadResult { Success = true }
        };

        InvokePrivateStatic(typeof(DotNetCrashAnalyzer), "EnrichDatadogAssembliesWithSymbolInfo", result, prep);

        var datadog = result.Assemblies.Items![0];
        Assert.Equal(123, datadog.AzurePipelinesBuildId);
        Assert.Equal("2025.01.01.1", datadog.AzurePipelinesBuildNumber);
        Assert.Equal("https://example.invalid/build/123", datadog.AzurePipelinesBuildUrl);
        Assert.True(datadog.SymbolsDownloaded);
        Assert.Equal("/tmp/symbols", datadog.SymbolsDirectory);

        var other = result.Assemblies.Items![1];
        Assert.Null(other.AzurePipelinesBuildId);
        Assert.Null(other.SymbolsDownloaded);
    }

    [Fact]
    public void MergeNativeAndManagedFramesBySP_MergesAndRenumbersFrames()
    {
        // Use a non-null logger to exercise logging branches.
        var analyzer = new DotNetCrashAnalyzer(
            new NoOpDebuggerManager(),
            sourceLinkResolver: null,
            clrMdAnalyzer: null,
            logger: NullLogger.Instance);

        var threadFallback = new ThreadInfo
        {
            ThreadId = "t1",
            CallStack = new List<StackFrame>
            {
                new() { FrameNumber = 1, StackPointer = "0x0", IsManaged = false, Function = "native" },
                new() { FrameNumber = 0, StackPointer = null, IsManaged = true, Function = "managed" },
            }
        };

        var threadBySp = new ThreadInfo
        {
            ThreadId = "t2",
            CallStack = new List<StackFrame>
            {
                new() { FrameNumber = 0, StackPointer = "0x3000", IsManaged = false, Function = "native0" },
                new() { FrameNumber = 1, StackPointer = "0x1000", IsManaged = true, Function = "managed1" },
                new() { FrameNumber = 2, StackPointer = "", IsManaged = false, Function = "native2" }, // missing SP
                new() { FrameNumber = 3, StackPointer = "0x2000", IsManaged = true, Function = "managed3" },
            }
        };

        var result = new CrashAnalysisResult
        {
            Threads = new ThreadsInfo
            {
                All = new List<ThreadInfo> { threadFallback, threadBySp }
            }
        };

        InvokePrivateInstance(analyzer, "MergeNativeAndManagedFramesBySP", result);

        // Fallback thread should be renumbered by frame number ordering.
        Assert.Equal(2, threadFallback.CallStack!.Count);
        Assert.Equal(0, threadFallback.CallStack[0].FrameNumber);
        Assert.Equal(1, threadFallback.CallStack[1].FrameNumber);

        // SP-merge thread should be sorted by effective SP and renumbered.
        Assert.Equal(4, threadBySp.CallStack!.Count);
        Assert.Equal(0, threadBySp.CallStack[0].FrameNumber);
        Assert.Equal(1, threadBySp.CallStack[1].FrameNumber);
        Assert.Equal(2, threadBySp.CallStack[2].FrameNumber);
        Assert.Equal(3, threadBySp.CallStack[3].FrameNumber);

        Assert.Contains(threadBySp.CallStack, f => f.Function == "managed1");
        Assert.Contains(threadBySp.CallStack, f => f.Function == "managed3");
        Assert.Contains(threadBySp.CallStack, f => f.Function == "native2");
    }

    [Theory]
    [InlineData("libstdc++.so.6", "deadbeef", "libstdc++.so.6", "[Native Code @ 0xdeadbeef]")]
    [InlineData("ld-linux-aarch64.so.1 + -1", null, "ld-linux-aarch64.so.1", "[ld-linux-aarch64.so.1]")]
    public void ParseNativeLibraryOnly_ParsesLibraryNameAndFallbackFunction(string callSite, string? ip, string expectedModule, string expectedFunction)
    {
        var tuple = (ValueTuple<string, string>)InvokePrivateStatic(typeof(DotNetCrashAnalyzer), "ParseNativeLibraryOnly", callSite, ip);
        Assert.Equal(expectedModule, tuple.Item1);
        Assert.Equal(expectedFunction, tuple.Item2);
    }

    [Fact]
    public void GetByRefAddress_PrefersLocationWhenHex()
    {
        var variable = new LocalVariable
        {
            Type = "System.String(ByRef)",
            Location = "0x0000000000000010",
            Value = "0x0000000000000020",
            HasData = true
        };

        var address = InvokePrivateStatic(typeof(DotNetCrashAnalyzer), "GetByRefAddress", variable) as string;
        Assert.Equal("0x0000000000000010", address);
    }

    [Theory]
    [InlineData("System.String(ByRef)", "System.String")]
    [InlineData("System.Int32", "System.Int32")]
    [InlineData(null, "")]
    public void GetBaseTypeFromByRef_StripsModifier(string? input, string expected)
    {
        var actual = InvokePrivateStatic(typeof(DotNetCrashAnalyzer), "GetBaseTypeFromByRef", input) as string;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ExtractStringFromDumpObj_ParsesStringLineAndTruncates()
    {
        var value = new string('a', 2000);
        var output = $"Type: System.String\\nString: {value}\\n";

        var parsed = InvokePrivateStatic(typeof(DotNetCrashAnalyzer), "ExtractStringFromDumpObj", output) as string;
        Assert.NotNull(parsed);
        Assert.StartsWith(new string('a', 10), parsed!, StringComparison.Ordinal);
        Assert.EndsWith("...", parsed, StringComparison.Ordinal);
    }

    private static object InvokePrivateInstance(object instance, string methodName, params object?[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        return method!.Invoke(instance, args)!;
    }

    private static T InvokePrivateInstanceWithRefIntReturn<T>(object instance, string methodName, string line, ref int frameNumber)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        object?[] args = { line, frameNumber };
        var result = method!.Invoke(instance, args);
        frameNumber = (int)args[1]!;
        return (T)result!;
    }

    private static object InvokePrivateStatic(Type type, string methodName, params object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        return method!.Invoke(null, args)!;
    }
}
