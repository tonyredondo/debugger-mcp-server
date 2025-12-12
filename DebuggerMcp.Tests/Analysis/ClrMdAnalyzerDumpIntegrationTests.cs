using System.Diagnostics;
using System.Runtime.InteropServices;
using DebuggerMcp.Analysis;
using DebuggerMcp.Analysis.Synchronization;
using DebuggerMcp.SourceLink;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// End-to-end integration tests for <see cref="ClrMdAnalyzer"/> using a real process dump.
/// </summary>
/// <remarks>
/// These tests intentionally exercise large sections of the ClrMD-based analysis pipeline (heap walks, thread stacks,
/// and synchronization analysis) to keep coverage representative of real usage.
/// </remarks>
[Collection("NonParallelEnvironment")]
public class ClrMdAnalyzerDumpIntegrationTests
{
    [Fact]
    public void ClrMdAnalyzer_WithRealDump_CanRunCoreAnalyses()
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
        var dumpPath = Path.Combine(tempDir, $"dump-target-{Guid.NewGuid():N}.dmp");

        using var process = StartDumpTarget(dumpTargetDll);

        try
        {
            CreateDumpWithCreatedump(processId: process.Id, dumpPath: dumpPath, tempDir: tempDir);
        }
        finally
        {
            SafeKill(process);
        }

        using var analyzer = new ClrMdAnalyzer(NullLogger.Instance);
        Assert.True(analyzer.OpenDump(dumpPath));

        try
        {
            Assert.True(analyzer.IsOpen);
            Assert.NotNull(analyzer.Runtime);

            // Modules + platform detection.
            var modules = analyzer.ListModules();
            Assert.NotEmpty(modules);
            _ = analyzer.GetNativeModulePaths();
            _ = analyzer.DetectIsAlpine();
            _ = analyzer.DetectArchitecture();
            _ = analyzer.GetModulePdbGuids(new[] { "System.Private.CoreLib" });

            // Metadata extraction (best-effort, should not throw).
            _ = analyzer.GetAllModulesWithAttributes();
            _ = analyzer.GetAssemblyAttributes("System.Private.CoreLib");

            // Heap analysis paths.
            var gc = analyzer.GetGcSummary();
            Assert.NotNull(gc);

            _ = analyzer.GetTopMemoryConsumers(topN: 5, timeoutMs: 10000);
            _ = analyzer.GetStringAnalysis(topN: 5, maxStringLength: 100, timeoutMs: 10000);
            _ = analyzer.GetAsyncAnalysis(timeoutMs: 10000);
            _ = analyzer.GetCombinedHeapAnalysis(topN: 5, maxStringLength: 100, timeoutMs: 10000);

            // Thread stacks.
            var stacks = analyzer.GetAllThreadStacks(includeArguments: false, includeLocals: false);
            Assert.NotNull(stacks);

            // Exercise argument/local extraction and sequence-point lookup paths.
            var sequencePointResolver = new SequencePointResolver(NullLogger.Instance);
            sequencePointResolver.AddPdbSearchPath(repoRoot);
            analyzer.SetSequencePointResolver(sequencePointResolver);

            var stacksWithArgsAndLocals = analyzer.GetAllThreadStacks(includeArguments: true, includeLocals: true);
            Assert.NotNull(stacksWithArgsAndLocals);

            // Type resolution.
            _ = analyzer.Name2EE("System.String");
            _ = analyzer.Name2EE("System.String", moduleName: "System.Private.CoreLib", searchHeapForGenerics: false);
            // Trigger the generic heap-search path by asking for a constructed generic type.
            _ = analyzer.Name2EE("System.Collections.Generic.Dictionary<System.String, System.Object>");
            // Force the generic heap-search scan by using a non-existent generic type name.
            _ = analyzer.Name2EE("System.Collections.Generic.NonExistent`1<System.String>", searchHeapForGenerics: true);
            _ = analyzer.Name2EEMethod("System.String", "ToString");
            _ = analyzer.Name2EEMethod("System.String", "DefinitelyNotAMethod");
            _ = analyzer.GetEnhancedThreadInfo();
            _ = analyzer.InspectModuleByName("System.Private.CoreLib");
            _ = analyzer.InspectModuleByName("Definitely.Not.A.Module");
            _ = analyzer.GetAssemblyAttributes("Definitely.Not.A.Module");

            // Object inspection: inspect a string and an array to exercise multiple paths.
            var heap = analyzer.Runtime!.Heap;
            ulong? anyObject = null;
            ulong? anyString = null;
            ulong? anyArray = null;
            foreach (var obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.IsFree)
                    continue;

                anyObject ??= obj.Address;
                if (anyString == null && obj.Type?.Name == "System.String")
                    anyString = obj.Address;
                if (anyArray == null && obj.Type?.IsArray == true)
                    anyArray = obj.Address;

                if (anyObject != null && anyString != null && anyArray != null)
                    break;
            }

            Assert.NotNull(anyObject);
            var inspected = analyzer.InspectObject(anyObject!.Value, maxDepth: 1, maxArrayElements: 3, maxStringLength: 64);
            Assert.NotNull(inspected);

            if (anyString != null)
            {
                var stringInspected = analyzer.InspectObject(anyString.Value, maxDepth: 1, maxArrayElements: 0, maxStringLength: 64);
                Assert.NotNull(stringInspected);
            }

            if (anyArray != null)
            {
                var arrayInspected = analyzer.InspectObject(anyArray.Value, maxDepth: 1, maxArrayElements: 3, maxStringLength: 16);
                Assert.NotNull(arrayInspected);
            }

            // Synchronization analysis (ClrMD-based).
            var sync = new SynchronizationAnalyzer(analyzer.Runtime!, NullLogger.Instance, skipSyncBlocks: false);
            var syncResult = sync.Analyze();
            Assert.NotNull(syncResult);

            // Also exercise the skipSyncBlocks path, which is used for cross-arch/emulated analysis scenarios.
            var syncSkipped = new SynchronizationAnalyzer(analyzer.Runtime!, NullLogger.Instance, skipSyncBlocks: true);
            var syncSkippedResult = syncSkipped.Analyze();
            Assert.NotNull(syncSkippedResult);
        }
        finally
        {
            analyzer.CloseDump();
            SafeDeleteFile(dumpPath);
            SafeDeleteDirectory(tempDir);
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

        // Wait for READY line with a short timeout.
        var readyTask = process!.StandardOutput.ReadLineAsync();
        var completed = Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromSeconds(15))).GetAwaiter().GetResult();
        Assert.Same(readyTask, completed);

        var ready = readyTask.GetAwaiter().GetResult();
        Assert.NotNull(ready);
        Assert.StartsWith("READY ", ready, StringComparison.Ordinal);

        return process;
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
        var dir = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", nameof(ClrMdAnalyzerDumpIntegrationTests), Guid.NewGuid().ToString("N"));
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
        // Use the test assembly output directory, which is reliably: .../bin/{Config}/{TFM}/
        var testAssemblyDir = new FileInfo(typeof(ClrMdAnalyzerDumpIntegrationTests).Assembly.Location).Directory;
        var tfm = testAssemblyDir?.Name ?? "net10.0";
        var config = testAssemblyDir?.Parent?.Name ?? "Debug";
        return (config, tfm);
    }

    private static void CreateDumpWithCreatedump(int processId, string dumpPath, string tempDir)
    {
        // createdump is shipped with the .NET runtime, but on some installations it may not have execute permissions.
        // Copying it into a temp directory allows us to chmod +x safely without requiring elevation.
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
