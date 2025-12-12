using System.Text;
using DebuggerMcp.Cli.Client;

namespace DebuggerMcp.Cli.Tests.Client;

/// <summary>
/// Unit tests for <see cref="ProgressStream"/>.
/// </summary>
public class ProgressStreamTests
{
    [Fact]
    public void Read_ReportsBytesRead()
    {
        var payload = Encoding.UTF8.GetBytes("hello world");
        using var inner = new MemoryStream(payload);
        var reported = new List<long>();
        var progress = new ListProgress(reported);

        using var stream = new ProgressStream(inner, inner.Length, progress);

        var buffer = new byte[5];
        var read1 = stream.Read(buffer, 0, buffer.Length);
        var read2 = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(5, read1);
        Assert.Equal(5, read2);
        Assert.Contains(5, reported);
        Assert.Contains(10, reported);
    }

    [Fact]
    public async Task ReadAsync_ReportsBytesRead()
    {
        var payload = Encoding.UTF8.GetBytes("hello world");
        await using var inner = new MemoryStream(payload);
        var reported = new List<long>();
        var progress = new ListProgress(reported);

        await using var stream = new ProgressStream(inner, inner.Length, progress);

        var buffer = new byte[4];
        var read1 = await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
        var read2 = await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);

        Assert.Equal(4, read1);
        Assert.Equal(4, read2);
        Assert.Contains(4, reported);
        Assert.Contains(8, reported);
    }

    [Fact]
    public void Write_ThrowsNotSupportedException()
    {
        using var inner = new MemoryStream();
        using var stream = new ProgressStream(inner, inner.Length, progress: null);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Write([1, 2, 3], 0, 3));
    }

    private sealed class ListProgress(List<long> items) : IProgress<long>
    {
        public void Report(long value) => items.Add(value);
    }
}
