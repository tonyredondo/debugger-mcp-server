using System.Text.Json;
using DebuggerMcp.McpTools;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Xunit;

namespace DebuggerMcp.Tests.Sampling;

public class McpToolSchemaInjectionTests
{
    [Fact]
    public void AnalyzeToolSchema_DoesNotIncludeInjectedParameters()
    {
        var sessionManager = new DebuggerSessionManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new CompactTools(sessionManager, symbolManager, watchStore, NullLoggerFactory.Instance);

        var method = typeof(CompactTools).GetMethod(nameof(CompactTools.Analyze));
        Assert.NotNull(method);

        var mcpTool = McpServerTool.Create(method!, tools);
        JsonElement schema = mcpTool.ProtocolTool.InputSchema;

        Assert.True(schema.ValueKind == JsonValueKind.Object);
        Assert.True(schema.TryGetProperty("properties", out var properties));

        Assert.True(properties.TryGetProperty("kind", out _));

        Assert.False(properties.TryGetProperty("server", out _));
        Assert.False(properties.TryGetProperty("cancellationToken", out _));
    }
}

