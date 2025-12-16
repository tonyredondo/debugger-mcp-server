using DebuggerMcp.Cli.Configuration;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Configuration;

/// <summary>
/// Tests for persisting and resolving last-used session IDs.
/// </summary>
public class ConnectionSettingsLastSessionsTests
{
    [Fact]
    public void LastSessions_RoundTripsInConfigFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try
        {
            var settings = new ConnectionSettings
            {
                ServerUrl = "http://localhost:5000",
                UserId = "user@example.com"
            };

            settings.SetLastSessionId(settings.ServerUrl, settings.UserId, "session-123");
            settings.SaveToFile(tempFile);

            var loaded = ConnectionSettings.LoadFromFile(tempFile);
            Assert.NotNull(loaded);

            Assert.Equal("session-123", loaded!.GetLastSessionId("http://localhost:5000", "user@example.com"));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void GetLastSessionId_NormalizesServerUrl()
    {
        var settings = new ConnectionSettings { UserId = "u" };
        settings.SetLastSessionId("localhost:5000/", "u", "session-abc");

        Assert.Equal("session-abc", settings.GetLastSessionId("http://localhost:5000", "u"));
    }

    [Fact]
    public void ClearLastSessionId_RemovesOnlyWhenSessionMatches()
    {
        var settings = new ConnectionSettings { UserId = "u" };
        settings.SetLastSessionId("http://localhost:5000", "u", "session-abc");

        Assert.False(settings.ClearLastSessionId("http://localhost:5000", "u", "different"));
        Assert.Equal("session-abc", settings.GetLastSessionId("http://localhost:5000", "u"));

        Assert.True(settings.ClearLastSessionId("http://localhost:5000", "u", "session-abc"));
        Assert.Null(settings.GetLastSessionId("http://localhost:5000", "u"));
    }
}

