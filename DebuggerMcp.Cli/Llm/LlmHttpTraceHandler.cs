#nullable enable

using System.Net.Http.Headers;
using System.Text;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Delegating handler that captures LLM provider HTTP request/response payloads into a trace store.
/// </summary>
/// <remarks>
/// This is best-effort and should never break the normal HTTP flow.
/// </remarks>
internal sealed class LlmHttpTraceHandler(LlmTraceStore trace, string providerLabel) : DelegatingHandler
{
    private readonly LlmTraceStore _trace = trace ?? throw new ArgumentNullException(nameof(trace));
    private readonly string _providerLabel = SanitizeFileComponent(string.IsNullOrWhiteSpace(providerLabel) ? "llm" : providerLabel.Trim());

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var id = _trace.NextId();
        var startedUtc = DateTime.UtcNow;

        string requestBody = string.Empty;
        try
        {
            if (request.Content != null)
            {
                requestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                request.Content = CloneContent(request.Content, requestBody);
            }
        }
        catch
        {
            // Ignore; trace should not break the request.
        }

        try
        {
            _trace.AppendEvent(new
            {
                kind = "llm_http_request",
                id,
                timestampUtc = startedUtc,
                provider = _providerLabel,
                method = request.Method.Method,
                url = request.RequestUri?.ToString() ?? string.Empty,
                bodyFile = $"{id:0000}.{_providerLabel}.request.json"
            });
            _trace.WriteJson($"{id:0000}.{_providerLabel}.request.json", requestBody);
        }
        catch
        {
            // Ignore.
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                _trace.AppendEvent(new
                {
                    kind = "llm_http_exception",
                    id,
                    timestampUtc = DateTime.UtcNow,
                    provider = _providerLabel,
                    message = ex.Message
                });
            }
            catch
            {
                // ignore
            }
            throw;
        }

        byte[] responseBytes = [];
        var responseCharset = response.Content?.Headers.ContentType?.CharSet;
        try
        {
            if (response.Content != null)
            {
                responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                response.Content = CloneContent(response.Content, responseBytes);
            }
        }
        catch
        {
            // Ignore; don't break downstream parsing.
        }

        try
        {
            var completedUtc = DateTime.UtcNow;
            var responseText = responseBytes.Length == 0 ? string.Empty : DecodeText(responseBytes, responseCharset);
            _trace.AppendEvent(new
            {
                kind = "llm_http_response",
                id,
                timestampUtc = completedUtc,
                provider = _providerLabel,
                status = (int)response.StatusCode,
                durationMs = (int)Math.Max(0, (completedUtc - startedUtc).TotalMilliseconds),
                bodyFile = $"{id:0000}.{_providerLabel}.response.json"
            });
            _trace.WriteJson($"{id:0000}.{_providerLabel}.response.json", responseText);
        }
        catch
        {
            // ignore
        }

        return response;
    }

    private static string DecodeText(byte[] bytes, string? charset)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        Encoding encoding;
        try
        {
            encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        }
        catch
        {
            encoding = Encoding.UTF8;
        }

        try
        {
            return encoding.GetString(bytes);
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>
    /// Clones request content so downstream can still read it after tracing.
    /// </summary>
    private static HttpContent CloneContent(HttpContent original, string body)
    {
        var mediaType = original.Headers.ContentType?.MediaType ?? "application/json";
        var charset = original.Headers.ContentType?.CharSet;
        Encoding encoding;
        try
        {
            encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        }
        catch
        {
            encoding = Encoding.UTF8;
        }
        var content = new StringContent(body ?? string.Empty, encoding, mediaType);
        CopyContentHeaders(original, content);
        return content;
    }

    /// <summary>
    /// Clones response content so downstream can still read it after tracing.
    /// </summary>
    private static HttpContent CloneContent(HttpContent original, byte[] bytes)
    {
        bytes ??= [];
        var content = new ByteArrayContent(bytes);
        CopyContentHeaders(original, content);
        return content;
    }

    /// <summary>
    /// Copies content headers from one content instance to another.
    /// </summary>
    private static void CopyContentHeaders(HttpContent from, HttpContent to)
    {
        foreach (var header in from.Headers)
        {
            // ContentType is already set by StringContent; overwrite to preserve charset/etc.
            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            to.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (from.Headers.ContentType != null)
        {
            try
            {
                to.Headers.ContentType = MediaTypeHeaderValue.Parse(from.Headers.ContentType.ToString());
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// Sanitizes the provider label for use in file names.
    /// </summary>
    private static string SanitizeFileComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "llm";
        }

        var s = value.Trim();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[^a-zA-Z0-9._-]+", "_", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        s = s.Trim('_');
        return string.IsNullOrWhiteSpace(s) ? "llm" : s;
    }
}
