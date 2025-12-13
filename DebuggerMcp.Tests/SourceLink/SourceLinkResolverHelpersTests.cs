using DebuggerMcp.SourceLink;
using Xunit;

namespace DebuggerMcp.Tests.SourceLink;

/// <summary>
/// Tests for Source Link helper logic in <see cref="SourceLinkResolver"/>.
/// </summary>
public class SourceLinkResolverHelpersTests
{
    [Fact]
    public void ResolveRawUrl_GlobPrefixMatch_ReturnsExpandedUrl()
    {
        var resolver = new SourceLinkResolver();
        var sourceLink = new SourceLinkInfo
        {
            Documents =
            {
                ["/src/*"] = "https://raw.githubusercontent.com/user/repo/commit/*"
            }
        };

        var raw = resolver.ResolveRawUrl(sourceLink, "/src/Foo/Bar.cs");

        Assert.Equal("https://raw.githubusercontent.com/user/repo/commit/Foo/Bar.cs", raw);
    }

    [Fact]
    public void ResolveRawUrl_GlobContainsMatch_ReturnsExpandedUrl()
    {
        var resolver = new SourceLinkResolver();
        var sourceLink = new SourceLinkInfo
        {
            Documents =
            {
                ["/src/*"] = "https://raw.githubusercontent.com/user/repo/commit/*"
            }
        };

        var raw = resolver.ResolveRawUrl(sourceLink, "C:/projects/MyApp/src/Foo.cs");

        Assert.Equal("https://raw.githubusercontent.com/user/repo/commit/Foo.cs", raw);
    }

    [Fact]
    public void ResolveRawUrl_ExactMatch_ReturnsTemplateUrl()
    {
        var resolver = new SourceLinkResolver();
        var sourceLink = new SourceLinkInfo
        {
            Documents =
            {
                ["C:/src/file.cs"] = "https://raw.githubusercontent.com/user/repo/commit/src/file.cs"
            }
        };

        var raw = resolver.ResolveRawUrl(sourceLink, "C:\\src\\file.cs");

        Assert.Equal("https://raw.githubusercontent.com/user/repo/commit/src/file.cs", raw);
    }

    [Fact]
    public void ResolveRawUrl_NoMatch_ReturnsNull()
    {
        var resolver = new SourceLinkResolver();
        var sourceLink = new SourceLinkInfo
        {
            Documents =
            {
                ["/src/*"] = "https://raw.githubusercontent.com/user/repo/commit/*"
            }
        };

        var raw = resolver.ResolveRawUrl(sourceLink, "/other/file.cs");

        Assert.Null(raw);
    }

    [Theory]
    [InlineData("https://raw.githubusercontent.com/user/repo/commit/file.cs", SourceProvider.GitHub)]
    [InlineData("https://github.com/user/repo/blob/main/file.cs", SourceProvider.GitHub)]
    [InlineData("https://gitlab.com/user/repo/-/raw/main/file.cs", SourceProvider.GitLab)]
    [InlineData("https://dev.azure.com/org/project/_apis/git/repositories/repo/items?path=/file.cs", SourceProvider.AzureDevOps)]
    [InlineData("https://bitbucket.org/user/repo/raw/main/file.cs", SourceProvider.Bitbucket)]
    [InlineData("https://example.com/raw/file.cs", SourceProvider.Generic)]
    public void DetectProvider_ReturnsExpectedProvider(string url, SourceProvider expected)
    {
        var provider = SourceLinkResolver.DetectProvider(url);

        Assert.Equal(expected, provider);
    }

    [Fact]
    public void ConvertToBrowsableUrl_GitHubRawUrl_ConvertsToBlobWithLine()
    {
        var raw = "https://raw.githubusercontent.com/user/repo/abc123/src/file.cs";

        var url = SourceLinkResolver.ConvertToBrowsableUrl(raw, 42, SourceProvider.GitHub);

        Assert.Equal("https://github.com/user/repo/blob/abc123/src/file.cs#L42", url);
    }

    [Fact]
    public void ConvertToBrowsableUrl_GitHubRawUrl_DotnetDotnetRuntimePaths_AreRewritten()
    {
        var raw = "https://raw.githubusercontent.com/dotnet/dotnet/abc123/src/libraries/System.Private.CoreLib/src/System/Threading/Lock.cs";

        var url = SourceLinkResolver.ConvertToBrowsableUrl(raw, 754, SourceProvider.GitHub);

        Assert.Equal(
            "https://github.com/dotnet/dotnet/blob/abc123/src/runtime/src/libraries/System.Private.CoreLib/src/System/Threading/Lock.cs#L754",
            url);
    }

    [Fact]
    public void ConvertToBrowsableUrl_GitHubBlobUrl_AppendsLine()
    {
        var raw = "https://github.com/user/repo/blob/main/src/file.cs";

        var url = SourceLinkResolver.ConvertToBrowsableUrl(raw, 123, SourceProvider.GitHub);

        Assert.Equal("https://github.com/user/repo/blob/main/src/file.cs#L123", url);
    }

    [Fact]
    public void ConvertToBrowsableUrl_GitHubBlobUrl_DotnetDotnetRuntimePaths_AreRewritten()
    {
        var raw = "https://github.com/dotnet/dotnet/blob/abc123/src/libraries/System.Private.CoreLib/src/System/Threading/Lock.cs";

        var url = SourceLinkResolver.ConvertToBrowsableUrl(raw, 754, SourceProvider.GitHub);

        Assert.Equal(
            "https://github.com/dotnet/dotnet/blob/abc123/src/runtime/src/libraries/System.Private.CoreLib/src/System/Threading/Lock.cs#L754",
            url);
    }

    [Fact]
    public void ConvertToBrowsableUrl_GitLabRawUrl_ConvertsToBlobWithLine()
    {
        var raw = "https://gitlab.com/user/repo/-/raw/main/src/file.cs";

        var url = SourceLinkResolver.ConvertToBrowsableUrl(raw, 7, SourceProvider.GitLab);

        Assert.Equal("https://gitlab.com/user/repo/-/blob/main/src/file.cs#L7", url);
    }

    [Fact]
    public void ConvertToBrowsableUrl_AzureDevOpsItemsApi_ConvertsToGitWithLine()
    {
        var raw = "https://dev.azure.com/org/project/_apis/git/repositories/repo/items?path=%2Fsrc%2Ffile.cs";

        var url = SourceLinkResolver.ConvertToBrowsableUrl(raw, 9, SourceProvider.AzureDevOps);

        Assert.Equal("https://dev.azure.com/org/project/_git/repo?path=/src/file.cs&line=9", url);
    }

    [Fact]
    public void ConvertToBrowsableUrl_BitbucketRawUrl_ConvertsToSrcWithLinesAnchor()
    {
        var raw = "https://bitbucket.org/user/repo/raw/main/src/file.cs";

        var url = SourceLinkResolver.ConvertToBrowsableUrl(raw, 55, SourceProvider.Bitbucket);

        Assert.Equal("https://bitbucket.org/user/repo/src/main/src/file.cs#lines-55", url);
    }

    [Fact]
    public void ConvertToBrowsableUrl_GenericUrlWithoutHash_AppendsLineAnchor()
    {
        var raw = "https://example.com/repo/file.cs";

        var url = SourceLinkResolver.ConvertToBrowsableUrl(raw, 3, SourceProvider.Generic);

        Assert.Equal("https://example.com/repo/file.cs#L3", url);
    }

    [Fact]
    public void ConvertToBrowsableUrl_GenericUrlWithHash_ReturnsUnchanged()
    {
        var raw = "https://example.com/repo/file.cs#L100";

        var url = SourceLinkResolver.ConvertToBrowsableUrl(raw, 3, SourceProvider.Generic);

        Assert.Equal(raw, url);
    }
}
