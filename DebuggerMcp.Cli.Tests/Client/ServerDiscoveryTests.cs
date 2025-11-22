using DebuggerMcp.Cli.Client;
using DebuggerMcp.Cli.Configuration;
using Moq;

namespace DebuggerMcp.Cli.Tests.Client;

/// <summary>
/// Unit tests for <see cref="ServerDiscovery"/> and <see cref="DiscoveredServer"/>.
/// </summary>
public class ServerDiscoveryTests : IDisposable
{
    private readonly string _tempDir;
    
    public ServerDiscoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ServerDiscoveryTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }
    
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    // ========== ServerCapabilities Tests ==========
    
    [Fact]
    public void ServerCapabilities_DefaultValues()
    {
        // Arrange & Act
        var caps = new ServerCapabilities();
        
        // Assert
        Assert.Equal("unknown", caps.Platform);
        Assert.Equal("unknown", caps.Architecture);
        Assert.False(caps.IsAlpine);
        Assert.Null(caps.RuntimeVersion);
        Assert.Null(caps.DebuggerType);
        Assert.Null(caps.Hostname);
        Assert.Null(caps.Version);
        Assert.Null(caps.Distribution);
    }
    
    [Fact]
    public void ServerCapabilities_WithValues()
    {
        // Arrange & Act
        var caps = new ServerCapabilities
        {
            Platform = "linux",
            Architecture = "arm64",
            IsAlpine = true,
            RuntimeVersion = "10.0.0",
            DebuggerType = "LLDB",
            Hostname = "test-host",
            Version = "1.0.0",
            Distribution = "alpine"
        };
        
        // Assert
        Assert.Equal("linux", caps.Platform);
        Assert.Equal("arm64", caps.Architecture);
        Assert.True(caps.IsAlpine);
        Assert.Equal("10.0.0", caps.RuntimeVersion);
        Assert.Equal("LLDB", caps.DebuggerType);
        Assert.Equal("test-host", caps.Hostname);
        Assert.Equal("1.0.0", caps.Version);
        Assert.Equal("alpine", caps.Distribution);
    }

    // ========== DiscoveredServer Tests ==========
    
    [Fact]
    public void DiscoveredServer_DefaultValues()
    {
        // Arrange & Act
        var server = new DiscoveredServer();
        
        // Assert
        Assert.Equal(string.Empty, server.Url);
        Assert.Null(server.ApiKey);
        Assert.False(server.IsOnline);
        Assert.Null(server.Capabilities);
        Assert.Null(server.ErrorMessage);
    }
    
    [Fact]
    public void DiscoveredServer_Name_WhenOffline_ReturnsDash()
    {
        // Arrange
        var server = new DiscoveredServer
        {
            Url = "http://localhost:5000",
            IsOnline = false
        };
        
        // Act & Assert
        Assert.Equal("-", server.Name);
    }
    
    [Fact]
    public void DiscoveredServer_Name_WhenOnlineWithAlpine_ReturnsAlpineName()
    {
        // Arrange
        var server = new DiscoveredServer
        {
            Url = "http://localhost:5000",
            IsOnline = true,
            Capabilities = new ServerCapabilities
            {
                Architecture = "arm64",
                IsAlpine = true,
                Distribution = "alpine"
            }
        };
        
        // Act & Assert
        Assert.Equal("alpine-arm64", server.Name);
    }
    
    [Fact]
    public void DiscoveredServer_Name_WhenOnlineWithDebian_ReturnsDebianName()
    {
        // Arrange
        var server = new DiscoveredServer
        {
            Url = "http://localhost:5000",
            IsOnline = true,
            Capabilities = new ServerCapabilities
            {
                Architecture = "x64",
                IsAlpine = false,
                Distribution = "debian"
            }
        };
        
        // Act & Assert
        Assert.Equal("debian-x64", server.Name);
    }
    
    [Fact]
    public void DiscoveredServer_Name_WhenNoDistribution_UsesDebian()
    {
        // Arrange
        var server = new DiscoveredServer
        {
            Url = "http://localhost:5000",
            IsOnline = true,
            Capabilities = new ServerCapabilities
            {
                Architecture = "x64",
                IsAlpine = false,
                Distribution = null
            }
        };
        
        // Act & Assert
        Assert.Equal("debian-x64", server.Name);
    }
    
    [Fact]
    public void DiscoveredServer_ShortUrl_ExtractsHostAndPort()
    {
        // Arrange
        var server = new DiscoveredServer
        {
            Url = "http://localhost:5000"
        };
        
        // Act & Assert
        Assert.Equal("localhost:5000", server.ShortUrl);
    }
    
    [Fact]
    public void DiscoveredServer_ShortUrl_WithPath_ExtractsHostAndPort()
    {
        // Arrange
        var server = new DiscoveredServer
        {
            Url = "http://server.example.com:8080/some/path"
        };
        
        // Act & Assert
        Assert.Equal("server.example.com:8080", server.ShortUrl);
    }
    
    [Fact]
    public void DiscoveredServer_ShortUrl_InvalidUrl_ReturnsOriginal()
    {
        // Arrange
        var server = new DiscoveredServer
        {
            Url = "not-a-valid-url"
        };
        
        // Act & Assert
        Assert.Equal("not-a-valid-url", server.ShortUrl);
    }

    // ========== ServerDiscovery Tests ==========
    
    [Fact]
    public void ServerDiscovery_Constructor_InitializesCorrectly()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var configManager = new TestableServerConfigManager(configPath);
        
        // Act
        var discovery = new ServerDiscovery(configManager);
        
        // Assert
        Assert.NotNull(discovery.Servers);
        Assert.Empty(discovery.Servers);
        Assert.Null(discovery.CurrentServer);
        Assert.Equal(0, discovery.OnlineCount);
    }
    
    [Fact]
    public async Task DiscoverAllAsync_WithNoServers_ReturnsEmptyList()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var configManager = new TestableServerConfigManager(configPath);
        var discovery = new ServerDiscovery(configManager);
        
        // Act
        var servers = await discovery.DiscoverAllAsync();
        
        // Assert
        Assert.Empty(servers);
        Assert.Empty(discovery.Servers);
    }
    
    [Fact]
    public async Task DiscoverAllAsync_WithUnreachableServer_MarksAsOffline()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var configManager = new TestableServerConfigManager(configPath);
        configManager.AddServer("http://localhost:59999"); // Unreachable port
        var discovery = new ServerDiscovery(configManager, timeout: TimeSpan.FromMilliseconds(100));
        
        // Act
        var servers = await discovery.DiscoverAllAsync();
        
        // Assert
        Assert.Single(servers);
        Assert.False(servers[0].IsOnline);
        Assert.NotNull(servers[0].ErrorMessage);
    }
    
    [Fact]
    public void FindServer_ByUrl_ReturnsServer()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var configManager = new TestableServerConfigManager(configPath);
        var discovery = new ServerDiscovery(configManager);
        
        // Manually add a discovered server for testing
        var discoveredServers = new List<DiscoveredServer>
        {
            new()
            {
                Url = "http://localhost:5000",
                IsOnline = true,
                Capabilities = new ServerCapabilities { Architecture = "arm64", IsAlpine = false }
            }
        };
        SetDiscoveredServers(discovery, discoveredServers);
        
        // Act
        var found = discovery.FindServer("http://localhost:5000");
        
        // Assert
        Assert.NotNull(found);
        Assert.Equal("http://localhost:5000", found.Url);
    }
    
    [Fact]
    public void FindServer_ByShortUrl_ReturnsServer()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var configManager = new TestableServerConfigManager(configPath);
        var discovery = new ServerDiscovery(configManager);
        
        var discoveredServers = new List<DiscoveredServer>
        {
            new()
            {
                Url = "http://localhost:5000",
                IsOnline = true,
                Capabilities = new ServerCapabilities { Architecture = "arm64", IsAlpine = false }
            }
        };
        SetDiscoveredServers(discovery, discoveredServers);
        
        // Act
        var found = discovery.FindServer("localhost:5000");
        
        // Assert
        Assert.NotNull(found);
        Assert.Equal("http://localhost:5000", found.Url);
    }
    
    [Fact]
    public void FindServer_ByName_ReturnsServer()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var configManager = new TestableServerConfigManager(configPath);
        var discovery = new ServerDiscovery(configManager);
        
        var discoveredServers = new List<DiscoveredServer>
        {
            new()
            {
                Url = "http://localhost:5000",
                IsOnline = true,
                Capabilities = new ServerCapabilities { Architecture = "arm64", IsAlpine = true }
            }
        };
        SetDiscoveredServers(discovery, discoveredServers);
        
        // Act
        var found = discovery.FindServer("alpine-arm64");
        
        // Assert
        Assert.NotNull(found);
        Assert.Equal("http://localhost:5000", found.Url);
    }
    
    [Fact]
    public void FindServer_NotFound_ReturnsNull()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var configManager = new TestableServerConfigManager(configPath);
        var discovery = new ServerDiscovery(configManager);
        
        // Act
        var found = discovery.FindServer("nonexistent");
        
        // Assert
        Assert.Null(found);
    }
    
    [Fact]
    public void FindServer_EmptyOrNull_ReturnsNull()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var configManager = new TestableServerConfigManager(configPath);
        var discovery = new ServerDiscovery(configManager);
        
        // Act & Assert
        Assert.Null(discovery.FindServer(""));
        Assert.Null(discovery.FindServer("   "));
        Assert.Null(discovery.FindServer(null!));
    }
    
    [Fact]
    public void FindMatchingServers_ReturnsMatchingServers()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var configManager = new TestableServerConfigManager(configPath);
        var discovery = new ServerDiscovery(configManager);
        
        var discoveredServers = new List<DiscoveredServer>
        {
            new()
            {
                Url = "http://localhost:5000",
                IsOnline = true,
                Capabilities = new ServerCapabilities { Architecture = "arm64", IsAlpine = false }
            },
            new()
            {
                Url = "http://localhost:5001",
                IsOnline = true,
                Capabilities = new ServerCapabilities { Architecture = "arm64", IsAlpine = true }
            },
            new()
            {
                Url = "http://localhost:5002",
                IsOnline = true,
                Capabilities = new ServerCapabilities { Architecture = "x64", IsAlpine = false }
            }
        };
        SetDiscoveredServers(discovery, discoveredServers);
        
        // Act
        var matching = discovery.FindMatchingServers("arm64", isAlpine: true);
        
        // Assert
        Assert.Single(matching);
        Assert.Equal("http://localhost:5001", matching[0].Url);
    }
    
    [Fact]
    public void FindMatchingServers_ExcludesOfflineServers()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var configManager = new TestableServerConfigManager(configPath);
        var discovery = new ServerDiscovery(configManager);
        
        var discoveredServers = new List<DiscoveredServer>
        {
            new()
            {
                Url = "http://localhost:5000",
                IsOnline = false, // Offline
                Capabilities = new ServerCapabilities { Architecture = "arm64", IsAlpine = true }
            },
            new()
            {
                Url = "http://localhost:5001",
                IsOnline = true,
                Capabilities = new ServerCapabilities { Architecture = "arm64", IsAlpine = true }
            }
        };
        SetDiscoveredServers(discovery, discoveredServers);
        
        // Act
        var matching = discovery.FindMatchingServers("arm64", isAlpine: true);
        
        // Assert
        Assert.Single(matching);
        Assert.Equal("http://localhost:5001", matching[0].Url);
    }
    
    [Fact]
    public void CurrentServerMatches_WhenMatches_ReturnsTrue()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var configManager = new TestableServerConfigManager(configPath);
        var discovery = new ServerDiscovery(configManager);
        
        discovery.CurrentServer = new DiscoveredServer
        {
            Url = "http://localhost:5000",
            IsOnline = true,
            Capabilities = new ServerCapabilities { Architecture = "arm64", IsAlpine = true }
        };
        
        // Act & Assert
        Assert.True(discovery.CurrentServerMatches("arm64", isAlpine: true));
    }
    
    [Fact]
    public void CurrentServerMatches_WhenArchMismatch_ReturnsFalse()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var configManager = new TestableServerConfigManager(configPath);
        var discovery = new ServerDiscovery(configManager);
        
        discovery.CurrentServer = new DiscoveredServer
        {
            Url = "http://localhost:5000",
            IsOnline = true,
            Capabilities = new ServerCapabilities { Architecture = "arm64", IsAlpine = true }
        };
        
        // Act & Assert
        Assert.False(discovery.CurrentServerMatches("x64", isAlpine: true));
    }
    
    [Fact]
    public void CurrentServerMatches_WhenAlpineMismatch_ReturnsFalse()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var configManager = new TestableServerConfigManager(configPath);
        var discovery = new ServerDiscovery(configManager);
        
        discovery.CurrentServer = new DiscoveredServer
        {
            Url = "http://localhost:5000",
            IsOnline = true,
            Capabilities = new ServerCapabilities { Architecture = "arm64", IsAlpine = true }
        };
        
        // Act & Assert
        Assert.False(discovery.CurrentServerMatches("arm64", isAlpine: false));
    }
    
    [Fact]
    public void CurrentServerMatches_WhenNoCurrentServer_ReturnsFalse()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var configManager = new TestableServerConfigManager(configPath);
        var discovery = new ServerDiscovery(configManager);
        
        // Act & Assert
        Assert.False(discovery.CurrentServerMatches("arm64", isAlpine: true));
    }
    
    [Fact]
    public void OnlineCount_ReturnsCorrectCount()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var configManager = new TestableServerConfigManager(configPath);
        var discovery = new ServerDiscovery(configManager);
        
        var discoveredServers = new List<DiscoveredServer>
        {
            new() { Url = "http://localhost:5000", IsOnline = true },
            new() { Url = "http://localhost:5001", IsOnline = false },
            new() { Url = "http://localhost:5002", IsOnline = true },
            new() { Url = "http://localhost:5003", IsOnline = false }
        };
        SetDiscoveredServers(discovery, discoveredServers);
        
        // Act & Assert
        Assert.Equal(2, discovery.OnlineCount);
    }
    
    // ========== Helpers ==========
    
    private static void SetDiscoveredServers(ServerDiscovery discovery, List<DiscoveredServer> servers)
    {
        var field = typeof(ServerDiscovery).GetField("_servers", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(discovery, servers);
    }
    
    /// <summary>
    /// Test helper that allows specifying custom config path.
    /// </summary>
    private class TestableServerConfigManager : ServerConfigManager
    {
        public TestableServerConfigManager(string configPath) : base()
        {
            var field = typeof(ServerConfigManager).GetField("_configPath", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(this, configPath);
        }
    }
}

