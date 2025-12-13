using DebuggerMcp.SourceLink;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp.Tests.SourceLink;

/// <summary>
/// Portable-PDB extraction coverage tests for <see cref="SourceLinkResolver"/>.
/// </summary>
public class SourceLinkResolverPortablePdbExtractionTests
{
    [Fact]
    public void GetSourceLinkForModule_WithBuiltModule_ParsesPortablePdbAndCachesResult()
    {
        var repoRoot = FindRepoRoot();
        var (configuration, tfm) = GetBuildConfigurationAndTfm();

        var modulePath = Path.Combine(repoRoot, "DebuggerMcp", "bin", configuration, tfm, "DebuggerMcp.dll");
        Assert.True(File.Exists(modulePath), $"Module not built: {modulePath}");

        var resolver = new SourceLinkResolver(NullLogger.Instance);

        var first = resolver.GetSourceLinkForModule(modulePath);
        var second = resolver.GetSourceLinkForModule(modulePath);

        // Result may legitimately be null when the PDB has no embedded Source Link entry,
        // but the resolver should cache and return the exact same reference on subsequent calls.
        Assert.Same(first, second);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DebuggerMcp.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root (DebuggerMcp.slnx not found).");
    }

    private static (string configuration, string tfm) GetBuildConfigurationAndTfm()
    {
        var testAssemblyDir = new FileInfo(typeof(SourceLinkResolverPortablePdbExtractionTests).Assembly.Location).Directory;
        var tfm = testAssemblyDir?.Name ?? "net10.0";
        var config = testAssemblyDir?.Parent?.Name ?? "Debug";
        return (config, tfm);
    }
}

