using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Shell;

namespace DebuggerMcp.Cli.Help;

/// <summary>
/// Comprehensive help system for the CLI.
/// </summary>
/// <remarks>
/// Provides:
/// <list type="bullet">
/// <item><description>Category-based help listing</description></item>
/// <item><description>Detailed command help with examples</description></item>
/// <item><description>Context-aware suggestions</description></item>
/// </list>
/// </remarks>
public static class HelpSystem
{
    /// <summary>
    /// Help categories with their descriptions.
    /// </summary>
    public static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["connection"] = "Server connection and status commands",
        ["files"] = "File upload and management commands",
        ["session"] = "Debugging session management",
        ["debugging"] = "Debugger commands and operations",
        ["analysis"] = "Crash and performance analysis",
        ["advanced"] = "Watch expressions, reports, and source link",
        ["general"] = "Help, history, and configuration"
    };

    /// <summary>
    /// Commands organized by category.
    /// </summary>
    public static readonly Dictionary<string, CommandInfo[]> CommandsByCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        ["connection"] = [
            new("connect", "[[url]]", "Connect to a Debugger MCP Server", ["connect", "connect http://localhost:5000", "connect localhost:5001 -k key"]),
            new("disconnect", "", "Disconnect from the current server", ["disconnect"]),
            new("status", "", "Show connection and session status", ["status"]),
            new("health", "[[url]]", "Check server health", ["health", "health http://localhost:5000"]),
            new("server", "<subcommand>", "Manage servers (list, add, remove, switch, init)", ["server list", "server add http://localhost:5001", "server switch alpine-x64", "server init"])
        ],
        ["files"] = [
            new("dumps", "<subcommand>", "Manage dump files (upload, list, info, delete, binary)", [
                "dumps upload ./crash.dmp           Upload a dump file",
                "dumps list                         List available dumps",
                "dumps info abc123                  Show dump details",
                "dumps delete abc123                Delete a dump",
                "dumps binary abc123 ./MyApp        Upload executable for standalone apps"
            ]),
            new("symbols", "<subcommand>", "Manage symbol files and Datadog symbols", [
                "symbols upload ./app.pdb           Upload symbol file",
                "symbols list                       List uploaded symbols",
                "symbols reload                     Reload symbols into debugger",
                "symbols clear                      Clear symbol cache",
                "symbols datadog download           Auto-detect and download Datadog symbols",
                "symbols datadog download -f        Download with version fallback",
                "symbols datadog clear              Clear downloaded Datadog symbols",
                "symbols datadog config             Show Datadog config"
            ]),
            new("stats", "", "Show server statistics", ["stats"])
        ],
        ["session"] = [
            new("session", "<subcommand>", "Manage debugging sessions (create, list, use, close, info, restore)", ["session create", "session list", "session use d03", "session close abc123", "session info abc123", "session restore abc123"])
        ],
        ["debugging"] = [
            new("open", "<dumpId>", "Open a dump file in the debugger", ["open abc123"]),
            new("close", "", "Close the current dump", ["close"]),
            new("exec", "<command>", "Execute a debugger command", ["exec !analyze -v", "exec k", "x !dumpheap -stat"]),
            new("cmd", "", "Enter multi-line command mode", ["cmd"]),
            new("sos", "", "Load SOS extension for .NET (usually auto-loaded)", ["sos"]),
            new("showobj", "<address>", "Inspect .NET object as JSON", ["showobj f7158ec79b48", "so f7158ec79b48 --depth 3"])
        ],
        ["analysis"] = [
            new("analyze", "<type>", "Run automated analysis (crash, dotnet, perf, cpu, memory, gc, threads, security)", ["analyze crash", "analyze dotnet", "analyze perf", "analyze security"]),
            new("compare", "<type>", "Compare two dumps (all, heap, threads, modules)", ["compare <dumpId>", "compare heap <dumpId>", "compare threads <dumpId>"])
        ],
        ["advanced"] = [
            new("watch", "<subcommand>", "Manage watch expressions (add, list, eval, remove, clear)", ["watch add @rsp", "watch list", "watch eval", "watch remove w1", "watch clear"]),
            new("report", "-o <file> [[options]]", "Generate analysis reports (requires -o output)", ["report -o ./report.md", "report -o ./report.html -f html", "report -o ./summary.json --summary -f json"]),
            new("sourcelink", "<subcommand>", "Source Link integration (resolve, info)", ["sourcelink /src/Program.cs", "sourcelink resolve /src/Program.cs", "sl info"])
        ],
        ["general"] = [
            new("help", "[[category|command]]", "Show help information", ["help", "help connection", "help analyze"]),
            new("history", "[[n|clear|search]]", "Manage command history", ["history", "history 50", "history search connect", "history clear"]),
            new("copy", "", "Copy last command result to clipboard", ["copy", "cp"]),
            new("clear", "", "Clear the screen", ["clear"]),
            new("set", "<key> <value>", "Set configuration option", ["set verbose true", "set output json"]),
            new("tools", "", "List available MCP tools", ["tools"]),
            new("version", "", "Show version information", ["version"]),
            new("exit", "", "Exit the CLI", ["exit", "quit"])
        ]
    };

    /// <summary>
    /// Shows the main help overview with all commands grouped by category.
    /// </summary>
    public static void ShowOverview(ConsoleOutput output)
    {
        output.Header("Debugger MCP CLI - Command Reference");
        output.WriteLine();
        output.Markup("[dim]A powerful command-line interface for remote crash dump analysis[/]");
        output.WriteLine();

        // Show all commands grouped by category
        foreach (var (categoryKey, categoryDesc) in Categories)
        {
            if (!CommandsByCategory.TryGetValue(categoryKey, out var commands))
            {
                continue;
            }

            output.Markup($"[bold cyan]{categoryKey.ToUpperInvariant()}[/] [dim]- {categoryDesc}[/]");
            
            foreach (var cmd in commands)
            {
                var syntax = string.IsNullOrEmpty(cmd.Syntax) 
                    ? cmd.Name 
                    : $"{cmd.Name} {cmd.Syntax}";
                
                // Calculate padding: escaped [[ and ]] count as 2 chars in string but render as 1
                // So we need extra padding for each escaped bracket pair
                var escapedBrackets = (syntax.Length - syntax.Replace("[[", "[").Replace("]]", "]").Length);
                var padding = 32 + escapedBrackets;
                
                output.Markup($"  [green]{syntax.PadRight(padding)}[/] {cmd.Description}");
            }
            output.WriteLine();
        }

        // Show keyboard shortcuts
        output.Markup("[bold cyan]KEYBOARD SHORTCUTS[/]");
        output.Markup("  [cyan]↑/↓[/]         Navigate command history");
        output.Markup("  [cyan]Tab[/]         Auto-complete commands");
        output.Markup("  [cyan]Ctrl+C[/]      Cancel current operation");
        output.Markup("  [cyan]Ctrl+L[/]      Clear screen");
        output.WriteLine();

        output.Dim("Type 'help <command>' for detailed help on a specific command");
    }

    /// <summary>
    /// Shows help for a specific category.
    /// </summary>
    public static void ShowCategoryHelp(ConsoleOutput output, string category)
    {
        if (!CommandsByCategory.TryGetValue(category, out var commands))
        {
            output.Error($"Unknown category: {category}");
            output.Dim($"Available categories: {string.Join(", ", Categories.Keys)}");
            return;
        }

        var description = Categories.GetValueOrDefault(category, "");
        output.Header($"{char.ToUpper(category[0])}{category[1..]} Commands");
        output.Markup($"[dim]{description}[/]");
        output.WriteLine();

        foreach (var cmd in commands)
        {
            var syntax = string.IsNullOrEmpty(cmd.Syntax) ? cmd.Name : $"{cmd.Name} {cmd.Syntax}";
            
            // Calculate padding for escaped brackets
            var escapedBrackets = (syntax.Length - syntax.Replace("[[", "[").Replace("]]", "]").Length);
            var padding = 30 + escapedBrackets;
            
            output.Markup($"  [green]{syntax.PadRight(padding)}[/] {cmd.Description}");
        }

        output.WriteLine();
        output.Dim("Type 'help <command>' for detailed help on a specific command");
    }

    /// <summary>
    /// Shows detailed help for a specific command.
    /// </summary>
    public static bool ShowCommandHelp(ConsoleOutput output, string command)
    {
        // Find command in categories
        foreach (var (category, commands) in CommandsByCategory)
        {
            var cmd = commands.FirstOrDefault(c => 
                c.Name.Equals(command, StringComparison.OrdinalIgnoreCase));
            
            if (cmd != null)
            {
                ShowCommandDetails(output, cmd, category);
                return true;
            }
        }

        return false; // Command not found - let Program.cs handle detailed help
    }

    private static void ShowCommandDetails(ConsoleOutput output, CommandInfo cmd, string category)
    {
        output.Header($"{cmd.Name.ToUpper()} Command");
        output.WriteLine();
        output.Markup(cmd.Description);
        output.WriteLine();

        output.Markup("[bold]USAGE[/]");
        var syntax = string.IsNullOrEmpty(cmd.Syntax) ? cmd.Name : $"{cmd.Name} {cmd.Syntax}";
        output.Markup($"  {syntax}");
        output.WriteLine();

        output.Markup("[bold]CATEGORY[/]");
        output.Markup($"  {category}");
        output.WriteLine();

        if (cmd.Examples.Length > 0)
        {
            output.Markup("[bold]EXAMPLES[/]");
            foreach (var example in cmd.Examples)
            {
                output.Markup($"  [yellow]{example}[/]");
            }
        }
    }

    /// <summary>
    /// Gets contextual help hints based on shell state.
    /// </summary>
    public static void ShowContextualHelp(ConsoleOutput output, ShellState state)
    {
        output.Markup("[bold cyan]SUGGESTED COMMANDS[/]");
        output.WriteLine();

        switch (state.Level)
        {
            case ShellStateLevel.Initial:
                output.Markup("  [yellow]connect <url>[/]     Connect to a Debugger MCP Server");
                output.Markup("  [yellow]help[/]              Show available commands");
                output.Markup("  [yellow]exit[/]              Exit the CLI");
                break;

            case ShellStateLevel.Connected:
                output.Markup("  [yellow]dumps upload <file>[/]  Upload a dump file");
                output.Markup("  [yellow]dumps list[/]           List available dumps");
                output.Markup("  [yellow]session create[/]       Start a debugging session");
                output.Markup("  [yellow]open <dumpId>[/]        Open a dump (auto-creates session)");
                break;

            case ShellStateLevel.Session:
                output.Markup("  [yellow]open <dumpId>[/]     Open a dump file");
                output.Markup("  [yellow]dumps list[/]        List available dumps");
                output.Markup("  [yellow]session close[/]     Close the current session");
                break;

            case ShellStateLevel.DumpLoaded:
                output.Markup("  [yellow]exec <cmd>[/]              Execute debugger command");
                output.Markup("  [yellow]analyze crash[/]           Run crash analysis");
                output.Markup("  [yellow]analyze dotnet[/]          Run .NET analysis");
                output.Markup("  [yellow]symbols datadog download[/] Download Datadog symbols (if present)");
                output.Markup("  [yellow]exec !threads[/]           List all threads (.NET)");
                output.Markup("  [yellow]exec k[/]                  Show call stack");
                output.Markup("  [yellow]watch add <expr>[/]        Add watch expression");
                output.Markup("  [yellow]report -o <file>[/]        Generate analysis report (writes to file)");
                output.Markup("  [yellow]close[/]                   Close current dump");
                break;
        }
    }

    /// <summary>
    /// Lists all available commands.
    /// </summary>
    public static void ListAllCommands(ConsoleOutput output)
    {
        output.Header("All Commands");
        output.WriteLine();

        foreach (var (category, commands) in CommandsByCategory)
        {
            var catDesc = Categories.GetValueOrDefault(category, "");
            output.Markup($"[bold cyan]{category.ToUpper()}[/] - [dim]{catDesc}[/]");
            
            foreach (var cmd in commands)
            {
                output.Markup($"  [green]{cmd.Name,-16}[/] {cmd.Description}");
            }
            output.WriteLine();
        }
    }
}

/// <summary>
/// Information about a command.
/// </summary>
public record CommandInfo(string Name, string Syntax, string Description, string[] Examples);
