using DebuggerMcp.Configuration;

namespace DebuggerMcp.Tests.Configuration;

/// <summary>
/// Tests for EnvironmentConfig class.
/// Note: These tests manipulate environment variables and restore them after each test.
/// </summary>
[Collection("NonParallelEnvironment")]
public class EnvironmentConfigTests : IDisposable
{
    private readonly Dictionary<string, string?> _originalValues = new();

    public EnvironmentConfigTests()
    {
        // Store original environment variables to restore after tests
        StoreOriginalValue(EnvironmentConfig.DumpStoragePath);
        StoreOriginalValue(EnvironmentConfig.SymbolStoragePath);
        StoreOriginalValue(EnvironmentConfig.ApiKey);
        StoreOriginalValue(EnvironmentConfig.CorsAllowedOrigins);
        StoreOriginalValue(EnvironmentConfig.RateLimitRequestsPerMinute);
        StoreOriginalValue(EnvironmentConfig.EnableSwagger);
        StoreOriginalValue(EnvironmentConfig.MaxSessionsPerUser);
        StoreOriginalValue(EnvironmentConfig.MaxTotalSessions);
        StoreOriginalValue(EnvironmentConfig.SessionCleanupIntervalMinutes);
        StoreOriginalValue(EnvironmentConfig.SessionInactivityThresholdMinutes);
        StoreOriginalValue(EnvironmentConfig.SosPluginPath);
        StoreOriginalValue(EnvironmentConfig.Port);
        StoreOriginalValue(EnvironmentConfig.MaxRequestBodySizeGb);
        StoreOriginalValue(EnvironmentConfig.AiSamplingTrace);
    }

    private void StoreOriginalValue(string name)
    {
        _originalValues[name] = Environment.GetEnvironmentVariable(name);
    }

    public void Dispose()
    {
        // Restore all original values
        foreach (var kvp in _originalValues)
        {
            if (kvp.Value == null)
            {
                Environment.SetEnvironmentVariable(kvp.Key, null);
            }
            else
            {
                Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
            }
        }
    }

    private static void SetEnv(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
    }

    private static void ClearEnv(string name)
    {
        Environment.SetEnvironmentVariable(name, null);
    }

    // ========== Constants Tests ==========

    [Fact]
    public void DumpStoragePath_ConstantName_IsCorrect()
    {
        Assert.Equal("DUMP_STORAGE_PATH", EnvironmentConfig.DumpStoragePath);
    }

    [Fact]
    public void SymbolStoragePath_ConstantName_IsCorrect()
    {
        Assert.Equal("SYMBOL_STORAGE_PATH", EnvironmentConfig.SymbolStoragePath);
    }

    [Fact]
    public void ApiKey_ConstantName_IsCorrect()
    {
        Assert.Equal("API_KEY", EnvironmentConfig.ApiKey);
    }

    [Fact]
    public void CorsAllowedOrigins_ConstantName_IsCorrect()
    {
        Assert.Equal("CORS_ALLOWED_ORIGINS", EnvironmentConfig.CorsAllowedOrigins);
    }

    [Fact]
    public void RateLimitRequestsPerMinute_ConstantName_IsCorrect()
    {
        Assert.Equal("RATE_LIMIT_REQUESTS_PER_MINUTE", EnvironmentConfig.RateLimitRequestsPerMinute);
    }

    [Fact]
    public void EnableSwagger_ConstantName_IsCorrect()
    {
        Assert.Equal("ENABLE_SWAGGER", EnvironmentConfig.EnableSwagger);
    }

    [Fact]
    public void MaxSessionsPerUser_ConstantName_IsCorrect()
    {
        Assert.Equal("MAX_SESSIONS_PER_USER", EnvironmentConfig.MaxSessionsPerUser);
    }

    [Fact]
    public void MaxTotalSessions_ConstantName_IsCorrect()
    {
        Assert.Equal("MAX_TOTAL_SESSIONS", EnvironmentConfig.MaxTotalSessions);
    }

    [Fact]
    public void SessionCleanupIntervalMinutes_ConstantName_IsCorrect()
    {
        Assert.Equal("SESSION_CLEANUP_INTERVAL_MINUTES", EnvironmentConfig.SessionCleanupIntervalMinutes);
    }

    [Fact]
    public void SessionInactivityThresholdMinutes_ConstantName_IsCorrect()
    {
        Assert.Equal("SESSION_INACTIVITY_THRESHOLD_MINUTES", EnvironmentConfig.SessionInactivityThresholdMinutes);
    }

    [Fact]
    public void SosPluginPath_ConstantName_IsCorrect()
    {
        Assert.Equal("SOS_PLUGIN_PATH", EnvironmentConfig.SosPluginPath);
    }

    [Fact]
    public void Port_ConstantName_IsCorrect()
    {
        Assert.Equal("PORT", EnvironmentConfig.Port);
    }

    [Fact]
    public void MaxRequestBodySizeGb_ConstantName_IsCorrect()
    {
        Assert.Equal("MAX_REQUEST_BODY_SIZE_GB", EnvironmentConfig.MaxRequestBodySizeGb);
    }

    [Fact]
    public void AiSamplingTrace_ConstantName_IsCorrect()
    {
        Assert.Equal("DEBUGGER_MCP_AI_SAMPLING_TRACE", EnvironmentConfig.AiSamplingTrace);
    }

    // ========== Default Values Tests ==========

    [Fact]
    public void DefaultRateLimitRequestsPerMinute_Is120()
    {
        Assert.Equal(120, EnvironmentConfig.DefaultRateLimitRequestsPerMinute);
    }

    [Fact]
    public void DefaultMaxSessionsPerUser_Is10()
    {
        Assert.Equal(10, EnvironmentConfig.DefaultMaxSessionsPerUser);
    }

    [Fact]
    public void DefaultMaxTotalSessions_Is50()
    {
        Assert.Equal(50, EnvironmentConfig.DefaultMaxTotalSessions);
    }

    [Fact]
    public void DefaultSessionCleanupIntervalMinutes_Is5()
    {
        Assert.Equal(5, EnvironmentConfig.DefaultSessionCleanupIntervalMinutes);
    }

    [Fact]
    public void DefaultSessionInactivityThresholdMinutes_Is1440()
    {
        // Default is 24 hours (1440 minutes) for long-running debug sessions
        Assert.Equal(1440, EnvironmentConfig.DefaultSessionInactivityThresholdMinutes);
    }

    [Fact]
    public void DefaultPort_Is5000()
    {
        Assert.Equal(5000, EnvironmentConfig.DefaultPort);
    }

    [Fact]
    public void DefaultMaxRequestBodySizeGb_Is5()
    {
        Assert.Equal(5, EnvironmentConfig.DefaultMaxRequestBodySizeGb);
    }

    [Fact]
    public void DefaultDumpStoragePath_ContainsTempPath()
    {
        var path = EnvironmentConfig.DefaultDumpStoragePath;
        Assert.Contains("WinDbgDumps", path);
    }

    [Fact]
    public void DefaultSymbolStoragePath_ContainsDebuggerMcp()
    {
        var path = EnvironmentConfig.DefaultSymbolStoragePath;
        Assert.Contains("debuggermcp", path, StringComparison.OrdinalIgnoreCase);
    }

    // ========== GetString Tests ==========

    [Fact]
    public void GetString_EnvSet_ReturnsEnvValue()
    {
        // Arrange
        SetEnv("TEST_STRING_VAR", "custom-value");

        // Act
        var result = EnvironmentConfig.GetString("TEST_STRING_VAR", "default");

        // Assert
        Assert.Equal("custom-value", result);

        // Cleanup
        ClearEnv("TEST_STRING_VAR");
    }

    [Fact]
    public void GetString_EnvNotSet_ReturnsDefault()
    {
        // Arrange
        ClearEnv("TEST_STRING_VAR_UNSET");

        // Act
        var result = EnvironmentConfig.GetString("TEST_STRING_VAR_UNSET", "default-value");

        // Assert
        Assert.Equal("default-value", result);
    }

    [Fact]
    public void GetString_EnvEmpty_ReturnsDefault()
    {
        // Arrange
        SetEnv("TEST_STRING_VAR_EMPTY", "");

        // Act
        var result = EnvironmentConfig.GetString("TEST_STRING_VAR_EMPTY", "default-value");

        // Assert
        Assert.Equal("default-value", result);

        // Cleanup
        ClearEnv("TEST_STRING_VAR_EMPTY");
    }

    // ========== GetInt Tests ==========

    [Fact]
    public void GetInt_ValidValue_ReturnsIntValue()
    {
        // Arrange
        SetEnv("TEST_INT_VAR", "42");

        // Act
        var result = EnvironmentConfig.GetInt("TEST_INT_VAR", 10);

        // Assert
        Assert.Equal(42, result);

        // Cleanup
        ClearEnv("TEST_INT_VAR");
    }

    [Fact]
    public void GetInt_EnvNotSet_ReturnsDefault()
    {
        // Arrange
        ClearEnv("TEST_INT_VAR_UNSET");

        // Act
        var result = EnvironmentConfig.GetInt("TEST_INT_VAR_UNSET", 99);

        // Assert
        Assert.Equal(99, result);
    }

    [Fact]
    public void GetInt_EnvEmpty_ReturnsDefault()
    {
        // Arrange
        SetEnv("TEST_INT_VAR_EMPTY", "");

        // Act
        var result = EnvironmentConfig.GetInt("TEST_INT_VAR_EMPTY", 77);

        // Assert
        Assert.Equal(77, result);

        // Cleanup
        ClearEnv("TEST_INT_VAR_EMPTY");
    }

    [Fact]
    public void GetInt_InvalidValue_ReturnsDefault()
    {
        // Arrange
        SetEnv("TEST_INT_VAR_INVALID", "not-a-number");

        // Act
        var result = EnvironmentConfig.GetInt("TEST_INT_VAR_INVALID", 55);

        // Assert
        Assert.Equal(55, result);

        // Cleanup
        ClearEnv("TEST_INT_VAR_INVALID");
    }

    [Fact]
    public void GetInt_NegativeValue_ReturnsDefault()
    {
        // Arrange
        SetEnv("TEST_INT_VAR_NEGATIVE", "-10");

        // Act
        var result = EnvironmentConfig.GetInt("TEST_INT_VAR_NEGATIVE", 33);

        // Assert
        Assert.Equal(33, result); // Negative values return default

        // Cleanup
        ClearEnv("TEST_INT_VAR_NEGATIVE");
    }

    [Fact]
    public void GetInt_ZeroValue_ReturnsDefault()
    {
        // Arrange
        SetEnv("TEST_INT_VAR_ZERO", "0");

        // Act
        var result = EnvironmentConfig.GetInt("TEST_INT_VAR_ZERO", 44);

        // Assert
        Assert.Equal(44, result); // Zero returns default (parsed > 0 check)

        // Cleanup
        ClearEnv("TEST_INT_VAR_ZERO");
    }

    // ========== GetBool Tests ==========

    [Fact]
    public void GetBool_True_ReturnsTrue()
    {
        // Arrange
        SetEnv("TEST_BOOL_VAR", "true");

        // Act
        var result = EnvironmentConfig.GetBool("TEST_BOOL_VAR", false);

        // Assert
        Assert.True(result);

        // Cleanup
        ClearEnv("TEST_BOOL_VAR");
    }

    [Fact]
    public void GetBool_TrueUpperCase_ReturnsTrue()
    {
        // Arrange
        SetEnv("TEST_BOOL_VAR_UPPER", "TRUE");

        // Act
        var result = EnvironmentConfig.GetBool("TEST_BOOL_VAR_UPPER", false);

        // Assert
        Assert.True(result);

        // Cleanup
        ClearEnv("TEST_BOOL_VAR_UPPER");
    }

    [Fact]
    public void GetBool_TrueMixedCase_ReturnsTrue()
    {
        // Arrange
        SetEnv("TEST_BOOL_VAR_MIXED", "True");

        // Act
        var result = EnvironmentConfig.GetBool("TEST_BOOL_VAR_MIXED", false);

        // Assert
        Assert.True(result);

        // Cleanup
        ClearEnv("TEST_BOOL_VAR_MIXED");
    }

    [Fact]
    public void GetBool_False_ReturnsFalse()
    {
        // Arrange
        SetEnv("TEST_BOOL_VAR_FALSE", "false");

        // Act
        var result = EnvironmentConfig.GetBool("TEST_BOOL_VAR_FALSE", true);

        // Assert
        Assert.False(result);

        // Cleanup
        ClearEnv("TEST_BOOL_VAR_FALSE");
    }

    [Fact]
    public void GetBool_OtherValue_ReturnsFalse()
    {
        // Arrange
        SetEnv("TEST_BOOL_VAR_OTHER", "yes");

        // Act
        var result = EnvironmentConfig.GetBool("TEST_BOOL_VAR_OTHER", true);

        // Assert
        Assert.False(result); // Only "true" returns true

        // Cleanup
        ClearEnv("TEST_BOOL_VAR_OTHER");
    }

    [Fact]
    public void GetBool_EnvNotSet_ReturnsDefault()
    {
        // Arrange
        ClearEnv("TEST_BOOL_VAR_UNSET");

        // Act
        var resultTrue = EnvironmentConfig.GetBool("TEST_BOOL_VAR_UNSET", true);
        var resultFalse = EnvironmentConfig.GetBool("TEST_BOOL_VAR_UNSET", false);

        // Assert
        Assert.True(resultTrue);
        Assert.False(resultFalse);
    }

    [Fact]
    public void GetBool_EnvEmpty_ReturnsDefault()
    {
        // Arrange
        SetEnv("TEST_BOOL_VAR_EMPTY", "");

        // Act
        var result = EnvironmentConfig.GetBool("TEST_BOOL_VAR_EMPTY", true);

        // Assert
        Assert.True(result);

        // Cleanup
        ClearEnv("TEST_BOOL_VAR_EMPTY");
    }

    // ========== Specific Getter Tests ==========

    [Fact]
    public void GetDumpStoragePath_EnvSet_ReturnsEnvValue()
    {
        // Arrange
        SetEnv(EnvironmentConfig.DumpStoragePath, "/custom/dump/path");

        // Act
        var result = EnvironmentConfig.GetDumpStoragePath();

        // Assert
        Assert.Equal("/custom/dump/path", result);
    }

    [Fact]
    public void GetDumpStoragePath_EnvNotSet_ReturnsDefault()
    {
        // Arrange
        ClearEnv(EnvironmentConfig.DumpStoragePath);

        // Act
        var result = EnvironmentConfig.GetDumpStoragePath();

        // Assert
        Assert.Equal(EnvironmentConfig.DefaultDumpStoragePath, result);
    }

    [Fact]
    public void GetSymbolStoragePath_EnvSet_ReturnsEnvValue()
    {
        // Arrange
        SetEnv(EnvironmentConfig.SymbolStoragePath, "/custom/symbol/path");

        // Act
        var result = EnvironmentConfig.GetSymbolStoragePath();

        // Assert
        Assert.Equal("/custom/symbol/path", result);
    }

    [Fact]
    public void GetSymbolStoragePath_EnvNotSet_ReturnsDefault()
    {
        // Arrange
        ClearEnv(EnvironmentConfig.SymbolStoragePath);

        // Act
        var result = EnvironmentConfig.GetSymbolStoragePath();

        // Assert
        Assert.Equal(EnvironmentConfig.DefaultSymbolStoragePath, result);
    }

    [Fact]
    public void GetRateLimit_EnvSet_ReturnsEnvValue()
    {
        // Arrange
        SetEnv(EnvironmentConfig.RateLimitRequestsPerMinute, "200");

        // Act
        var result = EnvironmentConfig.GetRateLimit();

        // Assert
        Assert.Equal(200, result);
    }

    [Fact]
    public void GetRateLimit_EnvNotSet_ReturnsDefault()
    {
        // Arrange
        ClearEnv(EnvironmentConfig.RateLimitRequestsPerMinute);

        // Act
        var result = EnvironmentConfig.GetRateLimit();

        // Assert
        Assert.Equal(120, result);
    }

    [Fact]
    public void GetMaxSessionsPerUser_EnvSet_ReturnsEnvValue()
    {
        // Arrange
        SetEnv(EnvironmentConfig.MaxSessionsPerUser, "10");

        // Act
        var result = EnvironmentConfig.GetMaxSessionsPerUser();

        // Assert
        Assert.Equal(10, result);
    }

    [Fact]
    public void GetMaxSessionsPerUser_EnvNotSet_ReturnsDefault()
    {
        // Arrange
        ClearEnv(EnvironmentConfig.MaxSessionsPerUser);

        // Act
        var result = EnvironmentConfig.GetMaxSessionsPerUser();

        // Assert
        Assert.Equal(10, result);
    }

    [Fact]
    public void GetMaxTotalSessions_EnvSet_ReturnsEnvValue()
    {
        // Arrange
        SetEnv(EnvironmentConfig.MaxTotalSessions, "100");

        // Act
        var result = EnvironmentConfig.GetMaxTotalSessions();

        // Assert
        Assert.Equal(100, result);
    }

    [Fact]
    public void GetMaxTotalSessions_EnvNotSet_ReturnsDefault()
    {
        // Arrange
        ClearEnv(EnvironmentConfig.MaxTotalSessions);

        // Act
        var result = EnvironmentConfig.GetMaxTotalSessions();

        // Assert
        Assert.Equal(50, result);
    }

    [Fact]
    public void GetSessionCleanupInterval_EnvSet_ReturnsTimeSpan()
    {
        // Arrange
        SetEnv(EnvironmentConfig.SessionCleanupIntervalMinutes, "15");

        // Act
        var result = EnvironmentConfig.GetSessionCleanupInterval();

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(15), result);
    }

    [Fact]
    public void GetSessionCleanupInterval_EnvNotSet_ReturnsDefault()
    {
        // Arrange
        ClearEnv(EnvironmentConfig.SessionCleanupIntervalMinutes);

        // Act
        var result = EnvironmentConfig.GetSessionCleanupInterval();

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(5), result);
    }

    [Fact]
    public void GetSessionInactivityThreshold_EnvSet_ReturnsTimeSpan()
    {
        // Arrange
        SetEnv(EnvironmentConfig.SessionInactivityThresholdMinutes, "60");

        // Act
        var result = EnvironmentConfig.GetSessionInactivityThreshold();

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(60), result);
    }

    [Fact]
    public void GetSessionInactivityThreshold_EnvNotSet_ReturnsDefault()
    {
        // Arrange
        ClearEnv(EnvironmentConfig.SessionInactivityThresholdMinutes);

        // Act
        var result = EnvironmentConfig.GetSessionInactivityThreshold();

        // Assert - Default is 24 hours (1440 minutes) for long-running debug sessions
        Assert.Equal(TimeSpan.FromMinutes(1440), result);
    }

    [Fact]
    public void IsSwaggerEnabled_EnvSetTrue_ReturnsTrue()
    {
        // Arrange
        SetEnv(EnvironmentConfig.EnableSwagger, "true");

        // Act
        var result = EnvironmentConfig.IsSwaggerEnabled();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsSwaggerEnabled_EnvNotSet_ReturnsFalse()
    {
        // Arrange
        ClearEnv(EnvironmentConfig.EnableSwagger);

        // Act
        var result = EnvironmentConfig.IsSwaggerEnabled();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsAiSamplingTraceEnabled_EnvSetTrue_ReturnsTrue()
    {
        // Arrange
        SetEnv(EnvironmentConfig.AiSamplingTrace, "true");

        // Act
        var result = EnvironmentConfig.IsAiSamplingTraceEnabled();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsAiSamplingTraceEnabled_EnvNotSet_ReturnsFalse()
    {
        // Arrange
        ClearEnv(EnvironmentConfig.AiSamplingTrace);

        // Act
        var result = EnvironmentConfig.IsAiSamplingTraceEnabled();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GetApiKey_EnvSet_ReturnsValue()
    {
        // Arrange
        SetEnv(EnvironmentConfig.ApiKey, "my-secret-key");

        // Act
        var result = EnvironmentConfig.GetApiKey();

        // Assert
        Assert.Equal("my-secret-key", result);
    }

    [Fact]
    public void GetApiKey_EnvNotSet_ReturnsNull()
    {
        // Arrange
        ClearEnv(EnvironmentConfig.ApiKey);

        // Act
        var result = EnvironmentConfig.GetApiKey();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetCorsAllowedOrigins_EnvSet_ReturnsSplitArray()
    {
        // Arrange
        SetEnv(EnvironmentConfig.CorsAllowedOrigins, "https://app.example.com,https://admin.example.com");

        // Act
        var result = EnvironmentConfig.GetCorsAllowedOrigins();

        // Assert
        Assert.Equal(2, result.Length);
        Assert.Contains("https://app.example.com", result);
        Assert.Contains("https://admin.example.com", result);
    }

    [Fact]
    public void GetCorsAllowedOrigins_EnvSetWithSpaces_TrimmedAndSplit()
    {
        // Arrange
        SetEnv(EnvironmentConfig.CorsAllowedOrigins, "  https://a.com , https://b.com  ");

        // Act
        var result = EnvironmentConfig.GetCorsAllowedOrigins();

        // Assert
        Assert.Equal(2, result.Length);
        Assert.Contains("https://a.com", result);
        Assert.Contains("https://b.com", result);
    }

    [Fact]
    public void GetCorsAllowedOrigins_EnvNotSet_ReturnsEmptyArray()
    {
        // Arrange
        ClearEnv(EnvironmentConfig.CorsAllowedOrigins);

        // Act
        var result = EnvironmentConfig.GetCorsAllowedOrigins();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void GetCorsAllowedOrigins_EnvEmpty_ReturnsEmptyArray()
    {
        // Arrange
        SetEnv(EnvironmentConfig.CorsAllowedOrigins, "");

        // Act
        var result = EnvironmentConfig.GetCorsAllowedOrigins();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void GetSosPluginPath_EnvSet_ReturnsValue()
    {
        // Arrange
        SetEnv(EnvironmentConfig.SosPluginPath, "/path/to/libsosplugin.so");

        // Act
        var result = EnvironmentConfig.GetSosPluginPath();

        // Assert
        Assert.Equal("/path/to/libsosplugin.so", result);
    }

    [Fact]
    public void GetSosPluginPath_EnvNotSet_ReturnsNull()
    {
        // Arrange
        ClearEnv(EnvironmentConfig.SosPluginPath);

        // Act
        var result = EnvironmentConfig.GetSosPluginPath();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetPort_EnvSet_ReturnsEnvValue()
    {
        // Arrange
        SetEnv(EnvironmentConfig.Port, "8080");

        // Act
        var result = EnvironmentConfig.GetPort();

        // Assert
        Assert.Equal(8080, result);
    }

    [Fact]
    public void GetPort_EnvNotSet_ReturnsDefault()
    {
        // Arrange
        ClearEnv(EnvironmentConfig.Port);

        // Act
        var result = EnvironmentConfig.GetPort();

        // Assert
        Assert.Equal(5000, result);
    }

    [Fact]
    public void GetMaxRequestBodySize_EnvSet_ReturnsCalculatedBytes()
    {
        // Arrange
        SetEnv(EnvironmentConfig.MaxRequestBodySizeGb, "10");

        // Act
        var result = EnvironmentConfig.GetMaxRequestBodySize();

        // Assert
        Assert.Equal(10L * 1024 * 1024 * 1024, result); // 10 GB in bytes
    }

    [Fact]
    public void GetMaxRequestBodySize_EnvNotSet_ReturnsDefault()
    {
        // Arrange
        ClearEnv(EnvironmentConfig.MaxRequestBodySizeGb);

        // Act
        var result = EnvironmentConfig.GetMaxRequestBodySize();

        // Assert
        Assert.Equal(5L * 1024 * 1024 * 1024, result); // 5 GB in bytes
    }
}
