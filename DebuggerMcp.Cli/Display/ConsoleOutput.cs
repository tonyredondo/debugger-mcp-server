using Spectre.Console;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Llm;
using DebuggerMcp.Cli.Shell.Transcript;
using SpectreMarkup = Spectre.Console.Markup;

namespace DebuggerMcp.Cli.Display;

/// <summary>
/// Provides formatted console output using Spectre.Console.
/// </summary>
/// <remarks>
/// Handles color theming, error display, success messages, and structured output.
/// Supports both text and JSON output modes.
/// </remarks>
public class ConsoleOutput
{
    private readonly IAnsiConsole _console;
    private OutputFormat _format;
    private bool _verbose;
    private static readonly AsyncLocal<Action<string>?> TranscriptSink = new();
    private static readonly LlmResponseRenderer DefaultLlmRenderer = new();

    /// <summary>
    /// Gets or sets the output format.
    /// </summary>
    public OutputFormat Format
    {
        get => _format;
        set => _format = value;
    }

    /// <summary>
    /// Gets or sets whether verbose output is enabled.
    /// </summary>
    public bool Verbose
    {
        get => _verbose;
        set => _verbose = value;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsoleOutput"/> class.
    /// </summary>
    /// <param name="console">The Spectre.Console instance.</param>
    public ConsoleOutput(IAnsiConsole console)
    {
        _console = console;
        _format = OutputFormat.Text;
        _verbose = false;
    }

    /// <summary>
    /// Begins capturing output to a transcript sink for the current async flow.
    /// </summary>
    internal IDisposable BeginTranscriptCapture(Action<string> sink)
    {
        if (sink == null)
        {
            throw new ArgumentNullException(nameof(sink));
        }

        var previous = TranscriptSink.Value;
        TranscriptSink.Value = sink;
        return new CaptureScope(() => TranscriptSink.Value = previous);
    }

    private static void EmitToTranscript(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            TranscriptSink.Value?.Invoke(text);
        }
    }

    /// <summary>
    /// Emits a line to the active transcript capture without writing to the console.
    /// </summary>
    internal void CaptureOnly(string text) => EmitToTranscript(text);

    private sealed class CaptureScope(Action onDispose) : IDisposable
    {
        private readonly Action _onDispose = onDispose;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _onDispose();
        }
    }

    /// <summary>
    /// Writes a success message.
    /// </summary>
    /// <param name="message">The message to display.</param>
    public void Success(string message)
    {
        EmitToTranscript($"OK: {message}");
        _console.MarkupLine($"[green]✓[/] {SpectreMarkup.Escape(message)}");
    }

    /// <summary>
    /// Writes an error message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public void Error(string message)
    {
        EmitToTranscript($"ERROR: {message}");
        _console.MarkupLine($"[red]✗ Error:[/] {SpectreMarkup.Escape(message)}");
    }

    /// <summary>
    /// Writes an error with exception details.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="exception">The exception.</param>
    public void Error(string message, Exception exception)
    {
        EmitToTranscript($"ERROR: {message}");
        _console.MarkupLine($"[red]✗ Error:[/] {SpectreMarkup.Escape(message)}");

        if (_verbose)
        {
            _console.WriteException(exception);
        }
        else
        {
            EmitToTranscript(exception.Message);
            _console.MarkupLine($"[dim]{SpectreMarkup.Escape(exception.Message)}[/]");
        }
    }

    /// <summary>
    /// Writes a warning message.
    /// </summary>
    /// <param name="message">The warning message.</param>
    public void Warning(string message)
    {
        EmitToTranscript($"WARN: {message}");
        _console.MarkupLine($"[yellow]⚠ Warning:[/] {SpectreMarkup.Escape(message)}");
    }

    /// <summary>
    /// Writes an informational message.
    /// </summary>
    /// <param name="message">The message.</param>
    public void Info(string message)
    {
        EmitToTranscript($"INFO: {message}");
        _console.MarkupLine($"[blue]ℹ[/] {SpectreMarkup.Escape(message)}");
    }

    /// <summary>
    /// Writes a dimmed/subtle message.
    /// </summary>
    /// <param name="message">The message.</param>
    public void Dim(string message)
    {
        EmitToTranscript(message);
        _console.MarkupLine($"[dim]{SpectreMarkup.Escape(message)}[/]");
    }

    /// <summary>
    /// Writes a plain message.
    /// </summary>
    /// <param name="message">The message.</param>
    public void WriteLine(string message = "")
    {
        EmitToTranscript(message);
        _console.WriteLine(message);
    }

    /// <summary>
    /// Writes an LLM response, rendering Markdown (best-effort) and ANSI colors (SGR only).
    /// </summary>
    /// <param name="response">The raw response text.</param>
    public void WriteLlmResponse(string response)
    {
        response ??= string.Empty;
        EmitToTranscript(response);

        var blocks = DefaultLlmRenderer.Render(response, consoleWidth: _console.Profile.Width);
        _console.Write(new Rows(blocks));
        _console.WriteLine();
    }

    /// <summary>
    /// Writes a markup message (with Spectre.Console markup).
    /// </summary>
    /// <param name="markup">The markup string.</param>
    public void Markup(string markup)
    {
        EmitToTranscript(StripSpectreMarkup(markup));
        _console.MarkupLine(markup);
    }

    /// <summary>
    /// Writes a key-value pair.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    public void KeyValue(string key, string? value)
    {
        EmitToTranscript($"{key}: {value ?? "(not set)"}");
        _console.MarkupLine($"  [cyan]{SpectreMarkup.Escape(key)}:[/] {SpectreMarkup.Escape(value ?? "(not set)")}");
    }

    /// <summary>
    /// Writes a header/title.
    /// </summary>
    /// <param name="title">The title text.</param>
    public void Header(string title)
    {
        EmitToTranscript(title);
        var rule = new Rule($"[bold cyan]{SpectreMarkup.Escape(title)}[/]")
        {
            Justification = Justify.Left,
            Style = Style.Parse("cyan")
        };
        _console.Write(rule);
    }

    /// <summary>
    /// Writes a panel with content.
    /// </summary>
    /// <param name="title">The panel title.</param>
    /// <param name="content">The panel content.</param>
    public void Panel(string title, string content)
    {
        EmitToTranscript($"{title}: {content}");
        var panel = new Panel(new Text(content))
        {
            Header = new PanelHeader(title),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0)
        };
        _console.Write(panel);
    }

    internal LlmAgentToolApprovalDecision PromptLlmAgentToolApproval(string toolName, string toolArgsPreview)
    {
        var safePreview = TranscriptRedactor.RedactText(toolArgsPreview ?? string.Empty);
        EmitToTranscript($"LLM agent requested tool: {toolName} {safePreview}");

        if (!_console.Profile.Capabilities.Interactive)
        {
            _console.MarkupLine(
                $"[yellow]LLM agent requested:[/] [cyan]{SpectreMarkup.Escape(toolName)}[/] [dim]{SpectreMarkup.Escape(safePreview)}[/] [dim](non-interactive; skipping)[/]");
            return LlmAgentToolApprovalDecision.DenyOnce;
        }

        var prompt = new SelectionPrompt<string>()
            .Title($"[yellow]LLM agent requests:[/] [cyan]{SpectreMarkup.Escape(toolName)}[/] [dim]{SpectreMarkup.Escape(safePreview)}[/]")
            .AddChoices(
                "Run once",
                "Always run this tool (this run)",
                "Always run any tool (this run)",
                "Skip",
                "Cancel agent run");

        var choice = _console.Prompt(prompt);
        return choice switch
        {
            "Run once" => LlmAgentToolApprovalDecision.AllowOnce,
            "Always run this tool (this run)" => LlmAgentToolApprovalDecision.AllowToolAlways,
            "Always run any tool (this run)" => LlmAgentToolApprovalDecision.AllowAllAlways,
            "Skip" => LlmAgentToolApprovalDecision.DenyOnce,
            _ => LlmAgentToolApprovalDecision.CancelRun
        };
    }

    /// <summary>
    /// Writes a table.
    /// </summary>
    /// <param name="headers">Column headers.</param>
    /// <param name="rows">Table rows.</param>
    public void Table(string[] headers, IEnumerable<string[]> rows)
    {
        if (headers.Length > 0)
        {
            EmitToTranscript(string.Join("\t", headers));
        }

        foreach (var row in rows)
        {
            EmitToTranscript(string.Join("\t", row.Select(cell => cell ?? string.Empty)));
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey);

        foreach (var header in headers)
        {
            table.AddColumn(new TableColumn($"[bold]{SpectreMarkup.Escape(header)}[/]"));
        }

        foreach (var row in rows)
        {
            table.AddRow(row.Select(cell => SpectreMarkup.Escape(cell ?? "")).ToArray());
        }

        _console.Write(table);
    }

    private static string StripSpectreMarkup(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        const string open = "\u0001";
        const string close = "\u0002";
        text = text.Replace("[[", open, StringComparison.Ordinal).Replace("]]", close, StringComparison.Ordinal);
        text = System.Text.RegularExpressions.Regex.Replace(text, "\\[[^\\[\\]]+\\]", string.Empty);
        return text
            .Replace(open, "[", StringComparison.Ordinal)
            .Replace(close, "]", StringComparison.Ordinal)
            .TrimEnd();
    }

    /// <summary>
    /// Shows a spinner while executing an async operation.
    /// Supports Ctrl+C cancellation.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="message">The status message.</param>
    /// <param name="operation">The async operation.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The operation result.</returns>
    /// <exception cref="OperationCanceledException">Thrown when Ctrl+C is pressed.</exception>
    public async Task<T> WithSpinnerAsync<T>(string message, Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        T result = default!;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Create a linked token source that combines external token with Ctrl+C
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Handle Ctrl+C to cancel the operation
        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true; // Prevent immediate termination
            cts.Cancel();
        }

        System.Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            await _console.Status()
                .AutoRefresh(true)
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync(message, async ctx =>
                {
                    // Start the operation
                    var operationTask = operation();

                    // Update status with elapsed time while waiting (every 1 second)
                    while (!operationTask.IsCompleted)
                    {
                        // Check for cancellation
                        if (cts.Token.IsCancellationRequested)
                        {
                            ctx.Status($"{message} [dim](cancelling...)[/]");
                            throw new OperationCanceledException("Operation cancelled by user", cts.Token);
                        }

                        var elapsed = stopwatch.Elapsed;
                        ctx.Status($"{message} [dim]({FormatElapsed(elapsed)})[/]");

                        // Wait 1 second before updating, but also check if task completed or cancelled
                        try
                        {
                            var delayTask = Task.Delay(1000, cts.Token);
                            await Task.WhenAny(operationTask, delayTask);
                        }
                        catch (OperationCanceledException)
                        {
                            // Cancellation requested during delay
                            ctx.Status($"{message} [dim](cancelling...)[/]");
                            throw new OperationCanceledException("Operation cancelled by user", cts.Token);
                        }
                    }

                    result = await operationTask;
                });
        }
        finally
        {
            System.Console.CancelKeyPress -= OnCancelKeyPress;
        }

        stopwatch.Stop();
        return result;
    }

    /// <summary>
    /// Shows a spinner while executing an async operation (no return value).
    /// Supports Ctrl+C cancellation.
    /// </summary>
    /// <param name="message">The status message.</param>
    /// <param name="operation">The async operation.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <exception cref="OperationCanceledException">Thrown when Ctrl+C is pressed.</exception>
    public async Task WithSpinnerAsync(string message, Func<Task> operation, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Create a linked token source that combines external token with Ctrl+C
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Handle Ctrl+C to cancel the operation
        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true; // Prevent immediate termination
            cts.Cancel();
        }

        System.Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            await _console.Status()
                .AutoRefresh(true)
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync(message, async ctx =>
                {
                    // Start the operation
                    var operationTask = operation();

                    // Update status with elapsed time while waiting (every 1 second)
                    while (!operationTask.IsCompleted)
                    {
                        // Check for cancellation
                        if (cts.Token.IsCancellationRequested)
                        {
                            ctx.Status($"{message} [dim](cancelling...)[/]");
                            throw new OperationCanceledException("Operation cancelled by user", cts.Token);
                        }

                        var elapsed = stopwatch.Elapsed;
                        ctx.Status($"{message} [dim]({FormatElapsed(elapsed)})[/]");

                        // Wait 1 second before updating, but also check if task completed or cancelled
                        try
                        {
                            var delayTask = Task.Delay(1000, cts.Token);
                            await Task.WhenAny(operationTask, delayTask);
                        }
                        catch (OperationCanceledException)
                        {
                            // Cancellation requested during delay
                            ctx.Status($"{message} [dim](cancelling...)[/]");
                            throw new OperationCanceledException("Operation cancelled by user", cts.Token);
                        }
                    }

                    await operationTask;
                });
        }
        finally
        {
            System.Console.CancelKeyPress -= OnCancelKeyPress;
        }

        stopwatch.Stop();
    }

    /// <summary>
    /// Formats elapsed time for display.
    /// </summary>
    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 60)
        {
            return $"{elapsed.TotalSeconds:F0}s";
        }
        else if (elapsed.TotalMinutes < 60)
        {
            return $"{elapsed.Minutes}m {elapsed.Seconds}s";
        }
        else
        {
            return $"{elapsed.Hours}h {elapsed.Minutes}m {elapsed.Seconds}s";
        }
    }

    /// <summary>
    /// Prompts the user for confirmation.
    /// </summary>
    /// <param name="message">The confirmation message.</param>
    /// <param name="defaultValue">The default value.</param>
    /// <returns>True if confirmed.</returns>
    public bool Confirm(string message, bool defaultValue = false)
    {
        return _console.Confirm(message, defaultValue);
    }

    /// <summary>
    /// Prompts the user for text input.
    /// </summary>
    /// <param name="prompt">The prompt message.</param>
    /// <param name="defaultValue">The default value.</param>
    /// <returns>The user input.</returns>
    public string Prompt(string prompt, string? defaultValue = null)
    {
        var textPrompt = new TextPrompt<string>(prompt);

        if (!string.IsNullOrEmpty(defaultValue))
        {
            textPrompt.DefaultValue(defaultValue);
        }

        return _console.Prompt(textPrompt);
    }

    /// <summary>
    /// Prompts the user for a secret (hidden input).
    /// </summary>
    /// <param name="prompt">The prompt message.</param>
    /// <returns>The secret input.</returns>
    public string PromptSecret(string prompt)
    {
        return _console.Prompt(
            new TextPrompt<string>(prompt)
                .Secret());
    }

    /// <summary>
    /// Gets the underlying console for advanced operations.
    /// </summary>
    public IAnsiConsole Console => _console;
}
