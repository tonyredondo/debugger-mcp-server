namespace DebuggerMcp.Tests;

/// <summary>
/// Tests for the McpResources (DebuggerResources) class.
/// Verifies that all MCP resources are correctly loaded and contain expected content.
/// </summary>
public class McpResourcesTests
{
    // ========== GetMcpTools Tests ==========

    [Fact]
    public void GetMcpTools_ReturnsNonEmptyContent()
    {
        var content = DebuggerResources.GetMcpTools();

        Assert.NotNull(content);
        Assert.NotEmpty(content);
    }

    [Fact]
    public void GetMcpTools_ContainsExpectedToolNames()
    {
        var content = DebuggerResources.GetMcpTools();

        Assert.Contains("session", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dump", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("analyze", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compare", content, StringComparison.OrdinalIgnoreCase);
    }

    // ========== GetWorkflowGuide Tests ==========

    [Fact]
    public void GetWorkflowGuide_ReturnsNonEmptyContent()
    {
        // Act
        var content = DebuggerResources.GetWorkflowGuide();

        // Assert
        Assert.NotNull(content);
        Assert.NotEmpty(content);
    }

    [Fact]
    public void GetWorkflowGuide_ContainsExpectedSections()
    {
        // Act
        var content = DebuggerResources.GetWorkflowGuide();

        // Assert - should contain workflow-related content
        Assert.Contains("workflow", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetWorkflowGuide_IsMarkdown()
    {
        // Act
        var content = DebuggerResources.GetWorkflowGuide();

        // Assert - Markdown files typically start with # or have # headers
        Assert.True(
            content.Contains('#') || content.StartsWith("---"),
            "Content should be Markdown format");
    }

    // ========== GetAnalysisGuide Tests ==========

    [Fact]
    public void GetAnalysisGuide_ReturnsNonEmptyContent()
    {
        // Act
        var content = DebuggerResources.GetAnalysisGuide();

        // Assert
        Assert.NotNull(content);
        Assert.NotEmpty(content);
    }

    [Fact]
    public void GetAnalysisGuide_ContainsAnalysisContent()
    {
        // Act
        var content = DebuggerResources.GetAnalysisGuide();

        // Assert - should contain analysis-related content
        Assert.Contains("analysis", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetAnalysisGuide_ContainsCrashAnalysis()
    {
        // Act
        var content = DebuggerResources.GetAnalysisGuide();

        // Assert
        Assert.Contains("crash", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetAnalysisGuide_ContainsDumpComparison()
    {
        // Act
        var content = DebuggerResources.GetAnalysisGuide();

        // Assert - should mention dump comparison
        Assert.Contains("compare", content, StringComparison.OrdinalIgnoreCase);
    }

    // ========== GetWinDbgCommands Tests ==========

    [Fact]
    public void GetWinDbgCommands_ReturnsNonEmptyContent()
    {
        // Act
        var content = DebuggerResources.GetWinDbgCommands();

        // Assert
        Assert.NotNull(content);
        Assert.NotEmpty(content);
    }

    [Fact]
    public void GetWinDbgCommands_ContainsCommonCommands()
    {
        // Act
        var content = DebuggerResources.GetWinDbgCommands();

        // Assert - should contain WinDbg-specific commands
        Assert.True(
            content.Contains("!analyze", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("windbg", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("command", StringComparison.OrdinalIgnoreCase),
            "Content should contain WinDbg command references");
    }

    [Fact]
    public void GetWinDbgCommands_IsMarkdown()
    {
        // Act
        var content = DebuggerResources.GetWinDbgCommands();

        // Assert
        Assert.Contains('#', content.ToString());
    }

    // ========== GetLldbCommands Tests ==========

    [Fact]
    public void GetLldbCommands_ReturnsNonEmptyContent()
    {
        // Act
        var content = DebuggerResources.GetLldbCommands();

        // Assert
        Assert.NotNull(content);
        Assert.NotEmpty(content);
    }

    [Fact]
    public void GetLldbCommands_ContainsLldbContent()
    {
        // Act
        var content = DebuggerResources.GetLldbCommands();

        // Assert - should contain LLDB-specific content
        Assert.True(
            content.Contains("lldb", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("command", StringComparison.OrdinalIgnoreCase),
            "Content should contain LLDB references");
    }

    [Fact]
    public void GetLldbCommands_IsMarkdown()
    {
        // Act
        var content = DebuggerResources.GetLldbCommands();

        // Assert
        Assert.Contains('#', content.ToString());
    }

    // ========== GetSosCommands Tests ==========

    [Fact]
    public void GetSosCommands_ReturnsNonEmptyContent()
    {
        // Act
        var content = DebuggerResources.GetSosCommands();

        // Assert
        Assert.NotNull(content);
        Assert.NotEmpty(content);
    }

    [Fact]
    public void GetSosCommands_ContainsSosContent()
    {
        // Act
        var content = DebuggerResources.GetSosCommands();

        // Assert - should contain SOS-specific content
        Assert.True(
            content.Contains("sos", StringComparison.OrdinalIgnoreCase) ||
            content.Contains(".net", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("clr", StringComparison.OrdinalIgnoreCase),
            "Content should contain SOS or .NET references");
    }

    [Fact]
    public void GetSosCommands_ContainsDotNetCommands()
    {
        // Act
        var content = DebuggerResources.GetSosCommands();

        // Assert - should contain common SOS commands
        Assert.True(
            content.Contains("dumpheap", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("clrstack", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("dumpobj", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("command", StringComparison.OrdinalIgnoreCase),
            "Content should contain common SOS command references");
    }

    // ========== GetTroubleshooting Tests ==========

    [Fact]
    public void GetTroubleshooting_ReturnsNonEmptyContent()
    {
        // Act
        var content = DebuggerResources.GetTroubleshooting();

        // Assert
        Assert.NotNull(content);
        Assert.NotEmpty(content);
    }

    [Fact]
    public void GetTroubleshooting_ContainsTroubleshootingContent()
    {
        // Act
        var content = DebuggerResources.GetTroubleshooting();

        // Assert
        Assert.True(
            content.Contains("troubleshoot", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("issue", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("problem", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("error", StringComparison.OrdinalIgnoreCase),
            "Content should contain troubleshooting-related content");
    }

    [Fact]
    public void GetTroubleshooting_IsMarkdown()
    {
        // Act
        var content = DebuggerResources.GetTroubleshooting();

        // Assert
        Assert.Contains('#', content.ToString());
    }

    // ========== Cross-Cutting Tests ==========

    [Fact]
    public void AllResources_AreDistinct()
    {
        // Act
        var workflow = DebuggerResources.GetWorkflowGuide();
        var analysis = DebuggerResources.GetAnalysisGuide();
        var mcpTools = DebuggerResources.GetMcpTools();
        var windbg = DebuggerResources.GetWinDbgCommands();
        var lldb = DebuggerResources.GetLldbCommands();
        var sos = DebuggerResources.GetSosCommands();
        var troubleshooting = DebuggerResources.GetTroubleshooting();

        // Assert - each resource should be different
        var resources = new[] { workflow, analysis, mcpTools, windbg, lldb, sos, troubleshooting };
        var distinctCount = resources.Distinct().Count();
        Assert.Equal(7, distinctCount);
    }

    [Fact]
    public void AllResources_DoNotContainErrorMessages()
    {
        // Act
        var workflow = DebuggerResources.GetWorkflowGuide();
        var analysis = DebuggerResources.GetAnalysisGuide();
        var mcpTools = DebuggerResources.GetMcpTools();
        var windbg = DebuggerResources.GetWinDbgCommands();
        var lldb = DebuggerResources.GetLldbCommands();
        var sos = DebuggerResources.GetSosCommands();
        var troubleshooting = DebuggerResources.GetTroubleshooting();

        // Assert - none should contain error loading messages
        var resources = new[] { workflow, analysis, mcpTools, windbg, lldb, sos, troubleshooting };
        foreach (var resource in resources)
        {
            Assert.DoesNotContain("Error Loading Resource", resource);
            Assert.DoesNotContain("Resource Not Found", resource);
        }
    }

    [Fact]
    public void AllResources_HaveReasonableLength()
    {
        // Act
        var workflow = DebuggerResources.GetWorkflowGuide();
        var analysis = DebuggerResources.GetAnalysisGuide();
        var mcpTools = DebuggerResources.GetMcpTools();
        var windbg = DebuggerResources.GetWinDbgCommands();
        var lldb = DebuggerResources.GetLldbCommands();
        var sos = DebuggerResources.GetSosCommands();
        var troubleshooting = DebuggerResources.GetTroubleshooting();

        // Assert - each resource should have meaningful content (at least 100 chars)
        Assert.True(workflow.Length >= 100, "Workflow guide should have substantial content");
        Assert.True(analysis.Length >= 100, "Analysis guide should have substantial content");
        Assert.True(mcpTools.Length >= 100, "MCP tools guide should have substantial content");
        Assert.True(windbg.Length >= 100, "WinDbg commands should have substantial content");
        Assert.True(lldb.Length >= 100, "LLDB commands should have substantial content");
        Assert.True(sos.Length >= 100, "SOS commands should have substantial content");
        Assert.True(troubleshooting.Length >= 100, "Troubleshooting guide should have substantial content");
    }

    [Fact]
    public void MultipleCallsToSameResource_ReturnSameContent()
    {
        // Act - call each method twice
        var workflow1 = DebuggerResources.GetWorkflowGuide();
        var workflow2 = DebuggerResources.GetWorkflowGuide();

        var analysis1 = DebuggerResources.GetAnalysisGuide();
        var analysis2 = DebuggerResources.GetAnalysisGuide();

        // Assert - content should be identical (cached/static)
        Assert.Equal(workflow1, workflow2);
        Assert.Equal(analysis1, analysis2);
    }

    // ========== Content Quality Tests ==========

    [Fact]
    public void WorkflowGuide_ContainsSessionManagement()
    {
        // Act
        var content = DebuggerResources.GetWorkflowGuide();

        // Assert - workflow should mention sessions
        Assert.True(
            content.Contains("session", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("create", StringComparison.OrdinalIgnoreCase),
            "Workflow should reference session management");
    }

    [Fact]
    public void WorkflowGuide_ContainsDumpOperations()
    {
        // Act
        var content = DebuggerResources.GetWorkflowGuide();

        // Assert - workflow should mention dump operations
        Assert.Contains("dump", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnalysisGuide_ContainsPerformanceAnalysis()
    {
        // Act
        var content = DebuggerResources.GetAnalysisGuide();

        // Assert - should cover performance analysis
        Assert.True(
            content.Contains("performance", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("cpu", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("memory", StringComparison.OrdinalIgnoreCase),
            "Analysis guide should cover performance analysis");
    }

    [Fact]
    public void WinDbgCommands_ContainsExtensionCommands()
    {
        // Act
        var content = DebuggerResources.GetWinDbgCommands();

        // Assert - should have extension commands (start with !)
        Assert.Contains("!", content);
    }

    [Fact]
    public void SosCommands_ContainsHeapAnalysis()
    {
        // Act
        var content = DebuggerResources.GetSosCommands();

        // Assert - should cover heap analysis
        Assert.Contains("heap", content, StringComparison.OrdinalIgnoreCase);
    }

    // ========== LoadResourceFile Tests (Error Paths) ==========

    [Fact]
    public void LoadResourceFile_WithNonExistentFile_ReturnsFallbackMessage()
    {
        // Act
        var content = DebuggerResources.LoadResourceFile("non_existent_file_12345.md");

        // Assert - should return a fallback message, not throw
        Assert.NotNull(content);
        Assert.Contains("Resource Not Found", content);
        Assert.Contains("non_existent_file_12345.md", content);
    }

    [Fact]
    public void LoadResourceFile_WithEmptyFileName_ReturnsFallbackOrHandlesGracefully()
    {
        // Act
        var content = DebuggerResources.LoadResourceFile("");

        // Assert - should handle gracefully
        Assert.NotNull(content);
    }

    [Fact]
    public void LoadResourceFile_WithNullFileName_ReturnsFallbackOrHandlesGracefully()
    {
        // Act & Assert - should either return fallback or throw ArgumentNullException
        try
        {
            var content = DebuggerResources.LoadResourceFile(null!);
            Assert.NotNull(content);
        }
        catch (ArgumentNullException)
        {
            // This is also acceptable behavior
        }
    }

    [Fact]
    public void LoadResourceFile_WithSpecialCharacters_ReturnsFallbackMessage()
    {
        // Act
        var content = DebuggerResources.LoadResourceFile("file<>:\"/\\|?*.md");

        // Assert - should return fallback or error message, not crash
        Assert.NotNull(content);
        Assert.True(
            content.Contains("Resource Not Found") || content.Contains("Error Loading Resource"),
            "Should return a fallback or error message for invalid file names");
    }

    [Fact]
    public void LoadResourceFile_WithValidFile_ReturnsContent()
    {
        // Act - use a known existing resource
        var content = DebuggerResources.LoadResourceFile("workflow_guide.md");

        // Assert - should return actual content, not error
        Assert.NotNull(content);
        Assert.DoesNotContain("Resource Not Found", content);
        Assert.DoesNotContain("Error Loading Resource", content);
    }

    [Fact]
    public void LoadResourceFile_WithPathTraversal_ReturnsFallbackMessage()
    {
        // Act - try path traversal
        var content = DebuggerResources.LoadResourceFile("../../../etc/passwd");

        // Assert - should return fallback, not actual file content
        Assert.NotNull(content);
        Assert.True(
            content.Contains("Resource Not Found") || content.Contains("Error Loading Resource"),
            "Should not allow path traversal");
    }
}
