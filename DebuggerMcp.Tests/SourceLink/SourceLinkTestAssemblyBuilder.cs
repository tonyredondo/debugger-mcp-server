using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace DebuggerMcp.Tests.SourceLink;

/// <summary>
/// Builds small portable-PDB test assemblies that embed Source Link metadata.
/// </summary>
internal static class SourceLinkTestAssemblyBuilder
{
    /// <summary>
    /// Source file path embedded into the generated test PDB.
    /// </summary>
    internal const string DefaultSourceFilePath = "/src/TestClass.cs";

    /// <summary>
    /// Compiles a temporary assembly and portable PDB that include Source Link mappings.
    /// </summary>
    /// <param name="outputDirectory">Directory that should receive the generated DLL and PDB.</param>
    /// <param name="assemblyName">Logical assembly name to emit.</param>
    /// <param name="sourceFilePath">Source file path that Source Link should map.</param>
    /// <returns>The generated module path and PDB path.</returns>
    internal static (string DllPath, string PdbPath) CompileAssemblyWithSourceLink(
        string outputDirectory,
        string assemblyName = "SourceLinkTestAssembly",
        string sourceFilePath = DefaultSourceFilePath)
    {
        Directory.CreateDirectory(outputDirectory);

        const string source = """
using System;

public static class TestClass
{
    public static int Add(int a, int b)
    {
        var c = a + b;
        return c;
    }
}
""";

        var syntaxTree = CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), path: sourceFilePath);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees: [syntaxTree],
            references:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            ],
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug));

        var dllPath = Path.Combine(outputDirectory, $"{assemblyName}.dll");
        var pdbPath = Path.Combine(outputDirectory, $"{assemblyName}.pdb");
        var sourceLinkJson = $$"""
{
  "documents": {
    "/src/*": "https://raw.githubusercontent.com/user/repo/abc123/src/*"
  }
}
""";

        using var dllStream = File.Create(dllPath);
        using var pdbStream = File.Create(pdbPath);
        using var sourceLinkStream = new MemoryStream(Encoding.UTF8.GetBytes(sourceLinkJson));

        var emitResult = compilation.Emit(
            peStream: dllStream,
            pdbStream: pdbStream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
            sourceLinkStream: sourceLinkStream);

        Assert.True(
            emitResult.Success,
            string.Join(Environment.NewLine, emitResult.Diagnostics.Select(diagnostic => diagnostic.ToString())));

        return (dllPath, pdbPath);
    }
}
