using System.Reflection;
using DebuggerMcp.Analysis;
using DebuggerMcp.SourceLink;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp.Tests.SourceLink;

[Collection("NonParallelEnvironment")]
public class DatadogSymbolServiceTests
{
    [Theory]
    [InlineData("3.31.0+14fd3a2f7f3f1b2c3d4e5f6a7b8c9d0e1f2a3b4c", "3.31.0")]
    [InlineData("3.31.0.14fd3a2f7f3f1b2c3d4e5f6a7b8c9d0e1f2a3b4c", "3.31.0")]
    [InlineData("3.31.0", "3.31.0")]
    [InlineData(null, null)]
    public void DatadogAssemblyInfo_Version_ExtractsVersionPart(string? informationalVersion, string? expected)
    {
        var info = new DatadogAssemblyInfo { InformationalVersion = informationalVersion };
        Assert.Equal(expected, info.Version);
    }

    [Fact]
    public void DatadogSymbolPreparationResult_SymbolsLoaded_ReflectsLoadResult()
    {
        var result = new DatadogSymbolPreparationResult
        {
            LoadResult = new SymbolLoadResult { Success = true }
        };

        Assert.True(result.SymbolsLoaded);

        result.LoadResult.Success = false;
        Assert.False(result.SymbolsLoaded);
    }

    [Theory]
    [InlineData("3.10.0+ABC123def456", "abc123def456")]
    [InlineData("3.10.0.abc123def456", "abc123def456")]
    [InlineData("3.10.0.abc123def456-extra", null)]
    [InlineData("3.10.0", null)]
    [InlineData(null, null)]
    public void ExtractCommitSha_ParsesExpectedFormats(string? informationalVersion, string? expected)
    {
        var extract = typeof(DatadogSymbolService)
            .GetMethod("ExtractCommitSha", BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(extract);

        var sha = (string?)extract!.Invoke(null, [informationalVersion]);
        Assert.Equal(expected, sha);
    }

    [Theory]
    [InlineData("Datadog.Trace", true)]
    [InlineData("Datadog.Trace.ClrProfiler.Managed", true)]
    [InlineData("datadog.profiler", true)]
    [InlineData("Other.Assembly", false)]
    public void IsDatadogAssembly_MatchesKnownPrefixes(string assemblyName, bool expected)
    {
        var isDatadog = typeof(DatadogSymbolService)
            .GetMethod("IsDatadogAssembly", BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(isDatadog);

        var actual = (bool)isDatadog!.Invoke(null, [assemblyName])!;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task PrepareSymbolsAsync_WhenDisabled_ReturnsMessageAndDoesNotThrow()
    {
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", "false");

            var service = new DatadogSymbolService(clrMdAnalyzer: null, logger: NullLogger.Instance);
            var result = await service.PrepareSymbolsAsync(
                platform: new PlatformInfo { Os = "Linux", Architecture = "x64" },
                symbolsOutputDirectory: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                executeCommand: _ => "",
                loadIntoDebugger: false,
                forceVersion: false,
                ct: CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("disabled", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", original);
        }
    }

    [Fact]
    public async Task PrepareSymbolsAsync_WhenEnabledButNoAssembliesFound_ReturnsSuccess()
    {
        var original = Environment.GetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", "true");

            // With no ClrMD analyzer, the scan returns empty and should short-circuit.
            var service = new DatadogSymbolService(clrMdAnalyzer: null, logger: NullLogger.Instance);
            var result = await service.PrepareSymbolsAsync(
                platform: new PlatformInfo { Os = "Linux", Architecture = "x64" },
                symbolsOutputDirectory: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                executeCommand: _ => "",
                loadIntoDebugger: false,
                forceVersion: false,
                ct: CancellationToken.None);

            Assert.True(result.Success);
            Assert.Contains("No Datadog assemblies", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATADOG_TRACE_SYMBOLS_ENABLED", original);
        }
    }

    [Fact]
    public void ExtractModuleGuidsFromDump_WhenAnalyzerIsNull_ReturnsEmpty()
    {
        var service = new DatadogSymbolService(clrMdAnalyzer: null, logger: NullLogger.Instance);

        var method = typeof(DatadogSymbolService)
            .GetMethod("ExtractModuleGuidsFromDump", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = (Dictionary<string, Guid>)method!.Invoke(service, [new List<DatadogAssemblyInfo>()])!;
        Assert.Empty(result);
    }
}
