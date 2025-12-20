using DebuggerMcp.Analysis;
using DebuggerMcp.ObjectInspection;
using DebuggerMcp.ObjectInspection.Models;
using Xunit;

namespace DebuggerMcp.Tests.ObjectInspection;

/// <summary>
/// Tests for internal pure helpers in <see cref="ObjectInspector"/>.
/// </summary>
public class ObjectInspectorInternalHelpersTests
{
    [Fact]
    public void ParseDumpDelegate_WithValidOutput_ParsesDelegate()
    {
        var output = """
Target           Method           Name
0000000000001111 0000000000002222 MyNamespace.MyType.MyMethod()
""";

        var info = ObjectInspector.ParseDumpDelegate(output);

        Assert.NotNull(info);
        Assert.Equal("1111", info.Target);
        Assert.Equal("2222", info.MethodDesc);
        Assert.Equal("MyNamespace.MyType.MyMethod()", info.MethodName);
    }

    [Fact]
    public void ParsePrintException_ParsesMessageHresultAndInnerException()
    {
        var output = """
Exception object: 000000000000abcd
Exception type:   System.InvalidOperationException
Message:          boom
HResult:          0x80131509
InnerException:   0000000000001234
""";

        var info = ObjectInspector.ParsePrintException(output);

        Assert.NotNull(info);
        Assert.Equal("boom", info.Message);
        Assert.NotNull(info.HResult);
        Assert.Equal(unchecked((int)0x80131509), info.HResult);
        Assert.Equal("1234", info.InnerException);
    }

    [Theory]
    [InlineData(0x200000, "Faulted", true, true, false)]
    [InlineData(0x400000, "Canceled", true, false, true)]
    [InlineData(0x1000000, "RanToCompletion", true, false, false)]
    [InlineData(0x2000000, "WaitingForActivation", false, false, false)]
    public void ParseTaskStateFlags_MapsFlagsToStatus(int flags, string status, bool? completed, bool? faulted, bool? canceled)
    {
        var info = new TaskInfo();

        ObjectInspector.ParseTaskStateFlags(flags, info);

        Assert.Equal(status, info.Status);
        Assert.Equal(completed, info.IsCompleted ?? false);
        Assert.Equal(faulted, info.IsFaulted ?? false);
        Assert.Equal(canceled, info.IsCanceled ?? false);
    }

    [Fact]
    public void ParseDumpMtForTypeInfo_ExtractsNameAssemblyAndSizes()
    {
        var output = """
MethodTable: 00007ff9abcd1111
EEClass:     00007ff9abcd2222
Name:        MyNamespace.MyType`1[[System.String, System.Private.CoreLib]]
File:        /tmp/MyAssembly.dll
BaseSize:    0x30
ComponentSize: 0x0
Number of Methods: 12
Number of IFaces in IFaceMap: 3
Parent MethodTable: 00007ff9abcd9999
""";

        var info = ObjectInspector.ParseDumpMtForTypeInfo(output, methodTable: "0x00007ff9abcd1111");

        Assert.NotNull(info);
        Assert.Equal("7ff9abcd1111", info.MethodTable);
        Assert.Equal("MyNamespace.MyType`1[[System.String, System.Private.CoreLib]]", info.FullName);
        Assert.Equal("MyNamespace", info.Namespace);
        Assert.Equal("MyAssembly", info.Assembly);
        Assert.Equal(0x30, info.BaseSize);
        Assert.Equal(12, info.MethodCount);
        Assert.Equal(3, info.InterfaceCount);
        Assert.True(info.IsGeneric);
        Assert.False(info.IsArray);
    }

    [Fact]
    public void ParseDumpMtForTypeInfo_WhenEnumType_MarksValueTypeAndEnum()
    {
        var output = """
MethodTable: 00007ff9abcd1111
Name:        MyNamespace.MyEnum
Parent MethodTable: 00007ff9abcd9999
System.Enum
""";

        var info = ObjectInspector.ParseDumpMtForTypeInfo(output, methodTable: "0x00007ff9abcd1111");

        Assert.NotNull(info);
        Assert.True(info!.IsValueType);
        Assert.True(info.IsEnum);
    }

    [Fact]
    public void ParseDumpMtForTypeInfo_WhenInterfaceAndMdInterfaceFlag_MarksAsInterface()
    {
        var output = """
MethodTable: 00007ff9abcd1111
Name:        IFoo
Class Attributes: 0x00000020
""";

        var info = ObjectInspector.ParseDumpMtForTypeInfo(output, methodTable: "0x00007ff9abcd1111");

        Assert.NotNull(info);
        Assert.True(info!.IsInterface);
    }

    [Fact]
    public void ConvertClrMdToInspected_ConvertsFieldsAndNestedObjects()
    {
        var clr = new ClrMdObjectInspection
        {
            Address = "0x1000",
            Type = "MyType",
            MethodTable = "0x2000",
            Size = 32,
            Fields =
            [
                new ClrMdFieldInspection { Name = "A", Type = "System.Int32", Value = 1 },
                new ClrMdFieldInspection { Name = "Child", Type = "MyChild", NestedObject = new ClrMdObjectInspection { Address = "0x3000", Type = "MyChild" } }
            ]
        };

        var inspected = ObjectInspector.ConvertClrMdToInspected(clr);

        Assert.Equal("0x1000", inspected.Address);
        Assert.Equal("MyType", inspected.Type);
        Assert.NotNull(inspected.Fields);
        Assert.Equal(2, inspected.Fields.Count);
        Assert.Equal(1, Assert.IsType<int>(inspected.Fields[0].Value));

        var nested = Assert.IsType<InspectedObject>(inspected.Fields[1].Value);
        Assert.Equal("0x3000", nested.Address);
        Assert.Equal("MyChild", nested.Type);
    }

    [Fact]
    public void ConvertClrMdToInspected_WhenElementsContainNestedObject_ConvertsToInspectedObject()
    {
        var clr = new ClrMdObjectInspection
        {
            Address = "0x1000",
            Type = "MyArray",
            Elements =
            [
                1,
                new ClrMdObjectInspection { Address = "0x2000", Type = "MyChild" }
            ]
        };

        var inspected = ObjectInspector.ConvertClrMdToInspected(clr);

        Assert.NotNull(inspected.Elements);
        Assert.Equal(2, inspected.Elements!.Count);
        Assert.Equal(1, Assert.IsType<int>(inspected.Elements[0]));

        var nested = Assert.IsType<InspectedObject>(inspected.Elements[1]);
        Assert.Equal("0x2000", nested.Address);
        Assert.Equal("MyChild", nested.Type);
    }
}
