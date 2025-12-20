using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DebuggerMcp.Configuration;
using DebuggerMcp.Controllers;

namespace DebuggerMcp;

/// <summary>
/// Manages symbol files and symbol server paths for debugging sessions.
/// </summary>
/// <remarks>
/// This class handles:
/// - Storage of uploaded symbol files organized by dumpId (.pdb, .so, .dylib, .dwarf)
/// - Management of symbol server paths (Microsoft, custom servers)
/// - Automatic symbol path configuration when opening dumps
/// - Symbol file validation and organization
/// 
/// Symbols are stored in the dump directory as .symbols_{dumpId}/ to:
/// - Keep symbols isolated per dump
/// - Share symbols with dotnet-symbol downloads
/// - Automatically clean up when dump is deleted
/// </remarks>
public class SymbolManager
{
    /// <summary>
    /// Gets the default base directory for storing dump files.
    /// Symbols are stored next to dumps in .symbols_{dumpId}/ folders.
    /// </summary>
    /// <returns>Platform-appropriate default dump storage path from <see cref="EnvironmentConfig"/>.</returns>
    private static string GetDefaultDumpStoragePath() => EnvironmentConfig.GetDumpStoragePath();

    /// <summary>
    /// Gets the default base directory for symbol server cache storage.
    /// </summary>
    /// <returns>Platform-appropriate default symbol cache path from <see cref="EnvironmentConfig"/>.</returns>
    private static string GetDefaultSymbolCachePath() => EnvironmentConfig.GetSymbolStoragePath();

    /// <summary>
    /// Maximum size for a symbol file (500 MB).
    /// </summary>
    private const long MaxSymbolFileSize = 500 * 1024 * 1024;

    private const int MaxSymbolZipEntries = 25_000;
    private const long MaxSymbolZipTotalUncompressedBytes = 2L * 1024 * 1024 * 1024; // 2 GiB
    private const long MaxSymbolZipSingleEntryBytes = 512L * 1024 * 1024; // 512 MiB
    private const int MaxSymbolZipPathLength = 1024;
    private const long MaxSymbolZipCompressionRatioThresholdBytes = 10L * 1024 * 1024; // 10 MiB
    private const double MaxSymbolZipCompressionRatio = 200.0;

    /// <summary>
    /// Microsoft public symbol server URL.
    /// </summary>
    public const string MicrosoftSymbolServer = "https://msdl.microsoft.com/download/symbols";

    /// <summary>
    /// NuGet symbol server URL.
    /// </summary>
    public const string NuGetSymbolServer = "https://symbols.nuget.org/download/symbols";



    /// <summary>
    /// Thread-safe dictionary mapping dumpId to symbol directory path.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _dumpSymbolDirectories = new();

    /// <summary>
    /// Thread-safe dictionary mapping sessionId to configured symbol paths.
    /// </summary>
    private readonly ConcurrentDictionary<string, List<string>> _sessionSymbolPaths = new();

    /// <summary>
    /// Base directory for caching symbol server downloads.
    /// </summary>
    private readonly string _symbolCacheBasePath;

    /// <summary>
    /// Base directory for storing dump files.
    /// Symbols are stored as .symbols_{dumpId}/ next to dumps.
    /// </summary>
    private readonly string _dumpStorageBasePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="SymbolManager"/> class.
    /// </summary>
    /// <param name="symbolCacheBasePath">Optional base path for symbol cache storage. Uses platform-appropriate default if not specified.</param>
    /// <param name="dumpStorageBasePath">Optional base path for dump storage. Used to locate .symbols_ folders.</param>
    public SymbolManager(string? symbolCacheBasePath = null, string? dumpStorageBasePath = null)
    {
        _symbolCacheBasePath = symbolCacheBasePath ?? GetDefaultSymbolCachePath();
        _dumpStorageBasePath = dumpStorageBasePath ?? GetDefaultDumpStoragePath();

        if (!Directory.Exists(_symbolCacheBasePath))
        {
            Directory.CreateDirectory(_symbolCacheBasePath);
        }

        if (!Directory.Exists(_dumpStorageBasePath))
        {
            Directory.CreateDirectory(_dumpStorageBasePath);
        }
    }



    /// <summary>
    /// Stores an uploaded symbol file for a specific dump.
    /// </summary>
    /// <param name="dumpId">Dump ID to associate the symbol file with.</param>
    /// <param name="fileName">Original file name of the symbol file.</param>
    /// <param name="fileStream">Stream containing the symbol file data.</param>
    /// <returns>The file path where the symbol was stored.</returns>
    /// <exception cref="ArgumentException">Thrown when parameters are invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when file size exceeds limit.</exception>
    public async Task<string> StoreSymbolFileAsync(string dumpId, string fileName, Stream fileStream)
    {
        // Validate parameters
        if (string.IsNullOrWhiteSpace(dumpId))
        {
            throw new ArgumentException("Dump ID cannot be null or empty.", nameof(dumpId));
        }

        fileName = NormalizeSymbolFileName(fileName);

        if (fileStream == null || !fileStream.CanRead)
        {
            throw new ArgumentException("File stream must be readable.", nameof(fileStream));
        }

        // Validate file extension
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!IsValidSymbolFileExtension(extension))
        {
            throw new ArgumentException($"Invalid symbol file extension: {extension}. Supported: .pdb, .so, .dylib, .dwarf, .sym, .debug, .dbg, .dsym", nameof(fileName));
        }

        // Check file size
        if (fileStream.Length > MaxSymbolFileSize)
        {
            throw new InvalidOperationException($"Symbol file size ({fileStream.Length} bytes) exceeds maximum allowed size ({MaxSymbolFileSize} bytes).");
        }

        // Create dump-specific symbol directory
        var dumpSymbolDir = GetOrCreateDumpSymbolDirectory(dumpId);

        // Store file by file name only (path components stripped to prevent traversal).
        var storagePath = Path.Combine(dumpSymbolDir, fileName);

        // Copy stream to file
        using (var fileStreamOut = File.Create(storagePath))
        {
            await fileStream.CopyToAsync(fileStreamOut);
        }

        return storagePath;
    }

    /// <summary>
    /// Stores multiple symbol files for a specific dump (batch upload).
    /// </summary>
    /// <param name="dumpId">Dump ID to associate the symbol files with.</param>
    /// <param name="files">Dictionary of fileName -> fileStream pairs.</param>
    /// <returns>List of file paths where symbols were stored.</returns>
    public async Task<List<string>> StoreSymbolFilesAsync(string dumpId, Dictionary<string, Stream> files)
    {
        if (string.IsNullOrWhiteSpace(dumpId))
        {
            throw new ArgumentException("Dump ID cannot be null or empty.", nameof(dumpId));
        }

        if (files == null || files.Count == 0)
        {
            throw new ArgumentException("Files dictionary cannot be null or empty.", nameof(files));
        }

        var storedPaths = new List<string>();

        foreach (var (fileName, fileStream) in files)
        {
            var path = await StoreSymbolFileAsync(dumpId, fileName, fileStream);
            storedPaths.Add(path);
        }

        return storedPaths;
    }

    /// <summary>
    /// Normalizes an uploaded symbol file name to prevent path traversal and invalid filesystem names.
    /// </summary>
    /// <param name="fileName">The uploaded file name.</param>
    /// <returns>A safe file name suitable for <see cref="Path.Combine(string,string)"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when the file name is empty or not a valid file name.</exception>
    private static string NormalizeSymbolFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("File name cannot be null or empty.", nameof(fileName));
        }

        // Normalize separators so we treat Windows-style paths as paths even on Unix.
        var normalized = fileName.Trim().Replace('\\', '/');
        var name = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            throw new ArgumentException("File name is not a valid file name.", nameof(fileName));
        }

        // Defense-in-depth: disallow any remaining directory separators.
        if (name.Contains('/') || name.Contains('\\'))
        {
            throw new ArgumentException("File name is not a valid file name.", nameof(fileName));
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("File name is not a valid file name.", nameof(fileName));
        }

        return name;
    }

    /// <summary>
    /// Stores a ZIP file containing multiple symbol files for a specific dump.
    /// The ZIP is extracted preserving its directory structure.
    /// </summary>
    /// <param name="dumpId">Dump ID to associate the symbols with.</param>
    /// <param name="zipStream">Stream containing the ZIP file data.</param>
    /// <returns>Result containing extracted files count and directory paths.</returns>
    public async Task<SymbolZipExtractionResult> StoreSymbolZipAsync(string dumpId, Stream zipStream)
    {
        if (string.IsNullOrWhiteSpace(dumpId))
        {
            throw new ArgumentException("Dump ID cannot be null or empty.", nameof(dumpId));
        }

        if (zipStream == null || !zipStream.CanRead)
        {
            throw new ArgumentException("ZIP stream must be readable.", nameof(zipStream));
        }

        // Create dump-specific symbol directory
        var dumpSymbolDir = GetOrCreateDumpSymbolDirectory(dumpId);
        var extractedFiles = new List<string>();
        var extractedDirs = new HashSet<string>();

        using var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read);

        var entryCount = 0;
        long totalUncompressed = 0;
        var baseDir = Path.GetFullPath(dumpSymbolDir);
        if (!baseDir.EndsWith(Path.DirectorySeparatorChar))
        {
            baseDir += Path.DirectorySeparatorChar;
        }
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var entry in archive.Entries)
        {
            // Skip directory entries (they have empty names or end with /)
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            entryCount++;
            if (entryCount > MaxSymbolZipEntries)
            {
                throw new InvalidOperationException($"ZIP contains too many entries ({entryCount}). Max allowed: {MaxSymbolZipEntries}.");
            }

            // Sanitize the entry path to prevent path traversal and zip bombs via pathological paths.
            var relativePath = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > MaxSymbolZipPathLength)
            {
                continue;
            }

            // Skip macOS metadata folders (__MACOSX contains resource forks and extended attributes)
            if (relativePath.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
                relativePath.Contains("/__MACOSX/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Skip common junk
            if (relativePath.EndsWith("/.DS_Store", StringComparison.OrdinalIgnoreCase) ||
                relativePath.EndsWith("/Thumbs.db", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Reject rooted paths and any dot-segments (ZipSlip defense-in-depth).
            if (relativePath.StartsWith("/", StringComparison.Ordinal) || relativePath.Contains('\0'))
            {
                continue;
            }

            var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
            {
                continue;
            }

            // Only extract symbol-related entries to reduce risk of storing arbitrary content.
            if (!ShouldExtractSymbolZipEntry(relativePath))
            {
                continue;
            }

            // Enforce conservative extraction limits to mitigate zip bombs.
            if (entry.Length < 0 || entry.Length > MaxSymbolZipSingleEntryBytes)
            {
                throw new InvalidOperationException($"ZIP entry '{entry.FullName}' is too large to extract ({entry.Length} bytes).");
            }

            totalUncompressed += entry.Length;
            if (totalUncompressed > MaxSymbolZipTotalUncompressedBytes)
            {
                throw new InvalidOperationException($"ZIP expands to too much data ({totalUncompressed} bytes). Max allowed: {MaxSymbolZipTotalUncompressedBytes} bytes.");
            }

            if (entry.Length >= MaxSymbolZipCompressionRatioThresholdBytes &&
                entry.CompressedLength > 0 &&
                (entry.Length / (double)entry.CompressedLength) > MaxSymbolZipCompressionRatio)
            {
                throw new InvalidOperationException($"ZIP entry '{entry.FullName}' appears highly-compressed (ratio {(entry.Length / (double)entry.CompressedLength):N1}); refusing to extract.");
            }

            // Build the full destination path and ensure it stays within the dump symbols directory.
            var destPath = Path.GetFullPath(Path.Combine(dumpSymbolDir, relativePath));
            if (!destPath.StartsWith(baseDir, pathComparison))
            {
                continue;
            }

            var destDir = Path.GetDirectoryName(destPath);

            // Ensure destination directory exists
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            // Track the directory for symbol paths
            if (!string.IsNullOrEmpty(destDir))
            {
                extractedDirs.Add(destDir);
            }

            // Extract the file
            using var entryStream = entry.Open();
            using var fileStream = File.Create(destPath);
            await entryStream.CopyToAsync(fileStream);

            extractedFiles.Add(relativePath);
        }

        // Also track the root symbol directory
        extractedDirs.Add(dumpSymbolDir);

        return new SymbolZipExtractionResult
        {
            DumpId = dumpId,
            ExtractedFilesCount = extractedFiles.Count,
            ExtractedFiles = extractedFiles,
            SymbolDirectories = extractedDirs.OrderBy(d => d).ToList(),
            RootSymbolDirectory = dumpSymbolDir
        };
    }

    /// <summary>
    /// Returns whether a ZIP entry should be extracted for symbol loading.
    /// </summary>
    /// <param name="relativePath">The entry path within the ZIP (relative, normalized).</param>
    /// <returns><see langword="true"/> when the entry looks like a symbol file; otherwise <see langword="false"/>.</returns>
    private static bool ShouldExtractSymbolZipEntry(string relativePath)
    {
        // Keep original path shape for dSYM bundle detection.
        var normalized = relativePath.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        // Allow DWARF files inside .dSYM bundles even if they have no extension.
        if (normalized.Contains(".dSYM/Contents/Resources/DWARF/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Compound debug symbols (e.g., libfoo.so.dbg).
        if (fileName.EndsWith(".so.dbg", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext))
        {
            return false;
        }

        // Symbol-related extensions (documented by the API).
        return ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".dwarf", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".sym", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".debug", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".dbg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".dsym", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets all symbol files from a directory and its subdirectories.
    /// </summary>
    /// <param name="directory">The directory to search.</param>
    /// <param name="lldbOnly">If true, returns only files that LLDB can load (.dbg, .debug). If false, includes all symbol types.</param>
    /// <returns>List of full paths to symbol files.</returns>
    public static List<string> GetSymbolFilesInDirectory(string directory, bool lldbOnly = false)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return new List<string>();

        // For LLDB, only .dbg and .debug files can be loaded with "target symbols add"
        // PDB files are Windows-specific and not supported by LLDB
        var symbolExtensions = lldbOnly
            ? new[] { ".dbg", ".debug" }
            : new[] { ".dbg", ".debug", ".pdb", ".sym", ".dwarf" };

        var result = new List<string>();

        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            // Check for extensions like .dbg, .debug, or compound like .so.dbg
            if (symbolExtensions.Contains(ext) || file.EndsWith(".so.dbg", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(file);
            }
        }

        return result;
    }

    /// <summary>
    /// Gets all subdirectories in a directory, including the directory itself.
    /// </summary>
    /// <param name="directory">The root directory.</param>
    /// <returns>List of all directories.</returns>
    public static List<string> GetAllSubdirectories(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return new List<string>();

        var result = new List<string> { directory };
        result.AddRange(Directory.GetDirectories(directory, "*", SearchOption.AllDirectories));
        return result;
    }

    /// <summary>
    /// Gets the symbol directory path for a dump ID.
    /// </summary>
    /// <param name="dumpId">The dump ID to look up.</param>
    /// <returns>The directory path containing symbols for the dump, or null if not found.</returns>
    /// <remarks>
    /// Looks for symbols in the following locations (in order):
    /// 1. {dumpStoragePath}/{userId}/.symbols_{dumpId}/ - Same location as dotnet-symbol downloads
    /// 2. {dumpStoragePath}/.symbols_{dumpId}/ - Root-level fallback when user directory is unknown
    /// </remarks>
    public string? GetDumpSymbolDirectory(string dumpId)
    {
        if (string.IsNullOrWhiteSpace(dumpId))
        {
            return null;
        }

        // Remove any file extension from dumpId (in case it was passed with .dmp)
        var cleanDumpId = Path.GetFileNameWithoutExtension(dumpId);
        if (string.IsNullOrWhiteSpace(cleanDumpId))
        {
            cleanDumpId = dumpId;
        }

        // 1. Check in user subdirectories: {dumpStoragePath}/{userId}/.symbols_{dumpId}/
        // Dumps are always stored per-user, so symbols are next to the dump file
        if (Directory.Exists(_dumpStorageBasePath))
        {
            foreach (var userDir in Directory.GetDirectories(_dumpStorageBasePath))
            {
                var symbolDirInUserDir = Path.Combine(userDir, $".symbols_{cleanDumpId}");
                if (Directory.Exists(symbolDirInUserDir) && HasSymbolFiles(symbolDirInUserDir))
                {
                    // Prefer per-user symbols co-located with the dump for isolation.
                    return symbolDirInUserDir;
                }
            }
        }

        // 2. Root-level symbols when user directory is unknown
        var rootSymbolDir = Path.Combine(_dumpStorageBasePath, $".symbols_{cleanDumpId}");
        if (Directory.Exists(rootSymbolDir) && HasSymbolFiles(rootSymbolDir))
        {
            // Fallback when the dump owner directory cannot be resolved.
            return rootSymbolDir;
        }

        return null;
    }

    /// <summary>
    /// Checks if a directory contains any symbol files.
    /// </summary>
    private static bool HasSymbolFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return false;

        // Check for any files (symbols can have various extensions)
        return Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length > 0;
    }

    /// <summary>
    /// Checks if a dump has associated symbol files.
    /// </summary>
    /// <param name="dumpId">The dump ID to check.</param>
    /// <returns>True if the dump has symbol files, false otherwise.</returns>
    public bool HasSymbols(string dumpId)
    {
        return GetDumpSymbolDirectory(dumpId) != null;
    }

    /// <summary>
    /// Lists all symbol files for a specific dump, including files in subdirectories.
    /// Returns relative paths from the symbol directory root.
    /// </summary>
    /// <param name="dumpId">The dump ID to list symbols for.</param>
    /// <returns>List of symbol file relative paths.</returns>
    public List<string> ListDumpSymbols(string dumpId)
    {
        var symbolDir = GetDumpSymbolDirectory(dumpId);
        if (symbolDir == null)
        {
            return new List<string>();
        }

        // Get all files recursively and return relative paths
        return Directory.GetFiles(symbolDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(symbolDir, f))
            .OrderBy(f => f)
            .ToList();
    }

    /// <summary>
    /// Deletes all symbol files for a specific dump.
    /// </summary>
    /// <param name="dumpId">The dump ID to delete symbols for.</param>
    public void DeleteDumpSymbols(string dumpId)
    {
        if (string.IsNullOrWhiteSpace(dumpId))
        {
            return;
        }

        var cleanDumpId = Path.GetFileNameWithoutExtension(dumpId);
        if (string.IsNullOrWhiteSpace(cleanDumpId))
        {
            cleanDumpId = dumpId;
        }

        // Find and delete all symbol directories for this dump
        foreach (var dir in GetAllDumpSymbolDirectories(cleanDumpId))
        {
            if (Directory.Exists(dir))
            {
                // Remove all symbol folders for this dump to avoid stale symbol usage.
                Directory.Delete(dir, true);
            }
        }

        _dumpSymbolDirectories.TryRemove(dumpId, out _);
        _dumpSymbolDirectories.TryRemove(cleanDumpId, out _);
    }

    /// <summary>
    /// Configures symbol paths for a session, including dump-specific symbols and remote servers.
    /// </summary>
    /// <param name="sessionId">Session ID to configure symbols for.</param>
    /// <param name="dumpId">Dump ID to include symbols from (optional).</param>
    /// <param name="additionalPaths">Additional symbol server URLs or paths (optional).</param>
    /// <param name="includeMicrosoftSymbols">Whether to include Microsoft Symbol Server (default: true).</param>
    public void ConfigureSessionSymbolPaths(string sessionId, string? dumpId = null, string? additionalPaths = null, bool includeMicrosoftSymbols = true)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session ID cannot be null or empty.", nameof(sessionId));
        }

        var paths = new List<string>();

        // Add Microsoft Symbol Server if requested
        if (includeMicrosoftSymbols)
        {
            paths.Add(MicrosoftSymbolServer);
        }

        // Add all dump-specific symbol directories that exist
        if (!string.IsNullOrWhiteSpace(dumpId))
        {
            var allSymbolDirs = GetAllDumpSymbolDirectories(dumpId);
            paths.AddRange(allSymbolDirs);
        }

        // Add additional paths if provided
        if (!string.IsNullOrWhiteSpace(additionalPaths))
        {
            var additionalPathList = additionalPaths.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p));

            paths.AddRange(additionalPathList);
        }

        // Store paths for session
        _sessionSymbolPaths[sessionId] = paths;
    }

    /// <summary>
    /// Gets ALL symbol directories for a dump ID.
    /// </summary>
    /// <param name="dumpId">The dump ID to look up.</param>
    /// <returns>List of all symbol directories that exist for the dump.</returns>
    private List<string> GetAllDumpSymbolDirectories(string dumpId)
    {
        var directories = new List<string>();

        if (string.IsNullOrWhiteSpace(dumpId))
        {
            return directories;
        }

        // Remove any file extension from dumpId
        var cleanDumpId = Path.GetFileNameWithoutExtension(dumpId);
        if (string.IsNullOrWhiteSpace(cleanDumpId))
        {
            cleanDumpId = dumpId;
        }

        // 1. Check in user subdirectories: {dumpStoragePath}/{userId}/.symbols_{dumpId}/
        // This is where dotnet-symbol downloads symbols
        if (Directory.Exists(_dumpStorageBasePath))
        {
            foreach (var userDir in Directory.GetDirectories(_dumpStorageBasePath))
            {
                var symbolDirInUserDir = Path.Combine(userDir, $".symbols_{cleanDumpId}");
                if (Directory.Exists(symbolDirInUserDir) && HasSymbolFiles(symbolDirInUserDir))
                {
                    // First preference: symbols stored alongside the dump in the user folder.
                    directories.Add(symbolDirInUserDir);
                }
            }
        }

        // 2. Root-level symbols when user directory is unknown
        var rootSymbolDir = Path.Combine(_dumpStorageBasePath, $".symbols_{cleanDumpId}");
        if (Directory.Exists(rootSymbolDir) && HasSymbolFiles(rootSymbolDir))
        {
            // Fallback when we can't resolve a user-scoped path (e.g., uploaded without user context).
            directories.Add(rootSymbolDir);
        }

        return directories;
    }

    /// <summary>
    /// Gets the configured symbol paths for a session.
    /// </summary>
    /// <param name="sessionId">Session ID to get paths for.</param>
    /// <returns>List of symbol paths, or empty list if not configured.</returns>
    public List<string> GetSessionSymbolPaths(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new List<string>();
        }

        return _sessionSymbolPaths.TryGetValue(sessionId, out var paths) ? paths : new List<string>();
    }

    /// <summary>
    /// Builds a WinDbg-compatible symbol path string for a session.
    /// </summary>
    /// <param name="sessionId">Session ID to build path for.</param>
    /// <param name="includeLocalCache">Whether to include local cache directory (default: true).</param>
    /// <returns>WinDbg symbol path string (e.g., "srv*cache*https://...; C:\symbols").</returns>
    public string BuildWinDbgSymbolPath(string sessionId, bool includeLocalCache = true)
    {
        var paths = GetSessionSymbolPaths(sessionId);
        if (paths.Count == 0)
        {
            return string.Empty;
        }

        var pathParts = new List<string>();

        // Add cache directive if requested
        if (includeLocalCache)
        {
            var cacheDir = Path.Combine(_symbolCacheBasePath, "cache");
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }

            // Add remote symbol servers with cache
            var remoteServers = paths.Where(p => p.StartsWith("http://") || p.StartsWith("https://"));
            foreach (var server in remoteServers)
            {
                pathParts.Add($"srv*{cacheDir}*{server}");
            }
        }
        else
        {
            // Add remote servers without cache
            var remoteServers = paths.Where(p => p.StartsWith("http://") || p.StartsWith("https://"));
            pathParts.AddRange(remoteServers.Select(s => $"srv*{s}"));
        }

        // Add local directories
        var localDirs = paths.Where(p => !p.StartsWith("http://") && !p.StartsWith("https://"));
        pathParts.AddRange(localDirs);

        return string.Join(";", pathParts);
    }

    /// <summary>
    /// Builds an LLDB-compatible symbol path string for a session.
    /// </summary>
    /// <param name="sessionId">Session ID to build path for.</param>
    /// <returns>LLDB symbol path string (space-separated local directories).</returns>
    /// <remarks>
    /// LLDB does not support remote symbol servers, so only local directories are included.
    /// </remarks>
    public string BuildLldbSymbolPath(string sessionId)
    {
        var paths = GetSessionSymbolPaths(sessionId);
        if (paths.Count == 0)
        {
            return string.Empty;
        }

        // LLDB only supports local directories, filter out URLs
        var localDirs = paths.Where(p => !p.StartsWith("http://") && !p.StartsWith("https://"));

        return string.Join(" ", localDirs);
    }

    /// <summary>
    /// Clears symbol path configuration for a session.
    /// </summary>
    /// <param name="sessionId">Session ID to clear paths for.</param>
    public void ClearSessionSymbolPaths(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _sessionSymbolPaths.TryRemove(sessionId, out _);
        }
    }



    /// <summary>
    /// Gets or creates the symbol directory for a dump.
    /// </summary>
    /// <param name="dumpId">Dump ID to get directory for.</param>
    /// <returns>Path to the dump's symbol directory.</returns>
    private string GetOrCreateDumpSymbolDirectory(string dumpId)
    {
        return _dumpSymbolDirectories.GetOrAdd(dumpId, id =>
        {
            // Remove any file extension from dumpId
            var cleanDumpId = Path.GetFileNameWithoutExtension(id);
            if (string.IsNullOrWhiteSpace(cleanDumpId))
            {
                cleanDumpId = id;
            }

            // Try to find the dump file and create symbols directory next to it
            var dumpFilePath = FindDumpFile(cleanDumpId);
            if (dumpFilePath != null)
            {
                var dumpDir = Path.GetDirectoryName(dumpFilePath);
                if (dumpDir != null)
                {
                    var symbolDir = Path.Combine(dumpDir, $".symbols_{cleanDumpId}");
                    if (!Directory.Exists(symbolDir))
                    {
                        Directory.CreateDirectory(symbolDir);
                    }
                    return symbolDir;
                }
            }

            // Fallback to root-level symbols directory when user path is unknown
            var rootSymbolDir = Path.Combine(_dumpStorageBasePath, $".symbols_{cleanDumpId}");
            if (!Directory.Exists(rootSymbolDir))
            {
                Directory.CreateDirectory(rootSymbolDir);
            }
            return rootSymbolDir;
        });
    }

    /// <summary>
    /// Finds the dump file path for a given dump ID.
    /// </summary>
    /// <param name="dumpId">The dump ID to find.</param>
    /// <returns>The full path to the dump file, or null if not found.</returns>
    private string? FindDumpFile(string dumpId)
    {
        if (!Directory.Exists(_dumpStorageBasePath))
            return null;

        // Search for {dumpId}.dmp in user subdirectories
        // Dumps are always stored per-user: {dumpStoragePath}/{userId}/{dumpId}.dmp
        var dumpFileName = $"{dumpId}.dmp";

        foreach (var userDir in Directory.GetDirectories(_dumpStorageBasePath))
        {
            var userPath = Path.Combine(userDir, dumpFileName);
            if (File.Exists(userPath))
                return userPath;
        }

        return null;
    }

    /// <summary>
    /// Validates if a file extension is a valid symbol file extension.
    /// </summary>
    /// <param name="extension">File extension to validate (including the dot).</param>
    /// <returns>True if valid, false otherwise.</returns>
    private static bool IsValidSymbolFileExtension(string extension)
    {
        var validExtensions = new[] { ".pdb", ".so", ".dylib", ".dwarf", ".sym", ".debug", ".dbg", ".dsym" };
        return validExtensions.Contains(extension.ToLowerInvariant());
    }

}
