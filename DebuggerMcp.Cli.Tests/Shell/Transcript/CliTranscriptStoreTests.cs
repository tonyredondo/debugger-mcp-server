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
        store.Append(new CliTranscriptEntry { Kind = "llm_tool", Text = "exec bt", Output = "bt-output" });

        store.FilterInPlace(e => e.Kind is not ("llm_user" or "llm_assistant" or "llm_tool"));

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

    [Fact]
    public void Ctor_WhenFilePathMissing_Throws()
    {
        _ = Assert.Throws<ArgumentException>(() => new CliTranscriptStore(" "));
    }

    [Fact]
    public void Append_WhenEntryNull_Throws()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "transcript.jsonl");
        var store = new CliTranscriptStore(path);

        _ = Assert.Throws<ArgumentNullException>(() => store.Append(null!));
    }

    [Fact]
    public void ReadTail_WhenMaxEntriesNonPositive_ReturnsEmpty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "transcript.jsonl");
        var store = new CliTranscriptStore(path);

        Assert.Empty(store.ReadTail(0));
    }

    [Fact]
    public void ReadTail_WhenFileMissing_ReturnsEmpty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "transcript.jsonl");
        var store = new CliTranscriptStore(path);

        Assert.Empty(store.ReadTail(10));
    }

    [Fact]
    public void ReadTail_WhenInvalidLinesPresent_SkipsThem()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "transcript.jsonl");

        File.WriteAllText(path, "\n{not-json\nnull\n{\"kind\":\"cli_command\",\"text\":\"status\"}\n");
        var store = new CliTranscriptStore(path);

        var tail = store.ReadTail(10);

        var entry = Assert.Single(tail);
        Assert.Equal("cli_command", entry.Kind);
        Assert.Equal("status", entry.Text);
    }

    [Fact]
    public void ReadTail_WhenMaxEntriesSmaller_DropsOldest()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "transcript.jsonl");
        var store = new CliTranscriptStore(path);

        store.Append(new CliTranscriptEntry { Kind = "cli_command", Text = "a" });
        store.Append(new CliTranscriptEntry { Kind = "cli_command", Text = "b" });
        store.Append(new CliTranscriptEntry { Kind = "cli_command", Text = "c" });

        var tail = store.ReadTail(2);

        Assert.Equal(2, tail.Count);
        Assert.Equal("b", tail[0].Text);
        Assert.Equal("c", tail[1].Text);
    }

    [Fact]
    public void ReadTailForScope_WhenMaxEntriesNonPositive_ReturnsEmpty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "transcript.jsonl");
        var store = new CliTranscriptStore(path);

        Assert.Empty(store.ReadTailForScope(0, "http://localhost", "s", "d"));
    }

    [Fact]
    public void ReadTailForScope_WhenFileMissing_ReturnsEmpty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "transcript.jsonl");
        var store = new CliTranscriptStore(path);

        Assert.Empty(store.ReadTailForScope(10, "http://localhost", "s", "d"));
    }

    [Fact]
    public void Clear_WhenFileExists_EmptiesTranscript()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "transcript.jsonl");
        var store = new CliTranscriptStore(path);

        store.Append(new CliTranscriptEntry { Kind = "cli_command", Text = "status" });
        Assert.NotEmpty(store.ReadTail(10));

        store.Clear();

        Assert.Empty(store.ReadTail(10));
        Assert.Equal(string.Empty, File.ReadAllText(path));
    }

    [Fact]
    public void FilterInPlace_WhenPredicateNull_Throws()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "transcript.jsonl");
        var store = new CliTranscriptStore(path);

        _ = Assert.Throws<ArgumentNullException>(() => store.FilterInPlace(null!));
    }

    [Fact]
    public void FilterInPlace_WhenFileMissing_Noops()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "transcript.jsonl");
        var store = new CliTranscriptStore(path);

        store.FilterInPlace(_ => true);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void FilterInPlace_SkipsInvalidLines_AndNormalizesRemaining()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "transcript.jsonl");

        File.WriteAllText(path, "\n{not-json\nnull\n{\"kind\":\"cli_command\",\"text\":\"status\"}\n");
        var store = new CliTranscriptStore(path);

        store.FilterInPlace(_ => true);

        var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Single(lines);
        var tail = store.ReadTail(10);
        Assert.Single(tail);
        Assert.Equal("status", tail[0].Text);
    }
}
