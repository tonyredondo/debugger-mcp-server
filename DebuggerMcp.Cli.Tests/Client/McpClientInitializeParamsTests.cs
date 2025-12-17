using System.Text.Json;
using DebuggerMcp.Cli.Client;

namespace DebuggerMcp.Cli.Tests.Client;

public class McpClientInitializeParamsTests
{
    [Fact]
    public void BuildInitializeParams_IncludesSamplingToolsCapability()
    {
        var parameters = McpClient.BuildInitializeParams();
        var json = JsonSerializer.Serialize(parameters);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("protocolVersion", out var pv));
        Assert.Equal(JsonValueKind.String, pv.ValueKind);

        Assert.True(root.TryGetProperty("capabilities", out var caps));
        Assert.True(caps.TryGetProperty("sampling", out var sampling));
        Assert.True(sampling.TryGetProperty("tools", out var tools));
        Assert.Equal(JsonValueKind.Object, tools.ValueKind);
    }
}

