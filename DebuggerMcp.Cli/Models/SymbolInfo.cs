using System.Globalization;
using System.Text.Json.Serialization;

namespace DebuggerMcp.Cli.Models;

/// <summary>
/// Represents information about an uploaded symbol file.
/// </summary>
public class SymbolInfo
{
    /// <summary>
    /// Gets or sets the symbol file name.
    /// </summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the dump ID this symbol is associated with.
    /// </summary>
    [JsonPropertyName("dumpId")]
    public string? DumpId { get; set; }

    /// <summary>
    /// Gets or sets the detected symbol format.
    /// </summary>
    [JsonPropertyName("symbolFormat")]
    public string? SymbolFormat { get; set; }

    /// <summary>
    /// Gets the size formatted as a human-readable string.
    /// </summary>
    [JsonIgnore]
    public string FormattedSize => FormatBytes(Size);

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
}

/// <summary>
/// Response from symbol upload.
/// </summary>
public class SymbolUploadResponse : SymbolInfo
{
    /// <summary>
    /// Gets or sets whether the upload was successful.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a message about the upload.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Response from batch symbol upload.
/// </summary>
public class SymbolBatchUploadResponse
{
    /// <summary>
    /// Gets or sets the total number of files processed.
    /// </summary>
    [JsonPropertyName("totalFiles")]
    public int TotalFiles { get; set; }

    /// <summary>
    /// Gets or sets the count of successful uploads.
    /// </summary>
    [JsonPropertyName("successfulUploads")]
    public int SuccessfulUploads { get; set; }

    /// <summary>
    /// Gets or sets the count of failed uploads.
    /// </summary>
    [JsonPropertyName("failedUploads")]
    public int FailedUploads { get; set; }

    /// <summary>
    /// Gets or sets the individual results for each file.
    /// </summary>
    [JsonPropertyName("results")]
    public List<SymbolUploadResult> Results { get; set; } = [];
}

/// <summary>
/// Result of a single symbol upload in a batch operation.
/// </summary>
public class SymbolUploadResult
{
    /// <summary>
    /// Gets or sets the file name.
    /// </summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the upload was successful.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the result message.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Response from symbol ZIP upload.
/// </summary>
public class SymbolZipUploadResponse
{
    /// <summary>
    /// Gets or sets the dump ID.
    /// </summary>
    [JsonPropertyName("dumpId")]
    public string DumpId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of files extracted.
    /// </summary>
    [JsonPropertyName("extractedFilesCount")]
    public int ExtractedFilesCount { get; set; }

    /// <summary>
    /// Gets or sets the number of symbol directories found.
    /// </summary>
    [JsonPropertyName("symbolDirectoriesCount")]
    public int SymbolDirectoriesCount { get; set; }

    /// <summary>
    /// Gets or sets the number of symbol files found (.dbg, .pdb, etc.).
    /// </summary>
    [JsonPropertyName("symbolFilesCount")]
    public int SymbolFilesCount { get; set; }

    /// <summary>
    /// Gets or sets the result message.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of symbol directories (relative paths).
    /// </summary>
    [JsonPropertyName("symbolDirectories")]
    public List<string> SymbolDirectories { get; set; } = [];
}

/// <summary>
/// Response containing a list of symbols for a dump.
/// </summary>
public class SymbolListResponse
{
    /// <summary>
    /// Gets or sets the dump ID.
    /// </summary>
    [JsonPropertyName("dumpId")]
    public string DumpId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of symbol file names.
    /// </summary>
    [JsonPropertyName("symbols")]
    public List<string> Symbols { get; set; } = [];
}
