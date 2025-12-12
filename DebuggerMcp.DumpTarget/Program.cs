using System.Collections.Concurrent;
using System.Threading;

namespace DebuggerMcp.DumpTarget;

internal sealed class Node
{
    public required string Name { get; init; }
    public Node? Next { get; set; }
    public int Value { get; init; }
}

internal static class Program
{
    private static readonly object LockA = new();
    private static readonly SemaphoreSlim Semaphore = new(0, 1);
    private static readonly ManualResetEventSlim Gate = new(initialState: false);

    public static async Task Main()
    {
        _ = new Timer(_ => { }, null, dueTime: 1000, period: 1000);

        // Allocate a variety of objects so heap analyzers have something to inspect.
        var largeString = new string('a', 2000);
        var strings = new List<string> { "alpha", "beta", "gamma", largeString };
        var bytes = new byte[1024 * 64];
        var dict = new Dictionary<string, object?>
        {
            ["strings"] = strings,
            ["bytes"] = bytes,
            ["now"] = DateTime.UtcNow,
        };

        var head = new Node { Name = "head", Value = 1 };
        head.Next = new Node { Name = "tail", Value = 2 };

        var queue = new ConcurrentQueue<object?>();
        queue.Enqueue(dict);
        queue.Enqueue(head);
        queue.Enqueue("marker:dump-target");

        // Create contention: one thread holds LockA, another waits on it.
        var holder = new Thread(() =>
        {
            lock (LockA)
            {
                Gate.Set();
                Thread.Sleep(Timeout.Infinite);
            }
        })
        { IsBackground = true };
        holder.Start();

        // Wait until the holder acquired the lock.
        Gate.Wait(TimeSpan.FromSeconds(5));

        var waiter = new Thread(() =>
        {
            lock (LockA)
            {
                Thread.Sleep(Timeout.Infinite);
            }
        })
        { IsBackground = true };
        waiter.Start();

        // Create a SemaphoreSlim waiter.
        _ = Task.Run(async () =>
        {
            await Semaphore.WaitAsync();
        });

        Console.WriteLine($"READY {Environment.ProcessId}");
        Console.Out.Flush();

        // Keep the process alive so the parent can collect a dump.
        await Task.Delay(TimeSpan.FromMinutes(5));
    }
}

