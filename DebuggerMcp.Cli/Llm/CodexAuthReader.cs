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
        var rawPath = string.IsNullOrWhiteSpace(overridePath) ? GetDefaultAuthFilePath() : overridePath.Trim();
        var path = ExpandUserPath(rawPath);
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

            var root = doc.RootElement;

            // Common shapes:
            // - { "OPENAI_API_KEY": "..." }
            // - { "openai_api_key": "..." }
            if (TryGetStringFromObject(root, out var key,
                    "OPENAI_API_KEY",
                    "openai_api_key"))
            {
                return key;
            }

            // - { "openai": { "api_key": "..." } }
            // - { "openai": { "OPENAI_API_KEY": "..." } }
            if (TryGetObject(root, "openai", out var openai) &&
                TryGetStringFromObject(openai, out key,
                    "api_key",
                    "OPENAI_API_KEY",
                    "openai_api_key"))
            {
                return key;
            }

            // - { "apiKeys": { "openai": "..." } }
            // - { "api_keys": { "openai": "..." } }
            if (TryGetObject(root, "apiKeys", out var apiKeys) ||
                TryGetObject(root, "api_keys", out apiKeys))
            {
                if (TryGetStringFromObject(apiKeys, out key, "openai", "OPENAI_API_KEY"))
                {
                    return key;
                }

                if (TryGetObject(apiKeys, "openai", out var apiKeysOpenAi) &&
                    TryGetStringFromObject(apiKeysOpenAi, out key, "api_key", "OPENAI_API_KEY"))
                {
                    return key;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetObject(JsonElement obj, string propertyName, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (!string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (prop.Value.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            value = prop.Value;
            return true;
        }

        return false;
    }

    private static bool TryGetStringFromObject(JsonElement obj, out string? value, params string[] keys)
    {
        value = null;
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (!keys.Any(k => string.Equals(prop.Name, k, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var s = prop.Value.GetString();
            if (string.IsNullOrWhiteSpace(s))
            {
                return false;
            }

            value = s.Trim();
            return true;
        }

        return false;
    }

    private static string ExpandUserPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) ||
            path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var suffix = path[2..];
            return Path.Combine(home, suffix);
        }

        return path;
    }
}
