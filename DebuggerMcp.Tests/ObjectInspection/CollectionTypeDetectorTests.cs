using DebuggerMcp;
using DebuggerMcp.ObjectInspection;
using Xunit;

namespace DebuggerMcp.Tests.ObjectInspection;

/// <summary>
/// Tests for CollectionTypeDetector type detection and helper methods.
/// </summary>
public class CollectionTypeDetectorTests
{
    // ============================================================
    // Detect() Tests - Tier 1: Simple Collections
    // ============================================================

    [Theory]
    [InlineData("System.Collections.Generic.List`1[[System.String]]", CollectionType.List)]
    [InlineData("System.Collections.Generic.List`1[[System.Int32, System.Private.CoreLib]]", CollectionType.List)]
    [InlineData("System.Collections.Generic.List`1[[MyApp.MyClass, MyApp]]", CollectionType.List)]
    public void Detect_List_ReturnsListType(string typeName, CollectionType expected)
    {
        Assert.Equal(expected, CollectionTypeDetector.Detect(typeName));
    }

    [Theory]
    [InlineData("System.Collections.Generic.Stack`1[[System.String]]", CollectionType.Stack)]
    [InlineData("System.Collections.Generic.Queue`1[[System.Int32]]", CollectionType.Queue)]
    [InlineData("System.Collections.Generic.HashSet`1[[System.String]]", CollectionType.HashSet)]
    public void Detect_Tier1Collections_ReturnsCorrectType(string typeName, CollectionType expected)
    {
        Assert.Equal(expected, CollectionTypeDetector.Detect(typeName));
    }

    // ============================================================
    // Detect() Tests - Tier 2: Key-Value Collections
    // ============================================================

    [Theory]
    [InlineData("System.Collections.Generic.Dictionary`2[[System.String],[System.Int32]]", CollectionType.Dictionary)]
    [InlineData("System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[MyApp.User, MyApp]]", CollectionType.Dictionary)]
    public void Detect_Dictionary_ReturnsDictionaryType(string typeName, CollectionType expected)
    {
        Assert.Equal(expected, CollectionTypeDetector.Detect(typeName));
    }

    [Theory]
    [InlineData("System.Collections.Generic.SortedDictionary`2[[System.String],[System.Int32]]", CollectionType.SortedDictionary)]
    [InlineData("System.Collections.Generic.SortedList`2[[System.String],[System.Int32]]", CollectionType.SortedList)]
    public void Detect_SortedCollections_ReturnsCorrectType(string typeName, CollectionType expected)
    {
        Assert.Equal(expected, CollectionTypeDetector.Detect(typeName));
    }

    // ============================================================
    // Detect() Tests - Tier 3: Concurrent Collections
    // ============================================================

    [Theory]
    [InlineData("System.Collections.Concurrent.ConcurrentDictionary`2[[System.String],[System.Int32]]", CollectionType.ConcurrentDictionary)]
    [InlineData("System.Collections.Concurrent.ConcurrentQueue`1[[System.String]]", CollectionType.ConcurrentQueue)]
    [InlineData("System.Collections.Concurrent.ConcurrentStack`1[[System.String]]", CollectionType.ConcurrentStack)]
    [InlineData("System.Collections.Concurrent.ConcurrentBag`1[[System.String]]", CollectionType.ConcurrentBag)]
    public void Detect_ConcurrentCollections_ReturnsCorrectType(string typeName, CollectionType expected)
    {
        Assert.Equal(expected, CollectionTypeDetector.Detect(typeName));
    }

    // ============================================================
    // Detect() Tests - Tier 4: Immutable Collections
    // ============================================================

    [Theory]
    [InlineData("System.Collections.Immutable.ImmutableArray`1[[System.String]]", CollectionType.ImmutableArray)]
    [InlineData("System.Collections.Immutable.ImmutableList`1[[System.String]]", CollectionType.ImmutableList)]
    [InlineData("System.Collections.Immutable.ImmutableDictionary`2[[System.String],[System.Int32]]", CollectionType.ImmutableDictionary)]
    [InlineData("System.Collections.Immutable.ImmutableHashSet`1[[System.String]]", CollectionType.ImmutableHashSet)]
    public void Detect_ImmutableCollections_ReturnsCorrectType(string typeName, CollectionType expected)
    {
        Assert.Equal(expected, CollectionTypeDetector.Detect(typeName));
    }

    // ============================================================
    // Detect() Tests - Arrays
    // ============================================================

    [Theory]
    [InlineData("System.String[]", CollectionType.Array)]
    [InlineData("System.Int32[]", CollectionType.Array)]
    [InlineData("MyApp.MyClass[]", CollectionType.Array)]
    [InlineData("System.Int32[,]", CollectionType.Array)]
    [InlineData("System.Int32[,,]", CollectionType.Array)]
    public void Detect_Arrays_ReturnsArrayType(string typeName, CollectionType expected)
    {
        Assert.Equal(expected, CollectionTypeDetector.Detect(typeName));
    }

    // ============================================================
    // Detect() Tests - Non-Collections
    // ============================================================

    [Theory]
    [InlineData("System.String", CollectionType.None)]
    [InlineData("System.Int32", CollectionType.None)]
    [InlineData("MyApp.MyClass", CollectionType.None)]
    [InlineData("System.Object", CollectionType.None)]
    [InlineData("", CollectionType.None)]
    [InlineData(null, CollectionType.None)]
    public void Detect_NonCollections_ReturnsNone(string? typeName, CollectionType expected)
    {
        Assert.Equal(expected, CollectionTypeDetector.Detect(typeName!));
    }

    // ============================================================
    // IsKeyValueCollection() Tests
    // ============================================================

    [Theory]
    [InlineData(CollectionType.Dictionary, true)]
    [InlineData(CollectionType.SortedDictionary, true)]
    [InlineData(CollectionType.SortedList, true)]
    [InlineData(CollectionType.ConcurrentDictionary, true)]
    [InlineData(CollectionType.ImmutableDictionary, true)]
    public void IsKeyValueCollection_KeyValueTypes_ReturnsTrue(CollectionType type, bool expected)
    {
        Assert.Equal(expected, CollectionTypeDetector.IsKeyValueCollection(type));
    }

    [Theory]
    [InlineData(CollectionType.List, false)]
    [InlineData(CollectionType.Stack, false)]
    [InlineData(CollectionType.Queue, false)]
    [InlineData(CollectionType.HashSet, false)]
    [InlineData(CollectionType.ConcurrentQueue, false)]
    [InlineData(CollectionType.ImmutableArray, false)]
    [InlineData(CollectionType.None, false)]
    public void IsKeyValueCollection_NonKeyValueTypes_ReturnsFalse(CollectionType type, bool expected)
    {
        Assert.Equal(expected, CollectionTypeDetector.IsKeyValueCollection(type));
    }

    // ============================================================
    // ExtractElementType() Tests
    // ============================================================

    [Theory]
    [InlineData("System.Collections.Generic.List`1[[System.String]]", "System.String")]
    [InlineData("System.Collections.Generic.List`1[[System.Int32]]", "System.Int32")]
    [InlineData("System.Collections.Generic.List`1[[MyApp.MyClass, MyApp]]", "MyApp.MyClass")]
    [InlineData("System.Collections.Generic.HashSet`1[[System.String, System.Private.CoreLib]]", "System.String")]
    public void ExtractElementType_ValidCollections_ReturnsElementType(string typeName, string expected)
    {
        Assert.Equal(expected, CollectionTypeDetector.ExtractElementType(typeName));
    }

    [Theory]
    [InlineData("System.String")]
    [InlineData("System.Collections.Generic.List")]
    [InlineData("")]
    public void ExtractElementType_InvalidCollections_ReturnsNull(string typeName)
    {
        Assert.Null(CollectionTypeDetector.ExtractElementType(typeName));
    }

    // ============================================================
    // ExtractKeyValueTypes() Tests
    // ============================================================

    [Fact]
    public void ExtractKeyValueTypes_ValidDictionary_ReturnsKeyValuePair()
    {
        var typeName = "System.Collections.Generic.Dictionary`2[[System.String],[System.Int32]]";
        var result = CollectionTypeDetector.ExtractKeyValueTypes(typeName);

        Assert.NotNull(result);
        Assert.Equal("System.String", result.Value.KeyType);
        Assert.Equal("System.Int32", result.Value.ValueType);
    }

    [Fact]
    public void ExtractKeyValueTypes_DictionaryWithAssemblyNames_ReturnsKeyValuePair()
    {
        var typeName = "System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[MyApp.User, MyApp]]";
        var result = CollectionTypeDetector.ExtractKeyValueTypes(typeName);

        Assert.NotNull(result);
        Assert.Equal("System.String", result.Value.KeyType);
        Assert.Equal("MyApp.User", result.Value.ValueType);
    }

    [Theory]
    [InlineData("System.Collections.Generic.List`1[[System.String]]")]
    [InlineData("System.String")]
    [InlineData("")]
    public void ExtractKeyValueTypes_NonDictionary_ReturnsNull(string typeName)
    {
        Assert.Null(CollectionTypeDetector.ExtractKeyValueTypes(typeName));
    }

    // ============================================================
    // IsInlineableType() Tests
    // ============================================================

    [Theory]
    [InlineData("System.Boolean", true)]
    [InlineData("System.Byte", true)]
    [InlineData("System.SByte", true)]
    [InlineData("System.Int16", true)]
    [InlineData("System.UInt16", true)]
    [InlineData("System.Int32", true)]
    [InlineData("System.UInt32", true)]
    [InlineData("System.Int64", true)]
    [InlineData("System.UInt64", true)]
    [InlineData("System.Single", true)]
    [InlineData("System.Double", true)]
    [InlineData("System.Decimal", true)]
    [InlineData("System.Char", true)]
    [InlineData("System.String", true)]
    [InlineData("System.IntPtr", true)]
    [InlineData("System.UIntPtr", true)]
    public void IsInlineableType_PrimitiveTypes_ReturnsTrue(string typeName, bool expected)
    {
        Assert.Equal(expected, CollectionTypeDetector.IsInlineableType(typeName));
    }

    [Theory]
    [InlineData("bool", true)]
    [InlineData("int", true)]
    [InlineData("long", true)]
    [InlineData("float", true)]
    [InlineData("double", true)]
    [InlineData("string", true)]
    [InlineData("nint", true)]
    [InlineData("nuint", true)]
    public void IsInlineableType_CSharpAliases_ReturnsTrue(string typeName, bool expected)
    {
        Assert.Equal(expected, CollectionTypeDetector.IsInlineableType(typeName));
    }

    [Theory]
    [InlineData("MyApp.MyClass", false)]
    [InlineData("System.Object", false)]
    [InlineData("System.DateTime", false)]
    [InlineData("System.Guid", false)]
    [InlineData("System.Collections.Generic.List`1[[System.String]]", false)]
    public void IsInlineableType_ComplexTypes_ReturnsFalse(string typeName, bool expected)
    {
        Assert.Equal(expected, CollectionTypeDetector.IsInlineableType(typeName));
    }

    // ============================================================
    // CalculateArrayElementAddress() Tests
    // ============================================================

    [Theory]
    [InlineData("0x1000", 0, 8, 8, "0x1010")] // x64: 0x1000 + 16 header + 0*8
    [InlineData("0x1000", 1, 8, 8, "0x1018")] // x64: 0x1000 + 16 header + 1*8
    [InlineData("0x1000", 2, 8, 8, "0x1020")] // x64: 0x1000 + 16 header + 2*8
    [InlineData("0x1000", 0, 4, 4, "0x1008")] // x86: 0x1000 + 8 header + 0*4
    [InlineData("0x1000", 1, 4, 4, "0x100c")] // x86: 0x1000 + 8 header + 1*4
    public void CalculateArrayElementAddress_VariousInputs_ReturnsCorrectAddress(
        string baseAddr, int index, int elementSize, int pointerSize, string expected)
    {
        var result = CollectionTypeDetector.CalculateArrayElementAddress(baseAddr, index, elementSize, pointerSize);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void CalculateArrayElementAddress_WithoutHexPrefix_ReturnsCorrectAddress()
    {
        var result = CollectionTypeDetector.CalculateArrayElementAddress("1000", 0, 8, 8);
        Assert.Equal("0x1010", result);
    }

    [Fact]
    public void GetArrayElementMethodTable_WhenDumpObjAndDumpMtContainElementMethodTable_ReturnsElementMethodTable()
    {
        var manager = new FakeDebuggerManager(
            outputs: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dumpobj 0x1000"] = "MethodTable: 0xDEADBEEF\n",
                ["dumpmt 0xDEADBEEF"] = "Element Methodtable: 0xFEEDFACE\n",
            });

        var elementMt = CollectionTypeDetector.GetArrayElementMethodTable(manager, "0x1000");

        Assert.Equal("0xFEEDFACE", elementMt);
    }

    [Fact]
    public void GetArrayElementMethodTable_WhenDumpObjHasNoMethodTable_ReturnsNull()
    {
        var manager = new FakeDebuggerManager(
            outputs: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dumpobj 0x1000"] = "Name: System.Int32[]\n",
            });

        Assert.Null(CollectionTypeDetector.GetArrayElementMethodTable(manager, "0x1000"));
    }

    [Fact]
    public void GetPointerSize_WhenPointerSizePropertyExists_ReturnsThatValue()
    {
        var manager = new FakeDebuggerManager(outputs: new Dictionary<string, string>())
        {
            PointerSize = 4
        };

        Assert.Equal(4, CollectionTypeDetector.GetPointerSize(manager));
    }

    [Theory]
    [InlineData("0x00000000", 4)]
    [InlineData("0x0000000000000000", 8)]
    public void GetPointerSize_WhenPointerSizePropertyMissing_UsesEeVersionAddressLength(string addr, int expected)
    {
        var manager = new NoPointerSizeDebuggerManager(
            outputs: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["!eeversion"] = $"JIT: {addr}\n"
            });

        Assert.Equal(expected, CollectionTypeDetector.GetPointerSize(manager));
    }

    [Fact]
    public void GetPointerSize_WhenExecuteCommandThrows_ReturnsDefault8()
    {
        var manager = new ThrowingDebuggerManager();

        Assert.Equal(8, CollectionTypeDetector.GetPointerSize(manager));
    }

    [Fact]
    public void GetTypeSize_WhenBaseSizeIsPresent_ReturnsBaseSize()
    {
        var manager = new FakeDebuggerManager(
            outputs: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dumpmt 0x1234"] = "BaseSize: 0x30\nComponentSize: 0x0\n"
            });

        Assert.Equal(0x30, CollectionTypeDetector.GetTypeSize(manager, "0x1234"));
    }

    [Fact]
    public void GetTypeSize_WhenOnlyComponentSizeIsPresent_ReturnsComponentSize()
    {
        var manager = new FakeDebuggerManager(
            outputs: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dumpmt 0x1234"] = "ComponentSize: 0x10\n"
            });

        Assert.Equal(0x10, CollectionTypeDetector.GetTypeSize(manager, "0x1234"));
    }

    [Fact]
    public void GetTypeSize_WhenNoSizeMarkersFound_ReturnsMinusOne()
    {
        var manager = new FakeDebuggerManager(
            outputs: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dumpmt 0x1234"] = "Name: System.Object\n"
            });

        Assert.Equal(-1, CollectionTypeDetector.GetTypeSize(manager, "0x1234"));
    }

    private class FakeDebuggerManager(Dictionary<string, string> outputs) : IDebuggerManager
    {
        private readonly Dictionary<string, string> _outputs = outputs;

        public int PointerSize { get; init; } = 8;

        public bool IsInitialized => true;
        public bool IsDumpOpen => true;
        public string? CurrentDumpPath => null;
        public string DebuggerType => "LLDB";
        public bool IsSosLoaded => true;
        public bool IsDotNetDump => true;

        public Task InitializeAsync() => Task.CompletedTask;
        public void OpenDumpFile(string dumpFilePath, string? executablePath = null) { }
        public void CloseDump() { }
        public string ExecuteCommand(string command) => _outputs.TryGetValue(command, out var output) ? output : string.Empty;
        public void LoadSosExtension() { }
        public void ConfigureSymbolPath(string symbolPath) { }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoPointerSizeDebuggerManager(Dictionary<string, string> outputs) : IDebuggerManager
    {
        private readonly Dictionary<string, string> _outputs = outputs;

        public bool IsInitialized => true;
        public bool IsDumpOpen => true;
        public string? CurrentDumpPath => null;
        public string DebuggerType => "LLDB";
        public bool IsSosLoaded => true;
        public bool IsDotNetDump => true;

        public Task InitializeAsync() => Task.CompletedTask;
        public void OpenDumpFile(string dumpFilePath, string? executablePath = null) { }
        public void CloseDump() { }
        public string ExecuteCommand(string command) => _outputs.TryGetValue(command, out var output) ? output : string.Empty;
        public void LoadSosExtension() { }
        public void ConfigureSymbolPath(string symbolPath) { }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingDebuggerManager : IDebuggerManager
    {
        public bool IsInitialized => true;
        public bool IsDumpOpen => true;
        public string? CurrentDumpPath => null;
        public string DebuggerType => "LLDB";
        public bool IsSosLoaded => true;
        public bool IsDotNetDump => true;

        public Task InitializeAsync() => Task.CompletedTask;
        public void OpenDumpFile(string dumpFilePath, string? executablePath = null) { }
        public void CloseDump() { }
        public string ExecuteCommand(string command) => throw new InvalidOperationException("boom");
        public void LoadSosExtension() { }
        public void ConfigureSymbolPath(string symbolPath) { }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
