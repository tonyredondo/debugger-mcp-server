using System.Runtime.InteropServices;

namespace DebuggerMcp.Tests.TestInfrastructure;

/// <summary>
/// Skips integration tests that require the runtime-shipped <c>createdump</c> helper when it is unavailable.
/// </summary>
public sealed class RequiresCreatedumpFactAttribute : FactAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RequiresCreatedumpFactAttribute"/> class.
    /// </summary>
    public RequiresCreatedumpFactAttribute()
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var candidates = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(runtimeDir, "createdump.exe"), Path.Combine(runtimeDir, "createdump") }
            : new[] { Path.Combine(runtimeDir, "createdump") };

        if (!candidates.Any(File.Exists))
        {
            Skip = "createdump is not available in the current runtime";
        }
    }
}
