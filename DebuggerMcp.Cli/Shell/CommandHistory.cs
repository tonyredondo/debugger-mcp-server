namespace DebuggerMcp.Cli.Shell;

/// <summary>
/// Manages command history with persistence support.
/// </summary>
/// <remarks>
/// Features:
/// <list type="bullet">
/// <item><description>In-memory history with navigation</description></item>
/// <item><description>Persistence to file</description></item>
/// <item><description>Configurable maximum size</description></item>
/// <item><description>Duplicate detection</description></item>
/// </list>
/// </remarks>
public class CommandHistory
{
    private readonly List<string> _history = [];
    private readonly string? _historyFilePath;
    private readonly int _maxSize;
    private int _position;

    /// <summary>
    /// Gets the number of commands in history.
    /// </summary>
    public int Count => _history.Count;

    /// <summary>
    /// Gets the current position in history (-1 means at end/new command).
    /// </summary>
    public int Position => _position;

    /// <summary>
    /// Gets all history entries.
    /// </summary>
    public IReadOnlyList<string> Entries => _history.AsReadOnly();

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandHistory"/> class.
    /// </summary>
    /// <param name="historyFilePath">Optional path to persist history.</param>
    /// <param name="maxSize">Maximum number of commands to keep.</param>
    public CommandHistory(string? historyFilePath = null, int maxSize = 1000)
    {
        _historyFilePath = historyFilePath;
        _maxSize = maxSize;
        _position = -1;

        // Load existing history from file
        if (!string.IsNullOrEmpty(_historyFilePath))
        {
            LoadFromFile();
        }
    }

    /// <summary>
    /// Adds a command to history.
    /// </summary>
    /// <param name="command">The command to add.</param>
    public void Add(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        // Don't add duplicates of the last command
        if (_history.Count > 0 && _history[^1] == command)
        {
            ResetPosition();
            return;
        }

        // Add to history
        _history.Add(command);

        // Trim if over max size
        while (_history.Count > _maxSize)
        {
            _history.RemoveAt(0);
        }

        // Reset position to end
        ResetPosition();

        // Persist if file path is configured
        if (!string.IsNullOrEmpty(_historyFilePath))
        {
            SaveToFile();
        }
    }

    /// <summary>
    /// Gets the previous command in history (for up arrow).
    /// </summary>
    /// <returns>The previous command, or null if at beginning.</returns>
    public string? GetPrevious()
    {
        if (_history.Count == 0)
        {
            return null;
        }

        if (_position == -1)
        {
            // First press of up arrow - go to last command
            _position = _history.Count - 1;
        }
        else if (_position > 0)
        {
            // Move back in history
            _position--;
        }

        return _history[_position];
    }

    /// <summary>
    /// Gets the next command in history (for down arrow).
    /// </summary>
    /// <returns>The next command, or empty string if at end.</returns>
    public string? GetNext()
    {
        if (_history.Count == 0 || _position == -1)
        {
            return null;
        }

        if (_position < _history.Count - 1)
        {
            // Move forward in history
            _position++;
            return _history[_position];
        }

        // At the end - return to new command mode
        _position = -1;
        return string.Empty;
    }

    /// <summary>
    /// Resets the position to the end (for new command).
    /// </summary>
    public void ResetPosition()
    {
        _position = -1;
    }

    /// <summary>
    /// Searches history for commands starting with the given prefix.
    /// </summary>
    /// <param name="prefix">The prefix to search for.</param>
    /// <returns>Matching commands, most recent first.</returns>
    public IEnumerable<string> Search(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            // Return all history, most recent first
            for (var i = _history.Count - 1; i >= 0; i--)
            {
                yield return _history[i];
            }
        }
        else
        {
            // Return matching commands, most recent first
            for (var i = _history.Count - 1; i >= 0; i--)
            {
                if (_history[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    yield return _history[i];
                }
            }
        }
    }

    /// <summary>
    /// Clears all history.
    /// </summary>
    public void Clear()
    {
        _history.Clear();
        _position = -1;

        // Clear file if configured
        if (!string.IsNullOrEmpty(_historyFilePath) && File.Exists(_historyFilePath))
        {
            try
            {
                File.Delete(_historyFilePath);
            }
            catch
            {
                // Ignore errors when clearing history file
            }
        }
    }

    /// <summary>
    /// Loads history from file.
    /// </summary>
    private void LoadFromFile()
    {
        if (string.IsNullOrEmpty(_historyFilePath) || !File.Exists(_historyFilePath))
        {
            return;
        }

        try
        {
            var lines = File.ReadAllLines(_historyFilePath);
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _history.Add(line);
                }
            }

            // Trim if loaded too many
            while (_history.Count > _maxSize)
            {
                _history.RemoveAt(0);
            }
        }
        catch
        {
            // Ignore errors loading history
        }
    }

    /// <summary>
    /// Saves history to file.
    /// </summary>
    private void SaveToFile()
    {
        if (string.IsNullOrEmpty(_historyFilePath))
        {
            return;
        }

        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(_historyFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write history
            File.WriteAllLines(_historyFilePath, _history);
        }
        catch
        {
            // Ignore errors saving history
        }
    }
}

