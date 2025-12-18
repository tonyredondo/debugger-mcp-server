using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

[Collection("NonParallelConsole")]
public class CodexAuthReaderTests
{
    [Fact]
    public void TryReadOpenAiApiKey_ReadsTopLevelKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", nameof(CodexAuthReaderTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "auth.json");

        try
        {
            File.WriteAllText(path, "{\"OPENAI_API_KEY\":\"k1\"}");
            Assert.Equal("k1", CodexAuthReader.TryReadOpenAiApiKey(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryReadOpenAiApiKey_ReadsNestedOpenAiObject()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", nameof(CodexAuthReaderTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "auth.json");

        try
        {
            File.WriteAllText(path, "{\"openai\":{\"api_key\":\"k2\"}}");
            Assert.Equal("k2", CodexAuthReader.TryReadOpenAiApiKey(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryReadOpenAiApiKey_ReadsApiKeysObject()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", nameof(CodexAuthReaderTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "auth.json");

        try
        {
            File.WriteAllText(path, "{\"apiKeys\":{\"openai\":\"k3\"}}");
            Assert.Equal("k3", CodexAuthReader.TryReadOpenAiApiKey(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryReadOpenAiApiKey_ExpandsTildePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var relDir = Path.Combine(".dbg-mcp-test", nameof(CodexAuthReaderTests), Guid.NewGuid().ToString("N"));
        var absDir = Path.Combine(home, relDir);
        Directory.CreateDirectory(absDir);
        var absPath = Path.Combine(absDir, "auth.json");

        var tildePath = "~" + Path.DirectorySeparatorChar + relDir + Path.DirectorySeparatorChar + "auth.json";

        try
        {
            File.WriteAllText(absPath, "{\"OPENAI_API_KEY\":\"k4\"}");
            Assert.Equal("k4", CodexAuthReader.TryReadOpenAiApiKey(tildePath));
        }
        finally
        {
            try { Directory.Delete(Path.Combine(home, ".dbg-mcp-test"), recursive: true); } catch { }
        }
    }
}

