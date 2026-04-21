using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DebuggerMcp.Symbols;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for session persistence/restore behavior in <see cref="DebuggerSessionManager"/>.
/// </summary>
public class SessionManagerPersistenceTests : IDisposable
{
    private sealed class TestDebuggerManager : IDebuggerManager
    {
        public bool IsInitialized { get; private set; }

        public bool IsDumpOpen { get; private set; }

        public string? CurrentDumpPath { get; private set; }

        public string DebuggerType { get; set; } = "Fake";

        public bool IsSosLoaded { get; private set; }

        public bool IsDotNetDump { get; private set; }

        public int OpenDumpCalls { get; private set; }

        public int InitializeCalls { get; private set; }

        public List<string> ConfiguredSymbolPaths { get; } = new();

        public List<string> Operations { get; } = new();

        public Task InitializeAsync()
        {
            InitializeCalls++;
            IsInitialized = true;
            return Task.CompletedTask;
        }

        public void OpenDumpFile(string dumpFilePath, string? executablePath = null)
        {
            Operations.Add("open");
            OpenDumpCalls++;
            IsDumpOpen = true;
            CurrentDumpPath = dumpFilePath;
            IsDotNetDump = true;
            IsSosLoaded = true;
        }

        public void CloseDump()
        {
            IsDumpOpen = false;
            CurrentDumpPath = null;
        }

        public string ExecuteCommand(string command) => string.Empty;

        public void LoadSosExtension()
        {
            IsSosLoaded = true;
        }

        public void ConfigureSymbolPath(string symbolPath)
        {
            Operations.Add("configure");
            ConfiguredSymbolPaths.Add(symbolPath);
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly string _root;

    public SessionManagerPersistenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SessionManagerPersistenceTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup in tests.
        }
    }

    [Fact]
    public void GetSession_WhenSessionOnlyOnDisk_RestoresIntoMemory()
    {
        var manager1 = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: Path.Combine(_root, "sessions"),
            debuggerFactory: _ => new TestDebuggerManager());

        var sessionId = manager1.CreateSession("user1");

        // New instance with the same storage root should restore from persisted session metadata.
        var manager2 = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: Path.Combine(_root, "sessions"),
            debuggerFactory: _ => new TestDebuggerManager());

        var restored = manager2.GetSession(sessionId, "user1");

        Assert.NotNull(restored);
        Assert.True(manager2.ListUserSessions("user1").Count >= 1);
    }

    [Fact]
    public void GetSession_WhenDumpPathExists_ReopensDumpAndConfiguresSymbolsBeforeOpen()
    {
        var sessionsPath = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(sessionsPath);

        var userDumpDir = Path.Combine(_root, "user1");
        Directory.CreateDirectory(userDumpDir);

        var dumpPath = Path.Combine(userDumpDir, "dump1.dmp");
        File.WriteAllText(dumpPath, "not-a-real-dump");
        var dumpSymbolsDir = Path.Combine(userDumpDir, ".symbols_dump1");
        Directory.CreateDirectory(dumpSymbolsDir);
        File.WriteAllText(Path.Combine(dumpSymbolsDir, "a.pdb"), "x");

        var symbolManager1 = new SymbolManager(symbolCacheBasePath: _root, dumpStorageBasePath: _root);
        var manager1 = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager(),
            symbolManager: symbolManager1);

        var sessionId = manager1.CreateSession("user1");
        var session = manager1.GetSessionInfo(sessionId, "user1");
        session.CurrentDumpId = "dump1";
        session.SymbolConfiguration = new PersistedSessionSymbolConfiguration
        {
            AdditionalLocalDirectories = new List<string> { Path.Combine(_root, "extra-symbols") }
        };

        // Persist CurrentDumpPath via the manager's Save-on-access behavior.
        ((TestDebuggerManager)session.Manager).OpenDumpFile(dumpPath);
        manager1.PersistSession(sessionId);

        var symbolManager2 = new SymbolManager(symbolCacheBasePath: _root, dumpStorageBasePath: _root);
        var manager2 = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager { },
            symbolManager: symbolManager2);

        var restoredManager = manager2.GetSession(sessionId, "user1");
        var restoredDebugger = Assert.IsType<TestDebuggerManager>(restoredManager);

        Assert.NotNull(restoredManager);
        Assert.Single(restoredDebugger.ConfiguredSymbolPaths);
        Assert.Contains(dumpSymbolsDir, restoredDebugger.ConfiguredSymbolPaths[0]);
        Assert.Contains(Path.Combine(_root, "extra-symbols"), restoredDebugger.ConfiguredSymbolPaths[0]);
        Assert.Equal(new[] { "configure", "open" }, restoredDebugger.Operations);
    }

    [Fact]
    public void GetSession_WhenOnlyPersistedSymbolConfigurationExists_RehydratesWithoutOpeningDump()
    {
        var sessionsPath = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(sessionsPath);

        var symbolManager1 = new SymbolManager(symbolCacheBasePath: _root, dumpStorageBasePath: _root);
        var manager1 = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager(),
            symbolManager: symbolManager1);

        var sessionId = manager1.CreateSession("user1");
        var session = manager1.GetSessionInfo(sessionId, "user1");
        session.SymbolConfiguration = new PersistedSessionSymbolConfiguration
        {
            AdditionalLocalDirectories = new List<string> { "/tmp/symbols" },
            AdditionalRemoteUrls = new List<string> { "https://symbols.example.com" }
        };
        manager1.PersistSession(sessionId);

        var symbolManager2 = new SymbolManager(symbolCacheBasePath: _root, dumpStorageBasePath: _root);
        var manager2 = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager(),
            symbolManager: symbolManager2);

        var restoredSession = manager2.GetSessionInfo(sessionId, "user1");
        var restoredConfiguration = symbolManager2.GetPersistedSessionSymbolConfiguration(sessionId);

        Assert.Null(restoredSession.CurrentDumpId);
        Assert.Contains("/tmp/symbols", restoredSession.SymbolConfiguration.AdditionalLocalDirectories);
        Assert.Contains("/tmp/symbols", restoredConfiguration.AdditionalLocalDirectories);
        Assert.Contains("https://symbols.example.com", restoredConfiguration.AdditionalRemoteUrls);
    }

    [Fact]
    public void GetSession_WhenRestoredOnLldbWithPersistedRemoteSymbolUrls_AttachesRuntimeWarningWithoutDroppingConfiguration()
    {
        var sessionsPath = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(sessionsPath);

        var symbolManager1 = new SymbolManager(symbolCacheBasePath: _root, dumpStorageBasePath: _root);
        var manager1 = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager { DebuggerType = "WinDbg" },
            symbolManager: symbolManager1);

        var sessionId = manager1.CreateSession("user1");
        var session = manager1.GetSessionInfo(sessionId, "user1");
        session.SymbolConfiguration = new PersistedSessionSymbolConfiguration
        {
            AdditionalRemoteUrls = new List<string> { "https://symbols.example.com" }
        };
        manager1.PersistSession(sessionId);

        var symbolManager2 = new SymbolManager(symbolCacheBasePath: _root, dumpStorageBasePath: _root);
        var manager2 = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager { DebuggerType = "LLDB" },
            symbolManager: symbolManager2);

        var restoredSession = manager2.GetSessionInfo(sessionId, "user1");
        var restoredConfiguration = symbolManager2.GetPersistedSessionSymbolConfiguration(sessionId);

        Assert.Single(restoredSession.RuntimeWarnings);
        Assert.Contains("LLDB", restoredSession.RuntimeWarnings[0], StringComparison.Ordinal);
        Assert.Contains("https://symbols.example.com", restoredConfiguration.AdditionalRemoteUrls);
    }

    [Fact]
    public void GetSession_WhenStorageRootPathChanges_ReResolvesDumpFromUserAndDumpId()
    {
        var sessionsPath = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(sessionsPath);
        var userDumpDir = Path.Combine(_root, "user1");
        Directory.CreateDirectory(userDumpDir);

        var currentDumpPath = Path.Combine(userDumpDir, "dump1.dmp");
        File.WriteAllText(currentDumpPath, "dump");

        var staleDumpPath = Path.Combine(_root, "old-storage", "user1", "dump1.dmp");

        var sessionId = Guid.NewGuid().ToString();
        var metadata = new SessionMetadata
        {
            SessionId = sessionId,
            UserId = "user1",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            LastAccessedAt = DateTime.UtcNow,
            CurrentDumpId = "dump1",
            CurrentDumpPath = staleDumpPath,
            SymbolConfiguration = new PersistedSessionSymbolConfiguration()
        };
        File.WriteAllText(
            Path.Combine(sessionsPath, $"{sessionId}.json"),
            JsonSerializer.Serialize(metadata));

        var manager = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager(),
            symbolManager: new SymbolManager(symbolCacheBasePath: _root, dumpStorageBasePath: _root));

        var restoredManager = Assert.IsType<TestDebuggerManager>(manager.GetSession(sessionId, "user1"));
        Assert.Equal(currentDumpPath, restoredManager.CurrentDumpPath);
    }

    [Fact]
    public void GetSession_WhenCanonicalDumpPathMissing_FallsBackToPersistedCurrentDumpPath()
    {
        var sessionsPath = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(sessionsPath);

        var persistedDumpDir = Path.Combine(_root, "legacy-storage", "user1");
        Directory.CreateDirectory(persistedDumpDir);
        var persistedDumpPath = Path.Combine(persistedDumpDir, "dump1.dmp");
        File.WriteAllText(persistedDumpPath, "legacy-dump");

        var sessionId = Guid.NewGuid().ToString();
        var metadata = new SessionMetadata
        {
            SessionId = sessionId,
            UserId = "user1",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            LastAccessedAt = DateTime.UtcNow,
            CurrentDumpId = "dump1",
            CurrentDumpPath = persistedDumpPath,
            SymbolConfiguration = new PersistedSessionSymbolConfiguration()
        };

        File.WriteAllText(
            Path.Combine(sessionsPath, $"{sessionId}.json"),
            JsonSerializer.Serialize(metadata));

        var manager = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager(),
            symbolManager: new SymbolManager(symbolCacheBasePath: _root, dumpStorageBasePath: _root));

        var restoredManager = Assert.IsType<TestDebuggerManager>(manager.GetSession(sessionId, "user1"));
        Assert.Equal(persistedDumpPath, restoredManager.CurrentDumpPath);
    }

    [Fact]
    public void GetSession_WhenNoPersistedDumpLocationExists_RestoresWithoutOpenDump()
    {
        var sessionsPath = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(sessionsPath);

        var sessionId = Guid.NewGuid().ToString();
        var metadata = new SessionMetadata
        {
            SessionId = sessionId,
            UserId = "user1",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            LastAccessedAt = DateTime.UtcNow,
            CurrentDumpId = "dump1",
            CurrentDumpPath = Path.Combine(_root, "legacy-storage", "user1", "dump1.dmp"),
            SymbolConfiguration = new PersistedSessionSymbolConfiguration()
        };

        File.WriteAllText(
            Path.Combine(sessionsPath, $"{sessionId}.json"),
            JsonSerializer.Serialize(metadata));

        var manager = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager(),
            symbolManager: new SymbolManager(symbolCacheBasePath: _root, dumpStorageBasePath: _root));

        var restoredSession = manager.GetSessionInfo(sessionId, "user1");
        var restoredDebugger = Assert.IsType<TestDebuggerManager>(restoredSession.Manager);

        Assert.Null(restoredSession.CurrentDumpId);
        Assert.Equal(0, restoredDebugger.OpenDumpCalls);
        Assert.Null(restoredDebugger.CurrentDumpPath);
    }

    [Fact]
    public void GetSessionUserId_WhenInMemorySessionExpired_CleansUpAndThrows()
    {
        var original = Environment.GetEnvironmentVariable("SESSION_INACTIVITY_THRESHOLD_MINUTES");
        Environment.SetEnvironmentVariable("SESSION_INACTIVITY_THRESHOLD_MINUTES", "1");

        try
        {
            var sessionsPath = Path.Combine(_root, "sessions");
            var manager = new DebuggerSessionManager(
                dumpStoragePath: _root,
                loggerFactory: NullLoggerFactory.Instance,
                sessionStoragePath: sessionsPath,
                debuggerFactory: _ => new TestDebuggerManager());

            var sessionId = manager.CreateSession("user1");
            var session = manager.GetSessionInfo(sessionId, "user1");
            session.LastAccessedAt = DateTime.UtcNow.Subtract(TimeSpan.FromHours(2));

            var callbackInvoked = false;
            manager.OnSessionClosed = _ => callbackInvoked = true;

            var ex = Assert.Throws<InvalidOperationException>(() => manager.GetSessionUserId(sessionId));
            Assert.Contains("expired", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(callbackInvoked);

            Assert.False(File.Exists(Path.Combine(sessionsPath, $"{sessionId}.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SESSION_INACTIVITY_THRESHOLD_MINUTES", original);
        }
    }

    [Fact]
    public void GetSessionUserId_WhenOnlyDiskSessionExpired_DeletesFileAndThrows()
    {
        var original = Environment.GetEnvironmentVariable("SESSION_INACTIVITY_THRESHOLD_MINUTES");
        Environment.SetEnvironmentVariable("SESSION_INACTIVITY_THRESHOLD_MINUTES", "1");

        try
        {
            var sessionsPath = Path.Combine(_root, "sessions");
            Directory.CreateDirectory(sessionsPath);

            var manager1 = new DebuggerSessionManager(
                dumpStoragePath: _root,
                loggerFactory: NullLoggerFactory.Instance,
                sessionStoragePath: sessionsPath,
                debuggerFactory: _ => new TestDebuggerManager());

            var sessionId = manager1.CreateSession("user1");

            // Make the persisted session look expired.
            var metadataPath = Path.Combine(sessionsPath, $"{sessionId}.json");
            var metadataJson = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize<SessionMetadata>(metadataJson);
            Assert.NotNull(metadata);
            metadata!.LastAccessedAt = DateTime.UtcNow.Subtract(TimeSpan.FromHours(2));
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

            var manager2 = new DebuggerSessionManager(
                dumpStoragePath: _root,
                loggerFactory: NullLoggerFactory.Instance,
                sessionStoragePath: sessionsPath,
                debuggerFactory: _ => new TestDebuggerManager());

            var ex = Assert.Throws<InvalidOperationException>(() => manager2.GetSessionUserId(sessionId));
            Assert.Contains("expired", ex.Message, StringComparison.OrdinalIgnoreCase);

            Assert.False(File.Exists(metadataPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SESSION_INACTIVITY_THRESHOLD_MINUTES", original);
        }
    }

    [Fact]
    public void GetStatistics_ReflectsPersistedAndInMemorySessions()
    {
        var sessionsPath = Path.Combine(_root, "sessions");
        var manager = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager());

        manager.CreateSession("user1");
        manager.CreateSession("user1");
        manager.CreateSession("user2");

        var stats = manager.GetStatistics();

        Assert.Equal(3, (int)stats["TotalSessions"]);
        Assert.Equal(3, (int)stats["InMemorySessions"]);
        Assert.Equal(3, (int)stats["PersistedSessions"]);
        Assert.Equal(2, (int)stats["UniqueUsers"]);

        var perUser = Assert.IsType<Dictionary<string, int>>(stats["SessionsPerUser"]);
        Assert.Equal(2, perUser["user1"]);
        Assert.Equal(1, perUser["user2"]);
    }

    [Fact]
    public void PersistSession_WhenNotInMemory_DoesNotThrow()
    {
        var sessionsPath = Path.Combine(_root, "sessions");
        var manager = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager());

        // Should not throw even if it isn't in memory (e.g. disk-only session on another server).
        manager.PersistSession(Guid.NewGuid().ToString());
    }

    [Fact]
    public void CleanupInactiveSessions_RemovesExpiredDiskOnlySessions()
    {
        var original = Environment.GetEnvironmentVariable("SESSION_INACTIVITY_THRESHOLD_MINUTES");
        Environment.SetEnvironmentVariable("SESSION_INACTIVITY_THRESHOLD_MINUTES", "1");

        try
        {
            var sessionsPath = Path.Combine(_root, "sessions");
            Directory.CreateDirectory(sessionsPath);

            var manager1 = new DebuggerSessionManager(
                dumpStoragePath: _root,
                loggerFactory: NullLoggerFactory.Instance,
                sessionStoragePath: sessionsPath,
                debuggerFactory: _ => new TestDebuggerManager());

            var sessionId = manager1.CreateSession("user1");

            // Make the persisted session look expired.
            var metadataPath = Path.Combine(sessionsPath, $"{sessionId}.json");
            var metadataJson = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize<SessionMetadata>(metadataJson);
            Assert.NotNull(metadata);
            metadata!.LastAccessedAt = DateTime.UtcNow.Subtract(TimeSpan.FromHours(2));
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

            // New manager instance has no in-memory sessions; cleanup should remove the disk one.
            var manager2 = new DebuggerSessionManager(
                dumpStoragePath: _root,
                loggerFactory: NullLoggerFactory.Instance,
                sessionStoragePath: sessionsPath,
                debuggerFactory: _ => new TestDebuggerManager());

            var cleaned = manager2.CleanupInactiveSessions(TimeSpan.FromMinutes(1));

            Assert.True(cleaned >= 1);
            Assert.False(File.Exists(metadataPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SESSION_INACTIVITY_THRESHOLD_MINUTES", original);
        }
    }

    [Fact]
    public void ListUserSessions_IncludesPersistedSessionsNotInMemory()
    {
        var sessionsPath = Path.Combine(_root, "sessions");

        var manager1 = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager());

        var sessionId = manager1.CreateSession("user1");

        var manager2 = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager());

        var sessions = manager2.ListUserSessions("user1");

        Assert.Contains(sessions, s => s.SessionId == sessionId);
        Assert.Contains(sessions, s => s.SessionId == sessionId && s.Manager == null);
    }

    [Fact]
    public void GetSessionUserId_WhenSessionOnlyOnDiskAndActive_ReturnsUserId()
    {
        var sessionsPath = Path.Combine(_root, "sessions");

        var manager1 = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager());

        var sessionId = manager1.CreateSession("user1");

        var manager2 = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager());

        var userId = manager2.GetSessionUserId(sessionId);

        Assert.Equal("user1", userId);
    }

    [Fact]
    public void GetSession_WhenDiskSessionExpired_DeletesFileAndThrows()
    {
        var original = Environment.GetEnvironmentVariable("SESSION_INACTIVITY_THRESHOLD_MINUTES");
        Environment.SetEnvironmentVariable("SESSION_INACTIVITY_THRESHOLD_MINUTES", "1");

        try
        {
            var sessionsPath = Path.Combine(_root, "sessions");
            Directory.CreateDirectory(sessionsPath);

            var manager1 = new DebuggerSessionManager(
                dumpStoragePath: _root,
                loggerFactory: NullLoggerFactory.Instance,
                sessionStoragePath: sessionsPath,
                debuggerFactory: _ => new TestDebuggerManager());

            var sessionId = manager1.CreateSession("user1");

            var metadataPath = Path.Combine(sessionsPath, $"{sessionId}.json");
            var metadataJson = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize<SessionMetadata>(metadataJson);
            Assert.NotNull(metadata);
            metadata!.LastAccessedAt = DateTime.UtcNow.Subtract(TimeSpan.FromHours(2));
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

            var manager2 = new DebuggerSessionManager(
                dumpStoragePath: _root,
                loggerFactory: NullLoggerFactory.Instance,
                sessionStoragePath: sessionsPath,
                debuggerFactory: _ => new TestDebuggerManager());

            var ex = Assert.Throws<InvalidOperationException>(() => manager2.GetSession(sessionId, "user1"));
            Assert.Contains("expired", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(metadataPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SESSION_INACTIVITY_THRESHOLD_MINUTES", original);
        }
    }
}
