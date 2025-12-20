using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using DebuggerMcp.Security;

namespace DebuggerMcp.Controllers;

/// <summary>
/// Controller for managing symbol files.
/// </summary>
/// <remarks>
/// Authentication is required when API_KEY environment variable is set.
/// </remarks>
[ApiController]
[Route("api/symbols")]
[Authorize]
public class SymbolController : ControllerBase
{
    private readonly SymbolManager _symbolManager;
    private readonly ILogger<SymbolController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SymbolController"/> class.
    /// </summary>
    /// <param name="symbolManager">Symbol manager instance.</param>
    /// <param name="logger">Logger instance.</param>
    public SymbolController(SymbolManager symbolManager, ILogger<SymbolController> logger)
    {
        _symbolManager = symbolManager;
        _logger = logger;
    }

    /// <summary>
    /// Uploads a symbol file for a specific dump.
    /// </summary>
    /// <param name="file">Symbol file to upload (for example: .pdb, .so, .dylib, .debug, .dbg, .dwarf, .sym).</param>
    /// <param name="dumpId">Dump ID to associate the symbol with.</param>
    /// <returns>Upload result.</returns>
    [HttpPost("upload")]
    [ProducesResponseType(typeof(SymbolUploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadSymbol(IFormFile file, [FromForm] string dumpId)
    {
        try
        {
            if (file == null || file.Length == 0)
            {
                // Nothing to process; return early to avoid allocating streams.
                return BadRequest(new { error = "No file provided" });
            }

            // Sanitize dumpId to prevent path traversal
            string sanitizedDumpId;
            try
            {
                sanitizedDumpId = PathSanitizer.SanitizeIdentifier(dumpId, nameof(dumpId));
            }
            catch (ArgumentException ex)
            {
                // Provide specific validation error from sanitizer.
                return BadRequest(new { error = ex.Message });
            }

            // Validate symbol file content
            using var stream = file.OpenReadStream();
            var header = new byte[SymbolFileValidator.MaxHeaderSize];
            var bytesRead = await stream.ReadAsync(header.AsMemory(0, SymbolFileValidator.MaxHeaderSize));

            if (bytesRead < SymbolFileValidator.MinimumBytesNeeded)
            {
                // Avoid storing obviously truncated files.
                return BadRequest(new { error = "File is too small to be a valid symbol file." });
            }

            if (!SymbolFileValidator.IsValidSymbolHeader(header, file.FileName))
            {
                var detectedFormat = SymbolFileValidator.GetSymbolFormat(header);
                return BadRequest(new
                {
                    error = $"Invalid symbol file format for extension '{Path.GetExtension(file.FileName)}'. Expected a valid symbol file (PDB, ELF, Mach-O, etc.).",
                    detectedFormat
                });
            }

            var symbolFormat = SymbolFileValidator.GetSymbolFormat(header);

            // Reset stream and store
            stream.Position = 0;
            _ = await _symbolManager.StoreSymbolFileAsync(sanitizedDumpId, file.FileName, stream);

            return Ok(new SymbolUploadResponse
            {
                DumpId = sanitizedDumpId,
                FileName = SymbolManager.GetSafeSymbolFileNameForStorage(file.FileName),
                Size = file.Length,
                Format = symbolFormat
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading symbol for dump {DumpId}", dumpId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "An internal error occurred while uploading the symbol file." });
        }
    }

    /// <summary>
    /// Uploads a ZIP file containing multiple symbol files for a specific dump.
    /// The ZIP file is extracted preserving its directory structure.
    /// All subdirectories are automatically added to the debugger's symbol search paths.
    /// </summary>
    /// <param name="file">The ZIP file containing symbols.</param>
    /// <param name="dumpId">Dump ID to associate the symbols with.</param>
    /// <returns>Extraction result with file counts and directory paths.</returns>
    /// <remarks>
    /// <para>Use this endpoint to upload a ZIP archive containing multiple symbol files organized in directories.</para>
    /// <para>The directory structure is preserved for extracted symbol entries, and all subdirectories are added to the symbol search path.</para>
    /// <para>Only symbol-related entries are extracted; other files in the ZIP are ignored.</para>
    /// <para>Supported symbol file types inside the ZIP: .pdb, .so, .dylib, .dwarf, .sym, .debug, .dbg, .so.dbg, and DWARF files under .dSYM/Contents/Resources/DWARF/ (even without an extension).</para>
    /// <para>The server applies defensive extraction limits (entry count, total size, per-entry size, and compression ratio) and rejects suspicious archives with a 400 response.</para>
    /// </remarks>
    [HttpPost("upload-zip")]
    [ProducesResponseType(typeof(SymbolZipUploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadSymbolZip(IFormFile file, [FromForm] string dumpId)
    {
        try
        {
            if (file == null || file.Length == 0)
            {
                // Reject empty submissions to avoid extra IO.
                return BadRequest(new { error = "No file provided" });
            }

            // Validate it's a ZIP file
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (extension != ".zip")
            {
                // Only ZIP archives supported here; other formats go through single upload.
                return BadRequest(new { error = "File must be a ZIP archive (.zip extension)" });
            }

            // Sanitize dumpId to prevent path traversal
            string sanitizedDumpId;
            try
            {
                sanitizedDumpId = PathSanitizer.SanitizeIdentifier(dumpId, nameof(dumpId));
            }
            catch (ArgumentException ex)
            {
                // Sanitize failure is a client error; surface message.
                return BadRequest(new { error = ex.Message });
            }

            // Check if it's actually a valid ZIP file by reading the header
            using var stream = file.OpenReadStream();
            var header = new byte[4];
            var bytesRead = await stream.ReadAsync(header.AsMemory(0, 4));

            // ZIP files start with PK\x03\x04 (local file header) or PK\x05\x06 (empty archive)
            if (bytesRead < 4 ||
                (header[0] != 0x50 || header[1] != 0x4B || (header[2] != 0x03 && header[2] != 0x05)))
            {
                // Prevent treating arbitrary files as ZIP to avoid extraction errors.
                return BadRequest(new { error = "File does not appear to be a valid ZIP archive" });
            }

            // Reset stream position
            stream.Position = 0;

            _logger.LogInformation("[SymbolController] Extracting symbol ZIP for dump {DumpId}, size: {Size} bytes",
                sanitizedDumpId, file.Length);

            // Extract ZIP
            var result = await _symbolManager.StoreSymbolZipAsync(sanitizedDumpId, stream);

            _logger.LogInformation("[SymbolController] Extracted {FileCount} files into {DirCount} directories for dump {DumpId}",
                result.ExtractedFilesCount, result.SymbolDirectories.Count, sanitizedDumpId);

            // Get symbol files for loading info
            var symbolFiles = SymbolManager.GetSymbolFilesInDirectory(result.RootSymbolDirectory);

            return Ok(new SymbolZipUploadResponse
            {
                DumpId = sanitizedDumpId,
                ExtractedFilesCount = result.ExtractedFilesCount,
                SymbolDirectoriesCount = result.SymbolDirectories.Count,
                SymbolFilesCount = symbolFiles.Count,
                Message = $"Successfully extracted {result.ExtractedFilesCount} files. Found {symbolFiles.Count} symbol files in {result.SymbolDirectories.Count} directories.",
                SymbolDirectories = result.SymbolDirectories.Select(d => Path.GetRelativePath(result.RootSymbolDirectory, d)).ToList()
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting symbol ZIP for dump {DumpId}", dumpId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "An internal error occurred while extracting the symbol ZIP." });
        }
    }

    /// <summary>
    /// Uploads multiple symbol files for a specific dump (batch upload).
    /// </summary>
    /// <param name="files">List of symbol files to upload.</param>
    /// <param name="dumpId">Dump ID to associate the symbols with.</param>
    /// <returns>Upload result.</returns>
    [HttpPost("upload-batch")]
    [ProducesResponseType(typeof(SymbolBatchUploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadSymbolsBatch(List<IFormFile> files, [FromForm] string dumpId)
    {
        try
        {
            if (files == null || files.Count == 0)
            {
                // Avoid running validations when nothing was supplied.
                return BadRequest(new { error = "No files provided" });
            }

            // Sanitize dumpId to prevent path traversal
            string sanitizedDumpId;
            try
            {
                sanitizedDumpId = PathSanitizer.SanitizeIdentifier(dumpId, nameof(dumpId));
            }
            catch (ArgumentException ex)
            {
                // Surface sanitization issues as client errors.
                return BadRequest(new { error = ex.Message });
            }

            var fileDict = new Dictionary<string, Stream>();
            var uploadedFiles = new List<SymbolFileInfo>();
            var validationErrors = new List<string>();

            try
            {
                // Validate and open all file streams
                foreach (var file in files)
                {
                    if (file != null && file.Length > 0)
                    {
                        var stream = file.OpenReadStream();

                        // Validate symbol file content
                        var header = new byte[SymbolFileValidator.MaxHeaderSize];
                        var bytesRead = await stream.ReadAsync(header.AsMemory(0, SymbolFileValidator.MaxHeaderSize));

                        if (bytesRead < SymbolFileValidator.MinimumBytesNeeded ||
                            !SymbolFileValidator.IsValidSymbolHeader(header, file.FileName))
                        {
                            // Skip invalid files but continue processing the rest.
                            validationErrors.Add($"Invalid symbol file: {file.FileName}");
                            stream.Dispose();
                            continue;
                        }

                        stream.Position = 0;
                        fileDict[file.FileName] = stream;
                    }
                }

                if (fileDict.Count == 0)
                {
                    return BadRequest(new
                    {
                        error = "No valid symbol files provided.",
                        validationErrors
                    });
                }

                // Store all valid files
                await _symbolManager.StoreSymbolFilesAsync(sanitizedDumpId, fileDict);

                // Build response (without exposing file paths)
                foreach (var kvp in fileDict)
                {
                    var file = files.First(f => f.FileName == kvp.Key);
                    uploadedFiles.Add(new SymbolFileInfo
                    {
                        FileName = SymbolManager.GetSafeSymbolFileNameForStorage(file.FileName),
                        Size = file.Length,
                        Format = SymbolFileValidator.GetSymbolFormat(GetHeader(kvp.Value))
                    });
                }

                var response = new SymbolBatchUploadResponse
                {
                    DumpId = sanitizedDumpId,
                    FilesUploaded = uploadedFiles.Count,
                    Files = uploadedFiles
                };

                // Include validation errors if some files were skipped
                if (validationErrors.Count > 0)
                {
                    // Partial success: return OK with skipped list so client can retry just the bad ones.
                    return Ok(new
                    {
                        response.DumpId,
                        response.FilesUploaded,
                        response.Files,
                        skippedFiles = validationErrors
                    });
                }

                return Ok(response);
            }
            finally
            {
                // Clean up streams
                foreach (var stream in fileDict.Values)
                {
                    stream?.Dispose();
                }
            }
        }

        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading symbols batch for dump {DumpId}", dumpId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "An internal error occurred while uploading symbol files." });
        }
    }

    /// <summary>
    /// Gets the header bytes from a stream.
    /// </summary>
    private static byte[] GetHeader(Stream stream)
    {
        var originalPosition = stream.Position;
        stream.Position = 0;
        var header = new byte[SymbolFileValidator.MaxHeaderSize];
        var bytesRead = 0;
        while (bytesRead < header.Length)
        {
            var read = stream.Read(header, bytesRead, header.Length - bytesRead);
            if (read == 0) break; // End of stream
            bytesRead += read;
        }
        stream.Position = originalPosition;
        return header;
    }

    /// <summary>
    /// Lists all symbol files for a specific dump.
    /// </summary>
    /// <param name="dumpId">Dump ID to list symbols for.</param>
    /// <returns>List of symbol file names.</returns>
    [HttpGet("dump/{dumpId}")]
    [ProducesResponseType(typeof(SymbolListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult ListDumpSymbols(string dumpId)
    {
        try
        {
            // Sanitize dumpId to prevent path traversal
            string sanitizedDumpId;
            try
            {
                sanitizedDumpId = PathSanitizer.SanitizeIdentifier(dumpId, nameof(dumpId));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }

            var symbols = _symbolManager.ListDumpSymbols(sanitizedDumpId);

            if (symbols.Count == 0)
            {
                return NotFound(new { error = $"No symbols found for dump {sanitizedDumpId}" });
            }

            return Ok(new SymbolListResponse
            {
                DumpId = sanitizedDumpId,
                SymbolCount = symbols.Count,
                Symbols = symbols
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing symbols for dump {DumpId}", dumpId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "An internal error occurred while listing symbol files." });
        }
    }

    /// <summary>
    /// Checks if a dump has associated symbol files.
    /// </summary>
    /// <param name="dumpId">Dump ID to check.</param>
    /// <returns>Boolean indicating if symbols exist.</returns>
    [HttpGet("dump/{dumpId}/exists")]
    [ProducesResponseType(typeof(SymbolExistsResponse), StatusCodes.Status200OK)]
    public IActionResult CheckDumpSymbols(string dumpId)
    {
        try
        {
            // Sanitize dumpId to prevent path traversal
            string sanitizedDumpId;
            try
            {
                sanitizedDumpId = PathSanitizer.SanitizeIdentifier(dumpId, nameof(dumpId));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }

            var hasSymbols = _symbolManager.HasSymbols(sanitizedDumpId);

            return Ok(new SymbolExistsResponse
            {
                DumpId = sanitizedDumpId,
                HasSymbols = hasSymbols
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking symbols for dump {DumpId}", dumpId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "An internal error occurred while checking symbol files." });
        }
    }

    /// <summary>
    /// Deletes all symbol files for a specific dump.
    /// </summary>
    /// <param name="dumpId">Dump ID to delete symbols for.</param>
    /// <returns>Deletion result.</returns>
    [HttpDelete("dump/{dumpId}")]
    [ProducesResponseType(typeof(SymbolDeleteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DeleteDumpSymbols(string dumpId)
    {
        try
        {
            // Sanitize dumpId to prevent path traversal
            string sanitizedDumpId;
            try
            {
                sanitizedDumpId = PathSanitizer.SanitizeIdentifier(dumpId, nameof(dumpId));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }

            if (!_symbolManager.HasSymbols(sanitizedDumpId))
            {
                return NotFound(new { error = $"No symbols found for dump {sanitizedDumpId}" });
            }

            _symbolManager.DeleteDumpSymbols(sanitizedDumpId);

            return Ok(new SymbolDeleteResponse
            {
                DumpId = sanitizedDumpId,
                Message = "Symbols deleted successfully"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting symbols for dump {DumpId}", dumpId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "An internal error occurred while deleting symbol files." });
        }
    }

    /// <summary>
    /// Gets information about available symbol servers.
    /// </summary>
    /// <returns>List of common symbol servers.</returns>
    [HttpGet("servers")]
    [ProducesResponseType(typeof(SymbolServersResponse), StatusCodes.Status200OK)]
    public IActionResult GetSymbolServers()
    {
        return Ok(new SymbolServersResponse
        {
            Servers = new[]
            {
                new SymbolServerInfo
                {
                    Name = "Microsoft Symbol Server",
                    Url = SymbolManager.MicrosoftSymbolServer,
                    Description = "Public symbols for Windows and Microsoft products"
                },
                new SymbolServerInfo
                {
                    Name = "NuGet Symbol Server",
                    Url = SymbolManager.NuGetSymbolServer,
                    Description = "Symbols for NuGet packages"
                }
            }
        });
    }
}

/// <summary>
/// Response model for symbol upload operation.
/// </summary>
/// <example>
/// {
///   "dumpId": "abc123-456def-789ghi",
///   "fileName": "MyApp.pdb",
///   "size": 1048576,
///   "format": "Windows PDB (MSF 7.0)"
/// }
/// </example>
public class SymbolUploadResponse
{
    /// <summary>
    /// Gets or sets the dump ID the symbol is associated with.
    /// </summary>
    /// <example>abc123-456def-789ghi</example>
    public string DumpId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the uploaded file name.
    /// </summary>
    /// <example>MyApp.pdb</example>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    /// <example>1048576</example>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the detected symbol file format.
    /// </summary>
    /// <example>Windows PDB (MSF 7.0)</example>
    public string? Format { get; set; }
}

/// <summary>
/// Response model for batch symbol upload operation.
/// </summary>
/// <example>
/// {
///   "dumpId": "abc123-456def-789ghi",
///   "filesUploaded": 3,
///   "files": [
///     { "fileName": "MyApp.pdb", "size": 1048576 },
///     { "fileName": "MyLib.pdb", "size": 524288 }
///   ]
/// }
/// </example>
public class SymbolBatchUploadResponse
{
    /// <summary>
    /// Gets or sets the dump ID the symbols are associated with.
    /// </summary>
    /// <example>abc123-456def-789ghi</example>
    public string DumpId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of files uploaded.
    /// </summary>
    /// <example>3</example>
    public int FilesUploaded { get; set; }

    /// <summary>
    /// Gets or sets the list of uploaded files.
    /// </summary>
    public List<SymbolFileInfo> Files { get; set; } = new();
}

/// <summary>
/// Information about an uploaded symbol file.
/// </summary>
public class SymbolFileInfo
{
    /// <summary>
    /// Gets or sets the file name.
    /// </summary>
    /// <example>MyApp.pdb</example>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    /// <example>1048576</example>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the detected symbol file format.
    /// </summary>
    /// <example>Windows PDB (MSF 7.0)</example>
    public string? Format { get; set; }
}

/// <summary>
/// Response model for listing dump symbols.
/// </summary>
/// <example>
/// {
///   "dumpId": "abc123-456def-789ghi",
///   "symbolCount": 2,
///   "symbols": ["MyApp.pdb", "MyLib.pdb"]
/// }
/// </example>
public class SymbolListResponse
{
    /// <summary>
    /// Gets or sets the dump ID.
    /// </summary>
    /// <example>abc123-456def-789ghi</example>
    public string DumpId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of symbol files.
    /// </summary>
    /// <example>2</example>
    public int SymbolCount { get; set; }

    /// <summary>
    /// Gets or sets the list of symbol file names.
    /// </summary>
    public List<string> Symbols { get; set; } = new();
}

/// <summary>
/// Response model for checking if symbols exist.
/// </summary>
/// <example>
/// {
///   "dumpId": "abc123-456def-789ghi",
///   "hasSymbols": true
/// }
/// </example>
public class SymbolExistsResponse
{
    /// <summary>
    /// Gets or sets the dump ID.
    /// </summary>
    /// <example>abc123-456def-789ghi</example>
    public string DumpId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether symbols exist for the dump.
    /// </summary>
    /// <example>true</example>
    public bool HasSymbols { get; set; }
}

/// <summary>
/// Response model for symbol deletion.
/// </summary>
/// <example>
/// {
///   "dumpId": "abc123-456def-789ghi",
///   "message": "Symbols deleted successfully"
/// }
/// </example>
public class SymbolDeleteResponse
{
    /// <summary>
    /// Gets or sets the dump ID.
    /// </summary>
    /// <example>abc123-456def-789ghi</example>
    public string DumpId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the result message.
    /// </summary>
    /// <example>Symbols deleted successfully</example>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response model for symbol servers list.
/// </summary>
public class SymbolServersResponse
{
    /// <summary>
    /// Gets or sets the list of available symbol servers.
    /// </summary>
    public SymbolServerInfo[] Servers { get; set; } = Array.Empty<SymbolServerInfo>();
}

/// <summary>
/// Information about a symbol server.
/// </summary>
public class SymbolServerInfo
{
    /// <summary>
    /// Gets or sets the server name.
    /// </summary>
    /// <example>Microsoft Symbol Server</example>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the server URL.
    /// </summary>
    /// <example>https://msdl.microsoft.com/download/symbols</example>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the server description.
    /// </summary>
    /// <example>Public symbols for Windows and Microsoft products</example>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Response model for symbol ZIP upload operation.
/// </summary>
public class SymbolZipUploadResponse
{
    /// <summary>
    /// Gets or sets the dump ID the symbols are associated with.
    /// </summary>
    public string DumpId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of files extracted.
    /// </summary>
    public int ExtractedFilesCount { get; set; }

    /// <summary>
    /// Gets or sets the number of symbol directories found.
    /// </summary>
    public int SymbolDirectoriesCount { get; set; }

    /// <summary>
    /// Gets or sets the number of symbol files found (.dbg, .pdb, etc.).
    /// </summary>
    public int SymbolFilesCount { get; set; }

    /// <summary>
    /// Gets or sets the result message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of symbol directories (relative paths).
    /// </summary>
    public List<string> SymbolDirectories { get; set; } = new();
}

/// <summary>
/// Result of extracting a symbol ZIP file (internal use).
/// </summary>
public class SymbolZipExtractionResult
{
    /// <summary>
    /// Gets or sets the dump ID the symbols are associated with.
    /// </summary>
    public string DumpId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of files extracted.
    /// </summary>
    public int ExtractedFilesCount { get; set; }

    /// <summary>
    /// Gets or sets the list of extracted file paths (relative to root).
    /// </summary>
    public List<string> ExtractedFiles { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of directories containing symbols.
    /// </summary>
    public List<string> SymbolDirectories { get; set; } = new();

    /// <summary>
    /// Gets or sets the root symbol directory path.
    /// </summary>
    public string RootSymbolDirectory { get; set; } = string.Empty;
}
