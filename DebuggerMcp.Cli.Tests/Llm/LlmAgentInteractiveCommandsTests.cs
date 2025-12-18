using System.Text.Json;
using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Llm;
using DebuggerMcp.Cli.Shell;
using DebuggerMcp.Cli.Shell.Transcript;
using Spectre.Console.Testing;

namespace DebuggerMcp.Cli.Tests.Llm;

public class LlmAgentInteractiveCommandsTests
{
    [Fact]
    public void TryHandle_ExitCommand_SetsShouldExit()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();
        using var temp = new TempTranscript();

        var handled = LlmAgentInteractiveCommands.TryHandle("/exit", output, state, temp.Store, out var shouldExit);

        Assert.True(handled);
        Assert.True(shouldExit);
    }

    [Fact]
    public void TryHandle_HelpCommand_WritesHelp()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();
        using var temp = new TempTranscript();

        var handled = LlmAgentInteractiveCommands.TryHandle("/help", output, state, temp.Store, out var shouldExit);

        Assert.True(handled);
        Assert.False(shouldExit);
        Assert.Contains("Available / commands", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/reset", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/exit", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryHandle_Reset_AppendsResetMarkerForScope()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();
        state.Settings.ServerUrl = "http://localhost:5001";
        state.SessionId = "s";
        state.DumpId = "d";
        using var temp = new TempTranscript();

        var handled = LlmAgentInteractiveCommands.TryHandle("/reset", output, state, temp.Store, out var shouldExit);

        Assert.True(handled);
        Assert.False(shouldExit);

        var entries = temp.ReadAll();
        var last = Assert.Single(entries, e => e.Kind == "llm_reset");
        Assert.Equal("http://localhost:5001", last.ServerUrl);
        Assert.Equal("s", last.SessionId);
        Assert.Equal("d", last.DumpId);
    }

    [Fact]
    public void TryHandle_ResetConversation_RemovesOnlyScopedConversation()
    {
        var console = new TestConsole();
        var output = new ConsoleOutput(console);
        var state = new ShellState();
        state.Settings.ServerUrl = "http://localhost:5001";
        state.SessionId = "s";
        state.DumpId = "d";
        using var temp = new TempTranscript();

        temp.Store.Append(new CliTranscriptEntry
        {
            Kind = "llm_user",
            Text = "hi",
            ServerUrl = "http://localhost:5001",
            SessionId = "s",
            DumpId = "d"
        });
        temp.Store.Append(new CliTranscriptEntry
        {
            Kind = "llm_assistant",
            Text = "hello",
            ServerUrl = "http://localhost:5001",
            SessionId = "s",
            DumpId = "d"
        });
        temp.Store.Append(new CliTranscriptEntry
        {
            Kind = "cli_command",
            Text = "status",
            ServerUrl = "http://localhost:5001",
            SessionId = "s",
            DumpId = "d"
        });
        temp.Store.Append(new CliTranscriptEntry
        {
            Kind = "llm_user",
            Text = "other scope",
            ServerUrl = "http://other",
            SessionId = "x",
            DumpId = "y"
        });

        var handled = LlmAgentInteractiveCommands.TryHandle("/reset conversation", output, state, temp.Store, out var shouldExit);

        Assert.True(handled);
        Assert.False(shouldExit);

        var entries = temp.ReadAll();
        Assert.DoesNotContain(entries, e => e.Kind is "llm_user" or "llm_assistant" or "llm_tool" &&
                                            e.ServerUrl == "http://localhost:5001" &&
                                            e.SessionId == "s" &&
                                            e.DumpId == "d");
        Assert.Contains(entries, e => e.Kind == "cli_command" && e.ServerUrl == "http://localhost:5001");
        Assert.Contains(entries, e => e.Kind == "llm_user" && e.ServerUrl == "http://other");
    }

    private sealed class TempTranscript : IDisposable
    {
        private readonly string _path;

        internal TempTranscript()
        {
            _path = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", "transcripts", Guid.NewGuid().ToString("N") + ".jsonl");
            Store = new CliTranscriptStore(_path);
        }

        internal CliTranscriptStore Store { get; }

        internal List<CliTranscriptEntry> ReadAll()
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var entries = new List<CliTranscriptEntry>();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var line in File.ReadAllLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var entry = JsonSerializer.Deserialize<CliTranscriptEntry>(line, options);
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }
            return entries;
        }

        public void Dispose()
        {
            try { File.Delete(_path); } catch { }
        }
    }
}
