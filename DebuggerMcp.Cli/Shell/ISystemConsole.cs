namespace DebuggerMcp.Cli.Shell;

/// <summary>
/// Small abstraction over <see cref="System.Console"/> to make interactive input testable.
/// </summary>
public interface ISystemConsole
{
    bool KeyAvailable { get; }

    ConsoleKeyInfo ReadKey(bool intercept);

    void Write(string value);

    void WriteLine();

    void WriteLine(string value);

    int CursorLeft { get; set; }

    int WindowWidth { get; }

    void Clear();

    void Beep();
}

public sealed class SystemConsole : ISystemConsole
{
    public bool KeyAvailable => Console.KeyAvailable;

    public ConsoleKeyInfo ReadKey(bool intercept) => Console.ReadKey(intercept);

    public void Write(string value) => Console.Write(value);

    public void WriteLine() => Console.WriteLine();

    public void WriteLine(string value) => Console.WriteLine(value);

    public int CursorLeft
    {
        get => Console.CursorLeft;
        set => Console.CursorLeft = value;
    }

    public int WindowWidth => Console.WindowWidth;

    public void Clear() => Console.Clear();

    public void Beep() => Console.Beep();
}
