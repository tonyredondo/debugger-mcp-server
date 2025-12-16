using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading.RateLimiting;
using DebuggerMcp.Configuration;
using DebuggerMcp.Watches;

namespace DebuggerMcp;

/// <summary>
/// Extension methods for configuring debugger services in the dependency injection container.
/// </summary>
/// <remarks>
/// This class provides a centralized location for all service registrations,
/// making Program.cs cleaner and the service configuration more testable.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds core debugger services to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="dumpStoragePath">The path where dump files are stored.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Registers:
    /// <list type="bullet">
    /// <item><description><see cref="DebuggerSessionManager"/> - Session management</description></item>
    /// <item><description><see cref="SymbolManager"/> - Symbol file management</description></item>
    /// <item><description><see cref="WatchStore"/> - Watch expression persistence</description></item>
    /// <item><description><see cref="SessionCleanupService"/> - Background cleanup</description></item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddDebuggerServices(this IServiceCollection services, string dumpStoragePath)
    {
        // Capture server start time at application startup, not at first request.
        services.AddSingleton(new ServerRuntimeInfo(DateTime.UtcNow));

        // Register session manager with the dump storage path and logger factory
        // Use a factory method to inject the ILoggerFactory from the service provider
        services.AddSingleton(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return new DebuggerSessionManager(dumpStoragePath, loggerFactory);
        });

        // Register symbol manager (depends on environment config)
        services.AddSingleton<SymbolManager>();

        // Register watch store with the dump storage path for persistence
        services.AddSingleton(new WatchStore(dumpStoragePath));

        // Register background service for session cleanup
        services.AddHostedService<SessionCleanupService>();

        return services;
    }

    /// <summary>
    /// Adds core debugger services using default paths from environment configuration.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Uses <see cref="EnvironmentConfig.GetDumpStoragePath"/> to determine the dump storage path.
    /// </remarks>
    public static IServiceCollection AddDebuggerServices(this IServiceCollection services)
    {
        return services.AddDebuggerServices(EnvironmentConfig.GetDumpStoragePath());
    }

    /// <summary>
    /// Adds rate limiting middleware to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="requestsPerMinute">Maximum requests per minute per client. Defaults to environment config value.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Configures a fixed window rate limiter that:
    /// <list type="bullet">
    /// <item><description>Limits by client IP address</description></item>
    /// <item><description>Uses a 1-minute window</description></item>
    /// <item><description>Allows a small queue for burst handling</description></item>
    /// <item><description>Returns 429 Too Many Requests when limit exceeded</description></item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddDebuggerRateLimiting(this IServiceCollection services, int? requestsPerMinute = null)
    {
        var limit = requestsPerMinute ?? EnvironmentConfig.GetRateLimit();

        services.AddRateLimiter(options =>
        {
            // Partition by client IP so one noisy client does not block others.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                httpContext => RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 5 // Allow small burst queuing for better UX.
                    }));

            // Return 429 when rate limit is exceeded to signal retry-after behavior to clients.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });

        return services;
    }

    /// <summary>
    /// Adds CORS configuration to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="allowedOrigins">Allowed origins. Defaults to environment config value.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// If no origins are configured:
    /// <list type="bullet">
    /// <item><description>Development: Allows any origin</description></item>
    /// <item><description>Production: Restricts to configured origins</description></item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddDebuggerCors(this IServiceCollection services, string[]? allowedOrigins = null)
    {
        var origins = allowedOrigins ?? EnvironmentConfig.GetCorsAllowedOrigins();

        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                // Check if specific origins are configured
                if (origins.Length > 0)
                {
                    // Production: restrict to explicit origins to prevent unintended cross-site access.
                    policy.WithOrigins(origins)
                          .AllowAnyMethod()
                          .AllowAnyHeader();
                }
                else
                {
                    // Development: allow any origin when no override is provided to simplify local use.
                    policy.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader();
                }
            });
        });

        return services;
    }

    /// <summary>
    /// Configures Kestrel server options for large file uploads.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="maxRequestBodySize">Maximum request body size in bytes. Defaults to environment config value.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Memory dumps can be very large (several GB), so this configures Kestrel
    /// to accept large uploads. The default is 5GB but can be configured via
    /// the MAX_REQUEST_BODY_SIZE_GB environment variable.
    ///
    /// NOTE: The dump upload controller also enforces the same limit to ensure
    /// consistent behavior across hosting environments.
    /// </remarks>
    public static IServiceCollection ConfigureKestrelForLargeUploads(this IServiceCollection services, long? maxRequestBodySize = null)
    {
        var maxSize = maxRequestBodySize ?? EnvironmentConfig.GetMaxRequestBodySize();

        services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
        {
            // Set maximum request body size for dump uploads
            options.Limits.MaxRequestBodySize = maxSize;
        });

        // Also configure FormOptions for multipart uploads (IFormFile)
        // Default ASP.NET Core limit is 128MB, but we need to match Kestrel's limit
        services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = maxSize;
        });

        return services;
    }
}
