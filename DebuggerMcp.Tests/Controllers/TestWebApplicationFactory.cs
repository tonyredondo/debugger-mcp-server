using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using DebuggerMcp.Security;
using DebuggerMcp.Watches;
using DebuggerMcp.Controllers;

namespace DebuggerMcp.Tests.Controllers;

/// <summary>
/// Custom test factory that creates a minimal HTTP application for controller testing.
/// </summary>
/// <remarks>
/// This factory builds a standalone test server without relying on the main Program.cs,
/// which uses conditional logic that doesn't work well with WebApplicationFactory.
/// </remarks>
public class TestWebApplicationFactory : IDisposable
{
    private readonly string _tempDir;
    private readonly IHost _host;
    private bool _disposed;

    /// <summary>
    /// Gets the path to the temporary directory used for test data.
    /// </summary>
    public string TempDirectory => _tempDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="TestWebApplicationFactory"/> class.
    /// </summary>
    public TestWebApplicationFactory()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DebuggerMcpTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);

        var builder = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    // Add MVC with controllers from the main assembly
                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                        {
                            // Ensure the controllers from the main assembly are loaded
                            var assembly = typeof(DumpController).Assembly;
                            var part = new AssemblyPart(assembly);
                            if (!manager.ApplicationParts.Any(p => p.Name == assembly.GetName().Name))
                            {
                                manager.ApplicationParts.Add(part);
                            }
                        });

                    services.AddEndpointsApiExplorer();

                    // Add session manager with test temp directory
                    services.AddSingleton(new DebuggerSessionManager(_tempDir));

                    // Add symbol manager
                    services.AddSingleton<SymbolManager>();

                    // Add watch store
                    services.AddSingleton(new WatchStore(_tempDir));

                    // Add authentication with anonymous access allowed (for testing)
                    services.AddAuthentication(ApiKeyAuthenticationOptions.SchemeName)
                        .AddScheme<ApiKeyAuthenticationOptions, TestApiKeyAuthenticationHandler>(
                            ApiKeyAuthenticationOptions.SchemeName,
                            options => { options.ApiKey = null; }); // No API key = anonymous access

                    services.AddAuthorization();

                    // Configure logging to suppress noise in tests
                    services.AddLogging(logging =>
                    {
                        logging.SetMinimumLevel(LogLevel.Warning);
                    });
                });

                webBuilder.Configure(app =>
                {
                    // Configure middleware pipeline
                    app.UseRouting();

                    // Use authentication and authorization
                    app.UseAuthentication();
                    app.UseAuthorization();

                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapControllers();

                        // Add health endpoint
                        endpoints.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
                    });
                });
            });

        _host = builder.Build();
        _host.Start();
    }

    /// <summary>
    /// Creates a new HTTP client for testing the application.
    /// Each call returns a new client to avoid disposal issues in tests.
    /// </summary>
    public HttpClient CreateClient()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TestWebApplicationFactory));
        }
        return _host.GetTestClient();
    }

    /// <summary>
    /// Disposes of resources used by the factory.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _host.Dispose();

        // Clean up temp directory
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}

/// <summary>
/// Test authentication handler that allows anonymous access by creating an authenticated identity.
/// </summary>
public class TestApiKeyAuthenticationHandler : ApiKeyAuthenticationHandler
{
    public TestApiKeyAuthenticationHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
    {
        // For testing, always create an authenticated identity (bypass auth)
        var claims = new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "TestUser"),
            new System.Security.Claims.Claim("AuthMethod", "TestBypass")
        };

        var identity = new System.Security.Claims.ClaimsIdentity(claims, Scheme.Name);
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);
        var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(ticket));
    }
}
