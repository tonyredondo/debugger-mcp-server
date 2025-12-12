using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DebuggerMcp.SourceLink;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace DebuggerMcp.Tests.SourceLink;

public class SequencePointResolverTests
{
    [Fact]
    public void GetSourceLocation_WhenPdbMissing_ReturnsNull()
    {
        var resolver = new SequencePointResolver();

        var location = resolver.GetSourceLocation("/tmp/does-not-exist.dll", methodToken: 1, ilOffset: 0);

        Assert.Null(location);
    }

    [Fact]
    public void GetSourceLocation_NegativeIlOffset_ReturnsNull()
    {
        // token doesn't matter; negative IL offset is rejected before any PDB access.
        var resolver = new SequencePointResolver();
        var location = resolver.GetSourceLocation("/tmp/anything.dll", methodToken: 1, ilOffset: -1);

        Assert.Null(location);
    }

    [Fact]
    public void GetSourceLocation_WithPortablePdb_ReturnsNearestSequencePoint()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var (dllPath, pdbPath) = CompileAssemblyWithPdb(tempDir);

            var (methodRow, firstOffset) = ReadAnyMethodSequencePoint(pdbPath);

            var resolver = new SequencePointResolver();
            var location = resolver.GetSourceLocation(dllPath, methodToken: methodRow, ilOffset: firstOffset);

            Assert.NotNull(location);
            Assert.True(location!.Resolved);
            Assert.EndsWith("TestClass.cs", location.SourceFile, StringComparison.OrdinalIgnoreCase);
            Assert.True(location.LineNumber > 0);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ClearCache_ForcesReload()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var (dllPath, pdbPath) = CompileAssemblyWithPdb(tempDir);

            var (methodRow, firstOffset) = ReadAnyMethodSequencePoint(pdbPath);
            var resolver = new SequencePointResolver();

            var a = resolver.GetSourceLocation(dllPath, methodRow, firstOffset);
            resolver.ClearCache();
            var b = resolver.GetSourceLocation(dllPath, methodRow, firstOffset);

            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.Equal(a!.SourceFile, b!.SourceFile);
            Assert.Equal(a.LineNumber, b.LineNumber);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    private static (string dllPath, string pdbPath) CompileAssemblyWithPdb(string outputDir)
    {
        var source = @"
using System;

public class TestClass
{
    public static int Add(int a, int b)
    {
        // keep a few lines so we have stable sequence points
        var c = a + b;
        return c;
    }
}";

        var syntaxTree = CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), path: "TestClass.cs");
        var compilation = CSharpCompilation.Create(
            assemblyName: "SeqPointTestAssembly",
            syntaxTrees: new[] { syntaxTree },
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));

        var dllPath = Path.Combine(outputDir, "SeqPointTestAssembly.dll");
        // NOTE: keep the PDB side-by-side with the module so SequencePointResolver
        // can find it without extra search paths.
        var pdbPath = Path.Combine(outputDir, "SeqPointTestAssembly.pdb");

        using var dllStream = File.Create(dllPath);
        using var pdbStream = File.Create(pdbPath);

        var emitResult = compilation.Emit(
            peStream: dllStream,
            pdbStream: pdbStream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));

        Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics));

        return (dllPath, pdbPath);
    }

    private static (uint methodRow, int firstOffset) ReadAnyMethodSequencePoint(string pdbPath)
    {
        using var stream = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();

        foreach (var handle in reader.MethodDebugInformation)
        {
            var info = reader.GetMethodDebugInformation(handle);
            var sp = info.GetSequencePoints().FirstOrDefault(p => !p.IsHidden);
            if (sp.Document.IsNil)
                continue;

            var defHandle = handle.ToDefinitionHandle();
            var methodRow = (uint)MetadataTokens.GetRowNumber(defHandle);
            return (methodRow, sp.Offset);
        }

        throw new InvalidOperationException("No sequence points found in test PDB");
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SafeDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
