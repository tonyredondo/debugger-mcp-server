using DebuggerMcp.Cli.Llm;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Llm;

[Collection("NonParallelConsole")]
public class LlmFileAttachmentsTests
{
    [Fact]
    public void ExtractAndLoad_RewritesPromptAndLoadsFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "report.json");
        File.WriteAllText(filePath, "{\"hello\":\"world\"}");

        var (cleaned, attachments) = LlmFileAttachments.ExtractAndLoad(
            $"Analyze #./{Path.GetFileName(filePath)} please",
            baseDirectory: tempRoot,
            maxBytesPerFile: 1000,
            maxTotalBytes: 2000);

        Assert.Contains("(<attached:", cleaned);
        Assert.Single(attachments);
        Assert.Equal($"./{Path.GetFileName(filePath)}", attachments[0].DisplayPath);
        Assert.EndsWith("report.json", attachments[0].AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"hello\"", attachments[0].Content);
    }

    [Fact]
    public void ExtractAndLoad_DoesNotTreatHashtagsAsAttachments()
    {
        var (cleaned, attachments) = LlmFileAttachments.ExtractAndLoad(
            "This is #not_a_file reference",
            baseDirectory: Environment.CurrentDirectory);

        Assert.Equal("This is #not_a_file reference", cleaned);
        Assert.Empty(attachments);
    }
}

