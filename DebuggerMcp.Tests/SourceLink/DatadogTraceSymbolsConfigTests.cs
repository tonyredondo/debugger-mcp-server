using DebuggerMcp.SourceLink;
using Xunit;

namespace DebuggerMcp.Tests.SourceLink;

/// <summary>
/// Tests for the DatadogTraceSymbolsConfig class.
/// </summary>
public class DatadogTraceSymbolsConfigTests
{
    [Fact]
    public void AzureDevOpsOrganization_ReturnsDatadoghq()
    {
        // Assert
        Assert.Equal("datadoghq", DatadogTraceSymbolsConfig.AzureDevOpsOrganization);
    }

    [Fact]
    public void AzureDevOpsProject_ReturnsDdTraceDotnet()
    {
        // Assert
        Assert.Equal("dd-trace-dotnet", DatadogTraceSymbolsConfig.AzureDevOpsProject);
    }

    [Fact]
    public void ApiVersion_Returns71()
    {
        // Assert
        Assert.Equal("7.1", DatadogTraceSymbolsConfig.ApiVersion);
    }

    [Fact]
    public void AzureDevOpsBaseUrl_ReturnsCorrectUrl()
    {
        // Assert
        Assert.Equal("https://dev.azure.com", DatadogTraceSymbolsConfig.AzureDevOpsBaseUrl);
    }

    [Fact]
    public void IsEnabled_DefaultsToTrue()
    {
        // Arrange - ensure env var is not set
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", null);

            // Act
            var result = DatadogTraceSymbolsConfig.IsEnabled();

            // Assert
            Assert.True(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", original);
        }
    }

    [Fact]
    public void IsEnabled_ReturnsFalse_WhenEnvVarSetToFalse()
    {
        // Arrange
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", "false");

            // Act
            var result = DatadogTraceSymbolsConfig.IsEnabled();

            // Assert
            Assert.False(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", original);
        }
    }

    [Fact]
    public void IsEnabled_ReturnsTrue_WhenEnvVarSetToTrue()
    {
        // Arrange
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", "true");

            // Act
            var result = DatadogTraceSymbolsConfig.IsEnabled();

            // Assert
            Assert.True(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", original);
        }
    }

    [Fact]
    public void GetTimeoutSeconds_ReturnsDefault120()
    {
        // Arrange
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_TIMEOUT_SECONDS", null);

            // Act
            var result = DatadogTraceSymbolsConfig.GetTimeoutSeconds();

            // Assert
            Assert.Equal(120, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_TIMEOUT_SECONDS", original);
        }
    }

    [Fact]
    public void GetTimeoutSeconds_ReturnsCustomValue()
    {
        // Arrange
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_TIMEOUT_SECONDS", "60");

            // Act
            var result = DatadogTraceSymbolsConfig.GetTimeoutSeconds();

            // Assert
            Assert.Equal(60, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_TIMEOUT_SECONDS", original);
        }
    }

    [Fact]
    public void GetMaxArtifactSize_ReturnsDefault500MB()
    {
        // Arrange
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_MAX_ARTIFACT_SIZE");
        try
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_MAX_ARTIFACT_SIZE", null);

            // Act
            var result = DatadogTraceSymbolsConfig.GetMaxArtifactSize();

            // Assert
            Assert.Equal(500 * 1024 * 1024, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_MAX_ARTIFACT_SIZE", original);
        }
    }

    [Fact]
    public void GetPatToken_ReturnsNull_WhenNotSet()
    {
        // Arrange
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_PAT");
        try
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_PAT", null);

            // Act
            var result = DatadogTraceSymbolsConfig.GetPatToken();

            // Assert
            Assert.Null(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_PAT", original);
        }
    }

    [Fact]
    public void GetPatToken_ReturnsValue_WhenSet()
    {
        // Arrange
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_PAT");
        try
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_PAT", "test-token");

            // Act
            var result = DatadogTraceSymbolsConfig.GetPatToken();

            // Assert
            Assert.Equal("test-token", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_PAT", original);
        }
    }

    [Fact]
    public void GetCacheDirectory_ReturnsCustomValue_WhenEnvVarSet()
    {
        // Arrange
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_CACHE_DIR");
        try
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_CACHE_DIR", "/custom/cache");

            // Act
            var result = DatadogTraceSymbolsConfig.GetCacheDirectory();

            // Assert
            Assert.Equal("/custom/cache", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_CACHE_DIR", original);
        }
    }

    [Theory]
    [InlineData("abc123def456", "abc123de")]
    [InlineData("abc123d", "abc123d")]
    [InlineData("abc", "abc")]
    [InlineData("", "(unknown)")]
    [InlineData(null, "(unknown)")]
    public void GetShortSha_ReturnsSafeValue(string? input, string expected)
    {
        // Act
        var result = DatadogTraceSymbolsConfig.GetShortSha(input);

        // Assert
        Assert.Equal(expected, result);
    }
}

