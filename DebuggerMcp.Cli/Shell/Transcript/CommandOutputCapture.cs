using System.Text;

namespace DebuggerMcp.Cli.Shell.Transcript;

/// <summary>
/// Captures console output for a single command by teeing <see cref="Console.Out"/>.
/// </summary>
internal sealed class CommandOutputCapture : IDisposable
{
    private readonly TextWriter _originalOut;
    private readonly CappedStringWriter _buffer;
    private bool _disposed;

    internal CommandOutputCapture(int maxCapturedChars = 200_000)
    {
        _originalOut = Console.Out;
        _buffer = new CappedStringWriter(Math.Max(1, maxCapturedChars), capacity: 4096);
        Console.SetOut(new TeeTextWriter(_originalOut, _buffer));
    }

    internal string GetCapturedText()
    {
        var text = _buffer.GetText();
        return AnsiText.StripAnsi(text);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Console.SetOut(_originalOut);
        _buffer.Dispose();
    }

    private sealed class CappedStringWriter : TextWriter
    {
        private readonly int _maxChars;
        private readonly StringBuilder _builder;
        private bool _truncated;

        internal CappedStringWriter(int maxChars, int capacity)
        {
            _maxChars = maxChars;
            _builder = new StringBuilder(capacity);
        }

        public override Encoding Encoding => Encoding.UTF8;

        internal string GetText()
        {
            if (!_truncated)
            {
                return _builder.ToString();
            }

            return _builder.ToString() + $"{Environment.NewLine}[...output capture truncated...]";
        }

        public override void Write(char value)
        {
            if (_truncated)
            {
                return;
            }

            if (_builder.Length + 1 > _maxChars)
            {
                _truncated = true;
                return;
            }

            _builder.Append(value);
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value) || _truncated)
            {
                return;
            }

            var remaining = _maxChars - _builder.Length;
            if (remaining <= 0)
            {
                _truncated = true;
                return;
            }

            if (value.Length > remaining)
            {
                _builder.Append(value.AsSpan(0, remaining));
                _truncated = true;
                return;
            }

            _builder.Append(value);
        }
    }
}
