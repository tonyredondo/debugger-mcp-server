using System.Reflection;
using DebuggerMcp.Cli.Llm;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Models;
using DebuggerMcp.Cli.Client;
using DebuggerMcp.Cli.Shell;
using DebuggerMcp.Cli.Shell.Transcript;
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
    [InlineData("{\"error\":null}", false)]
    [InlineData("{\"error\":false}", false)]
    [InlineData("{\"error\":true}", true)]
    [InlineData("{\"error\":{}}", true)]
    [InlineData("{\"Error\":\"0x1234\"}", false)]
    [InlineData("{\"Error\":\"boom\"}", true)]
    [InlineData("[]", false)]
    [InlineData("{not json", false)]
    [InlineData("", false)]
    [InlineData("{\"success\":true}", false)]
    [InlineData("Error: failed", true)]
    [InlineData("failed to do thing", true)]
    [InlineData("ok", false)]
    public void IsErrorResult_RecognizesErrors(string input, bool expected)
    {
        var result = InvokePrivate<bool>("IsErrorResult", input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Session ID not found", true)]
    [InlineData("session expired", true)]
    [InlineData("Session does not exist", true)]
    [InlineData("boom", false)]
    public void IsSessionNotFoundError_RecognizesCommonMessages(string message, bool expected)
    {
        var ex = new Exception(message);
        var result = InvokePrivate<bool>("IsSessionNotFoundError", ex);
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
    public void HandleSet_WhenMissingArgs_PrintsUsage()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();

        InvokePrivateVoid("HandleSet", new[] { "verbose" }, output, state);

        Assert.Contains("Usage: set", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Available settings", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HandleSet_WithOutputJson_UpdatesStateAndOutput()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();

        InvokePrivateVoid("HandleSet", new[] { "output", "json" }, output, state);

        Assert.Equal(OutputFormat.Json, state.Settings.OutputFormat);
        Assert.Contains("Output format", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HandleSet_WithTimeoutValid_UpdatesState()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();

        InvokePrivateVoid("HandleSet", new[] { "timeout", "15" }, output, state);

        Assert.Equal(15, state.Settings.TimeoutSeconds);
        Assert.Contains("Timeout: 15", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    public void HandleSet_WithTimeoutInvalid_PrintsError(string value)
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();

        InvokePrivateVoid("HandleSet", new[] { "timeout", value }, output, state);

        Assert.Contains("Invalid timeout value", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HandleSet_WithUserId_UpdatesState()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();

        InvokePrivateVoid("HandleSet", new[] { "user-id", "u123" }, output, state);

        Assert.Equal("u123", state.Settings.UserId);
        Assert.Contains("User ID", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HandleSet_WithUnknownSetting_PrintsError()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();

        InvokePrivateVoid("HandleSet", new[] { "wat", "1" }, output, state);

        Assert.Contains("Unknown setting", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Available settings", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShowStatus_WithServerInfo_WritesStatusSummary()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        state.DebuggerType = "LLDB";
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
        Assert.Contains("Session Debugger", console.Output);
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

    [Fact]
    public void ShowWelcomeBanner_WritesBanner()
    {
        var console = new TestConsole();

        InvokePrivateVoid("ShowWelcomeBanner", console);

        Assert.Contains("Debugger MCP CLI v", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendTranscriptCapped_WhenLineTooLong_AppendsTruncationMarker()
    {
        var sb = new System.Text.StringBuilder();

        InvokePrivateVoid("AppendTranscriptCapped", sb, new string('x', 500), 50);

        Assert.Contains("output capture truncated", sb.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendTranscriptCapped_WhenMaxCharsNonPositive_DoesNotAppend()
    {
        var sb = new System.Text.StringBuilder();

        InvokePrivateVoid("AppendTranscriptCapped", sb, "hello", 0);

        Assert.Empty(sb.ToString());
    }

    [Fact]
    public void AppendTranscriptCapped_WhenLineEmpty_AppendsBlankLine()
    {
        var sb = new System.Text.StringBuilder();

        InvokePrivateVoid("AppendTranscriptCapped", sb, string.Empty, 50);

        Assert.Equal(Environment.NewLine, sb.ToString());
    }

    [Fact]
    public void AppendTranscriptCapped_WhenLineFits_AppendsLine()
    {
        var sb = new System.Text.StringBuilder();

        InvokePrivateVoid("AppendTranscriptCapped", sb, "hello", 50);

        Assert.Equal("hello" + Environment.NewLine, sb.ToString());
    }

    [Fact]
    public void TruncateForTranscript_WhenMaxCharsNonPositive_ReturnsEmpty()
    {
        var result = InvokePrivate<string>("TruncateForTranscript", "hello", 0);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void TruncateForTranscript_WhenTextShorterThanLimit_ReturnsOriginal()
    {
        var result = InvokePrivate<string>("TruncateForTranscript", "hello", 10);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void TruncateForTranscript_WhenTextTooLong_AppendsMarker()
    {
        var result = InvokePrivate<string>("TruncateForTranscript", "hello world", 5);
        Assert.Contains("hello", result, StringComparison.Ordinal);
        Assert.Contains("truncated", result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("a.dll")]
    [InlineData("a.exe")]
    [InlineData("a.dylib")]
    [InlineData("a.dwarf")]
    [InlineData("a.dSYM")]
    [InlineData("a.dbg")]
    [InlineData("a.json")]
    [InlineData("a.xml")]
    public void GetFileIcon_ForKnownExtensions_ReturnsNonEmpty(string file)
    {
        var icon = InvokePrivate<string>("GetFileIcon", file);
        Assert.False(string.IsNullOrWhiteSpace(icon));
    }

    [Fact]
    public void RegisterMcpSamplingHandlers_RegistersServerHandler()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();
        var mcpClient = new McpClient();

        InvokePrivateVoid("RegisterMcpSamplingHandlers", output, state, mcpClient);

        Assert.True(mcpClient.UnregisterServerRequestHandler("sampling/createMessage"));
    }

    [Fact]
    public void RegisterMcpSamplingHandlers_WhenClientNull_WritesWarning()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();

        InvokePrivateVoid("RegisterMcpSamplingHandlers", output, state, null!);

        Assert.Contains("sampling", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Failed", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteLlmErrorContext_PrintsProviderDiagnostics()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var settings = new LlmSettings
        {
            Provider = "openai",
            OpenAiModel = "gpt-test",
            OpenAiBaseUrl = "https://example.base",
            AnthropicBaseUrl = "https://example.base",
            OpenRouterBaseUrl = "https://example.base"
        };

        InvokePrivateVoid("WriteLlmErrorContext", output, settings);

        Assert.Contains("LLM provider", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LLM base URL", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("example.base", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteLlmErrorContext_WhenAnthropic_PrintsAnthropicBaseUrl()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var settings = new LlmSettings
        {
            Provider = "anthropic",
            AnthropicModel = "claude-test",
            AnthropicBaseUrl = "https://anthropic.example",
            OpenAiBaseUrl = "https://openai.example",
            OpenRouterBaseUrl = "https://openrouter.example"
        };

        InvokePrivateVoid("WriteLlmErrorContext", output, settings);

        Assert.Contains("anthropic.example", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteLlmErrorContext_WhenOpenRouter_PrintsOpenRouterBaseUrl()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var settings = new LlmSettings
        {
            Provider = "openrouter",
            OpenRouterModel = "openrouter/test",
            OpenRouterBaseUrl = "https://openrouter.example",
            OpenAiBaseUrl = "https://openai.example",
            AnthropicBaseUrl = "https://anthropic.example"
        };

        InvokePrivateVoid("WriteLlmErrorContext", output, settings);

        Assert.Contains("openrouter.example", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryRenderJsonError_WhenErrorIsString_PrintsMessageAndReturnsTrue()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var rendered = InvokePrivate<bool>("TryRenderJsonError", output, "{\"error\":\"boom\"}");

        Assert.True(rendered);
        Assert.Contains("boom", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryRenderJsonError_WhenErrorIsObjectMessage_PrintsMessageAndReturnsTrue()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var rendered = InvokePrivate<bool>("TryRenderJsonError", output, "{\"error\":{\"message\":\"boom\"}}");

        Assert.True(rendered);
        Assert.Contains("boom", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryRenderJsonError_WhenRootHasMessage_PrintsMessageAndReturnsTrue()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var rendered = InvokePrivate<bool>("TryRenderJsonError", output, "{\"message\":\"boom\"}");

        Assert.True(rendered);
        Assert.Contains("boom", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryRenderJsonError_WhenJsonInvalid_ReturnsFalse()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var rendered = InvokePrivate<bool>("TryRenderJsonError", output, "{not json");

        Assert.False(rendered);
    }

    [Fact]
    public void TryRenderJsonError_WhenJsonNotObject_ReturnsFalse()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var rendered = InvokePrivate<bool>("TryRenderJsonError", output, "[]");

        Assert.False(rendered);
    }

    [Fact]
    public void TryRenderJsonError_WhenNoErrorFields_ReturnsFalse()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);

        var rendered = InvokePrivate<bool>("TryRenderJsonError", output, "{\"success\":true}");

        Assert.False(rendered);
    }

    [Fact]
    public void BuildAgentToolArgsPreview_WhenJsonHasCommand_ReturnsCommandString()
    {
        var call = new ChatToolCall("c1", "exec", "{\"command\":\"bt\"}");
        var preview = InvokePrivate<string>("BuildAgentToolArgsPreview", call);
        Assert.Equal("bt", preview);
    }

    [Fact]
    public void BuildAgentToolArgsPreview_WhenArgumentsEmpty_ReturnsEmpty()
    {
        var call = new ChatToolCall("c1", "exec", " ");
        var preview = InvokePrivate<string>("BuildAgentToolArgsPreview", call);
        Assert.Equal(string.Empty, preview);
    }

    [Fact]
    public void BuildAgentToolArgsPreview_WhenJsonDoesNotContainCommand_ReturnsJson()
    {
        var call = new ChatToolCall("c1", "exec", "{\"x\":1}");
        var preview = InvokePrivate<string>("BuildAgentToolArgsPreview", call);
        Assert.Contains("\"x\":1", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAgentToolArgsPreview_WhenJsonInvalidAndLong_Truncates()
    {
        var args = "{not json " + new string('x', 400);
        var call = new ChatToolCall("c1", "exec", args);

        var preview = InvokePrivate<string>("BuildAgentToolArgsPreview", call);

        Assert.True(preview.Length <= 203, $"Expected truncation to ~200 chars, got {preview.Length} chars.");
        Assert.EndsWith("...", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendLlmToolTranscript_AppendsEntryToTranscriptStore()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var transcriptPath = Path.Combine(tempRoot, "transcript.jsonl");
        try
        {
            var transcript = new CliTranscriptStore(transcriptPath);

            var state = new ShellState();
            state.Settings.ServerUrl = "http://localhost:5000";
            state.SetSession("s1", "LLDB");
            state.DumpId = "d1";

            var call = new ChatToolCall("c1", "exec", "{\"command\":\"apiKey=secret\"}");
            InvokePrivateVoid("AppendLlmToolTranscript", transcript, state, call, "apiKey=secret\nOK");

            var tail = transcript.ReadTail(10);
            var entry = Assert.Single(tail);
            Assert.Equal("llm_tool", entry.Kind);
            Assert.DoesNotContain("secret", entry.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", entry.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("***", entry.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    [Fact]
    public void AppendLlmToolTranscript_WhenPreviewTooLong_TruncatesText()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var transcriptPath = Path.Combine(tempRoot, "transcript.jsonl");
        try
        {
            var transcript = new CliTranscriptStore(transcriptPath);

            var state = new ShellState();
            state.Settings.ServerUrl = "http://localhost:5000";
            state.SetSession("s1", "LLDB");
            state.DumpId = "d1";

            var longCommand = new string('x', 500);
            var call = new ChatToolCall("c1", "exec", $"{{\"command\":\"{longCommand}\"}}");
            InvokePrivateVoid("AppendLlmToolTranscript", transcript, state, call, "ok");

            var tail = transcript.ReadTail(10);
            var entry = Assert.Single(tail);
            Assert.EndsWith("...", entry.Text, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    [Fact]
    public void HandleServerInit_WhenConfigMissing_CreatesDefaultConfig()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var configPath = Path.Combine(tempRoot, "servers.json");

        var configManager = new ServerConfigManager();
        var field = typeof(ServerConfigManager).GetField("_configPath", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(configManager, configPath);

        try
        {
            if (File.Exists(configManager.ConfigPath))
            {
                File.Delete(configManager.ConfigPath);
            }

            InvokePrivateVoid("HandleServerInit", console, output, configManager);

            Assert.True(File.Exists(configManager.ConfigPath));
            Assert.Contains("Created default configuration", console.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    [Fact]
    public void HandleServerInit_WhenConfigExistsAndUserDeclines_DoesNotOverwrite()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var configPath = Path.Combine(tempRoot, "servers.json");
        File.WriteAllText(configPath, "existing");

        var configManager = new ServerConfigManager();
        var field = typeof(ServerConfigManager).GetField("_configPath", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(configManager, configPath);

        try
        {
            console.Input.PushTextWithEnter("n");
            InvokePrivateVoid("HandleServerInit", console, output, configManager);
            Assert.Equal("existing", File.ReadAllText(configPath));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    [Fact]
    public void HandleServerInit_WhenConfigExistsAndUserConfirms_OverwritesConfig()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var configPath = Path.Combine(tempRoot, "servers.json");
        File.WriteAllText(configPath, "existing");

        var configManager = new ServerConfigManager();
        var field = typeof(ServerConfigManager).GetField("_configPath", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(configManager, configPath);

        try
        {
            console.Input.PushTextWithEnter("y");
            InvokePrivateVoid("HandleServerInit", console, output, configManager);

            Assert.NotEqual("existing", File.ReadAllText(configPath));
            Assert.Contains("Created default configuration", console.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    [Fact]
    public void CreateLlmHttpClient_WhenTraceStoreNull_UsesTimeout()
    {
        var settings = new LlmSettings { TimeoutSeconds = 3 };

        using var client = InvokePrivate<HttpClient>("CreateLlmHttpClient", settings, null);

        Assert.Equal(TimeSpan.FromSeconds(3), client.Timeout);
    }

    [Fact]
    public void CreateLlmHttpClient_WhenTraceStoreProvided_UsesTimeout()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var settings = new LlmSettings { TimeoutSeconds = 2, Provider = "openai" };
            var traceStore = new LlmTraceStore(tempRoot, maxFileBytes: 0);

            using var client = InvokePrivate<HttpClient>("CreateLlmHttpClient", settings, traceStore);

            Assert.Equal(TimeSpan.FromSeconds(2), client.Timeout);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ShowTools_WhenNotConnected_PrintsError()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var mcpClient = new McpClient();

        InvokePrivateVoid("ShowTools", output, mcpClient);

        Assert.Contains("Not connected", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShowTools_WhenNoTools_PrintsWarning()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var mcpClient = new McpClient();

        using var http = new HttpClient();
        typeof(McpClient).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(mcpClient, http);
        typeof(McpClient).GetField("_messageEndpoint", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(mcpClient, "/mcp");

        InvokePrivateVoid("ShowTools", output, mcpClient);

        Assert.Contains("No tools", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShowTools_WhenToolsPresent_ListsTools()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var mcpClient = new McpClient();

        using var http = new HttpClient();
        typeof(McpClient).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(mcpClient, http);
        typeof(McpClient).GetField("_messageEndpoint", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(mcpClient, "/mcp");

        var tools = (List<string>)typeof(McpClient).GetField("_availableTools", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mcpClient)!;
        tools.AddRange(["exec", "dump"]);

        InvokePrivateVoid("ShowTools", output, mcpClient);

        Assert.Contains("Available MCP Tools", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exec", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dump", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAnalyzeAsync_WhenUnknownKind_ReturnsError()
    {
        var mcpClient = new McpClient();

        var task = InvokePrivate<Task<string>>(
            "ExecuteAnalyzeAsync",
            mcpClient,
            "s1",
            "u1",
            "wat",
            CancellationToken.None);

        var result = await task;

        Assert.Contains("Unknown analyze kind", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckDumpServerCompatibility_WhenServerInfoMissing_DoesNothing()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState { ServerInfo = null };

        InvokePrivateVoid("CheckDumpServerCompatibility", true, "arm64", state, output);

        Assert.DoesNotContain("INCOMPATIBLE", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckDumpServerCompatibility_WhenGlibcDumpOnAlpineServer_WritesWarning()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState
        {
            ServerInfo = new ServerInfo
            {
                Description = "alpine-host",
                IsAlpine = true,
                Architecture = "arm64"
            }
        };

        InvokePrivateVoid("CheckDumpServerCompatibility", false, "arm64", state, output);

        Assert.Contains("INCOMPATIBLE", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("glibc", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckDumpServerCompatibility_WhenArchitectureMismatch_WritesWarning()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState
        {
            ServerInfo = new ServerInfo
            {
                Description = "srv",
                IsAlpine = false,
                Architecture = "arm64"
            }
        };

        InvokePrivateVoid("CheckDumpServerCompatibility", false, "x64", state, output);

        Assert.Contains("INCOMPATIBLE", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x64", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsLikelyIncompatibleWithCurrentServer_WhenServerInfoMissing_ReturnsFalse()
    {
        var state = new ShellState { ServerInfo = null };

        var result = InvokePrivate<bool>("IsLikelyIncompatibleWithCurrentServer", (bool?)null, "arm64", state);

        Assert.False(result);
    }

    [Fact]
    public void IsLikelyIncompatibleWithCurrentServer_WhenAlpineMismatch_ReturnsTrue()
    {
        var state = new ShellState
        {
            ServerInfo = new ServerInfo
            {
                Description = "srv",
                IsAlpine = false,
                Architecture = "arm64"
            }
        };

        var result = InvokePrivate<bool>("IsLikelyIncompatibleWithCurrentServer", true, "arm64", state);

        Assert.True(result);
    }

    [Theory]
    [InlineData("aarch64", "arm64")]
    [InlineData("amd64", "x64")]
    [InlineData("i686", "x86")]
    [InlineData("armv7", "arm")]
    public void NormalizeArchitecture_MapsCommonAliases(string input, string expected)
    {
        var result = InvokePrivate<string>("NormalizeArchitecture", input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatBytes_FormatsAndScalesUnits()
    {
        var bytes = InvokePrivate<string>("FormatBytes", 512L);
        Assert.Contains("B", bytes, StringComparison.OrdinalIgnoreCase);

        var kb = InvokePrivate<string>("FormatBytes", 2048L);
        Assert.Contains("KB", kb, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsToolResponseTimeoutError_WhenMessageMatches_ReturnsTrue()
    {
        var ex = new Exception("Timed out while waiting for response from tool");
        var result = InvokePrivate<bool>("IsToolResponseTimeoutError", ex);
        Assert.True(result);
    }

    [Fact]
    public void IsDumpAlreadyOpenError_WhenMessageMatches_ReturnsTrue()
    {
        var ex = new Exception("dump file is already open");
        var result = InvokePrivate<bool>("IsDumpAlreadyOpenError", ex);
        Assert.True(result);
    }

    [Fact]
    public void IsNoDumpOpenError_WhenMessageMatches_ReturnsTrue()
    {
        var ex = new Exception("no dump is open");
        var result = InvokePrivate<bool>("IsNoDumpOpenError", ex);
        Assert.True(result);
    }

    [Fact]
    public void IsLikelyIncompatibleWithCurrentServer_WhenArchMismatch_ReturnsTrue()
    {
        var state = new ShellState
        {
            ServerInfo = new ServerInfo
            {
                Description = "srv",
                IsAlpine = false,
                Architecture = "x64"
            }
        };

        var result = InvokePrivate<bool>("IsLikelyIncompatibleWithCurrentServer", (bool?)null, "arm64", state);
        Assert.True(result);
    }

    private static T InvokePrivate<T>(string name, params object?[] args)
    {
        var method = typeof(DebuggerMcp.Cli.Program).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, args);
        Assert.NotNull(result);
        return (T)result!;
    }

    private static void InvokePrivateVoid(string name, params object?[] args)
    {
        var method = typeof(DebuggerMcp.Cli.Program).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, args);
    }
}
