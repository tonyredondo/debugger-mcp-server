using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;

namespace DebuggerMcp.Logging;

/// <summary>
/// A simple file logger provider that writes logs to rolling daily files.
/// File format: {prefix}-{date}-{pid}.log (e.g., server-2024-12-02-12345.log)
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly string _filePrefix;
    private readonly LogLevel _minimumLevel;
    private readonly int _processId;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentFilePath = string.Empty;
    private DateTime _currentDate;

    public FileLoggerProvider(string logDirectory, string filePrefix = "server", LogLevel minimumLevel = LogLevel.Information)
    {
        _logDirectory = logDirectory;
        _filePrefix = filePrefix;
        _minimumLevel = minimumLevel;
        _processId = Environment.ProcessId;

        // Ensure log directory exists
        Directory.CreateDirectory(_logDirectory);

        // Initialize the writer for today
        EnsureWriterForDate(DateTime.UtcNow);
    }

    public ILogger CreateLogger(string categoryName)
    {
        // Reuse loggers per category to avoid recreating wrappers on every request
        return _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this, _minimumLevel));
    }

    internal void WriteLog(string categoryName, LogLevel logLevel, string message, Exception? exception)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            EnsureWriterForDate(now.DateTime);

            if (_writer == null)
                return;

            var levelString = logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "???"
            };

            // ISO 8601 format with milliseconds for easy parsing and duration calculations
            // Format: 2024-12-02T15:30:45.123Z (Z indicates UTC)
            var timestamp = now.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";
            var shortCategory = GetShortCategoryName(categoryName);

            _writer.WriteLine($"[{timestamp}] [{levelString}] [{shortCategory}] {message}");

            if (exception != null)
            {
                _writer.WriteLine($"  Exception: {exception.GetType().Name}: {exception.Message}");
                if (exception.StackTrace != null)
                {
                    foreach (var line in exception.StackTrace.Split('\n'))
                    {
                        _writer.WriteLine($"    {line.TrimEnd()}");
                    }
                }
            }

            _writer.Flush();
        }
    }

    private static string GetShortCategoryName(string categoryName)
    {
        // Get just the class name from fully qualified name
        var lastDot = categoryName.LastIndexOf('.');
        return lastDot >= 0 ? categoryName[(lastDot + 1)..] : categoryName;
    }

    private void EnsureWriterForDate(DateTime date)
    {
        var dateOnly = date.Date;

        if (_currentDate == dateOnly && _writer != null)
            // Writer already set for today
            return;

        // Close existing writer and null it before creating new one
        // This prevents ObjectDisposedException if new writer creation fails
        _writer?.Dispose();
        _writer = null;

        try
        {
            // Create new file for today with PID
            _currentDate = dateOnly;
            var fileName = $"{_filePrefix}-{dateOnly:yyyy-MM-dd}-{_processId}.log";
            _currentFilePath = Path.Combine(_logDirectory, fileName);

            // Open file in append mode
            _writer = new StreamWriter(
                new FileStream(_currentFilePath, FileMode.Append, FileAccess.Write, FileShare.Read),
                Encoding.UTF8)
            {
                AutoFlush = false
            };

            // Write header for new log file with ISO 8601 timestamp
            _writer.WriteLine($"=== Log started at {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fff}Z (PID: {_processId}) ===");
            _writer.Flush();
        }
        catch
        {
            // If file creation fails, ensure writer is null so we don't attempt writes
            _writer?.Dispose();
            _writer = null;
            // Silently fail - logging should not crash the application
        }
    }

    public void Dispose()
    {
        _loggers.Clear();

        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>
/// Individual logger that writes to the file logger provider.
/// </summary>
internal sealed class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly FileLoggerProvider _provider;
    private readonly LogLevel _minimumLevel;

    public FileLogger(string categoryName, FileLoggerProvider provider, LogLevel minimumLevel)
    {
        _categoryName = categoryName;
        _provider = provider;
        _minimumLevel = minimumLevel;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        _provider.WriteLog(_categoryName, logLevel, message, exception);
    }
}
