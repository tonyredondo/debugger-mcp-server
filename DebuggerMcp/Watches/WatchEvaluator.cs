using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DebuggerMcp.Watches;

/// <summary>
/// Evaluates watch expressions using the debugger.
/// Supports WinDbg and LLDB debuggers with appropriate command mapping.
/// </summary>
public class WatchEvaluator
{
    private readonly IDebuggerManager _manager;
    private readonly WatchStore _watchStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchEvaluator"/> class.
    /// </summary>
    /// <param name="manager">The debugger manager to use for evaluation.</param>
    /// <param name="watchStore">The watch store for persistence.</param>
    /// <exception cref="ArgumentNullException">Thrown when manager or watchStore is null.</exception>
    public WatchEvaluator(IDebuggerManager manager, WatchStore watchStore)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _watchStore = watchStore ?? throw new ArgumentNullException(nameof(watchStore));
    }

    /// <summary>
    /// Auto-detects the watch type based on the expression pattern.
    /// </summary>
    /// <param name="expression">The expression to analyze.</param>
    /// <returns>The detected watch type based on pattern matching.</returns>
    /// <remarks>
    /// Detection order matters - more specific patterns are checked first:
    /// <list type="number">
    /// <item><description>Memory addresses (0x prefix or 8-16 hex digits)</description></item>
    /// <item><description>Module!Symbol patterns (WinDbg symbol format)</description></item>
    /// <item><description>.NET object dumps (!do or !dumpobj)</description></item>
    /// <item><description>Simple identifiers (variable names)</description></item>
    /// <item><description>Global/static variables (g_ or s_ prefix)</description></item>
    /// <item><description>Default: treated as debugger expression</description></item>
    /// </list>
    /// </remarks>
    public static WatchType DetectWatchType(string expression)
    {
        // Guard: Handle null/empty input by defaulting to Expression type
        // Expression type is the most flexible and will attempt evaluation as-is
        if (string.IsNullOrWhiteSpace(expression))
        {
            return WatchType.Expression;
        }

        expression = expression.Trim();

        // Pattern 1: Memory address patterns
        // Matches: 0x12345678, 0x00007FF812345678, or raw hex like 12345678
        // These are displayed using memory read commands (db, dq, etc.)
        if (Regex.IsMatch(expression, @"^0x[0-9a-fA-F]+$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(expression, @"^[0-9a-fA-F]{8,16}$", RegexOptions.IgnoreCase))
        {
            return WatchType.MemoryAddress;
        }

        // Pattern 2: Module!Symbol format (e.g., ntdll!NtWaitForSingleObject)
        // This is the standard WinDbg format for symbols
        // Note: We exclude expressions starting with ! as those are debugger commands
        if (expression.Contains('!') && !expression.StartsWith("!"))
        {
            return WatchType.Variable;
        }

        // Pattern 3: .NET object dump commands
        // Users may explicitly request .NET object display using SOS commands
        // These will be executed directly to dump the managed object
        if (expression.StartsWith("!do ", StringComparison.OrdinalIgnoreCase) ||
            expression.StartsWith("!dumpobj ", StringComparison.OrdinalIgnoreCase))
        {
            return WatchType.Object;
        }

        // Pattern 4: Simple C-style identifier (variable name)
        // Matches: myVar, _count, temp123
        // These are displayed using symbol evaluation commands
        if (Regex.IsMatch(expression, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
        {
            return WatchType.Variable;
        }

        // Pattern 5: Global/static variable naming convention
        // Matches: g_AppState, s_Instance (common C/C++ conventions)
        // Treated as variables for symbol lookup
        if (Regex.IsMatch(expression, @"^[gs]_[a-zA-Z0-9_]+$", RegexOptions.IgnoreCase))
        {
            return WatchType.Variable;
        }

        // Default: Treat as a debugger expression
        // This covers complex expressions like poi(esp+8), @$t0, @@(myVar.Field)
        // The expression will be passed directly to the debugger's expression evaluator
        return WatchType.Expression;
    }

    /// <summary>
    /// Evaluates a single watch expression.
    /// </summary>
    /// <param name="watch">The watch to evaluate.</param>
    /// <returns>
    /// A <see cref="WatchEvaluationResult"/> containing the evaluation result or error information.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="watch"/> is null.</exception>
    /// <remarks>
    /// The evaluation process:
    /// <list type="number">
    /// <item><description>Build the appropriate debugger command based on watch type</description></item>
    /// <item><description>Execute the command via the debugger manager</description></item>
    /// <item><description>Parse the output for errors or values</description></item>
    /// <item><description>Update the watch's last evaluation metadata</description></item>
    /// </list>
    /// </remarks>
    public async Task<WatchEvaluationResult> EvaluateAsync(WatchExpression watch)
    {
        // Guard: Validate input parameter
        if (watch == null)
        {
            throw new ArgumentNullException(nameof(watch));
        }

        // Initialize the result with the watch's metadata
        var result = new WatchEvaluationResult
        {
            WatchId = watch.Id,
            Expression = watch.Expression,
            Type = watch.Type,
            Description = watch.Description,
            EvaluatedAt = DateTime.UtcNow
        };

        // Guard: Ensure a dump file is open before attempting evaluation
        // Without an open dump, there's no debugging context for the expression
        if (!_manager.IsDumpOpen)
        {
            result.Success = false;
            result.Error = "No dump file is open";
            return result;
        }

        try
        {
            // Build the debugger command appropriate for this watch type
            // Different types use different commands (db, dv, !do, etc.)
            var command = BuildEvaluationCommand(watch);

            // Execute the command asynchronously to avoid blocking
            // Task.Run ensures we don't block the calling async context
            var output = await Task.Run(() => _manager.ExecuteCommand(command));

            // Check the output for common error patterns from both WinDbg and LLDB
            // Error patterns include "error:", "Unable to", "No symbol", etc.
            if (IsErrorOutput(output))
            {
                result.Success = false;
                result.Error = ExtractErrorMessage(output);
            }
            else
            {
                // Evaluation succeeded - clean and store the output
                result.Success = true;
                result.Value = CleanOutput(output);
            }

            // Update the watch's metadata for tracking purposes
            // This allows users to see when a watch was last evaluated
            watch.LastEvaluatedAt = result.EvaluatedAt;
            watch.LastValue = result.Success ? result.Value : null;
        }
        catch (Exception ex)
        {
            // Catch all exceptions to ensure we return a result rather than throwing
            // This provides a better UX as all watches get results, even on error
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Evaluates all watches for a dump.
    /// </summary>
    /// <param name="userId">The user ID who owns the dump.</param>
    /// <param name="dumpId">The dump ID.</param>
    /// <returns>A report containing all evaluation results.</returns>
    public async Task<WatchEvaluationReport> EvaluateAllAsync(string userId, string dumpId)
    {
        var watches = await _watchStore.GetWatchesAsync(userId, dumpId);

        var report = new WatchEvaluationReport
        {
            DumpId = dumpId,
            TotalWatches = watches.Count,
            EvaluatedAt = DateTime.UtcNow
        };

        foreach (var watch in watches)
        {
            var result = await EvaluateAsync(watch);
            report.Watches.Add(result);

            if (result.Success)
            {
                report.SuccessfulEvaluations++;
            }
            else
            {
                report.FailedEvaluations++;
            }

            // Update the watch in the store with last value
            await _watchStore.UpdateWatchAsync(userId, dumpId, watch);
        }

        // Generate insights
        GenerateInsights(report);

        return report;
    }

    /// <summary>
    /// Builds the appropriate debugger command for evaluating a watch.
    /// </summary>
    /// <param name="watch">The watch expression.</param>
    /// <returns>The debugger command to execute.</returns>
    private string BuildEvaluationCommand(WatchExpression watch)
    {
        var isWinDbg = _manager.DebuggerType == "WinDbg";
        var expression = watch.Expression.Trim();

        return watch.Type switch
        {
            WatchType.MemoryAddress => BuildMemoryCommand(expression, isWinDbg),
            WatchType.Variable => BuildVariableCommand(expression, isWinDbg),
            WatchType.Object => BuildObjectCommand(expression, isWinDbg),
            WatchType.Expression => BuildExpressionCommand(expression, isWinDbg),
            _ => BuildExpressionCommand(expression, isWinDbg)
        };
    }

    /// <summary>
    /// Builds a command to display memory at an address.
    /// </summary>
    private static string BuildMemoryCommand(string expression, bool isWinDbg)
    {
        // Normalize address format
        var address = expression;
        if (!address.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            address = "0x" + address;
        }

        if (isWinDbg)
        {
            // Use dq for 64-bit display, show 4 lines
            return $"dq {address} L4";
        }
        else
        {
            // LLDB memory read
            return $"memory read {address} --count 32";
        }
    }

    /// <summary>
    /// Builds a command to display a variable.
    /// </summary>
    private static string BuildVariableCommand(string expression, bool isWinDbg)
    {
        if (isWinDbg)
        {
            // Try to display as typed data first
            if (expression.Contains('!'))
            {
                return $"dt {expression}";
            }
            // For simple names, use ? to evaluate
            return $"? {expression}";
        }
        else
        {
            // LLDB - try frame variable first, fall back to expression
            if (expression.Contains('`'))
            {
                return $"image lookup -s {expression.Replace('`', ' ')}";
            }
            return $"p {expression}";
        }
    }

    /// <summary>
    /// Builds a command to dump a .NET object.
    /// </summary>
    private static string BuildObjectCommand(string expression, bool isWinDbg)
    {
        // If expression already contains !do, use it as-is
        if (expression.StartsWith("!do ", StringComparison.OrdinalIgnoreCase) ||
            expression.StartsWith("!dumpobj ", StringComparison.OrdinalIgnoreCase))
        {
            return expression;
        }

        // Normalize address
        var address = expression;
        if (!address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(address, @"^[0-9a-fA-F]+$"))
        {
            address = "0x" + address;
        }

        // Use !do for both WinDbg and LLDB (SOS command)
        return $"!do {address}";
    }

    /// <summary>
    /// Builds a command to evaluate a general expression.
    /// </summary>
    private static string BuildExpressionCommand(string expression, bool isWinDbg)
    {
        if (isWinDbg)
        {
            // Check if it's already a command
            if (expression.StartsWith("!") || expression.StartsWith("."))
            {
                return expression;
            }
            return $"? {expression}";
        }
        else
        {
            // Check if it's already a command
            if (expression.StartsWith("!") || expression.StartsWith("sos "))
            {
                return expression;
            }
            return $"expression {expression}";
        }
    }

    /// <summary>
    /// Checks if the output indicates an error.
    /// </summary>
    private static bool IsErrorOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return true;
        }

        var errorPatterns = new[]
        {
            "Couldn't resolve",
            "Unable to read",
            "Memory access error",
            "Invalid address",
            "error:",
            "Error:",
            "couldn't find",
            "Symbol not found",
            "No symbol matches",
            "Unable to resolve",
            "Bad memory read",
            "?? ",
            "No type information",
            "Unable to find target"
        };

        return errorPatterns.Any(pattern =>
            output.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extracts a clean error message from debugger output.
    /// </summary>
    private static string ExtractErrorMessage(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "No output from debugger";
        }

        // Try to find the most relevant error line
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("invalid", StringComparison.OrdinalIgnoreCase))
            {
                return line.Trim();
            }
        }

        // Return first non-empty line
        return lines.FirstOrDefault()?.Trim() ?? "Evaluation failed";
    }

    /// <summary>
    /// Cleans up debugger output for display.
    /// </summary>
    private static string CleanOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        // Remove excessive whitespace but preserve structure
        var lines = output.Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        // Limit output length for readability
        const int maxLines = 50;
        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            lines.Add($"... ({lines.Count - maxLines} more lines)");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Generates insights from watch evaluation results.
    /// </summary>
    private static void GenerateInsights(WatchEvaluationReport report)
    {
        foreach (var watch in report.Watches)
        {
            if (!watch.Success)
            {
                report.Insights.Add($"⚠️ Watch '{watch.Expression}' failed to evaluate: {watch.Error}");
                continue;
            }

            // Check for null/zero values
            if (watch.Value != null)
            {
                var valueLower = watch.Value.ToLowerInvariant();

                if (valueLower.Contains("null") || valueLower.Contains("00000000`00000000"))
                {
                    report.Insights.Add($"⚠️ Watch '{watch.Expression}' is NULL - may be relevant to crash");
                }
                else if (watch.Type == WatchType.Object && valueLower.Contains("exception"))
                {
                    report.Insights.Add($"🔴 Watch '{watch.Expression}' contains an exception object");
                }
                else if (Regex.IsMatch(watch.Value, @"0x[cC][dD]{6,}"))
                {
                    report.Insights.Add($"⚠️ Watch '{watch.Expression}' contains uninitialized memory pattern (0xcdcdcdcd)");
                }
                else if (Regex.IsMatch(watch.Value, @"0x[dD]{8,}"))
                {
                    report.Insights.Add($"⚠️ Watch '{watch.Expression}' contains freed memory pattern (0xdddddddd)");
                }
                else if (Regex.IsMatch(watch.Value, @"0x[fF][eE][eE][eE]"))
                {
                    report.Insights.Add($"⚠️ Watch '{watch.Expression}' contains freed heap memory pattern (0xfeeefeee)");
                }
            }
        }

        // Summary insights
        if (report.FailedEvaluations > 0)
        {
            var failRate = (double)report.FailedEvaluations / report.TotalWatches * 100;
            if (failRate > 50)
            {
                report.Insights.Add($"⚠️ High failure rate: {failRate:F0}% of watches failed. Symbols may be missing.");
            }
        }
    }
}

