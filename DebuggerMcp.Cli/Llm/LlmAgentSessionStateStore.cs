using System.Collections.Concurrent;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Stores per-scope <c>llmagent</c> orchestration state in-memory so it can persist across multiple prompts
/// inside a single interactive session.
/// </summary>
/// <remarks>
/// Scope is defined to match transcript scoping: (serverUrl, sessionId, dumpId).
/// </remarks>
internal static class LlmAgentSessionStateStore
{
    private static readonly ConcurrentDictionary<TranscriptScopeKey, LlmAgentSessionState> States = new();

    /// <summary>
    /// Gets the current orchestration state for the provided scope, creating a new one if none exists.
    /// </summary>
    public static LlmAgentSessionState GetOrCreate(string? serverUrl, string? sessionId, string? dumpId)
        => States.GetOrAdd(new TranscriptScopeKey(serverUrl, sessionId, dumpId), static key => new LlmAgentSessionState(key));

    /// <summary>
    /// Clears and removes any state for the provided scope.
    /// </summary>
    public static void Reset(string? serverUrl, string? sessionId, string? dumpId)
        => States.TryRemove(new TranscriptScopeKey(serverUrl, sessionId, dumpId), out _);

    /// <summary>
    /// Small immutable key that mirrors transcript scoping: (serverUrl, sessionId, dumpId).
    /// </summary>
    internal readonly record struct TranscriptScopeKey(string? ServerUrl, string? SessionId, string? DumpId);
}
