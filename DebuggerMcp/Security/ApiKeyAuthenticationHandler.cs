using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using DebuggerMcp.Configuration;

namespace DebuggerMcp.Security;

/// <summary>
/// Authentication handler that validates API keys from request headers.
/// </summary>
/// <remarks>
/// API key authentication is optional and only enabled when the API_KEY environment variable is set.
/// When enabled, requests must include the X-API-Key header with a valid key.
/// See <see cref="EnvironmentConfig"/> for all configuration options.
/// </remarks>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    /// <summary>
    /// The header name where the API key should be provided.
    /// </summary>
    public const string ApiKeyHeaderName = "X-API-Key";

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiKeyAuthenticationHandler"/> class.
    /// </summary>
    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <summary>
    /// Handles the authentication by validating the API key from the request header.
    /// </summary>
    /// <returns>An authentication result.</returns>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Get the configured API key
        var configuredApiKey = Options.ApiKey;

        // If no API key is configured, allow all requests (authentication disabled)
        if (string.IsNullOrEmpty(configuredApiKey))
        {
            Logger.LogDebug("API key authentication is disabled (no API_KEY configured)");
            // Create an authenticated identity for anonymous users
            // The second parameter (authenticationType) must be non-null for IsAuthenticated to return true
            var anonymousClaims = new[]
            {
                new Claim(ClaimTypes.Name, "Anonymous"),
                new Claim("AuthMethod", "None")
            };
            var anonymousIdentity = new ClaimsIdentity(anonymousClaims, Scheme.Name);
            var anonymousPrincipal = new ClaimsPrincipal(anonymousIdentity);
            var anonymousTicket = new AuthenticationTicket(anonymousPrincipal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(anonymousTicket));
        }

        // Check if the request has the API key header
        if (!Request.Headers.TryGetValue(ApiKeyHeaderName, out var providedApiKey))
        {
            return Task.FromResult(AuthenticateResult.Fail($"Missing {ApiKeyHeaderName} header"));
        }

        // Validate the API key using constant-time comparison to prevent timing attacks
        if (!ConstantTimeEquals(configuredApiKey, providedApiKey.ToString()))
        {
            Logger.LogWarning("Invalid API key provided from {RemoteIp}", Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        }

        // Create the authenticated identity
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "ApiKeyUser"),
            new Claim("AuthMethod", "ApiKey")
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        Logger.LogDebug("API key authentication successful");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>
    /// Constant-time string comparison to prevent timing attacks.
    /// Uses CryptographicOperations.FixedTimeEquals which doesn't leak length information.
    /// </summary>
    private static bool ConstantTimeEquals(string expected, string provided)
    {
        // Convert to UTF8 bytes for constant-time comparison
        // Always compute both byte arrays to avoid leaking which string is longer
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided);

        // If lengths differ, we still need to do a constant-time comparison
        // to avoid leaking length information. We compare the provided bytes
        // against a padding of the expected bytes length.
        if (expectedBytes.Length != providedBytes.Length)
        {
            // Use expected bytes as base for comparison to maintain constant time
            // The comparison will fail, but timing won't reveal which string is longer
            _ = CryptographicOperations.FixedTimeEquals(
                expectedBytes,
                expectedBytes.Length <= providedBytes.Length
                    ? providedBytes.AsSpan(0, expectedBytes.Length)
                    : new byte[expectedBytes.Length]);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}

/// <summary>
/// Options for API key authentication.
/// </summary>
public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// The authentication scheme name.
    /// </summary>
    public const string SchemeName = "ApiKey";

    /// <summary>
    /// Gets or sets the API key to validate against.
    /// </summary>
    public string? ApiKey { get; set; }
}

/// <summary>
/// Extension methods for configuring API key authentication.
/// </summary>
public static class ApiKeyAuthenticationExtensions
{
    /// <summary>
    /// Adds API key authentication to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional action to configure options.</param>
    /// <returns>The authentication builder.</returns>
    public static AuthenticationBuilder AddApiKeyAuthentication(
        this IServiceCollection services,
        Action<ApiKeyAuthenticationOptions>? configureOptions = null)
    {
        return services.AddAuthentication(ApiKeyAuthenticationOptions.SchemeName)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationOptions.SchemeName,
                configureOptions ?? (options =>
                {
                    // Default: read from centralized configuration
                    options.ApiKey = EnvironmentConfig.GetApiKey();
                }));
    }
}

