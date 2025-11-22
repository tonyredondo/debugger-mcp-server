using Xunit;
using DebuggerMcp.SourceLink;
using DebuggerMcp.Analysis;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DebuggerMcp.Tests.SourceLink;

public class GitHubCommitResolverTests
{
    // ============================================================
    // URL PARSING TESTS
    // ============================================================

    [Theory]
    [InlineData("https://github.com/DataDog/dd-trace-dotnet", "DataDog/dd-trace-dotnet")]
    [InlineData("https://github.com/DataDog/dd-trace-dotnet.git", "DataDog/dd-trace-dotnet")]
    [InlineData("https://github.com/dotnet/aspnetcore/tree/abc123", "dotnet/aspnetcore")]
    [InlineData("git@github.com:owner/repo.git", "owner/repo")]
    [InlineData("https://github.com/Microsoft/TypeScript", "Microsoft/TypeScript")]
    [InlineData("https://github.com/user/repo?branch=main", "user/repo")]
    [InlineData("https://github.com/user/repo#readme", "user/repo")]
    [InlineData("https://github.com/user/repo/issues/123", "user/repo")]
    public void ExtractGitHubOwnerRepo_ParsesValidGitHubUrls(string url, string expected)
    {
        var result = GitHubCommitResolver.ExtractGitHubOwnerRepo(url);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://dev.azure.com/org/project")]
    [InlineData("https://bitbucket.org/user/repo")]
    [InlineData("https://example.com/owner/repo")]
    [InlineData("")]
    [InlineData(null)]
    public void ExtractGitHubOwnerRepo_ReturnsNullForNonGitHubUrls(string? url)
    {
        var result = GitHubCommitResolver.ExtractGitHubOwnerRepo(url);
        Assert.Null(result);
    }

    // ============================================================
    // SOURCE URL RESOLUTION TESTS
    // ============================================================

    [Fact]
    public void ResolveSourceUrl_UsesSourceCommitUrlWhenAvailable()
    {
        // Arrange
        using var resolver = new GitHubCommitResolver();
        var assembly = new AssemblyVersionInfo
        {
            CommitHash = "abc123",
            RepositoryUrl = "https://github.com/owner/repo",
            CustomAttributes = new Dictionary<string, string>
            {
                ["SourceCommitUrl"] = "https://github.com/owner/repo/tree/abc123"
            }
        };

        // Act
        var result = resolver.ResolveSourceUrl(assembly);

        // Assert
        Assert.Equal("https://github.com/owner/repo/tree/abc123", result);
    }

    [Fact]
    public void ResolveSourceUrl_ConstructsFromRepoAndHash()
    {
        // Arrange
        using var resolver = new GitHubCommitResolver();
        var assembly = new AssemblyVersionInfo
        {
            CommitHash = "abc123def456",
            RepositoryUrl = "https://github.com/DataDog/dd-trace-dotnet.git"
        };

        // Act
        var result = resolver.ResolveSourceUrl(assembly);

        // Assert
        Assert.Equal("https://github.com/DataDog/dd-trace-dotnet/tree/abc123def456", result);
    }

    [Fact]
    public void ResolveSourceUrl_ReturnsNullForNonGitHub()
    {
        // Arrange
        using var resolver = new GitHubCommitResolver();
        var assembly = new AssemblyVersionInfo
        {
            CommitHash = "abc123",
            RepositoryUrl = "https://gitlab.com/owner/repo"
        };

        // Act
        var result = resolver.ResolveSourceUrl(assembly);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveSourceUrl_ReturnsNullWhenMissingCommitHash()
    {
        // Arrange
        using var resolver = new GitHubCommitResolver();
        var assembly = new AssemblyVersionInfo
        {
            RepositoryUrl = "https://github.com/owner/repo"
        };

        // Act
        var result = resolver.ResolveSourceUrl(assembly);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveSourceUrl_ReturnsNullWhenMissingRepositoryUrl()
    {
        // Arrange
        using var resolver = new GitHubCommitResolver();
        var assembly = new AssemblyVersionInfo
        {
            CommitHash = "abc123"
        };

        // Act
        var result = resolver.ResolveSourceUrl(assembly);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveSourceUrl_PrefersSourceCommitUrlOverConstructed()
    {
        // Arrange
        using var resolver = new GitHubCommitResolver();
        var assembly = new AssemblyVersionInfo
        {
            CommitHash = "abc123",
            RepositoryUrl = "https://github.com/owner/repo",
            CustomAttributes = new Dictionary<string, string>
            {
                ["SourceCommitUrl"] = "https://github.com/different/url/tree/xyz789"
            }
        };

        // Act
        var result = resolver.ResolveSourceUrl(assembly);

        // Assert
        Assert.Equal("https://github.com/different/url/tree/xyz789", result);
    }

    // ============================================================
    // CACHE TESTS
    // ============================================================

    [Fact]
    public async Task FetchCommitInfo_UsesCacheOnSecondCall()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.SetupResponse(HttpStatusCode.OK, GetSampleCommitJson());
        var httpClient = new HttpClient(mockHandler);

        using var resolver = new GitHubCommitResolver(null, null, null, httpClient);

        // Act
        var result1 = await resolver.FetchCommitInfoAsync("owner/repo", "abc123");
        var result2 = await resolver.FetchCommitInfoAsync("owner/repo", "abc123");

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal(result1.AuthorName, result2.AuthorName);
        Assert.Equal(1, mockHandler.CallCount); // Only one HTTP call
    }

    [Fact]
    public async Task FetchCommitInfo_ParsesGitHubResponse()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.SetupResponse(HttpStatusCode.OK, GetSampleCommitJson());
        var httpClient = new HttpClient(mockHandler);

        using var resolver = new GitHubCommitResolver(null, null, null, httpClient);

        // Act
        var result = await resolver.FetchCommitInfoAsync("DataDog/dd-trace-dotnet", "14fd3a2fe0bdd94e16a806c46855652049073798");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("14fd3a2fe0bdd94e16a806c46855652049073798", result.Sha);
        Assert.Equal("andrewlock", result.AuthorName);
        Assert.Equal("2025-11-03T09:57:02Z", result.AuthorDate);
        Assert.Equal("github-actions[bot]", result.CommitterName);
        Assert.Equal("2025-11-03T09:57:02Z", result.CommitterDate);
        Assert.Equal("[Version Bump] 3.31.0", result.Message);
        Assert.Equal("https://github.com/DataDog/dd-trace-dotnet/tree/14fd3a2fe0bdd94e16a806c46855652049073798", result.TreeUrl);
    }

    [Fact]
    public async Task FetchCommitInfo_ReturnsNullFor404()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.SetupResponse(HttpStatusCode.NotFound, "{}");
        var httpClient = new HttpClient(mockHandler);

        using var resolver = new GitHubCommitResolver(null, null, null, httpClient);

        // Act
        var result = await resolver.FetchCommitInfoAsync("owner/repo", "nonexistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task FetchCommitInfo_Caches404ToAvoidRepeatedCalls()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.SetupResponse(HttpStatusCode.NotFound, "{}");
        var httpClient = new HttpClient(mockHandler);

        using var resolver = new GitHubCommitResolver(null, null, null, httpClient);

        // Act
        var result1 = await resolver.FetchCommitInfoAsync("owner/repo", "nonexistent");
        var result2 = await resolver.FetchCommitInfoAsync("owner/repo", "nonexistent");

        // Assert
        Assert.Null(result1);
        Assert.Null(result2);
        Assert.Equal(1, mockHandler.CallCount); // Only one HTTP call
    }

    [Fact]
    public async Task FetchCommitInfo_TruncatesLongMessages()
    {
        // Arrange
        var longMessage = new string('x', 2000);
        var json = GetSampleCommitJson(message: longMessage);
        
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.SetupResponse(HttpStatusCode.OK, json);
        var httpClient = new HttpClient(mockHandler);

        using var resolver = new GitHubCommitResolver(null, null, null, httpClient);

        // Act
        var result = await resolver.FetchCommitInfoAsync("owner/repo", "abc123");

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Message);
        Assert.True(result.Message.Length <= 1003); // 1000 + "..."
        Assert.EndsWith("...", result.Message);
    }

    [Fact]
    public async Task FetchCommitInfo_CaseInsensitiveCacheKey()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.SetupResponse(HttpStatusCode.OK, GetSampleCommitJson());
        var httpClient = new HttpClient(mockHandler);

        using var resolver = new GitHubCommitResolver(null, null, null, httpClient);

        // Act
        var result1 = await resolver.FetchCommitInfoAsync("Owner/Repo", "ABC123");
        var result2 = await resolver.FetchCommitInfoAsync("owner/repo", "abc123");

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal(1, mockHandler.CallCount); // Same cache key
    }

    // ============================================================
    // RATE LIMIT TESTS
    // ============================================================

    [Fact]
    public void GetRateLimitStatus_ReturnsDefaults()
    {
        // Arrange
        using var resolver = new GitHubCommitResolver();

        // Act
        var (remaining, resetTime) = resolver.GetRateLimitStatus();

        // Assert
        Assert.Equal(60, remaining);
        Assert.Equal(DateTime.MinValue, resetTime);
    }

    [Fact]
    public async Task FetchCommitInfo_UpdatesRateLimitFromHeaders()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.SetupResponse(HttpStatusCode.OK, GetSampleCommitJson());
        var httpClient = new HttpClient(mockHandler);

        using var resolver = new GitHubCommitResolver(null, null, null, httpClient);

        // Act
        await resolver.FetchCommitInfoAsync("owner/repo", "abc123");
        var (remaining, _) = resolver.GetRateLimitStatus();

        // Assert - mock handler sets remaining to 59
        Assert.Equal(59, remaining);
    }

    [Fact]
    public async Task FetchCommitInfo_ReturnsNullWhenRateLimitExceeded()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.SetupRateLimitExceeded();
        var httpClient = new HttpClient(mockHandler);

        using var resolver = new GitHubCommitResolver(null, null, null, httpClient);

        // First call - this sets rate limit to 0
        await resolver.FetchCommitInfoAsync("owner/repo", "first");
        
        // Second call should be blocked by rate limit check
        var result = await resolver.FetchCommitInfoAsync("owner/repo2", "second");

        // Assert - second call should return null without making HTTP request
        Assert.Null(result);
        Assert.Equal(1, mockHandler.CallCount); // Only one HTTP call made
    }

    [Fact]
    public async Task FetchCommitInfo_Handles403RateLimit()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.SetupResponse(HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}");
        mockHandler.SetupRateLimitHeaders(0, DateTime.UtcNow.AddHours(1));
        var httpClient = new HttpClient(mockHandler);

        using var resolver = new GitHubCommitResolver(null, null, null, httpClient);

        // Act
        var result = await resolver.FetchCommitInfoAsync("owner/repo", "abc123");

        // Assert
        Assert.Null(result);
    }

    // ============================================================
    // CACHE PERSISTENCE TESTS
    // ============================================================

    [Fact]
    public void SaveCache_DoesNotThrowWhenNoCacheDirectory()
    {
        // Arrange
        using var resolver = new GitHubCommitResolver(null, null);

        // Act & Assert (should not throw)
        resolver.SaveCache();
    }

    [Fact]
    public async Task FetchCommitInfo_PersistsAndLoadsCacheFromFile()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"github_cache_test_{Guid.NewGuid()}");
        
        try
        {
            var mockHandler = new MockHttpMessageHandler();
            mockHandler.SetupResponse(HttpStatusCode.OK, GetSampleCommitJson());
            var httpClient = new HttpClient(mockHandler);

            // First resolver - fetch and save
            using (var resolver1 = new GitHubCommitResolver(tempDir, null, null, httpClient))
            {
                var result = await resolver1.FetchCommitInfoAsync("owner/repo", "abc123");
                Assert.NotNull(result);
                resolver1.SaveCache();
            }

            // Verify cache file exists
            var cachePath = Path.Combine(tempDir, "github_commit_cache.json");
            Assert.True(File.Exists(cachePath));

            // Second resolver - load from cache (with different http client that would fail)
            var failHandler = new MockHttpMessageHandler();
            failHandler.SetupResponse(HttpStatusCode.InternalServerError, "error");
            var failClient = new HttpClient(failHandler);

            using (var resolver2 = new GitHubCommitResolver(tempDir, null, null, failClient))
            {
                var result = await resolver2.FetchCommitInfoAsync("owner/repo", "abc123");
                
                // Should get cached result, not call HTTP
                Assert.NotNull(result);
                Assert.Equal("andrewlock", result.AuthorName);
                Assert.Equal(0, failHandler.CallCount);
            }
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    // ============================================================
    // ERROR HANDLING TESTS
    // ============================================================

    [Fact]
    public async Task FetchCommitInfo_HandlesNetworkError()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.SetupException(new HttpRequestException("Network error"));
        var httpClient = new HttpClient(mockHandler);

        using var resolver = new GitHubCommitResolver(null, null, null, httpClient);

        // Act
        var result = await resolver.FetchCommitInfoAsync("owner/repo", "abc123");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task FetchCommitInfo_HandlesMalformedJson()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.SetupResponse(HttpStatusCode.OK, "{ invalid json }");
        var httpClient = new HttpClient(mockHandler);

        using var resolver = new GitHubCommitResolver(null, null, null, httpClient);

        // Act
        var result = await resolver.FetchCommitInfoAsync("owner/repo", "abc123");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task FetchCommitInfo_HandlesMissingProperties()
    {
        // Arrange
        var json = """
        {
            "sha": "abc123",
            "commit": {}
        }
        """;
        
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.SetupResponse(HttpStatusCode.OK, json);
        var httpClient = new HttpClient(mockHandler);

        using var resolver = new GitHubCommitResolver(null, null, null, httpClient);

        // Act
        var result = await resolver.FetchCommitInfoAsync("owner/repo", "abc123");

        // Assert - should handle gracefully
        Assert.Null(result);
    }

    // ============================================================
    // HELPER METHODS
    // ============================================================

    private static string GetSampleCommitJson(string message = "[Version Bump] 3.31.0")
    {
        return $$"""
        {
            "sha": "14fd3a2fe0bdd94e16a806c46855652049073798",
            "commit": {
                "author": {
                    "name": "andrewlock",
                    "email": "andrew@example.com",
                    "date": "2025-11-03T09:57:02Z"
                },
                "committer": {
                    "name": "github-actions[bot]",
                    "email": "41898282+github-actions[bot]@users.noreply.github.com",
                    "date": "2025-11-03T09:57:02Z"
                },
                "message": "{{message.Replace("\"", "\\\"")}}"
            },
            "url": "https://api.github.com/repos/DataDog/dd-trace-dotnet/commits/14fd3a2fe0bdd94e16a806c46855652049073798"
        }
        """;
    }
}

/// <summary>
/// Mock HTTP message handler for testing.
/// </summary>
public class MockHttpMessageHandler : HttpMessageHandler
{
    private HttpStatusCode _statusCode = HttpStatusCode.OK;
    private string _content = "{}";
    private Exception? _exception;
    private int _rateLimitRemaining = 59;
    private DateTime _rateLimitReset = DateTime.UtcNow.AddHours(1);

    public int CallCount { get; private set; }

    public void SetupResponse(HttpStatusCode statusCode, string content)
    {
        _statusCode = statusCode;
        _content = content;
        _exception = null;
    }

    public void SetupException(Exception exception)
    {
        _exception = exception;
    }

    public void SetupRateLimitHeaders(int remaining, DateTime reset)
    {
        _rateLimitRemaining = remaining;
        _rateLimitReset = reset;
    }

    public void SetupRateLimitExceeded()
    {
        _statusCode = HttpStatusCode.Forbidden;
        _content = "{\"message\":\"API rate limit exceeded\"}";
        _rateLimitRemaining = 0;
        _rateLimitReset = DateTime.UtcNow.AddHours(1);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;

        if (_exception != null)
        {
            throw _exception;
        }

        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_content, Encoding.UTF8, "application/json")
        };

        // Add rate limit headers
        response.Headers.Add("X-RateLimit-Remaining", _rateLimitRemaining.ToString());
        response.Headers.Add("X-RateLimit-Reset", new DateTimeOffset(_rateLimitReset).ToUnixTimeSeconds().ToString());

        return Task.FromResult(response);
    }
}

