using DebuggerMcp.Cli.Shell;

namespace DebuggerMcp.Cli.Tests.Shell;

/// <summary>
/// Tests for <see cref="AutoComplete"/>.
/// </summary>
public class AutoCompleteTests
{
    [Fact]
    public async Task GetCompletionsAsync_EmptyInput_ReturnsContextualCommands()
    {
        // Arrange
        var state = new ShellState();
        var autoComplete = new AutoComplete(state);

        // Act
        var result = await autoComplete.GetCompletionsAsync("", 0);

        // Assert
        Assert.True(result.HasCompletions);
        Assert.Contains("connect", result.Completions);
        Assert.Contains("help", result.Completions);
        Assert.Contains("exit", result.Completions);
    }

    [Fact]
    public async Task GetCompletionsAsync_PartialCommand_ReturnsMatches()
    {
        // Arrange
        var state = new ShellState();
        var autoComplete = new AutoComplete(state);

        // Act
        var result = await autoComplete.GetCompletionsAsync("con", 3);

        // Assert
        Assert.True(result.HasCompletions);
        Assert.Contains("connect", result.Completions);
        Assert.Equal("con", result.Prefix);
    }

    [Fact]
    public async Task GetCompletionsAsync_SessionSubcommand_ReturnsSubcommands()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        var autoComplete = new AutoComplete(state);

        // Act
        var result = await autoComplete.GetCompletionsAsync("session ", 8);

        // Assert
        Assert.True(result.HasCompletions);
        Assert.Contains("create", result.Completions);
        Assert.Contains("list", result.Completions);
        Assert.Contains("close", result.Completions);
    }

    [Fact]
    public async Task GetCompletionsAsync_DumpsSubcommand_ReturnsSubcommands()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        var autoComplete = new AutoComplete(state);

        // Act
        var result = await autoComplete.GetCompletionsAsync("dumps l", 7);

        // Assert
        Assert.True(result.HasCompletions);
        Assert.Contains("list", result.Completions);
    }

    [Fact]
    public async Task GetCompletionsAsync_NoMatch_ReturnsEmpty()
    {
        // Arrange
        var state = new ShellState();
        var autoComplete = new AutoComplete(state);

        // Act
        var result = await autoComplete.GetCompletionsAsync("xyz", 3);

        // Assert
        Assert.False(result.HasCompletions);
    }

    [Fact]
    public async Task GetCompletionsAsync_WithDumpIdProvider_ReturnsIds()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        var autoComplete = new AutoComplete(state);
        autoComplete.SetDumpIdProvider(() => Task.FromResult<IEnumerable<string>>(["dump1", "dump2", "dump3"]));

        // Act
        var result = await autoComplete.GetCompletionsAsync("open ", 5);

        // Assert
        Assert.True(result.HasCompletions);
        Assert.Contains("dump1", result.Completions);
        Assert.Contains("dump2", result.Completions);
        Assert.Contains("dump3", result.Completions);
    }

    [Fact]
    public async Task GetCompletionsAsync_Connected_IncludesMoreCommands()
    {
        // Arrange
        var state = new ShellState();
        state.SetConnected("http://localhost:5000");
        var autoComplete = new AutoComplete(state);

        // Act
        var result = await autoComplete.GetCompletionsAsync("", 0);

        // Assert
        Assert.True(result.HasCompletions);
        Assert.Contains("dumps", result.Completions);
        Assert.Contains("session", result.Completions);
        Assert.Contains("disconnect", result.Completions);
    }

    [Fact]
    public async Task GetCompletionsAsync_ForDumpUploadFilePath_SuggestsMatchingDumpFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", nameof(AutoCompleteTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var dumpPath = Path.Combine(tempDir, "example.dmp");
            var ignoredPath = Path.Combine(tempDir, "ignored.txt");
            File.WriteAllText(dumpPath, "dmp");
            File.WriteAllText(ignoredPath, "txt");

            var state = new ShellState();
            state.SetConnected("http://localhost:5000");
            var autoComplete = new AutoComplete(state);

            var input = $"dumps upload {Path.Combine(tempDir, "exa")}";
            var result = await autoComplete.GetCompletionsAsync(input, input.Length);

            Assert.True(result.HasCompletions);
            Assert.Contains(result.Completions, c => c.EndsWith("example.dmp", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Completions, c => c.EndsWith("ignored.txt", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CompletionResult_GetCommonPrefix_SingleMatch()
    {
        // Arrange
        var result = new CompletionResult(new[] { "connect" }, "con", 0);

        // Act
        var prefix = result.GetCommonPrefix();

        // Assert
        Assert.Equal("connect", prefix);
    }

    [Fact]
    public void CompletionResult_GetCommonPrefix_MultipleMatches()
    {
        // Arrange
        var result = new CompletionResult(new[] { "session", "set", "status" }, "s", 0);

        // Act
        var prefix = result.GetCommonPrefix();

        // Assert
        Assert.Equal("s", prefix); // Only 's' is common
    }

    [Fact]
    public void CompletionResult_GetCommonPrefix_SimilarMatches()
    {
        // Arrange
        var result = new CompletionResult(new[] { "session", "sessions" }, "sess", 0);

        // Act
        var prefix = result.GetCommonPrefix();

        // Assert
        Assert.Equal("session", prefix); // Common prefix is "session"
    }

    [Fact]
    public void CompletionResult_Empty_HasNoCompletions()
    {
        // Arrange & Act
        var result = CompletionResult.Empty;

        // Assert
        Assert.False(result.HasCompletions);
        Assert.Empty(result.Completions);
    }
}
