using DebuggerMcp.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.Tests.Logging;

/// <summary>
/// Unit tests for <see cref="FileLoggerExtensions"/>.
/// </summary>
public class FileLoggerExtensionsTests
{
    [Fact]
    public void GetLogStoragePath_WhenEnvVarSet_ReturnsEnvVar()
    {
        var original = Environment.GetEnvironmentVariable("LOG_STORAGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("LOG_STORAGE_PATH", "/tmp/debugger-mcp-logs");

            var path = FileLoggerExtensions.GetLogStoragePath();

            Assert.Equal("/tmp/debugger-mcp-logs", path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOG_STORAGE_PATH", original);
        }
    }

    [Fact]
    public void GetLogStoragePath_WhenEnvVarNotSet_ReturnsDefaultUnderBaseDirectory()
    {
        var original = Environment.GetEnvironmentVariable("LOG_STORAGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("LOG_STORAGE_PATH", null);

            var path = FileLoggerExtensions.GetLogStoragePath();

            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "logs"), path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOG_STORAGE_PATH", original);
        }
    }

    [Fact]
    public void AddFileLogger_RegistersILoggerProvider()
    {
        var services = new ServiceCollection();
        var builder = new LoggingBuilder(services);

        builder.AddFileLogger(logDirectory: "/tmp", filePrefix: "test", minimumLevel: LogLevel.Debug);

        using var provider = services.BuildServiceProvider();
        var providers = provider.GetServices<ILoggerProvider>().ToList();

        Assert.Contains(providers, p => p.GetType().Name.Contains("FileLoggerProvider", StringComparison.Ordinal));
    }

    private sealed class LoggingBuilder(IServiceCollection services) : ILoggingBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}

