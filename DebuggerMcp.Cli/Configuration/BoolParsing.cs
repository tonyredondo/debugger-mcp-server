namespace DebuggerMcp.Cli.Configuration;

/// <summary>
/// Shared boolean parsing helpers for CLI settings and commands.
/// </summary>
internal static class BoolParsing
{
    internal static bool TryParse(string? value, out bool result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = false;
            return false;
        }

        if (bool.TryParse(value, out result))
        {
            return true;
        }

        var v = value.Trim().ToLowerInvariant();
        if (v is "1" or "on" or "yes" or "y" or "enabled" or "enable" or "true")
        {
            result = true;
            return true;
        }

        if (v is "0" or "off" or "no" or "n" or "disabled" or "disable" or "false")
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }
}

