#nullable enable

namespace DebuggerMcp.Analysis;

/// <summary>
/// Helper methods for deciding when .NET-specific analysis is possible.
/// </summary>
internal static class DotNetAnalyzerAvailability
{
    internal static bool ShouldUseDotNetAnalyzer(bool isSosLoaded, bool isClrMdOpen)
        => isSosLoaded || isClrMdOpen;
}

