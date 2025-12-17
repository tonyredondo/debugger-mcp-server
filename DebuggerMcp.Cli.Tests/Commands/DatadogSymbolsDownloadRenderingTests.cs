using System.Reflection;
using DebuggerMcp.Cli.Display;
using Spectre.Console.Testing;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Commands;

public class DatadogSymbolsDownloadRenderingTests
{
    private static void InvokeRenderSummary(ConsoleOutput output, string json)
    {
        var method = typeof(DebuggerMcp.Cli.Program).GetMethod(
            "RenderDatadogSymbolsResultSummary",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object?[] { output, json });
    }

    [Fact]
    public void RenderDatadogSymbolsResultSummary_PrintsBuildArtifactsAndPaths()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var json = """
        {
          "success": true,
          "buildId": 192511,
          "buildNumber": "Override-192511",
          "buildUrl": "https://dev.azure.com/datadoghq/dd-trace-dotnet/_build/results?buildId=192511",
          "downloadedArtifacts": ["a1","a2"],
          "symbolDirectory": "/app/dumps/u/.symbols_x/.datadog/symbols-linux-x64",
          "nativeSymbolsDirectory": "/app/dumps/u/.symbols_x/.datadog/symbols-linux-x64/linux-x64",
          "managedSymbolsDirectory": "/app/dumps/u/.symbols_x/.datadog/symbols-linux-x64/net6.0",
          "filesExtracted": 15,
          "shaMismatch": false,
          "source": "https://dev.azure.com/datadoghq/dd-trace-dotnet/_build/results?buildId=192511",
          "platform": { "os": "Linux", "architecture": "x64", "isAlpine": false, "suffix": "linux-x64" },
          "targetFramework": "net6.0",
          "symbolsLoaded": { "success": true, "nativeSymbolsLoaded": 5, "managedSymbolPaths": 1, "commandsExecuted": 7 },
          "error": null
        }
        """;

        InvokeRenderSummary(output, json);

        Assert.Contains("Build", console.Output);
        Assert.Contains("Build ID", console.Output);
        Assert.Contains("Artifacts", console.Output);
        Assert.Contains("a1", console.Output);
        Assert.Contains("Server Directories", console.Output);
        Assert.Contains("Symbol Root", console.Output);
        Assert.Contains("Symbol Loading", console.Output);
        Assert.Contains("Native Symbols", console.Output);
        Assert.Contains("Managed Symbol Paths", console.Output);
        Assert.Contains("Source:", console.Output);
    }
}

