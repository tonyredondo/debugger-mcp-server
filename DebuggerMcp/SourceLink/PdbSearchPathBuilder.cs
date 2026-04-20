using Microsoft.Diagnostics.Runtime;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Builds a deterministic set of PDB search paths for ClrMD-based source resolution.
/// </summary>
internal static class PdbSearchPathBuilder
{
    /// <summary>
    /// Builds a list of existing directories that should be searched for PDBs.
    /// </summary>
    /// <param name="dumpPath">Path to the dump file.</param>
    /// <param name="dumpId">
    /// Optional dump identifier (used to locate per-dump symbol directories like <c>.symbols_&lt;dumpId&gt;</c>).
    /// </param>
    /// <param name="runtime">
    /// Optional ClrMD runtime used to add framework/runtime module directories as potential PDB locations.
    /// </param>
    /// <returns>Ordered, de-duplicated list of existing directories.</returns>
    internal static IReadOnlyList<string> BuildExistingPaths(string dumpPath, string? dumpId, ClrRuntime? runtime)
    {
        var localSymbolDirectories = new List<string>();

        if (!string.IsNullOrWhiteSpace(dumpPath))
        {
            var dumpDirectory = Path.GetDirectoryName(dumpPath);
            if (!string.IsNullOrWhiteSpace(dumpDirectory) && !string.IsNullOrWhiteSpace(dumpId))
            {
                localSymbolDirectories.Add(Path.Combine(dumpDirectory, $".symbols_{dumpId}"));
            }
        }

        return SourceResolutionPathBuilder.BuildExistingPaths(
            dumpPath,
            dumpId,
            executablePath: null,
            localSymbolDirectories,
            runtime);
    }
}

