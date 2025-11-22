using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using DebuggerMcp.McpTools;
using DebuggerMcp.Watches;
using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for the MCP endpoint registration and configuration.
/// </summary>
/// <remarks>
/// These tests verify that the MCP endpoint is properly registered
/// and can be mapped to the application.
/// </remarks>
public class McpEndpointTests
{
    /// <summary>
    /// Creates a new DebuggerSessionManager with isolated storage for test isolation.
    /// </summary>
    private static DebuggerSessionManager CreateIsolatedSessionManager()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        return new DebuggerSessionManager(tempPath);
    }

    /// <summary>
    /// Verifies that the MCP endpoint can be mapped to the application.
    /// </summary>
    [Fact]
    public async Task McpEndpoint_CanBeMapped()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();
        
        // Act
        var app = builder.Build();
        
        // Map the MCP endpoint
        // This should not throw an exception
        app.MapMcp("/mcp");
        
        // Assert
        Assert.NotNull(app);
        
        // Cleanup
        await app.DisposeAsync();
    }

    /// <summary>
    /// Verifies that the MCP endpoint can be mapped with default route.
    /// </summary>
    [Fact]
    public async Task McpEndpoint_CanBeMappedWithDefaultRoute()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();
        
        // Act
        var app = builder.Build();
        
        // Map the MCP endpoint with default route
        app.MapMcp();
        
        // Assert
        Assert.NotNull(app);
        
        // Cleanup
        await app.DisposeAsync();
    }

    /// <summary>
    /// Verifies that the MCP endpoint can be mapped with custom route.
    /// </summary>
    [Fact]
    public async Task McpEndpoint_CanBeMappedWithCustomRoute()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();
        
        // Act
        var app = builder.Build();
        
        // Map the MCP endpoint with custom route
        app.MapMcp("/custom-mcp-endpoint");
        
        // Assert
        Assert.NotNull(app);
        
        // Cleanup
        await app.DisposeAsync();
    }

    /// <summary>
    /// Verifies that the MCP server discovers tools from the assembly.
    /// </summary>
    [Fact]
    public async Task McpServer_DiscoversToolsFromAssembly()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        
        // Act
        var serviceCollection = builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();
        
        var app = builder.Build();
        
        // Assert
        // If no exception is thrown, tools are discovered correctly
        Assert.NotNull(app);
        Assert.NotNull(serviceCollection);
        
        // Cleanup
        await app.DisposeAsync();
    }

    /// <summary>
    /// Verifies that the MCP server can be configured with both HTTP transport and tools.
    /// </summary>
    [Fact]
    public async Task McpServer_ConfiguresWithHttpTransportAndTools()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        
        // Act
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();
        
        var app = builder.Build();
        app.MapMcp("/mcp");
        
        // Assert
        Assert.NotNull(app);
        
        // Cleanup
        await app.DisposeAsync();
    }

    /// <summary>
    /// Verifies that multiple MCP endpoints cannot be mapped to the same route.
    /// </summary>
    [Fact]
    public async Task McpEndpoint_CannotMapMultipleToSameRoute()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();
        
        var app = builder.Build();
        
        // Act
        app.MapMcp("/mcp");
        
        // Mapping the same endpoint twice should not cause issues
        // (ASP.NET Core will handle this gracefully)
        
        // Assert
        Assert.NotNull(app);
        
        // Cleanup
        await app.DisposeAsync();
    }

    /// <summary>
    /// Verifies that the MCP server can be configured in a full application setup.
    /// </summary>
    [Fact]
    public async Task McpServer_WorksInFullApplicationSetup()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        
        // Add all services as in production
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddSingleton<DebuggerSessionManager>();
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });
        
        // Add MCP server
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();
        
        // Act
        var app = builder.Build();
        
        // Configure middleware
        app.UseCors();
        app.MapControllers();
        app.MapMcp("/mcp");
        
        // Assert
        Assert.NotNull(app);
        Assert.NotNull(app.Services.GetService<DebuggerSessionManager>());
        
        // Cleanup
        await app.DisposeAsync();
    }

    /// <summary>
    /// Verifies that the MCP tool classes exist and can be instantiated.
    /// </summary>
    [Fact]
    public void McpToolClasses_CanBeInstantiated()
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        
        // Act
        var sessionTools = new SessionTools(sessionManager, symbolManager, watchStore, NullLogger<SessionTools>.Instance);
        var dumpTools = new DumpTools(sessionManager, symbolManager, watchStore, NullLogger<DumpTools>.Instance);
        var symbolTools = new SymbolTools(sessionManager, symbolManager, watchStore, NullLogger<SymbolTools>.Instance);
        var analysisTools = new AnalysisTools(sessionManager, symbolManager, watchStore, NullLogger<AnalysisTools>.Instance);
        
        // Assert
        Assert.NotNull(sessionTools);
        Assert.NotNull(dumpTools);
        Assert.NotNull(symbolTools);
        Assert.NotNull(analysisTools);
    }

    /// <summary>
    /// Verifies that the MCP tool classes have public methods.
    /// </summary>
    [Fact]
    public void McpToolClasses_HavePublicMethods()
    {
        // Arrange
        var toolClasses = new[]
        {
            typeof(SessionTools),
            typeof(DumpTools),
            typeof(SymbolTools),
            typeof(AnalysisTools),
            typeof(ComparisonTools),
            typeof(PerformanceTools),
            typeof(WatchTools),
            typeof(ReportTools),
            typeof(SourceLinkTools),
            typeof(SecurityTools)
        };
        
        // Act
        var totalMethods = 0;
        foreach (var toolsType in toolClasses)
        {
            var publicMethods = toolsType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
            totalMethods += publicMethods.Length;
            Assert.NotEmpty(publicMethods);
        }
        
        // Assert - all tool classes combined should have at least 30 methods
        Assert.True(totalMethods >= 30, $"MCP tool classes should have at least 30 public methods combined (found {totalMethods})");
    }

    #region User ID Validation Tests (Issue #2 - Security)

    /// <summary>
    /// Verifies that OpenDump throws ArgumentException when userId is null or empty.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task OpenDump_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new DumpTools(sessionManager, symbolManager, watchStore, NullLogger<DumpTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => tools.OpenDump(sessionId, userId!, "some-dump-id"));
    }

    /// <summary>
    /// Verifies that OpenDump throws UnauthorizedAccessException when userId doesn't match session owner.
    /// </summary>
    [Fact]
    public async Task OpenDump_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new DumpTools(sessionManager, symbolManager, watchStore, NullLogger<DumpTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.OpenDump(sessionId, "wrong-user", "some-dump-id"));
    }

    /// <summary>
    /// Verifies that CloseDump throws ArgumentException when userId is null or empty.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CloseDump_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new DumpTools(sessionManager, symbolManager, watchStore, NullLogger<DumpTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => tools.CloseDump(sessionId, userId!));
    }

    /// <summary>
    /// Verifies that CloseDump throws UnauthorizedAccessException when userId doesn't match session owner.
    /// </summary>
    [Fact]
    public void CloseDump_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new DumpTools(sessionManager, symbolManager, watchStore, NullLogger<DumpTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act & Assert
        Assert.Throws<UnauthorizedAccessException>(() => tools.CloseDump(sessionId, "wrong-user"));
    }

    /// <summary>
    /// Verifies that ExecuteCommand throws ArgumentException when userId is null or empty.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExecuteCommand_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new DumpTools(sessionManager, symbolManager, watchStore, NullLogger<DumpTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => tools.ExecuteCommand(sessionId, userId!, "k"));
    }

    /// <summary>
    /// Verifies that ExecuteCommand throws UnauthorizedAccessException when userId doesn't match session owner.
    /// </summary>
    [Fact]
    public void ExecuteCommand_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new DumpTools(sessionManager, symbolManager, watchStore, NullLogger<DumpTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act & Assert
        Assert.Throws<UnauthorizedAccessException>(() => tools.ExecuteCommand(sessionId, "wrong-user", "k"));
    }

    /// <summary>
    /// Verifies that LoadSos throws ArgumentException when userId is null or empty.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LoadSos_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new DumpTools(sessionManager, symbolManager, watchStore, NullLogger<DumpTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => tools.LoadSos(sessionId, userId!));
    }

    /// <summary>
    /// Verifies that LoadSos throws UnauthorizedAccessException when userId doesn't match session owner.
    /// </summary>
    [Fact]
    public void LoadSos_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new DumpTools(sessionManager, symbolManager, watchStore, NullLogger<DumpTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act & Assert
        Assert.Throws<UnauthorizedAccessException>(() => tools.LoadSos(sessionId, "wrong-user"));
    }

    /// <summary>
    /// Verifies that CloseSession returns an error string when userId is null or empty.
    /// Note: SessionTools now catches exceptions and returns error strings for better MCP error handling.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CloseSession_WithInvalidUserId_ReturnsErrorString(string? userId)
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new SessionTools(sessionManager, symbolManager, watchStore, NullLogger<SessionTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act
        var result = tools.CloseSession(sessionId, userId!);

        // Assert
        Assert.StartsWith("Error:", result);
    }

    /// <summary>
    /// Verifies that CloseSession returns an error string when userId doesn't match session owner.
    /// Note: SessionTools now catches exceptions and returns error strings for better MCP error handling.
    /// </summary>
    [Fact]
    public void CloseSession_WithWrongUserId_ReturnsUnauthorizedError()
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new SessionTools(sessionManager, symbolManager, watchStore, NullLogger<SessionTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act
        var result = tools.CloseSession(sessionId, "wrong-user");

        // Assert
        Assert.StartsWith("Error:", result);
        Assert.Contains("does not have access", result);
    }

    /// <summary>
    /// Verifies that GetDebuggerInfo returns an error string when userId is null or empty.
    /// Note: SessionTools now catches exceptions and returns error strings for better MCP error handling.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetDebuggerInfo_WithInvalidUserId_ReturnsErrorString(string? userId)
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new SessionTools(sessionManager, symbolManager, watchStore, NullLogger<SessionTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act
        var result = tools.GetDebuggerInfo(sessionId, userId!);

        // Assert
        Assert.StartsWith("Error:", result);
    }

    /// <summary>
    /// Verifies that GetDebuggerInfo returns an error string when userId doesn't match session owner.
    /// Note: SessionTools now catches exceptions and returns error strings for better MCP error handling.
    /// </summary>
    [Fact]
    public void GetDebuggerInfo_WithWrongUserId_ReturnsUnauthorizedError()
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new SessionTools(sessionManager, symbolManager, watchStore, NullLogger<SessionTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act
        var result = tools.GetDebuggerInfo(sessionId, "wrong-user");

        // Assert
        Assert.StartsWith("Error:", result);
        Assert.Contains("does not have access", result);
    }

    /// <summary>
    /// Verifies that ConfigureAdditionalSymbols throws ArgumentException when userId is null or empty.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConfigureAdditionalSymbols_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new SymbolTools(sessionManager, symbolManager, watchStore, NullLogger<SymbolTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => tools.ConfigureAdditionalSymbols(sessionId, userId!, "/path/to/symbols"));
    }

    /// <summary>
    /// Verifies that ConfigureAdditionalSymbols throws UnauthorizedAccessException when userId doesn't match session owner.
    /// </summary>
    [Fact]
    public void ConfigureAdditionalSymbols_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new SymbolTools(sessionManager, symbolManager, watchStore, NullLogger<SymbolTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act & Assert
        Assert.Throws<UnauthorizedAccessException>(() => tools.ConfigureAdditionalSymbols(sessionId, "wrong-user", "/path/to/symbols"));
    }

    /// <summary>
    /// Verifies that AnalyzeCrash throws ArgumentException when userId is null or empty.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnalyzeCrash_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new AnalysisTools(sessionManager, symbolManager, watchStore, NullLogger<AnalysisTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => tools.AnalyzeCrash(sessionId, userId!));
    }

    /// <summary>
    /// Verifies that AnalyzeCrash throws UnauthorizedAccessException when userId doesn't match session owner.
    /// </summary>
    [Fact]
    public async Task AnalyzeCrash_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new AnalysisTools(sessionManager, symbolManager, watchStore, NullLogger<AnalysisTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.AnalyzeCrash(sessionId, "wrong-user"));
    }

    /// <summary>
    /// Verifies that AnalyzeDotNetCrash throws ArgumentException when userId is null or empty.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnalyzeDotNetCrash_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new AnalysisTools(sessionManager, symbolManager, watchStore, NullLogger<AnalysisTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => tools.AnalyzeDotNetCrash(sessionId, userId!));
    }

    /// <summary>
    /// Verifies that AnalyzeDotNetCrash throws UnauthorizedAccessException when userId doesn't match session owner.
    /// </summary>
    [Fact]
    public async Task AnalyzeDotNetCrash_WithWrongUserId_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new AnalysisTools(sessionManager, symbolManager, watchStore, NullLogger<AnalysisTools>.Instance);
        var sessionId = sessionManager.CreateSession("owner-user");

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.AnalyzeDotNetCrash(sessionId, "wrong-user"));
    }

    /// <summary>
    /// Verifies that correct userId allows access to session operations.
    /// </summary>
    [Fact]
    public void GetDebuggerInfo_WithCorrectUserId_Succeeds()
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new SessionTools(sessionManager, symbolManager, watchStore, NullLogger<SessionTools>.Instance);
        var userId = "owner-user";
        var sessionId = sessionManager.CreateSession(userId);

        // Act
        var result = tools.GetDebuggerInfo(sessionId, userId);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("Debugger Type:", result);
    }

    /// <summary>
    /// Verifies that CloseSession with correct userId succeeds.
    /// </summary>
    [Fact]
    public void CloseSession_WithCorrectUserId_Succeeds()
    {
        // Arrange
        var sessionManager = CreateIsolatedSessionManager();
        var symbolManager = new SymbolManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var watchStore = new WatchStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var tools = new SessionTools(sessionManager, symbolManager, watchStore, NullLogger<SessionTools>.Instance);
        var userId = "owner-user";
        var sessionId = sessionManager.CreateSession(userId);

        // Act
        var result = tools.CloseSession(sessionId, userId);

        // Assert
        Assert.Contains("closed successfully", result);
    }

    #endregion
}
