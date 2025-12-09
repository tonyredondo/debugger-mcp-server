using System.Globalization;
using System.Text.Json.Serialization;

namespace DebuggerMcp.Cli.Models;

/// <summary>
/// Represents information about an uploaded dump file.
/// </summary>
public class DumpInfo
{
    /// <summary>
    /// Gets or sets the unique dump ID.
    /// </summary>
    [JsonPropertyName("dumpId")]
    public string DumpId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user ID that owns this dump.
    /// </summary>
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the original file name.
    /// </summary>
    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the upload timestamp.
    /// </summary>
    [JsonPropertyName("uploadedAt")]
    public DateTime UploadedAt { get; set; }

    /// <summary>
    /// Gets or sets the optional description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the detected dump format.
    /// </summary>
    [JsonPropertyName("dumpFormat")]
    public string? DumpFormat { get; set; }

    /// <summary>
    /// Gets or sets whether this dump is from an Alpine Linux system (musl libc).
    /// </summary>
    /// <remarks>
    /// Alpine dumps can only be debugged on Alpine hosts due to musl vs glibc differences.
    /// </remarks>
    [JsonPropertyName("isAlpineDump")]
    public bool? IsAlpineDump { get; set; }

    /// <summary>
    /// Gets or sets the detected .NET runtime version required to debug this dump.
    /// </summary>
    [JsonPropertyName("runtimeVersion")]
    public string? RuntimeVersion { get; set; }

    /// <summary>
    /// Gets or sets the processor architecture of the dump (e.g., "arm64", "x64").
    /// </summary>
    [JsonPropertyName("architecture")]
    public string? Architecture { get; set; }

    /// <summary>
    /// Gets or sets whether a custom executable has been uploaded for this dump.
    /// </summary>
    /// <remarks>
    /// For standalone .NET apps, the original executable is needed for proper debugging.
    /// </remarks>
    [JsonPropertyName("hasExecutable")]
    public bool HasExecutable { get; set; }

    /// <summary>
    /// Gets or sets the name of the uploaded executable.
    /// </summary>
    [JsonPropertyName("executableName")]
    public string? ExecutableName { get; set; }

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
/// Response from dump upload.
/// </summary>
public class DumpUploadResponse : DumpInfo
{
}

/// <summary>
/// Response from binary upload for standalone apps.
/// </summary>
public class BinaryUploadResponse
{
    /// <summary>
    /// Gets or sets the message.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
    
    /// <summary>
    /// Gets or sets the dump ID.
    /// </summary>
    [JsonPropertyName("dumpId")]
    public string? DumpId { get; set; }
    
    /// <summary>
    /// Gets or sets the executable name.
    /// </summary>
    [JsonPropertyName("executableName")]
    public string? ExecutableName { get; set; }
    
    /// <summary>
    /// Gets or sets the executable path.
    /// </summary>
    [JsonPropertyName("executablePath")]
    public string? ExecutablePath { get; set; }
    
    /// <summary>
    /// Gets or sets the file size.
    /// </summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>
/// Response containing a list of dumps.
/// </summary>
public class DumpListResponse
{
    /// <summary>
    /// Gets or sets the list of dumps.
    /// </summary>
    [JsonPropertyName("dumps")]
    public List<DumpInfo> Dumps { get; set; } = [];
}

/// <summary>
/// Response from dump deletion.
/// </summary>
public class DumpDeleteResponse
{
    /// <summary>
    /// Gets or sets whether the deletion was successful.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a message about the deletion.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
