using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Resolves source file paths to Source Link URLs using PDB metadata.
/// Supports Portable PDBs with embedded Source Link JSON.
/// </summary>
/// <remarks>
/// <para>
/// Source Link is a technology that enables source code debugging by embedding repository
/// URLs into PDB files at build time. This resolver extracts that information and converts
/// it into browsable URLs for common source control providers.
/// </para>
/// <para>Supported providers:</para>
/// <list type="bullet">
/// <item><description><b>GitHub</b>: github.com, raw.githubusercontent.com</description></item>
/// <item><description><b>GitLab</b>: gitlab.com and self-hosted instances</description></item>
/// <item><description><b>Azure DevOps</b>: dev.azure.com, visualstudio.com</description></item>
/// <item><description><b>Bitbucket</b>: bitbucket.org</description></item>
/// </list>
/// <para>Supported PDB formats:</para>
/// <list type="bullet">
/// <item><description>Portable PDBs with embedded Source Link custom debug info (GUID: CC110556-A091-4D38-9FEC-25AB9A351A6A)</description></item>
/// </list>
/// <para>Note: Windows PDBs (MSF format) do not support Source Link and will return null.</para>
/// </remarks>
public class SourceLinkResolver
{
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, ModuleSourceLinkCache> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warnedModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _warnedModulesLock = new();
    private readonly List<string> _symbolSearchPaths = new();

    // Source Link custom debug information GUID
    private static readonly Guid SourceLinkGuid = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");
    private static readonly string[] KnownBinaryExtensions = [".dll", ".exe", ".so", ".dylib", ".pdb"];

    /// <summary>
    /// Gets a stable module identifier for caching and PDB lookup.
    /// </summary>
    /// <remarks>
    /// Stack frames can report modules as:
    /// - A full file path (e.g. "/usr/share/dotnet/.../System.Threading.dll")
    /// - A file name (e.g. "libcoreclr.so")
    /// - A dotted assembly name (e.g. "System.Threading")
    ///
    /// For dotted assembly names, <see cref="Path.GetFileNameWithoutExtension(string)"/> would incorrectly
    /// treat the suffix (".Threading") as a file extension. This helper keeps dotted names intact.
    /// </remarks>
    private static string GetModuleIdentifier(string modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath))
        {
            return string.Empty;
        }

        var trimmed = modulePath.Trim();

        // Treat anything with directory separators as a file path.
        if (trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return Path.GetFileNameWithoutExtension(trimmed);
        }

        // Treat known binary extensions as file names.
        var ext = Path.GetExtension(trimmed);
        if (!string.IsNullOrWhiteSpace(ext) &&
            KnownBinaryExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
        {
            return Path.GetFileNameWithoutExtension(trimmed);
        }

        // Otherwise, assume the input is already an identifier (e.g. "System.Threading").
        return trimmed;
    }

    /// <summary>
    /// Determines whether a module string looks like a real file path or file name.
    /// </summary>
    private static bool LooksLikeFilePath(string modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath))
        {
            return false;
        }

        var trimmed = modulePath.Trim();

        if (trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return true;
        }

        var ext = Path.GetExtension(trimmed);
        return !string.IsNullOrWhiteSpace(ext) &&
               KnownBinaryExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceLinkResolver"/> class.
    /// </summary>
    /// <param name="logger">Optional logger instance (accepts any ILogger).</param>
    public SourceLinkResolver(ILogger? logger = null)
    {
        _logger = logger;
        _logger?.LogInformation("[SourceLink] SourceLinkResolver initialized");
    }

    /// <summary>
    /// Adds a path to search for PDB files.
    /// </summary>
    /// <param name="path">The directory path to search.</param>
    public void AddSymbolSearchPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger?.LogWarning("[SourceLink] AddSymbolSearchPath called with empty path, ignoring");
            return;
        }

        if (!Directory.Exists(path))
        {
            _logger?.LogWarning("[SourceLink] Symbol search path does not exist: {Path}", path);
            return;
        }

        _symbolSearchPaths.Add(path);
        _logger?.LogInformation("[SourceLink] Added symbol search path: {Path}", path);

        // List PDB files in this path for debugging
        try
        {
            var pdbFiles = Directory.GetFiles(path, "*.pdb", SearchOption.AllDirectories);
            _logger?.LogInformation("[SourceLink] Found {Count} PDB files in {Path}", pdbFiles.Length, path);
            foreach (var pdb in pdbFiles.Take(10))
            {
                _logger?.LogDebug("[SourceLink]   - {PdbFile}", Path.GetFileName(pdb));
            }
            if (pdbFiles.Length > 10)
            {
                _logger?.LogDebug("[SourceLink]   ... and {More} more", pdbFiles.Length - 10);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SourceLink] Error listing PDB files in {Path}", path);
        }
    }

    /// <summary>
    /// Resolves a source location to a Source Link URL.
    /// </summary>
    /// <param name="modulePath">Path to the module (DLL/EXE) whose PDB contains the source link.</param>
    /// <param name="sourceFile">Source file path from the PDB (as originally recorded at build time).</param>
    /// <param name="lineNumber">Line number in the source file.</param>
    /// <param name="columnNumber">Optional column number for more precise location.</param>
    /// <returns>
    /// A <see cref="SourceLocation"/> containing the resolved URL and provider information,
    /// or error details if resolution failed.
    /// </returns>
    /// <remarks>
    /// <para>Resolution process:</para>
    /// <list type="number">
    /// <item><description>Find the PDB file for the module</description></item>
    /// <item><description>Extract Source Link JSON from the PDB</description></item>
    /// <item><description>Match the source file against document patterns</description></item>
    /// <item><description>Detect the provider and convert to browsable URL</description></item>
    /// </list>
    /// <para>Common failure reasons:</para>
    /// <list type="bullet">
    /// <item><description>PDB file not found in search paths</description></item>
    /// <item><description>PDB is Windows format (not Portable PDB)</description></item>
    /// <item><description>PDB doesn't contain Source Link information</description></item>
    /// <item><description>Source file path doesn't match any document pattern</description></item>
    /// </list>
    /// </remarks>
    public SourceLocation Resolve(string modulePath, string sourceFile, int lineNumber, int? columnNumber = null)
    {
        _logger?.LogInformation("[SourceLink] Resolve called: Module={Module}, SourceFile={SourceFile}, Line={Line}",
            modulePath, sourceFile, lineNumber);

        // Initialize result with input parameters
        var result = new SourceLocation
        {
            SourceFile = sourceFile,
            LineNumber = lineNumber,
            ColumnNumber = columnNumber
        };

        try
        {
            var moduleName = GetModuleIdentifier(modulePath);
            _logger?.LogDebug("[SourceLink] Module name extracted: {ModuleName}", moduleName);

            // Step 1: Get Source Link information from the module's PDB
            // This uses caching to avoid re-reading PDB files
            var sourceLink = GetSourceLinkForModule(modulePath);

            // If no Source Link info found, return with error
            // This happens for native binaries, Windows PDBs, or stripped PDBs
            if (sourceLink == null)
            {
                result.Error = "No Source Link information found in PDB";
                // Only warn once per module to avoid log spam (can be thousands of frames from same module)
                lock (_warnedModulesLock)
                {
                    if (_warnedModules.Add(moduleName))
                    {
                        _logger?.LogWarning("[SourceLink] No Source Link info found for module: {Module}", modulePath);
                    }
                }
                return result;
            }

            _logger?.LogInformation("[SourceLink] Found Source Link with {Count} document patterns", sourceLink.Documents?.Count ?? 0);

            // Step 2: Try to match the source file path against document patterns
            // Source Link uses glob patterns to map local paths to repository URLs
            var rawUrl = ResolveRawUrl(sourceLink, sourceFile);

            // If no pattern matched, the source file may be from a different project
            // or the Source Link configuration was incomplete at build time
            if (rawUrl == null)
            {
                result.Error = $"Source file '{sourceFile}' not matched by any Source Link pattern";
                _logger?.LogWarning("[SourceLink] Source file not matched: {SourceFile}", sourceFile);
                // Log available patterns for debugging
                if (sourceLink.Documents != null)
                {
                    foreach (var (pattern, url) in sourceLink.Documents)
                    {
                        _logger?.LogDebug("[SourceLink]   Available pattern: {Pattern} -> {Url}", pattern, url);
                    }
                }
                return result;
            }

            // Step 3: Process the raw URL
            result.RawUrl = rawUrl;
            _logger?.LogDebug("[SourceLink] Raw URL resolved: {RawUrl}", rawUrl);

            // Detect which source control provider hosts this URL
            // This is needed to format the URL with the correct line number syntax
            result.Provider = DetectProvider(rawUrl);
            _logger?.LogDebug("[SourceLink] Provider detected: {Provider}", result.Provider);

            // Convert the raw content URL to a browsable URL with line number anchor
            // Each provider has different URL formats (e.g., GitHub uses #L123, Bitbucket uses #lines-123)
            result.Url = ConvertToBrowsableUrl(rawUrl, lineNumber, result.Provider);
            result.Resolved = true;

            _logger?.LogInformation("[SourceLink] ✓ Resolved: {SourceFile}:{Line} -> {Url}", sourceFile, lineNumber, result.Url);
        }
        catch (Exception ex)
        {
            // Capture the error but don't throw - callers expect a result object
            result.Error = $"Error resolving source link: {ex.Message}";
            _logger?.LogError(ex, "[SourceLink] Error resolving source link for {Module}:{SourceFile}", modulePath, sourceFile);
        }

        return result;
    }

    /// <summary>
    /// Resolves source locations for multiple stack frames.
    /// </summary>
    /// <param name="frames">Stack frames with module and source information.</param>
    /// <returns>Dictionary mapping frame index to resolved location.</returns>
    public Dictionary<int, SourceLocation> ResolveAll(IEnumerable<(int Index, string ModulePath, string SourceFile, int LineNumber)> frames)
    {
        var results = new Dictionary<int, SourceLocation>();

        foreach (var (index, modulePath, sourceFile, lineNumber) in frames)
        {
            if (!string.IsNullOrEmpty(modulePath) && !string.IsNullOrEmpty(sourceFile) && lineNumber > 0)
            {
                results[index] = Resolve(modulePath, sourceFile, lineNumber);
            }
        }

        return results;
    }

    /// <summary>
    /// Gets Source Link information for a module, with caching.
    /// </summary>
    /// <param name="modulePath">Path to the module.</param>
    /// <returns>Source Link info if found, null otherwise.</returns>
    public SourceLinkInfo? GetSourceLinkForModule(string modulePath)
    {
        var moduleName = GetModuleIdentifier(modulePath);
        _logger?.LogDebug("[SourceLink] GetSourceLinkForModule: {ModuleName} (path: {ModulePath})", moduleName, modulePath);

        if (_cache.TryGetValue(moduleName, out var cached))
        {
            _logger?.LogDebug("[SourceLink] Cache hit for {ModuleName}: HasSourceLink={HasSourceLink}, PdbPath={PdbPath}",
                moduleName, cached.HasSourceLink, cached.PdbPath ?? "null");
            return cached.SourceLink;
        }

        _logger?.LogDebug("[SourceLink] Cache miss for {ModuleName}, searching for PDB...", moduleName);

        var pdbPath = FindPdbFile(modulePath);

        if (pdbPath == null)
        {
            _logger?.LogWarning("[SourceLink] No PDB found for module: {ModuleName}", moduleName);
        }
        else
        {
            _logger?.LogInformation("[SourceLink] Found PDB for {ModuleName}: {PdbPath}", moduleName, pdbPath);
        }

        var sourceLink = pdbPath != null ? ExtractSourceLinkFromPdb(pdbPath) : null;

        var cacheEntry = new ModuleSourceLinkCache
        {
            ModuleName = moduleName,
            PdbPath = pdbPath,
            HasSourceLink = sourceLink != null,
            SourceLink = sourceLink
        };

        _cache[moduleName] = cacheEntry;
        _logger?.LogDebug("[SourceLink] Cached result for {ModuleName}: HasSourceLink={HasSourceLink}", moduleName, sourceLink != null);
        return sourceLink;
    }

    /// <summary>
    /// Clears the source link cache.
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Finds the PDB file for a given module.
    /// </summary>
    private string? FindPdbFile(string modulePath)
    {
        var moduleName = GetModuleIdentifier(modulePath);
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return null;
        }

        var pdbName = moduleName + ".pdb";
        _logger?.LogDebug("[SourceLink] FindPdbFile: Looking for {PdbName}", pdbName);
        _logger?.LogDebug("[SourceLink] FindPdbFile: {Count} symbol search paths configured", _symbolSearchPaths.Count);

        // Strategy 1: PDB next to module (only when modulePath is a real file path/name)
        if (LooksLikeFilePath(modulePath))
        {
            var pdbPath = Path.ChangeExtension(modulePath, ".pdb");
            _logger?.LogDebug("[SourceLink] Strategy 1 - Check next to module: {Path}", pdbPath);
            if (File.Exists(pdbPath))
            {
                _logger?.LogInformation("[SourceLink] ✓ Found PDB next to module: {Path}", pdbPath);
                return pdbPath;
            }
            _logger?.LogDebug("[SourceLink] Strategy 1 - Not found");
        }

        // Strategy 2: Search in symbol paths with case-insensitive matching
        // Symbol stores may use different casing (e.g., lowercase) than the original module name
        _logger?.LogDebug("[SourceLink] Strategy 2 - Search in {Count} symbol paths (case-insensitive)", _symbolSearchPaths.Count);
        foreach (var searchPath in _symbolSearchPaths)
        {
            _logger?.LogDebug("[SourceLink] Searching in: {SearchPath}", searchPath);

            // Search case-insensitively by enumerating all PDB files
            // This handles:
            // - Different casing (Module.pdb vs module.pdb)
            // - Symbol store structures like: modulename.pdb/{guid}/modulename.pdb
            try
            {
                var allPdbFiles = Directory.GetFiles(searchPath, "*.pdb", SearchOption.AllDirectories);
                foreach (var pdbFile in allPdbFiles)
                {
                    var fileName = Path.GetFileName(pdbFile);
                    if (string.Equals(fileName, pdbName, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.LogInformation("[SourceLink] ✓ Found PDB (case-insensitive): {Path}", pdbFile);
                        return pdbFile;
                    }
                }
                _logger?.LogDebug("[SourceLink]   Not found in {SearchPath} (searched {Count} PDB files)", searchPath, allPdbFiles.Length);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[SourceLink] Error searching in {SearchPath}", searchPath);
            }
        }

        _logger?.LogWarning("[SourceLink] ✗ PDB not found for module: {Module} (searched {Count} paths)", modulePath, _symbolSearchPaths.Count);
        return null;
    }

    /// <summary>
    /// Extracts Source Link JSON from a Portable PDB file.
    /// </summary>
    private SourceLinkInfo? ExtractSourceLinkFromPdb(string pdbPath)
    {
        _logger?.LogDebug("[SourceLink] ExtractSourceLinkFromPdb: {Path}", pdbPath);

        try
        {
            var fileInfo = new FileInfo(pdbPath);
            _logger?.LogDebug("[SourceLink] PDB file size: {Size} bytes", fileInfo.Length);

            using var stream = File.OpenRead(pdbPath);

            // Check if it's a Portable PDB by looking at the magic number
            var magic = new byte[4];
            if (stream.Read(magic, 0, 4) < 4)
            {
                _logger?.LogWarning("[SourceLink] PDB file too small to read magic number: {Path}", pdbPath);
                return null;
            }
            stream.Position = 0;

            var magicStr = System.Text.Encoding.ASCII.GetString(magic);
            _logger?.LogDebug("[SourceLink] PDB magic bytes: {Magic} (0x{Hex})", magicStr, BitConverter.ToString(magic).Replace("-", ""));

            // Portable PDB starts with "BSJB" (0x424A5342)
            if (magic[0] == 'B' && magic[1] == 'S' && magic[2] == 'J' && magic[3] == 'B')
            {
                _logger?.LogInformation("[SourceLink] Portable PDB detected: {Path}", pdbPath);
                return ExtractFromPortablePdb(stream);
            }

            // Check for Windows PDB (Microsoft C/C++ MSF 7.00)
            stream.Position = 0;
            var header = new byte[32];
            if (stream.Read(header, 0, 32) >= 32)
            {
                var headerStr = System.Text.Encoding.ASCII.GetString(header);
                if (headerStr.StartsWith("Microsoft C/C++ MSF"))
                {
                    _logger?.LogWarning("[SourceLink] Windows PDB (native) detected - Source Link not supported: {Path}", pdbPath);
                    return null;
                }
            }

            _logger?.LogWarning("[SourceLink] Unknown PDB format (not Portable PDB, not Windows PDB): {Path}", pdbPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SourceLink] Error reading PDB file: {Path}", pdbPath);
            return null;
        }
    }

    /// <summary>
    /// Extracts Source Link from a Portable PDB using System.Reflection.Metadata.
    /// </summary>
    private SourceLinkInfo? ExtractFromPortablePdb(Stream stream)
    {
        try
        {
            _logger?.LogDebug("[SourceLink] Parsing Portable PDB metadata...");

            using var peReader = MetadataReaderProvider.FromPortablePdbStream(stream, MetadataStreamOptions.LeaveOpen);
            var reader = peReader.GetMetadataReader();

            var debugInfoCount = reader.CustomDebugInformation.Count;
            _logger?.LogDebug("[SourceLink] Found {Count} custom debug information entries", debugInfoCount);

            // Look for Source Link custom debug information
            var entryIndex = 0;
            foreach (var handle in reader.CustomDebugInformation)
            {
                var info = reader.GetCustomDebugInformation(handle);
                var kind = reader.GetGuid(info.Kind);

                _logger?.LogDebug("[SourceLink] Entry {Index}: GUID={Guid}", entryIndex++, kind);

                if (kind == SourceLinkGuid)
                {
                    _logger?.LogInformation("[SourceLink] ✓ Found Source Link entry (GUID: {Guid})", SourceLinkGuid);

                    var blob = reader.GetBlobBytes(info.Value);
                    var json = System.Text.Encoding.UTF8.GetString(blob);

                    _logger?.LogInformation("[SourceLink] Source Link JSON ({Length} bytes): {Json}",
                        json.Length,
                        json.Length > 500 ? json.Substring(0, 500) + "..." : json);

                    var sourceLink = JsonSerializer.Deserialize<SourceLinkInfo>(json);

                    if (sourceLink?.Documents != null)
                    {
                        _logger?.LogInformation("[SourceLink] Parsed {Count} document mappings:", sourceLink.Documents.Count);
                        foreach (var (pattern, url) in sourceLink.Documents)
                        {
                            _logger?.LogDebug("[SourceLink]   {Pattern} -> {Url}", pattern, url);
                        }
                    }

                    return sourceLink;
                }
            }

            _logger?.LogWarning("[SourceLink] ✗ No Source Link entry found in Portable PDB (expected GUID: {Guid})", SourceLinkGuid);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SourceLink] Error parsing Portable PDB");
            return null;
        }
    }

    /// <summary>
    /// Resolves a source file path to a raw URL using Source Link document mappings.
    /// </summary>
    internal string? ResolveRawUrl(SourceLinkInfo sourceLink, string sourceFile)
    {
        // Normalize path separators
        var normalizedPath = sourceFile.Replace('\\', '/');
        _logger?.LogDebug("[SourceLink] ResolveRawUrl: sourceFile={SourceFile}, normalized={Normalized}", sourceFile, normalizedPath);

        foreach (var (pattern, urlTemplate) in sourceLink.Documents)
        {
            var normalizedPattern = pattern.Replace('\\', '/');
            _logger?.LogDebug("[SourceLink] Trying pattern: {Pattern}", normalizedPattern);

            // Handle glob patterns (e.g., "/src/*" or "C:/src/*")
            if (normalizedPattern.EndsWith("*"))
            {
                var prefix = normalizedPattern.Substring(0, normalizedPattern.Length - 1);
                _logger?.LogDebug("[SourceLink]   Glob pattern, prefix={Prefix}", prefix);

                // Check if the source file starts with the prefix
                if (normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var relativePath = normalizedPath.Substring(prefix.Length);
                    var urlBase = urlTemplate.EndsWith("*")
                        ? urlTemplate.Substring(0, urlTemplate.Length - 1)
                        : urlTemplate;

                    var result = urlBase + relativePath;
                    _logger?.LogInformation("[SourceLink] ✓ Pattern matched (prefix): {Pattern} -> {Result}", pattern, result);
                    return result;
                }
                _logger?.LogDebug("[SourceLink]   No prefix match: '{Path}' doesn't start with '{Prefix}'", normalizedPath, prefix);

                // Also try matching just the filename part for flexibility
                var trimmedPrefix = prefix.TrimStart('/');
                if (normalizedPath.Contains(trimmedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var idx = normalizedPath.IndexOf(trimmedPrefix, StringComparison.OrdinalIgnoreCase);
                    var relativePath = normalizedPath.Substring(idx + trimmedPrefix.Length);
                    var urlBase = urlTemplate.EndsWith("*")
                        ? urlTemplate.Substring(0, urlTemplate.Length - 1)
                        : urlTemplate;

                    var result = urlBase + relativePath;
                    _logger?.LogInformation("[SourceLink] ✓ Pattern matched (contains): {Pattern} -> {Result}", pattern, result);
                    return result;
                }
                _logger?.LogDebug("[SourceLink]   No contains match: '{Path}' doesn't contain '{TrimmedPrefix}'", normalizedPath, trimmedPrefix);
            }
            else
            {
                // Exact match
                _logger?.LogDebug("[SourceLink]   Exact match check: '{Path}' vs '{Pattern}'", normalizedPath, normalizedPattern);
                if (normalizedPath.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogInformation("[SourceLink] ✓ Pattern matched (exact): {Pattern} -> {Url}", pattern, urlTemplate);
                    return urlTemplate;
                }
            }
        }

        _logger?.LogWarning("[SourceLink] ✗ No pattern matched for: {SourceFile}", sourceFile);
        return null;
    }

    /// <summary>
    /// Detects the source control provider from the URL.
    /// </summary>
    internal static SourceProvider DetectProvider(string url)
    {
        var lowerUrl = url.ToLowerInvariant();

        if (lowerUrl.Contains("github.com") || lowerUrl.Contains("githubusercontent.com"))
            return SourceProvider.GitHub;

        if (lowerUrl.Contains("gitlab.com") || lowerUrl.Contains("gitlab"))
            return SourceProvider.GitLab;

        if (lowerUrl.Contains("dev.azure.com") || lowerUrl.Contains("visualstudio.com"))
            return SourceProvider.AzureDevOps;

        if (lowerUrl.Contains("bitbucket.org"))
            return SourceProvider.Bitbucket;

        return SourceProvider.Generic;
    }

    /// <summary>
    /// Converts a raw content URL to a browsable URL with line number.
    /// </summary>
    internal static string ConvertToBrowsableUrl(string rawUrl, int lineNumber, SourceProvider provider)
    {
        switch (provider)
        {
            case SourceProvider.GitHub:
                // raw.githubusercontent.com/user/repo/commit/path 
                // → github.com/user/repo/blob/commit/path#L123
                var githubMatch = Regex.Match(rawUrl, @"raw\.githubusercontent\.com/([^/]+)/([^/]+)/([^/]+)/(.+)");
                if (githubMatch.Success)
                {
                    var user = githubMatch.Groups[1].Value;
                    var repo = githubMatch.Groups[2].Value;
                    var commit = githubMatch.Groups[3].Value;
                    var path = githubMatch.Groups[4].Value;
                    if (string.Equals(user, "dotnet", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(repo, "dotnet", StringComparison.OrdinalIgnoreCase) &&
                        (path.StartsWith("src/libraries/", StringComparison.OrdinalIgnoreCase) ||
                         path.StartsWith("src/coreclr/", StringComparison.OrdinalIgnoreCase)))
                    {
                        path = $"src/runtime/{path}";
                    }
                    return $"https://github.com/{user}/{repo}/blob/{commit}/{path}#L{lineNumber}";
                }
                // Already a github.com URL
                if (rawUrl.Contains("github.com") && !rawUrl.Contains("#L"))
                {
                    // dotnet/dotnet repository layout nests runtime sources under src/runtime/.
                    if (rawUrl.Contains("github.com/dotnet/dotnet/", StringComparison.OrdinalIgnoreCase) &&
                        (rawUrl.Contains("/src/libraries/", StringComparison.OrdinalIgnoreCase) ||
                         rawUrl.Contains("/src/coreclr/", StringComparison.OrdinalIgnoreCase)) &&
                        !rawUrl.Contains("/src/runtime/", StringComparison.OrdinalIgnoreCase))
                    {
                        rawUrl = rawUrl.Replace("/src/libraries/", "/src/runtime/src/libraries/", StringComparison.OrdinalIgnoreCase)
                                       .Replace("/src/coreclr/", "/src/runtime/src/coreclr/", StringComparison.OrdinalIgnoreCase);
                    }
                    return $"{rawUrl}#L{lineNumber}";
                }
                break;

            case SourceProvider.GitLab:
                // gitlab.com/user/repo/-/raw/commit/path
                // → gitlab.com/user/repo/-/blob/commit/path#L123
                var gitlabMatch = Regex.Match(rawUrl, @"gitlab\.com/(.+?)/-/raw/([^/]+)/(.+)");
                if (gitlabMatch.Success)
                {
                    var projectPath = gitlabMatch.Groups[1].Value;
                    var commit = gitlabMatch.Groups[2].Value;
                    var path = gitlabMatch.Groups[3].Value;
                    return $"https://gitlab.com/{projectPath}/-/blob/{commit}/{path}#L{lineNumber}";
                }
                if (rawUrl.Contains("gitlab.com") && !rawUrl.Contains("#L"))
                {
                    return $"{rawUrl}#L{lineNumber}";
                }
                break;

            case SourceProvider.AzureDevOps:
                // dev.azure.com/org/project/_apis/git/repositories/repo/items?...
                // → dev.azure.com/org/project/_git/repo?path=...&line=123
                if (rawUrl.Contains("/_apis/git/"))
                {
                    var azureMatch = Regex.Match(rawUrl, @"dev\.azure\.com/([^/]+)/([^/]+)/_apis/git/repositories/([^/]+)/items\?.*path=([^&]+)");
                    if (azureMatch.Success)
                    {
                        var org = azureMatch.Groups[1].Value;
                        var project = azureMatch.Groups[2].Value;
                        var repo = azureMatch.Groups[3].Value;
                        var path = Uri.UnescapeDataString(azureMatch.Groups[4].Value);
                        return $"https://dev.azure.com/{org}/{project}/_git/{repo}?path={path}&line={lineNumber}";
                    }
                }
                break;

            case SourceProvider.Bitbucket:
                // bitbucket.org/user/repo/raw/commit/path
                // → bitbucket.org/user/repo/src/commit/path#lines-123
                var bitbucketMatch = Regex.Match(rawUrl, @"bitbucket\.org/([^/]+)/([^/]+)/raw/([^/]+)/(.+)");
                if (bitbucketMatch.Success)
                {
                    var user = bitbucketMatch.Groups[1].Value;
                    var repo = bitbucketMatch.Groups[2].Value;
                    var commit = bitbucketMatch.Groups[3].Value;
                    var path = bitbucketMatch.Groups[4].Value;
                    return $"https://bitbucket.org/{user}/{repo}/src/{commit}/{path}#lines-{lineNumber}";
                }
                break;
        }

        // Generic fallback - just append line number if possible
        if (!rawUrl.Contains("#"))
        {
            return $"{rawUrl}#L{lineNumber}";
        }

        return rawUrl;
    }

    /// <summary>
    /// Creates a short display string for a source location.
    /// </summary>
    public static string FormatShortLocation(SourceLocation location)
    {
        var fileName = Path.GetFileName(location.SourceFile);
        return $"{fileName}:{location.LineNumber}";
    }

    /// <summary>
    /// Creates a Markdown link for a source location.
    /// </summary>
    public static string FormatMarkdownLink(SourceLocation location)
    {
        if (!location.Resolved || string.IsNullOrEmpty(location.Url))
        {
            return FormatShortLocation(location);
        }

        var displayText = FormatShortLocation(location);
        return $"[{displayText}]({location.Url})";
    }

    /// <summary>
    /// Creates an HTML link for a source location.
    /// </summary>
    public static string FormatHtmlLink(SourceLocation location)
    {
        var displayText = System.Web.HttpUtility.HtmlEncode(FormatShortLocation(location));

        if (!location.Resolved || string.IsNullOrEmpty(location.Url))
        {
            return displayText;
        }

        var url = System.Web.HttpUtility.HtmlEncode(location.Url);
        return $"<a href=\"{url}\" target=\"_blank\" class=\"source-link\">📄 {displayText}</a>";
    }
}
