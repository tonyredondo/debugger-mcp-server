using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DebuggerMcp.Cli.Configuration;
using DebuggerMcp.Cli.Serialization;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Minimal OpenRouter client for chat completions (OpenAI-compatible API surface).
/// </summary>
public sealed class OpenRouterClient(HttpClient httpClient, LlmSettings settings)
{
    private static readonly JsonSerializerOptions JsonOptions = CliJsonSerializationDefaults.CaseInsensitiveCamelCaseIgnoreNull;

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly LlmSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public async Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        if (messages == null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        var apiKey = _settings.GetEffectiveOpenRouterApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenRouter API key is not configured.");
        }

        var baseUrl = _settings.OpenRouterBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("OpenRouter base URL is not configured.");
        }

        var url = $"{baseUrl}/chat/completions";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("DebuggerMcp.Cli/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // OpenRouter recommends these for attribution; keep defaults stable.
        request.Headers.TryAddWithoutValidation("X-Title", "DebuggerMcp.Cli");
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/tonyredondo/debugger-mcp-server");

        var payload = new OpenRouterChatRequest
        {
            Model = _settings.OpenRouterModel,
            Messages = messages.Select(m => new OpenRouterChatMessage { Role = m.Role, Content = m.Content }).ToList()
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"OpenRouter request failed ({(int)response.StatusCode}): {responseBody}");
        }

        OpenRouterChatResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<OpenRouterChatResponse>(responseBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Failed to parse OpenRouter response JSON.", ex);
        }

        var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("OpenRouter response did not contain any message content.");
        }

        return content.Trim();
    }

    private sealed class OpenRouterChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "openrouter/auto";

        [JsonPropertyName("messages")]
        public List<OpenRouterChatMessage> Messages { get; set; } = [];
    }

    private sealed class OpenRouterChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private sealed class OpenRouterChatResponse
    {
        [JsonPropertyName("choices")]
        public List<OpenRouterChoice>? Choices { get; set; }
    }

    private sealed class OpenRouterChoice
    {
        [JsonPropertyName("message")]
        public OpenRouterChoiceMessage? Message { get; set; }
    }

    private sealed class OpenRouterChoiceMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}

