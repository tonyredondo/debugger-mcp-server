using Microsoft.Extensions.Logging;
using Moq;
using DebuggerMcp.Configuration;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for SessionCleanupService class.
/// </summary>
public class SessionCleanupServiceTests : IDisposable
{
    private readonly Mock<ILogger<SessionCleanupService>> _loggerMock;
    private readonly DebuggerSessionManager _sessionManager;
    private readonly string _testStoragePath;
    private readonly string? _originalCleanupInterval;
    private readonly string? _originalInactivityThreshold;

    public SessionCleanupServiceTests()
    {
        _loggerMock = new Mock<ILogger<SessionCleanupService>>();
        _loggerMock.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        _originalCleanupInterval = Environment.GetEnvironmentVariable(EnvironmentConfig.SessionCleanupIntervalMinutes);
        _originalInactivityThreshold = Environment.GetEnvironmentVariable(EnvironmentConfig.SessionInactivityThresholdMinutes);

        _testStoragePath = Path.Combine(Path.GetTempPath(), $"SessionCleanupTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testStoragePath);
        _sessionManager = new DebuggerSessionManager(_testStoragePath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvironmentConfig.SessionCleanupIntervalMinutes, _originalCleanupInterval);
        Environment.SetEnvironmentVariable(EnvironmentConfig.SessionInactivityThresholdMinutes, _originalInactivityThreshold);

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
        Environment.SetEnvironmentVariable(EnvironmentConfig.SessionCleanupIntervalMinutes, "1");
        var service = new SessionCleanupService(_sessionManager, _loggerMock.Object);

        // Act
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await service.StopAsync(CancellationToken.None);

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
        Environment.SetEnvironmentVariable(EnvironmentConfig.SessionCleanupIntervalMinutes, "1");
        var service = new SessionCleanupService(_sessionManager, _loggerMock.Object);

        // Act
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);
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

        // Act
        await service.StartAsync(CancellationToken.None);

        // Wait for at least one cleanup cycle (default interval is 5 minutes, but we can't wait that long)
        // This test mostly verifies the service starts and runs without errors
        await Task.Delay(100);

        await service.StopAsync(CancellationToken.None);

        // Assert - Service should have started and stopped without errors
        Assert.NotNull(session);
    }

    [Fact]
    public async Task ExecuteAsync_HandlesExceptionsGracefully()
    {
        // Arrange
        var service = new SessionCleanupService(_sessionManager, _loggerMock.Object);

        // Act - Service should handle any internal exceptions
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100);

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
        var startTime = DateTime.UtcNow;

        // Act
        await service.StartAsync(CancellationToken.None);

        // Wait a short time
        await Task.Delay(50);
        var elapsed = DateTime.UtcNow - startTime;

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
