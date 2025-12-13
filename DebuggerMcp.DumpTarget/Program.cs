using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace DebuggerMcp.DumpTarget;

internal sealed class Node
{
    public required string Name { get; init; }
    public Node? Next { get; set; }
    public int Value { get; init; }
}

internal struct SampleNested
{
    public int X;
    public long Y;
}

internal struct SampleValueType
{
    public int A;
    public long B;
    public SampleNested Nested;
}

internal static class Program
{
    private static readonly object LockA = new();
    private static readonly SemaphoreSlim Semaphore = new(0, 1);
    private static readonly ManualResetEventSlim Gate = new(initialState: false);

    public static async Task Main()
    {
        _ = new Timer(_ => { }, null, dueTime: 1000, period: 1000);

        // Create a faulted Task to exercise async analysis paths in dump-based tests.
        var faultedTask = Task.Run(() => throw new InvalidOperationException("faulted:dump-target"));

        // Keep a managed exception graph alive so dump-based analysis can inspect it reliably.
        var innerException = new ArgumentException("inner:dump-target");
        var outerException = new InvalidOperationException("outer:dump-target", innerException)
        {
            Source = "DebuggerMcp.DumpTarget",
            HelpLink = "https://example.invalid/help"
        };
        outerException.Data["dump_target_marker"] = "marker:dump-target";
        outerException.Data["count"] = 42;

        var fileNotFoundException = new FileNotFoundException("file missing", "missing.dll");
        var missingMethodException = new MissingMethodException("My.Namespace.Type", "Missing");
        var outOfRangeException = new ArgumentOutOfRangeException("param", actualValue: 123, message: "out of range");
        var disposedException = new ObjectDisposedException("disposed-object", "disposed");
        var aggregateException = new AggregateException(
            "One or more errors occurred.",
            new InvalidOperationException("aggregate-inner"));

        // Keep a stack-allocated value type alive so ClrMD value-type inspection can be tested (dumpvc-like path).
        var vt = new SampleValueType
        {
            A = 123,
            B = 456,
            Nested = new SampleNested { X = 7, Y = 8 }
        };

        var vtAddress = GetUnmanagedAddressHex(ref vt);
        var vtMethodTable = GetMethodTableHex<SampleValueType>();

        // Allocate a variety of objects so heap analyzers have something to inspect.
        var largeString = new string('a', 2000);
        var strings = new List<string> { "alpha", "beta", "gamma", largeString };
        var bytes = new byte[1024 * 64];
        var dict = new Dictionary<string, object?>
        {
            ["strings"] = strings,
            ["bytes"] = bytes,
            ["now"] = DateTime.UtcNow,
            ["faultedTask"] = faultedTask,
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

        Console.WriteLine($"READY {Environment.ProcessId} VT={vtAddress} VTMT={vtMethodTable}");
        Console.Out.Flush();

        // Keep the process alive so the parent can collect a dump.
        await Task.Delay(TimeSpan.FromMinutes(5));
    }

    private static unsafe string GetUnmanagedAddressHex<T>(ref T value) where T : unmanaged
    {
        return $"0x{(ulong)(nuint)Unsafe.AsPointer(ref value):x}";
    }

    private static string GetMethodTableHex<T>()
    {
        return $"0x{(ulong)(nuint)typeof(T).TypeHandle.Value:x}";
    }
}
