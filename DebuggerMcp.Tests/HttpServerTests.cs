using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using DebuggerMcp.McpTools;
using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for the HTTP server infrastructure.
/// </summary>
/// <remarks>
/// These tests verify that the service registration used by the HTTP host
/// can be applied without building or starting an actual web server.
/// </remarks>
public class HttpServerTests
{
    /// <summary>
    /// Verifies that the production service registration can be applied without
    /// requiring a full <see cref="Microsoft.AspNetCore.Builder.WebApplication"/>.
    /// </summary>
    [Fact]
    public void ServiceRegistration_CanBeApplied()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services
            .AddDebuggerServices(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()))
            .AddDebuggerRateLimiting()
            .AddDebuggerCors();

        // Assert
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<DebuggerSessionManager>());
    }

    /// <summary>
    /// Verifies that the MCP server can be configured with HTTP transport
    /// without requiring a hosted web application.
    /// </summary>
    [Fact]
    public void McpServer_ConfiguresWithHttpTransport()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDebuggerServices(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        // Act
        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools(new[] { typeof(SessionTools) });

        // Assert
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider);
    }

    /// <summary>
    /// Verifies that the DebuggerSessionManager is registered as a singleton.
    /// </summary>
    [Fact]
    public void SessionManager_IsRegisteredAsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDebuggerServices(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        using var provider = services.BuildServiceProvider();

        // Act
        var manager1 = provider.GetService<DebuggerSessionManager>();
        var manager2 = provider.GetService<DebuggerSessionManager>();

        // Assert
        Assert.NotNull(manager1);
        Assert.NotNull(manager2);
        Assert.Same(manager1, manager2); // Should be the same instance (singleton)
    }

    /// <summary>
    /// Verifies that large-upload defaults are applied to both Kestrel and form options.
    /// </summary>
    [Fact]
    public void LargeUploadDefaults_AreConsistent()
    {
        // Arrange
        const long maxSize = 1234;
        var services = new ServiceCollection();

        // Act
        services.ConfigureKestrelForLargeUploads(maxSize);

        // Assert
        using var provider = services.BuildServiceProvider();
        var kestrelOptions = provider.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        var formOptions = provider.GetRequiredService<IOptions<FormOptions>>().Value;

        Assert.Equal(maxSize, kestrelOptions.Limits.MaxRequestBodySize);
        Assert.Equal(maxSize, formOptions.MultipartBodyLengthLimit);
    }
}
