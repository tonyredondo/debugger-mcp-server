using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DebuggerMcp.Analysis;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Tests for assembly metadata attribute extraction helpers in <see cref="ClrMdAnalyzer"/>.
/// </summary>
public class ClrMdAnalyzerAttributeExtractionTests
{
    [Fact]
    public void ExtractAttributes_FromDebuggerMcpAssembly_ReturnsVersionAndSomeAttributes()
    {
        var assemblyPath = typeof(ClrMdAnalyzer).Assembly.Location;
        Assert.False(string.IsNullOrWhiteSpace(assemblyPath));
        Assert.True(File.Exists(assemblyPath));

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();

        var (attributes, version) = ClrMdAnalyzer.ExtractAttributes(metadataReader);

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.NotEmpty(attributes);
        Assert.All(attributes, a => Assert.False(string.IsNullOrWhiteSpace(a.AttributeType)));
        Assert.Contains(attributes, a => a.AttributeType.Contains("TargetFramework", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractAttributes_FromFrameworkAssembly_DoesNotThrow()
    {
        var assemblyPath = typeof(string).Assembly.Location;
        Assert.False(string.IsNullOrWhiteSpace(assemblyPath));
        Assert.True(File.Exists(assemblyPath));

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();

        var (attributes, version) = ClrMdAnalyzer.ExtractAttributes(metadataReader);

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.NotNull(attributes);
    }
}

