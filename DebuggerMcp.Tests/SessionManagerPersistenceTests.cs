using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
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

        public string DebuggerType { get; } = "Fake";

        public bool IsSosLoaded { get; private set; }

        public bool IsDotNetDump { get; private set; }

        public int OpenDumpCalls { get; private set; }

        public int InitializeCalls { get; private set; }

        public Task InitializeAsync()
        {
            InitializeCalls++;
            IsInitialized = true;
            return Task.CompletedTask;
        }

        public void OpenDumpFile(string dumpFilePath, string? executablePath = null)
        {
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
    public void GetSession_WhenDumpPathExists_ReopensDumpAndInvokesOnSessionRestored()
    {
        var sessionsPath = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(sessionsPath);

        var dumpPath = Path.Combine(_root, "dummy.dmp");
        File.WriteAllText(dumpPath, "not-a-real-dump");

        var manager1 = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager());

        var sessionId = manager1.CreateSession("user1");
        var session = manager1.GetSessionInfo(sessionId, "user1");
        session.CurrentDumpId = "dump1";

        // Persist CurrentDumpPath via the manager's Save-on-access behavior.
        ((TestDebuggerManager)session.Manager).OpenDumpFile(dumpPath);
        manager1.GetSession(sessionId, "user1");

        var callbackInvoked = false;
        string? callbackDumpId = null;
        IDebuggerManager? callbackManager = null;

        var manager2 = new DebuggerSessionManager(
            dumpStoragePath: _root,
            loggerFactory: NullLoggerFactory.Instance,
            sessionStoragePath: sessionsPath,
            debuggerFactory: _ => new TestDebuggerManager { });

        manager2.OnSessionRestored = (sid, dumpId, mgr) =>
        {
            callbackInvoked = sid == sessionId;
            callbackDumpId = dumpId;
            callbackManager = mgr;
        };

        var restoredManager = manager2.GetSession(sessionId, "user1");

        Assert.NotNull(restoredManager);
        Assert.True(callbackInvoked);
        Assert.Equal("dump1", callbackDumpId);
        Assert.NotNull(callbackManager);
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
