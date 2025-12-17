using System.Reflection;
using Xunit;

namespace DebuggerMcp.Cli.Tests;

public class ProgramErrorResultDetectionTests
{
    private static bool InvokeIsErrorResult(string result)
    {
        var method = typeof(DebuggerMcp.Cli.Program).GetMethod(
            "IsErrorResult",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object?[] { result })!;
    }

    [Fact]
    public void IsErrorResult_WithErrorNull_ReturnsFalse()
    {
        // Arrange
        var json = """{"success":true,"error":null}""";

        // Act
        var isError = InvokeIsErrorResult(json);

        // Assert
        Assert.False(isError);
    }

    [Fact]
    public void IsErrorResult_WithErrorString_ReturnsTrue()
    {
        // Arrange
        var json = """{"error":"boom"}""";

        // Act
        var isError = InvokeIsErrorResult(json);

        // Assert
        Assert.True(isError);
    }

    [Fact]
    public void IsErrorResult_WithJsonArray_ReturnsFalse()
    {
        // Arrange
        var json = """[{"ok":true}]""";

        // Act
        var isError = InvokeIsErrorResult(json);

        // Assert
        Assert.False(isError);
    }
}

