using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Cache entry for a module's sequence points.
/// </summary>
public class ModuleSequencePoints
{
    /// <summary>
    /// Module name (without extension).
    /// </summary>
    public string ModuleName { get; set; } = string.Empty;

    /// <summary>
    /// Path to the PDB file.
    /// </summary>
    public string? PdbPath { get; set; }

    /// <summary>
    /// Methods with their sequence points, keyed by metadata token row number.
    /// </summary>
    public Dictionary<uint, List<MethodSequencePoint>> Methods { get; set; } = new();
}

/// <summary>
/// A sequence point mapping IL offset to source location.
/// Named differently from System.Reflection.Metadata.SequencePoint to avoid conflicts.
/// </summary>
public class MethodSequencePoint
{
    /// <summary>
    /// IL offset for this sequence point.
    /// </summary>
    public int ILOffset { get; set; }

    /// <summary>
    /// Source document path.
    /// </summary>
    public string Document { get; set; } = string.Empty;

    /// <summary>
    /// Start line number (1-based).
    /// </summary>
    public int StartLine { get; set; }

    /// <summary>
    /// Start column number (1-based).
    /// </summary>
    public int StartColumn { get; set; }

    /// <summary>
    /// End line number (1-based).
    /// </summary>
    public int EndLine { get; set; }

    /// <summary>
    /// End column number (1-based).
    /// </summary>
    public int EndColumn { get; set; }
}

/// <summary>
/// Resolves IL offsets to source file/line using PDB sequence points.
/// </summary>
public class SequencePointResolver
{
    private readonly ConcurrentDictionary<string, ModuleSequencePoints?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pdbSearchPaths = new();
    private readonly ILogger? _logger;
    private readonly object _searchPathsLock = new();

    /// <summary>
    /// Initializes a new instance of the SequencePointResolver class.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public SequencePointResolver(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Adds a path to search for PDB files.
    /// </summary>
    /// <param name="path">Directory path to add to the search list.</param>
    public void AddPdbSearchPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        lock (_searchPathsLock)
        {
            if (Directory.Exists(path) && !_pdbSearchPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _pdbSearchPaths.Add(path);
                _logger?.LogDebug("[SeqPoints] Added PDB search path: {Path}", path);
            }
        }
    }

    /// <summary>
    /// Clears the cache, forcing reload of PDB data on next access.
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
        _logger?.LogDebug("[SeqPoints] Cache cleared");
    }

    /// <summary>
    /// Gets the source location for a method at a specific IL offset.
    /// </summary>
    /// <param name="modulePath">Full path to the module.</param>
    /// <param name="methodToken">Metadata token row number of the method.</param>
    /// <param name="ilOffset">IL offset within the method.</param>
    /// <returns>Source location if found; null otherwise.</returns>
    public SourceLocation? GetSourceLocation(string modulePath, uint methodToken, int ilOffset)
        => GetSourceLocation(modulePath, pdbGuid: null, pdbRevision: null, methodToken, ilOffset);

    /// <summary>
    /// Gets the source location for a method at a specific IL offset, using an optional expected PDB signature.
    /// </summary>
    /// <param name="modulePath">Full path to the module.</param>
    /// <param name="pdbGuid">Expected PDB GUID for the module (if available).</param>
    /// <param name="pdbRevision">Expected PDB revision/stamp for the module (if available).</param>
    /// <param name="methodToken">Metadata token row number of the method.</param>
    /// <param name="ilOffset">IL offset within the method.</param>
    /// <returns>Source location if found; null otherwise.</returns>
    public SourceLocation? GetSourceLocation(string modulePath, Guid? pdbGuid, int? pdbRevision, uint methodToken, int ilOffset)
    {
        // Accept IL offset 0 as fallback for "method entry point"
        // Negative values are still invalid
        if (ilOffset < 0)
        {
            _logger?.LogTrace("[SeqPoints] Negative IL offset ({ILOffset}) for method 0x{Token:X} in {Module}", 
                ilOffset, methodToken, modulePath);
            return null;
        }

        var modulePoints = GetOrLoadModuleSequencePoints(modulePath, pdbGuid, pdbRevision);
        if (modulePoints == null)
        {
            _logger?.LogTrace("[SeqPoints] No sequence points for module {Module}", modulePath);
            return null;
        }

        if (!modulePoints.Methods.TryGetValue(methodToken, out var methodPoints))
        {
            _logger?.LogTrace("[SeqPoints] Method token 0x{Token:X} not found in {Module} (loaded {Count} methods)", 
                methodToken, Path.GetFileName(modulePath), modulePoints.Methods.Count);
            return null;
        }

        // Find the sequence point that contains this IL offset
        // We want the highest IL offset that is <= our target
        MethodSequencePoint? best = null;
        foreach (var sp in methodPoints)
        {
            if (sp.ILOffset <= ilOffset)
            {
                if (best == null || sp.ILOffset > best.ILOffset)
                    best = sp;
            }
        }

        if (best == null)
        {
            _logger?.LogTrace("[SeqPoints] No sequence point found for IL offset {ILOffset} in method 0x{Token:X}", 
                ilOffset, methodToken);
            return null;
        }

        return new SourceLocation
        {
            SourceFile = best.Document,
            LineNumber = best.StartLine,
            ColumnNumber = best.StartColumn,
            Resolved = true
        };
    }

    /// <summary>
    /// Gets or loads sequence points for a module, with caching.
    /// </summary>
    private ModuleSequencePoints? GetOrLoadModuleSequencePoints(string modulePath, Guid? pdbGuid, int? pdbRevision)
    {
        var moduleName = Path.GetFileNameWithoutExtension(modulePath);
        if (string.IsNullOrEmpty(moduleName))
            return null;

        var cacheKey = CreateCacheKey(moduleName, pdbGuid, pdbRevision);
        return _cache.GetOrAdd(cacheKey, _ =>
        {
            var pdbPath = FindPdbFile(modulePath, pdbGuid, pdbRevision);
            if (pdbPath == null)
            {
                _logger?.LogDebug("[SeqPoints] No matching PDB found for {Module} (expected {Guid}/{Rev})",
                    moduleName, pdbGuid, pdbRevision);
                return null;
            }

            try
            {
                return LoadSequencePoints(pdbPath, moduleName);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[SeqPoints] Failed to load PDB {PdbPath}", pdbPath);
                return null;
            }
        });
    }

    /// <summary>
    /// Finds the PDB file for a module.
    /// </summary>
    private string? FindPdbFile(string modulePath, Guid? expectedGuid, int? expectedRevision)
    {
        var moduleName = Path.GetFileNameWithoutExtension(modulePath);
        if (string.IsNullOrEmpty(moduleName))
            return null;

        var pdbName = moduleName + ".pdb";

        // Check alongside module first
        var moduleDir = Path.GetDirectoryName(modulePath);
        if (!string.IsNullOrEmpty(moduleDir))
        {
            var sideBySide = Path.Combine(moduleDir, pdbName);
            if (File.Exists(sideBySide) && IsMatchingPortablePdb(sideBySide, expectedGuid, expectedRevision))
                return sideBySide;
        }

        // Check search paths
        List<string> searchPaths;
        lock (_searchPathsLock)
        {
            searchPaths = _pdbSearchPaths.ToList();
        }

        foreach (var searchPath in searchPaths)
        {
            // Direct file in search path
            var pdbPath = Path.Combine(searchPath, pdbName);
            if (File.Exists(pdbPath) && IsMatchingPortablePdb(pdbPath, expectedGuid, expectedRevision))
                return pdbPath;

            // Also check subdirectories (symbol cache structure)
            try
            {
                foreach (var found in Directory.GetFiles(searchPath, pdbName, SearchOption.AllDirectories))
                {
                    if (IsMatchingPortablePdb(found, expectedGuid, expectedRevision))
                        return found;
                }
            }
            catch
            {
                // Ignore search errors
            }
        }

        return null;
    }

    private static string CreateCacheKey(string moduleName, Guid? pdbGuid, int? pdbRevision)
    {
        if (pdbGuid == null || pdbGuid.Value == Guid.Empty)
            return moduleName;

        return pdbRevision.HasValue
            ? $"{moduleName}|{pdbGuid:D}|{pdbRevision.Value}"
            : $"{moduleName}|{pdbGuid:D}";
    }

    private static bool IsMatchingPortablePdb(string pdbPath, Guid? expectedGuid, int? expectedRevision)
    {
        if (expectedGuid == null || expectedGuid.Value == Guid.Empty)
            return true;

        if (!TryReadPortablePdbSignature(pdbPath, out var guid, out var revision))
            return false;

        if (guid != expectedGuid.Value)
            return false;

        return !expectedRevision.HasValue || revision == expectedRevision.Value;
    }

    private static bool TryReadPortablePdbSignature(string pdbPath, out Guid guid, out int revision)
    {
        guid = Guid.Empty;
        revision = 0;

        try
        {
            using var stream = File.OpenRead(pdbPath);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
            var reader = provider.GetMetadataReader();

            var header = reader.DebugMetadataHeader;
            if (header == null)
                return false;

            var id = header.Id;

            // Portable PDB ID is 20 bytes: 16-byte GUID + 4-byte stamp (little-endian).
            if (id.IsDefaultOrEmpty || id.Length < 20)
                return false;

            guid = new Guid(id.AsSpan(0, 16));
            revision = BitConverter.ToInt32(id.AsSpan(16, 4));
            return guid != Guid.Empty;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Loads sequence points from a PDB file.
    /// </summary>
    private ModuleSequencePoints LoadSequencePoints(string pdbPath, string moduleName)
    {
        using var stream = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();

        var result = new ModuleSequencePoints
        {
            ModuleName = moduleName,
            PdbPath = pdbPath
        };

        foreach (var methodDebugHandle in reader.MethodDebugInformation)
        {
            try
            {
                var methodDebug = reader.GetMethodDebugInformation(methodDebugHandle);

                // Convert handle to row number (this is what ClrMD gives us as MetadataToken)
                var defHandle = methodDebugHandle.ToDefinitionHandle();
                var methodToken = (uint)MetadataTokens.GetRowNumber(defHandle);

                var points = new List<MethodSequencePoint>();
                foreach (var sp in methodDebug.GetSequencePoints())
                {
                    if (sp.IsHidden)
                        continue;

                    var document = reader.GetDocument(sp.Document);
                    var docName = reader.GetString(document.Name);

                    points.Add(new MethodSequencePoint
                    {
                        ILOffset = sp.Offset,
                        Document = docName,
                        StartLine = sp.StartLine,
                        StartColumn = sp.StartColumn,
                        EndLine = sp.EndLine,
                        EndColumn = sp.EndColumn
                    });
                }

                if (points.Count > 0)
                {
                    result.Methods[methodToken] = points;
                }
            }
            catch
            {
                // Skip methods with errors
            }
        }

        _logger?.LogDebug("[SeqPoints] Loaded {Count} methods from {PdbPath}",
            result.Methods.Count, pdbPath);

        return result;
    }
}
