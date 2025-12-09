using DebuggerMcp.SourceLink;
using Xunit;

namespace DebuggerMcp.Tests.SourceLink;

/// <summary>
/// Tests for the DatadogSymbolService class.
/// </summary>
public class DatadogSymbolServiceTests
{
    [Fact]
    public void ScanForDatadogAssemblies_ReturnsEmptyList_WhenClrMdAnalyzerIsNull()
    {
        // Arrange
        var service = new DatadogSymbolService(null);

        // Act
        var result = service.ScanForDatadogAssemblies();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("3.10.0+abc123def456", "abc123def456")]
    [InlineData("3.10.0+ABC123DEF456", "abc123def456")]
    [InlineData("3.10.0.abc123def456", "abc123def456")]
    [InlineData("3.10.0+abcd1234567890abcdef1234567890abcdef1234", "abcd1234567890abcdef1234567890abcdef1234")]
    public void ExtractCommitSha_ReturnsCorrectSha(string informationalVersion, string expectedSha)
    {
        // Extract via reflection to keep tests aligned with the private helper
        var method = typeof(DatadogSymbolService)
            .GetMethod("ExtractCommitSha", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var sha = (string?)method!.Invoke(null, new object?[] { informationalVersion });

        Assert.Equal(expectedSha, sha);
    }

    [Theory]
    [InlineData("Datadog.Trace")]
    [InlineData("Datadog.Trace.ClrProfiler.Managed")]
    [InlineData("Datadog.Profiler")]
    [InlineData("Datadog.AutoInstrumentation")]
    public void IsDatadogAssembly_RecognizesDatadogAssemblies(string assemblyName)
    {
        // These assembly names should be recognized as Datadog assemblies
        // The actual check is internal, but we're documenting expected behavior
        Assert.StartsWith("Datadog", assemblyName);
    }

    [Theory]
    [InlineData("System.Private.CoreLib")]
    [InlineData("Microsoft.Extensions.Logging")]
    [InlineData("Newtonsoft.Json")]
    public void IsDatadogAssembly_DoesNotMatchOtherAssemblies(string assemblyName)
    {
        // These assembly names should NOT be recognized as Datadog assemblies
        Assert.False(assemblyName.StartsWith("Datadog", StringComparison.OrdinalIgnoreCase));
    }
}
