using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DebuggerMcp.Configuration;

namespace DebuggerMcp.Logging;

/// <summary>
/// Extension methods for adding file logging to the logging builder.
/// </summary>
public static class FileLoggerExtensions
{
    /// <summary>
    /// Adds a file logger that writes to rolling daily log files.
    /// </summary>
    /// <param name="builder">The logging builder.</param>
    /// <param name="logDirectory">Directory to store log files. Defaults to LOG_STORAGE_PATH env var or /app/logs.</param>
    /// <param name="filePrefix">Prefix for log file names. Defaults to "server".</param>
    /// <param name="minimumLevel">Minimum log level. Defaults to Information.</param>
    /// <returns>The logging builder for chaining.</returns>
    public static ILoggingBuilder AddFileLogger(
        this ILoggingBuilder builder,
        string? logDirectory = null,
        string filePrefix = "server",
        LogLevel minimumLevel = LogLevel.Information)
    {
        var logPath = logDirectory ?? GetLogStoragePath();

        builder.Services.AddSingleton<ILoggerProvider>(
            // Use singleton provider so multiple ILogger instances reuse the same writer/rotation schedule
            _ => new FileLoggerProvider(logPath, filePrefix, minimumLevel));

        return builder;
    }

    /// <summary>
    /// Gets the log storage path from environment or default.
    /// </summary>
    public static string GetLogStoragePath()
    {
        return Environment.GetEnvironmentVariable("LOG_STORAGE_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "logs");
    }
}
