using DebuggerMcp.Cli.Llm;
using System.Text;
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
        Assert.Contains("Attached file:", attachments[0].MessageForModel);
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

    [Fact]
    public void ExtractAndLoad_AllowsLeadingParenthesisBeforeAttachment()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "report.json");
        File.WriteAllText(filePath, "{\"hello\":\"world\"}");

        var (cleaned, attachments, _) = LlmFileAttachments.ExtractAndLoad(
            "Analyze (#./report.json) please",
            baseDirectory: tempRoot,
            maxBytesPerFile: 1000,
            maxTotalBytes: 2000);

        Assert.Contains("(<attached:", cleaned);
        Assert.Single(attachments);
        Assert.Contains("\"hello\"", attachments[0].Content);
    }

    [Fact]
    public void ExtractAndLoad_SupportsPathsWithSpacesUsingParentheses()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var fileName = "my report.json";
        var filePath = Path.Combine(tempRoot, fileName);
        File.WriteAllText(filePath, "{\"hello\":\"world\"}");

        var (_, attachments, _) = LlmFileAttachments.ExtractAndLoad(
            "Analyze #(./my report.json) please",
            baseDirectory: tempRoot,
            maxBytesPerFile: 1000,
            maxTotalBytes: 2000);

        Assert.Single(attachments);
        Assert.Contains("\"hello\"", attachments[0].Content);
    }

    [Fact]
    public void ExtractAndLoad_RespectsMaxTotalBytesAcrossMultipleAttachments()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var fileA = Path.Combine(tempRoot, "a.txt");
        var fileB = Path.Combine(tempRoot, "b.txt");

        File.WriteAllText(fileA, new string('a', 800));
        File.WriteAllText(fileB, new string('b', 800));

        var (cleaned, attachments, _) = LlmFileAttachments.ExtractAndLoad(
            "Analyze #./a.txt and #./b.txt please",
            baseDirectory: tempRoot,
            maxBytesPerFile: 2000,
            maxTotalBytes: 900);

        Assert.Contains("(<attached:", cleaned);
        Assert.Equal(2, attachments.Count);
        Assert.Equal("./a.txt", attachments[0].DisplayPath);
        Assert.Equal("./b.txt", attachments[1].DisplayPath);
        Assert.False(attachments[0].Truncated);
        Assert.True(attachments[1].Truncated);

        var total = Encoding.UTF8.GetByteCount(attachments[0].MessageForModel) + Encoding.UTF8.GetByteCount(attachments[1].MessageForModel);
        Assert.True(total <= 900);
    }

    [Fact]
    public void ExtractAndLoad_DoesNotExceedBudget_WhenBudgetIsTiny()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var filePath = Path.Combine(tempRoot, "a.txt");
        File.WriteAllText(filePath, new string('a', 1000));

        var (_, attachments, _) = LlmFileAttachments.ExtractAndLoad(
            "Analyze #./a.txt",
            baseDirectory: tempRoot,
            maxBytesPerFile: 1000,
            maxTotalBytes: 60);

        var a = Assert.Single(attachments);
        Assert.True(Encoding.UTF8.GetByteCount(a.MessageForModel) <= 60);
    }
}
