using System.Text.Json;
using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

[Collection("NonParallelConsole")]
public class McpSamplingCreateMessageHandlerTraceAllowlistTests
{
    [Fact]
    public async Task HandleAsync_WhenTraceDirIsUnderGitRootButOutsideCwd_AllowsIt()
    {
        var gitRoot = FindGitRoot(Environment.CurrentDirectory);
        Assert.False(string.IsNullOrWhiteSpace(gitRoot));

        var originalCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = Path.Combine(gitRoot!, "DebuggerMcp.Cli.Tests");
        var traceParentToDelete = string.Empty;
        try
        {
            var settings = new DebuggerMcp.Cli.Configuration.LlmSettings { Provider = "openai", OpenAiModel = "gpt-5.2" };

            var traceDir = Path.Combine(gitRoot!, "logs-alpine-test", Guid.NewGuid().ToString("N"), "llm-http");
            traceParentToDelete = Path.GetDirectoryName(traceDir) ?? string.Empty;
            var systemPrompt = $$"""
            You are a test.

            [dbg-mcp-client-directive]
            {"httpTraceDir":"{{traceDir.Replace("\\", "\\\\")}}","maxFileBytes":1000}
            [/dbg-mcp-client-directive]
            """;

            LlmTraceStore? seenTraceStore = null;
            var handler = new McpSamplingCreateMessageHandler(
                settings,
                (request, traceStore, _) =>
                {
                    seenTraceStore = traceStore;
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

            Assert.NotNull(seenTraceStore);
            Assert.Equal(Path.GetFullPath(traceDir), seenTraceStore!.DirectoryPath);
            Assert.True(Directory.Exists(seenTraceStore.DirectoryPath));
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            if (!string.IsNullOrWhiteSpace(traceParentToDelete))
            {
                try
                {
                    Directory.Delete(traceParentToDelete, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    private static string? FindGitRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDirectory));
        for (var i = 0; i < 25 && dir != null; i++)
        {
            if (dir.EnumerateFileSystemInfos(".git").Any())
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
