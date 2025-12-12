using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DebuggerMcp.Analysis;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Tests for metadata decoding logic in <see cref="ClrMdAnalyzer"/>.
/// </summary>
public class ClrMdAnalyzerMetadataDecodingTests
{
    public static IEnumerable<object[]> AssemblyPaths()
    {
        // Framework
        yield return [typeof(string).Assembly.Location];
        yield return [typeof(Uri).Assembly.Location];
        yield return [typeof(Enumerable).Assembly.Location];

        // Repo
        yield return [typeof(ClrMdAnalyzer).Assembly.Location];
        yield return [typeof(ClrMdAnalyzerMetadataDecodingTests).Assembly.Location];
    }

    [Theory]
    [MemberData(nameof(AssemblyPaths))]
    public void ExtractAttributes_FromMultipleAssemblies_ReturnsAttributes(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();

        var (attributes, _) = ClrMdAnalyzer.ExtractAttributes(reader);

        Assert.NotNull(attributes);
        Assert.NotEmpty(attributes);
        Assert.All(attributes, a => Assert.False(string.IsNullOrWhiteSpace(a.AttributeType)));
    }

    [Fact]
    public void ExtractAttributes_FromSystemPrivateCoreLib_ReturnsSomeAttributes()
    {
        // System.Private.CoreLib is guaranteed to be a managed assembly with metadata.
        var assemblyPath = typeof(string).Assembly.Location;

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();

        var (attributes, version) = ClrMdAnalyzer.ExtractAttributes(reader);

        Assert.NotNull(attributes);
        Assert.NotEmpty(attributes);

        // Version should typically be present, but we only require "valid" output.
        if (version != null)
        {
            Assert.False(string.IsNullOrWhiteSpace(version));
        }
    }

    [Fact]
    public void ExtractAttributes_IncludesTargetFrameworkAttribute_ForProjectAssemblies()
    {
        var assemblyPath = typeof(ClrMdAnalyzerMetadataDecodingTests).Assembly.Location;

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();

        var (attributes, _) = ClrMdAnalyzer.ExtractAttributes(reader);

        Assert.Contains(attributes, a => a.AttributeType == "System.Runtime.Versioning.TargetFrameworkAttribute");
    }

    [Fact]
    public void ExtractAttributes_WhenDebuggableAttributePresent_ProducesReadableFlags()
    {
        // Many assemblies include DebuggableAttribute; if it exists, we expect a readable value.
        var assemblyPath = typeof(ClrMdAnalyzer).Assembly.Location;

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();

        var (attributes, _) = ClrMdAnalyzer.ExtractAttributes(reader);

        var dbg = attributes.FirstOrDefault(a => a.AttributeType == "System.Diagnostics.DebuggableAttribute");
        if (dbg != null)
        {
            Assert.NotNull(dbg.Value);
            Assert.NotEqual("<binary>", dbg.Value);
        }
    }
}
