using System.Text.Json;
using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

public class OpenRouterClientExtractTextTests
{
    [Theory]
    [InlineData("""["hello"]""", "hello")]
    [InlineData("""[{"type":"text","text":"hello"}]""", "hello")]
    [InlineData("""[{"type":"output_text","text":"hello"}]""", "hello")]
    public void ExtractText_WithSupportedArrayShapes_ReturnsText(string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);
        var text = OpenRouterClient.ExtractText(doc.RootElement);
        Assert.Equal(expected, text);
    }
}
