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
        Assert.Contains("untrusted", attachments[0].MessageForModel, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("(<skipped:", cleaned);
        Assert.Single(attachments);
        Assert.Equal("./a.txt", attachments[0].DisplayPath);
        Assert.True(attachments[0].Truncated);

        var total = Encoding.UTF8.GetByteCount(attachments[0].MessageForModel);
        Assert.True(total <= 900);
    }

    [Fact]
    public void ExtractAndLoad_DoesNotExceedBudget_WhenBudgetIsTiny()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var filePath = Path.Combine(tempRoot, "a.txt");
        File.WriteAllText(filePath, new string('a', 1000));

        var (cleaned, attachments, _) = LlmFileAttachments.ExtractAndLoad(
            "Analyze #./a.txt",
            baseDirectory: tempRoot,
            maxBytesPerFile: 1000,
            maxTotalBytes: 60);

        Assert.Empty(attachments);
        Assert.Contains("(<skipped:", cleaned);
    }

    [Fact]
    public void ExtractAndLoad_MissingFile_DoesNotPretendToAttach()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var (cleaned, attachments, reports) = LlmFileAttachments.ExtractAndLoad(
            "Analyze #./missing.json please",
            baseDirectory: tempRoot);

        Assert.Contains("(<missing:", cleaned);
        Assert.Empty(attachments);
        Assert.Empty(reports);
    }

    [Fact]
    public void ExtractAndLoad_SkippedAttachment_DoesNotPretendToAttach()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var filePath = Path.Combine(tempRoot, "a.txt");
        File.WriteAllText(filePath, "hello");

        var (cleaned, attachments, reports) = LlmFileAttachments.ExtractAndLoad(
            "Analyze #./a.txt please",
            baseDirectory: tempRoot,
            maxBytesPerFile: 1000,
            maxTotalBytes: 1);

        Assert.Contains("(<skipped:", cleaned);
        Assert.Empty(attachments);
        Assert.Empty(reports);
    }

    [Fact]
    public void ExtractAndLoad_FileContainingBackticks_UsesLongerFence()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "note.md");
        File.WriteAllText(filePath, "before\n```\ninside\n```\nafter\n");

        var (_, attachments, _) = LlmFileAttachments.ExtractAndLoad(
            "Analyze #./note.md please",
            baseDirectory: tempRoot,
            maxBytesPerFile: 10_000,
            maxTotalBytes: 20_000);

        var a = Assert.Single(attachments);

        // Should not use the same ``` fence when the content contains ```.
        Assert.DoesNotContain("\n```markdown\nbefore\n```\ninside", a.MessageForModel, StringComparison.Ordinal);
        Assert.Contains("````", a.MessageForModel, StringComparison.Ordinal);
    }
}
