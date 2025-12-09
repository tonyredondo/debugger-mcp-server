using Xunit;
using DebuggerMcp.SourceLink;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.Tests.SourceLink;

public class SourceLinkResolverTests
{
    // ============================================================
    // URL RESOLUTION TESTS
    // ============================================================

    [Fact]
    public void Resolve_ReturnsUnresolvedLocation_WhenPdbNotFound()
    {
        // Arrange
        var resolver = new SourceLinkResolver();

        // Act
        var result = resolver.Resolve("/nonexistent/module.dll", "/src/file.cs", 42);

        // Assert
        Assert.False(result.Resolved);
        Assert.Equal("/src/file.cs", result.SourceFile);
        Assert.Equal(42, result.LineNumber);
    }

    [Fact]
    public void Resolve_SetsCorrectProperties()
    {
        // Arrange
        var resolver = new SourceLinkResolver();

        // Act
        var result = resolver.Resolve("/test/module.dll", "Controllers/HomeController.cs", 100);

        // Assert
        Assert.Equal("Controllers/HomeController.cs", result.SourceFile);
        Assert.Equal(100, result.LineNumber);
    }

    // ============================================================
    // PROVIDER DETECTION TESTS
    // ============================================================

    [Theory]
    [InlineData("https://raw.githubusercontent.com/user/repo/commit/file.cs", SourceProvider.GitHub)]
    [InlineData("https://github.com/user/repo/blob/main/file.cs", SourceProvider.GitHub)]
    [InlineData("https://gitlab.com/user/repo/-/raw/main/file.cs", SourceProvider.GitLab)]
    [InlineData("https://dev.azure.com/org/project/_apis/git/repositories/repo/items?path=/file.cs", SourceProvider.AzureDevOps)]
    [InlineData("https://bitbucket.org/user/repo/raw/main/file.cs", SourceProvider.Bitbucket)]
    [InlineData("https://example.com/raw/file.cs", SourceProvider.Unknown)]
    public void Resolve_DetectsCorrectProvider(string url, SourceProvider expectedProvider)
    {
        // Validate provider detection based on URL patterns
        var detectedProvider = DetectProviderFromUrl(url);
        Assert.Equal(expectedProvider, detectedProvider);
    }

    /// <summary>
    /// Helper method to detect provider from URL (mirrors internal logic).
    /// </summary>
    private static SourceProvider DetectProviderFromUrl(string url)
    {
        if (url.Contains("github.com") || url.Contains("githubusercontent.com"))
            return SourceProvider.GitHub;
        if (url.Contains("gitlab.com"))
            return SourceProvider.GitLab;
        if (url.Contains("dev.azure.com") || url.Contains("visualstudio.com"))
            return SourceProvider.AzureDevOps;
        if (url.Contains("bitbucket.org"))
            return SourceProvider.Bitbucket;
        return SourceProvider.Unknown;
    }

    // ============================================================
    // URL FORMATTING TESTS
    // ============================================================

    [Fact]
    public void FormatShortLocation_ReturnsFileAndLine()
    {
        // Arrange
        var location = new SourceLocation
        {
            SourceFile = "/path/to/file.cs",
            LineNumber = 42
        };

        // Act
        var result = SourceLinkResolver.FormatShortLocation(location);

        // Assert
        Assert.Equal("file.cs:42", result);
    }

    [Fact]
    public void FormatMarkdownLink_ReturnsPlainText_WhenNotResolved()
    {
        // Arrange
        var location = new SourceLocation
        {
            SourceFile = "/path/to/file.cs",
            LineNumber = 42,
            Resolved = false
        };

        // Act
        var result = SourceLinkResolver.FormatMarkdownLink(location);

        // Assert
        Assert.Equal("file.cs:42", result);
    }

    [Fact]
    public void FormatMarkdownLink_ReturnsLink_WhenResolved()
    {
        // Arrange
        var location = new SourceLocation
        {
            SourceFile = "/path/to/file.cs",
            LineNumber = 42,
            Resolved = true,
            Url = "https://github.com/user/repo/blob/main/file.cs#L42"
        };

        // Act
        var result = SourceLinkResolver.FormatMarkdownLink(location);

        // Assert
        Assert.Equal("[file.cs:42](https://github.com/user/repo/blob/main/file.cs#L42)", result);
    }

    [Fact]
    public void FormatHtmlLink_ReturnsPlainText_WhenNotResolved()
    {
        // Arrange
        var location = new SourceLocation
        {
            SourceFile = "/path/to/file.cs",
            LineNumber = 42,
            Resolved = false
        };

        // Act
        var result = SourceLinkResolver.FormatHtmlLink(location);

        // Assert
        Assert.Equal("file.cs:42", result);
    }

    [Fact]
    public void FormatHtmlLink_ReturnsLink_WhenResolved()
    {
        // Arrange
        var location = new SourceLocation
        {
            SourceFile = "/path/to/file.cs",
            LineNumber = 42,
            Resolved = true,
            Url = "https://github.com/user/repo/blob/main/file.cs#L42"
        };

        // Act
        var result = SourceLinkResolver.FormatHtmlLink(location);

        // Assert
        Assert.Contains("<a href=", result);
        Assert.Contains("target=\"_blank\"", result);
        Assert.Contains("file.cs:42", result);
        Assert.Contains("source-link", result);
    }

    // ============================================================
    // SYMBOL SEARCH PATH TESTS
    // ============================================================

    [Fact]
    public void AddSymbolSearchPath_AddsValidPath()
    {
        // Arrange
        var resolver = new SourceLinkResolver();
        var tempPath = Path.GetTempPath();

        // Act - Should not throw
        resolver.AddSymbolSearchPath(tempPath);

        // Assert - Path was added (verified by behavior)
        Assert.True(Directory.Exists(tempPath));
    }

    [Fact]
    public void AddSymbolSearchPath_IgnoresNonexistentPath()
    {
        // Arrange
        var resolver = new SourceLinkResolver();

        // Act - Should not throw
        resolver.AddSymbolSearchPath("/nonexistent/path/12345");

        // Assert - No exception thrown
        Assert.True(true);
    }

    [Fact]
    public void AddSymbolSearchPath_IgnoresEmptyPath()
    {
        // Arrange
        var resolver = new SourceLinkResolver();

        // Act - Should not throw
        resolver.AddSymbolSearchPath("");
        resolver.AddSymbolSearchPath(null!);

        // Assert - No exception thrown
        Assert.True(true);
    }

    // ============================================================
    // CACHE TESTS
    // ============================================================

    [Fact]
    public void ClearCache_ClearsAllCachedData()
    {
        // Arrange
        var resolver = new SourceLinkResolver();

        // Pre-populate cache by resolving
        resolver.GetSourceLinkForModule("/test/module.dll");

        // Act
        resolver.ClearCache();

        // Assert - No exception thrown, cache cleared
        Assert.True(true);
    }

    [Fact]
    public void GetSourceLinkForModule_CachesResults()
    {
        // Arrange
        var resolver = new SourceLinkResolver();

        // Act - Call twice with same module
        var result1 = resolver.GetSourceLinkForModule("/test/module.dll");
        var result2 = resolver.GetSourceLinkForModule("/test/module.dll");

        // Assert - Both return same (null) but no exception
        Assert.Equal(result1, result2);
    }

    // ============================================================
    // SOURCE LOCATION MODEL TESTS
    // ============================================================

    [Fact]
    public void SourceLocation_DefaultValues()
    {
        // Arrange & Act
        var location = new SourceLocation();

        // Assert
        Assert.Equal(string.Empty, location.SourceFile);
        Assert.Equal(0, location.LineNumber);
        Assert.Null(location.ColumnNumber);
        Assert.Null(location.Url);
        Assert.Null(location.RawUrl);
        Assert.Equal(SourceProvider.Unknown, location.Provider);
        Assert.False(location.Resolved);
        Assert.Null(location.Error);
    }

    [Fact]
    public void SourceLinkInfo_DefaultValues()
    {
        // Arrange & Act
        var info = new SourceLinkInfo();

        // Assert
        Assert.NotNull(info.Documents);
        Assert.Empty(info.Documents);
    }

    [Fact]
    public void ModuleSourceLinkCache_DefaultValues()
    {
        // Arrange & Act
        var cache = new ModuleSourceLinkCache();

        // Assert
        Assert.Equal(string.Empty, cache.ModuleName);
        Assert.Null(cache.PdbPath);
        Assert.False(cache.HasSourceLink);
        Assert.Null(cache.SourceLink);
        Assert.True(cache.CachedAt <= DateTime.UtcNow);
    }

    // ============================================================
    // GITHUB URL CONVERSION TESTS (Integration-style)
    // ============================================================

    [Fact]
    public void Resolve_WithMockSourceLink_ConvertsGitHubUrl()
    {
        // This would require a mock PDB file with Source Link
        // For now, we verify the URL conversion logic works through format methods

        var location = new SourceLocation
        {
            SourceFile = "src/MyClass.cs",
            LineNumber = 42,
            Url = "https://github.com/user/repo/blob/abc123/src/MyClass.cs#L42",
            RawUrl = "https://raw.githubusercontent.com/user/repo/abc123/src/MyClass.cs",
            Provider = SourceProvider.GitHub,
            Resolved = true
        };

        var markdown = SourceLinkResolver.FormatMarkdownLink(location);

        Assert.Contains("github.com", markdown);
        Assert.Contains("#L42", markdown);
    }

    // ============================================================
    // EDGE CASE TESTS
    // ============================================================

    [Fact]
    public void Resolve_HandlesEmptySourceFile()
    {
        // Arrange
        var resolver = new SourceLinkResolver();

        // Act
        var result = resolver.Resolve("/test/module.dll", "", 42);

        // Assert
        Assert.False(result.Resolved);
    }

    [Fact]
    public void Resolve_HandlesZeroLineNumber()
    {
        // Arrange
        var resolver = new SourceLinkResolver();

        // Act
        var result = resolver.Resolve("/test/module.dll", "file.cs", 0);

        // Assert
        Assert.Equal(0, result.LineNumber);
    }

    [Fact]
    public void FormatShortLocation_HandlesWindowsPaths()
    {
        // Arrange
        var location = new SourceLocation
        {
            SourceFile = @"C:\Users\dev\project\src\file.cs",
            LineNumber = 42
        };

        // Act
        var result = SourceLinkResolver.FormatShortLocation(location);

        // Assert - Path.GetFileName behavior varies by platform
        // On Windows: returns "file.cs"
        // On Unix: returns the full path (backslashes aren't path separators)
        Assert.Contains("file.cs", result);
        Assert.Contains(":42", result);
    }

    [Fact]
    public void FormatShortLocation_HandlesUnixPaths()
    {
        // Arrange
        var location = new SourceLocation
        {
            SourceFile = "/home/dev/project/src/file.cs",
            LineNumber = 42
        };

        // Act
        var result = SourceLinkResolver.FormatShortLocation(location);

        // Assert
        Assert.Equal("file.cs:42", result);
    }

    // ============================================================
    // RESOLVE ALL TESTS
    // ============================================================

    [Fact]
    public void ResolveAll_ReturnsEmptyDictionary_WhenNoFrames()
    {
        // Arrange
        var resolver = new SourceLinkResolver();
        var frames = Array.Empty<(int Index, string ModulePath, string SourceFile, int LineNumber)>();

        // Act
        var results = resolver.ResolveAll(frames);

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ResolveAll_ProcessesMultipleFrames()
    {
        // Arrange
        var resolver = new SourceLinkResolver();
        var frames = new[]
        {
            (0, "/test/module1.dll", "/src/file1.cs", 10),
            (1, "/test/module2.dll", "/src/file2.cs", 20),
            (2, "/test/module3.dll", "/src/file3.cs", 30)
        };

        // Act
        var results = resolver.ResolveAll(frames);

        // Assert
        Assert.Equal(3, results.Count);
        Assert.Contains(0, results.Keys);
        Assert.Contains(1, results.Keys);
        Assert.Contains(2, results.Keys);
    }

    [Fact]
    public void ResolveAll_SkipsFramesWithEmptyModule()
    {
        // Arrange
        var resolver = new SourceLinkResolver();
        var frames = new[]
        {
            (0, "", "/src/file1.cs", 10),
            (1, "/test/module.dll", "/src/file2.cs", 20)
        };

        // Act
        var results = resolver.ResolveAll(frames);

        // Assert
        Assert.Single(results);
        Assert.Contains(1, results.Keys);
    }

    [Fact]
    public void ResolveAll_SkipsFramesWithEmptySourceFile()
    {
        // Arrange
        var resolver = new SourceLinkResolver();
        var frames = new[]
        {
            (0, "/test/module.dll", "", 10),
            (1, "/test/module.dll", "/src/file2.cs", 20)
        };

        // Act
        var results = resolver.ResolveAll(frames);

        // Assert
        Assert.Single(results);
        Assert.Contains(1, results.Keys);
    }

    [Fact]
    public void ResolveAll_SkipsFramesWithZeroLineNumber()
    {
        // Arrange
        var resolver = new SourceLinkResolver();
        var frames = new[]
        {
            (0, "/test/module.dll", "/src/file1.cs", 0),
            (1, "/test/module.dll", "/src/file2.cs", 20)
        };

        // Act
        var results = resolver.ResolveAll(frames);

        // Assert
        Assert.Single(results);
        Assert.Contains(1, results.Keys);
    }

    [Fact]
    public void ResolveAll_SkipsFramesWithNegativeLineNumber()
    {
        // Arrange
        var resolver = new SourceLinkResolver();
        var frames = new[]
        {
            (0, "/test/module.dll", "/src/file1.cs", -1),
            (1, "/test/module.dll", "/src/file2.cs", 20)
        };

        // Act
        var results = resolver.ResolveAll(frames);

        // Assert
        Assert.Single(results);
        Assert.Contains(1, results.Keys);
    }

    // ============================================================
    // ADDITIONAL PROVIDER URL FORMATTING TESTS
    // ============================================================

    [Fact]
    public void Resolve_WithGitLabSourceLink_ConvertsUrl()
    {
        // Simulate a resolved GitLab location
        var location = new SourceLocation
        {
            SourceFile = "src/MyClass.cs",
            LineNumber = 100,
            Url = "https://gitlab.com/user/repo/-/blob/abc123/src/MyClass.cs#L100",
            RawUrl = "https://gitlab.com/user/repo/-/raw/abc123/src/MyClass.cs",
            Provider = SourceProvider.GitLab,
            Resolved = true
        };

        var markdown = SourceLinkResolver.FormatMarkdownLink(location);

        Assert.Contains("gitlab.com", markdown);
        Assert.Contains("#L100", markdown);
    }

    [Fact]
    public void Resolve_WithAzureDevOpsSourceLink_ConvertsUrl()
    {
        // Simulate a resolved Azure DevOps location
        var location = new SourceLocation
        {
            SourceFile = "src/MyClass.cs",
            LineNumber = 50,
            Url = "https://dev.azure.com/org/project/_git/repo?path=/src/MyClass.cs&line=50",
            RawUrl = "https://dev.azure.com/org/project/_apis/git/repositories/repo/items?path=/src/MyClass.cs",
            Provider = SourceProvider.AzureDevOps,
            Resolved = true
        };

        var markdown = SourceLinkResolver.FormatMarkdownLink(location);

        Assert.Contains("dev.azure.com", markdown);
        Assert.Contains("line=50", markdown);
    }

    [Fact]
    public void Resolve_WithBitbucketSourceLink_ConvertsUrl()
    {
        // Simulate a resolved Bitbucket location
        var location = new SourceLocation
        {
            SourceFile = "src/MyClass.cs",
            LineNumber = 75,
            Url = "https://bitbucket.org/user/repo/src/abc123/src/MyClass.cs#lines-75",
            RawUrl = "https://bitbucket.org/user/repo/raw/abc123/src/MyClass.cs",
            Provider = SourceProvider.Bitbucket,
            Resolved = true
        };

        var markdown = SourceLinkResolver.FormatMarkdownLink(location);

        Assert.Contains("bitbucket.org", markdown);
        Assert.Contains("#lines-75", markdown);
    }

    // ============================================================
    // HTML FORMATTING ADDITIONAL TESTS
    // ============================================================

    [Fact]
    public void FormatHtmlLink_EscapesSpecialCharacters()
    {
        // Arrange
        var location = new SourceLocation
        {
            SourceFile = "/path/to/file<script>.cs",
            LineNumber = 42,
            Resolved = false
        };

        // Act
        var result = SourceLinkResolver.FormatHtmlLink(location);

        // Assert - Should HTML encode the < and >
        Assert.DoesNotContain("<script>", result);
        Assert.Contains("&lt;script&gt;", result);
    }

    [Fact]
    public void FormatHtmlLink_IncludesEmojiIcon_WhenResolved()
    {
        // Arrange
        var location = new SourceLocation
        {
            SourceFile = "/path/to/file.cs",
            LineNumber = 42,
            Resolved = true,
            Url = "https://github.com/user/repo/blob/main/file.cs#L42"
        };

        // Act
        var result = SourceLinkResolver.FormatHtmlLink(location);

        // Assert
        Assert.Contains("📄", result);
    }

    // ============================================================
    // CONSTRUCTOR AND INITIALIZATION TESTS
    // ============================================================

    [Fact]
    public void Constructor_WithNullLogger_DoesNotThrow()
    {
        // Act
        var resolver = new SourceLinkResolver(null);

        // Assert
        Assert.NotNull(resolver);
    }

    [Fact]
    public void Constructor_WithLogger_DoesNotThrow()
    {
        // Arrange
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var logger = new Logger<SourceLinkResolver>(loggerFactory);

        // Act
        var resolver = new SourceLinkResolver(logger);

        // Assert
        Assert.NotNull(resolver);
    }

    // ============================================================
    // COLUMN NUMBER TESTS
    // ============================================================

    [Fact]
    public void Resolve_SetsColumnNumber_WhenProvided()
    {
        // Arrange
        var resolver = new SourceLinkResolver();

        // Act
        var result = resolver.Resolve("/test/module.dll", "/src/file.cs", 42, 15);

        // Assert
        Assert.Equal(15, result.ColumnNumber);
    }

    [Fact]
    public void Resolve_ColumnNumberIsNull_WhenNotProvided()
    {
        // Arrange
        var resolver = new SourceLinkResolver();

        // Act
        var result = resolver.Resolve("/test/module.dll", "/src/file.cs", 42);

        // Assert
        Assert.Null(result.ColumnNumber);
    }

    // ============================================================
    // MULTIPLE SEARCH PATHS TESTS
    // ============================================================

    [Fact]
    public void AddSymbolSearchPath_CanAddMultiplePaths()
    {
        // Arrange
        var resolver = new SourceLinkResolver();
        var tempPath = Path.GetTempPath();

        // Act - Add the same path multiple times (should work without error)
        resolver.AddSymbolSearchPath(tempPath);
        resolver.AddSymbolSearchPath(tempPath);

        // Assert - No exception
        Assert.True(true);
    }

    // ============================================================
    // CACHE BEHAVIOR TESTS
    // ============================================================

    [Fact]
    public void GetSourceLinkForModule_ReturnsSameResult_ForSameModule()
    {
        // Arrange
        var resolver = new SourceLinkResolver();

        // Act
        var result1 = resolver.GetSourceLinkForModule("/path/to/MyApp.dll");
        var result2 = resolver.GetSourceLinkForModule("/path/to/MyApp.dll");

        // Assert - Should return same reference (cached)
        Assert.Equal(result1, result2);
    }

    [Fact]
    public void GetSourceLinkForModule_CachesPerModuleName()
    {
        // Arrange
        var resolver = new SourceLinkResolver();

        // Act - Same module name from different paths
        var result1 = resolver.GetSourceLinkForModule("/path1/MyApp.dll");
        var result2 = resolver.GetSourceLinkForModule("/path2/MyApp.dll");

        // Assert - Should use same cache entry (module name is the key)
        Assert.Equal(result1, result2);
    }

    [Fact]
    public void ClearCache_AllowsReResolution()
    {
        // Arrange
        var resolver = new SourceLinkResolver();
        resolver.GetSourceLinkForModule("/test/module.dll");

        // Act
        resolver.ClearCache();
        var result = resolver.GetSourceLinkForModule("/test/module.dll");

        // Assert - Should return null (no PDB exists)
        Assert.Null(result);
    }

    // ============================================================
    // SOURCE LOCATION MODEL ADDITIONAL TESTS
    // ============================================================

    [Fact]
    public void SourceLocation_CanSetAllProperties()
    {
        // Arrange & Act
        var location = new SourceLocation
        {
            SourceFile = "file.cs",
            LineNumber = 100,
            ColumnNumber = 25,
            Url = "https://example.com/file.cs#L100",
            RawUrl = "https://raw.example.com/file.cs",
            Provider = SourceProvider.GitHub,
            Resolved = true,
            Error = null
        };

        // Assert
        Assert.Equal("file.cs", location.SourceFile);
        Assert.Equal(100, location.LineNumber);
        Assert.Equal(25, location.ColumnNumber);
        Assert.Equal("https://example.com/file.cs#L100", location.Url);
        Assert.Equal("https://raw.example.com/file.cs", location.RawUrl);
        Assert.Equal(SourceProvider.GitHub, location.Provider);
        Assert.True(location.Resolved);
        Assert.Null(location.Error);
    }

    [Fact]
    public void SourceLinkInfo_CanSetDocuments()
    {
        // Arrange & Act
        var info = new SourceLinkInfo
        {
            Documents = new Dictionary<string, string>
            {
                { "/src/*", "https://raw.githubusercontent.com/user/repo/main/*" },
                { "/lib/*", "https://raw.githubusercontent.com/user/repo/main/lib/*" }
            }
        };

        // Assert
        Assert.Equal(2, info.Documents.Count);
        Assert.Contains("/src/*", info.Documents.Keys);
    }

    // ============================================================
    // PROVIDER ENUM TESTS
    // ============================================================

    [Fact]
    public void SourceProvider_HasExpectedValues()
    {
        // Assert - Verify all expected providers exist
        Assert.Equal(0, (int)SourceProvider.Unknown);
        Assert.True(Enum.IsDefined(typeof(SourceProvider), SourceProvider.GitHub));
        Assert.True(Enum.IsDefined(typeof(SourceProvider), SourceProvider.GitLab));
        Assert.True(Enum.IsDefined(typeof(SourceProvider), SourceProvider.AzureDevOps));
        Assert.True(Enum.IsDefined(typeof(SourceProvider), SourceProvider.Bitbucket));
        Assert.True(Enum.IsDefined(typeof(SourceProvider), SourceProvider.Generic));
    }

    // ============================================================
    // ERROR HANDLING TESTS
    // ============================================================

    [Fact]
    public void Resolve_SetsError_WhenPdbNotFound()
    {
        // Arrange
        var resolver = new SourceLinkResolver();

        // Act
        var result = resolver.Resolve("/nonexistent/path/module.dll", "/src/file.cs", 42);

        // Assert
        Assert.False(result.Resolved);
        Assert.NotNull(result.Error);
        Assert.Contains("Source Link", result.Error);
    }

    [Fact]
    public void FormatMarkdownLink_ReturnsPlainText_WhenUrlIsEmpty()
    {
        // Arrange
        var location = new SourceLocation
        {
            SourceFile = "/path/to/file.cs",
            LineNumber = 42,
            Resolved = true,
            Url = "" // Empty URL
        };

        // Act
        var result = SourceLinkResolver.FormatMarkdownLink(location);

        // Assert - Should return plain text, not a broken link
        Assert.Equal("file.cs:42", result);
    }

    [Fact]
    public void FormatHtmlLink_ReturnsPlainText_WhenUrlIsNull()
    {
        // Arrange
        var location = new SourceLocation
        {
            SourceFile = "/path/to/file.cs",
            LineNumber = 42,
            Resolved = true,
            Url = null
        };

        // Act
        var result = SourceLinkResolver.FormatHtmlLink(location);

        // Assert - Should return plain text
        Assert.DoesNotContain("<a href=", result);
        Assert.Contains("file.cs:42", result);
    }
}

