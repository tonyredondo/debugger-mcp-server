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

    [Fact]
    public void RenderDatadogSymbolsResultSummary_WhenShaMismatch_PrintsWarning()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var json = """
        {
          "success": true,
          "buildId": 1,
          "buildUrl": "https://example/build/1",
          "downloadedArtifacts": [],
          "symbolDirectory": "/s",
          "filesExtracted": 1,
          "shaMismatch": true,
          "error": null
        }
        """;

        InvokeRenderSummary(output, json);

        Assert.Contains("SHA mismatch", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderDatadogSymbolsResultSummary_WhenPdbsPatchedAndVerified_PrintsSuccessAndFileList()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var json = """
        {
          "success": true,
          "buildId": 1,
          "buildUrl": "https://example/build/1",
          "downloadedArtifacts": [],
          "symbolDirectory": "/s",
          "filesExtracted": 1,
          "pdbsPatched": {
            "patched": 2,
            "verified": 2,
            "files": [
              { "file": "a.pdb", "verified": true },
              { "file": "b.pdb", "verified": true }
            ]
          },
          "error": null
        }
        """;

        InvokeRenderSummary(output, json);

        Assert.Contains("Patched and verified", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("✓ a.pdb", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("✓ b.pdb", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderDatadogSymbolsResultSummary_WhenPdbsPatchedPartiallyVerified_PrintsWarning()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var json = """
        {
          "success": true,
          "buildId": 1,
          "buildUrl": "https://example/build/1",
          "downloadedArtifacts": [],
          "symbolDirectory": "/s",
          "filesExtracted": 1,
          "pdbsPatched": {
            "patched": 3,
            "verified": 1,
            "files": [
              { "file": "a.pdb", "verified": true },
              { "file": "b.pdb", "verified": false }
            ]
          },
          "error": null
        }
        """;

        InvokeRenderSummary(output, json);

        Assert.Contains("only 1 verified", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("✗ b.pdb", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderDatadogSymbolsResultSummary_WhenPdbsPatchedButNotVerified_PrintsError()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var json = """
        {
          "success": true,
          "buildId": 1,
          "buildUrl": "https://example/build/1",
          "downloadedArtifacts": [],
          "symbolDirectory": "/s",
          "filesExtracted": 1,
          "pdbsPatched": {
            "patched": 1,
            "verified": 0,
            "files": [
              { "file": "a.pdb", "verified": false }
            ]
          },
          "error": null
        }
        """;

        InvokeRenderSummary(output, json);

        Assert.Contains("verification failed", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("✗ a.pdb", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderDatadogSymbolsResultSummary_WhenSourceMissing_FallsBackToBuildUrl()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var json = """
        {
          "success": true,
          "buildId": 1,
          "buildUrl": "https://example/build/1",
          "downloadedArtifacts": [],
          "symbolDirectory": "/s",
          "filesExtracted": 1,
          "shaMismatch": false,
          "error": null
        }
        """;

        InvokeRenderSummary(output, json);

        Assert.Contains("Source: https://example/build/1", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderDatadogSymbolsResultSummary_WhenResultIsNotObject_PrintsError()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        InvokeRenderSummary(output, "[]");

        Assert.Contains("Unexpected result format", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderDatadogSymbolsResultSummary_WhenJsonInvalid_PrintsError()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        InvokeRenderSummary(output, "{not json");

        Assert.Contains("Error parsing result", console.Output, StringComparison.OrdinalIgnoreCase);
    }
}
