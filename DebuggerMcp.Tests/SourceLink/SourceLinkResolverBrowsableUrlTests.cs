using DebuggerMcp.SourceLink;
using Xunit;

namespace DebuggerMcp.Tests.SourceLink;

/// <summary>
/// Regression tests for <see cref="SourceLinkResolver.ConvertToBrowsableUrl"/>.
/// </summary>
public class SourceLinkResolverBrowsableUrlTests
{
    [Fact]
    public void ConvertToBrowsableUrl_GitHub_LineZero_DoesNotAppendAnchor()
    {
        // Arrange
        var rawUrl = "https://raw.githubusercontent.com/dotnet/dotnet/b0f34d51fccc69fd334253924abd8d6853fad7aa/src/coreclr/vm/jitinterface.cpp";

        // Act
        var url = SourceLinkResolver.ConvertToBrowsableUrl(rawUrl, lineNumber: 0, SourceProvider.GitHub);

        // Assert
        Assert.Equal(
            "https://github.com/dotnet/dotnet/blob/b0f34d51fccc69fd334253924abd8d6853fad7aa/src/runtime/src/coreclr/vm/jitinterface.cpp",
            url);
    }

    [Fact]
    public void ConvertToBrowsableUrl_GitHub_LinePositive_AppendsAnchor()
    {
        // Arrange
        var rawUrl = "https://raw.githubusercontent.com/dotnet/dotnet/b0f34d51fccc69fd334253924abd8d6853fad7aa/src/coreclr/vm/threads.cpp";

        // Act
        var url = SourceLinkResolver.ConvertToBrowsableUrl(rawUrl, lineNumber: 7058, SourceProvider.GitHub);

        // Assert
        Assert.EndsWith("#L7058", url);
        Assert.Contains("/src/runtime/src/coreclr/vm/threads.cpp", url);
    }
}

