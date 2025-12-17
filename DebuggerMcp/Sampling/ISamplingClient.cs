#nullable enable

using ModelContextProtocol.Protocol;

namespace DebuggerMcp.Sampling;

/// <summary>
/// Abstraction over MCP sampling so the server can request LLM completions from the connected client.
/// </summary>
public interface ISamplingClient
{
    /// <summary>
    /// Gets a value indicating whether the connected MCP client supports sampling.
    /// </summary>
    bool IsSamplingSupported { get; }

    /// <summary>
    /// Gets a value indicating whether the connected MCP client supports tool use during sampling.
    /// </summary>
    bool IsToolUseSupported { get; }

    /// <summary>
    /// Requests an LLM completion from the connected MCP client via MCP sampling.
    /// </summary>
    /// <param name="request">Sampling request parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The client's sampled message result.</returns>
    Task<CreateMessageResult> RequestCompletionAsync(
        CreateMessageRequestParams request,
        CancellationToken cancellationToken = default);
}
