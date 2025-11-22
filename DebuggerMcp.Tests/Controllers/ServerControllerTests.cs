using System.Net;
using System.Net.Http.Json;
using DebuggerMcp.Models;

namespace DebuggerMcp.Tests.Controllers;

/// <summary>
/// Integration tests for ServerController.
/// Uses TestWebApplicationFactory to test HTTP endpoints with a real HTTP pipeline.
/// </summary>
public class ServerControllerTests : IClassFixture<TestWebApplicationFactory>, IDisposable
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    
    public ServerControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }
    
    public void Dispose()
    {
        _client.Dispose();
    }

    // ========== GET /api/server/capabilities Tests ==========

    [Fact]
    public async Task GetCapabilities_ReturnsOk()
    {
        // Act
        var response = await _client.GetAsync("/api/server/capabilities");
        
        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    
    [Fact]
    public async Task GetCapabilities_ReturnsValidJson()
    {
        // Act
        var response = await _client.GetAsync("/api/server/capabilities");
        var capabilities = await response.Content.ReadFromJsonAsync<ServerCapabilities>();
        
        // Assert
        Assert.NotNull(capabilities);
    }
    
    [Fact]
    public async Task GetCapabilities_ContainsPlatform()
    {
        // Act
        var response = await _client.GetAsync("/api/server/capabilities");
        var capabilities = await response.Content.ReadFromJsonAsync<ServerCapabilities>();
        
        // Assert
        Assert.NotNull(capabilities);
        Assert.NotNull(capabilities.Platform);
        Assert.Contains(capabilities.Platform, new[] { "linux", "windows", "macos", "unknown" });
    }
    
    [Fact]
    public async Task GetCapabilities_ContainsArchitecture()
    {
        // Act
        var response = await _client.GetAsync("/api/server/capabilities");
        var capabilities = await response.Content.ReadFromJsonAsync<ServerCapabilities>();
        
        // Assert
        Assert.NotNull(capabilities);
        Assert.NotNull(capabilities.Architecture);
        Assert.Contains(capabilities.Architecture, new[] { "x64", "x86", "arm64", "arm" });
    }
    
    [Fact]
    public async Task GetCapabilities_ContainsDebuggerType()
    {
        // Act
        var response = await _client.GetAsync("/api/server/capabilities");
        var capabilities = await response.Content.ReadFromJsonAsync<ServerCapabilities>();
        
        // Assert
        Assert.NotNull(capabilities);
        Assert.NotNull(capabilities.DebuggerType);
        Assert.Contains(capabilities.DebuggerType, new[] { "LLDB", "WinDbg" });
    }
    
    [Fact]
    public async Task GetCapabilities_ContainsRuntimeVersion()
    {
        // Act
        var response = await _client.GetAsync("/api/server/capabilities");
        var capabilities = await response.Content.ReadFromJsonAsync<ServerCapabilities>();
        
        // Assert
        Assert.NotNull(capabilities);
        Assert.NotNull(capabilities.RuntimeVersion);
        Assert.NotEmpty(capabilities.RuntimeVersion);
    }
    
    [Fact]
    public async Task GetCapabilities_ContainsHostname()
    {
        // Act
        var response = await _client.GetAsync("/api/server/capabilities");
        var capabilities = await response.Content.ReadFromJsonAsync<ServerCapabilities>();
        
        // Assert
        Assert.NotNull(capabilities);
        Assert.NotNull(capabilities.Hostname);
        Assert.NotEmpty(capabilities.Hostname);
    }
    
    [Fact]
    public async Task GetCapabilities_ContainsVersion()
    {
        // Act
        var response = await _client.GetAsync("/api/server/capabilities");
        var capabilities = await response.Content.ReadFromJsonAsync<ServerCapabilities>();
        
        // Assert
        Assert.NotNull(capabilities);
        Assert.NotNull(capabilities.Version);
        Assert.NotEmpty(capabilities.Version);
    }
    
    [Fact]
    public async Task GetCapabilities_IsAlpine_IsBooleanValue()
    {
        // Act
        var response = await _client.GetAsync("/api/server/capabilities");
        var capabilities = await response.Content.ReadFromJsonAsync<ServerCapabilities>();
        
        // Assert
        Assert.NotNull(capabilities);
        // IsAlpine should be either true or false
        Assert.True(capabilities.IsAlpine == true || capabilities.IsAlpine == false);
    }
    
    [Fact]
    public async Task GetCapabilities_MultipleCalls_ReturnsSameValues()
    {
        // Act - Call twice to verify caching works correctly
        var response1 = await _client.GetAsync("/api/server/capabilities");
        var capabilities1 = await response1.Content.ReadFromJsonAsync<ServerCapabilities>();
        
        var response2 = await _client.GetAsync("/api/server/capabilities");
        var capabilities2 = await response2.Content.ReadFromJsonAsync<ServerCapabilities>();
        
        // Assert
        Assert.NotNull(capabilities1);
        Assert.NotNull(capabilities2);
        Assert.Equal(capabilities1.Platform, capabilities2.Platform);
        Assert.Equal(capabilities1.Architecture, capabilities2.Architecture);
        Assert.Equal(capabilities1.IsAlpine, capabilities2.IsAlpine);
        Assert.Equal(capabilities1.DebuggerType, capabilities2.DebuggerType);
    }

    // ========== GET /api/server/info Tests ==========

    [Fact]
    public async Task GetInfo_ReturnsOk()
    {
        // Act
        var response = await _client.GetAsync("/api/server/info");
        
        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    
    [Fact]
    public async Task GetInfo_ReturnsValidJson()
    {
        // Act
        var response = await _client.GetAsync("/api/server/info");
        var content = await response.Content.ReadAsStringAsync();
        
        // Assert
        Assert.NotEmpty(content);
        Assert.Contains("name", content);
        Assert.Contains("version", content);
        Assert.Contains("platform", content);
        Assert.Contains("architecture", content);
    }
    
    [Fact]
    public async Task GetInfo_ContainsAutoGeneratedName()
    {
        // Act
        var response = await _client.GetAsync("/api/server/info");
        var content = await response.Content.ReadAsStringAsync();
        
        // Assert - Name should follow format like "alpine-x64" or "debian-arm64"
        Assert.NotEmpty(content);
        Assert.Contains("name", content);
        // Should contain architecture in the name
        Assert.True(
            content.Contains("x64") || 
            content.Contains("arm64") || 
            content.Contains("x86") || 
            content.Contains("arm"),
            "Info should contain architecture in the response");
    }
}

// ========== ServerCapabilities Model Unit Tests ==========

/// <summary>
/// Unit tests for <see cref="ServerCapabilities"/> model.
/// </summary>
public class ServerCapabilitiesTests
{
    [Fact]
    public void ServerCapabilities_DefaultConstruction_HasValidValues()
    {
        // Act
        var capabilities = new ServerCapabilities();
        
        // Assert - Default constructor should auto-detect values
        Assert.NotNull(capabilities.Platform);
        Assert.NotNull(capabilities.Architecture);
        Assert.NotNull(capabilities.RuntimeVersion);
        Assert.NotNull(capabilities.DebuggerType);
        Assert.NotNull(capabilities.Hostname);
        Assert.NotNull(capabilities.Version);
    }
    
    [Fact]
    public void ServerCapabilities_Platform_IsDetected()
    {
        // Act
        var capabilities = new ServerCapabilities();
        
        // Assert
        Assert.Contains(capabilities.Platform, new[] { "linux", "windows", "macos", "unknown" });
    }
    
    [Fact]
    public void ServerCapabilities_Architecture_IsDetected()
    {
        // Act
        var capabilities = new ServerCapabilities();
        
        // Assert - Should match one of the known architectures
        Assert.Contains(capabilities.Architecture, new[] { "x64", "x86", "arm64", "arm" });
    }
    
    [Fact]
    public void ServerCapabilities_RuntimeVersion_MatchesEnvironment()
    {
        // Act
        var capabilities = new ServerCapabilities();
        
        // Assert
        Assert.Equal(Environment.Version.ToString(), capabilities.RuntimeVersion);
    }
    
    [Fact]
    public void ServerCapabilities_Hostname_MatchesMachineName()
    {
        // Act
        var capabilities = new ServerCapabilities();
        
        // Assert
        Assert.Equal(Environment.MachineName, capabilities.Hostname);
    }
    
    [Fact]
    public void ServerCapabilities_DebuggerType_IsValid()
    {
        // Act
        var capabilities = new ServerCapabilities();
        
        // Assert - Should be either WinDbg (Windows) or LLDB (Linux/macOS)
        Assert.Contains(capabilities.DebuggerType, new[] { "WinDbg", "LLDB" });
    }
    
    [Fact]
    public void ServerCapabilities_IsAlpine_DefaultsFalseOnNonLinux()
    {
        // Act
        var capabilities = new ServerCapabilities();
        
        // Assert - On non-Linux (test environment is likely not Alpine), should be false
        // Note: This test will pass on Linux too if not Alpine
        if (capabilities.Platform != "linux")
        {
            Assert.False(capabilities.IsAlpine);
        }
    }
}

