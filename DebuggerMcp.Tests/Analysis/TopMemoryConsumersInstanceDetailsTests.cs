using DebuggerMcp.Analysis;

namespace DebuggerMcp.Tests.Analysis;

public class TopMemoryConsumersInstanceDetailsTests
{
    [Fact]
    public void TryMarkStaticTypeProcessed_DedupesByMethodTableWhenAvailable()
    {
        var processedMethodTables = new HashSet<ulong>();
        var processedTypeNames = new HashSet<string>(StringComparer.Ordinal);

        Assert.True(ClrMdAnalyzer.TryMarkStaticTypeProcessed(0x1234, "TypeA", processedMethodTables, processedTypeNames));
        Assert.False(ClrMdAnalyzer.TryMarkStaticTypeProcessed(0x1234, "TypeA", processedMethodTables, processedTypeNames));

        Assert.True(ClrMdAnalyzer.TryMarkStaticTypeProcessed(0x5678, "TypeA", processedMethodTables, processedTypeNames));
        Assert.False(ClrMdAnalyzer.TryMarkStaticTypeProcessed(0x5678, "DifferentName", processedMethodTables, processedTypeNames));
    }

    [Fact]
    public void TryMarkStaticTypeProcessed_FallsBackToTypeNameWhenMethodTableUnknown()
    {
        var processedMethodTables = new HashSet<ulong>();
        var processedTypeNames = new HashSet<string>(StringComparer.Ordinal);

        Assert.True(ClrMdAnalyzer.TryMarkStaticTypeProcessed(0, "TypeA", processedMethodTables, processedTypeNames));
        Assert.False(ClrMdAnalyzer.TryMarkStaticTypeProcessed(0, "TypeA", processedMethodTables, processedTypeNames));

        Assert.True(ClrMdAnalyzer.TryMarkStaticTypeProcessed(0, " TypeB ", processedMethodTables, processedTypeNames));
        Assert.False(ClrMdAnalyzer.TryMarkStaticTypeProcessed(0, "TypeB", processedMethodTables, processedTypeNames));

        Assert.False(ClrMdAnalyzer.TryMarkStaticTypeProcessed(0, "   ", processedMethodTables, processedTypeNames));
    }

    [Fact]
    public void GetTypesToCollectInstancesFor_OnlyIncludesSmallCounts()
    {
        var top = new TopMemoryConsumers
        {
            BySize =
            [
                new TypeMemoryStats { Type = "A", Count = 1 },
                new TypeMemoryStats { Type = "B", Count = 5 },
                new TypeMemoryStats { Type = "C", Count = 6 }
            ],
            ByCount =
            [
                new TypeMemoryStats { Type = "D", Count = 2 },
                new TypeMemoryStats { Type = "E", Count = 999 }
            ]
        };

        var types = ClrMdAnalyzer.GetTypesToCollectInstancesFor(top, maxInstancesPerType: 5);

        Assert.Equal(3, types.Count);
        Assert.Equal(1, types["A"]);
        Assert.Equal(5, types["B"]);
        Assert.Equal(2, types["D"]);
        Assert.False(types.ContainsKey("C"));
        Assert.False(types.ContainsKey("E"));
    }

    [Fact]
    public void AttachInstancesToTopConsumers_AttachesToBySizeAndByCount()
    {
        var shared = new List<MemoryObjectInstance>
        {
            new() { Address = "0x0000000000000001", Size = 123 }
        };

        var top = new TopMemoryConsumers
        {
            BySize =
            [
                new TypeMemoryStats { Type = "A", Count = 1 },
                new TypeMemoryStats { Type = "B", Count = 10 }
            ],
            ByCount =
            [
                new TypeMemoryStats { Type = "A", Count = 1 }
            ]
        };

        ClrMdAnalyzer.AttachInstancesToTopConsumers(top, new Dictionary<string, List<MemoryObjectInstance>>
        {
            ["A"] = shared
        });

        Assert.NotNull(top.BySize![0].Instances);
        Assert.Same(shared, top.BySize![0].Instances);
        Assert.Null(top.BySize![1].Instances);

        Assert.NotNull(top.ByCount![0].Instances);
        Assert.Same(shared, top.ByCount![0].Instances);
    }

    [Fact]
    public void MemoryObjectInstance_CanCarryOwnerDetails()
    {
        var instance = new MemoryObjectInstance
        {
            Address = "0x0000000000000001",
            Size = 10,
            Owners =
            [
                new ObjectReferenceOwner
                {
                    Address = "0x0000000000000002",
                    Type = "OwnerType",
                    FieldName = "_field",
                    IsStatic = false
                },
                new ObjectReferenceOwner
                {
                    Address = "0x0000000000000003",
                    Type = "OwnerType2",
                    FieldName = "_other",
                    IsStatic = false
                }
            ]
        };

        Assert.Equal("0x0000000000000001", instance.Address);
        Assert.Equal(2, instance.Owners.Count);
        Assert.Equal("OwnerType", instance.Owners[0].Type);
        Assert.Equal("_field", instance.Owners[0].FieldName);
    }
}
