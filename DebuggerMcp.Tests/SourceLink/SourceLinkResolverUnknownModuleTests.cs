using DebuggerMcp.SourceLink;
using Microsoft.Extensions.Logging.Abstractions;

namespace DebuggerMcp.Tests.SourceLink;

/// <summary>
/// Covers Source Link resolution when the caller does not know the producing module.
/// </summary>
public sealed class SourceLinkResolverUnknownModuleTests
{
    /// <summary>
    /// Verifies that the module-agnostic overload can resolve Source Link from configured PDBs.
    /// </summary>
    [Fact]
    public void Resolve_WithoutModulePath_SearchesConfiguredPdbsAndReturnsBrowsableUrl()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            SourceLinkTestAssemblyBuilder.CompileAssemblyWithSourceLink(tempDirectory);

            var resolver = new SourceLinkResolver(NullLogger.Instance);
            resolver.AddSymbolSearchPath(tempDirectory);

            var result = resolver.Resolve(SourceLinkTestAssemblyBuilder.DefaultSourceFilePath, 42);

            Assert.True(result.Resolved);
            Assert.Equal(SourceProvider.GitHub, result.Provider);
            Assert.Equal(
                "https://github.com/user/repo/blob/abc123/src/TestClass.cs#L42",
                result.Url);
        }
        finally
        {
            SafeDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>
    /// Verifies that the existing module-aware overload remains compatible with empty module input.
    /// </summary>
    [Fact]
    public void Resolve_WithEmptyModulePath_UsesModuleAgnosticFallback()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            SourceLinkTestAssemblyBuilder.CompileAssemblyWithSourceLink(tempDirectory);

            var resolver = new SourceLinkResolver(NullLogger.Instance);
            resolver.AddSymbolSearchPath(tempDirectory);

            var result = resolver.Resolve(string.Empty, SourceLinkTestAssemblyBuilder.DefaultSourceFilePath, 17);

            Assert.True(result.Resolved);
            Assert.Equal(
                "https://github.com/user/repo/blob/abc123/src/TestClass.cs#L17",
                result.Url);
        }
        finally
        {
            SafeDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>
    /// Creates an isolated temporary directory for Source Link tests.
    /// </summary>
    /// <returns>The created directory path.</returns>
    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Deletes a temporary test directory on a best-effort basis.
    /// </summary>
    /// <param name="directory">Directory to delete.</param>
    private static void SafeDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
