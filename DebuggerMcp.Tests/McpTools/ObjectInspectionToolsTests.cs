using DebuggerMcp.Analysis;
using DebuggerMcp.McpTools;
using DebuggerMcp.Tests.TestDoubles;
using DebuggerMcp.Watches;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DebuggerMcp.Tests.McpTools;

/// <summary>
/// Tests for <see cref="ObjectInspectionTools"/>.
/// </summary>
public class ObjectInspectionToolsTests
{
    [Fact]
    public void InspectObject_WithMissingAddress_ReturnsJsonError()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"ObjectInspectionToolsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sessionManager = new DebuggerSessionManager(
                tempDir,
                debuggerFactory: _ => new FakeDebuggerManager());
            var symbolManager = new SymbolManager(tempDir);
            var watchStore = new WatchStore(tempDir);
            var tools = new ObjectInspectionTools(
                sessionManager,
                symbolManager,
                watchStore,
                NullLogger<ObjectInspectionTools>.Instance);

            var sessionId = sessionManager.CreateSession("user");
            var session = sessionManager.GetSessionInfo(sessionId, "user");

            // Act
            var json = tools.InspectObject(sessionId, "user", address: "   ");

            // Assert
            Assert.Contains("Address is required", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    [Fact]
    public void InspectObject_WhenClrMdNotOpen_ReturnsJsonError()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"ObjectInspectionToolsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sessionManager = new DebuggerSessionManager(
                tempDir,
                debuggerFactory: _ => new FakeDebuggerManager());
            var symbolManager = new SymbolManager(tempDir);
            var watchStore = new WatchStore(tempDir);
            var tools = new ObjectInspectionTools(
                sessionManager,
                symbolManager,
                watchStore,
                NullLogger<ObjectInspectionTools>.Instance);

            var sessionId = sessionManager.CreateSession("user");
            var session = sessionManager.GetSessionInfo(sessionId, "user");

            // Analyzer exists but is not open.
            session.ClrMdAnalyzer = new ClrMdAnalyzer();

            // Act
            var json = tools.InspectObject(sessionId, "user", address: "0x1234");

            // Assert
            Assert.Contains("ClrMD analyzer not available", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    [Fact]
    public void InspectObject_WithInvalidMethodTable_IgnoresMethodTableAndReturnsClrMdNotOpenError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ObjectInspectionToolsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sessionManager = new DebuggerSessionManager(
                tempDir,
                debuggerFactory: _ => new FakeDebuggerManager());
            var symbolManager = new SymbolManager(tempDir);
            var watchStore = new WatchStore(tempDir);
            var tools = new ObjectInspectionTools(
                sessionManager,
                symbolManager,
                watchStore,
                NullLogger<ObjectInspectionTools>.Instance);

            var sessionId = sessionManager.CreateSession("user");
            var session = sessionManager.GetSessionInfo(sessionId, "user");
            session.ClrMdAnalyzer = new ClrMdAnalyzer();

            var json = tools.InspectObject(sessionId, "user", address: "0x1234", methodTable: "not-hex");

            Assert.Contains("ClrMD analyzer not available", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    [Fact]
    public void InspectObject_WithUnknownSession_ReturnsJsonError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ObjectInspectionToolsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sessionManager = new DebuggerSessionManager(
                tempDir,
                debuggerFactory: _ => new FakeDebuggerManager());
            var symbolManager = new SymbolManager(tempDir);
            var watchStore = new WatchStore(tempDir);
            var tools = new ObjectInspectionTools(
                sessionManager,
                symbolManager,
                watchStore,
                NullLogger<ObjectInspectionTools>.Instance);

            var json = tools.InspectObject("missing", "user", address: "0x1234");

            Assert.Contains("not found", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    [Theory]
    [InlineData("not-hex")]
    [InlineData("0xZZZ")]
    public void InspectObject_WithInvalidAddress_ReturnsJsonError(string address)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ObjectInspectionToolsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sessionManager = new DebuggerSessionManager(
                tempDir,
                debuggerFactory: _ => new FakeDebuggerManager());
            var symbolManager = new SymbolManager(tempDir);
            var watchStore = new WatchStore(tempDir);
            var tools = new ObjectInspectionTools(
                sessionManager,
                symbolManager,
                watchStore,
                NullLogger<ObjectInspectionTools>.Instance);

            var sessionId = sessionManager.CreateSession("user");
            var session = sessionManager.GetSessionInfo(sessionId, "user");
            session.ClrMdAnalyzer = new ClrMdAnalyzer();

            var json = tools.InspectObject(sessionId, "user", address: address);

            Assert.Contains("Invalid address format", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    [Fact]
    public void Name2EE_WithMissingTypeName_ReturnsJsonError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ObjectInspectionToolsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sessionManager = new DebuggerSessionManager(
                tempDir,
                debuggerFactory: _ => new FakeDebuggerManager());
            var symbolManager = new SymbolManager(tempDir);
            var watchStore = new WatchStore(tempDir);
            var tools = new ObjectInspectionTools(
                sessionManager,
                symbolManager,
                watchStore,
                NullLogger<ObjectInspectionTools>.Instance);

            var sessionId = sessionManager.CreateSession("user");

            var json = tools.Name2EE(sessionId, "user", typeName: "   ");

            Assert.Contains("Type name is required", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    [Fact]
    public void Name2EEMethod_WithMissingTypeName_ReturnsJsonError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ObjectInspectionToolsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sessionManager = new DebuggerSessionManager(
                tempDir,
                debuggerFactory: _ => new FakeDebuggerManager());
            var symbolManager = new SymbolManager(tempDir);
            var watchStore = new WatchStore(tempDir);
            var tools = new ObjectInspectionTools(
                sessionManager,
                symbolManager,
                watchStore,
                NullLogger<ObjectInspectionTools>.Instance);

            var sessionId = sessionManager.CreateSession("user");

            var json = tools.Name2EEMethod(sessionId, "user", typeName: " ", methodName: "Foo");

            Assert.Contains("Type name is required", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    [Fact]
    public void Name2EEMethod_WithMissingMethodName_ReturnsJsonError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ObjectInspectionToolsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sessionManager = new DebuggerSessionManager(
                tempDir,
                debuggerFactory: _ => new FakeDebuggerManager());
            var symbolManager = new SymbolManager(tempDir);
            var watchStore = new WatchStore(tempDir);
            var tools = new ObjectInspectionTools(
                sessionManager,
                symbolManager,
                watchStore,
                NullLogger<ObjectInspectionTools>.Instance);

            var sessionId = sessionManager.CreateSession("user");

            var json = tools.Name2EEMethod(sessionId, "user", typeName: "System.String", methodName: " ");

            Assert.Contains("Method name is required", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    [Fact]
    public void DumpModule_WithMissingAddress_ReturnsJsonError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ObjectInspectionToolsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sessionManager = new DebuggerSessionManager(
                tempDir,
                debuggerFactory: _ => new FakeDebuggerManager());
            var symbolManager = new SymbolManager(tempDir);
            var watchStore = new WatchStore(tempDir);
            var tools = new ObjectInspectionTools(
                sessionManager,
                symbolManager,
                watchStore,
                NullLogger<ObjectInspectionTools>.Instance);

            var sessionId = sessionManager.CreateSession("user");

            var json = tools.DumpModule(sessionId, "user", address: " ");

            Assert.Contains("Address is required", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    [Theory]
    [InlineData("not-hex")]
    [InlineData("0xZZZ")]
    public void DumpModule_WithInvalidAddress_ReturnsJsonError(string address)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ObjectInspectionToolsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sessionManager = new DebuggerSessionManager(
                tempDir,
                debuggerFactory: _ => new FakeDebuggerManager());
            var symbolManager = new SymbolManager(tempDir);
            var watchStore = new WatchStore(tempDir);
            var tools = new ObjectInspectionTools(
                sessionManager,
                symbolManager,
                watchStore,
                NullLogger<ObjectInspectionTools>.Instance);

            var sessionId = sessionManager.CreateSession("user");
            var session = sessionManager.GetSessionInfo(sessionId, "user");
            session.ClrMdAnalyzer = new ClrMdAnalyzer();

            var json = tools.DumpModule(sessionId, "user", address: address);

            Assert.Contains("Invalid address format", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    [Fact]
    public void DumpModule_WithUnknownSession_ReturnsJsonError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ObjectInspectionToolsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sessionManager = new DebuggerSessionManager(
                tempDir,
                debuggerFactory: _ => new FakeDebuggerManager());
            var symbolManager = new SymbolManager(tempDir);
            var watchStore = new WatchStore(tempDir);
            var tools = new ObjectInspectionTools(
                sessionManager,
                symbolManager,
                watchStore,
                NullLogger<ObjectInspectionTools>.Instance);

            var json = tools.DumpModule("missing", "user", address: "0x1234");

            Assert.Contains("not found", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    [Fact]
    public void ListModules_WhenClrMdNotOpen_ReturnsJsonError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ObjectInspectionToolsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sessionManager = new DebuggerSessionManager(
                tempDir,
                debuggerFactory: _ => new FakeDebuggerManager());
            var symbolManager = new SymbolManager(tempDir);
            var watchStore = new WatchStore(tempDir);
            var tools = new ObjectInspectionTools(
                sessionManager,
                symbolManager,
                watchStore,
                NullLogger<ObjectInspectionTools>.Instance);

            var sessionId = sessionManager.CreateSession("user");
            var session = sessionManager.GetSessionInfo(sessionId, "user");
            session.ClrMdAnalyzer = new ClrMdAnalyzer();

            var json = tools.ListModules(sessionId, "user");

            Assert.Contains("ClrMD analyzer not available", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    [Fact]
    public void ClrStack_WhenClrMdNotOpen_ReturnsJsonError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ObjectInspectionToolsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sessionManager = new DebuggerSessionManager(
                tempDir,
                debuggerFactory: _ => new FakeDebuggerManager());
            var symbolManager = new SymbolManager(tempDir);
            var watchStore = new WatchStore(tempDir);
            var tools = new ObjectInspectionTools(
                sessionManager,
                symbolManager,
                watchStore,
                NullLogger<ObjectInspectionTools>.Instance);

            var sessionId = sessionManager.CreateSession("user");
            var session = sessionManager.GetSessionInfo(sessionId, "user");
            session.ClrMdAnalyzer = new ClrMdAnalyzer();

            var json = tools.ClrStack(sessionId, "user");

            Assert.Contains("ClrMD analyzer not available", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    [Fact]
    public void ClrStack_WithUnknownSession_ReturnsJsonError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ObjectInspectionToolsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sessionManager = new DebuggerSessionManager(
                tempDir,
                debuggerFactory: _ => new FakeDebuggerManager());
            var symbolManager = new SymbolManager(tempDir);
            var watchStore = new WatchStore(tempDir);
            var tools = new ObjectInspectionTools(
                sessionManager,
                symbolManager,
                watchStore,
                NullLogger<ObjectInspectionTools>.Instance);

            var json = tools.ClrStack("missing", "user");

            Assert.Contains("not found", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }
}
