using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DebuggerMcp.Security;

namespace DebuggerMcp.Tests.Security;

/// <summary>
/// Tests for ApiKeyAuthenticationHandler.
/// </summary>
public class ApiKeyAuthenticationHandlerTests : IDisposable
{
    private IHost? _host;
    private HttpClient? _client;

    public void Dispose()
    {
        _client?.Dispose();
        _host?.Dispose();
    }

    /// <summary>
    /// Creates a test host with the specified API key configuration.
    /// </summary>
    private async Task<HttpClient> CreateTestHost(string? apiKey)
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddControllers();

                    // Configure API key authentication with specified key
                    services.AddAuthentication(ApiKeyAuthenticationOptions.SchemeName)
                        .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                            ApiKeyAuthenticationOptions.SchemeName,
                            options => { options.ApiKey = apiKey; });

                    services.AddAuthorization();
                });

                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();

                    app.UseEndpoints(endpoints =>
                    {
                        // Protected endpoint that requires authentication
                        endpoints.MapGet("/protected", () => Results.Ok(new { message = "Success" }))
                            .RequireAuthorization();

                        // Public endpoint (no auth required)
                        endpoints.MapGet("/public", () => Results.Ok(new { message = "Public" }));
                    });
                });
            });

        _host = await builder.StartAsync();
        _client = _host.GetTestClient();
        return _client;
    }

    // ========== No API Key Configured (Anonymous Access Allowed) ==========

    [Fact]
    public async Task NoApiKeyConfigured_ProtectedEndpoint_ReturnsOk()
    {
        // Arrange - No API key configured means authentication is DISABLED
        // All requests should be allowed, including protected endpoints
        var client = await CreateTestHost(null);

        // Act
        var response = await client.GetAsync("/protected");

        // Assert - When no API key is configured, authentication is disabled
        // and all requests are allowed (anonymous users get authenticated identity)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NoApiKeyConfigured_PublicEndpoint_ReturnsOk()
    {
        // Arrange
        var client = await CreateTestHost(null);

        // Act
        var response = await client.GetAsync("/public");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EmptyApiKeyConfigured_ProtectedEndpoint_ReturnsOk()
    {
        // Arrange - Empty string is treated as no API key (authentication disabled)
        var client = await CreateTestHost("");

        // Act
        var response = await client.GetAsync("/protected");

        // Assert - Empty API key means authentication is disabled, all requests allowed
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ========== API Key Configured, Missing Header ==========

    [Fact]
    public async Task ApiKeyConfigured_NoHeader_ReturnsUnauthorized()
    {
        // Arrange
        var client = await CreateTestHost("test-api-key-12345");

        // Act - No X-API-Key header
        var response = await client.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ========== API Key Configured, Wrong Header ==========

    [Fact]
    public async Task ApiKeyConfigured_WrongKey_ReturnsUnauthorized()
    {
        // Arrange
        var client = await CreateTestHost("correct-api-key");
        client.DefaultRequestHeaders.Add("X-API-Key", "wrong-api-key");

        // Act
        var response = await client.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyConfigured_SimilarKey_ReturnsUnauthorized()
    {
        // Arrange - Test with a key that's similar but not exact
        var client = await CreateTestHost("my-secret-key");
        client.DefaultRequestHeaders.Add("X-API-Key", "my-secret-key1"); // Extra character

        // Act
        var response = await client.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyConfigured_ShorterKey_ReturnsUnauthorized()
    {
        // Arrange - Test with a shorter key
        var client = await CreateTestHost("my-secret-key-long");
        client.DefaultRequestHeaders.Add("X-API-Key", "my-secret");

        // Act
        var response = await client.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyConfigured_LongerKey_ReturnsUnauthorized()
    {
        // Arrange - Test with a longer key
        var client = await CreateTestHost("short");
        client.DefaultRequestHeaders.Add("X-API-Key", "short-but-extended-with-more");

        // Act
        var response = await client.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyConfigured_EmptyProvidedKey_ReturnsUnauthorized()
    {
        // Arrange
        var client = await CreateTestHost("valid-api-key");
        client.DefaultRequestHeaders.Add("X-API-Key", "");

        // Act
        var response = await client.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ========== API Key Configured, Correct Header ==========

    [Fact]
    public async Task ApiKeyConfigured_CorrectKey_ReturnsOk()
    {
        // Arrange
        var client = await CreateTestHost("my-secret-api-key");
        client.DefaultRequestHeaders.Add("X-API-Key", "my-secret-api-key");

        // Act
        var response = await client.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyConfigured_CorrectKey_PublicEndpoint_ReturnsOk()
    {
        // Arrange - Verify that public endpoints still work with API key
        var client = await CreateTestHost("my-secret-api-key");
        client.DefaultRequestHeaders.Add("X-API-Key", "my-secret-api-key");

        // Act
        var response = await client.GetAsync("/public");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyConfigured_CorrectLongKey_ReturnsOk()
    {
        // Arrange - Test with a long key
        var longKey = new string('x', 256);
        var client = await CreateTestHost(longKey);
        client.DefaultRequestHeaders.Add("X-API-Key", longKey);

        // Act
        var response = await client.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyConfigured_CorrectSpecialCharKey_ReturnsOk()
    {
        // Arrange - Test with special characters
        var specialKey = "key-with-$pecial!chars@123";
        var client = await CreateTestHost(specialKey);
        client.DefaultRequestHeaders.Add("X-API-Key", specialKey);

        // Act
        var response = await client.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyConfigured_CorrectUnicodeKey_ReturnsOk()
    {
        // Arrange - Test with Unicode characters
        var unicodeKey = "key-with-émoji-🔐-and-日本語";
        var client = await CreateTestHost(unicodeKey);
        client.DefaultRequestHeaders.Add("X-API-Key", unicodeKey);

        // Act
        var response = await client.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ========== Case Sensitivity ==========

    [Fact]
    public async Task ApiKeyConfigured_DifferentCase_ReturnsUnauthorized()
    {
        // Arrange - API keys should be case-sensitive
        var client = await CreateTestHost("MySecretKey");
        client.DefaultRequestHeaders.Add("X-API-Key", "mysecretkey");

        // Act
        var response = await client.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ========== Header Name Tests ==========

    [Fact]
    public async Task ApiKeyConfigured_WrongHeaderName_ReturnsUnauthorized()
    {
        // Arrange
        var client = await CreateTestHost("valid-key");
        client.DefaultRequestHeaders.Add("Authorization", "valid-key"); // Wrong header name

        // Act
        var response = await client.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ========== Multiple Requests ==========

    [Fact]
    public async Task MultipleRequests_SameKey_AllSucceed()
    {
        // Arrange
        var client = await CreateTestHost("persistent-key");
        client.DefaultRequestHeaders.Add("X-API-Key", "persistent-key");

        // Act & Assert - Multiple requests should all work
        for (int i = 0; i < 5; i++)
        {
            var response = await client.GetAsync("/protected");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    // ========== Constant Name Tests ==========

    [Fact]
    public void ApiKeyHeaderName_IsCorrect()
    {
        // Assert
        Assert.Equal("X-API-Key", ApiKeyAuthenticationHandler.ApiKeyHeaderName);
    }

    [Fact]
    public void SchemeName_IsCorrect()
    {
        // Assert
        Assert.Equal("ApiKey", ApiKeyAuthenticationOptions.SchemeName);
    }

    // ========== Options Tests ==========

    [Fact]
    public void ApiKeyAuthenticationOptions_DefaultApiKey_IsNull()
    {
        // Arrange & Act
        var options = new ApiKeyAuthenticationOptions();

        // Assert
        Assert.Null(options.ApiKey);
    }

    [Fact]
    public void ApiKeyAuthenticationOptions_CanSetApiKey()
    {
        // Arrange
        var options = new ApiKeyAuthenticationOptions();

        // Act
        options.ApiKey = "test-key";

        // Assert
        Assert.Equal("test-key", options.ApiKey);
    }

    // ========== Extension Method Tests ==========

    [Fact]
    public void AddApiKeyAuthentication_RegistersServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddApiKeyAuthentication(options => options.ApiKey = "test");

        // Assert
        var provider = services.BuildServiceProvider();
        var authOptions = provider.GetService<Microsoft.Extensions.Options.IOptionsMonitor<ApiKeyAuthenticationOptions>>();
        Assert.NotNull(authOptions);
    }

    [Fact]
    public void AddApiKeyAuthentication_WithNullConfigure_UsesDefaults()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act - Pass null for configure action
        services.AddApiKeyAuthentication(null);

        // Assert
        var provider = services.BuildServiceProvider();
        var authOptions = provider.GetService<Microsoft.Extensions.Options.IOptionsMonitor<ApiKeyAuthenticationOptions>>();
        Assert.NotNull(authOptions);
    }
}

