using System.ComponentModel;
using System.Text.Json;
using DebuggerMcp.SourceLink;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DebuggerMcp.McpTools;

/// <summary>
/// MCP tools for Source Link integration.
/// </summary>
/// <remarks>
/// Provides tools for:
/// <list type="bullet">
/// <item><description>Resolving source files to browsable URLs (GitHub, GitLab, etc.)</description></item>
/// <item><description>Getting Source Link document mappings from PDBs</description></item>
/// </list>
/// 
/// Source Link allows linking crash locations to the exact source code version
/// in version control systems.
/// </remarks>
[McpServerToolType]
public class SourceLinkTools(
    DebuggerSessionManager sessionManager,
    SymbolManager symbolManager,
    WatchStore watchStore,
    ILogger<SourceLinkTools> logger)
    : DebuggerToolsBase(sessionManager, symbolManager, watchStore, logger)
{
    /// <summary>
    /// JSON serialization options for Source Link results.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Resolves a source file path to a browsable Source Link URL.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <param name="sourceFile">The source file path from a stack frame.</param>
    /// <param name="lineNumber">Optional line number to include in the URL.</param>
    /// <returns>The resolved Source Link URL or an error message.</returns>
    /// <remarks>
    /// This tool attempts to resolve a source file path (from a stack frame) to a
    /// browsable URL in a version control system like GitHub, GitLab, or Azure DevOps.
    /// 
    /// The resolution uses Source Link information embedded in PDB files that are
    /// either uploaded with the dump or downloaded from symbol servers.
    /// 
    /// Supported providers:
    /// - GitHub (github.com)
    /// - GitLab (gitlab.com)
    /// - Azure DevOps (dev.azure.com)
    /// - Bitbucket (bitbucket.org)
    /// </remarks>
    [McpServerTool, Description("Resolve a source file to a browsable Source Link URL (GitHub, GitLab, etc.)")]
    public string ResolveSourceLink(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId,
        [Description("Source file path from a stack frame")] string sourceFile,
        [Description("Optional line number to include in the URL")] int? lineNumber = null)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);

        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        // Validate sourceFile is not empty
        if (string.IsNullOrWhiteSpace(sourceFile))
        {
            // Fail early rather than handing a meaningless lookup to the resolver
            throw new ArgumentException("sourceFile cannot be null or empty", nameof(sourceFile));
        }

        // Get the session to validate ownership
        var session = GetSessionInfo(sessionId, sanitizedUserId);

        // Create Source Link resolver with symbol paths
        var resolver = new SourceLinkResolver(Logger);
        if (!string.IsNullOrEmpty(session.CurrentDumpId))
        {
            // Symbol path is .symbols_{dumpId} folder where dotnet-symbol downloads PDBs
            var dumpIdWithoutExt = Path.GetFileNameWithoutExtension(session.CurrentDumpId);
            var symbolPath = Path.Combine(SessionManager.GetDumpStoragePath(), sanitizedUserId, $".symbols_{dumpIdWithoutExt}");
            Logger.LogInformation("[SourceLinkTools] Looking for symbols in: {SymbolPath}", symbolPath);
            if (Directory.Exists(symbolPath))
            {
                resolver.AddSymbolSearchPath(symbolPath);
            }
            else
            {
                // Warn so the user knows Source Link may fail due to missing PDBs
                Logger.LogWarning("[SourceLinkTools] Symbol path does not exist: {SymbolPath}", symbolPath);
            }
        }

        // Try to resolve the source file using the Resolve method
        // The Resolve method returns a SourceLocation object
        var location = resolver.Resolve(string.Empty, sourceFile, lineNumber ?? 0);

        // Return result with helpful message based on whether resolution succeeded
        if (location.Resolved && !string.IsNullOrEmpty(location.Url))
        {
            return $"Source Link URL: {location.Url}\n" +
                   $"Provider: {location.Provider}\n" +
                   $"Source File: {sourceFile}" +
                   (lineNumber.HasValue ? $"\nLine: {lineNumber}" : "");
        }
        else
        {
            // Provide guidance so the caller can fix missing Source Link info
            return $"Could not resolve Source Link for: {sourceFile}\n\n" +
                   "Possible reasons:\n" +
                   "- PDB files don't contain Source Link information\n" +
                   "- The source file path doesn't match any Source Link mapping\n" +
                   "- Symbol files haven't been uploaded for this dump\n\n" +
                   "Tip: Upload PDB files with Source Link information using the HTTP API.";
        }
    }

    /// <summary>
    /// Gets Source Link capabilities and configuration information.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="userId">The user ID that owns the session.</param>
    /// <returns>Source Link configuration information.</returns>
    /// <remarks>
    /// Returns information about Source Link capabilities and configured symbol paths.
    /// This is useful for debugging Source Link resolution issues.
    /// </remarks>
    [McpServerTool, Description("Get Source Link configuration and capabilities")]
    public string GetSourceLinkInfo(
        [Description("Session ID from CreateSession")] string sessionId,
        [Description("User ID that owns the session")] string userId)
    {
        // Validate input parameters
        ValidateSessionId(sessionId);

        // Sanitize userId to prevent path traversal attacks
        var sanitizedUserId = SanitizeUserId(userId);

        // Get the session to validate ownership
        var session = GetSessionInfo(sessionId, sanitizedUserId);

        // Build info about symbol paths
        // Symbol path is .symbols_{dumpId} folder where dotnet-symbol downloads PDBs
        var symbolPath = !string.IsNullOrEmpty(session.CurrentDumpId)
            ? Path.Combine(SessionManager.GetDumpStoragePath(), sanitizedUserId, $".symbols_{Path.GetFileNameWithoutExtension(session.CurrentDumpId)}")
            : null;

        var info = new
        {
            SupportedProviders = new[]
            {
                "GitHub (github.com)",
                "GitLab (gitlab.com)",
                "Azure DevOps (dev.azure.com)",
                "Bitbucket (bitbucket.org)"
            },
            SymbolSearchPaths = symbolPath != null && Directory.Exists(symbolPath)
                ? new[] { symbolPath }
                : Array.Empty<string>(),
            HasSymbolPath = symbolPath != null && Directory.Exists(symbolPath),
            CurrentDumpId = session.CurrentDumpId,
            Tips = new[]
            {
                "Upload PDB files that contain Source Link information",
                "Build with <PublishRepositoryUrl>true</PublishRepositoryUrl> in your .csproj",
                "Use Microsoft.SourceLink.GitHub or similar NuGet package"
            }
        };

        return JsonSerializer.Serialize(info, JsonOptions);
    }
}
