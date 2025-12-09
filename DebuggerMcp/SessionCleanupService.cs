using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DebuggerMcp.Configuration;

namespace DebuggerMcp;

/// <summary>
/// Background service that periodically cleans up inactive debugging sessions.
/// </summary>
/// <remarks>
/// This service runs in the background and removes sessions that haven't been
/// accessed within the configured inactivity threshold. This prevents resource
/// exhaustion from abandoned sessions.
/// 
/// Configuration via environment variables (see <see cref="EnvironmentConfig"/>):
/// - SESSION_CLEANUP_INTERVAL_MINUTES: Interval between cleanup runs (default: 5)
/// - SESSION_INACTIVITY_THRESHOLD_MINUTES: Sessions inactive longer than this are cleaned up (default: 60)
/// </remarks>
public class SessionCleanupService : BackgroundService
{
    private readonly DebuggerSessionManager _sessionManager;
    private readonly ILogger<SessionCleanupService> _logger;

    /// <summary>
    /// The interval between cleanup runs.
    /// </summary>
    private readonly TimeSpan _cleanupInterval;

    /// <summary>
    /// Sessions inactive for longer than this threshold will be cleaned up.
    /// </summary>
    private readonly TimeSpan _inactivityThreshold;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionCleanupService"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager to clean up.</param>
    /// <param name="logger">The logger instance.</param>
    public SessionCleanupService(DebuggerSessionManager sessionManager, ILogger<SessionCleanupService> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;

        // Read configuration from centralized EnvironmentConfig
        _cleanupInterval = EnvironmentConfig.GetSessionCleanupInterval();
        _inactivityThreshold = EnvironmentConfig.GetSessionInactivityThreshold();
    }

    /// <summary>
    /// Executes the background cleanup task.
    /// </summary>
    /// <param name="stoppingToken">Token to signal when the service should stop.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Session cleanup service started. Cleanup interval: {Interval}, Inactivity threshold: {Threshold}",
            _cleanupInterval, _inactivityThreshold);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait between cleanup passes to avoid a tight loop and respect configured cadence.
                await Task.Delay(_cleanupInterval, stoppingToken);

                var cleanedCount = _sessionManager.CleanupInactiveSessions(_inactivityThreshold);

                if (cleanedCount > 0)
                {
                    // Only log when work was done to reduce noise on idle periods.
                    _logger.LogInformation("Cleaned up {Count} inactive session(s)", cleanedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Service is stopping; swallow cancellation to allow graceful exit.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during session cleanup");
            }
        }

        _logger.LogInformation("Session cleanup service stopped");
    }
}
