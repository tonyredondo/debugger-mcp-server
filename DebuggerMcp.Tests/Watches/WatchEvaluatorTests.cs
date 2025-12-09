using System;
using System.IO;
using System.Threading.Tasks;
using DebuggerMcp;
using DebuggerMcp.Watches;
using Moq;
using Xunit;

namespace DebuggerMcp.Tests.Watches;

/// <summary>
/// Tests for the WatchEvaluator class.
/// </summary>
public class WatchEvaluatorTests : IDisposable
{
    private readonly string _testStoragePath;
    private readonly WatchStore _watchStore;

    public WatchEvaluatorTests()
    {
        _testStoragePath = Path.Combine(Path.GetTempPath(), $"WatchEvaluatorTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testStoragePath);
        _watchStore = new WatchStore(_testStoragePath);
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


    [Fact]
    public void Constructor_WithNullManager_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new WatchEvaluator(null!, _watchStore));
    }

    [Fact]
    public void Constructor_WithNullWatchStore_ThrowsArgumentNullException()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new WatchEvaluator(mockManager.Object, null!));
    }

    [Fact]
    public void Constructor_WithValidParameters_Succeeds()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();

        // Act
        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);

        // Assert
        Assert.NotNull(evaluator);
    }



    [Theory]
    [InlineData("0x12345678", WatchType.MemoryAddress)]
    [InlineData("0X00007FF812345678", WatchType.MemoryAddress)]
    [InlineData("12345678", WatchType.MemoryAddress)]
    [InlineData("00007ff812345678", WatchType.MemoryAddress)]
    public void DetectWatchType_WithHexAddress_ReturnsMemoryAddress(string expression, WatchType expected)
    {
        // Act
        var result = WatchEvaluator.DetectWatchType(expression);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("myModule!myVariable", WatchType.Variable)]
    [InlineData("ntdll!NtWaitForSingleObject", WatchType.Variable)]
    [InlineData("clr!JIT_New", WatchType.Variable)]
    public void DetectWatchType_WithModuleSymbol_ReturnsVariable(string expression, WatchType expected)
    {
        // Act
        var result = WatchEvaluator.DetectWatchType(expression);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("myVariable", WatchType.Variable)]
    [InlineData("g_DataManager", WatchType.Variable)]
    [InlineData("s_Instance", WatchType.Variable)]
    public void DetectWatchType_WithSimpleIdentifier_ReturnsVariable(string expression, WatchType expected)
    {
        // Act
        var result = WatchEvaluator.DetectWatchType(expression);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("!do 0x12345678", WatchType.Object)]
    [InlineData("!dumpobj 0x12345678", WatchType.Object)]
    public void DetectWatchType_WithDumpObjectCommand_ReturnsObject(string expression, WatchType expected)
    {
        // Act
        var result = WatchEvaluator.DetectWatchType(expression);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("poi(esp+8)", WatchType.Expression)]
    [InlineData("@@(myVar.Field)", WatchType.Expression)]
    [InlineData("myVar + 10", WatchType.Expression)]
    public void DetectWatchType_WithComplexExpression_ReturnsExpression(string expression, WatchType expected)
    {
        // Act
        var result = WatchEvaluator.DetectWatchType(expression);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void DetectWatchType_WithEmptyString_ReturnsExpression()
    {
        // Act
        var result = WatchEvaluator.DetectWatchType("");

        // Assert
        Assert.Equal(WatchType.Expression, result);
    }



    [Fact]
    public async Task EvaluateAsync_WhenDumpNotOpen_ReturnsError()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(false);

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression { Expression = "var1" };

        // Act
        var result = await evaluator.EvaluateAsync(watch);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("No dump file is open", result.Error);
    }

    [Fact]
    public async Task EvaluateAsync_WithValidWatch_ReturnsSuccessfulResult()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("00000000`12345678  Hello World");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression
        {
            Id = "watch-1",
            Expression = "0x12345678",
            Type = WatchType.MemoryAddress
        };

        // Act
        var result = await evaluator.EvaluateAsync(watch);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Contains("Hello World", result.Value);
    }

    [Fact]
    public async Task EvaluateAsync_WithMemoryAddress_UsesCorrectWinDbgCommand()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("result");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression
        {
            Expression = "0x12345678",
            Type = WatchType.MemoryAddress
        };

        // Act
        await evaluator.EvaluateAsync(watch);

        // Assert
        mockManager.Verify(m => m.ExecuteCommand(It.Is<string>(cmd => cmd.StartsWith("dq "))), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_WithMemoryAddress_UsesCorrectLldbCommand()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("LLDB");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("result");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression
        {
            Expression = "0x12345678",
            Type = WatchType.MemoryAddress
        };

        // Act
        await evaluator.EvaluateAsync(watch);

        // Assert
        mockManager.Verify(m => m.ExecuteCommand(It.Is<string>(cmd => cmd.StartsWith("memory read "))), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_WithObject_UsesDumpObjectCommand()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("result");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression
        {
            Expression = "0x12345678",
            Type = WatchType.Object
        };

        // Act
        await evaluator.EvaluateAsync(watch);

        // Assert
        mockManager.Verify(m => m.ExecuteCommand(It.Is<string>(cmd => cmd.StartsWith("!do "))), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_WithErrorOutput_ReturnsFailure()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("error: Symbol not found");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression { Expression = "nonExistentVar" };

        // Act
        var result = await evaluator.EvaluateAsync(watch);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task EvaluateAsync_UpdatesWatchLastEvaluatedAt()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("value");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression { Expression = "var1" };
        var originalTime = watch.LastEvaluatedAt;

        // Act
        await evaluator.EvaluateAsync(watch);

        // Assert
        Assert.NotNull(watch.LastEvaluatedAt);
        Assert.NotEqual(originalTime, watch.LastEvaluatedAt);
    }



    [Fact]
    public async Task EvaluateAllAsync_WithNoWatches_ReturnsEmptyReport()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);

        // Act
        var report = await evaluator.EvaluateAllAsync("user1", "dump1");

        // Assert
        Assert.NotNull(report);
        Assert.Equal(0, report.TotalWatches);
        Assert.Empty(report.Watches);
    }

    [Fact]
    public async Task EvaluateAllAsync_WithWatches_EvaluatesAll()
    {
        // Arrange
        await _watchStore.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "var1" });
        await _watchStore.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "var2" });

        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("value");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);

        // Act
        var report = await evaluator.EvaluateAllAsync("user1", "dump1");

        // Assert
        Assert.Equal(2, report.TotalWatches);
        Assert.Equal(2, report.Watches.Count);
        Assert.Equal(2, report.SuccessfulEvaluations);
        Assert.Equal(0, report.FailedEvaluations);
    }

    [Fact]
    public async Task EvaluateAllAsync_WithMixedResults_TracksSuccessAndFailure()
    {
        // Arrange
        await _watchStore.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "goodVar" });
        await _watchStore.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "badVar" });

        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.SetupSequence(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("good value")
            .Returns("error: Symbol not found");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);

        // Act
        var report = await evaluator.EvaluateAllAsync("user1", "dump1");

        // Assert
        Assert.Equal(2, report.TotalWatches);
        Assert.Equal(1, report.SuccessfulEvaluations);
        Assert.Equal(1, report.FailedEvaluations);
    }

    [Fact]
    public async Task EvaluateAllAsync_GeneratesInsightsForNullValues()
    {
        // Arrange
        await _watchStore.AddWatchAsync("user1", "dump1", new WatchExpression
        {
            Expression = "ptr",
            Description = "Important pointer"
        });

        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("00000000`00000000 null");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);

        // Act
        var report = await evaluator.EvaluateAllAsync("user1", "dump1");

        // Assert
        Assert.NotEmpty(report.Insights);
        Assert.Contains(report.Insights, i => i.Contains("NULL"));
    }



    [Fact]
    public async Task EvaluateAsync_WithVariable_WinDbg_WithModuleSymbol_UsesDt()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("result");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression
        {
            Expression = "ntdll!LdrpLoaderLock",
            Type = WatchType.Variable
        };

        // Act
        await evaluator.EvaluateAsync(watch);

        // Assert - Uses dt for module!symbol
        mockManager.Verify(m => m.ExecuteCommand(It.Is<string>(cmd => cmd.StartsWith("dt "))), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_WithVariable_LLDB_WithBacktick_UsesImageLookup()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("LLDB");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("result");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression
        {
            Expression = "myModule`mySymbol",
            Type = WatchType.Variable
        };

        // Act
        await evaluator.EvaluateAsync(watch);

        // Assert
        mockManager.Verify(m => m.ExecuteCommand(It.Is<string>(cmd => cmd.StartsWith("image lookup "))), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_WithVariable_LLDB_SimpleVar_UsesP()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("LLDB");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("result");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression
        {
            Expression = "myLocalVar",
            Type = WatchType.Variable
        };

        // Act
        await evaluator.EvaluateAsync(watch);

        // Assert
        mockManager.Verify(m => m.ExecuteCommand(It.Is<string>(cmd => cmd.StartsWith("p "))), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_WithObject_AlreadyDoCommand_PassesThrough()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("object data");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression
        {
            Expression = "!do 0x12345678",
            Type = WatchType.Object
        };

        // Act
        await evaluator.EvaluateAsync(watch);

        // Assert - Command passed through unchanged
        mockManager.Verify(m => m.ExecuteCommand("!do 0x12345678"), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_WithObject_DumpObjCommand_PassesThrough()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("object data");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression
        {
            Expression = "!dumpobj 0x12345678",
            Type = WatchType.Object
        };

        // Act
        await evaluator.EvaluateAsync(watch);

        // Assert - Command passed through unchanged
        mockManager.Verify(m => m.ExecuteCommand("!dumpobj 0x12345678"), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_WithObject_RawAddress_AddsDo()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("object data");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression
        {
            Expression = "12345678",
            Type = WatchType.Object
        };

        // Act
        await evaluator.EvaluateAsync(watch);

        // Assert - Adds 0x prefix and !do
        mockManager.Verify(m => m.ExecuteCommand("!do 0x12345678"), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_WithExpression_WinDbg_ExistingCommand_PassesThrough()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("result");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression
        {
            Expression = "!threads",
            Type = WatchType.Expression
        };

        // Act
        await evaluator.EvaluateAsync(watch);

        // Assert - Passes through existing command
        mockManager.Verify(m => m.ExecuteCommand("!threads"), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_WithExpression_WinDbg_DotCommand_PassesThrough()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("result");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression
        {
            Expression = ".effmach",
            Type = WatchType.Expression
        };

        // Act
        await evaluator.EvaluateAsync(watch);

        // Assert - Passes through dot command
        mockManager.Verify(m => m.ExecuteCommand(".effmach"), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_WithExpression_LLDB_SosCommand_PassesThrough()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("LLDB");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("result");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression
        {
            Expression = "sos clrstack",
            Type = WatchType.Expression
        };

        // Act
        await evaluator.EvaluateAsync(watch);

        // Assert - Passes through sos command
        mockManager.Verify(m => m.ExecuteCommand("sos clrstack"), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_WithExpression_LLDB_GenericExpression_UsesExpressionCmd()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("LLDB");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("result");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression
        {
            Expression = "x + y",
            Type = WatchType.Expression
        };

        // Act
        await evaluator.EvaluateAsync(watch);

        // Assert
        mockManager.Verify(m => m.ExecuteCommand("expression x + y"), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_WithMemoryAddress_NoPrefix_NormalizesAddress()
    {
        // Arrange
        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>())).Returns("memory data");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);
        var watch = new WatchExpression
        {
            Expression = "12345678",
            Type = WatchType.MemoryAddress
        };

        // Act
        await evaluator.EvaluateAsync(watch);

        // Assert - Adds 0x prefix
        mockManager.Verify(m => m.ExecuteCommand(It.Is<string>(cmd => cmd.Contains("0x12345678"))), Times.Once);
    }



    [Fact]
    public async Task EvaluateAllAsync_GeneratesInsightsForExceptionObject()
    {
        // Arrange - Use WatchType.Object which triggers exception insight
        // Value must contain "exception" but NOT contain "null" (which would trigger NULL insight first)
        await _watchStore.AddWatchAsync("user1", "dump1", new WatchExpression
        {
            Expression = "!do 0x12345678",
            Type = WatchType.Object,
            Description = "Exception object"
        });

        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        // Output contains "exception" but not "null"
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("MT: System.Exception\nMessage: Something went wrong\n_stackTrace: ...");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);

        // Act
        var report = await evaluator.EvaluateAllAsync("user1", "dump1");

        // Assert
        Assert.NotEmpty(report.Insights);
        Assert.Contains(report.Insights, i => i.Contains("exception object"));
    }

    [Fact]
    public async Task EvaluateAllAsync_GeneratesInsightsForUninitializedMemory()
    {
        // Arrange - Pattern needs to match 0x[cC][dD]{6,} (c followed by 6+ d's)
        await _watchStore.AddWatchAsync("user1", "dump1", new WatchExpression
        {
            Expression = "0x12345678",
            Type = WatchType.MemoryAddress,
            Description = "Memory block"
        });

        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        // Pattern 0xCDDDDDDD matches uninitialized memory (C followed by 7 D's)
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("00000000`12345678  0xCDDDDDDD value here");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);

        // Act
        var report = await evaluator.EvaluateAllAsync("user1", "dump1");

        // Assert
        Assert.NotEmpty(report.Insights);
        Assert.Contains(report.Insights, i => i.Contains("uninitialized memory"));
    }

    [Fact]
    public async Task EvaluateAllAsync_GeneratesInsightsForFreedMemory()
    {
        // Arrange - Pattern needs to match 0x[dD]{8,}
        await _watchStore.AddWatchAsync("user1", "dump1", new WatchExpression
        {
            Expression = "0x12345678",
            Type = WatchType.MemoryAddress,
            Description = "Memory block"
        });

        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        // Pattern 0xDDDDDDDD (8 D's) matches freed memory
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("00000000`12345678  0xDDDDDDDD value here");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);

        // Act
        var report = await evaluator.EvaluateAllAsync("user1", "dump1");

        // Assert
        Assert.NotEmpty(report.Insights);
        Assert.Contains(report.Insights, i => i.Contains("freed memory pattern"));
    }

    [Fact]
    public async Task EvaluateAllAsync_GeneratesInsightsForFreedHeapMemory()
    {
        // Arrange - Pattern needs to match 0x[fF][eE][eE][eE]
        await _watchStore.AddWatchAsync("user1", "dump1", new WatchExpression
        {
            Expression = "0x12345678",
            Type = WatchType.MemoryAddress,
            Description = "Memory block"
        });

        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        // Pattern 0xFEEEFEEE matches freed heap memory
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("00000000`12345678  0xFEEEFEEE value here");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);

        // Act
        var report = await evaluator.EvaluateAllAsync("user1", "dump1");

        // Assert
        Assert.NotEmpty(report.Insights);
        Assert.Contains(report.Insights, i => i.Contains("freed heap memory"));
    }

    [Fact]
    public async Task EvaluateAllAsync_GeneratesInsightsForHighFailureRate()
    {
        // Arrange - Add multiple watches where most will fail
        await _watchStore.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "bad1" });
        await _watchStore.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "bad2" });
        await _watchStore.AddWatchAsync("user1", "dump1", new WatchExpression { Expression = "bad3" });

        var mockManager = new Mock<IDebuggerManager>();
        mockManager.Setup(m => m.IsDumpOpen).Returns(true);
        mockManager.Setup(m => m.DebuggerType).Returns("WinDbg");
        mockManager.Setup(m => m.ExecuteCommand(It.IsAny<string>()))
            .Returns("error: Symbol not found");

        var evaluator = new WatchEvaluator(mockManager.Object, _watchStore);

        // Act
        var report = await evaluator.EvaluateAllAsync("user1", "dump1");

        // Assert
        Assert.Equal(3, report.FailedEvaluations);
        Assert.Contains(report.Insights, i => i.Contains("failure rate"));
    }

}

