using DebuggerMcp.SourceLink;
using System.Text.Json;

namespace DebuggerMcp.Tests.SourceLink;

public class GitHubReleasesCacheTests
{
    [Fact]
    public void SaveAndReload_RoundTripsReleaseEntries()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var cache = new GitHubReleasesCache(tempDir);

            var release = new GitHubReleaseInfo
            {
                Id = 123,
                TagName = "v1.2.3",
                Name = "Release 1.2.3",
                TargetCommitish = "deadbeef",
                HtmlUrl = "https://example.test/release",
                PublishedAt = DateTime.UtcNow
            };
            release.Assets.Add(new GitHubReleaseAsset { Name = "a.zip", BrowserDownloadUrl = "https://example.test/a.zip" });
            release.Assets.Add(new GitHubReleaseAsset { Name = "b.zip", BrowserDownloadUrl = "https://example.test/b.zip" });

            cache.SetReleaseByVersion("o", "r", "1.2.3", release);
            cache.SetReleaseByCommit("o", "r", "deadbeef", release);

            var symbolsDir = Path.Combine(tempDir, "symbols");
            Directory.CreateDirectory(symbolsDir);
            cache.SetDownloadedSymbols("1.2.3", "linux-x64", symbolsDir);

            cache.Save();

            var reloaded = new GitHubReleasesCache(tempDir);

            Assert.True(reloaded.TryGetReleaseByVersion("o", "r", "1.2.3", out var byVersion));
            Assert.NotNull(byVersion);
            Assert.Equal("v1.2.3", byVersion!.TagName);
            Assert.Equal(2, byVersion.Assets.Count);

            Assert.True(reloaded.TryGetReleaseByCommit("o", "r", "deadbeef", out var byCommit));
            Assert.NotNull(byCommit);
            Assert.Equal(123, byCommit!.Id);

            Assert.True(reloaded.HasDownloadedSymbols("1.2.3", "linux-x64", out var resolvedSymbolsDir));
            Assert.Equal(symbolsDir, resolvedSymbolsDir);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Load_ExpiresOldEntries()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var cacheFile = Path.Combine(tempDir, "github_releases_cache.json");
            var json = JsonSerializer.Serialize(new
            {
                releasesByVersion = new Dictionary<string, object>
                {
                    ["o/r/v1.0.0"] = new
                    {
                        CachedAt = DateTime.UtcNow.AddHours(-10),
                        Release = new
                        {
                            Id = 1,
                            TagName = "v1.0.0",
                            Name = "Old Release",
                            HtmlUrl = "https://example.test",
                            Assets = new Dictionary<string, string>()
                        }
                    }
                },
                releasesByCommit = new Dictionary<string, object>(),
                downloadedSymbols = new Dictionary<string, string>()
            });

            File.WriteAllText(cacheFile, json);

            var cache = new GitHubReleasesCache(tempDir);

            Assert.False(cache.TryGetReleaseByVersion("o", "r", "1.0.0", out var release));
            Assert.Null(release);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void HasDownloadedSymbols_ReturnsFalseWhenDirectoryMissing()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var cache = new GitHubReleasesCache(tempDir);
            var missing = Path.Combine(tempDir, "missing");
            cache.SetDownloadedSymbols("1.0.0", "linux-x64", missing);

            Assert.False(cache.HasDownloadedSymbols("1.0.0", "linux-x64", out var dir));
            // Out param returns the cached directory even if it no longer exists.
            Assert.Equal(missing, dir);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
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
