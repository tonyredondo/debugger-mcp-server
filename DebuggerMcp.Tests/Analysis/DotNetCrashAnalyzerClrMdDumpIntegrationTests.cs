using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DebuggerMcp.Analysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// End-to-end integration tests for <see cref="DotNetCrashAnalyzer"/> that use a real process dump + ClrMD.
/// </summary>
/// <remarks>
/// These tests intentionally execute large parts of the analysis pipeline to keep coverage representative of real usage,
/// while using a stub debugger manager to avoid depending on LLDB/WinDbg binaries.
/// </remarks>
[Collection("NonParallelEnvironment")]
public class DotNetCrashAnalyzerClrMdDumpIntegrationTests
{
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

            if (command.StartsWith("memory read", StringComparison.OrdinalIgnoreCase))
            {
                return outputs.TryGetValue("memory read", out var output) ? output : string.Empty;
            }

            return outputs.TryGetValue(command, out var exact) ? exact : string.Empty;
        }

        public void LoadSosExtension() => IsSosLoaded = true;
        public void ConfigureSymbolPath(string symbolPath) { }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AnalyzeDotNetCrashAsync_WithRealDumpAndClrMd_ExercisesDeepExceptionAndAssemblyPaths()
    {
        var ddSymbolsEnabled = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED");
        var githubApiEnabled = Environment.GetEnvironmentVariable("GITHUB_API_ENABLED");

        Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", "false");
        Environment.SetEnvironmentVariable("GITHUB_API_ENABLED", "false");

        try
        {
            var repoRoot = FindRepoRoot();
            var (configuration, tfm) = GetBuildConfigurationAndTfm();

            var dumpTargetDll = Path.Combine(
                repoRoot,
                "DebuggerMcp.DumpTarget",
                "bin",
                configuration,
                tfm,
                "DebuggerMcp.DumpTarget.dll");

            Assert.True(File.Exists(dumpTargetDll), $"Dump target not built: {dumpTargetDll}");

            var tempDir = CreateTempDirectory();
            var dumpPath = Path.Combine(tempDir, $"dump-dotnet-analyzer-{Guid.NewGuid():N}.dmp");

            using var process = StartDumpTarget(dumpTargetDll);

            try
            {
                CreateDumpWithCreatedump(processId: process.Id, dumpPath: dumpPath, tempDir: tempDir);
            }
            finally
            {
                SafeKill(process);
            }

            using var clrMd = new ClrMdAnalyzer(NullLogger.Instance);
            Assert.True(clrMd.OpenDump(dumpPath));

            try
            {
                // Find well-known exception objects created by the dump target.
                var outerExceptionAddress = FindObjectAddressByStringField(
                    clrMd,
                    typeName: "System.InvalidOperationException",
                    fieldName: "_message",
                    expectedContains: "outer:dump-target");

                var fileNotFoundAddress = FindObjectAddressByStringField(
                    clrMd,
                    typeName: "System.IO.FileNotFoundException",
                    fieldName: "_fileName",
                    expectedContains: "missing.dll");

                var missingMethodAddress = FindObjectAddressByStringField(
                    clrMd,
                    typeName: "System.MissingMethodException",
                    fieldName: "_className",
                    expectedContains: "My.Namespace.Type");

                var outOfRangeAddress = FindObjectAddressByStringField(
                    clrMd,
                    typeName: "System.ArgumentOutOfRangeException",
                    fieldName: "_paramName",
                    expectedContains: "param");

                var disposedAddress = FindObjectAddressByStringField(
                    clrMd,
                    typeName: "System.ObjectDisposedException",
                    fieldName: "_objectName",
                    expectedContains: "disposed-object");

                // AggregateException inner exception list might vary; use message matching.
                var aggregateAddress = FindObjectAddressByStringField(
                    clrMd,
                    typeName: "System.AggregateException",
                    fieldName: "_message",
                    expectedContains: "One or more errors occurred");

                // Sanity: ensure object inspection works for at least one located address.
                Assert.Equal("InvalidOperationException", clrMd.InspectObject(outerExceptionAddress)?.Type?.Split('.').Last());

                // Pick two modules so module/attribute enrichment has data.
                var moduleCandidates = clrMd.Runtime!.EnumerateModules().Where(m => !string.IsNullOrEmpty(m.Name)).ToList();
                Assert.NotEmpty(moduleCandidates);

                var dumpTargetModule = moduleCandidates.FirstOrDefault(m => m.Name!.EndsWith("DebuggerMcp.DumpTarget.dll", StringComparison.OrdinalIgnoreCase))
                                       ?? moduleCandidates.First();
                var coreLibModule = moduleCandidates.FirstOrDefault(m => m.Name!.EndsWith("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase))
                                    ?? moduleCandidates.First();

                var argvAddress = "0x0000ffffefcba618";
                var outputs = new Dictionary<string, string>
                {
                    ["thread list"] = "* thread #1: tid = 0x1234, 0x0000000100000000 dotnet`abort, name = 'dotnet', stop reason = signal SIGABRT",
                    ["bt all"] =
                        "* thread #1, name = 'dotnet', stop reason = signal SIGABRT\n" +
                        $"  * frame #0: 0x0000000100001000 SP=0x0000ffffefcb76a0 dotnet`exe_start(argc=2, argv={argvAddress})\n" +
                        $"    frame #1: 0x0000000100002000 SP=0x0000ffffefcb75a0 dotnet`main(argc=2, argv={argvAddress})\n" +
                        "  thread #2: tid = 0x5678\n" +
                        "    frame #0: 0x0000000100003000 SP=0x0000ffffefcb7000 libSystem.Native.dylib + 123\n",
                    ["image list"] =
                        "[  0] ABCDEFAB-0000-0000-0000-000000000000 0x0000000100000000 /usr/lib/dyld\n" +
                        "[  1] 12345678-0000-0000-0000-000000000000 0x0000000200000000 /usr/lib/libc.dylib\n",

                    ["memory read"] =
                        "0xffffefcba618: 0x0000ffffefcbbb24 0x0000ffffefcbbb2b\n" +
                        "0xffffefcba628: 0x0000000000000000 0x0000ffffefcbbb8f\n" +
                        "0xffffefcba638: 0x0000ffffefcbbba0 0x0000000000000000\n",
                    ["expr -- (char*)0x0000ffffefcbbb24"] = "(char *) $1 = 0x0000ffffefcbbb24 \"dotnet\"",
                    ["expr -- (char*)0x0000ffffefcbbb2b"] = "(char *) $2 = 0x0000ffffefcbbb2b \"/app/DebuggerMcp.DumpTarget.dll\"",
                    ["expr -- (char*)0x0000ffffefcbbb8f"] = "(char *) $3 = 0x0000ffffefcbbb8f \"DD_API_KEY=secret-value\"",
                    ["expr -- (char*)0x0000ffffefcbbba0"] = "(char *) $4 = 0x0000ffffefcbbba0 \"NOT_AN_ENV_VAR\"",

                    ["!eeversion"] = "CLR Version: 10.0.0.0",
                    ["!pe -nested"] =
                        $"Exception object: 0x{outerExceptionAddress:x}\n" +
                        "Exception type:   System.InvalidOperationException\n" +
                        "Message:          outer:dump-target\n" +
                        "StackTraceString: <none>\n" +
                        "HResult:          80131509\n" +
                        "\nNested exception -----------------------------------------------------\n" +
                        $"Exception object: 0x{fileNotFoundAddress:x}\n" +
                        "Exception type:   System.IO.FileNotFoundException\n" +
                        "Message:          file missing\n" +
                        "HResult:          80070002\n" +
                        "\nNested exception -----------------------------------------------------\n" +
                        $"Exception object: 0x{missingMethodAddress:x}\n" +
                        "Exception type:   System.MissingMethodException\n" +
                        "Message:          Method not found: 'System.Void My.Namespace.Type.Missing()'\n" +
                        "HResult:          80131513\n" +
                        "\nNested exception -----------------------------------------------------\n" +
                        $"Exception object: 0x{outOfRangeAddress:x}\n" +
                        "Exception type:   System.ArgumentOutOfRangeException\n" +
                        "Message:          out of range\n" +
                        "HResult:          80131502\n" +
                        "\nNested exception -----------------------------------------------------\n" +
                        $"Exception object: 0x{disposedAddress:x}\n" +
                        "Exception type:   System.ObjectDisposedException\n" +
                        "Message:          disposed\n" +
                        "HResult:          80131622\n" +
                        "\nNested exception -----------------------------------------------------\n" +
                        $"Exception object: 0x{aggregateAddress:x}\n" +
                        "Exception type:   System.AggregateException\n" +
                        "Message:          One or more errors occurred.\n" +
                        "HResult:          80131500\n",

                    ["!dumpheap -stat"] =
                        "MT    Count    TotalSize Class Name\n" +
                        "00007ff812345678    1000    50000 DebuggerMcp.DumpTarget.Node\n" +
                        "Total 1000 objects, 50000 bytes\n",
                    ["!clrthreads"] =
                        "ThreadCount:      2\n" +
                        "Total:            2\n" +
                        "Unstarted:        0\n" +
                        "Background:       1\n",
                    ["!finalizequeue"] = "generation 0 has 0 finalizable objects\n",
                    ["!syncblk"] =
                        "Index SyncBlock MonitorHeld Recursion Owning Thread Info  SyncBlock Owner\n" +
                        "  12 0000024453f8a5a8    1         1 0000024453f46720  1c54  10   00000244540a5820 System.Object\n",
                    ["!threadpool"] =
                        "Portable thread pool\n" +
                        "CPU utilization:  95%\n" +
                        "Workers Total:    2\n" +
                        "Workers Running:  2\n" +
                        "Workers Idle:     0\n" +
                        "Worker Min Limit: 2\n" +
                        "Worker Max Limit: 32767\n",
                    ["!ti"] =
                        "(L) 0x0000000000000001 @    3999 ms every     4000 ms |  0000000000000002 (System.Threading.Timer) -> DebuggerMcp.DumpTarget.Program\n",
                    ["!analyzeoom"] = "There was no managed OOM\n",
                    ["!crashinfo"] = "Signal: 11 (SIGSEGV)\n",
                    ["!dumpdomain"] =
                        $"Assembly: 0000000000000001 [{Path.GetFileNameWithoutExtension(coreLibModule.Name!)}]\n" +
                        $"Module: 0x{coreLibModule.Address:x} {coreLibModule.Name}\n" +
                        $"Assembly: 0000000000000002 [{Path.GetFileNameWithoutExtension(dumpTargetModule.Name!)}]\n" +
                        $"Module: 0x{dumpTargetModule.Address:x} {dumpTargetModule.Name}\n",
                };

                var manager = new StubDebuggerManager("LLDB", outputs);
                var analyzer = new DotNetCrashAnalyzer(manager, sourceLinkResolver: null, clrMdAnalyzer: clrMd, logger: NullLogger.Instance);

                var result = await analyzer.AnalyzeDotNetCrashAsync();

                Assert.NotNull(result.Exception);
                Assert.NotNull(result.Exception!.Analysis);
                Assert.NotNull(result.Exception.Analysis!.ExceptionChain);
                Assert.True(result.Exception.Analysis.ExceptionChain!.Count >= 3);

                Assert.NotNull(result.Assemblies);
                Assert.True(result.Assemblies!.Count >= 2);
                var dumpTargetAssembly = result.Assemblies.Items!.FirstOrDefault(a => a.Name.Equals("DebuggerMcp.DumpTarget", StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(dumpTargetAssembly);
                Assert.False(string.IsNullOrWhiteSpace(dumpTargetAssembly!.CommitHash));
                Assert.True(dumpTargetAssembly.CommitHash!.Length >= 7);
                Assert.All(dumpTargetAssembly.CommitHash, c => Assert.True(char.IsAsciiHexDigit(c)));
                Assert.Equal("https://github.com/tonyredondo/debugger-mcp-server", dumpTargetAssembly.RepositoryUrl);

                // When ClrMD is available, the analyzer should take the fast-path and skip SOS clrstack.
                Assert.DoesNotContain(manager.ExecutedCommands, c => c.Contains("clrstack -a -r -all", StringComparison.OrdinalIgnoreCase));

                CrashAnalysisResultContract.AssertValid(result);

                var json = CrashAnalyzer.ToJson(result);
                var roundTrip = JsonSerializer.Deserialize<CrashAnalysisResult>(json);
                Assert.NotNull(roundTrip);
                CrashAnalysisResultContract.AssertValid(roundTrip!);
            }
            finally
            {
                clrMd.CloseDump();
                SafeDeleteFile(dumpPath);
                SafeDeleteDirectory(tempDir);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", ddSymbolsEnabled);
            Environment.SetEnvironmentVariable("GITHUB_API_ENABLED", githubApiEnabled);
        }
    }

    private static Process StartDumpTarget(string dumpTargetDll)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dumpTargetDll}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = Process.Start(startInfo);
        Assert.NotNull(process);

        var readyTask = process!.StandardOutput.ReadLineAsync();
        var completed = Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromSeconds(15))).GetAwaiter().GetResult();
        Assert.Same(readyTask, completed);

        var readyLine = readyTask.GetAwaiter().GetResult() ?? string.Empty;
        Assert.StartsWith("READY ", readyLine, StringComparison.Ordinal);

        return process;
    }

    private static ulong FindObjectAddressByStringField(
        ClrMdAnalyzer analyzer,
        string typeName,
        string fieldName,
        string expectedContains)
    {
        var runtime = analyzer.Runtime;
        Assert.NotNull(runtime);

        var heap = runtime!.Heap;
        foreach (var obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.IsFree)
                continue;

            if (!string.Equals(obj.Type?.Name, typeName, StringComparison.Ordinal))
                continue;

            var inspected = analyzer.InspectObject(obj.Address, maxDepth: 1, maxArrayElements: 0, maxStringLength: 2048);
            var fields = inspected?.Fields;
            if (fields == null || fields.Count == 0)
                continue;

            var matchField = fields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase));
            var direct = matchField?.Value?.ToString();
            if (direct != null && direct.Contains(expectedContains, StringComparison.Ordinal))
            {
                return obj.Address;
            }

            // Fallback: when field names differ across runtimes, look for the marker anywhere in string fields.
            if (fields.Any(f => f.Value is string s && s.Contains(expectedContains, StringComparison.Ordinal)))
            {
                return obj.Address;
            }
        }

        throw new InvalidOperationException($"Could not find {typeName} with {fieldName} containing '{expectedContains}'.");
    }

    private static void SafeKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", nameof(DotNetCrashAnalyzerClrMdDumpIntegrationTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DebuggerMcp.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root (DebuggerMcp.slnx not found).");
    }

    private static (string configuration, string tfm) GetBuildConfigurationAndTfm()
    {
        var testAssemblyDir = new FileInfo(typeof(DotNetCrashAnalyzerClrMdDumpIntegrationTests).Assembly.Location).Directory;
        var tfm = testAssemblyDir?.Name ?? "net10.0";
        var config = testAssemblyDir?.Parent?.Name ?? "Debug";
        return (config, tfm);
    }

    private static void CreateDumpWithCreatedump(int processId, string dumpPath, string tempDir)
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var createdumpSource = Path.Combine(runtimeDir, "createdump");
        Assert.True(File.Exists(createdumpSource), $"createdump not found at: {createdumpSource}");

        var createdumpCopy = Path.Combine(tempDir, "createdump");
        File.Copy(createdumpSource, createdumpCopy, overwrite: true);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                createdumpCopy,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = createdumpCopy,
            Arguments = $"--withheap --name \"{dumpPath}\" {processId}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var createdump = Process.Start(startInfo);
        Assert.NotNull(createdump);

        var stdout = createdump!.StandardOutput.ReadToEnd();
        var stderr = createdump.StandardError.ReadToEnd();

        if (!createdump.WaitForExit(60000))
        {
            try { createdump.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("createdump did not exit within 60s.");
        }

        Assert.True(createdump.ExitCode == 0, $"createdump failed (exit {createdump.ExitCode}).\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.True(File.Exists(dumpPath), $"Dump file was not created: {dumpPath}");
    }
}
