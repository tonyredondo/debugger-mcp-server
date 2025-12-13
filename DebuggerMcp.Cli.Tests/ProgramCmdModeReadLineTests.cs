using System.Reflection;
using System.Text;
using DebuggerMcp.Cli.Shell;
using Spectre.Console;
using Spectre.Console.Testing;

namespace DebuggerMcp.Cli.Tests;

/// <summary>
/// Coverage tests for Program cmd-mode input reader.
/// </summary>
public class ProgramCmdModeReadLineTests
{
    [Fact]
    public void ReadCmdLineWithHistory_WithTypedCharacters_ReturnsEnteredLine()
    {
        var ansi = new TestConsole();
        var history = new CommandHistory();
        var systemConsole = new FakeSystemConsole(
            Key('a'),
            Key('b'),
            Key('c'),
            Key(ConsoleKey.Enter));

        var line = InvokeReadCmdLineWithHistory(ansi, systemConsole, "(lldb)", history);

        Assert.Equal("abc", line);
        Assert.Contains("abc", systemConsole.Output);
    }

    [Fact]
    public void ReadCmdLineWithHistory_WithHistoryNavigation_ReturnsPreviousEntry()
    {
        var ansi = new TestConsole();
        var history = new CommandHistory();
        history.Add("one");
        history.Add("two");

        var systemConsole = new FakeSystemConsole(
            Key(ConsoleKey.UpArrow),
            Key(ConsoleKey.Enter));

        var line = InvokeReadCmdLineWithHistory(ansi, systemConsole, "(lldb)", history);

        Assert.Equal("two", line);
    }

    [Fact]
    public void ReadCmdLineWithHistory_WhenCtrlC_ReturnsNull()
    {
        var ansi = new TestConsole();
        var history = new CommandHistory();

        var systemConsole = new FakeSystemConsole(
            Key(ConsoleKey.C, control: true));

        var line = InvokeReadCmdLineWithHistory(ansi, systemConsole, "(lldb)", history);

        Assert.Null(line);
        Assert.Contains("^C", systemConsole.Output);
    }

    private static string? InvokeReadCmdLineWithHistory(IAnsiConsole ansi, ISystemConsole systemConsole, string prompt, CommandHistory history)
    {
        var method = typeof(DebuggerMcp.Cli.Program).GetMethod(
            "ReadCmdLineWithHistory",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return (string?)method!.Invoke(null, new object[] { ansi, systemConsole, prompt, history });
    }

    private static ConsoleKeyInfo Key(char c)
        => new(c, (ConsoleKey)char.ToUpperInvariant(c), shift: false, alt: false, control: false);

    private static ConsoleKeyInfo Key(ConsoleKey key, bool shift = false, bool alt = false, bool control = false)
        => new('\0', key, shift, alt, control);

    private sealed class FakeSystemConsole(params ConsoleKeyInfo[] keys) : ISystemConsole
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);
        private readonly StringBuilder _output = new();

        public bool KeyAvailable => _keys.Count > 0;

        public ConsoleKeyInfo ReadKey(bool intercept)
        {
            if (_keys.Count == 0)
                return new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: false, alt: false, control: false);

            return _keys.Dequeue();
        }

        public void Write(string value)
        {
            _output.Append(value);
            CursorLeft += value.Length;
        }

        public void WriteLine()
        {
            _output.AppendLine();
            CursorLeft = 0;
        }

        public void WriteLine(string value)
        {
            _output.AppendLine(value);
            CursorLeft = 0;
        }

        public int CursorLeft { get; set; }

        public int WindowWidth { get; } = 120;

        public void Clear()
        {
            _output.Clear();
            CursorLeft = 0;
        }

        public void Beep()
        {
            // Ignore in tests.
        }

        public string Output => _output.ToString();
    }
}

