using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DebuggerMcp.Reporting;
using Xunit;

namespace DebuggerMcp.Tests.Reporting;

public class JsonMarkdownReportRendererCoverageTests
{
    [Fact]
    public void Render_WhenAllSectionsPresent_EmitsExpectedHeadingsAndDetails()
    {
        var reportJson = BuildCanonicalReportJson();
        var options = ReportOptions.FullReport;
        options.Format = ReportFormat.Markdown;
        options.IncludeRawJsonDetails = true;

        var markdown = JsonMarkdownReportRenderer.Render(reportJson, options);

        Assert.Contains("# Debugger MCP Report", markdown, StringComparison.Ordinal);
        Assert.Contains("## Table of Contents", markdown, StringComparison.Ordinal);
        Assert.Contains("## At a glance", markdown, StringComparison.Ordinal);
        Assert.Contains("<details><summary>Recommendations</summary>", markdown, StringComparison.Ordinal);
        Assert.Contains("## Faulting thread", markdown, StringComparison.Ordinal);
        Assert.Contains("## Threads", markdown, StringComparison.Ordinal);
        Assert.Contains("### Deadlock", markdown, StringComparison.Ordinal);
        Assert.Contains("## Memory & GC", markdown, StringComparison.Ordinal);
        Assert.Contains("### GC", markdown, StringComparison.Ordinal);
        Assert.Contains("## Security", markdown, StringComparison.Ordinal);
        Assert.Contains("<details><summary>Security recommendations</summary>", markdown, StringComparison.Ordinal);
        Assert.Contains("## Assemblies", markdown, StringComparison.Ordinal);
        Assert.Contains("## Modules", markdown, StringComparison.Ordinal);
        Assert.Contains("## Symbols", markdown, StringComparison.Ordinal);
        Assert.Contains("<details><summary>Native missing examples</summary>", markdown, StringComparison.Ordinal);
        Assert.Contains("## Timeline", markdown, StringComparison.Ordinal);
        Assert.Contains("## Source context index", markdown, StringComparison.Ordinal);
        Assert.Contains("## Signature", markdown, StringComparison.Ordinal);

        Assert.Contains("```json", markdown, StringComparison.Ordinal); // raw JSON details blocks
        Assert.Contains("<pre>", markdown, StringComparison.Ordinal);   // newline table cell escaping
        Assert.Contains("\\|", markdown, StringComparison.Ordinal);     // pipe escaping inside table cells
        Assert.Contains("\\`", markdown, StringComparison.Ordinal);     // backtick escaping
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
        var actual = InvokePrivateGuessFenceLanguage(typeof(JsonMarkdownReportRenderer), sourceFile);
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

    internal static string BuildCanonicalReportJson()
    {
        var root = new JsonObject
        {
            ["metadata"] = new JsonObject
            {
                ["dumpId"] = "dump`1",
                ["generatedAt"] = "2025-01-01T00:00:00Z",
                ["debuggerType"] = "LLDB",
                ["serverVersion"] = "1.2.3"
            },
            ["analysis"] = new JsonObject
            {
                ["summary"] = new JsonObject
                {
                    ["crashType"] = "Managed",
                    ["severity"] = "critical",
                    ["description"] = "Line1\r\nLine2|pipe with `backticks`",
                    ["threadCount"] = "2",
                    ["moduleCount"] = "1",
                    ["assemblyCount"] = "1",
                    ["recommendations"] = new JsonArray("do a", "do b")
                },
                ["threads"] = new JsonObject
                {
                    ["summary"] = new JsonObject { ["total"] = "2", ["foreground"] = "1", ["background"] = "1", ["unstarted"] = "0", ["dead"] = "0", ["pending"] = "0" },
                    ["deadlock"] = new JsonObject { ["detected"] = "true" },
                    ["threadPool"] = new JsonObject { ["workerThreads"] = 1 },
                    ["faultingThread"] = new JsonObject
                    {
                        ["threadId"] = "1",
                        ["state"] = "Running",
                        ["topFunction"] = "Foo",
                        ["managedThreadId"] = "1",
                        ["osThreadId"] = "0x123",
                        ["osThreadIdDecimal"] = "291",
                        ["gcMode"] = "Cooperative",
                        ["lockCount"] = "0",
                        ["apartmentState"] = "MTA",
                        ["isBackground"] = true,
                        ["isThreadpool"] = false,
                        ["callStack"] = new JsonArray(
                            new JsonObject
                            {
                                ["frameNumber"] = "0",
                                ["module"] = "m",
                                ["function"] = "f",
                                ["instructionPointer"] = "0x1",
                                ["stackPointer"] = "0x2",
                                ["isManaged"] = true,
                                ["sourceFile"] = "f.cs",
                                ["lineNumber"] = "10",
                                ["sourceUrl"] = "https://example.test/f.cs#L10",
                                ["sourceRawUrl"] = "https://raw.example.test/f.cs",
                                ["sourceContext"] = new JsonObject
                                {
                                    ["status"] = "remote",
                                    ["startLine"] = "7",
                                    ["endLine"] = "13",
                                    ["lines"] = new JsonArray("line 7", "line 8", "line 9", "line 10", "line 11", "line 12", "line 13")
                                }
                            })
                    },
                    ["all"] = new JsonArray(
                        new JsonObject { ["threadId"] = "1", ["state"] = "Running", ["isFaulting"] = true, ["topFunction"] = "Foo", ["frameCount"] = "1" },
                        new JsonObject { ["threadId"] = "2", ["state"] = "Waiting", ["isFaulting"] = false, ["topFunction"] = "Bar", ["frameCount"] = "0" })
                },
                ["environment"] = new JsonObject
                {
                    ["platform"] = new JsonObject { ["os"] = "Linux", ["architecture"] = "arm64", ["runtimeVersion"] = "9.0.10" },
                    ["runtime"] = new JsonObject { ["type"] = ".NET", ["clrVersion"] = "9.0.1025.47515" },
                    ["process"] = new JsonObject
                    {
                        ["pid"] = "123",
                        ["processName"] = "app",
                        ["commandLine"] = "/app --flag",
                        ["environmentVariables"] = new JsonArray("A=1", "B=2")
                    }
                },
                ["memory"] = new JsonObject
                {
                    ["gc"] = new JsonObject
                    {
                        ["heapCount"] = "1",
                        ["gcMode"] = "workstation",
                        ["isServerGC"] = "false",
                        ["totalHeapSize"] = "1000",
                        ["fragmentation"] = "0",
                        ["fragmentationBytes"] = "0",
                        ["finalizableObjectCount"] = "0"
                    },
                    ["topConsumers"] = new JsonObject { ["bySize"] = new JsonArray() },
                    ["strings"] = new JsonObject { ["summary"] = new JsonObject { ["totalStrings"] = 1 } },
                    ["leakAnalysis"] = new JsonObject { ["detected"] = false },
                    ["oom"] = new JsonObject { ["detected"] = false },
                    ["heapStats"] = new JsonObject { ["System.String"] = 1 }
                },
                ["synchronization"] = new JsonObject
                {
                    ["summary"] = "sync summary",
                    ["potentialDeadlocks"] = new JsonArray(new JsonObject { ["id"] = 1, ["threads"] = new JsonArray(1, 2), ["description"] = "deadlock" })
                },
                ["security"] = new JsonObject
                {
                    ["hasVulnerabilities"] = "false",
                    ["overallRisk"] = "low",
                    ["summary"] = "ok",
                    ["analyzedAt"] = "2025-01-01T00:00:00Z",
                    ["recommendations"] = new JsonArray("upgrade", "patch")
                },
                ["assemblies"] = new JsonObject
                {
                    ["items"] = new JsonArray(
                        new JsonObject
                        {
                            ["name"] = "My.Assembly",
                            ["version"] = "1.0.0",
                            ["path"] = "/path/to/My.Assembly.dll",
                            ["sourceUrl"] = "https://example.test/repo"
                        })
                },
                ["modules"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "libc.so",
                        ["baseAddress"] = "0x0",
                        ["hasSymbols"] = false
                    }),
                ["symbols"] = new JsonObject
                {
                    ["native"] = new JsonObject { ["missingCount"] = "1", ["examples"] = new JsonArray("libc.so") },
                    ["managed"] = new JsonObject { ["pdbMissingCount"] = "0" },
                    ["sourcelink"] = new JsonObject { ["resolvedCount"] = "1", ["unresolvedCount"] = "1" }
                },
                ["timeline"] = new JsonObject
                {
                    ["version"] = "1",
                    ["kind"] = "snapshot",
                    ["capturedAtUtc"] = "2025-01-01T00:00:00Z",
                    ["captureReason"] = "signal",
                    ["threads"] = new JsonArray(
                        new JsonObject
                        {
                            ["threadId"] = "thread_1",
                            ["osThreadId"] = "0x123",
                            ["state"] = "Running",
                            ["activity"] = "running",
                            ["topFrame"] = "m!f",
                            ["wait"] = ""
                        })
                },
                ["sourceContext"] = new JsonArray(
                    new JsonObject
                    {
                        ["threadId"] = "1",
                        ["frameNumber"] = "0",
                        ["module"] = "m",
                        ["function"] = "f",
                        ["sourceFile"] = "f.cs",
                        ["lineNumber"] = "10",
                        ["status"] = "remote"
                    }),
                ["signature"] = new JsonObject { ["kind"] = "crash", ["hash"] = "sha256:abc" },
            }
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    [Fact]
    public void PrivateHelpers_ElementToTextAndAppendKv_RenderScalars()
    {
        var elementToText = typeof(JsonMarkdownReportRenderer).GetMethod("ElementToText", BindingFlags.NonPublic | BindingFlags.Static);
        var appendKv = typeof(JsonMarkdownReportRenderer).GetMethod("AppendKv", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(elementToText);
        Assert.NotNull(appendKv);

        using var doc = JsonDocument.Parse("{\"s\":\"x\",\"n\":123,\"b\":true,\"f\":false,\"null\":null,\"a\":[1,2]}");
        var root = doc.RootElement;

        Assert.Equal("x", elementToText!.Invoke(null, new object[] { root.GetProperty("s") }) as string);
        Assert.Equal("123", elementToText.Invoke(null, new object[] { root.GetProperty("n") }) as string);
        Assert.Equal("True", elementToText.Invoke(null, new object[] { root.GetProperty("b") }) as string);
        Assert.Equal(string.Empty, elementToText.Invoke(null, new object[] { root.GetProperty("null") }) as string);
        Assert.Equal($"1{Environment.NewLine}2", elementToText.Invoke(null, new object[] { root.GetProperty("a") }) as string);

        var sb = new System.Text.StringBuilder();
        appendKv!.Invoke(null, new object[] { sb, root, "key", "s" });
        appendKv.Invoke(null, new object[] { sb, root, "bool", "b" });
        appendKv.Invoke(null, new object[] { sb, root, "num", "n" });
        appendKv.Invoke(null, new object[] { sb, root, "falsey", "f" });
        appendKv.Invoke(null, new object[] { sb, root, "null", "null" });
        appendKv.Invoke(null, new object[] { sb, root, "missing", "does_not_exist" });
        Assert.Contains("key", sb.ToString(), StringComparison.Ordinal);
        Assert.Contains("`x`", sb.ToString(), StringComparison.Ordinal);
        Assert.Contains("`true`", sb.ToString(), StringComparison.Ordinal);
        Assert.Contains("`123`", sb.ToString(), StringComparison.Ordinal);
        Assert.Contains("`false`", sb.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WhenNoAnalysis_PrintsNoAnalysisAvailable()
    {
        var reportJson = """
        {
          "metadata": { "dumpId":"d1" }
        }
        """;

        var options = ReportOptions.FullReport;
        options.Format = ReportFormat.Markdown;
        options.IncludeRawJsonDetails = false;

        var markdown = JsonMarkdownReportRenderer.Render(reportJson, options);

        Assert.Contains("_No analysis available._", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WhenFaultingThreadHasNoCallStack_PrintsNoCallStackAvailable()
    {
        var root = JsonNode.Parse(BuildCanonicalReportJson())!.AsObject();
        var analysis = root["analysis"]!.AsObject();
        analysis["environment"]!.AsObject()["process"]!.AsObject()["sensitiveDataFiltered"] = false;
        var threads = analysis["threads"]!.AsObject();
        var faultingThread = threads["faultingThread"]!.AsObject();
        faultingThread.Remove("callStack");

        var options = ReportOptions.FullReport;
        options.Format = ReportFormat.Markdown;
        options.IncludeRawJsonDetails = false;

        var markdown = JsonMarkdownReportRenderer.Render(root.ToJsonString(), options);
        Assert.Contains("_No call stack available._", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WhenArraysContainNonObjectEntries_SkipsThoseElements()
    {
        var root = JsonNode.Parse(BuildCanonicalReportJson())!.AsObject();
        var analysis = root["analysis"]!.AsObject();

        // Threads: include non-object thread entries and non-object call stack frames.
        var threads = analysis["threads"]!.AsObject();
        threads["all"]!.AsArray().Add(JsonValue.Create("bad-thread"));
        var faultingThread = threads["faultingThread"]!.AsObject();
        var callStack = faultingThread["callStack"]!.AsArray();
        callStack.Add(JsonValue.Create("bad-frame"));

        // Frame 1: missing sourceFile so GuessFenceLanguage returns empty.
        callStack.Add(new JsonObject
        {
            ["frameNumber"] = "1",
            ["module"] = "m2",
            ["function"] = "f2",
            ["instructionPointer"] = "0x2",
            ["stackPointer"] = "0x3",
            ["isManaged"] = true,
            ["sourceContext"] = new JsonObject
            {
                ["status"] = "remote",
                ["lines"] = new JsonArray("line 1")
            }
        });

        // Frame 2: source context error branch.
        callStack.Add(new JsonObject
        {
            ["frameNumber"] = "2",
            ["module"] = "m3",
            ["function"] = "f3",
            ["instructionPointer"] = "0x4",
            ["stackPointer"] = "0x5",
            ["isManaged"] = true,
            ["sourceFile"] = "f3.cs",
            ["sourceContext"] = new JsonObject
            {
                ["status"] = "remote",
                ["error"] = "not found"
            }
        });

        // Environment: exercise bool/string handling and arguments enumeration.
        var env = analysis["environment"]!.AsObject();
        env["runtime"]!.AsObject()["isHosted"] = false;
        env["crashInfo"] = new JsonObject { ["hasInfo"] = true, ["message"] = new JsonObject { ["m"] = 1 } };
        var process = env["process"]!.AsObject();
        process["sensitiveDataFiltered"] = "unknown";
        process["arguments"] = new JsonArray("--a", "--b");

        // Assemblies/modules/timeline/sourceContext index: include non-object entries.
        analysis["assemblies"]!.AsObject()["items"]!.AsArray().Add(JsonValue.Create("bad-assembly"));
        analysis["modules"]!.AsArray().Add(JsonValue.Create("bad-module"));
        env["process"]!.AsObject()["environmentVariables"]!.AsArray().Add(new JsonObject { ["k"] = "v" });
        analysis["timeline"]!.AsObject()["threads"]!.AsArray().Add(JsonValue.Create("bad-timeline"));
        analysis["sourceContext"]!.AsArray().Add(JsonValue.Create("bad-source-context"));

        var options = ReportOptions.FullReport;
        options.Format = ReportFormat.Markdown;
        options.IncludeRawJsonDetails = false;

        var markdown = JsonMarkdownReportRenderer.Render(root.ToJsonString(), options);
        Assert.Contains("## Faulting thread", markdown, StringComparison.Ordinal);
        Assert.Contains("not found", markdown, StringComparison.OrdinalIgnoreCase);
    }
}
