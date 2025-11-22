using DebuggerMcp.Cli.Shell;

namespace DebuggerMcp.Cli.Tests.Shell;

/// <summary>
/// Tests for <see cref="CommandHistory"/>.
/// </summary>
public class CommandHistoryTests
{
    [Fact]
    public void Constructor_CreatesEmptyHistory()
    {
        // Arrange & Act
        var history = new CommandHistory();

        // Assert
        Assert.Equal(0, history.Count);
        Assert.Empty(history.Entries);
    }

    [Fact]
    public void Add_AddsCommandToHistory()
    {
        // Arrange
        var history = new CommandHistory();

        // Act
        history.Add("connect http://localhost:5000");

        // Assert
        Assert.Equal(1, history.Count);
        Assert.Equal("connect http://localhost:5000", history.Entries[0]);
    }

    [Fact]
    public void Add_IgnoresEmptyAndWhitespace()
    {
        // Arrange
        var history = new CommandHistory();

        // Act
        history.Add("");
        history.Add("   ");
        history.Add(null!);

        // Assert
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void Add_IgnoresDuplicateOfLastCommand()
    {
        // Arrange
        var history = new CommandHistory();

        // Act
        history.Add("help");
        history.Add("help");
        history.Add("help");

        // Assert
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void Add_AllowsDuplicatesNotAdjacent()
    {
        // Arrange
        var history = new CommandHistory();

        // Act
        history.Add("help");
        history.Add("status");
        history.Add("help");

        // Assert
        Assert.Equal(3, history.Count);
    }

    [Fact]
    public void Add_RespectsMaxSize()
    {
        // Arrange
        var history = new CommandHistory(maxSize: 3);

        // Act
        history.Add("cmd1");
        history.Add("cmd2");
        history.Add("cmd3");
        history.Add("cmd4");

        // Assert
        Assert.Equal(3, history.Count);
        Assert.Equal("cmd2", history.Entries[0]);
        Assert.Equal("cmd4", history.Entries[2]);
    }

    [Fact]
    public void GetPrevious_ReturnsLastCommand()
    {
        // Arrange
        var history = new CommandHistory();
        history.Add("cmd1");
        history.Add("cmd2");
        history.Add("cmd3");

        // Act
        var result = history.GetPrevious();

        // Assert
        Assert.Equal("cmd3", result);
    }

    [Fact]
    public void GetPrevious_NavigatesBackward()
    {
        // Arrange
        var history = new CommandHistory();
        history.Add("cmd1");
        history.Add("cmd2");
        history.Add("cmd3");

        // Act & Assert
        Assert.Equal("cmd3", history.GetPrevious());
        Assert.Equal("cmd2", history.GetPrevious());
        Assert.Equal("cmd1", history.GetPrevious());
        // At beginning - stays at first
        Assert.Equal("cmd1", history.GetPrevious());
    }

    [Fact]
    public void GetPrevious_ReturnsNullWhenEmpty()
    {
        // Arrange
        var history = new CommandHistory();

        // Act
        var result = history.GetPrevious();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetNext_NavigatesForward()
    {
        // Arrange
        var history = new CommandHistory();
        history.Add("cmd1");
        history.Add("cmd2");
        history.Add("cmd3");
        history.GetPrevious(); // cmd3
        history.GetPrevious(); // cmd2
        history.GetPrevious(); // cmd1

        // Act & Assert
        Assert.Equal("cmd2", history.GetNext());
        Assert.Equal("cmd3", history.GetNext());
        // At end - returns empty for new command
        Assert.Equal(string.Empty, history.GetNext());
    }

    [Fact]
    public void GetNext_ReturnsNullWhenNotNavigating()
    {
        // Arrange
        var history = new CommandHistory();
        history.Add("cmd1");

        // Act
        var result = history.GetNext();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResetPosition_ResetsToEnd()
    {
        // Arrange
        var history = new CommandHistory();
        history.Add("cmd1");
        history.Add("cmd2");
        history.GetPrevious();
        history.GetPrevious();

        // Act
        history.ResetPosition();
        var result = history.GetPrevious();

        // Assert
        Assert.Equal("cmd2", result); // Back to last command
    }

    [Fact]
    public void Search_FindsMatchingCommands()
    {
        // Arrange
        var history = new CommandHistory();
        history.Add("connect http://localhost:5000");
        history.Add("help");
        history.Add("connect http://production:5000");
        history.Add("status");

        // Act
        var results = history.Search("connect").ToList();

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Equal("connect http://production:5000", results[0]); // Most recent first
        Assert.Equal("connect http://localhost:5000", results[1]);
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        // Arrange
        var history = new CommandHistory();
        history.Add("Help");
        history.Add("HELP");
        history.Add("help");

        // Act
        var results = history.Search("help").ToList();

        // Assert
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Search_WithEmptyPrefix_ReturnsAll()
    {
        // Arrange
        var history = new CommandHistory();
        history.Add("cmd1");
        history.Add("cmd2");

        // Act
        var results = history.Search("").ToList();

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Equal("cmd2", results[0]); // Most recent first
    }

    [Fact]
    public void Clear_RemovesAllHistory()
    {
        // Arrange
        var history = new CommandHistory();
        history.Add("cmd1");
        history.Add("cmd2");

        // Act
        history.Clear();

        // Assert
        Assert.Equal(0, history.Count);
        Assert.Empty(history.Entries);
    }

    [Fact]
    public void Persistence_SavesAndLoadsHistory()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        try
        {
            // Act - Create history and add commands
            var history1 = new CommandHistory(tempFile);
            history1.Add("cmd1");
            history1.Add("cmd2");
            history1.Add("cmd3");

            // Create new history from same file
            var history2 = new CommandHistory(tempFile);

            // Assert
            Assert.Equal(3, history2.Count);
            Assert.Equal("cmd1", history2.Entries[0]);
            Assert.Equal("cmd2", history2.Entries[1]);
            Assert.Equal("cmd3", history2.Entries[2]);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}

