using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

public class LlmTraceStoreTests
{
    [Fact]
    public void Ctor_WhenDirectoryPathEmpty_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => new LlmTraceStore(" ", maxFileBytes: 0));
        Assert.Contains("Trace directory", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsFileSizeCapped_WhenMaxFileBytesPositive_ReturnsTrue()
    {
        var temp = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", nameof(LlmTraceStoreTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var store = new LlmTraceStore(temp, maxFileBytes: 10);
            Assert.True(store.IsFileSizeCapped);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryCreate_WhenLabelWhitespace_UsesRunAndCreatesDirectory()
    {
        var store = LlmTraceStore.TryCreate(" ", maxFileBytes: 0);
        if (store == null)
        {
            return;
        }

        try
        {
            Assert.True(Directory.Exists(store.DirectoryPath));
            Assert.Contains("-run-", store.DirectoryPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(store.DirectoryPath, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryCreate_WhenLabelContainsInvalidCharacters_SanitizesLabel()
    {
        var store = LlmTraceStore.TryCreate("a b/c", maxFileBytes: 0);
        if (store == null)
        {
            return;
        }

        try
        {
            Assert.True(Directory.Exists(store.DirectoryPath));
            Assert.Contains("a_b_c", store.DirectoryPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(store.DirectoryPath, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryCreate_WhenRootDirectoryProvided_CreatesUnderThatRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", nameof(LlmTraceStoreTests), "root", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var store = LlmTraceStore.TryCreate("sampling", rootDirectory: root, maxFileBytes: 0);
        if (store == null)
        {
            return;
        }

        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullStore = Path.GetFullPath(store.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Assert.StartsWith(fullRoot, fullStore, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(store.DirectoryPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WriteText_WhenFileNameBlank_DoesNotCreateFile()
    {
        var temp = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", nameof(LlmTraceStoreTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var store = new LlmTraceStore(temp, maxFileBytes: 0);
            store.WriteText(" ", "hello");
            Assert.Empty(Directory.GetFiles(temp));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WriteText_WhenFileNameHasNoFileName_DoesNotCreateFile()
    {
        var temp = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", nameof(LlmTraceStoreTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var store = new LlmTraceStore(temp, maxFileBytes: 0);
            store.WriteText("/", "hello");
            Assert.Empty(Directory.GetFiles(temp));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AppendEvent_WhenDirectoryIsInvalid_DoesNotThrow()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", nameof(LlmTraceStoreTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "not-a-dir.txt");
        File.WriteAllText(filePath, "x");

        try
        {
            var store = new LlmTraceStore(filePath, maxFileBytes: 0);
            store.AppendEvent(new { kind = "test", message = "hello" });
            store.WriteText("out.txt", "hello");
            store.WriteJson("out.json", "{\"a\":1}");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WriteText_WhenExceedsMaxBytes_TruncatesAndAddsMarker()
    {
        var temp = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", nameof(LlmTraceStoreTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var store = new LlmTraceStore(temp, maxFileBytes: 50);
            store.WriteText("out.txt", new string('x', 200));

            var path = Path.Combine(temp, "out.txt");
            var text = File.ReadAllText(path);
            Assert.Contains("truncated", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("totalBytes", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WriteJson_WhenFileNameBlank_DoesNotCreateFile()
    {
        var temp = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", nameof(LlmTraceStoreTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var store = new LlmTraceStore(temp, maxFileBytes: 0);
            store.WriteJson(" ", "{\"a\":1}");
            Assert.Empty(Directory.GetFiles(temp));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WriteJson_WhenJsonInvalid_WritesRaw()
    {
        var temp = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", nameof(LlmTraceStoreTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var store = new LlmTraceStore(temp, maxFileBytes: 0);
            store.WriteJson("out.json", "{not json");

            var path = Path.Combine(temp, "out.json");
            var text = File.ReadAllText(path);
            Assert.Contains("{not json", text, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }
}
