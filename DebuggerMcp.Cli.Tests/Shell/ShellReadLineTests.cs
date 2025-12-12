using DebuggerMcp.Cli.Shell;
using Spectre.Console.Testing;

namespace DebuggerMcp.Cli.Tests.Shell;

/// <summary>
/// Unit tests for <see cref="ShellReadLine"/>.
/// </summary>
public class ShellReadLineTests
{
    [Fact]
    public async Task ReadLineAsync_WithEnter_ReturnsLineAndAddsHistory()
    {
        var testConsole = new TestConsole();
        var history = new CommandHistory();
        var state = new ShellState();
        var autoComplete = new AutoComplete(state);

        var systemConsole = new FakeSystemConsole(
            Key('h'),
            Key('i'),
            Key(ConsoleKey.Enter));

        var readline = new ShellReadLine(testConsole, history, autoComplete, state, systemConsole);

        var line = await readline.ReadLineAsync();

        Assert.Equal("hi", line);
        Assert.Contains("hi", history.Entries);
    }

    [Fact]
    public async Task ReadLineAsync_WhenCtrlC_ReturnsNull()
    {
        var testConsole = new TestConsole();
        var history = new CommandHistory();
        var state = new ShellState();
        var autoComplete = new AutoComplete(state);

        var systemConsole = new FakeSystemConsole(
            Key(ConsoleKey.C, control: true));

        var readline = new ShellReadLine(testConsole, history, autoComplete, state, systemConsole);

        var line = await readline.ReadLineAsync();

        Assert.Null(line);
    }

    [Fact]
    public async Task ReadLineAsync_WhenSingleTabCompletion_AppliesCompletion()
    {
        var testConsole = new TestConsole();
        var history = new CommandHistory();
        var state = new ShellState();
        var autoComplete = new AutoComplete(state);

        // "ver" should complete to the single command "version".
        var systemConsole = new FakeSystemConsole(
            Key('v'),
            Key('e'),
            Key('r'),
            Key(ConsoleKey.Tab),
            Key(ConsoleKey.Enter));

        var readline = new ShellReadLine(testConsole, history, autoComplete, state, systemConsole);

        var line = await readline.ReadLineAsync();

        Assert.Equal("version", line);
    }

    [Fact]
    public async Task ReadLineAsync_WhenHistoryRestored_RestoresSavedLine()
    {
        var testConsole = new TestConsole();
        var history = new CommandHistory();
        history.Add("one");
        history.Add("two");

        var state = new ShellState();
        var autoComplete = new AutoComplete(state);

        // Type "temp", go up to history ("two"), then down (should restore "temp").
        var systemConsole = new FakeSystemConsole(
            Key('t'),
            Key('e'),
            Key('m'),
            Key('p'),
            Key(ConsoleKey.UpArrow),
            Key(ConsoleKey.DownArrow),
            Key(ConsoleKey.Enter));

        var readline = new ShellReadLine(testConsole, history, autoComplete, state, systemConsole);

        var line = await readline.ReadLineAsync();

        Assert.Equal("temp", line);
    }

    [Fact]
    public async Task ReadLineAsync_WhenBackspace_RemovesCharacter()
    {
        var testConsole = new TestConsole();
        var history = new CommandHistory();
        var state = new ShellState();
        var autoComplete = new AutoComplete(state);

        var systemConsole = new FakeSystemConsole(
            Key('a'),
            Key('b'),
            Key(ConsoleKey.Backspace),
            Key(ConsoleKey.Enter));

        var readline = new ShellReadLine(testConsole, history, autoComplete, state, systemConsole);

        var line = await readline.ReadLineAsync();

        Assert.Equal("a", line);
    }

    private static ConsoleKeyInfo Key(char c) => new(c, (ConsoleKey)char.ToUpperInvariant(c), shift: false, alt: false, control: false);

    private static ConsoleKeyInfo Key(ConsoleKey key, bool shift = false, bool alt = false, bool control = false)
        => new('\0', key, shift, alt, control);

    private sealed class FakeSystemConsole(params ConsoleKeyInfo[] keys) : ISystemConsole
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);
        private readonly List<string> _writes = [];

        public bool KeyAvailable => _keys.Count > 0;

        public int CursorLeft { get; set; }

        public int WindowWidth { get; } = 120;

        public ConsoleKeyInfo ReadKey(bool intercept)
        {
            return _keys.Dequeue();
        }

        public void Write(string value) => _writes.Add(value);

        public void WriteLine() => _writes.Add("\n");

        public void WriteLine(string value) => _writes.Add(value + "\n");

        public void Clear() => _writes.Clear();

        public void Beep()
        {
            // Ignore in tests.
        }
    }
}

