using System.Runtime.InteropServices;

namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for DebuggerFactory class.
/// </summary>
public class DebuggerFactoryTests
{
    // ============================================================
    // CreateDebugger Tests
    // ============================================================

    [Fact]
    public void CreateDebugger_OnSupportedPlatform_ReturnsDebuggerManager()
    {
        // Act & Assert - should not throw on Windows/Linux/macOS
        if (DebuggerFactory.IsPlatformSupported())
        {
            var debugger = DebuggerFactory.CreateDebugger();
            Assert.NotNull(debugger);
            Assert.IsAssignableFrom<IDebuggerManager>(debugger);
        }
    }

    [Fact]
    public void CreateDebugger_OnWindows_ReturnsWinDbgManager()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Act
        var debugger = DebuggerFactory.CreateDebugger();

        // Assert
        Assert.IsType<WinDbgManager>(debugger);
    }

    [Fact]
    public void CreateDebugger_OnLinux_ReturnsLldbManager()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return; // Skip on non-Linux

        // Act
        var debugger = DebuggerFactory.CreateDebugger();

        // Assert
        Assert.IsType<LldbManager>(debugger);
    }

    [Fact]
    public void CreateDebugger_OnMacOS_ReturnsLldbManager()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return; // Skip on non-macOS

        // Act
        var debugger = DebuggerFactory.CreateDebugger();

        // Assert
        Assert.IsType<LldbManager>(debugger);
    }

    // ============================================================
    // GetDebuggerType Tests
    // ============================================================

    [Fact]
    public void GetDebuggerType_OnSupportedPlatform_ReturnsDebuggerTypeName()
    {
        // Act & Assert
        if (DebuggerFactory.IsPlatformSupported())
        {
            var type = DebuggerFactory.GetDebuggerType();
            Assert.NotNull(type);
            Assert.True(type == "WinDbg" || type == "LLDB");
        }
    }

    [Fact]
    public void GetDebuggerType_OnWindows_ReturnsWinDbg()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        // Act
        var type = DebuggerFactory.GetDebuggerType();

        // Assert
        Assert.Equal("WinDbg", type);
    }

    [Fact]
    public void GetDebuggerType_OnLinux_ReturnsLLDB()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        // Act
        var type = DebuggerFactory.GetDebuggerType();

        // Assert
        Assert.Equal("LLDB", type);
    }

    [Fact]
    public void GetDebuggerType_OnMacOS_ReturnsLLDB()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        // Act
        var type = DebuggerFactory.GetDebuggerType();

        // Assert
        Assert.Equal("LLDB", type);
    }

    // ============================================================
    // IsPlatformSupported Tests
    // ============================================================

    [Fact]
    public void IsPlatformSupported_OnCurrentPlatform_ReturnsExpectedResult()
    {
        // Act
        var isSupported = DebuggerFactory.IsPlatformSupported();

        // Assert - On Windows/Linux/macOS should be true
        var expectedSupported =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        Assert.Equal(expectedSupported, isSupported);
    }

    [Fact]
    public void IsPlatformSupported_OnWindows_ReturnsTrue()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        Assert.True(DebuggerFactory.IsPlatformSupported());
    }

    [Fact]
    public void IsPlatformSupported_OnLinux_ReturnsTrue()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        Assert.True(DebuggerFactory.IsPlatformSupported());
    }

    [Fact]
    public void IsPlatformSupported_OnMacOS_ReturnsTrue()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        Assert.True(DebuggerFactory.IsPlatformSupported());
    }

    // ============================================================
    // GetCurrentPlatform Tests
    // ============================================================

    [Fact]
    public void GetCurrentPlatform_ReturnsNonEmptyString()
    {
        // Act
        var platform = DebuggerFactory.GetCurrentPlatform();

        // Assert
        Assert.NotNull(platform);
        Assert.NotEmpty(platform);
    }

    [Fact]
    public void GetCurrentPlatform_ReturnsKnownPlatform()
    {
        // Act
        var platform = DebuggerFactory.GetCurrentPlatform();

        // Assert - should be one of the known platforms
        var knownPlatforms = new[] { "Windows", "Linux", "macOS", "Unknown" };
        Assert.Contains(platform, knownPlatforms);
    }

    [Fact]
    public void GetCurrentPlatform_OnWindows_ReturnsWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        Assert.Equal("Windows", DebuggerFactory.GetCurrentPlatform());
    }

    [Fact]
    public void GetCurrentPlatform_OnLinux_ReturnsLinux()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        Assert.Equal("Linux", DebuggerFactory.GetCurrentPlatform());
    }

    [Fact]
    public void GetCurrentPlatform_OnMacOS_ReturnsMacOS()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        Assert.Equal("macOS", DebuggerFactory.GetCurrentPlatform());
    }

    // ============================================================
    // Cross-method Consistency Tests
    // ============================================================

    [Fact]
    public void GetDebuggerType_And_CreateDebugger_AreConsistent()
    {
        // Only test if platform is supported
        if (!DebuggerFactory.IsPlatformSupported())
            return;

        // Act
        var type = DebuggerFactory.GetDebuggerType();
        var debugger = DebuggerFactory.CreateDebugger();

        // Assert
        if (type == "WinDbg" && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // CA1416: WinDbgManager is Windows-only, but we're inside a Windows platform check
            Assert.IsType<WinDbgManager>(debugger);
        }
        else if (type == "LLDB")
        {
            Assert.IsType<LldbManager>(debugger);
        }
    }

    [Fact]
    public void GetCurrentPlatform_And_IsPlatformSupported_AreConsistent()
    {
        // Act
        var platform = DebuggerFactory.GetCurrentPlatform();
        var isSupported = DebuggerFactory.IsPlatformSupported();

        // Assert - if we know the platform, it should be supported
        if (platform != "Unknown")
        {
            Assert.True(isSupported);
        }
    }

    [Fact]
    public void GetCurrentPlatform_And_GetDebuggerType_AreConsistent()
    {
        // Only test if platform is supported
        if (!DebuggerFactory.IsPlatformSupported())
            return;

        // Act
        var platform = DebuggerFactory.GetCurrentPlatform();
        var debuggerType = DebuggerFactory.GetDebuggerType();

        // Assert
        if (platform == "Windows")
        {
            Assert.Equal("WinDbg", debuggerType);
        }
        else if (platform == "Linux" || platform == "macOS")
        {
            Assert.Equal("LLDB", debuggerType);
        }
    }

    // ============================================================
    // Disposal Tests
    // ============================================================

    [Fact]
    public void CreateDebugger_ReturnsDisposableManager()
    {
        // Only test if platform is supported
        if (!DebuggerFactory.IsPlatformSupported())
            return;

        // Act
        var debugger = DebuggerFactory.CreateDebugger();

        // Assert
        Assert.IsAssignableFrom<IDisposable>(debugger);
    }

    [Fact]
    public void CreateDebugger_ReturnsAsyncDisposableManager()
    {
        // Only test if platform is supported
        if (!DebuggerFactory.IsPlatformSupported())
            return;

        // Act
        var debugger = DebuggerFactory.CreateDebugger();

        // Assert
        Assert.IsAssignableFrom<IAsyncDisposable>(debugger);
    }

    // ============================================================
    // Debugger Manager Initial State Tests
    // ============================================================

    [Fact]
    public void CreateDebugger_ReturnsManagerWithDumpNotOpen()
    {
        // Only test if platform is supported
        if (!DebuggerFactory.IsPlatformSupported())
            return;

        // Act
        var debugger = DebuggerFactory.CreateDebugger();

        // Assert
        Assert.False(debugger.IsDumpOpen);
    }

    [Fact]
    public void CreateDebugger_ReturnsManagerNotInitialized()
    {
        // Only test if platform is supported
        if (!DebuggerFactory.IsPlatformSupported())
            return;

        // Act
        var debugger = DebuggerFactory.CreateDebugger();

        // Assert
        Assert.False(debugger.IsInitialized);
    }

    [Fact]
    public void CreateDebugger_ReturnsManagerWithCorrectDebuggerType()
    {
        // Only test if platform is supported
        if (!DebuggerFactory.IsPlatformSupported())
            return;

        // Act
        var debugger = DebuggerFactory.CreateDebugger();
        var expectedType = DebuggerFactory.GetDebuggerType();

        // Assert
        Assert.Equal(expectedType, debugger.DebuggerType);
    }
}
