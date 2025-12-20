using System;
using System.Text.Json;
using DebuggerMcp.Reporting;
using Xunit;

namespace DebuggerMcp.Tests.Reporting;

public class ReportSectionApiTests
{
    [Fact]
    public void BuildIndex_IncludesTocAndHowToExpand()
    {
        var report = """
        {
          "metadata": { "dumpId":"d1", "debuggerType":"LLDB" },
          "analysis": {
            "summary": { "crashType":"Managed", "threadCount": 2 },
            "exception": { "type":"System.Exception" },
            "threads": { "all": [ { "threadId":"1" }, { "threadId":"2" } ] },
            "assemblies": { "items": [ { "name":"a" } ] }
          }
        }
        """;

        var json = ReportSectionApi.BuildIndex(report);
        Assert.Contains("\"toc\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("analysis.threads.all", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"howToExpand\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("report_get", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MCP: report(action=", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSection_WhenObjectPath_ReturnsValue()
    {
        var report = """
        {
          "metadata": { "dumpId":"d1" },
          "analysis": { "exception": { "type":"System.Exception", "message":"boom" } }
        }
        """;

        var json = ReportSectionApi.GetSection(report, "analysis.exception", limit: null, cursor: null, maxChars: 50_000);
        Assert.Contains("\"path\": \"analysis.exception\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"type\": \"System.Exception\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSection_WhenArrayPath_PagesAndReturnsCursor()
    {
        var report = """
        {
          "metadata": { "dumpId":"d1" },
          "analysis": { "threads": { "all": [ { "id":1 }, { "id":2 }, { "id":3 } ] } }
        }
        """;

        var first = ReportSectionApi.GetSection(report, "analysis.threads.all", limit: 2, cursor: null, maxChars: 50_000);
        Assert.Contains("\"limit\": 2", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"nextCursor\"", first, StringComparison.OrdinalIgnoreCase);

        var cursorStart = first.IndexOf("\"nextCursor\"", StringComparison.OrdinalIgnoreCase);
        Assert.True(cursorStart >= 0);
    }

    [Fact]
    public void GetSection_WhenInvalidPath_ReturnsInvalidPathError()
    {
        var report = """
        { "metadata": { "dumpId":"d1" }, "analysis": { } }
        """;

        var json = ReportSectionApi.GetSection(report, "analysis.nope", limit: null, cursor: null, maxChars: 50_000);
        Assert.Contains("\"code\": \"invalid_path\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSection_WhenCursorTooLarge_ReturnsInvalidCursorError()
    {
        var report = """
        {
          "metadata": { "dumpId":"d1" },
          "analysis": { "threads": { "all": [ { "id":1 } ] } }
        }
        """;

        var hugeCursor = new string('a', 5000);
        var json = ReportSectionApi.GetSection(report, "analysis.threads.all", limit: 1, cursor: hugeCursor, maxChars: 50_000);
        Assert.Contains("\"code\": \"invalid_cursor\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSection_WhenResponseWouldExceedMaxChars_ReturnsTooLargeError()
    {
        var longText = new string('x', 4000);
        var report = """
        {
          "metadata": { "dumpId":"d1" },
          "analysis": { "exception": { "type":"System.Exception", "message":"boom", "data":"__LONG__" } }
        }
        """;
        report = report.Replace("__LONG__", longText, StringComparison.Ordinal);

        var json = ReportSectionApi.GetSection(report, "analysis.exception", limit: null, cursor: null, maxChars: 1000);
        Assert.Contains("\"code\": \"too_large\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSection_WhenPathIsNotRooted_ReturnsInvalidPathError()
    {
        var report = """
        { "metadata": { "dumpId":"d1" }, "analysis": { "exception": { "type":"X" } } }
        """;

        var json = ReportSectionApi.GetSection(report, "exception", limit: null, cursor: null, maxChars: 50_000);
        Assert.Contains("\"code\": \"invalid_path\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSection_WhenPathIsTooLong_ReturnsInvalidPathError()
    {
        var report = """
        { "metadata": { "dumpId":"d1" }, "analysis": { "exception": { "type":"X" } } }
        """;

        var path = "analysis." + new string('a', 600);
        var json = ReportSectionApi.GetSection(report, path, limit: null, cursor: null, maxChars: 50_000);
        Assert.Contains("\"code\": \"invalid_path\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exceeds maximum length", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSection_WhenCursorPayloadIsInvalid_ReturnsInvalidCursorError()
    {
        var report = """
        { "metadata": { "dumpId":"d1" }, "analysis": { "threads": { "all": [ { "id":1 } ] } } }
        """;

        var json = ReportSectionApi.GetSection(report, "analysis.threads.all", limit: 1, cursor: "not_base64", maxChars: 50_000);
        Assert.Contains("\"code\": \"invalid_cursor\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSection_WhenCursorPathDoesNotMatch_ReturnsInvalidCursorError()
    {
        var report = """
        { "metadata": { "dumpId":"d1" }, "analysis": { "threads": { "all": [ { "id":1 }, { "id":2 } ] } } }
        """;

        // Cursor is intentionally built for a different path.
        var otherCursor = ReportSectionApi.GetSection(report, "analysis.threads.all", limit: 1, cursor: null, maxChars: 50_000);
        var reportWithSecondArray = """
        {
          "metadata": { "dumpId":"d1" },
          "analysis": {
            "threads": { "all": [ { "id":1 }, { "id":2 } ] },
            "assemblies": { "items": [ { "name":"a" } ] }
          }
        }
        """;

        // Use a different *array path* with a cursor from analysis.threads.all.
        var json = ReportSectionApi.GetSection(reportWithSecondArray, "analysis.assemblies.items", limit: 1, cursor: ExtractCursor(otherCursor), maxChars: 50_000);
        Assert.Contains("\"code\": \"invalid_cursor\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not match requested path", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSection_WhenCursorOffsetOutOfRange_ReturnsInvalidCursorError()
    {
        var report = """
        { "metadata": { "dumpId":"d1" }, "analysis": { "threads": { "all": [ { "id":1 } ] } } }
        """;

        // Build a syntactically valid cursor with an out-of-range offset.
        var cursor = EncodeCursor(path: "analysis.threads.all", offset: 999, limit: 1);
        var json = ReportSectionApi.GetSection(report, "analysis.threads.all", limit: 1, cursor: cursor, maxChars: 50_000);
        Assert.Contains("\"code\": \"invalid_cursor\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cursor offset is out of range", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSection_WhenPagingSecondPage_ReturnsRemainingItemsAndNoCursor()
    {
        var report = """
        { "metadata": { "dumpId":"d1" }, "analysis": { "threads": { "all": [ { "id":1 }, { "id":2 }, { "id":3 } ] } } }
        """;

        var first = ReportSectionApi.GetSection(report, "analysis.threads.all", limit: 2, cursor: null, maxChars: 50_000);
        var second = ReportSectionApi.GetSection(report, "analysis.threads.all", limit: null, cursor: ExtractCursor(first), maxChars: 50_000);

        Assert.Contains("\"id\": 3", second, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"nextCursor\"", second, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractCursor(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("page").GetProperty("nextCursor").GetString() ?? string.Empty;
    }

    private static string EncodeCursor(string path, int offset, int limit)
    {
        var json = JsonSerializer.Serialize(new { path, offset, limit }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var base64 = Convert.ToBase64String(bytes);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
