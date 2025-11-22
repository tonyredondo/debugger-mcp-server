using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DebuggerMcp;

/// <summary>
/// Contains MCP resources for the debugger server.
/// Resources are read-only data that can be exposed to MCP clients.
/// </summary>
/// <remarks>
/// MCP Resources follow the pattern:
/// - Each resource has a unique URI
/// - Resources can be text or binary (MIME type specified)
/// - Resources are listed via resources/list and read via resources/read
/// 
/// Available resources:
/// - debugger://workflow-guide - Complete workflow for analyzing memory dumps
/// - debugger://analysis-guide - Guide to automated analysis features and dump comparison
/// - debugger://windbg-commands - WinDbg commands reference
/// - debugger://lldb-commands - LLDB commands reference
/// - debugger://sos-commands - .NET SOS commands reference
/// - debugger://troubleshooting - Troubleshooting guide
/// - debugger://cli-guide - CLI client usage guide
/// </remarks>
[McpServerResourceType]
public class DebuggerResources
{
    private static readonly string WorkflowGuideContent;
    private static readonly string AnalysisGuideContent;
    private static readonly string WinDbgCommandsContent;
    private static readonly string LldbCommandsContent;
    private static readonly string SosCommandsContent;
    private static readonly string TroubleshootingContent;
    private static readonly string CliGuideContent;

    static DebuggerResources()
    {
        // Load resources from files at startup
        WorkflowGuideContent = LoadResourceFile("workflow_guide.md");
        AnalysisGuideContent = LoadResourceFile("analysis_guide.md");
        WinDbgCommandsContent = LoadResourceFile("windbg_commands.md");
        LldbCommandsContent = LoadResourceFile("lldb_commands.md");
        SosCommandsContent = LoadResourceFile("sos_commands.md");
        TroubleshootingContent = LoadResourceFile("troubleshooting.md");
        CliGuideContent = LoadResourceFile("cli_guide.md");
    }

    /// <summary>
    /// Gets the workflow guide for analyzing memory dumps.
    /// </summary>
    [McpServerResource, Description("Complete workflow for analyzing memory dumps using the debugger MCP server")]
    public static string GetWorkflowGuide()
    {
        return WorkflowGuideContent;
    }

    /// <summary>
    /// Gets the analysis guide covering crash analysis, .NET analysis, and dump comparison features.
    /// </summary>
    [McpServerResource, Description("Complete guide to automated analysis features including crash analysis, .NET analysis, and dump comparison")]
    public static string GetAnalysisGuide()
    {
        return AnalysisGuideContent;
    }

    /// <summary>
    /// Gets a reference of common WinDbg commands.
    /// </summary>
    [McpServerResource, Description("Common WinDbg commands for crash analysis and debugging on Windows")]
    public static string GetWinDbgCommands()
    {
        return WinDbgCommandsContent;
    }

    /// <summary>
    /// Gets a reference of common LLDB commands.
    /// </summary>
    [McpServerResource, Description("Common LLDB commands for crash analysis and debugging on macOS/Linux")]
    public static string GetLldbCommands()
    {
        return LldbCommandsContent;
    }

    /// <summary>
    /// Gets .NET SOS debugging commands reference.
    /// </summary>
    [McpServerResource, Description("Common SOS commands for .NET application debugging")]
    public static string GetSosCommands()
    {
        return SosCommandsContent;
    }

    /// <summary>
    /// Gets the troubleshooting guide.
    /// </summary>
    [McpServerResource, Description("Solutions to common issues when using the debugger MCP server")]
    public static string GetTroubleshooting()
    {
        return TroubleshootingContent;
    }

    /// <summary>
    /// Gets the CLI client usage guide.
    /// </summary>
    [McpServerResource, Description("Complete guide to using the dbg-mcp CLI client for remote debugging")]
    public static string GetCliGuide()
    {
        return CliGuideContent;
    }

    /// <summary>
    /// Loads a resource file from the Resources directory.
    /// </summary>
    /// <param name="fileName">The file name to load.</param>
    /// <returns>The file contents or a fallback message if not found.</returns>
    /// <remarks>
    /// This method is internal for testing purposes.
    /// It tries to load from file first, then from embedded resources.
    /// Returns a fallback message if neither succeeds.
    /// </remarks>
    internal static string LoadResourceFile(string fileName)
    {
        try
        {
            // Try loading from file in Resources directory
            var resourcePath = Path.Combine(AppContext.BaseDirectory, "Resources", fileName);
            if (File.Exists(resourcePath))
            {
                return File.ReadAllText(resourcePath);
            }

            // Try loading from embedded resource
            var assembly = typeof(DebuggerResources).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

            if (resourceName != null)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
            }

            return $"# Resource Not Found\n\nThe resource file '{fileName}' could not be loaded.";
        }
        catch (Exception ex)
        {
            return $"# Error Loading Resource\n\nFailed to load '{fileName}': {ex.Message}";
        }
    }
}
