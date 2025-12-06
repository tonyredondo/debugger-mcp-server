using DebuggerMcp.SourceLink;

namespace DebuggerMcp.Tests.McpTools;

/// <summary>
/// Tests for DatadogSymbolsTools MCP tools configuration.
/// These tests verify the static configuration without requiring full tool setup.
/// </summary>
public class DatadogSymbolsToolsTests
{
    [Fact]
    public void DatadogTraceSymbolsConfig_AzureDevOpsOrganization_ReturnsDatadoghq()
    {
        // Assert
        Assert.Equal("datadoghq", DatadogTraceSymbolsConfig.AzureDevOpsOrganization);
    }

    [Fact]
    public void DatadogTraceSymbolsConfig_AzureDevOpsProject_ReturnsDdTraceDotnet()
    {
        // Assert
        Assert.Equal("dd-trace-dotnet", DatadogTraceSymbolsConfig.AzureDevOpsProject);
    }

    [Fact]
    public void DatadogTraceSymbolsConfig_AzureDevOpsBaseUrl_ContainsAzureDevOps()
    {
        // Assert
        Assert.Contains("dev.azure.com", DatadogTraceSymbolsConfig.AzureDevOpsBaseUrl);
    }

    [Fact]
    public void DatadogTraceSymbolsConfig_GetTimeoutSeconds_ReturnsPositiveValue()
    {
        // Act
        var timeout = DatadogTraceSymbolsConfig.GetTimeoutSeconds();
        
        // Assert
        Assert.True(timeout > 0);
    }

    [Fact]
    public void DatadogTraceSymbolsConfig_GetMaxArtifactSize_ReturnsPositiveValue()
    {
        // Act
        var maxSize = DatadogTraceSymbolsConfig.GetMaxArtifactSize();
        
        // Assert
        Assert.True(maxSize > 0);
    }

    [Fact]
    public void DatadogTraceSymbolsConfig_GetShortSha_WithValidSha_ReturnsTruncated()
    {
        // Act
        var result = DatadogTraceSymbolsConfig.GetShortSha("1234567890abcdef");
        
        // Assert
        Assert.Equal("12345678", result);
    }

    [Fact]
    public void DatadogTraceSymbolsConfig_GetShortSha_WithShortSha_ReturnsOriginal()
    {
        // Act
        var result = DatadogTraceSymbolsConfig.GetShortSha("abc");
        
        // Assert
        Assert.Equal("abc", result);
    }

    [Fact]
    public void DatadogTraceSymbolsConfig_GetShortSha_WithNull_ReturnsUnknown()
    {
        // Act
        var result = DatadogTraceSymbolsConfig.GetShortSha(null);
        
        // Assert
        Assert.Equal("(unknown)", result);
    }

    [Fact]
    public void DatadogTraceSymbolsConfig_GetShortSha_WithEmpty_ReturnsUnknown()
    {
        // Act
        var result = DatadogTraceSymbolsConfig.GetShortSha("");
        
        // Assert
        Assert.Equal("(unknown)", result);
    }
}
