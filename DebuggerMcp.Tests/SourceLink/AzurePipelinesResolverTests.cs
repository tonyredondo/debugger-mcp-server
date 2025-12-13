using DebuggerMcp.Analysis;
using DebuggerMcp.SourceLink;
using System.Net;
using System.Text;

namespace DebuggerMcp.Tests.SourceLink;

/// <summary>
/// Tests for AzurePipelinesResolver.
/// </summary>
public class AzurePipelinesResolverTests
{
    [Fact]
    public void Constructor_WithNullCacheDirectory_UsesDefault()
    {
        // Arrange & Act
        using var resolver = new AzurePipelinesResolver(null, null);

        // Assert - no exception thrown
        Assert.NotNull(resolver);
    }

    [Fact]
    public void Constructor_WithCacheDirectory_StoresIt()
    {
        // Arrange
        var cacheDir = "/tmp/test-cache";

        // Act
        using var resolver = new AzurePipelinesResolver(cacheDir, null);

        // Assert - no exception thrown
        Assert.NotNull(resolver);
    }

    [Fact]
    public async Task FindBuildByCommitAsync_WithEmptyCommit_ReturnsNull()
    {
        // Arrange
        using var resolver = new AzurePipelinesResolver(null, null);

        // Act
        var result = await resolver.FindBuildByCommitAsync("org", "project", "");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task FindBuildByCommitAsync_WithNullCommit_ReturnsNull()
    {
        // Arrange
        using var resolver = new AzurePipelinesResolver(null, null);

        // Act
        var result = await resolver.FindBuildByCommitAsync("org", "project", null!);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task FindBuildByCommitAsync_WithMissingOrgOrProject_ReturnsNull_WithoutNetworkCall()
    {
        // Arrange
        var handler = new CountingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        using var resolver = new AzurePipelinesResolver(null, null, httpClient);

        // Act
        var result = await resolver.FindBuildByCommitAsync("", "project", "abc");

        // Assert
        Assert.Null(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ListArtifactsAsync_WithInvalidInputs_ReturnsEmpty_WithoutNetworkCall()
    {
        // Arrange
        var handler = new CountingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        using var resolver = new AzurePipelinesResolver(null, null, httpClient);

        // Act
        var result = await resolver.ListArtifactsAsync("org", "project", buildId: 0);

        // Assert
        Assert.Empty(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task DownloadArtifactAsync_WithInvalidInputs_ReturnsNull_WithoutNetworkCall()
    {
        // Arrange
        var handler = new CountingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        using var resolver = new AzurePipelinesResolver(null, null, httpClient);

        // Act
        var result = await resolver.DownloadArtifactAsync("org", "project", buildId: 0, artifactName: "a", outputDirectory: "out");

        // Assert
        Assert.Null(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ListArtifactsAsync_WithInvalidBuildId_ReturnsEmptyList()
    {
        // Arrange
        using var resolver = new AzurePipelinesResolver(null, null);

        // Act
        var result = await resolver.ListArtifactsAsync("org", "project", -1);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void SaveCache_DoesNotThrow()
    {
        // Arrange
        using var resolver = new AzurePipelinesResolver(null, null);

        // Act & Assert - no exception
        resolver.SaveCache();
    }

    [Fact]
    public void Dispose_MultipleTimesDoesNotThrow()
    {
        // Arrange
        var resolver = new AzurePipelinesResolver(null, null);

        // Act & Assert - no exception
        resolver.Dispose();
        resolver.Dispose();
    }

    [Theory]
    [InlineData("", "", "commit")]
    [InlineData("org", "", "commit")]
    [InlineData("", "project", "commit")]
    public async Task FindBuildByCommitAsync_WithMissingOrgOrProject_ReturnsNull(string org, string project, string commit)
    {
        // Arrange
        using var resolver = new AzurePipelinesResolver(null, null);

        // Act
        var result = await resolver.FindBuildByCommitAsync(org, project, commit);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task FindBuildByCommitAsync_WhenCachedNullAndVersionProvided_SkipsCommitLookupAndUsesTagLookup()
    {
        // Arrange
        var cacheDir = Path.Combine(Path.GetTempPath(), $"az-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheDir);

        try
        {
            var cachePath = Path.Combine(cacheDir, "azure_pipelines_cache.json");
            var json = """
{
  "lastUpdated": "2025-12-12T00:00:00Z",
  "builds": {
    "org/project/abc": {
      "cachedAt": "2099-01-01T00:00:00Z",
      "build": null
    }
  },
  "artifacts": {},
  "downloadedSymbols": {}
}
""";
            File.WriteAllText(cachePath, json);

            var handler = new CountingHttpMessageHandler(req =>
            {
                if (req.RequestUri?.Query.Contains("branchName=refs%2Ftags%2Fv1.2.3", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var payload = """
{ "value": [ { "id": 123, "buildNumber": "b", "status": "completed", "result": "succeeded", "sourceVersion": "abc" } ] }
""";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            using var httpClient = new HttpClient(handler);
            using var resolver = new AzurePipelinesResolver(cacheDir, null, httpClient);

            // Act
            var build = await resolver.FindBuildByCommitAsync("org", "project", "abc", version: "1.2.3");

            // Assert
            Assert.NotNull(build);
            Assert.Equal(123, build.Id);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task FindBuildByCommitAsync_WhenBuildMatchesCommit_ParsesAndReturnsBuild()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"az-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheDir);

        try
        {
            const string commit = "abcdef1234567890";

            var handler = new CountingHttpMessageHandler(_ =>
            {
                var payload = $$"""
{ "value": [
  { "id": 1, "buildNumber": "b1", "status": "completed", "result": "succeeded", "sourceVersion": "0000000", "sourceBranch": "refs/heads/main" },
  { "id": 2, "buildNumber": "b2", "status": "completed", "result": "succeeded", "sourceVersion": "{{commit}}", "sourceBranch": "refs/heads/main",
    "_links": { "web": { "href": "https://example.invalid/build/2" } } }
] }
""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            });

            using var httpClient = new HttpClient(handler);
            using var resolver = new AzurePipelinesResolver(cacheDir, null, httpClient);

            var build = await resolver.FindBuildByCommitAsync("org", "project", commit);

            Assert.NotNull(build);
            Assert.Equal(2, build!.Id);
            Assert.Equal("b2", build.BuildNumber);
            Assert.Equal(commit, build.SourceVersion);
            Assert.Equal("https://example.invalid/build/2", build.WebUrl);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task FindBuildByCommitAsync_WhenNoExactSourceVersionMatch_ReturnsNull()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"az-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheDir);

        try
        {
            var handler = new CountingHttpMessageHandler(_ =>
            {
                var payload = """
{ "value": [ { "id": 1, "buildNumber": "b1", "status": "completed", "result": "succeeded", "sourceVersion": "different" } ] }
""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            });

            using var httpClient = new HttpClient(handler);
            using var resolver = new AzurePipelinesResolver(cacheDir, null, httpClient);

            var build = await resolver.FindBuildByCommitAsync("org", "project", commitSha: "expected");

            Assert.Null(build);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ListArtifactsAsync_WhenResponseContainsArtifacts_ParsesArtifacts()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"az-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheDir);

        try
        {
            var handler = new CountingHttpMessageHandler(_ =>
            {
                var payload = """
{
  "value": [
    {
      "id": 10,
      "name": "symbols",
      "resource": {
        "downloadUrl": "https://example.invalid/artifacts/symbols.zip",
        "type": "container"
      }
    }
  ]
}
""";

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            });

            using var httpClient = new HttpClient(handler);
            using var resolver = new AzurePipelinesResolver(cacheDir, null, httpClient);

            var artifacts = await resolver.ListArtifactsAsync("org", "project", buildId: 123);

            Assert.Single(artifacts);
            Assert.Equal(10, artifacts[0].Id);
            Assert.Equal("symbols", artifacts[0].Name);
            Assert.Equal("https://example.invalid/artifacts/symbols.zip", artifacts[0].DownloadUrl);
            Assert.Equal("container", artifacts[0].ResourceType);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}

internal sealed class CountingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    public int CallCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(handler(request));
    }
}
