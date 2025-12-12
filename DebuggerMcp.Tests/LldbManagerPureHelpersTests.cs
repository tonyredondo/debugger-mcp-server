using DebuggerMcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for pure helper methods in <see cref="LldbManager"/>.
/// </summary>
public class LldbManagerPureHelpersTests
{
    [Theory]
    [InlineData("9.0", true)]
    [InlineData("9.0.10", true)]
    [InlineData("8.0.0.1", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("9", false)]
    [InlineData("9.x.1", false)]
    [InlineData("9.0.10.1.2", false)]
    public void IsValidVersionString_ValidatesFormat(string input, bool expected)
    {
        Assert.Equal(expected, LldbManager.IsValidVersionString(input));
    }

    [Fact]
    public void ParseRegisters_ParsesMixedArm64Registers()
    {
        var output = "x0 = 0x1\nfp = 0x2\nlr = 0x3\nsp = 0x4\npc = 0x5\ncpsr = 0x6\n";
        var regs = LldbManager.ParseRegisters(output);

        Assert.NotNull(regs);
        var actual = regs!;
        Assert.Equal(2ul, actual.FramePointer);
        Assert.Equal(3ul, actual.LinkRegister);
        Assert.Equal(4ul, actual.StackPointer);
        Assert.Equal(5ul, actual.ProgramCounter);
        Assert.Equal((uint)6, actual.StatusRegister);
        Assert.NotNull(actual.GeneralPurpose);
        Assert.True(actual.GeneralPurpose.TryGetValue("x0", out var x0));
        Assert.Equal(1ul, x0);
    }

    [Fact]
    public void ParseRegisters_ParsesMixedX64Registers()
    {
        var output = "rax = 0x10\nrbp = 0x20\nrsp = 0x30\nrip = 0x40\nrflags = 0x50\n";
        var regs = LldbManager.ParseRegisters(output);

        Assert.NotNull(regs);
        var actual = regs!;
        Assert.Equal(0x20ul, actual.FramePointer);
        Assert.Equal(0x30ul, actual.StackPointer);
        Assert.Equal(0x40ul, actual.ProgramCounter);
        Assert.Equal((uint)0x50, actual.StatusRegister);
        Assert.NotNull(actual.GeneralPurpose);
        Assert.True(actual.GeneralPurpose.TryGetValue("rax", out var rax));
        Assert.Equal(0x10ul, rax);
    }

    [Fact]
    public void IsUsableElf_WhenNotElf_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerPureHelpersTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var path = Path.Combine(tempDir, "not-elf.bin");
            File.WriteAllBytes(path, new byte[80]);

            Assert.False(LldbManager.IsUsableElf(path, NullLogger<LldbManager>.Instance));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void IsUsableElf_WhenValidElfExecutable_ReturnsTrue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerPureHelpersTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var path = Path.Combine(tempDir, "app");

            // Minimal ELF header (64 bytes) with:
            // - magic 0x7F 'E' 'L' 'F'
            // - class: 64-bit (2)
            // - e_type: ET_EXEC (2) at offset 16
            // - e_phnum: 1 at offset 56 for 64-bit
            var header = new byte[64];
            header[0] = 0x7F;
            header[1] = (byte)'E';
            header[2] = (byte)'L';
            header[3] = (byte)'F';
            header[4] = 2;
            header[16] = 2;
            header[56] = 1;

            File.WriteAllBytes(path, header);

            Assert.True(LldbManager.IsUsableElf(path, NullLogger<LldbManager>.Instance));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void IsUsableElf_WhenValidElfSharedObject_ReturnsTrue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerPureHelpersTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var path = Path.Combine(tempDir, "libfoo.so");

            // 64-bit ELF shared object (ET_DYN = 3) with program headers.
            var header = new byte[64];
            header[0] = 0x7F;
            header[1] = (byte)'E';
            header[2] = (byte)'L';
            header[3] = (byte)'F';
            header[4] = 2;
            header[16] = 3;
            header[56] = 1;

            File.WriteAllBytes(path, header);

            Assert.True(LldbManager.IsUsableElf(path, NullLogger<LldbManager>.Instance));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ParseVerifyCoreOutput_ParsesModulesAndDetectsMuslAndArchitecture()
    {
        var lines = new[]
        {
            "/tmp/core",
            "00007F3156DC7000 /lib/ld-musl-x86_64.so.1",
            "00007F3156DC8000 /usr/share/dotnet/shared/Microsoft.NETCore.App/9.0.10/libcoreclr.so",
            "not a module line",
        };

        var result = LldbManager.ParseVerifyCoreOutput(lines, NullLogger<LldbManager>.Instance);

        Assert.True(result.IsAlpine);
        Assert.Equal("x64", result.Architecture);
        Assert.Equal("ld-musl-x86_64.so.1", result.MainExecutableName);
        Assert.NotEmpty(result.ModulePaths);
        Assert.True(result.ModuleAddresses.Count >= 2);
    }

    [Fact]
    public void ParseVerifyCoreOutput_DetectsArm64FromAarch64()
    {
        var lines = new[]
        {
            "/tmp/core",
            "0000000000000001 /lib/ld-musl-aarch64.so.1",
            "0000000000000002 /usr/share/dotnet/shared/Microsoft.NETCore.App/9.0.10/libcoreclr.so",
        };

        var result = LldbManager.ParseVerifyCoreOutput(lines, NullLogger<LldbManager>.Instance);

        Assert.True(result.IsAlpine);
        Assert.Equal("arm64", result.Architecture);
    }

    [Fact]
    public void ParseVerifyCoreOutput_DetectsX86FromI686()
    {
        var lines = new[]
        {
            "/tmp/core",
            "00000001 /lib/ld-linux.so.2",
            "00000002 /usr/lib/i686-linux-gnu/libc.so.6",
        };

        var result = LldbManager.ParseVerifyCoreOutput(lines, NullLogger<LldbManager>.Instance);

        Assert.Equal("x86", result.Architecture);
    }

    [Theory]
    [InlineData("Microsoft.NETCore.App/9.0.10", "9.0.10")]
    [InlineData("/usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.0/libcoreclr.so", "8.0.0")]
    [InlineData("no runtime here", null)]
    [InlineData("Microsoft.NETCore.App/9.x", null)]
    public void ExtractRuntimeVersionFromDotnetSymbolLine_ExtractsOrReturnsNull(string line, string? expected)
    {
        Assert.Equal(expected, LldbManager.ExtractRuntimeVersionFromDotnetSymbolLine(line));
    }

    [Theory]
    [InlineData("", null, false)]
    [InlineData("some module list", "9.0.10", true)]
    [InlineData("/usr/share/dotnet/shared/Microsoft.NETCore.App/9.0.10/libcoreclr.so", null, true)]
    [InlineData("no clr modules", null, false)]
    public void DetectDotNetDump_DetectsViaRuntimeOrModuleList(string moduleList, string? runtimeVersion, bool expected)
    {
        Assert.Equal(expected, LldbManager.DetectDotNetDump(moduleList, runtimeVersion));
    }

    [Fact]
    public void FilterNativeModulesFromVerifyCore_KeepsSoAndVersionedSoOnly()
    {
        var modules = new Dictionary<string, ulong>
        {
            ["/usr/lib/libc.so.6"] = 1,
            ["/usr/lib/libssl.so"] = 2,
            ["/usr/lib/libmscordaccore.so.dbg"] = 3,
            ["/usr/share/dotnet/shared/Microsoft.NETCore.App/9.0.10/System.Private.CoreLib.dll"] = 4,
        };

        var native = LldbManager.FilterNativeModulesFromVerifyCore(modules);

        Assert.Contains(native, m => m.Key.EndsWith("libc.so.6", StringComparison.Ordinal));
        Assert.Contains(native, m => m.Key.EndsWith("libssl.so", StringComparison.Ordinal));
        Assert.DoesNotContain(native, m => m.Key.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(native, m => m.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseImageListOutputForLoadedModules_ParsesModuleNamesAndAddresses()
    {
        var output = "[  0] ABCDEFAB-0000-0000-0000-000000000000 0x0000f5855aa1c000 /usr/lib/libfoo.so\n" +
                     "[  1] 12345678-0000-0000-0000-000000000000 0x0000000000000000 /usr/lib/zero.so\n";

        var parsed = LldbManager.ParseImageListOutputForLoadedModules(output);

        Assert.True(parsed.ContainsKey("libfoo.so"));
        Assert.Equal(0x0000f5855aa1c000ul, parsed["libfoo.so"]);
        Assert.False(parsed.ContainsKey("zero.so"));
    }

    [Fact]
    public void BuildSymbolCacheModuleIndex_IndexesSoAndSkipsDbgFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"LldbManagerPureHelpersTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "libfoo.so"), "x");
            File.WriteAllText(Path.Combine(tempDir, "libbar.so.1"), "x");
            File.WriteAllText(Path.Combine(tempDir, "libfoo.so.dbg"), "x");

            var index = LldbManager.BuildSymbolCacheModuleIndex(tempDir);

            Assert.True(index.ContainsKey("libfoo.so"));
            Assert.True(index.ContainsKey("libbar.so.1"));
            Assert.False(index.ContainsKey("libfoo.so.dbg"));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
