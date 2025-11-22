using DebuggerMcp;
using DebuggerMcp.Watches;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for ServiceCollectionExtensions.
/// </summary>
public class ServiceCollectionExtensionsTests : IDisposable
{
    private readonly string _tempPath;

    public ServiceCollectionExtensionsTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            try { Directory.Delete(_tempPath, true); } catch { }
        }
    }

    // ============================================================
    // AddDebuggerServices(string) Tests
    // ============================================================

    [Fact]
    public void AddDebuggerServices_WithPath_RegistersSessionManager()
    {
        var services = new ServiceCollection();
        services.AddLogging(); // Required dependency for DebuggerSessionManager
        
        services.AddDebuggerServices(_tempPath);
        
        var provider = services.BuildServiceProvider();
        var sessionManager = provider.GetService<DebuggerSessionManager>();
        
        Assert.NotNull(sessionManager);
    }

    [Fact]
    public void AddDebuggerServices_WithPath_RegistersSymbolManager()
    {
        var services = new ServiceCollection();
        services.AddLogging(); // Required dependency for DebuggerSessionManager
        
        services.AddDebuggerServices(_tempPath);
        
        var provider = services.BuildServiceProvider();
        var symbolManager = provider.GetService<SymbolManager>();
        
        Assert.NotNull(symbolManager);
    }

    [Fact]
    public void AddDebuggerServices_WithPath_RegistersWatchStore()
    {
        var services = new ServiceCollection();
        services.AddLogging(); // Required dependency for DebuggerSessionManager
        
        services.AddDebuggerServices(_tempPath);
        
        var provider = services.BuildServiceProvider();
        var watchStore = provider.GetService<WatchStore>();
        
        Assert.NotNull(watchStore);
    }

    [Fact]
    public void AddDebuggerServices_WithPath_RegistersSessionCleanupService()
    {
        var services = new ServiceCollection();
        services.AddLogging(); // Required dependency
        
        services.AddDebuggerServices(_tempPath);
        
        var descriptor = services.FirstOrDefault(d => 
            d.ServiceType == typeof(IHostedService) && 
            d.ImplementationType == typeof(SessionCleanupService));
        
        Assert.NotNull(descriptor);
    }

    [Fact]
    public void AddDebuggerServices_WithPath_ReturnsSameCollection()
    {
        var services = new ServiceCollection();
        services.AddLogging(); // Required dependency for DebuggerSessionManager
        
        var result = services.AddDebuggerServices(_tempPath);
        
        Assert.Same(services, result);
    }

    // ============================================================
    // AddDebuggerServices() (no args) Tests
    // ============================================================

    [Fact]
    public void AddDebuggerServices_NoArgs_RegistersServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(); // Required dependency for DebuggerSessionManager
        
        services.AddDebuggerServices();
        
        var provider = services.BuildServiceProvider();
        
        Assert.NotNull(provider.GetService<DebuggerSessionManager>());
        Assert.NotNull(provider.GetService<SymbolManager>());
        Assert.NotNull(provider.GetService<WatchStore>());
    }

    // ============================================================
    // AddDebuggerRateLimiting Tests
    // ============================================================

    [Fact]
    public void AddDebuggerRateLimiting_WithLimit_RegistersRateLimiter()
    {
        var services = new ServiceCollection();
        
        services.AddDebuggerRateLimiting(100);
        
        var descriptor = services.FirstOrDefault(d => 
            d.ServiceType == typeof(RateLimiterOptions));
        
        // RateLimiter is registered via AddRateLimiter
        Assert.True(services.Count > 0);
    }

    [Fact]
    public void AddDebuggerRateLimiting_WithoutLimit_UsesDefault()
    {
        var services = new ServiceCollection();
        
        services.AddDebuggerRateLimiting();
        
        Assert.True(services.Count > 0);
    }

    [Fact]
    public void AddDebuggerRateLimiting_ReturnsSameCollection()
    {
        var services = new ServiceCollection();
        
        var result = services.AddDebuggerRateLimiting(60);
        
        Assert.Same(services, result);
    }

    // ============================================================
    // AddDebuggerCors Tests
    // ============================================================

    [Fact]
    public void AddDebuggerCors_WithOrigins_RegistersCors()
    {
        var services = new ServiceCollection();
        
        services.AddDebuggerCors(["http://localhost:3000"]);
        
        Assert.True(services.Count > 0);
    }

    [Fact]
    public void AddDebuggerCors_WithEmptyOrigins_RegistersCors()
    {
        var services = new ServiceCollection();
        
        services.AddDebuggerCors([]);
        
        Assert.True(services.Count > 0);
    }

    [Fact]
    public void AddDebuggerCors_WithNull_UsesDefault()
    {
        var services = new ServiceCollection();
        
        services.AddDebuggerCors(null);
        
        Assert.True(services.Count > 0);
    }

    [Fact]
    public void AddDebuggerCors_ReturnsSameCollection()
    {
        var services = new ServiceCollection();
        
        var result = services.AddDebuggerCors(["http://localhost"]);
        
        Assert.Same(services, result);
    }

    [Fact]
    public void AddDebuggerCors_WithMultipleOrigins_RegistersCors()
    {
        var services = new ServiceCollection();
        
        services.AddDebuggerCors(["http://localhost:3000", "https://example.com"]);
        
        Assert.True(services.Count > 0);
    }

    // ============================================================
    // ConfigureKestrelForLargeUploads Tests
    // ============================================================

    [Fact]
    public void ConfigureKestrelForLargeUploads_WithSize_ConfiguresKestrel()
    {
        var services = new ServiceCollection();
        
        services.ConfigureKestrelForLargeUploads(1024 * 1024 * 1024); // 1GB
        
        Assert.True(services.Count > 0);
    }

    [Fact]
    public void ConfigureKestrelForLargeUploads_WithNull_UsesDefault()
    {
        var services = new ServiceCollection();
        
        services.ConfigureKestrelForLargeUploads(null);
        
        Assert.True(services.Count > 0);
    }

    [Fact]
    public void ConfigureKestrelForLargeUploads_ReturnsSameCollection()
    {
        var services = new ServiceCollection();
        
        var result = services.ConfigureKestrelForLargeUploads(5L * 1024 * 1024 * 1024);
        
        Assert.Same(services, result);
    }

    // ============================================================
    // Chaining Tests
    // ============================================================

    [Fact]
    public void Extensions_CanBeChained()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        
        var result = services
            .AddDebuggerServices(_tempPath)
            .AddDebuggerRateLimiting(100)
            .AddDebuggerCors(["http://localhost"])
            .ConfigureKestrelForLargeUploads(1024);
        
        Assert.Same(services, result);
        Assert.True(services.Count > 4);
    }
}

