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
}

