using Microsoft.Extensions.Logging;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Result of loading symbols into the debugger.
/// </summary>
public class SymbolLoadResult
{
    /// <summary>
    /// Gets or sets whether symbol loading was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets the list of LLDB commands executed.
    /// </summary>
    public List<string> CommandsExecuted { get; } = new();

    /// <summary>
    /// Gets the list of native .debug files loaded.
    /// </summary>
    public List<string> NativeSymbolsLoaded { get; } = new();

    /// <summary>
    /// Gets the list of managed PDB directories configured.
    /// </summary>
    public List<string> ManagedSymbolPaths { get; } = new();

    /// <summary>
    /// Gets or sets any error message.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Loads Datadog symbols into LLDB for improved stack traces.
/// </summary>
public class DatadogSymbolLoader
{
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatadogSymbolLoader"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public DatadogSymbolLoader(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Generates LLDB commands to load symbols from a merged symbol directory.
    /// </summary>
    /// <param name="mergeResult">The artifact merge result with symbol paths.</param>
    /// <returns>List of LLDB commands to execute.</returns>
    public List<string> GenerateLldbCommands(ArtifactMergeResult mergeResult)
    {
        var commands = new List<string>();

        if (mergeResult?.SymbolDirectory == null)
            // Nothing to configure if merge failed
            return commands;

        // 1. Add search paths for native symbols
        if (mergeResult.NativeSymbolDirectory != null && Directory.Exists(mergeResult.NativeSymbolDirectory))
        {
            commands.Add($"settings append target.debug-file-search-paths \"{mergeResult.NativeSymbolDirectory}\"");
        }

        // 2. Explicitly load each .debug file for native symbols
        foreach (var debugFile in mergeResult.DebugSymbolFiles)
        {
            if (File.Exists(debugFile))
            {
                commands.Add($"target symbols add \"{debugFile}\"");
            }
        }

        // 3. Configure SOS to use the managed symbol directory
        if (mergeResult.ManagedSymbolDirectory != null && Directory.Exists(mergeResult.ManagedSymbolDirectory))
        {
            commands.Add($"setsymbolserver -directory \"{mergeResult.ManagedSymbolDirectory}\"");
        }

        return commands;
    }

    /// <summary>
    /// Loads symbols into the debugger by executing the generated commands.
    /// </summary>
    /// <param name="mergeResult">The artifact merge result with symbol paths.</param>
    /// <param name="executeCommand">Function to execute a debugger command.</param>
    /// <returns>Symbol load result.</returns>
    public async Task<SymbolLoadResult> LoadSymbolsAsync(
        ArtifactMergeResult mergeResult,
        Func<string, string> executeCommand)
    {
        var result = new SymbolLoadResult();

        try
        {
            var commands = GenerateLldbCommands(mergeResult);

            if (commands.Count == 0)
            {
                // We cannot proceed if we didn't produce any valid debugger commands
                result.ErrorMessage = "No symbol loading commands generated";
                return result;
            }

            foreach (var command in commands)
            {
                _logger?.LogDebug("Executing: {Command}", command);

                try
                {
                    var output = executeCommand(command);
                    result.CommandsExecuted.Add(command);

                    // Track what was loaded
                    if (command.Contains("target symbols add"))
                    {
                        result.NativeSymbolsLoaded.Add(
                            ExtractPath(command, "target symbols add"));
                    }
                    else if (command.Contains("setsymbolserver -directory"))
                    {
                        result.ManagedSymbolPaths.Add(
                            ExtractPath(command, "setsymbolserver -directory"));
                    }

                    _logger?.LogDebug("Command output: {Output}",
                        output.Length > 200 ? output[..200] + "..." : output);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to execute: {Command}", command);
                    // Continue with other commands so we load as many symbols as possible
                }
            }

            result.Success = result.CommandsExecuted.Count > 0;

            if (result.Success)
            {
                _logger?.LogInformation("Loaded {Native} native symbols and {Managed} managed symbol paths",
                    result.NativeSymbolsLoaded.Count, result.ManagedSymbolPaths.Count);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load symbols");
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Extracts a path from an LLDB command.
    /// </summary>
    private static string ExtractPath(string command, string prefix)
    {
        var startIndex = command.IndexOf('"');
        var endIndex = command.LastIndexOf('"');

        if (startIndex >= 0 && endIndex > startIndex)
        {
            return command[(startIndex + 1)..endIndex];
        }

        return command.Replace(prefix, "").Trim().Trim('"');
    }
}
