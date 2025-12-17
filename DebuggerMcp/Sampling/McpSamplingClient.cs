#nullable enable

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DebuggerMcp.Sampling;

/// <summary>
/// <see cref="ISamplingClient"/> implementation backed by a connected <see cref="McpServer"/> instance.
/// </summary>
public sealed class McpSamplingClient(McpServer server) : ISamplingClient
{
    private readonly McpServer _server = server ?? throw new ArgumentNullException(nameof(server));

    /// <inheritdoc/>
    public bool IsSamplingSupported => _server.ClientCapabilities?.Sampling != null;

    /// <inheritdoc/>
    public bool IsToolUseSupported => _server.ClientCapabilities?.Sampling?.Tools != null;

    /// <inheritdoc/>
    public Task<CreateMessageResult> RequestCompletionAsync(
        CreateMessageRequestParams request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsSamplingSupported)
        {
            throw new InvalidOperationException("Sampling is not supported by the connected client.");
        }

        return _server.SampleAsync(request, cancellationToken).AsTask();
    }
}
