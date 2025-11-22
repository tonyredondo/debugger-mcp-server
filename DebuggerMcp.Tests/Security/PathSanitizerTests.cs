using DebuggerMcp.Security;
using Xunit;

namespace DebuggerMcp.Tests.Security;

public class PathSanitizerTests
{
    [Theory]
    [InlineData("user123")]
    [InlineData("user-123")]
    [InlineData("user_123")]
    [InlineData("user@example.com")]
    [InlineData("abc123-def456-ghi789")]
    public void SanitizeIdentifier_ValidIdentifiers_ReturnsInput(string identifier)
    {
        var result = PathSanitizer.SanitizeIdentifier(identifier, "test");
        Assert.Equal(identifier, result);
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\..\\windows\\system32")]
    [InlineData("user/../../admin")]
    [InlineData("user\\..\\admin")]
    public void SanitizeIdentifier_PathTraversalAttempt_ThrowsArgumentException(string identifier)
    {
        var ex = Assert.Throws<ArgumentException>(() => 
            PathSanitizer.SanitizeIdentifier(identifier, "userId"));
        Assert.Contains("invalid characters", ex.Message.ToLower());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizeIdentifier_NullOrEmpty_ThrowsArgumentException(string? identifier)
    {
        var ex = Assert.Throws<ArgumentException>(() => 
            PathSanitizer.SanitizeIdentifier(identifier!, "userId"));
        Assert.Contains("cannot be null or empty", ex.Message.ToLower());
    }

    [Theory]
    [InlineData("user<script>")]
    [InlineData("user|name")]
    [InlineData("user*name")]
    [InlineData("user?name")]
    [InlineData("user\"name")]
    public void SanitizeIdentifier_InvalidCharacters_ThrowsArgumentException(string identifier)
    {
        var ex = Assert.Throws<ArgumentException>(() => 
            PathSanitizer.SanitizeIdentifier(identifier, "userId"));
        Assert.Contains("invalid characters", ex.Message.ToLower());
    }

    [Fact]
    public void SanitizeIdentifier_TooLongIdentifier_ThrowsArgumentException()
    {
        var longIdentifier = new string('a', 300);
        var ex = Assert.Throws<ArgumentException>(() => 
            PathSanitizer.SanitizeIdentifier(longIdentifier, "userId"));
        Assert.Contains("exceeds maximum length", ex.Message.ToLower());
    }

    [Theory]
    [InlineData("  user123  ")]
    public void SanitizeIdentifier_WhitespacePadded_TrimAndValidates(string identifier)
    {
        var result = PathSanitizer.SanitizeIdentifier(identifier, "test");
        Assert.Equal("user123", result);
    }

    [Theory]
    [InlineData("user123", true)]
    [InlineData("../admin", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidIdentifier_ReturnsExpected(string? identifier, bool expected)
    {
        var result = PathSanitizer.IsValidIdentifier(identifier!);
        Assert.Equal(expected, result);
    }
}

