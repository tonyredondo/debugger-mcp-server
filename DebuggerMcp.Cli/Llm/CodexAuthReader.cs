using System.Text.Json;

namespace DebuggerMcp.Cli.Llm;

/// <summary>
/// Reads OpenAI API keys from Codex auth files (best-effort).
/// </summary>
internal static class CodexAuthReader
{
    internal static string GetDefaultAuthFilePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".codex", "auth.json");
    }

    internal static string? TryReadOpenAiApiKey(string? overridePath = null)
    {
        var path = string.IsNullOrWhiteSpace(overridePath) ? GetDefaultAuthFilePath() : overridePath.Trim();
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(prop.Name, "openai_api_key", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = prop.Value.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}

