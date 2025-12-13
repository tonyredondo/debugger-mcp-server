using System.Reflection;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Models;
using DebuggerMcp.Cli.Shell;
using Spectre.Console;
using Spectre.Console.Testing;

namespace DebuggerMcp.Cli.Tests;

/// <summary>
/// Reflection-driven coverage tests for private helper methods in <see cref="DebuggerMcp.Cli.Program"/>.
/// </summary>
public class ProgramPrivateMethodCoverageTests
{
    [Fact]
    public void ShowDatadogSymbolsHelp_WritesHelpText()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        InvokePrivateVoid("ShowDatadogSymbolsHelp", output);

        Assert.Contains("Datadog Symbols", console.Output);
        Assert.Contains("SUBCOMMANDS", console.Output);
        Assert.Contains("download", console.Output);
    }

    [Theory]
    [InlineData("{\"error\":\"boom\"}", true)]
    [InlineData("{\"success\":true}", false)]
    [InlineData("Error: failed", true)]
    [InlineData("failed to do thing", true)]
    [InlineData("ok", false)]
    public void IsErrorResult_RecognizesErrors(string input, bool expected)
    {
        var result = InvokePrivate<bool>("IsErrorResult", input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void HandleSet_WithVerbose_UpdatesStateAndOutput()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();

        InvokePrivateVoid("HandleSet", new[] { "verbose", "true" }, output, state);

        Assert.True(state.Settings.Verbose);
        Assert.Contains("Verbose mode", console.Output);
    }

    [Fact]
    public void ShowStatus_WithServerInfo_WritesStatusSummary()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.Settings.UserId = "u";
        state.Settings.Verbose = true;
        state.Settings.OutputFormat = OutputFormat.Text;
        state.ServerInfo = new ServerInfo
        {
            Description = "TestHost",
            DebuggerType = "LLDB",
            DotNetVersion = "10.0.0",
            IsDocker = true,
            IsAlpine = true,
            Architecture = "arm64",
            InstalledRuntimes = ["10.0.0", "9.0.0"]
        };

        InvokePrivateVoid("ShowStatus", output, state);

        Assert.Contains("Current Status", console.Output);
        Assert.Contains("TestHost", console.Output);
        Assert.Contains("Supported .NET", console.Output);
    }

    [Fact]
    public void CheckDumpServerCompatibility_WhenAlpineDumpOnGlibcServer_WritesWarning()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState
        {
            ServerInfo = new ServerInfo
            {
                Description = "glibc-host",
                IsAlpine = false,
                Architecture = "arm64"
            }
        };

        InvokePrivateVoid("CheckDumpServerCompatibility", true, "arm64", state, output);

        Assert.Contains("INCOMPATIBLE", console.Output);
        Assert.Contains("Alpine", console.Output);
    }

    [Fact]
    public void BuildSymbolTree_WithPaths_BuildsRenderableTree()
    {
        var console = new TestConsole();

        var tree = InvokePrivate<Tree>(
            "BuildSymbolTree",
            new List<string>
            {
                "net6.0/Datadog.Trace.pdb",
                "linux-arm64/native/libddprof.debug",
                "linux-arm64/native/libddtrace.so"
            },
            "dump-123");

        console.Write(tree);

        Assert.Contains("Datadog.Trace.pdb", console.Output);
        Assert.Contains("linux-arm64", console.Output);
        Assert.Contains("libddtrace.so", console.Output);
    }

    private static T InvokePrivate<T>(string name, params object[] args)
    {
        var method = typeof(DebuggerMcp.Cli.Program).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, args);
        Assert.NotNull(result);
        return (T)result!;
    }

    private static void InvokePrivateVoid(string name, params object[] args)
    {
        var method = typeof(DebuggerMcp.Cli.Program).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, args);
    }
}
