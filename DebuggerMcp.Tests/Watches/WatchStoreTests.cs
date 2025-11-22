using System;
using System.IO;
using System.Threading.Tasks;
using DebuggerMcp.Watches;
using Xunit;

namespace DebuggerMcp.Tests.Watches;

/// <summary>
/// Tests for the WatchStore class.
/// </summary>
public class WatchStoreTests : IDisposable
{
    private readonly string _testStoragePath;
    private readonly WatchStore _store;

    public WatchStoreTests()
    {
        _testStoragePath = Path.Combine(Path.GetTempPath(), $"WatchStoreTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testStoragePath);
        _store = new WatchStore(_testStoragePath);
    }

    public void Dispose()
    {
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

    #region AddWatchAsync Tests

    [Fact]
    public async Task AddWatchAsync_WithValidWatch_ReturnsWatchWithId()
    {
        // Arrange
        var watch = new WatchExpression
        {
            Expression = "0x12345678",
            Description = "Test watch",
            Type = WatchType.MemoryAddress
        };

        // Act
        var result = await _store.AddWatchAsync("user1", "dump1", watch);

        // Assert
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.Id));
        Assert.Equal("0x12345678", result.Expression);
        Assert.Equal("Test watch", result.Description);
        Assert.Equal(WatchType.MemoryAddress, result.Type);
        Assert.Equal("dump1", result.DumpId);
    }

    [Fact]
    public async Task AddWatchAsync_WithDuplicateExpression_ThrowsInvalidOperationException()
    {
        // Arrange
        var watch1 = new WatchExpression { Expression = "myVar" };
        var watch2 = new WatchExpression { Expression = "myVar" };

        await _store.AddWatchAsync("user1", "dump1", watch1);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.AddWatchAsync("user1", "dump1", watch2));
    }

    [Fact]
    public async Task AddWatchAsync_WithEmptyExpression_ThrowsArgumentException()
    {
        // Arrange
        var watch = new WatchExpression { Expression = "" };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.AddWatchAsync("user1", "dump1", watch));
    }

    [Fact]
    public async Task AddWatchAsync_WithNullWatch_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _store.AddWatchAsync("user1", "dump1", null!));
    }

    [Fact]
    public async Task AddWatchAsync_WithEmptyUserId_ThrowsArgumentException()
    {
        // Arrange
        var watch = new WatchExpression { Expression = "test" };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.AddWatchAsync("", "dump1", watch));
    }

    #endregion

    #region GetWatchesAsync Tests

    [Fact]
    public async Task GetWatchesAsync_WithNoWatches_ReturnsEmptyList()
    {
        // Act
        var watches = await _store.GetWatchesAsync("user1", "dump1");

        // Assert
        Assert.NotNull(watches);
        Assert.Empty(watches);
    }

    [Fact]
    public async Task GetWatchesAsync_WithMultipleWatches_ReturnsAll()
    {
        // Arrange
        await _store.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "var1" });
        await _store.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "var2" });
        await _store.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "var3" });

        // Act
        var watches = await _store.GetWatchesAsync("user1", "dump1");

        // Assert
        Assert.Equal(3, watches.Count);
    }

    [Fact]
    public async Task GetWatchesAsync_WatchesAreSeparatedByDump()
    {
        // Arrange
        await _store.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "var1" });
        await _store.AddWatchAsync("user1", "dump2", new WatchExpression { Expression = "var2" });

        // Act
        var dump1Watches = await _store.GetWatchesAsync("user1", "dump1");
        var dump2Watches = await _store.GetWatchesAsync("user1", "dump2");

        // Assert
        Assert.Single(dump1Watches);
        Assert.Equal("var1", dump1Watches[0].Expression);
        Assert.Single(dump2Watches);
        Assert.Equal("var2", dump2Watches[0].Expression);
    }

    [Fact]
    public async Task GetWatchesAsync_WatchesAreSeparatedByUser()
    {
        // Arrange
        await _store.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "var1" });
        await _store.AddWatchAsync("user2", "dump1", new WatchExpression { Expression = "var2" });

        // Act
        var user1Watches = await _store.GetWatchesAsync("user1", "dump1");
        var user2Watches = await _store.GetWatchesAsync("user2", "dump1");

        // Assert
        Assert.Single(user1Watches);
        Assert.Single(user2Watches);
    }

    #endregion

    #region GetWatchAsync Tests

    [Fact]
    public async Task GetWatchAsync_WithExistingId_ReturnsWatch()
    {
        // Arrange
        var added = await _store.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "var1" });

        // Act
        var watch = await _store.GetWatchAsync("user1", "dump1", added.Id);

        // Assert
        Assert.NotNull(watch);
        Assert.Equal(added.Id, watch.Id);
        Assert.Equal("var1", watch.Expression);
    }

    [Fact]
    public async Task GetWatchAsync_WithNonExistingId_ReturnsNull()
    {
        // Act
        var watch = await _store.GetWatchAsync("user1", "dump1", "non-existing-id");

        // Assert
        Assert.Null(watch);
    }

    #endregion

    #region RemoveWatchAsync Tests

    [Fact]
    public async Task RemoveWatchAsync_WithExistingId_ReturnsTrue()
    {
        // Arrange
        var added = await _store.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "var1" });

        // Act
        var result = await _store.RemoveWatchAsync("user1", "dump1", added.Id);

        // Assert
        Assert.True(result);

        // Verify it's gone
        var watches = await _store.GetWatchesAsync("user1", "dump1");
        Assert.Empty(watches);
    }

    [Fact]
    public async Task RemoveWatchAsync_WithNonExistingId_ReturnsFalse()
    {
        // Act
        var result = await _store.RemoveWatchAsync("user1", "dump1", "non-existing-id");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task RemoveWatchAsync_WithEmptyWatchId_ThrowsArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.RemoveWatchAsync("user1", "dump1", ""));
    }

    #endregion

    #region UpdateWatchAsync Tests

    [Fact]
    public async Task UpdateWatchAsync_WithExistingWatch_ReturnsTrue()
    {
        // Arrange
        var added = await _store.AddWatchAsync("user1", "dump1", new WatchExpression 
        { 
            Expression = "var1",
            Description = "Original"
        });

        added.Description = "Updated";
        added.LastValue = "new value";

        // Act
        var result = await _store.UpdateWatchAsync("user1", "dump1", added);

        // Assert
        Assert.True(result);

        // Verify the update
        var watch = await _store.GetWatchAsync("user1", "dump1", added.Id);
        Assert.NotNull(watch);
        Assert.Equal("Updated", watch.Description);
        Assert.Equal("new value", watch.LastValue);
    }

    [Fact]
    public async Task UpdateWatchAsync_WithNonExistingWatch_ReturnsFalse()
    {
        // Arrange
        var watch = new WatchExpression { Id = "non-existing", Expression = "var1" };

        // Act
        var result = await _store.UpdateWatchAsync("user1", "dump1", watch);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region ClearWatchesAsync Tests

    [Fact]
    public async Task ClearWatchesAsync_WithWatches_ReturnsCount()
    {
        // Arrange
        await _store.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "var1" });
        await _store.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "var2" });
        await _store.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "var3" });

        // Act
        var count = await _store.ClearWatchesAsync("user1", "dump1");

        // Assert
        Assert.Equal(3, count);

        // Verify cleared
        var watches = await _store.GetWatchesAsync("user1", "dump1");
        Assert.Empty(watches);
    }

    [Fact]
    public async Task ClearWatchesAsync_WithNoWatches_ReturnsZero()
    {
        // Act
        var count = await _store.ClearWatchesAsync("user1", "dump1");

        // Assert
        Assert.Equal(0, count);
    }

    #endregion

    #region HasWatchesAsync Tests

    [Fact]
    public async Task HasWatchesAsync_WithWatches_ReturnsTrue()
    {
        // Arrange
        await _store.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "var1" });

        // Act
        var result = await _store.HasWatchesAsync("user1", "dump1");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task HasWatchesAsync_WithNoWatches_ReturnsFalse()
    {
        // Act
        var result = await _store.HasWatchesAsync("user1", "dump1");

        // Assert
        Assert.False(result);
    }

    #endregion

    #region GetWatchCountAsync Tests

    [Fact]
    public async Task GetWatchCountAsync_WithWatches_ReturnsCount()
    {
        // Arrange
        await _store.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "var1" });
        await _store.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "var2" });

        // Act
        var count = await _store.GetWatchCountAsync("user1", "dump1");

        // Assert
        Assert.Equal(2, count);
    }

    #endregion

    #region Persistence Tests

    [Fact]
    public async Task Watches_ArePersisted_AcrossStoreInstances()
    {
        // Arrange - Add watch using first store instance
        await _store.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "var1" });

        // Create a new store instance pointing to the same path
        var newStore = new WatchStore(_testStoragePath);

        // Act
        var watches = await newStore.GetWatchesAsync("user1", "dump1");

        // Assert
        Assert.Single(watches);
        Assert.Equal("var1", watches[0].Expression);
    }

    #endregion

    #region Cache Tests

    [Fact]
    public void InvalidateCache_ClearsSpecificCache()
    {
        // This tests the cache invalidation - no exception means success
        _store.InvalidateCache("user1", "dump1");
    }

    [Fact]
    public void InvalidateAllCaches_ClearsAllCaches()
    {
        // This tests the cache invalidation - no exception means success
        _store.InvalidateAllCaches();
    }

    #endregion
}

