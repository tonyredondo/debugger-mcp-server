using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using DebuggerMcp;
using DebuggerMcp.Configuration;
using DebuggerMcp.Logging;
using DebuggerMcp.Security;

// Parse command line arguments to determine mode
var isHttpMode = args.Contains("--http") || args.Contains("--api");
var isMcpHttpMode = args.Contains("--mcp-http");

if (isHttpMode || isMcpHttpMode)
{
    // ===== HTTP MODE (Upload API + optionally MCP over HTTP) =====
    // Use HTTP when acting as an upload API or when MCP needs to be reachable over SSE
    Console.WriteLine($"Starting in HTTP mode{(isMcpHttpMode ? " with MCP over HTTP/SSE" : "")}...");

    var webBuilder = WebApplication.CreateBuilder(args);

    // Add file logging for persistent log storage
    webBuilder.Logging.AddFileLogger(minimumLevel: LogLevel.Debug);

    // Resolve dump storage path from centralized configuration
    // Priority: Configuration["DumpStoragePath"] -> DUMP_STORAGE_PATH env var -> default temp path
    var dumpStoragePath = webBuilder.Configuration["DumpStoragePath"]
        ?? EnvironmentConfig.GetDumpStoragePath();

    // Configure services using extension methods for clean, testable setup
    webBuilder.Services
        .ConfigureKestrelForLargeUploads()
        .AddDebuggerServices(dumpStoragePath)
        .AddDebuggerRateLimiting()
        .AddDebuggerCors();

    // Add controllers and API documentation
    webBuilder.Services.AddControllers();
    webBuilder.Services.AddEndpointsApiExplorer();
    webBuilder.Services.AddSwaggerGen();

    // Add API key authentication (optional - only enforced when API_KEY env var is set)
    webBuilder.Services.AddApiKeyAuthentication();
    webBuilder.Services.AddAuthorization();

    // If MCP over HTTP is requested, add MCP server with HTTP transport
    if (isMcpHttpMode)
    {
        webBuilder.Services
            .AddMcpServer()
            .WithHttpTransport() // Use HTTP/SSE transport instead of stdio
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly(); // Expose documentation and guides as MCP resources
    }

    var app = webBuilder.Build();

    // Wire up session cleanup to also clear symbol paths (prevents memory leak)
    var sessionManager = app.Services.GetRequiredService<DebuggerSessionManager>();
    var symbolManager = app.Services.GetRequiredService<SymbolManager>();
    sessionManager.OnSessionClosed = symbolManager.ClearSessionSymbolPaths;

    // Configure middleware
    // Swagger is enabled by default in development, or when ENABLE_SWAGGER=true
    var enableSwagger = app.Environment.IsDevelopment() || EnvironmentConfig.IsSwaggerEnabled();
    if (enableSwagger)
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseCors();
    app.UseRateLimiter();

    // Add authentication and authorization middleware
    app.UseAuthentication();
    app.UseAuthorization();

    // Detect host information once at startup
    var hostInfo = DebuggerMcp.Configuration.HostInfo.Detect();

    // Health check endpoint for container orchestration (Kubernetes, Docker Swarm, etc.)
    app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
        .AllowAnonymous()
        .WithName("HealthCheck")
        .WithTags("Health");

    // Server info endpoint - exposes host information for clients
    // This is important for determining which dumps can be analyzed (Alpine vs glibc)
    app.MapGet("/info", () => Results.Ok(hostInfo))
        .AllowAnonymous()
        .WithName("ServerInfo")
        .WithTags("Health")
        .WithDescription("Returns information about the server host, including OS, architecture, and installed .NET runtimes");

    // Map controllers (for dump upload API)
    app.MapControllers();

    // Map MCP endpoints if in MCP HTTP mode
    if (isMcpHttpMode)
    {
        // SSE endpoint used by MCP clients; mounted only when requested
        app.MapMcp("/mcp"); // MCP protocol endpoint at /mcp
    }

    var port = EnvironmentConfig.GetPort();
    Console.WriteLine($"HTTP server started on port {port}");
    Console.WriteLine($"  - Host: {hostInfo.Description}{(hostInfo.IsDocker ? " (Docker)" : "")}");
    Console.WriteLine($"  - Health Check: http://localhost:{port}/health");
    Console.WriteLine($"  - Server Info: http://localhost:{port}/info");
    if (enableSwagger)
    {
        Console.WriteLine($"  - Swagger UI: http://localhost:{port}/swagger");
    }
    Console.WriteLine($"  - Upload API: http://localhost:{port}/api/dumps/upload");

    if (isMcpHttpMode)
    {
        Console.WriteLine($"  - MCP Endpoint: http://localhost:{port}/mcp");
    }

    await app.RunAsync();
}
else
{
    // ===== MCP SERVER MODE (stdio) =====
    // Use stdio transport when running as a pure MCP tool host (no HTTP API)
    Console.WriteLine("Starting in MCP Server mode (stdio)...");

    var builder = Host.CreateApplicationBuilder(args);

    // Configure all logs to go to stderr (required for MCP stdio transport)
    // This prevents logs from interfering with the MCP protocol on stdout
    builder.Logging.AddConsole(consoleLogOptions =>
    {
        consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    // Add file logging for persistent log storage
    builder.Logging.AddFileLogger(minimumLevel: LogLevel.Debug);

    // Register core debugger services
    builder.Services.AddDebuggerServices();

    // Configure the MCP Server with stdio transport
    // - AddMcpServer: Registers the MCP server services
    // - WithStdioServerTransport: Uses stdin/stdout for communication
    // - WithToolsFromAssembly: Automatically discovers and registers tools with [McpServerTool] attribute
    // - WithResourcesFromAssembly: Exposes documentation and guides as MCP resources
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly()
        .WithResourcesFromAssembly();

    // Build the application
    var host = builder.Build();

    // Wire up session cleanup to also clear symbol paths (prevents memory leak)
    var sessionManager = host.Services.GetRequiredService<DebuggerSessionManager>();
    var symbolManager = host.Services.GetRequiredService<SymbolManager>();
    sessionManager.OnSessionClosed = symbolManager.ClearSessionSymbolPaths;

    // Run the MCP server
    await host.RunAsync();
}
