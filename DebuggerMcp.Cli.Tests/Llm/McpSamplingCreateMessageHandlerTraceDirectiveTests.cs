using System.Text.Json;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

[Collection("NonParallelConsole")]
public class McpSamplingCreateMessageHandlerTraceDirectiveTests
{
    [Fact]
    public async Task HandleAsync_WhenSystemPromptContainsClientDirective_PassesTraceStoreAndStripsDirectiveFromPrompt()
    {
        var settings = new LlmSettings { Provider = "openai", OpenAiModel = "gpt-5.2" };

        var traceDir = Path.Combine(Path.GetTempPath(), "dbg-mcp-test-trace", Guid.NewGuid().ToString("N"));
        var systemPrompt = $$"""
        You are a test.

        [dbg-mcp-client-directive]
        {"httpTraceDir":"{{traceDir.Replace("\\", "\\\\")}}","maxFileBytes":12345}
        [/dbg-mcp-client-directive]
        """;

        var seenTraceStores = new List<LlmTraceStore?>();
        var seenRequests = new List<ChatCompletionRequest>();

        var handler = new McpSamplingCreateMessageHandler(
            settings,
            (request, traceStore, _) =>
            {
                seenRequests.Add(request);
                seenTraceStores.Add(traceStore);
                return Task.FromResult(new ChatCompletionResult { Text = "ok" });
            },
            progress: null);

        using var doc = JsonDocument.Parse($$"""
        {
          "systemPrompt": {{JsonSerializer.Serialize(systemPrompt)}},
          "messages": [
            { "role": "user", "content": "Hello" }
          ]
        }
        """);

        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);
        _ = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        Assert.Equal(2, seenTraceStores.Count);
        Assert.NotNull(seenTraceStores[0]);
        Assert.Same(seenTraceStores[0], seenTraceStores[1]);

        var expectedFull = Path.GetFullPath(traceDir);
        Assert.Equal(expectedFull, seenTraceStores[0]!.DirectoryPath);

        Assert.NotEmpty(seenRequests);
        Assert.DoesNotContain(
            seenRequests.SelectMany(r => r.Messages).Select(m => m.Content ?? string.Empty),
            c => c.Contains("dbg-mcp-client-directive", StringComparison.OrdinalIgnoreCase));

        try
        {
            Directory.Delete(expectedFull, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}

