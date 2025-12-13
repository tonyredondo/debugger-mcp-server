using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DebuggerMcp.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Coverage-oriented tests for crash recovery and discovery helpers in <see cref="LldbManager"/>.
/// </summary>
[Collection("NonParallelEnvironment")]
public class LldbManagerRecoveryAndDiscoveryTests
{
    private sealed class TestableLldbManager(Func<string, string> executeCommand) : LldbManager(NullLogger<LldbManager>.Instance)
    {
        private readonly Func<string, string> _executeCommand = executeCommand;

        public int InitializeCount { get; private set; }
        public List<(string DumpPath, string? ExecutablePath)> OpenedDumps { get; } = new();

        public override Task InitializeAsync()
        {
            InitializeCount++;
            return Task.CompletedTask;
        }

        public override void OpenDumpFile(string dumpFilePath, string? executablePath = null)
        {
            OpenedDumps.Add((dumpFilePath, executablePath));
        }

        public override string ExecuteCommand(string command) => _executeCommand(command);
    }

    [Fact]
    public void TryRecoverFromCrash_WhenDumpWasOpen_ReinitializesAndReopensDump()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var tempDir = CreateTempDirectory();
        var dumpPath = Path.Combine(tempDir, "test.core");
        File.WriteAllText(dumpPath, "not-a-real-dump");

        try
        {
            // Create an already-exited process so CleanupProcess can dispose it safely.
            var exited = StartAndWaitForExit("dotnet", "--version");

            var manager = new TestableLldbManager(_ => string.Empty);
            SetPrivateField(manager, "_lldbProcess", exited);
            SetPrivateField(manager, "_currentDumpPath", dumpPath);
            SetPrivateField(manager, "_currentExecutablePath", "/bin/fakeapp");
            SetAutoPropertyBackingField(manager, "IsDumpOpen", true);

            var recovered = (bool)InvokePrivateInstance(manager, "TryRecoverFromCrash");

            Assert.True(recovered);
            Assert.Equal(1, manager.InitializeCount);
            Assert.Single(manager.OpenedDumps);
            Assert.Equal(dumpPath, manager.OpenedDumps[0].DumpPath);
            Assert.Equal("/bin/fakeapp", manager.OpenedDumps[0].ExecutablePath);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void TryRecoverFromCrash_WhenNoDumpWasOpen_ReinitializesWithoutReopen()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var exited = StartAndWaitForExit("dotnet", "--version");
        var manager = new TestableLldbManager(_ => string.Empty);
        SetPrivateField(manager, "_lldbProcess", exited);
        SetAutoPropertyBackingField(manager, "IsDumpOpen", false);

        var recovered = (bool)InvokePrivateInstance(manager, "TryRecoverFromCrash");

        Assert.True(recovered);
        Assert.Equal(1, manager.InitializeCount);
        Assert.Empty(manager.OpenedDumps);
    }

    [Fact]
    public void FindSosPlugin_WhenEnvPathMissing_FallsBackToSymbolCache()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var original = Environment.GetEnvironmentVariable(EnvironmentConfig.SosPluginPath);
        var tempDir = CreateTempDirectory();
        try
        {
            // Point env var at a missing path to exercise the warning branch.
            Environment.SetEnvironmentVariable(EnvironmentConfig.SosPluginPath, Path.Combine(tempDir, "missing", "libsosplugin.so"));

            var symbolCache = Path.Combine(tempDir, "cache");
            var nested = Path.Combine(symbolCache, "nested");
            Directory.CreateDirectory(nested);
            var sosPath = Path.Combine(nested, "libsosplugin.so");
            File.WriteAllText(sosPath, "fake");

            var manager = new TestableLldbManager(_ => string.Empty);
            SetPrivateField(manager, "_symbolCacheDirectory", symbolCache);

            var found = (string?)InvokePrivateInstance(manager, "FindSosPlugin");

            Assert.Equal(sosPath, found);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentConfig.SosPluginPath, original);
            SafeDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void FindSosPlugin_WhenEnvPathExists_ReturnsEnvPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var original = Environment.GetEnvironmentVariable(EnvironmentConfig.SosPluginPath);
        var tempDir = CreateTempDirectory();
        try
        {
            var envPath = Path.Combine(tempDir, "libsosplugin.dylib");
            File.WriteAllText(envPath, "fake");
            Environment.SetEnvironmentVariable(EnvironmentConfig.SosPluginPath, envPath);

            var manager = new TestableLldbManager(_ => string.Empty);
            var found = (string?)InvokePrivateInstance(manager, "FindSosPlugin");

            Assert.Equal(envPath, found);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentConfig.SosPluginPath, original);
            SafeDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void LoadSosExtension_WhenPluginLoadFails_Throws()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var lldbStub = StartSleepProcess();
        try
        {
            var manager = new TestableLldbManager(command =>
            {
                if (command.StartsWith("plugin load", StringComparison.OrdinalIgnoreCase))
                {
                    return "error: failed to load plugin";
                }

                return string.Empty;
            });

            SetPrivateField(manager, "_lldbProcess", lldbStub);
            SetAutoPropertyBackingField(manager, "IsDumpOpen", true);

            var ex = Assert.Throws<InvalidOperationException>(() => manager.LoadSosExtension());
            Assert.Contains("Failed to load SOS plugin", ex.Message, StringComparison.OrdinalIgnoreCase);

            SetPrivateField(manager, "_lldbProcess", null);
        }
        finally
        {
            SafeKillProcess(lldbStub);
        }
    }

    [Fact]
    public void LoadSosExtension_WhenSosCommandsUnavailable_Throws()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var lldbStub = StartSleepProcess();
        try
        {
            var manager = new TestableLldbManager(command => command switch
            {
                var c when c.StartsWith("plugin load", StringComparison.OrdinalIgnoreCase) => "loaded",
                "sos help" => "error: 'sos' is not a valid command.",
                "soshelp" => "error: 'soshelp' is not a valid command.",
                _ => string.Empty
            });

            SetPrivateField(manager, "_lldbProcess", lldbStub);
            SetAutoPropertyBackingField(manager, "IsDumpOpen", true);

            var ex = Assert.Throws<InvalidOperationException>(() => manager.LoadSosExtension());
            Assert.Contains("commands are not available", ex.Message, StringComparison.OrdinalIgnoreCase);

            SetPrivateField(manager, "_lldbProcess", null);
        }
        finally
        {
            SafeKillProcess(lldbStub);
        }
    }

    [Fact]
    public void FindMatchingRuntimePath_WhenExactMatchMissing_FallsBackToNewest()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var manager = new TestableLldbManager(_ => string.Empty);
        SetPrivateField(manager, "_detectedRuntimeVersion", "0.0.0-missing");

        var runtimePath = (string?)InvokePrivateInstance(manager, "FindMatchingRuntimePath");

        Assert.False(string.IsNullOrEmpty(runtimePath));
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", nameof(LldbManagerRecoveryAndDiscoveryTests), Guid.NewGuid().ToString("N"));
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

    private static Process StartAndWaitForExit(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = Process.Start(startInfo);
        Assert.NotNull(process);

        Assert.True(process!.WaitForExit(30000), $"{fileName} did not exit within 30s.");
        return process;
    }

    private static Process StartSleepProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "sh",
            Arguments = "-c \"sleep 60\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo);
        Assert.NotNull(process);
        return process!;
    }

    private static void SafeKillProcess(Process process)
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
        finally
        {
            try { process.Dispose(); } catch { }
        }
    }

    private static object InvokePrivateInstance(object instance, string methodName, params object?[] args)
    {
        var method = typeof(LldbManager).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        return method!.Invoke(instance, args)!;
    }

    private static void SetPrivateField(object instance, string fieldName, object? value)
    {
        var field = typeof(LldbManager).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        field!.SetValue(instance, value);
    }

    private static void SetAutoPropertyBackingField(object instance, string propertyName, object value)
    {
        var fieldName = $"<{propertyName}>k__BackingField";
        var field = typeof(LldbManager).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        field!.SetValue(instance, value);
    }
}
