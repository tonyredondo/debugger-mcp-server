using System.Reflection;
using DebuggerMcp.Analysis;
using Xunit;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Reflection-based coverage tests for private helpers in <see cref="ProcessInfoExtractor"/>.
/// </summary>
public class ProcessInfoExtractorPrivateHelpersTests
{
    private static bool InvokePrivateStaticBool(string methodName, params object[] args)
    {
        var method = typeof(ProcessInfoExtractor).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, args)!;
    }

    private static (bool Success, ulong Value) InvokePrivateTryParsePointer(string input)
    {
        var method = typeof(ProcessInfoExtractor).GetMethod("TryParsePointer", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        object?[] args = { input, 0UL };
        var success = (bool)method!.Invoke(null, args)!;
        return (success, (ulong)args[1]!);
    }

    private static string InvokePrivateStaticString(string methodName, params object[] args)
    {
        var method = typeof(ProcessInfoExtractor).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, args)!;
    }

    [Theory]
    [InlineData("dotnet", true)]
    [InlineData("Samples.BuggyBits", true)]
    [InlineData("python3", true)]
    [InlineData("my-app", true)]
    [InlineData("ab", false)]
    [InlineData("1tool", false)]
    [InlineData("bad name", false)]
    [InlineData("tool|rm", false)]
    [InlineData("tool$HOME", false)]
    public void IsValidExecutableName_WithVariousInputs_ReturnsExpected(string input, bool expected)
    {
        var actual = InvokePrivateStaticBool("IsValidExecutableName", input);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("/usr/bin/dotnet", true)]
    [InlineData("./app", true)]
    [InlineData("../app", true)]
    [InlineData("--help", true)]
    [InlineData("-verbose", true)]
    [InlineData("https://example.com", true)]
    [InlineData("ab", false)]
    [InlineData("a", false)]
    [InlineData("x y z", true)]
    [InlineData("%%%%%not-arg%%%%%", false)]
    public void IsValidArgument_WithVariousInputs_ReturnsExpected(string input, bool expected)
    {
        var actual = InvokePrivateStaticBool("IsValidArgument", input);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("/usr/bin/dotnet", true)]
    [InlineData("./relative/path", true)]
    [InlineData("../relative/path", true)]
    [InlineData("dir/file.txt", true)]
    [InlineData("dotnet", true)]
    [InlineData("not-a-path", false)]
    [InlineData("a/b\nc", false)]
    public void LooksLikeFilePath_WithVariousInputs_ReturnsExpected(string input, bool expected)
    {
        var actual = InvokePrivateStaticBool("LooksLikeFilePath", input);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("PATH", true)]
    [InlineData("_X", true)]
    [InlineData("A1_B2", true)]
    [InlineData("", false)]
    [InlineData("1BAD", false)]
    [InlineData("BAD-KEY", false)]
    public void IsValidEnvVarKey_WithVariousInputs_ReturnsExpected(string input, bool expected)
    {
        var actual = InvokePrivateStaticBool("IsValidEnvVarKey", input);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("abc", true)]
    [InlineData("\u0001\u0002", false)]
    [InlineData("a\tb\nc\r", true)]
    public void IsPrintableString_WithVariousInputs_ReturnsExpected(string input, bool expected)
    {
        var actual = InvokePrivateStaticBool("IsPrintableString", input);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("0x10", true, 16UL)]
    [InlineData("10", true, 16UL)]
    [InlineData("", false, 0UL)]
    [InlineData("0xZZ", false, 0UL)]
    public void TryParsePointer_WithVariousInputs_ReturnsExpected(string input, bool expectedSuccess, ulong expectedValue)
    {
        var (success, value) = InvokePrivateTryParsePointer(input);
        Assert.Equal(expectedSuccess, success);
        Assert.Equal(expectedValue, value);
    }

    [Fact]
    public void RedactSensitiveOutputValue_WhenOutputContainsSensitiveEnvVar_RedactsValue()
    {
        var output = "(char *) $21 = 0x0000ffffefcbbb24 \"DD_API_KEY=secret123\"";
        var redacted = InvokePrivateStaticString("RedactSensitiveOutputValue", output);
        Assert.Contains("DD_API_KEY=<redacted>", redacted);
    }

    [Fact]
    public void RedactSensitiveOutputValue_WhenNoEnvVarLikePattern_ReturnsOriginal()
    {
        var output = "(char *) $1 = 0x0000ffffefcbbb24 \"just-a-string\"";
        var redacted = InvokePrivateStaticString("RedactSensitiveOutputValue", output);
        Assert.Equal(output, redacted);
    }
}
