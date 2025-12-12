using DebuggerMcp.ObjectInspection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.ObjectInspection;

/// <summary>
/// Tests for <see cref="ObjectInspector"/> using a fake <see cref="IDebuggerManager"/>.
/// </summary>
public class ObjectInspectorTests
{
    [Fact]
    public async Task InspectAsync_String_TruncatesLongString()
    {
        // Arrange
        var manager = new FakeDebuggerManager(command =>
        {
            if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
            {
                return """
Name:        System.String
MethodTable: 00007ff9abcd1234
EEClass:     00007ff9abcd5678
Size:        80(0x50) bytes
String:      aaaaaaaaaaaaaaaaaaaa
Fields:
None
""";
            }

            return string.Empty;
        });

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        // Act
        var inspected = await inspector.InspectAsync(manager, "0x1234", maxStringLength: 5);

        // Assert
        Assert.NotNull(inspected);
        Assert.NotNull(inspected.Fields);
        Assert.Single(inspected.Fields);
        Assert.Equal("_value", inspected.Fields[0].Name);
        var value = Assert.IsType<string>(inspected.Fields[0].Value);
        Assert.Contains("[truncated:", value);
        Assert.StartsWith("aaaaa", value);
    }

    [Fact]
    public async Task InspectAsync_Array_AddsTruncationMarker_WhenArrayLengthExceedsLimit()
    {
        // Arrange
        var manager = new FakeDebuggerManager(command =>
        {
            if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
            {
                return """
Name:        System.Int32[]
MethodTable: 00007ff9abcd1234
EEClass:     00007ff9abcd5678
Size:        48(0x30) bytes
Array:       Rank 1, Number of elements 3, Type System.Int32
Element Methodtable: 00007ff9abcd9999
Fields:
None
""";
            }

            if (command.StartsWith("dumparray", StringComparison.OrdinalIgnoreCase))
            {
                return """
Name:        System.Int32[]
MethodTable: 00007ff9abcd1234
Size:        48(0x30) bytes

Array:       Rank 1, Number of elements 3, Type System.Int32

0000000000000000 1
0000000000000001 2
0000000000000002 3
""";
            }

            return string.Empty;
        });

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        // Act
        var inspected = await inspector.InspectAsync(manager, "0x2000", maxArrayElements: 1);

        // Assert
        Assert.NotNull(inspected);
        Assert.NotNull(inspected.Elements);
        Assert.Equal(2, inspected.Elements.Count);
        Assert.Equal(1, Assert.IsType<int>(inspected.Elements[0]));
        Assert.Contains("more elements", inspected.Elements[1]?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task InspectAsync_CachesResults_ForSameInputs()
    {
        // Arrange
        ObjectInspector.ClearCache();

        var dumpCalls = 0;
        var manager = new FakeDebuggerManager(command =>
        {
            if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
            {
                dumpCalls++;
                return """
Name:        System.String
MethodTable: 00007ff9abcd1234
EEClass:     00007ff9abcd5678
Size:        80(0x50) bytes
String:      hello
Fields:
None
""";
            }

            return string.Empty;
        });

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        // Act
        var a = await inspector.InspectAsync(manager, "0x1234");
        var b = await inspector.InspectAsync(manager, "0x1234");

        // Assert
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(1, dumpCalls);
        Assert.True(ObjectInspector.CacheCount >= 1);
    }

    [Fact]
    public async Task InspectAsync_FieldPointsToRoot_ReturnsThisMarker()
    {
        // Arrange
        var manager = new FakeDebuggerManager(command =>
        {
            if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
            {
                return """
Name:        MyApp.MyType
MethodTable: 00007ff9abcd1234
EEClass:     00007ff9abcd5678
Size:        48(0x30) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9aaaa0000 4000001 00000008        System.Object  0 instance 0000000000001234 _self
""";
            }

            return string.Empty;
        });

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        // Act
        var inspected = await inspector.InspectAsync(manager, "0x1234", maxDepth: 3);

        // Assert
        Assert.NotNull(inspected);
        Assert.NotNull(inspected.Fields);
        Assert.Single(inspected.Fields);
        var valueObj = Assert.IsType<DebuggerMcp.ObjectInspection.Models.InspectedObject>(inspected.Fields[0].Value);
        Assert.Equal("[this]", valueObj.Type);
    }

    [Fact]
    public async Task InspectAsync_MaxDepthStopsRecursion_ReturnsMaxDepthMarker()
    {
        // Arrange
        var manager = new FakeDebuggerManager(command =>
        {
            if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
            {
                return """
Name:        MyApp.MyType
MethodTable: 00007ff9abcd1234
EEClass:     00007ff9abcd5678
Size:        48(0x30) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9aaaa0000 4000001 00000008        System.Object  0 instance 0000000000009999 _child
""";
            }

            return string.Empty;
        });

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        // Act
        // maxDepth=1 means we only inspect the root; any child object should be replaced by [max depth].
        var inspected = await inspector.InspectAsync(manager, "0x1234", maxDepth: 1);

        // Assert
        Assert.NotNull(inspected);
        Assert.NotNull(inspected.Fields);
        Assert.Single(inspected.Fields);
        var valueObj = Assert.IsType<DebuggerMcp.ObjectInspection.Models.InspectedObject>(inspected.Fields[0].Value);
        Assert.Equal("[max depth]", valueObj.Type);
    }

    [Fact]
    public async Task InspectToJsonAsync_NullAddress_ReturnsErrorJson()
    {
        // Arrange
        ObjectInspector.ClearCache();

        var manager = new FakeDebuggerManager(_ => throw new InvalidOperationException("No commands should be executed"));
        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        // Act
        var json = await inspector.InspectToJsonAsync(manager, "0x0");

        // Assert
        Assert.Contains("\"error\"", json);
        Assert.Contains("Failed to inspect object", json);
    }

    [Fact]
    public async Task InspectAsync_TruncatedTypeName_UsesDumpmtAndCachesPerInspection()
    {
        // Arrange
        ObjectInspector.ClearCache();

        var dumpmtCalls = 0;
        var manager = new FakeDebuggerManager(command =>
        {
            if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
            {
                return """
Name:        MyApp.VeryLongTypeName...
MethodTable: 00007ff9abcd1111
EEClass:     00007ff9abcd2222
Size:        48(0x30) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9abcd1111 4000001 00000008 MyApp.VeryLongTypeName...  0 instance 0000000000000000 _child
""";
            }

            if (command.Equals("dumpmt 00007ff9abcd1111", StringComparison.OrdinalIgnoreCase))
            {
                dumpmtCalls++;
                return """
MethodTable: 00007ff9abcd1111
Name:        MyApp.VeryLongTypeName`1[[System.String, System.Private.CoreLib]]
""";
            }

            return string.Empty;
        });

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        // Act
        var inspected = await inspector.InspectAsync(manager, "0x4000");

        // Assert
        Assert.NotNull(inspected);
        Assert.Equal("MyApp.VeryLongTypeName`1[[System.String, System.Private.CoreLib]]", inspected.Type);
        Assert.NotNull(inspected.Fields);
        Assert.Single(inspected.Fields);
        Assert.Equal("MyApp.VeryLongTypeName`1[[System.String, System.Private.CoreLib]]", inspected.Fields[0].Type);
        Assert.Equal(1, dumpmtCalls);
    }

    [Fact]
    public async Task InspectAsync_StringField_UsesDumpobjAndTruncates()
    {
        // Arrange
        ObjectInspector.ClearCache();

        var manager = new FakeDebuggerManager(command =>
        {
            if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase) &&
                command.Contains("5000", StringComparison.OrdinalIgnoreCase) &&
                !command.Contains("4fff", StringComparison.OrdinalIgnoreCase))
            {
                return """
Name:        System.String
MethodTable: 00007ff9abcd1234
EEClass:     00007ff9abcd5678
Size:        80(0x50) bytes
String:      helloworld
Fields:
None
""";
            }

            if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
            {
                return """
Name:        MyApp.HasString
MethodTable: 00007ff9abcd9999
EEClass:     00007ff9abcd7777
Size:        48(0x30) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9abcd1234 4000001 00000008        System.String  0 instance 0000000000005000 _name
""";
            }

            return string.Empty;
        });

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        // Act
        var inspected = await inspector.InspectAsync(manager, "0x4fff", maxStringLength: 5);

        // Assert
        Assert.NotNull(inspected);
        Assert.NotNull(inspected.Fields);
        Assert.Single(inspected.Fields);
        Assert.Equal("_name", inspected.Fields[0].Name);
        var value = Assert.IsType<string>(inspected.Fields[0].Value);
        Assert.StartsWith("hello", value);
        Assert.Contains("[truncated:", value);
    }

    [Fact]
    public async Task InspectAsync_NativePointerField_ReturnsRawValue()
    {
        // Arrange
        ObjectInspector.ClearCache();

        var manager = new FakeDebuggerManager(command =>
        {
            if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
            {
                return """
Name:        MyApp.HasPointer
MethodTable: 00007ff9abcd1000
EEClass:     00007ff9abcd2000
Size:        48(0x30) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9abcd3000 4000001 00000008                 PTR  0 instance 0000000000007777 _ptr
""";
            }

            return string.Empty;
        });

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        // Act
        var inspected = await inspector.InspectAsync(manager, "0x7000");

        // Assert
        Assert.NotNull(inspected);
        Assert.NotNull(inspected.Fields);
        Assert.Single(inspected.Fields);
        Assert.Equal("0000000000007777", inspected.Fields[0].Value);
    }

    [Fact]
    public async Task InspectAsync_EmbeddedValueType_UsesDumpvcWhenDumpobjFails()
    {
        // Arrange
        ObjectInspector.ClearCache();

        var dumpvcCalls = 0;
        var manager = new FakeDebuggerManager(command =>
        {
            if (command.Equals("dumpvc 00007ff9abcd2222 6000", StringComparison.OrdinalIgnoreCase))
            {
                dumpvcCalls++;
                return """
Name:        MyApp.MyStruct
MethodTable: 00007ff9abcd2222
EEClass:     00007ff9abcd3333
Size:        16(0x10) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9abcd4444 4000001 00000000        System.Int32  1 instance 42 _x
""";
            }

            if (command.Equals("dumpobj 0x6000", StringComparison.OrdinalIgnoreCase))
            {
                // Force dumpobj failure so ObjectInspector falls back to dumpvc for value types.
                return string.Empty;
            }

            if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
            {
                return """
Name:        MyApp.HasStruct
MethodTable: 00007ff9abcd9000
EEClass:     00007ff9abcd9001
Size:        48(0x30) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9abcd2222 4000001 00000008      MyApp.MyStruct  1 instance 0000000000006000 _s
""";
            }

            return string.Empty;
        });

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        // Act
        var inspected = await inspector.InspectAsync(manager, "0x8000", maxDepth: 3);

        // Assert
        Assert.NotNull(inspected);
        Assert.NotNull(inspected.Fields);
        Assert.Single(inspected.Fields);

        var inner = Assert.IsType<DebuggerMcp.ObjectInspection.Models.InspectedObject>(inspected.Fields[0].Value);
        Assert.Equal("MyApp.MyStruct", inner.Type);
        Assert.NotNull(inner.Fields);
        Assert.Single(inner.Fields);
        Assert.Equal("_x", inner.Fields[0].Name);
        Assert.Equal(42, Assert.IsType<int>(inner.Fields[0].Value));
        Assert.Equal(1, dumpvcCalls);
    }

    [Fact]
    public async Task InspectAsync_StaticEnumValue_UsesDumpmtMdToResolveName()
    {
        // Arrange
        ObjectInspector.ClearCache();

        var manager = new FakeDebuggerManager(command =>
        {
            if (command.Equals("dumpmt -md 00007ff9abcd5555", StringComparison.OrdinalIgnoreCase))
            {
                return """
Fields:
static literal int32 0 ValueZero
static literal int32 1 ValueOne
""";
            }

            if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
            {
                return """
Name:        MyApp.HasEnum
MethodTable: 00007ff9abcd6666
EEClass:     00007ff9abcd7777
Size:        48(0x30) bytes
Fields:
              MT    Field   Offset                 Type VT     Attr            Value Name
00007ff9abcd5555 4000001 00000000       MyApp.MyEnum  1 static       1 _mode
""";
            }

            return string.Empty;
        });

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        // Act
        var inspected = await inspector.InspectAsync(manager, "0x9000");

        // Assert
        Assert.NotNull(inspected);
        Assert.NotNull(inspected.Fields);
        Assert.Single(inspected.Fields);
        Assert.Equal("_mode", inspected.Fields[0].Name);
        Assert.Equal("ValueOne (1)", Assert.IsType<string>(inspected.Fields[0].Value));
    }

    [Fact]
    public async Task InspectAsync_LargeArray_SkipsElementInspectionAndReturnsSummary()
    {
        // Arrange
        ObjectInspector.ClearCache();

        var manager = new FakeDebuggerManager(command =>
        {
            if (command.StartsWith("dumpobj", StringComparison.OrdinalIgnoreCase))
            {
                return """
Name:        System.Int32[]
MethodTable: 00007ff9abcd1234
EEClass:     00007ff9abcd5678
Size:        48(0x30) bytes
Array:       Rank 1, Number of elements 10001, Type System.Int32
Element Methodtable: 00007ff9abcd9999
Fields:
None
""";
            }

            return string.Empty;
        });

        var inspector = new ObjectInspector(NullLogger<ObjectInspector>.Instance);

        // Act
        var inspected = await inspector.InspectAsync(manager, "0xA000");

        // Assert
        Assert.NotNull(inspected);
        Assert.Null(inspected.Elements);
        Assert.NotNull(inspected.Fields);
        Assert.Single(inspected.Fields);
        Assert.Equal("[large array]", inspected.Fields[0].Name);
    }

    private sealed class FakeDebuggerManager(Func<string, string> handler) : IDebuggerManager
    {
        public bool IsInitialized => true;
        public bool IsDumpOpen => true;
        public string? CurrentDumpPath => null;
        public string DebuggerType => "Fake";
        public bool IsSosLoaded => true;
        public bool IsDotNetDump => true;

        public Task InitializeAsync() => Task.CompletedTask;
        public void OpenDumpFile(string dumpFilePath, string? executablePath = null) => throw new NotSupportedException();
        public void CloseDump() => throw new NotSupportedException();
        public string ExecuteCommand(string command) => handler(command);
        public void LoadSosExtension() => throw new NotSupportedException();
        public void ConfigureSymbolPath(string symbolPath) => throw new NotSupportedException();
        public void AddSymbolPath(string symbolPath) => throw new NotSupportedException();
        public void AddExtensionSearchPath(string path) => throw new NotSupportedException();
        public void LoadExtension(string extensionPath) => throw new NotSupportedException();
        public string GetModules() => throw new NotSupportedException();
        public string GetThreads() => throw new NotSupportedException();
        public string GetStackTrace() => throw new NotSupportedException();
        public string GetException() => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
