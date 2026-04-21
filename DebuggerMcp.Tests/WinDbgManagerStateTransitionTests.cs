using System.Reflection;
using System.Runtime.ExceptionServices;
using DebuggerMcp.ObjectInspection;
using DebuggerMcp.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

#pragma warning disable CA1416 // Platform compatibility - WinDbgManager is Windows-only

namespace DebuggerMcp.Tests;

/// <summary>
/// Verifies WinDbg dump-state transitions that do not require a real DbgEng installation.
/// </summary>
public sealed class WinDbgManagerStateTransitionTests
{
    /// <summary>
    /// Verifies that opening a dump clears cached object-inspection results from the previous dump.
    /// </summary>
    [Fact]
    public void OpenDumpFileCore_WhenOpeningDump_ClearsObjectInspectorCache()
    {
        const string dumpPath = @"C:\dumps\sample.dmp";
        var cacheProbe = AddSyntheticObjectInspectionCacheEntry(dumpPath);

        using var manager = new WinDbgManager();
        var clientMock = new Mock<IDebugClient>(MockBehavior.Strict);
        var controlMock = new Mock<IDebugControl>(MockBehavior.Strict);
        var context = CreateInitializedContext(clientMock.Object, controlMock.Object);

        uint processorType = 0x8664;

        clientMock
            .Setup(client => client.OpenDumpFile(dumpPath))
            .Returns(0);

        controlMock
            .Setup(control => control.WaitForEvent(0, 5000))
            .Returns(0);

        controlMock
            .Setup(control => control.Execute(It.IsAny<uint>(), It.IsAny<string>(), It.IsAny<uint>()))
            .Returns(0);

        controlMock
            .Setup(control => control.GetExecutingProcessorType(out processorType))
            .Returns(0);

        InvokePrivateInstanceMethod(manager, "OpenDumpFileCore", context, dumpPath, null, false);

        Assert.Equal(0, ObjectInspector.GetCacheCount(cacheProbe));
        Assert.True(GetContextProperty<bool>(context, "IsDumpOpen"));
        Assert.Equal(dumpPath, GetContextProperty<string?>(context, "CurrentDumpPath"));

        clientMock.VerifyAll();
        controlMock.VerifyAll();
    }

    /// <summary>
    /// Verifies that closing a dump also clears cached object-inspection results.
    /// </summary>
    [Fact]
    public void CloseDumpCore_WhenClosingDump_ClearsObjectInspectorCache()
    {
        const string dumpPath = @"C:\dumps\sample.dmp";
        var cacheProbe = AddSyntheticObjectInspectionCacheEntry(dumpPath);

        using var manager = new WinDbgManager();
        var clientMock = new Mock<IDebugClient>(MockBehavior.Strict);
        var controlMock = new Mock<IDebugControl>(MockBehavior.Strict);
        var context = CreateInitializedContext(clientMock.Object, controlMock.Object);

        SetContextProperty(context, "IsDumpOpen", true);
        SetContextProperty(context, "CurrentDumpPath", dumpPath);
        SetContextProperty(context, "CurrentExecutablePath", @"C:\apps\sample.exe");
        SetContextProperty(context, "DetectedRuntimeVersion", "10.0.6");

        clientMock
            .Setup(client => client.EndSession(1))
            .Returns(0);

        InvokePrivateInstanceMethod(manager, "CloseDumpCore", context);

        Assert.Equal(0, ObjectInspector.GetCacheCount(cacheProbe));
        Assert.False(GetContextProperty<bool>(context, "IsDumpOpen"));
        Assert.Null(GetContextProperty<string?>(context, "CurrentDumpPath"));
        Assert.Null(GetContextProperty<string?>(context, "CurrentExecutablePath"));
        Assert.Null(GetContextProperty<string?>(context, "DetectedRuntimeVersion"));

        clientMock.VerifyAll();
        controlMock.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Inserts a synthetic entry into the object-inspection cache for one WinDbg dump scope.
    /// </summary>
    /// <param name="dumpPath">Dump path that should own the cached inspection entry.</param>
    /// <returns>A probe manager that can query the same dump-scoped cache.</returns>
    private static FakeDebuggerManager AddSyntheticObjectInspectionCacheEntry(string dumpPath)
    {
        var probeManager = new FakeDebuggerManager
        {
            DebuggerType = "WinDbg",
            CurrentDumpPath = dumpPath,
            CommandHandler = command =>
            {
                if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
                {
                    return """
Name:        Synthetic
MethodTable: 00007ff9abcd1234
EEClass:     00007ff9abcd5678
Size:        48(0x30) bytes
Fields:
None
""";
                }

                return string.Empty;
            }
        };
        ObjectInspector.ClearCache(probeManager);

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);
        var result = inspector.InspectAsync(probeManager, "0x1234").GetAwaiter().GetResult();

        Assert.NotNull(result);
        Assert.True(ObjectInspector.GetCacheCount(probeManager) > 0);
        return probeManager;
    }

    /// <summary>
    /// Creates an initialized reflected WinDbg engine context for private helper testing.
    /// </summary>
    /// <param name="client">Mocked debugger client.</param>
    /// <param name="control">Mocked debugger control interface.</param>
    /// <returns>The reflected private context instance.</returns>
    private static object CreateInitializedContext(IDebugClient client, IDebugControl control)
    {
        var contextType = typeof(WinDbgManager).GetNestedType("WinDbgEngineContext", BindingFlags.NonPublic);
        Assert.NotNull(contextType);

        var context = Activator.CreateInstance(contextType!);
        Assert.NotNull(context);

        SetContextProperty(context!, "Client", client);
        SetContextProperty(context!, "Control", control);

        return context!;
    }

    /// <summary>
    /// Sets a reflected property on the private WinDbg context.
    /// </summary>
    /// <param name="context">Context instance to mutate.</param>
    /// <param name="propertyName">Public property name to assign.</param>
    /// <param name="value">Value to set.</param>
    private static void SetContextProperty(object context, string propertyName, object? value)
    {
        var property = context.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        property!.SetValue(context, value);
    }

    /// <summary>
    /// Reads a reflected property from the private WinDbg context.
    /// </summary>
    /// <typeparam name="T">Expected property type.</typeparam>
    /// <param name="context">Context instance to inspect.</param>
    /// <param name="propertyName">Property name to read.</param>
    /// <returns>The reflected property value.</returns>
    private static T GetContextProperty<T>(object context, string propertyName)
    {
        var property = context.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        return (T)property!.GetValue(context)!;
    }

    /// <summary>
    /// Invokes a private instance method and rethrows the real inner exception on failure.
    /// </summary>
    /// <param name="instance">Instance that owns the private method.</param>
    /// <param name="methodName">Method name to invoke.</param>
    /// <param name="args">Arguments to pass to the method.</param>
    private static void InvokePrivateInstanceMethod(object instance, string methodName, params object?[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        try
        {
            method!.Invoke(instance, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
