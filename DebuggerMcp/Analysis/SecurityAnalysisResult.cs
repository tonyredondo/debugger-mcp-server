using System.Text.Json.Serialization;

namespace DebuggerMcp.Analysis;

/// <summary>
/// Represents the result of a security vulnerability analysis on a crash dump.
/// </summary>
public class SecurityAnalysisResult
{
    /// <summary>
    /// Gets or sets the list of detected vulnerabilities.
    /// </summary>
    [JsonPropertyName("vulnerabilities")]
    public List<Vulnerability> Vulnerabilities { get; set; } = new();

    /// <summary>
    /// Gets or sets the overall security risk level.
    /// </summary>
    [JsonPropertyName("overallRisk")]
    public SecurityRisk OverallRisk { get; set; } = SecurityRisk.None;

    /// <summary>
    /// Gets or sets a summary of the security analysis.
    /// </summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets security-related recommendations.
    /// </summary>
    [JsonPropertyName("recommendations")]
    public List<string> Recommendations { get; set; } = new();

    /// <summary>
    /// Gets or sets memory protection information.
    /// </summary>
    [JsonPropertyName("memoryProtections")]
    public MemoryProtectionInfo? MemoryProtections { get; set; }

    /// <summary>
    /// Gets or sets heap integrity analysis results.
    /// </summary>
    [JsonPropertyName("heapIntegrity")]
    public HeapIntegrityInfo? HeapIntegrity { get; set; }

    /// <summary>
    /// Gets or sets stack integrity analysis results.
    /// </summary>
    [JsonPropertyName("stackIntegrity")]
    public StackIntegrityInfo? StackIntegrity { get; set; }

    /// <summary>
    /// Gets or sets when the analysis was performed.
    /// </summary>
    [JsonPropertyName("analyzedAt")]
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets raw debugger output used in analysis.
    /// </summary>
    [JsonPropertyName("rawOutput")]
    public Dictionary<string, string> RawOutput { get; set; } = new();
}

/// <summary>
/// Represents a detected security vulnerability.
/// </summary>
public class Vulnerability
{
    /// <summary>
    /// Gets or sets the type of vulnerability.
    /// </summary>
    [JsonPropertyName("type")]
    public VulnerabilityType Type { get; set; }

    /// <summary>
    /// Gets or sets the severity of the vulnerability.
    /// </summary>
    [JsonPropertyName("severity")]
    public VulnerabilitySeverity Severity { get; set; }

    /// <summary>
    /// Gets or sets a description of the vulnerability.
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets detailed technical information.
    /// </summary>
    [JsonPropertyName("details")]
    public string? Details { get; set; }

    /// <summary>
    /// Gets or sets the memory address related to this vulnerability.
    /// </summary>
    [JsonPropertyName("address")]
    public string? Address { get; set; }

    /// <summary>
    /// Gets or sets the affected module.
    /// </summary>
    [JsonPropertyName("module")]
    public string? Module { get; set; }

    /// <summary>
    /// Gets or sets the affected function.
    /// </summary>
    [JsonPropertyName("function")]
    public string? Function { get; set; }

    /// <summary>
    /// Gets or sets indicators of compromise or exploitation.
    /// </summary>
    [JsonPropertyName("indicators")]
    public List<string> Indicators { get; set; } = new();

    /// <summary>
    /// Gets or sets remediation steps.
    /// </summary>
    [JsonPropertyName("remediation")]
    public List<string> Remediation { get; set; } = new();

    /// <summary>
    /// Gets or sets related CWE (Common Weakness Enumeration) IDs.
    /// </summary>
    [JsonPropertyName("cweIds")]
    public List<string> CweIds { get; set; } = new();

    /// <summary>
    /// Gets or sets the confidence level of the detection.
    /// </summary>
    [JsonPropertyName("confidence")]
    public DetectionConfidence Confidence { get; set; } = DetectionConfidence.Medium;
}

/// <summary>
/// Types of security vulnerabilities that can be detected.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VulnerabilityType
{
    /// <summary>
    /// Buffer overflow vulnerability (stack or heap).
    /// </summary>
    BufferOverflow,

    /// <summary>
    /// Stack buffer overflow specifically.
    /// </summary>
    StackBufferOverflow,

    /// <summary>
    /// Heap buffer overflow specifically.
    /// </summary>
    HeapBufferOverflow,

    /// <summary>
    /// Use-after-free vulnerability.
    /// </summary>
    UseAfterFree,

    /// <summary>
    /// Double-free vulnerability.
    /// </summary>
    DoubleFree,

    /// <summary>
    /// Null pointer dereference.
    /// </summary>
    NullDereference,

    /// <summary>
    /// Integer overflow or underflow.
    /// </summary>
    IntegerOverflow,

    /// <summary>
    /// Format string vulnerability.
    /// </summary>
    FormatString,

    /// <summary>
    /// Heap corruption detected.
    /// </summary>
    HeapCorruption,

    /// <summary>
    /// Stack corruption (e.g., canary corruption).
    /// </summary>
    StackCorruption,

    /// <summary>
    /// Uninitialized memory use.
    /// </summary>
    UninitializedMemory,

    /// <summary>
    /// Memory leak that could lead to DoS.
    /// </summary>
    MemoryExhaustion,

    /// <summary>
    /// Type confusion vulnerability.
    /// </summary>
    TypeConfusion,

    /// <summary>
    /// Race condition or data race.
    /// </summary>
    RaceCondition,

    /// <summary>
    /// Arbitrary code execution potential.
    /// </summary>
    CodeExecution,

    /// <summary>
    /// Information disclosure potential.
    /// </summary>
    InformationDisclosure,

    /// <summary>
    /// Denial of Service vulnerability.
    /// </summary>
    DenialOfService,

    /// <summary>
    /// Other/unknown vulnerability type.
    /// </summary>
    Other
}

/// <summary>
/// Severity levels for vulnerabilities.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VulnerabilitySeverity
{
    /// <summary>
    /// Informational finding.
    /// </summary>
    Info,

    /// <summary>
    /// Low severity - minor issue.
    /// </summary>
    Low,

    /// <summary>
    /// Medium severity - should be addressed.
    /// </summary>
    Medium,

    /// <summary>
    /// High severity - important to fix.
    /// </summary>
    High,

    /// <summary>
    /// Critical severity - immediate action required.
    /// </summary>
    Critical
}

/// <summary>
/// Overall security risk assessment.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityRisk
{
    /// <summary>
    /// No security issues detected.
    /// </summary>
    None,

    /// <summary>
    /// Low risk - minor issues.
    /// </summary>
    Low,

    /// <summary>
    /// Medium risk - vulnerabilities present.
    /// </summary>
    Medium,

    /// <summary>
    /// High risk - serious vulnerabilities.
    /// </summary>
    High,

    /// <summary>
    /// Critical risk - actively exploited or exploitable.
    /// </summary>
    Critical
}

/// <summary>
/// Confidence level of vulnerability detection.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DetectionConfidence
{
    /// <summary>
    /// Low confidence - may be false positive.
    /// </summary>
    Low,

    /// <summary>
    /// Medium confidence - likely accurate.
    /// </summary>
    Medium,

    /// <summary>
    /// High confidence - very likely accurate.
    /// </summary>
    High,

    /// <summary>
    /// Confirmed - definite vulnerability.
    /// </summary>
    Confirmed
}

/// <summary>
/// Information about memory protection mechanisms.
/// </summary>
public class MemoryProtectionInfo
{
    /// <summary>
    /// Gets or sets whether DEP/NX is enabled.
    /// </summary>
    [JsonPropertyName("depEnabled")]
    public bool DepEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether ASLR is enabled.
    /// </summary>
    [JsonPropertyName("aslrEnabled")]
    public bool AslrEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether stack canaries are present.
    /// </summary>
    [JsonPropertyName("stackCanariesPresent")]
    public bool StackCanariesPresent { get; set; }

    /// <summary>
    /// Gets or sets whether CFG (Control Flow Guard) is enabled.
    /// </summary>
    [JsonPropertyName("cfgEnabled")]
    public bool CfgEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether SafeSEH is enabled (Windows).
    /// </summary>
    [JsonPropertyName("safeSehEnabled")]
    public bool SafeSehEnabled { get; set; }

    /// <summary>
    /// Gets or sets modules without ASLR.
    /// </summary>
    [JsonPropertyName("modulesWithoutAslr")]
    public List<string> ModulesWithoutAslr { get; set; } = new();

    /// <summary>
    /// Gets or sets modules without DEP.
    /// </summary>
    [JsonPropertyName("modulesWithoutDep")]
    public List<string> ModulesWithoutDep { get; set; } = new();
}

/// <summary>
/// Information about heap integrity.
/// </summary>
public class HeapIntegrityInfo
{
    /// <summary>
    /// Gets or sets whether heap corruption was detected.
    /// </summary>
    [JsonPropertyName("corruptionDetected")]
    public bool CorruptionDetected { get; set; }

    /// <summary>
    /// Gets or sets the number of corrupted heap entries.
    /// </summary>
    [JsonPropertyName("corruptedEntries")]
    public int CorruptedEntries { get; set; }

    /// <summary>
    /// Gets or sets addresses of corrupted heap entries.
    /// </summary>
    [JsonPropertyName("corruptedAddresses")]
    public List<string> CorruptedAddresses { get; set; } = new();

    /// <summary>
    /// Gets or sets whether free list corruption was detected.
    /// </summary>
    [JsonPropertyName("freeListCorruption")]
    public bool FreeListCorruption { get; set; }

    /// <summary>
    /// Gets or sets heap metadata issues.
    /// </summary>
    [JsonPropertyName("metadataIssues")]
    public List<string> MetadataIssues { get; set; } = new();
}

/// <summary>
/// Information about stack integrity.
/// </summary>
public class StackIntegrityInfo
{
    /// <summary>
    /// Gets or sets whether stack corruption was detected.
    /// </summary>
    [JsonPropertyName("corruptionDetected")]
    public bool CorruptionDetected { get; set; }

    /// <summary>
    /// Gets or sets whether stack canary was corrupted.
    /// </summary>
    [JsonPropertyName("canaryCorrupted")]
    public bool CanaryCorrupted { get; set; }

    /// <summary>
    /// Gets or sets whether return address appears overwritten.
    /// </summary>
    [JsonPropertyName("returnAddressOverwritten")]
    public bool ReturnAddressOverwritten { get; set; }

    /// <summary>
    /// Gets or sets suspicious stack patterns found.
    /// </summary>
    [JsonPropertyName("suspiciousPatterns")]
    public List<string> SuspiciousPatterns { get; set; } = new();

    /// <summary>
    /// Gets or sets the affected thread ID.
    /// </summary>
    [JsonPropertyName("affectedThread")]
    public string? AffectedThread { get; set; }
}

