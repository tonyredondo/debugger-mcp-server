using System.Reflection;
using DebuggerMcp.Cli.Analysis;
using DebuggerMcp.Cli.Client;
using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Shell;
using DebuggerMcp.Cli.Tests.Collections;
using Spectre.Console.Testing;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Commands;

[Collection(nameof(EnvironmentVariableCollection))]
public class AnalyzeCommandTests
{
    [Fact]
    public async Task HandleAnalyzeAsync_WithoutOutputFile_PrintsError()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "LLDB");
        state.SetDumpLoaded("dump-123");

        var mcpClient = CreateConnectedMcpClientForTests();

        var task = (Task)InvokePrivate(
            "HandleAnalyzeAsync",
            new object?[]
            {
                new[] { "ai" },
                output,
                state,
                mcpClient
            });

        await task;

        Assert.Contains("Output file is required", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("analyze ai -o", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAnalyzeAsync_WithRefreshForNonAiType_PrintsError()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.SetSession("session-123", "LLDB");
        state.SetDumpLoaded("dump-123");

        var mcpClient = CreateConnectedMcpClientForTests();

        var outputFile = Path.Combine(CreateTempDirectory(), "crash.json");

        var task = (Task)InvokePrivate(
            "HandleAnalyzeAsync",
            new object?[]
            {
                new[] { "crash", "--refresh", "-o", outputFile },
                output,
                state,
                mcpClient
            });

        await task;

        Assert.Contains("only supported", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("analyze ai", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAnalyzeAsync_AiWithCacheHit_WritesCachedJson()
    {
        var cacheRoot = CreateTempDirectory();
        var outputDir = CreateTempDirectory();
        var previousEnv = Environment.GetEnvironmentVariable(AiAnalysisCache.CacheDirEnvVar);

        Environment.SetEnvironmentVariable(AiAnalysisCache.CacheDirEnvVar, cacheRoot);
        try
        {
            var console = new TestConsole();
            var output = new ConsoleOutput(console);
            var state = new ShellState();
            state.Settings.Llm.Provider = "openai";
            state.Settings.Llm.OpenAiModel = "gpt-5.2";
            state.Settings.Llm.OpenAiReasoningEffort = "medium";

            state.SetConnected("http://localhost:5000");
            state.SetSession("session-123", "LLDB");
            state.SetDumpLoaded("dump-123");

            var cache = new AiAnalysisCache(cacheRoot);
            var cacheKey = AiAnalysisCacheKey.Create(state.DumpId!, state.Settings.Llm);
            const string cachedJson = "{\"metadata\":{\"dumpId\":\"dump-123\"},\"analysis\":{\"aiAnalysis\":{\"rootCause\":\"cached\"}}}";
            await cache.WriteAsync(cacheKey, cachedJson);

            var mcpClient = CreateConnectedMcpClientForTests();
            var outputFile = Path.Combine(outputDir, "ai.json");

            var task = (Task)InvokePrivate(
                "HandleAnalyzeAsync",
                new object?[]
                {
                    new[] { "ai", "-o", outputFile },
                    output,
                    state,
                    mcpClient
                });

            await task;

            Assert.True(File.Exists(outputFile));
            var written = await File.ReadAllTextAsync(outputFile);
            Assert.Equal(cachedJson, written);
            Assert.Contains("cached AI analysis", console.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AiAnalysisCache.CacheDirEnvVar, previousEnv);
        }
    }

    private static McpClient CreateConnectedMcpClientForTests()
    {
        var client = new McpClient();

        var httpClientField = typeof(McpClient).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance);
        var messageEndpointField = typeof(McpClient).GetField("_messageEndpoint", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(httpClientField);
        Assert.NotNull(messageEndpointField);

        httpClientField!.SetValue(client, new HttpClient());
        messageEndpointField!.SetValue(client, "/mcp/messages");

        Assert.True(client.IsConnected);
        return client;
    }

    private static object InvokePrivate(string name, object?[] args)
    {
        var method = typeof(DebuggerMcp.Cli.Program).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, args)!;
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dbg-mcp-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
