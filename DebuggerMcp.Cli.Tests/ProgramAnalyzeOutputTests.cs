using System.Reflection;
using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Shell;
using Spectre.Console.Testing;

namespace DebuggerMcp.Cli.Tests;

public class ProgramAnalyzeOutputTests
{
    [Fact]
    public async Task RunAnalysisAsync_WithOutputFile_WritesFileAndDoesNotPrintFullResult()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();

        var tempFile = Path.Combine(Path.GetTempPath(), $"dbg-mcp-analyze-{Guid.NewGuid():N}.json");

        try
        {
            var method = typeof(DebuggerMcp.Cli.Program).GetMethod("RunAnalysisAsync", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            Func<Task<string>> analyze = () => Task.FromResult("{\"ok\":true}");
            var task = (Task)method!.Invoke(null, new object?[] { output, "AI Crash Analysis", analyze, state, tempFile })!;
            await task;

            Assert.True(File.Exists(tempFile));
            var fileText = await File.ReadAllTextAsync(tempFile);
            Assert.Contains("\"ok\":true", fileText);

            Assert.Contains("Saved to:", console.Output);
            Assert.DoesNotContain("{\"ok\":true}", console.Output);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
