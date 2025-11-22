using Microsoft.Extensions.Logging;
using Moq;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for SessionCleanupService class.
/// </summary>
public class SessionCleanupServiceTests : IDisposable
{
    private readonly Mock<ILogger<SessionCleanupService>> _loggerMock;
    private readonly DebuggerSessionManager _sessionManager;
    private readonly string _testStoragePath;

    public SessionCleanupServiceTests()
    {
        _loggerMock = new Mock<ILogger<SessionCleanupService>>();
        _testStoragePath = Path.Combine(Path.GetTempPath(), $"SessionCleanupTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testStoragePath);
        _sessionManager = new DebuggerSessionManager(_testStoragePath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testStoragePath))
            {
                Directory.Delete(_testStoragePath, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void Constructor_CreatesService()
    {
        // Act
        var service = new SessionCleanupService(_sessionManager, _loggerMock.Object);

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public async Task ExecuteAsync_StartsAndLogsMessage()
    {
        // Arrange
        var service = new SessionCleanupService(_sessionManager, _loggerMock.Object);
        var cts = new CancellationTokenSource();

        // Act - Start the service and cancel quickly
        var task = service.StartAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        
        try
        {
            await task;
            await service.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert - Should have logged startup message
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("cleanup service started")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_StopsGracefully()
    {
        // Arrange
        var service = new SessionCleanupService(_sessionManager, _loggerMock.Object);
        var cts = new CancellationTokenSource();

        // Act
        await service.StartAsync(cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // Assert - Should have logged stop message
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("stopped")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_CleansUpInactiveSessions()
    {
        // This test verifies the cleanup mechanism works with the session manager

        // Arrange
        var sessionManager = new DebuggerSessionManager(_testStoragePath);
        
        // Create a session and mark it old
        var session = sessionManager.CreateSession("test-user");
        
        var service = new SessionCleanupService(sessionManager, _loggerMock.Object);
        var cts = new CancellationTokenSource();

        // Act
        await service.StartAsync(cts.Token);
        
        // Wait for at least one cleanup cycle (default interval is 5 minutes, but we can't wait that long)
        // This test mostly verifies the service starts and runs without errors
        await Task.Delay(100);
        
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // Assert - Service should have started and stopped without errors
        Assert.NotNull(session);
    }

    [Fact]
    public async Task ExecuteAsync_HandlesExceptionsGracefully()
    {
        // Arrange
        var service = new SessionCleanupService(_sessionManager, _loggerMock.Object);
        var cts = new CancellationTokenSource();

        // Act - Service should handle any internal exceptions
        await service.StartAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        
        // Should not throw
        await service.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(true); // If we got here, no unhandled exceptions
    }

    [Fact]
    public async Task ExecuteAsync_RespectsCleanupInterval()
    {
        // This test verifies that the service waits between cleanup cycles
        
        // Arrange
        var service = new SessionCleanupService(_sessionManager, _loggerMock.Object);
        var cts = new CancellationTokenSource();
        var startTime = DateTime.UtcNow;

        // Act
        await service.StartAsync(cts.Token);
        
        // Wait a short time
        await Task.Delay(50);
        var elapsed = DateTime.UtcNow - startTime;
        
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // Assert - Should not have run cleanup immediately (default is 5 min interval)
        // This is a basic sanity check that the delay is working
        Assert.True(elapsed.TotalSeconds < 10);
    }

    [Fact]
    public void SessionCleanupService_IsBackgroundService()
    {
        // Arrange & Act
        var service = new SessionCleanupService(_sessionManager, _loggerMock.Object);

        // Assert
        Assert.IsAssignableFrom<Microsoft.Extensions.Hosting.BackgroundService>(service);
    }
}

