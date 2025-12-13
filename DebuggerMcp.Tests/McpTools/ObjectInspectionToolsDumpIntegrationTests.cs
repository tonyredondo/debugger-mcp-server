using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.McpTools;
using DebuggerMcp.Tests.TestDoubles;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.McpTools;

/// <summary>
/// End-to-end coverage tests for <see cref="ObjectInspectionTools"/> using a real ClrMD-backed dump.
/// </summary>
[Collection("NonParallelEnvironment")]
public class ObjectInspectionToolsDumpIntegrationTests
{
    [Fact]
    public void ObjectInspectionTools_WithRealDump_CoversCoreToolPaths()
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
        var dumpPath = Path.Combine(tempDir, $"dump-tools-{Guid.NewGuid():N}.dmp");

        using var process = StartDumpTarget(dumpTargetDll, out var readyLine);
        try
        {
            CreateDumpWithCreatedump(processId: process.Id, dumpPath: dumpPath, tempDir: tempDir);
        }
        finally
        {
            SafeKill(process);
        }

        var dumpStorage = Path.Combine(tempDir, "storage");
        Directory.CreateDirectory(dumpStorage);

        var sessionManager = new DebuggerSessionManager(
            dumpStoragePath: dumpStorage,
            debuggerFactory: _ => new FakeDebuggerManager());
        var symbolManager = new SymbolManager(symbolCacheBasePath: Path.Combine(tempDir, "symbols"), dumpStorageBasePath: dumpStorage);
        var watchStore = new WatchStore(dumpStorage);

        var tools = new ObjectInspectionTools(
            sessionManager,
            symbolManager,
            watchStore,
            NullLogger<ObjectInspectionTools>.Instance);

        var userId = "user";
        var sessionId = sessionManager.CreateSession(userId);
        var session = sessionManager.GetSessionInfo(sessionId, userId);

        using var analyzer = new ClrMdAnalyzer(NullLogger.Instance);
        Assert.True(analyzer.OpenDump(dumpPath));
        session.ClrMdAnalyzer = analyzer;

        try
        {
            // ListModules + DumpModule (exercise both tool wrappers and ClrMD module inspection).
            var listJson = tools.ListModules(sessionId, userId);
            using var listDoc = JsonDocument.Parse(listJson);

            Assert.True(listDoc.RootElement.TryGetProperty("count", out var countElement));
            Assert.True(listDoc.RootElement.TryGetProperty("modules", out var modulesElement));

            Assert.True(countElement.GetInt32() > 0);
            Assert.True(modulesElement.GetArrayLength() > 0);

            var firstModuleAddress = modulesElement[0].GetProperty("address").GetString();
            Assert.False(string.IsNullOrWhiteSpace(firstModuleAddress));

            var dumpModuleJson = tools.DumpModule(sessionId, userId, firstModuleAddress!);
            using var dumpModuleDoc = JsonDocument.Parse(dumpModuleJson);
            Assert.False(dumpModuleDoc.RootElement.TryGetProperty("error", out _));
            Assert.True(dumpModuleDoc.RootElement.TryGetProperty("name", out _));

            // Name2EE + Name2EEMethod.
            var name2eeJson = tools.Name2EE(sessionId, userId, "System.String");
            using var name2eeDoc = JsonDocument.Parse(name2eeJson);
            Assert.False(name2eeDoc.RootElement.TryGetProperty("error", out _));
            Assert.True(name2eeDoc.RootElement.TryGetProperty("foundType", out var foundType));
            Assert.True(foundType.ValueKind == JsonValueKind.Object);

            var name2eeMethodJson = tools.Name2EEMethod(sessionId, userId, "System.String", "ToString");
            using var name2eeMethodDoc = JsonDocument.Parse(name2eeMethodJson);
            Assert.False(name2eeMethodDoc.RootElement.TryGetProperty("error", out _));
            Assert.True(name2eeMethodDoc.RootElement.TryGetProperty("totalMethodsFound", out var totalMethodsFound));
            Assert.True(totalMethodsFound.GetInt32() > 0);

            // ClrStack: includeRegisters=false so the tool stays independent of LLDB.
            var stackJson = tools.ClrStack(sessionId, userId, includeArguments: true, includeLocals: true, includeRegisters: false);
            using var stackDoc = JsonDocument.Parse(stackJson);
            Assert.False(stackDoc.RootElement.TryGetProperty("error", out _));
            Assert.True(stackDoc.RootElement.TryGetProperty("threads", out var threads));
            Assert.True(threads.GetArrayLength() > 0);

            // InspectObject using the value-type address/method-table emitted by the dump target.
            var readyTokens = ParseReadyTokens(readyLine);
            if (readyTokens.TryGetValue("VT", out var vtAddressText) &&
                readyTokens.TryGetValue("VTMT", out var vtMethodTableText))
            {
                var inspectJson = tools.InspectObject(
                    sessionId,
                    userId,
                    address: vtAddressText,
                    methodTable: vtMethodTableText,
                    maxDepth: 3,
                    maxArrayElements: 0,
                    maxStringLength: 256);

                using var inspectDoc = JsonDocument.Parse(inspectJson);
                Assert.False(inspectDoc.RootElement.TryGetProperty("error", out _));
                Assert.True(inspectDoc.RootElement.TryGetProperty("fields", out var fields));

                var fieldNames = fields.EnumerateArray()
                    .Select(e => e.GetProperty("name").GetString())
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                Assert.Contains("A", fieldNames);
                Assert.Contains("B", fieldNames);
                Assert.Contains("Nested", fieldNames);
            }
        }
        finally
        {
            try { analyzer.CloseDump(); } catch { }
            session.ClrMdAnalyzer = null;
            SafeDeleteDirectory(tempDir);
        }
    }

    private static Process StartDumpTarget(string dumpTargetDll, out string readyLine)
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

        readyLine = readyTask.GetAwaiter().GetResult() ?? string.Empty;
        Assert.StartsWith("READY ", readyLine, StringComparison.Ordinal);

        return process;
    }

    private static Dictionary<string, string> ParseReadyTokens(string line)
    {
        // Format: "READY <pid> KEY=value KEY2=value2"
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 2; i < parts.Length; i++)
        {
            var part = parts[i];
            var equals = part.IndexOf('=');
            if (equals <= 0 || equals == part.Length - 1)
                continue;

            tokens[part[..equals]] = part[(equals + 1)..];
        }

        return tokens;
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
        var dir = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", nameof(ObjectInspectionToolsDumpIntegrationTests), Guid.NewGuid().ToString("N"));
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
        var testAssemblyDir = new FileInfo(typeof(ObjectInspectionToolsDumpIntegrationTests).Assembly.Location).Directory;
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
