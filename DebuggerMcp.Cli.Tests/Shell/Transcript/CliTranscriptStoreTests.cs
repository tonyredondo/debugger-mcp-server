using DebuggerMcp.Cli.Shell.Transcript;

namespace DebuggerMcp.Cli.Tests.Shell.Transcript;

public class CliTranscriptStoreTests
{
    [Fact]
    public void Append_ThenReadTail_ReturnsEntriesInOrder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "transcript.jsonl");

        var store = new CliTranscriptStore(path);

        store.Append(new CliTranscriptEntry { Kind = "cli_command", Text = "status", Output = "ok" });
        store.Append(new CliTranscriptEntry { Kind = "llm_user", Text = "hello" });
        store.Append(new CliTranscriptEntry { Kind = "llm_assistant", Text = "hi" });

        var tail = store.ReadTail(10);

        Assert.Equal(3, tail.Count);
        Assert.Equal("cli_command", tail[0].Kind);
        Assert.Equal("llm_user", tail[1].Kind);
        Assert.Equal("llm_assistant", tail[2].Kind);
    }

    [Fact]
    public void FilterInPlace_RemovesLlmEntries_WhenPredicateFiltersThemOut()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "transcript.jsonl");

        var store = new CliTranscriptStore(path);
        store.Append(new CliTranscriptEntry { Kind = "cli_command", Text = "status", Output = "ok" });
        store.Append(new CliTranscriptEntry { Kind = "llm_user", Text = "hello" });
        store.Append(new CliTranscriptEntry { Kind = "llm_assistant", Text = "hi" });

        store.FilterInPlace(e => e.Kind is not ("llm_user" or "llm_assistant"));

        var tail = store.ReadTail(10);
        Assert.Single(tail);
        Assert.Equal("cli_command", tail[0].Kind);
    }

    [Fact]
    public void ReadTailForScope_ReturnsOnlyMatchingEntries()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "transcript.jsonl");

        var store = new CliTranscriptStore(path);
        store.Append(new CliTranscriptEntry { Kind = "llm_user", Text = "a", ServerUrl = "http://localhost:5000", SessionId = "s1", DumpId = "d1" });
        store.Append(new CliTranscriptEntry { Kind = "llm_user", Text = "b", ServerUrl = "http://localhost:5000", SessionId = "s2", DumpId = "d1" });
        store.Append(new CliTranscriptEntry { Kind = "llm_user", Text = "c", ServerUrl = "http://localhost:5000", SessionId = "s1", DumpId = "d2" });
        store.Append(new CliTranscriptEntry { Kind = "llm_user", Text = "d", ServerUrl = "http://localhost:5000", SessionId = "s1", DumpId = "d1" });

        var tail = store.ReadTailForScope(10, "http://localhost:5000/", "s1", "d1");

        Assert.Equal(2, tail.Count);
        Assert.Equal("a", tail[0].Text);
        Assert.Equal("d", tail[1].Text);
    }
}
