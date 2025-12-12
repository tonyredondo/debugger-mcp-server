using System.Runtime.InteropServices;
using DebuggerMcp;
using DebuggerMcp.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp.Tests;

/// <summary>
/// Integration-style tests for <see cref="LldbManager"/> without requiring a real LLDB or dotnet-symbol installation.
/// </summary>
/// <remarks>
/// These tests create small fake executables named <c>lldb</c> and <c>dotnet-symbol</c> in a temporary directory,
/// prepend that directory to PATH, then exercise success paths of <see cref="LldbManager"/> that are otherwise hard
/// to test in CI.
/// </remarks>
[Collection("NonParallelEnvironment")]
public class LldbManagerFakeToolIntegrationTests
{
    [Fact]
    public async Task OpenDumpFile_WithFakeTools_RunsHappyPathAndLoadsSos()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // LldbManager is primarily Linux/macOS; its dotnet-symbol discovery uses 'which'.
            return;
        }

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalSosPluginPath = Environment.GetEnvironmentVariable(EnvironmentConfig.SosPluginPath);
        var originalDotnetSymbolPath = Environment.GetEnvironmentVariable(EnvironmentConfig.DotnetSymbolToolPath);

        var tempDir = CreateTempDirectory();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var runtimeVersion = "99.99.99";
        var fakeRuntimeDir = Path.Combine(home, ".dotnet", "shared", "Microsoft.NETCore.App", runtimeVersion);

        try
        {
            // Arrange - create fake runtime DAC file so FindMatchingRuntimePath succeeds.
            Directory.CreateDirectory(fakeRuntimeDir);
            File.WriteAllBytes(Path.Combine(fakeRuntimeDir, "libmscordaccore.so"), [0x7F, (byte)'E', (byte)'L', (byte)'F']);
            File.WriteAllBytes(Path.Combine(fakeRuntimeDir, "libmscordaccore.dylib"), [0xCF, 0xFA, 0xED, 0xFE]);

            // Arrange - create fake dump + modules used by verifycore parsing and module loading.
            var dumpPath = Path.Combine(tempDir, "test.core");
            File.WriteAllText(dumpPath, "not-a-real-dump");

            var fakeExePath = Path.Combine(tempDir, "fakeapp");
            File.WriteAllText(fakeExePath, "fake exe");

            var nativeModule1 = Path.Combine(tempDir, "libfoo.so");
            var nativeModule1Dbg = Path.Combine(tempDir, "libfoo.so.dbg");
            File.WriteAllText(nativeModule1, "fake so");
            File.WriteAllText(nativeModule1Dbg, "fake dbg");

            var nativeModule2 = Path.Combine(tempDir, "libbar.so.1");
            var nativeModule2Debug = Path.Combine(tempDir, "libbar.so.1.debug");
            File.WriteAllText(nativeModule2, "fake so.1");
            File.WriteAllText(nativeModule2Debug, "fake debug");

            // Arrange - create fake tools and inject into PATH.
            var fakeToolsDir = Path.Combine(tempDir, "tools");
            Directory.CreateDirectory(fakeToolsDir);

            var sosPluginPath = Path.Combine(tempDir, "libsosplugin.dylib");
            File.WriteAllText(sosPluginPath, "fake sos");

            WriteExecutableFile(Path.Combine(fakeToolsDir, "lldb"), BuildFakeLldbScript());
            var fakeDotnetSymbolPath = Path.Combine(fakeToolsDir, "dotnet-symbol");
            WriteExecutableFile(
                fakeDotnetSymbolPath,
                BuildFakeDotnetSymbolScript(
                    runtimeVersion: runtimeVersion,
                    verifyCoreDumpPath: dumpPath,
                    verifyCoreLines:
                    [
                        // First line is the dump path (verifycore prints it).
                        dumpPath,
                        // Module lines: address + path.
                        $"0000000000000001 {fakeExePath}",
                        $"0000000000001000 {nativeModule1}",
                        $"0000000000002000 {nativeModule2}",
                    ]));

            Environment.SetEnvironmentVariable("PATH", $"{fakeToolsDir}:{originalPath}");
            Environment.SetEnvironmentVariable(EnvironmentConfig.SosPluginPath, sosPluginPath);
            Environment.SetEnvironmentVariable(EnvironmentConfig.DotnetSymbolToolPath, fakeDotnetSymbolPath);

            var manager = new LldbManager(NullLogger<LldbManager>.Instance);

            // Act - initialize and open.
            await manager.InitializeAsync();

            // ConfigureSymbolPath is allowed before a dump is open.
            manager.ConfigureSymbolPath($"{tempDir}/sym1 {tempDir}/sym2");

            manager.OpenDumpFile(dumpPath, executablePath: fakeExePath);

            // Assert - happy path state is set.
            Assert.True(manager.IsInitialized);
            Assert.True(manager.IsDumpOpen);
            Assert.Equal(dumpPath, manager.CurrentDumpPath);
            Assert.True(manager.IsDotNetDump);
            Assert.True(manager.IsSosLoaded);
            Assert.NotNull(manager.VerifiedCorePlatform);
            Assert.NotEmpty(manager.VerifiedCorePlatform!.ModuleAddresses);

            // Act - basic SOS command should work via TransformSosCommand.
            var output = manager.ExecuteCommand("!pe");
            Assert.Contains("Fake SOS", output, StringComparison.Ordinal);

            // Cleanup
            manager.CloseDump();
            Assert.False(manager.IsDumpOpen);
            manager.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable(EnvironmentConfig.SosPluginPath, originalSosPluginPath);
            Environment.SetEnvironmentVariable(EnvironmentConfig.DotnetSymbolToolPath, originalDotnetSymbolPath);

            SafeDeleteDirectory(tempDir);
            SafeDeleteDirectory(fakeRuntimeDir);
        }
    }

    [Fact]
    public async Task OpenDumpFile_WhenStandaloneAppAndUsableElfExists_UsesElfAndContinues()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalSosPluginPath = Environment.GetEnvironmentVariable(EnvironmentConfig.SosPluginPath);
        var originalDotnetSymbolPath = Environment.GetEnvironmentVariable(EnvironmentConfig.DotnetSymbolToolPath);

        var tempDir = CreateTempDirectory();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var runtimeVersion = "99.99.99";
        var fakeRuntimeDir = Path.Combine(home, ".dotnet", "shared", "Microsoft.NETCore.App", runtimeVersion);

        try
        {
            Directory.CreateDirectory(fakeRuntimeDir);
            File.WriteAllBytes(Path.Combine(fakeRuntimeDir, "libmscordaccore.so"), [0x7F, (byte)'E', (byte)'L', (byte)'F']);

            var dumpPath = Path.Combine(tempDir, "test.core");
            File.WriteAllText(dumpPath, "not-a-real-dump");

            // Create a "usable" ELF .dbg for the main executable name so FindUsableElfForStandaloneApp succeeds.
            // Minimal ELF header (64 bytes):
            // - magic 0x7F 'E' 'L' 'F'
            // - class: 64-bit (2)
            // - e_type: ET_EXEC (2) at offset 16
            // - e_phnum: 1 at offset 56
            var usableElfDbgPath = Path.Combine(tempDir, "fakeapp.dbg");
            var header = new byte[64];
            header[0] = 0x7F;
            header[1] = (byte)'E';
            header[2] = (byte)'L';
            header[3] = (byte)'F';
            header[4] = 2;
            header[16] = 2;
            header[56] = 1;
            File.WriteAllBytes(usableElfDbgPath, header);

            var nativeModule = Path.Combine(tempDir, "libfoo.so");
            File.WriteAllText(nativeModule, "fake so");

            var fakeToolsDir = Path.Combine(tempDir, "tools");
            Directory.CreateDirectory(fakeToolsDir);

            var sosPluginPath = Path.Combine(tempDir, "libsosplugin.dylib");
            File.WriteAllText(sosPluginPath, "fake sos");

            WriteExecutableFile(Path.Combine(fakeToolsDir, "lldb"), BuildFakeLldbScript());
            var fakeDotnetSymbolPath = Path.Combine(fakeToolsDir, "dotnet-symbol");
            WriteExecutableFile(
                fakeDotnetSymbolPath,
                BuildFakeDotnetSymbolScript(
                    runtimeVersion: runtimeVersion,
                    verifyCoreDumpPath: dumpPath,
                    verifyCoreLines:
                    [
                        dumpPath,
                        // Main executable path must be non-dotnet and not in /dotnet/ to be considered standalone.
                        $"0000000000000001 {Path.Combine(tempDir, "fakeapp")}",
                        $"0000000000001000 {nativeModule}",
                    ]));

            Environment.SetEnvironmentVariable("PATH", $"{fakeToolsDir}:{originalPath}");
            Environment.SetEnvironmentVariable(EnvironmentConfig.SosPluginPath, sosPluginPath);
            Environment.SetEnvironmentVariable(EnvironmentConfig.DotnetSymbolToolPath, fakeDotnetSymbolPath);

            var manager = new LldbManager(NullLogger<LldbManager>.Instance);

            await manager.InitializeAsync();
            manager.OpenDumpFile(dumpPath, executablePath: null);

            Assert.True(manager.IsDumpOpen);
            Assert.True(manager.IsDotNetDump);
            Assert.True(manager.IsSosLoaded);
            Assert.NotNull(manager.VerifiedCorePlatform);

            manager.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable(EnvironmentConfig.SosPluginPath, originalSosPluginPath);
            Environment.SetEnvironmentVariable(EnvironmentConfig.DotnetSymbolToolPath, originalDotnetSymbolPath);

            SafeDeleteDirectory(tempDir);
            SafeDeleteDirectory(fakeRuntimeDir);
        }
    }

    [Fact]
    public async Task OpenDumpFile_WhenCachedSymbolsPresent_SkipsDotnetSymbolDownloadPass()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalSosPluginPath = Environment.GetEnvironmentVariable(EnvironmentConfig.SosPluginPath);
        var originalDotnetSymbolPath = Environment.GetEnvironmentVariable(EnvironmentConfig.DotnetSymbolToolPath);

        var tempDir = CreateTempDirectory();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var runtimeVersion = "99.99.99";
        var fakeRuntimeDir = Path.Combine(home, ".dotnet", "shared", "Microsoft.NETCore.App", runtimeVersion);

        try
        {
            Directory.CreateDirectory(fakeRuntimeDir);
            File.WriteAllBytes(Path.Combine(fakeRuntimeDir, "libmscordaccore.so"), [0x7F, (byte)'E', (byte)'L', (byte)'F']);

            var dumpPath = Path.Combine(tempDir, "test.core");
            File.WriteAllText(dumpPath, "not-a-real-dump");

            var fakeExePath = Path.Combine(tempDir, "fakeapp");
            File.WriteAllText(fakeExePath, "fake exe");

            var nativeModule = Path.Combine(tempDir, "libfoo.so");
            File.WriteAllText(nativeModule, "fake so");

            // Pre-create the symbol cache with files listed in metadata so DownloadSymbols can skip.
            var symbolCacheDir = Path.Combine(tempDir, ".symbols_test");
            Directory.CreateDirectory(symbolCacheDir);
            File.WriteAllText(Path.Combine(symbolCacheDir, "System.Private.CoreLib.dll"), "cached dll");
            File.WriteAllText(Path.Combine(symbolCacheDir, "libsosplugin.dylib"), "cached sos");

            var metadataPath = LldbManager.GetMetadataPathForDump(dumpPath);
            Assert.NotNull(metadataPath);

            var metadataJson = $$"""
{"RuntimeVersion":"{{runtimeVersion}}","SymbolFiles":["System.Private.CoreLib.dll","libsosplugin.dylib"]}
""";
            File.WriteAllText(metadataPath!, metadataJson);

            var fakeToolsDir = Path.Combine(tempDir, "tools");
            Directory.CreateDirectory(fakeToolsDir);

            var sosPluginPath = Path.Combine(tempDir, "libsosplugin.dylib");
            File.WriteAllText(sosPluginPath, "fake sos");

            WriteExecutableFile(Path.Combine(fakeToolsDir, "lldb"), BuildFakeLldbScript());
            var fakeDotnetSymbolPath = Path.Combine(fakeToolsDir, "dotnet-symbol");
            WriteExecutableFile(
                fakeDotnetSymbolPath,
                BuildFakeDotnetSymbolScript(
                    runtimeVersion: runtimeVersion,
                    verifyCoreDumpPath: dumpPath,
                    verifyCoreLines:
                    [
                        dumpPath,
                        $"0000000000000001 {fakeExePath}",
                        $"0000000000001000 {nativeModule}",
                    ]));

            Environment.SetEnvironmentVariable("PATH", $"{fakeToolsDir}:{originalPath}");
            Environment.SetEnvironmentVariable(EnvironmentConfig.SosPluginPath, sosPluginPath);
            Environment.SetEnvironmentVariable(EnvironmentConfig.DotnetSymbolToolPath, fakeDotnetSymbolPath);

            var manager = new LldbManager(NullLogger<LldbManager>.Instance);

            await manager.InitializeAsync();
            manager.OpenDumpFile(dumpPath, executablePath: fakeExePath);

            // The download pass writes a marker file; if the cache was used it should not exist.
            Assert.False(File.Exists(Path.Combine(symbolCacheDir, ".dotnet_symbol_ran")));

            manager.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable(EnvironmentConfig.SosPluginPath, originalSosPluginPath);
            Environment.SetEnvironmentVariable(EnvironmentConfig.DotnetSymbolToolPath, originalDotnetSymbolPath);

            SafeDeleteDirectory(tempDir);
            SafeDeleteDirectory(fakeRuntimeDir);
        }
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", nameof(LldbManagerFakeToolIntegrationTests), Guid.NewGuid().ToString("N"));
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

    private static void WriteExecutableFile(string path, string contents)
    {
        File.WriteAllText(path, contents);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static string BuildFakeLldbScript()
    {
        // This is a minimal fake LLDB that supports LldbManager's sentinel-based completion detection.
        // It echoes commands and returns canned outputs for specific commands used during OpenDumpFile/LoadSosExtension.
        return """
#!/usr/bin/env bash
set -euo pipefail

while IFS= read -r line; do
  if [[ "$line" == "---MCP-END---" ]]; then
    echo "(lldb) ---MCP-END---"
    echo "error: '---MCP-END---' is not a valid command." 1>&2
    continue
  fi

  # Echo the command with a prompt prefix, like real LLDB.
  echo "(lldb) $line"

  case "$line" in
    "image list")
      # Keep base addresses at 0 so ParseImageListOutputForLoadedModules treats them as not loaded.
      echo "[  0] 00000000-0000-0000-0000-000000000000 0x0000000000000000 /usr/lib/libsystem.dylib"
      echo "[  1] 00000000-0000-0000-0000-000000000000 0x0000000000000000 /usr/share/dotnet/shared/Microsoft.NETCore.App/99.99.99/libcoreclr.dylib"
      ;;
    "sos help"|"soshelp")
      echo "Fake SOS help"
      ;;
    "sosstatus")
      echo "Fake SOS status: OK"
      ;;
    "!pe"|"pe")
      echo "Fake SOS !pe output"
      ;;
    *)
      # Default: no output (successful no-op).
      ;;
  esac
done
""";
    }

    private static string BuildFakeDotnetSymbolScript(string runtimeVersion, string verifyCoreDumpPath, IEnumerable<string> verifyCoreLines)
    {
        var verifyCoreEchoLines = string.Join(
            "\n",
            verifyCoreLines.Select(l => $"echo \"{l.Replace("\"", "\\\"")}\""));

        // Fake dotnet-symbol supports:
        // - --verifycore <dump>
        // - --symbols ... (no-op)
        // - <dump> -o <dir> ... (creates dummy DLL + SOS plugin in output dir)
        return $$"""
#!/usr/bin/env bash
set -euo pipefail

args=("$@")

has_verifycore=0
has_symbols=0
out_dir=""

for ((i=0; i<${#args[@]}; i++)); do
  if [[ "${args[$i]}" == "--verifycore" ]]; then
    has_verifycore=1
  fi
  if [[ "${args[$i]}" == "--symbols" ]]; then
    has_symbols=1
  fi
  if [[ "${args[$i]}" == "-o" ]]; then
    j=$((i+1))
    out_dir="${args[$j]}"
  fi
done

if [[ $has_verifycore -eq 1 ]]; then
  # Print deterministic verifycore output.
  {{verifyCoreEchoLines}}
  # Ensure LldbManager's async OutputDataReceived handler has time to observe output before process exit.
  sleep 0.05
  exit 0
fi

if [[ -n "$out_dir" ]]; then
  mkdir -p "$out_dir"
fi

if [[ $has_symbols -eq 1 ]]; then
  # PDB download pass. Create a placeholder PDB for determinism.
  if [[ -n "$out_dir" ]]; then
    echo "PDB download complete"
    echo "fake-pdb" > "$out_dir/System.Private.CoreLib.pdb"
  fi
  sleep 0.05
  exit 0
fi

# Primary download pass: create a dummy DLL so DownloadPdbSymbols triggers.
if [[ -n "$out_dir" ]]; then
  echo "Downloading symbols for Microsoft.NETCore.App/{{runtimeVersion}}"
  echo "fake-dll" > "$out_dir/System.Private.CoreLib.dll"
  echo "fake-sos" > "$out_dir/libsosplugin.dylib"
  echo "ran" > "$out_dir/.dotnet_symbol_ran"
fi

sleep 0.05
exit 0
""";
    }
}
