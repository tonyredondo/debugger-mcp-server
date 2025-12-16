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

        var (cleaned, attachments, reports) = LlmFileAttachments.ExtractAndLoad(
            $"Analyze #./{Path.GetFileName(filePath)} please",
            baseDirectory: tempRoot,
            maxBytesPerFile: 1000,
            maxTotalBytes: 2000);

        Assert.Contains("(<attached:", cleaned);
        Assert.Single(attachments);
        Assert.Empty(reports);
        Assert.Equal($"./{Path.GetFileName(filePath)}", attachments[0].DisplayPath);
        Assert.EndsWith("report.json", attachments[0].AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"hello\"", attachments[0].Content);
    }

    [Fact]
    public void ExtractAndLoad_DoesNotTreatHashtagsAsAttachments()
    {
        var (cleaned, attachments, reports) = LlmFileAttachments.ExtractAndLoad(
            "This is #not_a_file reference",
            baseDirectory: Environment.CurrentDirectory);

        Assert.Equal("This is #not_a_file reference", cleaned);
        Assert.Empty(attachments);
        Assert.Empty(reports);
    }

    [Fact]
    public void ExtractAndLoad_EnforcesMaxAttachmentReadBytesCap()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "big.txt");

        // 1.1MB file (larger than the 1MB safety cap).
        File.WriteAllText(filePath, new string('x', 1_100_000));

        var (_, attachments, _) = LlmFileAttachments.ExtractAndLoad(
            $"Analyze #./{Path.GetFileName(filePath)} please",
            baseDirectory: tempRoot,
            maxBytesPerFile: 2_000_000,
            maxTotalBytes: 3_000_000);

        var a = Assert.Single(attachments);
        Assert.True(a.Truncated);
        Assert.Contains("truncated to 1048576 bytes", a.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractAndLoad_AllowsTrailingPunctuationAfterAttachment()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "report.json");
        File.WriteAllText(filePath, "{\"hello\":\"world\"}");

        var (_, attachments, _) = LlmFileAttachments.ExtractAndLoad(
            "Analyze #./report.json, please",
            baseDirectory: tempRoot,
            maxBytesPerFile: 1000,
            maxTotalBytes: 2000);

        Assert.Single(attachments);
        Assert.Contains("\"hello\"", attachments[0].Content);
    }
}
