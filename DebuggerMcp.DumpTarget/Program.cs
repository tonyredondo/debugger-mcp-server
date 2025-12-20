using System.Collections.Concurrent;
using System.Reflection;
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

internal struct SampleComplexStruct
{
    public string? Name;
    public object? Payload;
}

internal struct SampleValueType
{
    public int A;
    public long B;
    public SampleNested Nested;
}

internal sealed class EntryArrayRoot
{
    public static object? StaticEntries;

    public required object InstanceEntries;
    public object? SecondaryEntries;
}

internal sealed class TaskRoots
{
    public required Task Faulted;
    public required Task Completed;
    public required Task Canceled;
    public required Task Pending;
}

internal static class Program
{
    private static readonly object LockA = new();
    private static readonly SemaphoreSlim Semaphore = new(0, 1);
    private static readonly ManualResetEventSlim Gate = new(initialState: false);
    private static readonly ManualResetEventSlim LocalsReady = new(initialState: false);
    private static readonly ReaderWriterLockSlim RwWriteHeld = new(LockRecursionPolicy.NoRecursion);
    private static readonly ReaderWriterLockSlim RwReadHeld = new(LockRecursionPolicy.NoRecursion);
    private static readonly ManualResetEventSlim NeverSet = new(initialState: false);
    private static readonly AutoResetEvent AutoReset = new(initialState: false);
    private static readonly ManualResetEvent ManualReset = new(initialState: false);
    private static readonly Mutex Mutex = new(initiallyOwned: false);

    public static async Task Main()
    {
        var disableLargeHeap = IsEnabled("DUMP_TARGET_DISABLE_LARGE_HEAP");

        // A dedicated thread keeps a few primitives/strings alive on the stack so ClrMD ClrStack locals
        // enumeration can find realistic stack roots (primitives + strings) deterministically.
        var localsThread = new Thread(() =>
        {
            object boxedInt = 424242;
            object boxedBool = true;
            var localString = "stack:dump-target";
            LocalsReady.Set();

            while (true)
            {
                GC.KeepAlive(boxedInt);
                GC.KeepAlive(boxedBool);
                GC.KeepAlive(localString);
                Thread.Sleep(250);
            }
        })
        { IsBackground = true, Name = "dump-target-locals" };
        localsThread.Start();
        _ = LocalsReady.Wait(TimeSpan.FromSeconds(5));

        _ = new Timer(_ => { }, null, dueTime: 1000, period: 1000);

        // Create a faulted Task to exercise async analysis paths in dump-based tests.
        var faultedTask = Task.Run(() => throw new InvalidOperationException("faulted:dump-target"));
        var completedTask = Task.Run(() => 123);
        try
        {
            await faultedTask;
        }
        catch (InvalidOperationException)
        {
            // Expected.
        }
        await completedTask;

        var cts = new CancellationTokenSource();
        var canceledTask = Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
        cts.Cancel();
        try
        {
            await canceledTask;
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        var pendingTask = Task.Delay(TimeSpan.FromMinutes(10));
        var taskRoots = new TaskRoots
        {
            Faulted = faultedTask,
            Completed = completedTask,
            Canceled = canceledTask,
            Pending = pendingTask,
        };

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
        var boxedValuesArray = new object?[]
        {
            "marker:boxed-array",
            123,
            true,
            (short)12,
            (long)9999999999,
            3.14,
            'Z',
            (byte)7,
        };

        var dict = new Dictionary<string, object?>
        {
            ["strings"] = strings,
            ["bytes"] = bytes,
            ["now"] = DateTime.UtcNow,
            ["faultedTask"] = faultedTask,
            ["completedTask"] = completedTask,
            ["canceledTask"] = canceledTask,
            ["pendingTask"] = pendingTask,
            ["boxedValuesArray"] = boxedValuesArray,
        };

        var complexStructArray = new SampleComplexStruct[]
        {
            new() { Name = "marker:complex-struct-array", Payload = boxedValuesArray },
            new() { Name = "payload", Payload = dict },
        };
        dict["complexStructArray"] = complexStructArray;

        var head = new Node { Name = "head", Value = 1 };
        head.Next = new Node { Name = "tail", Value = 2 };

        // Build a large Dictionary<ulong,int> so the Entry[] array becomes a top heap consumer with a small count.
        // Keep its internal entries array referenced from both instance and static roots so owner-enrichment code paths
        // in ClrMD analysis have deterministic targets.
        Dictionary<ulong, int>? bigDict = null;
        object? bigEntries = null;
        EntryArrayRoot? entryRoot1 = null;
        EntryArrayRoot? entryRoot2 = null;

        if (!disableLargeHeap)
        {
            bigDict = new Dictionary<ulong, int>(capacity: 100_000);
            for (ulong i = 0; i < 100_000; i++)
            {
                bigDict[i] = (int)(i % int.MaxValue);
            }

            var entriesField = typeof(Dictionary<ulong, int>).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? typeof(Dictionary<ulong, int>).GetField("entries", BindingFlags.NonPublic | BindingFlags.Instance);
            bigEntries = entriesField?.GetValue(bigDict);
            if (bigEntries != null)
            {
                EntryArrayRoot.StaticEntries = bigEntries;
                entryRoot1 = new EntryArrayRoot { InstanceEntries = bigEntries };
                entryRoot2 = new EntryArrayRoot { InstanceEntries = bigEntries, SecondaryEntries = boxedValuesArray };

                // Detach the entry array from the dictionary so owner-enrichment isn't dominated by internal
                // Dictionary<> references. The entry array stays rooted via EntryArrayRoot (instance + static).
                try
                {
                    entriesField?.SetValue(bigDict, null);
                }
                catch
                {
                    // Best-effort.
                }
            }
        }

        var queue = new ConcurrentQueue<object?>();
        queue.Enqueue(dict);
        queue.Enqueue(taskRoots);
        queue.Enqueue(head);
        queue.Enqueue("marker:dump-target");

        if (entryRoot1 != null)
        {
            queue.Enqueue(entryRoot1);
        }

        if (entryRoot2 != null)
        {
            queue.Enqueue(entryRoot2);
        }

        // Avoid keeping redundant references to the large entry array on the async state machine.
        // Owners should be discovered via EntryArrayRoot (instance + static fields), not via program locals.
        bigDict = null;
        bigEntries = null;

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

        // Create ReaderWriterLockSlim contention (read waiter on a write-held lock, and write waiter on a read-held lock).
        RwWriteHeld.EnterWriteLock();
        new Thread(() =>
        {
            RwWriteHeld.EnterReadLock();
            try { Thread.Sleep(Timeout.Infinite); } finally { RwWriteHeld.ExitReadLock(); }
        })
        { IsBackground = true }.Start();

        RwReadHeld.EnterReadLock();
        new Thread(() =>
        {
            RwReadHeld.EnterWriteLock();
            try { Thread.Sleep(Timeout.Infinite); } finally { RwReadHeld.ExitWriteLock(); }
        })
        { IsBackground = true }.Start();

        // Create waiters for reset events and wait handles.
        new Thread(() => NeverSet.Wait()) { IsBackground = true }.Start();
        new Thread(() => AutoReset.WaitOne()) { IsBackground = true }.Start();
        new Thread(() => ManualReset.WaitOne()) { IsBackground = true }.Start();

        Mutex.WaitOne();
        new Thread(() =>
        {
            Mutex.WaitOne();
            try { Thread.Sleep(Timeout.Infinite); } finally { Mutex.ReleaseMutex(); }
        })
        { IsBackground = true }.Start();

        Console.WriteLine($"READY {Environment.ProcessId} VT={vtAddress} VTMT={vtMethodTable}");
        Console.Out.Flush();

        // Keep the process alive so the parent can collect a dump.
        await Task.Delay(TimeSpan.FromMinutes(5));
    }

    private static bool IsEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
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
