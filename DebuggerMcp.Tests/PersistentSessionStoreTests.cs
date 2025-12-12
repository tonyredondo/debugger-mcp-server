using System;
using System.Collections.Generic;
using System.IO;
using DebuggerMcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for <see cref="PersistentSessionStore"/>.
/// </summary>
public class PersistentSessionStoreTests : IDisposable
{
    private readonly string _storagePath;
    private readonly PersistentSessionStore _store;

    public PersistentSessionStoreTests()
    {
        _storagePath = Path.Combine(Path.GetTempPath(), $"PersistentSessionStoreTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_storagePath);

        _store = new PersistentSessionStore(NullLogger<PersistentSessionStore>.Instance, _storagePath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_storagePath))
            {
                Directory.Delete(_storagePath, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup in tests.
        }
    }

    [Fact]
    public void SaveMetadata_WithInvalidSessionId_ThrowsArgumentException()
    {
        var metadata = new SessionMetadata
        {
            SessionId = "not-a-guid",
            UserId = "user",
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        };

        Assert.Throws<ArgumentException>(() => _store.SaveMetadata(metadata));
    }

    [Fact]
    public void SaveMetadata_ThenLoad_RoundTripsMetadata()
    {
        var sessionId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;

        var metadata = new SessionMetadata
        {
            SessionId = sessionId,
            UserId = "user1",
            CreatedAt = now.AddMinutes(-1),
            LastAccessedAt = now,
            CurrentDumpId = "dump1",
            CurrentDumpPath = "/tmp/dump1.dmp",
            LastServerId = "server-1"
        };

        _store.SaveMetadata(metadata);

        var loaded = _store.Load(sessionId);

        Assert.NotNull(loaded);
        Assert.Equal(metadata.SessionId, loaded!.SessionId);
        Assert.Equal(metadata.UserId, loaded.UserId);
        Assert.Equal(metadata.CurrentDumpId, loaded.CurrentDumpId);
        Assert.Equal(metadata.CurrentDumpPath, loaded.CurrentDumpPath);
        Assert.Equal(metadata.LastServerId, loaded.LastServerId);
        Assert.True(loaded.LastAccessedAt >= metadata.LastAccessedAt.AddSeconds(-1));
    }

    [Fact]
    public void Load_WithInvalidSessionId_ReturnsNull()
    {
        var loaded = _store.Load("not-a-guid");
        Assert.Null(loaded);
    }

    [Fact]
    public void Load_WithMissingFile_ReturnsNull()
    {
        var loaded = _store.Load(Guid.NewGuid().ToString());
        Assert.Null(loaded);
    }

    [Fact]
    public void Load_WithCorruptJson_ReturnsNull()
    {
        var sessionId = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_storagePath, $"{sessionId}.json"), "{ invalid json");

        var loaded = _store.Load(sessionId);

        Assert.Null(loaded);
    }

    [Fact]
    public void LoadAll_SkipsInvalidAndCorruptFiles()
    {
        var okId = Guid.NewGuid().ToString();
        _store.SaveMetadata(new SessionMetadata
        {
            SessionId = okId,
            UserId = "user",
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        });

        // Corrupt JSON with a valid GUID filename.
        var corruptId = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_storagePath, $"{corruptId}.json"), "not-json");

        // A file that shouldn't be considered by LoadAll (not *.json).
        File.WriteAllText(Path.Combine(_storagePath, "ignore.txt"), "x");

        var all = _store.LoadAll();

        Assert.Single(all);
        Assert.Equal(okId, all[0].SessionId);
    }

    [Fact]
    public void Exists_WithInvalidSessionId_ReturnsFalse()
    {
        Assert.False(_store.Exists("not-a-guid"));
    }

    [Fact]
    public void Exists_WithSavedSession_ReturnsTrue()
    {
        var sessionId = Guid.NewGuid().ToString();
        _store.SaveMetadata(new SessionMetadata
        {
            SessionId = sessionId,
            UserId = "user",
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        });

        Assert.True(_store.Exists(sessionId));
    }

    [Fact]
    public void Delete_WithInvalidSessionId_DoesNothing()
    {
        // Should not throw.
        _store.Delete("not-a-guid");
    }

    [Fact]
    public void Delete_WithExistingFile_RemovesIt()
    {
        var sessionId = Guid.NewGuid().ToString();
        _store.SaveMetadata(new SessionMetadata
        {
            SessionId = sessionId,
            UserId = "user",
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        });

        Assert.True(_store.Exists(sessionId));

        _store.Delete(sessionId);

        Assert.False(_store.Exists(sessionId));
    }

    [Fact]
    public void CleanupExpiredSessions_DeletesExpiredSessions()
    {
        var now = DateTime.UtcNow;

        var expiredId = Guid.NewGuid().ToString();
        var freshId = Guid.NewGuid().ToString();

        _store.SaveMetadata(new SessionMetadata
        {
            SessionId = expiredId,
            UserId = "user",
            CreatedAt = now.AddDays(-2),
            LastAccessedAt = now.AddDays(-1)
        });

        _store.SaveMetadata(new SessionMetadata
        {
            SessionId = freshId,
            UserId = "user",
            CreatedAt = now.AddMinutes(-10),
            LastAccessedAt = now
        });

        var cleaned = _store.CleanupExpiredSessions(TimeSpan.FromHours(1));

        Assert.Equal(1, cleaned);
        Assert.False(_store.Exists(expiredId));
        Assert.True(_store.Exists(freshId));
    }
}

