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
}
