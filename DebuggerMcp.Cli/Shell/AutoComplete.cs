namespace DebuggerMcp.Cli.Shell;

/// <summary>
/// Provides tab completion for shell commands.
/// </summary>
/// <remarks>
/// Features:
/// <list type="bullet">
/// <item><description>Command name completion</description></item>
/// <item><description>Subcommand completion</description></item>
/// <item><description>Argument completion (dump IDs, session IDs, file paths)</description></item>
/// <item><description>Context-aware suggestions</description></item>
/// </list>
/// </remarks>
public class AutoComplete
{
    /// <summary>
    /// All available top-level commands.
    /// </summary>
    private static readonly string[] Commands =
    [
        "connect", "disconnect", "status", "health", "server",
        "dumps", "symbols", "stats",
        "llm", "llmagent",
        "session", "open", "close", "exec", "cmd", "showobj", "so",
        "analyze", "compare", "watch", "report", "sourcelink",
        "help", "history", "clear", "set", "version", "exit", "quit", "tools"
    ];

    /// <summary>
    /// Subcommands for each command.
    /// </summary>
	    private static readonly Dictionary<string, string[]> Subcommands = new(StringComparer.OrdinalIgnoreCase)
	    {
	        ["session"] = ["create", "list", "close", "info", "use"],
	        ["dumps"] = ["upload", "list", "info", "delete"],
	        ["symbols"] = ["upload", "list", "servers", "add", "clear"],
	        ["analyze"] = ["crash", "ai", "perf", "cpu", "memory", "gc", "contention", "security"],
	        ["compare"] = ["all", "heap", "threads", "modules"],
	        ["watch"] = ["add", "list", "eval", "remove", "clear"],
	        ["report"] = ["--format", "--output", "--summary", "markdown", "html", "json"],
	        ["sourcelink"] = ["resolve", "info"],
        ["llm"] = ["provider", "set-provider", "model", "reasoning-effort", "effort", "set-key", "set-agent", "agent", "set-agent-confirm", "agent-confirm", "reset"],
        ["set"] = ["verbose", "output", "timeout", "user"],
        ["history"] = ["clear", "search"]
    };

    /// <summary>
    /// Options for each command.
    /// </summary>
    private static readonly Dictionary<string, string[]> Options = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dumps"] = ["--description"],
        ["symbols"] = ["--dump-id"],
        ["analyze"] = ["--session-id"],
        ["compare"] = ["--dump1", "--dump2", "--session-id"],
        ["profile"] = ["--session-id", "--top"],
        ["report"] = ["--format", "--output", "--include-watches", "--include-comparison"],
        ["set"] = ["true", "false", "text", "json"]
    };

    private readonly ShellState _state;
    private Func<Task<IEnumerable<string>>>? _getDumpIds;
    private Func<Task<IEnumerable<string>>>? _getSessionIds;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoComplete"/> class.
    /// </summary>
    /// <param name="state">The shell state for context-aware completion.</param>
    public AutoComplete(ShellState state)
    {
        _state = state;
    }

    /// <summary>
    /// Sets the function to get available dump IDs.
    /// </summary>
    /// <param name="getDumpIds">Function that returns dump IDs.</param>
    public void SetDumpIdProvider(Func<Task<IEnumerable<string>>> getDumpIds)
    {
        _getDumpIds = getDumpIds;
    }

    /// <summary>
    /// Sets the function to get available session IDs.
    /// </summary>
    /// <param name="getSessionIds">Function that returns session IDs.</param>
    public void SetSessionIdProvider(Func<Task<IEnumerable<string>>> getSessionIds)
    {
        _getSessionIds = getSessionIds;
    }

    /// <summary>
    /// Gets completion suggestions for the current input.
    /// </summary>
    /// <param name="input">The current input text.</param>
    /// <param name="cursorPosition">The cursor position in the input.</param>
    /// <returns>List of possible completions.</returns>
    public async Task<CompletionResult> GetCompletionsAsync(string input, int cursorPosition)
    {
        // Get text up to cursor
        var textToCursor = input[..Math.Min(cursorPosition, input.Length)];
        var parts = textToCursor.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // No input - suggest all commands
        if (parts.Length == 0)
        {
            return new CompletionResult(
                GetContextualCommands(),
                string.Empty,
                0);
        }

        var lastPart = parts[^1];
        var lastPartStart = textToCursor.LastIndexOf(lastPart, StringComparison.Ordinal);

        // Single word - complete command name
        if (parts.Length == 1 && !textToCursor.EndsWith(' '))
        {
            var matches = Commands
                .Where(c => c.StartsWith(lastPart, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c)
                .ToList();

            return new CompletionResult(matches, lastPart, lastPartStart);
        }

        // Multiple words - complete based on context
        var command = parts[0].ToLowerInvariant();

        // If command has subcommands and we're completing second word (or awaiting second word after space)
        if (Subcommands.TryGetValue(command, out var subs))
        {
            // Case 1: "session " - awaiting subcommand
            if (parts.Length == 1 && textToCursor.EndsWith(' '))
            {
                return new CompletionResult(subs.ToList(), string.Empty, textToCursor.Length);
            }

            // Case 2: "session cr" - partial subcommand
            if (parts.Length == 2 && !textToCursor.EndsWith(' '))
            {
                var matches = subs
                    .Where(s => s.StartsWith(lastPart, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => s)
                    .ToList();

                return new CompletionResult(matches, lastPart, lastPartStart);
            }
        }

        // After command (or subcommand), complete arguments
        if (textToCursor.EndsWith(' ') || parts.Length >= 2)
        {
            var prefix = textToCursor.EndsWith(' ') ? string.Empty : lastPart;
            var startPos = textToCursor.EndsWith(' ') ? textToCursor.Length : lastPartStart;

            // Check for option completions
            if (prefix.StartsWith('-'))
            {
                if (Options.TryGetValue(command, out var opts))
                {
                    var matches = opts
                        .Where(o => o.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    return new CompletionResult(matches, prefix, startPos);
                }
            }

            // Context-specific completions
            var contextCompletions = await GetContextCompletionsAsync(command, parts, prefix);
            if (contextCompletions.Count > 0)
            {
                return new CompletionResult(contextCompletions, prefix, startPos);
            }

            // File path completion for dumps upload and symbols upload
            if (command is "dumps" or "symbols" && !prefix.StartsWith('-'))
            {
                var fileCompletions = GetFileCompletions(prefix);
                if (fileCompletions.Count > 0)
                {
                    return new CompletionResult(fileCompletions, prefix, startPos);
                }
            }
        }

        return CompletionResult.Empty;
    }

    /// <summary>
    /// Gets commands relevant to the current shell state.
    /// </summary>
    private List<string> GetContextualCommands()
    {
        var commands = new List<string>();

        switch (_state.Level)
        {
            case ShellStateLevel.Initial:
                // Only connection-related commands
                commands.AddRange(["connect", "llm", "llmagent", "help", "set", "version", "exit"]);
                break;

            case ShellStateLevel.Connected:
                // Connection + file + session commands
                commands.AddRange([
                    "disconnect", "status", "health",
                    "dumps", "symbols", "stats",
                    "llm", "llmagent",
                    "session", "open",
                    "help", "clear", "set", "version", "exit", "tools"
                ]);
                break;

            case ShellStateLevel.Session:
                // All commands except debugging (no dump loaded)
                commands.AddRange([
                    "disconnect", "status", "health",
                    "dumps", "symbols", "stats",
                    "llm", "llmagent",
                    "session", "open", "close",
                    "help", "clear", "set", "version", "exit", "tools"
                ]);
                break;

            case ShellStateLevel.DumpLoaded:
                // All commands
                commands.AddRange(Commands);
                break;
        }

        return commands.Distinct().OrderBy(c => c).ToList();
    }

    /// <summary>
    /// Gets context-specific completions (dump IDs, session IDs, etc.).
    /// </summary>
    private async Task<List<string>> GetContextCompletionsAsync(string command, string[] parts, string prefix)
    {
        var completions = new List<string>();

        // LLM provider selector values
        if (command == "llm" &&
            parts.Length >= 2 &&
            (parts[1].Equals("provider", StringComparison.OrdinalIgnoreCase) ||
             parts[1].Equals("set-provider", StringComparison.OrdinalIgnoreCase)))
        {
            completions.AddRange(new[] { "openrouter", "openai", "anthropic" }.Where(p =>
                string.IsNullOrEmpty(prefix) ||
                p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        }

        // LLM boolean toggles
        if (command == "llm" &&
            parts.Length >= 2 &&
            (parts[1].Equals("set-agent", StringComparison.OrdinalIgnoreCase) ||
             parts[1].Equals("set-agent-confirm", StringComparison.OrdinalIgnoreCase) ||
             parts[1].Equals("agent", StringComparison.OrdinalIgnoreCase) ||
             parts[1].Equals("agent-confirm", StringComparison.OrdinalIgnoreCase)))
        {
            completions.AddRange(new[] { "true", "false" }.Where(v =>
                string.IsNullOrEmpty(prefix) ||
                v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        }

        // LLM reset mode
        if (command == "llm" &&
            parts.Length >= 2 &&
            parts[1].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            completions.AddRange(new[] { "conversation" }.Where(v =>
                string.IsNullOrEmpty(prefix) ||
                v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        }

        // LLM reasoning effort values
        if (command == "llm" &&
            parts.Length >= 2 &&
            (parts[1].Equals("reasoning-effort", StringComparison.OrdinalIgnoreCase) ||
             parts[1].Equals("effort", StringComparison.OrdinalIgnoreCase)))
        {
            completions.AddRange(new[] { "low", "medium", "high", "unset" }.Where(v =>
                string.IsNullOrEmpty(prefix) ||
                v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        }

        // Commands that accept dump IDs
        if (command is "open" or "compare" && _getDumpIds != null)
        {
            try
            {
                var dumpIds = await _getDumpIds();
                completions.AddRange(dumpIds.Where(id =>
                    string.IsNullOrEmpty(prefix) ||
                    id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                // Ignore errors fetching dump IDs
            }
        }

        // Commands that accept session IDs
        if ((command == "session" && parts.Length >= 2 && parts[1] is "close" or "info" or "use") && _getSessionIds != null)
        {
            try
            {
                var sessionIds = await _getSessionIds();
                completions.AddRange(sessionIds.Where(id =>
                    string.IsNullOrEmpty(prefix) ||
                    id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                // Ignore errors fetching session IDs
            }
        }

	        // Analyze types
	        if (command == "analyze" && parts.Length == 1)
	        {
	            completions.AddRange(new[] { "crash", "ai" }.Where(t =>
	                string.IsNullOrEmpty(prefix) ||
	                t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
	        }

        // Report formats
        if (command == "report" && parts.Contains("--format"))
        {
            completions.AddRange(new[] { "markdown", "html", "json" }.Where(f =>
                string.IsNullOrEmpty(prefix) ||
                f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        }

        return completions;
    }

    /// <summary>
    /// Gets file path completions.
    /// </summary>
    private static List<string> GetFileCompletions(string prefix)
    {
        var completions = new List<string>();

        try
        {
            var directory = ".";
            var pattern = "*";

            if (!string.IsNullOrEmpty(prefix))
            {
                var dirPart = Path.GetDirectoryName(prefix);
                var filePart = Path.GetFileName(prefix);

                if (!string.IsNullOrEmpty(dirPart))
                {
                    directory = dirPart;
                }

                if (!string.IsNullOrEmpty(filePart))
                {
                    pattern = filePart + "*";
                }
            }

            if (Directory.Exists(directory))
            {
                // Add directories
                foreach (var dir in Directory.EnumerateDirectories(directory, pattern).Take(20))
                {
                    var relativePath = Path.GetRelativePath(".", dir);
                    completions.Add(relativePath + Path.DirectorySeparatorChar);
                }

                // Add files (dump and symbol files)
                var extensions = new[] { ".dmp", ".dump", ".core", ".mdmp", ".pdb", ".dbg", ".so", ".dylib" };
                foreach (var file in Directory.EnumerateFiles(directory, pattern).Take(30))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (extensions.Contains(ext) || string.IsNullOrEmpty(prefix))
                    {
                        var relativePath = Path.GetRelativePath(".", file);
                        completions.Add(relativePath);
                    }
                }
            }
        }
        catch
        {
            // Ignore errors getting file completions
        }

        return completions;
    }
}

/// <summary>
/// Result of a completion request.
/// </summary>
public class CompletionResult
{
    /// <summary>
    /// Empty completion result.
    /// </summary>
    public static readonly CompletionResult Empty = new([], string.Empty, 0);

    /// <summary>
    /// Gets the list of possible completions.
    /// </summary>
    public IReadOnlyList<string> Completions { get; }

    /// <summary>
    /// Gets the prefix that was matched.
    /// </summary>
    public string Prefix { get; }

    /// <summary>
    /// Gets the start position of the prefix in the input.
    /// </summary>
    public int StartPosition { get; }

    /// <summary>
    /// Gets whether there are any completions.
    /// </summary>
    public bool HasCompletions => Completions.Count > 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompletionResult"/> class.
    /// </summary>
    public CompletionResult(IReadOnlyList<string> completions, string prefix, int startPosition)
    {
        Completions = completions;
        Prefix = prefix;
        StartPosition = startPosition;
    }

    /// <summary>
    /// Gets the common prefix of all completions.
    /// </summary>
    public string GetCommonPrefix()
    {
        if (Completions.Count == 0)
        {
            return Prefix;
        }

        if (Completions.Count == 1)
        {
            return Completions[0];
        }

        var first = Completions[0];
        var commonLength = first.Length;

        foreach (var completion in Completions.Skip(1))
        {
            var matchLength = 0;
            var minLength = Math.Min(first.Length, completion.Length);

            while (matchLength < minLength &&
                   char.ToLowerInvariant(first[matchLength]) == char.ToLowerInvariant(completion[matchLength]))
            {
                matchLength++;
            }

            commonLength = Math.Min(commonLength, matchLength);
        }

        return first[..commonLength];
    }
}
