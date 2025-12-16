using System.Text;

namespace DebuggerMcp.Cli.Shell.Transcript;

/// <summary>
/// A <see cref="TextWriter"/> that forwards all writes to an inner writer while also copying to a buffer.
/// </summary>
internal sealed class TeeTextWriter(TextWriter inner, TextWriter copyTo) : TextWriter
{
    private readonly TextWriter _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly TextWriter _copyTo = copyTo ?? throw new ArgumentNullException(nameof(copyTo));

    public override Encoding Encoding => _inner.Encoding;

    public override IFormatProvider FormatProvider => _inner.FormatProvider;

    public override void Write(char value)
    {
        _inner.Write(value);
        _copyTo.Write(value);
    }

    public override void Write(string? value)
    {
        _inner.Write(value);
        _copyTo.Write(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        _inner.Write(buffer, index, count);
        _copyTo.Write(buffer, index, count);
    }

    public override void WriteLine(string? value)
    {
        _inner.WriteLine(value);
        _copyTo.WriteLine(value);
    }

    public override Task WriteAsync(char value)
    {
        var t1 = _inner.WriteAsync(value);
        var t2 = _copyTo.WriteAsync(value);
        return Task.WhenAll(t1, t2);
    }

    public override Task WriteAsync(string? value)
    {
        var t1 = _inner.WriteAsync(value);
        var t2 = _copyTo.WriteAsync(value);
        return Task.WhenAll(t1, t2);
    }

    public override Task WriteLineAsync(string? value)
    {
        var t1 = _inner.WriteLineAsync(value);
        var t2 = _copyTo.WriteLineAsync(value);
        return Task.WhenAll(t1, t2);
    }

    protected override void Dispose(bool disposing)
    {
        // Do not dispose inner writer (Console.Out) or the copy writer (StringWriter) here.
        base.Dispose(disposing);
    }
}

