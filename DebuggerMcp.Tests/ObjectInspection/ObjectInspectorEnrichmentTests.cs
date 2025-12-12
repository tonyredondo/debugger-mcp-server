using DebuggerMcp.ObjectInspection;
using DebuggerMcp.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.ObjectInspection;

/// <summary>
/// Tests for enrichment behaviors in <see cref="ObjectInspector"/> (delegate/exception/task/type).
/// </summary>
public class ObjectInspectorEnrichmentTests
{
    [Fact]
    public async Task InspectAsync_Delegate_AddsDelegateInfoAndEnrichment()
    {
        ObjectInspector.ClearCache();

        var manager = new FakeDebuggerManager
        {
            CommandHandler = command =>
            {
                if (command.Equals("dumpobj 0xabcd", StringComparison.OrdinalIgnoreCase))
                {
                    return """
Name:        System.Action
MethodTable: 00007ff9abcd0001
EEClass:     00007ff9abcd0002
Size:        48(0x30) bytes
Fields:
None
""";
                }

                if (command.Equals("dumpdelegate abcd", StringComparison.OrdinalIgnoreCase))
                {
                    return """
Target           Method           Name
0000000000001111 0000000000002222 MyNamespace.MyType.MyMethod()
""";
                }

                if (command.Equals("dumpmd 2222", StringComparison.OrdinalIgnoreCase))
                {
                    return """
Method Name: MyNamespace.MyType.MyMethod
Class: 00007ff9abcd3333
IsJitted: yes
Current CodeAddr: 0000000000004444
""";
                }

                if (command.Equals("dumpmt 00007ff9abcd3333", StringComparison.OrdinalIgnoreCase))
                {
                    return """
MethodTable: 00007ff9abcd3333
Name:        MyNamespace.MyType
""";
                }

                return string.Empty;
            }
        };

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        var inspected = await inspector.InspectAsync(manager, "0xABCD");

        Assert.NotNull(inspected);
        Assert.NotNull(inspected.Delegate);
        Assert.Equal("1111", inspected.Delegate.Target);
        Assert.Equal("2222", inspected.Delegate.MethodDesc);
        Assert.Equal("MyNamespace.MyType.MyMethod()", inspected.Delegate.MethodName);
        Assert.Equal("MyNamespace.MyType", inspected.Delegate.ClassName);
        Assert.True(inspected.Delegate.IsJitted);
        Assert.Equal("4444", inspected.Delegate.CodeAddress);

        Assert.Contains(manager.ExecutedCommands, c => c.Equals("dumpdelegate abcd", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(manager.ExecutedCommands, c => c.Equals("dumpmd 2222", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(manager.ExecutedCommands, c => c.Equals("dumpmt 00007ff9abcd3333", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InspectAsync_Exception_AddsExceptionInfo()
    {
        ObjectInspector.ClearCache();

        var manager = new FakeDebuggerManager
        {
            CommandHandler = command =>
            {
                if (command.Equals("dumpobj 0xeeee", StringComparison.OrdinalIgnoreCase))
                {
                    return """
Name:        System.InvalidOperationException
MethodTable: 00007ff9abcd0001
EEClass:     00007ff9abcd0002
Size:        48(0x30) bytes
Fields:
None
""";
                }

                if (command.Equals("pe eeee", StringComparison.OrdinalIgnoreCase))
                {
                    return """
Exception object: 000000000000eeee
Exception type:   System.InvalidOperationException
Message:          boom
HResult:          0x80131509
InnerException:   0000000000001234

StackTrace (generated):
   at MyNamespace.MyType.MyMethod()

""";
                }

                return string.Empty;
            }
        };

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        var inspected = await inspector.InspectAsync(manager, "0xEEEE");

        Assert.NotNull(inspected);
        Assert.NotNull(inspected.Exception);
        Assert.Equal("boom", inspected.Exception.Message);
        Assert.Equal(unchecked((int)0x80131509), inspected.Exception.HResult);
        Assert.Equal("1234", inspected.Exception.InnerException);
        Assert.Contains("MyNamespace.MyType.MyMethod", inspected.Exception.StackTrace ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectAsync_SystemRuntimeType_SetsTypeInfoAndSkipsFields()
    {
        ObjectInspector.ClearCache();

        var manager = new FakeDebuggerManager
        {
            CommandHandler = command =>
            {
                if (command.Equals("dumpobj 0x9999", StringComparison.OrdinalIgnoreCase))
                {
                    return """
Name:        System.RuntimeType
MethodTable: 00007ff9abcd0001
EEClass:     00007ff9abcd0002
Size:        48(0x30) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9abcd2222 4000001 00000008 System.RuntimeTypeHandle  1 instance 0000000000006000 m_handle
""";
                }

                if (command.Equals("dumpvc 00007ff9abcd2222 0000000000006000", StringComparison.OrdinalIgnoreCase))
                {
                    return """
m_type 00007ff9abcd9999
""";
                }

                if (command.Equals("dumpmt 00007ff9abcd9999", StringComparison.OrdinalIgnoreCase))
                {
                    return """
MethodTable: 00007ff9abcd9999
EEClass:     00007ff9abcd8888
Name:        MyNamespace.MyType
File:        /tmp/MyAssembly.dll
BaseSize:    0x30
ComponentSize: 0x0
Number of Methods: 12
Number of IFaces in IFaceMap: 3
""";
                }

                return string.Empty;
            }
        };

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        var inspected = await inspector.InspectAsync(manager, "0x9999");

        Assert.NotNull(inspected);
        Assert.Equal("System.RuntimeType", inspected.Type);
        Assert.Null(inspected.Fields);
        Assert.NotNull(inspected.TypeInfo);
        Assert.Equal("MyNamespace.MyType", inspected.TypeInfo.FullName);
        Assert.Equal("MyNamespace", inspected.TypeInfo.Namespace);
        Assert.Equal("MyAssembly", inspected.TypeInfo.Assembly);
        Assert.Equal("7ff9abcd9999", inspected.TypeInfo.MethodTable);
    }

    [Fact]
    public async Task InspectAsync_Task_AddsTaskInfoFromFields()
    {
        ObjectInspector.ClearCache();

        var manager = new FakeDebuggerManager
        {
            CommandHandler = command =>
            {
                if (command.Equals("dumpobj 0x7777", StringComparison.OrdinalIgnoreCase))
                {
                    return """
Name:        System.Threading.Tasks.Task
MethodTable: 00007ff9abcd0001
EEClass:     00007ff9abcd0002
Size:        48(0x30) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9abcd1111 4000001 00000008        System.Int32  1 instance 2097152 m_stateFlags
00007ff9abcd1111 4000002 0000000c        System.Int32  1 instance 42 m_taskId
00007ff9abcd2222 4000003 00000010      System.Object  0 instance 0000000000000000 m_contingentProperties
""";
                }

                if (command.Equals("dumpasync -addr 7777", StringComparison.OrdinalIgnoreCase))
                {
                    return "No async state machine found";
                }

                return string.Empty;
            }
        };

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        var inspected = await inspector.InspectAsync(manager, "0x7777");

        Assert.NotNull(inspected);
        Assert.NotNull(inspected.Task);
        Assert.Equal("Faulted", inspected.Task.Status);
        Assert.True(inspected.Task.IsCompleted);
        Assert.True(inspected.Task.IsFaulted);
        Assert.Equal(42, inspected.Task.Id);
    }
}
