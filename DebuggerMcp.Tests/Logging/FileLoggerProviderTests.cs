using DebuggerMcp.Logging;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.Tests.Logging;

public class FileLoggerProviderTests
{
    [Fact]
    public void CreateLogger_ReusesSameCategoryInstance()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            using var provider = new FileLoggerProvider(tempDir);

            var a1 = provider.CreateLogger("My.Category");
            var a2 = provider.CreateLogger("My.Category");
            var b = provider.CreateLogger("Other.Category");

            Assert.Same(a1, a2);
            Assert.NotSame(a1, b);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Log_WritesToFile_WithExpectedPrefix()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            using var provider = new FileLoggerProvider(tempDir, filePrefix: "test", minimumLevel: LogLevel.Information);
            var logger = provider.CreateLogger("My.Namespace.MyClass");

            logger.LogInformation("Hello {Value}", 123);

            var logFile = Directory.GetFiles(tempDir, "test-*.log", SearchOption.TopDirectoryOnly).Single();
            var content = File.ReadAllText(logFile);

            Assert.Contains("Log started", content);
            Assert.Contains("[INF] [MyClass] Hello 123", content);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Log_BelowMinimumLevel_DoesNotWriteMessage()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            using var provider = new FileLoggerProvider(tempDir, filePrefix: "test", minimumLevel: LogLevel.Warning);
            var logger = provider.CreateLogger("My.Namespace.MyClass");

            logger.LogInformation("This should be ignored");
            logger.LogWarning("This should be written");

            var logFile = Directory.GetFiles(tempDir, "test-*.log", SearchOption.TopDirectoryOnly).Single();
            var content = File.ReadAllText(logFile);

            Assert.DoesNotContain("This should be ignored", content);
            Assert.Contains("This should be written", content);
        }
        finally
        {
            SafeDeleteDirectory(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SafeDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}

