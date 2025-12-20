using System.Reflection;
using DebuggerMcp.Reporting;
using Xunit;

namespace DebuggerMcp.Tests.Reporting;

[Collection("SourceContextEnricher")]
public class SourceContextEnricherAdditionalCoverageTests
{
    [Fact]
    public void TryExtractContext_StringOverload_ReturnsSanitizedWindow()
    {
        var method = typeof(SourceContextEnricher).GetMethod(
            "TryExtractContext",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types:
            [
                typeof(string),
                typeof(int),
                typeof(List<string>).MakeByRefType(),
                typeof(int).MakeByRefType(),
                typeof(int).MakeByRefType()
            ],
            modifiers: null);

        Assert.NotNull(method);

        var args = new object?[]
        {
            "line1\npassword=\"supersecret\"\nline3\n",
            2,
            null,
            0,
            0
        };

        var ok = (bool)method!.Invoke(null, args)!;
        Assert.True(ok);

        var lines = Assert.IsType<List<string>>(args[2]);
        Assert.Contains(lines, l => l.Contains("<redacted>", StringComparison.OrdinalIgnoreCase));
        Assert.True((int)args[3]! > 0);
        Assert.True((int)args[4]! >= (int)args[3]!);
    }

    [Fact]
    public void IsPathUnderAnyRoot_UsesRootMatchingHelper()
    {
        var method = typeof(SourceContextEnricher).GetMethod(
            "IsPathUnderAnyRoot",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var root = Path.Combine(Path.GetTempPath(), $"SourceContextEnricherAdditionalCoverageTests_{Guid.NewGuid():N}");
        var file = Path.Combine(root, "a", "b.txt");

        var roots = new List<string> { root };
        var ok = (bool)method!.Invoke(null, new object[] { file, roots })!;
        Assert.True(ok);
    }
}

