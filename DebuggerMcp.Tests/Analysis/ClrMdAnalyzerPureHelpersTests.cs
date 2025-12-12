using DebuggerMcp.Analysis;

namespace DebuggerMcp.Tests.Analysis;

/// <summary>
/// Unit tests for pure helper methods in <see cref="ClrMdAnalyzer"/>.
/// </summary>
public class ClrMdAnalyzerPureHelpersTests
{
    [Theory]
    [InlineData("List<T>", "List`1")]
    [InlineData("Dictionary<string, int>", "Dictionary`2")]
    [InlineData("Dictionary<string, List<int>>", "Dictionary`2")]
    [InlineData("MyNamespace.MyType", "MyNamespace.MyType")]
    public void NormalizeGenericTypeName_HandlesCSharpSyntax(string input, string expected)
    {
        Assert.Equal(expected, ClrMdAnalyzer.NormalizeGenericTypeName(input));
    }

    [Theory]
    [InlineData("T>", 1)]
    [InlineData("string, int>", 2)]
    [InlineData("string, List<int>>", 2)]
    [InlineData("Dictionary<string, int>, int>", 2)]
    [InlineData("Dictionary<string, List<int>>, Tuple<int, string>>", 2)]
    public void CountGenericParameters_HandlesNestedGenerics(string paramSection, int expected)
    {
        Assert.Equal(expected, ClrMdAnalyzer.CountGenericParameters(paramSection));
    }

    [Theory]
    [InlineData("System.Collections.Generic.List`1", "System.Collections.Generic.List`1", "List`1", true, true)]
    [InlineData("System.Collections.Generic.List`1", "List<T>", "List`1", true, true)]
    [InlineData("My.Namespace.MyType", "MyType", "MyType", false, true)]
    [InlineData("My.Namespace.Outer+Inner", "Inner", "Inner", false, true)]
    [InlineData("My.Namespace.Outer+Inner`1", "Inner<T>", "Inner`1", true, true)]
    [InlineData("System.String", "Int32", "Int32", false, false)]
    public void MatchesTypeName_CoversGenericAndUnqualifiedMatching(
        string clrTypeName,
        string searchName,
        string normalizedName,
        bool isGenericSearch,
        bool expected)
    {
        Assert.Equal(expected, ClrMdAnalyzer.MatchesTypeName(clrTypeName, searchName, normalizedName, isGenericSearch));
    }

    [Theory]
    [InlineData("System.Collections.Generic.List`1[System.String]", "System.Collections.Generic.List", "List<string>", 90)]
    [InlineData("System.Collections.Generic.Dictionary`2[System.String,System.Int32]", "Dictionary", "Dictionary<string,int>", 90)]
    [InlineData("System.Collections.Generic.Dictionary`2[System.String,System.Int32]", "System.Collections.Generic.Dictionary", "Dictionary<string,int>", 100)]
    [InlineData("My.Namespace.Outer+Inner`1[System.Int32]", "Inner", "Inner<int>", 85)]
    [InlineData("SomethingElse`1[System.Int32]", "List", "List<int>", 0)]
    public void ScoreGenericTypeMatch_ScoresAsExpected(string clrTypeName, string baseTypeName, string fullSearchName, int expectedMinimum)
    {
        var score = ClrMdAnalyzer.ScoreGenericTypeMatch(clrTypeName, baseTypeName, fullSearchName);
        Assert.True(score >= expectedMinimum, $"Expected >= {expectedMinimum}, got {score}");
    }

    [Theory]
    [InlineData(0x1000000, "RanToCompletion")]
    [InlineData(0x200000, "Faulted")]
    [InlineData(0x400000, "Canceled")]
    [InlineData(0x0, "Pending")]
    public void GetTaskStatus_ReturnsExpectedStatus(int stateFlags, string expected)
    {
        Assert.Equal(expected, ClrMdAnalyzer.GetTaskStatus(stateFlags));
    }

    [Theory]
    [InlineData("", "Use string.Empty instead of \"\"")]
    [InlineData("true", "Use bool.TrueString")]
    [InlineData("false", "Use bool.FalseString")]
    [InlineData("null", "Consider using a constant")]
    [InlineData("https://example.test/", "Consider caching URL prefixes")]
    public void GetStringSuggestion_ReturnsExpectedSuggestions(string input, string expected)
    {
        Assert.Equal(expected, ClrMdAnalyzer.GetStringSuggestion(input));
    }

    [Fact]
    public void GetStringSuggestion_ShortStrings_RecommendInterning()
    {
        Assert.Equal("Consider string.Intern() for frequently used short strings", ClrMdAnalyzer.GetStringSuggestion("abc"));
    }

    [Fact]
    public void GetStringSuggestion_LongStrings_RecommendCaching()
    {
        Assert.Equal("Consider caching or using StringPool", ClrMdAnalyzer.GetStringSuggestion(new string('a', 40)));
    }

    [Fact]
    public void EscapeControlCharacters_EscapesKnownAndUnknownControlChars()
    {
        var value = "a\r\nb\t\0c" + ((char)0x1) + "d";
        var escaped = ClrMdAnalyzer.EscapeControlCharacters(value);

        Assert.Contains("\\r", escaped, StringComparison.Ordinal);
        Assert.Contains("\\n", escaped, StringComparison.Ordinal);
        Assert.Contains("\\t", escaped, StringComparison.Ordinal);
        Assert.Contains("\\0", escaped, StringComparison.Ordinal);
        Assert.Contains("\\u0001", escaped, StringComparison.Ordinal);
    }
}
