using DebuggerMcp.Models;
using Microsoft.AspNetCore.Mvc;

namespace DebuggerMcp.Controllers;

/// <summary>
/// Controller for server information and capabilities.
/// </summary>
/// <remarks>
/// Provides endpoints for clients to discover server characteristics,
/// which is essential for the CLI to match dumps to appropriate servers.
/// </remarks>
[ApiController]
[Route("api/server")]
public class ServerController : ControllerBase
{
    private readonly ILogger<ServerController> _logger;

    // Cache the capabilities since they don't change at runtime
    private static readonly ServerCapabilities _capabilities = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerController"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public ServerController(ILogger<ServerController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the server capabilities and characteristics.
    /// </summary>
    /// <returns>Server capabilities including platform, architecture, and Alpine status.</returns>
    /// <remarks>
    /// This endpoint is used by the CLI to:
    /// - Discover server characteristics at startup
    /// - Match dumps to appropriate servers based on architecture and Alpine status
    /// - Display server information in the `server list` command
    /// </remarks>
    /// <response code="200">Returns the server capabilities.</response>
    [HttpGet("capabilities")]
    [ProducesResponseType(typeof(ServerCapabilities), StatusCodes.Status200OK)]
    public ActionResult<ServerCapabilities> GetCapabilities()
    {
        // Log at debug to aid diagnostics without polluting normal logs.
        _logger.LogDebug("Server capabilities requested: Platform={Platform}, Arch={Arch}, Alpine={IsAlpine}",
            _capabilities.Platform, _capabilities.Architecture, _capabilities.IsAlpine);

        return Ok(_capabilities);
    }

    /// <summary>
    /// Gets a simple server info summary.
    /// </summary>
    /// <returns>Brief server information.</returns>
    [HttpGet("info")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public ActionResult GetInfo()
    {
        return Ok(new
        {
            name = $"{(_capabilities.IsAlpine ? "alpine" : _capabilities.Distribution ?? "linux")}-{_capabilities.Architecture}",
            version = _capabilities.Version,
            platform = _capabilities.Platform,
            architecture = _capabilities.Architecture,
            isAlpine = _capabilities.IsAlpine,
            debuggerType = _capabilities.DebuggerType
        });
    }
}
