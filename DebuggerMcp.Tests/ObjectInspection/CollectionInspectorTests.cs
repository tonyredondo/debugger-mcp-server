using DebuggerMcp.ObjectInspection;
using DebuggerMcp.ObjectInspection.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp.Tests.ObjectInspection;

public class CollectionInspectorTests
{
    [Fact]
    public async Task ExtractListElementsAsync_WhenItemsIsNull_ReturnsEmptyElements()
    {
        var inspector = CreateInspector();
        var fields = new List<DumpFieldInfo>
        {
            new() { Name = "_items", Value = "0000000000000000" },
            new() { Name = "_size", Value = "3" }
        };

        var result = await inspector.ExtractListElementsAsync(
            new FakeDebuggerManager(),
            collectionTypeName: "System.Collections.Generic.List<System.String>",
            fields: fields,
            maxElements: 10,
            depth: 0,
            maxDepth: 3,
            maxStringLength: 50,
            seenAddresses: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            rootAddress: "0x1",
            cancellationToken: CancellationToken.None);

        Assert.Null(result.Error);
        Assert.NotNull(result.Elements);
        Assert.Empty(result.Elements!);
        Assert.Equal(3, result.Count);
        Assert.Equal(0, result.ElementsReturned);
    }

    [Fact]
    public async Task ExtractListElementsAsync_ParsesElementsAndTruncates()
    {
        var inspector = CreateInspector();

        var manager = new FakeDebuggerManager
        {
            DumparrayOutput = string.Join("\n", new[]
            {
                "Name: System.String[]",
                "Number of elements 3",
                "[0] 0x0000000000001000",
                "[1] 0x0000000000002000",
                "[2] 0x0000000000003000"
            })
        };

        var fields = new List<DumpFieldInfo>
        {
            new() { Name = "_items", Value = "0x000000000000AAAA" },
            new() { Name = "_size", Value = "3" }
        };

        var result = await inspector.ExtractListElementsAsync(
            manager,
            collectionTypeName: "System.Collections.Generic.List<System.String>",
            fields: fields,
            maxElements: 2,
            depth: 0,
            maxDepth: 3,
            maxStringLength: 50,
            seenAddresses: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            rootAddress: "0x1",
            cancellationToken: CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(3, result.Count);
        Assert.Equal(3, result.Capacity);
        Assert.True(result.Truncated);
        Assert.Equal(2, result.ElementsReturned);
        Assert.Equal(2, result.Elements!.Count);
    }

    [Fact]
    public async Task ExtractQueueElementsAsync_ExtractsInCircularOrder()
    {
        var inspector = CreateInspector();

        var manager = new FakeDebuggerManager
        {
            DumparrayOutput = string.Join("\n", new[]
            {
                "Name: System.Int32[]",
                "Number of elements 4",
                "[0] 1",
                "[1] 2",
                "[2] 3",
                "[3] 4"
            })
        };

        var fields = new List<DumpFieldInfo>
        {
            new() { Name = "_array", Value = "0x000000000000BEEF" },
            new() { Name = "_head", Value = "2" },
            new() { Name = "_size", Value = "3" }
        };

        var result = await inspector.ExtractQueueElementsAsync(
            manager,
            collectionTypeName: "System.Collections.Generic.Queue<System.Int32>",
            fields: fields,
            maxElements: 10,
            depth: 0,
            maxDepth: 3,
            maxStringLength: 50,
            seenAddresses: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            rootAddress: "0x1",
            cancellationToken: CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(3, result.Count);
        Assert.Equal(4, result.Capacity);
        Assert.Equal(3, result.ElementsReturned);
        // Inlineable primitives may be returned as their string representation.
        Assert.Equal(new object?[] { "3", "4", "1" }, result.Elements);
    }

    [Fact]
    public async Task ExtractStackElementsAsync_WhenFieldsMissing_ReturnsError()
    {
        var inspector = CreateInspector();

        var result = await inspector.ExtractStackElementsAsync(
            new FakeDebuggerManager(),
            collectionTypeName: "System.Collections.Generic.Stack<System.Int32>",
            fields: [],
            maxElements: 10,
            depth: 0,
            maxDepth: 3,
            maxStringLength: 50,
            seenAddresses: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            rootAddress: "0x1",
            cancellationToken: CancellationToken.None);

        Assert.Equal("_array or _size field not found", result.Error);
        Assert.NotNull(result.Elements);
        Assert.Empty(result.Elements!);
    }

    [Fact]
    public async Task ExtractStackElementsAsync_ParsesElementsInLifoOrder_AndTruncates()
    {
        var inspector = CreateInspector();

        var manager = new FakeDebuggerManager
        {
            DumparrayOutput = string.Join("\n", new[]
            {
                "Name: System.Int32[]",
                "Number of elements 3",
                "[0] 1",
                "[1] 2",
                "[2] 3"
            })
        };

        var fields = new List<DumpFieldInfo>
        {
            new() { Name = "_array", Value = "0x000000000000BEEF" },
            new() { Name = "_size", Value = "3" }
        };

        var result = await inspector.ExtractStackElementsAsync(
            manager,
            collectionTypeName: "System.Collections.Generic.Stack<System.Int32>",
            fields: fields,
            maxElements: 2,
            depth: 0,
            maxDepth: 3,
            maxStringLength: 50,
            seenAddresses: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            rootAddress: "0x1",
            cancellationToken: CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(3, result.Count);
        Assert.Equal(3, result.Capacity);
        Assert.True(result.Truncated);
        Assert.Equal(2, result.ElementsReturned);
        Assert.Equal(new object?[] { "3", "2" }, result.Elements);
    }

    [Theory]
    [InlineData("System.Collections.Generic.HashSet<System.Int32>", "_entries", "_entries or _count field not found")]
    [InlineData("System.Collections.Generic.Dictionary<System.String,System.Int32>", "_entries", "_entries or _count field not found")]
    public async Task ExtractHashSetOrDictionary_WhenFieldsMissing_ReturnsError(
        string collectionTypeName,
        string requiredField1,
        string expectedError)
    {
        var inspector = CreateInspector();

        var fields = new List<DumpFieldInfo>
        {
            new() { Name = requiredField1, Value = "0x000000000000BEEF" }
        };

        var manager = new FakeDebuggerManager();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (collectionTypeName.Contains("HashSet", StringComparison.Ordinal))
        {
            var result = await inspector.ExtractHashSetElementsAsync(
                manager,
                collectionTypeName: collectionTypeName,
                fields: fields,
                maxElements: 10,
                depth: 0,
                maxDepth: 3,
                maxStringLength: 50,
                seenAddresses: seen,
                rootAddress: "0x1",
                cancellationToken: CancellationToken.None);

            Assert.Equal(expectedError, result.Error);
            Assert.NotNull(result.Elements);
            Assert.Empty(result.Elements!);
            return;
        }

        var dictResult = await inspector.ExtractDictionaryEntriesAsync(
            manager,
            collectionTypeName: collectionTypeName,
            fields: fields,
            maxElements: 10,
            depth: 0,
            maxDepth: 3,
            maxStringLength: 50,
            seenAddresses: seen,
            rootAddress: "0x1",
            cancellationToken: CancellationToken.None);

        Assert.Equal(expectedError, dictResult.Error);
        Assert.NotNull(dictResult.Entries);
        Assert.Empty(dictResult.Entries!);
    }

    [Theory]
    [InlineData("System.Collections.Generic.HashSet<System.Int32>", "not-an-int", "Could not parse _count: not-an-int")]
    [InlineData("System.Collections.Generic.Dictionary<System.String,System.Int32>", "not-an-int", "Could not parse _count: not-an-int")]
    public async Task ExtractHashSetOrDictionary_WhenCountNotInt_ReturnsError(
        string collectionTypeName,
        string countValue,
        string expectedError)
    {
        var inspector = CreateInspector();

        var fields = new List<DumpFieldInfo>
        {
            new() { Name = "_entries", Value = "0x000000000000BEEF" },
            new() { Name = "_count", Value = countValue }
        };

        var manager = new FakeDebuggerManager();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (collectionTypeName.Contains("HashSet", StringComparison.Ordinal))
        {
            var result = await inspector.ExtractHashSetElementsAsync(
                manager,
                collectionTypeName: collectionTypeName,
                fields: fields,
                maxElements: 10,
                depth: 0,
                maxDepth: 3,
                maxStringLength: 50,
                seenAddresses: seen,
                rootAddress: "0x1",
                cancellationToken: CancellationToken.None);

            Assert.Equal(expectedError, result.Error);
            Assert.NotNull(result.Elements);
            Assert.Empty(result.Elements!);
            return;
        }

        var dictResult = await inspector.ExtractDictionaryEntriesAsync(
            manager,
            collectionTypeName: collectionTypeName,
            fields: fields,
            maxElements: 10,
            depth: 0,
            maxDepth: 3,
            maxStringLength: 50,
            seenAddresses: seen,
            rootAddress: "0x1",
            cancellationToken: CancellationToken.None);

        Assert.Equal(expectedError, dictResult.Error);
        Assert.NotNull(dictResult.Entries);
        Assert.Empty(dictResult.Entries!);
    }

    [Fact]
    public void ExtractMethodTableFromDumpArray_WhenElementMethodTablePresent_ReturnsValue()
    {
        var method = typeof(CollectionInspector).GetMethod(
            "ExtractMethodTableFromDumpArray",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var output = "Element Methodtable: 0x00007ffabcdef0123";
        var value = (string?)method!.Invoke(null, new object?[] { output });

        Assert.Equal("0x00007ffabcdef0123", value);
    }

    [Fact]
    public void ExtractMethodTableFromDumpArray_WhenElementTypePresent_ReturnsValue()
    {
        var method = typeof(CollectionInspector).GetMethod(
            "ExtractMethodTableFromDumpArray",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var output = "Element Type: 00007ffabcdef0123";
        var value = (string?)method!.Invoke(null, new object?[] { output });

        Assert.Equal("00007ffabcdef0123", value);
    }

    [Fact]
    public void ExtractMethodTableFromDumpArray_WhenComponentMethodTablePresent_ReturnsValue()
    {
        var method = typeof(CollectionInspector).GetMethod(
            "ExtractMethodTableFromDumpArray",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var output = "ComponentMethodTable: 0x00007ffabcdef0123";
        var value = (string?)method!.Invoke(null, new object?[] { output });

        Assert.Equal("0x00007ffabcdef0123", value);
    }

    [Fact]
    public void ExtractMethodTableFromDumpArray_WhenNoMatch_ReturnsNull()
    {
        var method = typeof(CollectionInspector).GetMethod(
            "ExtractMethodTableFromDumpArray",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var value = (string?)method!.Invoke(null, new object?[] { "no mt here" });
        Assert.Null(value);
    }

    [Fact]
    public void ExtractElementSizeFromDumpArray_WhenElementSizePresent_ReturnsParsedValue()
    {
        var method = typeof(CollectionInspector).GetMethod(
            "ExtractElementSizeFromDumpArray",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var output = "Element Size: 24";
        var size = (int)method!.Invoke(null, new object?[] { output })!;

        Assert.Equal(24, size);
    }

    [Fact]
    public void ExtractElementSizeFromDumpArray_WhenComponentSizeHexPresent_ReturnsParsedValue()
    {
        var method = typeof(CollectionInspector).GetMethod(
            "ExtractElementSizeFromDumpArray",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var output = "ComponentSize: 0x18";
        var size = (int)method!.Invoke(null, new object?[] { output })!;

        Assert.Equal(24, size);
    }

    [Fact]
    public void ExtractElementSizeFromDumpArray_WhenNoMatch_ReturnsMinusOne()
    {
        var method = typeof(CollectionInspector).GetMethod(
            "ExtractElementSizeFromDumpArray",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var size = (int)method!.Invoke(null, new object?[] { "no size here" })!;

        Assert.Equal(-1, size);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0x00000000", true)]
    [InlineData("0000ABCD1234", true)]
    [InlineData("not-an-address", false)]
    public void LooksLikeAddress_WithVariousInputs_ReturnsExpected(string? value, bool expected)
    {
        var method = typeof(CollectionInspector).GetMethod(
            "LooksLikeAddress",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var result = (bool)method!.Invoke(null, new object?[] { value })!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseArrayElements_WhenMatchesPresent_ReturnsTrimmedValues()
    {
        var inspector = CreateInspector();
        var method = typeof(CollectionInspector).GetMethod(
            "ParseArrayElements",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotNull(method);

        var output = string.Join("\n", new[]
        {
            "Name: System.String[]",
            "Number of elements 2",
            "[0]  0x0000000000001000 ",
            "[1]  hello "
        });

        var result = (List<string>)method!.Invoke(inspector, new object?[] { output })!;

        Assert.Equal(new[] { "0x0000000000001000", "hello" }, result);
    }

    private static CollectionInspector CreateInspector()
    {
        var logger = NullLogger.Instance;
        var objectInspector = new ObjectInspector(logger);
        return new CollectionInspector(logger, objectInspector);
    }

    private sealed class FakeDebuggerManager : IDebuggerManager
    {
        public string DumparrayOutput { get; set; } = string.Empty;

        public bool IsInitialized => true;
        public bool IsDumpOpen => true;
        public string? CurrentDumpPath => null;
        public string DebuggerType => "Fake";
        public bool IsSosLoaded => true;
        public bool IsDotNetDump => true;

        public Task InitializeAsync() => Task.CompletedTask;
        public void OpenDumpFile(string dumpFilePath, string? executablePath = null) => throw new NotSupportedException();
        public void CloseDump() => throw new NotSupportedException();
        public void LoadSosExtension() => throw new NotSupportedException();
        public void ConfigureSymbolPath(string symbolPath) => throw new NotSupportedException();

        public string ExecuteCommand(string command)
        {
            if (command.StartsWith("dumparray", StringComparison.OrdinalIgnoreCase))
                return DumparrayOutput;

            throw new InvalidOperationException($"Unexpected command: {command}");
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
