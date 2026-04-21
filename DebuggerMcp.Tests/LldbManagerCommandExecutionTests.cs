using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using DebuggerMcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests the LLDB command pipeline behavior that depends on one shared process per session.
/// </summary>
[Collection("NonParallelEnvironment")]
public class LldbManagerCommandExecutionTests
{
    /// <summary>
    /// Uses a short command timeout so timeout-recovery behavior can be exercised quickly.
    /// </summary>
    private sealed class TestableProcessLldbManager : LldbManager
    {
        private readonly string _processFileName;
        private readonly string _processArguments;
        private readonly TimeSpan? _commandTimeout;

        /// <summary>
        /// Initializes a new instance of the <see cref="TestableProcessLldbManager"/> class.
        /// </summary>
        /// <param name="processFileName">The executable that hosts the fake LLDB script.</param>
        /// <param name="processArguments">The arguments used to launch the fake LLDB script.</param>
        /// <param name="commandTimeout">Optional timeout override for command execution.</param>
        public TestableProcessLldbManager(
            string processFileName,
            string processArguments,
            TimeSpan? commandTimeout = null)
            : base(NullLogger<LldbManager>.Instance)
        {
            _processFileName = processFileName;
            _processArguments = processArguments;
            _commandTimeout = commandTimeout;
        }

        /// <summary>
        /// Gets the timeout used by these tests.
        /// </summary>
        protected override TimeSpan CommandTimeout => _commandTimeout ?? base.CommandTimeout;

        /// <summary>
        /// Starts the fake LLDB process and wires its redirected streams into the base manager.
        /// </summary>
        public override async Task InitializeAsync()
        {
            if (IsInitialized)
            {
                throw new InvalidOperationException("LLDB is already initialized");
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _processFileName,
                    Arguments = _processArguments,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            process.OutputDataReceived += CreateDataReceivedHandler(this, "OnOutputDataReceived");
            process.ErrorDataReceived += CreateDataReceivedHandler(this, "OnErrorDataReceived");

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start fake LLDB process");
            }

            SetPrivateField(this, "_lldbProcess", process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await Task.Delay(100);

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Fake LLDB process exited immediately with code {process.ExitCode}");
            }

            InvokeExecuteCommandInternal(this, "settings set frame-format \"fake\"");
        }
    }

    /// <summary>
    /// Verifies that overlapping command calls do not share one LLDB command slot.
    /// </summary>
    [Fact]
    public async Task ExecuteCommandInternal_WhenTwoCallsOverlap_SerializesCommandsPerSession()
    {
        var originalGateFile = Environment.GetEnvironmentVariable("FAKE_LLDB_GATE_FILE");
        var originalStartedFile = Environment.GetEnvironmentVariable("FAKE_LLDB_STARTED_FILE");
        var tempDir = CreateTempDirectory();
        var gateFile = Path.Combine(tempDir, "release.txt");
        var startedFile = Path.Combine(tempDir, "started.txt");
        var scriptPath = Path.Combine(tempDir, "fake-lldb.ps1");

        try
        {
            File.WriteAllText(scriptPath, BuildFakeLldbScript());
            Environment.SetEnvironmentVariable("FAKE_LLDB_GATE_FILE", gateFile);
            Environment.SetEnvironmentVariable("FAKE_LLDB_STARTED_FILE", startedFile);

            var (processFileName, processArguments) = BuildFakeLldbLaunchCommand(scriptPath);
            using var manager = new TestableProcessLldbManager(processFileName, processArguments);
            await manager.InitializeAsync();

            var firstTask = Task.Run(() => InvokeExecuteCommandInternal(manager, "block-first"));
            WaitForFile(startedFile, TimeSpan.FromSeconds(5));

            var secondTask = Task.Run(() => InvokeExecuteCommandInternal(manager, "second"));
            await Task.Delay(200);

            Assert.False(secondTask.IsCompleted);

            File.WriteAllText(gateFile, "release");

            var firstOutput = await firstTask;
            var secondOutput = await secondTask;

            Assert.Contains("RESULT:block-first", firstOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("RESULT:second", firstOutput, StringComparison.Ordinal);
            Assert.Contains("RESULT:second", secondOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("RESULT:block-first", secondOutput, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_LLDB_GATE_FILE", originalGateFile);
            Environment.SetEnvironmentVariable("FAKE_LLDB_STARTED_FILE", originalStartedFile);
            SafeDeleteDirectory(tempDir);
        }
    }

    /// <summary>
    /// Verifies that a timed-out command forces recovery before another command can reuse the session.
    /// </summary>
    [Fact]
    public async Task ExecuteCommandInternal_WhenCommandTimesOut_RecoversBeforeNextCommand()
    {
        var originalGateFile = Environment.GetEnvironmentVariable("FAKE_LLDB_GATE_FILE");
        var originalStartedFile = Environment.GetEnvironmentVariable("FAKE_LLDB_STARTED_FILE");
        var tempDir = CreateTempDirectory();
        var startedFile = Path.Combine(tempDir, "timeout-started.txt");
        var scriptPath = Path.Combine(tempDir, "fake-lldb.ps1");

        try
        {
            File.WriteAllText(scriptPath, BuildFakeLldbScript());
            Environment.SetEnvironmentVariable("FAKE_LLDB_GATE_FILE", null);
            Environment.SetEnvironmentVariable("FAKE_LLDB_STARTED_FILE", startedFile);

            var (processFileName, processArguments) = BuildFakeLldbLaunchCommand(scriptPath);
            using var manager = new TestableProcessLldbManager(
                processFileName,
                processArguments,
                TimeSpan.FromMilliseconds(250));
            await manager.InitializeAsync();

            var timeoutException = Assert.Throws<InvalidOperationException>(
                () => InvokeExecuteCommandInternal(manager, "hang-for-timeout"));

            Assert.Contains("timed out", timeoutException.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("automatically recovered", timeoutException.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(manager.IsInitialized);

            var nextOutput = InvokeExecuteCommandInternal(manager, "after-timeout");

            Assert.Contains("RESULT:after-timeout", nextOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("hang-for-timeout", nextOutput, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_LLDB_GATE_FILE", originalGateFile);
            Environment.SetEnvironmentVariable("FAKE_LLDB_STARTED_FILE", originalStartedFile);
            SafeDeleteDirectory(tempDir);
        }
    }

    /// <summary>
    /// Creates a temporary directory for one test run.
    /// </summary>
    /// <returns>The created directory path.</returns>
    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "DebuggerMcp.Tests",
            nameof(LldbManagerCommandExecutionTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Best-effort recursive cleanup for the test working directory.
    /// </summary>
    /// <param name="path">The directory to remove.</param>
    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for temporary test files.
        }
    }

    /// <summary>
    /// Builds the host executable and arguments used to launch the fake LLDB script.
    /// </summary>
    /// <param name="scriptPath">The PowerShell script that emulates LLDB.</param>
    /// <returns>The executable and argument string used to start the fake process.</returns>
    private static (string FileName, string Arguments) BuildFakeLldbLaunchCommand(string scriptPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return (
                "powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"");
        }

        return ("pwsh", $"-NoProfile -File \"{scriptPath}\"");
    }

    /// <summary>
    /// Builds the fake LLDB PowerShell script used by the tests.
    /// </summary>
    /// <returns>The script contents.</returns>
    private static string BuildFakeLldbScript()
    {
        return """
               $sentinel = '---MCP-END---'
               $gateFile = $env:FAKE_LLDB_GATE_FILE
               $startedFile = $env:FAKE_LLDB_STARTED_FILE
               
               while (($line = [Console]::In.ReadLine()) -ne $null) {
                   if ($line -eq $sentinel) {
                       [Console]::Out.WriteLine("(lldb) $sentinel")
                       [Console]::Error.WriteLine("error: '$sentinel' is not a valid command.")
                       continue
                   }
               
                   [Console]::Out.WriteLine("(lldb) $line")
               
                   switch ($line) {
                       'block-first' {
                           if ($startedFile) {
                               New-Item -ItemType File -Path $startedFile -Force | Out-Null
                           }
               
                           while (-not (Test-Path $gateFile)) {
                               Start-Sleep -Milliseconds 25
                           }
               
                           [Console]::Out.WriteLine('RESULT:block-first')
                       }
               
                       'hang-for-timeout' {
                           if ($startedFile) {
                               New-Item -ItemType File -Path $startedFile -Force | Out-Null
                           }
               
                           Start-Sleep -Milliseconds 1200
                           [Console]::Out.WriteLine('RESULT:hang-for-timeout')
                       }
               
                       'after-timeout' {
                           [Console]::Out.WriteLine('RESULT:after-timeout')
                       }
               
                       default {
                           [Console]::Out.WriteLine("RESULT:$line")
                       }
                   }
               }
               """;
    }

    /// <summary>
    /// Polls until one file appears or fails the test if the timeout expires first.
    /// </summary>
    /// <param name="path">The file to wait for.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    private static void WaitForFile(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return;
            }

            Thread.Sleep(25);
        }

        Assert.True(File.Exists(path), $"Timed out waiting for file '{path}'.");
    }

    /// <summary>
    /// Invokes <c>LldbManager.ExecuteCommandInternal</c> and rethrows the inner exception directly.
    /// </summary>
    /// <param name="manager">The manager to execute against.</param>
    /// <param name="command">The LLDB command to send.</param>
    /// <returns>The command output returned by the manager.</returns>
    private static string InvokeExecuteCommandInternal(LldbManager manager, string command)
    {
        var method = typeof(LldbManager).GetMethod("ExecuteCommandInternal", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        try
        {
            return (string)method!.Invoke(manager, [command])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    /// <summary>
    /// Creates a delegate for one private LLDB output handler method.
    /// </summary>
    /// <param name="manager">The manager instance that owns the handler.</param>
    /// <param name="methodName">The private handler name.</param>
    /// <returns>The bound event handler delegate.</returns>
    private static DataReceivedEventHandler CreateDataReceivedHandler(LldbManager manager, string methodName)
    {
        var method = typeof(LldbManager).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        return (DataReceivedEventHandler)Delegate.CreateDelegate(
            typeof(DataReceivedEventHandler),
            manager,
            method!);
    }

    /// <summary>
    /// Sets one private LLDB manager field needed by the test host.
    /// </summary>
    /// <param name="instance">The manager instance to modify.</param>
    /// <param name="fieldName">The private field name.</param>
    /// <param name="value">The value to assign.</param>
    private static void SetPrivateField(object instance, string fieldName, object? value)
    {
        var field = typeof(LldbManager).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }
}
