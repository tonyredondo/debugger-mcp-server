using System;
using System.Reflection;
using System.Text.Json;
using DebuggerMcp.Reporting;
using Xunit;

namespace DebuggerMcp.Tests.Reporting;

public class JsonHtmlReportRendererCoverageTests
{
    [Fact]
    public void Render_WhenAllSectionsPresent_EmitsExpectedAnchorsAndDetails()
    {
        var reportJson = JsonMarkdownReportRendererCoverageTests.BuildCanonicalReportJson();

        var options = ReportOptions.FullReport;
        options.Format = ReportFormat.Html;
        options.IncludeRawJsonDetails = true;

        var html = JsonHtmlReportRenderer.Render(reportJson, options);

        Assert.Contains("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("id=\"at-a-glance\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"faulting-thread\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"threads\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"memory-gc\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"security\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"assemblies\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"modules\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"symbols\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"timeline\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"source-context-index\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"signature\"", html, StringComparison.Ordinal);

        Assert.Contains("<summary>Recommendations</summary>", html, StringComparison.Ordinal);
        Assert.Contains("Native missing examples", html, StringComparison.Ordinal);
        Assert.Contains("<pre class=\"code\"><code class=\"language-json\">", html, StringComparison.Ordinal); // raw JSON / code blocks
    }

    [Fact]
    public void Render_DoesNotReferenceHighlightJsCdn()
    {
        var reportJson = JsonMarkdownReportRendererCoverageTests.BuildCanonicalReportJson();

        var options = ReportOptions.FullReport;
        options.Format = ReportFormat.Html;
        options.IncludeRawJsonDetails = true;

        var html = JsonHtmlReportRenderer.Render(reportJson, options);

        Assert.DoesNotContain("cdnjs.cloudflare.com/ajax/libs/highlight.js", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("highlight.min.js", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_WhenAiAnalysisPresent_RendersAiSectionBeforeAtAGlance()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(JsonMarkdownReportRendererCoverageTests.BuildCanonicalReportJson())!.AsObject();
        var analysis = root["analysis"]!.AsObject();
        analysis["aiAnalysis"] = System.Text.Json.Nodes.JsonNode.Parse("""
        {
          "rootCause": "x",
          "confidence": "high",
          "reasoning": "r",
          "recommendations": ["a"],
          "evidence": ["e"]
        }
        """);

        var options = ReportOptions.FullReport;
        options.Format = ReportFormat.Html;
        options.IncludeRawJsonDetails = false;

        var html = JsonHtmlReportRenderer.Render(root.ToJsonString(), options);

        var aiIndex = html.IndexOf("id=\"ai-analysis\"", StringComparison.Ordinal);
        var glanceIndex = html.IndexOf("id=\"at-a-glance\"", StringComparison.Ordinal);
        Assert.True(aiIndex >= 0, "Expected AI section to be present.");
        Assert.True(glanceIndex >= 0, "Expected At a glance section to be present.");
        Assert.True(aiIndex < glanceIndex, "Expected AI section to appear before At a glance.");

        Assert.Contains("#ai-analysis", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WhenRawJsonDetailsDisabled_OmitsRawJsonExplorer()
    {
        var reportJson = JsonMarkdownReportRendererCoverageTests.BuildCanonicalReportJson();

        var options = ReportOptions.SummaryReport;
        options.Format = ReportFormat.Html;

        var html = JsonHtmlReportRenderer.Render(reportJson, options);

        Assert.DoesNotContain("id=\"raw-json\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script id=\"dbg-mcp-report-json-b64\"", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("a.cs", "csharp")]
    [InlineData("a.fs", "fsharp")]
    [InlineData("a.vb", "vbnet")]
    [InlineData("a.cpp", "cpp")]
    [InlineData("a.c", "c")]
    [InlineData("a.h", "cpp")]
    [InlineData("a.rs", "rust")]
    [InlineData("a.go", "go")]
    [InlineData("a.java", "java")]
    [InlineData("a.kt", "kotlin")]
    [InlineData("a.js", "javascript")]
    [InlineData("a.ts", "typescript")]
    [InlineData("a.py", "python")]
    [InlineData("a.rb", "ruby")]
    [InlineData("a.php", "php")]
    [InlineData("a.swift", "swift")]
    [InlineData("a.m", "objectivec")]
    [InlineData("a.sh", "bash")]
    [InlineData("a.ps1", "powershell")]
    [InlineData("a.json", "json")]
    [InlineData("a.yml", "yaml")]
    [InlineData("a.xml", "xml")]
    [InlineData("a.unknown", "")]
    public void GuessFenceLanguage_MapsExtensions(string sourceFile, string expected)
    {
        var actual = InvokePrivateGuessFenceLanguage(typeof(JsonHtmlReportRenderer), sourceFile);
        Assert.Equal(expected, actual);
    }

    private static string InvokePrivateGuessFenceLanguage(Type rendererType, string sourceFile)
    {
        var method = rendererType.GetMethod("GuessFenceLanguage", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { sourceFile }));
        var result = method!.Invoke(null, new object[] { doc.RootElement });
        return result as string ?? string.Empty;
    }

    [Fact]
    public void PrivateHelpers_ElementToText_RendersScalars()
    {
        var elementToText = typeof(JsonHtmlReportRenderer).GetMethod("ElementToText", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(elementToText);

        using var doc = JsonDocument.Parse("{\"s\":\"x\",\"n\":123,\"b\":true,\"null\":null,\"a\":[1,2]}");
        var root = doc.RootElement;

        Assert.Equal("x", elementToText!.Invoke(null, new object[] { root.GetProperty("s") }) as string);
        Assert.Equal("123", elementToText.Invoke(null, new object[] { root.GetProperty("n") }) as string);
        Assert.Equal("True", elementToText.Invoke(null, new object[] { root.GetProperty("b") }) as string);
        Assert.Equal(string.Empty, elementToText.Invoke(null, new object[] { root.GetProperty("null") }) as string);
        Assert.Equal($"1{Environment.NewLine}2", elementToText.Invoke(null, new object[] { root.GetProperty("a") }) as string);
    }

    [Fact]
    public void Render_WhenNoAnalysis_EmitsNoAnalysisMessage()
    {
        var reportJson = """
        {
          "metadata": { "dumpId":"d1" }
        }
        """;

        var options = ReportOptions.FullReport;
        options.Format = ReportFormat.Html;
        options.IncludeRawJsonDetails = false;

        var html = JsonHtmlReportRenderer.Render(reportJson, options);
        Assert.Contains("No analysis available.", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_WhenFaultingThreadHasNoCallStack_PrintsNoCallStackAvailable()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(JsonMarkdownReportRendererCoverageTests.BuildCanonicalReportJson())!.AsObject();
        var analysis = root["analysis"]!.AsObject();
        var threads = analysis["threads"]!.AsObject();
        var faultingThread = threads["faultingThread"]!.AsObject();
        faultingThread.Remove("callStack");

        var options = ReportOptions.FullReport;
        options.Format = ReportFormat.Html;
        options.IncludeRawJsonDetails = false;

        var html = JsonHtmlReportRenderer.Render(root.ToJsonString(), options);
        Assert.Contains("No call stack available.", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_WhenArraysContainNonObjectEntries_SkipsThoseElements()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(JsonMarkdownReportRendererCoverageTests.BuildCanonicalReportJson())!.AsObject();
        var analysis = root["analysis"]!.AsObject();

        var threads = analysis["threads"]!.AsObject();
        threads["all"]!.AsArray().Add(System.Text.Json.Nodes.JsonValue.Create("bad-thread"));

        var faultingThread = threads["faultingThread"]!.AsObject();
        var callStack = faultingThread["callStack"]!.AsArray();
        callStack.Add(System.Text.Json.Nodes.JsonValue.Create("bad-frame"));
        callStack.Add(new System.Text.Json.Nodes.JsonObject
        {
            ["frameNumber"] = "1",
            ["module"] = "m2",
            ["function"] = "f2",
            ["instructionPointer"] = "0x2",
            ["stackPointer"] = "0x3",
            ["isManaged"] = true,
            ["sourceContext"] = new System.Text.Json.Nodes.JsonObject
            {
                ["status"] = "remote",
                ["lines"] = new System.Text.Json.Nodes.JsonArray("line 1")
            }
        });
        callStack.Add(new System.Text.Json.Nodes.JsonObject
        {
            ["frameNumber"] = "2",
            ["module"] = "m3",
            ["function"] = "f3",
            ["instructionPointer"] = "0x4",
            ["stackPointer"] = "0x5",
            ["isManaged"] = true,
            ["sourceFile"] = "f3.cs",
            ["sourceContext"] = new System.Text.Json.Nodes.JsonObject
            {
                ["status"] = "remote",
                ["error"] = "not found"
            }
        });

        var env = analysis["environment"]!.AsObject();
        env["runtime"]!.AsObject()["isHosted"] = false;
        env["crashInfo"] = new System.Text.Json.Nodes.JsonObject
        {
            ["hasInfo"] = true,
            ["message"] = new System.Text.Json.Nodes.JsonObject { ["m"] = 1 }
        };
        env["process"]!.AsObject()["arguments"] = new System.Text.Json.Nodes.JsonArray("--a", "--b");

        var assembliesItems = analysis["assemblies"]!.AsObject()["items"]!.AsArray();
        assembliesItems[0]!.AsObject().Remove("sourceUrl");
        assembliesItems.Add(System.Text.Json.Nodes.JsonValue.Create("bad-assembly"));

        analysis["modules"]!.AsArray().Add(System.Text.Json.Nodes.JsonValue.Create("bad-module"));
        analysis["timeline"]!.AsObject()["threads"]!.AsArray().Add(System.Text.Json.Nodes.JsonValue.Create("bad-timeline"));
        analysis["sourceContext"]!.AsArray().Add(System.Text.Json.Nodes.JsonValue.Create("bad-source-context"));

        var options = ReportOptions.FullReport;
        options.Format = ReportFormat.Html;
        options.IncludeRawJsonDetails = false;

        var html = JsonHtmlReportRenderer.Render(root.ToJsonString(), options);
        Assert.Contains("not found", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(none)", html, StringComparison.OrdinalIgnoreCase);
    }
}
