#nullable enable

using System.Text;
using System.Text.Json;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Shell.Transcript;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Best-effort trace store for persisting LLM agent HTTP request/response payloads to disk.
/// </summary>
/// <remarks>
/// This is intended for debugging prompt/tool loops and can produce large artifacts.
/// The store never throws to callers; failures are swallowed.
/// </remarks>
internal sealed class LlmTraceStore
{
    private readonly object _gate = new();
    private readonly int _maxFileBytes;
    private int _counter;

    /// <summary>
    /// Gets the trace directory path.
    /// </summary>
    public string DirectoryPath { get; }

    /// <summary>
    /// Gets the trace event log path (<c>events.jsonl</c>).
    /// </summary>
    public string EventsFilePath { get; }

    public LlmTraceStore(string directoryPath, int maxFileBytes)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Trace directory cannot be null or empty.", nameof(directoryPath));
        }

        DirectoryPath = directoryPath;
        EventsFilePath = Path.Combine(DirectoryPath, "events.jsonl");
        _maxFileBytes = maxFileBytes <= 0 ? 2_000_000 : maxFileBytes;
    }

    /// <summary>
    /// Creates a new trace store under the default CLI config directory.
    /// </summary>
    /// <param name="label">Human-readable label used in the folder name.</param>
    /// <param name="maxFileBytes">Maximum bytes per file (default: 2,000,000).</param>
    /// <returns>The created store, or null if creation fails.</returns>
    public static LlmTraceStore? TryCreate(string label, int maxFileBytes = 2_000_000)
    {
        try
        {
            var root = Path.Combine(ConnectionSettings.DefaultConfigDirectory, "llmagent-trace");
            Directory.CreateDirectory(root);
            var dirName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{SanitizeFileComponent(label)}-{Guid.NewGuid():N}";
            var full = Path.Combine(root, dirName);
            Directory.CreateDirectory(full);
            return new LlmTraceStore(full, maxFileBytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the next monotonically increasing trace ID for naming files.
    /// </summary>
    public int NextId() => Interlocked.Increment(ref _counter);

    /// <summary>
    /// Appends a JSONL event record to <c>events.jsonl</c>.
    /// </summary>
    public void AppendEvent(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload);
            lock (_gate)
            {
                File.AppendAllText(EventsFilePath, json + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Best-effort only.
        }
    }

    /// <summary>
    /// Writes a JSON payload to the trace directory (pretty-printed when possible).
    /// </summary>
    public void WriteJson(string fileName, string json)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        try
        {
            json ??= string.Empty;
            json = TranscriptRedactor.RedactText(json);

            string output;
            try
            {
                using var doc = JsonDocument.Parse(json);
                output = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                output = json;
            }

            WriteTextInternal(fileName, output);
        }
        catch
        {
            // Best-effort only.
        }
    }

    /// <summary>
    /// Writes a text payload to the trace directory.
    /// </summary>
    public void WriteText(string fileName, string text)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        try
        {
            text ??= string.Empty;
            text = TranscriptRedactor.RedactText(text);
            WriteTextInternal(fileName, text);
        }
        catch
        {
            // Best-effort only.
        }
    }

    private void WriteTextInternal(string fileName, string text)
    {
        fileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        var path = Path.Combine(DirectoryPath, fileName);
        var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        if (bytes.Length > _maxFileBytes)
        {
            var truncated = Encoding.UTF8.GetString(bytes, 0, _maxFileBytes);
            truncated += $"{Environment.NewLine}... [truncated, totalBytes={bytes.Length}]";
            File.WriteAllText(path, truncated, Encoding.UTF8);
        }
        else
        {
            File.WriteAllText(path, text ?? string.Empty, Encoding.UTF8);
        }
    }

    private static string SanitizeFileComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "run";
        }

        var s = value.Trim();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[^a-zA-Z0-9._-]+", "_", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        s = s.Trim('_');
        return string.IsNullOrWhiteSpace(s) ? "run" : s;
    }
}
