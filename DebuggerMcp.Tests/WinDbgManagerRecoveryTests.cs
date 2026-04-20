using System.Reflection;
using System.Runtime.InteropServices;

#pragma warning disable CA1416 // Platform compatibility - WinDbgManager is Windows-only

namespace DebuggerMcp.Tests;

/// <summary>
/// Covers the WinDbg timeout/recovery helpers that do not require a real DbgEng installation.
/// </summary>
public class WinDbgManagerRecoveryTests
{
    /// <summary>
    /// Verifies that the dedicated WinDbg dispatcher runs work on an STA thread.
    /// </summary>
    [Fact]
    public async Task StaDispatcher_InvokeAsync_RunsWorkOnStaThread()
    {
        using var dispatcher = CreateDispatcher("RecoveryTests-STA");
        var invokeAsync = GetInvokeAsyncMethod(dispatcher.GetType(), typeof(ApartmentState));

        var task = (Task<ApartmentState>)invokeAsync.Invoke(
            dispatcher,
            [new Func<ApartmentState>(() => Thread.CurrentThread.GetApartmentState())])!;

        var apartmentState = await task;

        Assert.Equal(ApartmentState.STA, apartmentState);
    }

    /// <summary>
    /// Verifies that the dispatcher preserves exceptions from queued work.
    /// </summary>
    [Fact]
    public async Task StaDispatcher_InvokeAsync_PropagatesQueuedExceptions()
    {
        using var dispatcher = CreateDispatcher("RecoveryTests-Exceptions");
        var invokeAsync = GetInvokeAsyncMethod(dispatcher.GetType(), typeof(int));

        var task = (Task<int>)invokeAsync.Invoke(
            dispatcher,
            [new Func<int>(() => throw new COMException("boom"))])!;

        await Assert.ThrowsAsync<COMException>(async () => await task);
    }

    /// <summary>
    /// Verifies that the recovery classifier treats COM-style failures as recoverable.
    /// </summary>
    [Fact]
    public void ShouldAttemptRecovery_ReturnsTrueForRecoverableFailures()
    {
        var method = typeof(WinDbgManager).GetMethod("ShouldAttemptRecovery", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, [new COMException("dbgeng failed")])!);
        Assert.True((bool)method.Invoke(null, [new InvalidOperationException("HRESULT: 0x80004005")])!);
    }

    /// <summary>
    /// Verifies that obvious argument errors are not treated as engine corruption.
    /// </summary>
    [Fact]
    public void ShouldAttemptRecovery_ReturnsFalseForNonRecoverableFailures()
    {
        var method = typeof(WinDbgManager).GetMethod("ShouldAttemptRecovery", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.False((bool)method!.Invoke(null, [new ArgumentException("bad argument")])!);
    }

    /// <summary>
    /// Creates a WinDbg STA dispatcher instance through reflection.
    /// </summary>
    /// <param name="name">Thread name for the dispatcher.</param>
    /// <returns>The disposable dispatcher instance.</returns>
    private static IDisposable CreateDispatcher(string name)
    {
        var dispatcherType = typeof(WinDbgManager).GetNestedType("WinDbgStaDispatcher", BindingFlags.NonPublic);
        Assert.NotNull(dispatcherType);

        return (IDisposable)Activator.CreateInstance(dispatcherType!, [name])!;
    }

    /// <summary>
    /// Resolves the generic <c>InvokeAsync</c> method for a specific result type.
    /// </summary>
    /// <param name="dispatcherType">The reflected dispatcher type.</param>
    /// <param name="resultType">The desired result type for the generic method.</param>
    /// <returns>The closed generic method info.</returns>
    private static MethodInfo GetInvokeAsyncMethod(Type dispatcherType, Type resultType)
    {
        var method = dispatcherType.GetMethod("InvokeAsync", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        return method!.MakeGenericMethod(resultType);
    }
}
