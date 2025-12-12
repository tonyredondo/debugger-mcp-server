using Spectre.Console;

namespace DebuggerMcp.Cli.Shell;

/// <summary>
/// Custom ReadLine implementation with history and tab completion.
/// </summary>
/// <remarks>
/// Features:
/// <list type="bullet">
/// <item><description>Up/Down arrow for history navigation</description></item>
/// <item><description>Tab for auto-completion</description></item>
/// <item><description>Ctrl+C to cancel current line</description></item>
/// <item><description>Ctrl+L to clear screen</description></item>
/// <item><description>Home/End for cursor movement</description></item>
/// </list>
/// </remarks>
public class ShellReadLine
{
    private readonly IAnsiConsole _console;
    private readonly ISystemConsole _systemConsole;
    private readonly CommandHistory _history;
    private readonly AutoComplete _autoComplete;
    private readonly ShellState _state;

    private string _currentLine = string.Empty;
    private int _cursorPosition;
    private string? _savedLine; // For restoring after history navigation
    private int _completionIndex = -1;
    private CompletionResult? _lastCompletions;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShellReadLine"/> class.
    /// </summary>
    /// <param name="console">The console for output.</param>
    /// <param name="history">The command history.</param>
    /// <param name="autoComplete">The auto-complete provider.</param>
    /// <param name="state">The shell state.</param>
    public ShellReadLine(
        IAnsiConsole console,
        CommandHistory history,
        AutoComplete autoComplete,
        ShellState state,
        ISystemConsole? systemConsole = null)
    {
        _console = console;
        _systemConsole = systemConsole ?? new SystemConsole();
        _history = history;
        _autoComplete = autoComplete;
        _state = state;
    }

    /// <summary>
    /// Reads a line of input with history and completion support.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entered line, or null if cancelled.</returns>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        _currentLine = string.Empty;
        _cursorPosition = 0;
        _savedLine = null;
        _completionIndex = -1;
        _lastCompletions = null;

        // Write initial prompt
        WritePrompt();

        while (!cancellationToken.IsCancellationRequested)
        {
            // Check for available key
            if (!_systemConsole.KeyAvailable)
            {
                await Task.Delay(10, cancellationToken);
                continue;
            }

            var key = _systemConsole.ReadKey(intercept: true);

            // Handle special keys
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    _systemConsole.WriteLine();
                    if (!string.IsNullOrWhiteSpace(_currentLine))
                    {
                        _history.Add(_currentLine);
                    }
                    return _currentLine;

                case ConsoleKey.Escape:
                    // Clear current line
                    ClearLine();
                    _currentLine = string.Empty;
                    _cursorPosition = 0;
                    WriteCurrentLine();
                    break;

                case ConsoleKey.Backspace:
                    HandleBackspace();
                    break;

                case ConsoleKey.Delete:
                    HandleDelete();
                    break;

                case ConsoleKey.LeftArrow:
                    if (_cursorPosition > 0)
                    {
                        _cursorPosition--;
                        _systemConsole.CursorLeft--;
                    }
                    break;

                case ConsoleKey.RightArrow:
                    if (_cursorPosition < _currentLine.Length)
                    {
                        _cursorPosition++;
                        _systemConsole.CursorLeft++;
                    }
                    break;

                case ConsoleKey.Home:
                    MoveCursorToStart();
                    break;

                case ConsoleKey.End:
                    MoveCursorToEnd();
                    break;

                case ConsoleKey.UpArrow:
                    await HandleHistoryUpAsync();
                    break;

                case ConsoleKey.DownArrow:
                    HandleHistoryDown();
                    break;

                case ConsoleKey.Tab:
                    await HandleTabCompletionAsync();
                    break;

                case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    // Cancel current line
                    _systemConsole.WriteLine("^C");
                    return null;

                case ConsoleKey.L when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    // Clear screen
                    _systemConsole.Clear();
                    WritePrompt();
                    WriteCurrentLine();
                    break;

                case ConsoleKey.U when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    // Clear line before cursor
                    if (_cursorPosition > 0)
                    {
                        _currentLine = _currentLine[_cursorPosition..];
                        _cursorPosition = 0;
                        ClearLine();
                        WriteCurrentLine();
                    }
                    break;

                case ConsoleKey.K when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    // Clear line after cursor
                    if (_cursorPosition < _currentLine.Length)
                    {
                        _currentLine = _currentLine[.._cursorPosition];
                        ClearLine();
                        WriteCurrentLine();
                    }
                    break;

                case ConsoleKey.W when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    // Delete word before cursor
                    DeleteWordBackward();
                    break;

                default:
                    // Regular character
                    if (!char.IsControl(key.KeyChar))
                    {
                        InsertChar(key.KeyChar);
                    }
                    break;
            }

            // Reset completion state on non-Tab key
            if (key.Key != ConsoleKey.Tab)
            {
                _completionIndex = -1;
                _lastCompletions = null;
            }
        }

        // Cancellation requested - write newline so next output starts on fresh line
        _systemConsole.WriteLine();
        return null;
    }

    /// <summary>
    /// Writes the prompt to the console.
    /// </summary>
    private void WritePrompt()
    {
        _console.Markup(PromptBuilder.BuildMarkup(_state));
    }

    /// <summary>
    /// Writes the current line at the cursor position.
    /// </summary>
    private void WriteCurrentLine()
    {
        var promptLength = PromptBuilder.GetPromptLength(_state);
        _systemConsole.Write(_currentLine);

        // Position cursor correctly
        var targetPosition = promptLength + _cursorPosition;
        _systemConsole.CursorLeft = targetPosition;
    }

    /// <summary>
    /// Clears the current line (excluding prompt).
    /// </summary>
    private void ClearLine()
    {
        var promptLength = PromptBuilder.GetPromptLength(_state);
        _systemConsole.CursorLeft = promptLength;
        _systemConsole.Write(new string(' ', _currentLine.Length + 10));
        _systemConsole.CursorLeft = promptLength;
    }

    /// <summary>
    /// Handles backspace key.
    /// </summary>
    private void HandleBackspace()
    {
        if (_cursorPosition > 0)
        {
            _currentLine = _currentLine.Remove(_cursorPosition - 1, 1);
            _cursorPosition--;
            ClearLine();
            WriteCurrentLine();
        }
    }

    /// <summary>
    /// Handles delete key.
    /// </summary>
    private void HandleDelete()
    {
        if (_cursorPosition < _currentLine.Length)
        {
            _currentLine = _currentLine.Remove(_cursorPosition, 1);
            ClearLine();
            WriteCurrentLine();
        }
    }

    /// <summary>
    /// Inserts a character at the cursor position.
    /// </summary>
    private void InsertChar(char c)
    {
        _currentLine = _currentLine.Insert(_cursorPosition, c.ToString());
        _cursorPosition++;
        ClearLine();
        WriteCurrentLine();
    }

    /// <summary>
    /// Moves cursor to start of line.
    /// </summary>
    private void MoveCursorToStart()
    {
        _cursorPosition = 0;
        var promptLength = PromptBuilder.GetPromptLength(_state);
        _systemConsole.CursorLeft = promptLength;
    }

    /// <summary>
    /// Moves cursor to end of line.
    /// </summary>
    private void MoveCursorToEnd()
    {
        _cursorPosition = _currentLine.Length;
        var promptLength = PromptBuilder.GetPromptLength(_state);
        _systemConsole.CursorLeft = promptLength + _currentLine.Length;
    }

    /// <summary>
    /// Handles up arrow for history navigation.
    /// </summary>
    private Task HandleHistoryUpAsync()
    {
        // Save current line on first history navigation
        if (_savedLine == null)
        {
            _savedLine = _currentLine;
        }

        var previous = _history.GetPrevious();
        if (previous != null)
        {
            SetLine(previous);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles down arrow for history navigation.
    /// </summary>
    private void HandleHistoryDown()
    {
        var next = _history.GetNext();
        if (next != null)
        {
            // CommandHistory returns string.Empty when reaching the end.
            // If we previously saved an in-progress line, restore it.
            if (next.Length == 0 && _savedLine != null)
            {
                SetLine(_savedLine);
                _savedLine = null;
                return;
            }

            SetLine(next);
        }
        else if (_savedLine != null)
        {
            // Restore saved line when reaching end of history
            SetLine(_savedLine);
            _savedLine = null;
        }
    }

    /// <summary>
    /// Sets the current line and updates display.
    /// </summary>
    private void SetLine(string line)
    {
        ClearLine();
        _currentLine = line;
        _cursorPosition = line.Length;
        WriteCurrentLine();
    }

    /// <summary>
    /// Handles tab completion.
    /// </summary>
    private async Task HandleTabCompletionAsync()
    {
        // Get completions if we don't have them
        if (_lastCompletions == null || !_lastCompletions.HasCompletions)
        {
            _lastCompletions = await _autoComplete.GetCompletionsAsync(_currentLine, _cursorPosition);
            _completionIndex = -1;
        }

        if (!_lastCompletions.HasCompletions)
        {
            // Beep to indicate no completions
            _systemConsole.Beep();
            return;
        }

        if (_lastCompletions.Completions.Count == 1)
        {
            // Single completion - apply it directly
            ApplyCompletion(_lastCompletions.Completions[0]);
            _lastCompletions = null;
            return;
        }

        // Multiple completions - cycle through them or show common prefix
        if (_completionIndex == -1)
        {
            // First tab - try to complete to common prefix
            var commonPrefix = _lastCompletions.GetCommonPrefix();
            if (commonPrefix.Length > _lastCompletions.Prefix.Length)
            {
                ApplyCompletion(commonPrefix);
                // Don't reset - allow cycling on next tab
                return;
            }

            // Show all completions
            ShowCompletions();
            _completionIndex = 0;
        }
        else
        {
            // Cycle through completions
            _completionIndex = (_completionIndex + 1) % _lastCompletions.Completions.Count;
        }

        // Apply current completion
        ApplyCompletion(_lastCompletions.Completions[_completionIndex]);
    }

    /// <summary>
    /// Applies a completion to the current line.
    /// </summary>
    private void ApplyCompletion(string completion)
    {
        if (_lastCompletions == null)
        {
            return;
        }

        // Replace the prefix with the completion
        var before = _currentLine[.._lastCompletions.StartPosition];
        var after = _cursorPosition < _currentLine.Length
            ? _currentLine[_cursorPosition..]
            : string.Empty;

        _currentLine = before + completion + after;
        _cursorPosition = before.Length + completion.Length;

        ClearLine();
        WriteCurrentLine();
    }

    /// <summary>
    /// Shows all available completions.
    /// </summary>
    private void ShowCompletions()
    {
        if (_lastCompletions == null || !_lastCompletions.HasCompletions)
        {
            return;
        }

        _systemConsole.WriteLine();

        // Display completions in columns
        var maxWidth = _lastCompletions.Completions.Max(c => c.Length) + 2;
        var columns = Math.Max(1, _systemConsole.WindowWidth / maxWidth);
        var rows = (int)Math.Ceiling(_lastCompletions.Completions.Count / (double)columns);

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                var index = col * rows + row;
                if (index < _lastCompletions.Completions.Count)
                {
                    var completion = _lastCompletions.Completions[index];
                    _systemConsole.Write(completion.PadRight(maxWidth));
                }
            }
            _systemConsole.WriteLine();
        }

        // Rewrite prompt and current line
        WritePrompt();
        WriteCurrentLine();
    }

    /// <summary>
    /// Deletes the word before the cursor.
    /// </summary>
    private void DeleteWordBackward()
    {
        if (_cursorPosition == 0)
        {
            return;
        }

        var pos = _cursorPosition - 1;

        // Skip trailing spaces
        while (pos > 0 && _currentLine[pos] == ' ')
        {
            pos--;
        }

        // Find start of word
        while (pos > 0 && _currentLine[pos - 1] != ' ')
        {
            pos--;
        }

        _currentLine = _currentLine[..pos] + _currentLine[_cursorPosition..];
        _cursorPosition = pos;
        ClearLine();
        WriteCurrentLine();
    }
}
