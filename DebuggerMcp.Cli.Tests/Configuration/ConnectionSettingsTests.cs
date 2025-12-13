using DebuggerMcp.Cli.Configuration;

namespace DebuggerMcp.Cli.Tests.Configuration;

/// <summary>
/// Unit tests for <see cref="ConnectionSettings"/>.
/// </summary>
[Collection("NonParallelEnvironment")]
public class ConnectionSettingsTests
{
    [Fact]
    public void FromEnvironment_WithNoVariablesSet_ReturnsDefaults()
    {
        // Arrange - clear any environment variables
        var originalUrl = Environment.GetEnvironmentVariable(CliEnvironment.ServerUrl);
        var originalKey = Environment.GetEnvironmentVariable(CliEnvironment.ApiKey);
        Environment.SetEnvironmentVariable(CliEnvironment.ServerUrl, null);
        Environment.SetEnvironmentVariable(CliEnvironment.ApiKey, null);

        try
        {
            // Act
            var settings = ConnectionSettings.FromEnvironment();

            // Assert
            Assert.Null(settings.ServerUrl);
            Assert.Null(settings.ApiKey);
            Assert.Equal(Environment.UserName, settings.UserId);
            Assert.Equal(ConnectionSettings.DefaultTimeoutSeconds, settings.TimeoutSeconds);
            Assert.Equal(OutputFormat.Text, settings.OutputFormat);
            Assert.False(settings.Verbose);
        }
        finally
        {
            // Restore original values
            Environment.SetEnvironmentVariable(CliEnvironment.ServerUrl, originalUrl);
            Environment.SetEnvironmentVariable(CliEnvironment.ApiKey, originalKey);
        }
    }

    [Fact]
    public void Load_WithEnvironmentOverrides_ReturnsMergedSettings()
    {
        var originalUrl = Environment.GetEnvironmentVariable(CliEnvironment.ServerUrl);
        var originalKey = Environment.GetEnvironmentVariable(CliEnvironment.ApiKey);
        var originalUserId = Environment.GetEnvironmentVariable(CliEnvironment.UserId);
        var originalTimeout = Environment.GetEnvironmentVariable(CliEnvironment.Timeout);
        var originalOutput = Environment.GetEnvironmentVariable(CliEnvironment.OutputFormat);
        var originalVerbose = Environment.GetEnvironmentVariable(CliEnvironment.Verbose);
        var originalHistory = Environment.GetEnvironmentVariable(CliEnvironment.HistoryFile);

        try
        {
            Environment.SetEnvironmentVariable(CliEnvironment.ServerUrl, "http://localhost:5000");
            Environment.SetEnvironmentVariable(CliEnvironment.ApiKey, "k");
            Environment.SetEnvironmentVariable(CliEnvironment.UserId, "u");
            Environment.SetEnvironmentVariable(CliEnvironment.Timeout, "123");
            Environment.SetEnvironmentVariable(CliEnvironment.OutputFormat, "json");
            Environment.SetEnvironmentVariable(CliEnvironment.Verbose, "true");
            Environment.SetEnvironmentVariable(CliEnvironment.HistoryFile, "/tmp/history.txt");

            var settings = ConnectionSettings.Load();

            Assert.Equal("http://localhost:5000", settings.ServerUrl);
            Assert.Equal("k", settings.ApiKey);
            Assert.Equal("u", settings.UserId);
            Assert.Equal(123, settings.TimeoutSeconds);
            Assert.Equal(OutputFormat.Json, settings.OutputFormat);
            Assert.True(settings.Verbose);
            Assert.Equal("/tmp/history.txt", settings.HistoryFile);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CliEnvironment.ServerUrl, originalUrl);
            Environment.SetEnvironmentVariable(CliEnvironment.ApiKey, originalKey);
            Environment.SetEnvironmentVariable(CliEnvironment.UserId, originalUserId);
            Environment.SetEnvironmentVariable(CliEnvironment.Timeout, originalTimeout);
            Environment.SetEnvironmentVariable(CliEnvironment.OutputFormat, originalOutput);
            Environment.SetEnvironmentVariable(CliEnvironment.Verbose, originalVerbose);
            Environment.SetEnvironmentVariable(CliEnvironment.HistoryFile, originalHistory);
        }
    }

    [Fact]
    public void Validate_WithNoServerUrl_ReturnsError()
    {
        // Arrange
        var settings = new ConnectionSettings
        {
            ServerUrl = null
        };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.Single(errors);
        Assert.Contains("Server URL is required", errors[0]);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com")]
    [InlineData("://invalid")]
    public void Validate_WithInvalidServerUrl_ReturnsError(string invalidUrl)
    {
        // Arrange
        var settings = new ConnectionSettings
        {
            ServerUrl = invalidUrl
        };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.Contains(errors, e => e.Contains("Invalid server URL"));
    }

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("https://debugger.example.com")]
    [InlineData("http://192.168.1.1:8080")]
    public void Validate_WithValidServerUrl_ReturnsNoUrlErrors(string validUrl)
    {
        // Arrange
        var settings = new ConnectionSettings
        {
            ServerUrl = validUrl,
            UserId = "test-user"
        };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WithEmptyUserId_ReturnsError()
    {
        // Arrange
        var settings = new ConnectionSettings
        {
            ServerUrl = "http://localhost:5000",
            UserId = ""
        };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.Single(errors);
        Assert.Contains("User ID is required", errors[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_WithInvalidTimeout_ReturnsError(int invalidTimeout)
    {
        // Arrange
        var settings = new ConnectionSettings
        {
            ServerUrl = "http://localhost:5000",
            TimeoutSeconds = invalidTimeout
        };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.Contains(errors, e => e.Contains("Timeout must be a positive number"));
    }

    [Fact]
    public void GetBaseUri_WithValidUrl_ReturnsUri()
    {
        // Arrange
        var settings = new ConnectionSettings
        {
            ServerUrl = "http://localhost:5000"
        };

        // Act
        var uri = settings.GetBaseUri();

        // Assert
        Assert.NotNull(uri);
        Assert.Equal("http://localhost:5000/", uri.ToString());
    }

    [Fact]
    public void GetBaseUri_WithTrailingSlash_NormalizesUri()
    {
        // Arrange
        var settings = new ConnectionSettings
        {
            ServerUrl = "http://localhost:5000/"
        };

        // Act
        var uri = settings.GetBaseUri();

        // Assert
        Assert.NotNull(uri);
        Assert.Equal("http://localhost:5000/", uri.ToString());
    }

    [Fact]
    public void GetBaseUri_WithNoUrl_ReturnsNull()
    {
        // Arrange
        var settings = new ConnectionSettings
        {
            ServerUrl = null
        };

        // Act
        var uri = settings.GetBaseUri();

        // Assert
        Assert.Null(uri);
    }

    [Fact]
    public void Timeout_ReturnsTimeSpanFromSeconds()
    {
        // Arrange
        var settings = new ConnectionSettings
        {
            TimeoutSeconds = 60
        };

        // Act
        var timeout = settings.Timeout;

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(60), timeout);
    }

    [Fact]
    public void ApplyProfile_WithExistingProfile_AppliesSettings()
    {
        // Arrange
        var settings = new ConnectionSettings
        {
            Profiles = new Dictionary<string, ServerProfile>
            {
                ["production"] = new ServerProfile
                {
                    Url = "https://prod.example.com",
                    ApiKey = "prod-key",
                    UserId = "prod-user",
                    TimeoutSeconds = 600
                }
            }
        };

        // Act
        var result = settings.ApplyProfile("production");

        // Assert
        Assert.True(result);
        Assert.Equal("https://prod.example.com", settings.ServerUrl);
        Assert.Equal("prod-key", settings.ApiKey);
        Assert.Equal("prod-user", settings.UserId);
        Assert.Equal(600, settings.TimeoutSeconds);
    }

    [Fact]
    public void ApplyProfile_WithNonExistentProfile_ReturnsFalse()
    {
        // Arrange
        var settings = new ConnectionSettings();

        // Act
        var result = settings.ApplyProfile("nonexistent");

        // Assert
        Assert.False(result);
    }
}
