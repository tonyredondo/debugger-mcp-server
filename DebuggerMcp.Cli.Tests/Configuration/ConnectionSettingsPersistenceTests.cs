using System.Text.Json;
using DebuggerMcp.Cli.Configuration;
using Xunit;

namespace DebuggerMcp.Cli.Tests.Configuration;

/// <summary>
/// Persistence-focused tests for <see cref="ConnectionSettings"/>.
/// </summary>
public class ConnectionSettingsPersistenceTests
{
    [Fact]
    public void SaveToFile_CreatesDirectoryAndWritesJson_WithNonDefaultValues()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(tempRoot, "nested", "config.json");

        var settings = new ConnectionSettings
        {
            ServerUrl = "http://localhost:5000",
            ApiKey = "k",
            UserId = "test-user",
            TimeoutSeconds = 123,
            OutputFormat = OutputFormat.Json,
            HistoryFile = Path.Combine(tempRoot, "history.txt"),
            HistorySize = 42,
            Profiles = new Dictionary<string, ServerProfile>
            {
                ["prod"] = new() { Url = "https://prod.example.com", ApiKey = "prod-key", UserId = "prod-user", TimeoutSeconds = 999 }
            }
        };

        settings.SaveToFile(filePath);

        Assert.True(File.Exists(filePath));

        var json = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("http://localhost:5000", root.GetProperty("defaultServer").GetString());
        Assert.Equal("k", root.GetProperty("apiKey").GetString());
        Assert.Equal("test-user", root.GetProperty("userId").GetString());
        Assert.Equal(123, root.GetProperty("timeout").GetInt32());
        Assert.Equal("json", root.GetProperty("outputFormat").GetString());
        Assert.Equal(42, root.GetProperty("historySize").GetInt32());

        var profiles = root.GetProperty("profiles");
        Assert.True(profiles.TryGetProperty("prod", out var prodProfile));
        Assert.Equal("https://prod.example.com", prodProfile.GetProperty("url").GetString());
        Assert.Equal("prod-key", prodProfile.GetProperty("apiKey").GetString());
        Assert.Equal("prod-user", prodProfile.GetProperty("userId").GetString());
        Assert.Equal(999, prodProfile.GetProperty("timeoutSeconds").GetInt32());
    }

    [Fact]
    public void LoadFromFile_WhenFileMissing_ReturnsNull()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"), "missing.json");

        var settings = ConnectionSettings.LoadFromFile(missingPath);

        Assert.Null(settings);
    }

    [Fact]
    public void LoadFromFile_RoundTripsSettings()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DebuggerMcp.Cli.Tests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(tempRoot, "config.json");

        var original = new ConnectionSettings
        {
            ServerUrl = "http://localhost:5000",
            ApiKey = "k",
            UserId = "test-user",
            TimeoutSeconds = 321,
            OutputFormat = OutputFormat.Json,
            HistoryFile = Path.Combine(tempRoot, "history.txt"),
            HistorySize = 7,
            Profiles = new Dictionary<string, ServerProfile>
            {
                ["prod"] = new() { Url = "https://prod.example.com" }
            }
        };

        original.SaveToFile(filePath);

        var loaded = ConnectionSettings.LoadFromFile(filePath);

        Assert.NotNull(loaded);
        Assert.Equal(original.ServerUrl, loaded!.ServerUrl);
        Assert.Equal(original.ApiKey, loaded.ApiKey);
        Assert.Equal(original.UserId, loaded.UserId);
        Assert.Equal(original.TimeoutSeconds, loaded.TimeoutSeconds);
        Assert.Equal(original.OutputFormat, loaded.OutputFormat);
        Assert.Equal(original.HistoryFile, loaded.HistoryFile);
        Assert.Equal(original.HistorySize, loaded.HistorySize);
        Assert.True(loaded.Profiles.ContainsKey("prod"));
        Assert.Equal("https://prod.example.com", loaded.Profiles["prod"].Url);
    }

    [Fact]
    public void GetProfileNames_ReturnsProfileKeys()
    {
        var settings = new ConnectionSettings
        {
            Profiles = new Dictionary<string, ServerProfile>
            {
                ["prod"] = new() { Url = "https://prod.example.com" },
                ["staging"] = new() { Url = "https://staging.example.com" }
            }
        };

        var names = settings.GetProfileNames().OrderBy(x => x).ToList();

        Assert.Equal(["prod", "staging"], names);
    }
}

