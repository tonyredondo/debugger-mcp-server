#nullable enable

using DebuggerMcp.McpTools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using DebuggerMcp.Sampling;
using Xunit;

namespace DebuggerMcp.Tests.McpTools;

public class AiAnalysisToolsDefaultsTests
{
    [Fact]
    public void Analyze_DefaultMaxIterations_Is100()
    {
        var method = typeof(CompactTools).GetMethod("Analyze");
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        var maxIterations = Assert.Single(parameters, p => p.Name == "maxIterations");

        Assert.True(maxIterations.HasDefaultValue);
        Assert.Equal(100, maxIterations.DefaultValue);
    }

    [Fact]
    public void AiAnalysisTools_DefaultMaxIterations_Is100()
    {
        var method = typeof(AiAnalysisTools).GetMethod("AnalyzeCrashWithAiAsync");
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        var maxIterations = Assert.Single(parameters, p => p.Name == "maxIterations");

        Assert.True(maxIterations.HasDefaultValue);
        Assert.Equal(100, maxIterations.DefaultValue);
    }

    [Fact]
    public void AiAnalysisOrchestrator_DefaultMaxIterations_Is100()
    {
        var orchestrator = new DebuggerMcp.Analysis.AiAnalysisOrchestrator(
            samplingClient: new StubSamplingClient(),
            logger: NullLogger<DebuggerMcp.Analysis.AiAnalysisOrchestrator>.Instance);

        Assert.Equal(100, orchestrator.MaxIterations);
    }

    private sealed class StubSamplingClient : ISamplingClient
    {
        public bool IsSamplingSupported => true;

        public bool IsToolUseSupported => true;

        public Task<CreateMessageResult> RequestCompletionAsync(CreateMessageRequestParams request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
