using System.Text.Json;
using DebuggerMcp.Cli.Serialization;

namespace DebuggerMcp.Cli.Shell.Transcript;

/// <summary>
/// Simple JSONL transcript store for CLI commands and LLM conversations.
/// </summary>
public sealed class CliTranscriptStore
{
    private static readonly JsonSerializerOptions JsonOptions = CliJsonSerializationDefaults.CaseInsensitiveCamelCaseIgnoreNull;

    private readonly string _filePath;

    public CliTranscriptStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Transcript file path cannot be null or empty.", nameof(filePath));
        }

        _filePath = filePath;
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string FilePath => _filePath;

    public void Append(CliTranscriptEntry entry)
    {
        if (entry == null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        var line = JsonSerializer.Serialize(entry, JsonOptions);
        File.AppendAllText(_filePath, line + Environment.NewLine);
    }

    public IReadOnlyList<CliTranscriptEntry> ReadTail(int maxEntries)
    {
        if (maxEntries <= 0)
        {
            return [];
        }

        if (!File.Exists(_filePath))
        {
            return [];
        }

        var queue = new Queue<CliTranscriptEntry>(maxEntries);

        foreach (var line in File.ReadLines(_filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            CliTranscriptEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<CliTranscriptEntry>(line, JsonOptions);
            }
            catch
            {
                continue;
            }

            if (entry == null)
            {
                continue;
            }

            if (queue.Count == maxEntries)
            {
                queue.Dequeue();
            }
            queue.Enqueue(entry);
        }

        return queue.ToList();
    }

    /// <summary>
    /// Reads the last <paramref name="maxEntries"/> entries that match the given scope.
    /// </summary>
    public IReadOnlyList<CliTranscriptEntry> ReadTailForScope(
        int maxEntries,
        string? serverUrl,
        string? sessionId,
        string? dumpId)
    {
        if (maxEntries <= 0)
        {
            return [];
        }

        if (!File.Exists(_filePath))
        {
            return [];
        }

        var queue = new Queue<CliTranscriptEntry>(maxEntries);

        foreach (var line in File.ReadLines(_filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            CliTranscriptEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<CliTranscriptEntry>(line, JsonOptions);
            }
            catch
            {
                continue;
            }

            if (entry == null)
            {
                continue;
            }

            if (!TranscriptScope.Matches(entry, serverUrl, sessionId, dumpId))
            {
                continue;
            }

            if (queue.Count == maxEntries)
            {
                queue.Dequeue();
            }
            queue.Enqueue(entry);
        }

        return queue.ToList();
    }

    public void Clear()
    {
        if (File.Exists(_filePath))
        {
            File.WriteAllText(_filePath, string.Empty);
        }
    }

    public void FilterInPlace(Func<CliTranscriptEntry, bool> keepPredicate)
    {
        if (keepPredicate == null)
        {
            throw new ArgumentNullException(nameof(keepPredicate));
        }

        if (!File.Exists(_filePath))
        {
            return;
        }

        var tempPath = _filePath + ".tmp";

        using (var writer = new StreamWriter(tempPath, append: false))
        {
            foreach (var line in File.ReadLines(_filePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                CliTranscriptEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<CliTranscriptEntry>(line, JsonOptions);
                }
                catch
                {
                    continue;
                }

                if (entry == null || !keepPredicate(entry))
                {
                    continue;
                }

                var normalized = JsonSerializer.Serialize(entry, JsonOptions);
                writer.WriteLine(normalized);
            }
        }

        File.Copy(tempPath, _filePath, overwrite: true);
        File.Delete(tempPath);
    }
}
