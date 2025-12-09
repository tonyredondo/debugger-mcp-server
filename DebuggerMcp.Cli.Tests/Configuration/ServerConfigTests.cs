using DebuggerMcp.Cli.Configuration;

namespace DebuggerMcp.Cli.Tests.Configuration;

/// <summary>
/// Unit tests for <see cref="ServerConfigManager"/> and related classes.
/// </summary>
public class ServerConfigTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalDir;

    public ServerConfigTests()
    {
        // Create a temporary directory for test config files
        _tempDir = Path.Combine(Path.GetTempPath(), $"ServerConfigTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);

        // Store original directory
        _originalDir = Directory.GetCurrentDirectory();
    }

    public void Dispose()
    {
        // Clean up temp directory
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

    // ========== ServerEntry Tests ==========

    [Fact]
    public void ServerEntry_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var entry = new ServerEntry();

        // Assert
        Assert.Equal(string.Empty, entry.Url);
        Assert.Null(entry.ApiKey);
    }

    [Fact]
    public void ServerEntry_WithValues_StoresCorrectly()
    {
        // Arrange & Act
        var entry = new ServerEntry
        {
            Url = "http://localhost:5000",
            ApiKey = "test-key"
        };

        // Assert
        Assert.Equal("http://localhost:5000", entry.Url);
        Assert.Equal("test-key", entry.ApiKey);
    }

    // ========== ServerConfig Tests ==========

    [Fact]
    public void ServerConfig_DefaultValues_HasEmptyServerList()
    {
        // Arrange & Act
        var config = new ServerConfig();

        // Assert
        Assert.NotNull(config.Servers);
        Assert.Empty(config.Servers);
    }

    [Fact]
    public void ServerConfig_WithServers_StoresCorrectly()
    {
        // Arrange & Act
        var config = new ServerConfig
        {
            Servers =
            [
                new ServerEntry { Url = "http://localhost:5000" },
                new ServerEntry { Url = "http://localhost:5001", ApiKey = "key1" }
            ]
        };

        // Assert
        Assert.Equal(2, config.Servers.Count);
        Assert.Equal("http://localhost:5000", config.Servers[0].Url);
        Assert.Null(config.Servers[0].ApiKey);
        Assert.Equal("http://localhost:5001", config.Servers[1].Url);
        Assert.Equal("key1", config.Servers[1].ApiKey);
    }

    // ========== ServerConfigManager Tests ==========

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsEmptyConfig()
    {
        // Arrange
        var manager = new ServerConfigManager();

        // Act
        var config = manager.Load();

        // Assert
        Assert.NotNull(config);
        Assert.Empty(config.Servers);
    }

    [Fact]
    public void GetServers_WhenNoConfig_ReturnsEmptyList()
    {
        // Arrange
        var manager = new ServerConfigManager();

        // Act
        var servers = manager.GetServers();

        // Assert
        Assert.NotNull(servers);
        Assert.Empty(servers);
    }

    [Fact]
    public void AddServer_NewServer_ReturnsTrue()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var manager = new TestableServerConfigManager(configPath);

        // Act
        var result = manager.AddServer("http://localhost:5000");

        // Assert
        Assert.True(result);
        var servers = manager.GetServers();
        Assert.Single(servers);
        Assert.Equal("http://localhost:5000", servers[0].Url);
    }

    [Fact]
    public void AddServer_WithApiKey_StoresApiKey()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var manager = new TestableServerConfigManager(configPath);

        // Act
        var result = manager.AddServer("http://localhost:5000", "my-api-key");

        // Assert
        Assert.True(result);
        var servers = manager.GetServers();
        Assert.Single(servers);
        Assert.Equal("http://localhost:5000", servers[0].Url);
        Assert.Equal("my-api-key", servers[0].ApiKey);
    }

    [Fact]
    public void AddServer_DuplicateUrl_ReturnsFalse()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var manager = new TestableServerConfigManager(configPath);
        manager.AddServer("http://localhost:5000");

        // Act
        var result = manager.AddServer("http://localhost:5000");

        // Assert
        Assert.False(result);
        Assert.Single(manager.GetServers());
    }

    [Fact]
    public void AddServer_DuplicateUrlCaseInsensitive_ReturnsFalse()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var manager = new TestableServerConfigManager(configPath);
        manager.AddServer("http://localhost:5000");

        // Act
        var result = manager.AddServer("HTTP://LOCALHOST:5000");

        // Assert
        Assert.False(result);
        Assert.Single(manager.GetServers());
    }

    [Fact]
    public void AddServer_NormalizesUrlWithoutScheme()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var manager = new TestableServerConfigManager(configPath);

        // Act
        manager.AddServer("localhost:5000");

        // Assert
        var servers = manager.GetServers();
        Assert.Single(servers);
        Assert.Equal("http://localhost:5000", servers[0].Url);
    }

    [Fact]
    public void AddServer_NormalizesUrlWithTrailingSlash()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var manager = new TestableServerConfigManager(configPath);

        // Act
        manager.AddServer("http://localhost:5000/");

        // Assert
        var servers = manager.GetServers();
        Assert.Single(servers);
        Assert.Equal("http://localhost:5000", servers[0].Url);
    }

    [Fact]
    public void RemoveServer_ExistingServer_ReturnsTrue()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var manager = new TestableServerConfigManager(configPath);
        manager.AddServer("http://localhost:5000");
        manager.AddServer("http://localhost:5001");

        // Act
        var result = manager.RemoveServer("http://localhost:5000");

        // Assert
        Assert.True(result);
        var servers = manager.GetServers();
        Assert.Single(servers);
        Assert.Equal("http://localhost:5001", servers[0].Url);
    }

    [Fact]
    public void RemoveServer_NonExistingServer_ReturnsFalse()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var manager = new TestableServerConfigManager(configPath);
        manager.AddServer("http://localhost:5000");

        // Act
        var result = manager.RemoveServer("http://localhost:9999");

        // Assert
        Assert.False(result);
        Assert.Single(manager.GetServers());
    }

    [Fact]
    public void RemoveServer_CaseInsensitive_ReturnsTrue()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var manager = new TestableServerConfigManager(configPath);
        manager.AddServer("http://localhost:5000");

        // Act
        var result = manager.RemoveServer("HTTP://LOCALHOST:5000");

        // Assert
        Assert.True(result);
        Assert.Empty(manager.GetServers());
    }

    [Fact]
    public void CreateDefaultConfig_CreatesConfigWith4Servers()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var manager = new TestableServerConfigManager(configPath);

        // Act
        manager.CreateDefaultConfig();

        // Assert
        var servers = manager.GetServers();
        Assert.Equal(4, servers.Count);
        Assert.Contains(servers, s => s.Url == "http://localhost:5000");
        Assert.Contains(servers, s => s.Url == "http://localhost:5001");
        Assert.Contains(servers, s => s.Url == "http://localhost:5002");
        Assert.Contains(servers, s => s.Url == "http://localhost:5003");
    }

    [Fact]
    public void Save_PersistsToFile()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var manager = new TestableServerConfigManager(configPath);
        manager.AddServer("http://localhost:5000", "key1");

        // Act - Create new manager to verify persistence
        var manager2 = new TestableServerConfigManager(configPath);
        var servers = manager2.GetServers();

        // Assert
        Assert.Single(servers);
        Assert.Equal("http://localhost:5000", servers[0].Url);
        Assert.Equal("key1", servers[0].ApiKey);
    }

    [Fact]
    public void Load_WithInvalidJson_ReturnsEmptyConfig()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        File.WriteAllText(configPath, "{ invalid json }");
        var manager = new TestableServerConfigManager(configPath);

        // Act
        var config = manager.Load();

        // Assert
        Assert.NotNull(config);
        Assert.Empty(config.Servers);
    }

    [Fact]
    public void ConfigPath_ReturnsCorrectPath()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "servers.json");
        var manager = new TestableServerConfigManager(configPath);

        // Act & Assert
        Assert.Equal(configPath, manager.ConfigPath);
    }

    /// <summary>
    /// Test helper that allows specifying custom config path.
    /// </summary>
    private class TestableServerConfigManager : ServerConfigManager
    {
        private readonly string _customConfigPath;

        public TestableServerConfigManager(string configPath) : base()
        {
            _customConfigPath = configPath;

            // Use reflection to set the private _configPath field
            var field = typeof(ServerConfigManager).GetField("_configPath",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(this, configPath);
        }
    }
}

