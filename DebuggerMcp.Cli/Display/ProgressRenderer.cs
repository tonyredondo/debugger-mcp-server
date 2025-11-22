using Spectre.Console;

namespace DebuggerMcp.Cli.Display;

/// <summary>
/// Renders progress bars and spinners using Spectre.Console.
/// </summary>
public class ProgressRenderer
{
    private readonly IAnsiConsole _console;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgressRenderer"/> class.
    /// </summary>
    /// <param name="console">The Spectre.Console instance.</param>
    public ProgressRenderer(IAnsiConsole console)
    {
        _console = console;
    }

    /// <summary>
    /// Shows an upload progress bar for a file upload operation.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="fileName">The name of the file being uploaded.</param>
    /// <param name="totalBytes">Total file size in bytes.</param>
    /// <param name="operation">The async operation that reports bytes sent progress.</param>
    /// <returns>The operation result.</returns>
    public async Task<T> WithUploadProgressAsync<T>(
        string fileName,
        long totalBytes,
        Func<IProgress<long>, Task<T>> operation)
    {
        T result = default!;

        await _console.Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new TransferSpeedColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"[cyan]Uploading {EscapeMarkup(fileName)}[/]");
                task.MaxValue = totalBytes;

                var progress = new Progress<long>(bytesSent =>
                {
                    task.Value = bytesSent;
                });

                result = await operation(progress);
                task.Value = totalBytes;
                task.Description = $"[green]✓ Uploaded {EscapeMarkup(fileName)}[/]";
            });

        return result;
    }

    /// <summary>
    /// Shows progress for multiple file uploads.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="files">List of file names and sizes.</param>
    /// <param name="operation">The operation that uploads files and reports progress.</param>
    /// <returns>The operation result.</returns>
    public async Task<T> WithMultiFileUploadProgressAsync<T>(
        IReadOnlyList<(string FileName, long Size)> files,
        Func<Action<int, long>, Task<T>> operation)
    {
        T result = default!;

        await _console.Progress()
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
                // Create tasks for each file
                var tasks = new ProgressTask[files.Count];
                for (var i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    tasks[i] = ctx.AddTask($"[cyan]{EscapeMarkup(file.FileName)}[/]");
                    tasks[i].MaxValue = file.Size;
                }

                // Progress callback: (fileIndex, bytesSent) => update that file's progress
                void UpdateProgress(int fileIndex, long bytesSent)
                {
                    if (fileIndex >= 0 && fileIndex < tasks.Length)
                    {
                        tasks[fileIndex].Value = bytesSent;
                        if (bytesSent >= files[fileIndex].Size)
                        {
                            tasks[fileIndex].Description = $"[green]✓ {EscapeMarkup(files[fileIndex].FileName)}[/]";
                        }
                    }
                }

                result = await operation(UpdateProgress);

                // Mark all as complete
                for (var i = 0; i < tasks.Length; i++)
                {
                    tasks[i].Value = files[i].Size;
                    tasks[i].Description = $"[green]✓ {EscapeMarkup(files[i].FileName)}[/]";
                }
            });

        return result;
    }

    /// <summary>
    /// Shows a spinner while an operation is in progress.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="message">The status message.</param>
    /// <param name="operation">The async operation.</param>
    /// <returns>The operation result.</returns>
    public async Task<T> WithSpinnerAsync<T>(string message, Func<Task<T>> operation)
    {
        T result = default!;

        await _console.Status()
            .AutoRefresh(true)
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync(message, async _ =>
            {
                result = await operation();
            });

        return result;
    }

    /// <summary>
    /// Shows a spinner while an operation is in progress (no return value).
    /// </summary>
    /// <param name="message">The status message.</param>
    /// <param name="operation">The async operation.</param>
    public async Task WithSpinnerAsync(string message, Func<Task> operation)
    {
        await _console.Status()
            .AutoRefresh(true)
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync(message, async _ =>
            {
                await operation();
            });
    }

    /// <summary>
    /// Escapes markup characters in a string.
    /// </summary>
    private static string EscapeMarkup(string text)
    {
        return text
            .Replace("[", "[[")
            .Replace("]", "]]");
    }
}
