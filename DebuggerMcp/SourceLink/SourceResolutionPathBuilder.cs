using Microsoft.Diagnostics.Runtime;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Builds the canonical set of local directories that should participate in PDB and Source Link
/// resolution for the current dump context.
/// </summary>
/// <remarks>
/// The builder intentionally works only with the known dump context and known local caches. It
/// does not perform broad filesystem scans when the caller already knows which dump is open.
/// </remarks>
internal static class SourceResolutionPathBuilder
{
    /// <summary>
    /// Builds an ordered, de-duplicated list of existing directories that should be searched for
    /// symbols and source metadata.
    /// </summary>
    /// <param name="dumpPath">The resolved path to the currently open dump.</param>
    /// <param name="dumpId">The logical dump identifier associated with the dump.</param>
    /// <param name="executablePath">Optional resolved executable path for standalone apps.</param>
    /// <param name="localSymbolDirectories">The effective local symbol directories for the session.</param>
    /// <param name="runtime">Optional ClrMD runtime used to include runtime module directories.</param>
    /// <returns>Ordered, de-duplicated list of existing local directories.</returns>
    internal static IReadOnlyList<string> BuildExistingPaths(
        string dumpPath,
        string? dumpId,
        string? executablePath,
        IEnumerable<string>? localSymbolDirectories,
        ClrRuntime? runtime)
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
                var fullPath = Path.GetFullPath(path);
                if (!Directory.Exists(fullPath))
                {
                    return;
                }

                if (seen.Add(fullPath))
                {
                    paths.Add(fullPath);
                }
            }
            catch
            {
                // Best-effort only; invalid paths should not break the caller.
            }
        }

        // 1) The dump directory itself can contain side-by-side symbols or binaries.
        AddIfExists(Path.GetDirectoryName(dumpPath));

        // 2) Standalone apps frequently carry PDBs next to the uploaded executable.
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            AddIfExists(Path.GetDirectoryName(executablePath));
        }

        // 3) The effective session-local symbol directories should drive both debugger and source
        // resolution behavior. Datadog-managed symbols live under ".datadog" beneath these caches.
        foreach (var directory in localSymbolDirectories ?? Array.Empty<string>())
        {
            AddIfExists(directory);
            AddIfExists(Path.Combine(directory, ".datadog"));
        }

        // 4) Common user-local caches used by dotnet-symbol and package restores.
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            AddIfExists(Path.Combine(homeDirectory, ".dotnet", "symbolcache"));
            AddIfExists(Path.Combine(homeDirectory, ".nuget", "packages"));
        }

        // 5) Runtime module directories provide framework-side PDB locations for managed analysis.
        if (runtime != null)
        {
            foreach (var module in runtime.EnumerateModules())
            {
                AddIfExists(Path.GetDirectoryName(module.Name));
            }
        }

        _ = dumpId;
        return paths;
    }
}
