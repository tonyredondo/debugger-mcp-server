using DebuggerMcp.Cli.Help;

namespace DebuggerMcp.Cli.Tests.Help;

/// <summary>
/// Tests for the HelpSystem class.
/// </summary>
public class HelpSystemTests
{
    [Fact]
    public void Categories_ContainsExpectedCategories()
    {
        // Assert
        Assert.Contains("connection", HelpSystem.Categories.Keys);
        Assert.Contains("files", HelpSystem.Categories.Keys);
        Assert.Contains("session", HelpSystem.Categories.Keys);
        Assert.Contains("debugging", HelpSystem.Categories.Keys);
        Assert.Contains("analysis", HelpSystem.Categories.Keys);
        Assert.Contains("advanced", HelpSystem.Categories.Keys);
        Assert.Contains("general", HelpSystem.Categories.Keys);
    }

    [Fact]
    public void CommandsByCategory_ContainsAllCategories()
    {
        // Assert
        foreach (var category in HelpSystem.Categories.Keys)
        {
            Assert.True(HelpSystem.CommandsByCategory.ContainsKey(category),
                $"CommandsByCategory should contain '{category}'");
        }
    }

    [Theory]
    [InlineData("connection", "connect")]
    [InlineData("connection", "disconnect")]
    [InlineData("connection", "status")]
    [InlineData("connection", "health")]
    [InlineData("connection", "server")]
    [InlineData("files", "dumps")]
    [InlineData("files", "symbols")]
    [InlineData("session", "session")]
    [InlineData("debugging", "open")]
    [InlineData("debugging", "close")]
    [InlineData("debugging", "exec")]
    [InlineData("debugging", "sos")]
    [InlineData("analysis", "analyze")]
    [InlineData("analysis", "compare")]
    [InlineData("advanced", "watch")]
    [InlineData("advanced", "report")]
    [InlineData("advanced", "sourcelink")]
    [InlineData("general", "help")]
    [InlineData("general", "exit")]
    public void CommandsByCategory_ContainsCommand(string category, string commandName)
    {
        // Act
        var commands = HelpSystem.CommandsByCategory[category];

        // Assert
        Assert.Contains(commands, c => c.Name == commandName);
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("dumps")]
    [InlineData("session")]
    [InlineData("open")]
    [InlineData("exec")]
    [InlineData("analyze")]
    [InlineData("watch")]
    [InlineData("report")]
    public void CommandInfo_HasExamples(string commandName)
    {
        // Find command
        CommandInfo? command = null;
        foreach (var commands in HelpSystem.CommandsByCategory.Values)
        {
            command = commands.FirstOrDefault(c => c.Name == commandName);
            if (command != null) break;
        }

        // Assert
        Assert.NotNull(command);
        Assert.True(command.Examples.Length > 0, $"Command '{commandName}' should have examples");
    }

    [Theory]
    [InlineData("connect", "url")]
    [InlineData("dumps", "subcommand")]
    [InlineData("open", "dumpId")]
    [InlineData("exec", "command")]
    [InlineData("analyze", "type")]
    public void CommandInfo_HasSyntax(string commandName, string expectedSyntaxPart)
    {
        // Find command
        CommandInfo? command = null;
        foreach (var commands in HelpSystem.CommandsByCategory.Values)
        {
            command = commands.FirstOrDefault(c => c.Name == commandName);
            if (command != null) break;
        }

        // Assert
        Assert.NotNull(command);
        Assert.Contains(expectedSyntaxPart, command.Syntax, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Categories_HaveDescriptions()
    {
        // Assert
        foreach (var (category, description) in HelpSystem.Categories)
        {
            Assert.False(string.IsNullOrWhiteSpace(description),
                $"Category '{category}' should have a description");
        }
    }

    [Fact]
    public void AllCommands_HaveDescriptions()
    {
        // Assert
        foreach (var (category, commands) in HelpSystem.CommandsByCategory)
        {
            foreach (var command in commands)
            {
                Assert.False(string.IsNullOrWhiteSpace(command.Description),
                    $"Command '{command.Name}' in category '{category}' should have a description");
            }
        }
    }
}

