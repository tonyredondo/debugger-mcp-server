using System.Reflection;
using DebuggerMcp.SourceLink;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp.Tests.SourceLink;

public class PdbPatcherTests
{
    [Fact]
    public void PatchAllPdbsInDirectory_DirectoryMissing_ReturnsEmpty()
    {
        var patcher = new PdbPatcher(NullLogger.Instance);

        var results = patcher.PatchAllPdbsInDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.Empty(results);
    }

    [Fact]
    public void ReadGuidFromDll_FileMissing_ReturnsNull()
    {
        var patcher = new PdbPatcher(NullLogger.Instance);

        Assert.Null(patcher.ReadGuidFromDll(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.dll")));
    }

    [Fact]
    public void ReadGuidFromPdb_InvalidFile_ReturnsNull()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var pdbPath = Path.Combine(tempDir, "bad.pdb");
            File.WriteAllBytes(pdbPath, "not a pdb"u8.ToArray());

            var patcher = new PdbPatcher(NullLogger.Instance);

            Assert.Null(patcher.ReadGuidFromPdb(pdbPath));
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void PatchPdbToMatchDll_WhenDllHasNoCodeView_ReturnsError()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var dllPath = Path.Combine(tempDir, "fake.dll");
            File.WriteAllBytes(dllPath, new byte[] { 0, 1, 2, 3, 4 });
            var pdbPath = Path.Combine(tempDir, "fake.pdb");
            File.WriteAllBytes(pdbPath, new byte[] { 5, 6, 7, 8, 9 });

            var patcher = new PdbPatcher(NullLogger.Instance);
            var result = patcher.PatchPdbToMatchDll(dllPath, pdbPath);

            Assert.False(result.Success);
            Assert.Contains("Could not read GUID from DLL", result.ErrorMessage);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void FindPdbIdOffset_WithMinimalPortablePdb_ReturnsStreamOffset()
    {
        var patcher = new PdbPatcher(NullLogger.Instance);

        var expectedOffset = 0x80;
        var bytes = CreateMinimalPortablePdbBytes(expectedOffset);

        var offset = (int)InvokePrivate(patcher, "FindPdbIdOffset", bytes);

        Assert.Equal(expectedOffset, offset);
    }

    [Fact]
    public void PatchPdbGuid_UpdatesGuidBytesAtPdbIdOffset()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var patcher = new PdbPatcher(NullLogger.Instance);
            var pdbPath = Path.Combine(tempDir, "minimal.pdb");

            var idOffset = 0x80;
            var bytes = CreateMinimalPortablePdbBytes(idOffset);
            File.WriteAllBytes(pdbPath, bytes);

            var newGuid = Guid.NewGuid();

            var success = (bool)InvokePrivate(patcher, "PatchPdbGuid", pdbPath, newGuid);

            Assert.True(success);

            var patchedBytes = File.ReadAllBytes(pdbPath);
            var guidBytes = newGuid.ToByteArray();
            Assert.Equal(guidBytes, patchedBytes.Skip(idOffset).Take(16).ToArray());
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    private static byte[] CreateMinimalPortablePdbBytes(int pdbStreamOffset)
    {
        // This is not a fully valid PDB, but it is sufficient for exercising FindPdbIdOffset.
        // Layout:
        // [BSJB header + version string + flags + stream count + stream header (#Pdb)]
        // [#Pdb stream payload at pdbStreamOffset]
        var bytes = new byte[Math.Max(256, pdbStreamOffset + 32)];

        // Signature "BSJB".
        bytes[0] = 0x42;
        bytes[1] = 0x53;
        bytes[2] = 0x4A;
        bytes[3] = 0x42;

        // Major/minor/reserved left as zeros.

        // Version length at offset 12.
        BitConverter.GetBytes(4).CopyTo(bytes, 12);

        // Version string at offset 16, 4 bytes.
        bytes[16] = (byte)'t';
        bytes[17] = (byte)'e';
        bytes[18] = (byte)'s';
        bytes[19] = (byte)'t';

        // Flags (2 bytes) at offset 20 -> 0.

        // Stream count (Int16) at offset 22.
        BitConverter.GetBytes((short)1).CopyTo(bytes, 22);

        // Stream header starts at offset 24.
        BitConverter.GetBytes(pdbStreamOffset).CopyTo(bytes, 24); // streamOffset
        BitConverter.GetBytes(32).CopyTo(bytes, 28); // streamSize

        // Stream name starts at offset 32: "#Pdb\0" then padding.
        bytes[32] = (byte)'#';
        bytes[33] = (byte)'P';
        bytes[34] = (byte)'d';
        bytes[35] = (byte)'b';
        bytes[36] = 0;

        return bytes;
    }

    private static object InvokePrivate(PdbPatcher patcher, string methodName, params object[] args)
    {
        var method = typeof(PdbPatcher).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(patcher, args);
        Assert.NotNull(result);
        return result!;
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SafeDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}

