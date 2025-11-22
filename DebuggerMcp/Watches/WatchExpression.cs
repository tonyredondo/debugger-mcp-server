using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DebuggerMcp.Watches;

/// <summary>
/// Represents a watch expression that tracks a memory address, variable, or expression.
/// Watches are persisted per-dump and survive session restarts.
/// </summary>
public class WatchExpression
{
    /// <summary>
    /// Gets or sets the unique identifier for this watch.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets or sets the dump ID this watch is associated with.
    /// </summary>
    [JsonPropertyName("dumpId")]
    public string DumpId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the expression to evaluate (address, variable name, or expression).
    /// </summary>
    /// <remarks>
    /// Examples:
    /// - Memory address: "0x12345678"
    /// - Variable: "g_DataManager" or "myModule!myVariable"
    /// - Object: "0x00007ff812345678" (for .NET objects with !do)
    /// - Expression: "poi(esp+8)" or "@@(myVar.Field)"
    /// </remarks>
    [JsonPropertyName("expression")]
    public string Expression { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a human-readable description of what this watch represents.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the type of watch expression.
    /// </summary>
    [JsonPropertyName("type")]
    public WatchType Type { get; set; } = WatchType.Expression;

    /// <summary>
    /// Gets or sets when this watch was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets when this watch was last evaluated.
    /// </summary>
    [JsonPropertyName("lastEvaluatedAt")]
    public DateTime? LastEvaluatedAt { get; set; }

    /// <summary>
    /// Gets or sets the cached last value (for quick reference).
    /// </summary>
    [JsonPropertyName("lastValue")]
    public string? LastValue { get; set; }

    /// <summary>
    /// Gets or sets optional tags for categorization.
    /// </summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();
}

/// <summary>
/// Specifies the type of watch expression.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WatchType
{
    /// <summary>
    /// A memory address to display (e.g., "0x12345678").
    /// Uses dd/dq/db commands.
    /// </summary>
    MemoryAddress,

    /// <summary>
    /// A variable or symbol name (e.g., "g_DataManager", "myModule!myVar").
    /// Uses dt or ? commands.
    /// </summary>
    Variable,

    /// <summary>
    /// A .NET managed object address (uses !do command).
    /// </summary>
    Object,

    /// <summary>
    /// A general debugger expression (e.g., "poi(esp+8)").
    /// Uses ? or expression evaluation commands.
    /// </summary>
    Expression
}

/// <summary>
/// Result of evaluating a watch expression.
/// </summary>
public class WatchEvaluationResult
{
    /// <summary>
    /// Gets or sets the watch ID that was evaluated.
    /// </summary>
    [JsonPropertyName("watchId")]
    public string WatchId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the expression that was evaluated.
    /// </summary>
    [JsonPropertyName("expression")]
    public string Expression { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the watch type.
    /// </summary>
    [JsonPropertyName("type")]
    public WatchType Type { get; set; }

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the evaluated value.
    /// </summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>
    /// Gets or sets whether the evaluation was successful.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the error message if evaluation failed.
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets when this evaluation occurred.
    /// </summary>
    [JsonPropertyName("evaluatedAt")]
    public DateTime EvaluatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Container for all watch evaluation results for a dump.
/// </summary>
public class WatchEvaluationReport
{
    /// <summary>
    /// Gets or sets the dump ID.
    /// </summary>
    [JsonPropertyName("dumpId")]
    public string DumpId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when this evaluation report was generated.
    /// </summary>
    [JsonPropertyName("evaluatedAt")]
    public DateTime EvaluatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the total number of watches.
    /// </summary>
    [JsonPropertyName("totalWatches")]
    public int TotalWatches { get; set; }

    /// <summary>
    /// Gets or sets the number of successful evaluations.
    /// </summary>
    [JsonPropertyName("successfulEvaluations")]
    public int SuccessfulEvaluations { get; set; }

    /// <summary>
    /// Gets or sets the number of failed evaluations.
    /// </summary>
    [JsonPropertyName("failedEvaluations")]
    public int FailedEvaluations { get; set; }

    /// <summary>
    /// Gets or sets the individual watch results.
    /// </summary>
    [JsonPropertyName("watches")]
    public List<WatchEvaluationResult> Watches { get; set; } = new();

    /// <summary>
    /// Gets or sets insights derived from watch evaluations.
    /// </summary>
    [JsonPropertyName("insights")]
    public List<string> Insights { get; set; } = new();
}

