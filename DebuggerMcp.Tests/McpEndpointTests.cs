using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using DebuggerMcp.McpTools;
using DebuggerMcp.Watches;
using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for the MCP endpoint registration and configuration.
/// </summary>
/// <remarks>
/// These tests verify that MCP services and tool types can be registered.
/// Endpoint mapping itself is covered by the upstream ModelContextProtocol.AspNetCore package.
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
    /// Verifies that the MCP server can be configured with HTTP transport and tool/resource discovery.
    /// </summary>
    /// <remarks>
    /// This avoids building a full <c>WebApplication</c> because hosting startup can hang
    /// in some constrained CI/sandbox environments.
    /// </remarks>
    [Fact]
    public void McpServer_ConfiguresWithHttpTransportAndTools()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDebuggerServices(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        // Act
        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly();

        // Assert
        // Build without validation: the HTTP transport registers some ASP.NET hosting services
        // that depend on WebApplication/WebHost defaults.
        using var provider = services.BuildServiceProvider();
        Assert.True(
            services.Any(d => d.ServiceType.Namespace?.StartsWith("ModelContextProtocol", StringComparison.Ordinal) == true),
            "Expected MCP services to be registered.");
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
        var compactTools = new CompactTools(sessionManager, symbolManager, watchStore, NullLoggerFactory.Instance);

        // Assert
        Assert.NotNull(compactTools);
    }

    /// <summary>
    /// Verifies that the compact tool surface exists and is discoverable by the MCP server.
    /// </summary>
    [Fact]
    public void McpToolClasses_HavePublicMethods()
    {
        var methods = typeof(CompactTools).GetMethods(
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.DeclaredOnly);

        Assert.NotEmpty(methods);
    }


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

}
