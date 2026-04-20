using DebuggerMcp.Analysis;

namespace DebuggerMcp;

/// <summary>
/// Exposes optional debugger diagnostics that are shared across debugger implementations but do
/// not belong on the core lifecycle interface.
/// </summary>
public interface IDebuggerDiagnostics
{
    /// <summary>
    /// Returns the best normalized platform information currently available for the open dump.
    /// </summary>
    /// <returns>The normalized platform information, or <c>null</c> when it cannot be determined.</returns>
    PlatformInfo? GetPlatformInfo();

    /// <summary>
    /// Returns registers for the top frame of the specified OS threads.
    /// </summary>
    /// <param name="threadIds">OS thread identifiers to inspect.</param>
    /// <returns>A dictionary keyed by OS thread identifier.</returns>
    Dictionary<uint, ClrRegisterSet> GetTopFrameRegisters(IEnumerable<uint> threadIds);

    /// <summary>
    /// Returns register payloads for frames belonging to a specific OS thread.
    /// </summary>
    /// <param name="threadId">The OS thread identifier to inspect.</param>
    /// <returns>Per-frame register payloads for the thread.</returns>
    IReadOnlyList<DebuggerFrameRegisters> GetPerFrameRegisters(uint threadId);
}

/// <summary>
/// Represents the registers captured for a specific thread frame.
/// </summary>
public sealed class DebuggerFrameRegisters
{
    /// <summary>
    /// Gets or sets the OS thread identifier associated with the frame.
    /// </summary>
    public uint ThreadId { get; set; }

    /// <summary>
    /// Gets or sets the stack pointer for the frame.
    /// </summary>
    public ulong StackPointer { get; set; }

    /// <summary>
    /// Gets or sets the normalized register payload for the frame.
    /// </summary>
    public Dictionary<string, string> Registers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
