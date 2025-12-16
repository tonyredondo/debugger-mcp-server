using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Net.Http.Json;
using Spectre.Console;
using DebuggerMcp.Cli.Client;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Display;
using DebuggerMcp.Cli.Help;
using DebuggerMcp.Cli.Llm;
using DebuggerMcp.Cli.Models;
using DebuggerMcp.Cli.Shell;
using DebuggerMcp.Cli.Shell.Transcript;

namespace DebuggerMcp.Cli;

/// <summary>
/// Entry point for the Debugger MCP CLI.
/// </summary>
public class Program
{
    /// <summary>
    /// CLI version string.
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// Exit handler used by command implementations.
    /// </summary>
    /// <remarks>
    /// This indirection enables unit tests to validate error-path behavior without
    /// terminating the test process.
    /// </remarks>
    internal static Action<int> Exit { get; set; } = Environment.Exit;

    /// <summary>
    /// Main entry point.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        // Create root command
        var rootCommand = new RootCommand("Debugger MCP CLI - Remote crash dump analysis, debugging, and diagnostics")
        {
            Name = "dbg-mcp"
        };

        // Global options
        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "Enable verbose output for debugging");

        var outputOption = new Option<string>(
            aliases: ["--output", "-o"],
            getDefaultValue: () => "text",
            description: "Output format: text or json");

        var serverOption = new Option<string?>(
            aliases: ["--server", "-s"],
            description: "Server URL to connect to");

        var apiKeyOption = new Option<string?>(
            aliases: ["--api-key", "-k"],
            description: "API key for authentication");

        rootCommand.AddGlobalOption(verboseOption);
        rootCommand.AddGlobalOption(outputOption);
        rootCommand.AddGlobalOption(serverOption);
        rootCommand.AddGlobalOption(apiKeyOption);

        // Add commands
        AddVersionCommand(rootCommand);
        AddHealthCommand(rootCommand, serverOption, apiKeyOption, verboseOption);
        AddInteractiveCommand(rootCommand, verboseOption, outputOption, serverOption, apiKeyOption);
        AddServerCommands(rootCommand, verboseOption);

        // Build parser with middleware
        var parser = new CommandLineBuilder(rootCommand)
            .UseDefaults()
            .UseExceptionHandler((ex, context) =>
            {
                var console = AnsiConsole.Create(new AnsiConsoleSettings());
                var output = new ConsoleOutput(console);

                if (context.ParseResult.FindResultFor(verboseOption)?.GetValueOrDefault<bool>() == true)
                {
                    output.Error("An error occurred", ex);
                }
                else
                {
                    output.Error(ex.Message);
                }

                context.ExitCode = 1;
            })
            .Build();

        // If no arguments, run interactive shell
        if (args.Length == 0)
        {
            return await RunInteractiveShellAsync(
                ConnectionSettings.Load(),
                verbose: false,
                OutputFormat.Text);
        }

        return await parser.InvokeAsync(args);
    }

    /// <summary>
    /// Adds the version command.
    /// </summary>
    private static void AddVersionCommand(RootCommand rootCommand)
    {
        var versionCommand = new Command("version", "Display CLI version information");

        versionCommand.SetHandler(() =>
        {
            var console = AnsiConsole.Create(new AnsiConsoleSettings());
            console.MarkupLine($"[bold cyan]Debugger MCP CLI[/] version [green]{Version}[/]");
            console.MarkupLine($"[dim]Target Framework: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}[/]");
            console.MarkupLine($"[dim]OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}[/]");
        });

        rootCommand.AddCommand(versionCommand);
    }

    /// <summary>
    /// Adds the health command for checking server status.
    /// </summary>
    private static void AddHealthCommand(
        RootCommand rootCommand,
        Option<string?> serverOption,
        Option<string?> apiKeyOption,
        Option<bool> verboseOption)
    {
        var healthCommand = new Command("health", "Check if a Debugger MCP Server is healthy and reachable");

        var urlArgument = new Argument<string?>(
            name: "url",
            getDefaultValue: () => null,
            description: "Server URL to check (overrides --server option)");

        healthCommand.AddArgument(urlArgument);

        healthCommand.SetHandler(async (string? url, string? server, string? apiKey, bool verbose) =>
        {
            var console = AnsiConsole.Create(new AnsiConsoleSettings());
            var output = new ConsoleOutput(console) { Verbose = verbose };

            // Determine server URL
            var serverUrl = url ?? server ?? CliEnvironment.Get(CliEnvironment.ServerUrl);

            if (string.IsNullOrEmpty(serverUrl))
            {
                output.Error("No server URL specified. Use 'health <url>' or set DEBUGGER_MCP_URL environment variable.");
                Exit(1);
                return;
            }

            using var client = new HttpApiClient();

            try
            {
                client.Configure(serverUrl, apiKey);

                var health = await output.WithSpinnerAsync(
                    $"Checking health of [cyan]{serverUrl}[/]...",
                    () => client.CheckHealthAsync());

                if (health.IsHealthy)
                {
                    output.Success($"Server is healthy");
                    output.KeyValue("Status", health.Status);
                    if (!string.IsNullOrEmpty(health.Version))
                    {
                        output.KeyValue("Version", health.Version);
                    }
                    if (!string.IsNullOrEmpty(health.Uptime))
                    {
                        output.KeyValue("Uptime", health.Uptime);
                    }
                }
                else
                {
                    output.Error($"Server is not healthy: {health.Status}");
                    Exit(1);
                }
            }
            catch (Exception ex)
            {
                output.Error($"Failed to check health: {ex.Message}");
                if (verbose)
                {
                    console.WriteException(ex);
                }
                Exit(1);
            }
        }, urlArgument, serverOption, apiKeyOption, verboseOption);

        rootCommand.AddCommand(healthCommand);
    }

    /// <summary>
    /// Adds the interactive shell command.
    /// </summary>
    private static void AddInteractiveCommand(
        RootCommand rootCommand,
        Option<bool> verboseOption,
        Option<string> outputOption,
        Option<string?> serverOption,
        Option<string?> apiKeyOption)
    {
        var interactiveCommand = new Command("interactive", "Start the interactive shell (default when no command is given)")
        {
            IsHidden = true // Hidden because it's the default
        };

        interactiveCommand.SetHandler(async (bool verbose, string output, string? server, string? apiKey) =>
        {
            var settings = ConnectionSettings.Load();
            settings.Verbose = verbose;
            settings.OutputFormat = output.ToLowerInvariant() == "json" ? OutputFormat.Json : OutputFormat.Text;

            if (!string.IsNullOrEmpty(server))
            {
                settings.ServerUrl = server;
            }

            if (!string.IsNullOrEmpty(apiKey))
            {
                settings.ApiKey = apiKey;
            }

            var exitCode = await RunInteractiveShellAsync(settings, verbose, settings.OutputFormat);
            Exit(exitCode);
        }, verboseOption, outputOption, serverOption, apiKeyOption);

        rootCommand.AddCommand(interactiveCommand);
    }

    /// <summary>
    /// Adds the server management commands.
    /// </summary>
    private static void AddServerCommands(RootCommand rootCommand, Option<bool> verboseOption)
    {
        var serverCommand = new Command("server", "Manage server connections");

        // server list
        var listCommand = new Command("list", "List all configured servers and their capabilities");
        listCommand.SetHandler(async (bool verbose) =>
        {
            var console = AnsiConsole.Create(new AnsiConsoleSettings());
            var output = new ConsoleOutput(console) { Verbose = verbose };
            var configManager = new ServerConfigManager();
            var discovery = new ServerDiscovery(configManager);

            var servers = configManager.GetServers();
            if (servers.Count == 0)
            {
                output.Info("No servers configured. Use 'server add <url>' to add a server.");
                output.Info($"Config file location: {configManager.ConfigPath}");
                return;
            }

            await output.WithSpinnerAsync("Discovering servers...", async () =>
            {
                await discovery.DiscoverAllAsync();
                return true;
            });

            // Create table
            var table = new Table();
            table.Border(TableBorder.Rounded);
            table.AddColumn(new TableColumn("#").Centered());
            table.AddColumn(new TableColumn("URL").LeftAligned());
            table.AddColumn(new TableColumn("Arch").Centered());
            table.AddColumn(new TableColumn("Distro").Centered());
            table.AddColumn(new TableColumn("Status").Centered());
            table.AddColumn(new TableColumn("Name").LeftAligned());

            var index = 1;
            foreach (var server in discovery.Servers)
            {
                var statusColor = server.IsOnline ? "green" : "red";
                var status = server.IsOnline ? "online" : server.ErrorMessage ?? "offline";
                var arch = server.Capabilities?.Architecture ?? "-";
                var distro = server.Capabilities?.IsAlpine == true ? "alpine" : 
                    (server.Capabilities?.Distribution ?? "-");
                var name = server.Name;

                table.AddRow(
                    $"[dim]{index}[/]",
                    $"[cyan]{server.ShortUrl}[/]",
                    arch,
                    distro,
                    $"[{statusColor}]{status}[/]",
                    server.IsOnline ? $"[yellow]{name}[/]" : "[dim]-[/]"
                );
                index++;
            }

            console.WriteLine();
            console.Write(table);
            console.WriteLine();
            console.MarkupLine($"[dim]Servers: {servers.Count} configured, {discovery.OnlineCount} online[/]");
            console.MarkupLine($"[dim]Config: {configManager.ConfigPath}[/]");

            if (discovery.CurrentServer != null)
            {
                console.MarkupLine($"[dim]Current: {discovery.CurrentServer.Name} ({discovery.CurrentServer.ShortUrl})[/]");
            }
        }, verboseOption);
        serverCommand.AddCommand(listCommand);

        // server add
        var addCommand = new Command("add", "Add a new server to the configuration");
        var urlArgument = new Argument<string>("url", "Server URL (e.g., http://localhost:5000)");
        var apiKeyAddOption = new Option<string?>(
            aliases: ["--api-key", "-k"],
            description: "API key for authentication");
        addCommand.AddArgument(urlArgument);
        addCommand.AddOption(apiKeyAddOption);

        addCommand.SetHandler(async (string url, string? apiKey, bool verbose) =>
        {
            var console = AnsiConsole.Create(new AnsiConsoleSettings());
            var output = new ConsoleOutput(console) { Verbose = verbose };
            var configManager = new ServerConfigManager();
            var discovery = new ServerDiscovery(configManager);

            // Validate URL format
            if (!url.Contains("://"))
            {
                url = "http://" + url;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                output.Error($"Invalid URL format: {url}");
                Exit(1);
                return;
            }

            // Check if already exists
            var existing = configManager.GetServers();
            if (existing.Any(s => s.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                output.Warning($"Server already exists: {url}");
                return;
            }

            // Try to discover capabilities first
            var tempConfig = new ServerConfigManager();
            tempConfig.AddServer(url, apiKey);
            var tempDiscovery = new ServerDiscovery(tempConfig);

            DiscoveredServer? discovered = null;
            await output.WithSpinnerAsync($"Discovering server capabilities...", async () =>
            {
                var servers = await tempDiscovery.DiscoverAllAsync();
                discovered = servers.FirstOrDefault();
                return true;
            });

            if (discovered == null || !discovered.IsOnline)
            {
                output.Warning($"Server is not reachable: {discovered?.ErrorMessage ?? "unknown error"}");
                if (!console.Confirm("Add anyway?", false))
                {
                    return;
                }
            }

            // Add to config
            if (configManager.AddServer(url, apiKey))
            {
                if (discovered?.IsOnline == true)
                {
                    output.Markup($"[green]✓[/] Added server: [yellow]{discovered.Name}[/] ({url})");
                }
                else
                {
                    output.Success($"Added server: {url}");
                }
            }
            else
            {
                output.Error("Failed to add server");
            }
        }, urlArgument, apiKeyAddOption, verboseOption);
        serverCommand.AddCommand(addCommand);

        // server remove
        var removeCommand = new Command("remove", "Remove a server from the configuration");
        var removeArgument = new Argument<string>("url-or-name", "Server URL or auto-generated name (e.g., alpine-x64)");
        removeCommand.AddArgument(removeArgument);

        removeCommand.SetHandler(async (string urlOrName, bool verbose) =>
        {
            var console = AnsiConsole.Create(new AnsiConsoleSettings());
            var output = new ConsoleOutput(console) { Verbose = verbose };
            var configManager = new ServerConfigManager();
            var discovery = new ServerDiscovery(configManager);

            // First discover to resolve names
            await output.WithSpinnerAsync("Discovering servers...", async () =>
            {
                await discovery.DiscoverAllAsync();
                return true;
            });

            // Try to find by name first
            var server = discovery.FindServer(urlOrName);
            string urlToRemove;

            if (server != null)
            {
                urlToRemove = server.Url;
            }
            else
            {
                // Try as URL directly
                urlToRemove = urlOrName;
                if (!urlToRemove.Contains("://"))
                {
                    urlToRemove = "http://" + urlToRemove;
                }
            }

            if (configManager.RemoveServer(urlToRemove))
            {
                output.Success($"Removed server: {urlOrName}");
            }
            else
            {
                output.Error($"Server not found: {urlOrName}");
                Exit(1);
            }
        }, removeArgument, verboseOption);
        serverCommand.AddCommand(removeCommand);

        // server switch
        var switchCommand = new Command("switch", "Switch to a different server");
        var switchArgument = new Argument<string>("url-or-name", "Server URL or auto-generated name (e.g., alpine-x64)");
        switchCommand.AddArgument(switchArgument);

        switchCommand.SetHandler(async (string urlOrName, bool verbose) =>
        {
            var console = AnsiConsole.Create(new AnsiConsoleSettings());
            var output = new ConsoleOutput(console) { Verbose = verbose };
            var configManager = new ServerConfigManager();
            var discovery = new ServerDiscovery(configManager);

            // Discover servers
            await output.WithSpinnerAsync("Discovering servers...", async () =>
            {
                await discovery.DiscoverAllAsync();
                return true;
            });

            // Find the target server
            var server = discovery.FindServer(urlOrName);
            if (server == null)
            {
                output.Error($"Server not found: {urlOrName}");
                output.Info("Use 'server list' to see available servers.");
                Exit(1);
                return;
            }

            if (!server.IsOnline)
            {
                output.Error($"Server is offline: {server.ShortUrl} ({server.ErrorMessage})");
                Exit(1);
                return;
            }

            // Update connection settings
            var settings = ConnectionSettings.Load();
            settings.ServerUrl = server.Url;
            settings.ApiKey = server.ApiKey;
            settings.Save();

            discovery.CurrentServer = server;
            output.Markup($"[green]✓[/] Switched to: [yellow]{server.Name}[/] ({server.ShortUrl})");

            if (server.Capabilities != null)
            {
                output.KeyValue("Architecture", server.Capabilities.Architecture);
                output.KeyValue("Distribution", server.Capabilities.IsAlpine ? "Alpine" : (server.Capabilities.Distribution ?? "Linux"));
                output.KeyValue("Runtime", server.Capabilities.RuntimeVersion ?? "-");
            }
        }, switchArgument, verboseOption);
        serverCommand.AddCommand(switchCommand);

        // server init (create default config)
        var initCommand = new Command("init", "Initialize configuration with default localhost servers");
        initCommand.SetHandler((bool verbose) =>
        {
            var console = AnsiConsole.Create(new AnsiConsoleSettings());
            var output = new ConsoleOutput(console) { Verbose = verbose };
            var configManager = new ServerConfigManager();

            if (File.Exists(configManager.ConfigPath))
            {
                if (!console.Confirm($"Config already exists at {configManager.ConfigPath}. Overwrite?", false))
                {
                    return;
                }
            }

            configManager.CreateDefaultConfig();
            output.Success($"Created default configuration at: {configManager.ConfigPath}");
            output.Info("Default servers:");
            output.Info("  - http://localhost:5000 (debian-arm64)");
            output.Info("  - http://localhost:5001 (alpine-arm64)");
            output.Info("  - http://localhost:5002 (debian-x64)");
            output.Info("  - http://localhost:5003 (alpine-x64)");
        }, verboseOption);
        serverCommand.AddCommand(initCommand);

        rootCommand.AddCommand(serverCommand);
    }

    /// <summary>
    /// Runs the interactive shell.
    /// </summary>
    private static async Task<int> RunInteractiveShellAsync(
        ConnectionSettings settings,
        bool verbose,
        OutputFormat outputFormat)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings());
        var output = new ConsoleOutput(console)
        {
            Verbose = verbose,
            Format = outputFormat
        };

        // Show welcome banner
        ShowWelcomeBanner(console);

        // Create shell state
        var state = new ShellState
        {
            Settings = settings
        };
        state.Settings.Verbose = verbose;
        state.Settings.OutputFormat = outputFormat;

        // Initialize transcript store (commands + outputs) for LLM conversations.
        state.Transcript = new CliTranscriptStore(Path.Combine(ConnectionSettings.DefaultConfigDirectory, "cli_transcript.jsonl"));

        // Create command history with persistence
        var history = new CommandHistory(settings.HistoryFile, settings.HistorySize);
        output.Dim($"Command history: {history.Count} entries loaded");

        // Create auto-complete
        var autoComplete = new AutoComplete(state);

        // Create HTTP client
        using var httpClient = new HttpApiClient();

        // Create MCP client for debugging operations
        var mcpClient = new McpClient();
        mcpClient.ToolResponseTimeout = settings.Timeout;

        // Default server URL for auto-connect
        const string DefaultServerUrl = "http://localhost:5000";

        // Auto-connect: use configured URL or try default localhost
        var serverUrl = !string.IsNullOrEmpty(settings.ServerUrl) 
            ? settings.ServerUrl 
            : DefaultServerUrl;

        try
        {
            output.Dim($"Connecting to {serverUrl}...");
            httpClient.Configure(serverUrl, settings.ApiKey, settings.Timeout);
            
            // Use the normalized URL from the client (has http:// scheme added if missing)
            var normalizedUrl = httpClient.ServerUrl!;
            var health = await httpClient.CheckHealthAsync();

            if (health.IsHealthy)
            {
                state.SetConnected(normalizedUrl);
                settings.ServerUrl = normalizedUrl;
                settings.Save();
                output.Success($"Connected to {normalizedUrl}");

                // Get and display server host information
                var serverInfo = await httpClient.GetServerInfoAsync();
                if (serverInfo != null)
                {
                    state.ServerInfo = serverInfo;
                    output.WriteLine();
                    output.Header("Server Host Information");
                    output.KeyValue("Host", serverInfo.Description);
                    if (serverInfo.IsDocker)
                    {
                        output.KeyValue("Container", "Docker");
                    }
                    output.KeyValue("Debugger", serverInfo.DebuggerType);
                    output.KeyValue("Runtime", serverInfo.DotNetVersion);
                    
                    if (serverInfo.IsAlpine)
                    {
                        output.Warning("⚠️  Alpine Linux host - can only debug Alpine .NET dumps");
                    }
                    
                    if (serverInfo.InstalledRuntimes.Count > 0)
                    {
                        var majorVersions = serverInfo.InstalledRuntimes
                            .Select(r => r.Split('.')[0])
                            .Distinct()
                            .OrderByDescending(v => v)
                            .ToList();
                        output.KeyValue("Supported .NET", string.Join(", ", majorVersions.Select(v => $".NET {v}")));
                    }
                    output.WriteLine();
                }

                // Also connect the MCP client for debugging operations
                try
                {
                    await mcpClient.ConnectAsync(normalizedUrl, settings.ApiKey);
                    output.Success($"MCP client connected ({mcpClient.AvailableTools.Count} tools available)");
                    RegisterMcpSamplingHandlers(output, state, mcpClient);

                    var autoRestore = await DebuggerMcp.Cli.Shell.LastSessionAutoRestore.TryRestoreAsync(output, state, mcpClient);
                    if (autoRestore.ClearedSavedSession)
                    {
                        settings.Save();
                    }
                }
                catch (Exception mcpEx)
                {
                    output.Warning($"MCP client failed to connect: {mcpEx.Message}");
                    output.Dim("Debugging commands will be unavailable. File operations still work.");
                }
            }
            else
            {
                output.Warning($"Server at {normalizedUrl} is not healthy: {health.Status}");
            }
        }
        catch
        {
            // Silent failure for default URL, show message for configured URL
            if (!string.IsNullOrEmpty(settings.ServerUrl))
            {
                output.Warning($"Could not connect to {serverUrl}");
            }
            else
            {
                output.Dim($"No server at {DefaultServerUrl}. Use 'connect <url>' to connect.");
            }
        }

        // Set up auto-complete providers
        autoComplete.SetDumpIdProvider(async () =>
        {
            if (!state.IsConnected)
            {
                return [];
            }

            try
            {
                var dumps = await httpClient.ListDumpsAsync(state.Settings.UserId);
                return dumps.Select(d => d.DumpId);
            }
            catch
            {
                return [];
            }
        });

        autoComplete.SetSessionIdProvider(async () =>
        {
            if (!state.IsConnected || !mcpClient.IsConnected)
            {
                return Enumerable.Empty<string>();
            }

            try
            {
                var listJson = await mcpClient.ListSessionsAsync(state.Settings.UserId);
                if (IsErrorResult(listJson))
                {
                    return Enumerable.Empty<string>();
                }

                var parsed = System.Text.Json.JsonSerializer.Deserialize<SessionListResponse>(
                    listJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                return parsed?.Sessions?
                           .Select(s => s.SessionId)
                           .Where(id => !string.IsNullOrWhiteSpace(id))
                           .Select(id => id!)
                       ?? Enumerable.Empty<string>();
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        });

        // Run interactive loop
        return await RunShellLoopAsync(console, output, state, httpClient, mcpClient, history, autoComplete);
    }

    /// <summary>
    /// Shows the welcome banner.
    /// </summary>
    private static void ShowWelcomeBanner(IAnsiConsole console)
    {
        var panel = new Panel(
            new FigletText("DBG-MCP")
                .Centered()
                .Color(Color.Cyan1))
        {
            Border = BoxBorder.Double,
            BorderStyle = Style.Parse("cyan"),
            Padding = new Padding(2, 1)
        };

        console.Write(panel);
        console.MarkupLine($"[dim]Debugger MCP CLI v{Version} - Type 'help' for commands, 'exit' to quit.[/]");
        console.WriteLine();
    }

    /// <summary>
    /// Runs the main shell loop.
    /// </summary>
    private static async Task<int> RunShellLoopAsync(
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient,
        McpClient mcpClient,
        CommandHistory history,
        AutoComplete autoComplete)
    {
        // Create custom readline with history and completion
        var readline = new ShellReadLine(console, history, autoComplete, state);
        var cts = new CancellationTokenSource();

        // Handle Ctrl+C gracefully
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    // Read input with history and completion support
                    var input = await readline.ReadLineAsync(cts.Token);

                    if (input == null)
                    {
                        // Ctrl+C pressed - show new prompt
                        output.Warning("Interrupted. Type 'exit' to quit.");
                        cts = new CancellationTokenSource();
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(input))
                    {
                        continue;
                    }

                    // Parse command
                    var parts = ParseCommandLine(input);
                    if (parts.Length == 0)
                    {
                        continue;
                    }

                    var command = parts[0].ToLowerInvariant();
                    var args = parts.Skip(1).ToArray();

                    // Handle commands
                    var transcriptStore = state.Transcript;
                    var commandStartUtc = DateTimeOffset.UtcNow;
                    var transcriptBuffer = new System.Text.StringBuilder(capacity: 4096);
                    using var captureScope = transcriptStore == null
                        ? null
                        : output.BeginTranscriptCapture(line =>
                        {
                            AppendTranscriptCapped(transcriptBuffer, line, maxChars: 250_000);
                        });
                    try
                    {
                        var shouldExit = await HandleCommandAsync(command, args, console, output, state, httpClient, mcpClient, history);

                        if (transcriptStore != null)
                        {
                            try
                            {
                                var outputText = transcriptBuffer.ToString();
                                transcriptStore.Append(new CliTranscriptEntry
                                {
                                    TimestampUtc = commandStartUtc,
                                    Kind = "cli_command",
                                    Text = TranscriptRedactor.RedactCommand(input),
                                    Output = TranscriptRedactor.RedactText(TruncateForTranscript(outputText, maxChars: 50_000)),
                                    ServerUrl = state.Settings.ServerUrl,
                                    SessionId = state.SessionId,
                                    DumpId = state.DumpId
                                });
                            }
                            catch
                            {
                                // Transcript capture should never break the CLI.
                            }
                        }

                        if (shouldExit)
                        {
                            output.Dim("Goodbye!");
                            return 0;
                        }
                    }
                    finally { }
                }
                catch (OperationCanceledException)
                {
                    // Cancelled via Ctrl+C - write newline and create new token
                    Console.WriteLine();
                    output.Warning("Interrupted. Type 'exit' to quit.");
                    cts = new CancellationTokenSource();
                    continue;
                }
                catch (Exception ex)
                {
                    output.Error(ex.Message);
                    if (state.Settings.Verbose)
                    {
                        console.WriteException(ex);
                    }
                }
            }
        }
        finally
        {
            // Clean up MCP client
            await mcpClient.DisposeAsync();
            cts.Dispose();
        }

        return 0;
    }

    private static string TruncateForTranscript(string text, int maxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars] + $"{Environment.NewLine}[...truncated {text.Length - maxChars} chars...]";
    }

    private static void AppendTranscriptCapped(System.Text.StringBuilder buffer, string line, int maxChars)
    {
        if (maxChars <= 0 || buffer.Length >= maxChars)
        {
            return;
        }

        if (string.IsNullOrEmpty(line))
        {
            buffer.AppendLine();
            return;
        }

        var remaining = maxChars - buffer.Length;
        if (remaining <= 0)
        {
            return;
        }

        // Reserve room for newline; append partial line if needed.
        if (line.Length + Environment.NewLine.Length > remaining)
        {
            var sliceLen = Math.Max(0, remaining - Environment.NewLine.Length);
            if (sliceLen > 0)
            {
                buffer.Append(line.AsSpan(0, sliceLen));
            }
            buffer.AppendLine();
            buffer.AppendLine("[...output capture truncated...]");
            return;
        }

        buffer.AppendLine(line);
    }

    /// <summary>
    /// Builds the shell prompt based on current state.
    /// </summary>
    internal static string BuildPrompt(ShellState state)
    {
        var parts = new List<string> { "[grey]dbg-mcp[/]" };

        if (state.IsConnected && !string.IsNullOrEmpty(state.ServerDisplay))
        {
            // Use [[ and ]] to escape brackets in Spectre.Console markup
            parts.Add($"[cyan][[{state.ServerDisplay}]][/]");
        }

        if (!string.IsNullOrEmpty(state.SessionId))
        {
            var shortId = state.SessionId.Length > 8
                ? state.SessionId[..8]
                : state.SessionId;
            parts.Add($"[yellow]session:{shortId}[/]");
        }

        if (!string.IsNullOrEmpty(state.DumpId))
        {
            var shortId = state.DumpId.Length > 8
                ? state.DumpId[..8]
                : state.DumpId;
            parts.Add($"[green]dump:{shortId}[/]");

            if (!string.IsNullOrEmpty(state.DebuggerType))
            {
                // Use [[ and ]] to escape parentheses aren't an issue, but keep consistent
                parts.Add($"[magenta]({state.DebuggerType})[/]");
            }
        }

        return string.Join(" ", parts) + "> ";
    }

    /// <summary>
    /// Parses a command line into parts, respecting quotes.
    /// </summary>
    internal static string[] ParseCommandLine(string input)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var quoteChar = '"';

        foreach (var c in input)
        {
            if ((c == '"' || c == '\'') && !inQuotes)
            {
                inQuotes = true;
                quoteChar = c;
            }
            else if (c == quoteChar && inQuotes)
            {
                inQuotes = false;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts.ToArray();
    }

    /// <summary>
    /// Handles a shell command.
    /// </summary>
    /// <returns>True if the shell should exit.</returns>
    private static async Task<bool> HandleCommandAsync(
        string command,
        string[] args,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient,
        McpClient mcpClient,
        CommandHistory history)
    {
        switch (command)
        {
            case "exit":
            case "quit":
            case "q":
                return true;

            case "help":
            case "?":
                ShowHelp(output, args);
                break;

            case "clear":
            case "cls":
                console.Clear();
                break;

            case "history":
                HandleHistoryCommand(args, output, history);
                break;

            case "copy":
            case "cp":
                await HandleCopyAsync(output, state);
                break;

            case "version":
                output.Markup($"[bold cyan]Debugger MCP CLI[/] version [green]{Version}[/]");
                break;

            case "status":
                ShowStatus(output, state);
                break;

            case "connect":
                await HandleConnectAsync(args, output, state, httpClient, mcpClient);
                break;

            case "disconnect":
                await HandleDisconnectAsync(output, state, httpClient, mcpClient);
                break;

            case "health":
                await HandleHealthAsync(args, output, state, httpClient);
                break;

            case "set":
                var previousTimeoutSeconds = state.Settings.TimeoutSeconds;
                HandleSet(args, output, state);

                if (args.Length >= 2 &&
                    string.Equals(args[0], "timeout", StringComparison.OrdinalIgnoreCase) &&
                    state.Settings.TimeoutSeconds != previousTimeoutSeconds)
                {
                    // Apply timeout change immediately to clients.
                    if (state.IsConnected && !string.IsNullOrWhiteSpace(state.Settings.ServerUrl))
                    {
                        httpClient.Configure(state.Settings.ServerUrl!, state.Settings.ApiKey, state.Settings.Timeout);
                    }
                    mcpClient.ToolResponseTimeout = state.Settings.Timeout;
                }
                break;

            // File operations
            case "dumps":
                await HandleDumpsAsync(args, console, output, state, httpClient, mcpClient);
                break;

            case "symbols":
                await HandleSymbolsAsync(args, console, output, state, httpClient, mcpClient);
                break;

            case "stats":
                await HandleStatsAsync(output, state, httpClient);
                break;

            case "llm":
                await HandleLlmAsync(args, output, state);
                break;

            // Session operations (via MCP)
            case "session":
                await HandleSessionAsync(args, console, output, state, mcpClient);
                break;

            // Debugging operations (via MCP)
            case "open":
                await HandleOpenDumpAsync(args, output, state, mcpClient, httpClient);
                break;

            case "close":
                await HandleCloseDumpAsync(output, state, mcpClient);
                break;

            case "exec":
            case "x":
                await HandleExecAsync(args, output, state, mcpClient);
                break;

            case "cmd":
                await HandleMultiLineCommandAsync(console, output, state, mcpClient);
                break;

            case "showobj":
            case "so":
            case "inspect":
            case "obj":
                await HandleInspectObjectAsync(args, console, output, state, mcpClient);
                break;

            // Analysis operations (via MCP)
            case "analyze":
                await HandleAnalyzeAsync(args, output, state, mcpClient);
                break;

            case "compare":
                await HandleCompareAsync(args, output, state, mcpClient);
                break;

            case "watch":
            case "w":
                await HandleWatchAsync(args, output, state, mcpClient);
                break;

            case "report":
                await HandleReportAsync(args, console, output, state, mcpClient, httpClient);
                break;

            case "sourcelink":
            case "sl":
                await HandleSourceLinkAsync(args, output, state, mcpClient);
                break;

            case "tools":
                ShowTools(output, mcpClient);
                break;

            case "server":
                await HandleServerCommandAsync(args, console, output, state, httpClient, mcpClient);
                break;

            default:
                output.Error($"Unknown command: {command}");
                output.Dim("Type 'help' for available commands.");
                break;
        }

        return false;
    }

    /// <summary>
    /// Shows help information.
    /// </summary>
    internal static void ShowHelp(ConsoleOutput output, string[] args)
    {
        if (args.Length == 0)
        {
            // Show main help overview using HelpSystem
            HelpSystem.ShowOverview(output);
        }
        else
        {
            var arg = args[0].ToLowerInvariant();

            // Check if it's a category
            if (HelpSystem.Categories.ContainsKey(arg))
            {
                HelpSystem.ShowCategoryHelp(output, arg);
                return;
            }

            // Check if it's "all" to list all commands
            if (arg == "all" || arg == "commands")
            {
                HelpSystem.ListAllCommands(output);
                return;
            }

            // Show command-specific help
            var cmd = arg;
            switch (cmd)
            {
                case "connect":
                    output.Header("CONNECT Command");
                    output.WriteLine();
                    output.Markup("Connect to a Debugger MCP Server.");
                    output.WriteLine();
                    output.Markup("[bold]USAGE[/]");
                    output.Markup("  connect                         (reconnect to last/default server)");
                    output.Markup("  connect <url> [[--api-key <key>]]");
                    output.WriteLine();
                    output.Markup("[bold]ARGUMENTS[/]");
                    output.Markup("  [cyan]url[/]             Server URL (optional if server configured)");
                    output.WriteLine();
                    output.Markup("[bold]OPTIONS[/]");
                    output.Markup("  [cyan]--api-key, -k[/]   API key for authentication");
                    output.WriteLine();
                    output.Markup("[bold]AUTO-CONNECT[/]");
                    output.Markup("  When called without arguments, connects to:");
                    output.Markup("  1. Last connected server (if any)");
                    output.Markup("  2. First server in servers.json");
                    output.WriteLine();
                    output.Markup("[bold]EXAMPLES[/]");
                    output.Markup("  [yellow]connect[/]                           Reconnect to default");
                    output.Markup("  [yellow]connect http://localhost:5000[/]");
                    output.Markup("  [yellow]connect localhost:5001 -k my-key[/]");
                    break;

                case "health":
                    output.Header("HEALTH Command");
                    output.WriteLine();
                    output.Markup("Check if a server is healthy and reachable.");
                    output.WriteLine();
                    output.Markup("[bold]USAGE[/]");
                    output.Markup("  health [[url]]");
                    output.WriteLine();
                    output.Markup("[bold]ARGUMENTS[/]");
                    output.Markup("  [cyan]url[/]           Server URL (uses current connection if not specified)");
                    break;

                case "dumps":
                    output.Header("DUMPS Command");
                    output.WriteLine();
                    output.Markup("Manage dump files on the server.");
                    output.WriteLine();
                    output.Markup("[bold]SUBCOMMANDS[/]");
                    output.Markup("  [green]upload[/] <file> Upload a dump file to the server");
                    output.Markup("  [green]list[/]         List all dumps for the current user");
                    output.Markup("  [green]info[/] <id>    Show detailed info for a specific dump");
                    output.Markup("  [green]delete[/] <id>  Delete a dump file");
                    output.WriteLine();
                    output.Markup("[bold]UPLOAD OPTIONS[/]");
                    output.Markup("  [cyan]--description, -d[/]  Add a description to the dump");
                    output.WriteLine();
                    output.Markup("[bold]EXAMPLES[/]");
                    output.Markup("  [yellow]dumps upload ./crash.dmp[/]");
                    output.Markup("  [yellow]dumps upload ./crash.dmp -d 'Production crash'[/]");
                    output.Markup("  [yellow]dumps list[/]");
                    output.Markup("  [yellow]dumps info abc123[/]");
                    output.Markup("  [yellow]dumps delete abc123[/]");
                    output.Markup("  [yellow]dumps binary abc123 ./MyApp[/]");
                    output.Dim("    Upload executable for standalone app (use after dumps upload)");
                    break;

                case "symbols":
                    output.Header("SYMBOLS Command");
                    output.WriteLine();
                    output.Markup("Manage symbol files for dump analysis.");
                    output.WriteLine();
                    output.Markup("[bold]SUBCOMMANDS[/]");
                    output.Markup("  [green]upload[/] <file|pattern>  Upload symbol file(s) from client to server");
                    output.Markup("  [green]list[/] [[--dump-id <id>]] List symbols for a dump");
                    output.Markup("  [green]servers[/]              Show common symbol servers");
                    output.Markup("  [green]add[/] <url>             Add symbol server URL to session");
                    output.Markup("  [green]reload[/]               Reload symbols into running debugger");
                    output.Markup("  [green]clear[/] [[<dumpId>]]     Clear symbol cache for a dump");
                    output.Markup("  [green]datadog[/] <subcommand>  Download Datadog.Trace symbols from Azure Pipelines");
                    output.WriteLine();
                    output.Markup("[bold]DATADOG SYMBOLS[/] (for datadog)");
                    output.Markup("  [dim]Download debug symbols for Datadog.Trace assemblies from Azure Pipelines or GitHub.[/]");
                    output.Markup("  [dim]When no SHA is provided, assemblies are auto-detected from the opened dump.[/]");
                    output.Markup("  [cyan]datadog download[/]                 Auto-detect and download (SHA matching only)");
                    output.Markup("  [cyan]datadog download --force-version[/] Download with version/tag fallback");
                    output.Markup("  [cyan]datadog download <sha>[/]           Download for a specific commit SHA");
                    output.Markup("  [cyan]datadog download --build-id <id>[/] Download from Azure Pipelines build ID");
                    output.Markup("  [cyan]datadog clear[/]                    Clear downloaded symbols for current dump");
                    output.Markup("  [cyan]datadog clear --all[/]              Clear symbols and API caches");
                    output.Markup("  [cyan]datadog list <sha>[/]               List available artifacts for a commit");
                    output.Markup("  [cyan]datadog config[/]                   Show configuration and status");
                    output.WriteLine();
                    output.Markup("  [dim]Symbol lookup order (default): Azure Pipelines SHA → GitHub SHA[/]");
                    output.Markup("  [dim]With --force-version: + Azure Pipelines tag → GitHub tag[/]");
                    output.WriteLine();
                    output.Markup("[bold]ZIP ARCHIVE SUPPORT[/] (for upload)");
                    output.Markup("  [dim]Upload a .zip file to extract symbols preserving directory structure.[/]");
                    output.Markup("  [dim]All subdirectories are added to the debugger's symbol search paths.[/]");
                    output.WriteLine();
                    output.Markup("[bold]WILDCARD PATTERNS[/] (for upload)");
                    output.Markup("  [cyan]*.pdb[/]              All .pdb files in current directory");
                    output.Markup("  [cyan]./bin/*.pdb[/]        All .pdb files in ./bin");
                    output.Markup("  [cyan]**/*.pdb[/]           All .pdb files recursively");
                    output.WriteLine();
                    output.Markup("[bold]SYMBOL SERVERS[/] (for add)");
                    output.Markup("  [dim]Note: 'symbols add' configures symbol servers on the server side.[/]");
                    output.Markup("  [dim]Use URLs to remote symbol servers, not local paths.[/]");
                    output.WriteLine();
                    output.Markup("[bold]RELOAD SYMBOLS[/] (for reload)");
                    output.Markup("  [dim]Use after uploading symbols when a dump is already open.[/]");
                    output.Markup("  [dim]Adds directories to search paths and loads .dbg/.debug files.[/]");
                    output.WriteLine();
                    output.Markup("[bold]CACHE MANAGEMENT[/] (for clear)");
                    output.Markup("  [dim]Use 'symbols clear' after a timed-out download to force re-download.[/]");
                    output.Markup("  [dim]Downloaded symbols are cached per-dump in .symbols_<dumpId> folders.[/]");
                    output.WriteLine();
                    output.Markup("[bold]EXAMPLES[/]");
                    output.Markup("  [dim]General symbols:[/]");
                    output.Markup("  [yellow]symbols upload ./app.pdb[/]");
                    output.Markup("  [yellow]symbols upload *.pdb --dump-id abc123[/]");
                    output.Markup("  [yellow]symbols upload ./symbols.zip[/]    Upload and extract ZIP archive");
                    output.Markup("  [yellow]symbols list[/]");
                    output.Markup("  [yellow]symbols servers[/]");
                    output.Markup("  [yellow]symbols add https://msdl.microsoft.com/download/symbols[/]");
                    output.Markup("  [yellow]symbols reload[/]                  Reload symbols into debugger");
                    output.Markup("  [yellow]symbols clear[/]                   Clear cache for current dump");
                    output.WriteLine();
                    output.Markup("  [dim]Datadog symbols:[/]");
                    output.Markup("  [yellow]symbols datadog download[/]              Auto-detect and download (SHA only)");
                    output.Markup("  [yellow]symbols datadog download -f[/]           With version/tag fallback");
                    output.Markup("  [yellow]symbols datadog download 14fd3a2f[/]     Download for specific commit");
                    output.Markup("  [yellow]symbols datadog download -b 192179[/]    Download from Azure Pipelines build");
                    output.Markup("  [yellow]symbols datadog clear[/]                 Clear downloaded Datadog symbols");
                    output.Markup("  [yellow]symbols datadog clear --all[/]           Also clear API caches");
                    output.Markup("  [yellow]symbols datadog list 14fd3a2f[/]         List available artifacts");
                    output.Markup("  [yellow]symbols datadog config[/]                Show configuration");
                    break;

                case "stats":
                    output.Header("STATS Command");
                    output.WriteLine();
                    output.Markup("Show server statistics.");
                    output.WriteLine();
                    output.Markup("[bold]USAGE[/]");
                    output.Markup("  stats");
                    output.WriteLine();
                    output.Markup("[bold]DISPLAYS[/]");
                    output.Markup("  - Active sessions count");
                    output.Markup("  - Total dumps stored");
                    output.Markup("  - Storage space used");
                    output.Markup("  - Server uptime");
                    break;

                case "session":
                    output.Header("SESSION Command");
                    output.WriteLine();
                    output.Markup("Manage debugging sessions via MCP.");
                    output.WriteLine();
                    output.Markup("[bold]SUBCOMMANDS[/]");
                    output.Markup("  [green]create[/]        Create a new debugging session");
                    output.Markup("  [green]list[/]          List all your active sessions");
                    output.Markup("  [green]use[/] <id>      Attach to an existing session");
                    output.Markup("  [green]close[/] <id>    Close a specific session");
                    output.Markup("  [green]info[/] <id>     Get debugger info for a session");
                    output.WriteLine();
                    output.Markup("[bold]EXAMPLES[/]");
                    output.Markup("  [yellow]session create[/]");
                    output.Markup("  [yellow]session list[/]");
                    output.Markup("  [yellow]session use d0307dc3-5256-4eae-bbd2-5f12e67c6120[/]");
                    output.Markup("  [yellow]session close abc123[/]");
                    break;

                case "open":
                    output.Header("OPEN Command");
                    output.WriteLine();
                    output.Markup("Open a dump file in the debugger for analysis.");
                    output.WriteLine();
                    output.Markup("[bold]USAGE[/]");
                    output.Markup("  open <dumpId>");
                    output.WriteLine();
                    output.Markup("[bold]ARGUMENTS[/]");
                    output.Markup("  [cyan]dumpId[/]        The dump ID from upload (or uses last uploaded)");
                    output.WriteLine();
                    output.Markup("[bold]NOTES[/]");
                    output.Markup("  - Creates a session automatically if needed");
                    output.Markup("  - Configures symbols automatically");
                    output.Markup("  - Use 'upload' first to upload a dump file");
                    output.WriteLine();
                    output.Markup("[bold]EXAMPLES[/]");
                    output.Markup("  [yellow]open abc123[/]           Open specific dump");
                    output.Markup("  [yellow]upload crash.dmp[/]      Upload then...");
                    output.Markup("  [yellow]open[/]                  ...open the uploaded dump");
                    break;

                case "exec":
                case "x":
                    output.Header("EXEC Command");
                    output.WriteLine();
                    output.Markup("Execute a native debugger command.");
                    output.WriteLine();
                    output.Markup("[bold]USAGE[/]");
                    output.Markup("  exec <command>");
                    output.Markup("  x <command>");
                    output.WriteLine();
                    output.Markup("[bold]WINDBG COMMANDS[/]");
                    output.Markup("  [cyan]k[/]             Call stack");
                    output.Markup("  [cyan]~*[/]            All threads");
                    output.Markup("  [cyan]!analyze -v[/]   Crash analysis");
                    output.Markup("  [cyan]lm[/]            Loaded modules");
                    output.Markup("  [cyan]!threads[/]      .NET threads (when SOS is loaded)");
                    output.Markup("  [cyan]!clrstack[/]     .NET call stack (when SOS is loaded)");
                    output.WriteLine();
                    output.Markup("[bold]LLDB COMMANDS[/]");
                    output.Markup("  [cyan]bt[/]            Backtrace");
                    output.Markup("  [cyan]thread list[/]   All threads");
                    output.Markup("  [cyan]frame info[/]    Frame information");
                    output.WriteLine();
                    output.Markup("[bold]EXAMPLES[/]");
                    output.Markup("  [yellow]exec k[/]");
                    output.Markup("  [yellow]exec !analyze -v[/]");
                    output.Markup("  [yellow]x !dumpheap -stat[/]");
                    output.WriteLine();
                    output.Dim("Tip: Use 'cmd' to enter multi-line command mode.");
                    break;

                case "cmd":
                    output.Header("CMD Command");
                    output.WriteLine();
                    output.Markup("Enter multi-line command mode for continuous debugger interaction.");
                    output.WriteLine();
                    output.Markup("[bold]USAGE[/]");
                    output.Markup("  cmd");
                    output.WriteLine();
                    output.Markup("[bold]DESCRIPTION[/]");
                    output.Markup("  Opens an interactive mode where you can type debugger commands");
                    output.Markup("  directly without the 'exec' prefix. Each command is executed");
                    output.Markup("  immediately and results are displayed.");
                    output.WriteLine();
                    output.Markup("[bold]EXIT COMMANDS[/]");
                    output.Markup("  [cyan]exit[/]          Return to normal CLI mode");
                    output.Markup("  [cyan]quit[/]          Return to normal CLI mode");
                    output.Markup("  [cyan]q[/]             Return to normal CLI mode");
                    output.Markup("  [cyan]Ctrl+C[/]        Return to normal CLI mode");
                    output.WriteLine();
                    output.Markup("[bold]EXAMPLE SESSION[/]");
                    output.Markup("  [dim]dbg-mcp>[/] [yellow]cmd[/]");
                    output.Markup("  [dim]0:000>[/] [yellow]k[/]");
                    output.Markup("  [dim](stack output...)[/]");
                    output.Markup("  [dim]0:000>[/] [yellow]!threads[/]");
                    output.Markup("  [dim](threads output...)[/]");
                    output.Markup("  [dim]0:000>[/] [yellow]exit[/]");
                    output.Markup("  [dim]dbg-mcp>[/]");
                    break;

                case "showobj":
                case "so":
                    output.Header("SHOWOBJ Command");
                    output.WriteLine();
                    output.Markup("Inspect a .NET object at the given address and display it as JSON.");
                    output.WriteLine();
                    output.Markup("[bold]USAGE[/]");
                    output.Markup("  showobj <address> [[options]]");
                    output.Markup("  so <address>");
                    output.WriteLine();
                    output.Markup("[bold]ARGUMENTS[/]");
                    output.Markup("  [cyan]address[/]          Memory address of the object (hex)");
                    output.WriteLine();
                    output.Markup("[bold]OPTIONS[/]");
                    output.Markup("  [cyan]--mt, -m[/]         Method table (fallback if dumpobj fails)");
                    output.Markup("  [cyan]--depth, -d[/]      Max recursion depth (default: 5)");
                    output.Markup("  [cyan]--array-limit, -a[/] Max array elements to show (default: 10)");
                    output.Markup("  [cyan]--string-limit, -s[/] Max string length before truncation (default: 1024)");
                    output.Markup("  [cyan]-o, --output[/]     Save JSON to file");
                    output.WriteLine();
                    output.Markup("[bold]FEATURES[/]");
                    output.Markup("  • Recursively inspects nested objects");
                    output.Markup("  • Handles circular references ([[this]], [[seen]])");
                    output.Markup("  • Expands arrays (shows length + first N elements)");
                    output.Markup("  • Truncates long strings (1024 chars)");
                    output.Markup("  • Resolves primitive values directly");
                    output.WriteLine();
                    output.Markup("[bold]EXAMPLES[/]");
                    output.Markup("  [yellow]showobj f7158ec79b48[/]");
                    output.Markup("  [yellow]so f7158ec79b48 --depth 3[/]");
                    output.Markup("  [yellow]showobj f7158ec79b48 -o object.json[/]");
                    output.Markup("  [cyan]!dumpheap[/]     Heap analysis");
                    output.Markup("  [cyan]!eeheap[/]       GC heap info");
                    output.Markup("  [cyan]!dumpobj[/]      Dump object details");
                    output.WriteLine();
                    output.Markup("[bold]EXAMPLE[/]");
                    output.Markup("  [yellow]open abc123[/]");
                    output.Markup("  [yellow]exec !threads[/]");
                    break;

                case "analyze":
                    output.Header("ANALYZE Command");
                    output.WriteLine();
                    output.Markup("Run automated analysis on the current dump.");
                    output.WriteLine();
                    output.Markup("[bold]USAGE[/]");
                    output.Markup("  analyze <type>");
                    output.WriteLine();
                    output.Markup("[bold]ANALYSIS TYPES[/]");
                    output.Markup("  [cyan]crash[/]         General crash analysis");
                    output.Markup("  [cyan]dotnet[/]        .NET-specific analysis");
                    output.Markup("  [cyan]perf[/]          Performance profiling summary");
                    output.Markup("  [cyan]cpu[/]           CPU usage analysis");
                    output.Markup("  [cyan]memory[/]        Memory allocation analysis");
                    output.Markup("  [cyan]gc[/]            Garbage collection analysis");
                    output.Markup("  [cyan]contention[/]    Thread contention analysis");
                    output.Markup("  [cyan]security[/]      Security vulnerability scan");
                    output.WriteLine();
                    output.Markup("[bold]EXAMPLES[/]");
                    output.Markup("  [yellow]analyze crash[/]");
                    output.Markup("  [yellow]analyze dotnet[/]");
                    output.Markup("  [yellow]analyze perf[/]");
                    output.Markup("  [yellow]analyze security[/]");
                    break;

                case "compare":
                    output.Header("COMPARE Command");
                    output.WriteLine();
                    output.Markup("Compare two memory dumps to find differences.");
                    output.WriteLine();
                    output.Markup("[bold]USAGE[/]");
                    output.Markup("  compare [[type]] <baseline-session> <target-session>");
                    output.WriteLine();
                    output.Markup("[bold]COMPARISON TYPES[/]");
                    output.Markup("  [cyan]all[/]           Full comparison (default)");
                    output.Markup("  [cyan]heap[/]          Heap/memory comparison");
                    output.Markup("  [cyan]threads[/]       Thread comparison");
                    output.Markup("  [cyan]modules[/]       Loaded modules comparison");
                    output.WriteLine();
                    output.Markup("[bold]WORKFLOW[/]");
                    output.Markup("  1. Create two sessions and open dumps in each");
                    output.Markup("  2. Run compare with both session IDs");
                    output.WriteLine();
                    output.Markup("[bold]EXAMPLES[/]");
                    output.Markup("  [yellow]compare session1 session2[/]");
                    output.Markup("  [yellow]compare heap d03 a8b[/]  (partial IDs work!)");
                    output.Markup("  [yellow]compare threads s1 s2[/]");
                    break;

                case "watch":
                case "w":
                    output.Header("WATCH Command");
                    output.WriteLine();
                    output.Markup("Manage watch expressions to track values across sessions.");
                    output.WriteLine();
                    output.Markup("[bold]SUBCOMMANDS[/]");
                    output.Markup("  [green]add[/] <expr>     Add a watch expression");
                    output.Markup("  [green]list[/]          List all watches (default)");
                    output.Markup("  [green]eval[/] [[id]]     Evaluate watches (all or specific)");
                    output.Markup("  [green]remove[/] <id>   Remove a watch");
                    output.Markup("  [green]clear[/]         Clear all watches");
                    output.WriteLine();
                    output.Markup("[bold]OPTIONS[/]");
                    output.Markup("  --name, -n      Give the watch a friendly name");
                    output.WriteLine();
                    output.Markup("[bold]EXAMPLES[/]");
                    output.Markup("  [yellow]watch add 0x7fff1234[/]           Memory address");
                    output.Markup("  [yellow]watch add !dumpheap -stat[/]      Debugger command");
                    output.Markup("  [yellow]watch add @rsp --name stack[/]    Named watch");
                    output.Markup("  [yellow]watch list[/]");
                    output.Markup("  [yellow]watch eval[/]                     Evaluate all");
                    output.Markup("  [yellow]watch eval w1[/]                  Evaluate specific");
                    output.Markup("  [yellow]watch remove w1[/]");
                    output.Markup("  [yellow]watch clear[/]");
                    break;

                case "report":
                    output.Header("REPORT Command");
                    output.WriteLine();
                    output.Markup("Generate comprehensive crash analysis reports.");
                    output.WriteLine();
                    output.Markup("[bold]USAGE[/]");
                    output.Markup("  report -o <file> [[options]]");
                    output.WriteLine();
                    output.Markup("[bold]OPTIONS[/]");
                    output.Markup("  [cyan]--format, -f[/]    Report format (see formats below)");
                    output.Markup("  [cyan]--output, -o[/]    Output file path (required)");
                    output.Markup("  [cyan]--summary, -s[/]   Summary report only");
                    output.Markup("  [cyan]--no-watches[/]    Exclude watch expressions");
                    output.WriteLine();
                    output.Markup("[bold]INCLUDED ANALYSIS[/]");
                    output.Markup("  Reports automatically include:");
                    output.Markup("  • Top memory consumers by size and count");
                    output.Markup("  • Async/Task state analysis (faulted tasks, pending state machines)");
                    output.Markup("  • String duplicate detection with optimization suggestions");
                    output.Markup("  • Heap fragmentation calculation");
                    output.Markup("  Note: Uses parallel processing for Server GC (faster on multi-core)");
                    output.WriteLine();
                    output.Markup("[bold]FORMATS[/]");
                    output.Markup("  [green]markdown[/]  Markdown with ASCII charts (default)");
                    output.Markup("  [green]html[/]      Styled HTML, printable to PDF");
                    output.Markup("  [green]json[/]      Structured data for automation");
                    output.WriteLine();
                    output.Markup("[bold]EXAMPLES[/]");
                    output.Markup("  [yellow]report -o ./report.md[/]      Save to file");
                    output.Markup("  [yellow]report -o ./r.html -f html[/] Save HTML");
                    output.Markup("  [yellow]report -o ./summary.json --summary -f json[/]   JSON summary");
                    break;

                case "sourcelink":
                case "sl":
                    output.Header("SOURCELINK Command");
                    output.WriteLine();
                    output.Markup("Resolve source file paths to browsable URLs.");
                    output.WriteLine();
                    output.Markup("[bold]USAGE[/]");
                    output.Markup("  sourcelink [[resolve]] <path> [[line]]");
                    output.Markup("  sourcelink info");
                    output.WriteLine();
                    output.Markup("[bold]SUBCOMMANDS[/]");
                    output.Markup("  [green]resolve[/]       Resolve source file to URL (default)");
                    output.Markup("  [green]info[/]          Show Source Link configuration");
                    output.WriteLine();
                    output.Markup("[bold]SUPPORTED PROVIDERS[/]");
                    output.Markup("  - GitHub (github.com)");
                    output.Markup("  - GitLab (gitlab.com)");
                    output.Markup("  - Azure DevOps (dev.azure.com)");
                    output.Markup("  - Bitbucket (bitbucket.org)");
                    output.WriteLine();
                    output.Markup("[bold]EXAMPLES[/]");
                    output.Markup("  [yellow]sourcelink /src/Program.cs[/]");
                    output.Markup("  [yellow]sourcelink /src/Program.cs 42[/]    With line");
                    output.Markup("  [yellow]sl resolve MyApp/Startup.cs[/]");
                    output.Markup("  [yellow]sourcelink info[/]");
                    output.WriteLine();
                    output.Markup("[bold]TIPS[/]");
                    output.Markup("  - Upload PDB files with Source Link info");
                    output.Markup("  - Build with <PublishRepositoryUrl>true</PublishRepositoryUrl>");
                    break;

                case "server":
                    output.Header("SERVER Command");
                    output.WriteLine();
                    output.Markup("Manage multi-server connections for cross-platform dump analysis.");
                    output.WriteLine();
                    output.Markup("[bold]USAGE[/]");
                    output.Markup("  server <subcommand> [[options]]");
                    output.WriteLine();
                    output.Markup("[bold]SUBCOMMANDS[/]");
                    output.Markup("  [green]list[/]              List all configured servers with capabilities");
                    output.Markup("  [green]add[/] <url>         Add a new server to configuration");
                    output.Markup("  [green]remove[/] <url|name> Remove a server by URL or name");
                    output.Markup("  [green]switch[/] <url|name> Switch to a different server");
                    output.Markup("  [green]init[/]              Create default config for docker-compose");
                    output.WriteLine();
                    output.Markup("[bold]AUTO-GENERATED NAMES[/]");
                    output.Markup("  Servers are named by capabilities: alpine-arm64, debian-x64, etc.");
                    output.WriteLine();
                    output.Markup("[bold]EXAMPLES[/]");
                    output.Markup("  [yellow]server list[/]");
                    output.Markup("  [yellow]server init[/]                     Create default config");
                    output.Markup("  [yellow]server add http://localhost:5004[/]");
                    output.Markup("  [yellow]server add http://prod:5000 --api-key secret[/]");
                    output.Markup("  [yellow]server switch alpine-x64[/]        By auto-name");
                    output.Markup("  [yellow]server switch localhost:5001[/]    By URL");
                    output.Markup("  [yellow]server remove debian-arm64[/]");
                    output.WriteLine();
                    output.Markup("[bold]DUMP-SERVER MATCHING[/]");
                    output.Markup("  When opening a dump, the CLI checks if the server matches.");
                    output.Markup("  If there's a mismatch (arch or Alpine), you'll be prompted to switch.");
                    break;

                case "close":
                    output.Header("CLOSE Command");
                    output.WriteLine();
                    output.Markup("Close the currently open dump file.");
                    output.WriteLine();
                    output.Markup("[bold]USAGE[/]");
                    output.Markup("  close");
                    output.WriteLine();
                    output.Markup("[bold]NOTES[/]");
                    output.Markup("  - The session remains active");
                    output.Markup("  - You can open another dump in the same session");
                    break;

                case "history":
                    output.Header("HISTORY Command");
                    output.WriteLine();
                    output.Markup("View and manage command history.");
                    output.WriteLine();
                    output.Markup("[bold]USAGE[/]");
                    output.Markup("  history              Show recent commands");
                    output.Markup("  history <n>          Show last n commands");
                    output.Markup("  history search <s>   Search history for string");
                    output.Markup("  history clear        Clear all history");
                    output.WriteLine();
                    output.Markup("[bold]KEYBOARD SHORTCUTS[/]");
                    output.Markup("  [cyan]↑/↓[/]         Navigate through history");
                    output.Markup("  [cyan]Tab[/]         Auto-complete commands");
                    output.Markup("  [cyan]Ctrl+C[/]      Cancel current line");
                    output.Markup("  [cyan]Ctrl+L[/]      Clear screen");
                    output.Markup("  [cyan]Ctrl+U[/]      Clear line before cursor");
                    output.Markup("  [cyan]Ctrl+K[/]      Clear line after cursor");
                    output.Markup("  [cyan]Ctrl+W[/]      Delete word before cursor");
                    output.WriteLine();
                    output.Markup("[bold]PERSISTENCE[/]");
                    output.Markup($"  History saved to: [dim]{ConnectionSettings.DefaultHistoryFile}[/]");
                    break;

                case "copy":
                case "cp":
                    output.Header("COPY Command");
                    output.WriteLine();
                    output.Markup("Copy the result of the last executed command to the clipboard.");
                    output.WriteLine();
                    output.Markup("[bold]USAGE[/]");
                    output.Markup("  copy                 Copy last result to clipboard");
                    output.Markup("  cp                   Short form");
                    output.WriteLine();
                    output.Markup("[bold]NOTES[/]");
                    output.Markup("  - Works with results from exec, analyze, and other commands");
                    output.Markup("  - Shows the command name and result size");
                    output.Markup("  - Cross-platform: works on Windows, macOS, and Linux");
                    output.WriteLine();
                    output.Markup("[bold]EXAMPLES[/]");
                    output.Markup("  [yellow]exec bt[/]             Execute backtrace command");
                    output.Markup("  [yellow]copy[/]                Copy the backtrace to clipboard");
                    output.Markup("  [yellow]analyze crash[/]       Analyze crash");
                    output.Markup("  [yellow]cp[/]                  Copy analysis result to clipboard");
                    break;

                default:
                    output.Warning($"No help available for command: {cmd}");
                    break;
            }
        }
    }

    /// <summary>
    /// Handles the history command.
    /// </summary>
    internal static void HandleHistoryCommand(string[] args, ConsoleOutput output, CommandHistory history)
    {
        if (args.Length == 0)
        {
            // Show recent history
            var entries = history.Entries;
            if (entries.Count == 0)
            {
                output.Dim("No command history.");
                return;
            }

            output.Header($"Command History ({entries.Count} entries)");
            output.WriteLine();

            // Show last 20 entries with line numbers
            var startIndex = Math.Max(0, entries.Count - 20);
            for (var i = startIndex; i < entries.Count; i++)
            {
                output.Markup($"[dim]{i + 1,4}[/]  {entries[i]}");
            }

            output.WriteLine();
            output.Dim("Use 'history clear' to clear, 'history <n>' to show last n entries");
            return;
        }

        var subcommand = args[0].ToLowerInvariant();

        switch (subcommand)
        {
            case "clear":
                history.Clear();
                output.Success("Command history cleared.");
                break;

            case "search" when args.Length > 1:
                var searchTerm = args[1];
                var matches = history.Search(searchTerm).Take(20).ToList();

                if (matches.Count == 0)
                {
                    output.Warning($"No history entries match '{searchTerm}'");
                }
                else
                {
                    output.Header($"History matching '{searchTerm}'");
                    foreach (var match in matches)
                    {
                        output.Markup($"  {match}");
                    }
                }
                break;

            default:
                // Try to parse as number
                if (int.TryParse(subcommand, out var count) && count > 0)
                {
                    var entries = history.Entries;
                    var startIndex = Math.Max(0, entries.Count - count);

                    output.Header($"Last {Math.Min(count, entries.Count)} commands");
                    output.WriteLine();

                    for (var i = startIndex; i < entries.Count; i++)
                    {
                        output.Markup($"[dim]{i + 1,4}[/]  {entries[i]}");
                    }
                }
                else
                {
                    output.Error($"Unknown subcommand: {subcommand}");
                    output.Dim("Usage: history [clear|search <term>|<count>]");
                }
                break;
        }
    }

    /// <summary>
    /// Handles the copy command (copy last result to clipboard).
    /// </summary>
    private static async Task HandleCopyAsync(ConsoleOutput output, ShellState state)
    {
        if (!state.HasLastResult)
        {
            output.Warning("No command result available to copy.");
            output.Dim("Execute a command first (e.g., exec, analyze) then use 'copy' or 'cp'.");
            return;
        }

        try
        {
            await TextCopy.ClipboardService.SetTextAsync(state.LastCommandResult!);
            
            // Show summary of what was copied
            var resultLength = state.LastCommandResult!.Length;
            var lineCount = state.LastCommandResult.Split('\n').Length;
            var sizeDisplay = resultLength > 1024 
                ? $"{resultLength / 1024.0:F1} KB" 
                : $"{resultLength} bytes";
            
            output.Success($"Copied to clipboard!");
            output.KeyValue("Command", state.LastCommandName ?? "unknown");
            output.KeyValue("Size", sizeDisplay);
            output.KeyValue("Lines", lineCount.ToString());
        }
        catch (Exception ex)
        {
            output.Error($"Failed to copy to clipboard: {ex.Message}");
            output.Dim("Note: Clipboard access may require a display environment on Linux.");
        }
    }

    /// <summary>
    /// Shows current status.
    /// </summary>
    private static void ShowStatus(ConsoleOutput output, ShellState state)
    {
        output.Header("Current Status");
        output.WriteLine();

        output.KeyValue("Connected", state.IsConnected ? "Yes" : "No");

        if (state.IsConnected)
        {
            output.KeyValue("Server", state.ServerDisplay);
            
            // Show server host info if available
            if (state.ServerInfo != null)
            {
                var info = state.ServerInfo;
                output.KeyValue("Host", $"{info.Description}{(info.IsDocker ? " (Docker)" : "")}");
                output.KeyValue("Debugger", info.DebuggerType);
                output.KeyValue("Runtime", info.DotNetVersion);
                
                if (info.IsAlpine)
                {
                    output.Warning("⚠️  Alpine host - can only debug Alpine .NET dumps");
                }
                
                if (info.InstalledRuntimes.Count > 0)
                {
                    var majorVersions = info.InstalledRuntimes
                        .Select(r => r.Split('.')[0])
                        .Distinct()
                        .OrderByDescending(v => v)
                        .ToList();
                    output.KeyValue("Supported .NET", string.Join(", ", majorVersions.Select(v => $".NET {v}")));
                }
            }
        }

        output.KeyValue("Session", state.SessionId ?? "(none)");
        output.KeyValue("Dump", state.DumpId ?? "(none)");

        if (!string.IsNullOrEmpty(state.DebuggerType))
        {
            output.KeyValue("Session Debugger", state.DebuggerType);
        }

        output.WriteLine();
        output.KeyValue("User ID", state.Settings.UserId);
        output.KeyValue("Verbose", state.Settings.Verbose.ToString());
        output.KeyValue("Output Format", state.Settings.OutputFormat.ToString());
    }

    /// <summary>
    /// Handles the connect command.
    /// </summary>
    private static async Task HandleConnectAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient,
        McpClient mcpClient)
    {
        string? serverUrl = null;
        string? apiKey = null;

        // Parse arguments
        for (var i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--api-key" || args[i] == "-k") && i + 1 < args.Length)
            {
                apiKey = args[++i];
            }
            else if (!args[i].StartsWith("-"))
            {
                serverUrl = args[i];
            }
        }

        // If no URL provided, try to use configured/default server
        if (string.IsNullOrEmpty(serverUrl))
        {
            // First try last connected server
            if (!string.IsNullOrEmpty(state.Settings.ServerUrl))
            {
                serverUrl = state.Settings.ServerUrl;
                apiKey ??= state.Settings.ApiKey;
                output.Info($"Reconnecting to {serverUrl}...");
            }
            else
            {
                // Try first configured server from servers.json
                var configManager = new ServerConfigManager();
                var servers = configManager.GetServers();
                if (servers.Count > 0)
                {
                    serverUrl = servers[0].Url;
                    apiKey ??= servers[0].ApiKey;
                    output.Info($"Connecting to first configured server: {serverUrl}");
                }
                else
                {
                    output.Error("No server URL provided and no servers configured.");
                    output.Dim("Usage: connect <url> [--api-key <key>]");
                    output.Dim("   or: server add <url> to configure servers");
                    return;
                }
            }
        }

        try
        {
            // Configure HTTP client
            httpClient.Configure(serverUrl, apiKey, state.Settings.Timeout);
            
            // Use the normalized URL from the client (has http:// scheme added if missing)
            var normalizedUrl = httpClient.ServerUrl!;

            var health = await output.WithSpinnerAsync(
                $"Connecting to [cyan]{normalizedUrl}[/]...",
                () => httpClient.CheckHealthAsync());

            if (health.IsHealthy)
            {
                state.SetConnected(normalizedUrl);
                state.Settings.ServerUrl = normalizedUrl;
                state.Settings.ApiKey = apiKey;
                state.Settings.Save();

                output.Success($"Connected to {normalizedUrl}");
                output.KeyValue("Status", health.Status);

                // Get and display server host information
                var serverInfo = await httpClient.GetServerInfoAsync();
                if (serverInfo != null)
                {
                    state.ServerInfo = serverInfo;
                    output.WriteLine();
                    output.Header("Server Host Information");
                    output.KeyValue("Host", serverInfo.Description);
                    if (serverInfo.IsDocker)
                    {
                        output.KeyValue("Container", "Docker");
                    }
                    output.KeyValue("Debugger", serverInfo.DebuggerType);
                    output.KeyValue("Runtime", serverInfo.DotNetVersion);
                    
                    if (serverInfo.IsAlpine)
                    {
                        output.Warning("⚠️  Alpine Linux host - can only debug Alpine .NET dumps");
                    }
                    
                    if (serverInfo.InstalledRuntimes.Count > 0)
                    {
                        // Show major versions available
                        var majorVersions = serverInfo.InstalledRuntimes
                            .Select(r => r.Split('.')[0])
                            .Distinct()
                            .OrderByDescending(v => v)
                            .ToList();
                        output.KeyValue("Supported .NET", string.Join(", ", majorVersions.Select(v => $".NET {v}")));
                    }
                    output.WriteLine();
                }

                // Also connect MCP client for debugging operations
                try
                {
                    await output.WithSpinnerAsync(
                        "Connecting MCP client...",
                        async () =>
                        {
                            await mcpClient.ConnectAsync(normalizedUrl, apiKey);
                            return true;
                        });

                    output.Success($"MCP client connected ({mcpClient.AvailableTools.Count} tools available)");

                    RegisterMcpSamplingHandlers(output, state, mcpClient);

                    var autoRestore = await DebuggerMcp.Cli.Shell.LastSessionAutoRestore.TryRestoreAsync(output, state, mcpClient);
                    if (autoRestore.ClearedSavedSession)
                    {
                        state.Settings.Save();
                    }
                }
                catch (Exception mcpEx)
                {
                    output.Warning($"MCP connection failed: {mcpEx.Message}");
                    output.Dim("Debugging commands will not be available. File operations still work.");
                }
            }
            else
            {
                output.Error($"Server is not healthy: {health.Status}");
            }
        }
        catch (Exception ex)
        {
            output.Error($"Failed to connect: {ex.Message}");
        }
    }

    private static void RegisterMcpSamplingHandlers(ConsoleOutput output, ShellState state, McpClient mcpClient)
    {
        try
        {
            var llmSettings = state.Settings.Llm;
            llmSettings.ApplyEnvironmentOverrides();

            var handler = new McpSamplingCreateMessageHandler(
                llmSettings,
                async (request, ct) =>
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1, llmSettings.TimeoutSeconds)) };
                    var client = new OpenRouterClient(http, llmSettings);
                    return await client.ChatCompletionAsync(request, ct).ConfigureAwait(false);
                });

            mcpClient.RegisterServerRequestHandler(
                "sampling/createMessage",
                async (p, ct) => await handler.HandleAsync(p, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            output.Warning($"Failed to enable MCP sampling handler: {ex.Message}");
            output.Dim("AI sampling requests (server-initiated) will not work from this CLI.");
        }
    }

    /// <summary>
    /// Handles the disconnect command.
    /// </summary>
    private static async Task HandleDisconnectAsync(
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient,
        McpClient mcpClient)
    {
        if (!state.IsConnected)
        {
            output.Warning("Not connected to any server.");
            return;
        }

        var server = state.ServerDisplay;

        // Disconnect MCP client
        if (mcpClient.IsConnected)
        {
            await mcpClient.DisconnectAsync();
        }

        state.Reset();
        output.Success($"Disconnected from {server}");
    }

    /// <summary>
    /// Handles the health command.
    /// </summary>
    private static async Task HandleHealthAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient)
    {
        var serverUrl = args.Length > 0 ? args[0] : state.Settings.ServerUrl;

        if (string.IsNullOrEmpty(serverUrl))
        {
            if (!state.IsConnected)
            {
                output.Error("Not connected. Specify a URL or connect to a server first.");
                return;
            }
            serverUrl = state.Settings.ServerUrl;
        }

        using var tempClient = new HttpApiClient();
        tempClient.Configure(serverUrl!, state.Settings.ApiKey, state.Settings.Timeout);

        try
        {
            var health = await output.WithSpinnerAsync(
                $"Checking health of [cyan]{serverUrl}[/]...",
                () => tempClient.CheckHealthAsync());

            if (health.IsHealthy)
            {
                output.Success("Server is healthy");
                output.KeyValue("Status", health.Status);
            }
            else
            {
                output.Error($"Server is not healthy: {health.Status}");
            }
        }
        catch (Exception ex)
        {
            output.Error($"Health check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the set command.
    /// </summary>
    private static void HandleSet(string[] args, ConsoleOutput output, ShellState state)
    {
        if (args.Length < 2)
        {
            output.Error("Usage: set <key> <value>");
            output.Dim("Available settings: verbose, output, timeout, user-id");
            return;
        }

        var key = args[0].ToLowerInvariant();
        var value = args[1];

        switch (key)
        {
            case "verbose":
                state.Settings.Verbose = value.ToLowerInvariant() is "true" or "1" or "yes";
                output.Verbose = state.Settings.Verbose;
                output.Success($"Verbose mode: {(state.Settings.Verbose ? "enabled" : "disabled")}");
                break;

            case "output":
                state.Settings.OutputFormat = value.ToLowerInvariant() == "json"
                    ? OutputFormat.Json
                    : OutputFormat.Text;
                output.Format = state.Settings.OutputFormat;
                output.Success($"Output format: {state.Settings.OutputFormat}");
                break;

            case "timeout":
                if (int.TryParse(value, out var timeout) && timeout > 0)
                {
                    state.Settings.TimeoutSeconds = timeout;
                    output.Success($"Timeout: {timeout} seconds");
                }
                else
                {
                    output.Error("Invalid timeout value. Must be a positive number.");
                }
                break;

            case "user-id":
            case "userid":
                state.Settings.UserId = value;
                output.Success($"User ID: {value}");
                break;

            default:
                output.Error($"Unknown setting: {key}");
                output.Dim("Available settings: verbose, output, timeout, user-id");
                break;
        }
    }


    /// <summary>
    /// Handles the upload command.
    /// </summary>
    private static async Task HandleUploadAsync(
        string[] args,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient,
        McpClient mcpClient)
    {
        // Require connection
        if (!state.IsConnected)
        {
            output.Error("Not connected. Use 'connect <url>' first.");
            return;
        }

        string? filePath = null;
        string? description = null;

        // Parse arguments
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--description" or "-d" && i + 1 < args.Length)
            {
                description = args[++i];
            }
            else if (!args[i].StartsWith("-"))
            {
                filePath = args[i];
            }
        }

        // Validate
        if (string.IsNullOrEmpty(filePath))
        {
            output.Error("File path is required. Usage: upload <file> [--description <desc>]");
            return;
        }

        // Expand path
        filePath = Path.GetFullPath(filePath);

        if (!File.Exists(filePath))
        {
            output.Error($"File not found: {filePath}");
            return;
        }

        var fileInfo = new FileInfo(filePath);
        var fileName = fileInfo.Name;
        var fileSize = fileInfo.Length;

        output.Markup($"[blue]ℹ[/] Uploading [cyan]{fileName}[/] ({FormatBytes(fileSize)})...");

        try
        {
            var progressRenderer = new ProgressRenderer(console);

            var result = await progressRenderer.WithUploadProgressAsync(
                fileName,
                fileSize,
                progress => httpClient.UploadDumpAsync(
                    filePath,
                    state.Settings.UserId,
                    description,
                    progress));

            output.Success($"Upload complete!");
            output.WriteLine();
            output.KeyValue("Dump ID", result.DumpId);
            output.KeyValue("File Name", result.FileName ?? fileName);
            output.KeyValue("Size", result.FormattedSize);

            if (!string.IsNullOrEmpty(result.DumpFormat))
            {
                output.KeyValue("Format", result.DumpFormat);
            }

            // Show runtime version
            if (!string.IsNullOrEmpty(result.RuntimeVersion))
            {
                output.KeyValue("Runtime", $".NET {result.RuntimeVersion}");
            }
            
            // Show architecture
            if (!string.IsNullOrEmpty(result.Architecture))
            {
                output.KeyValue("Architecture", result.Architecture);
            }

            // Show Alpine/glibc host type
            if (result.IsAlpineDump.HasValue)
            {
                if (result.IsAlpineDump.Value)
                {
                    output.KeyValue("Host Type", "Alpine Linux (musl)");
                }
                else
                {
                    output.KeyValue("Host Type", "glibc (Debian/Ubuntu/etc.)");
                }
            }

            var likelyIncompatible = IsLikelyIncompatibleWithCurrentServer(result.IsAlpineDump, result.Architecture, state);
            if (!likelyIncompatible || !mcpClient.IsConnected)
            {
                // Best-effort: show compatibility warnings inline when we can determine the server characteristics.
                CheckDumpServerCompatibility(result.IsAlpineDump, result.Architecture, state, output);
            }

            output.KeyValue("Uploaded At", result.UploadedAt.ToString("yyyy-MM-dd HH:mm:ss"));

            // Select the dump for convenience (does not imply it's open/loaded).
            state.SetSelectedDump(result.DumpId);
            output.WriteLine();
            output.Dim("Dump selected. Use 'open' to start debugging.");

            // If the dump is incompatible with the current server, offer to switch now (so the next 'open' works).
            if (likelyIncompatible && mcpClient.IsConnected)
            {
                var switchResult = await CheckDumpServerMatchAndSwitchAsync(result.DumpId, output, state, httpClient, mcpClient);
                if (switchResult == ServerSwitchResult.NoSwitchNeeded)
                {
                    // Fall back to the simple compatibility warning when capabilities discovery fails.
                    CheckDumpServerCompatibility(result.IsAlpineDump, result.Architecture, state, output);
                }
            }
        }
        catch (HttpApiException ex)
        {
            output.Error($"Upload failed: {ex.Message}");
            if (state.Settings.Verbose)
            {
                output.Dim($"Error code: {ex.ErrorCode}");
            }
        }
        catch (Exception ex)
        {
            output.Error($"Upload failed: {ex.Message}");
            if (state.Settings.Verbose)
            {
                console.WriteException(ex);
            }
        }
    }

    /// <summary>
    /// Handles the dumps command.
    /// </summary>
    private static async Task HandleDumpsAsync(
        string[] args,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient,
        McpClient mcpClient)
    {
        // Require connection
        if (!state.IsConnected)
        {
            output.Error("Not connected. Use 'connect <url>' first.");
            return;
        }

        if (args.Length == 0)
        {
            output.Error("Subcommand required. Usage: dumps <upload|list|info|delete|binary>");
            return;
        }

        var subcommand = args[0].ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();

        switch (subcommand)
        {
            case "upload":
            case "up":
                await HandleUploadAsync(subArgs, console, output, state, httpClient, mcpClient);
                break;

            case "list":
            case "ls":
                await HandleDumpsListAsync(console, output, state, httpClient);
                break;

            case "info":
            case "show":
                await HandleDumpsInfoAsync(subArgs, output, state, httpClient);
                break;

            case "delete":
            case "rm":
                await HandleDumpsDeleteAsync(subArgs, console, output, state, httpClient);
                break;

            case "binary":
            case "bin":
                await HandleDumpsBinaryUploadAsync(subArgs, output, state, httpClient);
                break;

            default:
                output.Error($"Unknown subcommand: {subcommand}");
                output.Dim("Available subcommands: upload, list, info, delete, binary");
                break;
        }
    }

    /// <summary>
    /// Handles dumps list subcommand.
    /// </summary>
    private static async Task HandleDumpsListAsync(
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient)
    {
        try
        {
            var dumps = await output.WithSpinnerAsync(
                "Fetching dumps...",
                () => httpClient.ListDumpsAsync(state.Settings.UserId));

            if (dumps.Count == 0)
            {
                output.Info("No dumps found.");
                return;
            }

            output.Header($"Dumps ({dumps.Count} total)");
            output.WriteLine();
            output.Dim("Tip: Use partial IDs (like Docker) - e.g., 'dumps info bff4' or 'dumps delete cd01'");
            output.WriteLine();

            // Create table with full ID column
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .AddColumn(new TableColumn("[cyan]ID[/]"))
                .AddColumn(new TableColumn("[cyan]File Name[/]"))
                .AddColumn(new TableColumn("[cyan]Size[/]").RightAligned())
                .AddColumn(new TableColumn("[cyan]Runtime[/]"))
                .AddColumn(new TableColumn("[cyan]Arch[/]"))
                .AddColumn(new TableColumn("[cyan]Host[/]"))
                .AddColumn(new TableColumn("[cyan]Uploaded[/]"));

            // Check server info for compatibility warnings
            var serverIsAlpine = state.ServerInfo?.IsAlpine;
            var serverArch = state.ServerInfo?.Architecture;
            var hasIncompatibleDumps = false;
            var nowUtc = DateTime.UtcNow;

            output.CaptureOnly("ID\tFile Name\tSize\tRuntime\tArch\tHost\tUploaded");

            foreach (var dump in dumps)
            {
                // Show full ID for easy copying, highlight first 8 chars for partial matching
                var fullId = dump.DumpId;
                var displayId = fullId.Length > 8 
                    ? $"[yellow]{fullId[..8]}[/]{fullId[8..]}"
                    : $"[yellow]{fullId}[/]";
                
                // Check for host compatibility (Alpine vs glibc)
                var isHostIncompatible = false;
                if (dump.IsAlpineDump.HasValue && serverIsAlpine.HasValue)
                {
                    isHostIncompatible = dump.IsAlpineDump.Value != serverIsAlpine.Value;
                    if (isHostIncompatible) hasIncompatibleDumps = true;
                }
                
                // Check for architecture compatibility
                var isArchIncompatible = false;
                if (!string.IsNullOrEmpty(dump.Architecture) && !string.IsNullOrEmpty(serverArch))
                {
                    var normalizedDumpArch = NormalizeArchitecture(dump.Architecture);
                    var normalizedServerArch = NormalizeArchitecture(serverArch);
                    isArchIncompatible = !string.Equals(normalizedDumpArch, normalizedServerArch, StringComparison.OrdinalIgnoreCase);
                    if (isArchIncompatible) hasIncompatibleDumps = true;
                }
                
                // Show Alpine/glibc indicator with compatibility warning
                var hostType = dump.IsAlpineDump switch
                {
                    true when isHostIncompatible => "[red]Alpine ⚠[/]",
                    true => "[red]Alpine[/]",
                    false when isHostIncompatible => "[yellow]glibc ⚠[/]",
                    false => "glibc",
                    null => "-"
                };
                
                // Show runtime version (e.g., "9.0.10" -> ".NET 9.0")
                var runtime = "-";
                if (!string.IsNullOrEmpty(dump.RuntimeVersion))
                {
                    var parts = dump.RuntimeVersion.Split('.');
                    runtime = parts.Length >= 2 ? $".NET {parts[0]}.{parts[1]}" : $".NET {dump.RuntimeVersion}";
                }
                
                // Architecture with compatibility warning
                var arch = dump.Architecture ?? "-";
                if (isArchIncompatible && !string.IsNullOrEmpty(dump.Architecture))
                {
                    arch = $"[red]{dump.Architecture} ⚠[/]";
                }

                var transcriptHost = dump.IsAlpineDump switch
                {
                    true => "Alpine",
                    false => "glibc",
                    null => "-"
                };
                if (isHostIncompatible)
                {
                    transcriptHost += " (incompatible)";
                }

                var transcriptArch = dump.Architecture ?? "-";
                if (isArchIncompatible && !string.IsNullOrEmpty(dump.Architecture))
                {
                    transcriptArch += " (incompatible)";
                }

                var transcriptUploaded = dump.UploadedAt == default
                    ? "(unknown)"
                    : FormatUtcDateTimeWithAge(dump.UploadedAt, nowUtc, "yyyy-MM-dd HH:mm");

                output.CaptureOnly($"{dump.DumpId}\t{dump.FileName ?? "(unknown)"}\t{dump.FormattedSize}\t{runtime}\t{transcriptArch}\t{transcriptHost}\t{transcriptUploaded}");
                    
                table.AddRow(
                    displayId,
                    dump.FileName ?? "(unknown)",
                    dump.FormattedSize,
                    runtime,
                    arch,
                    hostType,
                    dump.UploadedAt == default 
                        ? "(unknown)" 
                        : FormatUtcDateTimeWithAge(dump.UploadedAt, nowUtc, "yyyy-MM-dd HH:mm"));
            }

            console.Write(table);
            
            // Show warning note if there are incompatible dumps
            if (hasIncompatibleDumps && state.ServerInfo != null)
            {
                output.WriteLine();
                var serverDesc = $"{(state.ServerInfo.IsAlpine ? "Alpine" : "glibc")} {state.ServerInfo.Architecture}";
                output.Warning($"⚠ Dumps marked with ⚠ are incompatible with this server ({serverDesc})");
                output.Dim("   Connect to a compatible server to debug those dumps.");
            }
        }
        catch (HttpApiException ex)
        {
            output.Error($"Failed to list dumps: {ex.Message}");
        }
        catch (Exception ex)
        {
            output.Error($"Failed to list dumps: {ex.Message}");
            if (state.Settings.Verbose)
            {
                console.WriteException(ex);
            }
        }
    }

    /// <summary>
    /// Resolves a partial dump ID to a full dump ID (Docker-style).
    /// </summary>
    /// <returns>The full dump ID, or null if not found or ambiguous.</returns>
    private static async Task<string?> ResolvePartialDumpIdAsync(
        string partialId,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient)
    {
        // If it looks like a full GUID, use it directly
        if (Guid.TryParse(partialId, out _))
        {
            return partialId;
        }

        // Get all dumps for the user
        var dumps = await httpClient.ListDumpsAsync(state.Settings.UserId);

        if (dumps.Count == 0)
        {
            output.Error("No dumps found.");
            return null;
        }

        // Find dumps that match the partial ID (case-insensitive)
        var matchingDumps = dumps
            .Where(d => d.DumpId.StartsWith(partialId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingDumps.Count == 0)
        {
            output.Error($"No dump found matching '{partialId}'");
            output.Dim("Use 'dumps list' to see available dumps.");
            return null;
        }

        if (matchingDumps.Count > 1)
        {
            output.Error($"Ambiguous dump ID '{partialId}' - matches {matchingDumps.Count} dumps:");
            foreach (var dump in matchingDumps.Take(5))
            {
                output.Dim($"  - {dump.DumpId}");
            }
            if (matchingDumps.Count > 5)
            {
                output.Dim($"  ... and {matchingDumps.Count - 5} more");
            }
            output.Dim("Please provide a more specific ID.");
            return null;
        }

        return matchingDumps[0].DumpId;
    }

    /// <summary>
    /// Handles dumps info subcommand.
    /// </summary>
    private static async Task HandleDumpsInfoAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient)
    {
        var partialDumpId = args.Length > 0 ? args[0] : (state.SelectedDumpId ?? state.DumpId);

        if (string.IsNullOrEmpty(partialDumpId))
        {
            output.Error("Dump ID required. Usage: dumps info <dumpId>");
            output.Dim("Tip: You can use partial IDs - e.g., 'dumps info bff4'");
            return;
        }

        try
        {
            // Resolve partial ID
            var dumpId = await ResolvePartialDumpIdAsync(partialDumpId, output, state, httpClient);
            if (dumpId == null)
            {
                return; // Error already shown
            }

            var dump = await output.WithSpinnerAsync(
                "Fetching dump info...",
                () => httpClient.GetDumpInfoAsync(state.Settings.UserId, dumpId));

            output.Header("Dump Information");
            output.WriteLine();

            output.KeyValue("Dump ID", dump.DumpId);
            output.KeyValue("User ID", dump.UserId);
            output.KeyValue("File Name", dump.FileName ?? "(unknown)");
            output.KeyValue("Size", dump.FormattedSize);
            output.KeyValue("Format", dump.DumpFormat ?? "(unknown)");
            
            // Show runtime version
            if (!string.IsNullOrEmpty(dump.RuntimeVersion))
            {
                output.KeyValue("Runtime", $".NET {dump.RuntimeVersion}");
            }
            
            // Show architecture
            if (!string.IsNullOrEmpty(dump.Architecture))
            {
                output.KeyValue("Architecture", dump.Architecture);
            }
            
            // Show Alpine/glibc host type
            if (dump.IsAlpineDump.HasValue)
            {
                if (dump.IsAlpineDump.Value)
                {
                    output.KeyValue("Host Type", "Alpine Linux (musl)");
                }
                else
                {
                    output.KeyValue("Host Type", "glibc (Debian/Ubuntu/etc.)");
                }
                
                // Check for host mismatch with server
                CheckDumpServerCompatibility(dump.IsAlpineDump, dump.Architecture, state, output);
            }
            else if (!string.IsNullOrEmpty(dump.Architecture))
            {
                // Even if we don't know Alpine status, check architecture
                CheckDumpServerCompatibility(null, dump.Architecture, state, output);
            }

            // Show standalone app binary info
            if (dump.HasExecutable)
            {
                output.KeyValue("Executable", dump.ExecutableName ?? "(unknown)");
                output.Dim("  Standalone app - binary will be used when opening dump");
            }
            
            output.KeyValue("Uploaded At", dump.UploadedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));

            if (!string.IsNullOrEmpty(dump.Description))
            {
                output.KeyValue("Description", dump.Description);
            }
        }
        catch (HttpApiException ex)
        {
            output.Error($"Failed to get dump info: {ex.Message}");
        }
        catch (Exception ex)
        {
            output.Error($"Failed to get dump info: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles dumps delete subcommand.
    /// </summary>
    private static async Task HandleDumpsDeleteAsync(
        string[] args,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient)
    {
        if (args.Length == 0)
        {
            output.Error("Dump ID required. Usage: dumps delete <dumpId>");
            output.Dim("Tip: You can use partial IDs - e.g., 'dumps delete bff4'");
            return;
        }

        var partialDumpId = args[0];

        // Resolve partial ID
        var dumpId = await ResolvePartialDumpIdAsync(partialDumpId, output, state, httpClient);
        if (dumpId == null)
        {
            return; // Error already shown
        }

        // Confirm deletion (show full ID for clarity)
        var confirm = console.Confirm($"Are you sure you want to delete dump [yellow]{dumpId}[/]?", defaultValue: false);

        if (!confirm)
        {
            output.Dim("Deletion cancelled.");
            return;
        }

        try
        {
            await output.WithSpinnerAsync(
                "Deleting dump...",
                async () =>
                {
                    await httpClient.DeleteDumpAsync(state.Settings.UserId, dumpId);
                    return true;
                });

            output.Success($"Dump {dumpId} deleted successfully.");

            // Clear dump ID from state if it matches
            if (state.DumpId == dumpId)
            {
                state.ClearDump();
            }

            if (state.SelectedDumpId == dumpId)
            {
                state.ClearSelectedDump();
            }
        }
        catch (HttpApiException ex)
        {
            output.Error($"Failed to delete dump: {ex.Message}");
        }
        catch (Exception ex)
        {
            output.Error($"Failed to delete dump: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles dumps binary upload subcommand.
    /// Uploads an executable binary for standalone .NET apps.
    /// </summary>
    private static async Task HandleDumpsBinaryUploadAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient)
    {
        // Parse arguments: dumps binary <dumpId> <binary-path>
        // Also accept: dumps binary upload <dumpId> <binary-path>
        var argList = args.ToList();
        
        // Skip "upload" if present (e.g., "dumps binary upload <dumpId> <path>")
        if (argList.Count > 0 && argList[0].Equals("upload", StringComparison.OrdinalIgnoreCase))
        {
            argList.RemoveAt(0);
        }

        if (argList.Count < 2)
        {
            output.Error("Usage: dumps binary <dumpId> <binary-path>");
            output.Dim("For standalone .NET apps, upload the original executable to enable proper debugging.");
            output.WriteLine();
            output.Markup("[bold]EXAMPLES[/]");
            output.Markup("  [yellow]dumps binary abc123 ./MyApp[/]");
            output.Dim("    Upload executable for dump abc123");
            output.Markup("  [yellow]dumps binary bff4 /path/to/MyStandaloneApp[/]");
            output.Dim("    Use partial dump ID and full path");
            return;
        }

        var partialDumpId = argList[0];
        var binaryPath = argList[1];

        // Validate binary file exists
        if (!File.Exists(binaryPath))
        {
            output.Error($"Binary file not found: {binaryPath}");
            return;
        }

        // Resolve partial dump ID
        var dumpId = await ResolvePartialDumpIdAsync(partialDumpId, output, state, httpClient);
        if (dumpId == null)
        {
            return; // Error already shown
        }

        try
        {
            var fileName = Path.GetFileName(binaryPath);
            var fileSize = new FileInfo(binaryPath).Length;
            
            output.Info($"Uploading binary '{fileName}' ({fileSize / 1024.0:N1} KB) for dump {dumpId[..8]}...");

            var result = await output.WithSpinnerAsync(
                "Uploading binary...",
                () => httpClient.UploadDumpBinaryAsync(state.Settings.UserId, dumpId, binaryPath));

            if (result != null)
            {
                output.Success($"Binary uploaded successfully!");
                output.KeyValue("Dump ID", dumpId);
                output.KeyValue("Executable", fileName);
                output.KeyValue("Size", $"{fileSize / 1024.0:N1} KB");
                output.WriteLine();
                output.Dim("The binary will be used automatically when opening this dump.");
                output.Dim("Use 'dump open " + dumpId[..8] + "' to open the dump with the standalone binary.");
            }
            else
            {
                output.Error("Failed to upload binary");
            }
        }
        catch (HttpApiException ex)
        {
            output.Error($"Failed to upload binary: {ex.Message}");
        }
        catch (Exception ex)
        {
            output.Error($"Failed to upload binary: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the symbols command.
    /// </summary>
    private static async Task HandleSymbolsAsync(
        string[] args,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient,
        McpClient? mcpClient = null)
    {
        // Require connection
        if (!state.IsConnected)
        {
            output.Error("Not connected. Use 'connect <url>' first.");
            return;
        }

        if (args.Length == 0)
        {
            output.Error("Subcommand required. Usage: symbols <upload|list|servers|add|datadog>");
            return;
        }

        var subcommand = args[0].ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();

        switch (subcommand)
        {
            case "upload":
                await HandleSymbolsUploadAsync(subArgs, console, output, state, httpClient);
                break;

            case "list":
            case "ls":
                await HandleSymbolsListAsync(subArgs, console, output, state, httpClient);
                break;

            case "servers":
                await HandleSymbolServersAsync(output, mcpClient);
                break;

            case "add":
                await HandleSymbolAddAsync(subArgs, output, state, mcpClient);
                break;

            case "clear":
                await HandleSymbolClearAsync(subArgs, output, state, mcpClient, httpClient);
                break;

            case "reload":
                await HandleSymbolReloadAsync(output, state, mcpClient);
                break;

            case "datadog":
            case "dd":
                await HandleDatadogSymbolsAsync(subArgs, output, state, mcpClient);
                break;

            default:
                output.Error($"Unknown subcommand: {subcommand}");
                output.Dim("Available subcommands: upload, list, servers, add, clear, reload, datadog");
                break;
        }
    }

    /// <summary>
    /// Handles symbols upload subcommand with wildcard support.
    /// </summary>
    private static async Task HandleSymbolsUploadAsync(
        string[] args,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient)
    {
        var filePatterns = new List<string>();
        string? dumpId = state.SelectedDumpId ?? state.DumpId;

        // Parse arguments
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--dump-id" && i + 1 < args.Length)
            {
                dumpId = args[++i];
            }
            else if (!args[i].StartsWith("-"))
            {
                filePatterns.Add(args[i]);
            }
        }

        // Validate
        if (filePatterns.Count == 0)
        {
            output.Error("File path required. Usage: symbols upload <file|pattern> [--dump-id <id>]");
            output.Dim("Supports wildcards: *.pdb, **/*.pdb, ./bin/*.pdb");
            return;
        }

        if (string.IsNullOrEmpty(dumpId))
        {
            output.Error("Dump ID required. Use --dump-id or upload a dump first.");
            output.Dim("Tip: You can use partial IDs - e.g., '--dump-id bff4'");
            return;
        }

        // Resolve partial dump ID to full ID
        var resolvedDumpId = await ResolvePartialDumpIdAsync(dumpId, output, state, httpClient);
        if (resolvedDumpId == null)
        {
            return; // Error already shown
        }

        // Check if any pattern has wildcards
        var hasWildcards = filePatterns.Any(p => p.Contains('*') || p.Contains('?'));

        if (!hasWildcards && filePatterns.Count == 1)
        {
            // Single file upload (with progress bar)
            await HandleSingleSymbolUploadAsync(filePatterns[0], resolvedDumpId, console, output, state, httpClient);
        }
        else
        {
            // Batch upload (with wildcards)
            await HandleBatchSymbolUploadAsync(filePatterns, resolvedDumpId, console, output, state, httpClient);
        }
    }

    /// <summary>
    /// Handles single symbol file upload with progress bar.
    /// </summary>
    private static async Task HandleSingleSymbolUploadAsync(
        string filePath,
        string dumpId,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient)
    {
        // Expand path
        filePath = Path.GetFullPath(filePath);

        if (!File.Exists(filePath))
        {
            output.Error($"File not found: {filePath}");
            return;
        }

        var fileInfo = new FileInfo(filePath);
        var fileName = fileInfo.Name;
        var fileSize = fileInfo.Length;
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        // Check if it's a ZIP file
        if (extension == ".zip")
        {
            await HandleZipSymbolUploadAsync(filePath, dumpId, console, output, state, httpClient);
            return;
        }

        output.Markup($"[blue]ℹ[/] Uploading symbol [cyan]{fileName}[/] ({FormatBytes(fileSize)})...");

        try
        {
            var progressRenderer = new ProgressRenderer(console);

            var result = await progressRenderer.WithUploadProgressAsync(
                fileName,
                fileSize,
                progress => httpClient.UploadSymbolAsync(filePath, dumpId, progress));

            output.Success("Symbol uploaded successfully!");
            output.KeyValue("File Name", result.FileName);
            output.KeyValue("Size", result.FormattedSize);

            if (!string.IsNullOrEmpty(result.SymbolFormat))
            {
                output.KeyValue("Format", result.SymbolFormat);
            }
        }
        catch (HttpApiException ex)
        {
            output.Error($"Upload failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            output.Error($"Upload failed: {ex.Message}");
            if (state.Settings.Verbose)
            {
                console.WriteException(ex);
            }
        }
    }

    /// <summary>
    /// Handles ZIP symbol file upload with extraction on server.
    /// </summary>
    private static async Task HandleZipSymbolUploadAsync(
        string zipPath,
        string dumpId,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient)
    {
        var fileInfo = new FileInfo(zipPath);
        var fileName = fileInfo.Name;
        var fileSize = fileInfo.Length;

        output.Markup($"[blue]ℹ[/] Uploading symbol ZIP [cyan]{fileName}[/] ({FormatBytes(fileSize)})...");
        output.Dim("The server will extract the ZIP and preserve directory structure.");

        try
        {
            var progressRenderer = new ProgressRenderer(console);

            var result = await progressRenderer.WithUploadProgressAsync(
                fileName,
                fileSize,
                progress => httpClient.UploadSymbolZipAsync(zipPath, dumpId, progress));

            output.Success("Symbol ZIP uploaded and extracted!");
            output.KeyValue("Extracted Files", result.ExtractedFilesCount.ToString());
            output.KeyValue("Symbol Files", result.SymbolFilesCount.ToString());
            output.KeyValue("Directories", result.SymbolDirectoriesCount.ToString());

            if (result.SymbolDirectories.Count > 0)
            {
                output.WriteLine();
                output.Dim("Symbol directories:");
                foreach (var dir in result.SymbolDirectories.Take(10))
                {
                    output.Markup($"  [dim]📁[/] {dir}");
                }
                if (result.SymbolDirectories.Count > 10)
                {
                    output.Dim($"  ... and {result.SymbolDirectories.Count - 10} more");
                }
            }

            output.WriteLine();
            output.Markup("[blue]ℹ[/] 💡 If a dump is already open, use [yellow]symbols reload[/] to load the new symbols.");
        }
        catch (HttpApiException ex)
        {
            output.Error($"Upload failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            output.Error($"Upload failed: {ex.Message}");
            if (state.Settings.Verbose)
            {
                console.WriteException(ex);
            }
        }
    }

    /// <summary>
    /// Handles batch symbol file upload with wildcard support.
    /// </summary>
    private static async Task HandleBatchSymbolUploadAsync(
        List<string> filePatterns,
        string dumpId,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient)
    {
        output.Info($"Searching for symbol files matching: {string.Join(", ", filePatterns)}");

        try
        {
            var batchResult = await console.Progress()
                .AutoRefresh(true)
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    ProgressTask? task = null;

                    var result = await httpClient.UploadSymbolsBatchAsync(
                        filePatterns,
                        dumpId,
                        (current, total, fileName) =>
                        {
                            if (task == null)
                            {
                                task = ctx.AddTask($"[cyan]Uploading symbols[/]");
                                task.MaxValue = total;
                            }

                            task.Value = current;
                            task.Description = $"[cyan]Uploading[/] {fileName} ({current}/{total})";
                        });

                    if (task != null)
                    {
                        task.Value = task.MaxValue;
                        task.Description = $"[green]✓ Uploaded {result.SuccessfulUploads} symbols[/]";
                    }

                    return result;
                });

            // Display results
            if (batchResult.TotalFiles == 0)
            {
                output.Warning("No matching symbol files found.");
                output.Dim("Supported extensions: .pdb, .dbg, .so, .dylib, .dll, .exe, .sym");
                return;
            }

            output.WriteLine();
            output.Header($"Batch Upload Complete ({batchResult.TotalFiles} files)");
            output.WriteLine();

            output.KeyValue("Total Files", batchResult.TotalFiles.ToString());
            output.KeyValue("Successful", $"[green]{batchResult.SuccessfulUploads}[/]");

            if (batchResult.FailedUploads > 0)
            {
                output.KeyValue("Failed", $"[red]{batchResult.FailedUploads}[/]");
            }

            // Show failed uploads
            var failedResults = batchResult.Results.Where(r => !r.Success).ToList();
            if (failedResults.Count > 0)
            {
                output.WriteLine();
                output.Markup("[yellow]Failed uploads:[/]");
                foreach (var failed in failedResults)
                {
                    output.Markup($"  [red]✗[/] {failed.FileName}: {failed.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            output.Error($"Batch upload failed: {ex.Message}");
            if (state.Settings.Verbose)
            {
                console.WriteException(ex);
            }
        }
    }

    /// <summary>
    /// Handles symbols list subcommand.
    /// </summary>
    private static async Task HandleSymbolsListAsync(
        string[] args,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient)
    {
        string? dumpId = state.SelectedDumpId ?? state.DumpId;

        // Parse arguments
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--dump-id" && i + 1 < args.Length)
            {
                dumpId = args[++i];
            }
        }

        if (string.IsNullOrEmpty(dumpId))
        {
            output.Error("Dump ID required. Use --dump-id or set a dump first.");
            output.Dim("Tip: You can use partial IDs - e.g., '--dump-id bff4'");
            return;
        }

        // Resolve partial dump ID to full ID
        var resolvedDumpId = await ResolvePartialDumpIdAsync(dumpId, output, state, httpClient);
        if (resolvedDumpId == null)
        {
            return; // Error already shown
        }

        try
        {
            var result = await output.WithSpinnerAsync(
                "Fetching symbols...",
                () => httpClient.ListSymbolsAsync(resolvedDumpId));

            if (result.Symbols.Count == 0)
            {
                output.Info($"No symbols found for dump {resolvedDumpId}.");
                return;
            }

            output.Header($"Symbols for dump {resolvedDumpId}");
            output.WriteLine();

            // Build and display tree view
            var tree = BuildSymbolTree(result.Symbols, resolvedDumpId);
            console.Write(tree);
            
            output.WriteLine();
            output.Dim($"Total: {result.Symbols.Count} files");
        }
        catch (HttpApiException ex)
        {
            output.Error($"Failed to list symbols: {ex.Message}");
        }
        catch (Exception ex)
        {
            output.Error($"Failed to list symbols: {ex.Message}");
            if (state.Settings.Verbose)
            {
                console.WriteException(ex);
            }
        }
    }
    
    /// <summary>
    /// Builds a tree view from a list of symbol file paths.
    /// </summary>
    private static Tree BuildSymbolTree(List<string> symbols, string rootLabel)
    {
        var tree = new Tree($"[yellow]📁 .symbols_{rootLabel.Split('-')[0]}[/]");
        
        // Build directory structure
        var rootNode = new Dictionary<string, object>();
        
        foreach (var symbol in symbols.OrderBy(s => s))
        {
            // Normalize path separators
            var normalizedPath = symbol.Replace('\\', '/');
            var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            
            var currentNode = rootNode;
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                var isFile = i == parts.Length - 1;
                
                if (isFile)
                {
                    // Store file as a string marker
                    currentNode[part] = "FILE";
                }
                else
                {
                    // Create or get directory
                    if (!currentNode.TryGetValue(part, out var existing) || existing is string)
                    {
                        currentNode[part] = new Dictionary<string, object>();
                    }
                    currentNode = (Dictionary<string, object>)currentNode[part];
                }
            }
        }
        
        // Convert to Spectre.Console tree
        AddNodesToTree(tree, rootNode);
        
        return tree;
    }
    
    /// <summary>
    /// Recursively adds nodes to a Spectre.Console tree.
    /// </summary>
    private static void AddNodesToTree(IHasTreeNodes parentNode, Dictionary<string, object> nodes)
    {
        // Sort: directories first, then files
        var sortedKeys = nodes.Keys
            .OrderBy(k => nodes[k] is string ? 1 : 0) // Directories first
            .ThenBy(k => k);
        
        foreach (var key in sortedKeys)
        {
            var value = nodes[key];
            
            if (value is string) // File
            {
                var icon = GetFileIcon(key);
                parentNode.AddNode($"{icon} [white]{key}[/]");
            }
            else if (value is Dictionary<string, object> children) // Directory
            {
                var dirNode = parentNode.AddNode($"[blue]📁 {key}[/]");
                AddNodesToTree(dirNode, children);
            }
        }
    }
    
    /// <summary>
    /// Gets an appropriate icon for a file based on its extension.
    /// </summary>
    private static string GetFileIcon(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdb" => "[green]🔧[/]",      // Debug symbols
            ".dll" => "[cyan]📦[/]",        // Assembly
            ".exe" => "[magenta]⚙️[/]",     // Executable
            ".so" => "[cyan]📦[/]",         // Linux shared library
            ".dylib" => "[cyan]📦[/]",      // macOS shared library
            ".dbg" => "[green]🔧[/]",       // Linux debug symbols
            ".dwarf" => "[green]🔧[/]",     // DWARF debug info
            ".dSYM" => "[green]🔧[/]",      // macOS debug symbols
            ".json" => "[yellow]📄[/]",     // JSON config
            ".xml" => "[yellow]📄[/]",      // XML config
            _ => "[dim]📄[/]"               // Generic file
        };
    }

    /// <summary>
    /// Handles the symbols servers subcommand (shows common symbol servers).
    /// </summary>
    private static async Task HandleSymbolServersAsync(
        ConsoleOutput output,
        McpClient? mcpClient)
    {
        if (mcpClient == null || !mcpClient.IsConnected)
        {
            output.Error("MCP not connected. Connect to a server first.");
            return;
        }

        try
        {
            var result = await output.WithSpinnerAsync(
                "Fetching symbol servers...",
                () => mcpClient.GetSymbolServersAsync());

            output.Header("Common Symbol Servers");
            output.WriteLine();
            output.WriteLine(result);
        }
        catch (McpClientException ex)
        {
            output.Error(ex.Message);
        }
        catch (Exception ex)
        {
            output.Error($"Failed to get symbol servers: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the symbols add subcommand (adds symbol server URL to session).
    /// </summary>
    private static async Task HandleSymbolAddAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        McpClient? mcpClient)
    {
        if (mcpClient == null || !mcpClient.IsConnected)
        {
            output.Error("MCP not connected. Connect to a server first.");
            return;
        }

        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Use 'session create' or 'open <dumpId>' first.");
            return;
        }

        if (args.Length == 0)
        {
            output.Error("Symbol server URL required. Usage: symbols add <url>");
            output.Dim("Example: symbols add https://msdl.microsoft.com/download/symbols");
            output.Dim("Example: symbols add https://nuget.smbsrc.net");
            output.Dim("");
            output.Dim("Use 'symbols servers' to see common symbol servers.");
            output.Dim("To upload local symbol files, use 'symbols upload <file>'.");
            return;
        }

        var symbolPath = args[0];

        // Warn if it looks like a local path rather than a URL
        if (!symbolPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !symbolPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !symbolPath.StartsWith("srv*", StringComparison.OrdinalIgnoreCase))
        {
            output.Warning("This looks like a local path, not a URL.");
            output.Dim("Note: 'symbols add' configures symbol servers on the remote server.");
            output.Dim("Local paths only work if they exist on the server, not your machine.");
            output.Dim("");
            output.Dim("Did you mean to upload a local file? Use: symbols upload <file>");
            output.Dim("For symbol server URLs, use: symbols add https://...");
            output.WriteLine();
        }

        try
        {
            var result = await output.WithSpinnerAsync(
                "Adding symbol path...",
                () => mcpClient.ConfigureAdditionalSymbolsAsync(state.SessionId!, state.Settings.UserId, symbolPath));

            if (IsErrorResult(result))
            {
                output.Error(result);
            }
            else
            {
                output.Success("Symbol path added successfully!");
                output.WriteLine(result);
            }
        }
        catch (McpClientException ex)
        {
            output.Error($"Failed to add symbol path: {ex.Message}");
        }
        catch (Exception ex)
        {
            output.Error($"Failed to add symbol path: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the symbols clear subcommand (clears downloaded symbol cache for a dump).
    /// </summary>
    private static async Task HandleSymbolClearAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        McpClient? mcpClient,
        HttpApiClient httpClient)
    {
        if (mcpClient == null || !mcpClient.IsConnected)
        {
            output.Error("MCP not connected. Connect to a server first.");
            return;
        }

        // Get dump ID from args or current state
        string? dumpId = args.Length > 0 ? args[0] : (state.SelectedDumpId ?? state.DumpId);

        if (string.IsNullOrEmpty(dumpId))
        {
            output.Error("Dump ID required. Usage: symbols clear <dumpId>");
            output.Dim("Or open a dump first with 'open <dumpId>'.");
            return;
        }

        try
        {
            // Resolve partial dump ID if needed
            dumpId = await ResolvePartialDumpIdAsync(dumpId, output, state, httpClient);

            output.Dim($"Clearing symbol cache for dump {dumpId}...");
            output.Dim("This will force a fresh symbol download on next 'open' command.");
            output.WriteLine();

            var result = await output.WithSpinnerAsync(
                "Clearing symbol cache...",
                () => mcpClient.ClearSymbolCacheAsync(state.Settings.UserId, dumpId!));

            if (IsErrorResult(result))
            {
                output.Error(result);
            }
            else
            {
                output.Success("Symbol cache cleared!");
                output.WriteLine(result);
            }
        }
        catch (McpClientException ex)
        {
            output.Error($"Failed to clear symbol cache: {ex.Message}");
        }
        catch (Exception ex)
        {
            output.Error($"Failed to clear symbol cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the symbols reload subcommand (reloads symbols into running debugger session).
    /// </summary>
    private static async Task HandleSymbolReloadAsync(
        ConsoleOutput output,
        ShellState state,
        McpClient? mcpClient)
    {
        if (mcpClient == null || !mcpClient.IsConnected)
        {
            output.Error("MCP not connected. Connect to a server first.");
            return;
        }

        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Open a dump first with 'open <dumpId>'.");
            return;
        }

        if (string.IsNullOrEmpty(state.DumpId))
        {
            output.Error("No dump is open. Open a dump first with 'open <dumpId>'.");
            return;
        }

        output.Info("Reloading symbols into debugger session...");
        output.Dim("This adds symbol directories to search paths and loads .dbg/.pdb files.");

        try
        {
            var result = await output.WithSpinnerAsync(
                "Reloading symbols...",
                () => mcpClient.ReloadSymbolsAsync(state.SessionId!, state.Settings.UserId));

            if (IsErrorResult(result))
            {
                output.Error(result);
            }
            else
            {
                output.Success("Symbols reloaded!");
                output.WriteLine(result);
            }
        }
        catch (McpClientException ex)
        {
            output.Error($"Failed to reload symbols: {ex.Message}");
        }
        catch (Exception ex)
        {
            output.Error($"Failed to reload symbols: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the symbols datadog subcommand for downloading Datadog.Trace symbols.
    /// </summary>
    private static async Task HandleDatadogSymbolsAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        McpClient? mcpClient)
    {
        if (mcpClient == null || !mcpClient.IsConnected)
        {
            output.Error("MCP not connected. Connect to a server first.");
            return;
        }

        // Show help if no subcommand provided
        if (args.Length == 0)
        {
            ShowDatadogSymbolsHelp(output);
            return;
        }

        // Parse subcommand
        var subcommand = args[0].ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();

        switch (subcommand)
        {
            case "download":
            case "dl":
                await HandleDatadogDownloadAsync(subArgs, output, state, mcpClient);
                break;

            case "list":
            case "ls":
                await HandleDatadogListAsync(subArgs, output, mcpClient);
                break;

            case "config":
            case "status":
                await HandleDatadogConfigAsync(output, mcpClient);
                break;

            case "clear":
            case "clean":
                await HandleDatadogClearAsync(subArgs, output, state, mcpClient);
                break;

            case "help":
            case "?":
                ShowDatadogSymbolsHelp(output);
                break;

            default:
                // If it looks like a commit SHA, treat it as download
                if (subcommand.Length >= 7 && subcommand.All(c => "0123456789abcdef".Contains(c)))
                {
                    await HandleDatadogDownloadAsync(new[] { subcommand }.Concat(subArgs).ToArray(), output, state, mcpClient);
                }
                else
                {
                    output.Error($"Unknown datadog subcommand: {subcommand}");
                    ShowDatadogSymbolsHelp(output);
                }
                break;
        }
    }

    /// <summary>
    /// Shows help for the symbols datadog subcommand.
    /// </summary>
    private static void ShowDatadogSymbolsHelp(ConsoleOutput output)
    {
        output.Header("Datadog Symbols - Download debug symbols from Azure Pipelines");
        output.WriteLine();

        output.Markup("[bold]DESCRIPTION[/]");
        output.Dim("  Download debug symbols for Datadog.Trace and related assemblies from Azure Pipelines.");
        output.Dim("  Symbols enable detailed stack traces in crash dumps containing Datadog libraries.");
        output.WriteLine();

        output.Markup("[bold]SUBCOMMANDS[/]");
        output.Markup("  [green]download[/]              Auto-detect assemblies from dump and download symbols");
        output.Markup("  [green]download <sha>[/]        Download symbols for a specific commit SHA");
        output.Markup("  [green]list <sha>[/]            List available artifacts for a commit SHA");
        output.Markup("  [green]clear[/]                 Clear downloaded symbols for current dump");
        output.Markup("  [green]config[/]                Show configuration and status");
        output.Markup("  [green]help[/]                  Show this help");
        output.WriteLine();

        output.Markup("[bold]OPTIONS (download)[/]");
        output.Markup("  [cyan]--tfm <framework>[/]     Target framework (e.g., net6.0, netcoreapp3.1)");
        output.Markup("  [cyan]--force-version, -f[/]   Enable version/tag fallback if exact SHA not found");
        output.Markup("  [cyan]--build-id, -b <id>[/]   Download directly from Azure Pipelines build ID");
        output.Markup("  [cyan]--no-load[/]             Download only, don't load symbols into debugger");
        output.WriteLine();
        output.Markup("[bold]OPTIONS (clear)[/]");
        output.Markup("  [cyan]--all, -a[/]             Also clear API caches (build/release lookups)");
        output.WriteLine();

        output.Markup("[bold]EXAMPLES[/]");
        output.Markup("  [yellow]symbols datadog download[/]");
        output.Dim("    Auto-detect Datadog assemblies and download symbols (SHA match only)");
        output.WriteLine();
        output.Markup("  [yellow]symbols datadog download --force-version[/]");
        output.Dim("    Auto-detect and download, fallback to version tag if SHA not found");
        output.WriteLine();
        output.Markup("  [yellow]symbols datadog download 14fd3a2f[/]");
        output.Dim("    Download symbols for commit 14fd3a2f");
        output.WriteLine();
        output.Markup("  [yellow]symbols datadog download --build-id 192179[/]");
        output.Dim("    Download symbols directly from Azure Pipelines build 192179");
        output.WriteLine();
        output.Markup("  [yellow]symbols datadog list 14fd3a2f[/]");
        output.Dim("    List available artifacts for commit 14fd3a2f");
        output.WriteLine();
        output.Markup("  [yellow]symbols datadog config[/]");
        output.Dim("    Show current configuration and status");
        output.WriteLine();
        output.Markup("  [yellow]symbols datadog clear[/]");
        output.Dim("    Remove all downloaded Datadog symbols for current dump");
        output.WriteLine();
        output.Markup("  [yellow]symbols datadog clear --all[/]");
        output.Dim("    Clear symbols and API caches (force fresh lookup)");
        output.WriteLine();

        output.Markup("[bold]ENVIRONMENT VARIABLES[/]");
        output.Markup("  [cyan]DATADOG_TRACE_SYMBOLS_ENABLED[/]         Enable/disable (default: true)");
        output.Markup("  [cyan]DATADOG_TRACE_SYMBOLS_PAT[/]             Azure DevOps PAT for private access");
        output.Markup("  [cyan]DATADOG_TRACE_SYMBOLS_CACHE_DIR[/]       Custom cache directory");
        output.Markup("  [cyan]DATADOG_TRACE_SYMBOLS_TIMEOUT_SECONDS[/] Download timeout (default: 300)");
    }

    /// <summary>
    /// Handles downloading Datadog symbols for a specific commit.
    /// </summary>
    private static async Task HandleDatadogDownloadAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Open a dump first with 'open <dumpId>'.");
            return;
        }

        // Parse arguments
        string? commitSha = null;
        string? targetFramework = null;
        var loadIntoDebugger = true;
        var forceVersion = false;
        int? buildId = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--tfm" && i + 1 < args.Length)
            {
                targetFramework = args[++i];
            }
            else if (args[i] == "--force-version" || args[i] == "-f")
            {
                forceVersion = true;
            }
            else if (args[i] == "--no-load")
            {
                loadIntoDebugger = false;
            }
            else if ((args[i] == "--build-id" || args[i] == "-b") && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var id))
                {
                    buildId = id;
                }
                else
                {
                    output.Error($"Invalid build ID: {args[i]}. Must be a number.");
                    return;
                }
            }
            else if (!args[i].StartsWith("-") && commitSha == null)
            {
                commitSha = args[i];
            }
        }

        // If no commit SHA provided AND no build ID, use auto-detection from dump
        // When build ID is provided, skip auto-detect and go directly to manual download
        if (string.IsNullOrEmpty(commitSha) && !buildId.HasValue)
        {
            output.Info("Auto-detecting Datadog assemblies from dump...");
            if (forceVersion)
            {
                output.Dim("Version fallback enabled - will try version/tag if exact SHA not found");
            }

            try
            {
                var result = await output.WithSpinnerAsync(
                    "Scanning dump and downloading Datadog symbols...",
                    () => mcpClient.PrepareDatadogSymbolsAsync(
                        state.SessionId!,
                        state.Settings.UserId,
                        loadIntoDebugger,
                        forceVersion));

                if (IsErrorResult(result))
                {
                    output.Error(result);
                    return;
                }

                // Pretty print the auto-detection result
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(result);
                    var root = doc.RootElement;

                    // Check if the operation succeeded
                    var isSuccess = root.TryGetProperty("success", out var successProp) &&
                                   successProp.ValueKind == System.Text.Json.JsonValueKind.True;

                    // Show detected assemblies
                    if (root.TryGetProperty("datadogAssemblies", out var assemblies) &&
                        assemblies.ValueKind == System.Text.Json.JsonValueKind.Array &&
                        assemblies.GetArrayLength() > 0)
                    {
                        output.Success($"Found {assemblies.GetArrayLength()} Datadog assemblies");
                        foreach (var asm in assemblies.EnumerateArray())
                        {
                            var name = asm.TryGetProperty("name", out var n) ? n.GetString() : "?";
                            var sha = asm.TryGetProperty("commitSha", out var s) ? s.GetString() : null;
                            output.Dim($"  {name} ({sha ?? "no commit SHA"})");
                        }
                    }

                    // If not successful, show error message and return
                    if (!isSuccess)
                    {
                        if (root.TryGetProperty("message", out var msg))
                        {
                            output.Error(msg.GetString() ?? "Symbol download failed");
                        }
                        else
                        {
                            output.Error("Symbol download failed");
                        }
                        return;
                    }

                    // Show download result
                    if (root.TryGetProperty("downloadResult", out var dl) &&
                        dl.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        output.Success("Symbols downloaded!");

                        // Show source (GitHub Releases vs Azure Pipelines)
                        var isGitHub = dl.TryGetProperty("buildUrl", out var urlProp) &&
                                      urlProp.GetString()?.Contains("github.com") == true;

                        if (dl.TryGetProperty("buildNumber", out var buildNum))
                            output.KeyValue(isGitHub ? "Release" : "Build", buildNum.GetString() ?? "");

                        if (dl.TryGetProperty("buildId", out var dlBuildId) &&
                            dlBuildId.ValueKind == System.Text.Json.JsonValueKind.Number)
                            output.KeyValue("Build ID", dlBuildId.GetInt32().ToString());

                        if (dl.TryGetProperty("downloadedArtifacts", out var artifacts) &&
                            artifacts.ValueKind == System.Text.Json.JsonValueKind.Array)
                            output.KeyValue("Artifacts", artifacts.GetArrayLength().ToString());

                        // Only show files extracted if > 0 (can be 0 when cached)
                        if (dl.TryGetProperty("filesExtracted", out var files) &&
                            files.ValueKind == System.Text.Json.JsonValueKind.Number &&
                            files.GetInt32() > 0)
                            output.KeyValue("Files Extracted", files.GetInt32().ToString());

                        // Show source URL
                        if (urlProp.ValueKind == System.Text.Json.JsonValueKind.String)
                            output.Dim($"Source: {urlProp.GetString()}");
                    }

                    // Show load result
                    if (root.TryGetProperty("symbolsLoaded", out var loaded) &&
                        loaded.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        output.WriteLine();
                        output.Markup("[bold]Symbol Loading:[/]");
                        if (loaded.TryGetProperty("nativeSymbolsLoaded", out var native) &&
                            native.ValueKind == System.Text.Json.JsonValueKind.Number)
                            output.KeyValue("Native Symbols", native.GetInt32().ToString());
                        if (loaded.TryGetProperty("managedSymbolPaths", out var managed) &&
                            managed.ValueKind == System.Text.Json.JsonValueKind.Number)
                            output.KeyValue("Managed Symbol Paths", managed.GetInt32().ToString());
                    }

                    // Show SHA mismatch warning if we fell back from commit to version
                    if (root.TryGetProperty("downloadResult", out var dlResult) &&
                        dlResult.TryGetProperty("shaMismatch", out var shaMismatch) &&
                        shaMismatch.ValueKind == System.Text.Json.JsonValueKind.True)
                    {
                        output.WriteLine();
                        output.Warning("Note: Exact commit SHA not found - downloaded symbols by version tag.");
                        output.Dim("  The symbols should match, but are from a release build rather than the exact commit.");
                    }

                    // Show PDB patching results if any
                    if (root.TryGetProperty("pdbsPatched", out var pdbsPatched) &&
                        pdbsPatched.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        var patchedCount = pdbsPatched.TryGetProperty("patched", out var patched)
                            ? patched.GetInt32() : 0;
                        var verifiedCount = pdbsPatched.TryGetProperty("verified", out var verified)
                            ? verified.GetInt32() : 0;

                        if (patchedCount > 0)
                        {
                            output.WriteLine();
                            if (verifiedCount == patchedCount)
                            {
                                output.Success($"⚙ Patched and verified {patchedCount} PDB file(s) to match dump module GUIDs:");
                            }
                            else if (verifiedCount > 0)
                            {
                                output.Warning($"⚙ Patched {patchedCount} PDB file(s), but only {verifiedCount} verified successfully:");
                            }
                            else
                            {
                                output.Error($"⚙ Patched {patchedCount} PDB file(s), but verification FAILED:");
                            }

                            if (pdbsPatched.TryGetProperty("files", out var patchedFiles) &&
                                patchedFiles.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var patchedFile in patchedFiles.EnumerateArray())
                                {
                                    var fileName = patchedFile.TryGetProperty("file", out var f)
                                        ? f.GetString() : "unknown";
                                    var fileVerified = patchedFile.TryGetProperty("verified", out var fv) && fv.GetBoolean();
                                    var checkmark = fileVerified ? "✓" : "✗";
                                    output.Dim($"  {checkmark} {fileName}");
                                }
                            }

                            if (verifiedCount == patchedCount)
                            {
                                output.Dim("  This allows SOS to load symbols despite the version mismatch.");
                            }
                            else
                            {
                                output.Error("  Symbol loading may not work correctly. Try clearing symbols and re-downloading.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Show error but don't dump raw JSON
                    output.Error($"Error parsing result: {ex.Message}");
                }

                return;
            }
            catch (McpClientException ex)
            {
                output.Error($"Failed to download Datadog symbols: {ex.Message}");
                return;
            }
            catch (Exception ex)
            {
                output.Error($"Failed to download Datadog symbols: {ex.Message}");
                return;
            }
        }

        // Manual mode with explicit commit SHA or build ID
        if (buildId.HasValue)
        {
            output.Info($"Downloading Datadog symbols from Azure Pipelines build {buildId}...");
            // When using build ID, commit SHA is not required - use a placeholder
            commitSha ??= "direct-build-download";
        }
        else if (!string.IsNullOrEmpty(commitSha))
        {
            output.Info($"Downloading Datadog symbols for commit {commitSha[..Math.Min(8, commitSha.Length)]}...");
        }
        
        if (forceVersion)
        {
            output.Dim("Version fallback enabled - will try version/tag if exact SHA not found");
        }
        if (!string.IsNullOrEmpty(targetFramework))
        {
            output.Dim($"Target framework: {targetFramework}");
        }

        try
        {
            var result = await output.WithSpinnerAsync(
                "Downloading Datadog symbols...",
                () => mcpClient.DownloadDatadogSymbolsAsync(
                    state.SessionId!,
                    state.Settings.UserId,
                    commitSha!,
                    targetFramework,
                    loadIntoDebugger,
                    forceVersion,
                    version: null,
                    buildId: buildId));

            if (IsErrorResult(result))
            {
                output.Error(result);
            }
            else
            {
                output.Success("Datadog symbols downloaded!");

                // Pretty print the JSON result
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(result);
                    var root = doc.RootElement;

                    // Determine source type
                    var isGitHub = root.TryGetProperty("buildUrl", out var urlProp) &&
                                  urlProp.GetString()?.Contains("github.com") == true;

                    if (root.TryGetProperty("buildNumber", out var buildNumElem))
                        output.KeyValue(isGitHub ? "Release" : "Build", buildNumElem.GetString() ?? "");

                    if (root.TryGetProperty("buildId", out var buildIdElem) &&
                        buildIdElem.ValueKind == System.Text.Json.JsonValueKind.Number)
                        output.KeyValue("Build ID", buildIdElem.GetInt32().ToString());

                    if (root.TryGetProperty("downloadedArtifacts", out var artifacts) &&
                        artifacts.ValueKind == System.Text.Json.JsonValueKind.Array)
                        output.KeyValue("Artifacts", artifacts.GetArrayLength().ToString());

                    // Only show files extracted if > 0 (can be 0 when cached)
                    if (root.TryGetProperty("filesExtracted", out var files) &&
                        files.ValueKind == System.Text.Json.JsonValueKind.Number &&
                        files.GetInt32() > 0)
                        output.KeyValue("Files Extracted", files.GetInt32().ToString());

                    if (root.TryGetProperty("symbolsLoaded", out var loaded) &&
                        loaded.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        if (loaded.TryGetProperty("nativeSymbolsLoaded", out var native) &&
                            native.ValueKind == System.Text.Json.JsonValueKind.Number)
                            output.KeyValue("Native Symbols", native.GetInt32().ToString());
                        if (loaded.TryGetProperty("managedSymbolPaths", out var managed) &&
                            managed.ValueKind == System.Text.Json.JsonValueKind.Number)
                            output.KeyValue("Managed Paths", managed.GetInt32().ToString());
                    }

                    if (urlProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        output.Dim($"Source: {urlProp.GetString()}");

                    // Show SHA mismatch warning and PDB patching info
                    if (root.TryGetProperty("shaMismatch", out var shaMismatch) &&
                        shaMismatch.ValueKind == System.Text.Json.JsonValueKind.True)
                    {
                        output.WriteLine();
                        output.Warning("Note: Exact commit SHA not found - downloaded symbols by version tag.");

                        // Show PDB patching info if any PDBs were patched
                        if (root.TryGetProperty("pdbsPatched", out var pdbsPatched) &&
                            pdbsPatched.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            var patchedCount = pdbsPatched.TryGetProperty("patched", out var patched)
                                ? patched.GetInt32() : 0;
                            var verifiedCount = pdbsPatched.TryGetProperty("verified", out var verified)
                                ? verified.GetInt32() : 0;

                            if (patchedCount > 0)
                            {
                                output.WriteLine();
                                if (verifiedCount == patchedCount)
                                {
                                    output.Success($"⚙ Patched and verified {patchedCount} PDB file(s) to match dump module GUIDs:");
                                }
                                else if (verifiedCount > 0)
                                {
                                    output.Warning($"⚙ Patched {patchedCount} PDB file(s), but only {verifiedCount} verified successfully:");
                                }
                                else
                                {
                                    output.Error($"⚙ Patched {patchedCount} PDB file(s), but verification FAILED:");
                                }

                                if (pdbsPatched.TryGetProperty("files", out var patchedFiles) &&
                                    patchedFiles.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    foreach (var patchedFile in patchedFiles.EnumerateArray())
                                    {
                                        var fileName = patchedFile.TryGetProperty("file", out var f)
                                            ? f.GetString() : "unknown";
                                        var fileVerified = patchedFile.TryGetProperty("verified", out var fv) && fv.GetBoolean();
                                        var checkmark = fileVerified ? "✓" : "✗";
                                        output.Dim($"  {checkmark} {fileName}");
                                    }
                                }

                                if (verifiedCount == patchedCount)
                                {
                                    output.Dim("  This allows SOS to load symbols despite the version mismatch.");
                                }
                                else
                                {
                                    output.Error("  Symbol loading may not work correctly. Try clearing symbols and re-downloading.");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    output.Error($"Error parsing result: {ex.Message}");
                }
            }
        }
        catch (McpClientException ex)
        {
            output.Error($"Failed to download Datadog symbols: {ex.Message}");
        }
        catch (Exception ex)
        {
            output.Error($"Failed to download Datadog symbols: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles listing available Datadog artifacts for a commit.
    /// </summary>
    private static async Task HandleDatadogListAsync(
        string[] args,
        ConsoleOutput output,
        McpClient mcpClient)
    {
        if (args.Length == 0)
        {
            output.Error("Commit SHA required.");
            output.Dim("Usage: symbols datadog list <commitSha>");
            return;
        }

        var commitSha = args[0];

        output.Info($"Listing Datadog artifacts for commit {commitSha[..Math.Min(8, commitSha.Length)]}...");

        try
        {
            var result = await output.WithSpinnerAsync(
                "Querying Azure Pipelines...",
                () => mcpClient.ListDatadogArtifactsAsync(commitSha));

            if (IsErrorResult(result))
            {
                output.Error(result);
            }
            else
            {
                // Pretty print the JSON result
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(result);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("build", out var build))
                    {
                        output.Header("Build Information");
                        if (build.TryGetProperty("number", out var num))
                            output.KeyValue("Build Number", num.GetString() ?? "");
                        if (build.TryGetProperty("id", out var id))
                            output.KeyValue("Build ID", id.GetInt32().ToString());
                        if (build.TryGetProperty("result", out var res))
                            output.KeyValue("Result", res.GetString() ?? "");
                        if (build.TryGetProperty("branch", out var branch))
                            output.KeyValue("Branch", branch.GetString() ?? "");
                    }

                    if (root.TryGetProperty("artifactsByCategory", out var categories))
                    {
                        output.Header("Available Artifacts");

                        void PrintCategory(string name, string propName)
                        {
                            if (categories.TryGetProperty(propName, out var items) &&
                                items.ValueKind == System.Text.Json.JsonValueKind.Array &&
                                items.GetArrayLength() > 0)
                            {
                                output.Markup($"[bold]{name}[/] ({items.GetArrayLength()})");
                                foreach (var item in items.EnumerateArray().Take(5))
                                {
                                    output.Dim($"  {item.GetString()}");
                                }
                                if (items.GetArrayLength() > 5)
                                    output.Dim($"  ... and {items.GetArrayLength() - 5} more");
                            }
                        }

                        PrintCategory("Tracer Symbols", "tracerSymbols");
                        PrintCategory("Profiler Symbols", "profilerSymbols");
                        PrintCategory("Monitoring Home", "monitoringHome");
                        PrintCategory("Universal Symbols", "universalSymbols");
                    }

                    if (root.TryGetProperty("totalArtifacts", out var total))
                        output.Dim($"Total artifacts: {total.GetInt32()}");
                }
                catch
                {
                    // Fall back to raw output
                    output.WriteLine(result);
                }
            }
        }
        catch (McpClientException ex)
        {
            output.Error($"Failed to list Datadog artifacts: {ex.Message}");
        }
        catch (Exception ex)
        {
            output.Error($"Failed to list Datadog artifacts: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles showing Datadog symbols configuration.
    /// </summary>
    private static async Task HandleDatadogConfigAsync(
        ConsoleOutput output,
        McpClient mcpClient)
    {
        try
        {
            var result = await output.WithSpinnerAsync(
                "Getting configuration...",
                () => mcpClient.GetDatadogSymbolsConfigAsync());

            if (IsErrorResult(result))
            {
                output.Error(result);
            }
            else
            {
                output.Header("Datadog Symbol Download Configuration");

                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(result);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("enabled", out var enabled))
                        output.KeyValue("Enabled", enabled.GetBoolean() ? "[green]Yes[/]" : "[red]No[/]");
                    if (root.TryGetProperty("hasPatToken", out var hasPat))
                        output.KeyValue("PAT Token", hasPat.GetBoolean() ? "[green]Configured[/]" : "[dim]Not set[/]");
                    if (root.TryGetProperty("timeoutSeconds", out var timeout))
                        output.KeyValue("Timeout", $"{timeout.GetInt32()} seconds");
                    if (root.TryGetProperty("maxArtifactSizeMB", out var maxSize))
                        output.KeyValue("Max Artifact Size", $"{maxSize.GetInt64()} MB");
                    if (root.TryGetProperty("cacheDirectory", out var cache))
                        output.KeyValue("Cache Directory", cache.GetString() ?? "");

                    if (root.TryGetProperty("azureDevOps", out var azure))
                    {
                        output.Markup("");
                        output.Markup("[bold]Azure DevOps[/]");
                        if (azure.TryGetProperty("organization", out var org))
                            output.KeyValue("Organization", org.GetString() ?? "");
                        if (azure.TryGetProperty("project", out var proj))
                            output.KeyValue("Project", proj.GetString() ?? "");
                    }

                    if (root.TryGetProperty("environmentVariables", out var envVars))
                    {
                        output.Markup("");
                        output.Markup("[bold]Environment Variables[/]");
                        foreach (var prop in envVars.EnumerateObject())
                        {
                            output.Dim($"  {prop.Name}: {prop.Value.GetString()}");
                        }
                    }
                }
                catch
                {
                    output.WriteLine(result);
                }
            }
        }
        catch (McpClientException ex)
        {
            output.Error($"Failed to get configuration: {ex.Message}");
        }
        catch (Exception ex)
        {
            output.Error($"Failed to get configuration: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles clearing Datadog symbols for the current dump.
    /// </summary>
    private static async Task HandleDatadogClearAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Open a dump first with 'open <dumpId>'.");
            return;
        }

        // Parse arguments
        var clearApiCache = args.Contains("--all") || args.Contains("-a");

        output.Info("Clearing downloaded Datadog symbols...");
        if (clearApiCache)
        {
            output.Dim("Also clearing API caches (build/release lookups)");
        }

        try
        {
            var result = await output.WithSpinnerAsync(
                "Clearing Datadog symbols...",
                () => mcpClient.ClearDatadogSymbolsAsync(
                    state.SessionId!,
                    state.Settings.UserId,
                    clearApiCache));

            if (IsErrorResult(result))
            {
                output.Error(result);
                return;
            }

            // Pretty print the result
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(result);
                var root = doc.RootElement;

                var isSuccess = root.TryGetProperty("success", out var successProp) &&
                               successProp.ValueKind == System.Text.Json.JsonValueKind.True;

                if (isSuccess)
                {
                    if (root.TryGetProperty("message", out var msg))
                    {
                        output.Success(msg.GetString() ?? "Symbols cleared");
                    }

                    if (root.TryGetProperty("filesDeleted", out var filesElem) &&
                        filesElem.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        var filesDeleted = filesElem.GetInt32();
                        if (filesDeleted > 0)
                        {
                            output.KeyValue("Files Deleted", filesDeleted.ToString());
                        }
                    }

                    if (root.TryGetProperty("sizeFreedMb", out var sizeElem) &&
                        sizeElem.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        var sizeMb = sizeElem.GetDouble();
                        if (sizeMb > 0)
                        {
                            output.KeyValue("Space Freed", $"{sizeMb:F1} MB");
                        }
                    }

                    if (root.TryGetProperty("apiCacheCleared", out var cacheElem) &&
                        cacheElem.ValueKind == System.Text.Json.JsonValueKind.True)
                    {
                        output.Dim("API caches cleared");
                    }

                    output.WriteLine();
                    output.Dim("Use 'symbols datadog download' to re-download symbols.");
                }
                else
                {
                    if (root.TryGetProperty("error", out var errMsg))
                    {
                        output.Error(errMsg.GetString() ?? "Failed to clear symbols");
                    }
                    else
                    {
                        output.Error("Failed to clear symbols");
                    }
                }
            }
            catch (Exception ex)
            {
                output.Error($"Error parsing result: {ex.Message}");
            }
        }
        catch (McpClientException ex)
        {
            output.Error($"Failed to clear Datadog symbols: {ex.Message}");
        }
        catch (Exception ex)
        {
            output.Error($"Failed to clear Datadog symbols: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the stats command.
    /// </summary>
    private static async Task HandleStatsAsync(
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient)
    {
        // Require connection
        if (!state.IsConnected)
        {
            output.Error("Not connected. Use 'connect <url>' first.");
            return;
        }

        try
        {
            var stats = await output.WithSpinnerAsync(
                "Fetching statistics...",
                () => httpClient.GetStatisticsAsync());

            output.Header("Server Statistics");
            output.WriteLine();

            output.KeyValue("Active Sessions", stats.ActiveSessions.ToString());
            output.KeyValue("Total Dumps", stats.TotalDumps.ToString());
            output.KeyValue("Storage Used", stats.FormattedStorageUsed);

            if (!string.IsNullOrEmpty(stats.Uptime))
            {
                output.KeyValue("Uptime", stats.Uptime);
            }
        }
        catch (HttpApiException ex)
        {
            output.Error($"Failed to get statistics: {ex.Message}");
        }
        catch (Exception ex)
        {
            output.Error($"Failed to get statistics: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the llm command (OpenRouter chat queries).
    /// </summary>
    private static async Task HandleLlmAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state)
    {
        var llmSettings = state.Settings.Llm;
        llmSettings.ApplyEnvironmentOverrides();

        state.Transcript ??= new CliTranscriptStore(Path.Combine(ConnectionSettings.DefaultConfigDirectory, "cli_transcript.jsonl"));
        var transcript = state.Transcript;

        if (args.Length == 0)
        {
            output.Header("LLM");
            output.WriteLine();
            output.KeyValue("Provider", "OpenRouter");
            output.KeyValue("Model", llmSettings.OpenRouterModel);
            output.KeyValue("API Key", string.IsNullOrWhiteSpace(llmSettings.GetEffectiveOpenRouterApiKey()) ? "(not set)" : "(configured)");
            output.WriteLine();
            output.Dim("Usage:");
            output.Dim("  llm <prompt>");
            output.Dim("  llm model <openrouter-model-id>");
            output.Dim("  llm set-key <api-key>            (persists to ~/.dbg-mcp/config.json)");
            output.Dim("  llm reset                        (clears only LLM conversation)");
            output.Dim("Tip: Prefer env var OPENROUTER_API_KEY to avoid persisting keys.");
            return;
        }

        var sub = args[0].ToLowerInvariant();
        switch (sub)
        {
            case "model":
                if (args.Length < 2)
                {
                    output.Error("Usage: llm model <openrouter-model-id>");
                    return;
                }
                llmSettings.OpenRouterModel = args[1].Trim();
                state.Settings.Save();
                output.Success($"LLM model set: {llmSettings.OpenRouterModel}");
                return;

            case "set-key":
                if (args.Length < 2)
                {
                    output.Error("Usage: llm set-key <api-key>");
                    return;
                }
                llmSettings.OpenRouterApiKey = args[1].Trim();
                state.Settings.Save();
                output.Success("OpenRouter API key saved to config.");
                output.Dim("Tip: Prefer env var OPENROUTER_API_KEY to avoid persisting keys.");
                return;

            case "reset":
                transcript.FilterInPlace(e =>
                    e.Kind is not ("llm_user" or "llm_assistant") ||
                    !TranscriptScope.Matches(e, state.Settings.ServerUrl, state.SessionId, state.DumpId));
                output.Success("Cleared LLM conversation history for the current session/dump (kept other sessions and CLI transcript).");
                return;
        }

        var prompt = string.Join(" ", args).Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            output.Error("Prompt cannot be empty.");
            return;
        }

        transcript.Append(new CliTranscriptEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Kind = "llm_user",
            Text = TranscriptRedactor.RedactText(prompt),
            ServerUrl = state.Settings.ServerUrl,
            SessionId = state.SessionId,
            DumpId = state.DumpId
        });

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1, llmSettings.TimeoutSeconds)) };
            var client = new OpenRouterClient(http, llmSettings);

            var history = transcript.ReadTailForScope(80, state.Settings.ServerUrl, state.SessionId, state.DumpId);
            var messages = TranscriptContextBuilder.BuildMessages(
                userPrompt: prompt,
                serverUrl: state.Settings.ServerUrl,
                sessionId: state.SessionId,
                dumpId: state.DumpId,
                transcriptTail: history,
                maxContextChars: 30_000);

            var response = await output.WithSpinnerAsync(
                "Calling LLM...",
                () => client.ChatAsync(messages, cancellationToken: default));

            transcript.Append(new CliTranscriptEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Kind = "llm_assistant",
                Text = TranscriptRedactor.RedactText(response),
                ServerUrl = state.Settings.ServerUrl,
                SessionId = state.SessionId,
                DumpId = state.DumpId
            });

            output.WriteLine();
            output.WriteLine(response);
        }
        catch (Exception ex)
        {
            output.Error(ex.Message);
        }
    }


    /// <summary>
    /// Shows available MCP tools.
    /// </summary>
    private static void ShowTools(ConsoleOutput output, McpClient mcpClient)
    {
        if (!mcpClient.IsConnected)
        {
            output.Error("Not connected. Use 'connect <url>' first.");
            return;
        }

        var tools = mcpClient.AvailableTools;
        if (tools.Count == 0)
        {
            output.Warning("No tools discovered.");
            return;
        }

        output.Header($"Available MCP Tools ({tools.Count})");
        output.WriteLine();

        foreach (var tool in tools.OrderBy(t => t))
        {
            output.Markup($"  [cyan]•[/] {tool}");
        }
    }


    /// <summary>
    /// Handles the session command (create, list, close).
    /// </summary>
    private static async Task HandleSessionAsync(
        string[] args,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (!state.IsConnected)
        {
            output.Error("Not connected. Use 'connect <url>' first.");
            return;
        }

        if (!mcpClient.IsConnected)
        {
            output.Error("MCP client not connected. Debugging commands unavailable.");
            return;
        }

        if (args.Length == 0)
        {
            output.Error("Subcommand required. Usage: session <create|list|close|info|use|restore>");
            return;
        }

        var subcommand = args[0].ToLowerInvariant();

        try
        {
            switch (subcommand)
            {
                case "create":
                case "new":
                    var createResult = await output.WithSpinnerAsync(
                        "Creating session...",
                        () => mcpClient.CreateSessionAsync(state.Settings.UserId));

                    if (IsErrorResult(createResult))
                    {
                        output.Error(createResult);
                        
                        // Provide helpful hints based on error type
                        if (createResult.Contains("maximum number of sessions", StringComparison.OrdinalIgnoreCase))
                        {
                            output.Dim("Tip: Run 'session list' to see your active sessions");
                            output.Dim("     Run 'session close <id>' to close unused sessions");
                            output.Dim("     Run 'session restore <id>' to reuse an existing session");
                        }
                    }
                    else
                    {
                        output.Success("Session created!");
                        output.Markup(createResult);

                        // Extract session ID from result and store in state
                        var sessionIdMatch = System.Text.RegularExpressions.Regex.Match(
                            createResult, @"SessionId:\s*([a-zA-Z0-9\-]+)");
	                        if (sessionIdMatch.Success)
	                        {
	                            state.SessionId = sessionIdMatch.Groups[1].Value;
	                            state.Settings.SetLastSessionId(state.Settings.ServerUrl, state.Settings.UserId, state.SessionId);
	                            state.Settings.Save();
	                            output.Dim($"Session ID saved: {state.SessionId}");
	                        }
                    }
                    break;

                case "list":
                case "ls":
                    await HandleSessionListAsync(console, output, state, mcpClient);
                    break;

                case "close":
                    var partialCloseId = args.Length > 1 ? args[1] : state.SessionId;
                    if (string.IsNullOrEmpty(partialCloseId))
                    {
                        output.Error("Session ID required. Usage: session close <sessionId>");
                        output.Dim("Tip: You can use partial IDs - e.g., 'session close d03'");
                        return;
                    }

                    // Resolve partial ID
                    var sessionToClose = await ResolvePartialSessionIdAsync(
                        partialCloseId, output, state, mcpClient);
                    if (string.IsNullOrEmpty(sessionToClose))
                    {
                        return;
                    }

                    string closeResult;
                    try
                    {
                        closeResult = await output.WithSpinnerAsync(
                            "Closing session...",
                            () => mcpClient.CloseSessionAsync(sessionToClose, state.Settings.UserId));

                        if (IsErrorResult(closeResult))
                        {
                            output.Error(closeResult);
                        }
	                        else
	                        {
	                            output.Success(closeResult);

	                            // Clear session from state if it matches
	                            if (state.SessionId == sessionToClose)
	                            {
	                                state.ClearSession();
	                            }

	                            if (state.Settings.ClearLastSessionId(state.Settings.ServerUrl, state.Settings.UserId, sessionToClose))
	                            {
	                                state.Settings.Save();
	                            }
	                        }
                    }
                    catch (McpClientException ex) when (SessionExpiryHandler.IsSessionExpired(ex))
                    {
                        // If the server no longer knows about this session, treat it as already closed and
                        // clear the local state if it was the active one.
                        output.Warning("Session no longer exists (expired/evicted).");
	                        if (state.SessionId == sessionToClose)
	                        {
	                            state.ClearSession();
	                        }
	                        
	                        if (state.Settings.ClearLastSessionId(state.Settings.ServerUrl, state.Settings.UserId, sessionToClose))
	                        {
	                            state.Settings.Save();
	                        }
	                    }
                    break;

                case "info":
                    var partialInfoId = args.Length > 1 ? args[1] : state.SessionId;
                    if (string.IsNullOrEmpty(partialInfoId))
                    {
                        output.Error("Session ID required. Usage: session info <sessionId>");
                        output.Dim("Tip: You can use partial IDs - e.g., 'session info d03'");
                        return;
                    }

                    // Resolve partial ID
                    var sessionForInfo = await ResolvePartialSessionIdAsync(
                        partialInfoId, output, state, mcpClient);
                    if (string.IsNullOrEmpty(sessionForInfo))
                    {
                        return;
                    }

                    var infoResult = await output.WithSpinnerAsync(
                        "Getting debugger info...",
                        () => mcpClient.GetDebuggerInfoAsync(sessionForInfo, state.Settings.UserId));
                    output.Markup(infoResult);
                    break;

                case "use":
                case "attach":
                case "connect":
                    var partialSessionId = args.Length > 1 ? args[1] : null;
                    if (string.IsNullOrEmpty(partialSessionId))
                    {
                        output.Error("Session ID required. Usage: session use <sessionId>");
                        output.Dim("Tip: You can use partial IDs (like Docker) - e.g., 'session use d03'");
                        return;
                    }

                    // Resolve partial session ID (Docker-style)
                    try
                    {
                        var resolvedSessionId = await ResolvePartialSessionIdAsync(
                            partialSessionId, output, state, mcpClient);

                        if (string.IsNullOrEmpty(resolvedSessionId))
                        {
                            return; // Error already shown
                        }

                        // Verify the session exists by getting its info
                        var verifyResult = await output.WithSpinnerAsync(
                            "Verifying session...",
                            () => mcpClient.GetDebuggerInfoAsync(resolvedSessionId, state.Settings.UserId));

                        if (IsErrorResult(verifyResult))
                        {
                            output.Error($"Session not found or access denied: {resolvedSessionId}");
                            return;
                        }

	                        state.SessionId = resolvedSessionId;
	                        state.Settings.SetLastSessionId(state.Settings.ServerUrl, state.Settings.UserId, resolvedSessionId);
	                        state.Settings.Save();
	                        state.ClearDump();
                        
                        // Extract debugger type from response
                        var debuggerTypeMatch = System.Text.RegularExpressions.Regex.Match(
                            verifyResult, @"Debugger\s*Type:\s*(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (debuggerTypeMatch.Success)
                        {
                            state.DebuggerType = debuggerTypeMatch.Groups[1].Value;
                        }

                        // Sync current dump from the server's session list (sessions can already have a dump open).
                        try
                        {
                            var listJson = await mcpClient.ListSessionsAsync(state.Settings.UserId);
                            if (!IsErrorResult(listJson))
                            {
                                var parsed = System.Text.Json.JsonSerializer.Deserialize<SessionListResponse>(
                                    listJson,
                                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                                if (parsed != null)
                                {
                                    SessionStateSynchronizer.TrySyncCurrentDumpFromSessionList(state, parsed);
                                }
                            }
                        }
                        catch
                        {
                            // Best-effort: keep prompt usable even if session list parsing fails.
                        }
                        
                        output.Success($"Now using session: {resolvedSessionId}");
                        output.Markup(verifyResult);
                    }
                    catch (Exception ex)
                    {
                        output.Error($"Failed to attach to session: {ex.Message}");
                    }
                    break;

                case "restore":
                    var partialRestoreId = args.Length > 1 ? args[1] : null;
                    if (string.IsNullOrEmpty(partialRestoreId))
                    {
                        output.Error("Session ID required. Usage: session restore <sessionId>");
                        output.Dim("Tip: Use 'session list' to see persisted sessions");
                        return;
                    }

                    try
                    {
                        var resolvedRestoreId = await ResolvePartialSessionIdAsync(
                            partialRestoreId, output, state, mcpClient);

                        if (string.IsNullOrEmpty(resolvedRestoreId))
                        {
                            return; // Error already shown
                        }

                        var restoreResult = await output.WithSpinnerAsync(
                            "Restoring session...",
                            () => mcpClient.RestoreSessionAsync(resolvedRestoreId, state.Settings.UserId));

	                        if (IsErrorResult(restoreResult))
	                        {
	                            output.Error(restoreResult);
	                            output.Dim("Use 'session list' to see available sessions or 'session create' to create a new one.");
	                            return;
	                        }

	                        state.SessionId = resolvedRestoreId;
	                        state.Settings.SetLastSessionId(state.Settings.ServerUrl, state.Settings.UserId, resolvedRestoreId);
	                        state.Settings.Save();
	                        
	                        // Check if dump is mentioned in result
	                        if (restoreResult.Contains("CurrentDump:") && !restoreResult.Contains("None"))
	                        {
                            var dumpMatch = System.Text.RegularExpressions.Regex.Match(
                                restoreResult, @"CurrentDump:\s*(\S+)");
                            if (dumpMatch.Success)
                            {
                                state.SetDumpLoaded(dumpMatch.Groups[1].Value);
                            }
                        }
                        
                        // Get debugger type for command mode prompt
                        try
                        {
                            var debuggerInfo = await mcpClient.GetDebuggerInfoAsync(resolvedRestoreId, state.Settings.UserId);
                            var debuggerTypeMatch = System.Text.RegularExpressions.Regex.Match(
                                debuggerInfo, @"Debugger\s*Type:\s*(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (debuggerTypeMatch.Success)
                            {
                                state.DebuggerType = debuggerTypeMatch.Groups[1].Value;
                            }
                        }
                        catch
                        {
                            // Debugger type extraction failed - not critical
                        }
                        
                        output.Success("Session restored!");
                        output.Markup(restoreResult);
                    }
                    catch (Exception ex)
                    {
                        output.Error($"Failed to restore session: {ex.Message}");
                    }
                    break;

                default:
                    output.Error($"Unknown subcommand: {subcommand}");
                    output.Dim("Available subcommands: create, list, close, info, use, restore");
                    break;
            }
        }
        catch (McpClientException ex)
        {
            output.Error(ex.Message);
        }
        catch (Exception ex)
        {
            output.Error($"Session operation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the open command (open a dump in the debugger).
    /// </summary>
    private static async Task HandleOpenDumpAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient,
        HttpApiClient httpClient,
        bool retryOnExpiredSession = true)
    {
        if (!state.IsConnected)
        {
            output.Error("Not connected. Use 'connect <url>' first.");
            return;
        }

        if (!mcpClient.IsConnected)
        {
            output.Error("MCP client not connected. Use HTTP commands instead.");
            return;
        }

        // Determine dump ID (supports partial IDs)
        var partialDumpId = args.Length > 0 ? args[0] : (state.SelectedDumpId ?? state.DumpId);
        if (string.IsNullOrEmpty(partialDumpId))
        {
            output.Error("Dump ID required. Usage: open <dumpId>");
            output.Dim("Upload a dump first with 'upload <file>' or specify a dump ID.");
            output.Dim("Tip: You can use partial IDs - e.g., 'open bff4'");
            return;
        }

        // Resolve partial dump ID to full ID
        var dumpId = await ResolvePartialDumpIdAsync(partialDumpId, output, state, httpClient);
        if (dumpId == null)
        {
            return; // Error already shown
        }

        // Check if dump matches current server (architecture and Alpine status)
        // If mismatch, offer to switch servers inline
        var switchResult = await CheckDumpServerMatchAndSwitchAsync(dumpId, output, state, httpClient, mcpClient);
        if (switchResult == ServerSwitchResult.Cancelled)
        {
            return; // User chose not to continue
        }

        // Auto-create session if needed
        if (string.IsNullOrEmpty(state.SessionId))
        {
            try
            {
                var createResult = await output.WithSpinnerAsync(
                    "Creating session...",
                    () => mcpClient.CreateSessionAsync(state.Settings.UserId));

                // Check if the result indicates an error (max sessions, etc.)
                if (IsErrorResult(createResult))
                {
                    output.Error(createResult);
                    
                    // Provide helpful hints based on error type
                    if (createResult.Contains("maximum number of sessions", StringComparison.OrdinalIgnoreCase))
                    {
                        output.Dim("Tip: Run 'session list' to see your active sessions");
                        output.Dim("     Run 'session close <id>' to close unused sessions");
                        output.Dim("     Run 'session restore <id>' to reuse an existing session");
                    }
                    return;
                }

	                var sessionIdMatch = System.Text.RegularExpressions.Regex.Match(
	                    createResult, @"SessionId:\s*([a-zA-Z0-9\-]+)");
	                if (sessionIdMatch.Success)
	                {
	                    state.SessionId = sessionIdMatch.Groups[1].Value;
	                    state.Settings.SetLastSessionId(state.Settings.ServerUrl, state.Settings.UserId, state.SessionId);
	                    state.Settings.Save();
	                    output.Success($"Session created: {state.SessionId}");
	                }
	                else
	                {
	                    output.Error("Failed to create session - unexpected response format");
	                    output.Dim(createResult);
	                    return;
                }
            }
            catch (Exception ex)
            {
                output.Error($"Failed to create session: {ex.Message}");
                
                // Check exception message for hints
                if (ex.Message.Contains("maximum number of sessions", StringComparison.OrdinalIgnoreCase))
                {
                    output.Dim("Tip: Run 'session list' to see your active sessions");
                    output.Dim("     Run 'session close <id>' to close unused sessions");
                }
                return;
            }
        }

        try
        {
            // Show informative pre-operation messages
            output.WriteLine();
            output.Markup("[yellow]Opening dump file...[/]");
            output.Dim("This operation involves several steps:");
            output.Dim("  1. Initializing debugger (LLDB on Linux/macOS, WinDbg on Windows)");
            output.Dim("  2. Downloading symbols via dotnet-symbol (can take 1-2 minutes)");
            output.Dim("  3. Loading dump file into debugger");
            output.Dim("  4. Configuring symbol paths");
            output.WriteLine();
            
            var result = await output.WithSpinnerAsync(
                "Downloading symbols and loading dump (please wait)...",
                () => mcpClient.OpenDumpAsync(state.SessionId!, state.Settings.UserId, dumpId));

            // Check if the result indicates an error
            if (IsErrorResult(result))
            {
                output.Error(result);
            }
            else
            {
	                output.Success("Dump opened successfully!");
	                output.Markup(result);
	                state.SetDumpLoaded(dumpId);
	                state.Settings.SetLastSessionId(state.Settings.ServerUrl, state.Settings.UserId, state.SessionId!);
	                state.Settings.Save();
	                
	                // Get debugger type for proper command mapping
	                try
	                {
                    var debuggerInfo = await mcpClient.GetDebuggerInfoAsync(state.SessionId!, state.Settings.UserId);
                    // Extract debugger type from response (e.g., "Debugger Type: LLDB" or "Debugger Type: WinDbg")
                    var debuggerTypeMatch = System.Text.RegularExpressions.Regex.Match(
                        debuggerInfo, @"Debugger\s*Type:\s*(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (debuggerTypeMatch.Success)
                    {
                        state.DebuggerType = debuggerTypeMatch.Groups[1].Value;
                        output.Dim($"Debugger: {state.DebuggerType}");
                    }
                }
                catch
                {
                    // Ignore errors getting debugger info - not critical
                }
                
                output.WriteLine();
                output.Dim("Tip: For .NET dumps, SOS is auto-loaded. Try 'analyze dotnet' or 'exec !threads'");
            }
        }
        catch (McpClientException ex) when (retryOnExpiredSession && IsSessionNotFoundError(ex))
        {
            output.Error(ex.Message);
            output.Warning("Your session appears to have expired. Creating a new session and retrying...");
            SessionExpiryHandler.ClearExpiredSession(state);
            await HandleOpenDumpAsync(args, output, state, mcpClient, httpClient, retryOnExpiredSession: false);
        }
        catch (McpClientException ex) when (IsToolResponseTimeoutError(ex))
        {
            output.Error(ex.Message);
            output.Dim("The server may still be processing the request. Syncing session state...");

            try
            {
                await DumpStateRecovery.TrySyncOpenedDumpFromServerAsync(state, mcpClient);
            }
            catch
            {
                // Best-effort only; keep the CLI usable even if sync fails.
            }

            if (!string.IsNullOrWhiteSpace(state.DumpId))
            {
                output.Info($"Server reports a dump is open: {state.DumpId}");
                output.Dim("If this is the dump you wanted, you can proceed; otherwise run 'close' and try again.");
            }
            else
            {
                output.Dim("No open dump detected yet. Try again in a moment (symbols may still be downloading).");
            }
        }
        catch (McpClientException ex) when (IsDumpAlreadyOpenError(ex))
        {
            output.Error(ex.Message);
            output.Dim("Syncing session state...");

            try
            {
                await DumpStateRecovery.TrySyncOpenedDumpFromServerAsync(state, mcpClient);
            }
            catch
            {
                // Best-effort only.
            }

            if (!string.IsNullOrWhiteSpace(state.DumpId))
            {
                output.Info($"Current open dump: {state.DumpId}");
                output.Dim("Run 'close' before opening a different dump.");
            }
        }
        catch (McpClientException ex)
        {
            output.Error(ex.Message);
        }
        catch (TimeoutException)
        {
            output.Error("Operation timed out. The dump might be too large or symbols are still downloading.");
            output.Dim("Try again - subsequent attempts may be faster if symbols are cached.");
        }
        catch (Exception ex)
        {
            output.Error($"Failed to open dump: {ex.Message}");
        }
    }

    /// <summary>
    /// Result of server switch check.
    /// </summary>
    private enum ServerSwitchResult
    {
        /// <summary>No switch needed, server matches.</summary>
        NoSwitchNeeded,
        /// <summary>Server was switched successfully.</summary>
        Switched,
        /// <summary>User chose to continue with mismatched server.</summary>
        ContinueWithMismatch,
        /// <summary>User cancelled the operation.</summary>
        Cancelled
    }

    /// <summary>
    /// Checks if the dump matches the current server and offers to switch if not.
    /// </summary>
    /// <returns>Result indicating whether to proceed with opening the dump.</returns>
    private static async Task<ServerSwitchResult> CheckDumpServerMatchAndSwitchAsync(
        string dumpId,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient,
        McpClient mcpClient)
    {
        try
        {
            // Get dump metadata
            var dumpInfo = await httpClient.GetDumpInfoAsync(state.Settings.UserId, dumpId);
            if (dumpInfo == null)
            {
                return ServerSwitchResult.NoSwitchNeeded; // Can't check, continue anyway
            }

            // Get server capabilities
            var configManager = new ServerConfigManager();
            var discovery = new ServerDiscovery(configManager);
            
            // Try to get current server capabilities
            ServerCapabilities? serverCaps = null;
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                if (!string.IsNullOrEmpty(state.Settings.ApiKey))
                {
                    client.DefaultRequestHeaders.Add("X-API-Key", state.Settings.ApiKey);
                }
                var url = state.Settings.ServerUrl?.TrimEnd('/') + "/api/server/capabilities";
                if (!string.IsNullOrEmpty(url) && url.StartsWith("http"))
                {
                    var response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        serverCaps = await response.Content.ReadFromJsonAsync<ServerCapabilities>();
                    }
                }
            }
            catch
            {
                // Can't get server capabilities, continue anyway
                return ServerSwitchResult.NoSwitchNeeded;
            }

            if (serverCaps == null)
            {
                return ServerSwitchResult.NoSwitchNeeded; // Can't check, continue anyway
            }

            // Extract dump characteristics
            var dumpArch = dumpInfo.Architecture;
            var dumpIsAlpine = dumpInfo.IsAlpineDump ?? false;

            // Check for mismatches
            var archMismatch = !string.IsNullOrEmpty(dumpArch) && 
                !serverCaps.Architecture.Equals(dumpArch, StringComparison.OrdinalIgnoreCase);
            var alpineMismatch = serverCaps.IsAlpine != dumpIsAlpine;

            if (!archMismatch && !alpineMismatch)
            {
                return ServerSwitchResult.NoSwitchNeeded; // Match, continue
            }

            // Show warning
            output.WriteLine();
            output.Warning("⚠️  Server mismatch detected!");
            output.WriteLine();

            // Show comparison (not using table to avoid centering)
            var dumpDistroDisplay = dumpIsAlpine ? "[cyan]Alpine[/]" : "[cyan]Debian/glibc[/]";
            var serverArchDisplay = archMismatch 
                ? $"[red]{serverCaps.Architecture}[/]" 
                : $"[green]{serverCaps.Architecture}[/]";
            var serverDistroDisplay = alpineMismatch 
                ? (serverCaps.IsAlpine ? "[red]Alpine[/]" : "[red]Debian/glibc[/]")
                : (serverCaps.IsAlpine ? "[green]Alpine[/]" : "[green]Debian/glibc[/]");

            output.Markup($"  [bold]Dump:[/]   {dumpArch ?? "unknown"}, {dumpDistroDisplay}");
            output.Markup($"  [bold]Server:[/] {serverArchDisplay}, {serverDistroDisplay}");
            output.WriteLine();

            // Discover all servers to find matches
            await output.WithSpinnerAsync("Finding matching servers...", async () =>
            {
                await discovery.DiscoverAllAsync();
                return true;
            });

            var matchingServers = discovery.FindMatchingServers(dumpArch ?? serverCaps.Architecture, dumpIsAlpine);

            if (matchingServers.Count == 0)
            {
                output.Warning("No matching servers configured.");
                output.WriteLine();
                output.Warning("The dump may not analyze correctly due to architecture/distribution mismatch.");
                
                if (output.Console.Confirm("Continue anyway?", false))
                {
                    return ServerSwitchResult.ContinueWithMismatch;
                }
                return ServerSwitchResult.Cancelled;
            }

            // Show matching servers and offer to switch
            var dumpDistro = dumpIsAlpine ? "Alpine" : "Debian/glibc";
            output.Markup($"This dump requires a [cyan]{dumpArch ?? "unknown"}[/], [cyan]{dumpDistro}[/] server.");
            output.WriteLine();

            // Build selection choices
            var choices = new List<string>();
            foreach (var server in matchingServers)
            {
                choices.Add($"Switch to {server.Name} ({server.ShortUrl})");
            }
            choices.Add("Continue with current server (may cause issues)");
            choices.Add("Cancel");

            var choice = output.Console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]What would you like to do?[/]")
                    .AddChoices(choices)
            );

            if (choice == "Cancel")
            {
                return ServerSwitchResult.Cancelled;
            }

            if (choice.StartsWith("Continue with current"))
            {
                output.Warning("Continuing with mismatched server. Analysis results may be incomplete or incorrect.");
                return ServerSwitchResult.ContinueWithMismatch;
            }

            // User chose to switch - find the selected server
            var selectedServer = matchingServers.FirstOrDefault(s => choice.Contains(s.Name));
            if (selectedServer == null)
            {
                output.Error("Failed to identify selected server.");
                return ServerSwitchResult.Cancelled;
            }

            // Perform the server switch
            output.WriteLine();
            output.Markup($"[blue]ℹ[/] Switching to [yellow]{selectedServer.Name}[/]...");

            try
            {
                // Disconnect from current MCP server
                await mcpClient.DisconnectAsync();

                // Update settings
                state.Settings.ServerUrl = selectedServer.Url;
                state.Settings.ApiKey = selectedServer.ApiKey;
                state.Settings.Save();

                // Reconnect HTTP client
                httpClient.Configure(selectedServer.Url, selectedServer.ApiKey, state.Settings.Timeout);

                // Check health
                var health = await httpClient.CheckHealthAsync();
                if (!health.IsHealthy)
                {
                    output.Error($"Server {selectedServer.Name} is not healthy: {health.Status}");
                    return ServerSwitchResult.Cancelled;
                }

                // Update state
                state.SetConnected(selectedServer.Url);

                // Fetch and update server info
                var serverInfo = await httpClient.GetServerInfoAsync();
                if (serverInfo != null)
                {
                    state.ServerInfo = serverInfo;
                }

                // Reconnect MCP client
                await mcpClient.ConnectAsync(selectedServer.Url, selectedServer.ApiKey);

                output.Markup($"[green]✓[/] Switched to [yellow]{selectedServer.Name}[/] ({selectedServer.ShortUrl})");

                // Clear session since we're on a new server
                if (!string.IsNullOrEmpty(state.SessionId))
                {
                    output.Dim("Session cleared (will create new session on this server)");
                    state.ClearSession();
                }

                return ServerSwitchResult.Switched;
            }
            catch (Exception ex)
            {
                output.Error($"Failed to switch server: {ex.Message}");
                output.Warning("Attempting to reconnect to original server...");

                // Try to reconnect to original server
                try
                {
                    var originalUrl = state.Settings.ServerUrl;
                    httpClient.Configure(originalUrl!, state.Settings.ApiKey, state.Settings.Timeout);
                    await mcpClient.ConnectAsync(originalUrl!, state.Settings.ApiKey);
                    output.Info("Reconnected to original server.");
                }
                catch
                {
                    output.Error("Failed to reconnect. Use 'connect <url>' to reconnect.");
                    state.IsConnected = false;
                }

                return ServerSwitchResult.Cancelled;
            }
        }
        catch (Exception ex)
        {
            // Log but don't block on check failures
            output.Dim($"[dim]Note: Could not verify server compatibility: {ex.Message}[/]");
            return ServerSwitchResult.NoSwitchNeeded;
        }
    }

    private static async Task HandleSessionListAsync(
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        var listJson = await output.WithSpinnerAsync(
            "Listing sessions...",
            () => mcpClient.ListSessionsAsync(state.Settings.UserId));

        if (IsErrorResult(listJson))
        {
            output.Error(listJson);
            return;
        }

        var parsed = System.Text.Json.JsonSerializer.Deserialize<SessionListResponse>(
            listJson,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (parsed?.Sessions == null)
        {
            output.Error("Failed to parse session list response.");
            output.Dim(listJson);
            return;
        }

        RenderSessionListTable(console, output, state, parsed);
    }

    internal static void RenderSessionListTable(
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        SessionListResponse response)
    {
        var sessions = response.Sessions ?? [];
        var nowUtc = DateTime.UtcNow;

        if (sessions.Count == 0)
        {
            output.Info("No active sessions found.");
            return;
        }

        output.Header($"Sessions ({sessions.Count} total)");
        output.WriteLine();
        output.Dim("Tip: Use partial IDs (like Docker) - e.g., 'session use 7532' or 'session close 7532'");
        output.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[cyan]ID[/]"))
            .AddColumn(new TableColumn("[cyan]Created[/]"))
            .AddColumn(new TableColumn("[cyan]Last Activity[/]"))
            .AddColumn(new TableColumn("[cyan]Dump[/]"));

        output.CaptureOnly("ID\tCreated\tLast Activity\tDump");

        foreach (var session in sessions.OrderByDescending(s => s.LastActivityUtc ?? string.Empty, StringComparer.Ordinal))
        {
            var sessionId = session.SessionId ?? string.Empty;
            var created = FormatUtcTimestampWithAge(session.CreatedAtUtc, nowUtc);
            var last = FormatUtcTimestampWithAge(session.LastActivityUtc, nowUtc);
            var dumpId = session.CurrentDumpId ?? string.Empty;

            var displayId = HighlightId(sessionId, state.SessionId);
            var displayDump = string.IsNullOrEmpty(dumpId) ? "(none)" : HighlightId(dumpId, null);

            output.CaptureOnly($"{sessionId}\t{created}\t{last}\t{(string.IsNullOrEmpty(dumpId) ? "(none)" : dumpId)}");
            table.AddRow(displayId, created, last, displayDump);
        }

        console.Write(table);
    }

    private static string HighlightId(string id, string? highlightMatch)
    {
        if (string.IsNullOrEmpty(id))
        {
            return string.Empty;
        }

        var prefix = id.Length > 8 ? id[..8] : id;
        var suffix = id.Length > 8 ? id[8..] : string.Empty;

        var isCurrent = !string.IsNullOrEmpty(highlightMatch) &&
                        string.Equals(id, highlightMatch, StringComparison.OrdinalIgnoreCase);

        if (isCurrent)
        {
            return suffix.Length > 0
                ? $"[bold green]{prefix}[/]{suffix}"
                : $"[bold green]{prefix}[/]";
        }

        return suffix.Length > 0
            ? $"[yellow]{prefix}[/]{suffix}"
            : $"[yellow]{prefix}[/]";
    }

    private static string FormatUtcTimestampWithAge(string? utcTimestamp, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(utcTimestamp))
        {
            return "(unknown)";
        }

        if (DateTime.TryParse(utcTimestamp, null, DateTimeStyles.RoundtripKind, out var dt))
        {
            return FormatUtcDateTimeWithAge(dt, nowUtc, "yyyy-MM-dd HH:mm:ss");
        }

        return utcTimestamp;
    }

    internal static string FormatUtcDateTimeWithAge(DateTime utcTimestamp, DateTime nowUtc, string format)
    {
        var utc = utcTimestamp.Kind == DateTimeKind.Utc ? utcTimestamp : utcTimestamp.ToUniversalTime();
        var baseValue = utc.ToString(format);

        var delta = nowUtc - utc;
        if (delta.TotalSeconds < 0)
        {
            return $"{baseValue} (in future)";
        }

        if (delta.TotalDays < 1)
        {
            return $"{baseValue} (<1 day ago)";
        }

        var days = (int)Math.Floor(delta.TotalDays);
        return days == 1
            ? $"{baseValue} (1 day ago)"
            : $"{baseValue} ({days} days ago)";
    }

    /// <summary>
    /// Resolves a partial session ID to a full session ID (Docker-style).
    /// </summary>
    /// <returns>The full session ID, or null if not found or ambiguous.</returns>
    private static async Task<string?> ResolvePartialSessionIdAsync(
        string partialId,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        // Get all sessions for the user (JSON)
        var listJson = await mcpClient.ListSessionsAsync(state.Settings.UserId);
        if (IsErrorResult(listJson))
        {
            output.Error(listJson);
            return null;
        }

        SessionListResponse? parsed;
        try
        {
            parsed = System.Text.Json.JsonSerializer.Deserialize<SessionListResponse>(
                listJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            output.Error($"Failed to parse session list response: {ex.Message}");
            output.Dim(listJson);
            return null;
        }

        var sessionIds = parsed?.Sessions?
            .Select(s => s.SessionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList() ?? [];

        if (sessionIds.Count == 0)
        {
            output.Error("No active sessions found.");
            return null;
        }

        // Find sessions that match the partial ID (case-insensitive)
        var matchingSessions = sessionIds
            .Where(id => id.StartsWith(partialId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingSessions.Count == 0)
        {
            output.Error($"No session found matching '{partialId}'");
            output.Dim("Available sessions:");
            foreach (var id in sessionIds.Take(5))
            {
                output.Dim($"  {id}");
            }
            if (sessionIds.Count > 5)
            {
                output.Dim($"  ... and {sessionIds.Count - 5} more");
            }
            return null;
        }

        if (matchingSessions.Count > 1)
        {
            output.Error($"Ambiguous session ID '{partialId}' - matches {matchingSessions.Count} sessions:");
            foreach (var id in matchingSessions.Take(5))
            {
                output.Dim($"  {id}");
            }
            if (matchingSessions.Count > 5)
            {
                output.Dim($"  ... and {matchingSessions.Count - 5} more");
            }
            output.Dim("Please provide more characters to disambiguate.");
            return null;
        }

        // Exactly one match - return it
        var resolvedId = matchingSessions[0];
        if (resolvedId != partialId)
        {
            output.Dim($"Resolved '{partialId}' to '{resolvedId}'");
        }
        return resolvedId;
    }

    /// <summary>
    /// Checks if a tool result indicates an error.
    /// </summary>
    private static bool IsErrorResult(string result)
    {
        if (string.IsNullOrEmpty(result))
        {
            return false;
        }

        // For JSON responses, check for top-level "error" field only
        // Don't flag nested [error: ...] markers as failures
        if (result.TrimStart().StartsWith("{") || result.TrimStart().StartsWith("["))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(result);
                var root = doc.RootElement;
                
                // Check for top-level "error" property
                if (root.TryGetProperty("error", out var errorProp))
                {
                    // Has an explicit error field - this is an error
                    return true;
                }
                
                // Check for "Error" property (capitalized)
                if (root.TryGetProperty("Error", out var errorProp2) && 
                    errorProp2.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var errorValue = errorProp2.GetString();
                    // Only error if the Error field contains an actual error message
                    return !string.IsNullOrEmpty(errorValue) && 
                           !errorValue.StartsWith("0x"); // Not an address
                }
                
                // Valid JSON object/array without top-level error - not an error
                return false;
            }
            catch
            {
                // Not valid JSON, fall through to text checks
            }
        }

        // For non-JSON text responses, check for error indicators at the start
        var trimmed = result.TrimStart();
        var errorPrefixes = new[]
        {
            "error:",
            "error occurred",
            "failed:",
            "failed to"
        };

        return errorPrefixes.Any(prefix => 
            trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Handles the close command (close the current dump).
    /// </summary>
    private static async Task HandleCloseDumpAsync(
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            output.Error("Not connected or MCP not available.");
            return;
        }

        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Warning("No active session.");
            return;
        }

        if (string.IsNullOrEmpty(state.DumpId))
        {
            // Best-effort recovery: the CLI may have timed out while the server kept opening the dump.
            try
            {
                await DumpStateRecovery.TrySyncOpenedDumpFromServerAsync(state, mcpClient);
            }
            catch
            {
                // Ignore sync failures.
            }

            if (string.IsNullOrEmpty(state.DumpId))
            {
                output.Warning("No dump is currently open.");
                return;
            }
        }

        try
        {
            var result = await output.WithSpinnerAsync(
                "Closing dump...",
                () => mcpClient.CloseDumpAsync(state.SessionId!, state.Settings.UserId));

            if (IsErrorResult(result))
            {
                output.Error(result);
            }
            else
            {
                output.Success(result);
                state.ClearDump();
            }
        }
        catch (McpClientException ex) when (IsNoDumpOpenError(ex))
        {
            output.Warning(ex.Message);
            state.ClearDump();
        }
        catch (McpClientException ex)
        {
            output.Error(ex.Message);
        }
        catch (Exception ex)
        {
            output.Error($"Failed to close dump: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the exec command (execute a debugger command).
    /// </summary>
    private static async Task HandleExecAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            output.Error("Not connected or MCP not available.");
            return;
        }

        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Use 'session create' or 'open <dumpId>' first.");
            return;
        }

        if (args.Length == 0)
        {
            output.Error("Command required. Usage: exec <debugger-command>");
            output.Dim("Examples: exec k, exec !analyze -v, exec !threads");
            return;
        }

        // Join all arguments as the command (allows spaces)
        var command = string.Join(" ", args);

        try
        {
            var result = await output.WithSpinnerAsync(
                $"Executing: [cyan]{command}[/]...",
                () => mcpClient.ExecuteCommandAsync(state.SessionId!, state.Settings.UserId, command));

            // Save result for copy command
            state.SetLastResult($"exec {command}", result);

            // Output result as plain text (debugger output)
            output.WriteLine();
            output.WriteLine(result);
        }
        catch (McpClientException ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (McpClientException ex)
        {
            output.Error(ex.Message);
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (Exception ex)
        {
            output.Error($"Command failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the cmd command (multi-line command mode).
    /// Allows entering multiple debugger commands in sequence without typing 'exec' each time.
    /// </summary>
    // Separate command history for debugger cmd mode
    private static CommandHistory? _cmdHistory;
    
    private static async Task HandleMultiLineCommandAsync(
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            output.Error("Not connected or MCP not available.");
            return;
        }

        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Use 'session create' or 'open <dumpId>' first.");
            return;
        }

        if (string.IsNullOrEmpty(state.DumpId))
        {
            output.Error("No dump loaded. Use 'open <dumpId>' first.");
            return;
        }

        // Initialize cmd history if needed (stored next to CLI binary)
        if (_cmdHistory == null)
        {
            var exePath = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
            var cmdHistoryPath = Path.Combine(exePath, "cmd_history.txt");
            _cmdHistory = new CommandHistory(cmdHistoryPath, maxSize: 500);
        }

        output.Header("Multi-Line Command Mode");
        output.Dim("Enter debugger commands directly. Commands are executed immediately.");
        output.Dim("Use ↑/↓ arrows for command history. Type 'exit' or press Ctrl+C to return.");
        output.Dim("Type '/help' for CLI commands available in this mode (e.g., /showobj).");
        output.WriteLine();

        var debuggerType = state.DebuggerType ?? "Debugger";
        var promptPrefix = debuggerType.ToLowerInvariant() switch
        {
            "windbg" => "0:000>",
            "lldb" => "(lldb)",
            _ => $"({debuggerType})>"
        };

        var systemConsole = new SystemConsole();

        while (true)
        {
            try
            {
                // Read command with history support
                var command = ReadCmdLineWithHistory(console, systemConsole, promptPrefix, _cmdHistory);

                // Check for exit commands or Ctrl+C
                if (command == null)
                {
                    output.Info("Exiting multi-line command mode.");
                    break;
                }
                
                if (string.IsNullOrEmpty(command))
                {
                    continue;
                }

                if (command.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                    command.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                    command.Equals("q", StringComparison.OrdinalIgnoreCase))
                {
                    output.Info("Exiting multi-line command mode.");
                    break;
                }

                // Add to history
                _cmdHistory.Add(command);

                // Handle special CLI commands with / prefix
                if (command.StartsWith("/"))
                {
                    var cliCommand = command[1..].Trim();
                    var cliParts = ParseCommandLine(cliCommand);
                    
                    if (cliParts.Length > 0)
                    {
                        var cliCmd = cliParts[0].ToLowerInvariant();
                        var cliArgs = cliParts.Length > 1 ? cliParts[1..] : [];
                        
                        switch (cliCmd)
                        {
                            case "inspect":
                            case "obj":
                            case "showobj":
                            case "so":
                            case "do":
                            case "dumpobj":
                                await HandleInspectObjectAsync(cliArgs, console, output, state, mcpClient);
                                continue;
                            case "dm":
                            case "dumpmodule":
                                await HandleDumpModuleClrMdAsync(cliArgs, console, output, state, mcpClient);
                                continue;
                            case "modules":
                            case "listmodules":
                                await HandleListModulesClrMdAsync(console, output, state, mcpClient);
                                continue;
                            case "n2e":
                            case "name2ee":
                                await HandleName2EEAsync(cliArgs, console, output, state, mcpClient);
                                continue;
                            case "clrstack":
                            case "cs":
                                await HandleClrStackAsync(cliArgs, console, output, state, mcpClient);
                                continue;
                            case "loadmodules":
                            case "lm":
                                await HandleLoadModulesAsync(cliArgs, console, output, state, mcpClient);
                                continue;
                            case "help":
                                output.Dim("Available / commands in cmd mode:");
                                output.Markup("  [cyan]/inspect <address>[/]  Inspect .NET object/value using ClrMD (safe)");
                                output.Markup("  [cyan]/obj <address>[/]      Alias for /inspect");
                                output.Markup("  [cyan]/inspect --mt <mt>[/]  For value types, provide method table");
                                output.Markup("  [cyan]/inspect --flat[/]     Flat view (depth=1, no recursion)");
                                output.Markup("  [cyan]/dm <address>[/]       Safe module dump using ClrMD (won't crash)");
                                output.Markup("  [cyan]/dumpmodule <addr>[/]  Alias for /dm");
                                output.Markup("  [cyan]/modules[/]            List all .NET modules using ClrMD");
                                output.Markup("  [cyan]/listmodules[/]        Alias for /modules");
                                output.Markup("  [cyan]/n2e <type>[/]         Find type by name (like !name2ee *!Type)");
                                output.Markup("  [cyan]/name2ee <type>[/]     Alias for /n2e");
                                output.Markup("  [cyan]/clrstack[/]           Fast managed stack walk using ClrMD (with registers)");
                                output.Markup("  [cyan]/cs[/]                 Alias for /clrstack");
                                output.Markup("  [cyan]/clrstack <tid>[/]     Stack for specific thread (OS thread ID)");
                                output.Markup("  [cyan]/clrstack --no-regs[/] Skip register fetching (faster)");
                                output.Markup("  [cyan]/loadmodules[/]        Load modules from verifycore at correct addresses");
                                output.Markup("  [cyan]/lm[/]                 Alias for /loadmodules");
                                output.Markup("  [cyan]/help[/]               Show this help");
                                output.WriteLine();
                                continue;
                            default:
                                output.Error($"Unknown / command: {cliCmd}");
                                output.Dim("Type '/help' for available commands.");
                                continue;
                        }
                    }
                }

                // Execute the debugger command
                var result = await mcpClient.ExecuteCommandAsync(state.SessionId!, state.Settings.UserId, command);
                
                // Save result for copy command
                state.SetLastResult($"cmd: {command}", result);
                
                output.WriteLine(result);
            }
            catch (OperationCanceledException)
            {
                output.Info("Exiting multi-line command mode.");
                break;
            }
            catch (McpClientException ex)
            {
                output.Error(ex.Message);
            }
            catch (Exception ex)
            {
                output.Error($"Command failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reads a line of input with history support for cmd mode.
    /// </summary>
    private static string? ReadCmdLineWithHistory(IAnsiConsole console, ISystemConsole systemConsole, string prompt, CommandHistory history)
    {
        var currentLine = string.Empty;
        var cursorPosition = 0;
        string? savedLine = null;

        // Write prompt
        console.Markup($"[cyan]{prompt}[/] ");
        var promptLength = prompt.Length + 1;

        while (true)
        {
            var key = systemConsole.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    systemConsole.WriteLine();
                    history.ResetPosition();
                    return currentLine;

                case ConsoleKey.Escape:
                    // Clear current line
                    ClearCmdLine(systemConsole, promptLength, currentLine.Length);
                    currentLine = string.Empty;
                    cursorPosition = 0;
                    break;

                case ConsoleKey.Backspace:
                    if (cursorPosition > 0)
                    {
                        currentLine = currentLine.Remove(cursorPosition - 1, 1);
                        cursorPosition--;
                        ClearCmdLine(systemConsole, promptLength, currentLine.Length + 1);
                        systemConsole.Write(currentLine);
                        systemConsole.CursorLeft = promptLength + cursorPosition;
                    }
                    break;

                case ConsoleKey.Delete:
                    if (cursorPosition < currentLine.Length)
                    {
                        currentLine = currentLine.Remove(cursorPosition, 1);
                        ClearCmdLine(systemConsole, promptLength, currentLine.Length + 1);
                        systemConsole.Write(currentLine);
                        systemConsole.CursorLeft = promptLength + cursorPosition;
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (cursorPosition > 0)
                    {
                        cursorPosition--;
                        systemConsole.CursorLeft--;
                    }
                    break;

                case ConsoleKey.RightArrow:
                    if (cursorPosition < currentLine.Length)
                    {
                        cursorPosition++;
                        systemConsole.CursorLeft++;
                    }
                    break;

                case ConsoleKey.Home:
                    cursorPosition = 0;
                    systemConsole.CursorLeft = promptLength;
                    break;

                case ConsoleKey.End:
                    cursorPosition = currentLine.Length;
                    systemConsole.CursorLeft = promptLength + currentLine.Length;
                    break;

                case ConsoleKey.UpArrow:
                    // Save current line on first history navigation
                    savedLine ??= currentLine;
                    var previous = history.GetPrevious();
                    if (previous != null)
                    {
                        ClearCmdLine(systemConsole, promptLength, currentLine.Length);
                        currentLine = previous;
                        cursorPosition = currentLine.Length;
                        systemConsole.Write(currentLine);
                    }
                    break;

                case ConsoleKey.DownArrow:
                    var next = history.GetNext();
                    if (next != null)
                    {
                        ClearCmdLine(systemConsole, promptLength, currentLine.Length);
                        currentLine = next;
                        cursorPosition = currentLine.Length;
                        systemConsole.Write(currentLine);
                    }
                    else if (savedLine != null)
                    {
                        ClearCmdLine(systemConsole, promptLength, currentLine.Length);
                        currentLine = savedLine;
                        cursorPosition = currentLine.Length;
                        systemConsole.Write(currentLine);
                        savedLine = null;
                    }
                    break;

                case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    systemConsole.WriteLine("^C");
                    history.ResetPosition();
                    return null; // Signal to exit

                case ConsoleKey.L when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    systemConsole.Clear();
                    console.Markup($"[cyan]{prompt}[/] ");
                    systemConsole.Write(currentLine);
                    systemConsole.CursorLeft = promptLength + cursorPosition;
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        currentLine = currentLine.Insert(cursorPosition, key.KeyChar.ToString());
                        cursorPosition++;
                        ClearCmdLine(systemConsole, promptLength, currentLine.Length - 1);
                        systemConsole.Write(currentLine);
                        systemConsole.CursorLeft = promptLength + cursorPosition;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Clears the command line area for cmd mode.
    /// </summary>
    private static void ClearCmdLine(ISystemConsole systemConsole, int promptLength, int lineLength)
    {
        systemConsole.CursorLeft = promptLength;
        systemConsole.Write(new string(' ', lineLength + 5));
        systemConsole.CursorLeft = promptLength;
    }

    /// <summary>
    /// <summary>
    /// Handles the unified /inspect command (replaces /do and /so).
    /// Uses ClrMD only, supports both reference types and value types.
    /// </summary>
    private static async Task HandleInspectObjectAsync(
        string[] args,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            output.Error("Not connected or MCP not available.");
            return;
        }

        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Use 'open <dumpId>' first.");
            return;
        }

        if (string.IsNullOrEmpty(state.DumpId))
        {
            output.Error("No dump loaded. Use 'open <dumpId>' first.");
            return;
        }

        if (args.Length == 0)
        {
            output.Error("Address required. Usage: /inspect <address> [--mt <methodtable>] [--flat] [--depth <n>] [-o <file>]");
            return;
        }

        // Parse arguments
        var address = args[0];
        string? methodTable = null;
        int maxDepth = 5;
        int maxArrayElements = 10;
        int maxStringLength = 1024;
        string? outputFile = null;

        for (int i = 1; i < args.Length; i++)
        {
            if ((args[i] == "--mt" || args[i] == "-m") && i + 1 < args.Length)
            {
                methodTable = args[++i];
            }
            else if (args[i] == "--flat")
            {
                maxDepth = 1; // Flat = depth 1
            }
            else if ((args[i] == "--depth" || args[i] == "-d") && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var d))
                {
                    maxDepth = d;
                }
            }
            else if ((args[i] == "--array-limit" || args[i] == "-a") && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var a))
                {
                    maxArrayElements = a;
                }
            }
            else if ((args[i] == "--string-limit" || args[i] == "-s") && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var s))
                {
                    maxStringLength = s;
                }
            }
            else if ((args[i] == "--output" || args[i] == "-o") && i + 1 < args.Length)
            {
                outputFile = args[++i];
            }
        }

        try
        {
            var mtInfo = methodTable != null ? $", MT: {methodTable}" : "";
            output.Dim($"Inspecting object at {address} (depth: {maxDepth}{mtInfo})...");

            var result = await output.WithSpinnerAsync(
                "Inspecting object...",
                () => mcpClient.InspectObjectAsync(
                    state.SessionId!,
                    state.Settings.UserId,
                    address,
                    methodTable,
                    maxDepth,
                    maxArrayElements,
                    maxStringLength));

            if (IsErrorResult(result))
            {
                output.Error(result);
                return;
            }

            // Save to file if requested
            if (!string.IsNullOrEmpty(outputFile))
            {
                try
                {
                    await File.WriteAllTextAsync(outputFile, result);
                    output.Success($"Object inspection saved to: {outputFile}");
                }
                catch (Exception ex)
                {
                    output.Error($"Failed to save to file: {ex.Message}");
                }
            }
            else
            {
                // Pretty print JSON to console
                output.WriteLine();
                output.WriteLine(result);
            }

            // Save result for copy command
            state.SetLastResult($"/inspect {address}", result);
        }
        catch (McpClientException ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (McpClientException ex)
        {
            output.Error($"Object inspection failed: {ex.Message}");
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (Exception ex)
        {
            output.Error($"Failed to inspect object: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the /dm (dumpmodule) command using ClrMD - safe alternative that won't crash.
    /// </summary>
    private static async Task HandleDumpModuleClrMdAsync(
        string[] args,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            output.Error("Not connected or MCP not available.");
            return;
        }

        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Use 'open <dumpId>' first.");
            return;
        }

        if (string.IsNullOrEmpty(state.DumpId))
        {
            output.Error("No dump loaded. Use 'open <dumpId>' first.");
            return;
        }

        if (args.Length == 0)
        {
            output.Error("Address required. Usage: /dm <address>");
            return;
        }

        var address = args[0];

        try
        {
            output.Dim($"Inspecting module at {address} using ClrMD (safe mode)...");

            var result = await output.WithSpinnerAsync(
                "Inspecting module...",
                () => mcpClient.DumpModuleAsync(
                    state.SessionId!,
                    state.Settings.UserId,
                    address));

            if (IsErrorResult(result))
            {
                output.Error(result);
                return;
            }

            // Pretty print JSON to console
            output.WriteLine();
            output.WriteLine(result);

            // Save result for copy command
            state.SetLastResult($"/dm {address}", result);
        }
        catch (McpClientException ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (McpClientException ex)
        {
            output.Error($"Module inspection failed: {ex.Message}");
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (Exception ex)
        {
            output.Error($"Failed to inspect module: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the /modules command using ClrMD - lists all modules.
    /// </summary>
    private static async Task HandleListModulesClrMdAsync(
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            output.Error("Not connected or MCP not available.");
            return;
        }

        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Use 'open <dumpId>' first.");
            return;
        }

        if (string.IsNullOrEmpty(state.DumpId))
        {
            output.Error("No dump loaded. Use 'open <dumpId>' first.");
            return;
        }

        try
        {
            output.Dim("Listing modules using ClrMD...");

            var result = await output.WithSpinnerAsync(
                "Listing modules...",
                () => mcpClient.ListModulesAsync(
                    state.SessionId!,
                    state.Settings.UserId));

            if (IsErrorResult(result))
            {
                output.Error(result);
                return;
            }

            // Pretty print JSON to console
            output.WriteLine();
            output.WriteLine(result);

            // Save result for copy command
            state.SetLastResult("/modules", result);
        }
        catch (McpClientException ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (McpClientException ex)
        {
            output.Error($"Module listing failed: {ex.Message}");
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (Exception ex)
        {
            output.Error($"Failed to list modules: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the /n2e (name2ee) command using ClrMD - find type by name.
    /// </summary>
    private static async Task HandleName2EEAsync(
        string[] args,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            output.Error("Not connected or MCP not available.");
            return;
        }

        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Use 'open <dumpId>' first.");
            return;
        }

        if (string.IsNullOrEmpty(state.DumpId))
        {
            output.Error("No dump loaded. Use 'open <dumpId>' first.");
            return;
        }

        if (args.Length == 0)
        {
            output.Error("Type name required. Usage: /n2e <typeName> [moduleName]");
            output.Dim("Examples:");
            output.Dim("  /n2e System.String");
            output.Dim("  /n2e MyNamespace.MyClass");
            output.Dim("  /n2e MyClass MyAssembly.dll");
            return;
        }

        var typeName = args[0];
        var includeAllModules = args.Any(a => a == "--all" || a == "-a");
        // Filter out flags when looking for module name
        var nonFlagArgs = args.Where(a => !a.StartsWith("-")).ToArray();
        var moduleName = nonFlagArgs.Length > 1 ? nonFlagArgs[1] : "*";

        try
        {
            output.Dim($"Searching for type '{typeName}' in {(moduleName == "*" ? "all modules" : moduleName)}...");

            var result = await output.WithSpinnerAsync(
                "Searching...",
                () => mcpClient.Name2EEAsync(
                    state.SessionId!,
                    state.Settings.UserId,
                    typeName,
                    moduleName,
                    includeAllModules));

            if (IsErrorResult(result))
            {
                output.Error(result);
                return;
            }

            // Pretty print result
            output.WriteLine();
            output.WriteLine(result);

            // Save result for copy command
            state.SetLastResult($"/n2e {typeName}", result);
        }
        catch (McpClientException ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (McpClientException ex)
        {
            output.Error($"Name2EE failed: {ex.Message}");
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (Exception ex)
        {
            output.Error($"Failed to search for type: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the /clrstack command - fast managed stack walk using ClrMD.
    /// </summary>
    private static async Task HandleClrStackAsync(
        string[] args,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            output.Error("Not connected or MCP not available.");
            return;
        }

        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Use 'open <dumpId>' first.");
            return;
        }

        if (string.IsNullOrEmpty(state.DumpId))
        {
            output.Error("No dump loaded. Use 'open <dumpId>' first.");
            return;
        }

        // Parse arguments (registers enabled by default, can disable with --no-regs)
        var includeRegisters = !args.Any(a => a == "--no-regs");
        var noArgs = args.Any(a => a == "--no-args");
        var noLocals = args.Any(a => a == "--no-locals");
        uint threadId = 0;

        // Check for thread ID argument (non-flag argument)
        var tidArg = args.FirstOrDefault(a => !a.StartsWith("-"));
        if (tidArg != null)
        {
            if (tidArg.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                uint.TryParse(tidArg[2..], System.Globalization.NumberStyles.HexNumber, null, out threadId);
            else
                uint.TryParse(tidArg, out threadId);
        }

        try
        {
            output.Dim($"Fetching managed stacks via ClrMD{(threadId > 0 ? $" for thread 0x{threadId:X}" : "")}...");

            var result = await output.WithSpinnerAsync(
                "Walking stacks...",
                () => mcpClient.ClrStackAsync(
                    state.SessionId!,
                    state.Settings.UserId,
                    includeArguments: !noArgs,
                    includeLocals: !noLocals,
                    includeRegisters: includeRegisters,
                    threadId: threadId));

            if (IsErrorResult(result))
            {
                output.Error(result);
                return;
            }

            // Parse and pretty-print the result
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(result);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errorProp))
                {
                    output.Error($"ClrStack error: {errorProp.GetString()}");
                    return;
                }

                var totalThreads = root.TryGetProperty("totalThreads", out var tt) ? tt.GetInt32() : 0;
                var totalFrames = root.TryGetProperty("totalFrames", out var tf) ? tf.GetInt32() : 0;
                var durationMs = root.TryGetProperty("durationMs", out var dm) ? dm.GetInt64() : 0;

                output.Success($"Found {totalThreads} threads, {totalFrames} frames ({durationMs}ms)");
                output.WriteLine();

                if (root.TryGetProperty("threads", out var threads))
                {
                    foreach (var thread in threads.EnumerateArray())
                    {
                        var osThreadId = thread.TryGetProperty("osThreadId", out var tid) ? tid.GetUInt32() : 0;
                        var managedThreadId = thread.TryGetProperty("managedThreadId", out var mtid) ? mtid.GetInt32() : 0;
                        var isFaulting = thread.TryGetProperty("isFaulting", out var faulting) && faulting.GetBoolean();

                        var faultMarker = isFaulting ? " [red]<< FAULTING[/]" : "";
                        output.Markup($"[yellow]OS Thread Id: 0x{osThreadId:x}[/] (Managed: {managedThreadId}){faultMarker}");

                        if (thread.TryGetProperty("frames", out var frames))
                        {
                            foreach (var frame in frames.EnumerateArray())
                            {
                                var frameIndex = frame.TryGetProperty("frameIndex", out var fi) ? fi.GetInt32() : 0;
                                var kind = frame.TryGetProperty("kind", out var k) ? k.GetString() : null;
                                
                                string? method = null;
                                if (frame.TryGetProperty("method", out var methodProp) && 
                                    methodProp.TryGetProperty("signature", out var sig))
                                {
                                    method = sig.GetString();
                                }
                                
                                var displayMethod = method ?? kind ?? "???";
                                
                                // Truncate very long method signatures
                                if (displayMethod.Length > 100)
                                    displayMethod = displayMethod[..97] + "...";

                                // Source location
                                var source = "";
                                if (frame.TryGetProperty("sourceLocation", out var srcLoc) &&
                                    srcLoc.TryGetProperty("sourceFile", out var srcFile))
                                {
                                    var fileName = Path.GetFileName(srcFile.GetString() ?? "");
                                    var line = srcLoc.TryGetProperty("lineNumber", out var ln) ? ln.GetInt32() : 0;
                                    if (!string.IsNullOrEmpty(fileName))
                                        source = $" [dim]@ {fileName}:{line}[/]";
                                }

                                output.Markup($"  [green]#{frameIndex:D2}[/] {displayMethod}{source}");

                                // Show arguments if present
                                if (frame.TryGetProperty("arguments", out var argsArray))
                                {
                                    foreach (var arg in argsArray.EnumerateArray())
                                    {
                                        var hasValue = arg.TryGetProperty("hasValue", out var hv) && hv.GetBoolean();
                                        if (!hasValue) continue;

                                        var name = arg.TryGetProperty("name", out var n) ? n.GetString() : "arg";
                                        var valueStr = arg.TryGetProperty("valueString", out var vs) ? vs.GetString() : null;
                                        if (valueStr != null)
                                        {
                                            // Truncate long values
                                            if (valueStr.Length > 60)
                                                valueStr = valueStr[..57] + "...";
                                            output.Markup($"      [dim]{name}[/] = [cyan]{valueStr}[/]");
                                        }
                                    }
                                }

                                // Show locals if present (first 5 only)
                                if (frame.TryGetProperty("locals", out var localsArray))
                                {
                                    var count = 0;
                                    foreach (var local in localsArray.EnumerateArray())
                                    {
                                        if (count++ >= 5) break;

                                        var hasValue = local.TryGetProperty("hasValue", out var hv) && hv.GetBoolean();
                                        if (!hasValue) continue;

                                        var index = local.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
                                        var name = local.TryGetProperty("name", out var n) ? n.GetString() : null;
                                        var displayName = name ?? $"local_{index}";
                                        var valueStr = local.TryGetProperty("valueString", out var vs) ? vs.GetString() : null;
                                        if (valueStr != null)
                                        {
                                            if (valueStr.Length > 60)
                                                valueStr = valueStr[..57] + "...";
                                            output.Markup($"      [dim]{displayName}[/] = [blue]{valueStr}[/]");
                                        }
                                    }
                                }
                            }
                        }

                        // Show registers if present
                        if (thread.TryGetProperty("topFrameRegisters", out var regs))
                        {
                            var sp = regs.TryGetProperty("stackPointer", out var spVal) ? spVal.GetUInt64() : 0;
                            var pc = regs.TryGetProperty("programCounter", out var pcVal) ? pcVal.GetUInt64() : 0;
                            output.Dim($"  Registers: SP=0x{sp:X} PC=0x{pc:X}");
                        }

                        output.WriteLine();
                    }
                }
            }
            catch
            {
                // If parsing fails, just show raw JSON
                output.WriteLine(result);
            }

            // Save result for copy command
            state.SetLastResult("/clrstack", result);
        }
        catch (McpClientException ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (McpClientException ex)
        {
            output.Error($"ClrStack failed: {ex.Message}");
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (Exception ex)
        {
            output.Error($"Failed to get stacks: {ex.Message}");
        }
    }


    /// <summary>
    /// Handles the /loadmodules command in cmd mode.
    /// </summary>
    private static async Task HandleLoadModulesAsync(
        string[] args,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            output.Error("Not connected or MCP not available.");
            return;
        }

        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Use 'open <dumpId>' first.");
            return;
        }

        try
        {
            // Module names are optional - can be comma-separated list
            string? moduleNames = args.Length > 0 ? string.Join(",", args) : null;

            if (moduleNames != null)
            {
                output.Info($"Loading modules: {moduleNames}...");
            }
            else
            {
                output.Info("Loading all available modules from verifycore...");
            }

            var result = await mcpClient.LoadVerifyCoreModulesAsync(state.SessionId, state.Settings.UserId, moduleNames);

            if (IsErrorResult(result))
            {
                output.Error(result);
                return;
            }

            // Display results
            foreach (var line in result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Successfully"))
                {
                    output.Success(line);
                }
                else if (line.StartsWith("Failed"))
                {
                    output.Warning(line);
                }
                else if (line.StartsWith("Tip:"))
                {
                    output.Dim(line);
                }
                else
                {
                    output.WriteLine(line);
                }
            }
        }
        catch (McpClientException ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (McpClientException ex)
        {
            output.Error($"Failed to load modules: {ex.Message}");
        }
        catch (Exception ex)
        {
            output.Error($"Failed to load modules: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the analyze command (crash analysis).
    /// </summary>
    private static async Task HandleAnalyzeAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            output.Error("Not connected or MCP not available.");
            return;
        }

        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Use 'open <dumpId>' first.");
            return;
        }

        if (args.Length == 0)
        {
            output.Error("Analysis type required.");
            output.Dim("Usage: analyze <type>");
            output.WriteLine();
            output.Markup("[bold]Analysis Types:[/]");
            output.Markup("  [cyan]crash[/]       General crash analysis");
            output.Markup("  [cyan]dotnet[/]      .NET-specific analysis");
            output.Markup("  [cyan]perf[/]        Performance profiling summary");
            output.Markup("  [cyan]cpu[/]         CPU usage analysis");
            output.Markup("  [cyan]memory[/]      Memory allocation analysis");
            output.Markup("  [cyan]gc[/]          Garbage collection analysis");
            output.Markup("  [cyan]threads[/]     Thread contention analysis");
            output.Markup("  [cyan]security[/]    Security vulnerability scan");
            return;
        }

        var analysisType = args[0].ToLowerInvariant();

        try
        {
            switch (analysisType)
            {
                case "crash":
                    await RunAnalysisAsync(output, "Crash Analysis",
                        () => mcpClient.AnalyzeCrashAsync(state.SessionId!, state.Settings.UserId), state);
                    break;

                case "dotnet":
                case ".net":
                case "net":
                    await RunAnalysisAsync(output, ".NET Analysis",
                        () => mcpClient.AnalyzeDotNetAsync(state.SessionId!, state.Settings.UserId), state);
                    break;

                case "perf":
                case "performance":
                    await RunAnalysisAsync(output, "Performance Analysis",
                        () => mcpClient.AnalyzePerformanceAsync(state.SessionId!, state.Settings.UserId), state);
                    break;

                case "cpu":
                    await RunAnalysisAsync(output, "CPU Usage Analysis",
                        () => mcpClient.AnalyzeCpuUsageAsync(state.SessionId!, state.Settings.UserId), state);
                    break;

                case "memory":
                case "alloc":
                case "allocations":
                    await RunAnalysisAsync(output, "Memory Allocation Analysis",
                        () => mcpClient.AnalyzeAllocationsAsync(state.SessionId!, state.Settings.UserId), state);
                    break;

                case "gc":
                case "garbage":
                    await RunAnalysisAsync(output, "Garbage Collection Analysis",
                        () => mcpClient.AnalyzeGcAsync(state.SessionId!, state.Settings.UserId), state);
                    break;

                case "threads":
                case "contention":
                case "locks":
                    await RunAnalysisAsync(output, "Thread Contention Analysis",
                        () => mcpClient.AnalyzeContentionAsync(state.SessionId!, state.Settings.UserId), state);
                    break;

                case "security":
                case "vuln":
                case "vulnerabilities":
                    await RunAnalysisAsync(output, "Security Vulnerability Analysis",
                        () => mcpClient.AnalyzeSecurityAsync(state.SessionId!, state.Settings.UserId), state);
                    break;

                default:
                    output.Error($"Unknown analysis type: {analysisType}");
                    output.Dim("Available types: crash, dotnet, perf, cpu, memory, gc, threads, security");
                    break;
            }
        }
        catch (McpClientException ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (McpClientException ex)
        {
            output.Error(ex.Message);
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (Exception ex)
        {
            output.Error($"Analysis failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Helper to run analysis with spinner and formatted output.
    /// </summary>
    private static async Task RunAnalysisAsync(
        ConsoleOutput output,
        string analysisName,
        Func<Task<string>> analyzeFunc,
        ShellState? state = null)
    {
        output.Dim($"Starting {analysisName}...");
        output.Dim("This involves executing debugger commands and parsing output.");
        output.WriteLine();
        
        var result = await output.WithSpinnerAsync(
            $"Analyzing (executing debugger commands)...",
            analyzeFunc);

        // Save result for copy command
        state?.SetLastResult($"analyze: {analysisName}", result);

        output.Success($"{analysisName} complete!");
        output.Header($"{analysisName} Results");
        output.WriteLine();
        output.WriteLine(result);
    }

    /// <summary>
    /// Handles the compare command (dump comparison).
    /// </summary>
    private static async Task HandleCompareAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            output.Error("Not connected or MCP not available.");
            return;
        }

        if (args.Length < 2)
        {
            output.Error("Comparison requires baseline and target session IDs.");
            output.WriteLine();
            output.Markup("[bold]Usage:[/]");
            output.Markup("  compare [[type]] <baseline-session> <target-session>");
            output.WriteLine();
            output.Markup("[bold]Comparison Types:[/]");
            output.Markup("  [cyan]all[/]         Full comparison (default)");
            output.Markup("  [cyan]heap[/]        Heap/memory comparison");
            output.Markup("  [cyan]threads[/]     Thread comparison");
            output.Markup("  [cyan]modules[/]     Loaded modules comparison");
            output.WriteLine();
            output.Markup("[bold]Examples:[/]");
            output.Markup("  [yellow]compare session1 session2[/]");
            output.Markup("  [yellow]compare heap session1 session2[/]");
            output.Dim("Tip: Use 'session list' to see available sessions");
            return;
        }

        // Parse arguments
        string comparisonType;
        string baselineSession;
        string targetSession;

        // Check if first arg is a comparison type or session ID
        var firstArg = args[0].ToLowerInvariant();
        if (firstArg is "all" or "heap" or "threads" or "modules" or "full" or "summary")
        {
            if (args.Length < 3)
            {
                output.Error("Missing session IDs. Usage: compare <type> <baseline> <target>");
                return;
            }
            comparisonType = firstArg;
            baselineSession = args[1];
            targetSession = args[2];
        }
        else
        {
            comparisonType = "all";
            baselineSession = args[0];
            targetSession = args[1];
        }

        // Resolve partial session IDs
        var resolvedBaseline = await ResolvePartialSessionIdAsync(baselineSession, output, state, mcpClient);
        if (resolvedBaseline == null)
        {
            return;
        }

        var resolvedTarget = await ResolvePartialSessionIdAsync(targetSession, output, state, mcpClient);
        if (resolvedTarget == null)
        {
            return;
        }

        try
        {
            switch (comparisonType)
            {
                case "all":
                case "full":
                case "summary":
                    await RunComparisonAsync(output, "Full Dump Comparison",
                        () => mcpClient.CompareDumpsAsync(
                            resolvedBaseline, state.Settings.UserId,
                            resolvedTarget, state.Settings.UserId));
                    break;

                case "heap":
                case "memory":
                    await RunComparisonAsync(output, "Heap Comparison",
                        () => mcpClient.CompareHeapsAsync(
                            resolvedBaseline, state.Settings.UserId,
                            resolvedTarget, state.Settings.UserId));
                    break;

                case "threads":
                case "thread":
                    await RunComparisonAsync(output, "Thread Comparison",
                        () => mcpClient.CompareThreadsAsync(
                            resolvedBaseline, state.Settings.UserId,
                            resolvedTarget, state.Settings.UserId));
                    break;

                case "modules":
                case "module":
                    await RunComparisonAsync(output, "Module Comparison",
                        () => mcpClient.CompareModulesAsync(
                            resolvedBaseline, state.Settings.UserId,
                            resolvedTarget, state.Settings.UserId));
                    break;

                default:
                    output.Error($"Unknown comparison type: {comparisonType}");
                    output.Dim("Available types: all, heap, threads, modules");
                    break;
            }
        }
        catch (McpClientException ex)
        {
            output.Error(ex.Message);
        }
        catch (Exception ex)
        {
            output.Error($"Comparison failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Helper to run comparison with spinner and formatted output.
    /// </summary>
    private static async Task RunComparisonAsync(
        ConsoleOutput output,
        string comparisonName,
        Func<Task<string>> compareFunc)
    {
        var result = await output.WithSpinnerAsync(
            $"Running {comparisonName}...",
            compareFunc);

        output.Header($"{comparisonName} Results");
        output.WriteLine();
        output.WriteLine(result);
    }

    /// <summary>
    /// Handles the watch command (watch expression management).
    /// </summary>
    private static async Task HandleWatchAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            output.Error("Not connected or MCP not available.");
            return;
        }

        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Use 'open <dumpId>' first.");
            return;
        }

        if (args.Length == 0)
        {
            // Default: list watches
            await HandleWatchListAsync(output, state, mcpClient);
            return;
        }

        var subcommand = args[0].ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();

        try
        {
            switch (subcommand)
            {
                case "add":
                case "a":
                    await HandleWatchAddAsync(subArgs, output, state, mcpClient);
                    break;

                case "list":
                case "ls":
                case "l":
                    await HandleWatchListAsync(output, state, mcpClient);
                    break;

                case "eval":
                case "evaluate":
                case "e":
                    await HandleWatchEvalAsync(subArgs, output, state, mcpClient);
                    break;

                case "remove":
                case "rm":
                case "delete":
                case "del":
                    await HandleWatchRemoveAsync(subArgs, output, state, mcpClient);
                    break;

                case "clear":
                    await HandleWatchClearAsync(output, state, mcpClient);
                    break;

                default:
                    // Treat as expression to add
                    await HandleWatchAddAsync(args, output, state, mcpClient);
                    break;
            }
        }
        catch (McpClientException ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (McpClientException ex)
        {
            output.Error(ex.Message);
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (Exception ex)
        {
            output.Error($"Watch operation failed: {ex.Message}");
        }
    }

    private static async Task HandleWatchAddAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (args.Length == 0)
        {
            output.Error("Expression required. Usage: watch add <expression> [--name <name>]");
            return;
        }

        string expression;
        string? name = null;

        // Parse --name option
        var argList = args.ToList();
        var nameIdx = argList.FindIndex(a => a == "--name" || a == "-n");
        if (nameIdx >= 0 && nameIdx + 1 < argList.Count)
        {
            name = argList[nameIdx + 1];
            argList.RemoveAt(nameIdx + 1);
            argList.RemoveAt(nameIdx);
        }

        expression = string.Join(" ", argList);

        try
        {
            var result = await output.WithSpinnerAsync(
                "Adding watch...",
                () => mcpClient.AddWatchAsync(state.SessionId!, state.Settings.UserId, expression, name));

            if (IsErrorResult(result))
            {
                output.Error(result);
            }
            else
            {
                output.Success("Watch added!");
                output.WriteLine(result);
            }
        }
        catch (McpClientException ex)
        {
            output.Error($"Failed to add watch: {ex.Message}");
        }
    }

    private static async Task HandleWatchListAsync(
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        var result = await output.WithSpinnerAsync(
            "Listing watches...",
            () => mcpClient.ListWatchesAsync(state.SessionId!, state.Settings.UserId));

        output.Header("Watches");
        output.WriteLine();
        output.WriteLine(result);
    }

    private static async Task HandleWatchEvalAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (args.Length == 0)
        {
            // Evaluate all watches
            var allResult = await output.WithSpinnerAsync(
                "Evaluating all watches...",
                () => mcpClient.EvaluateWatchesAsync(state.SessionId!, state.Settings.UserId));

            output.Header("Watch Values");
            output.WriteLine();
            output.WriteLine(allResult);
        }
        else
        {
            // Evaluate specific watch
            var watchId = args[0];
            var result = await output.WithSpinnerAsync(
                $"Evaluating watch {watchId}...",
                () => mcpClient.EvaluateWatchAsync(state.SessionId!, state.Settings.UserId, watchId));

            output.Header($"Watch {watchId} Value");
            output.WriteLine();
            output.WriteLine(result);
        }
    }

    private static async Task HandleWatchRemoveAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (args.Length == 0)
        {
            output.Error("Watch ID required. Usage: watch remove <watchId>");
            output.Dim("Use 'watch list' to see available watch IDs.");
            return;
        }

        var watchId = args[0];
        var result = await output.WithSpinnerAsync(
            $"Removing watch {watchId}...",
            () => mcpClient.RemoveWatchAsync(state.SessionId!, state.Settings.UserId, watchId));

        output.Success($"Watch {watchId} removed.");
        output.WriteLine(result);
    }

    private static async Task HandleWatchClearAsync(
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        var result = await output.WithSpinnerAsync(
            "Clearing all watches...",
            () => mcpClient.ClearWatchesAsync(state.SessionId!, state.Settings.UserId));

        output.Success("All watches cleared.");
        output.WriteLine(result);
    }

    /// <summary>
    /// Handles the report command (report generation).
    /// </summary>
    private static async Task HandleReportAsync(
        string[] args,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient,
        HttpApiClient? httpClient = null)
    {
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            output.Error("Not connected or MCP not available.");
            return;
        }

        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Use 'open <dumpId>' first.");
            return;
        }

        // Parse options
        var format = "markdown";
        var outputFile = (string?)null;
        var summary = false;
        var includeWatches = true;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--format" or "-f" when i + 1 < args.Length:
                    format = args[++i].ToLowerInvariant();
                    break;
                case "--output" or "-o" when i + 1 < args.Length:
                    outputFile = args[++i];
                    break;
                case "--summary" or "-s":
                    summary = true;
                    break;
                case "--no-watches":
                    includeWatches = false;
                    break;
                case "summary":
                    summary = true;
                    break;
                case "markdown" or "md":
                    format = "markdown";
                    break;
                case "html":
                    format = "html";
                    break;
                case "json":
                    format = "json";
                    break;
            }
        }

        // Validate format
        if (format is not ("markdown" or "html" or "json"))
        {
            output.Error($"Invalid format: {format}. Use 'markdown', 'html', or 'json'.");
            return;
        }

        if (string.IsNullOrWhiteSpace(outputFile))
        {
            output.Error("Output file is required. Reports are too large to display in the terminal.");
            output.Dim("Usage: report -o <file> [-f markdown|html|json] [--summary] [--no-watches]");
            output.Dim("Examples: report -o ./report.md | report -o ./report.json -f json | report -o ./summary.json --summary -f json");
            return;
        }

        try
        {
            string result;
            var spinnerText = summary ? "Generating summary report..." : "Generating full report...";
                
            if (summary)
            {
                result = await output.WithSpinnerAsync(
                    spinnerText,
                    () => mcpClient.GenerateSummaryReportAsync(state.SessionId!, state.Settings.UserId, format));
            }
            else
            {
                result = await output.WithSpinnerAsync(
                    spinnerText,
                    () => mcpClient.GenerateReportAsync(state.SessionId!, state.Settings.UserId, format, includeWatches));
            }

            // Store result for copy command
            state.SetLastResult(summary ? "report --summary" : "report", result);

            // Save to file
            var fullPath = Path.GetFullPath(outputFile);
            await File.WriteAllTextAsync(fullPath, result);
            output.Success($"Report saved to: {fullPath}");
        }
        catch (McpClientException ex)
        {
            output.Error(ex.Message);
        }
        catch (Exception ex)
        {
            output.Error($"Report generation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the sourcelink command (Source Link resolution).
    /// </summary>
    private static async Task HandleSourceLinkAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            output.Error("Not connected or MCP not available.");
            return;
        }

        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Use 'open <dumpId>' first.");
            return;
        }

        if (args.Length == 0)
        {
            // Show info
            await HandleSourceLinkInfoAsync(output, state, mcpClient);
            return;
        }

        var subcommand = args[0].ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();

        try
        {
            switch (subcommand)
            {
                case "resolve":
                case "r":
                    await HandleSourceLinkResolveAsync(subArgs, output, state, mcpClient);
                    break;

                case "info":
                case "i":
                    await HandleSourceLinkInfoAsync(output, state, mcpClient);
                    break;

                default:
                    // Treat as source file to resolve
                    await HandleSourceLinkResolveAsync(args, output, state, mcpClient);
                    break;
            }
        }
        catch (McpClientException ex)
        {
            output.Error(ex.Message);
        }
        catch (Exception ex)
        {
            output.Error($"Source Link operation failed: {ex.Message}");
        }
    }

    private static async Task HandleSourceLinkResolveAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (args.Length == 0)
        {
            output.Error("Source file path required. Usage: sourcelink resolve <path> [line]");
            return;
        }

        var sourceFile = args[0];
        int? lineNumber = null;

        if (args.Length > 1 && int.TryParse(args[1], out var line))
        {
            lineNumber = line;
        }

        var result = await output.WithSpinnerAsync(
            "Resolving Source Link...",
            () => mcpClient.ResolveSourceLinkAsync(state.SessionId!, state.Settings.UserId, sourceFile, lineNumber));

        output.Header("Source Link Resolution");
        output.WriteLine();
        output.WriteLine(result);
    }

    private static async Task HandleSourceLinkInfoAsync(
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        var result = await output.WithSpinnerAsync(
            "Getting Source Link info...",
            () => mcpClient.GetSourceLinkInfoAsync(state.SessionId!, state.Settings.UserId));

        output.Header("Source Link Configuration");
        output.WriteLine();
        output.WriteLine(result);
    }

    /// <summary>
    /// Handles quick debugger commands (threads, stack).
    /// </summary>
    private static async Task HandleQuickCommandAsync(
        string commandType,
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            output.Error("Not connected or MCP not available.");
            return;
        }

        if (string.IsNullOrEmpty(state.SessionId))
        {
            output.Error("No active session. Use 'open <dumpId>' first.");
            return;
        }

        // Map quick commands to actual debugger commands
        var debuggerCommand = commandType switch
        {
            "threads" => state.DebuggerType == "LLDB" ? "thread list" : "~*",
            "stack" => state.DebuggerType == "LLDB" ? "bt" : "k",
            _ => commandType
        };

        try
        {
            var result = await output.WithSpinnerAsync(
                $"Getting {commandType}...",
                () => mcpClient.ExecuteCommandAsync(state.SessionId!, state.Settings.UserId, debuggerCommand));

            // Save result for copy command
            state.SetLastResult(commandType, result);

            output.Header(commandType.ToUpperInvariant());
            output.WriteLine();
            output.WriteLine(result);
        }
        catch (McpClientException ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (McpClientException ex)
        {
            output.Error(ex.Message);
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex))
        {
            await TryRecoverSessionAsync(output, state, mcpClient);
        }
        catch (Exception ex)
        {
            output.Error($"Command failed: {ex.Message}");
        }
    }



    /// <summary>
    /// Handles server management commands in interactive shell.
    /// </summary>
    private static async Task HandleServerCommandAsync(
        string[] args,
        IAnsiConsole console,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient,
        McpClient mcpClient)
    {
        if (args.Length == 0)
        {
            output.Error("Usage: server <list|add|remove|switch|init>");
            output.Dim("  server list              - List all configured servers");
            output.Dim("  server add <url>         - Add a server");
            output.Dim("  server remove <url|name> - Remove a server");
            output.Dim("  server switch <url|name> - Switch to a server");
            output.Dim("  server init              - Create default config");
            return;
        }

        var subcommand = args[0].ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();
        var configManager = new ServerConfigManager();
        var discovery = new ServerDiscovery(configManager);

        switch (subcommand)
        {
            case "list":
            case "ls":
                await HandleServerListAsync(output, configManager, discovery);
                break;

            case "add":
                await HandleServerAddAsync(subArgs, console, output, configManager, discovery);
                break;

            case "remove":
            case "rm":
                await HandleServerRemoveAsync(subArgs, output, configManager, discovery);
                break;

            case "switch":
            case "sw":
                await HandleServerSwitchAsync(subArgs, output, state, httpClient, mcpClient, configManager, discovery);
                break;

            case "init":
                HandleServerInit(console, output, configManager);
                break;

            default:
                output.Error($"Unknown server subcommand: {subcommand}");
                output.Dim("Use: server <list|add|remove|switch|init>");
                break;
        }
    }

    private static async Task HandleServerListAsync(
        ConsoleOutput output,
        ServerConfigManager configManager,
        ServerDiscovery discovery)
    {
        var servers = configManager.GetServers();
        if (servers.Count == 0)
        {
            output.Info("No servers configured. Use 'server add <url>' to add a server.");
            output.Info($"Config file: {configManager.ConfigPath}");
            return;
        }

        await output.WithSpinnerAsync("Discovering servers...", async () =>
        {
            await discovery.DiscoverAllAsync();
            return true;
        });

        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("#").Centered());
        table.AddColumn(new TableColumn("URL").LeftAligned());
        table.AddColumn(new TableColumn("Arch").Centered());
        table.AddColumn(new TableColumn("Distro").Centered());
        table.AddColumn(new TableColumn("Status").Centered());
        table.AddColumn(new TableColumn("Name").LeftAligned());

        var index = 1;
        foreach (var server in discovery.Servers)
        {
            var statusColor = server.IsOnline ? "green" : "red";
            var status = server.IsOnline ? "online" : server.ErrorMessage ?? "offline";
            var arch = server.Capabilities?.Architecture ?? "-";
            var distro = server.Capabilities?.IsAlpine == true ? "alpine" :
                (server.Capabilities?.Distribution ?? "-");
            var name = server.Name;

            table.AddRow(
                $"[dim]{index}[/]",
                $"[cyan]{server.ShortUrl}[/]",
                arch,
                distro,
                $"[{statusColor}]{status}[/]",
                server.IsOnline ? $"[yellow]{name}[/]" : "[dim]-[/]"
            );
            index++;
        }

        output.WriteLine();
        output.Console.Write(table);
        output.WriteLine();
        output.Dim($"Servers: {servers.Count} configured, {discovery.OnlineCount} online");
        output.Dim($"Config: {configManager.ConfigPath}");

        if (discovery.CurrentServer != null)
        {
            output.Dim($"Current: {discovery.CurrentServer.Name} ({discovery.CurrentServer.ShortUrl})");
        }
    }

    private static async Task HandleServerAddAsync(
        string[] args,
        IAnsiConsole console,
        ConsoleOutput output,
        ServerConfigManager configManager,
        ServerDiscovery discovery)
    {
        if (args.Length == 0)
        {
            output.Error("Usage: server add <url> [--api-key <key>]");
            return;
        }

        var url = args[0];
        string? apiKey = null;

        // Parse --api-key option
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--api-key" || args[i] == "-k")
            {
                apiKey = args[i + 1];
                break;
            }
        }

        // Normalize URL
        if (!url.Contains("://"))
        {
            url = "http://" + url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            output.Error($"Invalid URL format: {url}");
            return;
        }

        // Check if already exists
        var existing = configManager.GetServers();
        if (existing.Any(s => s.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
        {
            output.Warning($"Server already exists: {url}");
            return;
        }

        // Try to discover capabilities first
        var tempConfig = new ServerConfigManager();
        tempConfig.AddServer(url, apiKey);
        var tempDiscovery = new ServerDiscovery(tempConfig);

        DiscoveredServer? discovered = null;
        await output.WithSpinnerAsync("Discovering server capabilities...", async () =>
        {
            var servers = await tempDiscovery.DiscoverAllAsync();
            discovered = servers.FirstOrDefault();
            return true;
        });

        if (discovered == null || !discovered.IsOnline)
        {
            output.Warning($"Server is not reachable: {discovered?.ErrorMessage ?? "unknown error"}");
            if (!console.Confirm("Add anyway?", false))
            {
                return;
            }
        }

        if (configManager.AddServer(url, apiKey))
        {
            if (discovered?.IsOnline == true)
            {
                output.Success($"Added server: [yellow]{discovered.Name}[/] ({url})");
            }
            else
            {
                output.Success($"Added server: {url}");
            }
        }
        else
        {
            output.Error("Failed to add server");
        }
    }

    private static async Task HandleServerRemoveAsync(
        string[] args,
        ConsoleOutput output,
        ServerConfigManager configManager,
        ServerDiscovery discovery)
    {
        if (args.Length == 0)
        {
            output.Error("Usage: server remove <url|name>");
            return;
        }

        var urlOrName = args[0];

        // First discover to resolve names
        await output.WithSpinnerAsync("Discovering servers...", async () =>
        {
            await discovery.DiscoverAllAsync();
            return true;
        });

        // Try to find by name first
        var server = discovery.FindServer(urlOrName);
        string urlToRemove;

        if (server != null)
        {
            urlToRemove = server.Url;
        }
        else
        {
            urlToRemove = urlOrName;
            if (!urlToRemove.Contains("://"))
            {
                urlToRemove = "http://" + urlToRemove;
            }
        }

        if (configManager.RemoveServer(urlToRemove))
        {
            output.Success($"Removed server: {urlOrName}");
        }
        else
        {
            output.Error($"Server not found: {urlOrName}");
        }
    }

    private static async Task HandleServerSwitchAsync(
        string[] args,
        ConsoleOutput output,
        ShellState state,
        HttpApiClient httpClient,
        McpClient mcpClient,
        ServerConfigManager configManager,
        ServerDiscovery discovery)
    {
        if (args.Length == 0)
        {
            output.Error("Usage: server switch <url|name>");
            return;
        }

        var urlOrName = args[0];

        // Discover servers
        await output.WithSpinnerAsync("Discovering servers...", async () =>
        {
            await discovery.DiscoverAllAsync();
            return true;
        });

        // Find the target server
        var server = discovery.FindServer(urlOrName);
        if (server == null)
        {
            output.Error($"Server not found: {urlOrName}");
            output.Info("Use 'server list' to see available servers.");
            return;
        }

        if (!server.IsOnline)
        {
            output.Error($"Server is offline: {server.ShortUrl} ({server.ErrorMessage})");
            return;
        }

        output.Markup($"[blue]ℹ[/] Switching to [yellow]{server.Name}[/]...");

        try
        {
            // Disconnect from current MCP server
            await mcpClient.DisconnectAsync();

            // Update settings
            state.Settings.ServerUrl = server.Url;
            state.Settings.ApiKey = server.ApiKey;
            state.Settings.Save();

            // Reconnect HTTP client
            httpClient.Configure(server.Url, server.ApiKey, state.Settings.Timeout);

            // Check health
            var health = await httpClient.CheckHealthAsync();
            if (!health.IsHealthy)
            {
                output.Error($"Server {server.Name} is not healthy: {health.Status}");
                return;
            }

            // Update state
            state.SetConnected(server.Url);

            // Fetch and update server info
            var serverInfo = await httpClient.GetServerInfoAsync();
            if (serverInfo != null)
            {
                state.ServerInfo = serverInfo;
            }

            // Reconnect MCP client
            await mcpClient.ConnectAsync(server.Url, server.ApiKey);

            output.Markup($"[green]✓[/] Switched to [yellow]{server.Name}[/] ({server.ShortUrl})");

            // Clear session since we're on a new server
            if (!string.IsNullOrEmpty(state.SessionId))
            {
                output.Dim("Session cleared (will create new session on this server)");
                state.ClearSession();
            }

            // Show server info
            if (state.ServerInfo != null)
            {
                output.KeyValue("Host", state.ServerInfo.Description);
                if (state.ServerInfo.IsAlpine)
                {
                    output.Warning("⚠️  Alpine host - can only debug Alpine .NET dumps");
                }
            }
            else if (server.Capabilities != null)
            {
                output.KeyValue("Architecture", server.Capabilities.Architecture);
                output.KeyValue("Distribution", server.Capabilities.IsAlpine ? "Alpine" : (server.Capabilities.Distribution ?? "Linux"));
            }
        }
        catch (Exception ex)
        {
            output.Error($"Failed to switch server: {ex.Message}");
        }
    }

    private static void HandleServerInit(
        IAnsiConsole console,
        ConsoleOutput output,
        ServerConfigManager configManager)
    {
        if (File.Exists(configManager.ConfigPath))
        {
            if (!console.Confirm($"Config already exists at {configManager.ConfigPath}. Overwrite?", false))
            {
                return;
            }
        }

        configManager.CreateDefaultConfig();
        output.Success($"Created default configuration at: {configManager.ConfigPath}");
        output.Info("Default servers:");
        output.Info("  - http://localhost:5000 (debian-arm64)");
        output.Info("  - http://localhost:5001 (alpine-arm64)");
        output.Info("  - http://localhost:5002 (debian-x64)");
        output.Info("  - http://localhost:5003 (alpine-x64)");
    }


    /// <summary>
    /// Checks if a dump is compatible with the connected server and shows a warning if not.
    /// </summary>
    /// <param name="isAlpineDump">Whether the dump is from Alpine Linux (musl).</param>
    /// <param name="dumpArchitecture">The dump's processor architecture (e.g., "arm64", "x64").</param>
    /// <param name="state">The current shell state containing server info.</param>
    /// <param name="output">Console output for displaying warnings.</param>
    private static void CheckDumpServerCompatibility(bool? isAlpineDump, string? dumpArchitecture, ShellState state, ConsoleOutput output)
    {
        var serverInfo = state.ServerInfo;
        if (serverInfo == null)
        {
            return; // No server info available, can't check compatibility
        }

        var hasIncompatibility = false;

        // Check Alpine/glibc compatibility
        if (isAlpineDump.HasValue)
        {
            var isAlpineServer = serverInfo.IsAlpine;

            if (isAlpineDump.Value && !isAlpineServer)
            {
                // Alpine dump on glibc server
                output.WriteLine();
                output.Error("⚠️  INCOMPATIBLE: This Alpine (musl) dump cannot be debugged on a glibc server!");
                output.Dim($"   Connected server: {serverInfo.Description}");
                output.Dim("   Connect to an Alpine server to debug this dump.");
                hasIncompatibility = true;
            }
            else if (!isAlpineDump.Value && isAlpineServer)
            {
                // glibc dump on Alpine server
                output.WriteLine();
                output.Error("⚠️  INCOMPATIBLE: This glibc dump cannot be debugged on an Alpine server!");
                output.Dim($"   Connected server: {serverInfo.Description}");
                output.Dim("   Connect to a glibc (Debian/Ubuntu) server to debug this dump.");
                hasIncompatibility = true;
            }
        }

        // Check architecture compatibility
        if (!string.IsNullOrEmpty(dumpArchitecture) && !string.IsNullOrEmpty(serverInfo.Architecture))
        {
            // Normalize architectures for comparison
            var normalizedDumpArch = NormalizeArchitecture(dumpArchitecture);
            var normalizedServerArch = NormalizeArchitecture(serverInfo.Architecture);

            if (!string.Equals(normalizedDumpArch, normalizedServerArch, StringComparison.OrdinalIgnoreCase))
            {
                if (!hasIncompatibility) output.WriteLine();
                output.Error($"⚠️  INCOMPATIBLE: This {dumpArchitecture} dump cannot be debugged on an {serverInfo.Architecture} server!");
                output.Dim($"   Connected server: {serverInfo.Description}");
                output.Dim($"   Connect to an {dumpArchitecture} server to debug this dump.");
            }
        }
    }

    private static bool IsLikelyIncompatibleWithCurrentServer(bool? isAlpineDump, string? dumpArchitecture, ShellState state)
    {
        var serverInfo = state.ServerInfo;
        if (serverInfo == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(dumpArchitecture) && !string.IsNullOrWhiteSpace(serverInfo.Architecture))
        {
            var normalizedDumpArch = NormalizeArchitecture(dumpArchitecture);
            var normalizedServerArch = NormalizeArchitecture(serverInfo.Architecture);
            if (!string.Equals(normalizedDumpArch, normalizedServerArch, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (isAlpineDump.HasValue)
        {
            if (serverInfo.IsAlpine != isAlpineDump.Value)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Normalizes architecture names for comparison.
    /// </summary>
    private static string NormalizeArchitecture(string arch)
    {
        return arch.ToLowerInvariant() switch
        {
            "aarch64" or "arm64" => "arm64",
            "x86_64" or "x64" or "amd64" => "x64",
            "i386" or "i686" or "x86" => "x86",
            "armv7" or "arm" => "arm",
            _ => arch.ToLowerInvariant()
        };
    }

    /// <summary>
    /// Formats bytes as a human-readable string.
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        double size = bytes;

        while (size >= 1024 && i < suffixes.Length - 1)
        {
            size /= 1024;
            i++;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", size, suffixes[i]);
    }


    /// <summary>
    /// Checks if an exception indicates a session not found error (session expired or removed).
    /// </summary>
    private static bool IsSessionNotFoundError(Exception ex)
    {
        return SessionExpiryHandler.IsSessionExpired(ex);
    }

    private static bool IsToolResponseTimeoutError(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("timed out", StringComparison.OrdinalIgnoreCase) &&
               message.Contains("waiting for response", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDumpAlreadyOpenError(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("dump file is already open", StringComparison.OrdinalIgnoreCase) ||
               (message.Contains("already open", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("close", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNoDumpOpenError(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("no dump", StringComparison.OrdinalIgnoreCase) &&
               message.Contains("open", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Attempts to recover from a session not found error by creating a new session and reopening the dump.
    /// </summary>
    /// <returns>True if recovery was successful, false otherwise.</returns>
    private static async Task<bool> TryRecoverSessionAsync(
        ConsoleOutput output,
        ShellState state,
        McpClient mcpClient)
    {
        // Check if we have the necessary state to recover
        if (!state.IsConnected || !mcpClient.IsConnected)
        {
            return false;
        }

        var expiredSessionId = state.SessionId;
        var dumpIdToReopen = state.DumpId;

        output.WriteLine();
        output.Warning($"⚠️  Session '{expiredSessionId?[..Math.Min(8, expiredSessionId?.Length ?? 0)]}...' has expired or was removed due to inactivity.");
        output.Info("Automatically creating a new session...");

        try
        {
            // Clear the old session state
            state.ClearSession();

            // Create a new session
            var createResult = await output.WithSpinnerAsync(
                "Creating new session...",
                () => mcpClient.CreateSessionAsync(state.Settings.UserId));

            // Parse session ID from result
            var sessionIdMatch = System.Text.RegularExpressions.Regex.Match(
                createResult, @"SessionId:\s*([a-zA-Z0-9\-]+)");

            if (!sessionIdMatch.Success)
            {
                output.Error("Failed to create new session: Could not parse session ID");
                return false;
            }

            var newSessionId = sessionIdMatch.Groups[1].Value;
            state.SetSession(newSessionId);
            output.Success($"New session created: {newSessionId[..8]}...");

            // If we had a dump open, reopen it
            if (!string.IsNullOrEmpty(dumpIdToReopen))
            {
                output.Info($"Reopening dump {dumpIdToReopen[..Math.Min(8, dumpIdToReopen.Length)]}...");

                var openResult = await output.WithSpinnerAsync(
                    "Reopening dump...",
                    () => mcpClient.OpenDumpAsync(newSessionId, state.Settings.UserId, dumpIdToReopen));

                if (IsErrorResult(openResult))
                {
                    output.Error($"Failed to reopen dump: {openResult}");
                    state.ClearDump();
                    return false;
                }

                state.SetDumpLoaded(dumpIdToReopen);

                // Try to get debugger type
                try
                {
                    var infoResult = await mcpClient.GetDebuggerInfoAsync(newSessionId, state.Settings.UserId);
                    var debuggerMatch = System.Text.RegularExpressions.Regex.Match(
                        infoResult, @"DebuggerType:\s*(\w+)");
                    if (debuggerMatch.Success)
                    {
                        state.DebuggerType = debuggerMatch.Groups[1].Value;
                    }
                }
                catch
                {
                    // Ignore - not critical
                }

                output.Success("Session recovered! Dump is ready.");
            }
            else
            {
                output.Success("Session recovered!");
            }

            output.WriteLine();
            output.Dim("Please retry your last command.");
            return true;
        }
        catch (Exception ex)
        {
            output.Error($"Failed to recover session: {ex.Message}");
            state.ClearSession();
            return false;
        }
    }

}
