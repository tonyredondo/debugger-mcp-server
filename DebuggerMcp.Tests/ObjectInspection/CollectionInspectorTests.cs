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
