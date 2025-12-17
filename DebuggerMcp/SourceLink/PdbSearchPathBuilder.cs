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
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfExists(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(path);
                if (!Directory.Exists(full))
                {
                    return;
                }
                if (seen.Add(full))
                {
                    paths.Add(full);
                }
            }
            catch
            {
                // Best-effort; ignore invalid paths.
            }
        }

        // 1) Side-by-side PDBs next to the dump.
        var dumpDirectory = Path.GetDirectoryName(dumpPath);
        AddIfExists(dumpDirectory);

        // 2) Per-dump symbol cache directory.
        if (!string.IsNullOrWhiteSpace(dumpDirectory))
        {
            var suffix = !string.IsNullOrWhiteSpace(dumpId)
                ? dumpId
                : Path.GetFileNameWithoutExtension(dumpPath);

            if (!string.IsNullOrWhiteSpace(suffix))
            {
                var symbolDir = Path.Combine(dumpDirectory, $".symbols_{suffix}");
                AddIfExists(symbolDir);

                // Datadog symbols are stored under a ".datadog" subdirectory of the per-dump symbol cache.
                AddIfExists(Path.Combine(symbolDir, ".datadog"));
            }
        }

        // 3) Common symbol cache locations.
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(homeDir))
        {
            AddIfExists(Path.Combine(homeDir, ".dotnet", "symbolcache"));
            AddIfExists(Path.Combine(homeDir, ".nuget", "packages"));
        }

        // 4) Runtime module directories (framework shared folders, etc.).
        if (runtime != null)
        {
            foreach (var module in runtime.EnumerateModules())
            {
                AddIfExists(Path.GetDirectoryName(module.Name));
            }
        }

        return paths;
    }
}

