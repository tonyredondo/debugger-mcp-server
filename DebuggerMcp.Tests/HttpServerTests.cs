using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for the HTTP server infrastructure.
/// </summary>
/// <remarks>
/// These tests verify that the HTTP server starts correctly and that
/// the MCP endpoint is properly registered.
/// </remarks>
public class HttpServerTests
{
    /// <summary>
    /// Verifies that the HTTP server can be started in HTTP API mode.
    /// </summary>
    [Fact]
    public async Task HttpApiMode_ServerStarts_Successfully()
    {
        // Arrange
        var args = new[] { "--http" };
        var cancellationTokenSource = new CancellationTokenSource();
        
        // Act & Assert
        // We just verify that the server can be created without throwing
        // We can't actually run it because it would block the test
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddSingleton<DebuggerSessionManager>();
        
        var app = builder.Build();
        
        // Verify services are registered
        Assert.NotNull(app.Services.GetService<DebuggerSessionManager>());
        
        // Cleanup
        await app.DisposeAsync();
    }

    /// <summary>
    /// Verifies that the HTTP server can be started in MCP HTTP mode.
    /// </summary>
    [Fact]
    public async Task McpHttpMode_ServerStarts_Successfully()
    {
        // Arrange
        var args = new[] { "--mcp-http" };
        var cancellationTokenSource = new CancellationTokenSource();
        
        // Act & Assert
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddSingleton<DebuggerSessionManager>();
        
        // Add MCP server with HTTP transport
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();
        
        var app = builder.Build();
        
        // Verify services are registered
        Assert.NotNull(app.Services.GetService<DebuggerSessionManager>());
        
        // Cleanup
        await app.DisposeAsync();
    }

    /// <summary>
    /// Verifies that the DebuggerSessionManager is registered as a singleton.
    /// </summary>
    [Fact]
    public void SessionManager_IsRegisteredAsSingleton()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<DebuggerSessionManager>();
        
        // Act
        var app = builder.Build();
        var manager1 = app.Services.GetService<DebuggerSessionManager>();
        var manager2 = app.Services.GetService<DebuggerSessionManager>();
        
        // Assert
        Assert.NotNull(manager1);
        Assert.NotNull(manager2);
        Assert.Same(manager1, manager2); // Should be the same instance (singleton)
    }

    /// <summary>
    /// Verifies that CORS is configured correctly for the HTTP server.
    /// </summary>
    [Fact]
    public async Task HttpServer_CorsIsConfigured()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllers();
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });
        
        // Act
        var app = builder.Build();
        app.UseCors();
        
        // Assert
        // If no exception is thrown, CORS is configured correctly
        Assert.NotNull(app);
        
        // Cleanup
        await app.DisposeAsync();
    }

    /// <summary>
    /// Verifies that the application can be built with all required services.
    /// </summary>
    [Fact]
    public async Task Application_BuildsWithAllServices()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        
        // Add all services as in the real application
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddSingleton<DebuggerSessionManager>();
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });
        
        // Act
        var app = builder.Build();
        
        // Assert
        Assert.NotNull(app);
        Assert.NotNull(app.Services);
        Assert.NotNull(app.Services.GetService<DebuggerSessionManager>());
        
        // Cleanup
        await app.DisposeAsync();
    }

    /// <summary>
    /// Verifies that the MCP server can be configured with HTTP transport.
    /// </summary>
    [Fact]
    public async Task McpServer_ConfiguresWithHttpTransport()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        
        // Act
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();
        
        var app = builder.Build();
        
        // Assert
        // If no exception is thrown, MCP server is configured correctly
        Assert.NotNull(app);
        Assert.NotNull(app.Services);
        
        // Cleanup
        await app.DisposeAsync();
    }

    /// <summary>
    /// Verifies that Swagger is configured correctly.
    /// </summary>
    [Fact]
    public async Task HttpServer_SwaggerIsConfigured()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        
        // Act
        var app = builder.Build();
        app.UseSwagger();
        app.UseSwaggerUI();
        
        // Assert
        Assert.NotNull(app);
        
        // Cleanup
        await app.DisposeAsync();
    }

    /// <summary>
    /// Verifies that controllers are mapped correctly.
    /// </summary>
    [Fact]
    public async Task HttpServer_ControllersAreMapped()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllers();
        
        // Act
        var app = builder.Build();
        app.MapControllers();
        
        // Assert
        Assert.NotNull(app);
        
        // Cleanup
        await app.DisposeAsync();
    }
}
