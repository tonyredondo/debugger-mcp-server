using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.SourceLink;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp.Tests.SourceLink;

public class GitHubReleasesResolverTests
{
    [Fact]
    public void ParseReleaseResponse_InvalidJson_ReturnsNull()
    {
        using var resolver = new GitHubReleasesResolver(cacheDirectory: CreateTempDirectory(), logger: NullLogger.Instance);

        var release = InvokePrivate<GitHubReleaseInfo?>(resolver, "ParseReleaseResponse", "{not json");

        Assert.Null(release);
    }

    [Fact]
    public void ParseReleaseResponse_ValidJson_ParsesReleaseAndAssets()
    {
        using var resolver = new GitHubReleasesResolver(cacheDirectory: CreateTempDirectory(), logger: NullLogger.Instance);

        var json = JsonSerializer.Serialize(new
        {
            id = 123,
            tag_name = "v1.2.3",
            name = "Release 1.2.3",
            target_commitish = "deadbeef",
            html_url = "https://example.test/release",
            published_at = DateTime.UtcNow,
            assets = new[]
            {
                new { id = 1, name = "linux-native-symbols.tar.gz", browser_download_url = "https://example.test/a", content_type = "application/gzip", size = 10 },
                new { id = 2, name = "windows-tracer-home.zip", browser_download_url = "https://example.test/b", content_type = "application/zip", size = 20 }
            }
        });

        var release = InvokePrivate<GitHubReleaseInfo?>(resolver, "ParseReleaseResponse", json);

        Assert.NotNull(release);
        Assert.Equal(123, release!.Id);
        Assert.Equal("v1.2.3", release.TagName);
        Assert.Equal("Release 1.2.3", release.Name);
        Assert.Equal("deadbeef", release.TargetCommitish);
        Assert.Equal("https://example.test/release", release.HtmlUrl);
        Assert.Equal(2, release.Assets.Count);
        Assert.Contains(release.Assets, a => a.Name == "linux-native-symbols.tar.gz");
        Assert.Contains(release.Assets, a => a.Name == "windows-tracer-home.zip");
    }

    [Fact]
    public void GetAssetsForPlatform_Linux_ReturnsLinuxNativeAndTracerHome()
    {
        using var resolver = new GitHubReleasesResolver(cacheDirectory: CreateTempDirectory(), logger: NullLogger.Instance);

        var assets = new List<GitHubReleaseAsset>
        {
            new() { Name = "linux-native-symbols.tar.gz" },
            new() { Name = "windows-tracer-home.zip" },
            new() { Name = "windows-native-symbols.zip" }
        };

        var platform = new PlatformInfo { Os = "Linux", Architecture = "x64" };

        var selected = InvokePrivate<List<GitHubReleaseAsset>>(resolver, "GetAssetsForPlatform", assets, platform);

        Assert.Equal(2, selected.Count);
        Assert.Contains(selected, a => a.Name == "linux-native-symbols.tar.gz");
        Assert.Contains(selected, a => a.Name == "windows-tracer-home.zip");
        Assert.DoesNotContain(selected, a => a.Name == "windows-native-symbols.zip");
    }

    [Fact]
    public void GetAssetsForPlatform_Windows_ReturnsTracerHomeAndWindowsNative()
    {
        using var resolver = new GitHubReleasesResolver(cacheDirectory: CreateTempDirectory(), logger: NullLogger.Instance);

        var assets = new List<GitHubReleaseAsset>
        {
            new() { Name = "linux-native-symbols.tar.gz" },
            new() { Name = "windows-tracer-home.zip" },
            new() { Name = "windows-native-symbols.zip" }
        };

        var platform = new PlatformInfo { Os = "Windows", Architecture = "x64" };

        var selected = InvokePrivate<List<GitHubReleaseAsset>>(resolver, "GetAssetsForPlatform", assets, platform);

        Assert.Equal(2, selected.Count);
        Assert.Contains(selected, a => a.Name == "windows-tracer-home.zip");
        Assert.Contains(selected, a => a.Name == "windows-native-symbols.zip");
        Assert.DoesNotContain(selected, a => a.Name == "linux-native-symbols.tar.gz");
    }

    [Theory]
    [InlineData("x64", null, null, "linux-x64")]
    [InlineData("aarch64", null, null, "linux-arm64")]
    [InlineData("x64", true, null, "linux-musl-x64")]
    [InlineData("x64", null, "musl", "linux-musl-x64")]
    public void GetLinuxSubfolderPattern_DetectsMuslAndNormalizesArch(
        string arch,
        bool? isAlpine,
        string? libcType,
        string expected)
    {
        using var resolver = new GitHubReleasesResolver(cacheDirectory: CreateTempDirectory(), logger: NullLogger.Instance);

        var platform = new PlatformInfo
        {
            Os = "Linux",
            Architecture = arch,
            IsAlpine = isAlpine,
            LibcType = libcType
        };

        var pattern = InvokePrivate<string>(resolver, "GetLinuxSubfolderPattern", platform);
        Assert.Equal(expected, pattern);
    }

    [Fact]
    public async Task ExtractAndMergeAssetsAsync_WithWindowsZips_ExtractsExpectedFiles()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            using var resolver = new GitHubReleasesResolver(cacheDirectory: tempDir, logger: NullLogger.Instance);

            var downloadsDir = Path.Combine(tempDir, "downloads");
            var symbolsDir = Path.Combine(tempDir, "symbols");
            Directory.CreateDirectory(downloadsDir);
            Directory.CreateDirectory(symbolsDir);

            var tracerHomeZip = Path.Combine(downloadsDir, "windows-tracer-home.zip");
            CreateZip(tracerHomeZip, new Dictionary<string, byte[]>
            {
                ["net6.0/Datadog.Trace.pdb"] = "pdb"u8.ToArray(),
                ["net6.0/Datadog.Trace.dll"] = "dll"u8.ToArray(),
                ["net7.0/skip.pdb"] = "skip"u8.ToArray()
            });

            var nativeZip = Path.Combine(downloadsDir, "windows-native-symbols.zip");
            CreateZip(nativeZip, new Dictionary<string, byte[]>
            {
                ["win-x64/Datadog.Trace.ClrProfiler.Native.pdb"] = "pdb"u8.ToArray(),
                ["win-arm64/skip.pdb"] = "skip"u8.ToArray()
            });

            var platform = new PlatformInfo { Os = "Windows", Architecture = "x64" };

            var task = (Task<ArtifactMergeResult?>)InvokePrivate(resolver, "ExtractAndMergeAssetsAsync", downloadsDir, symbolsDir, platform, "net6.0", CancellationToken.None);
            var result = await task;

            Assert.NotNull(result);
            Assert.True(result!.Success);
            Assert.NotNull(result.ManagedSymbolDirectory);
            Assert.NotNull(result.NativeSymbolDirectory);
            Assert.Contains(result.PdbFiles, p => p.EndsWith("Datadog.Trace.pdb", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.PdbFiles, p => p.EndsWith("Datadog.Trace.ClrProfiler.Native.pdb", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.NativeLibraries, p => p.EndsWith("Datadog.Trace.dll", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    private static void CreateZip(string zipPath, Dictionary<string, byte[]> entries)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, contents) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(contents);
        }
    }

    private static T InvokePrivate<T>(GitHubReleasesResolver resolver, string methodName, params object[] args)
    {
        return (T)InvokePrivate(resolver, methodName, args);
    }

    private static object InvokePrivate(GitHubReleasesResolver resolver, string methodName, params object[] args)
    {
        var method = typeof(GitHubReleasesResolver).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(resolver, args)!;
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
