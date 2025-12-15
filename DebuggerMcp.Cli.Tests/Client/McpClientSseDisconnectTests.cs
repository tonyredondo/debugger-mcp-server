using System.Collections.Concurrent;
using System.Reflection;
using DebuggerMcp.Cli.Client;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Client;

[Collection("NonParallelConsole")]
public class McpClientSseDisconnectTests
{
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("boom");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("boom");
    }

    [Fact]
    public async Task ListenForSseResponsesAsync_WhenStreamEnds_FailsPendingRequests()
    {
        var client = new McpClient();

        var pendingField = typeof(McpClient).GetField("_pendingRequests", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(pendingField);
        var pending = (ConcurrentDictionary<int, TaskCompletionSource<string>>)pendingField!.GetValue(client)!;

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[123] = tcs;

        var method = typeof(McpClient).GetMethod("ListenForSseResponsesAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        using var reader = new StreamReader(new MemoryStream());
        var task = (Task)method!.Invoke(client, new object[] { reader, CancellationToken.None })!;
        await task;

        await Assert.ThrowsAsync<McpClientException>(() => tcs.Task);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task ListenForSseResponsesAsync_WhenReadThrows_DoesNotWriteToConsole()
    {
        var client = new McpClient();
        var method = typeof(McpClient).GetMethod("ListenForSseResponsesAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);

            using var reader = new StreamReader(new ThrowingStream());
            var task = (Task)method!.Invoke(client, new object[] { reader, CancellationToken.None })!;
            await task;

            Assert.DoesNotContain("SSE listener error", writer.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            writer.Dispose();
        }
    }
}

