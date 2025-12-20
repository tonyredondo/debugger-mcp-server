using System.Reflection;
using System.Text.Json;
using DebuggerMcp.Analysis;
using DebuggerMcp.Sampling;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

public class AiAnalysisOrchestratorAdditionalCoverageTests
{
    private sealed class StubSamplingClient(bool isSamplingSupported, bool isToolUseSupported) : ISamplingClient
    {
        public bool IsSamplingSupported { get; } = isSamplingSupported;

        public bool IsToolUseSupported { get; } = isToolUseSupported;

        public Task<CreateMessageResult> RequestCompletionAsync(CreateMessageRequestParams request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void IsSamplingAvailable_ReflectsSamplingCapability(bool supported, bool expected)
    {
        var orchestrator = new AiAnalysisOrchestrator(
            new StubSamplingClient(isSamplingSupported: supported, isToolUseSupported: supported),
            NullLogger<AiAnalysisOrchestrator>.Instance);

        Assert.Equal(expected, orchestrator.IsSamplingAvailable);
    }

    [Fact]
    public void ExecuteReportGet_WithValidPath_ReturnsSection()
    {
        var execute = typeof(AiAnalysisOrchestrator).GetMethod("ExecuteReportGet", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(execute);

        const string reportJson = "{\"analysis\":{\"exception\":{\"type\":\"System.MissingMethodException\"}}}";
        using var doc = JsonDocument.Parse("{\"path\":\"analysis.exception\"}");

        var result = execute!.Invoke(null, new object[] { doc.RootElement, reportJson }) as string;
        Assert.NotNull(result);
        Assert.Contains("MissingMethodException", result!, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteReportGet_WhenMissingPath_ThrowsArgumentException()
    {
        var execute = typeof(AiAnalysisOrchestrator).GetMethod("ExecuteReportGet", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(execute);

        const string reportJson = "{\"analysis\":{\"exception\":{\"type\":\"System.MissingMethodException\"}}}";
        using var doc = JsonDocument.Parse("{\"limit\":10}");

        var ex = Assert.Throws<TargetInvocationException>(() => execute!.Invoke(null, new object[] { doc.RootElement, reportJson }));
        Assert.IsType<ArgumentException>(ex.InnerException);
    }
}

