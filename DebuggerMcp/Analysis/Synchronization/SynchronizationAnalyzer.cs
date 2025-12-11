using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.Analysis.Synchronization;

/// <summary>
/// Analyzes synchronization primitives in .NET memory dumps using ClrMD.
/// Detects locks, semaphores, events, and potential deadlocks.
/// </summary>
public class SynchronizationAnalyzer
{
    private readonly ClrRuntime _runtime;
    private readonly ClrHeap _heap;
    private readonly ILogger? _logger;
    private readonly bool _skipSyncBlocks;

    // Field names for SemaphoreSlim (may vary by .NET version)
    private static readonly string[] SemaphoreSlimCurrentCountFields = ["m_currentCount", "_currentCount"];
    private static readonly string[] SemaphoreSlimMaxCountFields = ["m_maxCount", "_maxCount"];
    private static readonly string[] SemaphoreSlimWaitCountFields = ["m_waitCount", "_waitCount"];
    private static readonly string[] SemaphoreSlimAsyncHeadFields = ["m_asyncHead", "_asyncHead"];

    // Field names for ReaderWriterLockSlim
    private static readonly string[] RwLockOwnersFields = ["_owners", "owners"];
    private static readonly string[] RwLockWriteWaitersFields = ["_numWriteWaiters", "numWriteWaiters"];
    private static readonly string[] RwLockReadWaitersFields = ["_numReadWaiters", "numReadWaiters"];
    private static readonly string[] RwLockUpgradeWaitersFields = ["_numUpgradeWaiters", "numUpgradeWaiters"];
    private static readonly string[] RwLockWriterIdFields = ["_writeLockOwnerId", "writeLockOwnerId"];

    // Field names for ManualResetEventSlim
    private static readonly string[] MresStateFields = ["m_combinedState", "_combinedState"];

    // ManualResetEventSlim state bit masks
    private const int MresSignaledBit = 0x8000;
    private const int MresNumWaitersShift = 16;

    /// <summary>
    /// Initializes a new instance of the <see cref="SynchronizationAnalyzer"/> class.
    /// </summary>
    /// <param name="runtime">The ClrMD runtime.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="skipSyncBlocks">If true, skips EnumerateSyncBlocks which can crash under cross-architecture emulation.</param>
    public SynchronizationAnalyzer(ClrRuntime runtime, ILogger? logger = null, bool skipSyncBlocks = false)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _heap = runtime.Heap;
        _logger = logger;
        _skipSyncBlocks = skipSyncBlocks;
    }

    /// <summary>
    /// Analyzes all synchronization primitives in the dump.
    /// </summary>
    /// <returns>Complete synchronization analysis result.</returns>
    public SynchronizationAnalysisResult Analyze()
    {
        _logger?.LogInformation("[SyncAnalyzer] Starting synchronization analysis... (skipSyncBlocks={SkipSyncBlocks})", _skipSyncBlocks);

        var result = new SynchronizationAnalysisResult();

        try
        {
            // Phase 1: Analyze Monitor locks from sync blocks
            // Note: EnumerateSyncBlocks can CRASH (SIGSEGV) under cross-architecture emulation
            // (e.g., x64 dump analyzed on arm64 host via Rosetta 2)
            if (_skipSyncBlocks)
            {
                _logger?.LogInformation("[SyncAnalyzer] Skipping sync block analysis (cross-arch or unsafe scenario)");
                result.MonitorLocks = new List<MonitorLockInfo>();
            }
            else
            {
                try
                {
                    result.MonitorLocks = AnalyzeMonitorLocks();
                    _logger?.LogDebug("[SyncAnalyzer] Found {Count} monitor locks", result.MonitorLocks.Count);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[SyncAnalyzer] Monitor lock analysis failed, continuing with other phases");
                    result.MonitorLocks = new List<MonitorLockInfo>();
                }
            }

            // Phase 2-5: Analyze all sync primitives in a single heap pass for efficiency
            // Note: EnumerateObjects can crash on corrupted dumps
            try
            {
                AnalyzeHeapSyncPrimitives(result);
                _logger?.LogDebug("[SyncAnalyzer] Found {Semaphores} SemaphoreSlim, {RWLocks} ReaderWriterLockSlim, {Events} reset events, {Handles} WaitHandle instances",
                    result.SemaphoreSlims?.Count ?? 0,
                    result.ReaderWriterLocks?.Count ?? 0,
                    result.ResetEvents?.Count ?? 0,
                    result.WaitHandles?.Count ?? 0);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[SyncAnalyzer] Heap sync primitives analysis failed, continuing with wait graph");
            }

            // Phase 6: Build wait graph
            result.WaitGraph = BuildWaitGraph(result);

            // Phase 7: Detect deadlocks
            result.PotentialDeadlocks = DetectDeadlocks(result.WaitGraph);
            if (result.PotentialDeadlocks.Count > 0)
            {
                _logger?.LogWarning("[SyncAnalyzer] Detected {Count} potential deadlock(s)!", result.PotentialDeadlocks.Count);
            }

            // Phase 8: Identify contention hotspots
            result.ContentionHotspots = IdentifyContentionHotspots(result);

            // Build summary
            result.Summary = BuildSummary(result);

            _logger?.LogInformation("[SyncAnalyzer] Synchronization analysis complete. Deadlocks: {Deadlocks}, Contention: {Contention}",
                result.PotentialDeadlocks.Count, result.Summary.ContentionDetected);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SyncAnalyzer] Error during synchronization analysis");
        }

        return result;
    }

    /// <summary>
    /// Analyzes Monitor locks by examining sync blocks.
    /// </summary>
    private List<MonitorLockInfo> AnalyzeMonitorLocks()
    {
        var locks = new List<MonitorLockInfo>();

        try
        {
            // Build thread address to thread mapping
            var threadsByAddress = new Dictionary<ulong, ClrThread>();
            foreach (var thread in _runtime.Threads)
            {
                try
                {
                    if (thread.Address != 0)
                    {
                        threadsByAddress[thread.Address] = thread;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "[SyncAnalyzer] Error reading thread address");
                }
            }

            // EnumerateSyncBlocks can crash on some dumps (especially Alpine/musl)
            // Wrap in defensive try-catch and iterate safely
            IEnumerable<SyncBlock>? syncBlocks = null;
            try
            {
                syncBlocks = _heap.EnumerateSyncBlocks();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[SyncAnalyzer] EnumerateSyncBlocks failed - sync block analysis skipped");
                return locks;
            }

            foreach (var syncBlock in syncBlocks)
            {
                try
                {
                    // Only include sync blocks that have a monitor held
                    if (syncBlock.IsMonitorHeld && syncBlock.HoldingThreadAddress != 0)
                    {
                        var obj = _heap.GetObject(syncBlock.Object);
                        
                        // Find the owning thread by address
                        ClrThread? owningThread = null;
                        if (threadsByAddress.TryGetValue(syncBlock.HoldingThreadAddress, out var thread))
                        {
                            owningThread = thread;
                        }
                        
                        var lockInfo = new MonitorLockInfo
                        {
                            Address = $"0x{syncBlock.Object:X16}",
                            ObjectType = obj.Type?.Name ?? "<unknown>",
                            OwnerThreadId = owningThread?.ManagedThreadId ?? 0,
                            OwnerOsThreadId = owningThread?.OSThreadId ?? 0,
                            RecursionCount = syncBlock.RecursionCount,
                            WaitingThreads = syncBlock.WaitingThreadCount > 0 
                                ? FindThreadsWaitingOnMonitor(syncBlock.Object) 
                                : null
                        };

                        locks.Add(lockInfo);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "[SyncAnalyzer] Error processing sync block at 0x{Address:X16}", syncBlock.Object);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SyncAnalyzer] Error during monitor lock analysis");
        }

        return locks;
    }

    /// <summary>
    /// Finds threads waiting on a specific monitor (by analyzing thread stacks and stack roots).
    /// </summary>
    private List<int>? FindThreadsWaitingOnMonitor(ulong objectAddress)
    {
        var waitingThreads = new List<int>();

        try
        {
            foreach (var thread in _runtime.Threads)
            {
                if (!thread.IsAlive)
                    continue;

                // Check if thread is in a Monitor wait method
                var isInMonitorWait = false;
                foreach (var frame in thread.EnumerateStackTrace())
                {
                    var methodName = frame.Method?.Name ?? "";
                    var typeName = frame.Method?.Type?.Name ?? "";

                    // Check for Monitor.Enter, Monitor.Wait patterns
                    if ((typeName.Contains("Monitor") && (methodName.Contains("Enter") || methodName.Contains("Wait"))) ||
                        (typeName.Contains("ObjectNative") && methodName.Contains("WaitTimeout")))
                    {
                        isInMonitorWait = true;
                        break;
                    }
                }

                if (!isInMonitorWait)
                    continue;

                // Check if our target object is on the thread's stack roots
                try
                {
                    var hasTargetObject = thread.EnumerateStackRoots()
                        .Any(root => root.Object == objectAddress);

                    if (hasTargetObject)
                    {
                        waitingThreads.Add(thread.ManagedThreadId);
                    }
                }
                catch
                {
                    // If we can't enumerate stack roots, add thread anyway since it's in a wait
                    // This is a heuristic - thread is waiting on SOME monitor
                    waitingThreads.Add(thread.ManagedThreadId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[SyncAnalyzer] Error finding threads waiting on monitor 0x{Address:X}", objectAddress);
        }

        return waitingThreads.Count > 0 ? waitingThreads : null;
    }

    /// <summary>
    /// Analyzes all heap-based synchronization primitives with owner information.
    /// Uses two phases: first collects primitives, then finds their owners.
    /// </summary>
    private void AnalyzeHeapSyncPrimitives(SynchronizationAnalysisResult result)
    {
        var semaphores = new List<SemaphoreSlimInfo>();
        var rwLocks = new List<ReaderWriterLockInfo>();
        var events = new List<ResetEventInfo>();
        var handles = new List<WaitHandleInfo>();
        
        // Track all sync primitive addresses for owner lookup
        var syncPrimitiveAddresses = new HashSet<ulong>();
        // Map from primitive address to its info object for updating with owner
        var addressToSemaphore = new Dictionary<ulong, SemaphoreSlimInfo>();
        var addressToRwLock = new Dictionary<ulong, ReaderWriterLockInfo>();
        var addressToEvent = new Dictionary<ulong, ResetEventInfo>();
        var addressToHandle = new Dictionary<ulong, WaitHandleInfo>();

        try
        {
            // Phase 1: Collect all sync primitives
            _logger?.LogDebug("[SyncAnalyzer] Phase 1: Collecting sync primitives...");
            foreach (var obj in _heap.EnumerateObjects())
            {
                var typeName = obj.Type?.Name;
                if (string.IsNullOrEmpty(typeName))
                    continue;

                switch (typeName)
                {
                    case "System.Threading.SemaphoreSlim":
                        var semInfo = ExtractSemaphoreSlimInfo(obj);
                        if (semInfo != null)
                        {
                            semaphores.Add(semInfo);
                            syncPrimitiveAddresses.Add(obj.Address);
                            addressToSemaphore[obj.Address] = semInfo;
                        }
                        break;

                    case "System.Threading.ReaderWriterLockSlim":
                        var rwInfo = ExtractReaderWriterLockInfo(obj);
                        if (rwInfo != null)
                        {
                            rwLocks.Add(rwInfo);
                            syncPrimitiveAddresses.Add(obj.Address);
                            addressToRwLock[obj.Address] = rwInfo;
                        }
                        break;

                    case "System.Threading.ManualResetEventSlim":
                        var mresInfo = ExtractManualResetEventSlimInfo(obj);
                        if (mresInfo != null)
                        {
                            events.Add(mresInfo);
                            syncPrimitiveAddresses.Add(obj.Address);
                            addressToEvent[obj.Address] = mresInfo;
                        }
                        break;

                    case "System.Threading.AutoResetEvent":
                    case "System.Threading.ManualResetEvent":
                        var eventInfo = new ResetEventInfo
                        {
                            Address = $"0x{obj.Address:X16}",
                            EventType = typeName.Split('.').Last(),
                            IsSignaled = false,
                            Waiters = 0
                        };
                        events.Add(eventInfo);
                        syncPrimitiveAddresses.Add(obj.Address);
                        addressToEvent[obj.Address] = eventInfo;
                        break;

                    case "System.Threading.Mutex":
                    case "System.Threading.Semaphore":
                    case "System.Threading.EventWaitHandle":
                        var handleInfo = new WaitHandleInfo
                        {
                            Address = $"0x{obj.Address:X16}",
                            HandleType = typeName.Split('.').Last(),
                            WaitingThreads = FindThreadsWaitingOnWaitHandle(obj.Address)
                        };
                        handles.Add(handleInfo);
                        syncPrimitiveAddresses.Add(obj.Address);
                        addressToHandle[obj.Address] = handleInfo;
                        break;
                }
            }

            // Phase 2: Find owners by scanning all objects for references to primitives
            if (syncPrimitiveAddresses.Count > 0)
            {
                _logger?.LogDebug("[SyncAnalyzer] Phase 2: Finding owners for {Count} primitives...", syncPrimitiveAddresses.Count);
                FindSyncPrimitiveOwners(syncPrimitiveAddresses, addressToSemaphore, addressToRwLock, addressToEvent, addressToHandle);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SyncAnalyzer] Error enumerating heap sync primitives");
        }

        result.SemaphoreSlims = semaphores;
        result.ReaderWriterLocks = rwLocks;
        result.ResetEvents = events;
        result.WaitHandles = handles;
    }

    /// <summary>
    /// Finds owners for sync primitives by scanning heap for objects with fields pointing to them,
    /// and also scanning static fields.
    /// </summary>
    private void FindSyncPrimitiveOwners(
        HashSet<ulong> syncPrimitiveAddresses,
        Dictionary<ulong, SemaphoreSlimInfo> addressToSemaphore,
        Dictionary<ulong, ReaderWriterLockInfo> addressToRwLock,
        Dictionary<ulong, ResetEventInfo> addressToEvent,
        Dictionary<ulong, WaitHandleInfo> addressToHandle)
    {
        var foundOwners = 0;
        var processedStaticTypes = new HashSet<string>();
        
        try
        {
            foreach (var obj in _heap.EnumerateObjects())
            {
                var objType = obj.Type;
                if (objType == null)
                    continue;

                var typeName = objType.Name ?? "";
                
                // Skip only sync primitive types themselves (they shouldn't be listed as owners of themselves)
                // But allow other System.Threading.* types like Thread, ThreadPool, etc.
                if (IsSyncPrimitiveType(typeName))
                    continue;

                // Check static fields once per type
                if (!processedStaticTypes.Contains(typeName))
                {
                    processedStaticTypes.Add(typeName);
                    foundOwners += FindStaticFieldOwners(objType, syncPrimitiveAddresses, 
                        addressToSemaphore, addressToRwLock, addressToEvent, addressToHandle);
                }

                // Check instance fields
                foreach (var field in objType.Fields)
                {
                    if (!field.IsObjectReference)
                        continue;

                    var fieldName = field.Name;
                    if (string.IsNullOrEmpty(fieldName))
                        continue;

                    try
                    {
                        var fieldValue = obj.ReadObjectField(fieldName);
                        if (!fieldValue.IsValid || fieldValue.IsNull)
                            continue;

                        var refAddr = fieldValue.Address;
                        if (!syncPrimitiveAddresses.Contains(refAddr))
                            continue;

                        // Found an owner! Add to the appropriate info object's owner list
                        var owner = new SyncPrimitiveOwner
                        {
                            Address = $"0x{obj.Address:X16}",
                            Type = typeName,
                            FieldName = fieldName,
                            IsStatic = false
                        };

                        AddOwnerToInfo(refAddr, owner, addressToSemaphore, addressToRwLock, addressToEvent, addressToHandle);
                        foundOwners++;
                    }
                    catch
                    {
                        // Skip fields that can't be read
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[SyncAnalyzer] Error finding sync primitive owners");
        }

        _logger?.LogDebug("[SyncAnalyzer] Found {Count} owner references for sync primitives", foundOwners);
    }

    /// <summary>
    /// Finds static field references to sync primitives for a given type.
    /// </summary>
    private int FindStaticFieldOwners(
        ClrType type,
        HashSet<ulong> syncPrimitiveAddresses,
        Dictionary<ulong, SemaphoreSlimInfo> addressToSemaphore,
        Dictionary<ulong, ReaderWriterLockInfo> addressToRwLock,
        Dictionary<ulong, ResetEventInfo> addressToEvent,
        Dictionary<ulong, WaitHandleInfo> addressToHandle)
    {
        var foundCount = 0;
        var typeName = type.Name ?? "<unknown>";

        foreach (var staticField in type.StaticFields)
        {
            if (!staticField.IsObjectReference)
                continue;

            try
            {
                // Try to read the static field value from each app domain
                foreach (var domain in _runtime.AppDomains)
                {
                    try
                    {
                        var fieldValue = staticField.ReadObject(domain);
                        if (!fieldValue.IsValid || fieldValue.IsNull)
                            continue;

                        var refAddr = fieldValue.Address;
                        if (!syncPrimitiveAddresses.Contains(refAddr))
                            continue;

                        // Found a static field owner!
                        var owner = new SyncPrimitiveOwner
                        {
                            Address = null, // No instance address for static fields
                            Type = typeName,
                            FieldName = staticField.Name ?? "<unknown>",
                            IsStatic = true
                        };

                        AddOwnerToInfo(refAddr, owner, addressToSemaphore, addressToRwLock, addressToEvent, addressToHandle);
                        foundCount++;
                    }
                    catch
                    {
                        // Skip domains where field can't be read
                    }
                }
            }
            catch
            {
                // Skip fields that can't be read
            }
        }

        return foundCount;
    }

    /// <summary>
    /// Checks if a type is a synchronization primitive type that shouldn't be listed as an owner.
    /// </summary>
    private static bool IsSyncPrimitiveType(string typeName)
    {
        return typeName is 
            "System.Threading.SemaphoreSlim" or
            "System.Threading.ReaderWriterLockSlim" or
            "System.Threading.ManualResetEventSlim" or
            "System.Threading.AutoResetEvent" or
            "System.Threading.ManualResetEvent" or
            "System.Threading.Mutex" or
            "System.Threading.Semaphore" or
            "System.Threading.EventWaitHandle" or
            // Also skip internal helper types within these primitives
            "System.Threading.SemaphoreSlim+TaskNode" or
            "System.Threading.ReaderWriterLockSlim+TimeoutTracker" or
            "System.Threading.ManualResetEventSlim+CancellationTokenRegistration";
    }

    /// <summary>
    /// Adds an owner to the appropriate sync primitive info object.
    /// </summary>
    private static void AddOwnerToInfo(
        ulong refAddr,
        SyncPrimitiveOwner owner,
        Dictionary<ulong, SemaphoreSlimInfo> addressToSemaphore,
        Dictionary<ulong, ReaderWriterLockInfo> addressToRwLock,
        Dictionary<ulong, ResetEventInfo> addressToEvent,
        Dictionary<ulong, WaitHandleInfo> addressToHandle)
    {
        if (addressToSemaphore.TryGetValue(refAddr, out var semInfo))
        {
            semInfo.Owners ??= [];
            semInfo.Owners.Add(owner);
        }
        else if (addressToRwLock.TryGetValue(refAddr, out var rwInfo))
        {
            rwInfo.Owners ??= [];
            rwInfo.Owners.Add(owner);
        }
        else if (addressToEvent.TryGetValue(refAddr, out var eventInfo))
        {
            eventInfo.Owners ??= [];
            eventInfo.Owners.Add(owner);
        }
        else if (addressToHandle.TryGetValue(refAddr, out var handleInfo))
        {
            handleInfo.Owners ??= [];
            handleInfo.Owners.Add(owner);
        }
    }

    /// <summary>
    /// Extracts information from a SemaphoreSlim object.
    /// </summary>
    private SemaphoreSlimInfo? ExtractSemaphoreSlimInfo(ClrObject obj)
    {
        try
        {
            var info = new SemaphoreSlimInfo
            {
                Address = $"0x{obj.Address:X16}"
            };

            // Read current count
            info.CurrentCount = ReadFieldInt32(obj, SemaphoreSlimCurrentCountFields);

            // Read max count
            info.MaxCount = ReadFieldInt32(obj, SemaphoreSlimMaxCountFields);

            // Read wait count (synchronous waiters)
            info.SyncWaiters = ReadFieldInt32(obj, SemaphoreSlimWaitCountFields);

            // Count async waiters by walking the TaskNode linked list
            info.AsyncWaiters = CountAsyncWaiters(obj);

            // Get detailed waiter information if contended
            if (info.IsContended)
            {
                info.Waiters = GetSemaphoreWaiters(obj, info.AsyncWaiters);
            }

            return info;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[SyncAnalyzer] Error extracting SemaphoreSlim info at 0x{Address:X}", obj.Address);
            return null;
        }
    }

    /// <summary>
    /// Counts async waiters by walking the TaskNode linked list.
    /// </summary>
    private int CountAsyncWaiters(ClrObject semaphore)
    {
        int count = 0;
        try
        {
            var head = ReadObjectField(semaphore, SemaphoreSlimAsyncHeadFields);
            if (head.IsValid && !head.IsNull)
            {
                var current = head;
                var visited = new HashSet<ulong>();

                while (current.IsValid && !current.IsNull && !visited.Contains(current.Address))
                {
                    visited.Add(current.Address);
                    count++;

                    // Try to get next node (field name varies)
                    var nextField = current.Type?.GetFieldByName("Next") ?? current.Type?.GetFieldByName("_next");
                    if (nextField != null)
                    {
                        current = current.ReadObjectField(nextField.Name!);
                    }
                    else
                    {
                        break;
                    }

                    // Safety limit
                    if (count > 10000) break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[SyncAnalyzer] Error counting async waiters");
        }

        return count;
    }

    /// <summary>
    /// Gets detailed waiter information for a SemaphoreSlim.
    /// </summary>
    private List<WaiterInfo>? GetSemaphoreWaiters(ClrObject semaphore, int asyncWaiters)
    {
        var waiters = new List<WaiterInfo>();

        try
        {
            // Find threads waiting on this semaphore (stack analysis)
            foreach (var thread in _runtime.Threads)
            {
                if (thread.IsAlive && IsThreadWaitingOnSemaphore(thread, semaphore.Address))
                {
                    waiters.Add(new WaiterInfo
                    {
                        ThreadId = thread.ManagedThreadId,
                        IsAsync = false
                    });
                }
            }

            // Add async waiters (we know the count, but can't get task addresses easily)
            for (int i = 0; i < asyncWaiters && i < 100; i++)
            {
                waiters.Add(new WaiterInfo
                {
                    IsAsync = true,
                    TaskAddress = "<async waiter>"
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[SyncAnalyzer] Error getting semaphore waiters");
        }

        return waiters.Count > 0 ? waiters : null;
    }

    /// <summary>
    /// Checks if a thread is waiting on a specific SemaphoreSlim.
    /// </summary>
    private bool IsThreadWaitingOnSemaphore(ClrThread thread, ulong semaphoreAddress)
    {
        try
        {
            // First check if thread is in a SemaphoreSlim wait method
            var isInSemaphoreWait = false;
            foreach (var frame in thread.EnumerateStackTrace())
            {
                var typeName = frame.Method?.Type?.Name ?? "";
                var methodName = frame.Method?.Name ?? "";

                if (typeName.Contains("SemaphoreSlim") &&
                    (methodName.Contains("Wait") || methodName.Contains("WaitUntilCountOrTimeout")))
                {
                    isInSemaphoreWait = true;
                    break;
                }
            }

            if (!isInSemaphoreWait)
                return false;

            // Verify the specific semaphore object is on the thread's stack
            try
            {
                return thread.EnumerateStackRoots()
                    .Any(root => root.Object == semaphoreAddress);
            }
            catch
            {
                // If we can't enumerate stack roots, return true since thread is in a wait
                return true;
            }
        }
        catch
        {
            // Ignore stack walk errors
        }

        return false;
    }

    /// <summary>
    /// Extracts information from a ReaderWriterLockSlim object.
    /// </summary>
    private ReaderWriterLockInfo? ExtractReaderWriterLockInfo(ClrObject obj)
    {
        try
        {
            var info = new ReaderWriterLockInfo
            {
                Address = $"0x{obj.Address:X16}"
            };

            // Read owners field (contains reader/writer/upgrader state as bit field)
            var owners = ReadFieldInt32(obj, RwLockOwnersFields);

            // Decode owners bit field
            // Bit layout varies by .NET version, but typically:
            // - Bits 0-9: reader count
            // - Bit 10: writer flag
            // - Bit 11: upgrader flag
            const int ReaderMask = 0x3FF; // 10 bits for readers
            const int WriterFlag = 0x400;
            const int UpgraderFlag = 0x800;

            info.CurrentReaders = owners & ReaderMask;
            info.HasWriter = (owners & WriterFlag) != 0;
            info.HasUpgrader = (owners & UpgraderFlag) != 0;

            // Read waiter counts
            info.WriteWaiters = (int)ReadFieldUInt32(obj, RwLockWriteWaitersFields);
            info.ReadWaiters = (int)ReadFieldUInt32(obj, RwLockReadWaitersFields);
            info.UpgradeWaiters = (int)ReadFieldUInt32(obj, RwLockUpgradeWaitersFields);

            // Read writer thread ID
            var writerId = ReadFieldInt32(obj, RwLockWriterIdFields);
            if (writerId != 0 && writerId != -1)
            {
                info.WriterThreadId = writerId;
            }

            return info;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[SyncAnalyzer] Error extracting ReaderWriterLockSlim info at 0x{Address:X}", obj.Address);
            return null;
        }
    }

    /// <summary>
    /// Extracts information from a ManualResetEventSlim object.
    /// </summary>
    private ResetEventInfo? ExtractManualResetEventSlimInfo(ClrObject obj)
    {
        try
        {
            var info = new ResetEventInfo
            {
                Address = $"0x{obj.Address:X16}",
                EventType = "ManualResetEventSlim"
            };

            // Read combined state field
            var combinedState = ReadFieldInt32(obj, MresStateFields);

            // Decode state - use unsigned shift to avoid sign extension
            info.IsSignaled = (combinedState & MresSignaledBit) != 0;
            info.Waiters = (int)((uint)combinedState >> MresNumWaitersShift);
            info.SpinCount = combinedState & 0x7FFF; // Lower 15 bits (spin count, excluding signaled bit)

            return info;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[SyncAnalyzer] Error extracting ManualResetEventSlim info at 0x{Address:X}", obj.Address);
            return null;
        }
    }

    /// <summary>
    /// Finds threads waiting on a specific WaitHandle by stack analysis.
    /// </summary>
    private List<int>? FindThreadsWaitingOnWaitHandle(ulong handleAddress)
    {
        var waitingThreads = new List<int>();

        try
        {
            foreach (var thread in _runtime.Threads)
            {
                if (!thread.IsAlive)
                    continue;

                // Check if thread is in a WaitHandle wait method
                var isInWaitHandleWait = false;
                foreach (var frame in thread.EnumerateStackTrace())
                {
                    var typeName = frame.Method?.Type?.Name ?? "";
                    var methodName = frame.Method?.Name ?? "";

                    if ((typeName.Contains("WaitHandle") || typeName.Contains("Mutex") ||
                         typeName.Contains("Semaphore") || typeName.Contains("EventWaitHandle")) &&
                        (methodName.Contains("WaitOne") || methodName.Contains("WaitAny") ||
                         methodName.Contains("WaitAll")))
                    {
                        isInWaitHandleWait = true;
                        break;
                    }
                }

                if (!isInWaitHandleWait)
                    continue;

                // Verify the specific handle object is on the thread's stack
                try
                {
                    var hasTargetObject = thread.EnumerateStackRoots()
                        .Any(root => root.Object == handleAddress);

                    if (hasTargetObject)
                    {
                        waitingThreads.Add(thread.ManagedThreadId);
                    }
                }
                catch
                {
                    // If we can't enumerate stack roots, add thread anyway since it's in a wait
                    waitingThreads.Add(thread.ManagedThreadId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[SyncAnalyzer] Error finding threads waiting on WaitHandle");
        }

        return waitingThreads.Count > 0 ? waitingThreads : null;
    }

    /// <summary>
    /// Builds a wait graph from the analyzed synchronization primitives.
    /// </summary>
    private WaitGraph BuildWaitGraph(SynchronizationAnalysisResult result)
    {
        var graph = new WaitGraph();
        var threadNodes = new Dictionary<int, WaitGraphNode>();
        var resourceNodes = new Dictionary<string, WaitGraphNode>();

        // Add nodes for threads with synchronization activity
        foreach (var thread in _runtime.Threads)
        {
            if (thread.IsAlive && thread.ManagedThreadId > 0)
            {
                var nodeId = $"thread_{thread.ManagedThreadId}";
                var node = new WaitGraphNode
                {
                    Id = nodeId,
                    Type = "thread",
                    Label = $"Thread {thread.ManagedThreadId} (OS: {thread.OSThreadId})"
                };
                threadNodes[thread.ManagedThreadId] = node;
            }
        }

        // Add monitor lock nodes and edges
        foreach (var monitor in result.MonitorLocks ?? [])
        {
            var nodeId = $"monitor_{monitor.Address}";
            var node = new WaitGraphNode
            {
                Id = nodeId,
                Type = "resource",
                Label = $"Monitor@{monitor.Address}",
                ResourceType = "Monitor"
            };
            resourceNodes[nodeId] = node;
            graph.Nodes.Add(node);

            // Edge: resource -> owner thread (owns)
            if (threadNodes.TryGetValue(monitor.OwnerThreadId, out var ownerNode))
            {
                graph.Edges.Add(new WaitGraphEdge
                {
                    From = nodeId,
                    To = ownerNode.Id,
                    Label = "owned by"
                });
            }

            // Edges: waiting threads -> resource (waits)
            foreach (var waiterId in monitor.WaitingThreads ?? [])
            {
                if (threadNodes.TryGetValue(waiterId, out var waiterNode))
                {
                    graph.Edges.Add(new WaitGraphEdge
                    {
                        From = waiterNode.Id,
                        To = nodeId,
                        Label = "waits"
                    });
                }
            }
        }

        // Add SemaphoreSlim nodes (only contended ones)
        // Note: SemaphoreSlim doesn't have a clear "owner" thread - any thread can release.
        // We can only show waiters, not ownership edges. This limits deadlock detection
        // to cases where we can infer ownership through other means.
        foreach (var sem in result.SemaphoreSlims?.Where(s => s.IsContended) ?? [])
        {
            var nodeId = $"semaphore_{sem.Address}";
            var node = new WaitGraphNode
            {
                Id = nodeId,
                Type = "resource",
                Label = sem.IsAsyncLock ? $"AsyncLock@{sem.Address}" : $"SemaphoreSlim@{sem.Address}",
                ResourceType = "SemaphoreSlim"
            };
            resourceNodes[nodeId] = node;
            graph.Nodes.Add(node);

            // Add waiter edges
            foreach (var waiter in sem.Waiters ?? [])
            {
                if (waiter.ThreadId.HasValue && threadNodes.TryGetValue(waiter.ThreadId.Value, out var waiterNode))
                {
                    graph.Edges.Add(new WaitGraphEdge
                    {
                        From = waiterNode.Id,
                        To = nodeId,
                        Label = "waits"
                    });
                }
            }
        }

        // Add ReaderWriterLock nodes (only contended ones)
        foreach (var rwLock in result.ReaderWriterLocks?.Where(r => r.IsContended) ?? [])
        {
            var nodeId = $"rwlock_{rwLock.Address}";
            var node = new WaitGraphNode
            {
                Id = nodeId,
                Type = "resource",
                Label = $"RWLock@{rwLock.Address}",
                ResourceType = "ReaderWriterLockSlim"
            };
            resourceNodes[nodeId] = node;
            graph.Nodes.Add(node);

            // Edge: resource -> writer thread (if any)
            if (rwLock.WriterThreadId.HasValue && threadNodes.TryGetValue(rwLock.WriterThreadId.Value, out var writerNode))
            {
                graph.Edges.Add(new WaitGraphEdge
                {
                    From = nodeId,
                    To = writerNode.Id,
                    Label = "owned by"
                });
            }
        }

        // Only add thread nodes that participate in the graph
        var participatingThreads = graph.Edges
            .SelectMany(e => new[] { e.From, e.To })
            .Where(id => id.StartsWith("thread_", StringComparison.Ordinal))
            .Distinct()
            .ToHashSet();

        foreach (var kvp in threadNodes)
        {
            if (participatingThreads.Contains(kvp.Value.Id))
            {
                graph.Nodes.Add(kvp.Value);
            }
        }

        return graph;
    }

    /// <summary>
    /// Detects deadlock cycles in the wait graph using DFS.
    /// </summary>
    private List<DeadlockCycle> DetectDeadlocks(WaitGraph graph)
    {
        var deadlocks = new List<DeadlockCycle>();

        if (graph.Nodes.Count == 0 || graph.Edges.Count == 0)
            return deadlocks;

        // Build adjacency list
        var adjacency = new Dictionary<string, List<string>>();
        foreach (var node in graph.Nodes)
        {
            adjacency[node.Id] = [];
        }

        foreach (var edge in graph.Edges)
        {
            // Ensure both nodes exist in adjacency list
            if (!adjacency.ContainsKey(edge.From))
            {
                adjacency[edge.From] = [];
            }
            if (!adjacency.ContainsKey(edge.To))
            {
                adjacency[edge.To] = [];
            }
            adjacency[edge.From].Add(edge.To);
        }

        // DFS to find cycles
        var visited = new HashSet<string>();
        var recursionStack = new HashSet<string>();
        var path = new List<string>();

        foreach (var node in graph.Nodes.Where(n => n.Type == "thread"))
        {
            if (!visited.Contains(node.Id))
            {
                var cycles = FindCycles(node.Id, adjacency, visited, recursionStack, path);
                foreach (var cycle in cycles)
                {
                    var deadlock = CreateDeadlockCycle(cycle, graph, deadlocks.Count + 1);
                    if (deadlock != null && !IsDuplicateDeadlock(deadlock, deadlocks))
                    {
                        deadlocks.Add(deadlock);
                    }
                }
            }
        }

        return deadlocks;
    }

    /// <summary>
    /// Finds cycles using DFS.
    /// </summary>
    private List<List<string>> FindCycles(
        string nodeId,
        Dictionary<string, List<string>> adjacency,
        HashSet<string> visited,
        HashSet<string> recursionStack,
        List<string> path)
    {
        var cycles = new List<List<string>>();

        visited.Add(nodeId);
        recursionStack.Add(nodeId);
        path.Add(nodeId);

        if (adjacency.TryGetValue(nodeId, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                if (!visited.Contains(neighbor))
                {
                    cycles.AddRange(FindCycles(neighbor, adjacency, visited, recursionStack, path));
                }
                else if (recursionStack.Contains(neighbor))
                {
                    // Found a cycle
                    var cycleStart = path.IndexOf(neighbor);
                    if (cycleStart >= 0)
                    {
                        var cycle = path.Skip(cycleStart).ToList();
                        cycle.Add(neighbor); // Complete the cycle
                        cycles.Add(cycle);
                    }
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        recursionStack.Remove(nodeId);

        return cycles;
    }

    /// <summary>
    /// Creates a DeadlockCycle from a cycle path.
    /// </summary>
    private DeadlockCycle? CreateDeadlockCycle(List<string> cyclePath, WaitGraph graph, int id)
    {
        var threads = cyclePath
            .Where(p => p.StartsWith("thread_", StringComparison.Ordinal))
            .Select(p => 
            {
                var idStr = p.Replace("thread_", "", StringComparison.Ordinal);
                return int.TryParse(idStr, out var threadId) ? threadId : 0;
            })
            .Where(t => t > 0)
            .Distinct()
            .ToList();

        if (threads.Count < 2)
            return null;

        var resources = cyclePath
            .Where(p => !p.StartsWith("thread_", StringComparison.Ordinal))
            .Select(p =>
            {
                var node = graph.Nodes.FirstOrDefault(n => n.Id == p);
                return node?.Label ?? p;
            })
            .Distinct()
            .ToList();

        var cycleStr = string.Join(" → ", cyclePath.Select(p =>
        {
            if (p.StartsWith("thread_", StringComparison.Ordinal))
                return $"Thread {p.Replace("thread_", "", StringComparison.Ordinal)}";
            var node = graph.Nodes.FirstOrDefault(n => n.Id == p);
            return node?.Label ?? p;
        }));

        return new DeadlockCycle
        {
            Id = id,
            Threads = threads,
            Resources = resources,
            Cycle = cycleStr,
            Description = $"Circular wait detected: {cycleStr}"
        };
    }

    /// <summary>
    /// Checks if a deadlock is a duplicate of an existing one.
    /// </summary>
    private static bool IsDuplicateDeadlock(DeadlockCycle newDeadlock, List<DeadlockCycle> existing)
    {
        foreach (var deadlock in existing)
        {
            if (deadlock.Threads.OrderBy(t => t).SequenceEqual(newDeadlock.Threads.OrderBy(t => t)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Identifies contention hotspots.
    /// </summary>
    private List<ContentionHotspot> IdentifyContentionHotspots(SynchronizationAnalysisResult result)
    {
        var hotspots = new List<ContentionHotspot>();

        // Check monitor locks
        foreach (var monitor in result.MonitorLocks?.Where(m => m.IsContended) ?? [])
        {
            var waiters = monitor.WaitingThreads?.Count ?? 0;
            hotspots.Add(new ContentionHotspot
            {
                Resource = monitor.Address,
                ResourceType = $"Monitor ({monitor.ObjectType})",
                WaitersCount = waiters,
                Severity = GetSeverity(waiters),
                Recommendation = "Consider reducing lock scope or using finer-grained locking"
            });
        }

        // Check SemaphoreSlims
        foreach (var sem in result.SemaphoreSlims?.Where(s => s.IsContended) ?? [])
        {
            var waiters = sem.SyncWaiters + sem.AsyncWaiters;
            hotspots.Add(new ContentionHotspot
            {
                Resource = sem.Address,
                ResourceType = sem.IsAsyncLock ? "AsyncLock (SemaphoreSlim)" : "SemaphoreSlim",
                WaitersCount = waiters,
                Severity = GetSeverity(waiters),
                Recommendation = sem.IsAsyncLock
                    ? "AsyncLock contention - consider reducing lock hold time or parallelizing work"
                    : "Consider increasing semaphore count or reducing lock hold time"
            });
        }

        // Check ReaderWriterLocks
        foreach (var rwLock in result.ReaderWriterLocks?.Where(r => r.IsContended) ?? [])
        {
            var waiters = rwLock.ReadWaiters + rwLock.WriteWaiters + rwLock.UpgradeWaiters;
            var description = rwLock.WriteWaiters > 0 ? "Writer starvation possible" :
                              rwLock.UpgradeWaiters > 0 ? "Upgrader starvation possible" : "";
            hotspots.Add(new ContentionHotspot
            {
                Resource = rwLock.Address,
                ResourceType = "ReaderWriterLockSlim",
                WaitersCount = waiters,
                Severity = GetSeverity(waiters),
                Recommendation = string.IsNullOrEmpty(description)
                    ? "Consider using concurrent collections instead"
                    : $"{description} - consider reducing write frequency"
            });
        }

        // Sort by severity and waiter count
        return hotspots
            .OrderByDescending(h => GetSeverityOrder(h.Severity))
            .ThenByDescending(h => h.WaitersCount)
            .ToList();
    }

    /// <summary>
    /// Gets severity level based on waiter count.
    /// </summary>
    private static string GetSeverity(int waiters)
    {
        return waiters switch
        {
            >= 10 => "critical",
            >= 5 => "high",
            >= 2 => "medium",
            _ => "low"
        };
    }

    /// <summary>
    /// Gets sort order for severity.
    /// </summary>
    private static int GetSeverityOrder(string severity)
    {
        return severity switch
        {
            "critical" => 4,
            "high" => 3,
            "medium" => 2,
            "low" => 1,
            _ => 0
        };
    }

    /// <summary>
    /// Builds the summary from analysis results.
    /// </summary>
    private SynchronizationSummary BuildSummary(SynchronizationAnalysisResult result)
    {
        var contendedSemaphores = result.SemaphoreSlims?.Count(s => s.IsContended) ?? 0;
        var asyncLocks = result.SemaphoreSlims?.Count(s => s.IsAsyncLock) ?? 0;

        return new SynchronizationSummary
        {
            TotalMonitorLocks = result.MonitorLocks?.Count ?? 0,
            TotalSemaphoreSlims = result.SemaphoreSlims?.Count ?? 0,
            TotalReaderWriterLocks = result.ReaderWriterLocks?.Count ?? 0,
            TotalResetEvents = result.ResetEvents?.Count ?? 0,
            TotalWaitHandles = result.WaitHandles?.Count ?? 0,
            ContentionDetected = result.ContentionHotspots?.Count > 0,
            PotentialDeadlockCount = result.PotentialDeadlocks?.Count ?? 0,
            ContendedSemaphoreSlims = contendedSemaphores,
            AsyncLockCount = asyncLocks
        };
    }

    #region Helper Methods

    /// <summary>
    /// Reads an int32 field trying multiple field names.
    /// </summary>
    private int ReadFieldInt32(ClrObject obj, string[] fieldNames)
    {
        foreach (var name in fieldNames)
        {
            var field = obj.Type?.GetFieldByName(name);
            if (field != null)
            {
                try
                {
                    return obj.ReadField<int>(name);
                }
                catch
                {
                    // Try next field name
                }
            }
        }
        return 0;
    }

    /// <summary>
    /// Reads a uint32 field trying multiple field names.
    /// </summary>
    private uint ReadFieldUInt32(ClrObject obj, string[] fieldNames)
    {
        foreach (var name in fieldNames)
        {
            var field = obj.Type?.GetFieldByName(name);
            if (field != null)
            {
                try
                {
                    return obj.ReadField<uint>(name);
                }
                catch
                {
                    // Try next field name
                }
            }
        }
        return 0;
    }

    /// <summary>
    /// Reads an object field trying multiple field names.
    /// </summary>
    private ClrObject ReadObjectField(ClrObject obj, string[] fieldNames)
    {
        foreach (var name in fieldNames)
        {
            var field = obj.Type?.GetFieldByName(name);
            if (field != null)
            {
                try
                {
                    return obj.ReadObjectField(name);
                }
                catch
                {
                    // Try next field name
                }
            }
        }
        return default;
    }

    #endregion
}

