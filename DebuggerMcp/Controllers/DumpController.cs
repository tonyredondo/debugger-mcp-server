using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using DebuggerMcp.Analysis;
using DebuggerMcp.Reporting;
using DebuggerMcp.Security;
using DebuggerMcp.SourceLink;
using DebuggerMcp.Watches;

namespace DebuggerMcp.Controllers;

/// <summary>
/// API controller for managing dump file uploads and metadata.
/// </summary>
/// <remarks>
/// This controller provides HTTP endpoints for uploading dump files,
/// retrieving dump information, and managing dump storage.
/// Authentication is required when API_KEY environment variable is set.
/// </remarks>
[ApiController]
[Route("api/dumps")]
[Authorize]
public class DumpController : ControllerBase
{
    /// <summary>
    /// The session manager for accessing session information and dump storage path.
    /// </summary>
    private readonly DebuggerSessionManager _sessionManager;

    /// <summary>
    /// The symbol manager for managing symbol files.
    /// </summary>
    private readonly SymbolManager _symbolManager;

    /// <summary>
    /// The watch store for managing watch expressions.
    /// </summary>
    private readonly WatchStore _watchStore;

    /// <summary>
    /// The logger for this controller.
    /// </summary>
    private readonly ILogger<DumpController> _logger;

    /// <summary>
    /// The logger factory for creating debugger loggers.
    /// </summary>
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Maximum allowed dump file size in bytes (default: 5GB).
    /// </summary>
    private const long MaxDumpFileSize = 5L * 1024 * 1024 * 1024;

    /// <summary>
    /// Initializes a new instance of the <see cref="DumpController"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager instance (also provides dump storage path).</param>
    /// <param name="symbolManager">The symbol manager instance.</param>
    /// <param name="watchStore">The watch store instance for managing watch expressions.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="loggerFactory">The logger factory for creating debugger loggers.</param>
    public DumpController(
        DebuggerSessionManager sessionManager,
        SymbolManager symbolManager,
        WatchStore watchStore,
        ILogger<DumpController> logger,
        ILoggerFactory loggerFactory)
    {
        _sessionManager = sessionManager;
        _symbolManager = symbolManager;
        _watchStore = watchStore;
        _logger = logger;
        _loggerFactory = loggerFactory;
        
        // Ensure the storage directory exists (path comes from SessionManager)
        Directory.CreateDirectory(_sessionManager.GetDumpStoragePath());
    }



    /// <summary>
    /// Uploads a dump file for the specified user.
    /// </summary>
    /// <param name="file">The dump file to upload (Windows .dmp, Linux/macOS core files).</param>
    /// <param name="userId">The user identifier for organizing dump storage.</param>
    /// <param name="description">Optional description of the dump for documentation purposes.</param>
    /// <returns>Information about the uploaded dump including the generated dump ID.</returns>
    /// <response code="200">Dump uploaded successfully.</response>
    /// <response code="400">Invalid request (missing file, invalid userId, file too large, or invalid format).</response>
    /// <response code="500">Internal server error during upload.</response>
    /// <remarks>
    /// <para>The upload process performs several security validations:</para>
    /// <list type="bullet">
    /// <item><description>User ID sanitization to prevent path traversal attacks</description></item>
    /// <item><description>File size limit check (default 5GB)</description></item>
    /// <item><description>Magic byte validation to ensure only valid dump formats are accepted</description></item>
    /// </list>
    /// <para>Supported dump formats:</para>
    /// <list type="bullet">
    /// <item><description>Windows Minidump (MDMP signature)</description></item>
    /// <item><description>Windows Full/Kernel Dump (PAGE signature)</description></item>
    /// <item><description>Linux ELF core (0x7F ELF signature)</description></item>
    /// <item><description>macOS Mach-O core (0xFEEDFACE/0xFEEDFACF signatures)</description></item>
    /// </list>
    /// </remarks>
    [HttpPost("upload")]
    [ProducesResponseType(typeof(DumpUploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UploadDump(
        [FromForm] IFormFile file,
        [FromForm] string userId,
        [FromForm] string? description = null)
    {
        try
        {
            // Validation 1: Ensure a file was provided
            // Empty files are rejected as they cannot be valid dumps
            if (file == null || file.Length == 0)
            {
                return BadRequest(new { error = "No file provided or file is empty" });
            }

            // Validation 2: Sanitize userId to prevent path traversal attacks
            // Users could attempt to escape the storage directory with "../" patterns
            string sanitizedUserId;
            try
            {
                sanitizedUserId = PathSanitizer.SanitizeIdentifier(userId, nameof(userId));
            }
            catch (ArgumentException ex)
            {
                // Path sanitization failed - return the specific validation error
                return BadRequest(new { error = ex.Message });
            }

            // Validation 3: Check file size against configured limit
            // This prevents disk exhaustion attacks and ensures reasonable upload times
            if (file.Length > MaxDumpFileSize)
            {
                return BadRequest(new 
                { 
                    error = $"File size exceeds maximum allowed size of {MaxDumpFileSize / (1024 * 1024 * 1024)}GB" 
                });
            }

            // Validation 4: Check magic bytes to ensure this is a valid dump file
            // This prevents arbitrary file uploads disguised as dumps
            using var headerStream = file.OpenReadStream();
            var header = new byte[DumpFileValidator.MinimumBytesNeeded];
            var bytesRead = await headerStream.ReadAsync(header.AsMemory(0, header.Length));
            
            // If we couldn't read enough bytes or the header doesn't match known formats, reject
            if (bytesRead < DumpFileValidator.MinimumBytesNeeded || !DumpFileValidator.IsValidDumpHeader(header))
            {
                var detectedFormat = DumpFileValidator.GetDumpFormat(header);
                return BadRequest(new 
                { 
                    error = "Invalid dump file format. File must be a valid memory dump (Windows MDMP/PAGE, Linux ELF core, or macOS Mach-O core).",
                    detectedFormat
                });
            }

            // Extract the detected format for the response
            var dumpFormat = DumpFileValidator.GetDumpFormat(header);

            // Generate unique dump ID using GUID to ensure no collisions
            var dumpId = Guid.NewGuid().ToString();

            // Create user-specific directory structure
            // Layout: {dumpStoragePath}/{userId}/{dumpId}.dmp
            var userDir = Path.Combine(_sessionManager.GetDumpStoragePath(), sanitizedUserId);
            Directory.CreateDirectory(userDir);

            // Construct the full file path using the sanitized identifiers
            var fileName = $"{dumpId}.dmp";
            var filePath = Path.Combine(userDir, fileName);

            // Log the upload attempt for audit purposes
            _logger.LogInformation(
                "Uploading dump file for user {UserId}, dumpId {DumpId}, size {Size} bytes, format {Format}",
                sanitizedUserId, dumpId, file.Length, dumpFormat);

            // Write the file to disk
            // We write in two parts: the header we already read, then the remaining content
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                // First, write the header bytes we already consumed for validation
                await stream.WriteAsync(header.AsMemory(0, bytesRead));
                
                // Then stream the rest of the file content
                await headerStream.CopyToAsync(stream);
            }

            // Analyze dump to detect Alpine status and runtime version (runs dotnet-symbol --verifycore)
            // This is important because Alpine dumps can only be debugged on Alpine hosts
            var analysisResult = await DumpAnalyzer.AnalyzeDumpAsync(filePath, _logger);

            // Build the response object
            // Note: We intentionally do NOT expose the internal file path for security
            var response = new DumpUploadResponse
            {
                DumpId = dumpId,
                UserId = sanitizedUserId,
                FileName = file.FileName,
                Size = file.Length,
                UploadedAt = DateTime.UtcNow,
                Description = description,
                DumpFormat = dumpFormat,
                IsAlpineDump = analysisResult.IsAlpine,
                RuntimeVersion = analysisResult.RuntimeVersion,
                Architecture = analysisResult.Architecture
            };

            // Save metadata to a sidecar JSON file for later retrieval
            var metadataPath = Path.Combine(userDir, $"{dumpId}.json");
            var metadata = new DumpMetadata
            {
                DumpId = dumpId,
                UserId = sanitizedUserId,
                FileName = file.FileName,
                Size = file.Length,
                UploadedAt = DateTime.UtcNow,
                Description = description,
                DumpFormat = dumpFormat,
                IsAlpineDump = analysisResult.IsAlpine,
                RuntimeVersion = analysisResult.RuntimeVersion,
                Architecture = analysisResult.Architecture
            };
            await System.IO.File.WriteAllTextAsync(metadataPath, 
                System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            _logger.LogInformation(
                "Dump file uploaded successfully for user {UserId}, dumpId {DumpId}, isAlpine: {IsAlpine}, runtimeVersion: {RuntimeVersion}, architecture: {Architecture}",
                sanitizedUserId, dumpId, analysisResult.IsAlpine, analysisResult.RuntimeVersion ?? "(not detected)", analysisResult.Architecture ?? "(not detected)");

            return Ok(response);
        }
        catch (Exception ex)
        {
            // Log the full exception for debugging but return a generic error
            // to avoid leaking internal details to the client
            _logger.LogError(ex, "Error uploading dump file for user {UserId}", userId);
            return StatusCode(500, new { error = "Internal server error during upload" });
        }
    }

    /// <summary>
    /// Retrieves information about a specific dump file.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="dumpId">The dump identifier.</param>
    /// <returns>Information about the dump file.</returns>
    /// <response code="200">Dump information retrieved successfully.</response>
    /// <response code="404">Dump not found.</response>
    [HttpGet("{userId}/{dumpId}")]
    [ProducesResponseType(typeof(DumpInfoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetDumpInfo(string userId, string dumpId)
    {
        try
        {
            // Sanitize identifiers to prevent path traversal
            string sanitizedUserId, sanitizedDumpId;
            try
            {
                sanitizedUserId = PathSanitizer.SanitizeIdentifier(userId, nameof(userId));
                sanitizedDumpId = PathSanitizer.SanitizeIdentifier(dumpId, nameof(dumpId));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }

            // Construct file path
            var userDir = Path.Combine(_sessionManager.GetDumpStoragePath(), sanitizedUserId);
            var filePath = Path.Combine(userDir, $"{sanitizedDumpId}.dmp");
            var metadataPath = Path.Combine(userDir, $"{sanitizedDumpId}.json");

            // Check if file exists
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound(new { error = $"Dump '{sanitizedDumpId}' not found for user '{sanitizedUserId}'" });
            }

            // Get file information
            var fileInfo = new FileInfo(filePath);

            // Create response (without exposing internal file path)
            var response = new DumpInfoResponse
            {
                DumpId = sanitizedDumpId,
                UserId = sanitizedUserId,
                Size = fileInfo.Length,
                CreatedAt = fileInfo.CreationTimeUtc,
                LastAccessedAt = fileInfo.LastAccessTimeUtc
            };

            // Try to load metadata if it exists
            if (System.IO.File.Exists(metadataPath))
            {
                try
                {
                    var metadataJson = System.IO.File.ReadAllText(metadataPath);
                    var metadata = System.Text.Json.JsonSerializer.Deserialize<DumpMetadata>(metadataJson);
                    if (metadata != null)
                    {
                        response.FileName = metadata.FileName;
                        response.UploadedAt = metadata.UploadedAt;
                        response.Description = metadata.Description;
                        response.DumpFormat = metadata.DumpFormat;
                        response.IsAlpineDump = metadata.IsAlpineDump;
                        response.RuntimeVersion = metadata.RuntimeVersion;
                        response.Architecture = metadata.Architecture;
                    }
                }
                catch
                {
                    // Ignore metadata read errors - file info is still available
                }
            }

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving dump info for user {UserId}, dumpId {DumpId}", 
                userId, dumpId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Lists all dumps for a specific user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <returns>A list of dump information for the user.</returns>
    /// <response code="200">Dump list retrieved successfully.</response>
    [HttpGet("user/{userId}")]
    [ProducesResponseType(typeof(List<DumpInfoResponse>), StatusCodes.Status200OK)]
    public IActionResult ListUserDumps(string userId)
    {
        try
        {
            // Sanitize userId to prevent path traversal
            string sanitizedUserId;
            try
            {
                sanitizedUserId = PathSanitizer.SanitizeIdentifier(userId, nameof(userId));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }

            // Get user directory
            var userDir = Path.Combine(_sessionManager.GetDumpStoragePath(), sanitizedUserId);

            // Check if directory exists
            if (!Directory.Exists(userDir))
            {
                return Ok(new List<DumpInfoResponse>());
            }

            // Get all .dmp files
            var dumpFiles = Directory.GetFiles(userDir, "*.dmp");

            // Create response list (without exposing internal file paths)
            var dumps = new List<DumpInfoResponse>();
            
            foreach (var filePath in dumpFiles)
            {
                var fileInfo = new FileInfo(filePath);
                var dumpId = Path.GetFileNameWithoutExtension(fileInfo.Name);
                var metadataPath = Path.Combine(userDir, $"{dumpId}.json");

                var response = new DumpInfoResponse
                {
                    DumpId = dumpId,
                    UserId = sanitizedUserId,
                    Size = fileInfo.Length,
                    CreatedAt = fileInfo.CreationTimeUtc,
                    LastAccessedAt = fileInfo.LastAccessTimeUtc
                };

                // Try to load metadata if it exists
                if (System.IO.File.Exists(metadataPath))
                {
                    try
                    {
                        var metadataJson = System.IO.File.ReadAllText(metadataPath);
                        var metadata = System.Text.Json.JsonSerializer.Deserialize<DumpMetadata>(metadataJson);
                        if (metadata != null)
                        {
                            response.FileName = metadata.FileName;
                            response.UploadedAt = metadata.UploadedAt;
                            response.Description = metadata.Description;
                            response.DumpFormat = metadata.DumpFormat;
                            response.IsAlpineDump = metadata.IsAlpineDump;
                            response.RuntimeVersion = metadata.RuntimeVersion;
                            response.Architecture = metadata.Architecture;
                        }
                    }
                    catch
                    {
                        // Ignore metadata read errors - file info is still available
                    }
                }

                dumps.Add(response);
            }

            // Sort by upload date (or creation date if no metadata)
            dumps = dumps
                .OrderByDescending(d => d.UploadedAt != default ? d.UploadedAt : d.CreatedAt)
                .ToList();

            return Ok(dumps);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing dumps for user {UserId}", userId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Deletes a specific dump file.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="dumpId">The dump identifier.</param>
    /// <returns>Success message if deleted.</returns>
    /// <response code="200">Dump deleted successfully.</response>
    /// <response code="404">Dump not found.</response>
    [HttpDelete("{userId}/{dumpId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DeleteDump(string userId, string dumpId)
    {
        try
        {
            // Sanitize identifiers to prevent path traversal
            string sanitizedUserId, sanitizedDumpId;
            try
            {
                sanitizedUserId = PathSanitizer.SanitizeIdentifier(userId, nameof(userId));
                sanitizedDumpId = PathSanitizer.SanitizeIdentifier(dumpId, nameof(dumpId));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }

            // Construct file paths
            var userDir = Path.Combine(_sessionManager.GetDumpStoragePath(), sanitizedUserId);
            var filePath = Path.Combine(userDir, $"{sanitizedDumpId}.dmp");
            var metadataPath = Path.Combine(userDir, $"{sanitizedDumpId}.json");

            // Check if file exists
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound(new { error = $"Dump '{sanitizedDumpId}' not found for user '{sanitizedUserId}'" });
            }

            // Delete the dump file
            System.IO.File.Delete(filePath);

            // Delete metadata file if it exists
            if (System.IO.File.Exists(metadataPath))
            {
                System.IO.File.Delete(metadataPath);
            }

            // Delete associated symbol files to prevent orphaned files
            _symbolManager.DeleteDumpSymbols(sanitizedDumpId);

            // Clean up watch store cache and locks for this dump
            _watchStore.CleanupDumpResources(sanitizedUserId, sanitizedDumpId);

            _logger.LogInformation("Dump {DumpId} and associated resources deleted for user {UserId}", sanitizedDumpId, sanitizedUserId);

            return Ok(new { message = $"Dump '{sanitizedDumpId}' and associated resources deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting dump {DumpId} for user {UserId}", dumpId, userId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Gets session statistics for monitoring and administration.
    /// </summary>
    /// <returns>Current session statistics.</returns>
    /// <response code="200">Statistics retrieved successfully.</response>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(SessionStatisticsResponse), StatusCodes.Status200OK)]
    public IActionResult GetSessionStatistics()
    {
        var stats = _sessionManager.GetStatistics();
        
        // Calculate dump statistics
        var (totalDumps, storageUsed) = CalculateDumpStatistics();
        
        // Calculate uptime
        var uptime = CalculateUptime();
        
        var totalSessions = (int)stats["TotalSessions"];
        
        return Ok(new SessionStatisticsResponse
        {
            ActiveSessions = totalSessions,
            TotalSessions = totalSessions,
            TotalDumps = totalDumps,
            StorageUsed = storageUsed,
            MaxSessionsPerUser = (int)stats["MaxSessionsPerUser"],
            MaxTotalSessions = (int)stats["MaxTotalSessions"],
            UniqueUsers = (int)stats["UniqueUsers"],
            SessionsPerUser = (Dictionary<string, int>)stats["SessionsPerUser"],
            Uptime = uptime,
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// The server start time for uptime calculation.
    /// </summary>
    private static readonly DateTime ServerStartTime = DateTime.UtcNow;

    /// <summary>
    /// Calculates dump statistics (count and storage used).
    /// </summary>
    private (int TotalDumps, long StorageUsed) CalculateDumpStatistics()
    {
        try
        {
            var storagePath = _sessionManager.GetDumpStoragePath();
            if (!Directory.Exists(storagePath))
            {
                return (0, 0);
            }

            var dumpFiles = Directory.GetFiles(storagePath, "*.dmp", SearchOption.AllDirectories);
            var totalDumps = dumpFiles.Length;
            var storageUsed = dumpFiles.Sum(f => new FileInfo(f).Length);

            return (totalDumps, storageUsed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to calculate dump statistics");
            return (0, 0);
        }
    }

    /// <summary>
    /// Calculates server uptime as a formatted string.
    /// </summary>
    private static string CalculateUptime()
    {
        var uptime = DateTime.UtcNow - ServerStartTime;
        
        if (uptime.TotalDays >= 1)
        {
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        }
        else if (uptime.TotalHours >= 1)
        {
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";
        }
        else
        {
            return $"{uptime.Minutes}m {uptime.Seconds}s";
        }
    }

    /// <summary>
    /// Compares two dump files and returns a detailed comparison report.
    /// </summary>
    /// <param name="request">The comparison request containing baseline and comparison dump identifiers.</param>
    /// <returns>A comprehensive comparison result.</returns>
    /// <response code="200">Comparison completed successfully.</response>
    /// <response code="400">Invalid request parameters.</response>
    /// <response code="404">One or both dump files not found.</response>
    /// <response code="500">Internal server error during comparison.</response>
    /// <remarks>
    /// This endpoint compares two memory dumps to identify:
    /// - Memory changes (growth, leaks)
    /// - Thread state changes
    /// - Module loading/unloading
    /// 
    /// Both dump files must have been previously uploaded via the upload endpoint.
    /// The comparison creates temporary debugging sessions that are automatically cleaned up.
    /// 
    /// Example request:
    /// ```json
    /// {
    ///   "baselineUserId": "user123",
    ///   "baselineDumpId": "abc-123",
    ///   "comparisonUserId": "user123",
    ///   "comparisonDumpId": "def-456"
    /// }
    /// ```
    /// </remarks>
    [HttpPost("compare")]
    [ProducesResponseType(typeof(DumpComparisonResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CompareDumps([FromBody] DumpComparisonRequest request)
    {
        IDebuggerManager? baselineManager = null;
        IDebuggerManager? comparisonManager = null;

        try
        {
            // Validate request
            if (request == null)
            {
                return BadRequest(new { error = "Request body is required" });
            }

            // Sanitize identifiers
            string sanitizedBaselineUserId, sanitizedBaselineDumpId;
            string sanitizedComparisonUserId, sanitizedComparisonDumpId;

            try
            {
                sanitizedBaselineUserId = PathSanitizer.SanitizeIdentifier(request.BaselineUserId, nameof(request.BaselineUserId));
                sanitizedBaselineDumpId = PathSanitizer.SanitizeIdentifier(request.BaselineDumpId, nameof(request.BaselineDumpId));
                sanitizedComparisonUserId = PathSanitizer.SanitizeIdentifier(request.ComparisonUserId, nameof(request.ComparisonUserId));
                sanitizedComparisonDumpId = PathSanitizer.SanitizeIdentifier(request.ComparisonDumpId, nameof(request.ComparisonDumpId));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }

            // Verify baseline dump exists
            var baselinePath = Path.Combine(
                _sessionManager.GetDumpStoragePath(),
                sanitizedBaselineUserId,
                $"{sanitizedBaselineDumpId}.dmp");

            if (!System.IO.File.Exists(baselinePath))
            {
                return NotFound(new { error = $"Baseline dump '{sanitizedBaselineDumpId}' not found for user '{sanitizedBaselineUserId}'" });
            }

            // Verify comparison dump exists
            var comparisonPath = Path.Combine(
                _sessionManager.GetDumpStoragePath(),
                sanitizedComparisonUserId,
                $"{sanitizedComparisonDumpId}.dmp");

            if (!System.IO.File.Exists(comparisonPath))
            {
                return NotFound(new { error = $"Comparison dump '{sanitizedComparisonDumpId}' not found for user '{sanitizedComparisonUserId}'" });
            }

            _logger.LogInformation(
                "Starting dump comparison: baseline={BaselineUser}/{BaselineDump}, comparison={ComparisonUser}/{ComparisonDump}",
                sanitizedBaselineUserId, sanitizedBaselineDumpId,
                sanitizedComparisonUserId, sanitizedComparisonDumpId);

            // Create temporary debugger managers for comparison
            baselineManager = DebuggerFactory.CreateDebugger(_loggerFactory);
            await baselineManager.InitializeAsync();
            baselineManager.OpenDumpFile(baselinePath);

            comparisonManager = DebuggerFactory.CreateDebugger(_loggerFactory);
            await comparisonManager.InitializeAsync();
            comparisonManager.OpenDumpFile(comparisonPath);

            // Perform comparison
            var comparer = new DumpComparer(baselineManager, comparisonManager);
            var result = await comparer.CompareAsync();

            // Populate identifiers
            result.Baseline = new DumpIdentifier
            {
                SessionId = "http-comparison",
                DumpId = sanitizedBaselineDumpId,
                DebuggerType = baselineManager.DebuggerType
            };
            result.Comparison = new DumpIdentifier
            {
                SessionId = "http-comparison",
                DumpId = sanitizedComparisonDumpId,
                DebuggerType = comparisonManager.DebuggerType
            };

            _logger.LogInformation(
                "Dump comparison completed: memoryDelta={MemoryDelta}, threadDelta={ThreadDelta}",
                result.HeapComparison?.MemoryDeltaBytes ?? 0,
                result.ThreadComparison?.ThreadCountDelta ?? 0);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error comparing dumps");
            return StatusCode(500, new { error = "Internal server error during comparison" });
        }
        finally
        {
            // Clean up debugger managers
            if (baselineManager != null)
            {
                try
                {
                    baselineManager.CloseDump();
                    await baselineManager.DisposeAsync();
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            if (comparisonManager != null)
            {
                try
                {
                    comparisonManager.CloseDump();
                    await comparisonManager.DisposeAsync();
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }

    /// <summary>
    /// Generates and downloads an analysis report for a dump file.
    /// </summary>
    /// <param name="userId">The user ID who owns the dump.</param>
    /// <param name="dumpId">The dump ID to generate report for.</param>
    /// <param name="format">Report format: markdown, html, or json (default: markdown).</param>
    /// <param name="includeRaw">Whether to include raw debugger output (default: false).</param>
    /// <returns>The generated report as a downloadable file.</returns>
    /// <remarks>
    /// Generates a comprehensive crash analysis report for the specified dump.
    /// 
    /// **Available formats:**
    /// - `markdown` - ASCII charts, GitHub-friendly, works in any text editor
    /// - `html` - Styled with CSS charts, opens in browser, can print to PDF
    /// - `json` - Structured data for programmatic consumption
    /// 
    /// **Example:**
    /// ```
    /// GET /api/dumps/user123/crash-dump-001/report?format=html
    /// ```
    /// 
    /// **Tip:** For PDF output, use `format=html` and print from your browser (File > Print > Save as PDF).
    /// </remarks>
    /// <response code="200">Report generated successfully.</response>
    /// <response code="400">Invalid parameters.</response>
    /// <response code="404">Dump not found.</response>
    /// <response code="500">Internal server error.</response>
    [HttpGet("{userId}/{dumpId}/report")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GenerateReport(
        string userId,
        string dumpId,
        [FromQuery] string format = "markdown",
        [FromQuery] bool includeRaw = false)
    {
        IDebuggerManager? manager = null;

        try
        {
            // Sanitize identifiers
            string sanitizedUserId, sanitizedDumpId;
            try
            {
                sanitizedUserId = PathSanitizer.SanitizeIdentifier(userId, nameof(userId));
                sanitizedDumpId = PathSanitizer.SanitizeIdentifier(dumpId, nameof(dumpId));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }

            // Parse format
            ReportFormat reportFormat;
            try
            {
                reportFormat = ReportService.ParseFormat(format);
            }
            catch (ArgumentException)
            {
                return BadRequest(new { error = $"Invalid format '{format}'. Supported formats: markdown, html, json" });
            }

            // Verify dump exists
            var dumpPath = Path.Combine(
                _sessionManager.GetDumpStoragePath(),
                sanitizedUserId,
                $"{sanitizedDumpId}.dmp");

            if (!System.IO.File.Exists(dumpPath))
            {
                return NotFound(new { error = $"Dump '{sanitizedDumpId}' not found for user '{sanitizedUserId}'" });
            }

            _logger.LogInformation(
                "Generating {Format} report for dump {UserId}/{DumpId}",
                format, sanitizedUserId, sanitizedDumpId);

            // Create debugger and open dump
            manager = DebuggerFactory.CreateDebugger(_loggerFactory);
            await manager.InitializeAsync();
            manager.OpenDumpFile(dumpPath);

            // Create Source Link resolver with symbol paths
            var sourceLinkResolver = new SourceLinkResolver(_logger);
            // Symbol path is .symbols_{dumpId} folder where dotnet-symbol downloads PDBs
            var dumpIdWithoutExt = System.IO.Path.GetFileNameWithoutExtension(sanitizedDumpId);
            var symbolPath = System.IO.Path.Combine(_sessionManager.GetDumpStoragePath(), sanitizedUserId, $".symbols_{dumpIdWithoutExt}");
            _logger.LogInformation("[DumpController] Looking for symbols in: {SymbolPath}", symbolPath);
            if (System.IO.Directory.Exists(symbolPath))
            {
                sourceLinkResolver.AddSymbolSearchPath(symbolPath);
            }
            else
            {
                _logger.LogWarning("[DumpController] Symbol path does not exist: {SymbolPath}", symbolPath);
            }

            // Run crash analysis
            var analyzer = new CrashAnalyzer(manager, sourceLinkResolver);
            var analysisResult = await analyzer.AnalyzeCrashAsync();
            
            // Run security analysis and include in results
            var securityAnalyzer = new SecurityAnalyzer(manager);
            var securityResult = await securityAnalyzer.AnalyzeSecurityAsync();
            if (securityResult != null)
            {
                analysisResult.Security = new SecurityInfo
                {
                    HasVulnerabilities = securityResult.Vulnerabilities?.Count > 0,
                    OverallRisk = securityResult.OverallRisk.ToString(),
                    Summary = securityResult.Summary,
                    AnalyzedAt = securityResult.AnalyzedAt.ToString("O"),
                    Findings = securityResult.Vulnerabilities?.Select(v => new SecurityFinding
                    {
                        Type = v.Type.ToString(),
                        Severity = v.Severity.ToString(),
                        Description = v.Description,
                        Location = v.Address,
                        Recommendation = v.Details
                    }).ToList(),
                    Recommendations = securityResult.Recommendations
                };
            }

            // Include watch results if any exist
            if (await _watchStore.HasWatchesAsync(sanitizedUserId, sanitizedDumpId))
            {
                var evaluator = new WatchEvaluator(manager, _watchStore);
                analysisResult.Watches = await evaluator.EvaluateAllAsync(sanitizedUserId, sanitizedDumpId);
            }

            // Generate report
            var reportService = new ReportService();
            var options = new ReportOptions
            {
                Format = reportFormat,
                IncludeRawOutput = includeRaw
            };
            var metadata = new ReportMetadata
            {
                DumpId = sanitizedDumpId,
                UserId = sanitizedUserId,
                DebuggerType = manager.DebuggerType,
                GeneratedAt = DateTime.UtcNow
            };

            var reportContent = reportService.GenerateReport(analysisResult, options, metadata);
            var contentType = ReportService.GetContentType(reportFormat);
            var fileExtension = ReportService.GetFileExtension(reportFormat);
            var fileName = $"report-{sanitizedDumpId}.{fileExtension}";

            _logger.LogInformation(
                "Report generated successfully for dump {UserId}/{DumpId}: {ByteCount} bytes",
                sanitizedUserId, sanitizedDumpId, reportContent.Length);

            return File(
                System.Text.Encoding.UTF8.GetBytes(reportContent),
                contentType,
                fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating report for dump {UserId}/{DumpId}", userId, dumpId);
            return StatusCode(500, new { error = "Internal server error during report generation" });
        }
        finally
        {
            // Clean up debugger manager
            if (manager != null)
            {
                try
                {
                    manager.CloseDump();
                    await manager.DisposeAsync();
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }
}


/// <summary>
/// Response model for dump upload operation.
/// </summary>
/// <remarks>
/// This response is returned after successfully uploading a memory dump file.
/// Use the <see cref="DumpId"/> in subsequent MCP tool calls to analyze the dump.
/// </remarks>
/// <example>
/// {
///   "dumpId": "abc123-456def-789ghi",
///   "userId": "user123",
///   "fileName": "crash.dmp",
///   "size": 524288000,
///   "uploadedAt": "2024-01-15T10:30:00Z",
///   "description": "Production crash",
///   "dumpFormat": "Windows Minidump"
/// }
/// </example>
public class DumpUploadResponse
{
    /// <summary>
    /// Gets or sets the unique identifier for the uploaded dump.
    /// </summary>
    /// <example>abc123-456def-789ghi</example>
    public string DumpId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user identifier who uploaded the dump.
    /// </summary>
    /// <example>user123</example>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the original file name.
    /// </summary>
    /// <example>crash.dmp</example>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    /// <example>524288000</example>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the upload timestamp.
    /// </summary>
    /// <example>2024-01-15T10:30:00Z</example>
    public DateTime UploadedAt { get; set; }

    /// <summary>
    /// Gets or sets the optional description.
    /// </summary>
    /// <example>Production crash on 2024-01-15</example>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the detected dump file format.
    /// </summary>
    /// <example>Windows Minidump</example>
    public string? DumpFormat { get; set; }
    
    /// <summary>
    /// Gets or sets whether this dump is from an Alpine Linux system (musl libc).
    /// </summary>
    /// <remarks>
    /// Alpine dumps can only be debugged on Alpine hosts due to musl vs glibc differences.
    /// </remarks>
    /// <example>true</example>
    public bool? IsAlpineDump { get; set; }
    
    /// <summary>
    /// Gets or sets the detected .NET runtime version required to debug this dump.
    /// </summary>
    /// <example>9.0.10</example>
    public string? RuntimeVersion { get; set; }
    
    /// <summary>
    /// Gets or sets the processor architecture of the dump.
    /// </summary>
    /// <example>arm64</example>
    public string? Architecture { get; set; }
}

/// <summary>
/// Metadata stored alongside dump files for later retrieval.
/// </summary>
public class DumpMetadata
{
    /// <summary>Gets or sets the dump identifier.</summary>
    public string DumpId { get; set; } = string.Empty;
    
    /// <summary>Gets or sets the user identifier.</summary>
    public string UserId { get; set; } = string.Empty;
    
    /// <summary>Gets or sets the original file name.</summary>
    public string? FileName { get; set; }
    
    /// <summary>Gets or sets the file size in bytes.</summary>
    public long Size { get; set; }
    
    /// <summary>Gets or sets the upload timestamp.</summary>
    public DateTime UploadedAt { get; set; }
    
    /// <summary>Gets or sets the optional description.</summary>
    public string? Description { get; set; }
    
    /// <summary>Gets or sets the detected dump format.</summary>
    public string? DumpFormat { get; set; }
    
    /// <summary>Gets or sets the detected .NET runtime version (e.g., "9.0.10").</summary>
    /// <remarks>
    /// This is populated by dotnet-symbol when analyzing the dump.
    /// Used by LLDB/SOS to find the correct DAC for debugging.
    /// </remarks>
    public string? RuntimeVersion { get; set; }
    
    /// <summary>Gets or sets whether this dump is from an Alpine Linux system (musl libc).</summary>
    /// <remarks>
    /// This is critical because Alpine Linux uses musl libc instead of glibc,
    /// which means Alpine dumps can only be debugged on Alpine hosts.
    /// Detected by checking for musl indicators in module paths (e.g., ld-musl, linux-musl).
    /// </remarks>
    public bool? IsAlpineDump { get; set; }
    
    /// <summary>Gets or sets the processor architecture of the dump (e.g., "arm64", "x64").</summary>
    public string? Architecture { get; set; }
    
    /// <summary>Gets or sets the list of symbol files downloaded by dotnet-symbol.</summary>
    /// <remarks>
    /// This list is used to verify that all required symbol files are present.
    /// If any file is missing, dotnet-symbol will be run again to download missing files.
    /// The list contains relative paths from the symbol cache directory.
    /// </remarks>
    public List<string>? SymbolFiles { get; set; }
}

/// <summary>
/// Response model for dump information.
/// </summary>
/// <remarks>
/// This response provides metadata about an uploaded dump file.
/// </remarks>
/// <example>
/// {
///   "dumpId": "abc123-456def-789ghi",
///   "userId": "user123",
///   "fileName": "crash.dmp",
///   "size": 524288000,
///   "uploadedAt": "2024-01-15T10:30:00Z",
///   "dumpFormat": "Windows Minidump"
/// }
/// </example>
public class DumpInfoResponse
{
    /// <summary>
    /// Gets or sets the dump identifier.
    /// </summary>
    /// <example>abc123-456def-789ghi</example>
    public string DumpId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user identifier.
    /// </summary>
    /// <example>user123</example>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the original file name.
    /// </summary>
    /// <example>crash.dmp</example>
    public string? FileName { get; set; }

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    /// <example>524288000</example>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the upload timestamp.
    /// </summary>
    /// <example>2024-01-15T10:30:00Z</example>
    public DateTime UploadedAt { get; set; }

    /// <summary>
    /// Gets or sets the optional description.
    /// </summary>
    /// <example>Production crash on 2024-01-15</example>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the detected dump format.
    /// </summary>
    /// <example>Windows Minidump</example>
    public string? DumpFormat { get; set; }

    /// <summary>
    /// Gets or sets the creation timestamp (file system).
    /// </summary>
    /// <example>2024-01-15T10:30:00Z</example>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the last accessed timestamp.
    /// </summary>
    /// <example>2024-01-15T11:00:00Z</example>
    public DateTime LastAccessedAt { get; set; }
    
    /// <summary>
    /// Gets or sets whether this dump is from an Alpine Linux system (musl libc).
    /// </summary>
    /// <remarks>
    /// Alpine dumps can only be debugged on Alpine hosts due to musl vs glibc differences.
    /// </remarks>
    /// <example>true</example>
    public bool? IsAlpineDump { get; set; }
    
    /// <summary>
    /// Gets or sets the detected .NET runtime version required to debug this dump.
    /// </summary>
    /// <example>9.0.10</example>
    public string? RuntimeVersion { get; set; }
    
    /// <summary>
    /// Gets or sets the processor architecture of the dump.
    /// </summary>
    /// <example>arm64</example>
    public string? Architecture { get; set; }
}

/// <summary>
/// Response model for session statistics.
/// </summary>
/// <remarks>
/// Provides monitoring information about active debugging sessions.
/// </remarks>
/// <example>
/// {
///   "activeSessions": 12,
///   "totalSessions": 12,
///   "totalDumps": 25,
///   "storageUsed": 5368709120,
///   "maxSessionsPerUser": 5,
///   "maxTotalSessions": 50,
///   "uniqueUsers": 4,
///   "sessionsPerUser": { "user1": 3, "user2": 5 },
///   "uptime": "2d 5h 30m",
///   "timestamp": "2024-01-15T10:30:00Z"
/// }
/// </example>
public class SessionStatisticsResponse
{
    /// <summary>
    /// Gets or sets the number of active sessions (alias for TotalSessions for CLI compatibility).
    /// </summary>
    /// <example>12</example>
    public int ActiveSessions { get; set; }

    /// <summary>
    /// Gets or sets the total number of active sessions.
    /// </summary>
    /// <example>12</example>
    public int TotalSessions { get; set; }

    /// <summary>
    /// Gets or sets the total number of stored dump files.
    /// </summary>
    /// <example>25</example>
    public int TotalDumps { get; set; }

    /// <summary>
    /// Gets or sets the total storage used in bytes.
    /// </summary>
    /// <example>5368709120</example>
    public long StorageUsed { get; set; }

    /// <summary>
    /// Gets or sets the maximum sessions allowed per user.
    /// </summary>
    /// <example>5</example>
    public int MaxSessionsPerUser { get; set; }

    /// <summary>
    /// Gets or sets the maximum total sessions allowed.
    /// </summary>
    /// <example>50</example>
    public int MaxTotalSessions { get; set; }

    /// <summary>
    /// Gets or sets the number of unique users with active sessions.
    /// </summary>
    /// <example>4</example>
    public int UniqueUsers { get; set; }

    /// <summary>
    /// Gets or sets the session count per user.
    /// </summary>
    public Dictionary<string, int> SessionsPerUser { get; set; } = new();

    /// <summary>
    /// Gets or sets the server uptime.
    /// </summary>
    /// <example>2d 5h 30m</example>
    public string? Uptime { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when statistics were generated.
    /// </summary>
    /// <example>2024-01-15T10:30:00Z</example>
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Request model for comparing two dump files.
/// </summary>
/// <remarks>
/// Used to specify the baseline and comparison dumps for the comparison endpoint.
/// Both dumps must have been previously uploaded via the upload endpoint.
/// </remarks>
/// <example>
/// {
///   "baselineUserId": "user123",
///   "baselineDumpId": "abc-123-def",
///   "comparisonUserId": "user123",
///   "comparisonDumpId": "ghi-456-jkl"
/// }
/// </example>
public class DumpComparisonRequest
{
    /// <summary>
    /// Gets or sets the user ID that owns the baseline dump.
    /// </summary>
    /// <example>user123</example>
    public string BaselineUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the dump ID for the baseline (older/before) dump.
    /// </summary>
    /// <example>abc-123-def</example>
    public string BaselineDumpId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user ID that owns the comparison dump.
    /// </summary>
    /// <example>user123</example>
    public string ComparisonUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the dump ID for the comparison (newer/after) dump.
    /// </summary>
    /// <example>ghi-456-jkl</example>
    public string ComparisonDumpId { get; set; } = string.Empty;
}

/// <summary>
/// Result of dump analysis from dotnet-symbol --verifycore and file command.
/// </summary>
public class DumpAnalysisResult
{
    /// <summary>
    /// Whether the dump is from an Alpine Linux system (musl libc).
    /// </summary>
    public bool? IsAlpine { get; set; }
    
    /// <summary>
    /// The detected .NET runtime version required to debug this dump (e.g., "9.0.10").
    /// </summary>
    public string? RuntimeVersion { get; set; }
    
    /// <summary>
    /// The processor architecture of the dump (e.g., "arm64", "x64").
    /// </summary>
    public string? Architecture { get; set; }
}

/// <summary>
/// Helper to analyze dumps using dotnet-symbol --verifycore and file command.
/// </summary>
public static class DumpAnalyzer
{
    // Regex to match .NET runtime paths like:
    // /usr/share/dotnet/shared/Microsoft.NETCore.App/9.0.10/System.Runtime.dll
    // /dotnet/shared/Microsoft.NETCore.App/8.0.5/libcoreclr.so
    private static readonly System.Text.RegularExpressions.Regex RuntimeVersionRegex = new(
        @"Microsoft\.NETCore\.App[/\\](\d+\.\d+\.\d+)[/\\]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Regex to extract architecture from file command output
    // Examples:
    // "ELF 64-bit LSB core file, ARM aarch64" -> arm64
    // "ELF 64-bit LSB core file, x86-64" -> x64
    // "platform: 'aarch64'" -> arm64
    private static readonly System.Text.RegularExpressions.Regex ArchitectureRegex = new(
        @"(ARM aarch64|aarch64|x86-64|x86_64|AMD64|i386|i686|ARM,|armv7)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Analyzes a dump file to detect Alpine status, runtime version, and architecture.
    /// </summary>
    /// <param name="dumpFilePath">Path to the dump file.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>Analysis result with Alpine detection, runtime version, and architecture.</returns>
    /// <remarks>
    /// Uses:
    /// - dotnet-symbol --verifycore for Alpine/runtime detection
    /// - file command for architecture detection
    /// </remarks>
    public static async Task<DumpAnalysisResult> AnalyzeDumpAsync(string dumpFilePath, ILogger? logger = null)
    {
        var result = new DumpAnalysisResult();
        
        try
        {
            // Find dotnet-symbol tool
            var dotnetSymbolPath = FindDotnetSymbolTool();
            if (string.IsNullOrEmpty(dotnetSymbolPath))
            {
                logger?.LogWarning("dotnet-symbol tool not found, cannot analyze dump");
                return result;
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = dotnetSymbolPath,
                Arguments = $"--verifycore \"{dumpFilePath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new System.Diagnostics.Process { StartInfo = startInfo };
            var output = new System.Text.StringBuilder();
            
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    output.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();

            // Wait up to 30 seconds for the verification
            var completed = await Task.Run(() => process.WaitForExit(30000));
            
            if (!completed)
            {
                try 
                { 
                    process.Kill(); 
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Exception while killing timed-out dotnet-symbol process (may have already exited)");
                }
                finally
                {
                    process.Dispose();
                }
                logger?.LogWarning("dotnet-symbol --verifycore timed out");
                return result;
            }

            var outputStr = output.ToString();
            
            // Check for musl indicators (Alpine detection)
            // Alpine uses musl libc, which shows up as:
            // - /lib/ld-musl-aarch64.so.1 or /lib/ld-musl-x86_64.so.1
            // - linux-musl-arm64 or linux-musl-x64 in native library paths
            result.IsAlpine = outputStr.Contains("/ld-musl-", StringComparison.OrdinalIgnoreCase) ||
                              outputStr.Contains("linux-musl-", StringComparison.OrdinalIgnoreCase) ||
                              outputStr.Contains("/musl-", StringComparison.OrdinalIgnoreCase);

            // Extract runtime version from paths like:
            // /usr/share/dotnet/shared/Microsoft.NETCore.App/9.0.10/System.Runtime.dll
            var match = RuntimeVersionRegex.Match(outputStr);
            if (match.Success)
            {
                result.RuntimeVersion = match.Groups[1].Value;
            }

            // Detect architecture using file command
            result.Architecture = await DetectArchitectureAsync(dumpFilePath, logger);

            logger?.LogInformation("Dump analysis for {DumpFile}: IsAlpine={IsAlpine}, RuntimeVersion={RuntimeVersion}, Architecture={Architecture}", 
                System.IO.Path.GetFileName(dumpFilePath), result.IsAlpine, result.RuntimeVersion ?? "(not detected)", result.Architecture ?? "(not detected)");

            return result;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to analyze dump");
            return result;
        }
    }

    /// <summary>
    /// Finds the dotnet-symbol tool in common locations.
    /// </summary>
    private static string? FindDotnetSymbolTool()
    {
        // Check PATH first
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var toolPath = Path.Combine(dir, "dotnet-symbol");
            if (File.Exists(toolPath))
            {
                return toolPath;
            }
        }

        // Check common tool locations
        var toolLocations = new[]
        {
            "/tools/dotnet-symbol",  // Docker container location
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "dotnet-symbol"),
            "/usr/local/bin/dotnet-symbol",
            "/usr/bin/dotnet-symbol"
        };

        foreach (var location in toolLocations)
        {
            if (File.Exists(location))
            {
                return location;
            }
        }

        return null;
    }

    /// <summary>
    /// Detects the processor architecture of a dump file using the file command.
    /// </summary>
    /// <param name="dumpFilePath">Path to the dump file.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>The architecture (e.g., "arm64", "x64") or null if detection failed.</returns>
    private static async Task<string?> DetectArchitectureAsync(string dumpFilePath, ILogger? logger)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "file",
                Arguments = $"\"{dumpFilePath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new System.Diagnostics.Process { StartInfo = startInfo };
            var output = new System.Text.StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    output.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();

            // Wait up to 10 seconds for the file command
            var completed = await Task.Run(() => process.WaitForExit(10000));

            if (!completed)
            {
                try 
                { 
                    process.Kill(); 
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Exception while killing timed-out file command process (may have already exited)");
                }
                finally
                {
                    process.Dispose();
                }
                logger?.LogWarning("file command timed out");
                return null;
            }

            var outputStr = output.ToString();
            
            // Parse architecture from file output
            // Examples:
            // "ELF 64-bit LSB core file, ARM aarch64, version 1 (GNU/Linux)"
            // "ELF 64-bit LSB core file, x86-64, version 1 (GNU/Linux)"
            // "PE32+ executable (console) x86-64" (Windows)
            var match = ArchitectureRegex.Match(outputStr);
            if (match.Success)
            {
                var arch = match.Groups[1].Value.ToLowerInvariant();
                
                // Normalize architecture names
                var normalizedArch = arch switch
                {
                    "arm aarch64" or "aarch64" => "arm64",
                    "x86-64" or "x86_64" or "amd64" => "x64",
                    "i386" or "i686" => "x86",
                    "arm," or "armv7" => "arm",
                    _ => arch
                };

                logger?.LogDebug("Detected architecture: {Architecture} (raw: {RawArch})", normalizedArch, arch);
                return normalizedArch;
            }

            logger?.LogDebug("Could not detect architecture from file output: {Output}", outputStr);
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to detect architecture using file command");
            return null;
        }
    }
}

